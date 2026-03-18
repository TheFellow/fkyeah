# Bug Report: Attractor Resume Path Skips Stylesheet + Stale Fallback Model ID

**Date:** 2026-03-18
**Severity:** High — resume runs silently use wrong LLM model for all stylesheet-assigned nodes
**Discovered via:** `attractor --resume` run of `pipelines/tdd-cycle.dot` where `decide_continue` (class `deep`) errored with `not_found_error: model: claude-sonnet-4-6-20250819`

---

## Summary

Two related bugs cause attractor resume runs to call the Anthropic API with an invalid model ID, producing a `not_found_error` on every stylesheet-dependent LLM node.

---

## Bug 1 — Resume path skips stylesheet transforms (root cause)

**File:** `src/Attractor.Cli/Program.fs`, line 675
**Function:** `resume`

### What happens

The `resume` function re-parses the DOT file using `Pipeline.parseOrRaise`, which is a raw DOT parse (`DotParser.parseOrRaise`) with **no transforms applied**. The stylesheet transform (`Stylesheet.apply`) is only invoked inside `Transforms.preparePipeline`, which is called by `runFromSource` (Engine.fs:947) but **not** by the resume path.

As a result, every node's `LlmModel` property is empty string after a resume. The `LlmBackend.Run` method falls through to the hardcoded fallback model (Bug 2).

### Affected code

```fsharp
// Program.fs:674-675 — resume function
let source = File.ReadAllText(f)
let graph = Pipeline.parseOrRaise source   // ← BUG: no stylesheet applied
```

### Fix

Replace `Pipeline.parseOrRaise` with `Transforms.preparePipeline` so the full transform pipeline (variable expansion + stylesheet) runs on resume, exactly as it does on a fresh run:

```fsharp
let source = File.ReadAllText(f)
let (graph, _) = Transforms.preparePipeline source None  // ← applies stylesheet
```

---

## Bug 2 — Stale hardcoded fallback model ID

**Files & lines:**
- `src/Attractor.Cli/Program.fs:93`
- `src/UnifiedLlm/HttpAdapters.fs:571`
- `src/UnifiedLlm/HttpAdapters.fs:772`
- `src/UnifiedLlm/HttpAdapters.fs:783`

### What happens

When `node.LlmModel` is empty (as happens on every resume due to Bug 1, or if a node has no stylesheet match), the code falls back to the hardcoded string `"claude-sonnet-4-6-20250819"`. This date suffix does not correspond to any real Anthropic model release and the API returns `not_found_error`.

### Affected code

```fsharp
// Program.fs:91-93
let model =
    if node.LlmModel <> "" then node.LlmModel
    else "claude-sonnet-4-6-20250819"   // ← invalid dated ID

// HttpAdapters.fs:570-572
let model =
    if request.Model = "" then "claude-sonnet-4-6-20250819"  // ← invalid
    else request.Model

// HttpAdapters.fs:772, 783 — same pattern
```

### Fix

Change the fallback to the undated alias `"claude-sonnet-4-6"` which the Anthropic API resolves correctly:

```fsharp
else "claude-sonnet-4-6"
```

---

## Reproduction

```
attractor pipelines/tdd-cycle.dot           # first run (works — fresh path calls preparePipeline)
# kill mid-run
attractor --resume attractor-logs/<dir> pipelines/tdd-cycle.dot
# → any LLM node hits: not_found_error: model: claude-sonnet-4-6-20250819
```

---

## Impact

- All `--resume` runs are broken for any pipeline using `model_stylesheet` class/shape selectors
- Fresh runs are unaffected (stylesheet applied correctly via `runFromSource`)
- Nodes degrade silently — the retry loop exhausts and falls through rather than crashing, masking the error

---

## Fix Checklist

- [ ] `src/Attractor.Cli/Program.fs:675` — use `Transforms.preparePipeline source None |> fst` instead of `Pipeline.parseOrRaise source`
- [ ] `src/Attractor.Cli/Program.fs:93` — change fallback to `"claude-sonnet-4-6"`
- [ ] `src/UnifiedLlm/HttpAdapters.fs:571` — change fallback to `"claude-sonnet-4-6"`
- [ ] `src/UnifiedLlm/HttpAdapters.fs:772` — change fallback to `"claude-sonnet-4-6"`
- [ ] `src/UnifiedLlm/HttpAdapters.fs:783` — change fallback to `"claude-sonnet-4-6"`
