module Sprint014ValidationTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open Xunit
open Attractor

let private validate (dot: string) =
    DotParser.parseOrRaise dot |> fun graph -> Validation.validate graph None

let private hasRule (rule: string) (diags: Diagnostic list) =
    diags |> List.exists (fun d -> d.Rule = rule)

let private ruleCount (rule: string) (diags: Diagnostic list) =
    diags |> List.filter (fun d -> d.Rule = rule) |> List.length

let private findRepoRoot () =
    let rec loop (dir: DirectoryInfo) =
        let marker = Path.Combine(dir.FullName, "docs", "sprints", "SPRINT-014.md")

        if File.Exists(marker) then
            dir.FullName
        elif isNull dir.Parent then
            failwith "Repository root not found from test working directory"
        else
            loop dir.Parent

    loop (DirectoryInfo(AppContext.BaseDirectory))

module LoopSessionPollutionRuleTests =

    [<Fact>]
    let ``loop_session_pollution emits for coding agent with thread_id in loop_restart cycle`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Implement [shape=tab, thread_id="impl", prompt="implement the task"]
                Commit [shape=tab, prompt="git add . && git commit -m done"]
                PickTask [shape=parallelogram, tool_command="python3 ledger.py next", timeout="10s"]
                start -> Implement -> Commit -> PickTask
                PickTask -> Implement [condition="outcome=success", loop_restart=true]
                PickTask -> exit [condition="outcome=fail"]
            }
            """

        Assert.True(hasRule "loop_session_pollution" diags)

    [<Fact>]
    let ``loop_session_pollution does not emit when thread_id is missing`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Implement [shape=tab, prompt="implement the task"]
                Commit [shape=tab, prompt="git add . && git commit -m done"]
                PickTask [shape=parallelogram, tool_command="python3 ledger.py next", timeout="10s"]
                start -> Implement -> Commit -> PickTask
                PickTask -> Implement [condition="outcome=success", loop_restart=true]
                PickTask -> exit [condition="outcome=fail"]
            }
            """

        Assert.False(hasRule "loop_session_pollution" diags)

    [<Fact>]
    let ``loop_session_pollution does not emit for node outside loop_restart reachability`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Preamble [shape=tab, thread_id="preamble", prompt="implement checklist"]
                Implement [shape=tab, prompt="implement the task"]
                Commit [shape=tab, prompt="git add . && git commit -m done"]
                PickTask [shape=parallelogram, tool_command="python3 ledger.py next", timeout="10s"]
                start -> Preamble -> Implement -> Commit -> PickTask
                PickTask -> Implement [condition="outcome=success", loop_restart=true]
                PickTask -> exit [condition="outcome=fail"]
            }
            """

        let loopDiags = diags |> List.filter (fun d -> d.Rule = "loop_session_pollution")
        Assert.DoesNotContain(loopDiags, fun d -> d.NodeId = "Preamble")

    [<Fact>]
    let ``loop_session_pollution emits once per offending node in same loop`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Implement [shape=tab, thread_id="impl", prompt="implement feature"]
                Review [shape=tab, thread_id="review", prompt="review the patch"]
                PickTask [shape=parallelogram, tool_command="python3 ledger.py next", timeout="10s"]
                start -> Implement -> Review -> PickTask
                PickTask -> Implement [condition="outcome=success", loop_restart=true]
                PickTask -> exit [condition="outcome=fail"]
            }
            """

        Assert.Equal(2, ruleCount "loop_session_pollution" diags)

    [<Fact>]
    let ``reachableInLoop terminates on self loop`` () =
        let graph =
            DotParser.parseOrRaise
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                A [shape=tab, thread_id="impl", prompt="implement"]
                start -> A
                A -> A [loop_restart=true]
                A -> exit [condition="outcome=fail"]
            }
            """

        let loopEdge = graph.Edges |> List.find (fun e -> e.LoopRestart)
        let inLoop = Validation.reachableInLoop graph loopEdge
        Assert.True(inLoop.Contains("A"))

module SafetyGateCoverageRuleTests =

    [<Fact>]
    let ``scope_gate_coverage emits when file editing node can reach commit without scope gate`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                FixFailures [shape=tab, prompt="modify files and implement fixes"]
                CommitAndSummarize [shape=tab, prompt="git add . && git commit -m partial"]
                start -> FixFailures -> CommitAndSummarize -> exit
            }
            """

        Assert.True(hasRule "scope_gate_coverage" diags)

    [<Fact>]
    let ``scope_gate_coverage and partial_commit_needs_build_gate stay quiet when gates are in place`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                FixFailures [shape=tab, prompt="implement updates and edit files"]
                CheckScope [shape=parallelogram, tool_command="bash scripts/check_scope.sh", timeout="30s"]
                BuildGate [shape=parallelogram, tool_command="go build ./...", timeout="300s"]
                CommitAndSummarize [shape=tab, prompt="git add . && git commit -m done"]
                start -> FixFailures -> CheckScope -> BuildGate -> CommitAndSummarize -> exit
            }
            """

        Assert.False(hasRule "scope_gate_coverage" diags)
        Assert.False(hasRule "partial_commit_needs_build_gate" diags)

    [<Fact>]
    let ``partial_commit_needs_build_gate emits when fail partial edge goes to commit`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                CheckFixResult [shape=diamond]
                CommitAndSummarize [shape=tab, prompt="git add . && git commit -m partial"]
                start -> CheckFixResult
                CheckFixResult -> CommitAndSummarize [condition="outcome=fail", label="give up - commit partial"]
                CheckFixResult -> exit [condition="outcome=success"]
                CommitAndSummarize -> exit
            }
            """

        Assert.True(hasRule "partial_commit_needs_build_gate" diags)

    [<Fact>]
    let ``parallelogram_needs_timeout emits when timeout is missing`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Gate [shape=parallelogram, tool_command="grep -q OK .ai/status.txt"]
                start -> Gate -> exit
            }
            """

        Assert.True(hasRule "parallelogram_needs_timeout" diags)

    [<Fact>]
    let ``parallelogram_needs_timeout does not emit when timeout exists`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Gate [shape=parallelogram, tool_command="grep -q OK .ai/status.txt", timeout="10s"]
                start -> Gate -> exit
            }
            """

        Assert.False(hasRule "parallelogram_needs_timeout" diags)

    [<Fact>]
    let ``timeout recommender maps build fast checks and default`` () =
        Assert.Equal("300s", Validation.recommendParallelogramTimeout "go build ./...")
        Assert.Equal("10s", Validation.recommendParallelogramTimeout "grep -q ok .ai/state.txt")
        Assert.Equal("60s", Validation.recommendParallelogramTimeout "custom-long-running-command")

