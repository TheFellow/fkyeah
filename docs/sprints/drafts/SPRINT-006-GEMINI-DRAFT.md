# Sprint 006: Spec Sync (fb57a55)

## Overview

This sprint focuses on synchronizing the `fkyeah` F# implementation with the latest attractor specification updates from the `strongdm/attractor` repository, specifically commit `fb57a55` ("Fix spec inconsistencies and refresh model guidance"). The update encompasses 15 behavioral changes, clarifying edge-selection semantics, retry-inheritance, lifecycle phases, and various attributes and model support. The codebase is currently stable (v0.3.1), and these changes are mostly targeted, low-to-medium risk surgical fixes, with the most significant structural change being the reordering of the lifecycle phases.

## Use Cases

- **Spec Conformance:** Ensure the engine strictly adheres to the latest Attractor specification, allowing developers to rely on predictable and standardized behavior.
- **Enhanced Parsing:** Support bare values with `:` and `-` in DOT files, and correctly strip surrounding quotes in condition evaluation, improving developer experience.
- **Robust Execution:** Prevent invalid edge fallbacks, properly inherit retry policies from the graph to individual nodes, and ensure pre-hook failures skip tool execution.

## Architecture

No new major abstractions are introduced. The architecture remains a three-layer stack (UnifiedLlm / CodingAgent / Attractor) utilizing idiomatic F# constructs (discriminated unions, `Option<'T>`, railway-oriented programming). 

The primary architectural shift is the reordering of the execution lifecycle: the `TRANSFORM` phase will now occur *before* the `VALIDATE` phase. This allows transformations (like stylesheet application or macro expansion) to finalize the graph structure before validation rules (like `exact-one` terminal node) are applied.

## Implementation

### Phase 1: Parsing, Validation & Lifecycle
**Tasks:**
1. **DotParser.fs:** Extend the lexer's `isIdentChar` to include `:` and `-`. Decide whether to introduce a new `AttrValue.BareValue` discriminated union case or widen the existing string parsing path (widening is preferred for backward compatibility).
2. **Conditions.fs:** Update `parseLiteral` to strip surrounding quotes, ensuring `outcome=success` and `outcome="success"` evaluate identically.
3. **Library.fs:** Reorder the execution lifecycle pipeline so that the `TRANSFORM` phase occurs before the `VALIDATE` phase.
4. **Validation.fs:** Update the `terminal_node` validation rule to utilize the existing `Graph.FindExitNode()` helper for consistency.

**DoD for Phase 1:**
- `isIdentChar` successfully parses identifiers with colons and hyphens.
- Condition literals handle quoted and unquoted strings equivalently.
- Unit tests pass for lifecycle reordering.
- Validation correctly identifies terminal nodes using the centralized helper.

### Phase 2: Engine & Handler Semantics
**Tasks:**
1. **Types.fs & Engine.fs:** Introduce `default_max_retries` with a default of 0. Ensure the legacy `default_max_retry` alias remains functional.
2. **Types.fs & Engine.fs:** Modify `Node.MaxRetries` to an `int option` (or utilize attribute map presence checks) to support inheritance. Graph-level `default_max_retries` must propagate to nodes that do not explicitly set `max_retries`.
3. **Engine.fs:** Update edge selection steps 2 & 3 to restrict matching to *unconditional* edges only.
4. **Engine.fs:** Remove the `bestByWeightThenLexical(edges)` fallback in edge selection; the engine should return `None` (no matched edge) instead.
5. **Engine.fs:** Implement the write path for `preferred_label` while maintaining the read path for backward compatibility with `preferred_next_label`.
6. **Engine.fs:** Set the default fidelity fallback to `compact` instead of `full` when unset.
7. **Handlers.fs (Parallel):** Refactor parallel handler logic to process results before the join policy. Remove deprecated parallel attributes: `error_policy`, `k_of_n`, and `quorum`.
8. **Handlers.fs (Hooks):** Ensure that a failure in a pre-hook immediately skips the associated tool call execution, rather than just logging the failure.

