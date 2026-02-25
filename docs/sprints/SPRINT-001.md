# Sprint: UnifiedLlm Spec Conformance

**Status:** Not Started
**Spec:** `unified-llm-spec.md` Sections 3-8
**Codebase:** `src/UnifiedLlm/`

## Overview

The UnifiedLlm library has solid routing, error handling, and basic tool support, but
the adapters were built for text-only completions. Today's work added tool definitions
to all three providers, but the audit against the full spec reveals 18 gaps ranging
from missing streaming to incomplete type definitions. This sprint closes every gap
with real implementation and conformance tests — no stubs, no fallbacks.

## Phase 1: Foundation Types & Request Fields

### Task 1.1: Add missing Request fields
**Spec:** 3.6 Request
**Files:** `src/UnifiedLlm/Types.fs`

Add `Temperature`, `TopP`, `StopSequences`, `ResponseFormat`, and `Metadata` to the
Request record. Wire them through all three adapters.

- [ ] Add `Temperature: float option` to Request
- [ ] Add `TopP: float option` to Request
- [ ] Add `StopSequences: string list option` to Request
- [ ] Add `ResponseFormat: ResponseFormat option` to Request (see Task 1.2)
- [ ] Add `Metadata: Map<string, string> option` to Request
- [ ] Anthropic adapter: serialize temperature, top_p, stop_sequences in request body
- [ ] OpenAI adapter: serialize temperature, top_p, stop in request body
- [ ] Gemini adapter: serialize temperature, topP, stopSequences in generationConfig
- [ ] Update `Request.Create` default values

**Definition of Done:**
- `make test` passes
- Unit tests verify each field round-trips through all 3 adapters (mock adapter checks field presence)
- Setting `Temperature = Some 0.0` produces deterministic-ish output for a known prompt across all 3 providers

### Task 1.2: ResponseFormat type and native structured output
**Spec:** 3.10, 4.5
**Files:** `src/UnifiedLlm/Types.fs`, `src/UnifiedLlm/Generation.fs`, `src/UnifiedLlm/HttpAdapters.fs`

Define `ResponseFormat` discriminated union and wire native structured output through
all three adapters.

- [ ] Define `ResponseFormat = Text | JsonObject | JsonSchema of name: string * schema: string * strict: bool`
- [ ] Anthropic adapter: use tool-based extraction approach (define an extraction tool, force tool_choice)
- [ ] OpenAI adapter: set `text.format` to `json_schema` in Responses API
- [ ] Gemini adapter: set `responseMimeType = "application/json"` + `responseSchema`
- [ ] Update `generate_object()` to use native format instead of prompt injection
- [ ] Add JSON parsing and validation in `generate_object()` response handling

**Definition of Done:**
- `generate_object()` returns valid parsed JSON for a schema across all 3 providers
- Conformance test: generate a `{name: string, age: int}` object from all 3 providers, validate schema
- No prompt injection — provider-native structured output only

### Task 1.3: FinishReason raw field and Response.raw
**Spec:** 3.7, 3.8
**Files:** `src/UnifiedLlm/Types.fs`, `src/UnifiedLlm/HttpAdapters.fs`

- [ ] Add `Raw: string` field to FinishReason (provider-specific reason string)
- [ ] Add `Raw: string option` field to Response (full provider response body)
- [ ] Add `Warnings: string list` field to Response
- [ ] Populate `Raw` in all 3 adapters from the response JSON
- [ ] Populate FinishReason.Raw with the provider-specific string (e.g., "end_turn", "STOP")

**Definition of Done:**
- `response.Raw` contains the full JSON response body for debugging
- `response.FinishReason` preserves the original provider string in `.Raw`
- Unit tests verify raw field is populated for each adapter

### Task 1.4: RateLimitInfo extraction
**Spec:** 3.12
**Files:** `src/UnifiedLlm/Types.fs`, `src/UnifiedLlm/HttpAdapters.fs`

- [ ] Define `RateLimitInfo = { Limit: int option; Remaining: int option; ResetAt: DateTimeOffset option }`
- [ ] Add `RateLimit: RateLimitInfo option` to Response
- [ ] Anthropic adapter: parse `anthropic-ratelimit-*` headers
- [ ] OpenAI adapter: parse `x-ratelimit-*` headers
- [ ] Gemini adapter: parse rate limit headers if available

**Definition of Done:**
- RateLimitInfo populated from response headers when present
- Unit test verifies header parsing for each provider format