module PromptAntipatternRuleTests =

    [<Fact>]
    let ``validate_measure_only emits when validate node tries to fix and rerun`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Validate [shape=tab, prompt="Run go build ./... and go test ./...; if they fail, try to fix and rerun."]
                start -> Validate -> exit
            }
            """

        Assert.True(hasRule "validate_measure_only" diags)

    [<Fact>]
    let ``validate_measure_only does not emit for measure-only prompt`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Validate [shape=tab, prompt="Run go build ./... and go test ./... once, report PASS/FAIL, do not attempt fixes."]
                start -> Validate -> exit
            }
            """

        Assert.False(hasRule "validate_measure_only" diags)

    [<Fact>]
    let ``validate_measure_only regex does not false-positive on innocent fix phrasing`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Validate [shape=tab, prompt="Run go test ./... once and report results. Also fix TypeScript typings before release docs are published."]
                start -> Validate -> exit
            }
            """

        Assert.False(hasRule "validate_measure_only" diags)

    [<Fact>]
    let ``review_gate_first_line_strict emits when upstream prompt is not explicit`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                OpusReview [shape=tab, prompt="Review changes and write APPROVED or REVISE with rationale in .ai/review.txt"]
                CheckReview [shape=parallelogram, tool_command="grep -q '^APPROVED$' .ai/review.txt", timeout="10s"]
                start -> OpusReview -> CheckReview -> exit
            }
            """

        Assert.True(hasRule "review_gate_first_line_strict" diags)

    [<Fact>]
    let ``review_gate_first_line_strict does not emit when upstream prompt mandates exact first line`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                OpusReview [shape=tab, prompt="Review changes. The FIRST LINE of .ai/review.txt MUST be exactly APPROVED on its own line — no markdown heading, no whitespace."]
                CheckReview [shape=parallelogram, tool_command="grep -q '^APPROVED$' .ai/review.txt", timeout="10s"]
                start -> OpusReview -> CheckReview -> exit
            }
            """

        Assert.False(hasRule "review_gate_first_line_strict" diags)

    [<Fact>]
    let ``review_gate_first_line_strict does not emit when gate has no upstream coding_agent`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                CheckReview [shape=parallelogram, tool_command="grep -q '^APPROVED$' .ai/review.txt", timeout="10s"]
                start -> CheckReview -> exit
            }
            """

        Assert.False(hasRule "review_gate_first_line_strict" diags)

    [<Fact>]
    let ``validate_measure_only emits on adversarial conditional-fix phrasing`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Validate [shape=tab, prompt="Run go test ./... — if the build fails, fix the TypeScript typings and re-run until green."]
                start -> Validate -> exit
            }
            """

        Assert.True(hasRule "validate_measure_only" diags)

    [<Fact>]
    let ``scratch_path_consistency emits when same slug drifts across full paths`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Plan [shape=tab, prompt="Write plan to .ai/plan.md"]
                Check [shape=parallelogram, tool_command="grep -q READY .ai/plan.txt", timeout="10s"]
                start -> Plan -> Check -> exit
            }
            """

        Assert.True(hasRule "scratch_path_consistency" diags)

    [<Fact>]
    let ``scratch_path_consistency stays quiet when paths are consistent`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                Plan [shape=tab, prompt="Write plan to .ai/plan.md"]
                Check [shape=parallelogram, tool_command="grep -q READY .ai/plan.md", timeout="10s"]
                start -> Plan -> Check -> exit
            }
            """

        Assert.False(hasRule "scratch_path_consistency" diags)

