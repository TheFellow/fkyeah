module Sprint014Tests

open System
open System.IO
open Xunit
open Attractor

let private createTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), $"attractor-sprint014-{Guid.NewGuid():N}")

    Directory.CreateDirectory(dir) |> ignore
    dir

module InitialContextTests =

    [<Fact>]
    let ``InitialContext seeds fresh runs and overrides graph defaults`` () =
        let mutable seenSeed = ""
        let mutable seenGoal = ""

        let handler =
            { new IHandler with
                member _.Execute(_, context, _, _) =
                    seenSeed <- context.Get("seed")
                    seenGoal <- context.Get("graph.goal")
                    Outcome.Success() }

        let graph =
            DotParser.parseOrRaise
                """
            digraph Test {
                graph [goal="from-graph"]
                start [shape=Mdiamond]
                exit [shape=Msquare]
                worker [type="custom"]
                start -> worker -> exit
            }
            """

        let logsRoot = createTempDir ()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", handler)

        let config =
            { RunConfig.Default(logsRoot) with
                Registry = registry
                InitialContextValues = Map.ofList [ "seed", "hello"; "graph.goal", "overridden" ] }

        let result = Engine.run graph config

        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal("hello", seenSeed)
        Assert.Equal("overridden", seenGoal)
        Assert.Equal("hello", result.Context.Get("seed"))

    [<Fact>]
    let ``InitialContext is reapplied on loop restart`` () =
        let mutable calls = 0
        let mutable secondPassSeed = ""

        let handlerA =
            { new IHandler with
                member _.Execute(_, context, _, _) =
                    calls <- calls + 1

                    if calls > 1 then
                        secondPassSeed <- context.Get("seed")
                        Outcome.Success(contextUpdates = Map.ofList [ "loop_done", "true" ])
                    else
                        Outcome.Success() }

        let handlerB =
            { new IHandler with
                member _.Execute(_, _, _, _) = Outcome.Success() }

        let graph =
            DotParser.parseOrRaise
                """
            digraph Test {
                graph [goal="restart"]
                start [shape=Mdiamond]
                exit [shape=Msquare]
                A [type="ha"]
                B [type="hb"]
                start -> A -> B
                B -> A [loop_restart=true]
                B -> exit [condition="context.loop_done=true"]
            }
            """

        let logsRoot = createTempDir ()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("ha", handlerA)
        registry.Register("hb", handlerB)

        let config =
            { RunConfig.Default(logsRoot) with
                Registry = registry
                InitialContextValues = Map.ofList [ "seed", "persisted" ] }

        let result = Engine.run graph config

        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal("persisted", secondPassSeed)
        Assert.Equal("persisted", result.Context.Get("seed"))

    [<Fact>]
    let ``InitialContext fills missing keys only on checkpoint resume`` () =
        let graph =
            DotParser.parseOrRaise
                """
            digraph Test {
                graph [goal="resume"]
                start [shape=Mdiamond]
                exit [shape=Msquare]
                A [shape=box, prompt="Step A"]
                start -> A -> exit
            }
            """

        let checkpoint =
            { Timestamp = DateTimeOffset.UtcNow
              CurrentNode = "A"
              CompletedNodes = [ "start"; "A" ]
              NodeRetries = Map.empty
              NodeOutcomes = Map.ofList [ "start", Outcome.Success(); "A", Outcome.Success() ]
              ContextValues = Map.ofList [ "graph.goal", "resume"; "seed", "existing" ]
              Logs = [] }

        let logsRoot = createTempDir ()

        let config =
            { RunConfig.Default(logsRoot) with
                InitialContextValues = Map.ofList [ "seed", "new"; "extra", "added" ] }

        let result = Engine.resumeFromCheckpoint graph config checkpoint

        Assert.Equal("existing", result.Context.Get("seed"))
        Assert.Equal("added", result.Context.Get("extra"))

module ParallelQualificationTests =

    [<Fact>]
    let ``Parallel handler writes qualified keys and lane summary`` () =
        let branchHandler =
            { new IHandler with
                member _.Execute(node, _, _, _) =
                    Outcome.Success(contextUpdates = Map.ofList [ "result_key", node.Id ]) }

        let graph =
            DotParser.parseOrRaise
                """
            digraph Test {
                graph [default_fidelity="full"]
                start [shape=Mdiamond]
                exit [shape=Msquare]
                fan_out [shape=component]
                A [type="branch", lane="alpha"]
                B [type="branch", lane="beta"]
                join [shape=tripleoctagon]
                start -> fan_out
                fan_out -> A
                fan_out -> B
                A -> join
                B -> join
                join -> exit
            }
            """

        let logsRoot = createTempDir ()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("branch", branchHandler)
        registry.Register("parallel", Handlers.ParallelHandler(resolveHandler = registry.Resolve))

        let config =
            { RunConfig.Default(logsRoot) with
                Registry = registry }

        let result = Engine.run graph config
        let raw = result.Context.Get("result_key")

        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal("A", result.Context.Get("parallel.fan_out.A.result_key"))
        Assert.Equal("B", result.Context.Get("parallel.fan_out.B.result_key"))
        Assert.Contains(raw, [| "A"; "B" |])
        Assert.Equal("alpha,beta", result.Context.Get("parallel.fan_out.lanes"))

