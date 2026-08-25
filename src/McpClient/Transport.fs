namespace McpClient

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading
open System.Threading.Tasks
open FSharp.Control

type ParsedSseEvent =
    { Data: byte array
      EventType: string
      RetryMs: int option
      LastEventId: string option }

module Transport =

    let private buildProcessStartInfo (command: string) (args: string list) (env: Map<string, string>) =
        let psi =
            if Path.IsPathRooted(command: string) then
                ProcessStartInfo(command)
            else
                let info = ProcessStartInfo("/usr/bin/env")
                info.ArgumentList.Add(command)
                info

        for arg in args do
            psi.ArgumentList.Add(arg)

        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.StandardOutputEncoding <- Encoding.UTF8
        psi.StandardErrorEncoding <- Encoding.UTF8

        for KeyValue(key, value) in env do
            psi.EnvironmentVariables[key] <- value

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

    let private stderrText (buffer: StringBuilder) =
        lock buffer (fun () -> buffer.ToString().Trim())

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
        (cancellationToken: CancellationToken)
        (idleTimeoutMs: int)
        : IAsyncEnumerable<ParsedSseEvent> =
        { new IAsyncEnumerable<ParsedSseEvent> with
            member _.GetAsyncEnumerator(enumeratorCancellationToken) =
                let reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true)

                let effectiveCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, enumeratorCancellationToken)

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

                        let emitted =
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

                        Some emitted
                    else
                        None

                let parseField (line: string) =
                    let index = line.IndexOf(':')

                    if index < 0 then
                        line, ""
                    else
                        let value = line[(index + 1) ..]

                        let normalized =
                            if value.StartsWith(" ", StringComparison.Ordinal) then
                                value.Substring(1)
                            else
                                value

                        line[.. (index - 1)], normalized

                { new IAsyncEnumerator<ParsedSseEvent> with
                    member _.Current = current

                    member _.MoveNextAsync() =
                        let moveNext: Task<bool> =
                            task {
                                let mutable nextEvent: ParsedSseEvent option = None

                                while keepReading && nextEvent.IsNone && not effectiveToken.IsCancellationRequested do
                                    let elapsed = int progress.ElapsedMilliseconds
                                    let remaining = idleTimeoutMs - elapsed

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
                                            // SSE comment — consume, do not count as progress.
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
                        effectiveCancellation.Dispose()
                        ValueTask() } }

    let parseSseStream (stream: Stream) (cancellationToken: CancellationToken) : IAsyncEnumerable<ParsedSseEvent> =
        parseSseStreamWithIdleTimeout stream cancellationToken (DefaultSseIdleTimeoutMs())

    let createStdioTransport command args env =
        let syncRoot = obj ()
        let stderrBuffer = StringBuilder()
        let mutable activeProcess: Process option = None
        let mutable stderrDrain: Task option = None

        let currentProcess () = lock syncRoot (fun () -> activeProcess)

        let clearState () =
            lock syncRoot (fun () ->
                activeProcess <- None
                stderrDrain <- None)

        { Connect =
            fun () ->
                async {
                    match currentProcess () with
                    | Some proc when not proc.HasExited -> return Ok()
                    | _ ->
                        try
                            let psi = buildProcessStartInfo command args env
                            let proc = new Process(StartInfo = psi)

                            if not (proc.Start()) then
                                return Error(McpError.InvalidConfiguration $"Failed to start command '{command}'")
                            else
                                let drainTask = startStderrDrain proc stderrBuffer

                                if proc.WaitForExit(200) then
                                    let details = stderrText stderrBuffer

                                    let reason =
                                        if details = "" then
                                            $"Command '{command}' exited during startup"
                                        else
                                            details

                                    clearState ()
                                    proc.Dispose()
                                    return Error(McpError.InvalidConfiguration reason)
                                else
                                    lock syncRoot (fun () ->
                                        activeProcess <- Some proc
                                        stderrDrain <- Some drainTask)

                                    return Ok()
                        with ex ->
                            return Error(McpError.InvalidConfiguration ex.Message)
                }
          Send =
            fun payload ->
                async {
                    match currentProcess () with
                    | None -> return Error McpError.NotConnected
                    | Some proc when proc.HasExited -> return Error(McpError.ProcessExited proc.ExitCode)
                    | Some proc ->
                        try
                            let text = Encoding.UTF8.GetString(payload)
                            do! proc.StandardInput.WriteLineAsync(text) |> Async.AwaitTask
                            do! proc.StandardInput.FlushAsync() |> Async.AwaitTask
                            return Ok()
                        with ex ->
                            return Error(McpError.TransportClosed ex.Message)
                }
          Receive =
            fun cancellationToken ->
                taskSeq {
                    match currentProcess () with
                    | Some proc ->
                        let mutable keepReading = true

                        while keepReading && not cancellationToken.IsCancellationRequested do
                            let! line = proc.StandardOutput.ReadLineAsync()

                            if isNull line then
                                keepReading <- false
                            else
                                yield Encoding.UTF8.GetBytes(line)
                    | None -> ()
                }
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

                            proc.Dispose()
                        with _ ->
                            ()

                        clearState ()
                } }

    let createHttpSseTransport (url: string) (requestUrlOpt: string option) (headers: Map<string, string>) =
        let requestUrl = defaultArg requestUrlOpt url
        let mutable lastEventId: string option = None
        let mutable retryDelayMs: int option = None
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
                request.Headers.TryAddWithoutValidation((key: string), (value: string))
                |> ignore

        let contentTypeEquals mediaType (value: string) =
            String.Equals(mediaType, value, StringComparison.OrdinalIgnoreCase)

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
                        use request = new HttpRequestMessage(HttpMethod.Post, (requestUrl: string))
                        applyHeaders request
                        request.Content <- new ByteArrayContent(payload)
                        request.Content.Headers.ContentType <- MediaTypeHeaderValue("application/json")
                        let! response = httpClient.SendAsync(request) |> Async.AwaitTask
                        use response = response

                        if response.IsSuccessStatusCode then
                            return Ok()
                        else
                            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                            return
                                Error(
                                    McpError.TransportClosed
                                        $"HTTP {(int response.StatusCode)} {response.ReasonPhrase}: {body}"
                                )
                    with
                    | :? TaskCanceledException as ex -> return Error(McpError.Timeout ex.Message)
                    | ex -> return Error(McpError.TransportClosed ex.Message)
                }
          Receive =
            fun cancellationToken ->
                { new IAsyncEnumerable<byte array> with
                    member _.GetAsyncEnumerator(enumeratorCancellationToken) =
                        let effectiveCancellation =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                cancellationToken,
                                enumeratorCancellationToken
                            )

                        let effectiveToken = effectiveCancellation.Token
                        let mutable request: HttpRequestMessage = null
                        let mutable response: HttpResponseMessage = null
                        let mutable stream: Stream = null
                        let mutable events: IAsyncEnumerator<ParsedSseEvent> = null
                        let mutable current: byte array = null
                        let mutable initialized = false
                        let mutable completed = false
                        let mutable jsonResponse = false

                        let initialize: Task =
                            task {
                                match client with
                                | Some httpClient ->
                                    request <- new HttpRequestMessage(HttpMethod.Get, (url: string))
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

                                    jsonResponse <- contentTypeEquals mediaType "application/json"

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
                                            do! initialize

                                        if completed || effectiveToken.IsCancellationRequested then
                                            return false
                                        elif jsonResponse then
                                            use memory = new MemoryStream()
                                            do! stream.CopyToAsync(memory, effectiveToken)
                                            current <- memory.ToArray()
                                            completed <- true
                                            return true
                                        else
                                            let! hasEvent = events.MoveNextAsync().AsTask()

                                            if hasEvent then
                                                let eventData = events.Current
                                                lastEventId <- eventData.LastEventId |> Option.orElse lastEventId
                                                retryDelayMs <- eventData.RetryMs |> Option.orElse retryDelayMs
                                                current <- eventData.Data
                                                return true
                                            else
                                                completed <- true
                                                return false
                                    }

                                ValueTask<bool>(moveNext)

                            member _.DisposeAsync() =
                                let dispose: Task =
                                    task {
                                        completed <- true

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
                } }
