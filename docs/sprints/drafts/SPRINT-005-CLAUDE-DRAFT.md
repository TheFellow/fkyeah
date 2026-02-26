# Sprint 005: Test Coverage Hardening

**Status:** Planned
**Spec:** `docs/feedback.md` (QA gap analysis)
**Codebase:** `tests/`, `conformance/`

## Overview

The first four sprints built and conformance-tested the full three-layer stack: UnifiedLlm (105 tests), CodingAgent (95 tests), and Attractor (184 tests), plus 128 conformance tests across 8 categories. A QA audit (`docs/feedback.md`) identified 28 specific gaps organized into P0-P3 priorities. The implementation is complete and stable — this sprint writes tests only. No production code changes.

The gaps fall into three patterns: (1) spec requirements with unit tests but no conformance-level E2E coverage, (2) spec requirements with zero test coverage, and (3) edge cases and behavioral contracts with only a single thin test. This sprint plugs all P0 and P1 gaps, and most P2 gaps, adding approximately 30 new unit tests and 10 new conformance tests.

## Use Cases

1. **Checkpoint durability**: A pipeline runs, checkpoints, and resumes. The conformance test verifies resumed runs produce the same artifacts as fresh runs.
2. **Prompt caching correctness**: Mock adapters return cache tokens. Unit tests verify the SDK maps them correctly — the biggest cost lever for production.
3. **apply_patch validation**: The OpenAI profile's patch tool actually parses and applies a v4a diff. Tests cover create, modify, and delete operations.
4. **Stylesheet E2E**: A pipeline with `model_stylesheet` overrides node models. The conformance test verifies the override appears in stage artifacts.
5. **Multi-step agent loop**: A mock adapter returns 3 rounds of tool calls before completing. Tests verify the agent loop tracks all steps correctly.

## Implementation Plan

### Phase 1: P0 — Conformance: Checkpoint Resume (~15%)

**Gap:** A2 — No conformance test verifies `--resume`
**Files:**
- `conformance/06-artifacts/06-checkpoint-resume/pipeline.dot` (Create)
- `conformance/06-artifacts/06-checkpoint-resume/test.sh` (Create)
- `conformance/06-artifacts/06-checkpoint-resume/README.md` (Create)

**Tasks:**
- [ ] Create a 3-node pipeline (start -> step1 -> step2 -> done)
- [ ] Test runs the pipeline fully, saves checkpoint
- [ ] Test runs again with `--resume <checkpoint>` and verifies:
  - Exit code 0
  - `checkpoint.json` has `completed_nodes` for all nodes
  - `node_outcomes` field is populated
  - Stage artifacts match between runs

### Phase 2: P0 — Unit: Prompt Caching (~15%)

**Gap:** B1 — 1 test vs 7+ spec requirements for caching
**Files:**
- `tests/UnifiedLlm.Tests/Tests.fs` (Modify)

**Tasks:**
- [ ] Test: Anthropic adapter injects `cache_control` breakpoints by default on system message
- [ ] Test: `cache_read_tokens` populated from mock Anthropic usage `cache_read_input_tokens`
- [ ] Test: `cache_write_tokens` populated from mock Anthropic usage `cache_creation_input_tokens`
- [ ] Test: OpenAI adapter maps `prompt_tokens_details.cached_tokens` to `cache_read_tokens`
- [ ] Test: Gemini adapter maps `cachedContentTokenCount` to `cache_read_tokens`
- [ ] Test: `Usage` addition preserves cache token fields (Some + None = Some)

### Phase 3: P0 — Unit: apply_patch Executor (~10%)

**Gap:** C1 — apply_patch executor parsing not tested
**Files:**
- `tests/CodingAgent.Tests/Tests.fs` (Modify)

**Tasks:**
- [ ] Test: apply_patch creates a new file from v4a patch
- [ ] Test: apply_patch modifies an existing file (context lines + changes)
- [ ] Test: apply_patch deletes a file
- [ ] Test: apply_patch with malformed patch returns error result (not exception)
- [ ] Test: apply_patch with non-existent target file returns error result

### Phase 4: P1 — Conformance: Stylesheet (~5%)