module ManagerLoopLaneTests =

    [<Fact>]
    let ``Manager loop polling mode propagates lane`` () =
        let handler = Handlers.ManagerLoopHandler() :> IHandler

        let node =
            { Id = "manager"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "house"
                      "max_cycles", AttrValue.Integer 1
                      "lane", AttrValue.String "deploy" ] }

        let outcome =
            handler.Execute(
                node,
                Context(),
                { Name = "test"
                  Nodes = Map.empty
                  Edges = []
                  GraphAttributes = Map.empty },
                createTempDir ()
            )

        Assert.True(outcome.ContextUpdates.ContainsKey("manager.lane"))
        Assert.Equal("deploy", outcome.ContextUpdates["manager.lane"])

    [<Fact>]
    let ``Manager loop polling mode omits lane when unset`` () =
        let handler = Handlers.ManagerLoopHandler() :> IHandler

        let node =
            { Id = "manager"
              Attributes = Map.ofList [ "shape", AttrValue.String "house"; "max_cycles", AttrValue.Integer 1 ] }

        let outcome =
            handler.Execute(
                node,
                Context(),
                { Name = "test"
                  Nodes = Map.empty
                  Edges = []
                  GraphAttributes = Map.empty },
                createTempDir ()
            )

        Assert.False(outcome.ContextUpdates.ContainsKey("manager.lane"))

module HumanMetadataTests =

    [<Fact>]
    let ``WaitForHuman includes node_id attr metadata and preserves existing keys`` () =
        let mutable metadata = Map.empty

        let interviewer =
            CallbackInterviewer(fun question ->
                metadata <- question.Metadata
                Answer.FromOption(question.Options.Head))
            :> IInterviewer

        let handler = Handlers.WaitForHumanHandler(interviewer) :> IHandler

        let node =
            { Id = "review"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "hexagon"
                      "prompt", AttrValue.String "Review"
                      "fidelity", AttrValue.String "full" ] }

        let context = Context()
        context.Set("last_stage", "build")

        let graph =
            { Name = "test"
              Nodes =
                Map.ofList
                    [ "review", node
                      "approve",
                      { Id = "approve"
                        Attributes = Map.empty }
                      "reject",
                      { Id = "reject"
                        Attributes = Map.empty } ]
              Edges =
                [ { FromNode = "review"
                    ToNode = "approve"
                    Attributes = Map.ofList [ "label", AttrValue.String "[A] Approve" ] }
                  { FromNode = "review"
                    ToNode = "reject"
                    Attributes = Map.ofList [ "label", AttrValue.String "[R] Reject" ] } ]
              GraphAttributes = Map.ofList [ "goal", AttrValue.String "Ship it" ] }

        let logsRoot = createTempDir ()
        let outcome = handler.Execute(node, context, graph, logsRoot)

        Assert.Equal(StageStatus.Success, outcome.Status)
        Assert.Equal("review", metadata["node_id"])
        Assert.Equal("Review", metadata["attr.prompt"])
        Assert.Equal("full", metadata["attr.fidelity"])
        Assert.Equal(logsRoot, metadata["logs_root"])
        Assert.Equal("build", metadata["last_stage"])
        Assert.Equal("Ship it", metadata["goal"])
        Assert.True(metadata.ContainsKey("prompt_file"))

    [<Fact>]
    let ``WaitForHuman freeform metadata preserves response_file alongside new metadata`` () =
        let mutable metadata = Map.empty

        let interviewer =
            CallbackInterviewer(fun question ->
                metadata <- question.Metadata
                File.WriteAllText(question.Metadata["response_file"], "details")
                Answer.FromText(""))
            :> IInterviewer

        let handler = Handlers.WaitForHumanHandler(interviewer) :> IHandler

        let node =
            { Id = "gate"
              Attributes =
                Map.ofList
                    [ "shape", AttrValue.String "hexagon"
                      "prompt", AttrValue.String "Explain the change" ] }

        let graph =
            { Name = "test"
              Nodes = Map.ofList [ "gate", node; "next", { Id = "next"; Attributes = Map.empty } ]
              Edges =
                [ { FromNode = "gate"
                    ToNode = "next"
                    Attributes = Map.ofList [ "label", AttrValue.String "next" ] } ]
              GraphAttributes = Map.ofList [ "goal", AttrValue.String "Collect input" ] }

        let outcome = handler.Execute(node, Context(), graph, createTempDir ())

        Assert.Equal(StageStatus.Success, outcome.Status)
        Assert.Equal("gate", metadata["node_id"])
        Assert.Equal("Explain the change", metadata["attr.prompt"])
        Assert.True(metadata.ContainsKey("response_file"))

