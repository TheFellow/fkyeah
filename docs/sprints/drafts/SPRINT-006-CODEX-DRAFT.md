# Sprint 006: Spec Sync (Codex Draft)

**Status:** Draft  
**Specs:** `attractor-spec.md`, `coding-agent-loop-spec.md`, `unified-llm-spec.md`  
**Primary codebase:** `src/Attractor/`, `src/UnifiedLlm/`

## Overview

Sprint 006 syncs fkyeah against attractor spec commit `fb57a55` by closing the remaining implementation deltas without destabilizing the existing parser, engine, or checkpoint formats.

The repo is already closer to the new spec than the intent suggests:

- TRANSFORM already runs before VALIDATE in `src/Attractor/Transforms.fs:45-54`; `src/Attractor/Library.fs:18-25` is only a thin wrapper.
- `goal_gate` already accepts `PartialSuccess` in `src/Attractor/Engine.fs:128-140`, and conformance already exists at `conformance/03-execution/18-goal-gate-partial-success/`.
- `Graph.FindExitNode()` already recognizes `shape=Msquare` and `id=exit/end` in `src/Attractor/Types.fs:428-435`.
- Backward-compatible read support for `preferred_next_label` already exists in `src/Attractor/Engine.fs:340-343`.

The real work is narrower and more surgical:

1. Finish the attribute rename and inheritance semantics for retries.
2. Restrict edge-selection steps 2 and 3 to unconditional edges.
3. Change the runtime default fidelity from `full` to `compact`.
4. Fix status-file writing, quoted-literal conditions, and bare-value lexing.
5. Make parallel fan-out deterministic by removing concurrent mutation of parent context.
6. Enforce pre-hook failure as a real skip/block, not a warning-only side effect.

## Use Cases

1. A pipeline with `graph [default_max_retries=5]` and no node override retries five times, while `max_retries=0` on a node still means "do not retry".
2. A node that returns `preferred_label="Fix"` does not accidentally traverse a conditional edge whose label also happens to be `Fix`.
3. A status file written by a tool or coding-agent stage serializes `preferred_label`, while older artifacts with `preferred_next_label` still resume correctly.
4. A condition such as `outcome="success"` or `fidelity=summary:high` parses and evaluates the same as the unquoted form.
5. A parallel node does not race by applying branch context updates to the shared parent context from multiple async branches.
6. A failed pre-hook can prevent a tool invocation instead of running the tool and merely printing a warning.

## Architecture

This sprint should stay additive and idiomatic in F#:

- Keep `Node` and `Graph` record shapes unchanged. Express "attribute present vs absent" with additive members returning `option`, not by introducing mutable sentinel state.
- Do not add a new `AttrValue.BareValue` DU case unless parsing cannot be represented as `AttrValue.String`. The current codebase already treats unquoted attribute values as strings; widening the lexer is the smallest correct change.
- Prefer `Result`-returning helpers for hook execution instead of "log and continue" side effects. That keeps tool-skip policy explicit and testable.
- Preserve compatibility by keeping legacy accessors (`DefaultMaxRetry`, `MaxRetries`) as wrappers over new option-based members, rather than deleting or renaming members used by tests.

Architecturally, the important boundaries are:

- Parser and condition grammar in `DotParser.fs` and `Conditions.fs`.
- Attribute presence and fallback semantics in `Types.fs`.
- Traversal, retry, edge selection, checkpoint/status compatibility, and fidelity in `Engine.fs`.
- Runtime side effects in `Handlers.fs`.
- Validation only for the exit-node helper alignment in `Validation.fs`.
- Model default metadata in `ModelCatalog.fs`.

One item is cross-layer if the spec requires full coverage for coding-agent tool calls: `CodingAgent.SessionConfig.ToolCallHook` is `unit`-returning in `src/CodingAgent/Types.fs:69`, and `src/CodingAgent/Session.fs:705-723` cannot currently block execution after a failed pre-hook. If Sprint 006 scope includes coding-agent sub-tools, that API has to change as a dependency.

## Implementation

### Phase 0: Baseline Verification and Scope Correction

#### Task 0.1: Close items already implemented in this workspace

- Verify `src/Attractor/Transforms.fs:45-54` is the actual lifecycle implementation and keep `src/Attractor/Library.fs:18-25` unchanged unless a doc comment is added for clarity.
- Verify `src/Attractor/Engine.fs:128-140` plus `conformance/03-execution/18-goal-gate-partial-success/` fully close the `goal_gate + partial_success` item.
- Verify `src/Attractor/Engine.fs:340-343` remains the only backward-compatible read path for `preferred_next_label`.
- Reconcile the intent with the current tree so the sprint only carries forward the real remaining code changes.

**Definition of Done:**