**Gap:** A1 — No conformance test for model_stylesheet
**Files:**
- `conformance/03-execution/16-stylesheet/pipeline.dot` (Create)
- `conformance/03-execution/16-stylesheet/test.sh` (Create)
- `conformance/03-execution/16-stylesheet/README.md` (Create)

**Tasks:**
- [ ] Create pipeline with `model_stylesheet` that assigns a model to box-shaped nodes
- [ ] Run in simulate mode
- [ ] Verify pipeline completes (exit 0)
- [ ] Verify stage artifacts reflect stylesheet properties (check status.json)

### Phase 5: P1 — Conformance: Fidelity, $goal Expansion, max_visits (~10%)

**Gap:** A3, A5, A8
**Files:**
- `conformance/03-execution/17-fidelity-edge/pipeline.dot` (Create)
- `conformance/03-execution/17-fidelity-edge/test.sh` (Create)
- `conformance/03-execution/17-fidelity-edge/README.md` (Create)
- `conformance/04-context/07-goal-expansion/pipeline.dot` (Create)
- `conformance/04-context/07-goal-expansion/test.sh` (Create)
- `conformance/04-context/07-goal-expansion/README.md` (Create)
- `conformance/03-execution/19-max-visits/pipeline.dot` (Create)
- `conformance/03-execution/19-max-visits/test.sh` (Create)
- `conformance/03-execution/19-max-visits/README.md` (Create)

**Tasks:**
- [ ] **Fidelity:** Pipeline with `fidelity=Truncate` on an edge; verify pipeline completes
- [ ] **$goal expansion:** Pipeline with `graph [goal="Build widget"]` and codergen node with `prompt="$goal"`. Verify `prompt.md` contains "Build widget"
- [ ] **max_visits:** Pipeline with a back-edge and `max_visits=3`. Verify pipeline exits non-zero after loop

### Phase 6: P1 — Unit: Multi-step Tool Loop & FinishReason Mapping (~10%)

**Gap:** C3, B9
**Files:**
- `tests/CodingAgent.Tests/Tests.fs` (Modify)
- `tests/UnifiedLlm.Tests/Tests.fs` (Modify)

**Tasks:**
- [ ] Test: 3-round tool loop — mock adapter returns tool calls for 3 rounds then text. Verify `session.History` has 3 `ToolResultsTurn` entries
- [ ] Test: 5-round tool loop — verify total usage aggregates across all rounds
- [ ] Test: Anthropic `end_turn` maps to FinishReason `stop`
- [ ] Test: Anthropic `stop_sequence` maps to FinishReason `stop`
- [ ] Test: Anthropic `max_tokens` maps to FinishReason `length`
- [ ] Test: Anthropic `tool_use` maps to FinishReason `tool_calls`
- [ ] Test: Gemini `STOP` maps to FinishReason `stop`
- [ ] Test: Gemini `MAX_TOKENS` maps to FinishReason `length`
- [ ] Test: Gemini `SAFETY` maps to FinishReason `content_filter`

### Phase 7: P1 — Unit: Usage Addition Edge Cases (~5%)

**Gap:** B8
**Files:**
- `tests/UnifiedLlm.Tests/Tests.fs` (Modify)

**Tasks:**
- [ ] Test: `Usage` addition where left has `CacheReadTokens = Some 5`, right has `None` → result is `Some 5`
- [ ] Test: `Usage` addition where left has `None`, right has `CacheWriteTokens = Some 10` → result is `Some 10`
- [ ] Test: `Usage` addition where both have `Some` values → sums correctly
- [ ] Test: `Usage` addition with `ReasoningTokens` — same None-handling behavior

### Phase 8: P2 — Conformance: Parallel Branch Failure (~5%)

**Gap:** A6
**Files:**
- `conformance/05-parallel/03-branch-failure/pipeline.dot` (Create)
- `conformance/05-parallel/03-branch-failure/test.sh` (Create)
- `conformance/05-parallel/03-branch-failure/README.md` (Create)

**Tasks:**
- [ ] Create pipeline where one parallel branch has a tool that exits non-zero
- [ ] Verify fan-in still completes
- [ ] Verify pipeline outcome reflects the partial failure
- [ ] Verify `parallel.branch.*.status` context vars are set correctly

### Phase 9: P2 — Unit: Abort/Cancellation and Streaming (~10%)

