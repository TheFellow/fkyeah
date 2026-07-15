open System
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Attractor
open UnifiedLlm

let mutable verbose = true
let cliVersion = "0.19.0"
let mutable tracePath: string option = None
let mutable cacheEnabled = false
let mutable cacheDirectory: string option = None
let mutable sharedCostLedger: CostLedger option = None
let mutable sharedObservabilitySink = ObservabilitySink.none

[<Literal>]
let ExitSuccess = 0

[<Literal>]
let ExitPipelineFailure = 1

[<Literal>]
let ExitValidationError = 1

[<Literal>]
let ExitConfigError = 3

let private resolveContextFidelity (node: Node) =
    node.GetAttrString("__resolved_fidelity", "").Trim()
    |> function
        | "" -> FidelityMode.Compact
        | raw -> FidelityMode.Parse(raw) |> Option.defaultValue FidelityMode.Compact

/// Build a system message from pipeline context so LLM nodes see prior stage outputs.
let buildContextMessage (context: Attractor.Context) (goal: string) : string =
    ContextPrompt.preparePromptContext FidelityMode.Compact context goal
    |> fun prepared -> prepared.SystemMessage

/// LLM backend that routes through the UnifiedLlm client, with full pipeline context
type LlmBackend(llmClient: Client) =
    interface ICodergenBackend with
        member _.Run(node, prompt, context) =
            try
                let model =
                    if node.LlmModel <> "" then
                        node.LlmModel
                    else
                        "claude-sonnet-5"

                let provider =
                    if node.LlmProvider <> "" then
                        Some node.LlmProvider
                    else if model.StartsWith("claude", StringComparison.Ordinal) then
                        Some "anthropic"
                    elif
                        model.StartsWith("gpt", StringComparison.Ordinal)
                        || model.StartsWith("o1", StringComparison.Ordinal)
                        || model.StartsWith("o3", StringComparison.Ordinal)
                        || model.StartsWith("o4", StringComparison.Ordinal)
                    then
                        Some "openai"
                    elif model.StartsWith("gemini", StringComparison.Ordinal) then
                        Some "gemini"
                    else
                        None

                // Build context from pipeline state
                let goal = context.Get("graph.goal", "")

                let preparedContext =
                    ContextPrompt.preparePromptContext (resolveContextFidelity node) context goal

                let systemMsg = preparedContext.SystemMessage

                if verbose then
                    let providerStr = provider |> Option.defaultValue "auto"

                    eprintfn
                        "        LLM call: model=%s provider=%s prompt=%d chars context=%d chars fidelity=%s budget=%d/%d"
                        model
                        providerStr
                        prompt.Length
                        systemMsg.Length
                        (preparedContext.FidelityMode.ToString())
                        preparedContext.CharBudgetUsed
                        (FidelityMode.charBudget preparedContext.FidelityMode)

                // Read reasoning effort from node (set by stylesheet or attribute)
                let reasoningEffort =
                    let re = node.ReasoningEffort

                    if re <> "" && re <> "high" then
                        Some re // "high" is default, don't send explicitly
                    else
                        None

                // Read previous response ID for conversation chaining (OpenAI Responses API)
                let prevResponseId =
                    context.TryGet($"llm.response_id.{model}")
                    |> Option.bind (fun v -> if v <> "" then Some v else None)

                let messages =
                    [ UnifiedLlm.Message.System(systemMsg); UnifiedLlm.Message.User(prompt) ]

                let maxTokens = Reasoning.recommendMaxTokens reasoningEffort 16384

                let request =
                    { Request.Create(model, messages) with
                        Provider = provider
                        MaxTokens = Some maxTokens
                        ReasoningEffort = reasoningEffort
                        PreviousResponseId = prevResponseId }

                let sw = System.Diagnostics.Stopwatch.StartNew()
                let response = llmClient.Complete(request)
                sw.Stop()

                match
                    Costing.tryCalculateCostById
                        response.Model
                        response.Usage
                        (response.Usage.CacheReadTokens |> Option.defaultValue 0 > 0)
                with
                | Some cost ->
                    context.Set("llm.cost_microdollars", (cost.TotalMicrodollars: int64).ToString())

                    context.Set(
                        "llm.cost_usd",
                        cost.TotalUsd.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    )

                    context.Set("llm.input_tokens", ((response.Usage.InputTokens: int).ToString()))
                    context.Set("llm.output_tokens", ((response.Usage.OutputTokens: int).ToString()))
                    context.Set("llm.cache_hit", (cost.CacheHit: bool).ToString())
                    context.Set("llm.model", response.Model)
                    context.Set("llm.provider", response.Provider)
                    context.Set("llm.last_node", node.Id)
                | None -> ()

                // Store response ID for conversation chaining on next call
                match response.ResponseId with
                | Some respId -> context.Set($"llm.response_id.{model}", respId)
                | None -> ()

                if verbose then
                    let tokens =
                        if response.Usage.InputTokens > 0 then
                            let cacheStr =
                                let read =
                                    match response.Usage.CacheReadTokens with
                                    | Some c when c > 0 -> sprintf " cache_read=%d" c
                                    | _ -> ""

                                let write =
                                    match response.Usage.CacheWriteTokens with
                                    | Some c when c > 0 -> sprintf " cache_write=%d" c
                                    | _ -> ""

                                read + write

                            let reasonStr =
                                match response.Usage.ReasoningTokens with
                                | Some r when r > 0 -> sprintf " reasoning=%d" r
                                | _ -> ""

                            sprintf
                                "in=%d out=%d%s%s"
                                response.Usage.InputTokens
                                response.Usage.OutputTokens
                                cacheStr
                                reasonStr
                        else
                            "tokens=n/a"

                    eprintfn "        LLM done: %d chars, %.1fs, %s" response.Text.Length sw.Elapsed.TotalSeconds tokens

                Ok response.Text
            with
            | :? OperationCanceledException ->
                eprintfn "        LLM call cancelled (Ctrl-C)"
                Result.Error(Outcome.Fail("Cancelled by user"))
            | :? ValidationError as ex ->
                eprintfn "        LLM validation error: %s" ex.Message
                Result.Error(Outcome.Fail($"Validation failed: {ex.Message}"))
            | ex ->
                eprintfn "        LLM error: %s" ex.Message
                Result.Error(Outcome.Retry($"LLM call failed: {ex.Message}"))

