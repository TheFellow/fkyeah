namespace JsonRpc

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type private CorrelatorCommand =
    | SendRequest of methodName: string * parameters: JsonElement option * reply: AsyncReplyChannel<Result<JsonElement, JsonRpcError>>
    | Resolve of JsonRpcId * Result<JsonElement, JsonRpcError>
    | FailAllPending of JsonRpcError
    | Stop of AsyncReplyChannel<unit>

type JsonRpcCorrelator =
    private
        { Mailbox: MailboxProcessor<CorrelatorCommand>
          Cancellation: CancellationTokenSource }

module Correlator =

    [<Literal>]
    let TransportClosedCode = -32098

    let private transportClosed reason =
        { Code = TransportClosedCode
          Message = reason
          Data = None }

    let private consumeMessages
        (receive: IAsyncEnumerable<byte array>)
        (mailbox: MailboxProcessor<CorrelatorCommand>)
        (onNotification: string -> JsonElement option -> unit)
        (cancellationToken: CancellationToken)
        =
        async {
            let enumerator = receive.GetAsyncEnumerator(cancellationToken)

            let disposeAsync () =
                enumerator.DisposeAsync().AsTask() |> Async.AwaitTask

            try
                let mutable keepReading = true

                while keepReading && not cancellationToken.IsCancellationRequested do
                    let! hasNext =
                        enumerator.MoveNextAsync().AsTask()
                        |> Async.AwaitTask

                    if not hasNext then
                        keepReading <- false
                    else
                        match Codec.decode enumerator.Current with
                        | Ok(Request _) ->
                            keepReading <- false
                            mailbox.Post(FailAllPending(transportClosed "Transport received unexpected JSON-RPC request"))
                        | Ok(Response(id, result)) -> mailbox.Post(Resolve(id, result))
                        | Ok(Notification(methodName, parameters)) -> onNotification methodName parameters
                        | Error error ->
                            keepReading <- false
                            mailbox.Post(FailAllPending(transportClosed $"Transport closed after malformed JSON-RPC message: {error}"))

                if not cancellationToken.IsCancellationRequested then
                    mailbox.Post(FailAllPending(transportClosed "Transport closed while awaiting JSON-RPC response"))
            with
            | :? OperationCanceledException -> ()
            | ex when not cancellationToken.IsCancellationRequested ->
                mailbox.Post(FailAllPending(transportClosed $"Transport closed: {ex.Message}"))
            do! disposeAsync ()
        }

    let start
        (send: byte array -> Async<Result<unit, string>>)
        (receive: IAsyncEnumerable<byte array>)
        (onNotification: string -> JsonElement option -> unit)
        =
        let cancellation = new CancellationTokenSource()

        let mailbox =
            MailboxProcessor.Start(
                (fun inbox ->
                    let rec loop nextId (pending: Map<JsonRpcId, AsyncReplyChannel<Result<JsonElement, JsonRpcError>>>) stopped =
                        async {
                            let! message = inbox.Receive()

                            match message with
                            | SendRequest(methodName, parameters, reply) when stopped ->
                                reply.Reply(Error(transportClosed "Transport closed"))
                                return! loop nextId pending stopped
                            | SendRequest(methodName, parameters, reply) ->
                                let id = NumberId nextId
                                let request =
                                    { Id = id
                                      Method = methodName
                                      Params = parameters }

                                Async.Start(
                                    async {
                                        let! sendResult = send (Codec.encode request)
                                        match sendResult with
                                        | Ok () -> ()
                                        | Error error -> inbox.Post(Resolve(id, Error(transportClosed $"Transport send failed: {error}")))
                                    },
                                    cancellation.Token
                                )

                                return! loop (nextId + 1) (pending |> Map.add id reply) stopped
                            | Resolve(id, result) ->
                                match pending |> Map.tryFind id with
                                | Some reply ->
                                    reply.Reply(result)
                                    return! loop nextId (pending |> Map.remove id) stopped
                                | None -> return! loop nextId pending stopped
                            | FailAllPending error ->
                                for KeyValue(_, reply) in pending do
                                    reply.Reply(Error error)
                                return! loop nextId Map.empty true
                            | Stop reply ->
                                let error = transportClosed "Transport closed"
                                for KeyValue(_, pendingReply) in pending do
                                    pendingReply.Reply(Error error)
                                reply.Reply()
                                return! loop nextId Map.empty true
                        }

                    loop 1 Map.empty false),
                cancellation.Token
            )

        Async.Start(consumeMessages receive mailbox onNotification cancellation.Token, cancellation.Token)

        { Mailbox = mailbox
          Cancellation = cancellation }

    let sendRequest methodName parameters correlator =
        correlator.Mailbox.PostAndAsyncReply(fun reply -> SendRequest(methodName, parameters, reply))

    let stop correlator =
        async {
            do! correlator.Mailbox.PostAndAsyncReply Stop
            correlator.Cancellation.Cancel()
            correlator.Cancellation.Dispose()
        }
