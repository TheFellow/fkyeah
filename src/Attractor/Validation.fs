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
                    graph.Nodes |> Map.toList |> List.filter (fun (_, n) -> n.Shape = "Mdiamond")

                match startNodes.Length with
                | 0 ->
                    [ Diagnostic.Error(
                          "start_node",
                          "Pipeline must have exactly one start node (shape=Mdiamond)",
                          fix = "Add a node with shape=Mdiamond"
                      ) ]
                | 1 -> []
                | n -> [ Diagnostic.Error("start_node", $"Pipeline must have exactly one start node but found {n}") ] }

    /// Rule: Exactly one terminal/exit node (shape=Msquare)
    let terminalNodeRule: ILintRule =
        { new ILintRule with
            member _.Name = "terminal_node"

            member _.Apply(graph) =
                let terminalCandidates =
                    graph.Nodes
                    |> Map.toList
                    |> List.filter (fun (id, n) -> n.Shape = "Msquare" || id = "exit" || id = "end")
                    |> List.map fst
                    |> List.distinct

                match terminalCandidates.Length with
                | 1 -> []
                | 0 ->
                    match graph.FindExitNode() with
                    | Some _ -> []
                    | None ->
                        [ Diagnostic.Error(
                              "terminal_node",
                              "Pipeline must have exactly one exit node (shape=Msquare or id=exit/end)",
                              fix = "Add a node with shape=Msquare or id=exit/end"
                          ) ]
                | count ->
                    [ Diagnostic.Error(
                          "terminal_node",
                          $"Pipeline must have exactly one exit node but found {count}",
                          fix = "Remove extra terminal nodes so exactly one remains"
                      ) ] }

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
                        if visited.Contains(id) then
                            None
                        else
                            Some(
                                Diagnostic.Error(
                                    "reachability",
                                    $"Node '{id}' is not reachable from the start node",
                                    nodeId = id
                                )
                            )) }

    /// Rule: All edge targets reference existing nodes
    let edgeTargetExistsRule: ILintRule =
        { new ILintRule with
            member _.Name = "edge_target_exists"

            member _.Apply(graph) =
                graph.Edges
                |> List.choose (fun edge ->
                    if not (graph.Nodes |> Map.containsKey edge.ToNode) then
                        Some(
                            Diagnostic.Error(
                                "edge_target_exists",
                                $"Edge target '{edge.ToNode}' does not exist",
                                edge = (edge.FromNode, edge.ToNode)
                            )
                        )
                    elif not (graph.Nodes |> Map.containsKey edge.FromNode) then
                        Some(
                            Diagnostic.Error(
                                "edge_target_exists",
                                $"Edge source '{edge.FromNode}' does not exist",
                                edge = (edge.FromNode, edge.ToNode)
                            )
                        )
                    else
                        None) }

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
                        [ Diagnostic.Error(
                              "start_no_incoming",
                              $"Start node '{startNode.Id}' must have no incoming edges",
                              nodeId = startNode.Id
                          ) ]
                    else
                        [] }

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
                        [ Diagnostic.Error(
                              "exit_no_outgoing",
                              $"Exit node '{exitNode.Id}' must have no outgoing edges",
                              nodeId = exitNode.Id
                          ) ]
                    else
                        [] }

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
                            Some(
                                Diagnostic.Error(
                                    "condition_syntax",
                                    $"Invalid condition on edge {edge.FromNode}->{edge.ToNode}: {msg}",
                                    edge = (edge.FromNode, edge.ToNode)
                                )
                            )
                    else
                        None) }

    /// Rule: Stylesheet syntax is valid
    let stylesheetSyntaxRule: ILintRule =
        { new ILintRule with
            member _.Name = "stylesheet_syntax"

            member _.Apply(graph) =
                let ss = graph.ModelStylesheet

                if ss <> "" then
                    match Stylesheet.validate ss with
                    | Ok() -> []
                    | Error msg -> [ Diagnostic.Error("stylesheet_syntax", $"Invalid model_stylesheet: {msg}") ]
                else
                    [] }

    /// Rule: Model references (direct node llm_model and stylesheet declarations)
    /// must resolve in the built-in ModelCatalog.
    let modelKnownRule: ILintRule =
        { new ILintRule with
            member _.Name = "model_known"

            member _.Apply(graph) =
                let diags = ResizeArray<Diagnostic>()

                let classify (modelId: string) =
                    if System.String.IsNullOrWhiteSpace(modelId) then
                        None
                    else
                        UnifiedLlm.ModelCatalog.tryResolveModel modelId

                // Per-node direct llm_model attribute
                for kv in graph.Nodes do
                    let node = kv.Value
                    let model = node.LlmModel

                    if model <> "" && (classify model).IsNone then
                        diags.Add(
                            Diagnostic.Error(
                                "model_known",
                                sprintf
                                    "Node '%s' references unknown model '%s'; run 'attractor --models' for the known catalog"
                                    node.Id
                                    model,
                                nodeId = node.Id,
                                fix =
                                    "Check spelling, use a known alias, or update the ModelCatalog if the model is real"
                            )
                        )

                // Stylesheet llm_model declarations
                if graph.ModelStylesheet <> "" then
                    match Stylesheet.parse graph.ModelStylesheet with
                    | Ok parsed ->
                        for rule in parsed.Rules do
                            for decl in rule.Declarations do
                                if decl.Property = "llm_model" && (classify decl.Value).IsNone then
                                    diags.Add(
                                        Diagnostic.Error(
                                            "model_known",
                                            sprintf
                                                "Stylesheet references unknown model '%s' (selector matches nodes in this graph); run 'attractor --models' for the known catalog"
                                                decl.Value,
                                            fix =
                                                "Check spelling, use a known alias, or update the ModelCatalog if the model is real"
                                        )
                                    )
                    | Error _ ->
                        // stylesheet_syntax rule reports this; nothing to add here
                        ()

                diags |> List.ofSeq }

    /// Rule: Node type values should be recognized
    let typeKnownRule: ILintRule =
        let knownTypes =
            set
                [ "start"
                  "exit"
                  "codergen"
                  "wait.human"
                  "conditional"
                  "parallel"
                  "parallel.fan_in"
                  "tool"
                  "stack.manager_loop"
                  "coding_agent" ]

        { new ILintRule with
            member _.Name = "type_known"

            member _.Apply(graph) =
                graph.Nodes
                |> Map.toList
                |> List.choose (fun (_, node) ->
                    if node.NodeType <> "" && not (knownTypes.Contains(node.NodeType)) then
                        Some(
                            Diagnostic.Warning(
                                "type_known",
                                $"Node '{node.Id}' has unknown type '{node.NodeType}'",
                                nodeId = node.Id
                            )
                        )
                    else
                        None) }

    /// Rule: manager_loop nodes should usually configure stack.child_dotfile
    let managerLoopChildDotfileRule: ILintRule =
        { new ILintRule with
            member _.Name = "manager_loop_child_dotfile"

            member _.Apply(graph) =
                let hasManagerLoopNode =
                    graph.Nodes
                    |> Map.exists (fun _ node -> ShapeMapping.resolveHandlerType node = "stack.manager_loop")

                if hasManagerLoopNode && String.IsNullOrWhiteSpace(graph.StackChildDotfile) then
                    [ Diagnostic.Warning(
                          "manager_loop_child_dotfile",
                          "Graph contains stack.manager_loop node(s) but no stack.child_dotfile graph attribute; manager will run polling mode fallback",
                          fix = "Set graph attribute stack.child_dotfile to enable child pipeline mode"
                      ) ]
                else
                    [] }

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
                            diags.Add(
                                Diagnostic.Warning(
                                    "fidelity_valid",
                                    $"Node '{node.Id}' has invalid fidelity mode '{node.Fidelity}'",
                                    nodeId = node.Id
                                )
                            )
                        | Some _ -> ()

                for edge in graph.Edges do
                    if edge.Fidelity <> "" then
                        match FidelityMode.Parse(edge.Fidelity) with
                        | None ->
                            diags.Add(
                                Diagnostic.Warning(
                                    "fidelity_valid",
                                    $"Edge {edge.FromNode}->{edge.ToNode} has invalid fidelity mode '{edge.Fidelity}'",
                                    edge = (edge.FromNode, edge.ToNode)
                                )
                            )
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
                        diags.Add(
                            Diagnostic.Warning(
                                "retry_target_exists",
                                $"Node '{node.Id}' retry_target '{node.RetryTarget}' does not exist",
                                nodeId = node.Id
                            )
                        )

                    if
                        node.FallbackRetryTarget <> ""
                        && not (graph.Nodes |> Map.containsKey node.FallbackRetryTarget)
                    then
                        diags.Add(
                            Diagnostic.Warning(
                                "retry_target_exists",
                                $"Node '{node.Id}' fallback_retry_target '{node.FallbackRetryTarget}' does not exist",
                                nodeId = node.Id
                            )
                        )

                    diags |> Seq.toList) }

    /// Rule: goal_gate nodes should have retry targets
    let goalGateHasRetryRule: ILintRule =
        { new ILintRule with
            member _.Name = "goal_gate_has_retry"

            member _.Apply(graph) =
                graph.Nodes
                |> Map.toList
                |> List.choose (fun (_, node) ->
                    if
                        node.GoalGate
                        && node.RetryTarget = ""
                        && node.FallbackRetryTarget = ""
                        && graph.RetryTarget = ""
                        && graph.FallbackRetryTarget = ""
                    then
                        Some(
                            Diagnostic.Warning(
                                "goal_gate_has_retry",
                                $"Node '{node.Id}' has goal_gate=true but no retry_target",
                                nodeId = node.Id,
                                fix = "Add a retry_target attribute"
                            )
                        )
                    else
                        None) }

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
                            Some(
                                Diagnostic.Warning(
                                    "retry_target_cycle",
                                    $"Node '{node.Id}' has a cycle in its retry_target chain",
                                    nodeId = node.Id
                                )
                            )
                        else
                            None
                    else
                        None) }

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
                        Some(
                            Diagnostic.Warning(
                                "prompt_on_llm_nodes",
                                $"Node '{node.Id}' resolves to codergen handler but has no prompt or label",
                                nodeId = node.Id,
                                fix = "Add a prompt or label attribute"
                            )
                        )
                    else
                        None) }

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
                        Some(
                            Diagnostic.Warning(
                                "max_visits_valid",
                                $"Node '{node.Id}' has non-positive max_visits={value}",
                                nodeId = node.Id,
                                fix = "Set max_visits to a positive integer"
                            )
                        )
                    | _ -> None) }

    let private normalizeAttrName (value: string) =
        value.ToLowerInvariant()
        |> Seq.filter Char.IsLetterOrDigit
        |> Seq.toArray
        |> String

    let private tokenizeAttrName (value: string) =
        value
            .ToLowerInvariant()
            .Split([| '.'; '_'; '-' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)

    let private levenshteinDistance (left: string) (right: string) =
        if left = right then
            0
        elif left = "" then
            right.Length
        elif right = "" then
            left.Length
        else
            let previous = Array.init (right.Length + 1) id
            let current = Array.zeroCreate<int> (right.Length + 1)

            for i in 1 .. left.Length do
                current[0] <- i

                for j in 1 .. right.Length do
                    let substitutionCost = if left[i - 1] = right[j - 1] then 0 else 1
                    current[j] <- min (min (current[j - 1] + 1) (previous[j] + 1)) (previous[j - 1] + substitutionCost)

                Array.blit current 0 previous 0 current.Length

            previous[right.Length]

    let private suggestAttribute (known: Set<string>) (attrName: string) =
        let attrNorm = normalizeAttrName attrName

        if attrNorm = "" then
            None
        else
            let attrLower = attrName.ToLowerInvariant()
            let attrTokens = tokenizeAttrName attrName

            known
            |> Seq.map (fun candidate ->
                let candidateNorm = normalizeAttrName candidate
                let candidateTokens = tokenizeAttrName candidate
                let distance = levenshteinDistance attrNorm candidateNorm
                let mutable bonus = 0

                if attrLower.Contains(candidate.ToLowerInvariant()) then
                    bonus <- bonus + 2

                if attrTokens.Length > 0 && candidateTokens.Length > 0 then
                    if attrTokens[0] = candidateTokens[0] then
                        bonus <- bonus + 1

                    if attrTokens[attrTokens.Length - 1] = candidateTokens[candidateTokens.Length - 1] then
                        bonus <- bonus + 2

                let score = distance - bonus
                (candidate, score, distance))
            |> Seq.sortBy (fun (_, score, distance) -> score, distance)
            |> Seq.tryHead
            |> Option.bind (fun (candidate, score, _) ->
                let threshold = max 2 (attrNorm.Length / 2)
                if score <= threshold then Some candidate else None)

    let private attributeMessage (subject: string) (attrName: string) (suggestion: string option) =
        match suggestion with
        | Some candidate -> $"{subject} has unrecognized attribute '{attrName}' (did you mean '{candidate}'?)"
        | None -> $"{subject} has unrecognized attribute '{attrName}'"

    /// Rule: warn on unrecognized node/edge/graph attribute names
    let attributeKnownRule: ILintRule =
        let nodeKnown = Set.union KnownAttributes.node KnownAttributes.graphvizPassthrough
        let edgeKnown = Set.union KnownAttributes.edge KnownAttributes.graphvizPassthrough
        let graphKnown = Set.union KnownAttributes.graph KnownAttributes.graphvizPassthrough

        { new ILintRule with
            member _.Name = "attribute_known"

            member _.Apply(graph) =
                let nodeDiags =
                    graph.Nodes
                    |> Map.toList
                    |> List.collect (fun (_, node) ->
                        node.Attributes
                        |> Map.toList
                        |> List.choose (fun (attrName, _) ->
                            if nodeKnown.Contains(attrName) then
                                None
                            else
                                let suggestion = suggestAttribute nodeKnown attrName

                                Some(
                                    Diagnostic.Warning(
                                        "attribute_known",
                                        attributeMessage $"Node '{node.Id}'" attrName suggestion,
                                        nodeId = node.Id
                                    )
                                )))

                let edgeDiags =
                    graph.Edges
                    |> List.collect (fun edge ->
                        edge.Attributes
                        |> Map.toList
                        |> List.choose (fun (attrName, _) ->
                            if edgeKnown.Contains(attrName) then
                                None
                            else
                                let suggestion = suggestAttribute edgeKnown attrName

                                Some(
                                    Diagnostic.Warning(
                                        "attribute_known",
                                        attributeMessage $"Edge {edge.FromNode}->{edge.ToNode}" attrName suggestion,
                                        edge = (edge.FromNode, edge.ToNode)
                                    )
                                )))

                // Graph-level unknown attrs are Info, not Warning: the tool-command
                // handler exposes every graph attribute as an env var, so authors
                // legitimately declare custom pipeline parameters at this level
                // (e.g. `planning_dir`, `target_package`). Node/edge typos still
                // warn because those have structural runtime effects.
                let graphDiags =
                    graph.GraphAttributes
                    |> Map.toList
                    |> List.choose (fun (attrName, _) ->
                        if graphKnown.Contains(attrName) then
                            None
                        else
                            let suggestion = suggestAttribute graphKnown attrName
                            Some(Diagnostic.Info("attribute_known", attributeMessage "Graph" attrName suggestion)))

                nodeDiags @ edgeDiags @ graphDiags }

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

                if terminalIds.IsEmpty then
                    [] // terminal_node rule handles this
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
                        if canReachTerminal.Contains(id) then
                            None
                        else
                            Some(
                                Diagnostic.Error(
                                    "terminal_reachability",
                                    $"Node '{id}' has no path to any terminal node — pipeline will hang",
                                    nodeId = id,
                                    fix = "Add an edge path from this node to an exit node"
                                )
                            )) }

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
                            Some(
                                Diagnostic.Error(
                                    "dead_end",
                                    $"Node '{id}' has no outgoing edges and is not a terminal node — pipeline will hang",
                                    nodeId = id,
                                    fix = "Add an edge from this node to the next stage or exit"
                                )
                            )
                        else
                            None
                    else
                        None) }

    /// Known LLM CLI names that should not be invoked from parallelogram (tool) nodes.
    /// These should use box (codergen) or tab (coding_agent) nodes instead, which provide
    /// session management, turn limits, structured outcome parsing, and retry handling.
    let private llmCliPatterns =
        [ "claude"; "codex"; "gemini"; "aider"; "cursor"; "opencode" ]

    /// Generate a pipeline synopsis (informational diagnostics describing capabilities)
    let generateSynopsis (graph: Graph) : Diagnostic list =
        let nodes = graph.Nodes |> Map.toList |> List.map snd

        let handlerTypes =
            nodes |> List.map ShapeMapping.resolveHandlerType |> List.distinct

        let hasToolNodes =
            nodes
            |> List.exists (fun n ->
                ShapeMapping.resolveHandlerType n = "tool"
                && n.GetAttrString("tool_command", "") <> "")

        let hasCodegenAgent =
            nodes
            |> List.exists (fun n ->
                let handlerType = ShapeMapping.resolveHandlerType n

                if handlerType = "tool" then
                    let cmd = n.GetAttrString("tool_command", "")
                    llmCliPatterns |> List.exists (fun (cli: string) -> cmd.Contains(cli))
                elif handlerType = "coding_agent" then
                    true
                elif handlerType = "codergen" then
                    let model = n.LlmModel
                    model.Contains("codex") || model.Contains("o3") || model.Contains("o4")
                else
                    false)

        let hasLlmNodes =
            nodes
            |> List.exists (fun n ->
                let handlerType = ShapeMapping.resolveHandlerType n
                handlerType = "codergen" || handlerType = "coding_agent")

        let hasHumanGates =
            nodes |> List.exists (fun n -> ShapeMapping.resolveHandlerType n = "wait.human")

        let hasGoalGates = nodes |> List.exists (fun n -> n.GoalGate)

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

                graph.Edges
                |> List.exists (fun e ->
                    match order.TryGetValue(e.FromNode), order.TryGetValue(e.ToNode) with
                    | (true, fromIdx), (true, toIdx) -> toIdx < fromIdx
                    | _ -> false)

        let hasConditionals =
            nodes
            |> List.exists (fun n -> ShapeMapping.resolveHandlerType n = "conditional")
            || graph.Edges |> List.exists (fun e -> e.Condition <> "")

        let hasLoopRestart = graph.Edges |> List.exists (fun e -> e.LoopRestart)

        let hasStylesheet = graph.ModelStylesheet <> ""

        let willProduceCodeChanges = hasCodegenAgent
        let willCallLlm = hasLlmNodes
        let willRunCommands = hasToolNodes

        let isPlanningOnly =
            willCallLlm && not willRunCommands && not willProduceCodeChanges

        let isExecutionPipeline = willProduceCodeChanges

        let diags = ResizeArray<Diagnostic>()

        // Pipeline type classification
        if isExecutionPipeline then
            diags.Add(
                Diagnostic.Info(
                    "synopsis",
                    "EXECUTION pipeline — will invoke coding agents and run commands to produce code changes"
                )
            )
        elif isPlanningOnly then
            diags.Add(
                Diagnostic.Info(
                    "synopsis",
                    "PLANNING pipeline — generates plans/docs via LLM but does NOT execute code changes"
                )
            )
        elif willRunCommands then
            diags.Add(
                Diagnostic.Info(
                    "synopsis",
                    "HYBRID pipeline — runs commands and LLM calls but no coding agent detected"
                )
            )
        else
            diags.Add(Diagnostic.Info("synopsis", "ANALYSIS pipeline — LLM-only, no tool commands or code changes"))

        // Capability flags
        let flags = ResizeArray<string>()

        if willCallLlm then
            flags.Add("LLM")

        if willRunCommands then
            flags.Add("TOOLS")

        if willProduceCodeChanges then
            flags.Add("CODE_CHANGES")

        if hasHumanGates then
            flags.Add("HUMAN_GATES")

        if hasGoalGates then
            flags.Add("GOAL_GATES")

        if hasParallel then
            flags.Add("PARALLEL")

        if hasFeedbackLoops then
            flags.Add("FEEDBACK_LOOPS")

        if hasConditionals then
            flags.Add("CONDITIONALS")

        if hasLoopRestart then
            flags.Add("LOOP_RESTART")

        if hasStylesheet then
            flags.Add("MODEL_STYLESHEET")

        diags.Add(Diagnostic.Info("synopsis", sprintf "Capabilities: [%s]" (flags |> String.concat " | ")))

        // Node breakdown
        let codergenCount =
            nodes
            |> List.filter (fun n ->
                let handlerType = ShapeMapping.resolveHandlerType n
                handlerType = "codergen" || handlerType = "coding_agent")
            |> List.length

        let toolCount =
            nodes
            |> List.filter (fun n -> ShapeMapping.resolveHandlerType n = "tool")
            |> List.length

        let humanCount =
            nodes
            |> List.filter (fun n -> ShapeMapping.resolveHandlerType n = "wait.human")
            |> List.length

        let conditionalCount =
            nodes
            |> List.filter (fun n -> ShapeMapping.resolveHandlerType n = "conditional")
            |> List.length

        let parallelCount =
            nodes
            |> List.filter (fun n -> ShapeMapping.resolveHandlerType n = "parallel")
            |> List.length

        diags.Add(
            Diagnostic.Info(
                "synopsis",
                sprintf
                    "Stages: %d LLM, %d tool, %d human, %d conditional, %d parallel"
                    codergenCount
                    toolCount
                    humanCount
                    conditionalCount
                    parallelCount
            )
        )

        // Warnings for common misconfigurations
        if isPlanningOnly then
            diags.Add(
                Diagnostic.Warning(
                    "synopsis",
                    "This pipeline has no tool nodes — it will generate LLM output but will NOT make any code changes or run any commands",
                    fix = "Add parallelogram (tool) nodes with tool_command to execute work, or invoke a coding agent"
                )
            )

        if willRunCommands && not willProduceCodeChanges && not isPlanningOnly then
            diags.Add(
                Diagnostic.Warning(
                    "synopsis",
                    "This pipeline runs tool commands but no coding agent was detected — tool outputs are captured but no code is written",
                    fix = "Add a tool node that invokes 'claude --auto' or 'codex exec' to implement changes"
                )
            )

        if not hasHumanGates && not hasFeedbackLoops then
            diags.Add(Diagnostic.Info("synopsis", "No human gates or feedback loops — pipeline runs straight through"))

        if not hasGoalGates && hasLlmNodes then
            diags.Add(
                Diagnostic.Info("synopsis", "No goal gates — pipeline will exit regardless of LLM outcome quality")
            )

        diags |> Seq.toList

    /// Rule: Warn when cumulative max_turns on a linear chain of coding_agent nodes
    /// exceeds a threshold without thread_id to isolate sessions
    let cumulativeTurnsRule: ILintRule =
        let threshold = 60
        let defaultMaxTurns = 20

        { new ILintRule with
            member _.Name = "cumulative_turns"

            member _.Apply(graph) =
                let agentTypes = set [ "coding_agent" ]

                let isAgent (node: Node) =
                    agentTypes.Contains(ShapeMapping.resolveHandlerType node)

                let getMaxTurns (node: Node) =
                    node.GetAttr("max_turns")
                    |> Option.bind (fun v -> v.AsInt())
                    |> Option.defaultValue defaultMaxTurns

                match graph.FindStartNode() with
                | None -> []
                | Some startNode ->
                    let diags = Dictionary<string, Diagnostic>()
                    // BFS tracking cumulative turns per thread along each path.
                    // Cap cumulative at 2x threshold so cycles that include a
                    // coding_agent terminate: once cumulative exceeds the cap,
                    // the warning has already fired for downstream agent nodes
                    // and further propagation adds no new diagnostics.
                    let cap = threshold * 2
                    let visited = Dictionary<string, int>()
                    let queue = Queue<string * int>()
                    queue.Enqueue(startNode.Id, 0)

                    while queue.Count > 0 do
                        let (nodeId, cumulativePrior) = queue.Dequeue()

                        match graph.Nodes |> Map.tryFind nodeId with
                        | None -> ()
                        | Some node ->
                            let rawCumulative =
                                if isAgent node then
                                    // If node has its own thread_id, it gets a fresh session
                                    if node.ThreadId <> "" then
                                        getMaxTurns node
                                    else
                                        cumulativePrior + getMaxTurns node
                                else
                                    cumulativePrior

                            let cumulative = min rawCumulative cap
                            // Only propagate when cumulative grows for this node.
                            // `cap` prevents unbounded growth across retry cycles.
                            let shouldProcess =
                                match visited.TryGetValue(nodeId) with
                                | true, prev -> cumulative > prev
                                | false, _ -> true

                            if shouldProcess then
                                visited[nodeId] <- cumulative

                                // Emit at most one cumulative_turns warning per node;
                                // a retry cycle would otherwise produce one per pass.
                                if
                                    isAgent node
                                    && node.ThreadId = ""
                                    && cumulativePrior >= threshold
                                    && not (diags.ContainsKey(nodeId))
                                then
                                    diags[nodeId] <-
                                        Diagnostic.Warning(
                                            "cumulative_turns",
                                            $"Node '{nodeId}' starts at ~{cumulativePrior} cumulative turns on the shared session; add thread_id to give it a fresh session",
                                            nodeId = nodeId,
                                            fix = $"Add thread_id attribute to node '{nodeId}'"
                                        )

                                for edge in graph.OutgoingEdges(nodeId) do
                                    queue.Enqueue(edge.ToNode, cumulative)

                    diags.Values |> Seq.toList }

    /// Rule: Warn when coding_agent/tab nodes have very low max_turns
    let lowMaxTurnsRule: ILintRule =
        let minRecommended = 15

        { new ILintRule with
            member _.Name = "low_max_turns"

            member _.Apply(graph) =
                let agentTypes = set [ "coding_agent" ]

                graph.Nodes
                |> Map.toList
                |> List.choose (fun (_, node) ->
                    let handlerType = ShapeMapping.resolveHandlerType node

                    if agentTypes.Contains(handlerType) then
                        match node.GetAttr("max_turns") |> Option.bind (fun v -> v.AsInt()) with
                        | Some turns when turns < minRecommended ->
                            Some(
                                Diagnostic.Warning(
                                    "low_max_turns",
                                    $"Node '{node.Id}' has max_turns={turns} which is likely too low for a coding agent; agents typically need 20-50 turns for diagnosis and fixing",
                                    nodeId = node.Id,
                                    fix = $"Set max_turns to at least {minRecommended} (recommended: 30-50)"
                                )
                            )
                        | _ -> None
                    else
                        None) }

    /// Rule: Warn when parallelogram (tool) nodes have conditional edges for only one outcome
    let parallelogramOutcomeRoutingRule: ILintRule =
        { new ILintRule with
            member _.Name = "parallelogram_outcome_routing"

            member _.Apply(graph) =
                graph.Nodes
                |> Map.toList
                |> List.choose (fun (_, node) ->
                    if ShapeMapping.resolveHandlerType node = "tool" then
                        let outgoing = graph.OutgoingEdges(node.Id)
                        let conditionEdges = outgoing |> List.filter (fun e -> e.Condition <> "")
                        // An unconditional edge catches whatever the conditional edges don't,
                        // so we don't need to warn about a "missing" outcome in that case.
                        let hasFallback = outgoing |> List.exists (fun e -> e.Condition = "")

                        if conditionEdges.IsEmpty || hasFallback then
                            None
                        else
                            let hasSuccess =
                                conditionEdges
                                |> List.exists (fun e ->
                                    e.Condition.Contains("outcome=success") || e.Condition.Contains("outcome!=fail"))

                            let hasFail =
                                conditionEdges
                                |> List.exists (fun e ->
                                    e.Condition.Contains("outcome=fail") || e.Condition.Contains("outcome!=success"))

                            if hasSuccess && hasFail then
                                None
                            elif hasSuccess then
                                Some(
                                    Diagnostic.Warning(
                                        "parallelogram_outcome_routing",
                                        $"Tool node '{node.Id}' routes on outcome=success but has no edge for outcome=fail; non-zero exit codes will have no route",
                                        nodeId = node.Id,
                                        fix = "Add an edge with condition=\"outcome=fail\" for the failure path"
                                    )
                                )
                            elif hasFail then
                                Some(
                                    Diagnostic.Warning(
                                        "parallelogram_outcome_routing",
                                        $"Tool node '{node.Id}' routes on outcome=fail but has no edge for outcome=success; zero exit codes will have no route",
                                        nodeId = node.Id,
                                        fix = "Add an edge with condition=\"outcome=success\" for the success path"
                                    )
                                )
                            else
                                None // has conditions but not outcome-based; other rules handle this
                    else
                        None) }

    /// Rule: Warn when parallelogram (tool) nodes invoke LLM CLIs directly via tool_command.
    /// LLM invocations from tool nodes bypass session management, turn limits, loop detection,
    /// tool exclusion, and structured outcome routing. Use a codergen or coding_agent node instead.
    let toolNodeLlmInvocationRule: ILintRule =
        { new ILintRule with
            member _.Name = "tool_node_llm_invocation"

            member _.Apply(graph) =
                graph.Nodes
                |> Map.toList
                |> List.choose (fun (_, node) ->
                    if ShapeMapping.resolveHandlerType node = "tool" then
                        let cmd = node.GetAttrString("tool_command", "")

                        if cmd <> "" then
                            llmCliPatterns
                            |> List.tryFind (fun cli -> cmd.Contains(cli))
                            |> Option.map (fun matched ->
                                Diagnostic.Warning(
                                    "tool_node_llm_invocation",
                                    $"Tool node '{node.Id}' invokes '{matched}' via tool_command — LLM invocations from parallelogram nodes bypass session management, turn limits, and structured outcome routing",
                                    nodeId = node.Id,
                                    fix =
                                        $"Use a box (codergen) or tab (coding_agent) node instead; for ACP agents, set acp_preset=\"{matched}\" on a codergen node"
                                ))
                        else
                            None
                    else
                        None) }

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
          modelKnownRule
          typeKnownRule
          managerLoopChildDotfileRule
          fidelityValidRule
          retryTargetExistsRule
          goalGateHasRetryRule
          retryTargetCycleRule
          promptOnLlmNodesRule
          maxVisitsRule
          attributeKnownRule
          cumulativeTurnsRule
          lowMaxTurnsRule
          parallelogramOutcomeRoutingRule
          toolNodeLlmInvocationRule ]

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
