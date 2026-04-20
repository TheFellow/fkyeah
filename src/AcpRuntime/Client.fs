namespace AcpRuntime

open System
open System.Text.Json
open System.Threading
open JsonRpc

type private ConnectionState =
    | Disconnected
    | Initializing
    | Connected

type private PendingRequest =
    | PendingInitialize of AsyncReplyChannel<Result<InitializeResult, AcpError>>
    | PendingPrompt of sessionId: string * AsyncReplyChannel<Result<PromptResult, AcpError>>
    | PendingCancel of sessionId: string * AsyncReplyChannel<Result<unit, AcpError>>

type internal ClientMsg =
    | Connect of
        endpoint: AcpEndpoint *
        delegateImpl: AcpDelegate *
        timeout: TimeSpan option *
        reply: AsyncReplyChannel<Result<InitializeResult, AcpError>>
    | Prompt of
        sessionId: string *
        content: ContentBlock list *
        metadata: PromptMetadata option *
        timeout: TimeSpan option *
        reply: AsyncReplyChannel<Result<PromptResult, AcpError>>
    | Cancel of sessionId: string * timeout: TimeSpan option * reply: AsyncReplyChannel<Result<unit, AcpError>>
    | AddObserver of NotificationObserver
    | IncomingMessage of byte array
    | TransportClosed of AcpError
    | RequestTimedOut of JsonRpcId
    | Disconnect of AsyncReplyChannel<unit>

type private ClientState =
    { ConnectionState: ConnectionState
      Transport: AcpTransport option
      Delegate: AcpDelegate
      Endpoint: AcpEndpoint option
      Pending: Map<JsonRpcId, PendingRequest>
      Observers: NotificationObserver list
      NextId: int
      ReceiveCancellation: CancellationTokenSource option
      CompletedSessions: Set<string> }

type AcpClient internal (agent: MailboxProcessor<ClientMsg>) =

    member _.Connect(endpoint: AcpEndpoint, delegateImpl: AcpDelegate, timeout: TimeSpan option) =
        agent.PostAndAsyncReply(fun reply -> Connect(endpoint, delegateImpl, timeout, reply))

    member _.Prompt
        (sessionId: string, content: ContentBlock list, metadata: PromptMetadata option, timeout: TimeSpan option)
        =
        agent.PostAndAsyncReply(fun reply -> Prompt(sessionId, content, metadata, timeout, reply))

    member _.Cancel(sessionId: string, timeout: TimeSpan option) =
        agent.PostAndAsyncReply(fun reply -> Cancel(sessionId, timeout, reply))

    member _.AddObserver(observer: NotificationObserver) = agent.Post(AddObserver observer)

    member _.Disconnect() = agent.PostAndAsyncReply Disconnect