**Gap:** B4, C4
**Files:**
- `tests/UnifiedLlm.Tests/Tests.fs` (Modify)
- `tests/CodingAgent.Tests/Tests.fs` (Modify)

**Tasks:**
- [ ] Test: `generate()` with AbortSignal that fires mid-execution — verify cancellation
- [ ] Test: `stream()` with AbortSignal — verify stream terminates
- [ ] Test: CodingAgent streaming with tool calls — verify TOOL_CALL_START/END events emitted between stream segments
- [ ] Test: CodingAgent streaming 2-round tool loop — verify events interleave correctly

### Phase 10: P2 — Unit: Audio/Document Types, RateLimitInfo, Warnings (~5%)

**Gap:** B3, B6, B7
**Files:**
- `tests/UnifiedLlm.Tests/Tests.fs` (Modify)

**Tasks:**
- [ ] Test: `AudioData` construction and round-trip (url path, data path)
- [ ] Test: `DocumentData` construction and round-trip
- [ ] Test: `RateLimitInfo` populated on Response from mock adapter headers
- [ ] Test: `Warning` populated on Response and accessible via `response.Warnings`

### Phase 11: P2 — Unit: Truncation Contract and edit_file Fuzzy Match (~5%)

**Gap:** C5, C6
**Files:**
- `tests/CodingAgent.Tests/Tests.fs` (Modify)

**Tasks:**
- [ ] Test: Large tool output → LLM receives truncated, event carries full — verify both values differ and are correct
- [ ] Test: Multiple tool calls → each TOOL_CALL_END event has independent FullOutput
- [ ] Test: `edit_file` with whitespace-normalized match (tabs vs spaces) — verify behavior (pass or documented error)
- [ ] Test: `edit_file` with trailing whitespace differences — verify behavior

## Files Summary

| File | Action | Purpose |
|------|--------|---------|
| `tests/UnifiedLlm.Tests/Tests.fs` | Modify | ~15 new tests: caching, Usage edges, FinishReason mapping, abort, types |
| `tests/CodingAgent.Tests/Tests.fs` | Modify | ~10 new tests: apply_patch, multi-step loop, streaming, truncation, edit fuzzy |
| `tests/Attractor.Tests/Tests.fs` | Modify | ~5 new tests: if any needed for conformance helper coverage |
| `conformance/06-artifacts/06-checkpoint-resume/` | Create | Checkpoint resume E2E test |
| `conformance/03-execution/16-stylesheet/` | Create | Stylesheet E2E test |
| `conformance/03-execution/17-fidelity-edge/` | Create | Fidelity edge attribute E2E test |
| `conformance/03-execution/19-max-visits/` | Create | max_visits loop prevention E2E test |
| `conformance/04-context/07-goal-expansion/` | Create | $goal prompt expansion E2E test |
| `conformance/05-parallel/03-branch-failure/` | Create | Parallel branch failure handling E2E test |

## Definition of Done

- [ ] All P0 gaps (A2, B1, C1) have passing tests
- [ ] All P1 gaps (A1, A3, A5, A8, B8, B9, C3) have passing tests
- [ ] P2 gaps A6, B3, B4, B6, B7, C4, C5, C6 have passing tests
- [ ] No existing tests broken (384 unit + 128 conformance)
- [ ] ~30 new unit tests added
- [ ] ~6 new conformance test directories added
- [ ] `make test` passes
- [ ] `make conformance` passes
- [ ] Each new test traces back to a gap ID in docs/feedback.md (comment in test)

## Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| New tests discover production bugs | Medium | Medium | Mark as `Skip("Known bug: #X")`, don't fix in this sprint |
| apply_patch executor is a stub | Medium | High | If stub, test the interface contract; document that executor needs implementation |
| Checkpoint --resume flag not supported by CLI | Low | Medium | Verify CLI supports it first; if not, test via library API instead |
| Conformance tests flaky in CI | Low | Medium | Use simulate mode for all non-model tests; deterministic assertions |

## Security Considerations

- No production code changes — no new attack surface
- Conformance tests use `--simulate` mode (no real API keys needed for pipeline tests)
- No secrets in test fixtures

## Dependencies

- Sprint 001-004 (all complete): Foundation this sprint builds on
- `make install` must succeed before conformance tests run