- The sprint task list is reduced to actual remaining deltas.
- No "fix" phase is scheduled for lifecycle order or goal-gate partial success unless a regression is found.

### Phase 1: Parser and Attribute Presence Semantics

#### Task 1.1: Extend bare-value lexing without widening the DU surface

- `src/Attractor/DotParser.fs:33`: change `isIdentChar` to accept `':'` and `'-'` in addition to the current `'.'`.
- `src/Attractor/DotParser.fs:240-242`: keep `Token.Identifier id -> AttrValue.String id`; do not add a new `AttrValue` case unless a later test proves the current representation is insufficient.
- Add parser coverage adjacent to `tests/Attractor.Tests/Tests.fs:143-156` and `tests/Attractor.Tests/Tests.fs:214-226` for:
  - `fidelity=summary:high`
  - `llm_model=gemini-3.1-pro-preview`
  - a condition-bearing bare string that includes `-`

#### Task 1.2: Model retry inheritance with `option`, not sentinel values

- `src/Attractor/Types.fs:212-216`: split node retry access into two additive members:
  - `member this.MaxRetriesOption : int option`
  - `member this.MaxRetries : int`
- `src/Attractor/Types.fs:389-393`: replace the current sentinel-based graph member with:
  - `member this.DefaultMaxRetriesOption : int option`
  - read `default_max_retries` first
  - fall back to legacy `default_max_retry`
  - keep `member this.DefaultMaxRetry : int` as a compatibility wrapper returning `0` when absent
- `src/Attractor/Types.fs:557-567`: update the clone/deep-copy comments to state that strings are immutable, copied by value semantics, and the artifact store handle is intentionally shared.

#### Task 1.3: Strip quotes in condition literals

- `src/Attractor/Conditions.fs:29-49`: extract a private helper such as `parseLiteral : string -> string` that trims whitespace and removes one matching pair of surrounding quotes before equality comparison.
- Use the helper for both `=` and `!=` branches.
- Add tests beside `tests/Attractor.Tests/Tests.fs:1441-1497` for:
  - `outcome="success"`
  - `preferred_label="Fix"`
  - `context.foo="bar-baz"`

**Definition of Done:**

- Unquoted and quoted literals evaluate identically where the spec says they should.
- Retry inheritance is expressible as presence/absence instead of `50` as an implicit sentinel.
- Parser changes do not require touching downstream code outside the existing `AttrValue.String` flow.

### Phase 2: Engine Semantics

#### Task 2.1: Rewrite retry inheritance to remove the sentinel branch

- `src/Attractor/Engine.fs:39-45`: replace the current `RetryPolicy.FromNode` logic with:
  - `node.MaxRetriesOption` wins when `Some`
  - otherwise `graph.DefaultMaxRetriesOption`
  - otherwise `0`
- Preserve the external meaning of `MaxAttempts = retries + 1`.
- Add/adjust tests around `tests/Attractor.Tests/Tests.fs:3098-3126` and the later retry tests so all of these cases are explicit:
  - graph default applies when node attr is absent
  - node `max_retries=0` overrides graph default
  - legacy `default_max_retry` still works
  - plural `default_max_retries` is preferred when both are present

#### Task 2.2: Restrict edge-selection steps 2 and 3 to unconditional edges

- `src/Attractor/Engine.fs:79-122`: compute `let unconditionalEdges = edges |> List.filter (fun e -> e.Condition = "")` once after the condition-match phase.
- `src/Attractor/Engine.fs:93-99`: run preferred-label matching against `unconditionalEdges`, not all `edges`.
- `src/Attractor/Engine.fs:105-109`: run suggested-next-id matching against `unconditionalEdges`, not all `edges`.
- `src/Attractor/Engine.fs:118-122`: keep step 4 as weighted selection among unconditional edges only; if none exist, return `None`.
- Extend tests near `tests/Attractor.Tests/Tests.fs:3379-3640` for:
  - preferred label must not match a conditional edge
  - suggested next id must not match a conditional edge
  - weighted unconditional fallback still works

#### Task 2.3: Change the default fidelity fallback from `full` to `compact`

- `src/Attractor/Engine.fs:152-175`: change node-level invalid fallback, graph-level invalid fallback, and the final default from `FidelityMode.Full` to `FidelityMode.Compact`.
- Update the assertion at `tests/Attractor.Tests/Tests.fs:2679-2683` so "nothing specified" resolves to `Compact`, not `Full`.
- Keep precedence order unchanged: edge > node > graph > default.

**Definition of Done:**

- Retry behavior no longer depends on magic numbers.
- Conditional edges cannot be selected by preferred label or suggested id.
- The spec default fidelity is reflected in both runtime behavior and tests.

### Phase 3: Handler Runtime Semantics

#### Task 3.1: Write `preferred_label` in status artifacts, keep old read compatibility

