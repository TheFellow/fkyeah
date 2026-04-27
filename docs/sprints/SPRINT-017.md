# Sprint 017: Multi-Edge Fan-Out (Faithful Port)

**Status:** Ready
**Codebase:** `src/Attractor/Engine.fs`, `src/Attractor/Conditions.fs`, `src/Attractor/Validation.fs`
**Depends on:** Sprint 016 (rawOutcome already shipped)
**Decision basis:** `docs/decisions/SPRINT-016-fanout-evaluation.md` — Option A chosen; back-compat break authorised.
**Upstream reference:** swift-omnikit `7678090` (parallel fan-out + status-block default-to-success — port the fan-out half), `f164a80` (raw outcome + unconditional fan-out — port the unconditional-fan-out half).

## Motivation

Swift's `selectAllMatchingEdges` extends edge selection to support implicit fan-out:

- **Multiple condition-matching edges** — when N>1 outgoing edges share the same matching condition (e.g. `CheckDoD -> DefineDoD1 [condition="outcome=needs_dod"]` plus two more), all N target nodes execute sequentially before the engine advances to a common fan-in successor.
- **Multiple unconditional edges** — when a node has N>1 outgoing edges with no condition (e.g. `ConsolidateDoD -> Plan1`, `-> Plan2`, `-> Plan3`), all N targets execute sequentially before advancing.

Today fkyeah's `EdgeSelection.selectEdge` resolves both cases via `bestByWeightThenLexical` and runs only the winner. The explicit `parallel`-shape handler is the documented way to fan out — but it carries different semantics (concurrent + isolated context per branch) and requires an extra wrapper node. Sprint 017 brings the implicit form to parity with upstream so future Swift port work can land cleanly.

The decision doc considered an opt-in attribute (`[fanout=true]`) but rejected it: the rare back-compat surface (no graph in the repo today triggers fan-out, since the only multi-edge fixture uses mutually-exclusive conditions) does not justify a permanent third selection mode.

## Design

Add `selectAllMatchingEdges : Node -> Outcome -> Context -> Graph -> Edge list` alongside existing `selectEdge`. Logic mirrors Swift `f164a80`:

1. If multiple outgoing edges have non-empty conditions and >1 evaluate true → return all condition-matched edges.
2. Else if exactly one condition matches → return that one (singleton list).
3. Else if multiple unconditional edges exist → return all unconditional edges.
4. Else fall back to existing `selectEdge` (covers label-match, suggested-next-ids, single unconditional, cancellation handling) → wrap result in a singleton list (or empty list).

Replace the `EdgeSelection.selectEdge node outcome context graph` call at `Engine.fs:1089` (the Step-6 success-path call) with `selectAllMatchingEdges` and branch on `length > 1`.

### Fan-out execution semantics (Swift parity)

When `selectAllMatchingEdges` returns ≥2 edges:

- Iterate `for edge in allEdges` **sequentially** — not concurrently. Each branch sees the cumulative context updates from prior branches.
- For each `edge.ToNode`: skip if `completedNodes.Contains(toId)` (handles checkpoint resume mid-fan-out via the existing `completedNodes` set — no new schema field).
- For each branch: execute the target node via the same `executeWithRetry` path used by the main loop, write `status.json`, apply context updates, save checkpoint after each branch, advance.
- After all branches complete, advance `currentNodeId` to the **fan-in node**, computed as: the first branch target (in `nextEdges` order) that has at least one outgoing edge; its first outgoing edge's `ToNode` is the fan-in (Swift parity).
- If no branch target has any outgoing edge, terminate the loop with the last branch's outcome (parity with Swift's `state.currentNodeId = nil` path).

### Hard rule: fan-out skips `loop_restart`

`loop_restart` semantics fire **only when the matched-edge set has exactly one entry** (Swift parity). When the set is ≥2, fan-out runs and `loop_restart` is ignored even if one of the branch edges has the attribute. Document this in the spec output. (Authors who want loop-restart with fan-out must split the graph into a fan-out node + a downstream restart-decision node.)

### Hard rule: fan-out applies only on Success / non-fail path

Swift only invokes fan-out in the "Normal edge selection" else-branch — i.e. when the node returned `Success`/`PartialSuccess`. The Fail-path edge selection at `Engine.fs:1067-1087` (which resolves a single fail-edge or retry target) is **unchanged**.

### Fan-in heuristic edge cases

The "first outgoing edge of the first branch target that has an outgoing edge" heuristic is fragile when:

- Branch targets have divergent first successors (e.g. Plan1 → Review, Plan2 → Skip).
- A branch target has zero outgoing edges (terminal branch).
- A branch target's first outgoing edge has a non-trivial condition.

