# Q1/Q2 Fan-Out Impact Evaluation

**Status:** Proposed — for review
**Authored:** 2026-04-26 (post-Sprint-016)
**Upstream reference:** swift-omnikit `7678090` (parallel fan-out + status-block default-to-success), `f164a80` (raw outcome + unconditional fan-out)
**Sprint 016 (already shipped):** ported the `rawOutcome` half of `f164a80` only.

## TL;DR

**Recommend Option B: port with explicit opt-in via an edge attribute (e.g. `[fanout=true]`).** Faithful porting (Option A) would silently change semantics for at least one existing conformance fixture and risks subtle interactions with `loop_restart` and checkpoint resume. The current `parallel`-shape handler already covers the common case. Opt-in costs ~250 LOC, leaves every existing graph unchanged, and unblocks future Swift ports that depend on multi-edge fan-out.

## Affected Graphs (Q1)

fkyeah's graphs use three fan-out patterns today:

| Pattern | Example | Today's behaviour | Behaviour under Option A |
|---|---|---|---|
| Explicit `parallel` shape (most common) | `kitchensink_parity.dot` (CritiquesParallel → 3 critiques), `megaplan.dot` (OrientParallel → 4 branches) | Concurrent + isolated context per branch via `Handlers.ParallelHandler` | Unchanged |
| Multiple edges, same condition | `conformance/02-validation/33-all-new-rules-integration/pipeline.dot`: `CheckFixResult → CommitAndSummarize [condition="outcome=success"]` AND `[condition="outcome=fail"]` | First match wins via `bestByWeightThenLexical` | Both branches execute (semantic change — would break the conformance assertion that `CommitAndSummarize` ran exactly once) |
| Multiple unconditional edges | None found in repo | (n/a) | Would fan out |

Bottom line: Option A breaks at least one existing conformance fixture. Option B leaves it untouched.

## Test/Conformance Impact (Q2)

- `tests/Attractor.Tests/` contains ~15 `selectEdge` tests pinning `bestByWeightThenLexical` for multi-condition-match cases. Option A: rewrite. Option B/C: zero impact.
- `conformance/02-validation/33-all-new-rules-integration` asserts single-execution of the duplicated-target node. Option A: failing. Option B/C: passing.
- New tests added under either A or B: ~8–10 new fixtures covering the fan-out happy path, fan-in heuristic, and checkpoint resume mid-fan-out.

## Swift Fan-In Heuristic Robustness (Q3)

After all branches complete, the Swift impl picks the fan-in node as **the first unconditional successor of any branch target**. Failure modes for fkyeah:

1. **Non-uniform branch lengths** — Plan1 → Review (1 hop) vs Plan2 → ReviewGate → Review (2 hops): the heuristic picks `Review` from Plan1 and may bypass `ReviewGate`. fkyeah's existing graphs use explicit Join nodes, so low practical risk — but worth documenting as an authoring rule.
2. **Branches that themselves fan out** — diamonds-of-diamonds. Heuristic picks the first branch's first unconditional successor; correct for symmetric diamonds, lossy otherwise.
3. **Branches with only conditional successors** — heuristic returns nil; resume hangs. Swift code skips already-completed targets via `state.completedNodes.contains(edge.to)` but does not handle a missing fan-in.
4. **Checkpoint mid-fan-out** — Swift relies on `completedNodes`; if the checkpoint records `currentNodeId` as the first completed branch target, resume could skip remaining branches. Subtle, silent failure mode.

Verdict: works for the diamond-with-Join shape that fkyeah authors actually use; not bulletproof for arbitrary DAGs. The heuristic should be paired with a "fan-out diamond rule" in `attractor schema` documentation.

## fkyeah-Specific Concerns (Q4)

1. **Overlap with `parallel` shape handler** — different semantics:
   - Today's `parallel` shape (Handlers.fs:923): concurrent execution, isolated context per branch, results merged with prefix `parallel.<nodeId>.<branchId>.<key>`.
   - Swift fan-out: sequential execution, each branch sees cumulative context updates from prior branches.
   - These are not interchangeable. Authors who want true concurrency must keep using the `parallel` shape.

2. **`loop_restart` interaction** — a `loop_restart` edge breaks out of the main loop and restarts from a target. Today: only one matching edge fires, so loop semantics are deterministic. With fan-out: `CheckTaskResult → Retry [condition=fail, loop_restart]` AND `CheckTaskResult → LogResult [condition=fail]` — does `LogResult` run before the restart? After? In parallel? The Swift impl iterates `for edge in allEdges`, so all targets execute sequentially before considering the restart, but a `loop_restart` mid-iteration would terminate the loop early. Behaviour needs to be specified.

