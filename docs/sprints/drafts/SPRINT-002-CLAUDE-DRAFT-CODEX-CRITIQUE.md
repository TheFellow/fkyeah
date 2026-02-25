# Critique: SPRINT-002-CLAUDE-DRAFT (Codex)

## Overall Assessment

Strong draft with good prioritization on the two biggest real gaps: profile tool auto-registration and subagent executor wiring. The plan is executable, but it is not yet sufficient to hit full `coding-agent-loop-spec.md` conformance as claimed.

## Findings

### Critical

1. **Missing context-window awareness work (`5.5`, `9.11`)**
   - No task covers usage threshold warnings (`chars/4`, 80% warning emit).
   - This is a required DoD item and currently absent from plan scope.

2. **System prompt scope is incomplete (`6.1`, `6.5`, `9.8`)**
   - Phase 3 focuses on git context, but not layered project-doc precedence (git-root -> cwd traversal), provider-specific file filtering rules, or byte-budget truncation behavior.
   - Full prompt conformance requires all of these, not only git metadata.

3. **Event conformance is deferred rather than implemented**
   - `ASSISTANT_TEXT_DELTA` / `TOOL_CALL_OUTPUT_DELTA` are marked "for future streaming support" (lines 86-87), but DoD expects full event coverage.
   - Without an actual stream path or equivalent delta-emission strategy, this remains a spec miss.

### High

4. **`AWAITING_INPUT` detection strategy is brittle and likely wrong**
   - Detecting "awaiting input" by "response ends with a question" (line 104) will create false positives and provider-dependent behavior.
   - This should be an explicit control path, not punctuation heuristics.

5. **Transition risk in removing handler-side tool registration**
   - Line 28 removes manual `registerCodingTools` from Attractor handler. Good end-state, but this needs an explicit migration gate and compatibility tests to avoid breaking existing coding-agent conformance while Session wiring is in flux.

6. **Subagent `working_dir` requirement overreaches spec intent**
   - Line 57 claims scoped subagent "can only read/write within it." The spec requires shared environment with optional scoped working directory, not hard sandboxing.
   - This adds security semantics not defined in scope and can surprise existing behavior.

7. **Timeout policy implementation is underspecified**
   - SIGTERM->wait->SIGKILL is included, but no explicit handling for `MaxCommandTimeoutMs` clamping and per-call override bounds in session-level dispatch.

### Medium

8. **Outdated baseline metrics in DoD section**
   - "71+ CodingAgent unit tests" and "target 100+" are stale relative to current repository test volume.
   - Not blocking, but weakens planning accuracy.

## Recommended Adjustments

1. Add explicit Phase item for context-window warning emission (`Warning` event at 80% threshold).
2. Expand Phase 3 to include full project-doc discovery rules (`AGENTS.md` always, provider-specific files, root-to-cwd layering, 32KB truncation marker).
3. Convert event phase from "future support" to concrete streaming/non-streaming conformance plan.
4. Replace question-mark heuristic with explicit host/session signal for `AwaitingInput` transitions.
5. Keep handler registration removal behind a parity gate: only after Session auto-registration + executor parity tests pass.
6. Clarify subagent `working_dir` as default working directory override, not a hard filesystem sandbox.

## Verdict

Good foundation draft, but not yet full-conformance ready. With the above additions/adjustments, it can reliably satisfy Section 9 DoD claims.
