namespace Attractor

open System
open System.Collections.Generic

/// Diagnostic severity levels
[<RequireQualifiedAccess>]
type Severity =
    | Error
    | Warning
    | Info

/// A validation diagnostic
type Diagnostic =
    { Rule: string
      Severity: Severity
      Message: string
      NodeId: string
      Edge: (string * string) option
      Fix: string }

    static member Error(rule, message, ?nodeId, ?edge, ?fix) =
        { Rule = rule
          Severity = Severity.Error
          Message = message
          NodeId = defaultArg nodeId ""
          Edge = edge
          Fix = defaultArg fix "" }

    static member Warning(rule, message, ?nodeId, ?edge, ?fix) =
        { Rule = rule
          Severity = Severity.Warning
          Message = message
          NodeId = defaultArg nodeId ""
          Edge = edge
          Fix = defaultArg fix "" }

    static member Info(rule, message, ?nodeId) =
        { Rule = rule
          Severity = Severity.Info
          Message = message
          NodeId = defaultArg nodeId ""
          Edge = None
          Fix = "" }

/// Lint rule interface
type ILintRule =
    abstract member Name: string
    abstract member Apply: Graph -> Diagnostic list

/// Validation exception
exception ValidationException of Diagnostic list

module Validation =

    /// Rule: Exactly one start node (shape=Mdiamond)
    let startNodeRule: ILintRule =
        { new ILintRule with
            member _.Name = "start_node"
            member _.Apply(graph) =
                let startNodes =
                    graph.Nodes
                    |> Map.toList
                    |> List.filter (fun (_, n) -> n.Shape = "Mdiamond")
                match startNodes.Length with
                | 0 ->
                    [ Diagnostic.Error("start_node", "Pipeline must have exactly one start node (shape=Mdiamond)",
                        fix = "Add a node with shape=Mdiamond") ]
                | 1 -> []
                | n ->
                    [ Diagnostic.Error("start_node",
                        $"Pipeline must have exactly one start node but found {n}") ] }

    /// Rule: Exactly one terminal/exit node (shape=Msquare)
    let terminalNodeRule: ILintRule =
        { new ILintRule with
            member _.Name = "terminal_node"
            member _.Apply(graph) =
                let exitNodes =
                    graph.Nodes
                    |> Map.toList
                    |> List.filter (fun (_, n) -> n.Shape = "Msquare")
                match exitNodes.Length with
                | 1 -> []
                | 0 ->
                    [ Diagnostic.Error("terminal_node", "Pipeline must have exactly one exit node (shape=Msquare)",
                        fix = "Add a node with shape=Msquare") ]
                | count ->
                    [ Diagnostic.Error(
                        "terminal_node",
                        $"Pipeline must have exactly one exit node but found {count}",
                        fix = "Remove extra shape=Msquare nodes so exactly one remains") ] }

    /// Rule: All nodes reachable from start
    let reachabilityRule: ILintRule =
        { new ILintRule with
            member _.Name = "reachability"
            member _.Apply(graph) =
                match graph.FindStartNode() with
                | None -> [] // start_node rule handles this
                | Some startNode ->
                    let visited = HashSet<string>()
                    let queue = Queue<string>()
                    queue.Enqueue(startNode.Id)
                    visited.Add(startNode.Id) |> ignore
                    while queue.Count > 0 do
                        let current = queue.Dequeue()
                        for edge in graph.OutgoingEdges(current) do
                            if visited.Add(edge.ToNode) then
                                queue.Enqueue(edge.ToNode)
                    graph.Nodes
                    |> Map.toList
                    |> List.choose (fun (id, _) ->
                        if visited.Contains(id) then None
                        else Some(Diagnostic.Error("reachability",
                            $"Node '{id}' is not reachable from the start node", nodeId = id))) }

    /// Rule: All edge targets reference existing nodes
    let edgeTargetExistsRule: ILintRule =
        { new ILintRule with
            member _.Name = "edge_target_exists"
            member _.Apply(graph) =
                graph.Edges
                |> List.choose (fun edge ->
                    if not (graph.Nodes |> Map.containsKey edge.ToNode) then
                        Some(Diagnostic.Error("edge_target_exists",
                            $"Edge target '{edge.ToNode}' does not exist",
                            edge = (edge.FromNode, edge.ToNode)))
                    elif not (graph.Nodes |> Map.containsKey edge.FromNode) then
                        Some(Diagnostic.Error("edge_target_exists",
                            $"Edge source '{edge.FromNode}' does not exist",
                            edge = (edge.FromNode, edge.ToNode)))
                    else None) }

    /// Rule: Start node has no incoming edges
    let startNoIncomingRule: ILintRule =
        { new ILintRule with
            member _.Name = "start_no_incoming"
            member _.Apply(graph) =
                match graph.FindStartNode() with
                | None -> []
                | Some startNode ->
                    let incoming = graph.IncomingEdges(startNode.Id)
                    if incoming.Length > 0 then
                        [ Diagnostic.Error("start_no_incoming",
                            $"Start node '{startNode.Id}' must have no incoming edges",
                            nodeId = startNode.Id) ]
                    else [] }

    /// Rule: Exit node has no outgoing edges
    let exitNoOutgoingRule: ILintRule =
        { new ILintRule with
            member _.Name = "exit_no_outgoing"
            member _.Apply(graph) =
                match graph.FindExitNode() with
                | None -> []
                | Some exitNode ->
                    let outgoing = graph.OutgoingEdges(exitNode.Id)
                    if outgoing.Length > 0 then
                        [ Diagnostic.Error("exit_no_outgoing",
                            $"Exit node '{exitNode.Id}' must have no outgoing edges",
                            nodeId = exitNode.Id) ]
                    else [] }

    /// Rule: Edge condition expressions parse correctly
    let conditionSyntaxRule: ILintRule =
        { new ILintRule with
            member _.Name = "condition_syntax"
            member _.Apply(graph) =
                graph.Edges
                |> List.choose (fun edge ->
                    if edge.Condition <> "" then
                        match Conditions.validate edge.Condition with
                        | Ok() -> None
                        | Error msg ->
                            Some(Diagnostic.Error("condition_syntax",
                                $"Invalid condition on edge {edge.FromNode}->{edge.ToNode}: {msg}",
                                edge = (edge.FromNode, edge.ToNode)))
                    else None) }

    /// Rule: Stylesheet syntax is valid
    let stylesheetSyntaxRule: ILintRule =
        { new ILintRule with
            member _.Name = "stylesheet_syntax"
            member _.Apply(graph) =
                let ss = graph.ModelStylesheet
                if ss <> "" then
                    match Stylesheet.validate ss with
                    | Ok() -> []
                    | Error msg ->
                        [ Diagnostic.Error("stylesheet_syntax", $"Invalid model_stylesheet: {msg}") ]
                else [] }

    /// Rule: Node type values should be recognized
    let typeKnownRule: ILintRule =
        let knownTypes =
            set [ "start"; "exit"; "codergen"; "wait.human"; "conditional";
                  "parallel"; "parallel.fan_in"; "tool"; "stack.manager_loop"; "coding_agent" ]
        { new ILintRule with
            member _.Name = "type_known"
            member _.Apply(graph) =
                graph.Nodes
                |> Map.toList
                |> List.choose (fun (_, node) ->
                    if node.NodeType <> "" && not (knownTypes.Contains(node.NodeType)) then
                        Some(Diagnostic.Warning("type_known",
                            $"Node '{node.Id}' has unknown type '{node.NodeType}'",
                            nodeId = node.Id))
                    else None) }

    /// Rule: Fidelity mode values must be valid
    let fidelityValidRule: ILintRule =
        { new ILintRule with
            member _.Name = "fidelity_valid"
            member _.Apply(graph) =
                let diags = ResizeArray<Diagnostic>()
                for kv in graph.Nodes do
                    let node = kv.Value
                    if node.Fidelity <> "" then
                        match FidelityMode.Parse(node.Fidelity) with
                        | None ->
                            diags.Add(Diagnostic.Warning("fidelity_valid",
                                $"Node '{node.Id}' has invalid fidelity mode '{node.Fidelity}'",
                                nodeId = node.Id))
                        | Some _ -> ()
                for edge in graph.Edges do
                    if edge.Fidelity <> "" then
                        match FidelityMode.Parse(edge.Fidelity) with
                        | None ->
                            diags.Add(Diagnostic.Warning("fidelity_valid",
                                $"Edge {edge.FromNode}->{edge.ToNode} has invalid fidelity mode '{edge.Fidelity}'",
                                edge = (edge.FromNode, edge.ToNode)))
                        | Some _ -> ()
                diags |> Seq.toList }

    /// Rule: retry_target and fallback_retry_target must reference existing nodes
    let retryTargetExistsRule: ILintRule =
        { new ILintRule with
            member _.Name = "retry_target_exists"
            member _.Apply(graph) =
                graph.Nodes
                |> Map.toList
                |> List.collect (fun (_, node) ->
                    let diags = ResizeArray<Diagnostic>()
                    if node.RetryTarget <> "" && not (graph.Nodes |> Map.containsKey node.RetryTarget) then
                        diags.Add(Diagnostic.Warning("retry_target_exists",
                            $"Node '{node.Id}' retry_target '{node.RetryTarget}' does not exist",
                            nodeId = node.Id))
                    if node.FallbackRetryTarget <> "" && not (graph.Nodes |> Map.containsKey node.FallbackRetryTarget) then
                        diags.Add(Diagnostic.Warning("retry_target_exists",
                            $"Node '{node.Id}' fallback_retry_target '{node.FallbackRetryTarget}' does not exist",
                            nodeId = node.Id))
                    diags |> Seq.toList) }

    /// Rule: goal_gate nodes should have retry targets
    let goalGateHasRetryRule: ILintRule =
        { new ILintRule with
            member _.Name = "goal_gate_has_retry"
            member _.Apply(graph) =
                graph.Nodes
                |> Map.toList
                |> List.choose (fun (_, node) ->
                    if node.GoalGate && node.RetryTarget = "" && node.FallbackRetryTarget = ""
                       && graph.RetryTarget = "" && graph.FallbackRetryTarget = "" then
                        Some(Diagnostic.Warning("goal_gate_has_retry",
                            $"Node '{node.Id}' has goal_gate=true but no retry_target",
                            nodeId = node.Id,
                            fix = "Add a retry_target attribute"))
                    else None) }

    /// Rule: Detect cycles in retry_target chains
    let retryTargetCycleRule: ILintRule =
        { new ILintRule with
            member _.Name = "retry_target_cycle"
            member _.Apply(graph) =
                graph.Nodes
                |> Map.toList
                |> List.choose (fun (_, node) ->
                    if node.GoalGate && node.RetryTarget <> "" then
                        let visited = HashSet<string>()
                        visited.Add(node.Id) |> ignore
                        let mutable current = node.RetryTarget
                        let mutable hasCycle = false
                        while current <> "" && not hasCycle do
                            if not (visited.Add(current)) then
                                hasCycle <- true
                            else
                                match graph.Nodes |> Map.tryFind current with
                                | Some n ->
                                    current <-
                                        if n.RetryTarget <> "" then n.RetryTarget
                                        elif n.FallbackRetryTarget <> "" then n.FallbackRetryTarget
                                        else ""
                                | None -> current <- ""
                        if hasCycle then
                            Some(Diagnostic.Warning("retry_target_cycle",
                                $"Node '{node.Id}' has a cycle in its retry_target chain",
                                nodeId = node.Id))
                        else None
                    else None) }

    /// Rule: Codergen nodes should have prompt or label
    let promptOnLlmNodesRule: ILintRule =
        { new ILintRule with
            member _.Name = "prompt_on_llm_nodes"
            member _.Apply(graph) =
                graph.Nodes
                |> Map.toList
                |> List.choose (fun (_, node) ->
                    let handlerType = ShapeMapping.resolveHandlerType node
                    if handlerType = "codergen" && node.Prompt = "" && node.Label = node.Id then
                        Some(Diagnostic.Warning("prompt_on_llm_nodes",
                            $"Node '{node.Id}' resolves to codergen handler but has no prompt or label",
                            nodeId = node.Id,
                            fix = "Add a prompt or label attribute"))
                    else None) }

    /// Rule: max_visits must be positive when provided
    let maxVisitsRule: ILintRule =
        { new ILintRule with
            member _.Name = "max_visits_valid"
            member _.Apply(graph) =
                graph.Nodes
                |> Map.toList
                |> List.choose (fun (_, node) ->
                    match node.GetAttr("max_visits") |> Option.bind (fun v -> v.AsInt()) with
                    | Some value when value <= 0 ->
                        Some(Diagnostic.Warning(
                            "max_visits_valid",
                            $"Node '{node.Id}' has non-positive max_visits={value}",
                            nodeId = node.Id,
                            fix = "Set max_visits to a positive integer"))
                    | _ -> None) }

    /// Rule: Every non-terminal node must have a path to a terminal node
    let terminalReachabilityRule: ILintRule =
        { new ILintRule with
            member _.Name = "terminal_reachability"
            member _.Apply(graph) =
                let terminalIds =
                    graph.Nodes
                    |> Map.toList
                    |> List.filter (fun (_, n) -> ShapeMapping.isTerminal n)
                    |> List.map fst
                    |> set
                if terminalIds.IsEmpty then [] // terminal_node rule handles this
                else
                    // BFS backwards from terminal nodes
                    let canReachTerminal = HashSet<string>()
                    let queue = Queue<string>()
                    for tid in terminalIds do
                        canReachTerminal.Add(tid) |> ignore
                        queue.Enqueue(tid)
                    while queue.Count > 0 do
                        let current = queue.Dequeue()
                        for edge in graph.IncomingEdges(current) do
                            if canReachTerminal.Add(edge.FromNode) then
                                queue.Enqueue(edge.FromNode)
                    graph.Nodes
                    |> Map.toList
                    |> List.choose (fun (id, _) ->
                        if canReachTerminal.Contains(id) then None
                        else Some(Diagnostic.Error("terminal_reachability",
                            $"Node '{id}' has no path to any terminal node — pipeline will hang",
                            nodeId = id,
                            fix = "Add an edge path from this node to an exit node"))) }

    /// Rule: Dead-end detection — non-terminal nodes with no outgoing edges
    let deadEndRule: ILintRule =
        { new ILintRule with
            member _.Name = "dead_end"
            member _.Apply(graph) =
                graph.Nodes
                |> Map.toList
                |> List.choose (fun (id, node) ->
                    if not (ShapeMapping.isTerminal node) && not (ShapeMapping.isStart node) then
                        let outgoing = graph.OutgoingEdges(id)
                        if outgoing.IsEmpty then
                            Some(Diagnostic.Error("dead_end",
                                $"Node '{id}' has no outgoing edges and is not a terminal node — pipeline will hang",
                                nodeId = id,
                                fix = "Add an edge from this node to the next stage or exit"))
                        else None
                    else None) }

    /// Generate a pipeline synopsis (informational diagnostics describing capabilities)
    let generateSynopsis (graph: Graph) : Diagnostic list =
        let nodes = graph.Nodes |> Map.toList |> List.map snd

        let handlerTypes =
            nodes |> List.map ShapeMapping.resolveHandlerType |> List.distinct

        let hasToolNodes =
            nodes |> List.exists (fun n ->
                ShapeMapping.resolveHandlerType n = "tool"
                && n.GetAttrString("tool_command", "") <> "")

        let hasCodegenAgent =
            nodes |> List.exists (fun n ->
                let handlerType = ShapeMapping.resolveHandlerType n
                if handlerType = "tool" then
                    let cmd = n.GetAttrString("tool_command", "")
                    cmd.Contains("claude") || cmd.Contains("codex") || cmd.Contains("aider")
                    || cmd.Contains("cursor") || cmd.Contains("opencode")
                elif handlerType = "coding_agent" then
                    true
                elif handlerType = "codergen" then
                    let model = n.LlmModel
                    model.Contains("codex") || model.Contains("o3") || model.Contains("o4")
                else false)

        let hasLlmNodes =
            nodes |> List.exists (fun n ->
                let handlerType = ShapeMapping.resolveHandlerType n
                handlerType = "codergen" || handlerType = "coding_agent")

        let hasHumanGates =
            nodes |> List.exists (fun n -> ShapeMapping.resolveHandlerType n = "wait.human")

        let hasGoalGates =
            nodes |> List.exists (fun n -> n.GoalGate)

        let hasParallel =
            nodes |> List.exists (fun n -> ShapeMapping.resolveHandlerType n = "parallel")

        let hasFeedbackLoops =
            // Check if any edge points backwards (to an earlier node in BFS order)
            match graph.FindStartNode() with
            | None -> false
            | Some startNode ->
                let order = Dictionary<string, int>()
                let queue = Queue<string>()
                let mutable idx = 0
                queue.Enqueue(startNode.Id)
                order[startNode.Id] <- idx
                while queue.Count > 0 do
                    let current = queue.Dequeue()
                    for edge in graph.OutgoingEdges(current) do
                        if not (order.ContainsKey(edge.ToNode)) then
                            idx <- idx + 1
                            order[edge.ToNode] <- idx
                            queue.Enqueue(edge.ToNode)
                graph.Edges |> List.exists (fun e ->
                    match order.TryGetValue(e.FromNode), order.TryGetValue(e.ToNode) with
                    | (true, fromIdx), (true, toIdx) -> toIdx < fromIdx
                    | _ -> false)

        let hasConditionals =
            nodes |> List.exists (fun n -> ShapeMapping.resolveHandlerType n = "conditional")
            || graph.Edges |> List.exists (fun e -> e.Condition <> "")

        let hasLoopRestart =
            graph.Edges |> List.exists (fun e -> e.LoopRestart)

        let hasStylesheet = graph.ModelStylesheet <> ""

        let willProduceCodeChanges = hasCodegenAgent
        let willCallLlm = hasLlmNodes
        let willRunCommands = hasToolNodes
        let isPlanningOnly = willCallLlm && not willRunCommands && not willProduceCodeChanges
        let isExecutionPipeline = willProduceCodeChanges

        let diags = ResizeArray<Diagnostic>()

        // Pipeline type classification
        if isExecutionPipeline then
            diags.Add(Diagnostic.Info("synopsis",
                "EXECUTION pipeline — will invoke coding agents and run commands to produce code changes"))
        elif isPlanningOnly then
            diags.Add(Diagnostic.Info("synopsis",
                "PLANNING pipeline — generates plans/docs via LLM but does NOT execute code changes"))
        elif willRunCommands then
            diags.Add(Diagnostic.Info("synopsis",
                "HYBRID pipeline — runs commands and LLM calls but no coding agent detected"))
        else
            diags.Add(Diagnostic.Info("synopsis",
                "ANALYSIS pipeline — LLM-only, no tool commands or code changes"))

        // Capability flags
        let flags = ResizeArray<string>()
        if willCallLlm then flags.Add("LLM")
        if willRunCommands then flags.Add("TOOLS")
        if willProduceCodeChanges then flags.Add("CODE_CHANGES")
        if hasHumanGates then flags.Add("HUMAN_GATES")
        if hasGoalGates then flags.Add("GOAL_GATES")
        if hasParallel then flags.Add("PARALLEL")
        if hasFeedbackLoops then flags.Add("FEEDBACK_LOOPS")
        if hasConditionals then flags.Add("CONDITIONALS")
        if hasLoopRestart then flags.Add("LOOP_RESTART")
        if hasStylesheet then flags.Add("MODEL_STYLESHEET")

        diags.Add(Diagnostic.Info("synopsis",
            sprintf "Capabilities: [%s]" (flags |> String.concat " | ")))

        // Node breakdown
        let codergenCount =
            nodes
            |> List.filter (fun n ->
                let handlerType = ShapeMapping.resolveHandlerType n
                handlerType = "codergen" || handlerType = "coding_agent")
            |> List.length
        let toolCount = nodes |> List.filter (fun n -> ShapeMapping.resolveHandlerType n = "tool") |> List.length
        let humanCount = nodes |> List.filter (fun n -> ShapeMapping.resolveHandlerType n = "wait.human") |> List.length
        let conditionalCount = nodes |> List.filter (fun n -> ShapeMapping.resolveHandlerType n = "conditional") |> List.length
        let parallelCount = nodes |> List.filter (fun n -> ShapeMapping.resolveHandlerType n = "parallel") |> List.length

        diags.Add(Diagnostic.Info("synopsis",
            sprintf "Stages: %d LLM, %d tool, %d human, %d conditional, %d parallel"
                codergenCount toolCount humanCount conditionalCount parallelCount))

        // Warnings for common misconfigurations
        if isPlanningOnly then
            diags.Add(Diagnostic.Warning("synopsis",
                "This pipeline has no tool nodes — it will generate LLM output but will NOT make any code changes or run any commands",
                fix = "Add parallelogram (tool) nodes with tool_command to execute work, or invoke a coding agent"))

        if willRunCommands && not willProduceCodeChanges && not isPlanningOnly then
            diags.Add(Diagnostic.Warning("synopsis",
                "This pipeline runs tool commands but no coding agent was detected — tool outputs are captured but no code is written",
                fix = "Add a tool node that invokes 'claude --auto' or 'codex exec' to implement changes"))

        if not hasHumanGates && not hasFeedbackLoops then
            diags.Add(Diagnostic.Info("synopsis",
                "No human gates or feedback loops — pipeline runs straight through"))

        if not hasGoalGates && hasLlmNodes then
            diags.Add(Diagnostic.Info("synopsis",
                "No goal gates — pipeline will exit regardless of LLM outcome quality"))

        diags |> Seq.toList

    /// All built-in lint rules
    let builtInRules: ILintRule list =
        [ startNodeRule
          terminalNodeRule
          reachabilityRule
          terminalReachabilityRule
          deadEndRule
          edgeTargetExistsRule
          startNoIncomingRule
          exitNoOutgoingRule
          conditionSyntaxRule
          stylesheetSyntaxRule
          typeKnownRule
          fidelityValidRule
          retryTargetExistsRule
          goalGateHasRetryRule
          retryTargetCycleRule
          promptOnLlmNodesRule
          maxVisitsRule ]

    /// Run validation on a graph with optional extra rules
    let validate (graph: Graph) (extraRules: ILintRule list option) : Diagnostic list =
        let rules =
            match extraRules with
            | Some extra -> builtInRules @ extra
            | None -> builtInRules
        let diags = rules |> List.collect (fun rule -> rule.Apply(graph))
        let synopsis = generateSynopsis graph
        diags @ synopsis

    /// Run validation and raise on error-severity diagnostics
    let validateOrRaise (graph: Graph) (extraRules: ILintRule list option) : Diagnostic list =
        let diagnostics = validate graph extraRules
        let errors = diagnostics |> List.filter (fun d -> d.Severity = Severity.Error)
        if not errors.IsEmpty then
            raise (ValidationException errors)
        diagnostics
