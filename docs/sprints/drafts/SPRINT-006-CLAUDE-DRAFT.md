# Sprint 006: Spec Sync — fb57a55

**Status:** Draft
**Spec:** `attractor-spec.md`, `coding-agent-loop-spec.md`, `unified-llm-spec.md` (commit fb57a55)
**Codebase:** v0.3.1, ~512 tests, Sprints 001–005 complete

## Overview

Synchronize fkyeah against the upstream attractor spec commit `fb57a55` ("Fix spec inconsistencies and refresh model guidance"). The spec tightened 15 behaviors: BareValue grammar, lifecycle ordering, retry inheritance, edge-selection restrictions, parallel handler cleanup, fidelity defaults, condition quote-stripping, pre-hook semantics, and model catalog updates. Most changes are surgical (1–10 line edits per file). The largest structural change is making `Node.MaxRetries` an `Option<int>` for inheritance semantics.

## Use Cases

- **Bare identifiers in DOT attributes.** Authors write `llm_model=claude-opus-4-6` (no quotes) and it parses correctly because `:` and `-` are now valid identifier characters.
- **Retry inheritance.** A graph sets `default_max_retries=3`; nodes without explicit `max_retries` inherit it. A node with `max_retries=0` explicitly overrides to zero retries.
- **Deterministic edge selection.** Steps 2 (preferred label) and 3 (suggested next IDs) only match unconditional edges. No more silent fallback to the lexicographically-first edge when no edges truly match — the engine returns `None` and halts or follows failure routing.
- **Correct condition matching.** `outcome=success` and `outcome="success"` both work identically because `evaluateClause` strips surrounding quotes from the RHS literal.
- **Lifecycle correctness.** Transforms (stylesheet, variable expansion) run before validation, ensuring the validator sees the final graph. This already works for fresh runs via `preparePipeline`; the resume path also re-runs transforms via the same function.

## Architecture

No new abstractions. The three-layer stack (UnifiedLlm / CodingAgent / Attractor) is unchanged. Key architectural decisions:

1. **`Node.MaxRetries` → `Option<int>`**. The `MaxRetries` computed member on `Node` changes from `int` (defaulting to 0) to `int option` where `None` means "not set — inherit from graph." This is the idiomatic F# way to distinguish "explicitly zero" from "absent." Callers that pattern-match or use `.Value` must be updated.

2. **BareValue: widen `isIdentChar`, not a new DU case.** Adding `:` and `-` to the lexer's `isIdentChar` function causes tokens like `claude-opus-4-6` and `summary:high` to lex as `Token.Identifier`, which the parser already maps to `AttrValue.String`. No new discriminated union case needed. This is the minimal change that satisfies the spec's `BareValue ::= [A-Za-z_][A-Za-z0-9_.:-]*` grammar.

3. **Transforms re-run on resume.** The CLI resume path (`Program.fs:675`) already calls `Transforms.preparePipeline` before `Engine.resumeFromCheckpoint`. No additional work needed — this is a verification item, not an implementation item.

4. **Fidelity default: `compact` not `full`.** Per spec §5.4, the runtime fallback when no fidelity is set at any level is `compact`. The current code defaults to `FidelityMode.Full` in `FidelityResolution.resolve`.

## Implementation

### Phase 1: Parser and Conditions (Items 1, 13)

**Item 1 — BareValue grammar**

*File:* `src/Attractor/DotParser.fs`
*Line 33:* Change `isIdentChar` to include `:` and `-`.

```fsharp
// Before (line 33):
let private isIdentChar c = Char.IsLetterOrDigit(c) || c = '_' || c = '.'

// After:
let private isIdentChar c = Char.IsLetterOrDigit(c) || c = '_' || c = '.' || c = ':' || c = '-'
```

*Interaction with `-` in other contexts:* The lexer already handles `-` as the start of negative numbers (line 159) and `->` arrow tokens (line 157) before falling through to identifier matching. Adding `-` to `isIdentChar` only affects characters *within* an identifier that started with `isIdentStart` (letter or underscore). So `A -> B` still tokenizes correctly — the `-` at position 0 of a token hits the arrow/number rules first. But `claude-opus-4-6` starting from `c` reads the whole bare value as one identifier.

