# Sprint 002: Coding Agent Loop Spec Conformance

**Status:** Planned
**Spec:** `coding-agent-loop-spec.md` Sections 2-7, 9
**Codebase:** `src/CodingAgent/`, integration in `src/Attractor/Handlers.fs`

## Overview

The CodingAgent library powers the `tab`-shaped Attractor handler, giving pipeline nodes a full agentic coding loop with tool access. The core loop, truncation, steering, and loop detection are solid, but an audit against coding-agent-loop-spec.md reveals that subagent tool executors are unconnected, profile tools aren't auto-registered for dispatch, the system prompt lacks git context and full project-doc discovery, and several event types are never emitted. This sprint closes every gap to reach full Section 9 DoD conformance.

## Use Cases

1. **Subagent delegation**: A coding agent spawns a subagent to handle a scoped subtask, waits for completion, and incorporates the result.
2. **Profile tool dispatch**: The LLM calls `write_file` via the API's tool-use mechanism and the Session dispatches it through auto-registered executors — no handler-side wiring needed.
3. **Git-aware prompting**: The system prompt tells the LLM the branch, modified file count, and recent commits.
4. **Graceful timeout**: A shell command hangs; SIGTERM, wait 2s, SIGKILL — partial output returned to the LLM with a timeout message.
5. **Context-window awareness**: Session warns when nearing the context limit, preventing silent truncation or API failures.

## Implementation Plan

### Phase 1: Profile Tool Auto-Registration

**Spec:** 3.7, 3.8
**Files:** `src/CodingAgent/Session.fs`, `src/CodingAgent/ToolRegistry.fs`

- [ ] At Session creation, auto-register executors for all profile tools in AgentToolRegistry
- [ ] Executors for each SharedTool: read_file, write_file, edit_file, shell, grep, glob, apply_patch
- [ ] Custom RegisterTool() overrides profile tools with same name (latest-wins)
- [ ] Add JSON Schema validation before dispatching tool arguments (required fields, types)
- [ ] On validation failure: send error result to model, not exception
- [ ] Migration gate: only remove handler-side registerCodingTools after Session auto-registration + conformance tests pass

**Definition of Done:**
- Session auto-registers all profile tools without handler intervention
- Mock test: LLM returns write_file tool call → Session dispatches and file is written
- Validation test: tool with `required: ["location"]` receives `{}` → error result sent to model
- Conformance: 08-coding-agent tests still pass (6/6)
- After parity confirmed: remove registerCodingTools from Handlers.fs CodingAgentHandler

### Phase 2: Subagent Tool Executors

**Spec:** 7.1-7.3
**Files:** `src/CodingAgent/SubAgent.fs`, `src/CodingAgent/Session.fs`

- [ ] Implement spawn_agent executor: calls SubAgent.spawn(), stores handle in session state, returns handle ID
- [ ] Implement send_input executor: finds handle by ID, calls SendInput()
- [ ] Implement wait executor: finds handle by ID, calls Wait(), returns result
- [ ] Implement close_agent executor: finds handle by ID, calls Close()
- [ ] Store active subagent handles in Dictionary<string, SubAgentHandle>
- [ ] Respect MaxSubagentDepth from SessionConfig
- [ ] Support working_dir parameter as CWD override (not filesystem sandbox — shared environment)
- [ ] Support model override parameter

**Definition of Done:**
- Unit test: spawn subagent with mock LLM → subagent processes input → wait returns result
- Depth limiting: spawn at max depth → error result returned to LLM
- Working dir: subagent CWD set to specified directory
- All 4 tool executors tested individually

### Phase 3: System Prompt Completeness

**Spec:** 6.1-6.5
**Files:** `src/CodingAgent/SystemPrompt.fs`, `src/CodingAgent/ProviderProfile.fs`

- [ ] Add git context to EnvironmentContext.build(): `Is git repository: true/false`, branch name, modified file count, recent 5 commits
- [ ] Implement git detection via `.git` directory check or `git rev-parse`
- [ ] Implement full project-doc discovery per spec 6.5:
  - AGENTS.md always loaded (regardless of provider)
  - Provider-specific: CLAUDE.md (anthropic), .codex/instructions.md (openai), GEMINI.md (gemini)
  - Root-level files loaded first, subdirectory files appended (deeper = higher precedence)
  - 32KB byte budget with truncation marker
- [ ] Expand provider base instructions to be comprehensive (match reference agent prompts)

**Definition of Done:**
- System prompt contains git branch when in a git repo
- System prompt contains "Is git repository: false" when not in a git repo
- AGENTS.md loaded for all providers; CLAUDE.md only for Anthropic; .codex/instructions.md only for OpenAI
- 32KB byte budget enforced with truncation marker
- Unit test: mock environment with git context → system prompt contains expected fields

### Phase 4: Event System Completeness

**Spec:** 2.9
**Files:** `src/CodingAgent/Types.fs`, `src/CodingAgent/Session.fs`