## Phase 2: Streaming

### Task 2.1: Anthropic SSE streaming
**Spec:** 4.2, 7.7, 8.2
**Files:** `src/UnifiedLlm/HttpAdapters.fs`

Replace the `Complete()` fallback in `AnthropicAdapter.Stream()` with real SSE parsing.

- [ ] Implement SSE line parser (reads `event:`, `data:` lines from chunked response)
- [ ] Map Anthropic events: `message_start`, `content_block_start`, `content_block_delta`, `content_block_stop`, `message_delta`, `message_stop`
- [ ] Emit `StreamStart` on `message_start`
- [ ] Emit `TextDelta` on `content_block_delta` with `type=text_delta`
- [ ] Emit `ToolCallStart`/`ToolCallDelta`/`ToolCallEnd` for `type=input_json_delta` blocks
- [ ] Emit `ThinkingEvent` for `type=thinking` blocks
- [ ] Emit `Finish` on `message_stop` with aggregated usage
- [ ] Handle `error` events gracefully

**Definition of Done:**
- `stream()` yields incremental text deltas for a multi-paragraph response
- Text deltas concatenated equal the full `complete()` response text
- Tool calls stream correctly (start → delta → end)
- Conformance test: stream a response from Claude, verify text arrives incrementally

### Task 2.2: OpenAI Responses API streaming
**Spec:** 4.2, 7.7
**Files:** `src/UnifiedLlm/HttpAdapters.fs`

- [ ] Send request with `stream: true` to Responses API
- [ ] Parse SSE events from chunked response
- [ ] Map events: `response.created`, `response.output_item.added`, `response.content_part.delta`, `response.output_item.done`, `response.completed`
- [ ] Emit appropriate StreamEvents for text and tool call deltas
- [ ] Handle `response.failed` events

**Definition of Done:**
- Same as 2.1 but for OpenAI: incremental text, tool call streaming, final usage

### Task 2.3: Gemini streaming
**Spec:** 4.2, 7.7
**Files:** `src/UnifiedLlm/HttpAdapters.fs`

- [ ] Use `streamGenerateContent` endpoint instead of `generateContent`
- [ ] Parse SSE/JSON chunks from response
- [ ] Map Gemini incremental content parts to StreamEvents

**Definition of Done:**
- Same criteria as 2.1/2.2 for Gemini

### Task 2.4: StreamEvent type completeness
**Spec:** 3.13, 3.14
**Files:** `src/UnifiedLlm/Types.fs`

- [ ] Add missing stream events: `TextStart`, `TextEnd`, `ReasoningStart`, `ReasoningEnd`, `StepFinish`, `StreamError`, `ProviderEvent`
- [ ] Add metadata fields to events where spec requires them (`text_id`, `response` on Finish)

**Definition of Done:**
- StreamEvent DU matches spec's event type list
- Streaming with tools emits `StepFinish` between tool execution rounds

## Phase 3: Tool Calling Completeness

### Task 3.1: Parallel tool execution
**Spec:** 5.7, 8.7
**Files:** `src/UnifiedLlm/Generation.fs`

Current `executeAllTools` uses `List.map` (sequential). Must execute concurrently.

- [ ] Replace `List.map executeTool` with `Async.Parallel` or `Task.WhenAll`
- [ ] Wait for ALL results before continuing
- [ ] Send all results in a single continuation request
- [ ] Preserve ordering (results match tool call order)
- [ ] Handle partial failures (some succeed, some error — send all results)

**Definition of Done:**
- Unit test: 3 tools with 100ms sleep each complete in ~100ms total, not ~300ms
- All results sent in one request (mock adapter captures single continuation call)
- Partial failure test: 2 tools succeed, 1 throws — all 3 results sent with correct `is_error` flags

### Task 3.2: ToolChoice mapping for OpenAI and Gemini
**Spec:** 5.3
**Files:** `src/UnifiedLlm/HttpAdapters.fs`

- [ ] OpenAI: map Auto → `"auto"`, None → `"none"`, Required → `"required"`, Named → `{"type":"function","name":"..."}`
- [ ] Gemini: map Auto → `"AUTO"`, None → `"NONE"`, Required → `"ANY"`, Named → `{"mode":"ANY","allowedFunctionNames":[...]}`
- [ ] Anthropic: fix None mode — omit tools array entirely instead of sending `type: "none"`

**Definition of Done:**
- Unit test per provider per mode verifies correct JSON serialization
- Anthropic None mode: request body has no `tools` key