*Edge case:* A value like `30s` (duration) still works because the number path is tried before the identifier path. A value like `-3` still works because the negative-number rule fires on `-` followed by a digit.

**Item 13 — Strip quotes in condition literals**

*File:* `src/Attractor/Conditions.fs`
*Function:* `evaluateClause` (lines 30–53)

The spec's `parse_literal` function (§10.5) strips surrounding double-quotes from the RHS of a condition clause. Currently `evaluateClause` does a raw `parts[1].Trim()` comparison. Add quote-stripping after trimming.

```fsharp
// In evaluateClause, after splitting on "!=" or "=", add:
let private parseLiteral (value: string) =
    let v = value.Trim()
    if v.Length >= 2 && v.[0] = '"' && v.[v.Length - 1] = '"' then
        v.Substring(1, v.Length - 2)
    else
        v
```

Then replace `parts[1].Trim()` with `parseLiteral parts[1]` in both the `!=` and `=` branches of `evaluateClause`.

**DoD Phase 1:**
- `Lexer.tokenize "llm_model=claude-opus-4-6"` produces `[Identifier "llm_model"; Equals; Identifier "claude-opus-4-6"; Eof]`
- `Lexer.tokenize "fidelity=summary:high"` produces `[Identifier "fidelity"; Equals; Identifier "summary:high"; Eof]`
- Negative numbers and `->` arrows still parse correctly (regression tests)
- `evaluateClause "outcome=\"success\"" ...` and `evaluateClause "outcome=success" ...` both return `true` when outcome is Success
- Unit tests for `parseLiteral` edge cases: empty string, single quote, no quotes, already-bare

---

### Phase 2: Lifecycle and Validation (Items 2, 12)

**Item 2 — TRANSFORM before VALIDATE**

The spec lifecycle is: `PARSE -> TRANSFORM -> VALIDATE -> INITIALIZE -> EXECUTE -> FINALIZE`.

*Current state:* `Transforms.preparePipeline` (Transforms.fs:46–54) already implements PARSE → TRANSFORM → VALIDATE in that order:
```fsharp
let graph = DotParser.parseOrRaise source          // PARSE
let transformed = applyAll transforms graph         // TRANSFORM
let diagnostics = Validation.validate transformed None  // VALIDATE
```

`Engine.runFromSource` (Engine.fs:951–956) calls `Transforms.preparePipeline` then `run`. The CLI resume path (Program.fs:675) also calls `Transforms.preparePipeline` before `resumeFromCheckpoint`.

**This is already correct.** The `Library.fs` `Pipeline` module exposes `prepare = Transforms.preparePipeline` which does the right order. Verify with a test, no code change needed.

However, `Library.fs` currently exposes `validate` and `validateOrRaise` as standalone functions that take an *untransformed* graph. If someone calls `Pipeline.validate` on a raw parsed graph (without calling `Pipeline.prepare` first), they validate pre-transform. Add a `Pipeline.prepareAndValidate` convenience or document the ordering requirement.

*Decision:* No code change — the existing `preparePipeline` function already does TRANSFORM then VALIDATE. Add a unit test that confirms transforms run before validation by constructing a graph that only passes validation after transformation (e.g., a stylesheet that sets a required attribute).

**Item 12 — terminal_node rule uses `FindExitNode()`**

*File:* `src/Attractor/Validation.fs`
*Lines 75–92:* The `terminalNodeRule` currently filters nodes by `n.Shape = "Msquare"` directly. The spec says to use `FindExitNode()` which also matches by ID (`exit`, `end`). Update to use the helper.

```fsharp
// Before (lines 79-82):
let exitNodes =
    graph.Nodes
    |> Map.toList
    |> List.filter (fun (_, n) -> n.Shape = "Msquare")

// After:
let exitNodes =
    graph.Nodes
    |> Map.toList
    |> List.filter (fun (_, n) ->
        n.Shape = "Msquare"
        || n.Id = "exit"
        || n.Id = "end")
```