- `src/Attractor/Handlers.fs:202-210`: rename the serialized JSON field from `preferred_next_label` to `preferred_label`.
- Do not change `src/Attractor/Engine.fs:340-343`; it must continue reading old and new field names.
- Add an artifact-level test proving newly written `status.json` files use `preferred_label`.

#### Task 3.2: Make hook execution policy explicit and block on failed pre-hooks

- `src/Attractor/Handlers.fs:172-200`: change `runHook` from `unit` to `Result<unit, string>` (or `Result<string, string>` if stdout is useful later).
- `src/Attractor/Handlers.fs:723-731`: in `ToolHandler`, if the pre-hook returns `Error`, do not start the process; return an `Outcome` with `Status = StageStatus.Skipped` or `Fail` based on the sprint decision.
- `src/Attractor/Handlers.fs:781-818`: keep post-hooks best-effort after actual tool execution and timeout, but treat them separately from pre-hook gating.
- Add tests beside the existing hook coverage around `tests/Attractor.Tests/Tests.fs:219-226` and the tool-handler sections to assert that a failing pre-hook prevents creation of tool output artifacts.

#### Task 3.3: Remove parallel fan-out races and merge results deterministically

- `src/Attractor/Handlers.fs:611-637`: stop mutating parent `context` inside each async branch.
- Return the full branch `Outcome` from each branch task, not just `(branchId, status, branchContext)`.
- `src/Attractor/Handlers.fs:640-677`: after `Async.Parallel`, fold results in the original branch order and build one deterministic `ContextUpdates` map that contains:
  - branch status keys
  - aggregate success/fail counts
  - `parallel.executed_nodes`
  - merged branch context updates, if the current behavior is intentionally preserved
- Preserve fan-in target discovery after results are collected.
- Add regression coverage near `tests/Attractor.Tests/Tests.fs:3261-3330` and the conformance under `conformance/05-parallel/03-branch-failure/` and `04-branch-timeout/` so branch updates no longer depend on race timing.

#### Task 3.4: Decide whether coding-agent sub-tools are in scope for the pre-hook skip rule

- If yes, add a dependency phase in:
  - `src/CodingAgent/Types.fs:69`
  - `src/CodingAgent/Session.fs:699-723`
- The hook must return a value that can short-circuit tool dispatch.
- If no, explicitly document Sprint 006 as covering Attractor `tool` nodes only.

**Definition of Done:**

- Newly written artifacts use the new field name.
- A failed pre-hook changes runtime behavior, not just stderr output.
- Parallel branches no longer write into shared parent state concurrently.

### Phase 4: Validation, Model Catalog, and Spec Regression Coverage

#### Task 4.1: Align terminal-node validation with `FindExitNode()`

- `src/Attractor/Validation.fs:74-92`: update `terminalNodeRule` so the "no terminal node" success/failure path consults `graph.FindExitNode()` rather than only scanning `shape=Msquare`.
- Keep exact-one enforcement. The helper should expand the accepted terminal identity, not weaken the count rule.
- Add a test near `tests/Attractor.Tests/Tests.fs:3195-3221` for a graph whose exit node is named `exit` or `end` without `shape=Msquare`.

#### Task 4.2: Refresh Gemini catalog defaults

- `src/UnifiedLlm/ModelCatalog.fs:75-80`: rename the Gemini entry to `gemini-3.1-pro-preview`.
- `src/UnifiedLlm/ModelCatalog.fs:89-94`: change `latestByProvider["gemini"]` to `gemini-3.1-pro-preview`.
- Update any nearby recommendation comment so the human-readable guidance matches the catalog entry.

#### Task 4.3: Reconcile tests and conformance with the current workspace

- Keep `conformance/03-execution/18-goal-gate-partial-success/` as verification-only unless it fails.
- Treat `conformance/05-parallel/03-branch-failure/` and `04-branch-timeout/` as existing assets to tighten, not create-from-scratch work.
- Add focused unit coverage in `tests/Attractor.Tests/Tests.fs` rather than introducing new broad harness layers.

**Definition of Done:**

- Validation accepts helper-identified exit nodes while still rejecting multiple terminal candidates.
- Gemini defaults in the catalog match the spec and the existing conformance directory names.
- The sprint ends with production and test deltas aligned to the actual state of the repo.

## Files Summary

