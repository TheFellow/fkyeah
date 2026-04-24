# Sprint 014: Loop-Aware Pipeline Validation & External-Shim Recovery

**Status:** Ready
**Codebase:** `src/Attractor/Validation.fs`, `src/Attractor/Types.fs`, `docs/spec/`
**Depends on:** SPRINT-013 (KnownAttributes registry + attribute_known rule — reused here)

## Motivation

A 7-phase SDLC grind pipeline (cedarverse `TASK-001` — RIBLT incremental sync, 15 commits) shipped end-to-end but burned five external-CLI "shim" recoveries, two manual `APPROVED` review injections, and four hand-patched checkpoints on the way. Every recovery traced back to a runtime footgun the validator does not currently warn about:

| Incident | Root cause | Current validator? |
|----------|-----------|---------------------|
| P0 Validate node exhausted 50 turns on fix-loop, wrote no report | Validate prompt mixed measure + fix | not caught |
| P3/P4/P5/P6 Implement retried within 30–60s every attempt | `thread_id="implement"` saturated across `loop_restart` iterations | `cumulative_turns` only checks single-pass, not loop |
| P3 FixFailures added off-plan edits that reached commit | Scope gate only wired after Implement, not after Fix* nodes | not caught |
| Hypothetical partial commit of red build | "give up — commit partial" edge had no build-green gate | not caught |
| P5/P6 OpusReview wrote no review file, pipeline routed to FixReviewIssues | Review thread saturated; `grep -q '^APPROVED$'` gate flunked on empty file | not caught |
| `Pipeline FAILED` at end-of-backlog confused operators | Terminal `PickTask` exits 1 on empty queue; cosmetic failure | not caught |

The existing `cumulative_turns`, `low_max_turns`, `parallelogram_outcome_routing`, and `tool_node_llm_invocation` rules cover the first generation of pitfalls. This sprint extends the validator to the second generation: loop-aware session tracking, safety-gate coverage, prompt-antipattern detection, and a documented external-shim recovery protocol.

## Phases

### Phase 1: Loop-aware session pollution warning (`Validation.fs`)

Extend `cumulativeTurnsRule` at `Validation.fs:974` so it counts turns *across* `loop_restart` back-edges, not just through forward edges. A node with `thread_id="implement"` that is reachable from a `loop_restart="true"` edge will re-use the same session on every iteration; after two or three loops, any `max_turns` budget is effectively zero.

Current behaviour at `Validation.fs:985-1053` BFS walks forward from Start, adds `max_turns` along the path for nodes sharing an empty `thread_id`, and emits `cumulative_turns` when the prior-sum crosses 60. It does not treat `loop_restart="true"` edges differently from other edges and so *undercounts* iterations.

Add a new rule `loop_session_pollution` (do not modify `cumulativeTurnsRule` — the single-pass warning is still valuable on its own):

```fsharp
let loopSessionPollutionRule: ILintRule =
    { new ILintRule with
        member _.Name = "loop_session_pollution"

        member _.Apply(graph) =
            // 1. Find all coding_agent nodes with a non-empty thread_id
            //    that are reachable from at least one loop_restart="true" edge.
            // 2. For each, emit a warning that the session is shared across
            //    loop iterations and will saturate after ~2 iterations at the
            //    node's max_turns budget.
            // 3. Suggest one of: (a) drop thread_id, (b) bump to a new
            //    thread_id per expected iteration count, (c) accept the cap.
            ...
    }
```

Detection sketch:
1. Collect the set of edges where `edge.LoopRestart = true`.
2. For each `loop_restart` edge `(u, v)`, BFS forward from `v` until the loop closes; collect the set `reachableInLoop` of nodes on the cycle.
3. For each node `n ∈ reachableInLoop` that is a `coding_agent` AND has a non-empty `thread_id`, emit the warning.
4. Suppress the warning when the `thread_id` value contains `${internal.loop_restart_count}` or any interpolation marker (forward-compatible for a future attractor feature).

