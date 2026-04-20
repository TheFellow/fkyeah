namespace CodingAgent

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Collections.Generic
open UnifiedLlm

/// Loop detection for repeating tool call patterns
module LoopDetection =

    /// Get a signature for a tool call (name + arguments hash)
    let toolCallSignature (tc: ToolCallData) : string =
        sprintf "%s:%d" tc.Name (tc.Arguments.GetHashCode())

    /// Extract the last N tool call signatures from history
    let extractRecentSignatures (history: Turn list) (window: int) : string list =
        history
        |> List.collect (fun turn ->
            match turn with
            | AssistantTurn(_, toolCalls, _, _, _) -> toolCalls |> List.map toolCallSignature
            | _ -> [])
        |> List.rev
        |> List.truncate window
        |> List.rev

    /// Detect repeating patterns in tool calls
    let detectLoop (history: Turn list) (windowSize: int) : bool =
        let recent = extractRecentSignatures history windowSize

        if recent.Length < windowSize then
            false
        else
            let arr = recent |> Array.ofList

            [ 1; 2; 3 ]
            |> List.exists (fun patternLen ->
                if windowSize % patternLen <> 0 then
                    false
                else
                    let pattern = arr.[0 .. patternLen - 1]
                    let mutable allMatch = true

                    for i in patternLen..patternLen .. (windowSize - 1) do
                        let chunk = arr.[i .. i + patternLen - 1]

                        if chunk <> pattern then
                            allMatch <- false

                    allMatch)

/// Convert history turns to UnifiedLlm messages
module HistoryConverter =
    let toMessages (history: Turn list) : Message list =
        history
        |> List.collect (fun turn ->
            match turn with
            | UserTurn(content, _) -> [ Message.user (content) ]
            | AssistantTurn(content, toolCalls, _, _, _) ->
                let parts =
                    [ if not (String.IsNullOrEmpty(content)) then
                          yield Text content ]
                    @ (toolCalls |> List.map (fun tc -> ToolCall tc))

                [ { Role = Role.Assistant
                    Content = parts
                    Name = None
                    ToolCallId = None } ]
            | ToolResultsTurn(results, _) ->
                results
                |> List.map (fun r -> Message.toolResult (r.ToolCallId, r.Content, r.IsError))
            | SteeringTurn(content, _) -> [ Message.user (content) ]
            | SystemTurn(content, _) -> [ Message.system (content) ])

module private JsonArgs =

    let parse (raw: string) =
        use doc = JsonDocument.Parse(raw)
        doc.RootElement.Clone()

    let tryGetProperty (root: JsonElement) (name: string) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if root.TryGetProperty(name, &value) then
            Some value
        else
            None

    let tryGetString (root: JsonElement) (name: string) =
        match tryGetProperty root name with
        | Some value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
        | _ -> None

    let tryGetInt (root: JsonElement) (name: string) =
        match tryGetProperty root name with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            let mutable parsed = 0
            if value.TryGetInt32(&parsed) then Some parsed else None
        | _ -> None

    let tryGetBool (root: JsonElement) (name: string) =
        match tryGetProperty root name with
        | Some value when value.ValueKind = JsonValueKind.True || value.ValueKind = JsonValueKind.False ->
            Some(value.GetBoolean())
        | _ -> None

    let tryGetStringArray (root: JsonElement) (name: string) =
        match tryGetProperty root name with
        | Some value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.choose (fun item ->
                if item.ValueKind = JsonValueKind.String then
                    Some(item.GetString())
                else
                    None)
            |> Seq.toList
            |> Some
        | _ -> None

