# Sprint 004: Cross-Spec Gap Closure

**Status:** Planned
**Spec:** `unified-llm-spec.md`, `coding-agent-loop-spec.md`, `attractor-spec.md`
**Codebase:** `src/UnifiedLlm/`, `src/CodingAgent/`, `src/Attractor/`, `src/Attractor.Cli/`

## Overview

A post-implementation audit against all three specs identified 30 conformance gaps spanning the UnifiedLlm client (15 gaps), CodingAgent loop (7 gaps), and Attractor engine (8 gaps). The core implementations are solid but several behavioral contracts, data-model fields, API surface items, and edge-case semantics were missed. This sprint closes every identified gap to reach full conformance across all three specs.

## Use Cases

1. **Parallel tool execution**: A coding agent asks the LLM to grep three directories simultaneously; all three execute concurrently because `SupportsParallelToolCalls` is now honored.
2. **Streaming middleware**: A logging middleware records every request/response — it applies to both `complete()` and `stream()` calls without special-casing.
3. **Checkpoint fidelity**: A pipeline resumes from checkpoint; nodes that previously produced `PartialSuccess` resume with that outcome restored, not silently promoted to `Success`.
4. **Graceful cycle restart**: A `loop_restart` edge fires; context keys from the previous cycle are fully removed (not set to `""`), so `TryGet` returns `None` as expected.
5. **Model catalog**: `listModels()` returns all models across all providers with costs, aliases, and vision flags — no provider argument required.

## Implementation Plan

---

### Phase 1: UnifiedLlm — ModelInfo and Model Catalog

**Spec:** 3.7
**Files:** `src/UnifiedLlm/ModelCatalog.fs`

- [ ] Add `MaxOutput: int` field to `ModelInfo`
- [ ] Add `InputCostPerMillion: float` field to `ModelInfo`
- [ ] Add `OutputCostPerMillion: float` field to `ModelInfo`
- [ ] Add `Aliases: string list` field to `ModelInfo`
- [ ] Rename `SupportsImages` to `SupportsVision` (or add as alias) on `ModelInfo`
- [ ] Populate all new fields for existing Anthropic, OpenAI, and Gemini model entries
- [ ] Add `listModels()` (no argument) that returns all models across all providers
- [ ] Add `getLatestModel(provider: string)` that returns the most capable current model for a provider

**Definition of Done:**
- `ModelInfo` has all 8 spec-required fields
- `listModels()` returns non-empty list covering all three providers
- `getLatestModel("anthropic")` / `("openai")` / `("gemini")` each return a valid model
- Unit tests for all new fields and functions

---

### Phase 2: UnifiedLlm — Type Corrections

**Spec:** 3.4, 3.6
**Files:** `src/UnifiedLlm/Types.fs`

- [ ] Add `ImageData: byte[] option` and `ImageMediaType: string option` fields to `ToolResultData` for computer-use / screenshot tool results
- [ ] Change `Response.Raw` from `string option` to `Map<string, obj> option` (or `JsonElement option`) so structured provider response fields are accessible

**Definition of Done:**
- `ToolResultData` compiles with the two new optional image fields
- `Response.Raw` carries structured data; existing callers that ignore `Raw` are unaffected
- Unit test: construct `ToolResultData` with image bytes → fields round-trip correctly

---

### Phase 3: UnifiedLlm — Streaming Additions

**Spec:** 4.2, 4.3
**Files:** `src/UnifiedLlm/Generation.fs`

- [ ] Add `StreamAccumulator` type: wraps `IAsyncEnumerable<StreamEvent>`, exposes `TextStream: IAsyncEnumerable<string>` (text chunks only) and `PartialResponse: unit -> Response` (snapshot of accumulated response so far)
- [ ] Add `streamObject<'T>()` function (analogous to `generateObject` but streaming): yields partial structured objects as deltas arrive, returns final `'T` on completion
- [ ] `StreamAccumulator` must handle all `StreamEvent` variants (text delta, tool call delta, usage, finish)

**Definition of Done:**
- `StreamAccumulator` wraps an existing stream and provides both accessors
- `streamObject` compiles and can be called with the same schema/type args as `generateObject`
- Unit test: accumulate a mock stream → `PartialResponse()` mid-stream returns partial text; final response matches

---

### Phase 4: UnifiedLlm — generate() Extensions

**Spec:** 4.1
**Files:** `src/UnifiedLlm/Generation.fs`, `src/UnifiedLlm/Types.fs`

