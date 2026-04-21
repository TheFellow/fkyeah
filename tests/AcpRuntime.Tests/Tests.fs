module AcpRuntimeTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open AcpRuntime
open Attractor
open JsonRpc
open MockAcpAgent
open Xunit

module Helpers =

    let createTempDir () =
        let path = Path.Combine(Path.GetTempPath(), $"acp-runtime-tests-{Guid.NewGuid():N}")
        Directory.CreateDirectory(path) |> ignore
        path

    let readFirst (stream: Collections.Generic.IAsyncEnumerable<byte array>) =
        task {
            let enumerator = stream.GetAsyncEnumerator(CancellationToken.None)

            try
                let! hasNext = enumerator.MoveNextAsync().AsTask()

                if hasNext then
                    return Some enumerator.Current
                else
                    return None
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }

    let startServer (transport: AcpTransport) (handler: AcpTransport -> JsonRpcRequest -> Async<unit>) =
        transport.Connect() |> Async.RunSynchronously |> ignore

        Async.Start(
            async {
                let enumerator =
                    transport.Receive CancellationToken.None
                    |> fun stream -> stream.GetAsyncEnumerator()

                let mutable keepReading = true

                try
                    while keepReading do
                        let! hasNext = enumerator.MoveNextAsync().AsTask() |> Async.AwaitTask

                        if hasNext then
                            match Codec.decode enumerator.Current with
                            | Ok(Request request) -> do! handler transport request
                            | Ok(Notification _) -> ()
                            | Ok(Response _) -> ()
                            | Error _ -> keepReading <- false
                        else
                            keepReading <- false
                finally
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
            }
        )

    let fixtureDll () = typeof<Marker>.Assembly.Location

    let makeGraph () =
        { Name = "test"
          Nodes = Map.empty
          Edges = []
          GraphAttributes = Map.empty }

    let makeAcpNode attributes =
        { Id = "agent"
          Attributes =
            attributes
            |> List.map (fun (key, value) -> key, AttrValue.String value)
            |> Map.ofList }

// ===========================================================================
// TYPE SYSTEM ACCEPTANCE TESTS
// ===========================================================================

module AcpErrorTypeTests =

    [<Fact>]
    let ``AcpError DU has exactly 12 cases`` () =
        let cases: AcpError list =
            [ AcpError.AlreadyConnected
              AcpError.NotConnected
              AcpError.ConnectionClosed
              AcpError.InvalidPayload "b"
              AcpError.InvalidResponse "c"
              AcpError.MissingResult "d"
              AcpError.TimedOut "e"
              AcpError.PermissionDenied "f"
              AcpError.PathOutsideRoot "g"
              AcpError.TransportClosed
              AcpError.ProcessExited 0
              AcpError.UnknownDelegateMethod "h" ]

        Assert.Equal(12, cases.Length)

        let tags =
            cases
            |> List.map (fun c ->
                Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(c, typeof<AcpError>)
                |> fst
                |> fun info -> info.Name)
            |> List.distinct

        Assert.Equal(12, tags.Length)

module AcpTransportKindTypeTests =

    [<Fact>]
    let ``AcpTransportKind has exactly 4 cases`` () =
        let cases: AcpTransportKind list =
            [ AcpTransportKind.Stdio
              AcpTransportKind.WebSocket
              AcpTransportKind.HttpSse
              AcpTransportKind.InMemory ]

        Assert.Equal(4, cases.Length)

        let tags =
            cases
            |> List.map (fun c ->
                Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(c, typeof<AcpTransportKind>)
                |> fst
                |> fun info -> info.Name)
            |> List.distinct

        Assert.Equal(4, tags.Length)

    [<Theory>]
    [<InlineData("stdio", "Stdio")>]
    [<InlineData("websocket", "WebSocket")>]
    [<InlineData("http_sse", "HttpSse")>]
    [<InlineData("memory", "InMemory")>]
    let ``AcpTransportKind Parse roundtrip for canonical names`` (input: string, expectedCase: string) =
        match AcpTransportKind.Parse(input) with
        | Some kind ->
            let tag =
                Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(kind, typeof<AcpTransportKind>)
                |> fst
                |> fun info -> info.Name

            Assert.Equal(expectedCase, tag)
        | None -> Assert.Fail($"AcpTransportKind.Parse returned None for '{input}'")

    [<Theory>]
    [<InlineData("ws")>]
    [<InlineData("sse")>]
    [<InlineData("http-sse")>]
    [<InlineData("http+sse")>]
    [<InlineData("in-memory")>]
    [<InlineData("in_memory")>]
    [<InlineData("inmemory")>]
    let ``AcpTransportKind Parse handles aliases`` (input: string) =
        let result = AcpTransportKind.Parse(input)
        Assert.True(result.IsSome, $"AcpTransportKind.Parse returned None for alias '{input}'")

    [<Fact>]
    let ``AcpTransportKind Parse returns None for unknown transport`` () =
        Assert.True((AcpTransportKind.Parse "grpc").IsNone)
        Assert.True((AcpTransportKind.Parse "").IsNone)
        Assert.True((AcpTransportKind.Parse "tcp").IsNone)

