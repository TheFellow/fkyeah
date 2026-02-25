# Sprint 002: Coding Agent Loop Spec Conformance

## Overview

The CodingAgent library powers the `tab`-shaped Attractor handler, giving pipeline nodes a full agentic coding loop with tool access. While the core loop, truncation, steering, and loop detection are solid, an audit against coding-agent-loop-spec.md reveals that subagent tool executors are unconnected, profile tools aren't auto-registered for dispatch, the system prompt lacks git context, and several event types are never emitted. This sprint closes every gap to reach full spec conformance.

The work follows the same pattern as SPRINT-001 (UnifiedLlm conformance): enumerate gaps from the spec's Definition of Done, implement each with real behavior, and verify with tests that exercise the actual functionality.

## Use Cases

1. **Subagent delegation**: A coding agent working on a large task spawns a subagent to handle a scoped subtask (e.g., "write the test file"), waits for completion, and incorporates the result.
2. **Profile tool dispatch**: When a pipeline runs a `tab` node, the LLM calls `write_file` via the API's tool-use mechanism, and the session dispatches it through the registered executor — without the handler having to manually wire each tool.
3. **Git-aware prompting**: The system prompt tells the LLM what branch it's on, what files are modified, and recent commit history so it can make contextually appropriate changes.
4. **Graceful timeout**: A shell command hangs; the session sends SIGTERM, waits 2 seconds, then SIGKILL — and returns the partial output to the LLM with a timeout message.

## Implementation Plan

### Phase 1: Profile Tool Auto-Registration (~25%)

**Spec:** 3.7, 3.8
**Files:** `src/CodingAgent/Session.fs`, `src/CodingAgent/ToolRegistry.fs`

The critical gap: profile tools (read_file, write_file, shell, etc.) are sent to the LLM as tool definitions but never registered in the session's AgentToolRegistry. The LLM calls them, the session tries to dispatch, and gets "unknown tool."

- [ ] At session creation, auto-register all profile tools in AgentToolRegistry
- [ ] Tool executors for each SharedTool: read_file, write_file, edit_file, shell, grep, glob, apply_patch
- [ ] Custom RegisterTool() overrides profile tools with same name (latest-wins)
- [ ] Remove manual tool registration from Handlers.fs CodingAgentHandler (let the session handle it)
- [ ] Add JSON Schema validation before dispatching tool arguments

**Definition of Done:**
- Session created with AnthropicProfile auto-registers edit_file, read_file, write_file, shell, grep, glob
- Session created with OpenAIProfile auto-registers apply_patch, read_file, write_file, shell, grep, glob
- CodingAgentHandler no longer calls registerCodingTools — session self-registers
- Mock test: LLM returns write_file tool call → session dispatches and file is written
- Conformance: 08-coding-agent tests still pass (6/6)

### Phase 2: Subagent Tool Executors (~25%)

**Spec:** 7.1-7.3
**Files:** `src/CodingAgent/SubAgent.fs`, `src/CodingAgent/Session.fs`

Tool definitions for spawn_agent, send_input, wait, close_agent exist but have no executor. Wire them up.

- [ ] Implement spawn_agent executor: calls SubAgent.spawn(), returns handle ID
- [ ] Implement send_input executor: finds handle by ID, calls SendInput()
- [ ] Implement wait executor: finds handle by ID, calls Wait(), returns result
- [ ] Implement close_agent executor: finds handle by ID, calls Close()
- [ ] Store active subagent handles in session state (Dictionary<string, SubAgentHandle>)
- [ ] Respect MaxSubagentDepth from SessionConfig
- [ ] Support working_dir parameter for directory scoping
- [ ] Support model override parameter

**Definition of Done:**
- Unit test: spawn subagent with mock LLM → subagent processes input → wait returns result
- Depth limiting: spawn at max depth → error result returned to LLM
- Working dir: subagent scoped to subdirectory can only read/write within it
- All 4 tool executors tested individually

### Phase 3: System Prompt Completeness (~15%)

**Spec:** 6.2-6.4
**Files:** `src/CodingAgent/SystemPrompt.fs`, `src/CodingAgent/ProviderProfile.fs`

- [ ] Add git context to EnvironmentContext.build(): branch, short status, recent commits
- [ ] Implement git detection: check for `.git` directory or `git rev-parse`
- [ ] Add "Is git repository: true/false" to environment block
- [ ] Add "Git branch: {name}" and "Modified files: {count}" when in git repo
- [ ] Expand provider base instructions to match spec's comprehensive templates
- [ ] Anthropic: mirror Claude Code's system prompt structure
- [ ] OpenAI: mirror Codex's system prompt structure