/// CLI event observer with verbose stats
type ConsoleEventObserver() =
    let timestamp () = DateTimeOffset.Now.ToString("HH:mm:ss")
    let mutable pipelineStart = DateTimeOffset.UtcNow
    let mutable stageCount = 0
    let mutable totalLlmStages = 0

    interface IEventObserver with
        member _.OnEvent(event) =
            match event with
            | PipelineEvent.PipelineStarted(name, id) ->
                pipelineStart <- DateTimeOffset.UtcNow
                printfn "[%s] Pipeline '%s' started (run %s)" (timestamp ()) name (id.Substring(0, 8))
            | PipelineEvent.StageStarted(name, index) ->
                stageCount <- index + 1
                printfn "[%s]   Stage %d: %s" (timestamp ()) index name
            | PipelineEvent.StageCompleted(name, index, duration) ->
                if duration.TotalSeconds >= 1.0 then
                    totalLlmStages <- totalLlmStages + 1

                printfn "[%s]   Stage %s completed (%.1fs)" (timestamp ()) name duration.TotalSeconds

                if verbose then
                    let elapsed = DateTimeOffset.UtcNow - pipelineStart
                    eprintfn "        [stats] stage %d/%d elapsed=%.0fs" (index + 1) stageCount elapsed.TotalSeconds
            | PipelineEvent.StageFailed(name, _, error, willRetry) ->
                let suffix = if willRetry then " (will retry)" else ""
                printfn "[%s]   Stage %s FAILED: %s%s" (timestamp ()) name error suffix
            | PipelineEvent.StageRetrying(name, _, attempt, delayMs) ->
                printfn "[%s]   Stage %s retrying (attempt %d, backoff %dms)" (timestamp ()) name attempt delayMs
            | PipelineEvent.PipelineCompleted(duration, count) ->
                printfn "[%s] Pipeline completed: %d stages in %.1fs" (timestamp ()) count duration.TotalSeconds

                if verbose then
                    eprintfn
                        "        [stats] total_stages=%d llm_stages=%d wall_time=%.1fs"
                        count
                        totalLlmStages
                        duration.TotalSeconds
            | PipelineEvent.PipelineFailed(error, duration) ->
                printfn "[%s] Pipeline FAILED after %.1fs: %s" (timestamp ()) duration.TotalSeconds error
            | PipelineEvent.CheckpointSaved(nodeId) ->
                if verbose then
                    eprintfn "        [stats] checkpoint saved: %s" nodeId
            | PipelineEvent.LoopRestarted(target, count, newLogs) ->
                printfn "[%s]   Loop restart #%d -> %s (logs: %s)" (timestamp ()) count target newLogs
            | PipelineEvent.ParallelStarted(branchCount) ->
                if verbose then
                    eprintfn "        [stats] parallel fan-out: %d branches" branchCount
            | PipelineEvent.ParallelBranchCompleted(branch, _, duration, success) ->
                if verbose then
                    let status = if success then "ok" else "FAIL"
                    eprintfn "        [stats] branch %s: %s (%.1fs)" branch status duration.TotalSeconds
            | PipelineEvent.ParallelCompleted(duration, successCount, failureCount) ->
                if verbose then
                    eprintfn
                        "        [stats] parallel done: %d ok, %d failed (%.1fs)"
                        successCount
                        failureCount
                        duration.TotalSeconds
            | PipelineEvent.ChangeImplementationStarted _
            | PipelineEvent.ChangeReviewCompleted _
            | PipelineEvent.ChangeScenarioCompleted _
            | PipelineEvent.DeployStarted _
            | PipelineEvent.DeployVerified _
            | PipelineEvent.DeployRolledBack _ -> ()
            | _ -> ()

// ============================================================================
// Self-describing help content
// ============================================================================

let printUsage () =
    printfn
        """Attractor - DOT-based AI pipeline runner

Usage:
  attractor <file.dot> [options]      Run a pipeline
  attractor --validate <file.dot>     Validate without executing
  attractor --resume <dir> <file.dot> Resume from checkpoint
  attractor checkpoint <subcommand>   Inspect/mutate checkpoint.json safely
  attractor serve [--port N]          Run HTTP server mode
  attractor schema                    Print the DOT schema reference
  attractor example                   Print an example pipeline
  attractor models                    List known models and aliases for llm_model=

Options:
  --logs <dir>       Output directory (default: ./.ai/attractor-logs/<timestamp>)
  --validate         Validate the DOT file without executing
  --resume <dir>     Resume from checkpoint in the given logs directory
  --auto-approve     Auto-approve all human gates (no interactive prompts)
  --simulate         Run with simulated LLM responses (no API keys needed)
  --cache            Enable persisted LLM response caching
  --cache-dir <dir>  Cache directory override (default: ./.fkyeah-cache)
  --trace <path>     Write structured observability JSON Lines
  --verbose          Print verbose stats/logging (default)
  --quiet            Suppress verbose stats/logging (LLM calls, tokens, timing)
  --models           List known models and aliases for llm_model=
  --version          Print version
  --help, -h         Show this help

Environment:
  ANTHROPIC_API_KEY  Enables live Anthropic LLM calls (claude-* models)
  OPENAI_API_KEY     Enables live OpenAI LLM calls (gpt-* models)
  GEMINI_API_KEY     Enables live Gemini LLM calls (gemini-* models)

LLM codergen nodes require at least one API key unless --simulate is passed."""

