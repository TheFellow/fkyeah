# QA Gap Analysis: Specs vs Test Coverage

**Date:** 2026-02-25
**Scope:** attractor-spec.md, unified-llm-spec.md, coding-agent-loop-spec.md
**Test inventory:** 384 unit tests (184 Attractor + 105 UnifiedLlm + 95 CodingAgent), 128 conformance tests

---

## Legend

- **Conformance** — shell-based E2E tests in `conformance/`
- **Unit** — F# xUnit tests in `tests/`
- **Spec §X.Y** — section reference in the relevant spec

---

## A. Conformance Test Gaps (Attractor Pipeline)

### A1. No conformance tests for Model Stylesheet (Spec §8)

The stylesheet system (CSS-like selectors: universal, shape, class, ID) has 7 unit tests
across `StylesheetTests` but zero conformance tests. A pipeline that uses
`model_stylesheet` to override node models has never been exercised E2E.

**Suggested test:** A pipeline where `model_stylesheet` assigns a model to box-shaped
nodes, and the test verifies the model appears in the stage artifacts.

### A2. No conformance tests for Checkpoint Resume (Spec §5 / §11.7)

Unit tests cover `Checkpoint load and resume works` and `resume restores checkpointed
node outcomes including PartialSuccess`, but no conformance test runs `attractor run`,
completes partially, then resumes from the checkpoint file.

**Suggested test:** A 3-node pipeline where the test runs to a midpoint, kills the
process, then resumes with `--resume <checkpoint>` and verifies the final artifacts
match a clean run.

### A3. No conformance tests for Fidelity projection (Spec §3.5)

Six unit tests (`FidelityProjectionTests`) cover Full/Truncate/Compact/Summary modes.
No conformance test passes a `fidelity=` attribute on an edge or node and verifies the
context was projected correctly before the next handler receives it.

**Suggested test:** A pipeline with `fidelity=Truncate` on an edge; verify downstream
node's prompt.md does not contain full-length context values.

### A4. No conformance tests for Manager Loop / Supervision (Spec §3.9)

Five unit tests in `ManagerLoopTests` cover exit conditions and pipeline integration. No
conformance test runs a pipeline with a manager node that supervises child execution.

### A5. No conformance tests for Transforms (Spec §9)

Variable expansion (`$goal`) and custom transforms are unit-tested only. No conformance
test verifies that `$goal` expansion appears in the actual `prompt.md` artifact written
to disk.

**Suggested test:** A pipeline with `graph [goal="Build X"]` and a codergen node whose
prompt uses `$goal`. Verify `prompt.md` contains `"Build X"`.

### A6. Parallel execution under-tested — only 2 conformance tests

Current coverage: `01-fan-out-fan-in`, `02-branch-context`.

Missing scenarios:
- Parallel branch failure + partial success (does fan-in still complete?)
- Parallel branch timeout
- Parallel branch with different models via stylesheet
- More than 2 parallel branches

### A7. Numbering gap: `03-execution/16-*` and `03-execution/17-*` missing

Tests jump from `15-auto-status` to `18-goal-gate-partial-success`. These were likely
planned but never implemented.

### A8. No conformance test for `max_visits` / infinite loop prevention

Unit test `max_visits stops infinite node loops` exists. No conformance pipeline
exercises this.

**Suggested test:** A pipeline with a deliberate back-edge and `max_visits=3`. Verify
the pipeline exits with failure after 3 visits, not infinite loop.

### A9. No conformance test for freeform gate or multi-select question types

The interviewer supports YesNo, SingleSelect, MultiSelect, FreeText. Only SingleSelect
(auto-approve picking first option) is conformance-tested via `12-auto-approve`.

Missing: freeform gate with `--auto-approve` writing prompt.md/response.md artifacts,
and a multi-select gate.

### A10. No conformance test for tool hooks (pre/post)

`tool_hooks.pre` and `tool_hooks.post` are parseable (unit test confirms), but no
conformance pipeline runs a tool node with hooks and verifies the hook commands execute.

