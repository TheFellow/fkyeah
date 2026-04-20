namespace Attractor

/// Transform interface for modifying the graph between parsing and validation
type ITransform =
    abstract member Apply: Graph -> Graph

module Transforms =

    /// Variable expansion transform: replaces $goal in prompts
    let variableExpansion: ITransform =
        { new ITransform with
            member _.Apply(graph) =
                let goal = graph.Goal

                let updatedNodes =
                    graph.Nodes
                    |> Map.map (fun _ node ->
                        if node.Prompt.Contains("$goal") then
                            let newPrompt = node.Prompt.Replace("$goal", goal)

                            { node with
                                Attributes = node.Attributes |> Map.add "prompt" (AttrValue.String newPrompt) }
                        else
                            node)

                { graph with Nodes = updatedNodes } }

    /// Agent-attribute keys whose presence on a box (codergen) node signals that the
    /// pipeline author intended a full coding agent session, not a single-turn LLM call.
    let private agentPromotionAttributes =
        Set.ofList
            [ "max_turns"
              "max_tool_rounds"
              "thread_id"
              "cwd"
              "command_timeout"
              "system_prompt" ]

    /// Auto-promotion transform: box nodes with agent-specific attributes are promoted
    /// to coding_agent handler type. This bridges compatibility with pipelines authored
    /// for backends where box nodes have full agent capabilities (e.g. Swift OmniKit).
    let codergenAutoPromotion: ITransform =
        { new ITransform with
            member _.Apply(graph) =
                let updatedNodes =
                    graph.Nodes
                    |> Map.map (fun _ node ->
                        let handlerType = ShapeMapping.resolveHandlerType node

                        if handlerType = "codergen" && node.NodeType = "" then
                            let hasAgentAttr =
                                node.Attributes |> Map.exists (fun k _ -> agentPromotionAttributes.Contains(k))

                            if hasAgentAttr then
                                { node with
                                    Attributes = node.Attributes |> Map.add "type" (AttrValue.String "coding_agent") }
                            else
                                node
                        else
                            node)

                { graph with Nodes = updatedNodes } }

    /// Stylesheet application transform
    let stylesheetApplication: ITransform =
        { new ITransform with
            member _.Apply(graph) =
                let ss = graph.ModelStylesheet

                if ss = "" then
                    graph
                else
                    match Stylesheet.parse ss with
                    | Ok parsed -> Stylesheet.apply parsed graph
                    | Error _ -> graph }

    /// Built-in transforms in application order
    let builtInTransforms: ITransform list =
        [ variableExpansion; codergenAutoPromotion; stylesheetApplication ]

    /// Apply all transforms to a graph
    let applyAll (transforms: ITransform list) (graph: Graph) : Graph =
        transforms |> List.fold (fun g t -> t.Apply(g)) graph

    /// Prepare a pipeline: parse, transform, validate
    let preparePipeline (source: string) (extraTransforms: ITransform list option) =
        let graph = DotParser.parseOrRaise source

        let transforms =
            match extraTransforms with
            | Some extra -> builtInTransforms @ extra
            | None -> builtInTransforms

        let transformed = applyAll transforms graph
        let diagnostics = Validation.validate transformed None
        (transformed, diagnostics)