- [ ] Define `StopCondition` type: discriminated union with at least `ToolCalled of toolName: string`, `TextMatches of pattern: string`, and `MaxRounds of n: int`
- [ ] Add `StopWhen: StopCondition list` optional parameter to `generate()` / `generateWithControl()`
- [ ] Engine checks stop conditions after each tool round and terminates if any match
- [ ] Define `TimeoutConfig` type: `{ TotalMs: int option; PerStepMs: int option }`
- [ ] Define `AdapterTimeout` type: `{ ConnectMs: int option; RequestMs: int option; StreamReadMs: int option }`
- [ ] Add `Timeout: TimeoutConfig option` to `GenerateOptions` or equivalent
- [ ] Add `Timeout: AdapterTimeout option` to adapter registration / `ClientConfig`

**Definition of Done:**
- `generate()` with `StopWhen = [ToolCalled "write_file"]` stops loop after first `write_file` call
- `TimeoutConfig` and `AdapterTimeout` types exist and are threaded through to HTTP calls
- Unit test: stop condition fires mid-loop → correct number of rounds executed

---

### Phase 5: UnifiedLlm — Error Hierarchy

**Spec:** 6.2
**Files:** `src/UnifiedLlm/Errors.fs`

- [ ] Add `ContextLengthError` (prompt + response exceeds model context window)
- [ ] Add `QuotaExceededError` (account/org quota hit, distinct from rate limit)
- [ ] Add `InvalidRequestError` (malformed request, e.g. invalid parameter combination)
- [ ] Add `InvalidToolCallError` (model returned a tool call that doesn't parse or match schema)
- [ ] Rename `AuthorizationError` → `AccessDeniedError` (403 Forbidden); update all catch sites

**Definition of Done:**
- All 5 error type changes compile
- Adapters map appropriate HTTP status/error codes to the new types (e.g. 413 → `ContextLengthError`, 429 quota variant → `QuotaExceededError`)
- Existing tests updated for `AccessDeniedError` rename; no test regressions

---

### Phase 6: UnifiedLlm — Adapter Interface and Middleware

**Spec:** 2.3, 7.1, 8.2, 8.6
**Files:** `src/UnifiedLlm/ProviderAdapter.fs`, `src/UnifiedLlm/Client.fs`, `src/UnifiedLlm/HttpAdapters.fs`

- [ ] Add optional `Initialize: unit -> Async<unit>` lifecycle method to `IProviderAdapter`
- [ ] Add optional `Close: unit -> Async<unit>` lifecycle method to `IProviderAdapter`
- [ ] Add optional `SupportsToolChoice: unit -> bool` capability method to `IProviderAdapter`
- [ ] Fix `Client.Stream()` to route through the middleware chain (currently bypasses it); middleware `next` function receives the streaming call and may wrap/observe it
- [ ] Gemini adapter: read and apply `request.ProviderOptions` (pass unknown keys as top-level Gemini request fields)
- [ ] Anthropic adapter: check `request.ProviderOptions["anthropic"]["auto_cache"]` — if `false`, skip automatic `cache_control` breakpoint injection

**Definition of Done:**
- A logging middleware registered on `Client` observes both `Complete` and `Stream` calls
- Gemini adapter forwards a custom `providerOptions` key to the Gemini API body
- Anthropic auto-cache can be disabled via `ProviderOptions`
- Unit test: middleware call count increments on stream calls

---

### Phase 7: CodingAgent — Parallel Tool Dispatch

**Spec:** 2.4
**Files:** `src/CodingAgent/Session.fs`

- [ ] When `profile.SupportsParallelToolCalls = true` and the model returns multiple tool calls in one response, dispatch them via `toolRegistry.DispatchAll(..., runParallel = true)`
- [ ] When `SupportsParallelToolCalls = false`, preserve existing sequential dispatch
- [ ] Collect all results and return in original call order (parallel results may arrive out of order)

**Definition of Done:**
- Mock test: profile with `SupportsParallelToolCalls = true` + 3 simultaneous tool calls → all 3 execute concurrently (verified via timing or task ordering)
- Profile with `SupportsParallelToolCalls = false` → sequential (existing behavior unchanged)
- Existing CodingAgent tests still pass

---

### Phase 8: CodingAgent — grep glob_filter and provider_options

**Spec:** 3.6, 2.6
**Files:** `src/CodingAgent/ProviderProfile.fs`, `src/CodingAgent/ExecutionEnvironment.fs`, `src/CodingAgent/Types.fs`, `src/CodingAgent/Session.fs`

- [ ] Add `glob_filter` optional string parameter to the `grep` tool definition (JSON schema: `"glob_filter": { "type": "string", "description": "Glob pattern to restrict search files, e.g. '*.fs'" }`)
- [ ] Update `IExecutionEnvironment.Grep` signature to accept `globFilter: string option`
- [ ] Update `LocalExecutionEnvironment.Grep` implementation to pass glob filter to the underlying search (e.g. `--glob` flag on ripgrep)
- [ ] Add `ProviderOptions: Map<string, obj> option` field to `SessionConfig`
- [ ] When building the `Request` in `Session.ProcessInput`, set `request.ProviderOptions` from `SessionConfig.ProviderOptions`

**Definition of Done:**
- LLM can call `grep` with `"glob_filter": "*.fs"` and results are restricted to `.fs` files
- `SessionConfig` with `ProviderOptions` set passes values through to every LLM request
- Unit tests for both

---

### Phase 9: CodingAgent — Event Stream and Truncation

**Spec:** 2.9, 5.2
**Files:** `src/CodingAgent/Session.fs`, `src/CodingAgent/Truncation.fs`

- [ ] Add `OnEvent: (SessionEvent -> unit) option` callback to `SessionConfig` for real-time event delivery (in addition to the existing batch `Session.Events` list)
- [ ] Emit events via the callback immediately when they occur (not just appending to the list)
- [ ] Before truncating tool output, capture the full pre-truncation string
- [ ] Attach full untruncated output to `TOOL_CALL_END` events (add a `FullOutput: string option` field to the relevant event case)
- [ ] Shell tool output uses `tail` truncation mode (keep end); file-read tools use `head_tail` (keep start+end)

**Definition of Done:**
- Host can register `OnEvent` callback and receive `TOOL_CALL_END` before `Session.ProcessInput` returns
- `TOOL_CALL_END` carries both truncated content (what the model sees) and full output (what the host sees)
- Shell output truncation verified to preserve the final N chars (where errors typically appear)
- Existing event tests pass

---

### Phase 10: Attractor — Validation, Context Reset, Checkpoint Fidelity

**Spec:** 11.2, 3.3, 11.7
**Files:** `src/Attractor/Validation.fs`, `src/Attractor/Engine.fs`, `src/Attractor/Types.fs`

- [ ] Fix exit-node validation rule: error when `exitNodes.Length <> 1` (not just when empty)
- [ ] Fix `loop_restart` context reset: create a new `Context` instance carrying only keys prefixed `graph.`; rebind the context reference in the engine loop (requires making context mutable or passing through a ref/mutable cell)
- [ ] Add `NodeOutcomes: Map<string, Outcome>` field to the `Checkpoint` type
- [ ] When saving a checkpoint, populate `NodeOutcomes` with the outcomes of all completed nodes
- [ ] On resume, restore per-node outcomes from `NodeOutcomes` instead of defaulting to `Outcome.Success`

**Definition of Done:**
- Pipeline with two `shape=Msquare` nodes fails validation with a clear error message
- After `loop_restart`, `context.TryGet("any_prior_key")` returns `None`
- Resume from checkpoint: a node that completed with `PartialSuccess` is restored as `PartialSuccess`
- Unit tests for all three behaviors

---

### Phase 11: Attractor — Engine Semantics

**Spec:** 3.4, 4.7
**Files:** `src/Attractor/Engine.fs`

- [ ] Fix `Fail`-outcome retry backoff: apply the normal `RetryPolicy.BackoffFor(attempt)` delay (not hardcoded 0) when retrying a `Fail` outcome
- [ ] Implement `auto_status` attribute: after a ToolHandler or CodergenHandler execute without writing a status file, if `node.AutoStatus = true`, synthesize a `Success` outcome rather than treating absence of status as failure

**Definition of Done:**
- Node with `max_retries=2` and `Fail` outcome: second attempt delayed by backoff interval (testable via mock timer or elapsed time)
- Node with `auto_status=true` and no status file written → `Outcome.Success` (not failure)
- Existing retry tests pass

---

### Phase 12: Attractor — Question Types and Manager Loop

**Spec:** 11.8, 4.8
**Files:** `src/Attractor/Interviewer.fs`, `src/Attractor/Handlers.fs`

- [ ] Add `MultiSelect` question type to `QuestionType` (allows multiple options to be selected; returns comma-separated keys)
- [ ] Align `QuestionType` case names with spec: add `SingleSelect` as alias or rename `MultipleChoice` → `SingleSelect` (preserve backward compatibility via alias or deprecated member)
- [ ] Implement active manager loop supervision in `ManagerLoopHandler`:
  - Observe: read subordinate agent's latest output from context
  - Steer: if output indicates the agent is stuck or off-track, inject a correction message into context
  - Wait: sleep between cycles up to `max_cycles` or until `stop_condition_key` is set
  - Emit a steering message to context (`manager.steering`) when a correction is injected

**Definition of Done:**
- `MultiSelect` question type renders all options and accepts comma-separated input
- Manager loop test: mock context where subordinate never sets stop key → loop exits after `max_cycles` with `Fail`
- Manager loop test: subordinate sets stop key on cycle 2 → loop exits `Success` with `TurnsUsed = 2`

---

## Files Summary

| File | Action | Purpose |
|------|--------|---------|
| `src/UnifiedLlm/ModelCatalog.fs` | Modify | New ModelInfo fields, listModels(), getLatestModel() |
| `src/UnifiedLlm/Types.fs` | Modify | ToolResultData image fields, Response.Raw type, StopCondition, TimeoutConfig |
| `src/UnifiedLlm/Generation.fs` | Modify | StreamAccumulator, streamObject, StopWhen parameter |
| `src/UnifiedLlm/Errors.fs` | Modify | 4 new error types, AccessDeniedError rename |
| `src/UnifiedLlm/ProviderAdapter.fs` | Modify | Initialize, Close, SupportsToolChoice on interface |
| `src/UnifiedLlm/Client.fs` | Modify | Middleware applies to stream() |
| `src/UnifiedLlm/HttpAdapters.fs` | Modify | Gemini ProviderOptions, Anthropic auto_cache opt-out |
| `src/CodingAgent/Session.fs` | Modify | Parallel dispatch, OnEvent callback, ProviderOptions passthrough |
| `src/CodingAgent/ProviderProfile.fs` | Modify | grep glob_filter parameter |
| `src/CodingAgent/ExecutionEnvironment.fs` | Modify | Grep glob filter implementation |
| `src/CodingAgent/Types.fs` | Modify | SessionConfig ProviderOptions, OnEvent, FullOutput on event |
| `src/CodingAgent/Truncation.fs` | Modify | Per-tool truncation mode (tail vs head_tail) |
| `src/Attractor/Validation.fs` | Modify | Exactly-one exit node rule |
| `src/Attractor/Engine.fs` | Modify | loop_restart context reset, Fail retry backoff, auto_status, checkpoint outcomes |
| `src/Attractor/Types.fs` | Modify | Checkpoint.NodeOutcomes field |
| `src/Attractor/Interviewer.fs` | Modify | MultiSelect question type, name alignment |
| `src/Attractor/Handlers.fs` | Modify | Active manager loop supervision |
| `tests/UnifiedLlm.Tests/Tests.fs` | Modify | Tests for all UnifiedLlm gaps |
| `tests/CodingAgent.Tests/Tests.fs` | Modify | Tests for parallel dispatch, grep filter, events |
| `tests/Attractor.Tests/Tests.fs` | Modify | Tests for validation, context reset, checkpoint, retry |

## Definition of Done

- [ ] `ModelInfo` has all spec-required fields; `listModels()` and `getLatestModel()` exist
- [ ] `ToolResultData` supports image fields; `Response.Raw` is structured
- [ ] `StreamAccumulator` and `streamObject` implemented
- [ ] `StopCondition` and `TimeoutConfig` types exist and are wired into `generate()`
- [ ] Error hierarchy has all 4 missing types; `AccessDeniedError` renamed
- [ ] `IProviderAdapter` has `Initialize`, `Close`, `SupportsToolChoice`
- [ ] Middleware applies to `stream()` calls
- [ ] Gemini adapter passes through `ProviderOptions`; Anthropic auto-cache is opt-outable
- [ ] Tool calls dispatch in parallel when `SupportsParallelToolCalls = true`
- [ ] `grep` tool has `glob_filter` parameter; `IExecutionEnvironment.Grep` accepts filter
- [ ] `SessionConfig.ProviderOptions` flows through to every LLM request
- [ ] `OnEvent` callback delivers events in real-time; `TOOL_CALL_END` carries full untruncated output
- [ ] Shell truncation uses `tail` mode; file reads use `head_tail`
- [ ] Validation rejects pipelines with != 1 exit node
- [ ] `loop_restart` creates a fresh context (missing keys return `None`)
- [ ] Checkpoint persists and restores per-node outcomes
- [ ] `Fail`-outcome retries apply backoff delay
- [ ] `auto_status=true` synthesizes `Success` when no status file written
- [ ] `MultiSelect` question type implemented
- [ ] Manager loop performs active observe/steer/wait supervision
- [ ] `make test` passes
- [ ] `make conformance` passes (123/123)

## Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `Response.Raw` type change breaks existing callers | Medium | Medium | All current callers ignore `Raw`; add JsonElement option without removing string conversion |
| Middleware on stream() requires interface change | Medium | Medium | Wrap stream in a middleware-aware `IAsyncEnumerable` decorator |
| Parallel tool dispatch changes observable ordering | Low | Low | Collect results in original index order; test with deterministic mock |
| loop_restart context rebind requires engine refactor | Medium | High | Pass context as `ref Context` through the execution pipeline |
| Manager loop active supervision is underspecified | Medium | Medium | Implement minimal viable version; observe = read context key, steer = set context key |

## Dependencies

- SPRINT-001 (UnifiedLlm): base adapter and generation pipeline
- SPRINT-002 (CodingAgent): session loop and tool registry
- SPRINT-003 (Attractor): engine, handlers, checkpoint system
