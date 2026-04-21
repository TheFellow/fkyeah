namespace CodingAgent

open System
open System.Collections
open System.Collections.Generic
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions
open System.Text
open System.Runtime.InteropServices

/// Interface for executing tools in an environment (local, Docker, K8s, WASM, etc.)
type IExecutionEnvironment =
    /// Read a file with optional offset and limit (1-based line numbers)
    abstract member ReadFile: path: string * offset: int option * limit: int option -> string
    /// Write content to a file, creating directories as needed
    abstract member WriteFile: path: string * content: string -> unit
    /// Check if a file exists
    abstract member FileExists: path: string -> bool
    /// List directory entries
    abstract member ListDirectory: path: string -> DirEntry list
    /// Execute a command with timeout
    abstract member ExecCommand: command: string * timeoutMs: int * workingDir: string option -> ExecResult

    /// Search file contents by regex pattern
    abstract member Grep:
        pattern: string * path: string * caseInsensitive: bool * maxResults: int * globFilter: string option -> string

    /// Find files by glob pattern
    abstract member Glob: pattern: string * path: string -> string list
    /// Get the working directory
    abstract member WorkingDirectory: string
    /// Get the platform identifier
    abstract member Platform: string
    /// Get the OS version
    abstract member OsVersion: string
    /// Initialize the environment
    abstract member Initialize: unit -> unit
    /// Clean up the environment
    abstract member Cleanup: unit -> unit

/// Environment variable filtering for sensitive data
module EnvVarFilter =
    let private sensitivePatterns =
        [ "_API_KEY$"; "_SECRET$"; "_TOKEN$"; "_PASSWORD$"; "_CREDENTIAL$" ]

    let private allowedVars =
        set
            [ "PATH"
              "HOME"
              "USER"
              "SHELL"
              "LANG"
              "TERM"
              "TMPDIR"
              "GOPATH"
              "CARGO_HOME"
              "NVM_DIR"
              "DOTNET_ROOT" ]

    /// Filter environment variables, excluding sensitive ones
    let filterEnvVars (vars: Collections.Generic.IDictionary<string, string>) : Map<string, string> =
        vars
        |> Seq.filter (fun kv ->
            if allowedVars.Contains(kv.Key) then
                true
            else
                let upper = kv.Key.ToUpperInvariant()

                sensitivePatterns
                |> List.exists (fun pat -> Regex.IsMatch(upper, pat, RegexOptions.IgnoreCase))
                |> not)
        |> Seq.map (fun kv -> kv.Key, kv.Value)
        |> Map.ofSeq

module private NativeMethods =
    [<DllImport("libc", SetLastError = true, EntryPoint = "kill")>]
    extern int unixKill(int pid, int signal)

    [<DllImport("libc", SetLastError = true, EntryPoint = "setpgid")>]
    extern int unixSetpgid(int pid, int pgid)

