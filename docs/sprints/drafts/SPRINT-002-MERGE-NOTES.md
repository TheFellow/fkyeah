# Sprint 002 Merge Notes

## Claude Draft Strengths
- Clear phasing with profile auto-registration as foundation
- Good use cases that ground the work in real scenarios
- Subagent lifecycle well-structured (spawn/send/wait/close)
- Git context addition to system prompt is practical and valuable

## Codex Draft Strengths
- Identified context-window awareness as a critical missing DoD item
- Correctly flagged that system prompt work needs full project-doc discovery, not just git
- Better risk framing around tool-registry migration
- Highlighted that AWAITING_INPUT detection via question heuristics is brittle

## Valid Critiques Accepted

1. **Context-window awareness (5.5)** — Added as new Phase. Required by DoD.
2. **System prompt: full project-doc discovery** — Expanded Phase 3 to include layered precedence, byte budget, provider-specific filtering. Not just git metadata.
3. **Event delta emission** — Changed from "future" to concrete plan. Non-streaming sessions will emit batched deltas from response parsing.
4. **AWAITING_INPUT as explicit signal** — Replaced question-mark heuristic with explicit model/host signaling approach.
5. **Handler registration migration gate** — Added explicit parity gate: remove handler-side registration only after Session auto-registration passes conformance.
6. **Subagent working_dir is override, not sandbox** — Corrected. Shared environment with CWD override, not filesystem restriction.
7. **MaxCommandTimeoutMs clamping** — Added to timeout phase.

## Critiques Rejected (with reasoning)

1. **"Outdated baseline metrics"** — True but not actionable in the plan doc. Test counts will be current at execution time.

## Interview Refinements Applied
- No interview questions needed for this sprint (low uncertainty).

## Final Decisions
- Profile tools auto-registered at Session creation; custom RegisterTool overrides
- Subagent executors wired with shared env + optional CWD override
- Context-window awareness added as new phase
- Event emission: batch deltas from non-streaming responses, true deltas when streaming