module PermissionStrategyTypeTests =

    [<Fact>]
    let ``PermissionStrategy has exactly 3 cases`` () =
        let cases: PermissionStrategy list =
            [ PermissionStrategy.DenyAll
              PermissionStrategy.AutoApprove
              PermissionStrategy.ConsolePrompt ]

        Assert.Equal(3, cases.Length)

        let tags =
            cases
            |> List.map (fun c ->
                Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(c, typeof<PermissionStrategy>)
                |> fst
                |> fun info -> info.Name)
            |> List.distinct

        Assert.Equal(3, tags.Length)

module ContentBlockTypeTests =

    [<Fact>]
    let ``ContentBlock Text case can be created`` () =
        let block = ContentBlock.Text "hello"
        Assert.NotNull(box block)

    [<Fact>]
    let ``ContentBlock Image case can be created`` () =
        let block = ContentBlock.Image "https://example.com/img.png"
        Assert.NotNull(box block)

    [<Fact>]
    let ``content_block decoder rejects unsupported type and missing required fields`` () =
        let unsupported =
            JsonSerializer.SerializeToElement([ {| ``type`` = "video" |} ])
            |> fun value -> value.Clone()

        let missingText =
            JsonSerializer.SerializeToElement([ {| ``type`` = "text" |} ])
            |> fun value -> value.Clone()

        match ContentBlock.ofElement unsupported with
        | Error message -> Assert.Contains("Unsupported content block type 'video'", message)
        | Ok _ -> Assert.Fail("Expected unsupported block type to fail")

        match ContentBlock.ofElement missingText with
        | Error message -> Assert.Contains("missing text", message)
        | Ok _ -> Assert.Fail("Expected missing text field to fail")

module AcpEndpointTypeTests =

    [<Fact>]
    let ``AcpEndpoint record has all required fields`` () =
        let endpoint =
            { AcpEndpoint.Transport = AcpTransportKind.Stdio
              Command = Some "/usr/bin/agent"
              Args = [ "--flag" ]
              Url = Some "ws://localhost:8080"
              Headers = Map.ofList [ "Authorization", "Bearer token" ]
              WorkingDirectory = Some "/tmp/work" }

        Assert.Equal(AcpTransportKind.Stdio, endpoint.Transport)
        Assert.Equal(Some "/usr/bin/agent", endpoint.Command)
        Assert.Equal<string list>([ "--flag" ], endpoint.Args)
        Assert.Equal(Some "ws://localhost:8080", endpoint.Url)
        Assert.True(endpoint.Headers.ContainsKey("Authorization"))
        Assert.Equal(Some "/tmp/work", endpoint.WorkingDirectory)

// ===========================================================================
// TRANSPORT TESTS
// ===========================================================================