module private PatchApplier =

    type private PatchKind =
        | AddFile of string
        | UpdateFile of string
        | DeleteFile of string

    let private resolvePath (workingDir: string) (filePath: string) =
        if Path.IsPathRooted(filePath) then
            filePath
        else
            Path.Combine(workingDir, filePath)

    let applyPatch (workingDir: string) (patchText: string) =
        let lines = patchText.Replace("\r\n", "\n").Split('\n') |> Array.toList

        if lines.IsEmpty || lines.Head.Trim() <> "*** Begin Patch" then
            failwith "Invalid patch: missing *** Begin Patch header"

        if not (lines |> List.exists (fun l -> l.Trim() = "*** End Patch")) then
            failwith "Invalid patch: missing *** End Patch footer"

        let withoutHeader = lines |> List.tail

        let rec parseSections
            (acc: (PatchKind * string * string list) list)
            (kind: PatchKind option)
            (filePath: string option)
            (content: string list)
            (remaining: string list)
            : (PatchKind * string * string list) list =
            match remaining with
            | [] ->
                match kind, filePath with
                | Some k, Some p -> List.rev ((k, p, List.rev content) :: acc)
                | _ -> List.rev acc
            | line :: rest ->
                if line.StartsWith("*** Add File: ") then
                    let path = line.Substring("*** Add File: ".Length).Trim()

                    let nextAcc =
                        match kind, filePath with
                        | Some k, Some p -> (k, p, List.rev content) :: acc
                        | _ -> acc

                    parseSections nextAcc (Some(AddFile(path))) (Some(path)) [] rest
                elif line.StartsWith("*** Update File: ") then
                    let path = line.Substring("*** Update File: ".Length).Trim()

                    let nextAcc =
                        match kind, filePath with
                        | Some k, Some p -> (k, p, List.rev content) :: acc
                        | _ -> acc

                    parseSections nextAcc (Some(UpdateFile(path))) (Some(path)) [] rest
                elif line.StartsWith("*** Delete File: ") then
                    let path = line.Substring("*** Delete File: ".Length).Trim()

                    let nextAcc =
                        match kind, filePath with
                        | Some k, Some p -> (k, p, List.rev content) :: acc
                        | _ -> acc

                    parseSections nextAcc (Some(DeleteFile(path))) (Some(path)) [] rest
                elif line.Trim() = "*** End Patch" then
                    match kind, filePath with
                    | Some k, Some p -> List.rev ((k, p, List.rev content) :: acc)
                    | _ -> List.rev acc
                else
                    parseSections acc kind filePath (line :: content) rest

        let sections = parseSections [] None None [] withoutHeader

        for (kind, path, contentLines) in sections do
            let fullPath = resolvePath workingDir path

            match kind with
            | AddFile _ ->
                let text =
                    contentLines
                    |> List.choose (fun line ->
                        if line.StartsWith("+") then
                            Some(line.Substring(1))
                        else
                            None)
                    |> String.concat "\n"

                let dir = Path.GetDirectoryName(fullPath)

                if not (String.IsNullOrEmpty(dir)) && not (Directory.Exists(dir)) then
                    Directory.CreateDirectory(dir) |> ignore

                File.WriteAllText(fullPath, text)
            | DeleteFile _ ->
                if File.Exists(fullPath) then
                    File.Delete(fullPath)
            | UpdateFile _ ->
                if not (File.Exists(fullPath)) then
                    failwith $"Cannot update missing file: {path}"

                let original = File.ReadAllText(fullPath)

                let rec parseHunks
                    (acc: string list list)
                    (current: string list)
                    (remaining: string list)
                    : string list list =
                    match remaining with
                    | [] ->
                        match current with
                        | [] -> List.rev acc
                        | _ -> List.rev ((List.rev current) :: acc)
                    | line :: rest ->
                        if line.StartsWith("@@") then
                            match current with
                            | [] -> parseHunks acc [] rest
                            | _ -> parseHunks ((List.rev current) :: acc) [] rest
                        elif line.StartsWith("*** ") then
                            match current with
                            | [] -> List.rev acc
                            | _ -> List.rev ((List.rev current) :: acc)
                        else
                            parseHunks acc (line :: current) rest

                let hunks = parseHunks [] [] contentLines
                let mutable updated = original

                for hunkLines in hunks do
                    let oldBlock =
                        hunkLines
                        |> List.choose (fun (line: string) ->
                            if line.StartsWith(" ") || line.StartsWith("-") then
                                Some(line.Substring(1))
                            else
                                None)
                        |> String.concat "\n"

                    let newBlock =
                        hunkLines
                        |> List.choose (fun (line: string) ->
                            if line.StartsWith(" ") || line.StartsWith("+") then
                                Some(line.Substring(1))
                            else
                                None)
                        |> String.concat "\n"

                    if oldBlock <> "" then
                        if not (updated.Contains(oldBlock)) then
                            failwith $"Patch hunk not found in {path}"

                        updated <- updated.Replace(oldBlock, newBlock, StringComparison.Ordinal)

                File.WriteAllText(fullPath, updated)