**DoD for Phase 2:**
- Retry inheritance logic is covered by unit tests.
- Edge selection correctly fails (returns None) when no valid paths exist, removing the fallback.
- Pre-hook failures prevent tool execution, verified by new conformance tests.
- Parallel handlers correctly ignore deprecated attributes.

### Phase 3: Model Catalog & Documentation
**Tasks:**
1. **Types.fs:** Update comments regarding context deep-copying to clarify that strings in F# are inherently immutable.
2. **ModelCatalog.fs:** Rename `gemini-3-pro-preview` to `gemini-3.1-pro-preview` and update top-recommendation comments.
3. **Testing:** Verify `goal_gate` correctly accepts `PartialSuccess` (add a conformance test to ensure this existing feature does not regress).

**DoD for Phase 3:**
- Model catalog reflects the latest Gemini model names.
- Conformance test added for `PartialSuccess` in goal gates.

## Files Summary

| File | Planned Changes |
|------|-----------------|
| `src/Attractor/DotParser.fs` | Extend `isIdentChar` for `:` and `-`. |
| `src/Attractor/Library.fs` | Move TRANSFORM phase before VALIDATE phase. |
| `src/Attractor/Types.fs` | Add `default_max_retries` (with legacy alias), change `Node.MaxRetries` to `int option`, update comments on deep-copy. |
| `src/Attractor/Engine.fs` | Implement retry inheritance, restrict edge selection steps 2/3, remove lexical fallback, update `preferred_label` write path, change default fidelity to `compact`. |
| `src/Attractor/Validation.fs` | Use `FindExitNode()` for terminal node validation. |
| `src/Attractor/Handlers.fs` | Parallel handlers: process results before join, remove `error_policy`/`k_of_n`/`quorum`. Pre-hooks: skip tool execution on failure. |
| `src/Attractor/Conditions.fs` | Strip quotes in `parseLiteral`. |
| `src/UnifiedLlm/ModelCatalog.fs`| Rename to `gemini-3.1-pro-preview`. |
| `tests/*` | Add unit and conformance tests for retry inheritance, edge selection, pre-hook skips, condition quote-stripping, and lifecycle order. |

## Definition of Done

- All 15 spec items from the intent document are successfully implemented.
- The project compiles with zero warnings and zero errors (`dotnet build`).
- All 512+ existing tests pass, and new tests are added for the updated behaviors (`dotnet test`).
- No regressions occur on existing `.dot` example files.
- Backward compatibility is maintained for `default_max_retry` and reading `preferred_next_label`.

## Risks

- **Lifecycle Reordering:** Moving `TRANSFORM` before `VALIDATE` is a fundamental change to the execution pipeline. This may surface latent bugs where validation implicitly relied on untransformed graph state.
- **Type Change:** Changing `Node.MaxRetries` to `int option` is a breaking change to the internal API and will require updating test factories and instantiation across the codebase.

## Security

- No new security boundaries are crossed. Pre-hook skip failures actively improve security/safety by preventing execution when preconditions fail.

## Dependencies

- F# implementation aligns specifically with `attractor-spec.md` at commit `fb57a55`.
- Requires existing conformance test suite framework.

## Open Questions

1. **Max Retries Inheritance:** Should `Node.MaxRetries` become `int option` (explicit `None` = "not set") or should we rely on attribute map presence checking? *Recommendation: `int option` is more idiomatic in F# and provides stronger compile-time guarantees, despite the necessary refactoring in tests.*
2. **BareValue Parsing:** Should `BareValue` be a new `AttrValue` case or just widen the existing `Identifier -> String` path in the parser? *Recommendation: Widening the string path is simpler, maintains backward compatibility with existing AST processors, and fulfills the spec requirement without introducing new discriminated union cases.*
