namespace Attractor

open System
open System.IO
open System.Text.Json

/// Backoff configuration for retries
type BackoffConfig =
    { InitialDelayMs: int
      BackoffFactor: float
      MaxDelayMs: int
      Jitter: bool }

    static member Default =
        { InitialDelayMs = 200; BackoffFactor = 2.0; MaxDelayMs = 60000; Jitter = true }

    static member None =
        { InitialDelayMs = 0; BackoffFactor = 1.0; MaxDelayMs = 0; Jitter = false }

    member this.DelayForAttempt(attempt: int) =
        let delay = float this.InitialDelayMs * Math.Pow(this.BackoffFactor, float (attempt - 1))
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

    static member None = { MaxAttempts = 1; Backoff = BackoffConfig.None }

    static member Standard =
        { MaxAttempts = 5; Backoff = BackoffConfig.Default }

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
      ExtraTransforms: ITransform list }

    static member Default(logsRoot: string) =
        { LogsRoot = logsRoot
          Registry = HandlerRegistry.CreateDefault()
          EventEmitter = EventEmitter()
          ExtraTransforms = [] }

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
            if wCmp <> 0 then wCmp
            else compare a.ToNode b.ToNode) // ascending lexical
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
                |> List.filter (fun e ->
                    e.Condition <> "" && Conditions.evaluate e.Condition outcome context)
            if not conditionMatched.IsEmpty then
                bestByWeightThenLexical conditionMatched
            else
                // Step 2: Preferred label match
                let labelMatch =
                    if outcome.PreferredLabel <> "" then
                        let normalizedPref = AcceleratorKey.normalizeLabel outcome.PreferredLabel
                        unconditional
                        |> List.tryFind (fun e ->
                            AcceleratorKey.normalizeLabel e.Label = normalizedPref)
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
                        if UnifiedLlm.HttpCancellation.isCancelled() then None
                        else
                            if not unconditional.IsEmpty then
                                bestByWeightThenLexical unconditional
                            else
                                None

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
                    | StageStatus.Success | StageStatus.PartialSuccess -> None
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
        targets
        |> List.tryFind (fun t -> t <> "" && graph.Nodes |> Map.containsKey t)

/// Fidelity resolution: edge > node > graph > default(compact)
module FidelityResolution =

    /// Resolve fidelity mode for a node, considering incoming edge override
    let resolve (incomingEdge: Edge option) (node: Node) (graph: Graph) : FidelityMode =
        // 1. Edge-level override (highest priority)
        let edgeFidelity =
            incomingEdge
            |> Option.bind (fun e ->
                if e.Fidelity <> "" then FidelityMode.Parse(e.Fidelity)
                else None)
        match edgeFidelity with
        | Some f -> f
        | None ->
            // 2. Node-level
            if node.Fidelity <> "" then
                FidelityMode.Parse(node.Fidelity) |> Option.defaultValue FidelityMode.Compact
            else
                // 3. Graph-level default
                if graph.DefaultFidelity <> "" then
                    FidelityMode.Parse(graph.DefaultFidelity) |> Option.defaultValue FidelityMode.Compact
                else
                    // 4. Default
                    FidelityMode.Compact