module EventExpansionTests =

    type private RecordingAsyncObserver(events: ResizeArray<PipelineEvent>) =
        interface IAsyncEventObserver with
            member _.OnEventAsync(event) =
                async {
                    do! Async.Sleep 10
                    events.Add(event)
                }

    type private ThrowingAsyncObserver() =
        interface IAsyncEventObserver with
            member _.OnEventAsync(_) =
                async {
                    do! Async.Sleep 5
                    failwith "boom"
                }

    [<Fact>]
    let ``EventCollector captures new Sprint 014 event cases`` () =
        let collector = EventCollector()
        let emitter = EventEmitter()

        let evt =
            PipelineEvent.ChangeImplementationStarted(PipelineEventContext.Empty, "test")

        emitter.AddObserver(collector)

        emitter.Emit(evt)

        Assert.Contains(evt, collector.Events)

    [<Fact>]
    let ``EventEmitter EmitAsync invokes async observers and isolates failures`` () =
        let seen = ResizeArray<PipelineEvent>()
        let emitter = EventEmitter()
        emitter.AddAsyncObserver(RecordingAsyncObserver(seen))
        emitter.AddAsyncObserver(ThrowingAsyncObserver())
        let evt = PipelineEvent.DeployStarted(PipelineEventContext.Empty, "prod")

        emitter.EmitAsync(evt)

        Assert.Single(seen) |> ignore
        Assert.Equal(evt, seen[0])

module AcpPresetTests =

    let private withEnvCleared (keys: string list) (action: unit -> unit) =
        let original =
            keys |> List.map (fun key -> key, Environment.GetEnvironmentVariable(key))

        try
            for key, _ in original do
                Environment.SetEnvironmentVariable(key, null)

            action ()
        finally
            for key, value in original do
                Environment.SetEnvironmentVariable(key, value)

    [<Fact>]
    let ``AcpPresets resolve defaults and parse aliases`` () =
        withEnvCleared
            [ "ATTRACTOR_CODEX_ACP_AGENT_BIN"
              "ATTRACTOR_CODEX_MODEL"
              "ATTRACTOR_CODEX_ACP_CWD"
              "ATTRACTOR_CODEX_ACP_TIMEOUT_SECONDS"
              "ATTRACTOR_CLAUDE_ACP_AGENT_BIN"
              "ATTRACTOR_CLAUDE_ACP_CWD"
              "ATTRACTOR_CLAUDE_ACP_TIMEOUT_SECONDS"
              "ATTRACTOR_GEMINI_ACP_AGENT_BIN"
              "ATTRACTOR_GEMINI_ACP_CWD"
              "ATTRACTOR_GEMINI_ACP_TIMEOUT_SECONDS" ]
            (fun () ->
                Assert.Equal(Some AcpPresets.PresetKind.Codex, AcpPresets.PresetKind.Parse("codex"))
                Assert.Equal(Some AcpPresets.PresetKind.ClaudeCode, AcpPresets.PresetKind.Parse("claude-code"))
                Assert.Equal(Some AcpPresets.PresetKind.Gemini, AcpPresets.PresetKind.Parse("gemini_cli"))
                Assert.True((AcpPresets.PresetKind.Parse("unknown")).IsNone)

                let codex = AcpPresets.resolve AcpPresets.PresetKind.Codex "/tmp"
                let claude = AcpPresets.resolve AcpPresets.PresetKind.ClaudeCode "/tmp"
                let gemini = AcpPresets.resolve AcpPresets.PresetKind.Gemini "/tmp"

                Assert.Equal("codex", codex.Command)
                Assert.Equal(AcpRuntime.AcpTransportKind.Stdio, codex.Transport)
                Assert.Equal<string list>([ "exec"; "-m"; "gpt-5.6" ], codex.Args)
                Assert.Equal("claude", claude.Command)
                Assert.Equal("gemini", gemini.Command)

                let endpoint = AcpPresets.toEndpoint codex
                Assert.Equal(Some "codex", endpoint.Command)
                Assert.Equal(Some "/tmp", endpoint.WorkingDirectory))