module TransportTests =

    [<Fact>]
    let ``transport kind parse round-trips aliases`` () =
        Assert.Equal(Some AcpTransportKind.Stdio, AcpTransportKind.Parse("stdio"))
        Assert.Equal(Some AcpTransportKind.WebSocket, AcpTransportKind.Parse("ws"))
        Assert.Equal(Some AcpTransportKind.HttpSse, AcpTransportKind.Parse("http+sse"))
        Assert.Equal(Some AcpTransportKind.InMemory, AcpTransportKind.Parse("in-memory"))
        Assert.Equal("memory", AcpTransportKind.InMemory.ToString())

    [<Fact>]
    let ``in-memory transport pair sends receives and disconnects`` () =
        let left, right = Transport.createInMemoryPair ()
        left.Connect() |> Async.RunSynchronously |> ignore
        right.Connect() |> Async.RunSynchronously |> ignore

        let receiveTask = Helpers.readFirst (right.Receive CancellationToken.None)
        let payload = Encoding.UTF8.GetBytes("""{"hello":"world"}""")
        left.Send payload |> Async.RunSynchronously |> ignore

        let echoed = receiveTask.Result |> Option.map Encoding.UTF8.GetString
        Assert.Equal(Some """{"hello":"world"}""", echoed)

        left.Disconnect() |> Async.RunSynchronously

        match right.Send payload |> Async.RunSynchronously with
        | Error AcpError.TransportClosed -> ()
        | other -> Assert.Fail($"Unexpected send result after disconnect: {other}")

    [<Fact>]
    let ``in-memory transport queue semantics preserves message order`` () =
        let left, right = Transport.createInMemoryPair ()
        left.Connect() |> Async.RunSynchronously |> ignore
        right.Connect() |> Async.RunSynchronously |> ignore

        for i in 1..5 do
            let payload = Encoding.UTF8.GetBytes($"message-{i}")
            left.Send payload |> Async.RunSynchronously |> ignore

        let cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.0))
        let enumerator = (right.Receive cts.Token).GetAsyncEnumerator(cts.Token)
        let received = ResizeArray<string>()

        for _ in 1..5 do
            let hasNext = enumerator.MoveNextAsync().AsTask().Result
            Assert.True(hasNext)
            received.Add(Encoding.UTF8.GetString(enumerator.Current))

        Assert.Equal(5, received.Count)

        for i in 1..5 do
            Assert.Equal($"message-{i}", received.[i - 1])

        left.Disconnect() |> Async.RunSynchronously
        right.Disconnect() |> Async.RunSynchronously

    [<Fact>]
    let ``in-memory transport bidirectional communication`` () =
        let left, right = Transport.createInMemoryPair ()
        left.Connect() |> Async.RunSynchronously |> ignore
        right.Connect() |> Async.RunSynchronously |> ignore

        left.Send(Encoding.UTF8.GetBytes("from-left"))
        |> Async.RunSynchronously
        |> ignore

        right.Send(Encoding.UTF8.GetBytes("from-right"))
        |> Async.RunSynchronously
        |> ignore

        let cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.0))

        let enumRight = (right.Receive cts.Token).GetAsyncEnumerator(cts.Token)
        Assert.True(enumRight.MoveNextAsync().AsTask().Result)
        Assert.Equal("from-left", Encoding.UTF8.GetString(enumRight.Current))

        let enumLeft = (left.Receive cts.Token).GetAsyncEnumerator(cts.Token)
        Assert.True(enumLeft.MoveNextAsync().AsTask().Result)
        Assert.Equal("from-right", Encoding.UTF8.GetString(enumLeft.Current))

        left.Disconnect() |> Async.RunSynchronously
        right.Disconnect() |> Async.RunSynchronously

    [<Fact>]
    let ``AcpTransport record has expected field signatures`` () =
        let transport, _ = Transport.createInMemoryPair ()
        let _connect: unit -> Async<Result<unit, AcpError>> = transport.Connect
        let _send: byte array -> Async<Result<unit, AcpError>> = transport.Send

        let _receive: CancellationToken -> Collections.Generic.IAsyncEnumerable<byte array> =
            transport.Receive

        let _disconnect: unit -> Async<unit> = transport.Disconnect
        let _isConnected: unit -> bool = transport.IsConnected
        Assert.True(true)

// ===========================================================================
// DELEGATE TESTS
// ===========================================================================