### Task 3.3: Tool call argument validation
**Spec:** 5.8
**Files:** `src/UnifiedLlm/Tools.fs` or `src/UnifiedLlm/Generation.fs`

- [ ] Parse tool call arguments as JSON
- [ ] Validate against the tool's parameter schema (required fields, types)
- [ ] On validation failure: send error result to model with descriptive message
- [ ] No exception raised — model gets a chance to retry

**Definition of Done:**
- Test: tool with `required: ["location"]` receives `{}` → error result sent to model, not exception
- Test: tool receives valid args → executes normally

### Task 3.4: Streaming with tools (StepFinish events)
**Spec:** 5.9
**Files:** `src/UnifiedLlm/Generation.fs`

- [ ] When `stream()` is used with active tools, emit events across multiple steps
- [ ] Emit `StepFinish` event after each tool execution round (between model calls)
- [ ] Consumer sees continuous stream spanning multiple model calls

**Definition of Done:**
- Test: stream with tool that triggers 2 rounds — StepFinish appears between rounds
- Text deltas from round 1 and round 2 both delivered through the stream

## Phase 4: Thinking & Reasoning

### Task 4.1: Anthropic thinking block extraction
**Spec:** 3.5, 8.5
**Files:** `src/UnifiedLlm/HttpAdapters.fs`

- [ ] Parse `thinking` content blocks from Anthropic response alongside `text` and `tool_use`
- [ ] Create `ContentPart.Thinking` parts in the response Message
- [ ] Populate `Response.Reasoning` from thinking block text
- [ ] Handle redacted thinking blocks (type=thinking, text="[redacted]")

**Definition of Done:**
- Response from Claude with `reasoning_effort=high` has non-empty `response.Reasoning`
- Thinking content preserved in Message.Content as Thinking parts
- Conformance test: call Claude with thinking enabled, verify reasoning text is captured

### Task 4.2: Gemini thinking token mapping
**Spec:** 8.5
**Files:** `src/UnifiedLlm/HttpAdapters.fs`

- [ ] Map `usageMetadata.thoughtsTokenCount` to `Usage.ReasoningTokens`

**Definition of Done:**
- Response from Gemini thinking model has `usage.ReasoningTokens.IsSome`

## Phase 5: Retry Integration

### Task 5.1: Wire retry into generate()
**Spec:** 4.3, 6.9, 8.8
**Files:** `src/UnifiedLlm/Generation.fs`

- [ ] Add `maxRetries: int` parameter to `generate()` (default from RetryConfig)
- [ ] Wrap each LLM call in the tool loop with `Retry.execute()`
- [ ] Retries apply per-step (per model call), not to the entire multi-step operation
- [ ] `maxRetries = 0` disables automatic retries
- [ ] Transient errors (429, 5xx) retried transparently with backoff
- [ ] Non-retryable errors (401, 403, 404) raised immediately

**Definition of Done:**
- Test: mock adapter returns 429 on first call, success on second → generate() succeeds
- Test: mock adapter returns 401 → generate() raises immediately, no retry
- Test: `maxRetries = 0` → no retry even on 429

### Task 5.2: Per-request timeout and cancellation
**Spec:** 4.7
**Files:** `src/UnifiedLlm/Types.fs`, `src/UnifiedLlm/HttpAdapters.fs`, `src/UnifiedLlm/Generation.fs`

- [ ] Add `Timeout: TimeSpan option` to Request (overrides HttpClient default)
- [ ] Create per-request `CancellationTokenSource` from timeout
- [ ] Pass token to `HttpClient.Send()` calls
- [ ] Add `AbortSignal` type and wire through `generate()` for caller-driven cancellation
- [ ] Raise `RequestTimeoutError` on timeout, `AbortError` on cancellation

**Definition of Done:**
- Test: request with 1s timeout against a slow mock → RequestTimeoutError raised
- Test: abort signal triggered mid-request → AbortError raised

## Phase 6: Prompt Caching Completeness

### Task 6.1: Anthropic cache_write_tokens and beta header
**Spec:** 2.10, 8.6
**Files:** `src/UnifiedLlm/HttpAdapters.fs`

