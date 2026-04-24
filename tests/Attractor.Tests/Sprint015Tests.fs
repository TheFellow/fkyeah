module Sprint015Tests

open System
open System.IO
open System.Text.Json
open Xunit
open Attractor

let private createTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), $"attractor-sprint015-{Guid.NewGuid():N}")

    Directory.CreateDirectory(dir) |> ignore
    dir

let private parse (dot: string) = DotParser.parseOrRaise dot
let private validate (dot: string) = dot |> parse |> fun g -> Validation.validate g None

module InterpolationTests =

    [<Fact>]
    let ``interpolateAttrValue resolves context internal and bare keys`` () =
        let context = Context()
        context.Set("context.foo", "bar")
        context.Set("internal.loop_restart_count", "2")

        Assert.Equal("bar", Engine.interpolateAttrValue context "${context.foo}")
        Assert.Equal("2", Engine.interpolateAttrValue context "${internal.loop_restart_count}")
        Assert.Equal("bar", Engine.interpolateAttrValue context "${foo}")

    [<Fact>]
    let ``interpolateAttrValue leaves unresolved references literal`` () =
        let context = Context()
        Assert.Equal("x-${missing}-y", Engine.interpolateAttrValue context "x-${missing}-y")

    [<Fact>]
    let ``interpolateAttrValue honors escaped literals`` () =
        let context = Context()
        context.Set("context.foo", "bar")
        Assert.Equal("${foo}", Engine.interpolateAttrValue context "$${foo}")
        Assert.Equal("literal=${foo} resolved=bar", Engine.interpolateAttrValue context "literal=$${foo} resolved=${foo}")