Add a **validation lint warning** (`fanout_fan_in_ambiguous`) in `Validation.fs` that fires when a node has fan-out-eligible outgoing edges but the branch targets do not all share a common first-outgoing-edge target. The warning prints all branch targets and their candidate fan-in nodes so authors can add an explicit Join node if needed.

## Phases

### Phase 1: `selectAllMatchingEdges`

**`src/Attractor/Engine.fs`** — add to the `EdgeSelection` module after the existing `selectEdge`:

```fsharp
/// Return ALL matching outgoing edges (for parallel fan-out).
/// Falls back to single-edge selection when only one edge qualifies.
/// See SPRINT-017.md and swift-omnikit f164a80.
let selectAllMatchingEdges
    (node: Node)
    (outcome: Outcome)
    (context: Context)
    (graph: Graph)
    : Edge list =
    let edges = graph.OutgoingEdges(node.Id)

    if edges.IsEmpty then
        []
    else
        let conditionMatched =
            edges
            |> List.filter (fun e ->
                e.Condition <> "" && Conditions.evaluate e.Condition outcome context)

        if not conditionMatched.IsEmpty then
            conditionMatched
        else
            let unconditional = edges |> List.filter (fun e -> e.Condition = "")
            if unconditional.Length > 1 then
                unconditional
            else
                // Fall back to single-edge logic (label, suggestion, single unconditional, cancellation)
                match selectEdge node outcome context graph with
                | Some edge -> [ edge ]
                | None -> []
```

Note: when there is exactly one condition-matched edge, it is returned as a singleton — bypassing the `selectEdge` label/suggestion/cancellation steps for that case. This is intentional Swift parity: a single condition-match wins outright.

### Phase 2: Engine fan-out branch in `executeLoop`

**`src/Attractor/Engine.fs:1089`** (Step 6 success-path edge selection) — replace the current `selectEdge` call with `selectAllMatchingEdges`. Restructure Step 6/7/8 as:

```fsharp
let nextEdges =
    if outcome.Status = StageStatus.Fail then
        // unchanged Fail-path single-edge logic, wrap in [edge] / []
        ...
    else
        EdgeSelection.selectAllMatchingEdges node outcome context graph

if nextEdges.Length > 1 then
    // Fan-out: execute each branch sequentially.
    // - Skip targets already in completedNodes (resume safety).
    // - Run executeWithRetry per branch; write status.json; apply context updates; checkpoint after each.
    // - Determine fan-in via the first branch target (in order) that has outgoing edges.
    // - currentNode := fan-in node, OR terminate if no fan-in.
    // - DO NOT honour loop_restart on any fan-out branch edge.
    ...
elif nextEdges.IsEmpty then
    // existing terminate-loop path
    ...
else
    // Single edge — existing Step 7/8 (loop_restart + advance), unchanged
    ...
```

The fan-out body must reuse the existing per-node execution helpers (`executeWithRetry`, `recordOutcome`, `writeStatus`, `applyUpdates`, `saveCheckpoint`, `emitter.Emit`) — extract a helper `runSingleNode` if needed, but prefer minimal refactor.

### Phase 3: Validation lint — `fanout_fan_in_ambiguous`

**`src/Attractor/Validation.fs`** — add a new rule (after the existing rules; preserve numbering convention from Sprint 013).

For each node N in the graph, compute `fanoutEligible(N)`:
- Conditional edges grouped by exact condition string; any group with size ≥2 → those edges are fan-out-eligible.
- If `unconditional(N).Length ≥ 2` → all of them are fan-out-eligible.

For each fan-out-eligible group, compute the candidate fan-in for each branch target as `branchTarget.OutgoingEdges |> List.tryHead |> Option.map (fun e -> e.ToNode)`. If the set of candidate fan-ins has more than one distinct value, emit:

```
warning[fanout_fan_in_ambiguous] node "<id>": fan-out branches converge to different first successors:
  - <branchTarget1> -> <fanIn1 or "(terminal)">
  - <branchTarget2> -> <fanIn2 or "(terminal)">
Authors should add an explicit join node so all branches converge cleanly.
```

When all branch targets converge to the same first successor, emit no warning. When all branches are terminal (no outgoing edges), emit no warning (the engine will terminate the loop after fan-out).

### Phase 4: Drive-by — `Conditions.fs:90 validate` recognises `==`

Sprint 016 review surfaced this. The `validate` function should explicitly handle `==` alongside `=` and `!=`. Today it passes by accident (split on `=` happens to leave both halves non-empty). Add:

```fsharp
elif clause.Contains("==") then
    let parts = clause.Split("==", 2, StringSplitOptions.None)
    parts.Length = 2 && not (String.IsNullOrWhiteSpace parts[0])
elif ...
```

Mirror the order used in `evaluateClause`: check `!=` first, then `==`, then `=`. Re-order both functions consistently if needed.

### Phase 5: Tests

**Unit tests** (`tests/Attractor.Tests/Sprint017Tests.fs`):

- `selectAllMatchingEdges` returns all condition-matched edges when multiple match (3 edges, all `outcome="needs_dod"`, all returned).
- `selectAllMatchingEdges` returns single edge when only one condition matches.
- `selectAllMatchingEdges` returns all unconditional edges when count ≥ 2 and no conditional match.
- `selectAllMatchingEdges` returns single unconditional via fallback when count = 1.
- `selectAllMatchingEdges` returns label-match via fallback when no condition/unconditional fan-out applies.
- Engine integration: a graph with three edges `A -> B [condition="outcome=needs_dod"]`, `A -> C [condition="outcome=needs_dod"]`, `A -> D [condition="outcome=needs_dod"]`, all converging on `E`, executes B → C → D → E in order.
- Engine integration: a graph with two unconditional edges `A -> B`, `A -> C`, both converging on `D`, executes B → C → D.
- Engine integration: `loop_restart` on a fan-out edge is **ignored** (executes the branch, does not restart).
- Engine integration: checkpoint resume after B has completed — re-running skips B, executes C → D → E.
- Validation: `fanout_fan_in_ambiguous` warning fires when branches diverge.
- Validation: no warning when branches converge.
- `Conditions.validate "outcome == \"needs_dod\""` returns `true`.

**Conformance fixture** (`conformance/05-parallel/04-multi-edge-fanout/`): a tool-node-emitted custom outcome triggers three sibling tool nodes via duplicated condition edges; all three execute and their context updates are visible in the fan-in's prompt.

### Phase 6: Schema documentation

**`src/Attractor.Cli/Program.fs printSchema`** — add a "FAN-OUT" section near the edge-attributes block:

```
# FAN-OUT (multi-edge)
#
# When a node has multiple outgoing edges that all match the same condition,
# OR multiple unconditional outgoing edges, the engine executes all target
# nodes sequentially before advancing to the common fan-in successor.
#
# The fan-in node is the first outgoing-edge target of the first branch with successors.
# Authors should ensure all branches converge to the same fan-in node, or
# the validator's fanout_fan_in_ambiguous warning will fire.
#
# loop_restart is IGNORED on edges that participate in fan-out.
# For true concurrent/isolated execution, use shape=parallel instead.
```

## Definition of Done

- [ ] `EdgeSelection.selectAllMatchingEdges` implemented with the four-step fallback ladder.
- [ ] `Engine.executeLoop` Step 6 success path uses `selectAllMatchingEdges` and branches on `length > 1`.
- [ ] Sequential fan-out body skips already-completed nodes, runs each branch via the existing per-node helpers, advances to the fan-in successor.
- [ ] `loop_restart` is bypassed when the matched-edge set is ≥2.
- [ ] Fail-path edge selection (lines 1067-1087) is **unchanged**.
- [ ] `Validation.fs fanout_fan_in_ambiguous` lint rule added; emits warning only when first-successors diverge.
- [ ] `Conditions.validate` recognises `==` explicitly.
- [ ] `attractor schema` documents fan-out semantics.
- [ ] All existing unit tests pass (`make test`).
- [ ] All conformance suites pass (`make conformance`) — flag any pre-existing fixture whose semantics change. The decision doc identified `conformance/02-validation/33-all-new-rules-integration` as a candidate; verify it still passes (its conditions `outcome=success` and `outcome=fail` are mutually exclusive, so fan-out should not fire).
- [ ] New unit tests cover the bullets in Phase 5.
- [ ] New conformance fixture under `conformance/05-parallel/04-multi-edge-fanout/`.
- [ ] Zero new compiler warnings, no fantomas/lint regressions (`make format-check && make lint && make analyze`).

## Out of Scope

- Concurrent fan-out (`parallel`-shape semantics) — unchanged.
- A first-class `[fanout=true]` opt-in attribute (Option B from the decision doc).
- Refactoring the explicit `parallel` handler.
- Reworking checkpoint schema — fan-out resume relies on the existing `completedNodes` set.
- Changes to the Fail-path edge selection or retry-target resolution.
- The other two Sprint 016 review nice-to-haves (`Sprint016Tests` discrimination, `Engine.fs:611-615 Option.toObj` idiom) — defer.
