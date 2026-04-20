module JsonRpcTests

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open JsonRpc
open Xunit

module JsonRpcCodecAndCorrelatorTests =

    type ChannelAsyncEnumerable(reader: ChannelReader<byte array>) =
        interface IAsyncEnumerable<byte array> with
            member _.GetAsyncEnumerator(cancellationToken: CancellationToken) =
                let mutable current = Array.empty<byte>

                let rec readNext () =
                    task {
                        let! canRead = reader.WaitToReadAsync(cancellationToken).AsTask()

                        if not canRead then
                            return false
                        else
                            let mutable item = Unchecked.defaultof<byte array>

                            if reader.TryRead(&item) then
                                current <- item
                                return true
                            else
                                return! readNext ()
                    }

                { new IAsyncEnumerator<byte array> with
                    member _.Current = current
                    member _.MoveNextAsync() = ValueTask<bool>(readNext ())
                    member _.DisposeAsync() = ValueTask() }

    let private parseJson (json: string) =
        JsonDocument.Parse(json).RootElement.Clone()

    let private responseBytes id resultJson =
        let payload =
            match id with
            | StringId value -> $"""{{"jsonrpc":"2.0","id":"{value}","result":{resultJson}}}"""
            | NumberId value -> $"""{{"jsonrpc":"2.0","id":{value},"result":{resultJson}}}"""

        Encoding.UTF8.GetBytes(payload)

    let private errorBytes id =
        let payload =
            match id with
            | StringId value ->
                $"""{{"jsonrpc":"2.0","id":"{value}","error":{{"code":-32601,"message":"Method not found"}}}}"""
            | NumberId value ->
                $"""{{"jsonrpc":"2.0","id":{value},"error":{{"code":-32601,"message":"Method not found"}}}}"""

        Encoding.UTF8.GetBytes(payload)

    let private decodeRequestInfo (payload: byte array) =
        use document = JsonDocument.Parse(payload)
        let root = document.RootElement
        let idElement = root.GetProperty("id")
        let methodName = root.GetProperty("method").GetString()

        let id =
            match idElement.ValueKind with
            | JsonValueKind.String -> StringId(idElement.GetString())
            | _ -> NumberId(idElement.GetInt32())

        id, methodName

    [<Fact>]
    let ``codec round-trips encoded request fields`` () =
        let request =
            { Id = NumberId 42
              Method = "tools/call"
              Params = Some(parseJson """{"name":"echo","arguments":{"text":"hello"}}""") }

        let encoded = Codec.encode request
        use document = JsonDocument.Parse(encoded)
        let root = document.RootElement

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString())
        Assert.Equal(42, root.GetProperty("id").GetInt32())
        Assert.Equal("tools/call", root.GetProperty("method").GetString())
        Assert.Equal("echo", root.GetProperty("params").GetProperty("name").GetString())

    [<Fact>]
    let ``decode success response with string id`` () =
        let payload =
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":"req-1","result":{"ok":true}}""")

        match Codec.decode payload with
        | Ok(Response(StringId id, Ok result)) ->
            Assert.Equal("req-1", id)
            Assert.True(result.GetProperty("ok").GetBoolean())
        | other -> Assert.Fail($"Unexpected decode result: {other}")

    [<Fact>]
    let ``decode success response with numeric id`` () =
        let payload =
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":7,"result":{"value":123}}""")

        match Codec.decode payload with
        | Ok(Response(NumberId id, Ok result)) ->
            Assert.Equal(7, id)
            Assert.Equal(123, result.GetProperty("value").GetInt32())
        | other -> Assert.Fail($"Unexpected decode result: {other}")

    [<Fact>]
    let ``decode error response returns typed JsonRpcError`` () =
        let payload = errorBytes (StringId "oops")

        match Codec.decode payload with
        | Ok(Response(StringId id, Error error)) ->
            Assert.Equal("oops", id)
            Assert.Equal(-32601, error.Code)
            Assert.Equal("Method not found", error.Message)
        | other -> Assert.Fail($"Unexpected decode result: {other}")

    [<Fact>]
    let ``decode notification without id returns notification`` () =
        let payload =
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"tools/changed","params":{"count":2}}""")

        match Codec.decode payload with
        | Ok(Notification(methodName, Some parameters)) ->
            Assert.Equal("tools/changed", methodName)
            Assert.Equal(2, parameters.GetProperty("count").GetInt32())
        | other -> Assert.Fail($"Unexpected decode result: {other}")

    [<Fact>]
    let ``decode request with id returns request`` () =
        let payload =
            Encoding.UTF8.GetBytes(
                """{"jsonrpc":"2.0","id":7,"method":"filesystem/read_text_file","params":{"path":"notes.txt"}}"""
            )

        match Codec.decode payload with
        | Ok(Request request) ->
            match request.Id with
            | NumberId id -> Assert.Equal(7, id)
            | _ -> Assert.Fail("Expected numeric id")

            Assert.Equal("filesystem/read_text_file", request.Method)

            let parameters =
                request.Params
                |> Option.defaultWith (fun () ->
                    Assert.Fail("Expected params")
                    Unchecked.defaultof<_>)

            Assert.Equal("notes.txt", parameters.GetProperty("path").GetString())
        | other -> Assert.Fail($"Unexpected decode result: {other}")

    [<Fact>]
    let ``encode response writes result object`` () =
        let payload = Codec.encodeResponse (StringId "abc") (parseJson """{"ok":true}""")
        let json = Encoding.UTF8.GetString(payload)

        Assert.Contains("\"id\":\"abc\"", json)
        Assert.Contains("\"result\":{\"ok\":true}", json)

    [<Fact>]
    let ``encode error writes error object`` () =
        let payload =
            Codec.encodeError
                (NumberId 3)
                { Code = -32601
                  Message = "Method not found"
                  Data = None }

        let json = Encoding.UTF8.GetString(payload)

        Assert.Contains("\"id\":3", json)
        Assert.Contains("\"code\":-32601", json)
        Assert.Contains("Method not found", json)

    [<Fact>]
    let ``decode malformed json returns descriptive error`` () =
        let payload = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"result":""")

        match Codec.decode payload with
        | Error error -> Assert.Contains("Malformed JSON", error)
        | Ok _ -> Assert.Fail("Expected decode to fail")

    [<Fact>]
    let ``decode rejects numeric id that does not fit Int32`` () =
        let payload =
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":9999999999,"result":{}}""")

        match Codec.decode payload with
        | Error "JSON-RPC id number must fit Int32" -> ()
        | other -> Assert.Fail($"Unexpected decode result: {other}")

    [<Fact>]
    let ``correlator resolves three concurrent requests out of order`` () =
        let channel = Channel.CreateUnbounded<byte array>()
        let sent = ResizeArray<JsonRpcId * string>()
        let gate = obj ()

        let send payload =
            async {
                let id, methodName = decodeRequestInfo payload

                let shouldReply =
                    lock gate (fun () ->
                        sent.Add(id, methodName)
                        sent.Count = 3)

                if shouldReply then
                    let responses = lock gate (fun () -> sent |> Seq.toList |> List.rev)

                    for (responseId, responseMethod) in responses do
                        do!
                            channel.Writer
                                .WriteAsync(responseBytes responseId $"""{{"method":"{responseMethod}"}}""")
                                .AsTask()
                            |> Async.AwaitTask

                return Ok()
            }

        let correlator =
            Correlator.start send (ChannelAsyncEnumerable(channel.Reader) :> IAsyncEnumerable<_>) (fun _ _ -> ())

        let tasks =
            [ "one"; "two"; "three" ]
            |> List.map (fun methodName -> Correlator.sendRequest methodName None correlator |> Async.StartAsTask)

        let results = Task.WhenAll(tasks).Result |> Array.toList

        for (methodName, result) in List.zip [ "one"; "two"; "three" ] results do
            match result with
            | Ok payload -> Assert.Equal(methodName, payload.GetProperty("method").GetString())
            | Error error -> Assert.Fail($"Unexpected correlator error: {error.Message}")

        Correlator.stop correlator |> Async.RunSynchronously

    [<Fact>]
    let ``correlator fails pending requests when transport stream ends`` () =
        let channel = Channel.CreateUnbounded<byte array>()
        let send _ = async { return Ok() }

        let correlator =
            Correlator.start send (ChannelAsyncEnumerable(channel.Reader) :> IAsyncEnumerable<_>) (fun _ _ -> ())

        let requestOne = Correlator.sendRequest "one" None correlator |> Async.StartAsTask
        let requestTwo = Correlator.sendRequest "two" None correlator |> Async.StartAsTask

        channel.Writer.Complete()

        let results = Task.WhenAll([| requestOne; requestTwo |]).Result

        for result in results do
            match result with
            | Error error ->
                Assert.Equal(Correlator.TransportClosedCode, error.Code)
                Assert.Contains("Transport closed", error.Message)
            | Ok _ -> Assert.Fail("Expected pending request to fail")

        Correlator.stop correlator |> Async.RunSynchronously

    [<Fact>]
    let ``correlator fails all pending requests when transport yields unexpected request message`` () =
        let channel = Channel.CreateUnbounded<byte array>()
        let send _ = async { return Ok() }

        let correlator =
            Correlator.start send (ChannelAsyncEnumerable(channel.Reader) :> IAsyncEnumerable<_>) (fun _ _ -> ())

        let pending = Correlator.sendRequest "one" None correlator |> Async.StartAsTask

        let unexpectedRequest =
            Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"echo"}}""")

        channel.Writer.WriteAsync(unexpectedRequest).AsTask().Wait()

        match pending.Result with
        | Error error ->
            Assert.Equal(Correlator.TransportClosedCode, error.Code)
            Assert.Contains("unexpected JSON-RPC request", error.Message)
        | Ok _ -> Assert.Fail("Expected pending request to fail")

        Correlator.stop correlator |> Async.RunSynchronously

    [<Fact>]
    let ``correlator surfaces transport send failure to waiting caller`` () =
        let channel = Channel.CreateUnbounded<byte array>()
        let send _ = async { return Error "boom" }

        let correlator =
            Correlator.start send (ChannelAsyncEnumerable(channel.Reader) :> IAsyncEnumerable<_>) (fun _ _ -> ())

        match Correlator.sendRequest "one" None correlator |> Async.RunSynchronously with
        | Error error ->
            Assert.Equal(Correlator.TransportClosedCode, error.Code)
            Assert.Contains("Transport send failed: boom", error.Message)
        | Ok _ -> Assert.Fail("Expected send failure to surface")

        Correlator.stop correlator |> Async.RunSynchronously

    [<Fact>]
    let ``correlator routes notifications to callback`` () =
        let channel = Channel.CreateUnbounded<byte array>()
        let notificationReceived = TaskCompletionSource<string>()
        let send _ = async { return Ok() }

        let correlator =
            Correlator.start
                send
                (ChannelAsyncEnumerable(channel.Reader) :> IAsyncEnumerable<_>)
                (fun methodName parameters ->
                    let count =
                        parameters
                        |> Option.map (fun value -> value.GetProperty("count").GetInt32())
                        |> Option.defaultValue -1

                    notificationReceived.TrySetResult($"{methodName}:{count}") |> ignore)

        channel.Writer
            .WriteAsync(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"tools/changed","params":{"count":5}}"""))
            .AsTask()
            .Wait()

        let completed = notificationReceived.Task.Wait(TimeSpan.FromSeconds(3.0))
        Assert.True(completed)
        Assert.Equal("tools/changed:5", notificationReceived.Task.Result)

        Correlator.stop correlator |> Async.RunSynchronously