/// The core coding agent session
type Session(profile: IProviderProfile, env: IExecutionEnvironment, client: Client, ?config: SessionConfig, ?depth: int)
    =
    let mutable sessionId = Guid.NewGuid().ToString("N")
    let history = List<Turn>()
    let events = List<SessionEvent>()
    let steeringQueue = Queue<string>()
    let followupQueue = Queue<string>()
    let mutable state = Idle
    let mutable abortSignaled = false
    let config = config |> Option.defaultValue SessionConfig.Default
    let toolRegistry = AgentToolRegistry()
    let mutable userInstructions: string option = None
    let currentDepth = depth |> Option.defaultValue 0
    let activeSubagents = Dictionary<string, Session>()
    let subagentStatuses = Dictionary<string, string>()
    let subagentLock = obj ()
    let mutable contextWarningEmitted = false
    let mutable awaitingInputRequested = false
    let mutable sessionCostMicrodollars = 0L

    let toolCache =
        CacheStore.fileSystem
            { CacheConfig.Default with
                MaxEntries = 512
                PersistencePath = None }

    let cacheableToolNames =
        set [ "read_file"; "read_many_files"; "grep"; "glob"; "list_dir" ]

    let mutatingToolNames =
        set
            [ "write_file"
              "edit_file"
              "apply_patch"
              "shell"
              "spawn_agent"
              "send_input"
              "wait"
              "close_agent" ]

    let emitWithFullOutput (kind: EventKind) (data: Map<string, string>) (fullOutput: string option) =
        let evt: SessionEvent =
            { Kind = kind
              Timestamp = DateTime.UtcNow
              SessionId = sessionId
              Data = data
              FullOutput = fullOutput }

        events.Add(evt)

        match config.OnEvent with
        | Some callback -> callback evt
        | None -> ()

    let emit (kind: EventKind) (data: Map<string, string>) = emitWithFullOutput kind data None

    let countTurns () =
        history
        |> Seq.filter (fun t ->
            match t with
            | UserTurn _
            | AssistantTurn _ -> true
            | _ -> false)
        |> Seq.length

    let historyChars () =
        history
        |> Seq.sumBy (fun turn ->
            match turn with
            | UserTurn(content, _) -> content.Length
            | AssistantTurn(content, toolCalls, reasoning, _, _) ->
                content.Length
                + (reasoning |> Option.defaultValue "" |> String.length)
                + (toolCalls |> List.sumBy (fun tc -> tc.Name.Length + tc.Arguments.Length))
            | ToolResultsTurn(results, _) -> results |> List.sumBy (fun r -> r.Content.Length)
            | SteeringTurn(content, _) -> content.Length
            | SystemTurn(content, _) -> content.Length)

    let checkContextUsage () =
        if not contextWarningEmitted && profile.ContextWindowSize > 0 then
            let tokens = historyChars () / 4
            let threshold = int (float profile.ContextWindowSize * 0.8)

            if tokens >= threshold then
                contextWarningEmitted <- true
                let percentage = (float tokens / float profile.ContextWindowSize) * 100.0

                emit
                    Warning
                    (Map.ofList
                        [ "message", sprintf "Context usage at ~%.1f%% of context window" percentage
                          "current_tokens", string tokens
                          "limit_tokens", string profile.ContextWindowSize
                          "percentage", sprintf "%.1f" percentage ])

    let drainSteering () =
        while steeringQueue.Count > 0 do
            let msg = steeringQueue.Dequeue()
            history.Add(SteeringTurn(msg, DateTime.UtcNow))
            emit SteeringInjected (Map.ofList [ "content", msg ])
            checkContextUsage ()

    let closeAllSubagents () =
        lock subagentLock (fun () ->
            for session in activeSubagents.Values do
                try
                    session.Close()
                with _ ->
                    ()

            activeSubagents.Clear()
            subagentStatuses.Clear())

    let resolveProviderProfile (providerId: string) (model: string) : IProviderProfile option =
        match providerId.Trim().ToLowerInvariant() with
        | "openai" -> Some(OpenAIProfile(model) :> IProviderProfile)
        | "anthropic" -> Some(AnthropicProfile(model) :> IProviderProfile)
        | "gemini" -> Some(GeminiProfile(model) :> IProviderProfile)
        | _ -> None

    let parseRequiredString (root: JsonElement) (name: string) =
        JsonArgs.tryGetString root name
        |> Option.defaultWith (fun () -> failwith $"{name} is required")

    let formatShellResult (result: ExecResult) =
        let sb = StringBuilder()
        sb.AppendLine($"exit_code: {result.ExitCode}") |> ignore
        sb.AppendLine($"timed_out: {result.TimedOut}") |> ignore

        if result.Stdout <> "" then
            sb.AppendLine("stdout:") |> ignore
            sb.AppendLine(result.Stdout.TrimEnd()) |> ignore

        if result.Stderr <> "" then
            sb.AppendLine("stderr:") |> ignore
            sb.AppendLine(result.Stderr.TrimEnd()) |> ignore

        if result.TimedOut then
            sb.AppendLine(
                $"[ERROR: Command timed out after {result.DurationMs}ms. Partial output shown above. Retry with longer timeout_ms.]"
            )
            |> ignore

        sb.ToString().TrimEnd()

    let builtInExecutors =
        Dictionary<string, string -> IExecutionEnvironment -> string>()

    do
        builtInExecutors.[SharedTools.readFile.Name] <-
            (fun args environment ->
                let root = JsonArgs.parse args
                let filePath = parseRequiredString root "file_path"
                let offset = JsonArgs.tryGetInt root "offset"
                let limit = JsonArgs.tryGetInt root "limit"
                environment.ReadFile(filePath, offset, limit))

        builtInExecutors.[SharedTools.writeFile.Name] <-
            (fun args environment ->
                let root = JsonArgs.parse args
                let filePath = parseRequiredString root "file_path"
                let content = JsonArgs.tryGetString root "content" |> Option.defaultValue ""
                environment.WriteFile(filePath, content)
                "OK")

        builtInExecutors.[SharedTools.editFile.Name] <-
            (fun args environment ->
                let root = JsonArgs.parse args
                let filePath = parseRequiredString root "file_path"
                let oldString = parseRequiredString root "old_string"
                let newString = JsonArgs.tryGetString root "new_string" |> Option.defaultValue ""
                let replaceAll = JsonArgs.tryGetBool root "replace_all" |> Option.defaultValue false

                if not (environment.FileExists(filePath)) then
                    failwith $"File not found: {filePath}"

                let fullPath =
                    if Path.IsPathRooted(filePath) then
                        filePath
                    else
                        Path.Combine(environment.WorkingDirectory, filePath)

                let source = File.ReadAllText(fullPath)

                // Normalize whitespace: collapse runs of spaces/tabs to single space, trim trailing per line
                let normalizeWs (s: string) =
                    s.Split('\n')
                    |> Array.map (fun line -> Regex.Replace(line, @"[ \t]+", " ").TrimEnd())
                    |> String.concat "\n"

                let exactMatch = source.Contains(oldString, StringComparison.Ordinal)
                let useNormalized = not exactMatch

                if useNormalized then
                    let normSource = normalizeWs source
                    let normOld = normalizeWs oldString

                    if not (normSource.Contains(normOld, StringComparison.Ordinal)) then
                        failwith "old_string not found in file"

                let updated =
                    if useNormalized then
                        // Whitespace-normalized replacement: find in normalized space, map back to source lines
                        let sourceLines = source.Split('\n')

                        let normSourceLines =
                            sourceLines
                            |> Array.map (fun line -> Regex.Replace(line, @"[ \t]+", " ").TrimEnd())

                        let normOldLines = (normalizeWs oldString).Split('\n')

                        let mutable startLine = -1

                        for i in 0 .. normSourceLines.Length - normOldLines.Length do
                            if startLine < 0 then
                                let mutable matches = true

                                for j in 0 .. normOldLines.Length - 1 do
                                    if normSourceLines.[i + j] <> normOldLines.[j] then
                                        matches <- false

                                if matches then
                                    startLine <- i

                        if startLine < 0 then
                            failwith "old_string not found in file"

                        let before = sourceLines.[.. startLine - 1] |> String.concat "\n"
                        let after = sourceLines.[startLine + normOldLines.Length ..] |> String.concat "\n"
                        let prefix = if startLine > 0 then before + "\n" else ""

                        let suffix =
                            if startLine + normOldLines.Length < sourceLines.Length then
                                "\n" + after
                            else
                                ""

                        prefix + newString + suffix
                    elif replaceAll then
                        source.Replace(oldString, newString, StringComparison.Ordinal)
                    else
                        let idx = source.IndexOf(oldString, StringComparison.Ordinal)

                        if idx < 0 then
                            failwith "old_string not found in file"

                        source.Substring(0, idx) + newString + source.Substring(idx + oldString.Length)

                File.WriteAllText(fullPath, updated)
                "OK")

        builtInExecutors.[SharedTools.shell.Name] <-
            (fun args environment ->
                let root = JsonArgs.parse args
                let command = parseRequiredString root "command"

                let requestedTimeout =
                    JsonArgs.tryGetInt root "timeout_ms"
                    |> Option.defaultValue config.DefaultCommandTimeoutMs

                let timeoutMs = requestedTimeout |> max 1 |> min config.MaxCommandTimeoutMs
                let result = environment.ExecCommand(command, timeoutMs, None)
                formatShellResult result)

        builtInExecutors.[SharedTools.grep.Name] <-
            (fun args environment ->
                let root = JsonArgs.parse args
                let pattern = parseRequiredString root "pattern"
                let path = JsonArgs.tryGetString root "path" |> Option.defaultValue "."

                let caseInsensitive =
                    JsonArgs.tryGetBool root "case_insensitive" |> Option.defaultValue false

                let maxResults = JsonArgs.tryGetInt root "max_results" |> Option.defaultValue 100
                let globFilter = JsonArgs.tryGetString root "glob_filter"
                environment.Grep(pattern, path, caseInsensitive, maxResults, globFilter))

        builtInExecutors.[SharedTools.glob.Name] <-
            (fun args environment ->
                let root = JsonArgs.parse args
                let pattern = parseRequiredString root "pattern"
                let path = JsonArgs.tryGetString root "path" |> Option.defaultValue "."
                environment.Glob(pattern, path) |> String.concat "\n")

        builtInExecutors.[SharedTools.applyPatch.Name] <-
            (fun args environment ->
                let root = JsonArgs.parse args
                let patch = parseRequiredString root "patch"
                PatchApplier.applyPatch environment.WorkingDirectory patch
                "OK")

        builtInExecutors.[SharedTools.readManyFiles.Name] <-
            (fun args environment ->
                let root = JsonArgs.parse args

                let paths =
                    JsonArgs.tryGetStringArray root "paths"
                    |> Option.orElseWith (fun () -> JsonArgs.tryGetStringArray root "file_paths")
                    |> Option.defaultWith (fun () -> failwith "paths is required")

                let offset = JsonArgs.tryGetInt root "offset"
                let limit = JsonArgs.tryGetInt root "limit"

                paths
                |> List.map (fun path ->
                    let content = environment.ReadFile(path, offset, limit)
                    $"--- {path} ---\n{content}")
                |> String.concat "\n\n")

        builtInExecutors.[SharedTools.listDir.Name] <-
            (fun args environment ->
                let root = JsonArgs.parse args
                let path = JsonArgs.tryGetString root "path" |> Option.defaultValue "."

                let depth =
                    JsonArgs.tryGetInt root "depth" |> Option.defaultValue 1 |> max 1 |> min 16

                let rec walk (basePath: string) (remainingDepth: int) (indent: string) =
                    let entries =
                        environment.ListDirectory(basePath) |> List.sortBy (fun entry -> entry.Name)

                    [ for entry in entries do
                          let suffix = if entry.IsDir then "/" else ""

                          let size =
                              match entry.Size with
                              | Some s -> $" ({s} bytes)"
                              | None -> ""

                          yield $"{indent}{entry.Name}{suffix}{size}"

                          if entry.IsDir && remainingDepth > 1 then
                              let child =
                                  if Path.IsPathRooted(basePath) then
                                      Path.Combine(basePath, entry.Name)
                                  elif basePath = "." then
                                      entry.Name
                                  else
                                      Path.Combine(basePath, entry.Name)

                              yield! walk child (remainingDepth - 1) (indent + "  ") ]

                walk path depth "" |> String.concat "\n")

        builtInExecutors.[SharedTools.spawnAgent.Name] <-
            (fun args _ ->
                let root = JsonArgs.parse args
                let task = parseRequiredString root "task"

                if currentDepth >= config.MaxSubagentDepth then
                    failwith $"Subagent depth limit reached (current={currentDepth}, max={config.MaxSubagentDepth})"

                let workingDirOverride = JsonArgs.tryGetString root "working_dir"
                let modelOverride = JsonArgs.tryGetString root "model"
                let maxTurnsOverride = JsonArgs.tryGetInt root "max_turns"

                let subEnv: IExecutionEnvironment =
                    match workingDirOverride with
                    | None -> env
                    | Some wd ->
                        let resolved =
                            if Path.IsPathRooted(wd) then
                                wd
                            else
                                Path.GetFullPath(Path.Combine(env.WorkingDirectory, wd))

                        LocalExecutionEnvironment(resolved) :> IExecutionEnvironment

                let subProfile =
                    match modelOverride with
                    | None -> profile
                    | Some model ->
                        resolveProviderProfile profile.Id model
                        |> Option.defaultWith (fun () ->
                            failwith $"Model override is unsupported for provider '{profile.Id}'")

                let subConfig =
                    match maxTurnsOverride with
                    | Some maxTurns -> { config with MaxTurns = maxTurns }
                    | None -> config

                let agentId = Guid.NewGuid().ToString("N")

                let subSession =
                    Session(subProfile, subEnv, client, subConfig, depth = currentDepth + 1)

                lock subagentLock (fun () ->
                    activeSubagents.[agentId] <- subSession
                    subagentStatuses.[agentId] <- "running")

                try
                    subSession.ProcessInput(task)
                    let status = if subSession.State = Closed then "failed" else "completed"
                    lock subagentLock (fun () -> subagentStatuses.[agentId] <- status)
                with _ ->
                    lock subagentLock (fun () -> subagentStatuses.[agentId] <- "failed")

                let status =
                    lock subagentLock (fun () ->
                        match subagentStatuses.TryGetValue(agentId) with
                        | true, value -> value
                        | false, _ -> "unknown")

                $"agent_id: {agentId}\nstatus: {status}")

        builtInExecutors.[SharedTools.sendInput.Name] <-
            (fun args _ ->
                let root = JsonArgs.parse args
                let agentId = parseRequiredString root "agent_id"
                let message = parseRequiredString root "message"

                let subSession =
                    lock subagentLock (fun () ->
                        match activeSubagents.TryGetValue(agentId) with
                        | true, s -> Some s
                        | false, _ -> None)

                match subSession with
                | None -> failwith $"Unknown subagent: {agentId}"
                | Some s ->
                    lock subagentLock (fun () -> subagentStatuses.[agentId] <- "running")
                    s.ProcessInput(message)

                    lock subagentLock (fun () ->
                        if s.State = Closed then
                            subagentStatuses.[agentId] <- "failed"
                        else
                            subagentStatuses.[agentId] <- "completed")

                    "OK")

        builtInExecutors.[SharedTools.wait.Name] <-
            (fun args _ ->
                let root = JsonArgs.parse args
                let agentId = parseRequiredString root "agent_id"

                let subSession =
                    lock subagentLock (fun () ->
                        match activeSubagents.TryGetValue(agentId) with
                        | true, s -> Some s
                        | false, _ -> None)

                match subSession with
                | None -> failwith $"Unknown subagent: {agentId}"
                | Some s ->
                    let output =
                        s.History
                        |> List.rev
                        |> List.tryPick (fun t ->
                            match t with
                            | AssistantTurn(content, _, _, _, _) -> Some content
                            | _ -> None)
                        |> Option.defaultValue ""

                    let turns =
                        s.History
                        |> List.filter (fun t ->
                            match t with
                            | UserTurn _
                            | AssistantTurn _ -> true
                            | _ -> false)
                        |> List.length

                    let status =
                        lock subagentLock (fun () ->
                            match subagentStatuses.TryGetValue(agentId) with
                            | true, value -> value
                            | false, _ -> "unknown")

                    let success = status <> "failed"
                    lock subagentLock (fun () -> subagentStatuses.[agentId] <- "completed")
                    $"output: {output}\nsuccess: {success}\nturns_used: {turns}")

        builtInExecutors.[SharedTools.closeAgent.Name] <-
            (fun args _ ->
                let root = JsonArgs.parse args
                let agentId = parseRequiredString root "agent_id"

                let removed =
                    lock subagentLock (fun () ->
                        match activeSubagents.TryGetValue(agentId) with
                        | true, s ->
                            s.Close()
                            activeSubagents.Remove(agentId) |> ignore
                            subagentStatuses.Remove(agentId) |> ignore
                            true
                        | false, _ -> false)

                if not removed then
                    failwith $"Unknown subagent: {agentId}"

                "OK")

    let autoRegisterProfileTools () =
        for definition in profile.ToolDefinitions do
            match builtInExecutors.TryGetValue(definition.Name) with
            | true, execute ->
                toolRegistry.Register(
                    { Definition = definition
                      IsCacheable = cacheableToolNames.Contains(definition.Name)
                      Execute = execute }
                )
            | false, _ -> ()

    let completeWithStreaming (request: Request) : Response =
        let text = StringBuilder()
        let mutable emittedTextStart = false
        let mutable responseFromStream: Response option = None
        let mutable finishUsage: Usage option = None

        for evt in client.Stream(request) do
            match evt with
            | StreamEvent.TextStart _ ->
                if not emittedTextStart then
                    emittedTextStart <- true
                    emit AssistantTextStart Map.empty
            | StreamEvent.TextDelta(_, delta) ->
                if not emittedTextStart then
                    emittedTextStart <- true
                    emit AssistantTextStart Map.empty

                text.Append(delta) |> ignore
                emit AssistantTextDelta (Map.ofList [ "delta", delta ])
            | StreamEvent.ToolCallStart tc ->
                emit CodingAgent.EventKind.ToolCallStart (Map.ofList [ "tool_name", tc.Name; "call_id", tc.Id ])
            | StreamEvent.ToolCallEnd tc -> emit CodingAgent.EventKind.ToolCallEnd (Map.ofList [ "call_id", tc.Id ])
            | StreamEvent.StepFinish(_, response) -> responseFromStream <- response
            | StreamEvent.Finish(_, usage, response) ->
                finishUsage <- usage

                if response.IsSome then
                    responseFromStream <- response
            | _ -> ()

        let finalText = text.ToString()

        match responseFromStream with
        | Some response ->
            if not emittedTextStart then
                emit AssistantTextStart Map.empty

                if response.Text <> "" then
                    emit AssistantTextDelta (Map.ofList [ "delta", response.Text ])

            let finalMessage =
                if finalText = "" then
                    response.Message
                else
                    { response.Message with
                        Content = [ Text finalText ] }

            { response with
                Message = finalMessage
                Usage = finishUsage |> Option.defaultValue response.Usage }
        | None ->
            if not emittedTextStart then
                emit AssistantTextStart Map.empty

                if finalText <> "" then
                    emit AssistantTextDelta (Map.ofList [ "delta", finalText ])

            { Id = Guid.NewGuid().ToString("N")
              Model = request.Model
              Provider = profile.Id
              Message = Message.assistant (finalText)
              FinishReason = Stop "stream_end"
              Usage = finishUsage |> Option.defaultValue Usage.Zero
              ResponseId = None
              Raw = None
              Warnings = []
              RateLimit = None }

    let preHookResult (tc: ToolCallData) (errorMessage: string) =
        { ToolCallId = tc.Id
          Content = errorMessage
          IsError = true
          ImageData = None
          ImageMediaType = None }

    let runToolCallHook (phase: ToolCallHookPhase) (tc: ToolCallData) (result: ToolResultData option) =
        match config.ToolCallHook with
        | None -> Result.Ok()
        | Some hook ->
            try
                hook phase tc result
            with ex ->
                Result.Error ex.Message

    let runPostHookBestEffort (tc: ToolCallData) (result: ToolResultData) =
        match runToolCallHook ToolCallHookPhase.Post tc (Some result) with
        | Result.Ok() -> ()
        | Result.Error msg ->
            emit EventKind.Warning (Map.ofList [ "call_id", tc.Id; "message", $"Tool post-hook failed: {msg}" ])

    let finalizeToolResult (tc: ToolCallData) (result: ToolResultData) (invokePostHook: bool) =
        let fullOutput = result.Content
        let truncatedContent = Truncation.truncateToolOutput fullOutput tc.Name config

        emit ToolCallOutputDelta (Map.ofList [ "call_id", tc.Id; "delta", fullOutput ])

        if result.IsError then
            emit EventKind.Error (Map.ofList [ "call_id", tc.Id; "message", truncatedContent ])

            emitWithFullOutput
                CodingAgent.EventKind.ToolCallEnd
                (Map.ofList [ "call_id", tc.Id; "error", truncatedContent ])
                (Some fullOutput)
        else
            emitWithFullOutput
                CodingAgent.EventKind.ToolCallEnd
                (Map.ofList [ "call_id", tc.Id; "output", truncatedContent ])
                (Some fullOutput)

        if invokePostHook then
            runPostHookBestEffort tc result

        { result with
            Content = truncatedContent }

    let executeToolCall (tc: ToolCallData) =
        emit CodingAgent.EventKind.ToolCallStart (Map.ofList [ "tool_name", tc.Name; "call_id", tc.Id ])

        match runToolCallHook ToolCallHookPhase.Pre tc None with
        | Result.Error msg -> finalizeToolResult tc (preHookResult tc msg) false
        | Result.Ok() ->
            if mutatingToolNames.Contains(tc.Name) then
                toolCache.Clear()

            match toolRegistry.Resolve(tc.Name) with
            | Some tool when tool.IsCacheable ->
                let cacheKey = CacheKey.fromToolCall tc.Name tc.Arguments env.WorkingDirectory

                match Async.RunSynchronously(toolCache.TryGetTool cacheKey) with
                | Some cached -> finalizeToolResult tc { cached with ToolCallId = tc.Id } true
                | None ->
                    let result = toolRegistry.Dispatch(tc, env)
                    let finalized = finalizeToolResult tc result true

                    if not finalized.IsError then
                        Async.RunSynchronously(toolCache.PutTool cacheKey finalized)

                    finalized
            | _ ->
                let result = toolRegistry.Dispatch(tc, env)
                finalizeToolResult tc result true

    let executeToolCalls (toolCalls: ToolCallData list) : ToolResultData list =
        let runParallel = profile.SupportsParallelToolCalls && toolCalls.Length > 1

        if runParallel then
            let preResults =
                toolCalls
                |> List.map (fun tc ->
                    emit CodingAgent.EventKind.ToolCallStart (Map.ofList [ "tool_name", tc.Name; "call_id", tc.Id ])
                    (tc, runToolCallHook ToolCallHookPhase.Pre tc None))

            let dispatchable =
                preResults
                |> List.choose (fun (tc, hookResult) ->
                    match hookResult with
                    | Result.Ok() -> Some tc
                    | Result.Error _ -> None)

            let rawResultsByCallId =
                if dispatchable.IsEmpty then
                    Map.empty
                else
                    let raw = toolRegistry.DispatchAll(dispatchable, env, runParallel = true)

                    (dispatchable, raw)
                    ||> List.zip
                    |> List.map (fun (tc, result) -> tc.Id, result)
                    |> Map.ofList

            preResults
            |> List.map (fun (tc, hookResult) ->
                match hookResult with
                | Result.Error msg -> finalizeToolResult tc (preHookResult tc msg) false
                | Result.Ok() ->
                    let raw =
                        rawResultsByCallId
                        |> Map.tryFind tc.Id
                        |> Option.defaultValue (preHookResult tc $"Tool dispatch failed for call_id '{tc.Id}'")

                    finalizeToolResult tc raw true)
        else
            toolCalls |> List.map executeToolCall

    do
        autoRegisterProfileTools ()
        emit SessionStart (Map.ofList [ "provider", profile.Id; "model", profile.Model ])

    /// Get session ID
    member _.SessionId = sessionId

    /// Get current state
    member _.State = state

    /// Get history as list
    member _.History = history |> Seq.toList

    /// Get events as list
    member _.Events = events |> Seq.toList

    /// Get the config
    member _.Config = config

    member _.UserInstructions = userInstructions

    member _.CurrentDepth = currentDepth

    member _.SteeringQueueSnapshot = steeringQueue |> Seq.toList

    member _.FollowupQueueSnapshot = followupQueue |> Seq.toList

    member _.SubagentStatusSnapshot =
        lock subagentLock (fun () -> subagentStatuses |> Seq.map (fun pair -> pair.Key, pair.Value) |> Seq.toList)

    /// Cumulative token usage across every assistant turn in this session.
    member _.Usage =
        history
        |> Seq.fold
            (fun acc turn ->
                match turn with
                | AssistantTurn(_, _, _, usage, _) -> acc + usage
                | _ -> acc)
            Usage.Zero

    /// Cumulative microdollar cost, summed per-call so cache-hit semantics
    /// apply at the individual call granularity.
    member _.CostMicrodollars = sessionCostMicrodollars

    /// Set user instructions override
    member _.SetUserInstructions(instructions: string) = userInstructions <- Some instructions

    /// Register a custom tool
    member _.RegisterTool(tool: RegisteredTool) = toolRegistry.Register(tool)

    /// Queue a steering message for injection after current tool round
    member _.Steer(message: string) = steeringQueue.Enqueue(message)

    /// Queue a follow-up message for after current input completes
    member _.FollowUp(message: string) = followupQueue.Enqueue(message)

    /// Explicit host signal: next natural completion should transition to AwaitingInput.
    member _.RequestAwaitingInput() = awaitingInputRequested <- true

    /// Signal abort
    member _.Abort() =
        abortSignaled <- true
        closeAllSubagents ()
        state <- Closed
        emit SessionEnd (Map.ofList [ "reason", "aborted" ])

    /// Close the session
    member _.Close() =
        closeAllSubagents ()
        state <- Closed
        emit SessionEnd (Map.ofList [ "reason", "closed" ])

    member this.SaveCheckpoint(path: string) =
        let checkpoint =
            { Version = SessionCheckpointV1.CurrentVersion
              SessionId = sessionId
              ProviderId = profile.Id
              Model = profile.Model
              WorkingDirectory = env.WorkingDirectory
              State = string state
              UserInstructions = userInstructions
              AwaitingInputRequested = awaitingInputRequested
              CurrentDepth = currentDepth
              History = this.History |> List.map SessionPersistence.turnToDto
              Events = this.Events |> List.map SessionPersistence.eventToDto
              SteeringQueue = this.SteeringQueueSnapshot
              FollowupQueue = this.FollowupQueueSnapshot
              SubagentMetadata = this.SubagentStatusSnapshot |> List.map SessionPersistence.subagentToDto
              SavedAt = DateTimeOffset.UtcNow }

        match Async.RunSynchronously(SessionPersistence.fileBacked().Save path checkpoint) with
        | Result.Ok() -> ()
        | Result.Error message -> failwith message

    member private _.RestoreCheckpoint(checkpoint: SessionCheckpointV1) =
        sessionId <- checkpoint.SessionId
        history.Clear()
        events.Clear()
        steeringQueue.Clear()
        followupQueue.Clear()

        lock subagentLock (fun () ->
            subagentStatuses.Clear()

            for metadata in checkpoint.SubagentMetadata do
                subagentStatuses[metadata.AgentId] <- metadata.Status)

        for turn in checkpoint.History do
            history.Add(SessionPersistence.turnOfDto turn)

        for event in checkpoint.Events do
            events.Add(SessionPersistence.eventOfDto event)

        for message in checkpoint.SteeringQueue do
            steeringQueue.Enqueue(message)

        for message in checkpoint.FollowupQueue do
            followupQueue.Enqueue(message)

        userInstructions <- checkpoint.UserInstructions
        awaitingInputRequested <- checkpoint.AwaitingInputRequested

        state <-
            match checkpoint.State with
            | "Idle" -> Idle
            | "Processing" -> Processing
            | "AwaitingInput" -> AwaitingInput
            | "Closed" -> Closed
            | _ -> Idle

    static member RestoreFromCheckpoint
        (
            profile: IProviderProfile,
            env: IExecutionEnvironment,
            client: Client,
            checkpointPath: string,
            ?config: SessionConfig
        ) =
        match Async.RunSynchronously(SessionPersistence.fileBacked().Load checkpointPath) with
        | Result.Error message -> failwith message
        | Result.Ok checkpoint ->
            let session =
                Session(profile, env, client, ?config = config, depth = checkpoint.CurrentDepth)

            session.RestoreCheckpoint(checkpoint)
            session

    /// Process user input through the agentic loop
    member this.ProcessInput(userInput: string) =
        if state = Closed then
            failwith "Session is closed"

        state <- Processing
        emit TurnStart (Map.ofList [ "input", userInput ])
        history.Add(UserTurn(userInput, DateTime.UtcNow))
        emit UserInput (Map.ofList [ "content", userInput ])
        checkContextUsage ()

        // Drain pending steering before first LLM call
        drainSteering ()

        let mutable roundCount = 0
        let mutable keepLooping = true

        while keepLooping && not abortSignaled do
            // Check round limits
            if config.MaxToolRoundsPerInput > 0 && roundCount >= config.MaxToolRoundsPerInput then
                emit TurnLimit (Map.ofList [ "round", string roundCount ])
                keepLooping <- false
            elif config.MaxTurns > 0 && countTurns () >= config.MaxTurns then
                emit TurnLimit (Map.ofList [ "total_turns", string (countTurns ()) ])
                keepLooping <- false
            else
                // Build LLM request
                let systemPrompt = SystemPrompt.build profile env userInstructions
                let messages = HistoryConverter.toMessages (history |> Seq.toList)
                let toolDefs = profile.ToolDefinitions

                let request =
                    { Request.Create(profile.Model, Message.system (systemPrompt) :: messages) with
                        Tools = Some toolDefs
                        ToolChoice = Some ToolChoice.Auto
                        ReasoningEffort = config.ReasoningEffort
                        Provider = Some profile.Id
                        ProviderOptions = config.ProviderOptions }

                emit LlmCallStart Map.empty

                // Assistant output begin for non-streaming path.
                if not config.EnableStreaming || not profile.SupportsStreaming then
                    emit AssistantTextStart Map.empty

                let response =
                    if config.EnableStreaming && profile.SupportsStreaming then
                        completeWithStreaming request
                    else
                        client.Complete(request)

                emit LlmCallEnd (Map.ofList [ "model", response.Model ])

                // Record assistant turn
                let toolCalls = response.ToolCalls

                if not config.EnableStreaming || not profile.SupportsStreaming then
                    emit AssistantTextDelta (Map.ofList [ "delta", response.Text ])

                history.Add(
                    AssistantTurn(response.Text, toolCalls, response.Reasoning, response.Usage, DateTime.UtcNow)
                )

                // Accumulate per-call cost so cache-hit semantics apply per individual
                // call. Summing usage and pricing once at the end would incorrectly zero
                // output cost whenever any call in the session had a cache read.
                let cacheHit = (response.Usage.CacheReadTokens |> Option.defaultValue 0) > 0

                match Costing.tryCalculateCostById response.Model response.Usage cacheHit with
                | Some cost -> sessionCostMicrodollars <- sessionCostMicrodollars + cost.TotalMicrodollars
                | None -> ()

                emit AssistantTextEnd (Map.ofList [ "text", response.Text ])
                checkContextUsage ()

                // Natural completion: no tool calls
                if toolCalls.IsEmpty then
                    keepLooping <- false
                else
                    roundCount <- roundCount + 1

                    let results = executeToolCalls toolCalls

                    history.Add(ToolResultsTurn(results, DateTime.UtcNow))
                    checkContextUsage ()

                    // Drain steering after tool round
                    drainSteering ()

                    // Loop detection
                    if config.EnableLoopDetection then
                        if LoopDetection.detectLoop (history |> Seq.toList) config.LoopDetectionWindow then
                            let warning =
                                sprintf
                                    "Loop detected: the last %d tool calls follow a repeating pattern. Try a different approach."
                                    config.LoopDetectionWindow

                            history.Add(SteeringTurn(warning, DateTime.UtcNow))
                            emit EventKind.LoopDetection (Map.ofList [ "message", warning ])
                            checkContextUsage ()

        // Process follow-ups
        if followupQueue.Count > 0 && not abortSignaled then
            emit TurnEnd (Map.ofList [ "reason", "follow_up" ])
            let nextInput = followupQueue.Dequeue()
            this.ProcessInput(nextInput)
        else if not abortSignaled then
            if awaitingInputRequested then
                awaitingInputRequested <- false
                state <- AwaitingInput
                emit TurnEnd (Map.ofList [ "state", "awaiting_input" ])
                emit SessionEnd (Map.ofList [ "state", "awaiting_input" ])
            else
                state <- Idle
                emit TurnEnd (Map.ofList [ "state", "idle" ])
                emit SessionEnd (Map.ofList [ "state", "idle" ])