Wait — `FindExitNode()` returns at most *one* node (the first match). But the validation rule needs to count *all* exit nodes to enforce "exactly one." We can't just call `FindExitNode()` because it collapses multiple matches. Instead, widen the filter predicate to match the same criteria as `FindExitNode`: shape=Msquare OR id=exit OR id=end. This ensures the validation rule is consistent with the runtime exit-node resolution.

**DoD Phase 2:**
- Unit test: a graph with transforms that must run before validation passes successfully
- `terminalNodeRule` detects exit nodes by shape OR id (`exit`/`end`)
- Existing validation tests still pass (no regressions)

---

### Phase 3: Retry Inheritance (Items 3, 4)

**Item 3 — `default_max_retries` rename + legacy alias + default 0**

*File:* `src/Attractor/Types.fs`
*Lines 389–393:* `Graph.DefaultMaxRetry` reads from `default_max_retry` with default 50.

```fsharp
// Before (lines 389-393):
member this.DefaultMaxRetry =
    this.GraphAttributes
    |> Map.tryFind "default_max_retry"
    |> Option.bind (fun v -> v.AsInt())
    |> Option.defaultValue 50

// After — rename to DefaultMaxRetries, read both keys, default 0:
member this.DefaultMaxRetries =
    this.GraphAttributes
    |> Map.tryFind "default_max_retries"
    |> Option.orElseWith (fun () -> this.GraphAttributes |> Map.tryFind "default_max_retry")
    |> Option.bind (fun v -> v.AsInt())
    |> Option.defaultValue 0

// Legacy alias (backward compat for callers):
member this.DefaultMaxRetry = this.DefaultMaxRetries
```

**Item 4 — `Node.MaxRetries` becomes `Option<int>`**

*File:* `src/Attractor/Types.fs`
*Lines 212–216:* Currently returns `int` defaulting to 0.

```fsharp
// Before (lines 212-216):
member this.MaxRetries =
    this.Attributes
    |> Map.tryFind "max_retries"
    |> Option.bind (fun v -> v.AsInt())
    |> Option.defaultValue 0

// After — returns Option<int>, None = not set (inherit from graph):
member this.MaxRetries =
    this.Attributes
    |> Map.tryFind "max_retries"
    |> Option.bind (fun v -> v.AsInt())
```

*File:* `src/Attractor/Engine.fs`
*Lines 39–45:* `RetryPolicy.FromNode` must implement the inheritance chain:

```fsharp
// Before (lines 39-45):
static member FromNode(node: Node, graph: Graph) =
    let maxRetries =
        if node.MaxRetries > 0 then node.MaxRetries
        elif graph.DefaultMaxRetry > 0 && graph.DefaultMaxRetry <> 50 then graph.DefaultMaxRetry
        else 0
    { MaxAttempts = maxRetries + 1
      Backoff = BackoffConfig.Default }

// After — clean inheritance: node explicit > graph default > 0:
static member FromNode(node: Node, graph: Graph) =
    let maxRetries =
        match node.MaxRetries with
        | Some n -> n                  // Node explicitly sets max_retries (even if 0)
        | None -> graph.DefaultMaxRetries  // Inherit from graph (which defaults to 0)
    { MaxAttempts = maxRetries + 1
      Backoff = BackoffConfig.Default }
```

*Downstream impact:* Search the codebase for `node.MaxRetries` and `\.MaxRetries` to find all call sites that need updating. The only production usage is `RetryPolicy.FromNode`. Test code that constructs nodes with `MaxRetries` may need updating if it reads the value directly.

**DoD Phase 3:**
- `RetryPolicy.FromNode` with node `max_retries=None`, graph `default_max_retries=3` → `MaxAttempts=4`
- `RetryPolicy.FromNode` with node `max_retries=Some 0`, graph `default_max_retries=5` → `MaxAttempts=1` (explicit zero overrides)
- `RetryPolicy.FromNode` with both unset → `MaxAttempts=1` (default 0 retries)
- Legacy attribute name `default_max_retry` still works
- `Graph.DefaultMaxRetry` alias still compiles (backward compat)

