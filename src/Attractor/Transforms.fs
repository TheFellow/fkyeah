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

    /// Stylesheet application transform
    let stylesheetApplication: ITransform =
        { new ITransform with
            member _.Apply(graph) =
                let ss = graph.ModelStylesheet
                if ss = "" then graph
                else
                    match Stylesheet.parse ss with
                    | Ok parsed -> Stylesheet.apply parsed graph
                    | Error _ -> graph }

    /// Built-in transforms in application order
    let builtInTransforms: ITransform list =
        [ variableExpansion
          stylesheetApplication ]

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