let printSchema () =
    printfn
        """# Attractor DOT Schema Reference
#
# Attractor pipelines are directed graphs in Graphviz DOT syntax.
# Each node is a stage (LLM call, human gate, tool, conditional branch, etc.)
# and edges define transitions between stages.
#
# To generate a pipeline: write a `digraph name { ... }` with the elements
# below, then run: attractor --validate file.dot && attractor file.dot

# ═══════════════════════════════════════════════════════════════════════════
# GRAPH STRUCTURE
# ═══════════════════════════════════════════════════════════════════════════
#
#   digraph <name> {
#       graph [ <graph-attributes> ]
#       node  [ <node-defaults> ]       // optional defaults for all nodes
#       edge  [ <edge-defaults> ]       // optional defaults for all edges
#       <node-declarations>
#       <edge-declarations>
#   }

# ═══════════════════════════════════════════════════════════════════════════
# GRAPH ATTRIBUTES (inside `graph [...]`)
# ═══════════════════════════════════════════════════════════════════════════
#
#   goal                 String    Pipeline-level goal. Available as $goal in prompts.
#   label                String    Display name for the pipeline.
#   model_stylesheet     String    CSS-like LLM model/provider assignment.
#                                  Selectors: * (universal), box (shape), .class, #id
#                                  Properties: llm_model, llm_provider, reasoning_effort
#                                  Specificity: * < shape < .class < #id
#                                  Example: "* { llm_model: claude-sonnet-5; }
#                                            .critical { llm_model: claude-opus-4-6; }"
#   default_fidelity     String    Default context fidelity mode for all nodes.
#                                  Values: full | truncate | compact |
#                                          summary:low | summary:medium | summary:high
#   default_max_retry    Integer   Global retry ceiling (default: 50).
#   retry_target         String    Node to jump to on unsatisfied goal gate at exit.
#   fallback_retry_target String   Secondary retry target.

# ═══════════════════════════════════════════════════════════════════════════
# NODE SHAPES (determines handler type)
# ═══════════════════════════════════════════════════════════════════════════
#
#   Shape             Handler Type         Purpose
#   ─────────────     ──────────────────   ──────────────────────────────────
#   Mdiamond          start                Entry point (exactly one required)
#   Msquare           exit                 Terminal node (exactly one required)
#   box               codergen             LLM task (default for unspecified shapes)
#   tab               coding_agent         LLM agent with tool execution (max_turns attr)
#   diamond           conditional          Pass-through; engine evaluates edge conditions
#   hexagon           wait.human           Human approval gate (interactive prompt)
#   component         parallel             Fan-out: branches execute concurrently
#   tripleoctagon     parallel.fan_in      Fan-in: consolidate parallel results
#   parallelogram     tool                 Execute shell command (tool_command attr)
#   house             stack.manager_loop   Supervisor: bounded observe/steer/wait loop

# ═══════════════════════════════════════════════════════════════════════════
# NODE ATTRIBUTES (inside `node_id [...]`)
# ═══════════════════════════════════════════════════════════════════════════
#
#   shape              String    Graphviz shape (see table above). Default: box
#   type               String    Explicit handler type override (e.g. type="tool")
#   label              String    Display name (defaults to node ID)
#   prompt             String    LLM prompt text. Use $goal for variable expansion.
#   class              String    Comma-separated class names for stylesheet matching.
#   goal_gate          Boolean   If true, must succeed before pipeline can exit.
#   max_retries        Integer   Additional retry attempts on RETRY/FAIL outcomes.
#   retry_target       String    Node to redirect to if this goal gate fails.
#   fallback_retry_target String Secondary redirect target.
#   allow_partial      Boolean   Accept PARTIAL_SUCCESS when retries exhausted.
#   fidelity           String    Context fidelity mode override for this node.
#   thread_id          String    Session reuse key (used with fidelity=full).
#   fresh_session      Boolean   Generate unique thread_id per invocation (ignores thread_id).
#   timeout            Duration  Max execution time. Quote for Graphviz compat: "500ms", "10s", "5m", "1h", "1d"
#   llm_model          String    LLM model override (e.g. "claude-opus-4-6").
#   llm_provider       String    LLM provider override (e.g. "anthropic").
#   reasoning_effort   String    Reasoning depth: low | medium | high (default: high)
#   auto_status        Boolean   Auto-generate SUCCESS if handler writes no status.
#   tool_command       String    Shell command to execute (for tool/parallelogram nodes).
#                                Env vars available: $ATTRACTOR_PROMPT_FILE (path to prompt.txt),
#                                $ATTRACTOR_STAGE_DIR, $ATTRACTOR_LOGS_ROOT, $ATTRACTOR_NODE_ID.
#                                If node also has `prompt`, it's written to prompt.txt and piped to stdin.
#   prompt             String    For tool nodes: written to {stage_dir}/prompt.txt and piped to stdin.
#                                Use this for long prompts that would break shell escaping in tool_command.
#   scope_gate         String    Post-success command that must exit 0; non-zero can trigger scope_revert + retry.
#   scope_revert       String    Best-effort command run when scope_gate fails.
#   scope_gate_max_retries Integer Number of primary-handler re-attempts after scope_gate failure (default: 1).
#   requires_green_build String  Pre-condition command run before handler; non-zero skips handler and fails node.
#   max_cycles         Integer   Max supervision cycles (for house/manager_loop nodes).
#   stop_condition_key String    Context key to check for stop (manager_loop, default: "manager.stop").
#   human_default_choice String  Default choice on human gate timeout.

# ═══════════════════════════════════════════════════════════════════════════
# CODING AGENT ATTRIBUTES (tab shape only)
# ═══════════════════════════════════════════════════════════════════════════
#
#   max_turns          Integer   Maximum agent turns (default: 20)
#   max_tool_rounds    Integer   Maximum tool rounds per input (default: 25)
#   command_timeout    Duration  Timeout per shell command (default: "120s")
#   system_prompt      String    System instructions for the agent session
#   acp_preset         String    ACP stdio preset: codex | claude-code | gemini.
#                                Preset defaults command/args/working directory/timeout;
#                                explicit acp_* node attributes still override them.

# ═══════════════════════════════════════════════════════════════════════════
# EDGE ATTRIBUTES (inside `node_a -> node_b [...]`)
# ═══════════════════════════════════════════════════════════════════════════
#
#   label              String    Display caption and routing key.
#                                Accelerator key formats: "[A] Approve", "R) Reject", "Y - Yes"
#   condition          String    Boolean guard expression. Edge only taken if true.
#                                Syntax: LHS=RHS | LHS!=RHS | clause && clause
#                                Variables: outcome (success|fail|retry|partial_success|skipped)
#                                           preferred_label
#                                           context.<key> (resolves from context, missing = "")
#                                Examples: "outcome=success"
#                                          "outcome=fail"
#                                          "outcome=success && context.tests_passed=true"
#   weight             Integer   Priority for edge selection (higher wins). Default: 0
#   fidelity           String    Override fidelity mode for target node (edge > node > graph).
#   thread_id          String    Override thread ID for target node.
#   loop_restart       Boolean   If true, restart pipeline with fresh logs and context.

# FAN-OUT (multi-edge)
#
# When a node has multiple outgoing edges that all match the same condition,
# OR multiple unconditional outgoing edges, the engine executes all target
# nodes sequentially before advancing to the common fan-in successor.
#
# The fan-in node is the first outgoing-edge target of the first branch.
# Authors should ensure all branches converge to the same fan-in node, or
# the validator's fanout_fan_in_ambiguous warning will fire.
#
# loop_restart is IGNORED on edges that participate in fan-out.
# For true concurrent/isolated execution, use shape=parallel instead.

# ═══════════════════════════════════════════════════════════════════════════
# EDGE SELECTION PRIORITY (5-step algorithm)
# ═══════════════════════════════════════════════════════════════════════════
#
#   1. Condition match   — edges whose condition evaluates to true
#   2. Preferred label   — edge whose label matches outcome.preferred_label
#   3. Suggested IDs     — edge whose target is in outcome.suggested_next_ids
#   4. Highest weight    — among unconditional edges, highest weight wins
#   5. Lexical tiebreak  — alphabetically first target node ID

# ═══════════════════════════════════════════════════════════════════════════
# VALIDATION RULES (checked by --validate)
# ═══════════════════════════════════════════════════════════════════════════
#
#   - Exactly one start node (shape=Mdiamond)
#   - Exactly one exit node (shape=Msquare)
#   - Start node has no incoming edges
#   - Exit node has no outgoing edges
#   - All nodes reachable from start (error)
#   - Every node has a path to a terminal node (error — pipeline will hang)
#   - Non-terminal nodes have outgoing edges (error — dead end)
#   - All edge targets reference existing nodes
#   - Edge condition expressions parse correctly
#   - Stylesheet syntax is valid
#   - Fidelity mode values are recognized
#   - Retry targets reference existing nodes
#   - Goal gate nodes should have retry targets (warning)
#   - Retry target chains have no cycles (warning)
#   - Codergen nodes should have prompt or label (warning)
#   - loop_session_pollution: coding_agent with static thread_id reachable from loop_restart will saturate session budget across iterations (warning)
#   - conflicting_session_attrs: fresh_session=true cannot be combined with explicit thread_id (error)
#   - scope_gate_coverage: file-editing coding_agent can reach commit-like node without passing a scope-check tool gate (warning)
#   - partial_commit_needs_build_gate: fail/partial edge to commit-like node is missing an intermediate build/test gate (warning)
#   - parallelogram_needs_timeout: every tool/parallelogram node should set timeout to avoid wedged pipeline hangs (warning)
#   - fanout_fan_in_ambiguous: implicit fan-out branches converge to different first successors (warning)
#   - validate_measure_only: validation prompt mixes measure commands with in-node fix-loop instructions (warning)
#   - review_gate_first_line_strict: strict anchored grep gate lacks upstream prompt requiring exact first-line token output (warning)
#   - scratch_path_consistency: .ai scratch slug appears at multiple paths across prompts/tool_command usage (warning)
#   - terminal_exit_on_empty_backlog: Pick/ledger backlog gate routes outcome=fail to Exit and can report cosmetic failure (info)
#   - Synopsis: classifies pipeline as EXECUTION/PLANNING/HYBRID/ANALYSIS

# ═══════════════════════════════════════════════════════════════════════════
# ARTIFACTS (written to logs_root during execution)
# ═══════════════════════════════════════════════════════════════════════════
#
#   {logs_root}/manifest.json          Pipeline metadata (name, goal, start time)
#   {logs_root}/checkpoint.json        Crash recovery state (current node, context, etc.)
#   {logs_root}/{node_id}/prompt.md    LLM prompt sent to this stage
#   {logs_root}/{node_id}/response.md  LLM response received
#   {logs_root}/{node_id}/status.json  Outcome: status, context_updates, notes
#   {logs_root}/{node_id}/tool_output.txt  Full tool output (for tool nodes)
#   {logs_root}/{node_id}/tool_stderr.txt  Tool stderr (if any)
#   {logs_root}/restart-{N}/          Fresh logs directory after loop_restart

# ═══════════════════════════════════════════════════════════════════════════
# NOTES
# ═══════════════════════════════════════════════════════════════════════════
#
# Comment stripping respects quoted strings — // and /* inside "..."
# are preserved. URLs like "https://example.com" and globs like
# "find . -name '*.go'" work correctly in tool_command values.
#
# Checkpoint CLI:
#   attractor checkpoint inspect <run-dir>
#   attractor checkpoint mark-done <run-dir> <node-id> [--outcome=success|fail] [--note=...] [--no-backup]
#   attractor checkpoint set-outcome <run-dir> <node-id> <outcome> [--tool-stdout=...] [--no-backup]
#   attractor checkpoint diff <run-dir>
#   attractor checkpoint backup <run-dir>
# ═══════════════════════════════════════════════════════════════════════════"""