| File | Current lines | Planned change |
|------|---------------|----------------|
| `src/Attractor/DotParser.fs` | `33`, `240-242` | Widen bare identifier chars to include `:` and `-`; keep identifiers flowing through `AttrValue.String`. |
| `src/Attractor/Library.fs` | `18-25` | Verify-only. No semantic change required unless adding a clarifying comment that prepare delegates to the transform-first pipeline. |
| `src/Attractor/Transforms.fs` | `45-54` | Verify-only. This is the actual lifecycle implementation already doing parse -> transform -> validate. |
| `src/Attractor/Types.fs` | `212-216`, `389-393`, `557-567` | Add option-based retry accessors, preserve compat wrappers, remove sentinel semantics, update deep-copy comments. |
| `src/Attractor/Engine.fs` | `39-45`, `79-122`, `152-175`, `340-343` | Rewrite retry inheritance, restrict edge-selection steps 2/3 to unconditional edges, default fidelity to `compact`, preserve old/new preferred-label read compatibility. |
| `src/Attractor/Handlers.fs` | `172-210`, `611-677`, `723-818` | Make hooks return `Result`, rename written status field to `preferred_label`, block tool execution on failed pre-hook, remove parallel shared-context mutation. |
| `src/Attractor/Validation.fs` | `74-92` | Consult `FindExitNode()` for terminal-node detection while keeping exact-one semantics. |
| `src/Attractor/Conditions.fs` | `29-49` | Add literal parsing that strips surrounding quotes before `=` / `!=` comparisons. |
| `src/UnifiedLlm/ModelCatalog.fs` | `75-80`, `89-94` | Rename the Gemini Pro preview entry and latest-provider mapping to `gemini-3.1-pro-preview`. |
| `tests/Attractor.Tests/Tests.fs` | `143-156`, `1441-1497`, `2655-2683`, `3098-3126`, `3261-3640` | Add or adjust focused parser, condition, retry, fidelity, edge-selection, hook, validation, and parallel regression tests. |
| `conformance/03-execution/18-goal-gate-partial-success/` | existing | Verify existing coverage; no planned creation work. |
| `conformance/05-parallel/03-branch-failure/`, `04-branch-timeout/` | existing | Tighten assertions after the deterministic parallel-merge fix if needed. |

## Definition of Done

- All remaining spec deltas from `fb57a55` are either implemented or explicitly marked "already done" with evidence in code/tests.
- `default_max_retries` is supported, `default_max_retry` remains a legacy alias, and missing values default to zero retries.
- Preferred-label and suggested-next-id selection cannot traverse conditional edges.
- `preferred_label` is written in new `status.json` artifacts, and both old and new field names are readable.
- The default fidelity fallback is `compact`.
- Tool pre-hook failure is behaviorally enforced.
- Parallel fan-out no longer mutates shared parent context from concurrent branches.
- `dotnet build` is warning-free.
- `dotnet test` passes.

## Risks

- **Cross-layer hook scope:** Full spec compliance for coding-agent sub-tools may require changing `CodingAgent` APIs outside the files named in the intent.
- **Behavioral compatibility:** Changing the default fidelity to `compact` can surface hidden assumptions in tests or prompts that relied on full-context projection.
- **Parallel merge semantics:** Removing concurrent writes may reveal tests that accidentally depended on branch-completion order.
- **Validation ambiguity:** Using `FindExitNode()` in validation needs careful counting logic so `id=exit` support does not weaken the exact-one guarantee.

## Security

- Expanding bare-value lexing increases accepted unquoted syntax, but it does not change execution behavior by itself; shell execution still only occurs through explicit tool handlers.
- Enforcing pre-hook failure as a hard gate is a net security improvement because policy hooks can actually stop a tool invocation.
- Keeping backward-compatible status-file reads limited to known field names avoids broadening checkpoint/status deserialization surface area.
- Parallel merge changes should avoid shared mutable state across async branches; that reduces race-driven nondeterminism and makes audit trails easier to trust.

## Dependencies

- Existing transform ordering in `src/Attractor/Transforms.fs:45-54` should be treated as the source of truth; no separate lifecycle refactor is needed.
- Existing conformance assets under `conformance/03-execution/18-goal-gate-partial-success/` and `conformance/05-parallel/03-04-*` should be reused.
- If pre-hook skip must apply to coding-agent sub-tools, Sprint 006 depends on a coordinated API change in `src/CodingAgent/Types.fs` and `src/CodingAgent/Session.fs`.
- Verification remains `dotnet build` and `dotnet test`; no new harness dependency is required.

## Open Questions

1. Does the pre-hook skip requirement apply only to Attractor `tool` nodes, or also to coding-agent tool calls inside `Session`?
2. Should a failed pre-hook produce `StageStatus.Skipped` or `StageStatus.Fail`? `Skipped` matches the wording, but `Fail` may make goal-gate and routing behavior more explicit.
3. Should invalid explicit fidelity strings also fall back to `compact`, or should only the "unset" case change while invalid values continue to warn and degrade differently?
4. In parallel fan-out, should branch `PartialSuccess` count as success, failure, or remain distinct in aggregate accounting?
5. For terminal-node validation, is `id=Exit` / `id=END` intentionally unsupported, or should `FindExitNode()` be normalized case-insensitively at the same time?