/// Local execution environment that runs on the local machine
type LocalExecutionEnvironment(workingDir: string) =

    let resolvedWorkingDir =
        if Path.IsPathRooted(workingDir) then
            workingDir
        else
            Path.GetFullPath(workingDir)

    let trySetProcessGroup (processId: int) =
        if OperatingSystem.IsWindows() then
            ()
        else
            try
                if NativeMethods.unixSetpgid (processId, processId) <> 0 then
                    ()
            with _ ->
                ()

    let sendUnixSignalToGroup (processId: int) (signal: int) =
        if not (OperatingSystem.IsWindows()) then
            try
                NativeMethods.unixKill (-processId, signal) |> ignore
            with _ ->
                ()

    interface IExecutionEnvironment with

        member _.ReadFile(path, offset, limit) =
            let fullPath =
                if Path.IsPathRooted(path) then
                    path
                else
                    Path.Combine(resolvedWorkingDir, path)

            if not (File.Exists(fullPath)) then
                failwith (sprintf "File not found: %s" fullPath)

            let lines = File.ReadAllLines(fullPath)
            let startLine = (offset |> Option.defaultValue 1) - 1 |> max 0
            let count = limit |> Option.defaultValue 2000

            let selectedLines =
                lines |> Array.skip (min startLine lines.Length) |> Array.truncate count

            selectedLines
            |> Array.mapi (fun i line -> sprintf "%4d | %s" (startLine + i + 1) line)
            |> String.concat Environment.NewLine

        member _.WriteFile(path, content) =
            let fullPath =
                if Path.IsPathRooted(path) then
                    path
                else
                    Path.Combine(resolvedWorkingDir, path)

            let dir = Path.GetDirectoryName(fullPath)

            if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore

            File.WriteAllText(fullPath, content)

        member _.FileExists(path) =
            let fullPath =
                if Path.IsPathRooted(path) then
                    path
                else
                    Path.Combine(resolvedWorkingDir, path)

            File.Exists(fullPath)

        member _.ListDirectory(path) =
            let fullPath =
                if Path.IsPathRooted(path) then
                    path
                else
                    Path.Combine(resolvedWorkingDir, path)

            if not (Directory.Exists(fullPath)) then
                failwith (sprintf "Directory not found: %s" fullPath)

            let entries =
                Directory.GetFileSystemEntries(fullPath)
                |> Array.map (fun entry ->
                    let info = FileInfo(entry)
                    let isDir = Directory.Exists(entry)

                    { Name = Path.GetFileName(entry)
                      IsDir = isDir
                      Size = if isDir then None else Some info.Length })
                |> Array.toList

            entries

        member _.ExecCommand(command, timeoutMs, workingDir) =
            let effectiveDir = workingDir |> Option.defaultValue resolvedWorkingDir
            let psi = ProcessStartInfo()

            if OperatingSystem.IsWindows() then
                psi.FileName <- "cmd.exe"
                psi.Arguments <- sprintf "/c %s" command
            else
                psi.FileName <- "/bin/bash"
                psi.Arguments <- sprintf "-c \"%s\"" (command.Replace("\"", "\\\""))

            psi.WorkingDirectory <- effectiveDir
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true

            // Filter environment variables
            let envDict = Dictionary<string, string>()

            for entry in Environment.GetEnvironmentVariables() |> Seq.cast<Collections.DictionaryEntry> do
                envDict.[entry.Key.ToString()] <- entry.Value.ToString()

            let filteredVars = EnvVarFilter.filterEnvVars envDict
            psi.Environment.Clear()

            for kv in filteredVars do
                psi.Environment.[kv.Key] <- kv.Value

            let sw = Stopwatch.StartNew()
            use proc = new Process()
            proc.StartInfo <- psi

            let stdoutSb = StringBuilder()
            let stderrSb = StringBuilder()

            proc.OutputDataReceived.Add(fun e ->
                if not (isNull e.Data) then
                    stdoutSb.AppendLine(e.Data) |> ignore)

            proc.ErrorDataReceived.Add(fun e ->
                if not (isNull e.Data) then
                    stderrSb.AppendLine(e.Data) |> ignore)

            proc.Start() |> ignore
            trySetProcessGroup proc.Id
            proc.BeginOutputReadLine()
            proc.BeginErrorReadLine()

            let exited = proc.WaitForExit(timeoutMs)
            let mutable timedOut = false

            if not exited then
                timedOut <- true

                if OperatingSystem.IsWindows() then
                    try
                        proc.Kill(true)
                    with _ ->
                        ()
                else
                    sendUnixSignalToGroup proc.Id 15 // SIGTERM
                    let exitedAfterTerm = proc.WaitForExit(2000)

                    if not exitedAfterTerm then
                        sendUnixSignalToGroup proc.Id 9 // SIGKILL

                try
                    proc.WaitForExit(2000) |> ignore
                with _ ->
                    ()
            else
                // Flush async output events
                proc.WaitForExit()

            sw.Stop()
            let stdout = stdoutSb.ToString().TrimEnd()
            let stderr = stderrSb.ToString().TrimEnd()

            if timedOut then
                { Stdout = stdout
                  Stderr = stderr
                  ExitCode = -1
                  TimedOut = true
                  DurationMs = int sw.ElapsedMilliseconds }
            else
                { Stdout = stdout
                  Stderr = stderr
                  ExitCode = proc.ExitCode
                  TimedOut = false
                  DurationMs = int sw.ElapsedMilliseconds }

        member _.Grep(pattern, path, caseInsensitive, maxResults, globFilter) =
            let fullPath =
                if Path.IsPathRooted(path) then
                    path
                else
                    Path.Combine(resolvedWorkingDir, path)

            let regexOptions =
                if caseInsensitive then
                    RegexOptions.IgnoreCase
                else
                    RegexOptions.None

            let regex = Regex(pattern, regexOptions)
            let results = System.Collections.Generic.List<string>()

            let matchesGlob (filePath: string) =
                match globFilter with
                | None -> true
                | Some pattern when pattern.Trim() = "" -> true
                | Some pattern ->
                    let escaped = Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".")
                    let globRegex = Regex("^" + escaped + "$", RegexOptions.IgnoreCase)
                    let fileName = Path.GetFileName(filePath)
                    globRegex.IsMatch(fileName)

            let searchFile (filePath: string) =
                if results.Count < maxResults && matchesGlob filePath then
                    try
                        let lines = File.ReadAllLines(filePath)

                        for i in 0 .. lines.Length - 1 do
                            if results.Count < maxResults && regex.IsMatch(lines.[i]) then
                                results.Add(sprintf "%s:%d:%s" filePath (i + 1) lines.[i])
                    with _ ->
                        ()

            if File.Exists(fullPath) then
                searchFile fullPath
            elif Directory.Exists(fullPath) then
                for file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories) do
                    if results.Count < maxResults then
                        searchFile file

            results |> Seq.toArray |> String.concat Environment.NewLine

        member _.Glob(pattern, path) =
            let fullPath =
                if Path.IsPathRooted(path) then
                    path
                else
                    Path.Combine(resolvedWorkingDir, path)

            if not (Directory.Exists(fullPath)) then
                []
            else
                // Simple glob: replace ** with recursive, * with single level
                let searchPattern =
                    if pattern.Contains("**") then
                        "*"
                    else
                        pattern.Replace("**", "*")

                let searchOption =
                    if pattern.Contains("**") then
                        SearchOption.AllDirectories
                    else
                        SearchOption.TopDirectoryOnly

                try
                    Directory.EnumerateFiles(fullPath, searchPattern, searchOption)
                    |> Seq.toList
                    |> List.sortByDescending (fun f -> File.GetLastWriteTimeUtc(f))
                with _ ->
                    []

        member _.WorkingDirectory = resolvedWorkingDir

        member _.Platform =
            if OperatingSystem.IsMacOS() then "darwin"
            elif OperatingSystem.IsLinux() then "linux"
            elif OperatingSystem.IsWindows() then "windows"
            else "unknown"

        member _.OsVersion = Environment.OSVersion.VersionString

        member _.Initialize() = ()
        member _.Cleanup() = ()