let printExample () =
    printfn
        """// Example: Multi-stage code review pipeline with human approval
//
// This pipeline plans a task, implements it, runs tests, and asks a human
// to approve. If tests fail, it loops back to implement. If the human
// rejects, it loops back to plan.
//
// Run:   attractor pipeline.dot --auto-approve
// Validate: attractor --validate pipeline.dot
// Render:   dot -Tpng pipeline.dot -o pipeline.png

digraph code_review {
    graph [
        goal="Implement and review a new feature",
        label="Code Review Pipeline",
        model_stylesheet="* { llm_model: claude-sonnet-5; } .critical { llm_model: claude-opus-4-6; }",
        default_max_retry=3
    ]

    start [shape=Mdiamond]
    done  [shape=Msquare]

    plan [
        shape=box,
        prompt="Create a detailed implementation plan for: $goal. List the files to create or modify, the changes needed, and the test strategy."
    ]

    implement [
        shape=box,
        class="critical",
        prompt="Implement the plan. Write the actual code changes. Goal: $goal",
        goal_gate=true,
        retry_target="plan"
    ]

    run_tests [
        shape=parallelogram,
        tool_command="echo 'Running tests...' && dotnet test 2>&1 | tail -5",
        timeout="60s"
    ]

    check_tests [shape=diamond, label="Tests Pass?"]

    review [
        shape=hexagon,
        label="Human Review"
    ]

    fix [
        shape=box,
        prompt="The tests failed or the reviewer requested changes. Fix the issues. Goal: $goal",
        max_retries=2
    ]

    start -> plan -> implement -> run_tests -> check_tests

    check_tests -> review     [label="Pass", condition="outcome=success", weight=10]
    check_tests -> fix        [label="Fix",  condition="outcome=fail"]
    fix -> run_tests

    review -> done            [label="[A] Approve"]
    review -> plan            [label="[R] Revise"]
}"""

let printModels () =
    let models = ModelCatalog.listModels ()
    let byProvider = models |> List.groupBy (fun m -> m.Provider) |> List.sortBy fst
    printfn "Known models for llm_model= (use either the canonical ID or any alias):"
    printfn ""

    for (provider, list) in byProvider do
        printfn "# %s (llm_provider=%s)" (provider.ToUpperInvariant()) provider

        for m in list |> List.sortBy (fun m -> m.Id) do
            let aliases =
                match m.Aliases with
                | [] -> ""
                | xs -> xs |> String.concat ", " |> sprintf " (aliases: %s)"

            printfn "  %-38s %s%s" m.Id m.DisplayName aliases

        printfn ""

    printfn "Reference in DOT:"
    printfn "  node_id [shape=box, llm_model=\"claude-sonnet-5\"]"
    printfn "  model_stylesheet=\"* { llm_model: gpt-5.6; } .planner { llm_model: claude-opus-4-7; }\""