module DelegateTests =

    [<Fact>]
    let ``denyAll rejects file and terminal operations`` () =
        let readResult =
            AcpDelegate.denyAll.ReadTextFile { Path = "notes.txt" }
            |> Async.RunSynchronously

        let createResult =
            AcpDelegate.denyAll.TerminalCreate
                { Command = "/bin/echo"
                  Args = []
                  WorkingDirectory = None
                  Environment = Map.empty }
            |> Async.RunSynchronously

        match readResult with
        | Error(AcpError.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Unexpected read result: {other}")

        match createResult with
        | Error(AcpError.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Unexpected terminal result: {other}")

    [<Fact>]
    let ``denyAll rejects WriteTextFile`` () =
        match
            AcpDelegate.denyAll.WriteTextFile { Path = "/tmp/x"; Content = "y" }
            |> Async.RunSynchronously
        with
        | Error(AcpError.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")

    [<Fact>]
    let ``denyAll rejects TerminalOutput`` () =
        match
            AcpDelegate.denyAll.TerminalOutput { TerminalId = "t1"; MaxBytes = None }
            |> Async.RunSynchronously
        with
        | Error(AcpError.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")

    [<Fact>]
    let ``denyAll rejects TerminalWaitForExit`` () =
        match
            AcpDelegate.denyAll.TerminalWaitForExit { TerminalId = "t1"; TimeoutMs = None }
            |> Async.RunSynchronously
        with
        | Error(AcpError.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")

    [<Fact>]
    let ``denyAll rejects TerminalKill`` () =
        match AcpDelegate.denyAll.TerminalKill { TerminalId = "t1" } |> Async.RunSynchronously with
        | Error(AcpError.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")

    [<Fact>]
    let ``denyAll rejects TerminalRelease`` () =
        match
            AcpDelegate.denyAll.TerminalRelease { TerminalId = "t1" }
            |> Async.RunSynchronously
        with
        | Error(AcpError.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")

    [<Fact>]
    let ``denyAll rejects RequestPermission`` () =
        match
            AcpDelegate.denyAll.RequestPermission
                { Operation = "test"
                  Subject = None
                  Reason = None }
            |> Async.RunSynchronously
        with
        | Error(AcpError.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Expected PermissionDenied, got {other}")

module DefaultDelegateTests =

    [<Fact>]
    let ``default delegate auto-approve reads file within root`` () =
        let root = Helpers.createTempDir ()
        let filePath = Path.Combine(root, "notes.txt")
        File.WriteAllText(filePath, "hello")

        let delegateImpl =
            DefaultDelegate.createDefaultDelegate root PermissionStrategy.AutoApprove 4096

        let result =
            delegateImpl.ReadTextFile { Path = "notes.txt" } |> Async.RunSynchronously

        match result with
        | Ok value -> Assert.Equal("hello", value.Content)
        | Error error -> Assert.Fail(AcpError.describe error)

    [<Fact>]
    let ``default delegate rejects absolute path outside root`` () =
        let root = Helpers.createTempDir ()
        let outside = Helpers.createTempDir ()
        let outsideFile = Path.Combine(outside, "secret.txt")
        File.WriteAllText(outsideFile, "secret")

        let delegateImpl =
            DefaultDelegate.createDefaultDelegate root PermissionStrategy.AutoApprove 4096

        match delegateImpl.ReadTextFile { Path = outsideFile } |> Async.RunSynchronously with
        | Error(AcpError.PathOutsideRoot _) -> ()
        | other -> Assert.Fail($"Unexpected result: {other}")

    [<Fact>]
    let ``default delegate rejects symlink escape`` () =
        let root = Helpers.createTempDir ()
        let outside = Helpers.createTempDir ()
        let outsideFile = Path.Combine(outside, "secret.txt")
        File.WriteAllText(outsideFile, "secret")
        let linkPath = Path.Combine(root, "escape")
        Directory.CreateSymbolicLink(linkPath, outside) |> ignore

        let delegateImpl =
            DefaultDelegate.createDefaultDelegate root PermissionStrategy.AutoApprove 4096

        match
            delegateImpl.ReadTextFile { Path = "escape/secret.txt" }
            |> Async.RunSynchronously
        with
        | Error(AcpError.PathOutsideRoot _) -> ()
        | other -> Assert.Fail($"Unexpected result: {other}")

    [<Fact>]
    let ``default delegate deny-all rejects read and terminal create`` () =
        let root = Helpers.createTempDir ()

        let delegateImpl =
            DefaultDelegate.createDefaultDelegate root PermissionStrategy.DenyAll 4096

        match delegateImpl.ReadTextFile { Path = "notes.txt" } |> Async.RunSynchronously with
        | Error(AcpError.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Unexpected read result: {other}")

        match
            delegateImpl.TerminalCreate
                { Command = "/bin/sh"
                  Args = [ "-c"; "echo hi" ]
                  WorkingDirectory = None
                  Environment = Map.empty }
            |> Async.RunSynchronously
        with
        | Error(AcpError.PermissionDenied _) -> ()
        | other -> Assert.Fail($"Unexpected terminal result: {other}")

    [<Fact>]
    let ``terminal kill on exited process is no-op`` () =
        let root = Helpers.createTempDir ()

        let delegateImpl =
            DefaultDelegate.createDefaultDelegate root PermissionStrategy.AutoApprove 4096

        let terminalId =
            match
                delegateImpl.TerminalCreate
                    { Command = "/bin/sh"
                      Args = [ "-c"; "exit 0" ]
                      WorkingDirectory = None
                      Environment = Map.empty }
                |> Async.RunSynchronously
            with
            | Ok result -> result.TerminalId
            | Error error ->
                Assert.Fail(AcpError.describe error)
                ""

        Thread.Sleep(200)

        match delegateImpl.TerminalKill { TerminalId = terminalId } |> Async.RunSynchronously with
        | Ok result -> Assert.False(result.Killed)
        | Error error -> Assert.Fail(AcpError.describe error)

    [<Fact>]
    let ``default delegate dotdot path outside root returns PathOutsideRoot`` () =
        let root = Helpers.createTempDir ()

        let delegateImpl =
            DefaultDelegate.createDefaultDelegate root PermissionStrategy.AutoApprove 4096

        let escapedPath = Path.Combine(root, "..", "outside.txt")

        match delegateImpl.ReadTextFile { Path = escapedPath } |> Async.RunSynchronously with
        | Error(AcpError.PathOutsideRoot _) -> ()
        | other -> Assert.Fail($"Expected PathOutsideRoot, got {other}")

    [<Fact>]
    let ``default delegate WriteTextFile outside root returns PathOutsideRoot`` () =
        let root = Helpers.createTempDir ()

        let delegateImpl =
            DefaultDelegate.createDefaultDelegate root PermissionStrategy.AutoApprove 4096

        match
            delegateImpl.WriteTextFile
                { Path = "/tmp/evil.txt"
                  Content = "malicious" }
            |> Async.RunSynchronously
        with
        | Error(AcpError.PathOutsideRoot _) -> ()
        | other -> Assert.Fail($"Expected PathOutsideRoot, got {other}")

    [<Fact>]
    let ``default delegate WriteTextFile within root succeeds with AutoApprove`` () =
        let root = Helpers.createTempDir ()

        let delegateImpl =
            DefaultDelegate.createDefaultDelegate root PermissionStrategy.AutoApprove 4096

        let targetFile = Path.Combine(root, "output.txt")

        match
            delegateImpl.WriteTextFile
                { Path = targetFile
                  Content = "written content" }
            |> Async.RunSynchronously
        with
        | Ok result ->
            Assert.True(File.Exists(targetFile))
            Assert.Equal("written content", File.ReadAllText(targetFile))
            Assert.True(result.BytesWritten > 0)
        | Error error -> Assert.Fail($"Expected Ok, got Error: {AcpError.describe error}")

    [<Fact>]
    let ``terminal output max_bytes truncates on UTF8 boundary`` () =
        let root = Helpers.createTempDir ()

        let delegateImpl =
            DefaultDelegate.createDefaultDelegate root PermissionStrategy.AutoApprove 4096
        // Generate a large multibyte output: 200 × "é" (2 bytes each = 400 UTF-8 bytes)
        let terminalId =
            match
                delegateImpl.TerminalCreate
                    { Command = "/bin/sh"
                      Args = [ "-c"; "python3 -c \"print('é' * 200, end='')\"" ]
                      WorkingDirectory = None
                      Environment = Map.empty }
                |> Async.RunSynchronously
            with
            | Ok result -> result.TerminalId
            | Error error ->
                Assert.Fail(AcpError.describe error)
                ""

        delegateImpl.TerminalWaitForExit
            { TerminalId = terminalId
              TimeoutMs = Some 5000 }
        |> Async.RunSynchronously
        |> ignore
        // Small delay to ensure drain loops have completed
        System.Threading.Thread.Sleep(100)

        match
            delegateImpl.TerminalOutput
                { TerminalId = terminalId
                  MaxBytes = Some 50 }
            |> Async.RunSynchronously
        with
        | Ok result ->
            let byteCount = Encoding.UTF8.GetByteCount(result.Output)
            Assert.True(byteCount <= 50, $"Expected at most 50 bytes, got {byteCount}")
            // No replacement characters (U+FFFD) means truncation respected codepoint boundaries
            Assert.DoesNotContain("\uFFFD", result.Output)
            // Output should be non-empty (we have plenty of data)
            Assert.True(result.Output.Length > 0, "Expected non-empty truncated output")
        | Error error -> Assert.Fail(AcpError.describe error)

    [<Fact>]
    let ``terminal wait_for_exit returns timed_out without losing later exit result`` () =
        let root = Helpers.createTempDir ()

        let delegateImpl =
            DefaultDelegate.createDefaultDelegate root PermissionStrategy.AutoApprove 4096

        let terminalId =
            match
                delegateImpl.TerminalCreate
                    { Command = "/bin/sh"
                      Args = [ "-c"; "sleep 1; exit 7" ]
                      WorkingDirectory = None
                      Environment = Map.empty }
                |> Async.RunSynchronously
            with
            | Ok result -> result.TerminalId
            | Error error ->
                Assert.Fail(AcpError.describe error)
                ""

        let first =
            delegateImpl.TerminalWaitForExit
                { TerminalId = terminalId
                  TimeoutMs = Some 100 }
            |> Async.RunSynchronously

        let second =
            delegateImpl.TerminalWaitForExit
                { TerminalId = terminalId
                  TimeoutMs = Some 3000 }
            |> Async.RunSynchronously

        match first with
        | Ok result ->
            Assert.True(result.TimedOut)
            Assert.True(result.ExitCode.IsNone)
        | Error error -> Assert.Fail(AcpError.describe error)

        match second with
        | Ok result ->
            Assert.False(result.TimedOut)
            Assert.Equal(Some 7, result.ExitCode)
        | Error error -> Assert.Fail(AcpError.describe error)

// ===========================================================================
// CLIENT TESTS
// ===========================================================================

module ClientTests =

    [<Fact>]
    let ``client initializes routes notifications and prompts successfully`` () =
        let clientTransport, serverTransport = Transport.createInMemoryPair ()
        let notification = TaskCompletionSource<string>()

        Helpers.startServer serverTransport (fun transport request ->
            async {
                match request.Method with
                | "initialize" ->
                    let payload =
                        JsonSerializer.SerializeToElement
                            {| protocolVersion = "2026-03-23"
                               capabilities = {| prompt = true |}
                               serverInfo = {| name = "test-server" |} |}
                        |> fun value -> value.Clone()

                    do! transport.Send(Codec.encodeResponse request.Id payload) |> Async.Ignore
                | "session/prompt" ->
                    do!
                        transport.Send(
                            Codec.encodeNotification
                                "agent/progress"
                                (Some(JsonSerializer.SerializeToElement({| count = 1 |}).Clone()))
                        )
                        |> Async.Ignore

                    let payload =
                        JsonSerializer.SerializeToElement
                            {| sessionId = "s-1"
                               content =
                                [ {| ``type`` = "text"
                                     text = "hello from acp" |} ]
                               stopReason = "completed" |}
                        |> fun value -> value.Clone()

                    do! transport.Send(Codec.encodeResponse request.Id payload) |> Async.Ignore
                | "session/cancel" ->
                    do!
                        transport.Send(
                            Codec.encodeResponse
                                request.Id
                                (JsonSerializer.SerializeToElement({| cancelled = true |}).Clone())
                        )
                        |> Async.Ignore
                | _ ->
                    do!
                        transport.Send(
                            Codec.encodeError
                                request.Id
                                { Code = -32601
                                  Message = "missing"
                                  Data = None }
                        )
                        |> Async.Ignore
            })

        let client = Client.create (fun _ -> Ok clientTransport)

        client.AddObserver(fun methodName parameters ->
            let count =
                parameters
                |> Option.map (fun value -> value.GetProperty("count").GetInt32())
                |> Option.defaultValue 0

            notification.TrySetResult($"{methodName}:{count}") |> ignore)

        let endpoint =
            { Transport = AcpTransportKind.InMemory
              Command = None
              Args = []
              Url = None
              Headers = Map.empty
              WorkingDirectory = None }

        let initialize =
            client.Connect(endpoint, AcpDelegate.denyAll, Some(TimeSpan.FromSeconds(1.0)))
            |> Async.RunSynchronously

        let prompt =
            client.Prompt("s-1", [ ContentBlock.text "hello" ], None, Some(TimeSpan.FromSeconds(1.0)))
            |> Async.RunSynchronously

        match initialize with
        | Ok result -> Assert.Equal("2026-03-23", result.ProtocolVersion)
        | Error error -> Assert.Fail(AcpError.describe error)

        match prompt with
        | Ok result ->
            Assert.Contains(
                "hello from acp",
                result.Content
                |> List.map (function
                    | ContentBlock.Text text -> text
                    | _ -> "")
                |> String.concat "\n"
            )
        | Error error -> Assert.Fail(AcpError.describe error)

        Assert.Equal("agent/progress:1", notification.Task.Result)
        client.Disconnect() |> Async.RunSynchronously

    [<Fact>]
    let ``client timeout triggers best-effort cancel`` () =
        let clientTransport, serverTransport = Transport.createInMemoryPair ()
        let cancelReceived = TaskCompletionSource<bool>()

        Helpers.startServer serverTransport (fun transport request ->
            async {
                match request.Method with
                | "initialize" ->
                    let payload =
                        JsonSerializer.SerializeToElement
                            {| protocolVersion = "2026-03-23"
                               capabilities = {| prompt = true |}
                               serverInfo = {| name = "timeout-server" |} |}
                        |> fun value -> value.Clone()

                    do! transport.Send(Codec.encodeResponse request.Id payload) |> Async.Ignore
                | "session/prompt" -> ()
                | "session/cancel" ->
                    cancelReceived.TrySetResult(true) |> ignore

                    do!
                        transport.Send(
                            Codec.encodeResponse
                                request.Id
                                (JsonSerializer.SerializeToElement({| cancelled = true |}).Clone())
                        )
                        |> Async.Ignore
                | _ -> ()
            })

        let client = Client.create (fun _ -> Ok clientTransport)

        let endpoint =
            { Transport = AcpTransportKind.InMemory
              Command = None
              Args = []
              Url = None
              Headers = Map.empty
              WorkingDirectory = None }

        client.Connect(endpoint, AcpDelegate.denyAll, Some(TimeSpan.FromSeconds(1.0)))
        |> Async.RunSynchronously
        |> ignore

        match
            client.Prompt("slow-session", [ ContentBlock.text "slow" ], None, Some(TimeSpan.FromMilliseconds(500.0)))
            |> Async.RunSynchronously
        with
        | Error(AcpError.TimedOut _) -> ()
        | other -> Assert.Fail($"Unexpected prompt result: {other}")

        Assert.True(cancelReceived.Task.Wait(TimeSpan.FromSeconds(5.0)))
        client.Disconnect() |> Async.RunSynchronously

    [<Fact>]
    let ``client cancel race where response arrives before cancel resolves as success`` () =
        let clientTransport, serverTransport = Transport.createInMemoryPair ()

        Helpers.startServer serverTransport (fun transport request ->
            async {
                match request.Method with
                | "initialize" ->
                    let payload =
                        JsonSerializer.SerializeToElement
                            {| protocolVersion = "2026-03-23"
                               capabilities = {| |}
                               serverInfo = {| name = "race-server" |} |}
                        |> fun value -> value.Clone()

                    do! transport.Send(Codec.encodeResponse request.Id payload) |> Async.Ignore
                | "session/prompt" ->
                    do! Async.Sleep(50)

                    let payload =
                        JsonSerializer.SerializeToElement
                            {| sessionId = "race-session"
                               content =
                                [ {| ``type`` = "text"
                                     text = "race winner" |} ]
                               stopReason = "completed" |}
                        |> fun value -> value.Clone()

                    do! transport.Send(Codec.encodeResponse request.Id payload) |> Async.Ignore
                | _ -> ()
            })

        let client = Client.create (fun _ -> Ok clientTransport)

        let endpoint =
            { Transport = AcpTransportKind.InMemory
              Command = None
              Args = []
              Url = None
              Headers = Map.empty
              WorkingDirectory = None }

        client.Connect(endpoint, AcpDelegate.denyAll, Some(TimeSpan.FromSeconds(2.0)))
        |> Async.RunSynchronously
        |> ignore

        let promptResult =
            client.Prompt("race-session", [ ContentBlock.text "hello" ], None, Some(TimeSpan.FromSeconds(5.0)))
            |> Async.RunSynchronously

        Assert.True(Result.isOk promptResult, $"Expected success, got {promptResult}")
        client.Disconnect() |> Async.RunSynchronously

    [<Fact>]
    let ``client returns AlreadyConnected on second connect without disconnect`` () =
        let clientTransport, serverTransport = Transport.createInMemoryPair ()

        Helpers.startServer serverTransport (fun transport request ->
            async {
                match request.Method with
                | "initialize" ->
                    let payload =
                        JsonSerializer.SerializeToElement
                            {| protocolVersion = "2026-03-23"
                               capabilities = {| |}
                               serverInfo = {| name = "connect-server" |} |}
                        |> fun value -> value.Clone()

                    do! transport.Send(Codec.encodeResponse request.Id payload) |> Async.Ignore
                | _ -> ()
            })

        let client = Client.create (fun _ -> Ok clientTransport)

        let endpoint =
            { Transport = AcpTransportKind.InMemory
              Command = None
              Args = []
              Url = None
              Headers = Map.empty
              WorkingDirectory = None }

        match
            client.Connect(endpoint, AcpDelegate.denyAll, Some(TimeSpan.FromSeconds(1.0)))
            |> Async.RunSynchronously
        with
        | Ok _ -> ()
        | Error error -> Assert.Fail(AcpError.describe error)

        match
            client.Connect(endpoint, AcpDelegate.denyAll, Some(TimeSpan.FromSeconds(1.0)))
            |> Async.RunSynchronously
        with
        | Error AcpError.AlreadyConnected -> ()
        | other -> Assert.Fail($"Unexpected second connect result: {other}")

        client.Disconnect() |> Async.RunSynchronously

// ===========================================================================
// HANDLER TESTS
// ===========================================================================

module HandlerTests =

    [<Fact>]
    let ``handler registry resolves acp agent via explicit type`` () =
        let registry =
            HandlerRegistry.CreateDefault(acpPermissionStrategy = PermissionStrategy.DenyAll)

        let node =
            { Id = "agent"
              Attributes = Map.ofList [ "type", AttrValue.String "acp.agent"; "shape", AttrValue.String "tab" ] }

        let handler = registry.Resolve(node)
        Assert.True(handler :? AcpHandlers.AcpAgentHandler)

    [<Fact>]
    let ``acp handler succeeds with in-memory transport`` () =
        let handler =
            AcpHandlers.AcpAgentHandler(permissionStrategy = PermissionStrategy.DenyAll) :> IHandler

        let node =
            Helpers.makeAcpNode
                [ "shape", "tab"
                  "type", "acp.agent"
                  "acp_transport", "memory"
                  "prompt", "Write a status update" ]

        let logsRoot = Helpers.createTempDir ()

        let outcome = handler.Execute(node, Context(), Helpers.makeGraph (), logsRoot)

        Assert.Equal(StageStatus.Success, outcome.Status)
        Assert.True(File.Exists(Path.Combine(logsRoot, "agent", "response.md")))
        Assert.True(File.Exists(Path.Combine(logsRoot, "agent", "acp_session.json")))
        Assert.Contains("Simulated ACP", File.ReadAllText(Path.Combine(logsRoot, "agent", "response.md")))
        Assert.True(outcome.ContextUpdates.ContainsKey("acp.output.agent"))
        Assert.True(outcome.ContextUpdates.ContainsKey("acp.session_id.agent"))
        Assert.True(outcome.ContextUpdates.ContainsKey("acp.notifications.agent.count"))

    [<Fact>]
    let ``acp handler stdio timeout fails with timeout message`` () =
        let handler =
            AcpHandlers.AcpAgentHandler(permissionStrategy = PermissionStrategy.DenyAll) :> IHandler

        let node =
            Helpers.makeAcpNode
                [ "shape", "tab"
                  "type", "acp.agent"
                  "acp_transport", "stdio"
                  "acp_command", "dotnet"
                  "acp_args_json", JsonSerializer.Serialize([ Helpers.fixtureDll (); "--slow" ])
                  "acp_timeout_ms", "50"
                  "prompt", "Take your time" ]

        let graph = Helpers.makeGraph ()
        let logsRoot = Helpers.createTempDir ()

        let outcome = handler.Execute(node, Context(), graph, logsRoot)

        Assert.Equal(StageStatus.Fail, outcome.Status)
        Assert.Contains("Timed out", outcome.FailureReason)
        Assert.True(File.Exists(Path.Combine(logsRoot, "agent", "acp_session.json")))

    [<Fact>]
    let ``acp handler stdio permission denial is recorded in artifact`` () =
        let handler =
            AcpHandlers.AcpAgentHandler(permissionStrategy = PermissionStrategy.DenyAll) :> IHandler

        let node =
            Helpers.makeAcpNode
                [ "shape", "tab"
                  "type", "acp.agent"
                  "acp_transport", "stdio"
                  "acp_command", "dotnet"
                  "acp_args_json", JsonSerializer.Serialize([ Helpers.fixtureDll (); "--deny-test" ])
                  "prompt", "Read a file if needed" ]

        let logsRoot = Helpers.createTempDir ()

        let outcome = handler.Execute(node, Context(), Helpers.makeGraph (), logsRoot)

        Assert.Equal(StageStatus.Success, outcome.Status)
        let artifact = File.ReadAllText(Path.Combine(logsRoot, "agent", "acp_session.json"))
        Assert.Contains("delegate_denials", artifact)
        Assert.Contains("filesystem/read_text_file", artifact)

// ===========================================================================
// DEPENDENCY TESTS
// ===========================================================================

module DependencyTests =

    [<Fact>]
    let ``AcpRuntime does NOT reference Attractor project`` () =
        let testAssemblyDir = Path.GetDirectoryName(typeof<AcpError>.Assembly.Location)

        let projectDir =
            Path.GetFullPath(Path.Combine(testAssemblyDir, "..", "..", "..", "..", "..", "src", "AcpRuntime"))

        let projectFile = Path.Combine(projectDir, "AcpRuntime.fsproj")

        if File.Exists(projectFile) then
            let content = File.ReadAllText(projectFile)
            Assert.DoesNotContain("Attractor.fsproj", content)

    [<Fact>]
    let ``AcpRuntime references JsonRpc`` () =
        let testAssemblyDir = Path.GetDirectoryName(typeof<AcpError>.Assembly.Location)

        let projectDir =
            Path.GetFullPath(Path.Combine(testAssemblyDir, "..", "..", "..", "..", "..", "src", "AcpRuntime"))

        let projectFile = Path.Combine(projectDir, "AcpRuntime.fsproj")

        if File.Exists(projectFile) then
            let content = File.ReadAllText(projectFile)
            Assert.Contains("JsonRpc", content)