---

### Phase 4: Edge Selection (Items 6, 7, 11)

**Item 6 — Steps 2 & 3: restrict to unconditional edges**

*File:* `src/Attractor/Engine.fs`
*Lines 93–113:* In `EdgeSelection.selectEdge`, Steps 2 and 3 currently search all `edges`. Per spec §3.3, they should only consider unconditional edges.

```fsharp
// Step 2 (lines 93-101): Change from searching `edges` to filtering first:
let labelMatch =
    if outcome.PreferredLabel <> "" then
        let normalizedPref = AcceleratorKey.normalizeLabel outcome.PreferredLabel
        edges
        |> List.tryFind (fun e ->
            e.Condition = "" &&
            AcceleratorKey.normalizeLabel e.Label = normalizedPref)
    else
        None

// Step 3 (lines 105-109): Same restriction:
let suggestedMatch =
    if not outcome.SuggestedNextIds.IsEmpty then
        outcome.SuggestedNextIds
        |> List.tryPick (fun suggestedId ->
            edges |> List.tryFind (fun e ->
                e.Condition = "" && e.ToNode = suggestedId))
    else
        None
```

**Item 7 — Remove fallback `bestByWeightThenLexical(edges)`**

*File:* `src/Attractor/Engine.fs`

The spec's edge selection returns `NONE` when no step matches. Currently Steps 4 & 5 (lines 118–121) fall back to `bestByWeightThenLexical unconditional`. Per spec, this is correct — Steps 4 & 5 select among unconditional edges by weight/lexical. This IS the spec behavior. The "remove fallback" item refers to ensuring there is NO fallback beyond Step 5. Currently, the code at line 122 returns `None` when `unconditional` is empty, which is correct.

*Verification:* Review the code path. After Step 1 (condition match), Step 2 (preferred label among unconditional), Step 3 (suggested IDs among unconditional), Steps 4&5 pick the best unconditional edge. If no unconditional edges exist, return `None`. The `bestByWeightThenLexical` helper is only called on `conditionMatched` (Step 1) and `unconditional` (Steps 4&5) — never on the full edge set as a final fallback. **This is already correct per spec.** The private `bestByWeightThenLexical` function stays; what matters is it's never called on all edges indiscriminately.

*However*, the current code on line 90 calls `bestByWeightThenLexical conditionMatched` for Step 1, which returns an `Edge option`. Confirm this matches spec: "If condition_matched is not empty: RETURN best_by_weight_then_lexical(condition_matched)." Yes, this is correct.

**Item 11 — `preferred_label` write path**

*File:* `src/Attractor/Engine.fs`
*Lines 596–597:* The code already writes `preferred_label` to context:

```fsharp
if outcome.PreferredLabel <> "" then
    context.Set("preferred_label", outcome.PreferredLabel)
```

*File:* `src/Attractor/Engine.fs`
*Lines 340–342:* The `tryLoadStatusOutcome` function reads `preferred_next_label` first, then falls back to `preferred_label`:

```fsharp
let preferredLabel =
    tryGetJsonString root "preferred_next_label"
    |> Option.orElseWith (fun () -> tryGetJsonString root "preferred_label")
    |> Option.defaultValue fallback.PreferredLabel
```

*File:* `src/Attractor/Handlers.fs`
*Line 204:* The `writeStatus` function writes `preferred_next_label`:

```fsharp
let status =
    {| outcome = outcome.Status.ToString()
       preferred_next_label = outcome.PreferredLabel
       ...
```

Per spec, the **write** path should use `preferred_label`. The **read** path should accept both for backward compat. Update `writeStatus`:

```fsharp
// Before (Handlers.fs line 204):
preferred_next_label = outcome.PreferredLabel

// After:
preferred_label = outcome.PreferredLabel
```

