namespace Attractor

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// Backoff configuration for retries
type BackoffConfig =
    { InitialDelayMs: int
      BackoffFactor: float
      MaxDelayMs: int
      Jitter: bool }

    static member Default =
        { InitialDelayMs = 200
          BackoffFactor = 2.0
          MaxDelayMs = 60000
          Jitter = true }

    static member None =
        { InitialDelayMs = 0
          BackoffFactor = 1.0
          MaxDelayMs = 0
          Jitter = false }

    member this.DelayForAttempt(attempt: int) =
        let delay =
            float this.InitialDelayMs * Math.Pow(this.BackoffFactor, float (attempt - 1))

        let delay = Math.Min(delay, float this.MaxDelayMs)

        if this.Jitter then
            let rng = Random()
            int (delay * (0.5 + rng.NextDouble()))
        else
            int delay

/// Retry policy for node execution
type RetryPolicy =
    { MaxAttempts: int
      Backoff: BackoffConfig }

    static member None =
        { MaxAttempts = 1
          Backoff = BackoffConfig.None }

    static member Standard =
        { MaxAttempts = 5
          Backoff = BackoffConfig.Default }

    static member FromNode(node: Node, graph: Graph) =
        let maxRetries =
            match node.MaxRetriesOption with
            | Some n -> n
            | None -> graph.DefaultMaxRetry

        { MaxAttempts = maxRetries + 1
          Backoff = BackoffConfig.Default }

/// Pipeline run configuration
type RunConfig =
    { LogsRoot: string
      Registry: HandlerRegistry
      EventEmitter: EventEmitter
      ExtraTransforms: ITransform list
      InitialContextValues: Map<string, string> }

    static member Default(logsRoot: string) =
        { LogsRoot = logsRoot
          Registry = HandlerRegistry.CreateDefault()
          EventEmitter = EventEmitter()
          ExtraTransforms = []
          InitialContextValues = Map.empty }

/// Pipeline run result
type RunResult =
    { FinalOutcome: Outcome
      CompletedNodes: string list
      NodeOutcomes: Map<string, Outcome>
      Context: Context }

/// Edge selection algorithm
module EdgeSelection =

    let private bestByWeightThenLexical (edges: Edge list) =
        edges
        |> List.sortWith (fun a b ->
            let wCmp = compare b.Weight a.Weight // descending weight
            if wCmp <> 0 then wCmp else compare a.ToNode b.ToNode) // ascending lexical
        |> List.tryHead

    /// Select the next edge from a node based on the 5-step priority algorithm
    let selectEdge (node: Node) (outcome: Outcome) (context: Context) (graph: Graph) : Edge option =
        let edges = graph.OutgoingEdges(node.Id)

        if edges.IsEmpty then
            None
        else
            let unconditional = edges |> List.filter (fun e -> e.Condition = "")
            // Step 1: Condition matching
            let conditionMatched =
                edges
                |> List.filter (fun e -> e.Condition <> "" && Conditions.evaluate e.Condition outcome context)

            if not conditionMatched.IsEmpty then
                bestByWeightThenLexical conditionMatched
            else
                // Step 2: Preferred label match
                let labelMatch =
                    if outcome.PreferredLabel <> "" then
                        let normalizedPref = AcceleratorKey.normalizeLabel outcome.PreferredLabel

                        unconditional
                        |> List.tryFind (fun e -> AcceleratorKey.normalizeLabel e.Label = normalizedPref)
                    else
                        None

                match labelMatch with
                | Some e -> Some e
                | None ->
                    // Step 3: Suggested next IDs
                    let suggestedMatch =
                        if not outcome.SuggestedNextIds.IsEmpty then
                            outcome.SuggestedNextIds
                            |> List.tryPick (fun suggestedId ->
                                unconditional |> List.tryFind (fun e -> e.ToNode = suggestedId))
                        else
                            None

                    match suggestedMatch with
                    | Some e -> Some e
                    | None ->
                        // Step 4 & 5: Block unconditional edges if pipeline was cancelled
                        if UnifiedLlm.HttpCancellation.isCancelled () then
                            None
                        else if not unconditional.IsEmpty then
                            bestByWeightThenLexical unconditional
                        else
                            None

    /// Return ALL matching outgoing edges (for multi-edge fan-out).
    /// Falls back to single-edge selection when only one edge qualifies.
    let selectAllMatchingEdges (node: Node) (outcome: Outcome) (context: Context) (graph: Graph) : Edge list =
        let edges = graph.OutgoingEdges(node.Id)

        if edges.IsEmpty then
            []
        else
            let conditionMatched =
                edges
                |> List.filter (fun e -> e.Condition <> "" && Conditions.evaluate e.Condition outcome context)

            if not conditionMatched.IsEmpty then
                conditionMatched
            else
                let unconditional = edges |> List.filter (fun e -> e.Condition = "")

                if unconditional.Length > 1 then
                    unconditional
                else
                    match selectEdge node outcome context graph with
                    | Some edge -> [ edge ]
                    | None -> []

/// Goal gate enforcement
module GoalGates =

    /// Check if all goal gates are satisfied
    let checkGoalGates (graph: Graph) (nodeOutcomes: Map<string, Outcome>) : bool * Node option =
        let failedGate =
            nodeOutcomes
            |> Map.tryPick (fun nodeId outcome ->
                match graph.Nodes |> Map.tryFind nodeId with
                | Some node when node.GoalGate ->
                    match outcome.Status with
                    | StageStatus.Success
                    | StageStatus.PartialSuccess -> None
                    | _ -> Some node
                | _ -> None)

        match failedGate with
        | None -> (true, None)
        | Some node -> (false, Some node)

    /// Get the retry target for a failed goal gate
    let getRetryTarget (node: Node) (graph: Graph) : string option =
        let targets =
            [ node.RetryTarget
              node.FallbackRetryTarget
              graph.RetryTarget
              graph.FallbackRetryTarget ]

        targets |> List.tryFind (fun t -> t <> "" && graph.Nodes |> Map.containsKey t)

/// Fidelity resolution: edge > node > graph > default(compact)
module FidelityResolution =

    /// Resolve fidelity mode for a node, considering incoming edge override
    let resolve (incomingEdge: Edge option) (node: Node) (graph: Graph) : FidelityMode =
        // 1. Edge-level override (highest priority)
        let edgeFidelity =
            incomingEdge
            |> Option.bind (fun e ->
                if e.Fidelity <> "" then
                    FidelityMode.Parse(e.Fidelity)
                else
                    None)

        match edgeFidelity with
        | Some f -> f
        | None ->
            // 2. Node-level
            if node.Fidelity <> "" then
                FidelityMode.Parse(node.Fidelity) |> Option.defaultValue FidelityMode.Compact
            else if
                // 3. Graph-level default
                graph.DefaultFidelity <> ""
            then
                FidelityMode.Parse(graph.DefaultFidelity)
                |> Option.defaultValue FidelityMode.Compact
            else
                // 4. Default
                FidelityMode.Compact

