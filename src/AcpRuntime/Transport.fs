namespace AcpRuntime

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

type AcpTransport =
    { Connect: unit -> Async<Result<unit, AcpError>>
      Send: byte array -> Async<Result<unit, AcpError>>
      Receive: CancellationToken -> IAsyncEnumerable<byte array>
      Disconnect: unit -> Async<unit>
      IsConnected: unit -> bool }

type ParsedSseEvent =
    { Data: byte array
      EventType: string
      RetryMs: int option
      LastEventId: string option }

module Transport =

    [<Literal>]
    let DefaultMaxMessageSize = 1024 * 1024

    type private InMemoryPairState =
        { SyncRoot: obj
          LeftToRight: Channel<byte array>
          RightToLeft: Channel<byte array>
          mutable Closed: bool }

    type private InMemoryEndpointState = { mutable Connected: bool }

    let private cloneBytes (payload: byte array) = Array.copy payload

    let private buildProcessStartInfo (command: string) (args: string list) (workingDirectory: string option) =
        let psi =
            if Path.IsPathRooted(command) then
                ProcessStartInfo(command)
            else
                let info = ProcessStartInfo("/usr/bin/env")
                info.ArgumentList.Add(command)
                info

        for arg in args do
            psi.ArgumentList.Add(arg)

        match workingDirectory with
        | Some path when not (String.IsNullOrWhiteSpace(path)) -> psi.WorkingDirectory <- path
        | _ -> ()

        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.StandardOutputEncoding <- Encoding.UTF8
        psi.StandardErrorEncoding <- Encoding.UTF8
        psi

    let private startStderrDrain (proc: Process) (buffer: StringBuilder) =
        Task.Run(fun () ->
            task {
                let mutable keepReading = true

                while keepReading do
                    let! line = proc.StandardError.ReadLineAsync()

                    if isNull line then
                        keepReading <- false
                    else
                        lock buffer (fun () -> buffer.AppendLine(line) |> ignore)
            }
            :> Task)

    let private observeTaskFault (pending: Task) =
        pending.ContinueWith(
            (fun (completed: Task) ->
                if completed.IsFaulted then
                    completed.Exception |> ignore),
            TaskContinuationOptions.ExecuteSynchronously
        )
        |> ignore

    let private terminateProcess (proc: Process) =
        async {
            try
                use killer =
                    Process.Start(ProcessStartInfo("/bin/kill", $"-TERM {proc.Id}", UseShellExecute = false))

                if not (isNull killer) then
                    do! killer.WaitForExitAsync() |> Async.AwaitTask
            with _ ->
                ()
        }

    let private channelReceive (reader: ChannelReader<byte array>) (ct: CancellationToken) =
        { new IAsyncEnumerable<byte array> with
            member _.GetAsyncEnumerator(enumeratorCancellationToken) =
                let effectiveCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(ct, enumeratorCancellationToken)

                let effectiveToken = effectiveCancellation.Token
                let mutable current: byte array = null
                let mutable completed = false

                { new IAsyncEnumerator<byte array> with
                    member _.Current = current

                    member _.MoveNextAsync() =
                        let moveNext: Task<bool> =
                            task {
                                let mutable found = false

                                try
                                    while not found && not completed && not effectiveToken.IsCancellationRequested do
                                        let! canRead = reader.WaitToReadAsync(effectiveToken).AsTask()

                                        if not canRead then
                                            completed <- true
                                        else
                                            let mutable item = Unchecked.defaultof<byte array>

                                            if reader.TryRead(&item) then
                                                current <- item
                                                found <- true

                                    return found
                                with :? OperationCanceledException when effectiveToken.IsCancellationRequested ->
                                    completed <- true
                                    return false
                            }

                        ValueTask<bool>(moveNext)

                    member _.DisposeAsync() =
                        completed <- true
                        effectiveCancellation.Cancel()
                        effectiveCancellation.Dispose()
                        ValueTask() } }

    let private emptyReceive<'T> () =
        { new IAsyncEnumerable<'T> with
            member _.GetAsyncEnumerator(_) =
                { new IAsyncEnumerator<'T> with
                    member _.Current = Unchecked.defaultof<'T>
                    member _.MoveNextAsync() = ValueTask<bool>(false)
                    member _.DisposeAsync() = ValueTask() } }

    let createInMemoryPair () =
        let pairState =
            { SyncRoot = obj ()
              LeftToRight = Channel.CreateUnbounded<byte array>()
              RightToLeft = Channel.CreateUnbounded<byte array>()
              Closed = false }

        let createTransport
            (localState: InMemoryEndpointState)
            (incoming: ChannelReader<byte array>)
            (outgoing: ChannelWriter<byte array>)
            =
            { Connect =
                fun () ->
                    async {
                        return
                            lock pairState.SyncRoot (fun () ->
                                if pairState.Closed then
                                    Error AcpError.TransportClosed
                                elif localState.Connected then
                                    Error AcpError.AlreadyConnected
                                else
                                    localState.Connected <- true
                                    Ok())
                    }
              Send =
                fun payload ->
                    async {
                        let writable =
                            lock pairState.SyncRoot (fun () ->
                                if pairState.Closed then
                                    Error AcpError.TransportClosed
                                elif not localState.Connected then
                                    Error AcpError.NotConnected
                                else
                                    Ok())

                        match writable with
                        | Error error -> return Error error
                        | Ok() ->
                            try
                                do! outgoing.WriteAsync(cloneBytes payload).AsTask() |> Async.AwaitTask
                                return Ok()
                            with :? ChannelClosedException ->
                                return Error AcpError.TransportClosed
                    }
              Receive =
                fun ct ->
                    if localState.Connected then
                        channelReceive incoming ct
                    else
                        emptyReceive ()
              Disconnect =
                fun () ->
                    async {
                        let shouldClose =
                            lock pairState.SyncRoot (fun () ->
                                localState.Connected <- false

                                if pairState.Closed then
                                    false
                                else
                                    pairState.Closed <- true
                                    true)

                        if shouldClose then
                            pairState.LeftToRight.Writer.TryComplete() |> ignore
                            pairState.RightToLeft.Writer.TryComplete() |> ignore
                    }
              IsConnected = fun () -> lock pairState.SyncRoot (fun () -> localState.Connected && not pairState.Closed) }

        let leftState = { Connected = false }
        let rightState = { Connected = false }

        createTransport leftState pairState.RightToLeft.Reader pairState.LeftToRight.Writer,
        createTransport rightState pairState.LeftToRight.Reader pairState.RightToLeft.Writer

    /// Default: 2 minutes between non-heartbeat events. Heartbeat-only traffic
    /// (SSE comment lines `:foo`, `event: ping|keepalive|heartbeat`) will not
    /// refresh this budget, so a server that sends only pings will eventually
    /// raise TimeoutException.
    let private tryParsePositiveMillisecondsFromSecondsEnvVar (envVarName: string) : int option =
        let rawValue = Environment.GetEnvironmentVariable(envVarName)

        if String.IsNullOrWhiteSpace(rawValue) then
            None
        else
            match
                Double.TryParse(rawValue, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture)
            with
            | true, seconds when seconds > 0.0 ->
                let milliseconds = seconds * 1000.0

                if milliseconds <= float Int32.MaxValue then
                    Some(int milliseconds)
                else
                    None
            | _ -> None

    let private resolveDefaultSseIdleTimeoutMs () : int =
        tryParsePositiveMillisecondsFromSecondsEnvVar "ATTRACTOR_LLM_INACTIVITY_TIMEOUT_SECONDS"
        |> Option.orElseWith (fun () ->
            tryParsePositiveMillisecondsFromSecondsEnvVar "ATTRACTOR_AGENT_INACTIVITY_TIMEOUT_SECONDS")
        |> Option.defaultValue 120_000

    let DefaultSseIdleTimeoutMs () : int = resolveDefaultSseIdleTimeoutMs ()

    let private isHeartbeatEventType (eventType: string) =
        match eventType with
        | "ping"
        | "keepalive"
        | "heartbeat" -> true
        | _ -> false

    let parseSseStreamWithIdleTimeout
        (stream: Stream)
        (ct: CancellationToken)
        (idleTimeoutMs: int)
        : IAsyncEnumerable<ParsedSseEvent> =
        { new IAsyncEnumerable<ParsedSseEvent> with
            member _.GetAsyncEnumerator(enumeratorCancellationToken) =
                let reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true)

                let effectiveCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(ct, enumeratorCancellationToken)

                let effectiveToken = effectiveCancellation.Token
                let dataLines = ResizeArray<string>()
                let mutable eventType = "message"
                let mutable retryMs: int option = None
                let mutable lastEventId: string option = None
                let mutable seenField = false
                let progress = Stopwatch.StartNew()
                let mutable keepReading = true
                let mutable current = Unchecked.defaultof<ParsedSseEvent>

                let emitEvent () =
                    if seenField then
                        let payload = String.concat "\n" (dataLines |> Seq.toList)

                        let eventData =
                            { Data = Encoding.UTF8.GetBytes(payload)
                              EventType = eventType
                              RetryMs = retryMs
                              LastEventId = lastEventId }

                        let wasHeartbeat = isHeartbeatEventType eventType
                        dataLines.Clear()
                        eventType <- "message"
                        retryMs <- None
                        lastEventId <- None
                        seenField <- false

                        if not wasHeartbeat then
                            progress.Restart()

                        Some eventData
                    else
                        None

                let parseField (line: string) =
                    let index = line.IndexOf(':')

                    if index < 0 then
                        line, ""
                    else
                        let rawValue = line[(index + 1) ..]

                        let value =
                            if rawValue.StartsWith(" ", StringComparison.Ordinal) then
                                rawValue.Substring(1)
                            else
                                rawValue

                        line[.. (index - 1)], value

                { new IAsyncEnumerator<ParsedSseEvent> with
                    member _.Current = current

                    member _.MoveNextAsync() =
                        let moveNext: Task<bool> =
                            task {
                                let mutable nextEvent: ParsedSseEvent option = None

                                while keepReading && nextEvent.IsNone && not effectiveToken.IsCancellationRequested do
                                    let remaining = idleTimeoutMs - int progress.ElapsedMilliseconds

                                    if remaining <= 0 then
                                        raise (
                                            TimeoutException(
                                                sprintf
                                                    "SSE stream idle timeout (no progress within %dms)"
                                                    idleTimeoutMs
                                            )
                                        )

                                    use idleCts = new CancellationTokenSource()
                                    idleCts.CancelAfter(remaining)

                                    use linked =
                                        CancellationTokenSource.CreateLinkedTokenSource(effectiveToken, idleCts.Token)

                                    let mutable line: string = null
                                    let mutable idleFired = false

                                    try
                                        let! read = reader.ReadLineAsync(linked.Token).AsTask()
                                        line <- read
                                    with :? OperationCanceledException ->
                                        if effectiveToken.IsCancellationRequested then
                                            keepReading <- false
                                        else
                                            idleFired <- true

                                    if idleFired then
                                        raise (
                                            TimeoutException(
                                                sprintf
                                                    "SSE stream idle timeout (no progress within %dms)"
                                                    idleTimeoutMs
                                            )
                                        )

                                    if keepReading then
                                        if isNull line then
                                            keepReading <- false
                                            nextEvent <- emitEvent ()
                                        elif line = "" then
                                            nextEvent <- emitEvent ()
                                        elif line.StartsWith(":", StringComparison.Ordinal) then
                                            ()
                                        else
                                            let fieldName, value = parseField line

                                            match fieldName with
                                            | "data" ->
                                                seenField <- true
                                                dataLines.Add(value)
                                            | "event" ->
                                                seenField <- true
                                                eventType <- if value = "" then "message" else value
                                            | "id" ->
                                                seenField <- true
                                                lastEventId <- Some value
                                            | "retry" ->
                                                match Int32.TryParse(value) with
                                                | true, parsed ->
                                                    seenField <- true
                                                    retryMs <- Some parsed
                                                | _ -> ()
                                            | _ -> ()

                                match nextEvent with
                                | Some eventData ->
                                    current <- eventData
                                    return true
                                | None -> return false
                            }

                        ValueTask<bool>(moveNext)

                    member _.DisposeAsync() =
                        keepReading <- false
                        reader.Dispose()
                        effectiveCancellation.Cancel()
                        effectiveCancellation.Dispose()
                        ValueTask() } }

    let parseSseStream (stream: Stream) (ct: CancellationToken) : IAsyncEnumerable<ParsedSseEvent> =
        parseSseStreamWithIdleTimeout stream ct (DefaultSseIdleTimeoutMs())

    let createStdioTransport (command: string) (args: string list) (workingDirectory: string option) =
        let syncRoot = obj ()
        let stderrBuffer = StringBuilder()
        let mutable activeProcess: Process option = None
        let mutable stderrDrain: Task option = None
        let mutable lastError: AcpError option = None

        let setClosed error =
            lock syncRoot (fun () -> lastError <- Some error)

        let currentProcess () = lock syncRoot (fun () -> activeProcess)

        let stateError () = lock syncRoot (fun () -> lastError)

        let clearState () =
            lock syncRoot (fun () ->
                activeProcess <- None
                stderrDrain <- None)

        { Connect =
            fun () ->
                async {
                    match currentProcess (), stateError () with
                    | Some proc, _ when not proc.HasExited -> return Error AcpError.AlreadyConnected
                    | _, Some(AcpError.ProcessExited exitCode) -> return Error(AcpError.ProcessExited exitCode)
                    | _ ->
                        try
                            let psi = buildProcessStartInfo command args workingDirectory
                            let proc = new Process(StartInfo = psi, EnableRaisingEvents = true)
                            proc.Exited.Add(fun _ -> setClosed (AcpError.ProcessExited proc.ExitCode))

                            if not (proc.Start()) then
                                return Error(AcpError.InvalidPayload $"Failed to start command '{command}'")
                            else
                                let drainTask = startStderrDrain proc stderrBuffer

                                if proc.WaitForExit(200) then
                                    let details = lock stderrBuffer (fun () -> stderrBuffer.ToString().Trim())

                                    let reason =
                                        if String.IsNullOrWhiteSpace(details) then
                                            $"Command '{command}' exited during startup"
                                        else
                                            details

                                    setClosed (AcpError.ProcessExited proc.ExitCode)
                                    clearState ()
                                    proc.Dispose()
                                    return Error(AcpError.InvalidPayload reason)
                                else
                                    lock syncRoot (fun () ->
                                        lastError <- None
                                        activeProcess <- Some proc
                                        stderrDrain <- Some drainTask)

                                    return Ok()
                        with ex ->
                            return Error(AcpError.InvalidPayload ex.Message)
                }
          Send =
            fun payload ->
                async {
                    match currentProcess (), stateError () with
                    | _, Some error -> return Error error
                    | None, _ -> return Error AcpError.NotConnected
                    | Some proc, _ when proc.HasExited -> return Error(AcpError.ProcessExited proc.ExitCode)
                    | Some proc, _ ->
                        try
                            let text = Encoding.UTF8.GetString(payload)
                            do! proc.StandardInput.WriteLineAsync(text) |> Async.AwaitTask
                            do! proc.StandardInput.FlushAsync() |> Async.AwaitTask
                            return Ok()
                        with ex ->
                            setClosed AcpError.TransportClosed
                            return Error(AcpError.InvalidPayload ex.Message)
                }
          Receive =
            fun ct ->
                { new IAsyncEnumerable<byte array> with
                    member _.GetAsyncEnumerator(enumeratorCancellationToken) =
                        let effectiveCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(ct, enumeratorCancellationToken)

                        let effectiveToken = effectiveCancellation.Token
                        let proc = currentProcess ()
                        let mutable current: byte array = null
                        let mutable completed = false

                        { new IAsyncEnumerator<byte array> with
                            member _.Current = current

                            member _.MoveNextAsync() =
                                let moveNext: Task<bool> =
                                    task {
                                        if completed || effectiveToken.IsCancellationRequested then
                                            return false
                                        else
                                            match proc with
                                            | None ->
                                                completed <- true
                                                return false
                                            | Some activeProcess ->
                                                let readTask = activeProcess.StandardOutput.ReadLineAsync()

                                                let cancellationSignal =
                                                    TaskCompletionSource<unit>(
                                                        TaskCreationOptions.RunContinuationsAsynchronously
                                                    )

                                                use cancellationRegistration =
                                                    effectiveToken.Register(fun () ->
                                                        cancellationSignal.TrySetResult(()) |> ignore)

                                                let! winner =
                                                    Task.WhenAny(readTask :> Task, cancellationSignal.Task :> Task)

                                                if Object.ReferenceEquals(winner, readTask) then
                                                    let! line = readTask

                                                    if isNull line then
                                                        completed <- true

                                                        if activeProcess.HasExited then
                                                            setClosed (AcpError.ProcessExited activeProcess.ExitCode)
                                                        else
                                                            setClosed AcpError.TransportClosed

                                                        return false
                                                    else
                                                        let payload = Encoding.UTF8.GetBytes(line)

                                                        if payload.Length > DefaultMaxMessageSize then
                                                            completed <- true

                                                            setClosed (
                                                                AcpError.InvalidPayload
                                                                    $"Stdio message exceeded {DefaultMaxMessageSize} bytes"
                                                            )

                                                            return false
                                                        else
                                                            current <- payload
                                                            return true
                                                else
                                                    observeTaskFault readTask
                                                    completed <- true
                                                    return false
                                    }

                                ValueTask<bool>(moveNext)

                            member _.DisposeAsync() =
                                completed <- true
                                effectiveCancellation.Cancel()
                                effectiveCancellation.Dispose()
                                ValueTask() } }
          Disconnect =
            fun () ->
                async {
                    match currentProcess () with
                    | None -> ()
                    | Some proc ->
                        try
                            try
                                proc.StandardInput.Close()
                            with _ ->
                                ()

                            if not proc.HasExited then
                                do! terminateProcess proc

                                if not (proc.WaitForExit(5000)) then
                                    try
                                        proc.Kill(true)
                                    with _ ->
                                        ()

                            match lock syncRoot (fun () -> stderrDrain) with
                            | Some drain -> do! drain |> Async.AwaitTask
                            | None -> ()
                        with _ ->
                            ()

                        try
                            proc.Dispose()
                        with _ ->
                            ()

                        clearState ()
                        setClosed AcpError.TransportClosed
                }
          IsConnected =
            fun () ->
                match currentProcess (), stateError () with
                | Some proc, None -> not proc.HasExited
                | _ -> false }

    let createWebSocketTransport (url: string) (headers: Map<string, string>) =
        let mutable socket: ClientWebSocket option = None

        let currentSocket () = socket

        { Connect =
            fun () ->
                async {
                    match currentSocket () with
                    | Some existing when existing.State = WebSocketState.Open -> return Error AcpError.AlreadyConnected
                    | _ ->
                        let created = new ClientWebSocket()

                        for KeyValue(key, value) in headers do
                            created.Options.SetRequestHeader(key, value)

                        do! created.ConnectAsync(Uri(url), CancellationToken.None) |> Async.AwaitTask
                        socket <- Some created
                        return Ok()
                }
          Send =
            fun payload ->
                async {
                    match currentSocket () with
                    | Some ws when ws.State = WebSocketState.Open ->
                        do!
                            ws.SendAsync(ArraySegment(payload), WebSocketMessageType.Text, true, CancellationToken.None)
                            |> Async.AwaitTask

                        return Ok()
                    | _ -> return Error AcpError.NotConnected
                }
          Receive =
            fun ct ->
                { new IAsyncEnumerable<byte array> with
                    member _.GetAsyncEnumerator(enumeratorCancellationToken) =
                        let effectiveCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(ct, enumeratorCancellationToken)

                        let effectiveToken = effectiveCancellation.Token

                        let socket =
                            currentSocket () |> Option.filter (fun ws -> ws.State = WebSocketState.Open)

                        let buffer = Array.zeroCreate<byte> 4096
                        let mutable current: byte array = null
                        let mutable completed = false

                        { new IAsyncEnumerator<byte array> with
                            member _.Current = current

                            member _.MoveNextAsync() =
                                let moveNext: Task<bool> =
                                    task {
                                        match socket with
                                        | None ->
                                            completed <- true
                                            return false
                                        | Some ws when completed || effectiveToken.IsCancellationRequested ->
                                            return false
                                        | Some ws ->
                                            use memory = new MemoryStream()
                                            let mutable finished = false
                                            let mutable shouldYield = false

                                            try
                                                while not finished && not effectiveToken.IsCancellationRequested do
                                                    let! result = ws.ReceiveAsync(ArraySegment(buffer), effectiveToken)

                                                    match result.MessageType with
                                                    | WebSocketMessageType.Close ->
                                                        completed <- true
                                                        finished <- true
                                                    | _ ->
                                                        memory.Write(buffer, 0, result.Count)

                                                        if memory.Length > int64 DefaultMaxMessageSize then
                                                            finished <- true
                                                            completed <- true
                                                        elif result.EndOfMessage then
                                                            finished <- true
                                                            shouldYield <- true

                                                if shouldYield then
                                                    current <- memory.ToArray()
                                                    return true
                                                else
                                                    return false
                                            with :? OperationCanceledException when
                                                effectiveToken.IsCancellationRequested ->
                                                completed <- true
                                                return false
                                    }

                                ValueTask<bool>(moveNext)

                            member _.DisposeAsync() =
                                completed <- true
                                effectiveCancellation.Cancel()
                                effectiveCancellation.Dispose()
                                ValueTask() } }
          Disconnect =
            fun () ->
                async {
                    match currentSocket () with
                    | Some ws ->
                        try
                            if ws.State = WebSocketState.Open || ws.State = WebSocketState.CloseReceived then
                                do!
                                    ws.CloseAsync(
                                        WebSocketCloseStatus.NormalClosure,
                                        "disconnect",
                                        CancellationToken.None
                                    )
                                    |> Async.AwaitTask
                        with _ ->
                            ()

                        ws.Dispose()
                        socket <- None
                    | None -> ()
                }
          IsConnected =
            fun () ->
                match currentSocket () with
                | Some ws -> ws.State = WebSocketState.Open
                | None -> false }

    let createHttpSseTransport (url: string) (requestUrlOpt: string option) (headers: Map<string, string>) =
        let requestUrl = defaultArg requestUrlOpt url
        let mutable lastEventId: string option = None
        let mutable client: HttpClient option = None

        let ensureClient () =
            match client with
            | Some existing -> existing
            | None ->
                let created = new HttpClient()
                client <- Some created
                created

        let applyHeaders (request: HttpRequestMessage) =
            for KeyValue(key, value) in headers do
                request.Headers.TryAddWithoutValidation(key, value) |> ignore

        { Connect =
            fun () ->
                async {
                    ensureClient () |> ignore
                    return Ok()
                }
          Send =
            fun payload ->
                async {
                    try
                        let httpClient = ensureClient ()
                        use request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                        applyHeaders request
                        request.Content <- new ByteArrayContent(payload)
                        request.Content.Headers.ContentType <- MediaTypeHeaderValue("application/json")
                        let! response = httpClient.SendAsync(request) |> Async.AwaitTask
                        use response = response

                        if response.IsSuccessStatusCode then
                            return Ok()
                        else
                            return Error AcpError.TransportClosed
                    with
                    | :? TaskCanceledException as ex -> return Error(AcpError.TimedOut ex.Message)
                    | ex -> return Error(AcpError.InvalidPayload ex.Message)
                }
          Receive =
            fun ct ->
                { new IAsyncEnumerable<byte array> with
                    member _.GetAsyncEnumerator(enumeratorCancellationToken) =
                        let effectiveCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(ct, enumeratorCancellationToken)

                        let effectiveToken = effectiveCancellation.Token
                        let mutable request: HttpRequestMessage = null
                        let mutable response: HttpResponseMessage = null
                        let mutable stream: Stream = null
                        let mutable events: IAsyncEnumerator<ParsedSseEvent> = null
                        let mutable current: byte array = null
                        let mutable initialized = false
                        let mutable completed = false
                        let mutable jsonResponse = false

                        let initialize () : Task =
                            task {
                                match client with
                                | Some httpClient ->
                                    request <- new HttpRequestMessage(HttpMethod.Get, url)
                                    request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("text/event-stream"))
                                    applyHeaders request

                                    match lastEventId with
                                    | Some value when value <> "" ->
                                        request.Headers.TryAddWithoutValidation("Last-Event-ID", value) |> ignore
                                    | _ -> ()

                                    let! received =
                                        httpClient.SendAsync(
                                            request,
                                            HttpCompletionOption.ResponseHeadersRead,
                                            effectiveToken
                                        )

                                    response <- received
                                    response.EnsureSuccessStatusCode() |> ignore
                                    let! receivedStream = response.Content.ReadAsStreamAsync(effectiveToken)
                                    stream <- receivedStream

                                    let mediaType =
                                        if isNull response.Content.Headers.ContentType then
                                            ""
                                        else
                                            response.Content.Headers.ContentType.MediaType

                                    jsonResponse <-
                                        String.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)

                                    if not jsonResponse then
                                        events <-
                                            (parseSseStream stream effectiveToken).GetAsyncEnumerator(effectiveToken)
                                | None -> completed <- true

                                initialized <- true
                            }

                        { new IAsyncEnumerator<byte array> with
                            member _.Current = current

                            member _.MoveNextAsync() =
                                let moveNext: Task<bool> =
                                    task {
                                        if not initialized then
                                            do! initialize ()

                                        if completed || effectiveToken.IsCancellationRequested then
                                            return false
                                        elif jsonResponse then
                                            use memory = new MemoryStream()
                                            do! stream.CopyToAsync(memory, effectiveToken)
                                            completed <- true

                                            if memory.Length <= int64 DefaultMaxMessageSize then
                                                current <- memory.ToArray()
                                                return true
                                            else
                                                return false
                                        else
                                            let mutable found = false

                                            while not found
                                                  && not completed
                                                  && not effectiveToken.IsCancellationRequested do
                                                let! hasEvent = events.MoveNextAsync().AsTask()

                                                if hasEvent then
                                                    let eventData = events.Current
                                                    lastEventId <- eventData.LastEventId |> Option.orElse lastEventId

                                                    if eventData.Data.Length <= DefaultMaxMessageSize then
                                                        current <- eventData.Data
                                                        found <- true
                                                else
                                                    completed <- true

                                            return found
                                    }

                                ValueTask<bool>(moveNext)

                            member _.DisposeAsync() =
                                let dispose: Task =
                                    task {
                                        completed <- true
                                        effectiveCancellation.Cancel()

                                        if not (isNull events) then
                                            do! events.DisposeAsync().AsTask()

                                        if not (isNull stream) then
                                            stream.Dispose()

                                        if not (isNull response) then
                                            response.Dispose()

                                        if not (isNull request) then
                                            request.Dispose()

                                        effectiveCancellation.Dispose()
                                    }

                                ValueTask(dispose) } }
          Disconnect =
            fun () ->
                async {
                    match client with
                    | Some httpClient ->
                        httpClient.Dispose()
                        client <- None
                    | None -> ()
                }
          IsConnected = fun () -> client.IsSome }