The read path in `tryLoadStatusOutcome` already handles both names — keep it as-is for backward compat with old checkpoints.

**DoD Phase 4:**
- Step 2: preferred label only matches unconditional edges (test: conditional edge with matching label is skipped)
- Step 3: suggested IDs only match unconditional edges (test: conditional edge with matching target is skipped)
- `writeStatus` writes `preferred_label` (not `preferred_next_label`)
- `tryLoadStatusOutcome` still reads both `preferred_next_label` and `preferred_label`
- Edge selection returns `None` when all edges are conditional and none match

---

### Phase 5: Fidelity, Context, and Handlers (Items 8, 9, 10, 14)

**Item 14 — Fidelity fallback: `compact` not `full`**

*File:* `src/Attractor/Engine.fs`
*Lines 152–175:* `FidelityResolution.resolve`

```fsharp
// Before (lines 167-175):
if node.Fidelity <> "" then
    FidelityMode.Parse(node.Fidelity) |> Option.defaultValue FidelityMode.Full
else
    if graph.DefaultFidelity <> "" then
        FidelityMode.Parse(graph.DefaultFidelity) |> Option.defaultValue FidelityMode.Full
    else
        FidelityMode.Full

// After — change all three FidelityMode.Full defaults to FidelityMode.Compact:
if node.Fidelity <> "" then
    FidelityMode.Parse(node.Fidelity) |> Option.defaultValue FidelityMode.Compact
else
    if graph.DefaultFidelity <> "" then
        FidelityMode.Parse(graph.DefaultFidelity) |> Option.defaultValue FidelityMode.Compact
    else
        FidelityMode.Compact
```

*Note:* The `Option.defaultValue` cases only fire for invalid/unparseable fidelity strings. The final `else` is the true default for "no fidelity set anywhere." Changing all three to `Compact` is consistent with the spec: "Default when unset: `compact`."

**Item 9 — Context deep-copy: verify strings are immutable, update comments**

*File:* `src/Attractor/Types.fs`
*`Context.Clone()` method (lines 557–567):*

F# strings are .NET `System.String` — immutable by definition. The `Clone()` method copies key-value pairs by reference, which is safe because strings can't be mutated. Add a doc comment clarifying this:

```fsharp
/// Clone the context. String values are shared by reference (safe because
/// System.String is immutable). The clone has an independent Dictionary and
/// log list so mutations do not affect the original.
member _.Clone() = ...
```

No functional code change. Comment-only.

**Item 8 — Parallel handler: results before join; remove error_policy/k_of_n/quorum**

*File:* `src/Attractor/Handlers.fs`
*`ParallelHandler` (lines 604–678):*

The current implementation already:
1. Executes branches concurrently (lines 612–644)
2. Collects results (line 644)
3. Writes per-branch results to context (lines 650–659)
4. Determines status (lines 661–663)

This already matches "results before join" — results are stored, then the join policy (implicit wait_all) determines the final status. **No structural change needed.**

For removal of `error_policy`, `k_of_n`, `quorum`: these attributes are never referenced in the current `ParallelHandler` implementation. They don't exist in the codebase. **Nothing to remove — already compliant.** Add a comment noting these are explicitly unsupported per spec.

**Item 10 — Pre-hook failure skips tool call**

*File:* `src/Attractor/Handlers.fs`
*`ToolHandler.Execute` (lines 699–829):*

Currently `runHook` (lines 172–199) prints a warning on non-zero exit but doesn't return a value indicating failure. The `ToolHandler` calls `runHook` for the pre-hook at line 723 but ignores the result and proceeds to execute the tool command regardless.

Per spec §9.7: "Pre-hook: Exit code 0 means proceed; non-zero means skip the tool call."

*Changes:*
1. Make `runHook` return a `bool` (true = success, false = failure):

```fsharp
// Before (lines 172-199): returns unit
let private runHook ... : unit =

// After: returns bool (true = ok, false = failed/skip)
let private runHook ... : bool =
    if hookCommand = "" then
        true
    else
        ...
        if proc.ExitCode <> 0 then
            eprintfn "Warning: hook command failed for node %s: %s" nodeId stderr
            false
        else
            true
```

