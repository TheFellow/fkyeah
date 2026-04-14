namespace Attractor

open System
open System.IO
open System.Text.RegularExpressions
open System.Text
open System.Text.Json
open CodingAgent
open UnifiedLlm

module Handlers =

    let private truncate (maxChars: int) (text: string) =
        if String.IsNullOrEmpty(text) then ""
        elif text.Length > maxChars then text.Substring(0, maxChars) + "\n[truncated]"
        else text

    let private inferProviderFromModel (model: string) =
        let lower = model.ToLowerInvariant()
        if lower.StartsWith("claude") then "anthropic"
        elif lower.StartsWith("gpt")
             || lower.StartsWith("o1")
             || lower.StartsWith("o3")
             || lower.StartsWith("o4")
             || lower.Contains("codex") then
            "openai"
        elif lower.StartsWith("gemini") then "gemini"
        else "anthropic"

    let private profileForProvider (provider: string) (model: string) : IProviderProfile option =
        match provider.Trim().ToLowerInvariant() with
        | "openai" -> Some(OpenAIProfile(model) :> IProviderProfile)
        | "anthropic" -> Some(AnthropicProfile(model) :> IProviderProfile)
        | "gemini" -> Some(GeminiProfile(model) :> IProviderProfile)
        | _ -> None

    let private getLatestAssistantText (history: Turn list) =
        history
        |> List.rev
        |> List.tryPick (fun turn ->
            match turn with
            | AssistantTurn(content, _, _, _, _) when content <> "" -> Some content
            | _ -> None)
        |> Option.defaultValue ""

    let private formatHistory (history: Turn list) =
        history
        |> List.map (fun turn ->
            match turn with
            | UserTurn(content, timestamp) ->
                box {| kind = "user"; content = content; timestamp = timestamp |}
            | AssistantTurn(content, toolCalls, reasoning, usage, timestamp) ->
                let toolCallsData =
                    toolCalls
                    |> List.map (fun tc ->
                        {| id = tc.Id
                           name = tc.Name
                           arguments = tc.Arguments |})
                let usageData =
                    {| input_tokens = usage.InputTokens
                       output_tokens = usage.OutputTokens
                       reasoning_tokens = usage.ReasoningTokens
                       cache_read_tokens = usage.CacheReadTokens
                       cache_write_tokens = usage.CacheWriteTokens |}
                box
                    {| kind = "assistant"
                       content = content
                       tool_calls = toolCallsData
                       reasoning = reasoning
                       usage = usageData
                       timestamp = timestamp |}
            | ToolResultsTurn(results, timestamp) ->
                let resultsData =
                    results
                    |> List.map (fun r ->
                        {| tool_call_id = r.ToolCallId
                           content = r.Content
                           is_error = r.IsError |})
                box {| kind = "tool_results"; results = resultsData; timestamp = timestamp |}
            | SteeringTurn(content, timestamp) ->
                box {| kind = "steering"; content = content; timestamp = timestamp |}
            | SystemTurn(content, timestamp) ->
                box {| kind = "system"; content = content; timestamp = timestamp |})

    let buildCodingAgentInput (node: Node) (context: Context) (graph: Graph) =
        let promptBase =
            if node.Prompt <> "" then node.Prompt
            else node.Label
        let prompt = promptBase.Replace("$goal", graph.Goal)
        let snapshot = context.Snapshot()
        let sb = StringBuilder()

        if graph.Goal <> "" then
            sb.AppendLine("## Pipeline Goal") |> ignore
            sb.AppendLine(graph.Goal) |> ignore
            sb.AppendLine() |> ignore

        let appendContext label key =
            match snapshot |> Map.tryFind key with
            | Some value when value <> "" ->
                sb.AppendLine($"## {label}") |> ignore
                sb.AppendLine(truncate 20000 value) |> ignore
                sb.AppendLine() |> ignore
            | _ -> ()

        appendContext "Previous Stage Response" "last_response"
        appendContext "Previous Tool Output" "tool.output"
        appendContext "Previous Tool Stderr" "tool.stderr"
        appendContext "Human Input" "human.gate.input"

        sb.AppendLine("## Task") |> ignore
        sb.AppendLine(prompt) |> ignore
        sb.ToString()

    let private resolveVisitCount (context: Context) =
        context.TryGet("node.visit_count")
        |> Option.bind (fun raw ->
            match Int32.TryParse(raw) with
            | true, value -> Some value
            | _ -> None)
        |> Option.defaultValue 1

    let private resolveStageDirs (logsRoot: string) (node: Node) (context: Context) =
        let rootDir = Path.Combine(logsRoot, node.Id)
        let visit = resolveVisitCount context
        Path.Combine(rootDir, sprintf "%03d" visit), rootDir

    let private ensureDir (path: string) =
        if not (Directory.Exists(path)) then
            Directory.CreateDirectory(path) |> ignore

    let private writeStageFile (stageDir: string) (rootDir: string) (fileName: string) (content: string) =
        ensureDir stageDir
        File.WriteAllText(Path.Combine(stageDir, fileName), content)
        if stageDir <> rootDir then
            ensureDir rootDir
            File.WriteAllText(Path.Combine(rootDir, fileName), content)

    let private appendKnownLlmContext (context: Context) (baseUpdates: (string * string) list) =
        let extraKeys =
            [ "llm.cost_microdollars"
              "llm.cost_usd"
              "llm.input_tokens"
              "llm.output_tokens"
              "llm.cache_hit"
              "llm.model"
              "llm.provider"
              "llm.last_node" ]

        (baseUpdates, extraKeys)
        ||> List.fold (fun updates key ->
            match context.TryGet(key) with
            | Some value when value <> "" -> (key, value) :: updates
            | _ -> updates)
        |> List.rev
        |> Map.ofList

    let private resolvePreparedContext (node: Node) (context: Context) (graph: Graph) =
        let fidelity =
            let resolved = node.GetAttrString("__resolved_fidelity", "").Trim()
            if resolved <> "" then
                FidelityMode.Parse(resolved) |> Option.defaultValue FidelityMode.Compact
            elif node.Fidelity <> "" then
                FidelityMode.Parse(node.Fidelity) |> Option.defaultValue FidelityMode.Compact
            elif graph.DefaultFidelity <> "" then
                FidelityMode.Parse(graph.DefaultFidelity) |> Option.defaultValue FidelityMode.Compact
            else
                FidelityMode.Compact

        ContextPrompt.preparePromptContext fidelity context graph.Goal

    let resolveWorkingDir (node: Node) (graph: Graph) (defaultWorkingDir: string) =
        let nodeCwd = node.GetAttrString("cwd", "")
        let graphCwd = graph.GetGraphAttrString("cwd", "")
        let configuredWorkingDir =
            if nodeCwd <> "" then nodeCwd
            elif graphCwd <> "" then graphCwd
            elif defaultWorkingDir <> "" then defaultWorkingDir
            else Environment.CurrentDirectory
        if Path.IsPathRooted(configuredWorkingDir) then configuredWorkingDir
        else Path.GetFullPath(configuredWorkingDir)

    let private parseOutcomeFailPatterns (node: Node) =
        node.OutcomeFailPattern.Split('|', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.toList

    let private tryOutcomeFailFromPattern (node: Node) (responseText: string) (contextUpdates: Map<string, string>) =
        let patterns = parseOutcomeFailPatterns node
        patterns
        |> List.tryFind (fun pattern ->
            responseText.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
        |> Option.map (fun pattern ->
            { Outcome.Fail($"Matched outcome_fail_pattern '{pattern}'") with
                Notes = $"Response matched fail pattern '{pattern}'"
                ContextUpdates = contextUpdates })

    let private runHook
        (hookCommand: string)
        (workingDir: string)
        (nodeId: string)
        (stageDir: string)
        (logsRoot: string)
        (extraEnv: (string * string) list)
        : Result<string, string> =
        if hookCommand = "" then
            Result.Ok ""
        else
            let psi = System.Diagnostics.ProcessStartInfo("/bin/sh")
            psi.ArgumentList.Add("-c")
            psi.ArgumentList.Add(hookCommand)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.WorkingDirectory <- workingDir
            psi.EnvironmentVariables["ATTRACTOR_NODE_ID"] <- nodeId
            psi.EnvironmentVariables["ATTRACTOR_STAGE_DIR"] <- stageDir
            psi.EnvironmentVariables["ATTRACTOR_LOGS_ROOT"] <- logsRoot
            psi.EnvironmentVariables["ATTRACTOR_CWD"] <- workingDir
            for (k, v) in extraEnv do
                psi.EnvironmentVariables[k] <- v

            use proc = System.Diagnostics.Process.Start(psi)
            let stdoutTask = proc.StandardOutput.ReadToEndAsync()
            let stderrTask = proc.StandardError.ReadToEndAsync()
            proc.WaitForExit()
            let stdout = stdoutTask.Result
            let stderr = stderrTask.Result

            if proc.ExitCode = 0 then
                Result.Ok stdout
            else
                let details =
                    if not (String.IsNullOrWhiteSpace(stderr)) then stderr.Trim()
                    elif not (String.IsNullOrWhiteSpace(stdout)) then stdout.Trim()
                    else "(no output)"
                Result.Error $"Hook failed with exit code {proc.ExitCode}: {details}"

    /// Write a status.json file for a node outcome
    let writeStatus (stageDir: string) (rootDir: string) (outcome: Outcome) =
        HandlerArtifacts.writeStatus stageDir rootDir outcome

    /// Start handler: no-op, returns SUCCESS
    type StartHandler() =
        interface IHandler with
            member _.Execute(_, _, _, _) =
                Outcome.Success()

    /// Exit handler: no-op, returns SUCCESS
    type ExitHandler() =
        interface IHandler with
            member _.Execute(_, _, _, _) =
                Outcome.Success()

    /// Codergen handler: LLM task execution
    type CodergenHandler(?backend: ICodergenBackend) =
        interface IHandler with
            member _.Execute(node, context, graph, logsRoot) =
                // 1. Build prompt
                let prompt =
                    if node.Prompt <> "" then node.Prompt
                    else node.Label
                let prompt = prompt.Replace("$goal", graph.Goal)

                // 2. Write prompt and context snapshot to logs
                let stageDir, rootDir = resolveStageDirs logsRoot node context
                writeStageFile stageDir rootDir "prompt.md" prompt
                let contextSnapshot = context.Snapshot()
                if not contextSnapshot.IsEmpty then
                    let contextJson =
                        contextSnapshot
                        |> Map.toList
                        |> List.map (fun (k, v) -> sprintf "  %s: %s" (System.Text.Json.JsonSerializer.Serialize(k)) (System.Text.Json.JsonSerializer.Serialize(v)))
                        |> String.concat ",\n"
                    writeStageFile stageDir rootDir "context.json" (sprintf "{\n%s\n}" contextJson)

                let preparedContext = resolvePreparedContext node context graph
                let contextBudgetJson =
                    JsonSerializer.Serialize(
                        {| fidelity_mode = preparedContext.FidelityMode.ToString()
                           token_budget = preparedContext.TokenBudget
                           char_budget = FidelityMode.charBudget preparedContext.FidelityMode
                           char_budget_used = preparedContext.CharBudgetUsed
                           fresh_session = preparedContext.UsedFreshSession
                           included_keys = preparedContext.IncludedKeys
                           truncated_keys = preparedContext.TruncatedKeys
                           excluded_keys = preparedContext.ExcludedKeys |},
                        JsonSerializerOptions(WriteIndented = true))
                writeStageFile stageDir rootDir "context_budget.json" contextBudgetJson

                // 3. Call backend (once!)
                match backend with
                | Some b ->
                    match b.Run(node, prompt, context) with
                    | Result.Error outcome ->
                        writeStatus stageDir rootDir outcome
                        outcome
                    | Result.Ok responseText ->
                        writeStageFile stageDir rootDir "response.md" responseText
                        // Store up to 10K of response in context so subsequent stages can see it
                        let contextResponse =
                            if responseText.Length > 10000 then responseText.Substring(0, 10000) + "\n[response truncated for context — full text in logs]"
                            else responseText
                        let contextUpdates =
                            appendKnownLlmContext context [ "last_stage", node.Id; "last_response", contextResponse ]
                        let outcome =
                            match tryOutcomeFailFromPattern node responseText contextUpdates with
                            | Some failOutcome -> failOutcome
                            | None ->
                                Outcome.Success(
                                    notes = $"Stage completed: {node.Id}",
                                    contextUpdates = contextUpdates)
                        writeStatus stageDir rootDir outcome
                        outcome
                | None ->
                    let responseText = $"[Simulated] Response for stage: {node.Id}"
                    writeStageFile stageDir rootDir "response.md" responseText
                    let contextUpdates =
                        appendKnownLlmContext context [ "last_stage", node.Id; "last_response", responseText ]
                    let outcome =
                        match tryOutcomeFailFromPattern node responseText contextUpdates with
                        | Some failOutcome -> failOutcome
                        | None ->
                            Outcome.Success(
                                notes = $"Stage completed: {node.Id}",
                                contextUpdates = contextUpdates)
                    writeStatus stageDir rootDir outcome
                    outcome

    /// Coding agent handler: full multi-turn coding session with tool access.
    type CodingAgentHandler(llmClient: Client, ?defaultWorkingDir: string) =
        let defaultWorkingDir = defaultArg defaultWorkingDir ""

        interface IHandler with
            member _.Execute(node, context, graph, logsRoot) =
                let stageDir, rootDir = resolveStageDirs logsRoot node context

                let model =
                    if node.LlmModel <> "" then node.LlmModel
                    else
                        let graphModel = graph.GetGraphAttrString("llm_model", "")
                        if graphModel <> "" then graphModel else "claude-sonnet-4-6"

                let providerId =
                    if node.LlmProvider <> "" then node.LlmProvider
                    else inferProviderFromModel model

                match profileForProvider providerId model with
                | None ->
                    let outcome = Outcome.Fail($"Unsupported coding_agent provider '{providerId}'")
                    writeStatus stageDir rootDir outcome
                    outcome
                | Some profile ->
                    let workingDir = resolveWorkingDir node graph defaultWorkingDir

                    if not (Directory.Exists(workingDir)) then
                        Directory.CreateDirectory(workingDir) |> ignore

                    let userInput = buildCodingAgentInput node context graph
                    writeStageFile stageDir rootDir "prompt.md" userInput

                    let maxTurns =
                        node.GetAttr("max_turns")
                        |> Option.bind (fun v -> v.AsInt())
                        |> Option.defaultValue 20
                    let maxToolRounds =
                        node.GetAttr("max_tool_rounds")
                        |> Option.bind (fun v -> v.AsInt())
                        |> Option.defaultValue 25
                    let commandTimeout =
                        node.GetAttr("command_timeout")
                        |> Option.bind (fun v -> v.AsInt())
                        |> Option.defaultValue 120000
                    let reasoningEffort =
                        let raw = node.GetAttrString("reasoning_effort", "high").Trim()
                        if raw = "" then None else Some raw
                    let preHook = node.ToolHooksPre
                    let postHook = node.ToolHooksPost
                    let toolCallHook =
                        if preHook = "" && postHook = "" then
                            None
                        else
                            Some(fun kind (toolCall: UnifiedLlm.ToolCallData) (toolResult: UnifiedLlm.ToolResultData option) ->
                                if kind = CodingAgent.ToolCallHookPhase.Pre then
                                    runHook
                                        preHook
                                        workingDir
                                        node.Id
                                        stageDir
                                        logsRoot
                                        [ "TOOL_NAME", toolCall.Name
                                          "TOOL_ARGS", toolCall.Arguments
                                          "NODE_ID", node.Id ]
                                    |> Result.map (fun _ -> ())
                                elif kind = CodingAgent.ToolCallHookPhase.Post then
                                    let resultContent =
                                        toolResult
                                        |> Option.map (fun r -> r.Content)
                                        |> Option.defaultValue ""
                                    let exitCode =
                                        toolResult
                                        |> Option.map (fun r -> if r.IsError then "1" else "0")
                                        |> Option.defaultValue "1"
                                    match
                                        runHook
                                        postHook
                                        workingDir
                                        node.Id
                                        stageDir
                                        logsRoot
                                        [ "TOOL_NAME", toolCall.Name
                                          "TOOL_RESULT", resultContent
                                          "EXIT_CODE", exitCode
                                          "NODE_ID", node.Id ]
                                    with
                                    | Result.Ok _ -> Result.Ok()
                                    | Result.Error err ->
                                        eprintfn "Warning: post-hook command failed for node %s: %s" node.Id err
                                        Result.Ok()
                                else
                                    Result.Ok())

                    let sessionConfig =
                        { SessionConfig.Default with
                            MaxTurns = maxTurns
                            MaxToolRoundsPerInput = maxToolRounds
                            DefaultCommandTimeoutMs = commandTimeout
                            ReasoningEffort = reasoningEffort
                            ToolCallHook = toolCallHook }

                    let env = LocalExecutionEnvironment(workingDir) :> IExecutionEnvironment
                    env.Initialize()
                    let checkpointPath = Path.Combine(rootDir, SessionPersistence.checkpointFileName)
                    let threadId = node.ThreadId
                    let sharedCheckpointPath =
                        if threadId <> "" then
                            let threadDir = Path.Combine(logsRoot, $"thread-{threadId}")
                            Directory.CreateDirectory(threadDir) |> ignore
                            Path.Combine(threadDir, SessionPersistence.checkpointFileName)
                        else
                            Path.Combine(logsRoot, SessionPersistence.checkpointFileName)

                    let saveCheckpoint (session: Session) =
                        session.SaveCheckpoint(checkpointPath)
                        if not (String.Equals(sharedCheckpointPath, checkpointPath, StringComparison.Ordinal)) then
                            session.SaveCheckpoint(sharedCheckpointPath)

                    try
                        try
                            let session =
                                let restorePath =
                                    if File.Exists(sharedCheckpointPath) then sharedCheckpointPath
                                    elif File.Exists(checkpointPath) then checkpointPath
                                    else ""

                                if restorePath <> "" then
                                    Session.RestoreFromCheckpoint(profile, env, llmClient, restorePath, sessionConfig)
                                else
                                    Session(profile, env, llmClient, sessionConfig)

                            let systemPrompt = node.GetAttrString("system_prompt", "").Trim()
                            if systemPrompt <> "" && session.UserInstructions.IsNone then
                                session.SetUserInstructions(systemPrompt)

                            let autoCheckpointKey = $"{node.Id}:{session.SessionId}"
                            AutoCheckpointRegistry.register autoCheckpointKey (fun () -> saveCheckpoint session)

                            try
                                session.ProcessInput(userInput)
                                saveCheckpoint session
                            finally
                                AutoCheckpointRegistry.unregister autoCheckpointKey

                            let finalResponse = getLatestAssistantText session.History
                            writeStageFile stageDir rootDir "response.md" finalResponse

                            let historyJson =
                                session.History
                                |> formatHistory
                                |> fun turns ->
                                    JsonSerializer.Serialize(
                                        turns,
                                        JsonSerializerOptions(WriteIndented = true))
                            writeStageFile stageDir rootDir "history.json" historyJson

                            let toolOutputFromSession =
                                session.Events
                                |> List.choose (fun evt ->
                                    if evt.Kind = CodingAgent.EventKind.ToolCallEnd then
                                        evt.Data |> Map.tryFind "output"
                                    else None)
                                |> String.concat "\n\n"
                            let toolOutput = toolOutputFromSession
                            if toolOutput <> "" then
                                writeStageFile stageDir rootDir "tool_output.txt" toolOutput

                            let hitTurnLimit =
                                session.Events |> List.exists (fun evt -> evt.Kind = CodingAgent.EventKind.TurnLimit)
                            let aborted =
                                session.State = SessionState.Closed
                                || (session.Events
                                    |> List.exists (fun evt ->
                                        evt.Kind = CodingAgent.EventKind.SessionEnd
                                        && (evt.Data |> Map.tryFind "reason" = Some "aborted")))

                            let contextUpdates =
                                appendKnownLlmContext context [ "last_stage", node.Id; "last_response", finalResponse ]

                            let outcomeBase =
                                if session.State = SessionState.Idle && not hitTurnLimit && not aborted then
                                    Outcome.Success(
                                        notes = $"Stage completed: {node.Id}",
                                        contextUpdates = contextUpdates)
                                elif aborted then
                                    { Outcome.Fail("Coding agent session aborted") with
                                        ContextUpdates = contextUpdates }
                                else
                                    { Outcome.Fail("Coding agent session hit turn limits before completion") with
                                        ContextUpdates = contextUpdates }
                            let outcome =
                                match tryOutcomeFailFromPattern node finalResponse contextUpdates with
                                | Some failOutcome -> failOutcome
                                | None -> outcomeBase
                            writeStatus stageDir rootDir outcome
                            outcome
                        with
                        | :? UnifiedLlm.ProviderError as pe when pe.Retryable ->
                            // Let retryable provider errors propagate to Engine's retry loop
                            reraise ()
                        | :? System.Net.Http.HttpRequestException ->
                            // Network-level failures are retryable
                            reraise ()
                        | ex ->
                            let outcome = Outcome.Fail($"Coding agent failed: {ex.Message}")
                            writeStatus stageDir rootDir outcome
                            outcome
                    finally
                        env.Cleanup()

    /// Conditional handler: no-op pass-through, engine evaluates edge conditions
    type ConditionalHandler() =
        interface IHandler with
            member _.Execute(node, _, _, _) =
                Outcome.Success(notes = $"Conditional node evaluated: {node.Id}")

    /// Wait for human handler
    type WaitForHumanHandler(interviewer: IInterviewer) =
        interface IHandler with
            member _.Execute(node, context, graph, logsRoot) =
                let stageDir, rootDir = resolveStageDirs logsRoot node context
                // 1. Derive choices from outgoing edges
                let edges = graph.OutgoingEdges(node.Id)
                if edges.IsEmpty then
                    Outcome.Fail("No outgoing edges for human gate")
                else
                    let choices =
                        edges
                        |> List.map (fun edge ->
                            let label = if edge.Label <> "" then edge.Label else edge.ToNode
                            let key = AcceleratorKey.parse label
                            {| Key = key; Label = label; To = edge.ToNode |})

                    // 2. Write prompt.md if node has a prompt attribute
                    let hasPrompt = node.Prompt <> ""
                    let promptFilePath =
                        if hasPrompt then
                            let expandedPrompt =
                                node.Prompt.Replace("$ATTRACTOR_LOGS_ROOT", logsRoot)
                            let path = Path.Combine(stageDir, "prompt.md")
                            writeStageFile stageDir rootDir "prompt.md" expandedPrompt
                            Some path
                        else
                            None

                    // Build metadata with logs root and last completed stage
                    let lastStage = context.Get("last_stage", "")
                    let metadata =
                        let m =
                            Map.ofList [
                                "logs_root", logsRoot
                                "last_stage", lastStage
                                "goal", graph.Goal
                                "node_id", node.Id
                            ]
                        let withAttrs =
                            node.Attributes
                            |> Map.fold (fun acc key value ->
                                acc |> Map.add $"attr.{key}" (value.AsString())) m
                        match promptFilePath with
                        | Some pf -> withAttrs |> Map.add "prompt_file" pf
                        | None -> withAttrs

                    // 3. Freeform vs multi-choice path
                    if edges.Length = 1 && hasPrompt then
                        // FREEFORM PATH: single edge + prompt = freeform input gate
                        let responseFilePath = Path.Combine(stageDir, "response.md")
                        writeStageFile stageDir rootDir "response.md" ""

                        let metadata = metadata |> Map.add "response_file" responseFilePath

                        let question =
                            { Text = if node.Label <> node.Id then node.Label else "Enter input:"
                              Type = QuestionType.Freeform
                              Options = []
                              Default = None
                              TimeoutSeconds = None
                              Stage = node.Id
                              Metadata = metadata }

                        let answer =
                            try
                                interviewer.Ask(question)
                            with
                            | :? OperationCanceledException ->
                                Answer.Skipped

                        if answer.IsSkipped then
                            Outcome.Fail("human skipped interaction")
                        else
                            // Read response.md contents
                            let responseContent =
                                if File.Exists(responseFilePath) then
                                    File.ReadAllText(responseFilePath).Trim()
                                else
                                    ""
                            // Use file content if available, otherwise fall back to answer text
                            let inputValue =
                                if responseContent <> "" then responseContent
                                else answer.Value

                            let selected = choices[0]
                            { Outcome.Success(
                                contextUpdates =
                                    Map.ofList [
                                        "human.gate.selected", selected.Key
                                        "human.gate.label", selected.Label
                                        "human.gate.input", inputValue ])
                              with
                                SuggestedNextIds = [ selected.To ] }
                    else
                        // MULTI-CHOICE PATH (existing behavior, enhanced with prompt_file)
                        let options =
                            choices
                            |> List.map (fun c ->
                                { Key = c.Key; Label = AcceleratorKey.displayLabel c.Label })

                        let question =
                            { Text = if node.Label <> node.Id then node.Label else "Select an option:"
                              Type = QuestionType.MultipleChoice
                              Options = options
                              Default = None
                              TimeoutSeconds = None
                              Stage = node.Id
                              Metadata = metadata }

                        let answer =
                            try
                                interviewer.Ask(question)
                            with
                            | :? OperationCanceledException ->
                                Answer.Skipped

                        // Handle timeout/skip
                        if answer.IsTimeout then
                            let defaultChoice =
                                node.GetAttrString("human.default_choice", "")
                            if defaultChoice <> "" then
                                let selected =
                                    choices
                                    |> List.tryFind (fun c -> c.Key = defaultChoice || c.Label = defaultChoice)
                                    |> Option.defaultValue choices[0]
                                { Outcome.Success(
                                    contextUpdates = Map.ofList [ "human.gate.selected", selected.Key; "human.gate.label", selected.Label ])
                                  with
                                    SuggestedNextIds = [ selected.To ] }
                            else
                                Outcome.Retry("human gate timeout, no default")
                        elif answer.IsSkipped then
                            Outcome.Fail("human skipped interaction")
                        else
                            // Find matching choice
                            let selected =
                                choices
                                |> List.tryFind (fun c ->
                                    c.Key.Equals(answer.Value, StringComparison.OrdinalIgnoreCase)
                                    || AcceleratorKey.normalizeLabel c.Label = AcceleratorKey.normalizeLabel answer.Value
                                    || (answer.SelectedOption |> Option.map (fun o -> o.Key = c.Key) |> Option.defaultValue false))
                                |> Option.defaultValue choices[0]

                            { Outcome.Success(
                                contextUpdates = Map.ofList [ "human.gate.selected", selected.Key; "human.gate.label", selected.Label ])
                              with
                                SuggestedNextIds = [ selected.To ]
                                PreferredLabel = selected.Label }

    /// Parallel handler: fans out execution to branches concurrently.
    /// Accepts a registry resolver so it can actually execute branch node handlers.
    /// Spec note (§4.8): `error_policy`, `k_of_n`, and `quorum` attributes are intentionally unsupported.
    /// They are parsed as inert metadata and ignored at runtime.
    type ParallelHandler(?resolveHandler: Node -> IHandler) =
        interface IHandler with
            member _.Execute(node, context, graph, logsRoot) =
                let branches = graph.OutgoingEdges(node.Id)
                if branches.IsEmpty then
                    Outcome.Success(notes = "No branches to execute")
                else
                    // Execute branches concurrently with isolated context clones
                    let branchTasks =
                        branches
                        |> List.map (fun branch ->
                            async {
                                let branchContext = context.Clone()
                                match graph.Nodes |> Map.tryFind branch.ToNode with
                                | Some targetNode ->
                                    try
                                        // Actually execute the branch handler if we have a resolver
                                        let outcome =
                                            match resolveHandler with
                                            | Some resolve ->
                                                let handler = resolve targetNode
                                                handler.Execute(targetNode, branchContext, graph, logsRoot)
                                            | None ->
                                                // Fallback: no resolver, just mark success
                                                Outcome.Success(notes = $"Branch {targetNode.Id} (no handler)")

                                        // Keep branch updates isolated until all branches complete.
                                        return (branch.ToNode, outcome.Status, outcome.ContextUpdates)
                                    with _ ->
                                        return (branch.ToNode, StageStatus.Fail, Map.empty)
                                | None ->
                                    return (branch.ToNode, StageStatus.Fail, Map.empty)
                            })
                        |> Array.ofList

                    let results =
                        branchTasks
                        |> Async.Parallel
                        |> Async.RunSynchronously
                        |> Array.toList

                    let successCount = results |> List.filter (fun (_, s, _) -> s = StageStatus.Success) |> List.length
                    let failCount = results |> List.filter (fun (_, s, _) -> s <> StageStatus.Success) |> List.length

                    let mergedBranchUpdates =
                        results
                        |> List.fold (fun acc (branchId, _, updates) ->
                            updates
                            |> Map.fold (fun state key value ->
                                state
                                |> Map.add $"parallel.{node.Id}.{branchId}.{key}" value
                                |> Map.add key value) acc) Map.empty

                    // Write per-branch results to context + record executed nodes
                    let executedNodes =
                        results |> List.map (fun (id, _, _) -> id) |> String.concat ","
                    let laneNames =
                        results
                        |> List.map (fun (branchId, _, _) ->
                            let branchNode = graph.Nodes |> Map.tryFind branchId
                            branchNode
                            |> Option.map (fun n -> n.GetAttrString("lane", branchId))
                            |> Option.defaultValue branchId)
                    let metadataUpdates =
                        results
                        |> List.fold (fun acc (branchId, status, _) ->
                            let branchNode = graph.Nodes |> Map.tryFind branchId
                            let lane =
                                branchNode
                                |> Option.map (fun n -> n.GetAttrString("lane", branchId))
                                |> Option.defaultValue branchId
                            acc
                            |> Map.add $"parallel.branch.{branchId}.status" (status.ToString())
                            |> Map.add $"parallel.{node.Id}.{branchId}.lane" lane)
                            (Map.ofList
                                [ "parallel.success_count", string successCount
                                  "parallel.fail_count", string failCount
                                  "parallel.executed_nodes", executedNodes ])
                        |> Map.add $"parallel.{node.Id}.lanes" (String.concat "," laneNames)
                    let contextUpdates =
                        metadataUpdates
                        |> Map.fold (fun state key value -> Map.add key value state) mergedBranchUpdates

                    let status =
                        if failCount = 0 then StageStatus.Success
                        else StageStatus.PartialSuccess

                    // Find the fan-in node: look for nodes that all branches converge to
                    let fanInTargets =
                        branches
                        |> List.collect (fun branch ->
                            graph.OutgoingEdges(branch.ToNode)
                            |> List.map (fun e -> e.ToNode))
                        |> List.distinct

                    { Status = status
                      PreferredLabel = ""
                      SuggestedNextIds = fanInTargets
                      ContextUpdates = contextUpdates
                      Notes = $"Parallel: {successCount} succeeded, {failCount} failed"
                      FailureReason = "" }

    /// Fan-in handler: consolidates parallel results
    type FanInHandler() =
        interface IHandler with
            member _.Execute(node, context, _, _) =
                // Read parallel branch results from context
                let successCount = context.TryGet("parallel.success_count")
                let failCount = context.TryGet("parallel.fail_count")
                match successCount, failCount with
                | Some sc, Some fc ->
                    Outcome.Success(
                        notes = $"Fan-in completed: {sc} succeeded, {fc} failed",
                        contextUpdates = Map.ofList [ "parallel.fan_in.completed", "true" ])
                | _ ->
                    // No parallel results, pass through
                    Outcome.Success(notes = "Fan-in: no parallel results, passing through")

    /// Tool handler: executes external commands with timeout and output truncation.
    /// If the node has a `prompt` attribute, writes it to {stage_dir}/prompt.txt
    /// and sets ATTRACTOR_PROMPT_FILE env var so tool_command can reference it.
    type ToolHandler(?maxOutputBytes: int) =
        let outputLimit = defaultArg maxOutputBytes 30000

        interface IHandler with
            member _.Execute(node, context, graph, logsRoot) =
                let command = node.GetAttrString("tool_command", "")
                if command = "" then
                    Outcome.Fail("No tool_command specified")
                else
                    try
                        let stageDir, rootDir = resolveStageDirs logsRoot node context
                        let workingDir = resolveWorkingDir node graph ""
                        ensureDir stageDir
                        ensureDir rootDir
                        // If node has a prompt, write it to a file for the tool to consume
                        let promptFile =
                            let prompt = node.Prompt
                            if prompt <> "" then
                                let path = Path.Combine(stageDir, "prompt.txt")
                                writeStageFile stageDir rootDir "prompt.txt" prompt
                                Some path
                            else
                                None

                        match
                            runHook
                                node.ToolHooksPre
                                workingDir
                                node.Id
                                stageDir
                                logsRoot
                                [ "TOOL_NAME", "shell"
                                  "TOOL_ARGS", command
                                  "NODE_ID", node.Id ]
                        with
                        | Result.Error hookError ->
                            eprintfn "Warning: pre-hook command failed for node %s: %s" node.Id hookError
                            let outcome =
                                { Status = StageStatus.Skipped
                                  PreferredLabel = ""
                                  SuggestedNextIds = []
                                  ContextUpdates = Map.empty
                                  Notes = "Tool execution skipped because pre-hook failed"
                                  FailureReason = hookError }
                            writeStatus stageDir rootDir outcome
                            outcome
                        | Result.Ok _ ->
                            let psi = System.Diagnostics.ProcessStartInfo("/bin/sh")
                            psi.ArgumentList.Add("-c")
                            psi.ArgumentList.Add(command)
                            psi.RedirectStandardOutput <- true
                            psi.RedirectStandardError <- true
                            psi.RedirectStandardInput <- (promptFile.IsSome)
                            psi.UseShellExecute <- false
                            psi.WorkingDirectory <- workingDir

                            // Set env vars for the tool command
                            match promptFile with
                            | Some path ->
                                psi.EnvironmentVariables["ATTRACTOR_PROMPT_FILE"] <- path
                            | None -> ()
                            psi.EnvironmentVariables["ATTRACTOR_STAGE_DIR"] <- rootDir
                            psi.EnvironmentVariables["ATTRACTOR_LOGS_ROOT"] <- logsRoot
                            psi.EnvironmentVariables["ATTRACTOR_NODE_ID"] <- node.Id
                            psi.EnvironmentVariables["ATTRACTOR_CWD"] <- workingDir

                            let proc = System.Diagnostics.Process.Start(psi)

                            // Pipe prompt to stdin if available
                            match promptFile with
                            | Some path ->
                                let promptText = File.ReadAllText(path)
                                proc.StandardInput.Write(promptText)
                                proc.StandardInput.Close()
                            | None -> ()

                            // Read stdout and stderr asynchronously to avoid deadlocks
                            let stdoutTask = proc.StandardOutput.ReadToEndAsync()
                            let stderrTask = proc.StandardError.ReadToEndAsync()

                            // Apply timeout if configured
                            let timeoutMs =
                                node.Timeout
                                |> Option.map (fun d -> int d.Milliseconds)
                                |> Option.defaultValue 0 // 0 = no timeout

                            let completed =
                                if timeoutMs > 0 then
                                    proc.WaitForExit(timeoutMs)
                                else
                                    proc.WaitForExit()
                                    true

                            if not completed then
                                try proc.Kill(true) with _ -> ()
                                match
                                    runHook
                                        node.ToolHooksPost
                                        workingDir
                                        node.Id
                                        stageDir
                                        logsRoot
                                        [ "TOOL_NAME", "shell"
                                          "TOOL_RESULT", ""
                                          "EXIT_CODE", "124"
                                          "NODE_ID", node.Id ]
                                with
                                | Result.Error err ->
                                    eprintfn "Warning: post-hook command failed for node %s: %s" node.Id err
                                | Result.Ok _ -> ()
                                Outcome.Fail($"Tool timed out after {timeoutMs}ms: {command}")
                            else
                                let fullOutput = stdoutTask.Result
                                let stderr = stderrTask.Result
                                let exitCode = proc.ExitCode

                                // Write full output to logs
                                writeStageFile stageDir rootDir "tool_output.txt" fullOutput
                                if stderr.Length > 0 then
                                    writeStageFile stageDir rootDir "tool_stderr.txt" stderr

                                // Truncate output for context
                                let truncatedOutput =
                                    if fullOutput.Length > outputLimit then
                                        fullOutput.Substring(0, outputLimit) + $"\n[WARNING: Tool output was truncated. {fullOutput.Length - outputLimit} characters removed]"
                                    else
                                        fullOutput

                                match
                                    runHook
                                        node.ToolHooksPost
                                        workingDir
                                        node.Id
                                        stageDir
                                        logsRoot
                                        [ "TOOL_NAME", "shell"
                                          "TOOL_RESULT", truncatedOutput
                                          "EXIT_CODE", string exitCode
                                          "NODE_ID", node.Id ]
                                with
                                | Result.Error err ->
                                    eprintfn "Warning: post-hook command failed for node %s: %s" node.Id err
                                | Result.Ok _ -> ()

                                let updates =
                                    Map.ofList
                                        [ "tool.output", truncatedOutput
                                          "tool.stderr", stderr
                                          "tool_stdout", truncatedOutput
                                          "tool_stderr", stderr
                                          "tool_exit_code", string exitCode ]

                                if exitCode = 0 then
                                    Outcome.Success(
                                        notes = $"Tool completed: {command}",
                                        contextUpdates = updates)
                                else
                                    { Outcome.Fail($"Tool failed with exit code {exitCode}") with
                                        ContextUpdates = updates }
                    with ex ->
                        Outcome.Fail(ex.Message)

    /// Mutable callback for running child pipelines, wired up in Engine.fs.
    /// Signature: dotSource -> logsRoot -> (finalOutcome * contextSnapshot)
    let mutable childPipelineRunner: (string -> string -> Outcome * Map<string, string>) option = None

    /// Manager loop handler: child pipeline execution with polling/steering fallback.
    /// The optional runChild callback has signature: dotSource -> logsRoot -> (finalOutcome * contextSnapshot).
    /// When omitted, falls back to the module-level childPipelineRunner mutable (wired by EngineWiring in Engine.fs).
    type ManagerLoopHandler(?runChild: string -> string -> Outcome * Map<string, string>) =

        let resolveRunner () =
            match runChild with
            | Some f -> Some f
            | None -> childPipelineRunner

        let getIntAttr (node: Node) (keys: string list) (defaultValue: int) =
            keys
            |> List.tryPick (fun key ->
                node.GetAttr(key)
                |> Option.bind (fun v -> v.AsInt()))
            |> Option.defaultValue defaultValue

        let executePollingMode (node: Node) (context: Context) =
            let lane = node.GetAttrString("lane", "")
            let maxCycles = getIntAttr node [ "max_cycles"; "manager.max_cycles" ] 10

            let stopConditionKey =
                node.GetAttrString("stop_condition_key", "manager.stop")

            let observeKey =
                node.GetAttrString("observe_key", "manager.subordinate_output")

            let waitMs =
                node.GetAttr("wait_ms")
                |> Option.bind (fun v -> v.AsInt())
                |> Option.defaultValue 100
                |> max 0

            let mutable cycle = 0
            let mutable stopped = false
            let mutable lastObserved = ""
            let mutable repeatCount = 0
            let mutable steeringMessage = ""

            let hasStopSignal () =
                match context.TryGet(stopConditionKey) with
                | Some raw when raw.Equals("true", StringComparison.OrdinalIgnoreCase) -> true
                | _ ->
                    match context.TryGet("manager.child_complete") with
                    | Some raw when raw.Equals("true", StringComparison.OrdinalIgnoreCase) -> true
                    | _ -> false

            let shouldInjectSteering (observed: string) =
                if String.IsNullOrWhiteSpace(observed) then
                    false
                else
                    let lower = observed.ToLowerInvariant()
                    repeatCount >= 1
                    || lower.Contains("stuck")
                    || lower.Contains("off-track")
                    || lower.Contains("off track")
                    || lower.Contains("blocked")
                    || lower.Contains("loop")

            while cycle < maxCycles && not stopped do
                cycle <- cycle + 1

                // Observe subordinate output.
                let observed = context.TryGet(observeKey) |> Option.defaultValue ""
                if observed <> "" then
                    context.Set("manager.last_observed", observed)

                if observed <> "" && observed = lastObserved then
                    repeatCount <- repeatCount + 1
                else
                    repeatCount <- 0
                lastObserved <- observed

                if hasStopSignal () then
                    stopped <- true

                if not stopped then
                    // Steer when subordinate appears stuck or off-track.
                    if shouldInjectSteering observed then
                        steeringMessage <-
                            $"Cycle {cycle}: steer subordinate back to goal, unblock the next concrete step, and report progress."
                        context.Set("manager.steering", steeringMessage)
                        context.Set("manager.correction_injected", "true")

                    context.Set("manager.cycle", string cycle)
                    if cycle < maxCycles then
                        System.Threading.Thread.Sleep(waitMs)

            let stoppedStr = if stopped then "true" else "false"
            let status =
                if stopped then StageStatus.Success
                else StageStatus.Fail
            { Status = status
              PreferredLabel = ""
              SuggestedNextIds = []
              ContextUpdates =
                Map.ofList
                    [ "manager.total_cycles", string cycle
                      "manager.turns_used", string cycle
                      "manager.stopped", stoppedStr ]
                    |> fun updates ->
                        if lane <> "" then
                            updates |> Map.add "manager.lane" lane
                        else
                            updates
                    |> fun updates ->
                        if steeringMessage <> "" then
                            updates |> Map.add "manager.steering" steeringMessage
                        else
                            updates
              Notes = $"Manager loop: {cycle} cycles, stopped={stopped}"
              FailureReason =
                if status = StageStatus.Fail then
                    $"manager loop reached max_cycles ({maxCycles}) without stop condition"
                else "" }

        let executeChildMode (node: Node) (_context: Context) (graph: Graph) (logsRoot: string) =
            let childDotfile = graph.StackChildDotfile.Trim()
            let childWorkdir = graph.StackChildWorkdir.Trim()

            let resolvedPath =
                if Path.IsPathRooted(childDotfile) then
                    childDotfile
                elif childWorkdir <> "" then
                    Path.Combine(childWorkdir, childDotfile)
                else
                    childDotfile

            if not (File.Exists(resolvedPath)) then
                Outcome.Fail($"Manager loop node {node.Id}: child DOT not found: {resolvedPath}")
            else
                match resolveRunner() with
                | None ->
                    Outcome.Fail($"Manager loop node {node.Id}: child pipeline runner not configured (Engine not wired)")
                | Some runChildFn ->
                let childDotSource = File.ReadAllText(resolvedPath)
                let maxCycles = getIntAttr node [ "manager.max_cycles"; "max_cycles" ] 10
                let pollInterval = getIntAttr node [ "manager.poll_interval"; "poll_interval" ] 5 |> max 0
                let lane = node.GetAttrString("lane", "")

                let mutable cycle = 0
                let mutable finalOutcome: Outcome option = None
                let mutable lastChildStatus = "unknown"

                while cycle < maxCycles && finalOutcome.IsNone do
                    let childLogsRoot = Path.Combine(logsRoot, node.Id, $"cycle_{cycle}")

                    try
                        let (childOutcome, childContext) = runChildFn childDotSource childLogsRoot
                        lastChildStatus <- childOutcome.Status.ToString().ToLowerInvariant()

                        match childOutcome.Status with
                        | StageStatus.Success
                        | StageStatus.PartialSuccess ->
                            let baseUpdates =
                                [ "manager.cycle_count", string (cycle + 1)
                                  "manager.total_cycles", string (cycle + 1)
                                  "manager.turns_used", string (cycle + 1)
                                  "manager.child_status", lastChildStatus ]
                                |> fun updates ->
                                    if lane <> "" then
                                        ("manager.lane", lane) :: updates
                                    else
                                        updates

                            let updates =
                                childContext
                                |> Map.fold (fun acc key value -> acc |> Map.add $"child.{key}" value) (baseUpdates |> Map.ofList)

                            finalOutcome <-
                                Some
                                    { Outcome.Success(
                                        notes = $"Manager loop completed after {cycle + 1} cycle(s)",
                                        contextUpdates = updates) with
                                        Status = childOutcome.Status }
                        | _ ->
                            if cycle < maxCycles - 1 && pollInterval > 0 then
                                System.Threading.Thread.Sleep(pollInterval * 1000)
                    with ex ->
                        if cycle = maxCycles - 1 then
                            finalOutcome <-
                                Some(
                                    Outcome.Fail(
                                        $"Manager loop node {node.Id}: child pipeline threw after {cycle + 1} cycle(s): {ex.Message}"
                                    ))
                        elif pollInterval > 0 then
                            System.Threading.Thread.Sleep(pollInterval * 1000)

                    cycle <- cycle + 1

                match finalOutcome with
                | Some outcome -> outcome
                | None ->
                    Outcome.Fail(
                        $"Manager loop node {node.Id}: child pipeline failed after {maxCycles} cycle(s), last status: {lastChildStatus}"
                    )

        interface IHandler with
            member _.Execute(node, context, graph, logsRoot) =
                let stageDir, rootDir = resolveStageDirs logsRoot node context
                let childDotfile = graph.StackChildDotfile.Trim()

                let outcome =
                    if childDotfile = "" then
                        executePollingMode node context
                    else
                        executeChildMode node context graph logsRoot

                writeStatus stageDir rootDir outcome
                outcome

/// Handler registry
type HandlerRegistry() =
    let handlers = System.Collections.Generic.Dictionary<string, IHandler>()
    let mutable defaultHandler: IHandler = Handlers.CodergenHandler() :> IHandler

    member _.Register(typeString: string, handler: IHandler) =
        handlers[typeString] <- handler

    member _.SetDefault(handler: IHandler) =
        defaultHandler <- handler

    member _.Resolve(node: Node) : IHandler =
        // 1. Explicit type attribute
        if node.NodeType <> "" then
            match handlers.TryGetValue(node.NodeType) with
            | true, h -> h
            | false, _ -> defaultHandler
        else
            // 2. Shape-based resolution
            let handlerType = ShapeMapping.resolveHandlerType node
            match handlers.TryGetValue(handlerType) with
            | true, h -> h
            | false, _ -> defaultHandler

    /// Create a registry with all built-in handlers registered
    static member CreateDefault
        (
            ?interviewer: IInterviewer,
            ?backend: ICodergenBackend,
            ?llmClient: Client,
            ?acpPermissionStrategy: AcpRuntime.PermissionStrategy
        ) =
        let registry = HandlerRegistry()
        let interviewer = defaultArg interviewer (AutoApproveInterviewer() :> IInterviewer)
        let llmClient = defaultArg llmClient (Client())
        let acpPermissionStrategy =
            defaultArg acpPermissionStrategy AcpRuntime.PermissionStrategy.DenyAll
        registry.Register("start", Handlers.StartHandler())
        registry.Register("exit", Handlers.ExitHandler())
        match backend with
        | Some b -> registry.Register("codergen", Handlers.CodergenHandler(b))
        | None -> registry.Register("codergen", Handlers.CodergenHandler())
        registry.Register("coding_agent", Handlers.CodingAgentHandler(llmClient))
        registry.Register("wait.human", Handlers.WaitForHumanHandler(interviewer))
        registry.Register("conditional", Handlers.ConditionalHandler())
        // Pass the registry's own Resolve method so parallel branches execute real handlers
        registry.Register("parallel", Handlers.ParallelHandler(resolveHandler = registry.Resolve))
        registry.Register("parallel.fan_in", Handlers.FanInHandler())
        registry.Register("tool", Handlers.ToolHandler())
        registry.Register("mcp.tool", McpHandlers.McpToolHandler())
        registry.Register("acp.agent", AcpHandlers.AcpAgentHandler(permissionStrategy = acpPermissionStrategy))
        registry.Register("stack.manager_loop", Handlers.ManagerLoopHandler())
        match backend with
        | Some b -> registry.SetDefault(Handlers.CodergenHandler(b))
        | None -> registry.SetDefault(Handlers.CodergenHandler())
        registry