Example output:
```
warning[loop_session_pollution] node "Implement": thread_id="implement" is shared across loop iterations (reachable from loop_restart edge CommitAndSummarize->PickTask). After ~2 iterations the session saturates and max_turns=80 is effectively 0.
  fix: Remove thread_id (fresh session per iteration) OR use a per-iteration id (bump between long runs).
```

**Tests:**
- Conformance: DOT with `Implement [thread_id="impl"]` on a `loop_restart` cycle emits `loop_session_pollution`.
- Conformance: Same node without `thread_id` does NOT emit.
- Conformance: Node with `thread_id` that is NOT reachable from a loop_restart (e.g. a one-shot preamble) does NOT emit.
- Conformance: Multiple agent nodes on the same loop emit one warning each.
- Unit: `reachableInLoop` computation terminates on self-loops.

### Phase 2: Safety-gate coverage warnings (`Validation.fs`)

Three new rules, each catching a distinct class of "missing gate" that allowed bad state to reach commit in the reference incident.

#### 2a. `scope_gate_coverage`

File-editing agent nodes (`coding_agent` shape whose prompt mentions `create`/`modify`/`implement`/`write`/`edit`) should have a scope-check parallelogram on the forward path before any commit-like node. In the reference pipeline, `Implement` had a CheckScope gate but `FixFailures` and `FixReviewIssues` did not — off-plan edits made inside those fix nodes reached commit un-gated.

Detection:
1. Define `isFileEditingAgent` heuristic: `coding_agent` + prompt regex `(?i)\b(create|modify|implement|write|edit)\b`.
2. Define `isCommitLike` heuristic: `coding_agent` whose prompt contains `git commit` OR `git add`.
3. Define `isScopeGate` heuristic: `parallelogram` whose `tool_command` contains `scope` (e.g. `bash docs/check_scope.sh`, `diff --name-only | ...scope.txt`).
4. For each `isFileEditingAgent` node `n`, walk forward. If *any* path from `n` reaches an `isCommitLike` node *without* passing through an `isScopeGate`, emit `scope_gate_coverage`.

Example:
```
warning[scope_gate_coverage] node "FixFailures": file-editing agent reaches commit-like node "CommitAndSummarize" without passing through a scope gate.
  fix: Add a CheckScope parallelogram (tool_command running a scope-check script) on the edge between "FixFailures" and "CommitAndSummarize".
```

#### 2b. `partial_commit_needs_build_gate`

The pattern `CheckFixResult[outcome=fail] -> CommitAndSummarize` ships a partial implementation to commit. Without a `go build` / `npm run build` / language-appropriate gate in between, that edge can commit a tree that does not compile.

Detection:
1. Find edges with `condition` containing `outcome=fail` and a label containing `partial` OR `give up`.
2. If the edge target is `isCommitLike`, look for an intermediate `parallelogram` whose `tool_command` matches `(go build|npm run build|cargo build|mvn compile|dotnet build|pytest --collect-only|go test .* -run=\^\$)`.
3. If none found, emit `partial_commit_needs_build_gate`.

Example:
```
warning[partial_commit_needs_build_gate] edge "CheckFixResult" -> "CommitAndSummarize" [label="give up — commit partial"]: partial-commit path has no build-green gate. A red build will commit broken code.
  fix: Insert a parallelogram running `go build ./... && go vet ./... && go test -count=1 -run=^$ ./...` (or your language's equivalent), and route fail to an Abort node.
```

#### 2c. `parallelogram_needs_timeout`

A `shape=parallelogram` without a `timeout` attribute can hang the whole pipeline if the shell command wedges (slow test, network call, unresponsive subprocess). The reference pipeline had timeouts on every gate, but the validator didn't enforce it.

Detection: trivial — every `parallelogram` must have `timeout`. If missing, emit a warning with a recommended default based on `tool_command` heuristic:

| Command pattern | Recommended timeout |
|-----------------|---------------------|
| `grep`, `head`, `test -f`, `python3.*ledger` | `10s` |
| `git`, `rg`, small shell scripts | `30s` |
| `go build`, `go test`, `cargo build`, etc. | `300s` |
| anything else | `60s` (with a note to size explicitly) |

**Tests for Phase 2:**
- Conformance: DOT with FixFailures → Commit but no CheckScope in between emits `scope_gate_coverage`.
- Conformance: DOT with CheckFixResult fail → Commit but no build gate emits `partial_commit_needs_build_gate`.
- Conformance: DOT with a scope gate correctly placed emits NEITHER of the above.
- Conformance: Parallelogram without `timeout` emits `parallelogram_needs_timeout`.
- Conformance: Parallelogram with `timeout="10s"` does NOT emit.
- Unit: Heuristic timeout recommender picks `300s` for `go build`, `10s` for `grep`.

### Phase 3: Prompt-antipattern warnings (`Validation.fs`)

#### 3a. `validate_measure_only`

A validator node that both (a) runs build/test/vet commands AND (b) attempts in-node fixes burns its turn budget on the fix loop and leaves no turns to write its report. Downstream CheckValidation then hits a missing report file and routes unpredictably.

Detection:
1. Node is a `coding_agent` whose prompt contains at least one of: `go build`, `go test`, `go vet`, `npm test`, `pytest`, `cargo test`, `mvn test`, `dotnet test`.
2. AND prompt contains a fix hint matching `(?is)if.*fail.*(fix|try|re-?run)` OR `attempt.*fix` OR `try to fix`.
3. Emit `validate_measure_only` with fix suggestion.

Example:
```
warning[validate_measure_only] node "Validate": prompt instructs the agent to both run validation commands AND attempt fixes. Validate should measure-only; fix work belongs in a downstream FixFailures node.
  fix: Rewrite prompt to "Run each command ONCE. Do NOT attempt fixes — write PASS/FAIL and exit. A FixFailures node handles recovery."
```

#### 3b. `review_gate_first_line_strict`

A review parallelogram that uses `grep -q '^APPROVED$'` (exact first-line match with anchors) expects the reviewer to produce `APPROVED` as the exact first line — no markdown fencing, no heading. If the upstream reviewer node's prompt doesn't explicitly say so, any `# Review\n\nAPPROVED\n` output fails the gate.

Detection:
1. Find parallelogram nodes whose `tool_command` matches `grep -q '\^[A-Z]+\$'` (anchored single-word check).
2. Find the upstream `coding_agent` node on the incoming edge chain.
3. If that upstream node's prompt does NOT contain the exact token being matched (e.g. `APPROVED`) *together with* language like "first line" / "line 1" / "exactly" / "on its own line", emit `review_gate_first_line_strict`.

Example:
```
warning[review_gate_first_line_strict] gate "CheckReview" uses strict anchor `grep -q '^APPROVED$'` but upstream reviewer "OpusReview" prompt does not require APPROVED as the exact first line. A valid review starting with a markdown heading will fail the gate.
  fix: In "OpusReview" prompt, add: "The FIRST LINE of the file MUST be exactly `APPROVED` or exactly `REVISE` on its own line — no markdown heading, no leading whitespace."
```

#### 3c. `scratch_path_consistency`

Scratch files (`.ai/*.md`, `.ai/*.txt`) must be written and read at the same paths across all nodes. In practice drift happens: Plan writes `.ai/plan.md`, Reviewer reads `.ai/sprint_plan.md`, CheckBlocked greps `.ai/plan.txt`. All three are different files; the pipeline silently no-ops.

Detection:
1. Extract every path matching `\.ai/[a-zA-Z_]*\.(md|txt)` from every node's `prompt` and `tool_command`.
2. Group by suffix slug (e.g. `plan`, `review`, `commit_msg`, `implementation_status`).
3. If a slug appears with more than one full path across nodes, emit `scratch_path_consistency`.

