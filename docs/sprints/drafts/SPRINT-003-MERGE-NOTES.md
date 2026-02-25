# Sprint 003 Merge Notes

## Claude Draft Strengths
- CWD control, outcome detection, cycle caps directly address failure analysis issues
- Log versioning preserves backward compat (latest at root, versioned in subdirs)
- Artifact store with filesystem backing is simple and debuggable
- Good use cases grounding each feature

## Codex Draft Strengths
- Identified DOT parser qualified-key support as prerequisite for tool hooks
- Correctly flagged tool-hook semantic mismatch (node boundary vs tool-call boundary)
- Added engine retry for FAIL outcomes (missing from Claude draft)
- Better HTTP server scoping with minimal endpoint contract
- Event coverage for parallel/human/checkpoint flows

## Valid Critiques Accepted

1. **Parser must support qualified keys** — Added as prerequisite phase before tool hooks. `tool_hooks.pre` requires dotted key parsing.
2. **Tool-hook semantics: tool-call boundary, not node boundary** — Corrected. Pre/post hooks fire around individual LLM tool calls, matching spec 9.7.
3. **Engine retry for FAIL outcomes** — Added as explicit task. Spec 11.5 requires this.
4. **HTTP server mode** — Added as full phase. User confirmed Full REST API scope in interview.
5. **Artifact store threshold: 100KB not 10KB** — Aligned with spec default.
6. **CWD scope: tool handler only** — CodingAgent handler already handles it. Focus on ToolHandler consistency.
7. **Event coverage for parallel/human flows** — Added explicit tasks.
8. **Node visit counts reset on loop_restart** — Accepted. Visit counts per-restart, not cumulative.

## Critiques Rejected (with reasoning)

1. **"CLI --help already implemented"** — Partially true, but CLI parsing is still manual string matching. Proper argument parser library is warranted.
2. **"Outdated baseline metrics"** — Same as SPRINT-002, not actionable in plan doc.

## Interview Refinements Applied
- HTTP server mode: Full REST API (user confirmed)
- Artifact store: Filesystem-backed (user confirmed)

## Final Decisions
- Parser qualified-key support first, then tool hooks
- Tool hooks at tool-call boundary per spec 9.7
- HTTP server with POST /pipelines, GET /{id}, GET /{id}/events (SSE), POST /{id}/cancel, question endpoints
- Artifact store: 100KB threshold, filesystem at {logs_root}/artifacts/
- Visit counts per-restart scope, not cumulative
- Engine retry for FAIL under retry policy added
