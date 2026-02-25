# Sprint 002 Intent: Coding Agent Loop Spec Conformance

## Seed

Close the gaps between coding-agent-loop-spec.md and the CodingAgent library implementation. The core agentic loop works but critical wiring is missing: subagent tool executors, profile tool auto-registration, system prompt completeness, event coverage, and process signal handling.

## Context

The CodingAgent library (src/CodingAgent/) implements a bounded agentic loop with tool dispatch, truncation, loop detection, steering, and multi-turn conversation. It was recently integrated into the Attractor engine as a `tab`-shaped handler (CodingAgentHandler) and passes conformance tests for file-write and modify-existing scenarios across all 3 providers.

However, an audit against coding-agent-loop-spec.md reveals ~35% of the spec is unimplemented or partially implemented:

### Critical Gaps
1. **Subagent tool executors NOT wired** — spawn_agent, send_input, wait, close_agent tool definitions exist but have no executable code
2. **Profile tools NOT auto-registered** — Tools from ProviderProfile.ToolDefinitions are sent to the LLM API but never registered in the Session's AgentToolRegistry for dispatch
3. **Tool argument validation missing** — No JSON Schema validation before tool execution

### High Gaps
4. AWAITING_INPUT session state missing (spec 2.3)
5. Streaming events not implemented (ASSISTANT_TEXT_START, ASSISTANT_TEXT_DELTA, TOOL_CALL_OUTPUT_DELTA)
6. SESSION_START event never emitted
7. Git context missing from system prompt (spec 6.4)
8. Timeout handling: direct SIGKILL instead of SIGTERM→wait 2s→SIGKILL (spec 4.2)
9. Gemini tools incomplete (missing read_many_files, list_dir per spec 3.6)

### Medium Gaps
10. Context window awareness not implemented (spec 5.5)
11. System prompt base instructions are minimal placeholders vs. spec's comprehensive templates

## Recent Sprint Context

- **SPRINT-001**: UnifiedLlm Spec Conformance — addresses the LLM client layer (streaming, parallel tools, retry, structured output). Currently in progress. Foundation types already landed.

## Relevant Codebase Areas

| File | Role |
|------|------|
| `src/CodingAgent/Session.fs` | Core agentic loop |
| `src/CodingAgent/ProviderProfile.fs` | Tool definitions and system prompt templates |
| `src/CodingAgent/ExecutionEnvironment.fs` | File I/O and shell execution |
| `src/CodingAgent/ToolRegistry.fs` | Tool dispatch system |
| `src/CodingAgent/SubAgent.fs` | Subagent spawning (definitions only) |
| `src/CodingAgent/SystemPrompt.fs` | System prompt assembly |
| `src/CodingAgent/Types.fs` | SessionConfig, Turn, EventKind |
| `tests/CodingAgent.Tests/Tests.fs` | 71 existing unit tests |

## Constraints

- Must not break existing CodingAgent handler conformance tests (6/6 passing)
- Must not break existing 71 CodingAgent unit tests
- Profile tool registration must work with the existing RegisterTool mechanism
- Subagent executors must respect MaxSubagentDepth

## Success Criteria

- All coding-agent-loop-spec.md Definition of Done items (Section 9) pass
- Subagent tools actually work end-to-end (spawn, send, wait, close)
- Profile tools dispatch through the session without manual registration by handler code
- System prompt includes git context when in a git repo
- Unit test count increases significantly (target: 100+)

## Verification Strategy

- Unit tests with mock LLM adapter for all new functionality
- Existing conformance tests (08-coding-agent/) must still pass
- New conformance test: subagent spawning scenario
- Event emission coverage verified by checking session.Events after test execution

## Uncertainty Assessment

- Correctness uncertainty: **Medium** — subagent lifecycle management needs careful state handling
- Scope uncertainty: **Low** — gaps are clearly enumerated from the spec audit
- Architecture uncertainty: **Low** — extends existing patterns (tool registry, session, events)

## Open Questions

1. Should profile tools replace or merge with handler-registered tools in the session?
2. For subagent file scoping (working_dir param), should it create a new LocalExecutionEnvironment or restrict the existing one?
3. How comprehensive should the Gemini-specific tool additions be (read_many_files, list_dir, web_search)?
