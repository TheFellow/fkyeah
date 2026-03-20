# Sprint 006: Spec Sync (fb57a55)

**Status:** Planned
**Spec:** `attractor-spec.md`, `coding-agent-loop-spec.md`, `unified-llm-spec.md` (commit `fb57a55`)
**Codebase:** `src/Attractor/`, `src/CodingAgent/`, `src/UnifiedLlm/`, `tests/`

## Overview

Sprint 006 syncs the fkyeah implementation against commit `fb57a55` ("Fix spec inconsistencies and refresh model guidance") from `strongdm/attractor`. The three spec files have been copied to the repo root. This sprint implements the 11 remaining behavioral deltas (4 items were already implemented).

**Already done — verify only:**
- Lifecycle TRANSFORM before VALIDATE (`Transforms.preparePipeline`, line 46-54)
- `goal_gate` accepts `PartialSuccess` (`Engine.fs:135`)
- Context deep copy (F# strings are immutable by value; just update comment on `Clone()`)
- Item 7 fallback already absent: after Steps 4&5 select best unconditional edge, `None` is returned when no unconditional edges exist — no all-edges fallback exists
- Parallel handler `error_policy`/`k_of_n`/`quorum` already absent from codebase — add comment only

**Real work (11 items):** lexer widening, retry rename + option-type inheritance, edge-selection steps 2&3 restriction, fidelity default → compact, `preferred_next_label` → `preferred_label` write fix in `Handlers.fs:204`, pre-hook skip (both Attractor ToolHandler and CodingAgent Session), condition quote-stripping, validation helper alignment, model catalog update, and new tests.

## Use Cases

1. `graph [default_max_retries=5]` with no node override retries five times; `max_retries=0` on a node still means no retry, overriding the graph default.
2. A node returning `preferred_label="Fix"` does **not** traverse a conditional edge labeled `Fix` — only unconditional edges are candidates for steps 2 and 3.
3. Condition `outcome="success"` and `outcome=success` evaluate identically; `fidelity=summary:high` parses without quotes.
4. A failed pre-hook produces `StageStatus.Skipped` and **stops the tool from executing**, both for Attractor `tool` nodes and CodingAgent sub-tool calls.
5. `gemini-3.1-pro-preview` is the correct default Gemini model ID everywhere in the catalog.
6. An exit node identified by `id=exit` or `id=end` (without `shape=Msquare`) passes the terminal-node validation rule.
7. Legacy `.dot` files using `default_max_retry` and `preferred_next_label` continue to work.

## Architecture

No new abstractions. All changes are surgical within existing module boundaries. Key architectural decisions:

- **`Node.MaxRetries` → `Option<int>`**: The explicit `Some n` means "set by author"; `None` means "inherit from graph". Keep a backward-compat `MaxRetries` property (returns `defaultArg MaxRetriesOption 0`) for test sites that don't need inheritance semantics.
- **BareValue lexing**: Widen `isIdentChar` in the lexer to include `':'` and `'-'`. No new `AttrValue` DU case needed — unquoted identifiers continue to flow through `AttrValue.String`.
- **Pre-hook returns `Result<unit, string>`**: Converts the current "log and continue" side effect into an explicit gate. CodingAgent's `ToolCallHook` API gets the same treatment.
- **Parallel branches**: Branches write only to their own cloned context; the parent context is updated from results after all branches complete, deterministically.

## Implementation

### Phase 1: Parser, Types, and Conditions

#### Task 1.1 — Widen bare-value lexer (`DotParser.fs`)

**File:** `src/Attractor/DotParser.fs:33`

- Change `isIdentChar` to: `Char.IsLetterOrDigit(c) || c = '_' || c = '.' || c = ':' || c = '-'`
- No change needed downstream — `Token.Identifier id` → `AttrValue.String id` path already handles this
- Add parser tests for: `fidelity=summary:high`, `llm_model=gemini-3.1-pro-preview`, conditions with `-` in bare values

#### Task 1.2 — `default_max_retries` rename + legacy alias + option inheritance (`Types.fs`)

**File:** `src/Attractor/Types.fs`

- `Node.MaxRetries` → add `member this.MaxRetriesOption : int option` that checks attribute map presence; keep `member this.MaxRetries : int` as `defaultArg this.MaxRetriesOption 0`
- `Graph`: replace `DefaultMaxRetry` with:
  ```fsharp
  member this.DefaultMaxRetriesOption =
      this.GraphAttributes
      |> Map.tryFind "default_max_retries"
      |> Option.orElseWith (fun () -> Map.tryFind "default_max_retry" this.GraphAttributes)
      |> Option.bind (fun v -> v.AsInt())
  member this.DefaultMaxRetry = defaultArg this.DefaultMaxRetriesOption 0
  ```
- Update deep-copy comment on `Context.Clone()` to state: "F# strings are immutable; copy is by value. Artifact store handle is intentionally shared."

#### Task 1.3 — `parseLiteral` for condition quote-stripping (`Conditions.fs`)

**File:** `src/Attractor/Conditions.fs:29-49`

- Add private `parseLiteral : string -> string` that trims whitespace, strips one surrounding `"..."` pair if present
- Apply to both `=` and `!=` comparison branches in `evaluateClause`
- Add tests for: `outcome="success"`, `preferred_label="Fix"`, `context.foo="bar-baz"`, unquoted equivalents

**Definition of Done — Phase 1:**
- [ ] `fidelity=summary:high` parses without error
- [ ] `llm_model=gemini-3.1-pro-preview` parses without error
- [ ] `outcome="success"` condition evaluates true when outcome is success
- [ ] `Node.MaxRetriesOption` returns `None` when attribute absent, `Some n` when present
- [ ] `DefaultMaxRetriesOption` prefers `default_max_retries`; falls back to `default_max_retry`
- [ ] `dotnet build` clean

### Phase 2: Engine Semantics

#### Task 2.1 — Retry inheritance (`Engine.fs:39-45`)

**File:** `src/Attractor/Engine.fs`

Replace `RetryPolicy.FromNode` logic:
```fsharp
static member FromNode(node: Node, graph: Graph) =
    let maxRetries =
        match node.MaxRetriesOption with
        | Some n -> n
        | None -> graph.DefaultMaxRetry  // DefaultMaxRetry already returns 0 when absent
    { MaxAttempts = maxRetries + 1
      Backoff = BackoffConfig.Default }
```

Add tests:
- Graph default applies when node attr absent
- Node `max_retries=0` overrides graph `default_max_retries=5`
- Legacy `default_max_retry` works
- `default_max_retries` preferred when both present in graph attrs

#### Task 2.2 — Edge selection: restrict steps 2 & 3 to unconditional edges (`Engine.fs:79-122`)

**File:** `src/Attractor/Engine.fs`

After condition-match phase, compute once:
```fsharp
let unconditional = edges |> List.filter (fun e -> e.Condition = "")
```

- Step 2 (preferred label): search `unconditional` only, not `edges`
- Step 3 (suggested next IDs): search `unconditional` only, not `edges`
- Step 4 (weight fallback): already uses `unconditional` — no change
- Remove the old final `bestByWeightThenLexical(edges)` fallback — return `None` when `unconditional` is empty

Add tests:
- Preferred label does not match a conditional edge
- Suggested next id does not match a conditional edge
- Weighted fallback still works for unconditional edges

#### Task 2.3 — Default fidelity fallback: `compact` not `full` (`Engine.fs:152-175`)

**File:** `src/Attractor/Engine.fs`

In `FidelityResolution.resolve`, change the final fallback from `FidelityMode.Full` to `FidelityMode.Compact`. Update the test at `tests/Attractor.Tests/Tests.fs:2679-2683` (or nearby) so "nothing specified" asserts `Compact`.

**Definition of Done — Phase 2:**
- [ ] Retry inheritance: all 4 cases pass
- [ ] Preferred label / suggested next id: cannot traverse conditional edges
- [ ] Unset fidelity resolves to `Compact` in tests
- [ ] `dotnet build` clean; existing edge-selection tests still pass

### Phase 3: Handler Runtime Semantics

#### Task 3.1 — Pre-hook blocks tool execution (Attractor — `Handlers.fs`)

**File:** `src/Attractor/Handlers.fs`

- Change `runHook` to return `Result<string, string>` (Ok stdout / Error stderr + exit code)
- In `ToolHandler` (parallelogram shape): if pre-hook returns `Error`, produce `Outcome.Skipped` and do NOT start the tool process; log the hook failure reason
- Post-hook remains best-effort (log only)
- Add tests asserting failed pre-hook → no tool output artifact created, outcome = Skipped

#### Task 3.2 — Pre-hook blocks tool execution (CodingAgent — `Types.fs` + `Session.fs`)

**Files:** `src/CodingAgent/Types.fs:69`, `src/CodingAgent/Session.fs:699-723`

- Change `ToolCallHook` in `SessionConfig` from `unit`-returning to `Result<unit, string>`-returning (or equivalent discriminated union)
- In `Session.execute_single_tool`, if pre-hook returns `Error`, return a `ToolResult` with `is_error = true` and `content` = hook error message; do not call the registered executor
- Add tests asserting failed pre-hook → tool executor not called, error result returned

#### Task 3.3 — Parallel: add spec-compliance comment (`Handlers.fs:604-678`)

**File:** `src/Attractor/Handlers.fs`

- `error_policy`, `k_of_n`, and `quorum` attributes do not exist in the current handler — already spec-compliant. Add a comment block noting these are explicitly unsupported per spec §4.8.
- Verify that branches write only to their own cloned context during execution; results are stored in parent context after all branches complete. If any concurrent parent-context mutation is found, fix it.
- Tighten existing conformance tests under `conformance/05-parallel/03-branch-failure/` and `04-branch-timeout/` if they exist.

#### Task 3.4 — Fix `preferred_next_label` → `preferred_label` write path (`Handlers.fs:204`)

- **Bug:** `Handlers.fs:204` in `writeStatus` serializes `preferred_next_label = outcome.PreferredLabel`. Change to `preferred_label = outcome.PreferredLabel`.
- Keep backward-compat read at `Engine.fs:342` unchanged — it reads both `preferred_next_label` then `preferred_label` for old checkpoint compat.
- Add artifact-level test: write a status.json via `writeStatus`, parse it back, assert the JSON key is `preferred_label` not `preferred_next_label`.

**Definition of Done — Phase 3:**
- [ ] Pre-hook failure: Attractor tool node produces `Skipped`, tool process not started
- [ ] Pre-hook failure: CodingAgent tool call returns error, executor not called
- [ ] Parallel branches don't mutate shared parent context concurrently
- [ ] `error_policy`, `k_of_n`, `quorum` attributes silently ignored (no parse error)
- [ ] `preferred_label` field name confirmed in writes; backward compat read confirmed
- [ ] `dotnet build` clean; `dotnet test` passes

### Phase 4: Validation, Model Catalog & Spec Regression

#### Task 4.1 — terminal_node validation uses `FindExitNode()` (`Validation.fs:74-92`)

**File:** `src/Attractor/Validation.fs`

Update `terminalNodeRule` to consult `graph.FindExitNode()` (which already handles `shape=Msquare`, `id=exit`, `id=end`) for the zero-terminal-nodes check. The exact-one enforcement stays. Add test for exit node named `exit` without `shape=Msquare`.

#### Task 4.2 — Gemini model catalog update (`ModelCatalog.fs`)

**File:** `src/UnifiedLlm/ModelCatalog.fs`

- Rename `gemini-3-pro-preview` → `gemini-3.1-pro-preview` (model ID, display name, `latestByProvider["gemini"]`)
- Update any recommendation comment to reflect 3.1 Pro Preview as top Gemini model

#### Task 4.3 — Spec regression tests

- Verify `conformance/03-execution/18-goal-gate-partial-success/` passes (goal_gate + PartialSuccess)
- Add focused unit test for `id=exit` terminal node passing validation
- Existing conformance suite must pass with no new skips

**Definition of Done — Phase 4:**
- [ ] `id=exit` graph passes validation; `id=Exit` still fails (case-sensitive, by design)
- [ ] `gemini-3.1-pro-preview` is returned by `latestByProvider["gemini"]`
- [ ] All conformance tests pass including `18-goal-gate-partial-success`

## Files Summary

| File | Lines | Change |
|------|-------|--------|
| `src/Attractor/DotParser.fs` | 33 | Widen `isIdentChar`: add `':'` and `'-'` |
| `src/Attractor/Types.fs` | 212-216, 389-393, 557-567 | `MaxRetriesOption`, `DefaultMaxRetriesOption`, legacy aliases, deep-copy comment |
| `src/Attractor/Engine.fs` | 39-45, 79-122, 152-175 | Retry inheritance, edge-select steps 2/3 unconditional, fidelity default → compact |
| `src/Attractor/Conditions.fs` | 29-49 | `parseLiteral` strips surrounding quotes |
| `src/Attractor/Handlers.fs` | 172-210 | Pre-hook → `Result<bool>`, skip tool on failure; `writeStatus` field rename `preferred_next_label` → `preferred_label` |
| `src/Attractor/Handlers.fs` | 604-678 | Add comment: `error_policy`/`k_of_n`/`quorum` unsupported per spec §4.8 (no code change) |
| `src/Attractor/Validation.fs` | 74-92 | `terminalNodeRule` uses `FindExitNode()` |
| `src/CodingAgent/Types.fs` | ~69 | `ToolCallHook` returns `Result<unit, string>` |
| `src/CodingAgent/Session.fs` | ~699-723 | Gate tool execution on pre-hook result |
| `src/UnifiedLlm/ModelCatalog.fs` | ~75-94 | `gemini-3.1-pro-preview` |
| `tests/Attractor.Tests/Tests.fs` | various | Parser, condition, retry, edge-select, fidelity, hook, validation, parallel tests |
| `tests/CodingAgent.Tests/` | new | Pre-hook skip test for coding-agent tool calls |

**Verify-only (no production code):**

| File | Item | Evidence |
|------|------|---------|
| `src/Attractor/Transforms.fs:46-54` | Lifecycle order already correct | `preparePipeline`: parse → transform → validate |
| `src/Attractor/Engine.fs:135` | goal_gate PartialSuccess already correct | `\| StageStatus.Success \| StageStatus.PartialSuccess -> None` |
| `src/Attractor/Engine.fs:342` | backward compat read already correct | reads `preferred_next_label` then `preferred_label` |
| `src/Attractor/Handlers.fs:604-678` | Parallel deprecated attrs already absent | add comment only |

## Definition of Done

- [ ] **Build:** `dotnet build` — zero errors, zero warnings
- [ ] **Tests:** `dotnet test` — all existing tests pass + new tests added per phase
- [ ] **Lexer:** bare values with `:` and `-` parse without quotes
- [ ] **Retry inheritance:** `Node.MaxRetriesOption` / `Graph.DefaultMaxRetriesOption` + `DefaultMaxRetry` legacy alias; `RetryPolicy.FromNode` uses option-chain, no sentinels
- [ ] **Edge selection:** steps 2 & 3 search unconditional edges only; no all-edges fallback
- [ ] **Fidelity:** unset defaults to `compact` in both runtime and tests
- [ ] **Pre-hook Attractor:** `StageStatus.Skipped`, tool process not started
- [ ] **Pre-hook CodingAgent:** hook returns `Result`, executor skipped on `Error`
- [ ] **Parallel:** `error_policy`/`k_of_n`/`quorum` already absent; comment added; no concurrent parent-context mutation confirmed
- [ ] **Conditions:** quoted and unquoted literals evaluate identically
- [ ] **Validation:** `id=exit`/`id=end` accepted by terminal-node rule
- [ ] **Gemini model:** `gemini-3.1-pro-preview` throughout
- [ ] **Backward compat:** `default_max_retry`, `preferred_next_label` still work

## Risks

| Risk | Mitigation |
|------|-----------|
| `Option<int>` type change on `Node.MaxRetries` breaks test call sites | Add compat wrapper `MaxRetries: int = defaultArg MaxRetriesOption 0`; fix test sites that care about inheritance |
| Fidelity default change (`full` → `compact`) breaks tests expecting full context | Update those tests; full context is still available explicitly |
| CodingAgent pre-hook API change widens the blast radius | Narrow the change: only gate tool execution, no other session state changes |
| Parallel determinism change exposes tests relying on race timing | Tighten conformance tests first; merge produces same keys in stable order |

## Security

- Pre-hook enforcement is a net security improvement: policy scripts can actually stop a tool call
- Widened lexer accepts more bare-value syntax but does not change execution behavior; shell execution still only occurs through explicit tool handlers
- No new serialization/deserialization surface beyond the `preferred_label` field rename (backward compat read maintained)

## Dependencies

- No new external packages
- CodingAgent change depends on completing the Attractor pre-hook work first (establish the pattern)
- Conformance assets `conformance/03-execution/18-goal-gate-partial-success/`, `conformance/05-parallel/03-branch-failure/`, `04-branch-timeout/` must exist (verify before Phase 3)

## Open Questions

1. **Parallel `PartialSuccess` in aggregate:** Is a branch producing `PartialSuccess` counted as success or failure in `wait_all` / `first_success` join? (Deferred — keep existing behavior)
2. **Invalid fidelity strings:** Should an unrecognized explicit fidelity string fall back to `compact`, or warn + degrade? (Deferred — keep current behavior)
3. **Case-insensitive exit node ID:** `id=Exit` or `id=END` — intentionally unsupported? (Deferred — keep exact-match `exit`/`end` per current `FindExitNode()`)