// ============================================================================
// Core CLI logic
// ============================================================================

let parseArgs (args: string array) =
    let mutable dotFile = None
    let mutable logsRoot = None
    let mutable validateOnly = false
    let mutable resumeDir = None
    let mutable autoApprove = false
    let mutable showHelp = false
    let mutable showSchema = false
    let mutable showExample = false
    let mutable showModels = false
    let mutable showVersion = false
    let mutable simulate = false
    let mutable quiet = false
    let mutable explicitVerbose = false
    let mutable trace = None
    let mutable cache = false
    let mutable cacheDir = None
    let mutable servePort = None
    let mutable i = 0

    while i < args.Length do
        match args[i] with
        | "--help"
        | "-h" -> showHelp <- true
        | "--logs" when i + 1 < args.Length ->
            logsRoot <- Some args[i + 1]
            i <- i + 1
        | "--validate" -> validateOnly <- true
        | "--resume" when i + 1 < args.Length ->
            resumeDir <- Some args[i + 1]
            i <- i + 1
        | "--auto-approve" -> autoApprove <- true
        | "--simulate" -> simulate <- true
        | "--quiet"
        | "-q" -> quiet <- true
        | "--verbose" -> explicitVerbose <- true
        | "--trace" when i + 1 < args.Length ->
            trace <- Some args[i + 1]
            i <- i + 1
        | "--cache" -> cache <- true
        | "--cache-dir" when i + 1 < args.Length ->
            cacheDir <- Some args[i + 1]
            cache <- true
            i <- i + 1
        | "--version" -> showVersion <- true
        | "serve" ->
            if servePort.IsNone then
                servePort <- Some 8080
        | "--port" when i + 1 < args.Length ->
            match Int32.TryParse(args[i + 1]) with
            | true, value when value > 0 && value <= 65535 ->
                servePort <- Some value
                i <- i + 1
            | _ ->
                eprintfn "Invalid --port value: %s" args[i + 1]
                showHelp <- true
                i <- i + 1
        | "schema"
        | "--schema" -> showSchema <- true
        | "example"
        | "--example" -> showExample <- true
        | "models"
        | "--models" -> showModels <- true
        | arg when not (arg.StartsWith("-", StringComparison.Ordinal)) && dotFile.IsNone -> dotFile <- Some arg
        | arg ->
            eprintfn "Unknown argument: %s" arg
            showHelp <- true

        i <- i + 1

    (dotFile,
     logsRoot,
     validateOnly,
     resumeDir,
     autoApprove,
     showHelp,
     showSchema,
     showExample,
     showModels,
     showVersion,
     simulate,
     quiet,
     explicitVerbose,
     trace,
     cache,
     cacheDir,
     servePort)

let validate (source: string) =
    let graph = Pipeline.parseOrRaise source
    let diags = Pipeline.validate graph

    let errors = diags |> List.filter (fun d -> d.Severity = Severity.Error)
    let warnings = diags |> List.filter (fun d -> d.Severity = Severity.Warning)
    let infos = diags |> List.filter (fun d -> d.Severity = Severity.Info)
    let informational = infos |> List.filter (fun d -> d.Rule <> "synopsis")

    // Print errors, warnings, and non-synopsis informational diagnostics
    for d in errors do
        let nodeStr = if d.NodeId <> "" then $" ({d.NodeId})" else ""
        printfn "  [ERROR] %s%s: %s" d.Rule nodeStr d.Message

    for d in warnings do
        let nodeStr = if d.NodeId <> "" then $" ({d.NodeId})" else ""
        printfn "  [WARN] %s%s: %s" d.Rule nodeStr d.Message

    for d in informational do
        let nodeStr = if d.NodeId <> "" then $" ({d.NodeId})" else ""
        printfn "  [INFO] %s%s: %s" d.Rule nodeStr d.Message

    if errors.IsEmpty && warnings.IsEmpty && informational.IsEmpty then
        printfn "  No issues found."

    printfn ""
    printfn "Nodes: %d | Edges: %d | Goal: %s" graph.Nodes.Count graph.Edges.Length graph.Goal

    // Print synopsis
    let synopsisLines = infos |> List.filter (fun d -> d.Rule = "synopsis")

    if not synopsisLines.IsEmpty then
        printfn ""
        printfn "Synopsis:"

        for d in synopsisLines do
            printfn "  %s" d.Message

    printfn ""

    if not errors.IsEmpty then
        eprintfn "%d error(s) found. Fix before running." errors.Length
        ExitValidationError
    else
        ExitSuccess

/// Create a handler registry with LLM backend if keys are available
let private configureClientMiddleware (client: UnifiedLlm.Client) =
    let validator = RequestValidator.fromCatalog ()
    let ledger = CostLedger.inMemory ()
    sharedCostLedger <- Some ledger

    let consoleSink =
        if verbose then
            ObservabilitySink.console true
        else
            ObservabilitySink.none

    let sink =
        match tracePath with
        | Some path -> ObservabilitySink.combine [ consoleSink; ObservabilitySink.jsonLines path ]
        | None -> consoleSink

    sharedObservabilitySink <- sink

    client.AddMiddlewareFn(Middleware.validation validator sink)
    client.AddMiddlewareFn(Middleware.circuitBreaker CircuitBreakerConfig.Default sink)

    if cacheEnabled then
        let root =
            cacheDirectory
            |> Option.defaultValue (Path.Combine(Environment.CurrentDirectory, ".fkyeah-cache"))

        let store =
            CacheStore.fileSystem
                { CacheConfig.Default with
                    PersistencePath = Some root }

        client.AddMiddlewareFn(Middleware.cache store sink)

    client.AddMiddlewareFn(Middleware.observability sink (Some ledger))

