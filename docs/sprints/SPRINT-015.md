# Sprint 015: Loop-Aware Engine Primitives & First-Class Shim CLI

**Status:** Ready
**Codebase:** `src/Attractor/Engine.fs`, `src/Attractor/Types.fs`, `src/Attractor/Handlers.fs`, `src/Attractor.Cli/Program.fs`, `examples/`
**Depends on:** SPRINT-014 (validator rules assume these primitives exist — landing S015 first makes S014's `loop_session_pollution` and gate-coverage warnings actionable)

## Motivation

SPRINT-014 adds validator warnings for the pitfalls that bit the cedarverse `TASK-001` run — loop-iteration session pollution, missing scope gates, unguarded partial commits, cosmetic `Pipeline FAILED`. Warnings are necessary but not sufficient: every warning today forces the pipeline author to *hand-wire* the fix (bump `thread_id="review-v2"` each phase, duplicate CheckScope after every Fix* node, add a CheckBuildBeforePartial parallelogram, rename ledger sentinels). That's mechanical work the engine should do.

This sprint lands the engine primitives that turn those warnings into *structural* invariants, and formalises the external-shim recovery recipe as a real CLI instead of a Python one-liner that mutates checkpoint JSON.

Two engine changes alone (per-iteration `thread_id` interpolation + built-in subtask loop) would have kept cedarverse `TASK-001` entirely inside attractor with zero shim recoveries. Everything else below is defense-in-depth and operator ergonomics.

## Phases

### Phase 1: Context interpolation for attribute values (`Engine.fs`, `Types.fs`)

**Problem.** `thread_id="review"` is a static string. A loop that runs the review node once per iteration reuses the same session on every pass; after ~2 iterations the session saturates and Opus taps out in <10s without writing a review file. The only workarounds today are (a) edit the DOT between runs (`review` → `review-v2`), (b) drop `thread_id` entirely and lose cross-turn scratch memory.

**Fix.** Resolve `${context.<key>}` / `${internal.<key>}` references inside attribute values at node-execution time, immediately before the attribute is consumed by the handler. Scope the resolver to a whitelist of attributes to keep semantics predictable:

- `thread_id` — primary use case (per-iteration sessions)
- `prompt` — variable expansion for phase-aware prompts
- `cwd` — per-iteration working directories
- `tool_command` — already supports some expansion via shell; keep consistent

Use `${...}` syntax (Bash-like) rather than `$...` to avoid collision with existing `$goal` graph-level expansion. Reserved prefixes inside the braces: `context.` (reads from `Context.Get`), `internal.` (reads from internal counters like `loop_restart_count`, `node.visit_count`), plain names default to `context.` for ergonomics.

**Implementation sketch.**

Add in `Engine.fs` near the top of `Engine`:

```fsharp
let private interpolationPattern =
    System.Text.RegularExpressions.Regex(@"\$\{([a-zA-Z0-9_.]+)\}", RegexOptions.Compiled)

let private interpolateAttr (context: Context) (rawValue: string) : string =
    interpolationPattern.Replace(rawValue, fun m ->
        let key = m.Groups.[1].Value
        let lookup =
            if key.StartsWith("internal.") || key.StartsWith("context.") then key
            else "context." + key
        context.Get(lookup) |> Option.defaultValue m.Value  // leave literal if unresolved
    )
```

Apply it at the session-resolution site in `Engine.fs:630-632` (the existing `thread_id` setter):

```fsharp
if fidelity = FidelityMode.Full && node.ThreadId <> "" then
    let resolved = interpolateAttr context node.ThreadId
    context.Set("thread_id", resolved)
```

And in the handler-prompt pathway (wherever `node.Prompt` reaches the LLM adapter) — find the call sites via `grep -n "node\.Prompt\b\|\.Prompt\s*=" src/Attractor/Handlers.fs src/Attractor.*/`.

**Example pipeline usage:**

```dot
// Per-iteration review session — saturates within an iteration, fresh next iteration.
OpusReview [
    shape="tab",
    class="reviewer",
    thread_id="review-${internal.loop_restart_count}",
    max_turns="40",
    ...
];

// Per-node-visit implement session for retry isolation (turns reset on each retry).
Implement [
    shape="tab",
    thread_id="impl-${internal.loop_restart_count}-v${node.Implement.visit_count}",
    ...
];
```

**Tests:**
- Unit: `interpolateAttr` resolves `${internal.loop_restart_count}` to the current counter.
- Unit: Unresolved references (`${nonexistent}`) are left literal in output.
- Unit: Escaped literal `$${foo}` stays as `${foo}` (escape rule).
- Integration: A DOT with `thread_id="review-${internal.loop_restart_count}"` produces distinct session keys across two loop iterations (inspect the run's session-checkpoint or thread dir names).
- Regression: Pipelines without `${...}` produce byte-identical thread_id handling to HEAD.

### Phase 2: `fresh_session` attribute (`Types.fs`, `Engine.fs`)

**Problem.** Phase 1 gives precise per-iteration control but requires authors to think about which counter to interpolate. For the common case — "always start fresh; I don't want this session reused under any circumstance" — a single declarative attribute is simpler and less error-prone than `thread_id="x-${internal.loop_restart_count}-${node.visit_count}-${node.foo.visit_count}"`.

**Fix.** Add `fresh_session="true"` as a node attribute. When set, the engine generates a unique session key per invocation (e.g. `<node_id>-<utc-epoch-ms>-<pid>`) and ignores `thread_id`. Mutually exclusive with `thread_id` — validator should error if both are set (cross-reference SPRINT-013 `attribute_known` / emit a new error-severity `conflicting_session_attrs` diagnostic, both candidates fit that rule).

**Implementation:**

- Add accessor to `Types.fs` next to `ThreadId` at line 271:
  ```fsharp
  member this.FreshSession =
      this.Attributes
      |> Map.tryFind "fresh_session"
      |> Option.map (fun v -> v.AsString() = "true")
      |> Option.defaultValue false
  ```
- Add `"fresh_session"` to `KnownAttributes.node` (from SPRINT-013's Phase 1 registry).
- In `Engine.fs:630`, gate the thread_id setter: if `node.FreshSession`, override with a generated ephemeral id before `Context.Set`.

**Tests:**
- Unit: Accessor returns `false` by default, `true` when `fresh_session="true"`.
- Integration: Two runs of the same node produce distinct thread_id values in the logs.
- Conformance: Validator emits `conflicting_session_attrs` error when both `thread_id` and `fresh_session="true"` are set on the same node.

### Phase 3: Structural safety attributes — `scope_gate` and `requires_green_build` (`Types.fs`, `Engine.fs`)

**Problem.** SPRINT-014 warns when a file-editing node reaches commit without a scope gate on the path, or when a partial-commit edge bypasses a build gate. The warning is correct, but forcing authors to author *parallelogram check + parallelogram revert + route-to-and-from-fix* wiring for every file-editing node is verbose and error-prone (five nodes of boilerplate per gate, easy to skip on the second one).

**Fix.** Make the gate a first-class node attribute. The engine runs it as an implicit step bracketing the node's own execution, with well-defined outcome handling.

#### 3a. `scope_gate="<command>"`

Semantics: After the node's primary execution completes with `outcome=success`, the engine shells out to `<command>`. If it exits non-zero, the engine runs an implicit revert step (either the companion `scope_revert="<command>"` attribute, or a default `git checkout HEAD -- $(git diff --name-only | diff -c .ai/sprint_scope.txt | ...)`) and retries the primary execution up to `scope_gate_max_retries` (default 1). If still red, the node is marked `outcome=fail`.

#### 3b. `requires_green_build="<command>"`

Semantics: Runs BEFORE the node's primary execution. If non-zero exit, the node is not entered at all and the engine emits `outcome=fail` with a clear "pre-condition failed" reason. Attach to the terminal commit node so a red tree never gets committed, regardless of which upstream path reached commit.

**Implementation:**

Add both attributes to `Types.fs` near existing accessors, add them to `KnownAttributes.node`, and hook the execution wrapper in `Engine.executeWithRetry` at `Engine.fs:257` (wrap the handler call with pre/post bracket runs). Keep the implicit shell-out path consistent with how `parallelogram tool_command` executes today (same timeout handling, same stdout/stderr capture into `context.tool_output` / `context.tool_stderr`).

**Example:**

```dot
FixFailures [
    shape="tab",
    scope_gate="bash docs/check_scope.sh",
    scope_revert="bash docs/revert_offscope.sh",
    ...
];

CommitAndSummarize [
    shape="tab",
    requires_green_build="go build ./... && go vet ./... && go test -count=1 -run=^$ ./...",
    ...
];
```

Two attributes replace 4–6 parallelogram nodes + edge-routing boilerplate per pipeline.

**Tests:**
- Integration: A node with `scope_gate` that touches an out-of-scope file has the change auto-reverted and the primary retries once.
- Integration: A node with `requires_green_build` on a red tree is skipped with `outcome=fail`; context carries the build output.
- Integration: Both attributes unset → byte-identical behaviour to HEAD.
- Conformance: SPRINT-014's `scope_gate_coverage` warning is suppressed when a downstream file-editing node has `scope_gate` set (the rule considers the attribute-based gate as coverage).

### Phase 4: First-class `attractor checkpoint` CLI (`Program.fs`, `Engine.fs`)

**Problem.** The external-shim recovery recipe ends with hand-patching checkpoint JSON in Python. The patch has to touch top-level `current_node`, `completed_nodes`, `node_outcomes[<name>]`, *and* `context.outcome` — miss one and attractor's resume behaviour is subtly wrong (we lost two resumes to `context.current_node` vs top-level `current_node` confusion alone).

**Fix.** Add `attractor checkpoint` as a subcommand tree, with subcommands that enforce the invariants across all four fields atomically:

```
attractor checkpoint inspect <run-dir>
    Pretty-print the checkpoint: completed_nodes, current_node, per-node
    outcomes, any pending retries. Accepts bare run-dir OR restart-N subdir.

attractor checkpoint mark-done <run-dir> <node-id> [--outcome=success|fail] [--note="..."]
    Append to completed_nodes, set top-level current_node, populate
    node_outcomes[<node-id>] with status + notes + a default context_updates
    block. Default outcome=success.

attractor checkpoint set-outcome <run-dir> <node-id> <outcome> [--tool-stdout=...]
    Update an existing node_outcome entry. For parallelogram-gate fakery
    (e.g. forcing CheckReview to success after a manual APPROVED file write).
    Sets context.outcome + context.tool_* fields consistently.

attractor checkpoint diff <run-dir>
    Show what's changed vs the on-disk .dot file (e.g. warn if a node that
    appears in completed_nodes no longer exists in the graph).

attractor checkpoint backup <run-dir>
    Write checkpoint.json.bak. Run automatically by mark-done/set-outcome
    unless --no-backup is passed.
```

All subcommands operate in-place on the given run directory's `checkpoint.json` (auto-detect `restart-N/` subdirs — pick the one with the latest mtime unless user specifies a path explicitly). Each mutation writes a `.bak` first. Output is always valid JSON (no comment banner) so the file round-trips through attractor's own loader.

**Implementation:**

Add a new module `Attractor.Cli.Checkpoint` (new file `src/Attractor.Cli/Checkpoint.fs`). The CLI dispatcher at `Program.fs:~1306` gains a `"checkpoint"` branch that delegates to the module. Reuse `Engine.loadCheckpoint` at `Engine.fs:903` for reads.

For `mark-done`, write the exact field pattern our cedarverse shim used (tested and known-good):

```fsharp
let markDone (runDir: string) (nodeId: string) (outcome: Outcome) (note: string) =
    let chk = Engine.loadCheckpoint runDir |> Option.get
    chk.CompletedNodes <- chk.CompletedNodes @ [ nodeId ]
    chk.CurrentNode <- nodeId
    chk.NodeOutcomes <-
        Map.add nodeId
            { Status = outcome;
              ContextUpdates = Map.ofList [
                "last_response", $"Marked done via checkpoint CLI: {note}";
                "last_stage", nodeId;
              ];
              FailureReason = ""; Notes = note; PreferredLabel = "";
              SuggestedNextIds = [] }
            chk.NodeOutcomes
    chk.Context <- chk.Context
        |> Map.add "outcome" (outcome.ToString())
        |> Map.add "last_stage" nodeId
    Engine.saveCheckpoint runDir chk
```

The CLI makes every hand-patched scenario from cedarverse one line: `attractor checkpoint mark-done .ai/attractor-logs/20260423-215054/restart-2 Implement --note "codex shim"`.

**Tests:**
- Unit: `mark-done` mutates all four fields atomically (completed_nodes, current_node, node_outcomes, context).
- Unit: `set-outcome` with `--tool-stdout` populates context.tool_output correctly.
- Unit: `inspect` on a fresh run and a post-mark-done run produces machine-readable JSON (for use in scripts).
- Conformance: Round-trip test — patch a checkpoint, resume, verify the resume picks up at the expected node.
- Regression: `checkpoint backup` creates a valid JSON file identical to the original.

### Phase 5: Built-in subtask-loop pattern (`examples/`, optional `Program.fs`)

**Problem.** The "Production SDLC with grind loop" example in `examples/` and in the `attractor` skill bakes in *one commit per phase*. When phases are big, the Implement node's turn budget gets exhausted and the whole iteration retries / shims. In cedarverse `TASK-001`, five of seven phases were too large for any finite `max_turns`. The root cause isn't the engine — it's that the canonical template conflates `phase` with `commit`.

**Fix (pattern-level, no core-engine change needed):** Ship a new canonical example, `examples/sdlc_grind_subtask.dot`, that inverts the structure:

```
outer loop (per phase):
  Plan          -> PlanDecompose  -> PickSubtask
                                   ↑          ↓
                                 (back-edge) Implement -> Validate -> Review -> Commit
                                             (inner loop runs per subtask,
                                              each producing one commit)
  PickSubtask (empty) -> PhaseComplete -> (outer back-edge) Plan
```

Key features of the new example:

- `Plan` produces `.ai/sprint_plan.md` AND `.ai/sprint_subtasks/01-*.md`, `02-*.md`, ... (one per commit-sized unit).
- `PickSubtask` is a parallelogram that picks the next `.ai/sprint_subtasks/NN-*.md`, renames it to `.ai/sprint_current_subtask.md`, removes the original. Exits 1 when none remain.
- `Implement` reads `.ai/sprint_current_subtask.md` (NOT the full plan) — narrow, commit-sized context.
- The inner loop commits per subtask, so partial phase progress is never thrown away on Implement failure.
- Thread IDs use Phase 1 interpolation: `thread_id="impl-${context.current_phase}-${context.current_subtask}"`.

**Optional:** Add `attractor scaffold sdlc-grind [--subtasks]` CLI that generates the example pre-wired for a repo (discover the language, generate appropriate validation commands, populate `docs/ledger.py` template, etc.). Keep this out of P5's DoD if it balloons — it's a standalone sprint if prioritised.

**Tests:**
- The new `examples/sdlc_grind_subtask.dot` passes `attractor --validate`.
- Conformance test: A small fixture repo with a 3-subtask plan runs end-to-end, producing 3 commits.
- Docs: `SKILL.md` (at `/Users/Ryan.Harris/.claude/skills/attractor/SKILL.md`) gets a pointer to the new example in the "Common Patterns" section, positioning it as the *preferred* SDLC pattern over the older whole-phase-per-commit template.

## Definition of Done

- [ ] `${context.<key>}` / `${internal.<key>}` interpolation for `thread_id`, `prompt`, `cwd`, `tool_command` attribute values
- [ ] Escape rule (`$${foo}` → literal `${foo}`) documented in spec
- [ ] `fresh_session="true"` node attribute wired through `Engine.executeWithRetry`
- [ ] `conflicting_session_attrs` validator error when `thread_id` + `fresh_session="true"` both set
- [ ] `scope_gate` / `scope_revert` node attributes with engine-run bracket + 1-retry default
- [ ] `requires_green_build` node attribute running BEFORE primary execution, skipping on non-zero
- [ ] `Attractor.Cli.Checkpoint` module + `attractor checkpoint {inspect, mark-done, set-outcome, diff, backup}` subcommands
- [ ] Every mutation auto-creates a `.bak` (opt-out via `--no-backup`)
- [ ] `attractor checkpoint mark-done` round-trips through `Engine.loadCheckpoint` → resume correctly
- [ ] `examples/sdlc_grind_subtask.dot` canonical pattern passes `--validate` with no warnings
- [ ] `examples/sdlc_grind_subtask.dot` integration test: 3-subtask fixture produces 3 commits end-to-end
- [ ] SPRINT-014's validator rules updated to recognise the new attributes (scope_gate + requires_green_build suppress `scope_gate_coverage` / `partial_commit_needs_build_gate` warnings)
- [ ] `attractor schema` output lists the new attributes + CLI subcommand tree
- [ ] Spec docs `docs/spec/` updated: attribute interpolation semantics; fresh_session semantics; scope_gate execution order; checkpoint CLI reference
- [ ] All existing tests pass
- [ ] Regression: all existing `examples/*.dot` pipelines produce byte-identical run behaviour to HEAD (no new attributes set on them → no new behaviour)
- [ ] Zero compiler warnings

## Ordering notes

Phases 1–4 are independent and can be parallelised / picked off in any order. Phase 5 benefits from Phase 1 (subtask-loop example uses interpolated thread_id) but can ship without it by hard-coding a per-phase thread_id bump.

If time pressure forces a sub-sprint, the highest-ROI slice is **Phase 1 + Phase 4**: per-iteration `thread_id` resolves cedarverse's primary failure mode (5 of 7 phases), and the checkpoint CLI formalises the fallback recovery path for the remaining cases. Phases 2, 3, 5 are additive polish.

## Reference

Inputs to this sprint:
- `SPRINT-014.md` — validator rules this sprint's primitives are meant to satisfy structurally
- `/Users/Ryan.Harris/.claude/skills/attractor/SKILL.md` v2.1 — tribal knowledge the engine primitives are meant to obsolete
- cedarverse `TASK-001` branch `riblt-sync-plan` at `$HOME/go/src/github.com/delinea/cedarverse` — the source incident