2. In `ToolHandler.Execute`, check the pre-hook result and skip tool execution on failure:

```fsharp
// After pre-hook call (around line 723):
let preHookOk =
    runHook
        node.ToolHooksPre
        workingDir
        node.Id
        stageDir
        logsRoot
        [ "TOOL_NAME", "shell"
          "TOOL_ARGS", command
          "NODE_ID", node.Id ]
if not preHookOk then
    Outcome.Fail($"Pre-hook failed for node '{node.Id}', tool call skipped")
else
    // ... existing tool execution code ...
```

3. Update `CodingAgentHandler` similarly — pre-hook failure should skip the tool call in the coding agent's `toolCallHook`:

```fsharp
// In CodingAgentHandler (around lines 336-367), the toolCallHook lambda:
// Currently runHook is called as a side effect. If pre-hook fails,
// the tool call should be skipped. The CodingAgent's ToolCallHook
// mechanism needs to support returning a "skip" signal.
```

*Note:* The `CodingAgentHandler`'s `toolCallHook` is a `Action`-style callback (returns `unit`). To support pre-hook skip, the hook would need to return a bool or throw. This is a deeper change in the CodingAgent layer. For this sprint, implement pre-hook skip in `ToolHandler` only. The `CodingAgentHandler` pre-hook behavior can be tracked as a follow-up.

**DoD Phase 5:**
- `FidelityResolution.resolve` returns `Compact` when no fidelity is set at any level
- Context.Clone() has updated immutability comment
- ToolHandler pre-hook non-zero exit → `Outcome.Fail`, tool command not executed
- Parallel handler has comment noting `error_policy`/`k_of_n`/`quorum` are unsupported per spec
- Unit test: pre-hook fails → tool not run, outcome is Fail

---

### Phase 6: Model Catalog and Verification (Items 5, 15)

**Item 15 — ModelCatalog: `gemini-3.1-pro-preview`**

*File:* `src/UnifiedLlm/ModelCatalog.fs`
*Lines 75–80:* Rename the model ID and update display name.

```fsharp
// Before (lines 75-80):
{ Id = "gemini-3-pro-preview"; Provider = "gemini"; DisplayName = "Gemini 3 Pro (Preview)"
  ...
  Aliases = [ "gemini-3-pro"; "gemini-pro" ]

// After:
{ Id = "gemini-3.1-pro-preview"; Provider = "gemini"; DisplayName = "Gemini 3.1 Pro (Preview)"
  ...
  Aliases = [ "gemini-3.1-pro"; "gemini-pro"; "gemini-3-pro" ]
```

*Lines 90–94:* Update the `latestByProvider` map:

```fsharp
// Before (line 93):
"gemini", "gemini-3-pro-preview"

// After:
"gemini", "gemini-3.1-pro-preview"
```

Keep `"gemini-3-pro"` as an alias for backward compat.

**Item 5 — `goal_gate` accepts PARTIAL_SUCCESS (verify + add conformance test)**

*File:* `src/Attractor/Engine.fs`
*Lines 128–141:* `GoalGates.checkGoalGates` already accepts `PartialSuccess`:

```fsharp
match outcome.Status with
| StageStatus.Success | StageStatus.PartialSuccess -> None  // gate satisfied
| _ -> Some node  // gate failed
```

**Already correct.** Add a dedicated unit test that constructs a node outcome with `PartialSuccess`, calls `checkGoalGates`, and asserts the gate is satisfied.

**DoD Phase 6:**
- `ModelCatalog.getModelInfo "gemini-3.1-pro-preview"` returns `Some info`
- `ModelCatalog.getLatestModel "gemini"` returns the updated model
- Old alias `gemini-3-pro` still resolves (via alias lookup if implemented, or note as a test gap)
- Unit test: `checkGoalGates` passes with `PartialSuccess` outcome
- Unit test: `checkGoalGates` fails with `Fail` outcome