Example:
```
warning[scratch_path_consistency] scratch file slug "plan" appears as .ai/plan.md in node "ReadAndPlan" but .ai/sprint_plan.md in node "Implement". The two files are unrelated; downstream nodes will find an empty/stale plan.
  fix: Pick one convention (recommend .ai/sprint_plan.md) and use it in every node.
```

**Tests for Phase 3:**
- Conformance: Validate node with fix-loop prompt emits `validate_measure_only`.
- Conformance: Same node with measure-only prompt does NOT emit.
- Conformance: `grep -q '^APPROVED$'` + reviewer prompt without "first line" language emits `review_gate_first_line_strict`.
- Conformance: Inconsistent scratch paths across nodes emit `scratch_path_consistency`.
- Conformance: Consistent scratch paths do NOT emit.
- Unit: Prompt regex does not false-positive on "fix" used in an unrelated sentence ("fix TypeScript typings before").

### Phase 4: Cosmetic-failure awareness + checkpoint anatomy docs

#### 4a. `terminal_exit_on_empty_backlog` (`Validation.fs`)

A common ledger pattern: `PickTask [tool_command="python3 ledger.py next"]` exits 1 when no tasks remain. The pipeline's `PickTask -> Exit [condition="outcome=fail"]` edge routes correctly, but attractor's run-level status reports `Pipeline FAILED: Tool failed with exit code 1`. This misleads operators into thinking a real failure occurred.

Detection:
1. Find `parallelogram` nodes named `Pick*` or with `tool_command` containing `ledger` / `next` / `queue` / `backlog`.
2. That are reachable from `Start`.
3. AND route to an `Exit` node on `condition=outcome=fail`.
4. Emit a note-severity diagnostic suggesting either accepting the cosmetic failure or migrating to a sentinel-string gate.

Example:
```
note[terminal_exit_on_empty_backlog] node "PickTask" routes to Exit on outcome=fail — when the backlog is empty this will report "Pipeline FAILED" even though the pipeline ran correctly.
  fix (optional): Change tool_command to emit a sentinel string (e.g. "NONE") with exit 0, and gate on the sentinel with `grep -vq '^NONE$'`. Alternatively, document the cosmetic failure in the pipeline header.
```

#### 4b. Checkpoint anatomy + external-shim recovery protocol (`docs/spec/`)

Add `docs/spec/checkpoint-anatomy.md` (new file) documenting:
- Layout of `attractor-logs/<run-id>/`, `restart-N/` subdirs, and which `checkpoint.json` a resume actually reads.
- Fields that matter when manually patching: top-level `current_node` vs `context.current_node`, `completed_nodes`, `node_outcomes[<name>]`, `context.outcome`, `context.tool_*`.
- The "resume advances to the successor of `current_node`" semantic.

Add `docs/spec/external-shim-recovery.md` (new file) documenting the recovery protocol for situations where an in-graph agent node cannot self-recover (polluted session, parse errors, provider-side instability):
1. Kill attractor.
2. Confirm plan + scope files are preserved.
3. Run the coding agent externally (`codex exec`, `claude --print`, etc.) against the plan.
4. Verify the tree builds/tests.
5. Patch the checkpoint to mark the in-graph node complete (with example Python).
6. Resume.

Position both docs clearly as *recovery* tools, not design patterns — and reference pitfall #7 of `SKILL.md` to disambiguate from the "NEVER shell out to LLM CLIs" rule (that rule is about `parallelogram tool_command` content; the shim is external tooling outside the graph).

**Tests for Phase 4:**
- Conformance: Terminal `PickTask` → Exit[fail] emits `terminal_exit_on_empty_backlog` (severity note).
- Conformance: A non-terminal ledger check does NOT emit (not routed to Exit).
- Docs: New `docs/spec/checkpoint-anatomy.md` renders correctly (markdown link-check if CI runs one).
- Docs: New `docs/spec/external-shim-recovery.md` includes a working Python snippet that mutates a checkpoint fixture in-tree without corrupting it (smoke test).