---

## B. Unified LLM Unit Test Gaps

### B1. Prompt caching — 1 test vs 7+ spec requirements (Spec §2.10)

Only test: `Anthropic provider option auto_cache=false disables cache_control injection`.

Missing:
- Verify `cache_control` breakpoints are injected **by default** for Anthropic
- Verify `cache_read_tokens` / `cache_write_tokens` are populated from mock response
  usage data
- OpenAI: verify `cache_read_tokens` mapped from
  `prompt_tokens_details.cached_tokens`
- Gemini: verify `cache_read_tokens` mapped from `cachedContentTokenCount`

### B2. No native API verification (Spec §2.7 — marked "Critical")

The spec is emphatic that OpenAI must use the Responses API, Anthropic must use the
Messages API, and Gemini must use the native Gemini API. All 105 tests use mock
adapters. No test inspects the HTTP request shape to verify native API compliance.

### B3. Audio and Document content parts — zero tests (Spec §3.5)

`AudioData` and `DocumentData` types are defined in `Types.fs` but never exercised in
any test. Not even construction/round-trip tests.

### B4. No abort/cancellation tests (Spec §4.3)

`AbortSignal` is a defined type. No test verifies that `generate()` or `stream()`
respects abort signals mid-operation.

### B5. No timeout tests at SDK level (Spec §4.3)

`TimeoutConfig` exists with `total_timeout` and `per_step_timeout`. No test verifies
timeout enforcement or that `TimeoutError` is raised when exceeded.

### B6. RateLimitInfo never verified populated (Spec §3.12)

The type exists on `Response`, but no test checks that a mock adapter can populate it
from headers and that it surfaces correctly through the middleware chain.

### B7. Warnings on Response never verified (Spec §3.11)

The `Warning` type is defined, `Response.Warnings` is a field, but no test verifies
warnings are populated or accessible on a returned response.

### B8. Usage addition: Some + None case missing (Spec §3.9)

One test covers `Usage addition both None stays None`. Missing the case where one side
has `Some 5` and the other has `None` — spec says the result should be `Some 5`
(treating None as 0).

### B9. FinishReason raw field not tested across provider mappings

Tests create `Stop "stop"` but don't verify the full provider mapping table:
- Anthropic `end_turn` -> `stop`
- Anthropic `stop_sequence` -> `stop`
- Anthropic `max_tokens` -> `length`
- Anthropic `tool_use` -> `tool_calls`
- Gemini `STOP` -> `stop`
- Gemini `MAX_TOKENS` -> `length`
- Gemini `SAFETY` / `RECITATION` -> `content_filter`

---

## C. Coding Agent Unit Test Gaps

### C1. `apply_patch` executor not tested (Spec §3.4)

Tests verify the OpenAI profile *includes* `apply_patch` in tool names. No test
exercises the actual v4a patch parsing and application logic (create file, delete file,
modify file via unified diff).

### C2. No cross-provider parity matrix (Spec §9.12)

The spec lists 15 test cases x 3 providers = 45 test cells. All current tests use a
generic `TestProfile`. No test verifies that actual OpenAI/Anthropic/Gemini profiles
produce correct provider-specific system prompts and tool schemas when building LLM
requests.

### C3. No multi-step tool loop tests

The mock adapter only exercises 1-round loops (call tool -> return text). Missing: 3+
round loops where the model reads -> edits -> verifies in sequence, which is the primary
use case for a coding agent.

### C4. Streaming mode minimally tested

One test: `Streaming mode emits incremental AssistantTextDelta events`.

Missing:
- Streaming with tool calls mid-stream
- Stream pause/resume during tool execution
- `TOOL_CALL_OUTPUT_DELTA` events for streaming tools

### C5. Truncated vs full output contract under-tested

