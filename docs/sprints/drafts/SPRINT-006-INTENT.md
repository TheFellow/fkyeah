# Sprint 006 Intent: Spec Sync — fb57a55 "Fix spec inconsistencies and refresh model guidance"

## Seed

Sync the fkyeah F# implementation against the latest attractor spec updates from
`strongdm/attractor` commit `fb57a55` ("Fix spec inconsistencies and refresh model
guidance"). The three spec files (`attractor-spec.md`, `coding-agent-loop-spec.md`,
`unified-llm-spec.md`) have already been copied to the repo root. This sprint
implements the 15 behavioral changes they describe.

## Context

- **Current project state:** fkyeah v0.3.1, Sprints 001–005 complete. Full three-layer
  stack (UnifiedLlm / CodingAgent / Attractor) is implemented, conformance-tested, and
  CI-green. The codebase is stable and well-tested (~512 tests).
- **Spec update scope:** The upstream spec tightened edge-selection semantics, changed
  retry-inheritance, added a TRANSFORM lifecycle phase, renamed two attributes, fixed
  parallel handler ordering, and made several model/grammar clarifications.
- **Some changes already done:** `Stylesheet.fs` already has the shape-selector
  specificity hierarchy (0→1→2→3). `GoalGates.checkGoalGates` already accepts
  `PartialSuccess`. `Validation.fs` already requires exactly one terminal node.
  `Graph.FindExitNode()` already handles `id matching exit/end`.
- **Risk level:** Low-to-medium. Most changes are small surgical fixes. The lifecycle
  reordering (TRANSFORM before VALIDATE) is the largest structural change.

## Recent Sprint Context

- **Sprint 005 (Test Coverage Hardening):** 19 new unit tests, 11 conformance suites,
  `apply_patch` E2E, checkpoint/resume conformance, prompt caching, streaming-with-tools.
- **Recent commits:** v0.3.1 bump; GPT-5.4 model support + conformance tests; CI
  workflow added; fix for non-retriable LLM errors + unmatched fail edges; stylesheet
  applied on resume.
- **No open spec divergences** beyond those introduced by fb57a55.

## Relevant Codebase Areas

| File | Items |
|------|-------|
| `src/Attractor/DotParser.fs` | BareValue grammar (`:` and `-` in bare identifiers) |
| `src/Attractor/Library.fs` | Lifecycle: move TRANSFORM before VALIDATE |
| `src/Attractor/Types.fs` | `default_max_retries` rename + legacy alias; `max_retries` option type; deep-copy comments; fidelity fallback |
| `src/Attractor/Engine.fs` | RetryPolicy inheritance; edge selection steps 2 & 3; fallback removal; preferred_label write path; fidelity default |
| `src/Attractor/Validation.fs` | terminal_node rule: use `FindExitNode()` |
| `src/Attractor/Handlers.fs` | Parallel: results before join policy, remove error_policy/k_of_n/quorum; pre-hook skip on failure |
| `src/Attractor/Conditions.fs` | `parseLiteral` strips surrounding quotes |
| `src/UnifiedLlm/ModelCatalog.fs` | `gemini-3-pro-preview` → `gemini-3.1-pro-preview` |

## Constraints

- F# idiomatic style: discriminated unions, option types, no null, railway-oriented error handling.
- No breaking changes to public API shapes that tests already cover — add new members, don't remove.
- Legacy alias `default_max_retry` must still work (backward compat).
- `preferred_next_label` must still be read from status.json (backward compat); write path uses `preferred_label`.
- All existing tests must continue to pass.
- `dotnet build` zero warnings, `dotnet test` zero failures.

## Success Criteria

1. All 15 spec items implemented (see detailed list below).
2. `dotnet build` → no errors, no warnings.
3. `dotnet test` → all existing tests pass + new conformance/unit tests for changed behavior.
4. No regressions on existing `.dot` example files.

## Spec Items (15)

| # | Item | File(s) | Status |
|---|------|---------|--------|
| 1 | BareValue: extend lexer `isIdentChar` to include `:` and `-`; add `AttrValue.BareValue` or widen `String`; add `BareLiteral` to Conditions | DotParser.fs, Conditions.fs | TODO |
| 2 | Lifecycle: TRANSFORM phase before VALIDATE in Library.fs | Library.fs | TODO |
| 3 | `default_max_retries` (rename + legacy alias + default 0) | Types.fs, Engine.fs | TODO |
| 4 | `max_retries` inherits from graph (option-type or attribute presence check) | Types.fs, Engine.fs | TODO |
| 5 | `goal_gate` accepts PARTIAL_SUCCESS — already done; add conformance test | Engine.fs, tests | VERIFY |
| 6 | Edge selection steps 2 & 3: restrict to unconditional edges only | Engine.fs | TODO |
| 7 | Remove fallback `bestByWeightThenLexical(edges)` — return None | Engine.fs | TODO |
| 8 | Parallel handler: results before join; remove error_policy, k_of_n, quorum | Handlers.fs | TODO |
| 9 | Context deep-copy: verify strings are immutable, update comments | Types.fs | TODO |
| 10 | Pre-hook failure skips tool call (not just logs) | Handlers.fs | TODO |
| 11 | `preferred_label` write path in Engine.fs; keep backward-compat read | Engine.fs | VERIFY |
| 12 | terminal_node rule uses FindExitNode() — already exact-one; update to use helper | Validation.fs | TODO |
| 13 | `parseLiteral`: strip quotes in Conditions.evaluateClause | Conditions.fs | TODO |
| 14 | Fidelity fallback: `compact` not `full` when unset | Engine.fs | TODO |
| 15 | ModelCatalog: `gemini-3.1-pro-preview`; update top-recommendation comment | ModelCatalog.fs | TODO |

## Verification Strategy

- **Spec reference:** `attractor-spec.md` §2.5–2.6 (attributes), §3.1 (lifecycle), §3.3 (edge selection), §3.4 (goal gates), §3.5 (retry policy), §4.8 (parallel), §5.4 (fidelity), §7 (validation), §10 (conditions).
- **Unit tests:** New tests in `tests/Attractor.Tests/` for edge selection, retry inheritance, pre-hook skip, conditions quote-stripping, lifecycle order.
- **Conformance tests:** Existing suites must pass; add new shell conformance for pre-hook skip behavior and BareValue parsing if feasible.
- **Edge cases:**
  - `default_max_retries=0` (default) means no retries unless node sets `max_retries`
  - Node with explicit `max_retries=0` overrides a graph-level `default_max_retries=5`
  - Condition `outcome=success` (no quotes) and `outcome="success"` (with quotes) both work
  - Preferred-label match does NOT follow a conditional edge
  - Pipeline with `error_policy` attr in parallel node — attribute silently ignored (no parse error)

## Uncertainty Assessment

- **Correctness uncertainty: Low** — changes are targeted spec clarifications with clear before/after semantics
- **Scope uncertainty: Low** — 15 items, all identified, bounded to ~8 files
- **Architecture uncertainty: Low** — no new abstractions; lifecycle reorder is the riskiest but Library.fs is the main entry point

## Open Questions

1. For `max_retries` inheritance: should `Node.MaxRetries` become `Option<int>` (explicit None = "not set") or should we check attribute map presence? Option type is cleanest but is a breaking type change on the Node record — need to assess test impact.
2. Should `BareValue` be a new `AttrValue` case or just widen the existing `Identifier → String` path in the parser? Widening is simpler and backward compatible.
