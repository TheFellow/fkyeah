# Sprint 005: Test Coverage Hardening

**Status:** Planned
**Spec:** `docs/feedback.md` (QA gap analysis), `attractor-spec.md`, `unified-llm-spec.md`, `coding-agent-loop-spec.md`
**Codebase:** `tests/`, `conformance/`, minimal production code if needed for stubs

## Overview

The first four sprints built and conformance-tested the full three-layer stack: UnifiedLlm (105 tests), CodingAgent (95 tests), and Attractor (184 tests), plus 128 conformance tests across 8 categories. A QA audit (`docs/feedback.md`) identified 29 specific gaps organized into P0-P3 priorities across 5 categories (A-E).

This sprint plugs those gaps. The primary deliverable is new tests — unit tests (F# xUnit) and conformance tests (shell E2E). Minimal production code is allowed only where executor stubs need real implementations to make tests meaningful (specifically `apply_patch`). Production bugs discovered by new tests are marked `Skip` with a clear bug note rather than fixed in this sprint.

## Sprint Principles

- **Concrete, spec-tied tests**: every new test maps to a named gap ID (A1, B1, etc.) and spec section
- **Observable behavior, not type presence**: tests must validate behavior, not just that a type compiles
- **Deterministic**: conformance tests use `--simulate` unless the gap requires live API keys (then `require_env` + skip)
- **Minimal harness changes**: small `conformance/lib.sh` helpers are allowed; no engine/adapter changes unless a stub executor needs a real implementation

## Use Cases

1. **Durable runs**: Checkpoint/resume proven E2E — spawn, interrupt mid-run, resume, verify completion without re-running completed stages.
2. **Cost controls**: Prompt caching behavior and usage token mapping locked in across all three providers.
3. **Real coding workflows**: `apply_patch` works end-to-end: create, modify, delete files via v4a diff format.
4. **Spec features are E2E real**: Stylesheet routing, fidelity projection, `$goal` transforms, `max_visits`, tool hooks, gate question types, and manager loop exercised in pipelines.
5. **Agent realism**: Multi-round tool loops (3+ rounds) and streaming-with-tools tested, not implied.

## Implementation Plan

### Phase 1: P0 — Checkpoint Resume Conformance (~10%)

**Gap:** A2
**Spec:** attractor-spec §5, §11.7
**Files:**
- `conformance/03-execution/16-checkpoint-resume/pipeline.dot` (Create)
- `conformance/03-execution/16-checkpoint-resume/test.sh` (Create)
- `conformance/03-execution/16-checkpoint-resume/README.md` (Create)

**Tasks:**
- [ ] Create a 3-node pipeline with tool stages (deterministic under `--simulate`)
- [ ] Test spawns attractor in the background, polls `checkpoint.json` until stage 1 completes
- [ ] Kill the attractor process (simulated crash)
- [ ] Resume with `attractor --resume "$LOGS_DIR" pipeline.dot --simulate --auto-approve`
- [ ] Assert: exit 0, previously-completed nodes not re-executed (artifact timestamps unchanged), final artifacts match a clean baseline run

**Definition of Done:**
- Conformance test deterministic under `--simulate`, passes locally and in CI

### Phase 2: P0 — Prompt Caching Unit Tests (~10%)

**Gap:** B1
**Spec:** unified-llm-spec §2.10
**Files:**
- `tests/UnifiedLlm.Tests/Tests.fs` (Modify — new `PromptCachingTests` module)

**Tasks:**
- [ ] Test: Anthropic adapter injects `cache_control` breakpoints **by default** on system messages
- [ ] Test: `Usage.CacheReadTokens` mapped from mock Anthropic `cache_read_input_tokens`
- [ ] Test: `Usage.CacheWriteTokens` mapped from mock Anthropic `cache_creation_input_tokens`
- [ ] Test: OpenAI `Usage.CacheReadTokens` mapped from `prompt_tokens_details.cached_tokens`
- [ ] Test: Gemini `Usage.CacheReadTokens` mapped from `cachedContentTokenCount`
- [ ] Test: `Usage` addition preserves cache token fields (`Some 5 + None = Some 5`)

### Phase 3: P0 — apply_patch Executor (~10%)

**Gap:** C1
**Spec:** coding-agent-loop-spec §3.4
**Files:**
- `tests/CodingAgent.Tests/Tests.fs` (Modify — new `ApplyPatchExecutorTests` module)
- `src/CodingAgent/ProviderProfile.fs` (Modify — if executor is stub, implement v4a parsing)

**Tasks:**
- [ ] Test: apply_patch creates a new file from v4a patch
- [ ] Test: apply_patch modifies an existing file (context lines + hunk replace)
- [ ] Test: apply_patch deletes a file
- [ ] Test: Invalid patch (missing header/footer) returns error result, not exception
- [ ] Test: Patch targeting non-existent file returns error result
- [ ] If executor is a stub: implement v4a patch parsing and application logic

### Phase 4: P1 — Conformance: Stylesheet (~5%)

**Gap:** A1, A7 (fills numbering gap at `03-execution/17-*`)
**Spec:** attractor-spec §8
**Files:**
- `conformance/03-execution/17-model-stylesheet/pipeline.dot` (Create)
- `conformance/03-execution/17-model-stylesheet/test.sh` (Create)
- `conformance/03-execution/17-model-stylesheet/README.md` (Create)

**Tasks:**
- [ ] Pipeline uses `model_stylesheet` with selectors (`*`, shape, `.class`, `#id`)
- [ ] Assert the chosen model per-node is recorded in stage artifacts (manifest or status.json model field)
- [ ] Assert node-level override beats stylesheet selector

### Phase 5: P1 — Conformance: Fidelity, $goal, max_visits (~10%)

**Gap:** A3, A5, A8
**Files:**
- `conformance/04-context/07-fidelity-projection/` (Create)
- `conformance/03-execution/19-goal-expansion/` (Create)
- `conformance/03-execution/20-max-visits/` (Create)

**Tasks:**
- [ ] **A3 Fidelity**: Pipeline with `fidelity=Truncate` on an edge. Assert downstream `prompt.md` does NOT contain the full upstream context value — only the truncated projection
- [ ] **A5 $goal expansion**: Pipeline with `graph [goal="Build widget"]` and codergen node with `prompt="$goal"`. Assert `prompt.md` contains "Build widget"
- [ ] **A8 max_visits**: Pipeline with a deliberate back-edge loop and `max_visits=3`. Assert pipeline exits non-zero after 3 visits

### Phase 6: P1 — Unit: Multi-step Tool Loop, FinishReason Mapping, Usage Edges (~10%)

**Gap:** C3, B9, B8
**Files:**
- `tests/CodingAgent.Tests/Tests.fs` (Modify)
- `tests/UnifiedLlm.Tests/Tests.fs` (Modify)

**Tasks:**
- [ ] **C3**: Mock adapter returns tool calls for 3 rounds then text. Assert 3 `ToolResultsTurn` entries in history, correct ordering
- [ ] **C3**: 5-round tool loop — verify `total_usage` aggregates across all rounds
- [ ] **B9**: Table-driven FinishReason mapping tests:
  - Anthropic: `end_turn` -> stop, `stop_sequence` -> stop, `max_tokens` -> length, `tool_use` -> tool_calls
  - Gemini: `STOP` -> stop, `MAX_TOKENS` -> length, `SAFETY` -> content_filter, `RECITATION` -> content_filter
- [ ] **B8**: `Usage` addition: `Some 5 + None = Some 5`, `None + Some 10 = Some 10`, both `Some` sums, `ReasoningTokens` same behavior

### Phase 7: P2 — Conformance: Parallel Branch Failure + Timeout (~5%)

**Gap:** A6
**Files:**
- `conformance/05-parallel/03-branch-failure/` (Create)
- `conformance/05-parallel/04-branch-timeout/` (Create)

**Tasks:**
- [ ] **Failure**: One branch uses a tool that exits non-zero, another succeeds. Verify fan-in completes, assert pipeline outcome and per-branch status in checkpoint/manifest (verify actual artifact schema first)
- [ ] **Timeout**: One branch tool exceeds timeout. Assert bounded termination and correct outcome

### Phase 8: P2 — Conformance: Tool Hooks, Gate Types, Manager Loop (~10%)

**Gap:** A10, A9, A4
**Files:**
- `conformance/03-execution/21-tool-hooks/` (Create)
- `conformance/03-execution/22-gate-question-types/` (Create)
- `conformance/03-execution/23-manager-loop/` (Create)

**Tasks:**
- [ ] **A10 Tool hooks**: Pipeline with `tool_hooks.pre` and `tool_hooks.post` writing marker files. Assert markers exist with expected env vars (`TOOL_NAME`, `NODE_ID`)
- [ ] **A9 Gate types**: Pipeline with FreeText and MultiSelect gates under `--auto-approve`. Assert artifacts written (prompt.md/response.md)
- [ ] **A4 Manager loop**: Pipeline with manager node supervising a subordinate stage. Assert max-cycles exit and stop-key exit behaviors

### Phase 9: P2 — Unit: RateLimitInfo, Warnings, Audio/Document, Abort, Timeout (~10%)

**Gap:** B6, B7, B3, B4, B5
**Files:**
- `tests/UnifiedLlm.Tests/Tests.fs` (Modify)

**Tasks:**
- [ ] **B6**: `RateLimitInfo` populated on Response from mock adapter headers — verify surfaced correctly
- [ ] **B7**: `Warning` populated on Response — verify `response.Warnings` is non-empty and accessible
- [ ] **B3**: `AudioData` construction + round-trip (url and data variants). `DocumentData` same
- [ ] **B4**: `generate()` with AbortSignal that fires — verify cancellation (Skip if production seam missing)
- [ ] **B5**: SDK-level `TimeoutConfig` enforcement — verify `TimeoutError` raised (Skip if not yet implemented)

### Phase 10: P2 — Unit: Streaming with Tools, Truncation Contract, edit_file Fuzzy (~10%)

**Gap:** C4, C5, C6, C2
**Files:**
- `tests/CodingAgent.Tests/Tests.fs` (Modify)

**Tasks:**
- [ ] **C4**: Streaming mode with tool calls mid-stream — verify `TOOL_CALL_START`/`TOOL_CALL_END` events between stream segments
- [ ] **C4**: Streaming 2-round tool loop — verify events interleave correctly
- [ ] **C5**: Large tool output → verify LLM receives truncated, TOOL_CALL_END event carries full output. Table-driven across tool types
- [ ] **C6**: `edit_file` with whitespace-normalized match (tabs vs spaces) — verify behavior or document gap with Skip
- [ ] **C2**: Verify OpenAI/Anthropic/Gemini profiles produce correct provider-specific system prompts and tool schemas (spot-check, not exhaustive matrix)

### Phase 11: D-Category — Design Notes (Defer Implementation) (~5%)

**Gap:** D1, D2, D3, D4
**Deliverable:** Section in this sprint doc describing what to add, where, and why. No implementation.

**D1 — Library-level conformance for UnifiedLlm and CodingAgent**
Design: Create `conformance/09-unifiedllm/` and `conformance/10-codingagent/` directories that test the libraries directly via a small F# test harness binary (not the attractor CLI). Requires building a separate test runner.

**D2 — Error/recovery conformance**
Design: Tests that inject failures (malformed LLM responses, I/O errors, network timeouts) into the pipeline via mock adapters. Requires a `--fault-inject` CLI flag or a test-only binary.

**D3 — Model matrix beyond code generation**
Design: Add scenarios 09-reasoning-task, 10-multi-turn-tool-use, 11-thinking-blocks to the 07-models matrix. Requires new pipeline.dot files with non-codegen prompts.

**D4 — Expanded coding agent conformance**
Design: Add scenarios covering shell execution, grep/glob, truncation verification, steering, subagent spawning, timeout handling to 08-coding-agent. Requires 13+ new test directories.

## Files Summary

| File | Action | Purpose |
|------|--------|---------|
| `tests/UnifiedLlm.Tests/Tests.fs` | Modify | ~15 new tests: caching (B1), Usage edges (B8), FinishReason mapping (B9), RateLimitInfo (B6), Warnings (B7), Audio/Document (B3), abort (B4), timeout (B5) |
| `tests/CodingAgent.Tests/Tests.fs` | Modify | ~12 new tests: apply_patch (C1), multi-step loop (C3), streaming (C4), truncation (C5), edit fuzzy (C6), provider parity (C2) |
| `src/CodingAgent/ProviderProfile.fs` | Modify (if needed) | apply_patch executor implementation |
| `conformance/03-execution/16-checkpoint-resume/` | Create | A2: checkpoint resume E2E |
| `conformance/03-execution/17-model-stylesheet/` | Create | A1/A7: stylesheet E2E + numbering gap |
| `conformance/03-execution/19-goal-expansion/` | Create | A5: $goal expansion E2E |
| `conformance/03-execution/20-max-visits/` | Create | A8: max_visits loop prevention E2E |
| `conformance/03-execution/21-tool-hooks/` | Create | A10: tool hooks pre/post E2E |
| `conformance/03-execution/22-gate-question-types/` | Create | A9: FreeText/MultiSelect gate E2E |
| `conformance/03-execution/23-manager-loop/` | Create | A4: manager loop supervision E2E |
| `conformance/04-context/07-fidelity-projection/` | Create | A3: fidelity projection E2E |
| `conformance/05-parallel/03-branch-failure/` | Create | A6: parallel branch failure |
| `conformance/05-parallel/04-branch-timeout/` | Create | A6: parallel branch timeout |

## Definition of Done

- [ ] All P0 gaps (A2, B1, C1) have passing tests
- [ ] All P1 gaps (A1, A3, A5, A7, A8, B8, B9, C3) have passing tests
- [ ] All P2 gaps attempted; green preferred, `Skip` allowed only when production seams are missing
- [ ] D-category design notes documented (no implementation required)
- [ ] ~27 new unit tests added
- [ ] ~11 new conformance test directories added
- [ ] No existing tests broken (384 unit + 128 conformance)
- [ ] Each new test references its gap ID in name or module header
- [ ] `make test` passes
- [ ] `make conformance` passes

## Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| New tests discover production bugs | Medium | Medium | Mark as `Skip("Known bug: ...")`, don't fix in this sprint |
| apply_patch executor is a stub requiring implementation | Medium | High | Interview confirmed: implement if needed (allow minimal production code) |
| Resume conformance flakiness (timing) | Medium | Medium | Use tool stages + checkpoint polling + generous timeouts; `--simulate` avoids LLM variability |
| Conformance assertions based on wrong artifact schema | Medium | Medium | Verify actual schema in source before writing assertions; add lib.sh helpers if needed |
| Some P2 gaps require production seams (abort, timeout) | Medium | Low | Skip-annotate with clear follow-up note |
| Conformance env variance (macOS/Linux) | Low | Medium | POSIX tooling only; avoid GNU-only flags |

## Security Considerations

- No new attack surface (test code only, except potential apply_patch executor)
- Conformance tests use `--simulate` mode (no real API keys needed for pipeline tests)
- No secrets in test fixtures

## Dependencies

- Sprint 001-004 (all complete): Foundation this sprint builds on
- `make install` must succeed before conformance tests run