- [ ] Populate `Usage.CacheWriteTokens` from `usage.cache_creation_input_tokens`
- [ ] Verify `prompt-caching-2024-07-31` beta header is NOT needed (it's GA now — confirm)
- [ ] Add `provider_options.anthropic.auto_cache = false` support to disable cache_control injection

**Definition of Done:**
- Multi-turn test: turn 1 has CacheWriteTokens > 0, turn 2 has CacheReadTokens > 0
- Disabling auto_cache produces request without cache_control blocks

### Task 6.2: Gemini caching
**Spec:** 2.10
**Files:** `src/UnifiedLlm/HttpAdapters.fs`

- [ ] Map `cachedContentTokenCount` from Gemini usageMetadata to `Usage.CacheReadTokens`
- [ ] Document that Gemini prefix caching is automatic (no client-side configuration)

**Definition of Done:**
- Usage.CacheReadTokens populated from Gemini response when cache hit occurs

## Phase 7: Conformance & Cross-Provider Parity

### Task 7.1: Spec 8.9 cross-provider parity matrix
**Spec:** 8.9
**Files:** `conformance/` (new test category or extend existing)

Implement the full cross-provider test matrix from the spec:

- [ ] Simple text generation: OpenAI, Anthropic, Gemini
- [ ] Streaming text generation: OpenAI, Anthropic, Gemini
- [ ] Single tool call + execution: OpenAI, Anthropic, Gemini
- [ ] Multiple parallel tool calls: OpenAI, Anthropic, Gemini
- [ ] Multi-step tool loop (3+ rounds): OpenAI, Anthropic, Gemini
- [ ] Streaming with tool calls: OpenAI, Anthropic, Gemini
- [ ] Structured output (generate_object): OpenAI, Anthropic, Gemini
- [ ] Reasoning/thinking token reporting: OpenAI, Anthropic, Gemini
- [ ] Error handling (invalid API key → 401): OpenAI, Anthropic, Gemini
- [ ] Usage token counts accurate: OpenAI, Anthropic, Gemini
- [ ] Prompt caching (cache_read > 0 on turn 2+): OpenAI, Anthropic, Gemini

**Definition of Done:**
- All cells in the matrix pass with real API calls
- Test runner produces a clear matrix output showing pass/fail per cell
- No test fakes provider responses — all real API calls

## Sprint Summary

| Phase | Tasks | Priority | Estimated Effort |
|-------|-------|----------|-----------------|
| 1. Foundation Types | 1.1-1.4 | High | 4 tasks |
| 2. Streaming | 2.1-2.4 | Critical | 4 tasks |
| 3. Tool Calling | 3.1-3.4 | Critical | 4 tasks |
| 4. Thinking | 4.1-4.2 | High | 2 tasks |
| 5. Retry | 5.1-5.2 | High | 2 tasks |
| 6. Caching | 6.1-6.2 | Medium | 2 tasks |
| 7. Conformance | 7.1 | Critical | 1 task (large) |
| **Total** | **19 tasks** | | |

## Dependency Order

```
Phase 1 (types) → Phase 2 (streaming) → Phase 3.4 (streaming+tools)
Phase 1 (types) → Phase 3.1-3.3 (tool completeness)
Phase 1 (types) → Phase 4 (thinking)
Phase 1 (types) → Phase 5 (retry)
Phase 1 (types) → Phase 6 (caching)
All phases → Phase 7 (conformance matrix)
```

Phase 1 is the foundation — every other phase depends on the type changes. Phases 2-6
can proceed in parallel after Phase 1. Phase 7 is the final validation gate.

## Key Files

| File | Changes |
|------|---------|
| `src/UnifiedLlm/Types.fs` | Request fields, ResponseFormat, RateLimitInfo, StreamEvent |
| `src/UnifiedLlm/HttpAdapters.fs` | Streaming, thinking extraction, field serialization, caching |
| `src/UnifiedLlm/Generation.fs` | Parallel tools, retry integration, structured output |
| `src/UnifiedLlm/Tools.fs` | Argument validation |
| `src/UnifiedLlm/Retry.fs` | No changes (already correct) |
| `tests/UnifiedLlm.Tests/Tests.fs` | Tests for all new functionality |

## Spec Reference

All gap items trace to `unified-llm-spec.md`:
- Section 3: Data Model (3.6, 3.7, 3.8, 3.10, 3.12, 3.13)
- Section 4: Generation and Streaming (4.2, 4.3, 4.5, 4.7)
- Section 5: Tool Calling (5.3, 5.7, 5.8, 5.9)
- Section 6: Error Handling (6.9)
- Section 7: Provider Adapter Contract (7.7)
- Section 8: Definition of Done (8.2, 8.5, 8.6, 8.7, 8.8, 8.9)
