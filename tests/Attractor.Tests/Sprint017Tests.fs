module Sprint017Tests

open System
open System.IO
open Xunit
open Attractor

let private createTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), $"attractor-sprint017-{Guid.NewGuid():N}")

    Directory.CreateDirectory(dir) |> ignore
    dir

let private makeNode (id: string) = { Id = id; Attributes = Map.empty }

let private makeEdge (fromNode: string) (toNode: string) (attrs: (string * AttrValue) list) =
    { FromNode = fromNode
      ToNode = toNode
      Attributes = Map.ofList attrs }

module SelectAllMatchingEdgesTests =

    [<Fact>]
    let ``selectAllMatchingEdges returns all condition matches`` () =
        let node = makeNode "A"

        let graph =
            { Name = "t"
              Nodes =
                Map.ofList
                    [ "A", node
                      "B", makeNode "B"
                      "C", makeNode "C"
                      "D", makeNode "D" ]
              Edges =
                [ makeEdge "A" "B" [ "condition", AttrValue.String "outcome=needs_dod" ]
                  makeEdge "A" "C" [ "condition", AttrValue.String "outcome=needs_dod" ]
                  makeEdge "A" "D" [ "condition", AttrValue.String "outcome=needs_dod" ] ]
              GraphAttributes = Map.empty }

        let outcome =
            { Outcome.Success() with
                RawOutcome = Some "needs_dod" }

        let edges = EdgeSelection.selectAllMatchingEdges node outcome (Context()) graph
        Assert.Equal<string list>([ "B"; "C"; "D" ], edges |> List.map (fun e -> e.ToNode))

    [<Fact>]
    let ``selectAllMatchingEdges returns singleton when one condition matches`` () =
        let node = makeNode "A"

        let graph =
            { Name = "t"
              Nodes = Map.ofList [ "A", node; "B", makeNode "B"; "C", makeNode "C" ]
              Edges =
                [ makeEdge "A" "B" [ "condition", AttrValue.String "outcome=needs_dod" ]
                  makeEdge "A" "C" [ "condition", AttrValue.String "outcome=fail" ] ]
              GraphAttributes = Map.empty }

        let outcome =
            { Outcome.Success() with
                RawOutcome = Some "needs_dod" }

        let edges = EdgeSelection.selectAllMatchingEdges node outcome (Context()) graph
        Assert.Equal<string list>([ "B" ], edges |> List.map (fun e -> e.ToNode))

    [<Fact>]
    let ``selectAllMatchingEdges returns all unconditional edges when count is at least two`` () =
        let node = makeNode "A"

        let graph =
            { Name = "t"
              Nodes =
                Map.ofList
                    [ "A", node
                      "B", makeNode "B"
                      "C", makeNode "C"
                      "D", makeNode "D" ]
              Edges =
                [ makeEdge "A" "B" [ "condition", AttrValue.String "outcome=fail" ]
                  makeEdge "A" "C" []
                  makeEdge "A" "D" [] ]
              GraphAttributes = Map.empty }

        let edges = EdgeSelection.selectAllMatchingEdges node (Outcome.Success()) (Context()) graph
        Assert.Equal<string list>([ "C"; "D" ], edges |> List.map (fun e -> e.ToNode))

    [<Fact>]
    let ``selectAllMatchingEdges returns single unconditional via fallback`` () =
        let node = makeNode "A"

        let graph =
            { Name = "t"
              Nodes = Map.ofList [ "A", node; "B", makeNode "B"; "C", makeNode "C" ]
              Edges =
                [ makeEdge "A" "B" [ "condition", AttrValue.String "outcome=fail" ]
                  makeEdge "A" "C" [] ]
              GraphAttributes = Map.empty }

        let edges = EdgeSelection.selectAllMatchingEdges node (Outcome.Success()) (Context()) graph
        Assert.Equal<string list>([ "C" ], edges |> List.map (fun e -> e.ToNode))

    [<Fact>]
    let ``selectAllMatchingEdges falls back to preferred label selection`` () =
        let node = makeNode "A"

        let outcome =
            { Outcome.Success() with
                PreferredLabel = "Fix" }

        let graph =
            { Name = "t"
              Nodes = Map.ofList [ "A", node; "B", makeNode "B"; "C", makeNode "C" ]
              Edges =
                [ makeEdge "A" "B" [ "label", AttrValue.String "Fix" ]
                  makeEdge "A" "C" [ "condition", AttrValue.String "outcome=fail" ] ]
              GraphAttributes = Map.empty }

        let edges = EdgeSelection.selectAllMatchingEdges node outcome (Context()) graph
        Assert.Equal<string list>([ "B" ], edges |> List.map (fun e -> e.ToNode))