module FreshSessionTests =

    [<Fact>]
    let ``FreshSession accessor defaults false and parses true`` () =
        let graph =
            parse
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                A [shape=tab, fresh_session=true]
                B [shape=tab]
                start -> A -> B -> exit
            }
            """

        Assert.True(graph.Nodes["A"].FreshSession)
        Assert.False(graph.Nodes["B"].FreshSession)

    [<Fact>]
    let ``fresh_session generates distinct thread ids across runs`` () =
        let seen = ResizeArray<string>()

        let handler =
            { new IHandler with
                member _.Execute(_, context, _, _) =
                    seen.Add(context.Get("thread_id", ""))
                    Outcome.Success() }

        let graph =
            parse
                """
            digraph Test {
                graph [default_fidelity="full"]
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Worker [type="custom", fresh_session=true]
                start -> Worker -> exit
            }
            """

        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", handler)

        let runOnce () =
            let logsRoot = createTempDir ()

            let config =
                { RunConfig.Default(logsRoot) with
                    Registry = registry }

            Engine.run graph config |> ignore

        runOnce ()
        System.Threading.Thread.Sleep(2)
        runOnce ()

        Assert.Equal(2, seen.Count)
        Assert.False(String.Equals(seen[0], seen[1], StringComparison.Ordinal))
        Assert.StartsWith("Worker-", seen[0])

    [<Fact>]
    let ``conflicting_session_attrs emits error when thread_id and fresh_session are both set`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                A [shape=tab, thread_id="review", fresh_session=true]
                start -> A -> exit
            }
            """

        let diag =
            diags
            |> List.tryFind (fun d -> d.Rule = "conflicting_session_attrs" && d.NodeId = "A")

        Assert.True(diag.IsSome)
        Assert.Equal(Severity.Error, diag.Value.Severity)

    [<Fact>]
    let ``conflicting_session_attrs emits when thread_id is an interpolation-only value and fresh_session=true`` () =
        // thread_id="${internal.loop_restart_count}" is dead weight when fresh_session=true —
        // the engine ignores it. Flag it rather than silently accept an unused attribute.
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                A [shape=tab, thread_id="${internal.loop_restart_count}", fresh_session=true]
                start -> A -> exit
            }
            """

        Assert.True(diags |> List.exists (fun d -> d.Rule = "conflicting_session_attrs" && d.NodeId = "A"))

module StructuralSafetyAttrTests =

    [<Fact>]
    let ``requires_green_build non-zero skips primary handler and fails`` () =
        let mutable calls = 0

        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    calls <- calls + 1
                    Outcome.Success() }

        let graph =
            parse
                """
            digraph Test {
                graph [default_fidelity="full"]
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Worker [type="custom", requires_green_build="exit 7"]
                start -> Worker -> exit
            }
            """

        let logsRoot = createTempDir ()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", handler)

        let result =
            Engine.run
                graph
                { RunConfig.Default(logsRoot) with
                    Registry = registry }

        Assert.Equal(0, calls)
        Assert.Equal(StageStatus.Fail, result.FinalOutcome.Status)
        Assert.Contains("pre-condition failed: exit 7 exited 7", result.FinalOutcome.FailureReason)
        Assert.Equal("7", result.Context.Get("tool_exit_code", ""))

    [<Fact>]
    let ``scope_gate failure runs revert and retries once`` () =
        let root = createTempDir ()
        let mutable calls = 0
        let offscopePath = Path.Combine(root, "offscope.txt")

        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    calls <- calls + 1
                    File.WriteAllText(offscopePath, "changed")
                    Outcome.Success() }

        let gateCommand = $"test ! -f '{offscopePath}'"
        let revertCommand = $"rm -f '{offscopePath}'"

        let graph =
            parse
                $"""
            digraph Test {{
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Worker [
                    type="custom",
                    cwd="{root}",
                    scope_gate="{gateCommand}",
                    scope_revert="{revertCommand}",
                    scope_gate_max_retries=1
                ]
                start -> Worker -> exit
            }}
            """

        let logsRoot = createTempDir ()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", handler)

        let result =
            Engine.run
                graph
                { RunConfig.Default(logsRoot) with
                    Registry = registry }

        Assert.Equal(StageStatus.Fail, result.FinalOutcome.Status)
        Assert.Equal(2, calls)
        Assert.False(File.Exists(offscopePath))
        Assert.Contains("scope_gate rejected changes after 2 attempts", result.FinalOutcome.FailureReason)

    [<Fact>]
    let ``without structural attrs execution trace is unchanged`` () =
        let trace = ResizeArray<string>()
        let mutable attempts = 0

        let handler =
            { new IHandler with
                member _.Execute(_, _, _, _) =
                    attempts <- attempts + 1
                    trace.Add($"worker#{attempts}")

                    if attempts = 1 then
                        Outcome.Retry("retry once")
                    else
                        Outcome.Success() }

        let graph =
            parse
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Worker [type="custom", max_retries=1]
                start -> Worker -> exit
            }
            """

        let logsRoot = createTempDir ()
        let registry = HandlerRegistry.CreateDefault()
        registry.Register("custom", handler)

        let result =
            Engine.run
                graph
                { RunConfig.Default(logsRoot) with
                    Registry = registry }

        Assert.Equal(StageStatus.Success, result.FinalOutcome.Status)
        Assert.Equal<string list>([ "worker#1"; "worker#2" ], trace |> Seq.toList)

    [<Fact>]
    let ``scope_gate_coverage warning is suppressed when node has scope_gate attr`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Fix [shape=tab, prompt="implement and modify files", scope_gate="bash scripts/check_scope.sh"]
                Commit [shape=tab, prompt="git add . && git commit -m done"]
                start -> Fix -> Commit -> exit
            }
            """

        Assert.DoesNotContain(diags, fun d -> d.Rule = "scope_gate_coverage")

    [<Fact>]
    let ``partial_commit_needs_build_gate warning is suppressed when commit has requires_green_build`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Check [shape=diamond]
                Commit [shape=tab, prompt="git add . && git commit -m partial", requires_green_build="go build ./..."]
                start -> Check
                Check -> Commit [condition="outcome=fail", label="give up - commit partial"]
                Check -> exit [condition="outcome=success"]
                Commit -> exit
            }
            """

        Assert.DoesNotContain(diags, fun d -> d.Rule = "partial_commit_needs_build_gate")

module CheckpointCliTests =

    [<Fact>]
    let ``checkpoint mark-done mutates atomic fields and round-trips through loadCheckpoint`` () =
        let runDir = createTempDir ()
        let context = Context()
        context.Set("seed", "v1")

        let checkpoint =
            Attractor.Checkpoint.Create(context, "start", [ "start" ], Map.empty, Map.ofList [ "start", Outcome.Success() ])

        Engine.saveCheckpoint runDir checkpoint

        let exitCode =
            global.Checkpoint.dispatch
                [| "mark-done"
                   runDir
                   "Worker"
                   "--outcome=success"
                   "--note=shim" |]

        Assert.Equal(0, exitCode)
        Assert.True(File.Exists(Path.Combine(runDir, "checkpoint.json.bak")))

        let loaded = Engine.loadCheckpoint runDir
        Assert.True(loaded.IsSome)
        Assert.Equal("Worker", loaded.Value.CurrentNode)
        Assert.Contains("Worker", loaded.Value.CompletedNodes)
        Assert.Equal(StageStatus.Success, loaded.Value.NodeOutcomes["Worker"].Status)
        Assert.Equal("success", loaded.Value.ContextValues["outcome"])
        Assert.Equal("Worker", loaded.Value.ContextValues["last_stage"])

    [<Fact>]
    let ``checkpoint set-outcome writes tool_output context`` () =
        let runDir = createTempDir ()
        let context = Context()
        context.Set("seed", "v1")

        let checkpoint =
            Attractor.Checkpoint.Create(
                context,
                "Worker",
                [ "start"; "Worker" ],
                Map.empty,
                Map.ofList [ "start", Outcome.Success(); "Worker", Outcome.Success() ]
            )

        Engine.saveCheckpoint runDir checkpoint

        let exitCode =
            global.Checkpoint.dispatch
                [| "set-outcome"
                   runDir
                   "Worker"
                   "fail"
                   "--tool-stdout=forced output" |]

        Assert.Equal(0, exitCode)

        let loaded = Engine.loadCheckpoint runDir |> Option.get
        Assert.Equal("forced output", loaded.ContextValues["tool_output"])
        Assert.Equal("forced output", loaded.ContextValues["tool_stdout"])
        Assert.Equal(StageStatus.Fail, loaded.NodeOutcomes["Worker"].Status)

    [<Fact>]
    let ``checkpoint backup creates valid json identical to original`` () =
        let runDir = createTempDir ()
        let context = Context()
        context.Set("seed", "v1")
        let checkpoint = Attractor.Checkpoint.Create(context, "start", [ "start" ], Map.empty, Map.empty)
        Engine.saveCheckpoint runDir checkpoint

        let originalPath = Path.Combine(runDir, "checkpoint.json")
        let exitCode = global.Checkpoint.dispatch [| "backup"; runDir |]
        let backupPath = Path.Combine(runDir, "checkpoint.json.bak")

        Assert.Equal(0, exitCode)
        Assert.True(File.Exists(backupPath))
        Assert.Equal(File.ReadAllText(originalPath), File.ReadAllText(backupPath))

        use _ = JsonDocument.Parse(File.ReadAllText(backupPath))
        Assert.True(true)