---

### Phase 7: Tests

New tests to add across test files:

**`tests/Attractor.Tests/`:**

| Test | Validates |
|------|-----------|
| `BareValue_colon_in_identifier` | `isIdentChar` accepts `:`, parses `summary:high` as identifier |
| `BareValue_hyphen_in_identifier` | `isIdentChar` accepts `-`, parses `claude-opus-4-6` as identifier |
| `BareValue_arrow_not_broken` | `A -> B` still tokenizes as `Identifier, Arrow, Identifier` |
| `BareValue_negative_number_not_broken` | `-3` still tokenizes as `IntegerLit -3` |
| `Condition_quoted_literal_matches` | `evaluateClause 'outcome="success"'` matches `Success` outcome |
| `Condition_unquoted_literal_matches` | `evaluateClause 'outcome=success'` matches `Success` outcome |
| `Condition_quoted_and_unquoted_equivalent` | Both forms produce identical results |
| `parseLiteral_strips_quotes` | `parseLiteral "\"hello\""` → `"hello"` |
| `parseLiteral_bare_passthrough` | `parseLiteral "hello"` → `"hello"` |
| `RetryPolicy_inherits_from_graph` | Node without max_retries inherits graph default |
| `RetryPolicy_explicit_zero_overrides` | Node max_retries=0 overrides graph default |
| `RetryPolicy_both_unset_means_no_retry` | Neither set → MaxAttempts=1 |
| `RetryPolicy_legacy_alias` | `default_max_retry` attribute still works |
| `EdgeSelection_step2_unconditional_only` | Preferred label skips conditional edges |
| `EdgeSelection_step3_unconditional_only` | Suggested ID skips conditional edges |
| `EdgeSelection_no_fallback_all_conditional` | Returns None when all edges are conditional and none match |
| `Fidelity_default_compact` | `FidelityResolution.resolve None node graph` returns `Compact` when nothing set |
| `GoalGate_partial_success_accepted` | `checkGoalGates` passes with PartialSuccess |
| `TerminalNode_validates_by_id` | Node with id=`exit` detected as terminal by validator |
| `WriteStatus_uses_preferred_label` | `writeStatus` JSON contains `preferred_label` key |
| `PreHook_failure_skips_tool` | ToolHandler returns Fail when pre-hook exits non-zero |

**`tests/UnifiedLlm.Tests/`:**

| Test | Validates |
|------|-----------|
| `ModelCatalog_gemini_31_pro_preview` | Model ID lookup succeeds |
| `ModelCatalog_gemini_latest` | `getLatestModel "gemini"` returns 3.1 |
| `ModelCatalog_gemini_3_pro_alias` | Old alias still resolves |

## Files Summary

| File | Changes |
|------|---------|
| `src/Attractor/DotParser.fs:33` | Add `:` and `-` to `isIdentChar` |
| `src/Attractor/Conditions.fs` | Add `parseLiteral` helper; use it in `evaluateClause` |
| `src/Attractor/Types.fs:212-216` | `Node.MaxRetries` → `int option` (remove `Option.defaultValue 0`) |
| `src/Attractor/Types.fs:389-393` | Rename `DefaultMaxRetry` → `DefaultMaxRetries`; read both keys; default 0; add legacy alias |
| `src/Attractor/Types.fs:557` | Add doc comment on `Clone()` string immutability |
| `src/Attractor/Engine.fs:39-45` | `RetryPolicy.FromNode` uses `Option` match for inheritance |
| `src/Attractor/Engine.fs:93-113` | Steps 2 & 3 filter `e.Condition = ""` |
| `src/Attractor/Engine.fs:152-175` | `FidelityResolution.resolve` defaults to `Compact` |
| `src/Attractor/Engine.fs:596-597` | Write path already correct; verify only |
| `src/Attractor/Handlers.fs:172-199` | `runHook` returns `bool` instead of `unit` |
| `src/Attractor/Handlers.fs:699-829` | `ToolHandler` checks pre-hook result, skips on failure |
| `src/Attractor/Handlers.fs:204` | `writeStatus` writes `preferred_label` (not `preferred_next_label`) |
| `src/Attractor/Handlers.fs:604-678` | Add comment re: unsupported parallel attrs |
| `src/Attractor/Validation.fs:79-82` | `terminalNodeRule` filter includes id=exit/end |
| `src/UnifiedLlm/ModelCatalog.fs:75-80,90-94` | Rename gemini model ID, update aliases and latest map |
| `tests/Attractor.Tests/Tests.fs` | ~20 new unit tests |
| `tests/UnifiedLlm.Tests/Tests.fs` | ~3 new unit tests |