Spec §2.9 says `TOOL_CALL_END` event carries FULL untruncated output while the LLM
gets truncated output. One Sprint004 test touches this (`OnEvent callback receives
TOOL_CALL_END with both truncated and full output`), but a single test for this
behavioral contract is thin.

### C6. No `edit_file` fuzzy matching test

Spec §3.3: "the implementation may attempt fuzzy matching (whitespace normalization,
Unicode equivalence)". No test verifies this behavior or documents that it is not
implemented.

---

## D. Cross-Cutting Gaps

### D1. No conformance tests for UnifiedLlm or CodingAgent as standalone libraries

All conformance goes through the `attractor` CLI. If someone consumes `UnifiedLlm` or
`CodingAgent` as a library, there is no conformance-level coverage for them.

### D2. No error/recovery conformance tests

No conformance test exercises: binary crash mid-pipeline, I/O error during artifact
writes, malformed LLM response handling, or network timeout during execution.

### D3. Model matrix (07-models) only tests code generation

All 72 model tests (9 models x 8 scenarios) are "generate a program in language X,
extract it, run it". Missing: model tests for reasoning-heavy tasks, multi-turn tool
use, or model-specific features (thinking blocks, apply_patch format).

### D4. Coding agent conformance (08-coding-agent) is thin

Only 6 tests (3 models x 2 scenarios). Spec §9.12 calls for 15 scenarios x 3
providers. Missing: shell execution, grep/glob, truncation verification, steering,
subagent spawning, timeout handling.

---

## E. Prioritized Recommendations

### P0 — High Impact, Address First

| ID | Gap | Effort | Why |
|----|-----|--------|-----|
| A2 | Checkpoint resume conformance test | Low | Critical for durability claims; a single `.dot` + `test.sh` |
| B1 | Prompt caching unit tests | Medium | Biggest cost lever for production use |
| C1 | `apply_patch` executor unit tests | Medium | OpenAI profile is untestable without this |

### P1 — Important Coverage Holes

| ID | Gap | Effort | Why |
|----|-----|--------|-----|
| A1 | Stylesheet conformance test | Low | Simple pipeline with `model_stylesheet` |
| A3 | Fidelity conformance test | Low | Pipeline with `fidelity=Truncate` on edge |
| A5 | `$goal` expansion conformance test | Low | Verify in actual `prompt.md` artifact |
| A8 | `max_visits` conformance test | Low | Pipeline with deliberate loop |
| C3 | Multi-step tool loop unit tests (3+ rounds) | Medium | Core agent loop coverage |
| B8 | Usage addition Some + None test | Low | One-liner to close a spec gap |
| B9 | FinishReason provider mapping tests | Low | Table-driven test from spec §3.8 |

### P2 — Robustness and Completeness

| ID | Gap | Effort | Why |
|----|-----|--------|-----|
| A6 | Parallel branch failure/timeout conformance | Medium | Edge case robustness |
| A4 | Manager loop conformance test | Medium | Supervision feature coverage |
| B3 | Audio/Document content types | Low | Completeness |
| B4 | Abort/cancellation tests (SDK + agent) | Medium | Operational reliability |
| B5 | Timeout tests at SDK level | Medium | Production safety |
| C2 | Cross-provider coding agent parity tests | High | Full spec compliance |
| C4 | Streaming with tool calls | Medium | First-class streaming claim |

### P3 — Housekeeping and Polish

| ID | Gap | Effort | Why |
|----|-----|--------|-----|
| A7 | Fill conformance numbering gap (16, 17) | Low | Housekeeping |
| A9 | Freeform / multi-select gate conformance | Low | Question type coverage |
| A10 | Tool hooks conformance | Low | Feature completeness |
| B2 | Native API shape verification | High | Requires HTTP interception |
| B6 | RateLimitInfo population test | Low | Type completeness |
| B7 | Warnings on Response test | Low | Type completeness |
| D2 | Error/recovery conformance tests | Medium | Resilience |
| D3 | Model matrix beyond code generation | High | Breadth of model coverage |
