# Sprint 005: Test Coverage Hardening (Codex Draft)

**Status:** Draft  
**Specs:** `attractor-spec.md`, `unified-llm-spec.md`, `coding-agent-loop-spec.md`  
**Codebase:** tests in `tests/` + E2E in `conformance/` (no production code changes)

## Overview

The implementation stack is now feature-complete and green:
- `make test` = 384 unit tests
- `make conformance` = 128 conformance tests

`docs/feedback.md` identifies a prioritized set of remaining QA gaps (A–D). This sprint closes those gaps **with tests only** — unit tests (F# xUnit) and/or conformance tests (shell E2E). Any production defects discovered by new tests are documented and the failing test is marked `Skip` with a clear bug note (so the sprint remains tests-only).

> Note: `docs/feedback.md` describes “28 holes” but enumerates A1–A10 (10), B1–B9 (9), C1–C6 (6), D1–D4 (4) = 29. Sprint kickoff should reconcile whether one item is already covered or out of scope.

## Sprint Principles (Established Style)

- **Concrete, spec-tied tests**: every new test maps to a named gap ID + spec section.
- **No stubs / no hand-waving**: tests must validate observable behavior, not just type presence.
- **Deterministic**: conformance tests should use `--simulate` unless the gap requires live keys (then use `require_env` + skip).
- **Minimal harness changes**: small `conformance/lib.sh` helpers are allowed, but no engine/adapter changes this sprint.

## Use Cases (What This Sprint Makes Safer)

1. **Durable runs**: checkpoint/resume is proven E2E, not just via unit tests. (A2)
2. **Cost controls**: prompt caching behavior and usage accounting are locked in. (B1)
3. **Real coding workflows**: `apply_patch` works end-to-end and is regression-covered. (C1)
4. **Spec features are E2E real**: stylesheet routing, fidelity projection, `$goal` transforms, `max_visits`, and hooks are exercised in pipelines. (A1/A3/A5/A8/A10)
5. **Agent realism**: multi-round tool loops and streaming-with-tools are tested, not implied. (C3/C4)

## Implementation Plan

### Phase 1 (P0): Must-Have Coverage

#### Task 1.1: A2 — Checkpoint resume conformance test
**Spec:** attractor-spec §5, §11.7  
**Target:** new `conformance/03-execution/16-checkpoint-resume/`

- [ ] Add a pipeline with at least 2 non-trivial stages (prefer **tool** stages for determinism).
- [ ] In `test.sh`, run `attractor` in the background with `--logs "$LOGS_DIR"` and poll `checkpoint.json` until stage 1 completes.
- [ ] Kill the pipeline process (simulated “crash”), then run `attractor --resume "$LOGS_DIR" pipeline.dot --simulate --auto-approve`.
- [ ] Assert:
  - [ ] resume run exits success
  - [ ] previously-completed nodes are not re-executed (artifact timestamps or visit dirs unchanged)
  - [ ] final artifacts match a clean baseline run in a separate logs dir

**Definition of Done:**
- Conformance test is deterministic under `--simulate` and passes locally + in CI.

#### Task 1.2: B1 — Prompt caching unit tests (expand from 1 → spec baseline)
**Spec:** unified-llm-spec §2.10  
**Target:** `tests/UnifiedLlm.Tests/Tests.fs` (new module `PromptCachingTests`)

- [ ] Verify Anthropic `cache_control` breakpoints are injected **by default** (not only “auto_cache=false disables”).
- [ ] Verify `Usage.CacheReadTokens` and `Usage.CacheWriteTokens` mapping from mock Anthropic usage JSON.
- [ ] Verify OpenAI `Usage.CacheReadTokens` mapping from `prompt_tokens_details.cached_tokens`.
- [ ] Verify Gemini `Usage.CacheReadTokens` mapping from `cachedContentTokenCount`.

**Definition of Done:**
- New tests cover the full mapping + default-injection behavior and do not require live keys.

#### Task 1.3: C1 — `apply_patch` executor unit tests (create/update/delete + failure modes)
**Spec:** coding-agent-loop-spec §3.4  
**Target:** `tests/CodingAgent.Tests/Tests.fs` (new module `ApplyPatchExecutorTests`)

- [ ] End-to-end dispatch: model returns tool call `{ name = "apply_patch" }` and Session applies changes in a temp working dir.
- [ ] Cover:
  - [ ] Add file
  - [ ] Update file (hunk replace)
  - [ ] Delete file
  - [ ] Invalid patch (missing header/footer) returns tool error (not crash)
  - [ ] Update missing file produces a clear error

**Definition of Done:**
- Tests validate on-disk results and error messaging contracts.

---

### Phase 2 (P1): Close the Next Tier of Gaps

#### Task 2.1: A1 — Stylesheet conformance
**Spec:** attractor-spec §8  
**Target:** new `conformance/03-execution/17-model-stylesheet/` (fills numbering gap A7 at the same time)

- [ ] Pipeline uses `graph [model_stylesheet="..."]` to assign models by selector (`*`, shape, `.class`, `#id`).
- [ ] Assert model selection is visible in artifacts (prompt metadata or stage manifest fields).
- [ ] Ensure node-level override beats stylesheet.

**Definition of Done:**
- Conformance proves stylesheet routing E2E and fills `03-execution/17-*` gap.

#### Task 2.2: A3 — Fidelity projection conformance
**Spec:** attractor-spec §3.5  
**Target:** `conformance/04-context/07-fidelity-projection/` (or `03-execution/19-*` if execution-only is preferred)

- [ ] Pipeline passes a large context value through an edge with `fidelity=Truncate`.
- [ ] Assert downstream `prompt.md` does **not** contain the full value, only the truncated projection marker/shape expected by the engine.

#### Task 2.3: A5 — `$goal` expansion conformance
**Spec:** attractor-spec §9  
**Target:** `conformance/03-execution/19-goal-expansion/` (or `04-context/08-goal-expansion/`)

- [ ] Pipeline sets `graph [goal="Build X"]` and uses `$goal` in a codergen prompt.
- [ ] Assert `prompt.md` contains the expanded goal text.

#### Task 2.4: A8 — `max_visits` conformance
**Spec:** attractor-spec §11.3 (cycle safety)  
**Target:** `conformance/03-execution/20-max-visits/`

- [ ] Pipeline contains a deliberate back-edge loop with `max_visits=3`.
- [ ] Assert pipeline fails after 3 visits with an actionable failure reason.

#### Task 2.5: B8 — Usage addition Some + None case
**Spec:** unified-llm-spec §3.9  
**Target:** `tests/UnifiedLlm.Tests/Tests.fs` (table-driven micro-tests)

- [ ] Add the missing case: `Some 5 + None = Some 5` (treat `None` as 0).

#### Task 2.6: B9 — FinishReason provider mapping table tests
**Spec:** unified-llm-spec §3.8  
**Target:** `tests/UnifiedLlm.Tests/Tests.fs`

- [ ] Table-driven test: provider raw reason → normalized `FinishReason` (Anthropic + Gemini mapping list from feedback).

#### Task 2.7: C3 — Multi-step tool loop tests (3+ rounds)
**Spec:** coding-agent-loop-spec §9 (agent loop realism)  
**Target:** `tests/CodingAgent.Tests/Tests.fs`

- [ ] Create a mock adapter that returns: tool call → tool call → final assistant text.
- [ ] Assert:
  - [ ] 3+ rounds execute
  - [ ] history ordering is correct (`UserTurn`, `AssistantTurn`, `ToolResultsTurn`, …)
  - [ ] tool result content influences subsequent round (assert request body contains tool output)

---

### Phase 3 (P2): Robustness and Completeness (Stretch, But Preferably Green)

#### Task 3.1: A10 — Tool hooks conformance (pre/post)
**Spec:** attractor-spec §9.7  
**Target:** `conformance/03-execution/21-tool-hooks/`

- [ ] Pipeline includes a tool stage that runs `echo`, with `tool_hooks.pre` and `tool_hooks.post` set to commands that write marker files into `$LOGS_DIR`.
- [ ] Assert both hook markers exist and contain expected env vars (`TOOL_NAME`, `NODE_ID`, etc).

#### Task 3.2: A9 — FreeText + MultiSelect gate conformance
**Spec:** attractor-spec interviewer question types  
**Target:** `conformance/03-execution/22-gate-question-types/`

- [ ] Pipeline contains a FreeText gate and a MultiSelect gate.
- [ ] Under `--auto-approve`, assert artifacts are written (prompt/response) and selection parsing works for comma-separated choices.

#### Task 3.3: A6 — Parallel failure/timeout conformance
**Spec:** attractor-spec parallel semantics  
**Target:** `conformance/05-parallel/03-branch-failure/` + `04-branch-timeout/`

- [ ] Failure: one branch fails, another succeeds; assert fan-in semantics match spec (pipeline outcome + artifacts).
- [ ] Timeout: one branch times out; assert bounded termination and correct outcome.

#### Task 3.4: A4 — Manager loop conformance
**Spec:** attractor-spec §3.9  
**Target:** `conformance/03-execution/23-manager-loop/`

- [ ] Pipeline uses `manager_loop` node supervising a subordinate stage.
- [ ] Assert max-cycles behavior, stop-key exit behavior, and steering injection artifacts.

#### Task 3.5: B6/B7 — RateLimit + Warnings population tests
**Spec:** unified-llm-spec §3.11, §3.12  
**Target:** `tests/UnifiedLlm.Tests/Tests.fs`

- [ ] RateLimitInfo: parse headers in mock adapter and assert surfaced on `Response.RateLimit`.
- [ ] Warnings: ensure `Response.Warnings` is preserved through middleware + returned to callers.

#### Task 3.6: B3/B4/B5 — Audio/Document, abort, timeout tests (may require skip gates)
**Spec:** unified-llm-spec §3.5, §4.3  
**Target:** `tests/UnifiedLlm.Tests/Tests.fs`

- [ ] Audio/Document: construction + round-trip serialization tests.
- [ ] Abort: cancellation respected mid-operation (if adapter/client surfaces cancellation hooks).
- [ ] Timeout: SDK-level timeout enforcement (if currently implemented).

**Policy:** If the runtime lacks the necessary control points without production changes, add the test as `[<Fact(Skip="...")>]` with a bug ticket reference.

#### Task 3.7: C4/C5/C6 — Streaming-with-tools, full-output contract, edit_file fuzzy matching
**Spec:** coding-agent-loop-spec §2.9, §3.3  
**Target:** `tests/CodingAgent.Tests/Tests.fs`

- [ ] Streaming + tool calls: validate event sequences and state transitions.
- [ ] Full vs truncated output: strengthen existing coverage into table-driven cases across tools.
- [ ] edit_file fuzzy matching: verify behavior if implemented; otherwise document gap with a skipped test.

---

### Phase 4 (D): Cross-Cutting Conformance (Likely Defer / Design-Only)

These are valuable, but large and may not fit a tests-only sprint without harness work:
- **D1**: “library-level conformance” for `UnifiedLlm` and `CodingAgent` (currently conformance goes via CLI).
- **D2**: crash / I/O / malformed-response recovery E2E.
- **D3/D4**: expand model and coding-agent conformance matrices beyond codegen.

**Sprint posture:** produce an executable test design doc section (what to add, where, and why), then open follow-up sprint intent if needed.

## Files Summary (Expected Touchpoints)

| Area | Files/Dirs | Action |
|------|------------|--------|
| UnifiedLlm unit tests | `tests/UnifiedLlm.Tests/Tests.fs` | Add modules for B1/B6/B7/B8/B9 (+ optional B3–B5) |
| CodingAgent unit tests | `tests/CodingAgent.Tests/Tests.fs` | Add modules for C1/C3 (+ optional C4–C6) |
| Attractor conformance | `conformance/03-execution/16-*` … | Add new test directories for A1/A2/A5/A8/A9/A10 (+ optional A4/A6) |
| Conformance helpers | `conformance/lib.sh` | Add helper assertions only if needed (compare dirs, wait-for-checkpoint, etc.) |

## Definition of Done

- [ ] Every **P0** gap (A2, B1, C1) has at least one green test.
- [ ] Every **P1** gap listed in `docs/feedback.md` has at least one green test.
- [ ] Most **P2** gaps have at least one test (green preferred; skip allowed only when production changes would be required).
- [ ] `make test` passes.
- [ ] `make conformance` passes.
- [ ] Each new test references its gap ID in its name or module header (e.g., `A2`, `B1`).

## Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| “Tests only” finds real production bugs | Medium | High | Mark failing tests `Skip` with a bug note; do not fix prod code this sprint |
| Resume conformance flakiness (timing) | Medium | Medium | Use tool stages + checkpoint polling + generous timeouts; avoid LLM variability via `--simulate` |
| Some gaps require new hooks/injection seams | Medium | Medium | Document with skipped tests + follow-up intent; do not breach tests-only constraint |
| Conformance env variance (macOS/Linux) | Low | Medium | Prefer POSIX tooling; avoid GNU-only flags; keep shell scripts portable |

## Dependencies

- Built on top of SPRINT-004 completion baseline (all prior gap closures assumed present).
- Conformance requires `make install` (publishes and installs `~/bin/attractor`).