module EngineFanoutTests =

    let private handlerWithOutcomes (executed: ResizeArray<string>) (outcomes: Map<string, Outcome>) =
        { new IHandler with
            member _.Execute(node, _, _, _) =
                executed.Add(node.Id)
                outcomes |> Map.tryFind node.Id |> Option.defaultValue (Outcome.Success()) }

    [<Fact>]
    let ``Engine runs multi-condition fan-out sequentially then fan-in`` () =
        let dot =
            """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom"]
            B [type="custom"]
            C [type="custom"]
            D [type="custom"]
            E [type="custom"]
            start -> A
            A -> B [condition="outcome=needs_dod"]
            A -> C [condition="outcome=needs_dod"]
            A -> D [condition="outcome=needs_dod"]
            B -> E
            C -> E
            D -> E
            E -> exit
        }
        """

        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir ()
        let executed = ResizeArray<string>()
        let registry = HandlerRegistry.CreateDefault()

        let outcomes =
            Map.ofList
                [ "A",
                  { Outcome.Success() with
                      RawOutcome = Some "needs_dod" } ]

        registry.Register("custom", handlerWithOutcomes executed outcomes)

        let result =
            Engine.run
                graph
                { RunConfig.Default(logsRoot) with
                    Registry = registry }

        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal<string list>([ "A"; "B"; "C"; "D"; "E" ], executed |> Seq.toList)

    [<Fact>]
    let ``Engine runs multi-unconditional fan-out sequentially then fan-in`` () =
        let dot =
            """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom"]
            B [type="custom"]
            C [type="custom"]
            D [type="custom"]
            start -> A
            A -> B
            A -> C
            B -> D
            C -> D
            D -> exit
        }
        """

        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir ()
        let executed = ResizeArray<string>()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", handlerWithOutcomes executed Map.empty)

        let result =
            Engine.run
                graph
                { RunConfig.Default(logsRoot) with
                    Registry = registry }

        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal<string list>([ "A"; "B"; "C"; "D" ], executed |> Seq.toList)

    [<Fact>]
    let ``loop_restart on fan-out edge is ignored`` () =
        let dot =
            """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom"]
            B [type="custom"]
            C [type="custom"]
            D [type="custom"]
            start -> A
            A -> B [condition="outcome=needs_dod", loop_restart=true]
            A -> C [condition="outcome=needs_dod"]
            B -> D
            C -> D
            D -> exit
        }
        """

        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir ()
        let executed = ResizeArray<string>()
        let registry = HandlerRegistry.CreateDefault()

        let outcomes =
            Map.ofList
                [ "A",
                  { Outcome.Success() with
                      RawOutcome = Some "needs_dod" } ]

        registry.Register("custom", handlerWithOutcomes executed outcomes)

        let result =
            Engine.run
                graph
                { RunConfig.Default(logsRoot) with
                    Registry = registry }

        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Contains("B", executed)
        Assert.Contains("C", executed)
        Assert.Contains("D", executed)
        Assert.False(Directory.Exists(Path.Combine(logsRoot, "restart-1")))

    [<Fact>]
    let ``resume mid-fan-out skips completed branch and executes remaining branches`` () =
        let dot =
            """
        digraph Test {
            graph [goal="resume-fanout"]
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [type="custom"]
            B [type="custom"]
            C [type="custom"]
            D [type="custom"]
            E [type="custom"]
            start -> A
            A -> B [condition="outcome=needs_dod"]
            A -> C [condition="outcome=needs_dod"]
            A -> D [condition="outcome=needs_dod"]
            B -> E
            C -> E
            D -> E
            E -> exit
        }
        """

        let graph = DotParser.parseOrRaise dot
        let logsRoot = createTempDir ()
        let executed = ResizeArray<string>()
        let registry = HandlerRegistry.CreateDefault()

        let outcomes =
            Map.ofList
                [ "A",
                  { Outcome.Success() with
                      RawOutcome = Some "needs_dod" } ]

        registry.Register("custom", handlerWithOutcomes executed outcomes)

        let checkpoint =
            { Timestamp = DateTimeOffset.UtcNow
              CurrentNode = "A"
              CompletedNodes = [ "start"; "A"; "B" ]
              NodeRetries = Map.empty
              NodeOutcomes =
                Map.ofList
                    [ "start", Outcome.Success()
                      "A",
                      { Outcome.Success() with
                          RawOutcome = Some "needs_dod" }
                      "B", Outcome.Success() ]
              ContextValues = Map.ofList [ "graph.goal", "resume-fanout"; "outcome", "success" ]
              Logs = [] }

        let result =
            Engine.resumeFromCheckpoint
                graph
                { RunConfig.Default(logsRoot) with
                    Registry = registry }
                checkpoint

        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.DoesNotContain("A", executed)
        Assert.DoesNotContain("B", executed)
        Assert.Equal<string list>([ "C"; "D"; "E" ], executed |> Seq.toList)

module ValidationFanoutTests =

    [<Fact>]
    let ``fanout_fan_in_ambiguous warning emits when first successors diverge`` () =
        let dot =
            """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box]
            B [shape=box]
            C [shape=box]
            E [shape=box]
            F [shape=box]
            start -> A
            A -> B [condition="outcome=needs_dod"]
            A -> C [condition="outcome=needs_dod"]
            B -> E
            C -> F
            E -> exit
            F -> exit
        }
        """

        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None

        Assert.True(
            diags
            |> List.exists (fun d -> d.Rule = "fanout_fan_in_ambiguous" && d.NodeId = "A")
        )

    [<Fact>]
    let ``fanout_fan_in_ambiguous warning does not emit when first successors converge`` () =
        let dot =
            """
        digraph Test {
            start [shape=Mdiamond]
            exit [shape=Msquare]
            A [shape=box]
            B [shape=box]
            C [shape=box]
            E [shape=box]
            start -> A
            A -> B [condition="outcome=needs_dod"]
            A -> C [condition="outcome=needs_dod"]
            B -> E
            C -> E
            E -> exit
        }
        """

        let graph = DotParser.parseOrRaise dot
        let diags = Validation.validate graph None

        Assert.False(diags |> List.exists (fun d -> d.Rule = "fanout_fan_in_ambiguous"))

module ConditionsValidationTests =

    [<Fact>]
    let ``Conditions.validate explicitly accepts double equals`` () =
        Assert.True((Conditions.validate "outcome == \"needs_dod\"").IsOk)