### Phase 5: Wire into `builtInRules` and regenerate schema

Append the new rules to `builtInRules` at `Validation.fs:1169`:

```fsharp
let builtInRules: ILintRule list =
    [ ...existing...
      loopSessionPollutionRule
      scopeGateCoverageRule
      partialCommitNeedsBuildGateRule
      parallelogramNeedsTimeoutRule
      validateMeasureOnlyRule
      reviewGateFirstLineStrictRule
      scratchPathConsistencyRule
      terminalExitOnEmptyBacklogRule ]
```

Update `attractor schema` output (in `Program.fs`, see SPRINT-013 Phase 3 for the editing location — likely `printSchema()` around `Program.fs:272-316`) to list the new rule names in the rules section. Include a one-line description per rule so `attractor schema | less` is a complete authoring reference.

**Tests for Phase 5:**
- Conformance: `attractor schema` output mentions every rule name from `builtInRules`.
- Conformance: A pipeline that hits ALL new rules produces the expected set of diagnostics (integration-style fixture).
- Regression: Existing pipelines under `examples/` continue to pass `--validate` with no new errors (new diagnostics are warnings/notes, never errors).

## Definition of Done

- [ ] `loopSessionPollutionRule` implemented with BFS-across-loop_restart detection
- [ ] `scopeGateCoverageRule` with file-editing-agent → commit-like-node path check
- [ ] `partialCommitNeedsBuildGateRule` with heuristic build-command detection
- [ ] `parallelogramNeedsTimeoutRule` with timeout recommendation by command class
- [ ] `validateMeasureOnlyRule` with prompt regex (build/test commands + fix hint)
- [ ] `reviewGateFirstLineStrictRule` cross-checking anchored grep vs reviewer prompt
- [ ] `scratchPathConsistencyRule` with slug-based path grouping
- [ ] `terminalExitOnEmptyBacklogRule` with ledger-pattern heuristic (severity note)
- [ ] All new rules wired into `builtInRules`
- [ ] `attractor schema` lists every rule name with a one-line description
- [ ] `docs/spec/checkpoint-anatomy.md` covers layout, field semantics, resume behaviour
- [ ] `docs/spec/external-shim-recovery.md` covers kill→shim→verify→patch→resume, with a working patch snippet
- [ ] Both docs cross-reference pitfall #7 of `SKILL.md` to disambiguate from in-graph LLM-CLI shelling
- [ ] Conformance tests for every rule (positive + negative cases)
- [ ] Regression: all existing `examples/*.dot` pipelines pass `--validate` unchanged (no new errors; new warnings acceptable)
- [ ] All existing tests pass
- [ ] Zero compiler warnings

## Reference: cedarverse TASK-001 incident log

Artefacts from the source incident live at `$HOME/go/src/github.com/delinea/cedarverse` on branch `riblt-sync-plan`:

| Phase | Cost | Recovery |
|-------|------|----------|
| P0 Measurement spike | ~1h before Validate turn-limit fail | Hand-wrote FAIL report + resumed into FixFailures |
| P1 Scaffolding | clean on the hardened pipeline | — |
| P2 Entities | 3× Implement retries (52m total) | natural retry path; succeeded on attempt 3 |
| P3 Policy groups | Implement retries <2min apart | external codex-CLI shim; patched checkpoint |
| P4 Fork riblt | Implement retries <2min apart | external codex-CLI shim |
| P5 Protobuf+gRPC | Implement retries <2min + OpusReview 0-sec no-op | external codex-CLI shim + manual APPROVED review file |
| P6 Productionisation | Implement retries <2min + OpusReview 0-sec no-op | external codex-CLI shim + manual APPROVED review file |

Four checkpoint hand-patches total; recipes live in `~/.claude/skills/attractor/SKILL.md` (v2.1) under "Checkpoint anatomy" and "External coding-agent shim". This sprint turns those tribal recipes into validator rules + canonical spec docs.
