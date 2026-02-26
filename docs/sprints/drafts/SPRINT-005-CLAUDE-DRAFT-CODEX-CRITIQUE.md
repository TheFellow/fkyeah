# Critique: SPRINT-005-CLAUDE-DRAFT (Codex)

## Overall Assessment

The draft is directionally correct: it stays “tests only”, maps work to the gap list, and sequences P0 → P1 → P2 sensibly. The main weaknesses are (a) an incorrect/insufficient conformance design for `--resume`, and (b) several conformance assertions that don’t actually validate the gap’s spec claim (they only assert “pipeline completes” or assert fields that may not exist in artifacts).

## Strengths

1. **Good prioritization**: P0 (A2/B1/C1) is the right focus, and the proposed test areas match the highest-impact QA gaps.
2. **Concrete file touchpoints**: it names specific test files and conformance directories, which aligns with our sprint style.
3. **Reasonable scope estimate**: “~30 unit, ~10 conformance” feels plausible for closing P0/P1 plus a slice of P2.

## Findings

### Critical

1. **A2 `--resume` conformance test does not actually test resume behavior**
   - Phase 1 describes: “run fully, then run again with `--resume <checkpoint>` and compare artifacts”.
   - The CLI contract is `--resume <logs-dir>` (not a checkpoint file), and resuming a *fully completed* run is not a realistic durability scenario.
   - To validate checkpoint durability, the test should simulate an interruption mid-run (spawn attractor, wait until checkpoint shows stage N complete, kill the process, then resume and assert the remainder completes without re-running completed stages).

2. **Several conformance tests assert completion, not the spec property**
   - **A3 Fidelity**: “verify pipeline completes” does not validate fidelity projection. The test must assert downstream `prompt.md` (or equivalent artifact) is missing the full upstream context and shows the projected/truncated form.
   - **A1 Stylesheet**: asserting “check `status.json` reflects stylesheet properties” is likely incorrect unless we’ve confirmed model selection is serialized there. The test needs to assert the *chosen model* is recorded in a stable artifact (or add a conformance helper that reads the engine’s recorded model for the stage).

### High

3. **A6 Parallel failure assertions may be based on non-existent context keys**
   - The draft proposes asserting `parallel.branch.*.status` context vars. If these keys don’t exist (or are named differently), the conformance will be brittle or invalid.
   - This needs alignment with the actual artifact/manifest schema currently written by the engine.

4. **Definition of Done over-commits P2**
   - DoD states all listed P2 gaps must have passing tests. Sprint intent is “most P2” and explicitly allows the risk that new tests uncover production bugs.
   - For a “tests only” sprint, the DoD should allow `Skip` for P2 items that require production seams (abort/timeout/stream-with-tools), with a clear follow-up note.

### Medium

5. **Numbering gap (A7) is solved implicitly but not called out**
   - The plan creates `03-execution/16-*` and `03-execution/17-*`, which neatly fills A7, but it’s not explicitly tracked as an objective.
   - Calling this out helps ensure we don’t accidentally pick conflicting numbers or leave the gap unresolved.

6. **Checkpoint resume test placement is debatable**
   - Draft places A2 under `conformance/06-artifacts/06-checkpoint-resume/`. That’s defensible, but feedback classifies it under “execution gaps (A)”.
   - Either is fine, but the plan should justify the category choice (and ensure `run-all.sh` picks it up consistently).

## Recommended Adjustments

1. Redesign A2 conformance as: **run → poll checkpoint → kill → resume → compare to baseline** (and pass `--resume "$LOGS_DIR"` per CLI usage).
2. For A1/A3, change assertions to verify **the intended invariant**:
   - Stylesheet: assert chosen model per-node is recorded and matches selector precedence.
   - Fidelity: assert downstream prompt/context artifacts reflect the projection mode (not merely “success”).
3. For A6, confirm the artifact schema first, then assert on **documented** fields (or add minimal conformance helpers to parse the manifest/checkpoint consistently).
4. Relax P2 DoD to “tests added, green where possible; skipped only when prod seams are missing”, explicitly aligning with sprint intent.
5. Explicitly track A7 (conformance numbering gap) as a first-class checkbox item.

## Verdict

Solid baseline sprint plan with correct priorities, but it needs correction on the `--resume` conformance mechanics and tighter assertions for stylesheet/fidelity/parallel semantics to ensure the tests actually close the named gaps.
