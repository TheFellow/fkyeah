# Sprint 005 Merge Notes

## Claude Draft Strengths
- Concrete phase-by-phase structure with per-phase effort percentages
- Explicit file paths for every new conformance test directory
- Per-gap suggested test descriptions (e.g., "A pipeline where model_stylesheet assigns a model...")
- Clean mapping from gap IDs to phases
- Included Security Considerations section

## Codex Draft Strengths
- "Sprint Principles" section establishing test-writing norms (spec-tied, no stubs, deterministic, minimal harness changes)
- Better A2 resume test design: spawn -> poll checkpoint -> kill -> resume (not "run fully then resume")
- Explicit Phase 4 (D) deferral of cross-cutting gaps as "design-only" — realistic scoping
- Correctly noted feedback.md counts 29 items not 28
- Grouped related P2 items (B3/B4/B5 together, B6/B7 together) for implementation efficiency

## Valid Critiques Accepted

1. **A2 resume test design (Critical)**: Claude's design ran the pipeline fully then resumed, which doesn't test durability. Accepted Codex's design: spawn in background, poll checkpoint, kill mid-run, resume. Uses `--resume "$LOGS_DIR"` (not checkpoint file).

2. **Weak conformance assertions (Critical)**: Claude's A1/A3 tests only asserted "pipeline completes". Accepted: A1 must assert the chosen model is recorded per-node; A3 must assert downstream prompt.md reflects truncated projection, not the full value.

3. **A6 parallel branch context keys (High)**: Claude assumed `parallel.branch.*.status` keys exist. Accepted: need to verify actual artifact schema before asserting on specific key names.

4. **P2 DoD over-commitment (High)**: Claude's DoD required all listed P2 gaps to have passing tests. Accepted: relaxed to "green preferred; Skip allowed when production seams are missing."

5. **A7 numbering gap not tracked (Medium)**: Claude filled the numbering gap implicitly by using 16/17. Accepted: now explicitly tracked as a checkbox item.

## Critiques Rejected (with reasoning)

None — all five critique points were valid and accepted.

## Interview Refinements Applied

1. **Scope expansion**: User said "implement if needed" — apply_patch executor may require production code to make tests meaningful. The sprint is no longer "pure QA only."
2. **All P2s**: User wants comprehensive coverage. All 14 P2 items are in scope, with Skip-annotation as the escape valve for items requiring production seams.

## Final Decisions

- **A2 placement**: `conformance/03-execution/16-checkpoint-resume/` (Codex's placement in execution, fills numbering gap)
- **A1 placement**: `conformance/03-execution/17-model-stylesheet/` (Codex's suggestion, fills second gap number)
- **Conformance numbering**: 16-checkpoint-resume, 17-model-stylesheet, 19-goal-expansion, 20-max-visits, 21-tool-hooks, 22-gate-question-types, 23-manager-loop (18 already taken by goal-gate-partial-success)
- **D-category**: Deferred to follow-up sprint per Codex. Include design notes in the sprint doc.
- **Sprint Principles**: Adopted from Codex draft as a section in the final document
- **apply_patch**: Allow minimal production code (executor implementation) if current executor is a stub