module TerminalExitRuleAndDocsTests =

    [<Fact>]
    let ``terminal_exit_on_empty_backlog emits when Pick ledger gate fails to Exit`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                PickTask [shape=parallelogram, tool_command="python3 ledger.py next", timeout="10s"]
                Work [shape=tab, prompt="implement selected task"]
                start -> PickTask
                PickTask -> Work [condition="outcome=success"]
                PickTask -> exit [condition="outcome=fail"]
                Work -> exit
            }
            """

        Assert.True(hasRule "terminal_exit_on_empty_backlog" diags)

    [<Fact>]
    let ``terminal_exit_on_empty_backlog does not emit when fail does not route to Exit`` () =
        let diags =
            validate
                """
            digraph Test {
                start [shape=Mdiamond]
                exit [shape=Msquare]
                PickTask [shape=parallelogram, tool_command="python3 ledger.py next", timeout="10s"]
                Retry [shape=diamond]
                Work [shape=tab, prompt="implement selected task"]
                start -> PickTask
                PickTask -> Work [condition="outcome=success"]
                PickTask -> Retry [condition="outcome=fail"]
                Retry -> Work
                Work -> exit
            }
            """

        Assert.False(hasRule "terminal_exit_on_empty_backlog" diags)

    [<Fact>]
    let ``external shim python snippet patches checkpoint fixture without corrupting JSON`` () =
        let repoRoot = findRepoRoot ()
        let docPath = Path.Combine(repoRoot, "docs", "spec", "external-shim-recovery.md")
        let markdown = File.ReadAllText(docPath)
        let fenceStart = markdown.IndexOf("```python", StringComparison.Ordinal)
        Assert.True(fenceStart >= 0, "python snippet block not found in external-shim-recovery.md")

        let snippetStart = fenceStart + "```python".Length
        let fenceEnd = markdown.IndexOf("```", snippetStart, StringComparison.Ordinal)
        Assert.True(fenceEnd > snippetStart, "python snippet end fence not found in external-shim-recovery.md")

        let snippet = markdown.Substring(snippetStart, fenceEnd - snippetStart).Trim()

        let tmpDir =
            Path.Combine(Path.GetTempPath(), $"attractor-sprint014-smoke-{Guid.NewGuid():N}")

        Directory.CreateDirectory(tmpDir) |> ignore
        let scriptPath = Path.Combine(tmpDir, "patch_checkpoint.py")
        let fixturePath = Path.Combine(tmpDir, "checkpoint.json")

        try
            File.WriteAllText(scriptPath, snippet)

            File.WriteAllText(
                fixturePath,
                """{
  "current_node": "Validate",
  "completed_nodes": ["start"],
  "node_outcomes": {
    "Validate": { "status": "fail", "preferred_label": "", "suggested_next_ids": [], "context_updates": {}, "notes": "", "failure_reason": "stuck" }
  },
  "context": { "outcome": "fail" }
}"""
            )

            let psi = ProcessStartInfo("python3")
            psi.Arguments <- $"\"{scriptPath}\" \"{fixturePath}\""
            psi.WorkingDirectory <- tmpDir
            psi.RedirectStandardError <- true
            psi.RedirectStandardOutput <- true
            psi.UseShellExecute <- false

            use proc = Process.Start(psi)
            proc.WaitForExit(15000) |> ignore

            let stderr = proc.StandardError.ReadToEnd()
            Assert.True(proc.ExitCode = 0, $"python snippet exited {proc.ExitCode}: {stderr}")

            let patched = File.ReadAllText(fixturePath)
            use doc = JsonDocument.Parse(patched)
            let root = doc.RootElement
            let contextOutcome = root.GetProperty("context").GetProperty("outcome").GetString()
            Assert.Equal("success", contextOutcome)
        finally
            if Directory.Exists(tmpDir) then
                Directory.Delete(tmpDir, true)