/// The pipeline execution engine
module Engine =

    /// Mirror graph attributes into the context
    let mirrorGraphAttributes (graph: Graph) (context: Context) =
        context.Set("graph.goal", graph.Goal)
        for kv in graph.GraphAttributes do
            context.Set($"graph.{kv.Key}", kv.Value.AsString())

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
        let mutable attempt = 1
        let mutable finalOutcome = Outcome.Fail("max retries exceeded")
        let mutable cont = true

        while cont && attempt <= retryPolicy.MaxAttempts do
            try
                let outcome = handler.Execute(node, context, graph, logsRoot)

                match outcome.Status with
                | StageStatus.Success | StageStatus.PartialSuccess ->
                    finalOutcome <- outcome
                    cont <- false

                | StageStatus.Retry
                | StageStatus.Fail ->
                    if attempt < retryPolicy.MaxAttempts then
                        let delay = retryPolicy.Backoff.DelayForAttempt(attempt)
                        emitter.Emit(PipelineEvent.StageRetrying(node.Id, nodeIndex, attempt, delay))
                        if delay > 0 then
                            System.Threading.Thread.Sleep(delay)
                        attempt <- attempt + 1
                    else
                        if outcome.Status = StageStatus.Retry && node.AllowPartial then
                            finalOutcome <- Outcome.PartialSuccess(notes = "retries exhausted, partial accepted")
                        elif outcome.Status = StageStatus.Retry then
                            finalOutcome <- Outcome.Fail("max retries exceeded")
                        else
                            finalOutcome <- outcome
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
                    emitter.Emit(PipelineEvent.StageRetrying(node.Id, nodeIndex, attempt, retryPolicy.Backoff.DelayForAttempt(attempt)))
                    let delay = retryPolicy.Backoff.DelayForAttempt(attempt)
                    if delay > 0 then
                        System.Threading.Thread.Sleep(delay)
                    attempt <- attempt + 1
                else
                    finalOutcome <- Outcome.Fail(ex.Message)
                    cont <- false

        finalOutcome

    /// Save checkpoint to disk
    let saveCheckpoint (logsRoot: string) (checkpoint: Checkpoint) =
        if not (Directory.Exists(logsRoot)) then
            Directory.CreateDirectory(logsRoot) |> ignore

        let serializedOutcomes =
            checkpoint.NodeOutcomes
            |> Map.map (fun _ outcome ->
                {| status = outcome.Status.ToString()
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
        let json = JsonSerializer.Serialize(data, JsonSerializerOptions(WriteIndented = true))
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

                let notes =
                    tryGetJsonString root "notes"
                    |> Option.defaultValue fallback.Notes

                let failureReason =
                    tryGetJsonString root "failure_reason"
                    |> Option.defaultValue fallback.FailureReason

                Some
                    { Status = status
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
        let manifestJson = JsonSerializer.Serialize(manifest, JsonSerializerOptions(WriteIndented = true))
        File.WriteAllText(Path.Combine(logsRoot, "manifest.json"), manifestJson)

        emitter.Emit(PipelineEvent.PipelineStarted(graph.Name, Guid.NewGuid().ToString("N")))
        let startTime = DateTimeOffset.UtcNow

        let completedNodes = ResizeArray<string>()
        let nodeOutcomes = System.Collections.Generic.Dictionary<string, Outcome>()
        let nodeRetries = System.Collections.Generic.Dictionary<string, int>()
        let nodeVisitCounts = System.Collections.Generic.Dictionary<string, int>()

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

        while running do
            // Check for user cancellation (Ctrl-C)
            if UnifiedLlm.HttpCancellation.isCancelled() then
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
                | None ->
                    running <- false
                nodeIndex <- nodeIndex + 1
            else

            // Step 1: Check for terminal node
            if ShapeMapping.isTerminal node then
                let (gateOk, failedGate) =
                    GoalGates.checkGoalGates graph (nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)
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
                    | None ->
                        running <- false
                else
                    running <- false
            else
                // Resolve fidelity for this node
                let fidelity = FidelityResolution.resolve lastEdge node graph
                let nodeForHandler =
                    { node with
                        Attributes =
                            node.Attributes
                            |> Map.add "__resolved_fidelity" (AttrValue.String(string fidelity)) }
                let handlerContext =
                    if fidelity = FidelityMode.Full then context
                    else context.Project(fidelity)

                // Step 2: Execute node handler with retry policy
                let handler = registry.Resolve(nodeForHandler)
                let retryPolicy = RetryPolicy.FromNode(nodeForHandler, graph)

                emitter.Emit(PipelineEvent.StageStarted(node.Id, nodeIndex))
                let handlerType = ShapeMapping.resolveHandlerType node
                if handlerType = "parallel" then
                    let branches = graph.OutgoingEdges(node.Id)
                    emitter.Emit(PipelineEvent.ParallelStarted(branches.Length))
                    branches
                    |> List.iteri (fun idx branch ->
                        emitter.Emit(PipelineEvent.ParallelBranchStarted(branch.ToNode, idx)))
                elif handlerType = "wait.human" then
                    let question =
                        if node.Label <> "" then node.Label
                        else node.Id
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
                context.Set("node.visit_count", string visitCount)
                context.Set($"node.{node.Id}.visit_count", string visitCount)

                // Set thread_id in context if fidelity=full and node has thread_id
                if fidelity = FidelityMode.Full && node.ThreadId <> "" then
                    context.Set("thread_id", node.ThreadId)

                let outcome =
                    if visitCount > node.MaxVisits then
                        Outcome.Fail($"Node '{node.Id}' exceeded max_visits ({node.MaxVisits})")
                    else
                        let raw = executeWithRetry handler nodeForHandler handlerContext graph currentLogsRoot retryPolicy emitter nodeIndex
                        let statusPath = Path.Combine(currentLogsRoot, node.Id, "status.json")
                        match tryLoadStatusOutcome statusPath raw with
                        | Some parsed -> parsed
                        | None ->
                            let shouldAutoStatus =
                                node.AutoStatus
                                && (handlerType = "tool" || handlerType = "codergen")
                            if shouldAutoStatus && not (File.Exists(statusPath)) then
                                { raw with
                                    Status = StageStatus.Success
                                    FailureReason = ""
                                    Notes =
                                        if raw.Notes <> "" then raw.Notes
                                        else "auto_status synthesized success (status.json not found)" }
                            else
                                raw

                let stageDuration = DateTimeOffset.UtcNow - stageStart

                match outcome.Status with
                | StageStatus.Fail ->
                    emitter.Emit(PipelineEvent.StageFailed(node.Id, nodeIndex, outcome.FailureReason, false))
                | _ ->
                    emitter.Emit(PipelineEvent.StageCompleted(node.Id, nodeIndex, stageDuration))

                if handlerType = "parallel" then
                    let branchStatuses =
                        outcome.ContextUpdates
                        |> Map.toList
                        |> List.choose (fun (k, v) ->
                            if k.StartsWith("parallel.branch.") && k.EndsWith(".status") then
                                let branchId = k.Replace("parallel.branch.", "").Replace(".status", "")
                                Some (branchId, (v = "success"))
                            else None)
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
                    let question =
                        if node.Label <> "" then node.Label
                        else node.Id
                    if outcome.Status = StageStatus.Retry && outcome.FailureReason.Contains("timeout", StringComparison.OrdinalIgnoreCase) then
                        emitter.Emit(PipelineEvent.InterviewTimeout(question, node.Id, stageDuration))
                    else
                        let answer =
                            outcome.ContextUpdates
                            |> Map.tryFind "human.gate.label"
                            |> Option.orElseWith (fun () -> outcome.ContextUpdates |> Map.tryFind "human.gate.selected")
                            |> Option.defaultValue ""
                        emitter.Emit(PipelineEvent.InterviewCompleted(question, answer, stageDuration))

                // Step 3: Record completion
                completedNodes.Add(node.Id)
                nodeOutcomes[node.Id] <- outcome

                // Step 4: Apply context updates to canonical context
                context.ApplyUpdates(outcome.ContextUpdates)
                context.Set("outcome", outcome.Status.ToString())
                if outcome.PreferredLabel <> "" then
                    context.Set("preferred_label", outcome.PreferredLabel)

                // Step 5: Save checkpoint
                let checkpoint =
                    Checkpoint.Create(
                        context, node.Id,
                        completedNodes |> Seq.toList,
                        nodeRetries |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq,
                        nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)
                saveCheckpoint currentLogsRoot checkpoint
                emitter.Emit(PipelineEvent.CheckpointSaved(node.Id))

                lastOutcome <- outcome

                // Step 6: Select next edge
                let nextEdge =
                    if outcome.Status = StageStatus.Fail then
                        if outcome.FailureReason.Contains("exceeded max_visits", StringComparison.OrdinalIgnoreCase) then
                            None
                        else
                            let failEdge =
                                graph.OutgoingEdges(node.Id)
                                |> List.filter (fun e -> e.Condition <> "")
                                |> List.tryFind (fun e -> Conditions.evaluate e.Condition outcome context)
                            match failEdge with
                            | Some edge -> Some edge
                            | None ->
                                let retryTarget =
                                    [ node.RetryTarget
                                      node.FallbackRetryTarget
                                      graph.RetryTarget
                                      graph.FallbackRetryTarget ]
                                    |> List.tryFind (fun target ->
                                        target <> "" && (graph.Nodes |> Map.containsKey target))
                                retryTarget
                                |> Option.map (fun target ->
                                    { FromNode = node.Id
                                      ToNode = target
                                      Attributes = Map.empty })
                    else
                        EdgeSelection.selectEdge node outcome context graph
                match nextEdge with
                | None ->
                    if outcome.Status = StageStatus.Fail then
                        lastOutcome <- outcome
                    running <- false
                | Some edge ->
                    lastEdge <- Some edge

                    // Step 7: Handle loop_restart
                    if edge.LoopRestart then
                        restartCount <- restartCount + 1
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
                            let restartJson = JsonSerializer.Serialize(restartManifest, JsonSerializerOptions(WriteIndented = true))
                            File.WriteAllText(Path.Combine(newLogsRoot, "restart-manifest.json"), restartJson)
                            currentLogsRoot <- newLogsRoot
                            // Reset context: keep only graph.* attributes
                            let graphAttrs =
                                context.Snapshot()
                                |> Map.filter (fun k _ -> k.StartsWith("graph."))
                            let freshContext = createContext newLogsRoot
                            for kv in graphAttrs do
                                freshContext.Set(kv.Key, kv.Value)
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
                    | Some nextNode ->
                        currentNode <- nextNode
                    | None ->
                        lastOutcome <- Outcome.Fail($"Edge target '{edge.ToNode}' not found")
                        running <- false

                nodeIndex <- nodeIndex + 1

        let totalDuration = DateTimeOffset.UtcNow - startTime

        match lastOutcome.Status with
        | StageStatus.Success | StageStatus.PartialSuccess ->
            emitter.Emit(PipelineEvent.PipelineCompleted(totalDuration, completedNodes.Count))
        | _ ->
            emitter.Emit(PipelineEvent.PipelineFailed(lastOutcome.FailureReason, totalDuration))

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
                    else []
                let timestamp =
                    if root.TryGetProperty("timestamp") |> fst then
                        DateTimeOffset.Parse(root.GetProperty("timestamp").GetString())
                    else DateTimeOffset.UtcNow
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

        let emitter = config.EventEmitter
        let logsRoot = config.LogsRoot
        let registry = config.Registry

        emitter.Emit(PipelineEvent.PipelineStarted(graph.Name, Guid.NewGuid().ToString("N")))
        let startTime = DateTimeOffset.UtcNow

        let completedNodes = ResizeArray<string>(checkpoint.CompletedNodes)
        let nodeOutcomes = System.Collections.Generic.Dictionary<string, Outcome>()
        for kv in checkpoint.NodeOutcomes do
            nodeOutcomes[kv.Key] <- kv.Value
        // Backward compatibility for checkpoints that predate node_outcomes
        for nodeId in checkpoint.CompletedNodes do
            if not (nodeOutcomes.ContainsKey(nodeId)) then
                nodeOutcomes[nodeId] <- Outcome.Success(notes = $"Resumed: {nodeId}")
        let nodeRetries = System.Collections.Generic.Dictionary<string, int>()
        for kv in checkpoint.NodeRetries do
            nodeRetries[kv.Key] <- kv.Value

        // Find the node AFTER the checkpointed one
        let lastCompletedNode = checkpoint.CurrentNode
        let lastNode = graph.Nodes[lastCompletedNode]
        let lastOutcomeForEdge =
            checkpoint.NodeOutcomes
            |> Map.tryFind lastCompletedNode
            |> Option.defaultValue (Outcome.Success())
        let nextEdge = EdgeSelection.selectEdge lastNode lastOutcomeForEdge context graph

        let mutable currentNode =
            match nextEdge with
            | Some edge -> graph.Nodes[edge.ToNode]
            | None -> graph.Nodes[lastCompletedNode] // fallback

        let mutable lastOutcome = Outcome.Success()
        let mutable running = true
        let mutable nodeIndex = completedNodes.Count
        let mutable resumeDegradePending = true

        while running do
            // Check for user cancellation (Ctrl-C)
            if UnifiedLlm.HttpCancellation.isCancelled() then
                lastOutcome <- Outcome.Fail("Pipeline cancelled by user")
                running <- false
            else

            let node = currentNode
            if ShapeMapping.isTerminal node then
                let (gateOk, failedGate) =
                    GoalGates.checkGoalGates graph (nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)
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
                let fidelity = FidelityResolution.resolve None node graph
                let handlerContext =
                    if resumeDegradePending then
                        context.Set("resume.degraded_fidelity", "summary:high")
                        context.Set("resume.degraded_node", node.Id)
                        context.Project(FidelityMode.SummaryHigh)
                    elif fidelity = FidelityMode.Full then context
                    else context.Project(fidelity)

                let handler = registry.Resolve(node)
                let retryPolicy = RetryPolicy.FromNode(node, graph)
                emitter.Emit(PipelineEvent.StageStarted(node.Id, nodeIndex))
                context.Set("current_node", node.Id)
                let outcome = executeWithRetry handler node handlerContext graph logsRoot retryPolicy emitter nodeIndex

                let handlerType = ShapeMapping.resolveHandlerType node
                let statusPath = Path.Combine(logsRoot, node.Id, "status.json")
                let outcome =
                    match tryLoadStatusOutcome statusPath outcome with
                    | Some parsed -> parsed
                    | None ->
                        let shouldAutoStatus =
                            node.AutoStatus
                            && (handlerType = "tool" || handlerType = "codergen")
                        if shouldAutoStatus && not (File.Exists(statusPath)) then
                            { outcome with
                                Status = StageStatus.Success
                                FailureReason = ""
                                Notes =
                                    if outcome.Notes <> "" then outcome.Notes
                                    else "auto_status synthesized success (status.json not found)" }
                        else
                            outcome

                completedNodes.Add(node.Id)
                nodeOutcomes[node.Id] <- outcome
                context.ApplyUpdates(outcome.ContextUpdates)
                context.Set("outcome", outcome.Status.ToString())
                if outcome.PreferredLabel <> "" then
                    context.Set("preferred_label", outcome.PreferredLabel)

                let cp =
                    Checkpoint.Create(
                        context, node.Id,
                        completedNodes |> Seq.toList,
                        nodeRetries |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq,
                        nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq)
                saveCheckpoint logsRoot cp
                lastOutcome <- outcome

                let nextEdge = EdgeSelection.selectEdge node outcome context graph
                match nextEdge with
                | None ->
                    if outcome.Status = StageStatus.Fail then
                        lastOutcome <- Outcome.Fail($"Stage '{node.Id}' failed with no outgoing fail edge")
                    running <- false
                | Some edge ->
                    match graph.Nodes |> Map.tryFind edge.ToNode with
                    | Some nextNode -> currentNode <- nextNode
                    | None ->
                        lastOutcome <- Outcome.Fail($"Edge target '{edge.ToNode}' not found")
                        running <- false

                nodeIndex <- nodeIndex + 1
                if resumeDegradePending then
                    resumeDegradePending <- false
                    context.Set("resume.degraded_fidelity", "restored")

        let totalDuration = DateTimeOffset.UtcNow - startTime
        match lastOutcome.Status with
        | StageStatus.Success | StageStatus.PartialSuccess ->
            emitter.Emit(PipelineEvent.PipelineCompleted(totalDuration, completedNodes.Count))
        | _ ->
            emitter.Emit(PipelineEvent.PipelineFailed(lastOutcome.FailureReason, totalDuration))

        { FinalOutcome = lastOutcome
          CompletedNodes = completedNodes |> Seq.toList
          NodeOutcomes = nodeOutcomes |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
          Context = context }

    /// Parse, validate, and run a pipeline from DOT source
    let runFromSource (source: string) (config: RunConfig) : RunResult =
        let (graph, diagnostics) = Transforms.preparePipeline source (Some config.ExtraTransforms)
        let errors = diagnostics |> List.filter (fun d -> d.Severity = Severity.Error)
        if not errors.IsEmpty then
            failwithf "Validation errors: %s" (errors |> List.map (fun d -> d.Message) |> String.concat "; ")
        run graph config
