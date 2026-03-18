# Bug Report: Silent Failure on Non-Retriable LLM Errors

**Date:** 2026-03-18
**Severity:** High — a pipeline with a failed critical LLM node reports `Result: SUCCESS` and routes forward as if nothing went wrong
**Discovered via:** `decide_continue` (model `claude-sonnet-4-6-20250819`, `not_found_error` 404) — retried 3x, pipeline routed to `announce_done`, reported SUCCESS

---

## Summary

Two bugs compound to turn a hard LLM failure into a silent success. A 404 `not_found_error` (non-retriable by design — `NotFoundError.Retryable = false`) is retried up to `MaxAttempts` times anyway, and after exhaustion the engine routes to the highest-weight outgoing edge regardless of whether its condition matched. The pipeline completes with `Result: SUCCESS`.

---

## Bug 1 — Non-retriable provider errors are retried anyway

**File:** `src/Attractor/Engine.fs`
**Function:** `executeWithRetry`, line 231

### What happens

All exceptions thrown by `handler.Execute` are caught uniformly and retried up to `MaxAttempts` times without checking `ProviderError.Retryable`. The `UnifiedLlm` error hierarchy correctly marks permanent errors as non-retriable:

```fsharp
// Errors.fs — these are permanent, should never be retried
NotFoundError    (404, Retryable = false)  // invalid model ID
AuthenticationError (401, Retryable = false)
AccessDeniedError   (403, Retryable = false)
InvalidRequestError (400/422, Retryable = false)
```

But `executeWithRetry` ignores the flag:

```fsharp
// Engine.fs:231-240
with ex ->
    if attempt < retryPolicy.MaxAttempts then
        emitter.Emit(PipelineEvent.StageRetrying(...))   // ← retries 404 just like 429
        ...attempt <- attempt + 1
    else
        finalOutcome <- Outcome.Fail(ex.Message)
        cont <- false
```

A `NotFoundError` (bad model ID) hits this path and is retried 3× before failing. This wastes time and, with the right `MaxAttempts`, can add tens of seconds of futile delay per failed node.

### Fix

Check `ProviderError.Retryable` before deciding to retry:

```fsharp
with ex ->
    let isRetriable =
        match ex with
        | :? ProviderError as pe -> pe.Retryable
        | _ -> true  // unknown exceptions are assumed transient
    if isRetriable && attempt < retryPolicy.MaxAttempts then
        emitter.Emit(PipelineEvent.StageRetrying(...))
        ...attempt <- attempt + 1
    else
        finalOutcome <- Outcome.Fail(ex.Message)
        cont <- false
```

---

## Bug 2 — `selectEdge` falls back to highest-weight edge ignoring failed outcome

**File:** `src/Attractor/Engine.fs`
**Function:** `EdgeSelection.selectEdge`, line 122

### What happens

`selectEdge` has a 5-step fallback cascade to find the next edge. Step 5 is the final fallback when no unconditioned edges exist:

```fsharp
// Engine.fs:118-122
let unconditional = edges |> List.filter (fun e -> e.Condition = "")
if not unconditional.IsEmpty then
    bestByWeightThenLexical unconditional
else
    bestByWeightThenLexical edges   // ← BUG: picks ANY edge, conditions ignored
```

When all outgoing edges from a node are conditioned (as they are on `decide_continue`), and none of their conditions match (because the node failed), Step 5 picks the **highest-weight edge regardless of its condition**. For `decide_continue`:

```dot
decide_continue -> assess        [condition="context.decision=CONTINUE"]
decide_continue -> announce_done [condition="context.decision=DONE", weight=10]
```

No condition matches (no decision was made — the LLM failed). Step 5 selects `announce_done` (weight=10). The pipeline routes forward to `announce_done` and then reports `Result: SUCCESS`.

### Why this is wrong

The fallback is designed for the common case where a node has only unconditioned outgoing edges and you need a tiebreak. Falling through to **conditioned** edges whose conditions did **not** match is incorrect in all cases, and catastrophically wrong when the node failed: it silently routes the pipeline forward as if the node had produced a valid outcome.

### Fix

Remove the Step 5 fallback to conditioned edges. Return `None` when no unconditioned edge exists and no condition matched:

```fsharp
let unconditional = edges |> List.filter (fun e -> e.Condition = "")
if not unconditional.IsEmpty then
    bestByWeightThenLexical unconditional
else
    None  // no match — caller decides (halt pipeline on Fail, or end naturally on terminal node)
```

The callers already handle `None` correctly:
- Regular run (`Engine.run`, line 634): `None` + `Fail` → halts pipeline
- Resume run (`Engine.resumeFromCheckpoint`, line 917): `None` + `Fail` → halts pipeline

---

## Combined failure scenario (what we observed)

```
decide_continue LLM call → NotFoundError(404, Retryable=false)
  ↓ Bug 1: retried 3× anyway
  ↓ Outcome.Fail("...")
  ↓ Bug 2: selectEdge falls to bestByWeightThenLexical(all edges)
  ↓ picks announce_done [weight=10] despite condition never matching
  ↓ announce_done runs
  ↓ Pipeline reports: Result: SUCCESS
```

---

## Fix Checklist

- [ ] `src/Attractor/Engine.fs` `executeWithRetry` ~line 231: check `ProviderError.Retryable` before retrying exceptions
- [ ] `src/Attractor/Engine.fs` `selectEdge` ~line 122: replace `bestByWeightThenLexical edges` with `None` as the final fallback when no unconditional edge exists

---

## Notes

- Both fixes are small, targeted, and non-breaking for well-formed pipelines (correct pipelines have unconditional edges or matching conditions, so the new `None` fallback is never reached in the happy path)
- After Bug 1 fix: a 404 will fail immediately on the first attempt (no wasted retries)
- After Bug 2 fix: a failed node with no matching edge will halt the pipeline with a clear error (`Stage 'decide_continue' failed with no outgoing fail edge`) rather than routing silently forward