## Definition of Done

- [ ] All 15 spec items addressed (implemented or verified as already correct)
- [ ] `dotnet build` → zero errors, zero warnings
- [ ] `dotnet test` → all existing 512+ tests pass
- [ ] ~23 new unit tests pass covering all changed behaviors
- [ ] `Node.MaxRetries` returns `int option`; all callers updated
- [ ] `Graph.DefaultMaxRetries` reads both attribute names, defaults to 0
- [ ] Edge selection Steps 2 & 3 only match unconditional edges
- [ ] `FidelityResolution.resolve` defaults to `Compact`
- [ ] `parseLiteral` strips surrounding quotes in condition evaluation
- [ ] `isIdentChar` accepts `:` and `-`
- [ ] `writeStatus` writes `preferred_label`; read path accepts both names
- [ ] Pre-hook failure in ToolHandler skips tool execution
- [ ] ModelCatalog updated to `gemini-3.1-pro-preview`
- [ ] No regressions on existing `.dot` example files

## Risks

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| `Node.MaxRetries` type change breaks test constructors | High | Medium | Search all `MaxRetries` usages in tests; update to use `Some n` or construct via attribute maps |
| BareValue `-` conflicts with negative number/arrow lexing | Low | High | Lexer precedence already handles this — `-` only matches inside identifiers started by a letter. Add regression tests for `-3` and `->` |
| Fidelity default change affects existing pipelines | Medium | Medium | Pipelines that relied on implicit `Full` fidelity will now get `Compact`. This matches spec intent but may need user awareness |
| `runHook` return type change from `unit` to `bool` | Medium | Low | All existing callers either ignore the return (CodingAgentHandler) or are updated (ToolHandler). CodingAgentHandler needs follow-up for pre-hook skip |
| `preferred_label` write path breaks old checkpoint readers | Low | Low | Old readers that expect `preferred_next_label` in status.json won't find it. The engine's read path handles both, but external tools may not |

## Security

- No new attack surface. Pre-hook skip behavior is a security improvement — tool execution is prevented when preconditions fail.
- No new external dependencies.
- The `runHook` change ensures defense-in-depth: a failing guard prevents downstream execution rather than logging and continuing.

## Dependencies

- Upstream spec: `attractor-spec.md` at commit `fb57a55`
- .NET 10.0, F# compiler
- Existing test infrastructure (xUnit)

## Open Questions

1. **CodingAgentHandler pre-hook skip.** The `CodingAgentHandler`'s `toolCallHook` is a side-effect callback (`unit` return). Supporting pre-hook skip there requires either changing the callback signature to return `bool`, or using an exception-based skip signal. This sprint implements pre-hook skip in `ToolHandler` only; CodingAgentHandler follows up in a future sprint.

2. **Fidelity default change impact.** Changing the default from `Full` to `Compact` is spec-correct but may affect existing pipelines that implicitly relied on full context. Consider whether this warrants a release note or migration guide.

3. **`terminalNodeRule` consistency with `FindExitNode`.** The validation rule now checks `Shape = "Msquare" || Id = "exit" || Id = "end"`, which is broader than the original shape-only check. This means a node with `id="exit"` and `shape="box"` would be counted as a terminal node for validation purposes. Is this the intended behavior? The spec says "shape=Msquare or id matching `exit`/`end`" — so yes.