/// The pipeline execution engine
module Engine =

    let private tryParseInt64 (value: string option) =
        match value with
        | Some raw ->
            match Int64.TryParse(raw) with
            | true, parsed -> Some parsed
            | _ -> None
        | None -> None

    let private tryParseInt (value: string option) =
        match value with
        | Some raw ->
            match Int32.TryParse(raw) with
            | true, parsed -> Some parsed
            | _ -> None
        | None -> None

    let private writeCostSummary (logsRoot: string) (perNode: Map<string, int64 * int * int>) =
        let totalMicros =
            perNode |> Seq.sumBy (fun pair -> let micros, _, _ = pair.Value in micros)

        let payload =
            {| totalCostMicrodollars = totalMicros
               totalCostUsd = decimal totalMicros / 1_000_000m
               callCount = perNode.Count
               perNode =
                perNode
                |> Seq.map (fun pair ->
                    let micros, inputTokens, outputTokens = pair.Value

                    {| nodeId = pair.Key
                       costMicrodollars = micros
                       inputTokens = inputTokens
                       outputTokens = outputTokens |})
                |> Seq.toArray |}

        let json =
            JsonSerializer.Serialize(payload, JsonSerializerOptions(WriteIndented = true))

        File.WriteAllText(Path.Combine(logsRoot, "cost-summary.json"), json)

    /// Mirror graph attributes into the context
    let mirrorGraphAttributes (graph: Graph) (context: Context) =
        context.Set("graph.goal", graph.Goal)

        for kv in graph.GraphAttributes do
            context.Set($"graph.{kv.Key}", kv.Value.AsString())

    let applyInitialContext (config: RunConfig) (context: Context) (fillMissingOnly: bool) =
        for kv in config.InitialContextValues do
            if not fillMissingOnly || context.Get(kv.Key, "") = "" then
                context.Set(kv.Key, kv.Value)

    let private tryResolveFanInEdge (edges: Edge list) (graph: Graph) : Edge option =
        edges
        |> List.tryPick (fun edge -> graph.OutgoingEdges(edge.ToNode) |> List.tryHead)

    /// Run all fan-out branches sequentially and return the chosen fan-in edge.
    let private runFanOut
        (edges: Edge list)
        (graph: Graph)
        (initialOutcome: Outcome)
        (isCompleted: string -> bool)
        (shouldContinue: unit -> bool)
        (executeBranch: Edge -> Outcome)
        : Edge option * Outcome =
        let mutable lastBranchOutcome = initialOutcome

        for edge in edges do
            if shouldContinue () && not (isCompleted edge.ToNode) then
                lastBranchOutcome <- executeBranch edge

        let fanInEdge =
            if shouldContinue () then
                tryResolveFanInEdge edges graph
            else
                None

        fanInEdge, lastBranchOutcome

    let private interpolationPattern =
        Regex(@"\$\{([a-zA-Z0-9_.]+)\}", RegexOptions.Compiled)

    let private escapedInterpolationPattern =
        Regex(@"\$\$\{([a-zA-Z0-9_.]+)\}", RegexOptions.Compiled)

    let interpolateAttrValue (context: Context) (rawValue: string) : string =
        if String.IsNullOrEmpty(rawValue) then
            rawValue
        else
            let escapedSentinels = System.Collections.Generic.Dictionary<string, string>()
            let mutable escapeIndex = 0

            let masked =
                escapedInterpolationPattern.Replace(
                    rawValue,
                    fun m ->
                        let token = $"__attr_interp_escape_{escapeIndex}__"
                        escapeIndex <- escapeIndex + 1
                        escapedSentinels[token] <- "${" + m.Groups.[1].Value + "}"
                        token
                )

            let interpolated =
                interpolationPattern.Replace(
                    masked,
                    fun m ->
                        let key = m.Groups.[1].Value

                        let lookupKey =
                            if key.StartsWith("internal.", StringComparison.Ordinal) then
                                key
                            elif key.StartsWith("context.", StringComparison.Ordinal) then
                                key
                            else
                                "context." + key

                        context.Get(lookupKey, m.Value)
                )

            escapedSentinels
            |> Seq.fold (fun (state: string) pair -> state.Replace(pair.Key, pair.Value)) interpolated

    let private withInterpolatedAttr (context: Context) (key: string) (attrs: Map<string, AttrValue>) =
        match attrs |> Map.tryFind key with
        | Some value ->
            let raw = value.AsString()
            let resolved = interpolateAttrValue context raw

            if raw = resolved then
                attrs
            else
                attrs |> Map.add key (AttrValue.String resolved)
        | None -> attrs

    let private generateFreshThreadId (nodeId: string) =
        let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        let pid = System.Diagnostics.Process.GetCurrentProcess().Id
        // Include a short random suffix so two visits inside the same millisecond
        // (tight retry loops, loop_restart back-edges) still produce distinct ids.
        let rand = Guid.NewGuid().ToString("N").Substring(0, 8)
        $"{nodeId}-{now}-{pid}-{rand}"

    let private prepareNodeForExecution (context: Context) (fidelity: FidelityMode) (node: Node) =
        let resolvedThreadId =
            if node.FreshSession then
                generateFreshThreadId node.Id
            elif node.ThreadId <> "" then
                interpolateAttrValue context node.ThreadId
            else
                ""

        if fidelity = FidelityMode.Full && resolvedThreadId <> "" then
            context.Set("thread_id", resolvedThreadId)

        let attrs =
            node.Attributes
            |> withInterpolatedAttr context "prompt"
            |> withInterpolatedAttr context "cwd"
            |> withInterpolatedAttr context "tool_command"
            |> (fun current ->
                if resolvedThreadId <> "" then
                    current |> Map.add "thread_id" (AttrValue.String resolvedThreadId)
                else
                    current)

        { node with Attributes = attrs }

    let private runStructuralCommand
        (node: Node)
        (context: Context)
        (graph: Graph)
        (logsRoot: string)
        (suffix: string)
        (command: string)
        =
        let attrs =
            let baseAttrs =
                [ "shape", AttrValue.String "parallelogram"
                  "type", AttrValue.String "tool"
                  "tool_command", AttrValue.String command ]
                |> Map.ofList

            let withCwd =
                match node.GetAttr("cwd") with
                | Some cwd -> baseAttrs |> Map.add "cwd" cwd
                | None -> baseAttrs

            match node.GetAttr("timeout") with
            | Some timeout -> withCwd |> Map.add "timeout" timeout
            | None -> withCwd

        let structuralNode =
            { Id = $"{node.Id}{suffix}"
              Attributes = attrs }

        let toolHandler = Handlers.ToolHandler() :> IHandler
        let outcome = toolHandler.Execute(structuralNode, context, graph, logsRoot)
        context.ApplyUpdates(outcome.ContextUpdates)

        match outcome.ContextUpdates |> Map.tryFind "tool_stdout" with
        | Some stdout -> context.Set("tool_output", stdout)
        | None -> ()

        match outcome.ContextUpdates |> Map.tryFind "tool_stderr" with
        | Some stderr -> context.Set("tool_stderr", stderr)
        | None -> ()

        outcome

    let private executePrimaryWithRetry
        (handler: IHandler)
        (node: Node)
        (context: Context)
        (graph: Graph)
        (logsRoot: string)
        (retryPolicy: RetryPolicy)
        (emitter: EventEmitter)
        (nodeIndex: int)
        : Outcome =
        let mutable attempt = 1
        let mutable finalOutcome = Outcome.Fail("max retries exceeded")
        let mutable cont = true

        while cont && attempt <= retryPolicy.MaxAttempts do
            try
                let outcome = handler.Execute(node, context, graph, logsRoot)

                match outcome.Status with
                | StageStatus.Success
                | StageStatus.PartialSuccess ->
                    finalOutcome <- outcome
                    cont <- false

                | StageStatus.Fail ->
                    // Fail is deterministic — stop immediately, let edge conditions route
                    finalOutcome <- outcome
                    cont <- false

                | StageStatus.Retry ->
                    if attempt < retryPolicy.MaxAttempts then
                        let delay = retryPolicy.Backoff.DelayForAttempt(attempt)
                        emitter.Emit(PipelineEvent.StageRetrying(node.Id, nodeIndex, attempt, delay))

                        if delay > 0 then
                            System.Threading.Thread.Sleep(delay)

                        attempt <- attempt + 1
                    else
                        if node.AllowPartial then
                            finalOutcome <- Outcome.PartialSuccess(notes = "retries exhausted, partial accepted")
                        else
                            finalOutcome <- Outcome.Fail("max retries exceeded")

                        cont <- false

                | StageStatus.Skipped ->
                    finalOutcome <- outcome
                    cont <- false

            with ex ->
                let isRetriable =
                    match ex with
                    | :? UnifiedLlm.ProviderError as pe -> pe.Retryable
                    | _ -> true

                if isRetriable && attempt < retryPolicy.MaxAttempts then
                    emitter.Emit(
                        PipelineEvent.StageRetrying(
                            node.Id,
                            nodeIndex,
                            attempt,
                            retryPolicy.Backoff.DelayForAttempt(attempt)
                        )
                    )

                    let delay = retryPolicy.Backoff.DelayForAttempt(attempt)

                    if delay > 0 then
                        System.Threading.Thread.Sleep(delay)

                    attempt <- attempt + 1
                else
                    finalOutcome <- Outcome.Fail(ex.Message)
                    cont <- false

        finalOutcome

    /// Execute a node with retry policy
    let executeWithRetry
        (handler: IHandler)
        (node: Node)
        (context: Context)
        (graph: Graph)
        (logsRoot: string)
        (retryPolicy: RetryPolicy)
        (emitter: EventEmitter)
        (nodeIndex: int)
        : Outcome =
        let requiresGreenBuild = node.RequiresGreenBuild.Trim()

        if requiresGreenBuild <> "" then
            let command = interpolateAttrValue context requiresGreenBuild

            let gateOutcome =
                runStructuralCommand node context graph logsRoot ".__requires_green_build" command

            if gateOutcome.Status <> StageStatus.Success then
                let reason =
                    match gateOutcome.ContextUpdates |> Map.tryFind "tool_exit_code" with
                    | Some exitCode when exitCode <> "" -> $"pre-condition failed: {command} exited {exitCode}"
                    | _ -> $"pre-condition failed: {gateOutcome.FailureReason}"

                { Outcome.Fail(reason) with
                    ContextUpdates = gateOutcome.ContextUpdates }
            else
                let primary =
                    executePrimaryWithRetry handler node context graph logsRoot retryPolicy emitter nodeIndex

                let scopeGate = node.ScopeGate.Trim()

                if scopeGate = "" || primary.Status <> StageStatus.Success then
                    primary
                else
                    let gateCommand = interpolateAttrValue context scopeGate
                    let scopeRevert = node.ScopeRevert.Trim()
                    let maxScopeRetries = max 0 node.ScopeGateMaxRetries
                    let mutable attempts = 1
                    let mutable retriesRemaining = maxScopeRetries
                    let mutable finalOutcome = primary
                    let mutable doneLoop = false

                    while not doneLoop do
                        if finalOutcome.Status <> StageStatus.Success then
                            doneLoop <- true
                        else
                            let gateOutcome =
                                runStructuralCommand node context graph logsRoot ".__scope_gate" gateCommand

                            if gateOutcome.Status = StageStatus.Success then
                                doneLoop <- true
                            else
                                if scopeRevert <> "" then
                                    let revertCommand = interpolateAttrValue context scopeRevert

                                    runStructuralCommand node context graph logsRoot ".__scope_revert" revertCommand
                                    |> ignore

                                if retriesRemaining > 0 then
                                    retriesRemaining <- retriesRemaining - 1
                                    attempts <- attempts + 1

                                    finalOutcome <-
                                        executePrimaryWithRetry
                                            handler
                                            node
                                            context
                                            graph
                                            logsRoot
                                            retryPolicy
                                            emitter
                                            nodeIndex
                                else
                                    finalOutcome <-
                                        { Outcome.Fail($"scope_gate rejected changes after {attempts} attempts") with
                                            ContextUpdates = gateOutcome.ContextUpdates }

                                    doneLoop <- true

                    finalOutcome
        else
            let primary =
                executePrimaryWithRetry handler node context graph logsRoot retryPolicy emitter nodeIndex

            let scopeGate = node.ScopeGate.Trim()

            if scopeGate = "" || primary.Status <> StageStatus.Success then
                primary
            else
                let gateCommand = interpolateAttrValue context scopeGate
                let scopeRevert = node.ScopeRevert.Trim()
                let maxScopeRetries = max 0 node.ScopeGateMaxRetries
                let mutable attempts = 1
                let mutable retriesRemaining = maxScopeRetries
                let mutable finalOutcome = primary
                let mutable doneLoop = false

                while not doneLoop do
                    if finalOutcome.Status <> StageStatus.Success then
                        doneLoop <- true
                    else
                        let gateOutcome =
                            runStructuralCommand node context graph logsRoot ".__scope_gate" gateCommand

                        if gateOutcome.Status = StageStatus.Success then
                            doneLoop <- true
                        else
                            if scopeRevert <> "" then
                                let revertCommand = interpolateAttrValue context scopeRevert

                                runStructuralCommand node context graph logsRoot ".__scope_revert" revertCommand
                                |> ignore

                            if retriesRemaining > 0 then
                                retriesRemaining <- retriesRemaining - 1
                                attempts <- attempts + 1

                                finalOutcome <-
                                    executePrimaryWithRetry
                                        handler
                                        node
                                        context
                                        graph
                                        logsRoot
                                        retryPolicy
                                        emitter
                                        nodeIndex
                            else
                                finalOutcome <-
                                    { Outcome.Fail($"scope_gate rejected changes after {attempts} attempts") with
                                        ContextUpdates = gateOutcome.ContextUpdates }

                                doneLoop <- true

                finalOutcome

    /// Save checkpoint to disk
    let saveCheckpoint (logsRoot: string) (checkpoint: Checkpoint) =
        if not (Directory.Exists(logsRoot)) then
            Directory.CreateDirectory(logsRoot) |> ignore

        let serializedOutcomes =
            checkpoint.NodeOutcomes
            |> Map.map (fun _ outcome ->
                {| status = outcome.Status.ToString()
                   raw_outcome =
                    match outcome.RawOutcome with
                    | Some value -> value
                    | None -> null
                   preferred_label = outcome.PreferredLabel
                   suggested_next_ids = outcome.SuggestedNextIds
                   context_updates = outcome.ContextUpdates
                   notes = outcome.Notes
                   failure_reason = outcome.FailureReason |})

        let data =
            {| timestamp = checkpoint.Timestamp.ToString("o")
               current_node = checkpoint.CurrentNode
               completed_nodes = checkpoint.CompletedNodes
               node_retries = checkpoint.NodeRetries
               node_outcomes = serializedOutcomes
               context = checkpoint.ContextValues
               logs = checkpoint.Logs |}

        let json =
            JsonSerializer.Serialize(data, JsonSerializerOptions(WriteIndented = true))

        File.WriteAllText(Path.Combine(logsRoot, "checkpoint.json"), json)

    let private tryGetJsonString (root: JsonElement) (name: string) : string option =
        if root.TryGetProperty(name) |> fst then
            let prop = root.GetProperty(name)

            match prop.ValueKind with
            | JsonValueKind.Null
            | JsonValueKind.Undefined -> None
            | JsonValueKind.String -> Some(prop.GetString())
            | _ -> Some(prop.ToString())
        else
            None

    let private tryGetJsonStringList (root: JsonElement) (name: string) : string list option =
        if root.TryGetProperty(name) |> fst then
            let prop = root.GetProperty(name)

            if prop.ValueKind = JsonValueKind.Array then
                prop.EnumerateArray()
                |> Seq.map (fun e ->
                    match e.ValueKind with
                    | JsonValueKind.String -> e.GetString()
                    | _ -> e.ToString())
                |> Seq.toList
                |> Some
            else
                None
        else
            None

    let private tryGetJsonStringMap (root: JsonElement) (name: string) : Map<string, string> option =
        if root.TryGetProperty(name) |> fst then
            let prop = root.GetProperty(name)

            if prop.ValueKind = JsonValueKind.Object then
                prop.EnumerateObject()
                |> Seq.map (fun p ->
                    let value =
                        match p.Value.ValueKind with
                        | JsonValueKind.String -> p.Value.GetString()
                        | JsonValueKind.Null
                        | JsonValueKind.Undefined -> ""
                        | _ -> p.Value.ToString()

                    p.Name, value)
                |> Map.ofSeq
                |> Some
            else
                None
        else
            None

    /// Load outcome from a stage status.json file when present.
    /// Supports both "outcome" and "status" keys for compatibility.
    let private tryLoadStatusOutcome (statusPath: string) (fallback: Outcome) : Outcome option =
        if not (File.Exists(statusPath)) then
            None
        else
            try
                use doc = JsonDocument.Parse(File.ReadAllText(statusPath))
                let root = doc.RootElement

                let statusRaw =
                    tryGetJsonString root "outcome"
                    |> Option.orElseWith (fun () -> tryGetJsonString root "status")

                let status =
                    statusRaw
                    |> Option.bind StageStatus.Parse
                    |> Option.defaultValue fallback.Status

                let preferredLabel =
                    tryGetJsonString root "preferred_next_label"
                    |> Option.orElseWith (fun () -> tryGetJsonString root "preferred_label")
                    |> Option.defaultValue fallback.PreferredLabel

                let suggestedNextIds =
                    tryGetJsonStringList root "suggested_next_ids"
                    |> Option.defaultValue fallback.SuggestedNextIds

                let contextUpdates =
                    tryGetJsonStringMap root "context_updates"
                    |> Option.defaultValue fallback.ContextUpdates

                let notes = tryGetJsonString root "notes" |> Option.defaultValue fallback.Notes

                let failureReason =
                    tryGetJsonString root "failure_reason"
                    |> Option.defaultValue fallback.FailureReason

                Some
                    { Status = status
                      RawOutcome = statusRaw
                      PreferredLabel = preferredLabel
                      SuggestedNextIds = suggestedNextIds
                      ContextUpdates = contextUpdates
                      Notes = notes
                      FailureReason = failureReason }
            with _ ->
                None

    /// Run a pipeline from a parsed graph
    let run (graph: Graph) (config: RunConfig) : RunResult =
        let createContext (logsRoot: string) =
            let ctx = Context()
            ctx.ConfigureArtifactStore(FileArtifactStore(logsRoot) :> IArtifactStore)
            ctx

        let mutable context = createContext config.LogsRoot
        mirrorGraphAttributes graph context
        applyInitialContext config context false

        let emitter = config.EventEmitter
        let logsRoot = config.LogsRoot
        let registry = config.Registry

        if not (Directory.Exists(logsRoot)) then
            Directory.CreateDirectory(logsRoot) |> ignore

        // Write manifest
        let manifest =
            {| name = graph.Name
               goal = graph.Goal
               start_time = DateTimeOffset.UtcNow.ToString("o") |}

        let manifestJson =
            JsonSerializer.Serialize(manifest, JsonSerializerOptions(WriteIndented = true))

        File.WriteAllText(Path.Combine(logsRoot, "manifest.json"), manifestJson)

        emitter.Emit(PipelineEvent.PipelineStarted(graph.Name, Guid.NewGuid().ToString("N")))
        let startTime = DateTimeOffset.UtcNow

        let completedNodes = ResizeArray<string>()
        let nodeOutcomes = System.Collections.Generic.Dictionary<string, Outcome>()
        let nodeRetries = System.Collections.Generic.Dictionary<string, int>()
        let nodeVisitCounts = System.Collections.Generic.Dictionary<string, int>()
        let nodeCosts = System.Collections.Generic.Dictionary<string, int64 * int * int>()

        // Find start node
        let startNode =
            match graph.FindStartNode() with
            | Some n -> n
            | None -> failwith "No start node found in graph"

        let mutable currentNode = startNode
        let mutable lastOutcome = Outcome.Success()
        let mutable running = true
        let mutable nodeIndex = 0
        let mutable currentLogsRoot = logsRoot
        let mutable restartCount = 0
        let maxLoopRestarts = 10
        let mutable lastEdge: Edge option = None
        let goalGateRetryVisited = System.Collections.Generic.HashSet<string>()
        context.Set("internal.loop_restart_count", (restartCount: int).ToString())

        let executeMainFanOutBranch (parentNode: Node) (branchTarget: Edge) : Outcome =
            match graph.Nodes |> Map.tryFind branchTarget.ToNode with
            | None ->
                let fail = Outcome.Fail($"Edge target '{branchTarget.ToNode}' not found")
                running <- false
                fail
            | Some branchNode ->
                let branchFidelity = FidelityResolution.resolve (Some branchTarget) branchNode graph

                let branchNodeWithFidelity =
                    { branchNode with
                        Attributes =
                            branchNode.Attributes
                            |> Map.add
                                "__resolved_fidelity"
                                (AttrValue.String((branchFidelity: FidelityMode).ToString())) }

                let branchContext =
                    if branchFidelity = FidelityMode.Full then
                        context
                    else
                        context.Project(branchFidelity)

                emitter.Emit(PipelineEvent.StageStarted(branchNode.Id, nodeIndex))
                let branchHandlerType = ShapeMapping.resolveHandlerType branchNode
                let branchStageStart = DateTimeOffset.UtcNow
                context.Set("current_node", branchNode.Id)

                let branchVisitCount =
                    let current =
                        match nodeVisitCounts.TryGetValue(branchNode.Id) with
                        | true, count -> count
                        | false, _ -> 0

                    let updated = current + 1
                    nodeVisitCounts[branchNode.Id] <- updated
                    updated

                context.Set("node.visit_count", ((branchVisitCount: int).ToString()))
                context.Set($"node.{branchNode.Id}.visit_count", ((branchVisitCount: int).ToString()))

                let branchNodeForHandler =
                    prepareNodeForExecution context branchFidelity branchNodeWithFidelity

                let branchHandler = registry.Resolve(branchNodeForHandler)
                let branchRetryPolicy = RetryPolicy.FromNode(branchNodeForHandler, graph)

                let rawBranchOutcome =
                    if branchVisitCount > branchNode.MaxVisits then
                        Outcome.Fail($"Node '{branchNode.Id}' exceeded max_visits ({branchNode.MaxVisits})")
                    else
                        executeWithRetry
                            branchHandler
                            branchNodeForHandler
                            branchContext
                            graph
                            currentLogsRoot
                            branchRetryPolicy
                            emitter
                            nodeIndex

                let branchStatusPath = Path.Combine(currentLogsRoot, branchNode.Id, "status.json")

                let branchOutcome =
                    match tryLoadStatusOutcome branchStatusPath rawBranchOutcome with
                    | Some parsed -> parsed
                    | None ->
                        let shouldAutoStatus =
                            branchNode.AutoStatus
                            && (branchHandlerType = "tool" || branchHandlerType = "codergen")

                        if shouldAutoStatus && not (File.Exists(branchStatusPath)) then
                            { rawBranchOutcome with
                                Status = StageStatus.Success
                                FailureReason = ""
                                Notes =
                                    if rawBranchOutcome.Notes <> "" then
                                        rawBranchOutcome.Notes
                                    else
                                        "auto_status synthesized success (status.json not found)" }
                        else
                            rawBranchOutcome

                let branchStageDuration = DateTimeOffset.UtcNow - branchStageStart

                match branchOutcome.Status with
                | StageStatus.Fail ->
                    let isGateCheck =
                        branchHandlerType = "tool"
                        && graph.OutgoingEdges(branchNode.Id) |> List.exists (fun e -> e.Condition <> "")

                    if isGateCheck then
                        emitter.Emit(PipelineEvent.StageCompleted(branchNode.Id, nodeIndex, branchStageDuration))
                    else
                        emitter.Emit(
                            PipelineEvent.StageFailed(branchNode.Id, nodeIndex, branchOutcome.FailureReason, false)
                        )
                | _ -> emitter.Emit(PipelineEvent.StageCompleted(branchNode.Id, nodeIndex, branchStageDuration))

                completedNodes.Add(branchNode.Id)
                nodeOutcomes[branchNode.Id] <- branchOutcome
                context.ApplyUpdates(branchOutcome.ContextUpdates)
                context.Set("outcome", branchOutcome.OutcomeString)

                if branchOutcome.PreferredLabel <> "" then
                    context.Set("preferred_label", branchOutcome.PreferredLabel)

                match context.TryGet("llm.last_node"), tryParseInt64 (context.TryGet("llm.cost_microdollars")) with
                | Some lastNode, Some costMicros when lastNode = branchNode.Id ->
                    let inputTokens =
                        tryParseInt (context.TryGet("llm.input_tokens")) |> Option.defaultValue 0

                    let outputTokens =
                        tryParseInt (context.TryGet("llm.output_tokens")) |> Option.defaultValue 0

                    nodeCosts[branchNode.Id] <- costMicros, inputTokens, outputTokens

                    writeCostSummary
                        currentLogsRoot
                        (nodeCosts |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq)
                | _ ->
                    writeCostSummary
                        currentLogsRoot
                        (nodeCosts |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq)

                let branchCheckpoint =
                    Checkpoint.Create(
                        context,
                        parentNode.Id,
                        completedNodes |> Seq.toList,
                        nodeRetries |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq,
                        nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                    )

                saveCheckpoint currentLogsRoot branchCheckpoint
                emitter.Emit(PipelineEvent.CheckpointSaved(branchNode.Id))
                branchOutcome

        while running do
            // Check for user cancellation (Ctrl-C)
            if UnifiedLlm.HttpCancellation.isCancelled () then
                lastOutcome <- Outcome.Fail("Pipeline cancelled by user")
                running <- false
            else

                let node = currentNode

                // Step 0: Skip nodes already executed by parallel fan-out
                let parallelExecuted =
                    match context.TryGet("parallel.executed_nodes") with
                    | Some nodes when nodes <> "" -> nodes.Split(',') |> Set.ofArray
                    | _ -> Set.empty

                if parallelExecuted.Contains(node.Id) then
                    // This node was already executed by the parallel handler — skip to its outgoing edge
                    completedNodes.Add(node.Id)
                    let skipEdge = graph.OutgoingEdges(node.Id) |> List.tryHead

                    match skipEdge with
                    | Some edge ->
                        lastEdge <- Some edge

                        match graph.Nodes |> Map.tryFind edge.ToNode with
                        | Some nextNode -> currentNode <- nextNode
                        | None ->
                            lastOutcome <- Outcome.Fail($"Edge target '{edge.ToNode}' not found")
                            running <- false
                    | None -> running <- false

                    nodeIndex <- nodeIndex + 1
                else if

                    // Step 1: Check for terminal node
                    ShapeMapping.isTerminal node
                then
                    let (gateOk, failedGate) =
                        GoalGates.checkGoalGates
                            graph
                            (nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)

                    if not gateOk then
                        match failedGate with
                        | Some failedNode ->
                            match GoalGates.getRetryTarget failedNode graph with
                            | Some target ->
                                // Cycle guard: detect if we've already retried to this target
                                let cycleKey = $"{failedNode.Id}->{target}"

                                if goalGateRetryVisited.Add(cycleKey) then
                                    currentNode <- graph.Nodes[target]
                                    () // continue the loop
                                else
                                    lastOutcome <- Outcome.Fail($"Goal gate retry cycle detected: {cycleKey}")
                                    running <- false
                            | None ->
                                lastOutcome <- Outcome.Fail("Goal gate unsatisfied and no retry target")
                                running <- false
                        | None -> running <- false
                    else
                        running <- false
                else
                    // Resolve fidelity for this node
                    let fidelity = FidelityResolution.resolve lastEdge node graph

                    let nodeWithFidelity =
                        { node with
                            Attributes =
                                node.Attributes
                                |> Map.add "__resolved_fidelity" (AttrValue.String((fidelity: FidelityMode).ToString())) }

                    let handlerContext =
                        if fidelity = FidelityMode.Full then
                            context
                        else
                            context.Project(fidelity)

                    emitter.Emit(PipelineEvent.StageStarted(node.Id, nodeIndex))
                    let handlerType = ShapeMapping.resolveHandlerType node

                    if handlerType = "parallel" then
                        let branches = graph.OutgoingEdges(node.Id)
                        emitter.Emit(PipelineEvent.ParallelStarted(branches.Length))

                        branches
                        |> List.iteri (fun idx branch ->
                            emitter.Emit(PipelineEvent.ParallelBranchStarted(branch.ToNode, idx)))
                    elif handlerType = "wait.human" then
                        let question = if node.Label <> "" then node.Label else node.Id
                        emitter.Emit(PipelineEvent.InterviewStarted(question, node.Id))

                    let stageStart = DateTimeOffset.UtcNow
                    context.Set("current_node", node.Id)

                    let visitCount =
                        let current =
                            match nodeVisitCounts.TryGetValue(node.Id) with
                            | true, count -> count
                            | false, _ -> 0

                        let updated = current + 1
                        nodeVisitCounts[node.Id] <- updated
                        updated

                    context.Set("node.visit_count", ((visitCount: int).ToString()))
                    context.Set($"node.{node.Id}.visit_count", ((visitCount: int).ToString()))

                    // Apply runtime interpolation and session attributes just before handler handoff.
                    let nodeForHandler = prepareNodeForExecution context fidelity nodeWithFidelity

                    // Step 2: Execute node handler with retry policy
                    let handler = registry.Resolve(nodeForHandler)
                    let retryPolicy = RetryPolicy.FromNode(nodeForHandler, graph)

                    let outcome =
                        if visitCount > node.MaxVisits then
                            Outcome.Fail($"Node '{node.Id}' exceeded max_visits ({node.MaxVisits})")
                        else
                            let raw =
                                executeWithRetry
                                    handler
                                    nodeForHandler
                                    handlerContext
                                    graph
                                    currentLogsRoot
                                    retryPolicy
                                    emitter
                                    nodeIndex

                            let statusPath = Path.Combine(currentLogsRoot, node.Id, "status.json")

                            match tryLoadStatusOutcome statusPath raw with
                            | Some parsed -> parsed
                            | None ->
                                let shouldAutoStatus =
                                    node.AutoStatus && (handlerType = "tool" || handlerType = "codergen")

                                if shouldAutoStatus && not (File.Exists(statusPath)) then
                                    { raw with
                                        Status = StageStatus.Success
                                        FailureReason = ""
                                        Notes =
                                            if raw.Notes <> "" then
                                                raw.Notes
                                            else
                                                "auto_status synthesized success (status.json not found)" }
                                else
                                    raw

                    let stageDuration = DateTimeOffset.UtcNow - stageStart

                    match outcome.Status with
                    | StageStatus.Fail ->
                        // Tool/parallelogram nodes with conditional outgoing edges are gate checks —
                        // a non-zero exit code is an expected routing outcome, not an error
                        let isGateCheck =
                            handlerType = "tool"
                            && graph.OutgoingEdges(node.Id) |> List.exists (fun e -> e.Condition <> "")

                        if isGateCheck then
                            emitter.Emit(PipelineEvent.StageCompleted(node.Id, nodeIndex, stageDuration))
                        else
                            emitter.Emit(PipelineEvent.StageFailed(node.Id, nodeIndex, outcome.FailureReason, false))
                    | _ -> emitter.Emit(PipelineEvent.StageCompleted(node.Id, nodeIndex, stageDuration))

                    if handlerType = "parallel" then
                        let branchStatuses =
                            outcome.ContextUpdates
                            |> Map.toList
                            |> List.choose (fun (k, v) ->
                                if
                                    k.StartsWith("parallel.branch.", StringComparison.Ordinal)
                                    && k.EndsWith(".status", StringComparison.Ordinal)
                                then
                                    let branchId = k.Replace("parallel.branch.", "").Replace(".status", "")
                                    Some(branchId, (v = "success"))
                                else
                                    None)

                        branchStatuses
                        |> List.iteri (fun idx (branchId, success) ->
                            emitter.Emit(PipelineEvent.ParallelBranchCompleted(branchId, idx, stageDuration, success)))

                        let successCount =
                            outcome.ContextUpdates
                            |> Map.tryFind "parallel.success_count"
                            |> Option.bind (fun raw ->
                                match Int32.TryParse(raw) with
                                | true, value -> Some value
                                | _ -> None)
                            |> Option.defaultValue 0

                        let failCount =
                            outcome.ContextUpdates
                            |> Map.tryFind "parallel.fail_count"
                            |> Option.bind (fun raw ->
                                match Int32.TryParse(raw) with
                                | true, value -> Some value
                                | _ -> None)
                            |> Option.defaultValue 0

                        emitter.Emit(PipelineEvent.ParallelCompleted(stageDuration, successCount, failCount))
                    elif handlerType = "wait.human" then
                        let question = if node.Label <> "" then node.Label else node.Id

                        if
                            outcome.Status = StageStatus.Retry
                            && outcome.FailureReason.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                        then
                            emitter.Emit(PipelineEvent.InterviewTimeout(question, node.Id, stageDuration))
                        else
                            let answer =
                                outcome.ContextUpdates
                                |> Map.tryFind "human.gate.label"
                                |> Option.orElseWith (fun () ->
                                    outcome.ContextUpdates |> Map.tryFind "human.gate.selected")
                                |> Option.defaultValue ""

                            emitter.Emit(PipelineEvent.InterviewCompleted(question, answer, stageDuration))

                    // Step 3: Record completion
                    completedNodes.Add(node.Id)
                    nodeOutcomes[node.Id] <- outcome

                    // Step 4: Apply context updates to canonical context
                    context.ApplyUpdates(outcome.ContextUpdates)
                    context.Set("outcome", outcome.OutcomeString)

                    if outcome.PreferredLabel <> "" then
                        context.Set("preferred_label", outcome.PreferredLabel)

                    match context.TryGet("llm.last_node"), tryParseInt64 (context.TryGet("llm.cost_microdollars")) with
                    | Some lastNode, Some costMicros when lastNode = node.Id ->
                        let inputTokens =
                            tryParseInt (context.TryGet("llm.input_tokens")) |> Option.defaultValue 0

                        let outputTokens =
                            tryParseInt (context.TryGet("llm.output_tokens")) |> Option.defaultValue 0

                        nodeCosts[node.Id] <- costMicros, inputTokens, outputTokens

                        writeCostSummary
                            currentLogsRoot
                            (nodeCosts |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq)
                    | _ ->
                        writeCostSummary
                            currentLogsRoot
                            (nodeCosts |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq)

                    // Step 5: Save checkpoint
                    let checkpoint =
                        Checkpoint.Create(
                            context,
                            node.Id,
                            completedNodes |> Seq.toList,
                            nodeRetries |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq,
                            nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                        )

                    saveCheckpoint currentLogsRoot checkpoint
                    emitter.Emit(PipelineEvent.CheckpointSaved(node.Id))

                    lastOutcome <- outcome

                    // Step 6: Select next edges
                    let nextEdges =
                        if outcome.Status = StageStatus.Fail then
                            if
                                outcome.FailureReason.Contains(
                                    "exceeded max_visits",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            then
                                []
                            else
                                let failEdge =
                                    graph.OutgoingEdges(node.Id)
                                    |> List.filter (fun e -> e.Condition <> "")
                                    |> List.tryFind (fun e -> Conditions.evaluate e.Condition outcome context)

                                match failEdge with
                                | Some edge -> [ edge ]
                                | None ->
                                    let retryTarget =
                                        [ node.RetryTarget
                                          node.FallbackRetryTarget
                                          graph.RetryTarget
                                          graph.FallbackRetryTarget ]
                                        |> List.tryFind (fun target ->
                                            target <> "" && (graph.Nodes |> Map.containsKey target))

                                    match retryTarget with
                                    | Some target ->
                                        [ { FromNode = node.Id
                                            ToNode = target
                                            Attributes = Map.empty } ]
                                    | None -> []
                        else
                            EdgeSelection.selectAllMatchingEdges node outcome context graph

                    if nextEdges.Length > 1 then
                        let fanInEdge, fanOutOutcome =
                            runFanOut
                                nextEdges
                                graph
                                lastOutcome
                                completedNodes.Contains
                                (fun () -> running)
                                (executeMainFanOutBranch node)

                        lastOutcome <- fanOutOutcome

                        if running then
                            match fanInEdge with
                            | Some edge ->
                                lastEdge <- Some edge

                                match graph.Nodes |> Map.tryFind edge.ToNode with
                                | Some nextNode -> currentNode <- nextNode
                                | None ->
                                    lastOutcome <- Outcome.Fail($"Edge target '{edge.ToNode}' not found")
                                    running <- false
                            | None -> running <- false
                    elif nextEdges.IsEmpty then
                        if outcome.Status = StageStatus.Fail then
                            lastOutcome <- outcome

                        running <- false
                    else
                        let edge = nextEdges.Head
                        lastEdge <- Some edge

                        // Step 7: Handle loop_restart
                        if edge.LoopRestart then
                            restartCount <- restartCount + 1
                            context.Set("internal.loop_restart_count", ((restartCount: int).ToString()))

                            if restartCount > maxLoopRestarts then
                                lastOutcome <- Outcome.Fail($"Max loop restarts ({maxLoopRestarts}) exceeded")
                                running <- false
                            else
                                // Create new logs subdirectory
                                let newLogsRoot = Path.Combine(logsRoot, $"restart-{restartCount}")

                                if not (Directory.Exists(newLogsRoot)) then
                                    Directory.CreateDirectory(newLogsRoot) |> ignore
                                // Write restart manifest
                                let restartManifest =
                                    {| restart_count = restartCount
                                       previous_logs = currentLogsRoot
                                       target_node = edge.ToNode
                                       timestamp = DateTimeOffset.UtcNow.ToString("o") |}

                                let restartJson =
                                    JsonSerializer.Serialize(
                                        restartManifest,
                                        JsonSerializerOptions(WriteIndented = true)
                                    )

                                File.WriteAllText(Path.Combine(newLogsRoot, "restart-manifest.json"), restartJson)
                                currentLogsRoot <- newLogsRoot
                                // Reset context: keep only graph.* attributes
                                let graphAttrs =
                                    context.Snapshot()
                                    |> Map.filter (fun k _ -> k.StartsWith("graph.", StringComparison.Ordinal))

                                let freshContext = createContext newLogsRoot

                                for kv in graphAttrs do
                                    freshContext.Set(kv.Key, kv.Value)

                                applyInitialContext config freshContext false
                                freshContext.Set("internal.loop_restart_count", ((restartCount: int).ToString()))
                                context <- freshContext
                                // Clear tracking state
                                completedNodes.Clear()
                                nodeOutcomes.Clear()
                                nodeRetries.Clear()
                                nodeVisitCounts.Clear()
                                goalGateRetryVisited.Clear()
                                emitter.Emit(PipelineEvent.LoopRestarted(edge.ToNode, restartCount, newLogsRoot))

                        // Step 8: Advance to next node
                        match graph.Nodes |> Map.tryFind edge.ToNode with
                        | Some nextNode -> currentNode <- nextNode
                        | None ->
                            lastOutcome <- Outcome.Fail($"Edge target '{edge.ToNode}' not found")
                            running <- false

                    nodeIndex <- nodeIndex + 1

        let totalDuration = DateTimeOffset.UtcNow - startTime

        match lastOutcome.Status with
        | StageStatus.Success
        | StageStatus.PartialSuccess ->
            emitter.Emit(PipelineEvent.PipelineCompleted(totalDuration, completedNodes.Count))
        | _ -> emitter.Emit(PipelineEvent.PipelineFailed(lastOutcome.FailureReason, totalDuration))

        writeCostSummary logsRoot (nodeCosts |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq)

        { FinalOutcome = lastOutcome
          CompletedNodes = completedNodes |> Seq.toList
          NodeOutcomes = nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
          Context = context }

    /// Load a checkpoint from disk
    let loadCheckpoint (logsRoot: string) : Checkpoint option =
        let path = Path.Combine(logsRoot, "checkpoint.json")

        if File.Exists(path) then
            try
                let json = File.ReadAllText(path)
                let doc = JsonDocument.Parse(json)
                let root = doc.RootElement
                let currentNode = root.GetProperty("current_node").GetString()

                let completedNodes =
                    root.GetProperty("completed_nodes").EnumerateArray()
                    |> Seq.map (fun e -> e.GetString())
                    |> Seq.toList

                let nodeRetries =
                    root.GetProperty("node_retries").EnumerateObject()
                    |> Seq.map (fun p -> p.Name, p.Value.GetInt32())
                    |> Map.ofSeq

                let nodeOutcomes =
                    if root.TryGetProperty("node_outcomes") |> fst then
                        root.GetProperty("node_outcomes").EnumerateObject()
                        |> Seq.map (fun p ->
                            let outcomeJson = p.Value

                            let status =
                                if outcomeJson.TryGetProperty("status") |> fst then
                                    StageStatus.Parse(outcomeJson.GetProperty("status").GetString())
                                    |> Option.defaultValue StageStatus.Success
                                else
                                    StageStatus.Success

                            let rawOutcome =
                                if outcomeJson.TryGetProperty("raw_outcome") |> fst then
                                    let raw = outcomeJson.GetProperty("raw_outcome")

                                    match raw.ValueKind with
                                    | JsonValueKind.String -> Some(raw.GetString())
                                    | JsonValueKind.Null
                                    | JsonValueKind.Undefined -> None
                                    | _ -> Some(raw.ToString())
                                else
                                    None

                            let preferredLabel =
                                if outcomeJson.TryGetProperty("preferred_label") |> fst then
                                    outcomeJson.GetProperty("preferred_label").GetString()
                                else
                                    ""

                            let suggestedNextIds =
                                if outcomeJson.TryGetProperty("suggested_next_ids") |> fst then
                                    outcomeJson.GetProperty("suggested_next_ids").EnumerateArray()
                                    |> Seq.map (fun e -> e.GetString())
                                    |> Seq.toList
                                else
                                    []

                            let contextUpdates =
                                if outcomeJson.TryGetProperty("context_updates") |> fst then
                                    outcomeJson.GetProperty("context_updates").EnumerateObject()
                                    |> Seq.map (fun e -> e.Name, e.Value.GetString())
                                    |> Map.ofSeq
                                else
                                    Map.empty

                            let notes =
                                if outcomeJson.TryGetProperty("notes") |> fst then
                                    outcomeJson.GetProperty("notes").GetString()
                                else
                                    ""

                            let failureReason =
                                if outcomeJson.TryGetProperty("failure_reason") |> fst then
                                    outcomeJson.GetProperty("failure_reason").GetString()
                                else
                                    ""

                            p.Name,
                            { Status = status
                              RawOutcome = rawOutcome
                              PreferredLabel = preferredLabel
                              SuggestedNextIds = suggestedNextIds
                              ContextUpdates = contextUpdates
                              Notes = notes
                              FailureReason = failureReason })
                        |> Map.ofSeq
                    else
                        Map.empty

                let contextValues =
                    root.GetProperty("context").EnumerateObject()
                    |> Seq.map (fun p -> p.Name, p.Value.GetString())
                    |> Map.ofSeq

                let logs =
                    if root.TryGetProperty("logs") |> fst then
                        root.GetProperty("logs").EnumerateArray()
                        |> Seq.map (fun e -> e.GetString())
                        |> Seq.toList
                    else
                        []

                let timestamp =
                    if root.TryGetProperty("timestamp") |> fst then
                        DateTimeOffset.Parse(root.GetProperty("timestamp").GetString())
                    else
                        DateTimeOffset.UtcNow

                Some
                    { Timestamp = timestamp
                      CurrentNode = currentNode
                      CompletedNodes = completedNodes
                      NodeRetries = nodeRetries
                      NodeOutcomes = nodeOutcomes
                      ContextValues = contextValues
                      Logs = logs }
            with _ ->
                None
        else
            None

    /// Resume a pipeline from a checkpoint
    let resumeFromCheckpoint (graph: Graph) (config: RunConfig) (checkpoint: Checkpoint) : RunResult =
        let context = Context()
        context.ConfigureArtifactStore(FileArtifactStore(config.LogsRoot) :> IArtifactStore)
        // Restore context from checkpoint
        for kv in checkpoint.ContextValues do
            context.Set(kv.Key, kv.Value)

        for log in checkpoint.Logs do
            context.AppendLog(log)

        applyInitialContext config context true

        let emitter = config.EventEmitter
        let logsRoot = config.LogsRoot
        let registry = config.Registry

        emitter.Emit(PipelineEvent.PipelineStarted(graph.Name, Guid.NewGuid().ToString("N")))
        let startTime = DateTimeOffset.UtcNow

        let completedNodes = ResizeArray<string>(checkpoint.CompletedNodes)
        let nodeOutcomes = System.Collections.Generic.Dictionary<string, Outcome>()
        let nodeCosts = System.Collections.Generic.Dictionary<string, int64 * int * int>()

        for kv in checkpoint.NodeOutcomes do
            nodeOutcomes[kv.Key] <- kv.Value
        // Backward compatibility for checkpoints that predate node_outcomes
        for nodeId in checkpoint.CompletedNodes do
            if not (nodeOutcomes.ContainsKey(nodeId)) then
                nodeOutcomes[nodeId] <- Outcome.Success(notes = $"Resumed: {nodeId}")

        let nodeRetries = System.Collections.Generic.Dictionary<string, int>()

        for kv in checkpoint.NodeRetries do
            nodeRetries[kv.Key] <- kv.Value

        let lastCompletedNode = checkpoint.CurrentNode
        let mutable currentNode = graph.Nodes[lastCompletedNode]

        let mutable lastOutcome = Outcome.Success()
        let mutable running = true
        let mutable nodeIndex = completedNodes.Count
        let mutable resumeDegradePending = true

        let resolveNextEdges (node: Node) (outcome: Outcome) =
            if outcome.Status = StageStatus.Fail then
                let failEdge =
                    graph.OutgoingEdges(node.Id)
                    |> List.filter (fun e -> e.Condition <> "")
                    |> List.tryFind (fun e -> Conditions.evaluate e.Condition outcome context)

                match failEdge with
                | Some edge -> [ edge ]
                | None ->
                    let retryTarget =
                        [ node.RetryTarget
                          node.FallbackRetryTarget
                          graph.RetryTarget
                          graph.FallbackRetryTarget ]
                        |> List.tryFind (fun target -> target <> "" && (graph.Nodes |> Map.containsKey target))

                    match retryTarget with
                    | Some target ->
                        [ { FromNode = node.Id
                            ToNode = target
                            Attributes = Map.empty } ]
                    | None -> []
            else
                EdgeSelection.selectAllMatchingEdges node outcome context graph

        let executeResumeNode (incomingEdge: Edge option) (node: Node) (checkpointNodeId: string option) =
            let fidelity = FidelityResolution.resolve incomingEdge node graph

            let nodeWithFidelity =
                { node with
                    Attributes =
                        node.Attributes
                        |> Map.add "__resolved_fidelity" (AttrValue.String((fidelity: FidelityMode).ToString())) }

            let handlerContext =
                if resumeDegradePending then
                    context.Set("resume.degraded_fidelity", "summary:high")
                    context.Set("resume.degraded_node", node.Id)
                    context.Project(FidelityMode.SummaryHigh)
                elif fidelity = FidelityMode.Full then
                    context
                else
                    context.Project(fidelity)

            emitter.Emit(PipelineEvent.StageStarted(node.Id, nodeIndex))
            context.Set("current_node", node.Id)

            let nodeForHandler = prepareNodeForExecution context fidelity nodeWithFidelity
            let handler = registry.Resolve(nodeForHandler)
            let retryPolicy = RetryPolicy.FromNode(nodeForHandler, graph)

            let rawOutcome =
                executeWithRetry handler nodeForHandler handlerContext graph logsRoot retryPolicy emitter nodeIndex

            let handlerType = ShapeMapping.resolveHandlerType node
            let statusPath = Path.Combine(logsRoot, node.Id, "status.json")

            let outcome =
                match tryLoadStatusOutcome statusPath rawOutcome with
                | Some parsed -> parsed
                | None ->
                    let shouldAutoStatus =
                        node.AutoStatus && (handlerType = "tool" || handlerType = "codergen")

                    if shouldAutoStatus && not (File.Exists(statusPath)) then
                        { rawOutcome with
                            Status = StageStatus.Success
                            FailureReason = ""
                            Notes =
                                if rawOutcome.Notes <> "" then
                                    rawOutcome.Notes
                                else
                                    "auto_status synthesized success (status.json not found)" }
                    else
                        rawOutcome

            completedNodes.Add(node.Id)
            nodeOutcomes[node.Id] <- outcome
            context.ApplyUpdates(outcome.ContextUpdates)
            context.Set("outcome", outcome.OutcomeString)

            if outcome.PreferredLabel <> "" then
                context.Set("preferred_label", outcome.PreferredLabel)

            match context.TryGet("llm.last_node"), tryParseInt64 (context.TryGet("llm.cost_microdollars")) with
            | Some lastNode, Some costMicros when lastNode = node.Id ->
                let inputTokens =
                    tryParseInt (context.TryGet("llm.input_tokens")) |> Option.defaultValue 0

                let outputTokens =
                    tryParseInt (context.TryGet("llm.output_tokens")) |> Option.defaultValue 0

                nodeCosts[node.Id] <- costMicros, inputTokens, outputTokens
                writeCostSummary logsRoot (nodeCosts |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq)
            | _ -> writeCostSummary logsRoot (nodeCosts |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq)

            let cp =
                Checkpoint.Create(
                    context,
                    checkpointNodeId |> Option.defaultValue node.Id,
                    completedNodes |> Seq.toList,
                    nodeRetries |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq,
                    nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
                )

            saveCheckpoint logsRoot cp

            if resumeDegradePending then
                resumeDegradePending <- false
                context.Set("resume.degraded_fidelity", "restored")

            outcome

        let executeResumeFanOutBranch (parentNode: Node) (branchTarget: Edge) : Outcome =
            match graph.Nodes |> Map.tryFind branchTarget.ToNode with
            | Some branchNode -> executeResumeNode (Some branchTarget) branchNode (Some parentNode.Id)
            | None ->
                let fail = Outcome.Fail($"Edge target '{branchTarget.ToNode}' not found")
                running <- false
                fail

        while running do
            // Check for user cancellation (Ctrl-C)
            if UnifiedLlm.HttpCancellation.isCancelled () then
                lastOutcome <- Outcome.Fail("Pipeline cancelled by user")
                running <- false
            else

                let node = currentNode

                if completedNodes.Contains(node.Id) then
                    let recordedOutcome =
                        match nodeOutcomes.TryGetValue(node.Id) with
                        | true, value -> value
                        | false, _ -> Outcome.Success()

                    let nextEdges = resolveNextEdges node recordedOutcome

                    if nextEdges.Length > 1 then
                        let fanInEdge, fanOutOutcome =
                            runFanOut
                                nextEdges
                                graph
                                lastOutcome
                                completedNodes.Contains
                                (fun () -> running)
                                (executeResumeFanOutBranch node)

                        lastOutcome <- fanOutOutcome

                        if running then
                            match fanInEdge with
                            | Some edge ->
                                match graph.Nodes |> Map.tryFind edge.ToNode with
                                | Some nextNode -> currentNode <- nextNode
                                | None ->
                                    lastOutcome <- Outcome.Fail($"Edge target '{edge.ToNode}' not found")
                                    running <- false
                            | None -> running <- false
                    elif nextEdges.IsEmpty then
                        running <- false
                    else
                        let edge = nextEdges.Head

                        match graph.Nodes |> Map.tryFind edge.ToNode with
                        | Some nextNode -> currentNode <- nextNode
                        | None ->
                            lastOutcome <- Outcome.Fail($"Edge target '{edge.ToNode}' not found")
                            running <- false
                elif ShapeMapping.isTerminal node then
                    let (gateOk, failedGate) =
                        GoalGates.checkGoalGates
                            graph
                            (nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)

                    if not gateOk then
                        match failedGate with
                        | Some failedNode ->
                            match GoalGates.getRetryTarget failedNode graph with
                            | Some target -> currentNode <- graph.Nodes[target]
                            | None ->
                                lastOutcome <- Outcome.Fail("Goal gate unsatisfied and no retry target")
                                running <- false
                        | None -> running <- false
                    else
                        running <- false
                else
                    let outcome = executeResumeNode None node None
                    lastOutcome <- outcome

                    let nextEdges = resolveNextEdges node outcome

                    if nextEdges.Length > 1 then
                        let fanInEdge, fanOutOutcome =
                            runFanOut
                                nextEdges
                                graph
                                lastOutcome
                                completedNodes.Contains
                                (fun () -> running)
                                (executeResumeFanOutBranch node)

                        lastOutcome <- fanOutOutcome

                        if running then
                            match fanInEdge with
                            | Some edge ->
                                match graph.Nodes |> Map.tryFind edge.ToNode with
                                | Some nextNode -> currentNode <- nextNode
                                | None ->
                                    lastOutcome <- Outcome.Fail($"Edge target '{edge.ToNode}' not found")
                                    running <- false
                            | None -> running <- false
                    elif nextEdges.IsEmpty then
                        if outcome.Status = StageStatus.Fail then
                            lastOutcome <- Outcome.Fail($"Stage '{node.Id}' failed with no outgoing fail edge")

                        running <- false
                    else
                        let edge = nextEdges.Head

                        match graph.Nodes |> Map.tryFind edge.ToNode with
                        | Some nextNode -> currentNode <- nextNode
                        | None ->
                            lastOutcome <- Outcome.Fail($"Edge target '{edge.ToNode}' not found")
                            running <- false

                    nodeIndex <- nodeIndex + 1

        let totalDuration = DateTimeOffset.UtcNow - startTime

        match lastOutcome.Status with
        | StageStatus.Success
        | StageStatus.PartialSuccess ->
            emitter.Emit(PipelineEvent.PipelineCompleted(totalDuration, completedNodes.Count))
        | _ -> emitter.Emit(PipelineEvent.PipelineFailed(lastOutcome.FailureReason, totalDuration))

        writeCostSummary logsRoot (nodeCosts |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq)

        { FinalOutcome = lastOutcome
          CompletedNodes = completedNodes |> Seq.toList
          NodeOutcomes = nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
          Context = context }

    /// Parse, validate, and run a pipeline from DOT source
    let runFromSource (source: string) (config: RunConfig) : RunResult =
        let (graph, diagnostics) =
            Transforms.preparePipeline source (Some config.ExtraTransforms)

        let errors = diagnostics |> List.filter (fun d -> d.Severity = Severity.Error)

        if not errors.IsEmpty then
            failwithf "Validation errors: %s" (errors |> List.map (fun d -> d.Message) |> String.concat "; ")

        run graph config