- [ ] Emit SessionStart event at session creation (currently missing)
- [ ] Emit AssistantTextStart before LLM response processing
- [ ] Emit AssistantTextDelta: for non-streaming sessions, emit as batched delta from parsed response; for streaming, emit true incremental deltas
- [ ] Emit ToolCallOutputDelta: same strategy (batched or streaming)
- [ ] Ensure every EventKind value has at least one emission site

**Definition of Done:**
- session.Events includes SessionStart as first event
- session.Events includes AssistantTextStart/AssistantTextEnd bracketing each response
- Delta events present in history for both streaming and non-streaming paths
- All EventKind values emitted at appropriate points

### Phase 5: Process Signal Handling & Session Lifecycle

**Spec:** 4.2, 2.3
**Files:** `src/CodingAgent/ExecutionEnvironment.fs`, `src/CodingAgent/Types.fs`

- [ ] Implement SIGTERM → wait 2s → SIGKILL for command timeouts (Unix platforms)
- [ ] Clamp timeout per MaxCommandTimeoutMs from SessionConfig
- [ ] Format timeout message for LLM: `[ERROR: Command timed out after {X}ms. Partial output shown above. Retry with longer timeout_ms.]`
- [ ] Fall back to Kill() on Windows where SIGTERM unavailable
- [ ] Add AwaitingInput session state as explicit host/session signal (not question-mark heuristic)
- [ ] State transitions: Idle → Processing → AwaitingInput → Processing → Idle

**Definition of Done:**
- Timeout test: mock slow command → SIGTERM sent first, SIGKILL after 2s
- Timeout message matches spec format
- AwaitingInput state set via explicit mechanism, not content heuristics
- State transitions tested through full lifecycle

### Phase 6: Context-Window Awareness

**Spec:** 5.5, 9.11
**Files:** `src/CodingAgent/Session.fs`, `src/CodingAgent/Types.fs`

- [ ] Track approximate token usage across session history (chars/4 heuristic)
- [ ] Emit Warning event when usage exceeds 80% of profile.ContextWindowSize
- [ ] Include usage metrics in event data (current tokens, limit, percentage)

**Definition of Done:**
- Session with large history emits Warning event at 80% threshold
- Warning includes current/limit/percentage data
- Unit test: mock session with accumulating history → warning emitted at threshold

### Phase 7: Gemini Tool Parity

**Spec:** 3.6
**Files:** `src/CodingAgent/ProviderProfile.fs`

- [ ] Add read_many_files tool to Gemini profile (reads multiple files in one call)
- [ ] Add list_dir tool to Gemini profile
- [ ] Register executors for Gemini-specific tools

**Definition of Done:**
- Gemini profile includes read_many_files and list_dir in ToolDefinitions
- Executors work: read_many_files reads multiple files, list_dir lists directory

## Files Summary

| File | Action | Purpose |
|------|--------|---------|
| `src/CodingAgent/Session.fs` | Modify | Auto-register profile tools, subagent handles, events, context-window tracking |
| `src/CodingAgent/SubAgent.fs` | Modify | Wire tool executors |
| `src/CodingAgent/SystemPrompt.fs` | Modify | Git context, full project-doc discovery |
| `src/CodingAgent/ProviderProfile.fs` | Modify | Gemini tools, comprehensive base instructions |
| `src/CodingAgent/ExecutionEnvironment.fs` | Modify | SIGTERM→wait→SIGKILL, timeout messages |
| `src/CodingAgent/Types.fs` | Modify | AwaitingInput state, new event kinds |
| `src/CodingAgent/ToolRegistry.fs` | Modify | Schema validation before dispatch |
| `src/Attractor/Handlers.fs` | Modify | Remove registerCodingTools after parity gate |
| `tests/CodingAgent.Tests/Tests.fs` | Modify | All new unit tests |

## Definition of Done

- [ ] All coding-agent-loop-spec.md Section 9 DoD items pass
- [ ] Profile tools auto-registered — no manual wiring in handlers
- [ ] Subagent tools work end-to-end (spawn → send → wait → close)
- [ ] System prompt includes git context and full project-doc discovery
- [ ] All EventKind values emitted at appropriate points
- [ ] SIGTERM→wait 2s→SIGKILL for command timeouts
- [ ] AwaitingInput state implemented via explicit signal
- [ ] Context-window warning at 80% threshold
- [ ] Tool argument JSON Schema validation before dispatch
- [ ] Gemini profile includes read_many_files and list_dir
- [ ] Existing tests pass; conformance 08-coding-agent 6/6
- [ ] `make test` passes
- [ ] `make conformance` passes

## Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Profile auto-registration breaks existing handler tests | Medium | Medium | Migration gate: parity before removal |
| Subagent concurrency issues | Medium | High | Single-threaded dispatch, mutex on handle map |
| Git command execution in CI/Docker | Low | Medium | Graceful degrade when git not available |
| SIGTERM not available on Windows | Low | Low | Platform check, fall back to Kill() |

## Dependencies

- SPRINT-001 (UnifiedLlm): Streaming support enables true delta events
