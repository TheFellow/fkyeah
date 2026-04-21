namespace AcpRuntime

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text
open System.Threading.Tasks

type private TerminalSession =
    { Id: string
      Process: Process
      Output: StringBuilder
      OutputLock: obj
      mutable OutputBytes: int
      mutable Truncated: bool }

module DefaultDelegate =

    let private comparison =
        if OperatingSystem.IsWindows() then
            StringComparison.OrdinalIgnoreCase
        else
            StringComparison.Ordinal

    let private appendOutput (limitBytes: int) (session: TerminalSession) (text: string) =
        let bytes = Encoding.UTF8.GetBytes(text)

        lock session.OutputLock (fun () ->
            if session.OutputBytes >= limitBytes then
                session.Truncated <- true
            else
                let remaining = limitBytes - session.OutputBytes

                if bytes.Length <= remaining then
                    session.Output.Append(text) |> ignore
                    session.OutputBytes <- session.OutputBytes + bytes.Length
                else
                    let mutable consumed = 0
                    let mutable index = 0

                    while index < text.Length && consumed < remaining do
                        let current = text[index].ToString()
                        let size = Encoding.UTF8.GetByteCount(current)

                        if consumed + size <= remaining then
                            session.Output.Append(current) |> ignore
                            consumed <- consumed + size
                            index <- index + 1
                        else
                            index <- text.Length

                    session.OutputBytes <- session.OutputBytes + consumed
                    session.Truncated <- true)

    let private readOutput (session: TerminalSession) =
        lock session.OutputLock (fun () -> session.Output.ToString(), session.Truncated)

    let private buildProcessStartInfo
        (command: string)
        (args: string list)
        (workingDirectory: string)
        (environment: Map<string, string>)
        =
        let psi =
            if Path.IsPathRooted(command) then
                ProcessStartInfo(command)
            else
                let info = ProcessStartInfo("/usr/bin/env")
                info.ArgumentList.Add(command)
                info

        for arg in args do
            psi.ArgumentList.Add(arg)

        psi.WorkingDirectory <- workingDirectory
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.StandardOutputEncoding <- Encoding.UTF8
        psi.StandardErrorEncoding <- Encoding.UTF8

        for KeyValue(key, value) in environment do
            psi.EnvironmentVariables[key] <- value

        psi

    let private promptForPermission (request: PermissionRequest) =
        let subject = request.Subject |> Option.defaultValue "(none)"
        let reason = request.Reason |> Option.defaultValue ""
        Console.Write($"Allow {request.Operation} on {subject}? {reason} [y/N]: ")
        Console.Out.Flush()
        let response = Console.ReadLine()

        not (isNull response)
        && response.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase)

    let private authorize permissionStrategy request =
        match permissionStrategy with
        | PermissionStrategy.DenyAll -> Error(AcpError.PermissionDenied $"Operation '{request.Operation}' is denied")
        | PermissionStrategy.AutoApprove ->
            Ok
                { Allowed = true
                  Reason = Some "auto-approved" }
        | PermissionStrategy.ConsolePrompt ->
            if promptForPermission request then
                Ok
                    { Allowed = true
                      Reason = Some "approved" }
            else
                Error(AcpError.PermissionDenied $"Operation '{request.Operation}' was denied")

    let private resolveLinkAwareFullPath (path: string) =
        let fullPath = Path.GetFullPath(path)
        let root = Path.GetPathRoot(fullPath)
        let relative = fullPath.Substring(root.Length)

        let segments =
            relative.Split(
                [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |],
                StringSplitOptions.RemoveEmptyEntries
            )

        let mutable current = root
        let mutable index = 0

        while index < segments.Length do
            let nextPath = Path.Combine(current, segments[index])

            if Directory.Exists(nextPath) then
                let info = DirectoryInfo(nextPath) :> FileSystemInfo

                current <-
                    if not (isNull info.LinkTarget) then
                        info.ResolveLinkTarget(true).FullName
                    else
                        info.FullName

                index <- index + 1
            elif File.Exists(nextPath) then
                let info = FileInfo(nextPath) :> FileSystemInfo

                current <-
                    if not (isNull info.LinkTarget) then
                        info.ResolveLinkTarget(true).FullName
                    else
                        info.FullName

                index <- index + 1
            else
                let remaining = segments[index..]

                current <-
                    remaining
                    |> Array.fold (fun acc segment -> Path.Combine(acc, segment)) current
                    |> Path.GetFullPath

                index <- segments.Length

        Path.GetFullPath(current)

    let private isWithinRoot (rootFull: string) (candidate: string) =
        candidate.Equals(rootFull, comparison)
        || candidate.StartsWith(rootFull + Path.DirectorySeparatorChar.ToString(), comparison)

    let private validatePath (rootFull: string) (requestedPath: string) =
        let combined =
            if Path.IsPathRooted(requestedPath) then
                requestedPath
            else
                Path.Combine(rootFull, requestedPath)

        let resolved = resolveLinkAwareFullPath combined

        if isWithinRoot rootFull resolved then
            Ok resolved
        else
            Error(
                AcpError.PathOutsideRoot
                    $"'{requestedPath}' resolves to '{resolved}' which is outside root '{rootFull}'"
            )

    let createDefaultDelegate (rootDirectory: string) (permissionStrategy: PermissionStrategy) (outputByteLimit: int) =
        let rootFull = resolveLinkAwareFullPath rootDirectory
        let sessions = ConcurrentDictionary<string, TerminalSession>()

        let requestPermission operation subject reason =
            authorize
                permissionStrategy
                { Operation = operation
                  Subject = subject
                  Reason = reason }

        { ReadTextFile =
            fun (request: ReadTextFileRequest) ->
                async {
                    match requestPermission "filesystem/read_text_file" (Some request.Path) None with
                    | Error error -> return Error error
                    | Ok _ ->
                        match validatePath rootFull request.Path with
                        | Error error -> return Error error
                        | Ok path ->
                            if not (File.Exists(path)) then
                                return Error(AcpError.InvalidPayload $"File '{request.Path}' does not exist")
                            else
                                let content = File.ReadAllText(path)
                                return Ok { Path = path; Content = content }
                }
          WriteTextFile =
            fun (request: WriteTextFileRequest) ->
                async {
                    match requestPermission "filesystem/write_text_file" (Some request.Path) None with
                    | Error error -> return Error error
                    | Ok _ ->
                        match validatePath rootFull request.Path with
                        | Error error -> return Error error
                        | Ok path ->
                            let dir = Path.GetDirectoryName(path)

                            if not (String.IsNullOrWhiteSpace(dir)) && not (Directory.Exists(dir)) then
                                Directory.CreateDirectory(dir) |> ignore

                            File.WriteAllText(path, request.Content)

                            return
                                Ok
                                    { Path = path
                                      BytesWritten = Encoding.UTF8.GetByteCount(request.Content) }
                }
          TerminalCreate =
            fun (request: TerminalCreateRequest) ->
                async {
                    match requestPermission "terminal/create" (Some request.Command) None with
                    | Error error -> return Error error
                    | Ok _ ->
                        let workingDirectoryResult =
                            match request.WorkingDirectory with
                            | Some path -> validatePath rootFull path
                            | None -> Ok rootFull

                        match workingDirectoryResult with
                        | Error error -> return Error error
                        | Ok workingDirectory ->
                            let psi =
                                buildProcessStartInfo request.Command request.Args workingDirectory request.Environment

                            let proc = new Process(StartInfo = psi)

                            if not (proc.Start()) then
                                return Error(AcpError.InvalidPayload $"Failed to start '{request.Command}'")
                            else
                                let terminalId = Guid.NewGuid().ToString("N")

                                let session =
                                    { Id = terminalId
                                      Process = proc
                                      Output = StringBuilder()
                                      OutputLock = obj ()
                                      OutputBytes = 0
                                      Truncated = false }

                                let rec drainLoop (reader: StreamReader) =
                                    Task.Run(fun () ->
                                        task {
                                            let mutable keepReading = true

                                            while keepReading do
                                                let! line = reader.ReadLineAsync()

                                                if isNull line then
                                                    keepReading <- false
                                                else
                                                    appendOutput outputByteLimit session (line + Environment.NewLine)
                                        }
                                        :> Task)

                                drainLoop proc.StandardOutput |> ignore
                                drainLoop proc.StandardError |> ignore
                                sessions[terminalId] <- session
                                return Ok { TerminalId = terminalId }
                }
          TerminalOutput =
            fun (request: TerminalOutputRequest) ->
                async {
                    match sessions.TryGetValue(request.TerminalId) with
                    | true, session ->
                        let output, truncated = readOutput session

                        let cappedOutput =
                            match request.MaxBytes with
                            | Some maxBytes when Encoding.UTF8.GetByteCount(output) > maxBytes ->
                                let mutable consumed = 0
                                let mutable index = 0

                                while index < output.Length && consumed < maxBytes do
                                    let current = output[index].ToString()
                                    let size = Encoding.UTF8.GetByteCount(current)

                                    if consumed + size <= maxBytes then
                                        consumed <- consumed + size
                                        index <- index + 1
                                    else
                                        index <- output.Length

                                output.Substring(0, index)
                            | _ -> output

                        return
                            Ok
                                { Output = cappedOutput
                                  Truncated = truncated
                                  IsRunning = not session.Process.HasExited }
                    | _ -> return Error(AcpError.InvalidPayload $"Unknown terminal '{request.TerminalId}'")
                }
          TerminalWaitForExit =
            fun (request: TerminalWaitForExitRequest) ->
                async {
                    match sessions.TryGetValue(request.TerminalId) with
                    | true, session ->
                        match request.TimeoutMs with
                        | Some timeoutMs when timeoutMs > 0 ->
                            let! completed =
                                session.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(float timeoutMs))
                                |> Async.AwaitTask
                                |> Async.Catch

                            let output, _ = readOutput session

                            match completed with
                            | Choice1Of2 _ ->
                                return
                                    Ok
                                        { ExitCode = Some session.Process.ExitCode
                                          Output = output
                                          TimedOut = false }
                            | Choice2Of2 _ ->
                                return
                                    Ok
                                        { ExitCode = None
                                          Output = output
                                          TimedOut = true }
                        | _ ->
                            do! session.Process.WaitForExitAsync() |> Async.AwaitTask
                            let output, _ = readOutput session

                            return
                                Ok
                                    { ExitCode = Some session.Process.ExitCode
                                      Output = output
                                      TimedOut = false }
                    | _ -> return Error(AcpError.InvalidPayload $"Unknown terminal '{request.TerminalId}'")
                }
          TerminalKill =
            fun (request: TerminalKillRequest) ->
                async {
                    match sessions.TryGetValue(request.TerminalId) with
                    | true, session when session.Process.HasExited -> return Ok { Killed = false }
                    | true, session ->
                        try
                            session.Process.Kill(true)
                            return Ok { Killed = true }
                        with ex ->
                            return Error(AcpError.InvalidPayload ex.Message)
                    | _ -> return Error(AcpError.InvalidPayload $"Unknown terminal '{request.TerminalId}'")
                }
          TerminalRelease =
            fun (request: TerminalReleaseRequest) ->
                async {
                    match sessions.TryRemove(request.TerminalId) with
                    | true, session ->
                        try
                            if session.Process.HasExited then
                                session.Process.Dispose()
                        with _ ->
                            ()

                        return Ok { Released = true }
                    | _ -> return Ok { Released = false }
                }
          RequestPermission =
            fun (request: PermissionRequest) ->
                async { return requestPermission request.Operation request.Subject request.Reason } }