let makeRegistry (autoApprove: bool) (simulate: bool) : Result<HandlerRegistry, int> =
    let interviewer: IInterviewer =
        if autoApprove then
            AutoApproveInterviewer() :> IInterviewer
        else
            ConsoleInterviewer() :> IInterviewer

    let acpPermissionStrategy =
        if autoApprove then
            AcpRuntime.PermissionStrategy.AutoApprove
        else
            AcpRuntime.PermissionStrategy.DenyAll

    let anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
    let openaiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    let geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
    let hasAnthropic = not (String.IsNullOrEmpty(anthropicKey))
    let hasOpenai = not (String.IsNullOrEmpty(openaiKey))
    let hasGemini = not (String.IsNullOrEmpty(geminiKey))

    if simulate then
        eprintfn "  Simulation mode (--simulate)"
        Ok(HandlerRegistry.CreateDefault(interviewer = interviewer, acpPermissionStrategy = acpPermissionStrategy))
    elif hasAnthropic || hasOpenai || hasGemini then
        let llmClient = UnifiedLlm.Client()
        configureClientMiddleware llmClient

        if hasAnthropic then
            eprintfn "  Registered Anthropic adapter (ANTHROPIC_API_KEY)"
            llmClient.RegisterAdapter(UnifiedLlm.AnthropicAdapter(anthropicKey))

        if hasOpenai then
            eprintfn "  Registered OpenAI adapter (OPENAI_API_KEY)"
            llmClient.RegisterAdapter(UnifiedLlm.OpenAIAdapter(openaiKey))

        if hasGemini then
            eprintfn "  Registered Gemini adapter (GEMINI_API_KEY)"
            llmClient.RegisterAdapter(UnifiedLlm.GeminiAdapter(geminiKey))

        let backend = LlmBackend(llmClient) :> ICodergenBackend
        eprintfn "  Using live LLM backend"

        Ok(
            HandlerRegistry.CreateDefault(
                interviewer = interviewer,
                backend = backend,
                llmClient = llmClient,
                acpPermissionStrategy = acpPermissionStrategy
            )
        )
    else
        eprintfn
            "Error: No API keys found. Set ANTHROPIC_API_KEY, OPENAI_API_KEY, or GEMINI_API_KEY — or pass --simulate."

        eprintfn ""
        eprintfn "  attractor <file.dot> --simulate          # run with mock LLM responses"
        eprintfn "  export ANTHROPIC_API_KEY=sk-...           # or set an API key for live calls"
        Result.Error ExitConfigError

let run (source: string) (logsRoot: string) (autoApprove: bool) (simulate: bool) =
    match makeRegistry autoApprove simulate with
    | Result.Error code -> code
    | Ok registry ->

        let emitter = EventEmitter()
        emitter.AddObserver(ConsoleEventObserver())

        if verbose then
            let graph = Pipeline.parseOrRaise source
            eprintfn "  Pipeline: %s | Goal: %s" graph.Name graph.Goal
            eprintfn "  Nodes: %d | Edges: %d | Logs: %s" graph.Nodes.Count graph.Edges.Length logsRoot

        let config =
            { LogsRoot = logsRoot
              Registry = registry
              EventEmitter = emitter
              ExtraTransforms = []
              InitialContextValues = Map.empty }

        let result = Pipeline.runFromSource source config

        printfn ""

        match result.FinalOutcome.Status with
        | StageStatus.Success ->
            printfn "Result: SUCCESS"
            printfn "Completed stages: %s" (result.CompletedNodes |> String.concat " -> ")
            sharedCostLedger |> Option.iter (fun ledger -> printfn "%s" (ledger.Summary()))
            printfn "Logs: %s" logsRoot
            ExitSuccess
        | StageStatus.PartialSuccess ->
            printfn "Result: PARTIAL SUCCESS"
            printfn "Completed stages: %s" (result.CompletedNodes |> String.concat " -> ")
            sharedCostLedger |> Option.iter (fun ledger -> printfn "%s" (ledger.Summary()))
            printfn "Logs: %s" logsRoot
            ExitSuccess
        | _ ->
            eprintfn "Result: FAILED"
            eprintfn "Reason: %s" result.FinalOutcome.FailureReason
            eprintfn "Completed stages: %s" (result.CompletedNodes |> String.concat " -> ")
            eprintfn "Logs: %s" logsRoot
            ExitPipelineFailure

let resume (logsRoot: string) (dotFile: string option) (autoApprove: bool) (simulate: bool) =
    match Engine.loadCheckpoint logsRoot with
    | None ->
        eprintfn "No checkpoint found in %s" logsRoot
        ExitConfigError
    | Some checkpoint ->
        match dotFile with
        | None ->
            eprintfn "Specify a DOT file when resuming: attractor <file.dot> --resume <logs-dir>"
            ExitConfigError
        | Some f ->
            match makeRegistry autoApprove simulate with
            | Result.Error code -> code
            | Ok registry ->

                let source = File.ReadAllText(f)
                let (graph, _) = Transforms.preparePipeline source None

                let emitter = EventEmitter()
                emitter.AddObserver(ConsoleEventObserver())

                let config =
                    { LogsRoot = logsRoot
                      Registry = registry
                      EventEmitter = emitter
                      ExtraTransforms = []
                      InitialContextValues = Map.empty }

                printfn
                    "Resuming from checkpoint at node '%s' (%d nodes completed)"
                    checkpoint.CurrentNode
                    checkpoint.CompletedNodes.Length

                let result = Engine.resumeFromCheckpoint graph config checkpoint

                printfn ""

                match result.FinalOutcome.Status with
                | StageStatus.Success ->
                    printfn "Result: SUCCESS"
                    printfn "Completed stages: %s" (result.CompletedNodes |> String.concat " -> ")
                    ExitSuccess
                | _ ->
                    eprintfn "Result: FAILED"
                    eprintfn "Reason: %s" result.FinalOutcome.FailureReason
                    ExitPipelineFailure

type PendingQuestion =
    { Id: string
      Question: Question
      Completion: TaskCompletionSource<Answer> }

type HttpInterviewer() =
    let pending = ConcurrentDictionary<string, PendingQuestion>()

    member _.PendingQuestions = pending.Values |> Seq.sortBy (fun q -> q.Id) |> Seq.toList

    member _.TryAnswer(questionId: string, answer: string) =
        match pending.TryRemove(questionId) with
        | true, question ->
            let normalized = answer.Trim()

            let resolved =
                match
                    question.Question.Options
                    |> List.tryFind (fun o ->
                        o.Key.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                        || o.Label.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                with
                | Some opt -> Answer.FromOption(opt)
                | None -> Answer.FromText(normalized)

            question.Completion.TrySetResult(resolved)
        | false, _ -> false

    interface IInterviewer with
        member _.Ask(question) =
            let questionId = Guid.NewGuid().ToString("N")

            let completion =
                TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously)

            let pendingQuestion =
                { Id = questionId
                  Question = question
                  Completion = completion }

            pending[questionId] <- pendingQuestion

            let timeoutMs =
                question.TimeoutSeconds
                |> Option.map (fun s -> int (s * 1000.0))
                |> Option.defaultValue Timeout.Infinite

            let completed = completion.Task.Wait(timeoutMs)

            if completed then
                completion.Task.Result
            else
                pending.TryRemove(questionId) |> ignore
                Answer.Timeout

        member this.AskMultiple(questions) =
            questions |> List.map (fun q -> (this :> IInterviewer).Ask(q))

        member _.Inform(_, _) = ()