3. **`goal_gate` / `scope_gate`** — checked after node execution, not during edge selection. Unaffected.

4. **Checkpoint resume** — `currentNodeId` is a single string today. Mid-fan-out resume requires either:
   - A `pending_fanout_targets: string list` field in the checkpoint schema, or
   - Re-derivation: on resume, look at the previous node's outgoing edges, evaluate which still match, and skip those already in `completedNodes`.
   Either approach is ~100 LOC of careful code, with silent-failure risk if wrong.

## Authoring Ergonomics (Q5)

**Today (explicit `parallel`):**
```
  Coordinator -> Review     [label="Parallel"]
  Review [shape=parallel]   // explicit fan-out node
  Review -> Plan1
  Review -> Plan2
  Plan1 -> Join
  Plan2 -> Join
```

**Swift form (implicit fan-out):**
```
  Coordinator -> Plan1
  Coordinator -> Plan2      // two edges from same source
  Plan1 -> Review
  Plan2 -> Review
```

| Trait | `parallel` shape | Swift implicit |
|---|---|---|
| Verbosity | More (extra node) | Less |
| Discoverability | High (`shape=parallel` is grep-able) | Low (multi-edge is invisible structure) |
| Footgun risk | Low | High — a typo'd condition silently fans out |
| Concurrency | Yes (concurrent by design) | No (sequential) — confuses authors |
| Schema-self-documenting | Yes (`attractor schema` lists shape) | No (would require new prose) |

The `parallel` form is more verbose but harder to misuse. Implicit fan-out's biggest risk is silent behaviour change from minor edits.

## Options (Q6)

### A. Port faithfully

**Scope:** ~400 LOC.
- `EdgeSelection.selectEdge` returns `Edge list`; rewire all 4 call sites.
- Sequential fan-out branch loop in `Engine.executeLoop`.
- Checkpoint schema: add `pending_fanout_targets`, ~100 LOC.
- Test rewrite: ~15 existing tests changed; ~10 new fan-out tests; conformance fixture `33-all-new-rules-integration` rewritten.

**Risk:** High.
- Breaks at least one conformance fixture immediately.
- `loop_restart` interaction needs new specification.
- Checkpoint mid-fan-out is silent-failure territory.
- `parallel` shape now competes with implicit fan-out — author confusion.

**Pick if:** full upstream parity is more important than backward compatibility, and you're willing to gate every multi-edge graph through migration.

### B. Opt-in via edge attribute

**Scope:** ~250 LOC.
- Add `Edge.Fanout: bool` (parsed from `fanout=true` attribute).
- `selectEdge` returns single best winner unless any matching edge has `Fanout=true`, in which case return all matches.
- Sequential fan-out branch loop (same as A).
- Checkpoint schema: same `pending_fanout_targets` (~100 LOC).
- Tests: existing tests unchanged; ~8 new opt-in fan-out fixtures.

**Risk:** Low–medium.
- Existing graphs unaffected (zero conformance regression).
- Three edge-selection modes now (single-best, label-match, fan-out) — more documentation surface.
- Checkpoint mid-fan-out still has the silent-failure risk; isolatable to opt-in graphs.
- `loop_restart` + `fanout` is a documented unsupported combination.

**Pick if:** you want the option available for new graphs without forcing migration on existing ones.

### C. Don't port

**Scope:** ~0 engine LOC. ~500 words of decision doc + ~100 words in `attractor schema` / conformance README explaining the difference vs upstream.

**Risk:** Lowest.
- Future Swift feature ports may depend on multi-edge fan-out; deferred decision becomes deferred work.

**Pick if:** the `parallel` shape covers your use cases and you want to minimise complexity and test churn.

## Recommendation

**Option B — opt-in via `[fanout=true]` edge attribute.**

It threads three needs:
1. **Backward compatibility** — every current conformance suite and production graph keeps working.
2. **Forward optionality** — new graphs can adopt implicit fan-out, unblocking future upstream ports without retroactive migration.
3. **Tractable scope** — ~250 LOC fits one focused sprint; the checkpoint-resume work is the same cost under A or B and benefits both.

Pair this with:
- A documented "unsupported combinations" list in `attractor schema`: `fanout` × `loop_restart`, `fanout` × `goal_gate`, `fanout` × `scope_gate`.
- A short README note in `conformance/05-parallel/` (or wherever the new fixtures land) explaining the difference between `parallel` shape (concurrent + isolated) and `fanout` edges (sequential + cumulative context).
- A future Sprint 017 (or similar) for the checkpoint-safety audit so mid-fan-out resume is exercised explicitly.

The opt-in attribute also acts as a footgun mitigation: silent multi-edge fan-out from a typo can't happen because the author has to write `fanout=true` deliberately.