module Client =

    let private defaultCapabilities = Json.serializeToElement {| delegates = true |}

    let private serializeInitializeRequest () =
        Json.serializeToElement
            {| protocolVersion = "2026-03-23"
               clientInfo =
                {| name = "fkyeah-attractor"
                   version = "1" |}
               capabilities = defaultCapabilities |}

    let private serializePromptRequest
        (sessionId: string)
        (content: ContentBlock list)
        (metadata: PromptMetadata option)
        =
        let baseRequest =
            {| sessionId = sessionId
               prompt = ContentBlock.toElement content |}

        match
            metadata
            |> Option.bind (fun value -> value.McpServersJson |> Option.map (fun json -> value, json))
        with
        | Some(_, mcpServersJson) ->
            Json.serializeToElement
                {| sessionId = baseRequest.sessionId
                   prompt = baseRequest.prompt
                   metadata = {| mcpServersJson = mcpServersJson |} |}
        | None -> Json.serializeToElement baseRequest

    let private serializeCancelRequest (sessionId: string) =
        Json.serializeToElement {| sessionId = sessionId |}

    let private parseServerInfo (element: JsonElement) =
        Ok
            { Name = Json.tryGetString "name" element |> Option.defaultValue ""
              Version = Json.tryGetString "version" element }

    let private parseInitializeResult (element: JsonElement) =
        let protocolVersion =
            Json.tryGetString "protocolVersion" element
            |> Option.orElseWith (fun () -> Json.tryGetString "protocol_version" element)
            |> Option.defaultValue ""

        if protocolVersion = "" then
            Error(AcpError.MissingResult "initialize response is missing protocolVersion")
        else
            let capabilities =
                Json.tryGetProperty "capabilities" element
                |> Option.defaultValue (Json.serializeToElement {| |})

            let serverInfo =
                Json.tryGetProperty "serverInfo" element
                |> Option.orElseWith (fun () -> Json.tryGetProperty "server_info" element)
                |> Option.map parseServerInfo

            match serverInfo |> Option.defaultValue (Ok { Name = ""; Version = None }) with
            | Error message -> Error(AcpError.InvalidResponse message)
            | Ok info ->
                let normalizedInfo =
                    if info.Name = "" && info.Version.IsNone then
                        None
                    else
                        Some info

                Ok
                    { ProtocolVersion = protocolVersion
                      Capabilities = capabilities
                      ServerInfo = normalizedInfo }

    let private parsePromptResult (element: JsonElement) =
        let sessionId =
            Json.tryGetString "sessionId" element
            |> Option.orElseWith (fun () -> Json.tryGetString "session_id" element)
            |> Option.defaultValue ""

        if sessionId = "" then
            Error(AcpError.MissingResult "prompt response is missing sessionId")
        else
            let contentElement =
                Json.tryGetProperty "content" element
                |> Option.orElseWith (fun () -> Json.tryGetProperty "output" element)
                |> Option.defaultValue (Json.serializeToElement [ {| ``type`` = "text"; text = "" |} ])

            match ContentBlock.ofElement contentElement with
            | Error error -> Error(AcpError.InvalidResponse error)
            | Ok content ->
                Ok
                    { SessionId = sessionId
                      Content = content
                      StopReason =
                        Json.tryGetString "stopReason" element
                        |> Option.orElseWith (fun () -> Json.tryGetString "stop_reason" element)
                      Metadata = Json.tryGetProperty "metadata" element }

    let private mapRpcError (error: JsonRpcError) =
        match error.Code with
        | -32001 -> AcpError.PermissionDenied error.Message
        | -32601 when error.Message.Contains("delegate", StringComparison.OrdinalIgnoreCase) ->
            AcpError.UnknownDelegateMethod error.Message
        | _ -> AcpError.InvalidResponse $"RPC error {error.Code}: {error.Message}"

    let private mapDelegateError (error: AcpError) =
        match error with
        | AcpError.PermissionDenied _ ->
            { Code = -32001
              Message = "permission denied"
              Data = None }
        | AcpError.PathOutsideRoot message ->
            { Code = -32002
              Message = message
              Data = None }
        | AcpError.UnknownDelegateMethod methodName ->
            { Code = -32601
              Message = $"Unknown delegate method '{methodName}'"
              Data = None }
        | AcpError.InvalidPayload message ->
            { Code = -32602
              Message = message
              Data = None }
        | _ ->
            { Code = -32000
              Message = AcpError.describe error
              Data = None }

    let private invokeDelegate (delegateImpl: AcpDelegate) (request: JsonRpcRequest) =
        let bindResult parse invoke project =
            match request.Params with
            | None ->
                async { return Error(AcpError.InvalidPayload $"Delegate method '{request.Method}' requires params") }
            | Some parameters ->
                match parse parameters with
                | Error error -> async { return Error(AcpError.InvalidPayload error) }
                | Ok parsed ->
                    async {
                        let! result = invoke parsed
                        return result |> Result.map project
                    }

        match request.Method with
        | "filesystem/read_text_file" ->
            bindResult Json.deserialize<ReadTextFileRequest> delegateImpl.ReadTextFile Json.serializeToElement
        | "filesystem/write_text_file" ->
            bindResult Json.deserialize<WriteTextFileRequest> delegateImpl.WriteTextFile Json.serializeToElement
        | "terminal/create" ->
            bindResult Json.deserialize<TerminalCreateRequest> delegateImpl.TerminalCreate Json.serializeToElement
        | "terminal/output" ->
            bindResult Json.deserialize<TerminalOutputRequest> delegateImpl.TerminalOutput Json.serializeToElement
        | "terminal/wait_for_exit" ->
            bindResult
                Json.deserialize<TerminalWaitForExitRequest>
                delegateImpl.TerminalWaitForExit
                Json.serializeToElement
        | "terminal/kill" ->
            bindResult Json.deserialize<TerminalKillRequest> delegateImpl.TerminalKill Json.serializeToElement
        | "terminal/release" ->
            bindResult Json.deserialize<TerminalReleaseRequest> delegateImpl.TerminalRelease Json.serializeToElement
        | "permissions/request" ->
            bindResult Json.deserialize<PermissionRequest> delegateImpl.RequestPermission Json.serializeToElement
        | unknown -> async { return Error(AcpError.UnknownDelegateMethod unknown) }

    let private startReceiveLoop
        (transport: AcpTransport)
        (ct: CancellationToken)
        (inbox: MailboxProcessor<ClientMsg>)
        =
        Async.Start(
            async {
                let enumerator = transport.Receive ct |> fun stream -> stream.GetAsyncEnumerator(ct)

                let disposeAsync () =
                    enumerator.DisposeAsync().AsTask() |> Async.AwaitTask

                try
                    let mutable keepReading = true

                    while keepReading && not ct.IsCancellationRequested do
                        let! hasNext = enumerator.MoveNextAsync().AsTask() |> Async.AwaitTask

                        if not hasNext then
                            keepReading <- false
                        else
                            inbox.Post(IncomingMessage enumerator.Current)

                    if not ct.IsCancellationRequested then
                        inbox.Post(TransportClosed AcpError.ConnectionClosed)
                with
                | :? OperationCanceledException -> ()
                | ex when not ct.IsCancellationRequested ->
                    inbox.Post(TransportClosed(AcpError.InvalidPayload ex.Message))

                do! disposeAsync ()
            },
            ct
        )

    let private scheduleTimeout (timeout: TimeSpan option) (requestId: JsonRpcId) (inbox: MailboxProcessor<ClientMsg>) =
        match timeout with
        | Some value when value <= TimeSpan.Zero -> inbox.Post(RequestTimedOut requestId)
        | Some value ->
            Async.Start(
                async {
                    do! Async.Sleep(max 1 (int value.TotalMilliseconds))
                    inbox.Post(RequestTimedOut requestId)
                }
            )
        | None -> ()

    let private failPending pending error =
        for KeyValue(_, request) in pending do
            match request with
            | PendingInitialize reply -> reply.Reply(Error error)
            | PendingPrompt(_, reply) -> reply.Reply(Error error)
            | PendingCancel(_, reply) -> reply.Reply(Error error)

    let private bestEffortCancel (transport: AcpTransport option) (nextId: int) (sessionId: string) =
        async {
            match transport with
            | Some activeTransport ->
                let request =
                    { Id = NumberId nextId
                      Method = "session/cancel"
                      Params = Some(serializeCancelRequest sessionId) }

                let! _ = activeTransport.Send(Codec.encode request)
                return ()
            | None -> return ()
        }

    let create (transportFactory: AcpEndpoint -> Result<AcpTransport, AcpError>) =
        let initialState =
            { ConnectionState = Disconnected
              Transport = None
              Delegate = AcpDelegate.denyAll
              Endpoint = None
              Pending = Map.empty
              Observers = []
              NextId = 1
              ReceiveCancellation = None
              CompletedSessions = Set.empty }

        let agent =
            MailboxProcessor.Start(fun inbox ->
                let rec loop state =
                    async {
                        let! msg = inbox.Receive()

                        match msg with
                        | AddObserver observer ->
                            return!
                                loop
                                    { state with
                                        Observers = observer :: state.Observers }

                        | Connect(endpoint, delegateImpl, timeout, reply) ->
                            match state.ConnectionState with
                            | Connected
                            | Initializing ->
                                reply.Reply(Error AcpError.AlreadyConnected)
                                return! loop state
                            | Disconnected ->
                                match transportFactory endpoint with
                                | Error error ->
                                    reply.Reply(Error error)
                                    return! loop state
                                | Ok transport ->
                                    let! connectResult = transport.Connect()

                                    match connectResult with
                                    | Error error ->
                                        reply.Reply(Error error)
                                        return! loop state
                                    | Ok() ->
                                        let receiveCancellation = new CancellationTokenSource()
                                        startReceiveLoop transport receiveCancellation.Token inbox

                                        let requestId = NumberId state.NextId

                                        let request =
                                            { Id = requestId
                                              Method = "initialize"
                                              Params = Some(serializeInitializeRequest ()) }

                                        let pending = state.Pending |> Map.add requestId (PendingInitialize reply)

                                        let nextState =
                                            { state with
                                                ConnectionState = Initializing
                                                Transport = Some transport
                                                Delegate = delegateImpl
                                                Endpoint = Some endpoint
                                                Pending = pending
                                                NextId = state.NextId + 1
                                                ReceiveCancellation = Some receiveCancellation }

                                        scheduleTimeout timeout requestId inbox
                                        let! sendResult = transport.Send(Codec.encode request)

                                        match sendResult with
                                        | Ok() -> return! loop nextState
                                        | Error error ->
                                            failPending pending error
                                            do! transport.Disconnect()
                                            receiveCancellation.Cancel()
                                            receiveCancellation.Dispose()

                                            return!
                                                loop
                                                    { initialState with
                                                        Observers = state.Observers }

                        | Prompt(sessionId, content, metadata, timeout, reply) ->
                            match state.ConnectionState, state.Transport with
                            | Connected, Some transport ->
                                let requestId = NumberId state.NextId

                                let request =
                                    { Id = requestId
                                      Method = "session/prompt"
                                      Params = Some(serializePromptRequest sessionId content metadata) }

                                let pending = state.Pending |> Map.add requestId (PendingPrompt(sessionId, reply))

                                let nextState =
                                    { state with
                                        Pending = pending
                                        NextId = state.NextId + 1 }

                                scheduleTimeout timeout requestId inbox
                                let! sendResult = transport.Send(Codec.encode request)

                                match sendResult with
                                | Ok() -> return! loop nextState
                                | Error error ->
                                    reply.Reply(Error error)

                                    return!
                                        loop
                                            { nextState with
                                                Pending = nextState.Pending |> Map.remove requestId }
                            | _ ->
                                reply.Reply(Error AcpError.NotConnected)
                                return! loop state

                        | Cancel(sessionId, timeout, reply) ->
                            match state.ConnectionState, state.Transport with
                            | Connected, Some transport when state.CompletedSessions.Contains(sessionId) ->
                                reply.Reply(Ok())
                                return! loop state
                            | Connected, Some transport ->
                                let requestId = NumberId state.NextId

                                let request =
                                    { Id = requestId
                                      Method = "session/cancel"
                                      Params = Some(serializeCancelRequest sessionId) }

                                let pending = state.Pending |> Map.add requestId (PendingCancel(sessionId, reply))

                                let nextState =
                                    { state with
                                        Pending = pending
                                        NextId = state.NextId + 1 }

                                scheduleTimeout timeout requestId inbox
                                let! sendResult = transport.Send(Codec.encode request)

                                match sendResult with
                                | Ok() -> return! loop nextState
                                | Error error ->
                                    reply.Reply(Error error)

                                    return!
                                        loop
                                            { nextState with
                                                Pending = nextState.Pending |> Map.remove requestId }
                            | _ ->
                                reply.Reply(Error AcpError.NotConnected)
                                return! loop state

                        | IncomingMessage payload ->
                            match Codec.decode payload with
                            | Error error ->
                                let acpError = AcpError.InvalidPayload error
                                failPending state.Pending acpError

                                return!
                                    loop
                                        { initialState with
                                            Observers = state.Observers }
                            | Ok(Notification(methodName, parameters)) ->
                                for observer in List.rev state.Observers do
                                    observer methodName parameters

                                return! loop state
                            | Ok(Request request) ->
                                let! result = invokeDelegate state.Delegate request

                                match state.Transport with
                                | Some transport ->
                                    let payload =
                                        match result with
                                        | Ok response -> Codec.encodeResponse request.Id response
                                        | Error error -> Codec.encodeError request.Id (mapDelegateError error)

                                    let! sendResult = transport.Send payload

                                    match sendResult with
                                    | Ok() -> return! loop state
                                    | Error error ->
                                        failPending state.Pending error

                                        return!
                                            loop
                                                { initialState with
                                                    Observers = state.Observers }
                                | None -> return! loop state
                            | Ok(Response(id, result)) ->
                                match state.Pending |> Map.tryFind id with
                                | None -> return! loop state
                                | Some pending ->
                                    let nextState =
                                        { state with
                                            Pending = state.Pending |> Map.remove id }

                                    match pending, result with
                                    | PendingInitialize reply, Ok payload ->
                                        match parseInitializeResult payload with
                                        | Error error ->
                                            reply.Reply(Error error)

                                            return!
                                                loop
                                                    { nextState with
                                                        ConnectionState = Disconnected
                                                        Transport = None
                                                        Endpoint = None }
                                        | Ok initializeResult ->
                                            match nextState.Transport with
                                            | Some transport ->
                                                let initializedPayload =
                                                    Codec.encodeNotification
                                                        "initialized"
                                                        (Some(Json.serializeToElement {| |}))

                                                let! sendResult = transport.Send initializedPayload

                                                match sendResult with
                                                | Error error ->
                                                    reply.Reply(Error error)

                                                    return!
                                                        loop
                                                            { nextState with
                                                                ConnectionState = Disconnected
                                                                Transport = None
                                                                Endpoint = None }
                                                | Ok() ->
                                                    reply.Reply(Ok initializeResult)

                                                    return!
                                                        loop
                                                            { nextState with
                                                                ConnectionState = Connected }
                                            | None ->
                                                reply.Reply(Error AcpError.NotConnected)
                                                return! loop nextState
                                    | PendingInitialize reply, Error error ->
                                        reply.Reply(Error(mapRpcError error))

                                        return!
                                            loop
                                                { nextState with
                                                    ConnectionState = Disconnected
                                                    Transport = None
                                                    Endpoint = None }
                                    | PendingPrompt(sessionId, reply), Ok payload ->
                                        match parsePromptResult payload with
                                        | Error error ->
                                            reply.Reply(Error error)
                                            return! loop nextState
                                        | Ok promptResult ->
                                            reply.Reply(Ok promptResult)

                                            return!
                                                loop
                                                    { nextState with
                                                        CompletedSessions = nextState.CompletedSessions.Add(sessionId) }
                                    | PendingPrompt(_, reply), Error error ->
                                        reply.Reply(Error(mapRpcError error))
                                        return! loop nextState
                                    | PendingCancel(_, reply), Ok _ ->
                                        reply.Reply(Ok())
                                        return! loop nextState
                                    | PendingCancel(sessionId, reply), Error error ->
                                        if
                                            error.Message.Contains(
                                                "already completed",
                                                StringComparison.OrdinalIgnoreCase
                                            )
                                        then
                                            reply.Reply(Ok())

                                            return!
                                                loop
                                                    { nextState with
                                                        CompletedSessions = nextState.CompletedSessions.Add(sessionId) }
                                        else
                                            reply.Reply(Error(mapRpcError error))
                                            return! loop nextState

                        | RequestTimedOut requestId ->
                            match state.Pending |> Map.tryFind requestId with
                            | None -> return! loop state
                            | Some pending ->
                                let nextState =
                                    { state with
                                        Pending = state.Pending |> Map.remove requestId }

                                match pending with
                                | PendingInitialize reply ->
                                    reply.Reply(Error(AcpError.TimedOut "initialize"))
                                    return! loop nextState
                                | PendingPrompt(sessionId, reply) ->
                                    reply.Reply(Error(AcpError.TimedOut $"session '{sessionId}' timed out"))
                                    do! bestEffortCancel state.Transport state.NextId sessionId

                                    return!
                                        loop
                                            { nextState with
                                                NextId = state.NextId + 1 }
                                | PendingCancel(sessionId, reply) ->
                                    reply.Reply(Error(AcpError.TimedOut $"cancel '{sessionId}' timed out"))
                                    return! loop nextState

                        | TransportClosed error ->
                            failPending state.Pending error

                            match state.ReceiveCancellation with
                            | Some cancellation ->
                                cancellation.Cancel()
                                cancellation.Dispose()
                            | None -> ()

                            match state.Transport with
                            | Some transport -> do! transport.Disconnect()
                            | None -> ()

                            return!
                                loop
                                    { initialState with
                                        Observers = state.Observers }

                        | Disconnect reply ->
                            failPending state.Pending AcpError.ConnectionClosed

                            match state.ReceiveCancellation with
                            | Some cancellation ->
                                cancellation.Cancel()
                                cancellation.Dispose()
                            | None -> ()

                            match state.Transport with
                            | Some transport -> do! transport.Disconnect()
                            | None -> ()

                            reply.Reply()

                            return!
                                loop
                                    { initialState with
                                        Observers = state.Observers }
                    }

                loop initialState)

        AcpClient(agent)