type PipelineRunRecord =
    { Id: string
      LogsRoot: string
      Interviewer: HttpInterviewer
      EventQueue: ConcurrentQueue<string>
      mutable Status: string
      mutable CurrentNode: string
      mutable FinalStatus: string
      mutable FailureReason: string
      mutable ContextSnapshot: Map<string, string> }

type ServerEventObserver(run: PipelineRunRecord) =
    interface IEventObserver with
        member _.OnEvent(evt) =
            let json =
                match evt with
                | PipelineEvent.PipelineStarted(name, id) ->
                    run.Status <- "running"

                    JsonSerializer.Serialize(
                        box
                            {| kind = "PipelineStarted"
                               name = name
                               id = id |}
                    )
                | PipelineEvent.StageStarted(name, index) ->
                    run.CurrentNode <- name

                    JsonSerializer.Serialize(
                        box
                            {| kind = "StageStarted"
                               name = name
                               index = index |}
                    )
                | PipelineEvent.StageCompleted(name, index, duration) ->
                    JsonSerializer.Serialize(
                        box
                            {| kind = "StageCompleted"
                               name = name
                               index = index
                               duration_ms = int duration.TotalMilliseconds |}
                    )
                | PipelineEvent.StageFailed(name, index, error, willRetry) ->
                    JsonSerializer.Serialize(
                        box
                            {| kind = "StageFailed"
                               name = name
                               index = index
                               error = error
                               will_retry = willRetry |}
                    )
                | PipelineEvent.CheckpointSaved(nodeId) ->
                    JsonSerializer.Serialize(
                        box
                            {| kind = "CheckpointSaved"
                               node_id = nodeId |}
                    )
                | PipelineEvent.PipelineCompleted(duration, count) ->
                    run.Status <- "completed"
                    run.FinalStatus <- "success"

                    JsonSerializer.Serialize(
                        box
                            {| kind = "PipelineCompleted"
                               duration_ms = int duration.TotalMilliseconds
                               completed = count |}
                    )
                | PipelineEvent.PipelineFailed(error, duration) ->
                    run.Status <- "failed"
                    run.FinalStatus <- "failed"
                    run.FailureReason <- error

                    JsonSerializer.Serialize(
                        box
                            {| kind = "PipelineFailed"
                               error = error
                               duration_ms = int duration.TotalMilliseconds |}
                    )
                | _ -> JsonSerializer.Serialize(box {| kind = string evt |})

            run.EventQueue.Enqueue(json)

let readRequestBody (req: HttpListenerRequest) =
    use reader = new StreamReader(req.InputStream, req.ContentEncoding)
    reader.ReadToEnd()

let writeJson (resp: HttpListenerResponse) (statusCode: int) (payload: obj) =
    let json =
        JsonSerializer.Serialize(payload, JsonSerializerOptions(WriteIndented = true))

    let bytes = Encoding.UTF8.GetBytes(json)
    resp.StatusCode <- statusCode
    resp.ContentType <- "application/json"
    resp.ContentEncoding <- Encoding.UTF8
    resp.OutputStream.Write(bytes, 0, bytes.Length)
    resp.OutputStream.Close()

let writeText (resp: HttpListenerResponse) (statusCode: int) (contentType: string) (body: string) =
    let bytes = Encoding.UTF8.GetBytes(body)
    resp.StatusCode <- statusCode
    resp.ContentType <- contentType
    resp.ContentEncoding <- Encoding.UTF8
    resp.OutputStream.Write(bytes, 0, bytes.Length)
    resp.OutputStream.Close()

