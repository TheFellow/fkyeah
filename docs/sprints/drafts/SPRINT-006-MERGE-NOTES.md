# Sprint 006 Merge Notes

## Draft Sources

- **Gemini draft** — Clean 3-phase structure (Parsing/Validation/Lifecycle, Engine, Catalog). Good phasing but brief; missed the parallel race condition and CodingAgent pre-hook scope.
- **Codex draft** — Most detailed. Line-number-precise. Caught that TRANSFORM is already before VALIDATE, identified the parallel shared-context mutation race, and raised the CodingAgent API scope question. Slightly over-specified on Phase 0 "baseline verification".
- **Claude draft** — Completed after initial merge. 591 lines, 7-phase plan. Caught three corrections: (1) `Handlers.fs:204` actually writes `preferred_next_label` (not Engine.fs:258) — real bug fix; (2) parallel deprecated attrs already absent from codebase — comment only; (3) item 7 fallback removal already correct — Steps 4&5 already return None. Detailed test list of 21 tests incorporated.

## Codex Strengths Accepted

- **Item 2 already done:** `Transforms.preparePipeline` (line 46-54) already performs PARSE→TRANSFORM→VALIDATE. Confirmed via code reading. Item 2 removed from work list.
- **Item 5 already done:** `GoalGates.checkGoalGates` (Engine.fs:128-140) already accepts PartialSuccess. Conformance test at `conformance/03-execution/18-goal-gate-partial-success/` likely exists. Verify only.
- **Precise line numbers** for every task adopted as implementation guidance.
- **Parallel race condition:** branches writing to parent context concurrently is a real correctness issue. Added explicitly to Phase 3.
- **CodingAgent pre-hook scope:** API change needed in `CodingAgent/Types.fs` and `CodingAgent/Session.fs`. User confirmed both layers.
- **Preferred label write path:** Already using `preferred_label` in writes, backward compat already in reads. Verify + test only.

## Gemini Strengths Accepted

- Clean phase naming structure (Parsing/Validation/Lifecycle, Engine, Catalog) adopted.
- Concise DoD per phase incorporated.

## Interview Decisions Applied

- `Node.MaxRetries` → `Option<int>` (not attribute map check)
- BareValue → widen `isIdentChar` (not new DU case)
- TRANSFORM on resume → yes, already done via `preparePipeline`
- Pre-hook failure outcome → `StageStatus.Skipped`
- Pre-hook scope → both Attractor tool nodes AND CodingAgent.Session sub-tool calls

## Items Reduced to Verify-Only (No Production Code)

| # | Item | Evidence |
|---|------|---------|
| 2 | Lifecycle TRANSFORM before VALIDATE | Transforms.fs:46-54 + Engine.fs:952 |
| 5 | goal_gate accepts PartialSuccess | Engine.fs:135 |
| 7 | Fallback removal | After Steps 4&5 (unconditional by weight), None is already returned |
| 8 | Parallel deprecated attrs | `error_policy`/`k_of_n`/`quorum` don't exist in code — add comment |
| 9 | Context deep copy | F# strings are immutable; update comment on Clone() |

## Items Upgraded from Verify to Real Work (Claude correction)

| # | Item | Correction |
|---|------|-----------|
| 11 | preferred_label write path | `Handlers.fs:204` writes `preferred_next_label` — real bug fix needed |

## Open Questions Resolved

1. pre-hook outcome → Skipped
2. pre-hook scope → both Attractor tool nodes and CodingAgent sub-tools
3. BareValue → widen isIdentChar (user decision)
4. max_retries → Option<int> (user decision)
5. transforms on resume → already correct

## Remaining Open (deferred to implementation)

- Invalid explicit fidelity strings: keep current behavior (warn + degrade)
- Branch PartialSuccess in parallel aggregate accounting: keep existing behavior
- Case-insensitive exit node id matching (`Exit`, `END`): out of scope, keep current exact-match (`exit`, `end`)