**Definition of Done:**
- System prompt contains git branch name when session runs inside a git repo
- System prompt contains "Is git repository: false" when not in a git repo
- Provider-specific instructions are comprehensive (not placeholder one-liners)
- Unit test: mock environment with git context → system prompt contains expected fields

### Phase 4: Event System Completeness (~15%)

**Spec:** 2.9
**Files:** `src/CodingAgent/Types.fs`, `src/CodingAgent/Session.fs`

- [ ] Emit SessionStart event at session creation (currently missing)
- [ ] Add ASSISTANT_TEXT_START event kind and emit before LLM response processing
- [ ] Add ASSISTANT_TEXT_DELTA event kind (for future streaming support)
- [ ] Add TOOL_CALL_OUTPUT_DELTA event kind (for future streaming support)
- [ ] Ensure every event kind defined in Types.fs is actually emitted somewhere

**Definition of Done:**
- session.Events includes SessionStart as first event after creation
- session.Events includes AssistantTextStart before each AssistantTextEnd
- All EventKind values have at least one emission site
- Unit test: run a session → verify complete event sequence

### Phase 5: Process Signal Handling & Edge Cases (~10%)

**Spec:** 4.2, 2.3
**Files:** `src/CodingAgent/ExecutionEnvironment.fs`, `src/CodingAgent/Types.fs`

- [ ] Implement SIGTERM → wait 2s → SIGKILL pattern for command timeouts
- [ ] Format timeout message for LLM: include partial output + "[TIMEOUT after Xs]"
- [ ] Add AWAITING_INPUT session state (model asked a question, no tool calls, open-ended)
- [ ] Detect awaiting input: response has no tool calls and ends with a question

**Definition of Done:**
- Timeout test: mock slow command → SIGTERM sent first, then SIGKILL after 2s
- Timeout message includes partial stdout captured before kill
- AWAITING_INPUT state set when LLM responds with question and no tool calls
- State transitions tested: Idle → Processing → AwaitingInput → Processing → Idle

### Phase 6: Gemini Tool Parity (~10%)

**Spec:** 3.6
**Files:** `src/CodingAgent/ProviderProfile.fs`

- [ ] Add read_many_files tool to Gemini profile
- [ ] Add list_dir tool to Gemini profile
- [ ] Register executors for Gemini-specific tools

**Definition of Done:**
- Gemini profile includes read_many_files and list_dir in ToolDefinitions
- Executors work: read_many_files reads multiple files in one call, list_dir lists directory

## Files Summary

| File | Action | Purpose |
|------|--------|---------|
| `src/CodingAgent/Session.fs` | Modify | Auto-register profile tools, subagent handle storage, event emission |
| `src/CodingAgent/SubAgent.fs` | Modify | Wire tool executors for spawn/send/wait/close |
| `src/CodingAgent/SystemPrompt.fs` | Modify | Git context in environment block |
| `src/CodingAgent/ProviderProfile.fs` | Modify | Gemini tools, comprehensive base instructions |
| `src/CodingAgent/ExecutionEnvironment.fs` | Modify | SIGTERM→wait→SIGKILL, timeout messages |
| `src/CodingAgent/Types.fs` | Modify | AWAITING_INPUT state, new event kinds |
| `src/CodingAgent/ToolRegistry.fs` | Modify | Schema validation before dispatch |
| `src/Attractor/Handlers.fs` | Modify | Remove manual registerCodingTools (session self-registers) |
| `tests/CodingAgent.Tests/Tests.fs` | Modify | All new unit tests |

## Definition of Done

- [ ] All coding-agent-loop-spec.md Section 9 DoD items pass
- [ ] Profile tools auto-registered — no manual wiring needed in handlers
- [ ] Subagent tools work end-to-end (spawn → send → wait → close)
- [ ] System prompt includes git context when in a git repo
- [ ] All EventKind values emitted at appropriate points
- [ ] SIGTERM→wait 2s→SIGKILL for command timeouts
- [ ] AWAITING_INPUT state implemented and tested
- [ ] Gemini profile includes read_many_files and list_dir
- [ ] Existing tests pass: 71+ CodingAgent unit tests, 6/6 conformance
- [ ] New tests: target 100+ CodingAgent unit tests
- [ ] `make test` passes
- [ ] `make conformance` passes

## Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Subagent concurrency issues | Medium | High | Single-threaded dispatch, mutex on handle map |
| Profile auto-registration breaks existing handler tests | Medium | Medium | Keep RegisterTool override mechanism, update tests |
| Git command execution in CI/Docker | Low | Medium | Gracefully degrade when git not available |
| SIGTERM not available on Windows | Low | Low | Platform check, fall back to Kill() on Windows |

## Dependencies

- SPRINT-001 (UnifiedLlm): Some improvements to streaming/tool types benefit this sprint
- No external dependencies
