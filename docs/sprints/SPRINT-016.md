# Sprint 016: Raw Outcome Preservation for Edge Conditions

**Status:** Ready
**Codebase:** `src/Attractor/Types.fs`, `src/Attractor/Engine.fs`, `src/Attractor/Conditions.fs`, `src/Attractor/HandlerContracts.fs`, `src/Attractor.Cli/Checkpoint.fs`
**Depends on:** None
**Upstream reference:** swift-omnikit commit `f164a80` (`fix(attractor): preserve raw outcome for custom edge conditions and unconditional fan-out`) — port the rawOutcome half only; multi-edge fan-out is out of scope.

## Motivation

Today, when a coding-agent or codergen node emits a JSON status block like

```json
{ "outcome": "needs_dod", "context_updates": {} }
```

the engine pipes that string through `StageStatus.Parse`, which only knows the five enum members `success | partial_success | retry | fail | skipped`. Anything else falls back to the previous `Status` value. The string `"needs_dod"` is dropped on the floor.

Edge conditions then evaluate against `outcome.Status.ToString()` — never the raw agent string — so a guard like

```dot
CheckDoD -> DefineDoD [ condition = "outcome == \"needs_dod\"" ];
```

can never match. Authors are forced to encode custom routing in `preferred_label`, `context_updates`, or one of the five enum buckets, which is cramped.

Fix: preserve the raw outcome string emitted by the handler and use it (when present) for both context publishing and condition evaluation.

## Design

Add an optional `RawOutcome: string option` field to the `Outcome` record. Semantics:

- `None` → behaviour is identical to today (back-compat).
- `Some s` → `s` is what authors see at `context["outcome"]` and what `Conditions.evaluate` reads when resolving the bare `outcome` key.

The five `Outcome` factory ctors leave `RawOutcome = None` so all current call sites are unaffected. Only the JSON-status loader populates it from the wire-format `outcome` field, before that string is parsed into the `StageStatus` enum.

## Phases

### Phase 1: Type and resolution change

1. **`src/Attractor/Types.fs:120`** — extend the `Outcome` record:

   ```fsharp
   type Outcome =
       { Status: StageStatus
         /// The raw "outcome" string emitted by a handler/agent before
         /// StageStatus.Parse coercion. None → fall back to Status.ToString().
         /// Populated from JSON status blocks; preserved across status.json
         /// round-trips and checkpoint resume so edge conditions can match
         /// custom outcome strings (e.g. "needs_dod", "has_dod").
         RawOutcome: string option
         PreferredLabel: string
         SuggestedNextIds: string list
         ContextUpdates: Map<string, string>
         Notes: string
         FailureReason: string }
   ```

   All five static ctors (`Success`, `Fail`, `Retry`, `PartialSuccess`, plus the existing pattern used elsewhere) initialise `RawOutcome = None`.

2. Add a member helper:

   ```fsharp
   member this.OutcomeString =
       this.RawOutcome |> Option.defaultValue (this.Status.ToString())
   ```

   Use this everywhere `outcome.Status.ToString()` is currently used to publish or evaluate the outcome **string**. Do **not** use it where `Status` is matched against the enum directly (e.g. retry logic, gate checks).

3. **`src/Attractor/Conditions.fs:23`** — `resolveKey` for the `"outcome"` key returns `outcome.OutcomeString`.

4. **`src/Attractor/Engine.fs:1013` and `:1425`** — both `context.Set("outcome", outcome.Status.ToString())` call sites become `context.Set("outcome", outcome.OutcomeString)`.

### Phase 2: Round-trip through status.json

5. **`src/Attractor/HandlerContracts.fs:26` (`HandlerArtifacts.writeStatus`)** — the serialised `outcome` field is `outcome.OutcomeString` (not `outcome.Status.ToString()`). This way a custom outcome written to status.json today round-trips back when the engine (or a checkpoint resume) re-reads it.

6. **`src/Attractor/Engine.fs:685` (`tryLoadStatusOutcome`)** — preserve the raw JSON `outcome` field on the loaded `Outcome`:

   ```fsharp
   Some
       { Status = status
         RawOutcome = statusRaw                 // the unparsed JSON string
         PreferredLabel = preferredLabel
         ... }
   ```

   When the JSON omits `outcome` and falls back to the `status` key, populate `RawOutcome` from whichever was found. When neither key is present, leave `RawOutcome = None` and use the fallback's status.

### Phase 3: Checkpoint round-trip (CLI)

7. **`src/Attractor.Cli/Checkpoint.fs:116`, `:149`, `:206`, `:254`** — the four sites that build `"outcome"` from `status.ToString()` should use the same `OutcomeString` semantics. If the checkpoint format does not currently persist the raw outcome, extend the on-disk schema **only if** existing tests reveal a resume path that materially needs it; otherwise use `Status.ToString()` for backward compatibility and leave a one-line `// TODO` comment noting the limitation. Prefer the smallest change that doesn't break checkpoint compatibility.

   (Read the four sites and the surrounding write/read functions before making any schema change. If the checkpoint already persists the parsed `Outcome`, plumb `RawOutcome` through the same JSON schema; if it only persists the enum, leave it as enum-only and document.)

## Definition of Done

- [ ] `Outcome.RawOutcome: string option` field added; all five ctors default to `None`.
- [ ] `Outcome.OutcomeString` helper added.
- [ ] `Conditions.resolveKey "outcome"` returns `OutcomeString`.
- [ ] Both `context.Set("outcome", ...)` sites in `Engine.fs` use `OutcomeString`.
- [ ] `HandlerArtifacts.writeStatus` writes `OutcomeString` for the `outcome` field.
- [ ] `Engine.tryLoadStatusOutcome` populates `RawOutcome` from the parsed JSON string.
- [ ] Checkpoint round-trip handled per Phase 3 (either fully plumbed or documented limitation).
- [ ] All existing tests pass (`make test`).
- [ ] All conformance suites pass (`make conformance`).
- [ ] New unit tests:
  - `Conditions.evaluate "outcome == \"needs_dod\""` is `true` for an `Outcome` with `RawOutcome = Some "needs_dod"` and `Status = Success`.
  - `Conditions.evaluate "outcome == \"success\""` is `true` for an `Outcome` with `RawOutcome = None` and `Status = Success` (back-compat).
  - `Outcome.OutcomeString` falls back to `Status.ToString()` when `RawOutcome = None`.
  - `tryLoadStatusOutcome` populates `RawOutcome` from a status.json containing `{"outcome":"needs_dod", ...}` and the loaded `Status` falls through to the fallback.
  - `HandlerArtifacts.writeStatus` round-trip: an `Outcome` with `RawOutcome = Some "needs_dod"` writes a status.json whose `outcome` field equals `"needs_dod"`.
- [ ] New conformance test under `conformance/04-context/` (or similar): a tool node writes its own `status.json` with a custom `outcome` string, and a downstream edge condition `outcome == "<custom>"` matches and routes correctly.
- [ ] Zero new compiler warnings, no fantomas/lint regressions (`make format-check && make lint && make analyze`).

## Out of Scope

- Multi-edge fan-out (`selectAllMatchingEdges`) — explicit follow-up, see Q1/Q2 discussion.
- Changing the `StageStatus` enum or the existing five ctors' signatures.
- Any change to the wire format of `status.json` beyond preserving the raw `outcome` string that authors already write.
