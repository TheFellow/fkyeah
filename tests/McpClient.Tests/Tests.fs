module McpClientTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open McpClient
open Xunit

module McpClientTransportAndServerTests =

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

    let private createTempDir () =
        let dir = Path.Combine(Path.GetTempPath(), $"mcp-client-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(dir) |> ignore
        dir

    let private parseJson (json: string) =
        JsonDocument.Parse(json).RootElement.Clone()

    let private responseBytes id resultJson =
        let payload =
            match id with
            | JsonRpc.StringId value -> $"""{{"jsonrpc":"2.0","id":"{value}","result":{resultJson}}}"""
            | JsonRpc.NumberId value -> $"""{{"jsonrpc":"2.0","id":{value},"result":{resultJson}}}"""

        Encoding.UTF8.GetBytes(payload)

    let private errorBytes id code message =
        let payload =
            match id with
            | JsonRpc.StringId value ->
                $"""{{"jsonrpc":"2.0","id":"{value}","error":{{"code":{code},"message":"{message}"}}}}"""
            | JsonRpc.NumberId value ->
                $"""{{"jsonrpc":"2.0","id":{value},"error":{{"code":{code},"message":"{message}"}}}}"""

        Encoding.UTF8.GetBytes(payload)

    let private decodeRequest (payload: byte array) =
        use document = JsonDocument.Parse(payload)
        let root = document.RootElement
        let idElement = root.GetProperty("id")

        let id =
            match idElement.ValueKind with
            | JsonValueKind.String -> JsonRpc.StringId(idElement.GetString())
            | _ -> JsonRpc.NumberId(idElement.GetInt32())

        let methodName = root.GetProperty("method").GetString()

        let parameters =
            let mutable value = Unchecked.defaultof<JsonElement>

            if root.TryGetProperty("params", &value) then
                Some(value.Clone())
            else
                None

        id, methodName, parameters

    let private collectAsync (source: IAsyncEnumerable<'T>) =
        let enumerator = source.GetAsyncEnumerator(CancellationToken.None)
        let results = ResizeArray<'T>()
        let mutable keepReading = true

        while keepReading do
            let hasNext = enumerator.MoveNextAsync().AsTask().Result

            if hasNext then
                results.Add(enumerator.Current)
            else
                keepReading <- false

        enumerator.DisposeAsync().AsTask().Wait()
        results |> Seq.toList

    let private firstItem (source: IAsyncEnumerable<byte array>) =
        task {
            let enumerator = source.GetAsyncEnumerator(CancellationToken.None)

            try
                let! hasNext = enumerator.MoveNextAsync().AsTask()

                if hasNext then
                    return Some enumerator.Current
                else
                    return None
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }

    let private killCheck pid =
        use proc =
            Process.Start(ProcessStartInfo("/bin/kill", $"-0 {pid}", UseShellExecute = false))

        proc.WaitForExit()
        proc.ExitCode = 0

    let private resultOrFail result =
        match result with
        | Ok value -> value
        | Error error ->
            Assert.Fail(McpError.describe error)
            Unchecked.defaultof<_>

    let private stdioConfig command args =
        { Name = "mock"
          Transport = McpTransportKind.Stdio
          Command = Some command
          Args = args
          Env = Map.empty
          Url = None
          RequestUrl = None
          Headers = Map.empty }

    [<Fact>]
    let ``stdio transport connects and disconnect kills process`` () =
        let workDir = createTempDir ()
        let pidFile = Path.Combine(workDir, "pid.txt")

        let transport =
            Transport.createStdioTransport "/bin/sh" [ "-c"; $"echo $$ > '{pidFile}'; cat" ] Map.empty

        let connectResult = transport.Connect() |> Async.RunSynchronously
        Assert.True(Result.isOk connectResult)

        let deadline = DateTime.UtcNow.AddSeconds(3.0)

        while not (File.Exists(pidFile)) && DateTime.UtcNow < deadline do
            Thread.Sleep(25)

        Assert.True(File.Exists(pidFile))
        let pid = File.ReadAllText(pidFile).Trim()

        transport.Disconnect() |> Async.RunSynchronously

        Thread.Sleep(100)
        Assert.False(killCheck pid)

    [<Fact>]
    let ``stdio transport connect with nonexistent command returns invalid configuration`` () =
        let transport =
            Transport.createStdioTransport "/definitely/missing/mcp-server" [] Map.empty

        match transport.Connect() |> Async.RunSynchronously with
        | Error(McpError.InvalidConfiguration message) ->
            Assert.Contains("No such file", message, StringComparison.OrdinalIgnoreCase)
        | other -> Assert.Fail($"Unexpected connect result: {other}")

    [<Fact>]
    let ``stdio transport send receive round trip with cat`` () =
        let transport = Transport.createStdioTransport "/bin/cat" [] Map.empty
        transport.Connect() |> Async.RunSynchronously |> resultOrFail |> ignore

        let receiveTask = firstItem (transport.Receive CancellationToken.None)
        let payload = Encoding.UTF8.GetBytes("""{"hello":"world"}""")
        transport.Send payload |> Async.RunSynchronously |> resultOrFail |> ignore

        let echoed = receiveTask.Result |> Option.map Encoding.UTF8.GetString
        Assert.Equal(Some """{"hello":"world"}""", echoed)

        transport.Disconnect() |> Async.RunSynchronously

    [<Fact>]
    let ``stdio receive honors enumerator cancellation while waiting for output`` () =
        task {
            let transport = Transport.createStdioTransport "/bin/cat" [] Map.empty

            transport.Connect() |> Async.RunSynchronously |> resultOrFail |> ignore
            use cancellation = new CancellationTokenSource(150)
            let stopwatch = Stopwatch.StartNew()

            let enumerator =
                (transport.Receive CancellationToken.None).GetAsyncEnumerator(cancellation.Token)

            try
                let! hasItem = enumerator.MoveNextAsync().AsTask()
                Assert.False(hasItem)
                Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(50.0), TimeSpan.FromSeconds(3.0))
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
                transport.Disconnect() |> Async.RunSynchronously
        }
        :> Task

    [<Fact>]
    let ``stdio transport disconnect after process exit is idempotent`` () =
        let transport =
            Transport.createStdioTransport "/bin/sh" [ "-c"; "sleep 0.3" ] Map.empty

        transport.Connect() |> Async.RunSynchronously |> resultOrFail |> ignore
        Thread.Sleep(500)
        transport.Disconnect() |> Async.RunSynchronously
        transport.Disconnect() |> Async.RunSynchronously

    [<Fact>]
    let ``http sse transport uses custom headers and separate request url`` () =
        let port = 18080 + Random().Next(1000)
        let listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        let getHeader = TaskCompletionSource<string>()
        let postHeader = TaskCompletionSource<string>()
        let postPath = TaskCompletionSource<string>()
        let postBody = TaskCompletionSource<string>()

        let server: Task =
            Task.Run(fun () ->
                task {
                    let mutable handled = 0

                    while handled < 2 do
                        let context = listener.GetContext()
                        handled <- handled + 1

                        if context.Request.HttpMethod = "GET" then
                            getHeader.TrySetResult(context.Request.Headers["X-Test"]) |> ignore
                            context.Response.StatusCode <- 200
                            context.Response.ContentType <- "text/event-stream"
                            use writer = new StreamWriter(context.Response.OutputStream, Encoding.UTF8)
                            writer.Write("data: {\"ok\":true}\n\n")
                            writer.Flush()
                            context.Response.Close()
                        else
                            use reader =
                                new StreamReader(context.Request.InputStream, context.Request.ContentEncoding)

                            let body = reader.ReadToEnd()
                            postHeader.TrySetResult(context.Request.Headers["X-Test"]) |> ignore
                            postPath.TrySetResult(context.Request.Url.AbsolutePath) |> ignore
                            postBody.TrySetResult(body) |> ignore
                            context.Response.StatusCode <- 200
                            context.Response.ContentType <- "application/json"
                            let bytes = Encoding.UTF8.GetBytes("""{"accepted":true}""")
                            context.Response.OutputStream.Write(bytes, 0, bytes.Length)
                            context.Response.Close()

                    listener.Stop()
                }
                :> Task)

        let transport =
            Transport.createHttpSseTransport
                $"http://127.0.0.1:{port}/events"
                (Some $"http://127.0.0.1:{port}/rpc")
                (Map.ofList [ "X-Test", "true" ])

        transport.Connect() |> Async.RunSynchronously |> resultOrFail |> ignore
        let receiveTask = firstItem (transport.Receive CancellationToken.None)

        transport.Send(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0"}"""))
        |> Async.RunSynchronously
        |> resultOrFail
        |> ignore

        let received = receiveTask.Result |> Option.map Encoding.UTF8.GetString
        Assert.Equal(Some """{"ok":true}""", received)
        Assert.Equal("true", getHeader.Task.Result)
        Assert.Equal("true", postHeader.Task.Result)
        Assert.Equal("/rpc", postPath.Task.Result)
        Assert.Contains("\"jsonrpc\":\"2.0\"", postBody.Task.Result)

        transport.Disconnect() |> Async.RunSynchronously
        server.Wait()

    [<Fact>]
    let ``sse parser yields single line data event`` () =
        use stream = new MemoryStream(Encoding.UTF8.GetBytes("data: hello\n\n"))
        let events = Transport.parseSseStream stream CancellationToken.None |> collectAsync

        Assert.Single(events) |> ignore
        Assert.Equal("hello", Encoding.UTF8.GetString(events.Head.Data))

    [<Fact>]
    let ``sse parser concatenates multiline data with newline`` () =
        use stream = new MemoryStream(Encoding.UTF8.GetBytes("data: one\ndata: two\n\n"))
        let events = Transport.parseSseStream stream CancellationToken.None |> collectAsync

        Assert.Equal("one\ntwo", Encoding.UTF8.GetString(events.Head.Data))

    [<Fact>]
    let ``sse parser ignores comment lines`` () =
        use stream = new MemoryStream(Encoding.UTF8.GetBytes(": ping\ndata: hello\n\n"))
        let events = Transport.parseSseStream stream CancellationToken.None |> collectAsync

        Assert.Single(events) |> ignore
        Assert.Equal("hello", Encoding.UTF8.GetString(events.Head.Data))

    [<Fact>]
    let ``sse parser captures retry field`` () =
        use stream =
            new MemoryStream(Encoding.UTF8.GetBytes("retry: 1500\ndata: hello\n\n"))

        let events = Transport.parseSseStream stream CancellationToken.None |> collectAsync

        Assert.Equal(Some 1500, events.Head.RetryMs)

    [<Fact>]
    let ``sse parser dispatches empty data event`` () =
        use stream = new MemoryStream(Encoding.UTF8.GetBytes("data:\n\n"))
        let events = Transport.parseSseStream stream CancellationToken.None |> collectAsync

        Assert.Single(events) |> ignore
        Assert.Equal("", Encoding.UTF8.GetString(events.Head.Data))

    [<Fact>]
    let ``sse parser captures event_type and last_event_id and resets them per event`` () =
        use stream =
            new MemoryStream(
                Encoding.UTF8.GetBytes("event: tool\nid: evt-1\ndata: first\n\nid: evt-2\ndata: second\n\n")
            )

        let events = Transport.parseSseStream stream CancellationToken.None |> collectAsync

        Assert.Equal(2, events.Length)
        Assert.Equal("tool", events.[0].EventType)
        Assert.Equal(Some "evt-1", events.[0].LastEventId)
        Assert.Equal("first", Encoding.UTF8.GetString(events.[0].Data))
        Assert.Equal("message", events.[1].EventType)
        Assert.Equal(Some "evt-2", events.[1].LastEventId)
        Assert.Equal("second", Encoding.UTF8.GetString(events.[1].Data))

    let private createFakeTransport
        (handler:
            int
                -> ChannelWriter<byte array>
                -> JsonRpc.JsonRpcId
                -> string
                -> JsonElement option
                -> Async<Result<unit, McpError>>)
        =
        let methods = ResizeArray<string>()
        let mutable connectCount = 0
        let mutable disconnectCount = 0
        let initialChannel = Channel.CreateUnbounded<byte array>()
        let mutable writer = initialChannel.Writer
        let mutable reader = initialChannel.Reader

        let resetChannel () =
            let channel = Channel.CreateUnbounded<byte array>()
            writer <- channel.Writer
            reader <- channel.Reader

        let transport =
            { Connect =
                fun () ->
                    async {
                        connectCount <- connectCount + 1
                        resetChannel ()
                        return Ok()
                    }
              Send =
                fun payload ->
                    async {
                        let id, methodName, parameters = decodeRequest payload
                        methods.Add(methodName)
                        return! handler connectCount writer id methodName parameters
                    }
              Receive = fun _ -> ChannelAsyncEnumerable(reader) :> IAsyncEnumerable<byte array>
              Disconnect =
                fun () ->
                    async {
                        disconnectCount <- disconnectCount + 1
                        writer.TryComplete() |> ignore
                    } }

        transport, methods, (fun () -> connectCount), (fun () -> disconnectCount)

    [<Fact>]
    let ``server factory validates stdio and sse configs`` () =
        let stdio = stdioConfig "/bin/cat" []

        let sse =
            { Name = "remote"
              Transport = McpTransportKind.HttpSse
              Command = None
              Args = []
              Env = Map.empty
              Url = Some "http://127.0.0.1:9000/events"
              RequestUrl = Some "http://127.0.0.1:9000/rpc"
              Headers = Map.empty }

        Assert.True(Result.isOk (Server.createServer stdio McpConnectionPolicy.NoRetry))
        Assert.True(Result.isOk (Server.createServer sse McpConnectionPolicy.NoRetry))

        Assert.True(
            match Server.createServer { stdio with Command = None } McpConnectionPolicy.NoRetry with
            | Error(McpError.InvalidConfiguration _) -> true
            | _ -> false
        )

        Assert.True(
            match Server.createServer { sse with Url = None } McpConnectionPolicy.NoRetry with
            | Error(McpError.InvalidConfiguration _) -> true
            | _ -> false
        )

    [<Fact>]
    let ``config parser handles valid invalid and missing field cases`` () =
        let valid =
            """{"servers":[{"name":"mock","transport":"stdio","command":"/bin/cat","args":[]},{"name":"remote","transport":"sse","url":"https://example.com/events"}]}"""

        let missing = """{"servers":[{"name":"mock","transport":"stdio"}]}"""
        let invalid = """{"servers": ["""

        match Config.parseConfigText valid with
        | Ok configs -> Assert.Equal(2, configs.Length)
        | Error error -> Assert.Fail(McpError.describe error)

        match Config.parseConfigText invalid with
        | Error(McpError.InvalidConfiguration message) -> Assert.Contains("Invalid JSON", message)
        | other -> Assert.Fail($"Unexpected parse result: {other}")

        match Config.parseConfigText missing with
        | Error(McpError.InvalidConfiguration message) -> Assert.Contains("command", message)
        | other -> Assert.Fail($"Unexpected parse result: {other}")

    [<Fact>]
    let ``server list tools returns zero tools and parses input schema aliases`` () =
        let transport, _, _, _ =
            createFakeTransport (fun _ writer id methodName _ ->
                async {
                    match methodName with
                    | "initialize" ->
                        do!
                            writer.WriteAsync(responseBytes id """{"protocolVersion":"2025-03-26"}""").AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | "tools/list" ->
                        do!
                            writer
                                .WriteAsync(
                                    responseBytes
                                        id
                                        """{"tools":[{"name":"echo","description":"Echoes","input_schema":{"type":"object"}}]}"""
                                )
                                .AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | _ -> return Ok()
                })

        let config = stdioConfig "/bin/cat" []

        let server =
            Server.createServerWithTransport config transport McpConnectionPolicy.NoRetry
            |> resultOrFail

        match server.ListTools() |> Async.RunSynchronously with
        | Ok tools ->
            Assert.Single(tools) |> ignore
            Assert.Equal("echo", tools.Head.Name)
            Assert.Equal("object", tools.Head.InputSchema.GetProperty("type").GetString())
        | Error error -> Assert.Fail(McpError.describe error)

        server.Cleanup() |> Async.RunSynchronously

    [<Fact>]
    let ``server tools list can return zero tools`` () =
        let transport, _, _, _ =
            createFakeTransport (fun _ writer id methodName _ ->
                async {
                    match methodName with
                    | "initialize" ->
                        do!
                            writer.WriteAsync(responseBytes id """{"protocolVersion":"2025-03-26"}""").AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | "tools/list" ->
                        do!
                            writer.WriteAsync(responseBytes id """{"tools":[]}""").AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | _ -> return Ok()
                })

        let server =
            Server.createServerWithTransport (stdioConfig "/bin/cat" []) transport McpConnectionPolicy.NoRetry
            |> resultOrFail

        match server.ListTools() |> Async.RunSynchronously with
        | Ok tools -> Assert.Empty(tools)
        | Error error -> Assert.Fail(McpError.describe error)

        server.Cleanup() |> Async.RunSynchronously

    [<Fact>]
    let ``server list_tools caches first successful response`` () =
        let transport, methods, _, _ =
            createFakeTransport (fun _ writer id methodName _ ->
                async {
                    match methodName with
                    | "initialize" ->
                        do!
                            writer.WriteAsync(responseBytes id """{"protocolVersion":"2025-03-26"}""").AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | "tools/list" ->
                        do!
                            writer
                                .WriteAsync(
                                    responseBytes
                                        id
                                        """{"tools":[{"name":"echo","description":"Echo","inputSchema":{"type":"object"}}]}"""
                                )
                                .AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | _ -> return Ok()
                })

        let server =
            Server.createServerWithTransport (stdioConfig "/bin/cat" []) transport McpConnectionPolicy.NoRetry
            |> resultOrFail

        let first = server.ListTools() |> Async.RunSynchronously |> resultOrFail
        let second = server.ListTools() |> Async.RunSynchronously |> resultOrFail

        Assert.Single(first) |> ignore
        Assert.Single(second) |> ignore
        Assert.Equal(1, methods |> Seq.filter ((=) "tools/list") |> Seq.length)

        server.Cleanup() |> Async.RunSynchronously

    [<Fact>]
    let ``server maps rpc error envelopes to McpError RpcError`` () =
        let transport, _, _, _ =
            createFakeTransport (fun _ writer id methodName _ ->
                async {
                    match methodName with
                    | "initialize" ->
                        do!
                            writer.WriteAsync(responseBytes id """{"protocolVersion":"2025-03-26"}""").AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | "tools/call" ->
                        do!
                            writer.WriteAsync(errorBytes id -32601 "Tool not found").AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | _ -> return Ok()
                })

        let server =
            Server.createServerWithTransport (stdioConfig "/bin/cat" []) transport McpConnectionPolicy.NoRetry
            |> resultOrFail

        match
            server.CallTool "missing" (parseJson """{"text":"hello"}""")
            |> Async.RunSynchronously
        with
        | Error(McpError.RpcError(code, message)) ->
            Assert.Equal(-32601, code)
            Assert.Equal("Tool not found", message)
        | other -> Assert.Fail($"Unexpected call result: {other}")

        server.Cleanup() |> Async.RunSynchronously

    [<Fact>]
    let ``server call_tool accepts is_error alias in payload`` () =
        let transport, _, _, _ =
            createFakeTransport (fun _ writer id methodName _ ->
                async {
                    match methodName with
                    | "initialize" ->
                        do!
                            writer.WriteAsync(responseBytes id """{"protocolVersion":"2025-03-26"}""").AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | "tools/call" ->
                        do!
                            writer
                                .WriteAsync(
                                    responseBytes id """{"content":[{"type":"text","text":"bad"}],"is_error":true}"""
                                )
                                .AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | _ -> return Ok()
                })

        let server =
            Server.createServerWithTransport (stdioConfig "/bin/cat" []) transport McpConnectionPolicy.NoRetry
            |> resultOrFail

        match
            server.CallTool "echo" (parseJson """{"text":"hello"}""")
            |> Async.RunSynchronously
        with
        | Ok result ->
            Assert.True(result.IsError)

            Assert.Equal(
                "bad",
                result.Content.EnumerateArray()
                |> Seq.head
                |> fun item -> item.GetProperty("text").GetString()
            )
        | Error error -> Assert.Fail(McpError.describe error)

        server.Cleanup() |> Async.RunSynchronously

    [<Fact>]
    let ``server reconnect refreshes tools before retrying call`` () =
        let mutable firstCall = true

        let transport, methods, getConnectCount, _ =
            createFakeTransport (fun _ writer id methodName _ ->
                async {
                    match methodName with
                    | "initialize" ->
                        do!
                            writer.WriteAsync(responseBytes id """{"protocolVersion":"2025-03-26"}""").AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | "tools/list" ->
                        do!
                            writer
                                .WriteAsync(
                                    responseBytes
                                        id
                                        """{"tools":[{"name":"echo_upper","description":"Echo","inputSchema":{"type":"object"}}]}"""
                                )
                                .AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | "tools/call" when firstCall ->
                        firstCall <- false
                        return Error(McpError.TransportClosed "simulated drop")
                    | "tools/call" ->
                        do!
                            writer
                                .WriteAsync(
                                    responseBytes id """{"content":[{"type":"text","text":"OK"}],"isError":false}"""
                                )
                                .AsTask()
                            |> Async.AwaitTask

                        return Ok()
                    | _ -> return Ok()
                })

        let server =
            Server.createServerWithTransport
                (stdioConfig "/bin/cat" [])
                transport
                { McpConnectionPolicy.Default with
                    MaxRetries = 1
                    RetryDelay = TimeSpan.Zero
                    RefreshToolsOnReconnect = true }
            |> resultOrFail

        server.ListTools() |> Async.RunSynchronously |> resultOrFail |> ignore

        match
            server.CallTool "echo_upper" (parseJson """{"text":"hello"}""")
            |> Async.RunSynchronously
        with
        | Ok result ->
            Assert.False(result.IsError)
            Assert.Equal(2, methods |> Seq.filter ((=) "tools/list") |> Seq.length)
            Assert.Equal(2, methods |> Seq.filter ((=) "tools/call") |> Seq.length)
            Assert.Equal(2, getConnectCount ())
        | Error error -> Assert.Fail(McpError.describe error)

        server.Cleanup() |> Async.RunSynchronously

    [<Fact>]
    let ``discover tools handles empty list and collisions by server name`` () =
        match ToolDiscovery.discoverTools [] |> Async.RunSynchronously with
        | Ok [] -> ()
        | other -> Assert.Fail($"Unexpected empty discovery result: {other}")

        let tool =
            { Name = "read_file"
              Description = "Read a file"
              InputSchema = parseJson """{"type":"object"}""" }

        let serverA =
            { Config =
                { stdioConfig "/bin/cat" [] with
                    Name = "alpha" }
              ListTools = fun () -> async { return Ok [ tool ] }
              CallTool = fun _ _ -> async { return Error McpError.NotConnected }
              Cleanup = fun () -> async { return () } }

        let serverB =
            { Config =
                { stdioConfig "/bin/cat" [] with
                    Name = "beta" }
              ListTools = fun () -> async { return Ok [ tool ] }
              CallTool = fun _ _ -> async { return Error McpError.NotConnected }
              Cleanup = fun () -> async { return () } }

        match ToolDiscovery.discoverTools [ serverA; serverB ] |> Async.RunSynchronously with
        | Ok discovered ->
            Assert.Equal(2, discovered.Length)
            Assert.Contains(discovered, fun item -> item.ServerName = "alpha" && item.Definition.Name = "read_file")
            Assert.Contains(discovered, fun item -> item.ServerName = "beta" && item.Definition.Name = "read_file")
        | Error error -> Assert.Fail(McpError.describe error)

    [<Fact>]
    let ``connection policy defaults and no retry values are stable`` () =
        Assert.True(McpConnectionPolicy.Default.AutoReconnect)
        Assert.True(McpConnectionPolicy.Default.RefreshToolsOnReconnect)
        Assert.False(McpConnectionPolicy.NoRetry.AutoReconnect)
        Assert.Equal(0, McpConnectionPolicy.NoRetry.MaxRetries)