let serve (port: int) =
    let listener = new HttpListener()
    listener.Prefixes.Add($"http://127.0.0.1:{port}/")
    listener.Start()
    printfn "Attractor server listening on http://127.0.0.1:%d" port

    let runs = ConcurrentDictionary<string, PipelineRunRecord>()

    let startRun (dot: string) (simulate: bool) (autoApprove: bool) =
        match makeRegistry autoApprove simulate with
        | Result.Error code -> Result.Error code
        | Result.Ok registry ->
            let id = Guid.NewGuid().ToString("N")
            let logsRoot = Path.Combine(".ai", "attractor-logs", id)
            let interviewer = HttpInterviewer()

            let run =
                { Id = id
                  LogsRoot = logsRoot
                  Interviewer = interviewer
                  EventQueue = ConcurrentQueue<string>()
                  Status = "queued"
                  CurrentNode = ""
                  FinalStatus = ""
                  FailureReason = ""
                  ContextSnapshot = Map.empty }

            if not (runs.TryAdd(id, run)) then
                Result.Error ExitConfigError
            else
                Task.Run(fun () ->
                    try
                        let emitter = EventEmitter()
                        emitter.AddObserver(ServerEventObserver(run))

                        let config =
                            { LogsRoot = logsRoot
                              Registry = registry
                              EventEmitter = emitter
                              ExtraTransforms = []
                              InitialContextValues = Map.empty }

                        let result = Pipeline.runFromSource dot config
                        run.ContextSnapshot <- result.Context.Snapshot()
                        run.FinalStatus <- result.FinalOutcome.Status.ToString()

                        if
                            result.FinalOutcome.Status <> StageStatus.Success
                            && result.FinalOutcome.Status <> StageStatus.PartialSuccess
                        then
                            run.Status <- "failed"
                            run.FailureReason <- result.FinalOutcome.FailureReason
                        elif run.Status <> "failed" then
                            run.Status <- "completed"
                    with ex ->
                        run.Status <- "failed"
                        run.FailureReason <- ex.Message

                    ())
                |> ignore

                Result.Ok run

    let rec loop () =
        let ctx = listener.GetContext()
        let req = ctx.Request
        let resp = ctx.Response

        let pathSegments =
            req.Url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        try
            match req.HttpMethod, pathSegments with
            | "POST", [ "pipelines" ] ->
                let body = readRequestBody req
                let doc = JsonDocument.Parse(body)
                let root = doc.RootElement
                let dot = root.GetProperty("dot").GetString()

                let simulate =
                    let mutable v = Unchecked.defaultof<JsonElement>
                    not (root.TryGetProperty("simulate", &v) && v.ValueKind = JsonValueKind.False)

                let autoApprove =
                    let mutable v = Unchecked.defaultof<JsonElement>
                    root.TryGetProperty("auto_approve", &v) && v.ValueKind = JsonValueKind.True

                match startRun dot simulate autoApprove with
                | Result.Ok run ->
                    writeJson
                        resp
                        202
                        (box
                            {| id = run.Id
                               status = run.Status
                               logs_root = run.LogsRoot |})
                | Result.Error code ->
                    writeJson
                        resp
                        400
                        (box
                            {| error = "failed to start pipeline"
                               code = code |})

            | "GET", [ "pipelines"; id ] ->
                match runs.TryGetValue(id) with
                | true, run ->
                    writeJson
                        resp
                        200
                        (box
                            {| id = run.Id
                               status = run.Status
                               current_node = run.CurrentNode
                               final_status = run.FinalStatus
                               failure_reason = run.FailureReason
                               logs_root = run.LogsRoot
                               context = run.ContextSnapshot |})
                | false, _ -> writeJson resp 404 (box {| error = "pipeline not found" |})

            | "GET", [ "pipelines"; id; "events" ] ->
                match runs.TryGetValue(id) with
                | true, run ->
                    resp.StatusCode <- 200
                    resp.ContentType <- "text/event-stream"
                    resp.Headers.Add("Cache-Control", "no-cache")
                    resp.Headers.Add("Connection", "keep-alive")
                    use writer = new StreamWriter(resp.OutputStream, Encoding.UTF8)
                    let mutable keepStreaming = true

                    while keepStreaming do
                        let mutable evt = ""

                        if run.EventQueue.TryDequeue(&evt) then
                            writer.Write("data: ")
                            writer.Write(evt)
                            writer.Write("\n\n")
                            writer.Flush()
                        else
                            Thread.Sleep(200)

                            if run.Status = "completed" || run.Status = "failed" then
                                keepStreaming <- false

                    resp.OutputStream.Close()
                | false, _ -> writeJson resp 404 (box {| error = "pipeline not found" |})

            | "POST", [ "pipelines"; id; "cancel" ] ->
                match runs.TryGetValue(id) with
                | true, run ->
                    UnifiedLlm.HttpCancellation.cancel ()
                    run.Status <- "cancelled"
                    writeJson resp 202 (box {| id = id; status = "cancelled" |})
                | false, _ -> writeJson resp 404 (box {| error = "pipeline not found" |})

            | "GET", [ "pipelines"; id; "questions" ] ->
                match runs.TryGetValue(id) with
                | true, run ->
                    let questions =
                        run.Interviewer.PendingQuestions
                        |> List.map (fun q ->
                            {| id = q.Id
                               stage = q.Question.Stage
                               text = q.Question.Text
                               options = q.Question.Options |})

                    writeJson
                        resp
                        200
                        (box
                            {| pipeline_id = id
                               questions = questions |})
                | false, _ -> writeJson resp 404 (box {| error = "pipeline not found" |})

            | "POST", [ "pipelines"; id; "questions"; qid; "answer" ] ->
                match runs.TryGetValue(id) with
                | true, run ->
                    let body = readRequestBody req
                    let doc = JsonDocument.Parse(body)
                    let answer = doc.RootElement.GetProperty("answer").GetString()

                    if run.Interviewer.TryAnswer(qid, answer) then
                        writeJson
                            resp
                            200
                            (box
                                {| pipeline_id = id
                                   question_id = qid
                                   status = "answered" |})
                    else
                        writeJson resp 404 (box {| error = "question not found" |})
                | false, _ -> writeJson resp 404 (box {| error = "pipeline not found" |})

            | _ -> writeJson resp 404 (box {| error = "not found" |})
        with ex ->
            writeJson resp 500 (box {| error = ex.Message |})

        loop ()

    try
        loop ()
        ExitSuccess
    finally
        listener.Stop()

[<EntryPoint>]
let main args =
    if
        args.Length > 0
        && String.Equals(args[0], "checkpoint", StringComparison.OrdinalIgnoreCase)
    then
        Checkpoint.dispatch (args |> Array.skip 1)
    else

        // Handle Ctrl-C: first press cancels gracefully, second press force-exits
        let mutable cancelCount = 0

        Console.CancelKeyPress.Add(fun e ->
            cancelCount <- cancelCount + 1
            e.Cancel <- true
            eprintfn ""

            if cancelCount >= 2 then
                eprintfn "  Force quit."
                Environment.Exit(130)
            else
                eprintfn "  Interrupted (Ctrl-C). Cancelling in-flight LLM calls..."
                eprintfn "  Press Ctrl-C again to force quit."
                CodingAgent.AutoCheckpointRegistry.saveAll ()
                UnifiedLlm.HttpCancellation.cancel ())

        let (dotFile,
             logsRoot,
             validateOnly,
             resumeDir,
             autoApprove,
             showHelp,
             showSchema,
             showExample,
             showModels,
             showVersion,
             simulate,
             quiet,
             explicitVerbose,
             trace,
             cache,
             cacheDir,
             servePort) =
            parseArgs args

        if quiet then
            verbose <- false
        elif explicitVerbose then
            verbose <- true

        tracePath <- trace
        cacheEnabled <- cache
        cacheDirectory <- cacheDir

        if showSchema then
            printSchema ()
            ExitSuccess
        elif showExample then
            printExample ()
            ExitSuccess
        elif showModels then
            printModels ()
            ExitSuccess
        elif showVersion then
            printfn "%s" cliVersion
            ExitSuccess
        elif servePort.IsSome then
            serve servePort.Value
        elif showHelp || (dotFile.IsNone && resumeDir.IsNone) then
            printUsage ()
            if showHelp then ExitSuccess else ExitConfigError
        else

            try
                match resumeDir with
                | Some dir -> resume dir dotFile autoApprove simulate
                | None ->
                    let file = dotFile.Value

                    if not (File.Exists(file)) then
                        eprintfn "File not found: %s" file
                        ExitConfigError
                    else
                        let source = File.ReadAllText(file)

                        if validateOnly then
                            validate source
                        else
                            let logs =
                                logsRoot
                                |> Option.defaultWith (fun () ->
                                    Path.Combine(
                                        ".ai",
                                        "attractor-logs",
                                        DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")
                                    ))

                            run source logs autoApprove simulate
            with
            | :? OperationCanceledException ->
                eprintfn ""
                eprintfn "Aborted by user."
                130
            | :? AggregateException as ae when
                ae.InnerExceptions |> Seq.exists (fun e -> e :? OperationCanceledException)
                ->
                eprintfn ""
                eprintfn "Aborted by user."
                130
            | ex ->
                eprintfn "Error: %s" ex.Message
                ExitPipelineFailure
