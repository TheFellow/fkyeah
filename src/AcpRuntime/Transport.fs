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
open FSharp.Control

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

    type private InMemoryEndpointState =
        { mutable Connected: bool }

    let private cloneBytes (payload: byte array) =
        Array.copy payload

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

    let private terminateProcess (proc: Process) =
        async {
            try
                use killer = Process.Start(ProcessStartInfo("/bin/kill", $"-TERM {proc.Id}", UseShellExecute = false))
                if not (isNull killer) then
                    do! killer.WaitForExitAsync() |> Async.AwaitTask
            with _ ->
                ()
        }

    let private channelReceive (reader: ChannelReader<byte array>) (ct: CancellationToken) =
        taskSeq {
            try
                let mutable keepReading = true
                while keepReading && not ct.IsCancellationRequested do
                    let! canRead = reader.WaitToReadAsync(ct).AsTask()
                    if not canRead then
                        keepReading <- false
                    else
                        let mutable item = Unchecked.defaultof<byte array>
                        if reader.TryRead(&item) then
                            yield item
            with :? OperationCanceledException ->
                ()
        }

    let createInMemoryPair () =
        let pairState =
            { SyncRoot = obj ()
              LeftToRight = Channel.CreateUnbounded<byte array>()
              RightToLeft = Channel.CreateUnbounded<byte array>()
              Closed = false }

        let createTransport (localState: InMemoryEndpointState) (incoming: ChannelReader<byte array>) (outgoing: ChannelWriter<byte array>) =
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
                        | Ok () ->
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
                        taskSeq { () }
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
              IsConnected =
                fun () ->
                    lock pairState.SyncRoot (fun () -> localState.Connected && not pairState.Closed) }

        let leftState = { Connected = false }
        let rightState = { Connected = false }

        createTransport leftState pairState.RightToLeft.Reader pairState.LeftToRight.Writer,
        createTransport rightState pairState.LeftToRight.Reader pairState.RightToLeft.Writer

    let parseSseStream (stream: Stream) (ct: CancellationToken) : IAsyncEnumerable<ParsedSseEvent> =
        taskSeq {
            use reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true)

            let dataLines = ResizeArray<string>()
            let mutable eventType = "message"
            let mutable retryMs: int option = None
            let mutable lastEventId: string option = None
            let mutable seenField = false

            let emitEvent () =
                if seenField then
                    let payload = String.concat "\n" (dataLines |> Seq.toList)
                    let eventData =
                        { Data = Encoding.UTF8.GetBytes(payload)
                          EventType = eventType
                          RetryMs = retryMs
                          LastEventId = lastEventId }
                    dataLines.Clear()
                    eventType <- "message"
                    retryMs <- None
                    lastEventId <- None
                    seenField <- false
                    Some eventData
                else
                    None

            let parseField (line: string) =
                let index = line.IndexOf(':')
                if index < 0 then
                    line, ""
                else
                    let rawValue = line[(index + 1)..]
                    let value =
                        if rawValue.StartsWith(" ", StringComparison.Ordinal) then
                            rawValue.Substring(1)
                        else
                            rawValue
                    line[..(index - 1)], value

            let mutable keepReading = true

            while keepReading && not ct.IsCancellationRequested do
                let! line = reader.ReadLineAsync()
                if isNull line then
                    keepReading <- false
                    match emitEvent () with
                    | Some eventData -> yield eventData
                    | None -> ()
                elif line = "" then
                    match emitEvent () with
                    | Some eventData -> yield eventData
                    | None -> ()
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
        }

    let createStdioTransport (command: string) (args: string list) (workingDirectory: string option) =
        let syncRoot = obj ()
        let stderrBuffer = StringBuilder()
        let mutable activeProcess: Process option = None
        let mutable stderrDrain: Task option = None
        let mutable lastError: AcpError option = None

        let setClosed error =
            lock syncRoot (fun () -> lastError <- Some error)

        let currentProcess () =
            lock syncRoot (fun () -> activeProcess)

        let stateError () =
            lock syncRoot (fun () -> lastError)

        let clearState () =
            lock syncRoot (fun () ->
                activeProcess <- None
                stderrDrain <- None)

        { Connect =
            fun () ->
                async {
                    match currentProcess (), stateError () with
                    | Some proc, _ when not proc.HasExited -> return Error AcpError.AlreadyConnected
                    | _, Some(AcpError.ProcessExited exitCode) ->
                        return Error(AcpError.ProcessExited exitCode)
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
                                    let details =
                                        lock stderrBuffer (fun () -> stderrBuffer.ToString().Trim())
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
                    | Some proc, _ when proc.HasExited ->
                        return Error(AcpError.ProcessExited proc.ExitCode)
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
                taskSeq {
                    match currentProcess () with
                    | Some proc ->
                        let mutable keepReading = true
                        while keepReading && not ct.IsCancellationRequested do
                            let! line = proc.StandardOutput.ReadLineAsync()
                            if isNull line then
                                keepReading <- false
                                if proc.HasExited then
                                    setClosed (AcpError.ProcessExited proc.ExitCode)
                                else
                                    setClosed AcpError.TransportClosed
                            else
                                let payload = Encoding.UTF8.GetBytes(line)
                                if payload.Length > DefaultMaxMessageSize then
                                    keepReading <- false
                                    setClosed (AcpError.InvalidPayload $"Stdio message exceeded {DefaultMaxMessageSize} bytes")
                                else
                                    yield payload
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
                    | Some existing when existing.State = WebSocketState.Open ->
                        return Error AcpError.AlreadyConnected
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
                        do! ws.SendAsync(ArraySegment(payload), WebSocketMessageType.Text, true, CancellationToken.None) |> Async.AwaitTask
                        return Ok()
                    | _ -> return Error AcpError.NotConnected
                }
          Receive =
            fun ct ->
                taskSeq {
                    match currentSocket () with
                    | Some ws when ws.State = WebSocketState.Open ->
                        let buffer = Array.zeroCreate<byte> 4096
                        let mutable keepReading = true
                        while keepReading && not ct.IsCancellationRequested do
                            use ms = new MemoryStream()
                            let mutable finished = false
                            let mutable shouldYield = false
                            while not finished && not ct.IsCancellationRequested do
                                let! result = ws.ReceiveAsync(ArraySegment(buffer), ct)
                                match result.MessageType with
                                | WebSocketMessageType.Close ->
                                    keepReading <- false
                                    finished <- true
                                | _ ->
                                    ms.Write(buffer, 0, result.Count)
                                    if ms.Length > int64 DefaultMaxMessageSize then
                                        finished <- true
                                        keepReading <- false
                                    elif result.EndOfMessage then
                                        finished <- true
                                        shouldYield <- true
                            if shouldYield then
                                yield ms.ToArray()
                    | _ -> ()
                }
          Disconnect =
            fun () ->
                async {
                    match currentSocket () with
                    | Some ws ->
                        try
                            if ws.State = WebSocketState.Open || ws.State = WebSocketState.CloseReceived then
                                do! ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None) |> Async.AwaitTask
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
                    | :? TaskCanceledException as ex ->
                        return Error(AcpError.TimedOut ex.Message)
                    | ex ->
                        return Error(AcpError.InvalidPayload ex.Message)
                }
          Receive =
            fun ct ->
                taskSeq {
                    match client with
                    | Some httpClient ->
                        use request = new HttpRequestMessage(HttpMethod.Get, url)
                        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("text/event-stream"))
                        applyHeaders request
                        match lastEventId with
                        | Some value when value <> "" -> request.Headers.TryAddWithoutValidation("Last-Event-ID", value) |> ignore
                        | _ -> ()

                        let! response = httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                        use response = response
                        response.EnsureSuccessStatusCode() |> ignore

                        let! stream = response.Content.ReadAsStreamAsync(ct)
                        use stream = stream

                        let mediaType =
                            if isNull response.Content.Headers.ContentType then
                                ""
                            else
                                response.Content.Headers.ContentType.MediaType

                        if String.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) then
                            use memory = new MemoryStream()
                            do! stream.CopyToAsync(memory, ct)
                            if memory.Length <= int64 DefaultMaxMessageSize then
                                yield memory.ToArray()
                        else
                            for eventData in parseSseStream stream ct do
                                lastEventId <- eventData.LastEventId |> Option.orElse lastEventId
                                if eventData.Data.Length <= DefaultMaxMessageSize then
                                    yield eventData.Data
                    | None -> ()
                }
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
