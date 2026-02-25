# Sprint: CodingAgent Loop Spec Conformance (Codex Draft)

**Status:** Draft
**Spec:** `coding-agent-loop-spec.md` Sections 2-7, 9
**Codebase:** `src/CodingAgent/`, integration touchpoints in `src/Attractor/Handlers.fs`

## Overview

`src/CodingAgent/` has a solid base loop (turn history, loop detection, truncation, steering queues, profile-specific tool definitions), but several spec-critical behaviors are still missing or only partially wired.

Highest-risk gaps in current code:

1. Subagent tools are defined (`spawn_agent`, `send_input`, `wait`, `close_agent`) but not executable in normal session dispatch.
2. Profile tool definitions are not auto-registered into `Session` dispatch; handlers currently compensate by manual registration.
3. Tool argument schema validation is missing.
4. Session lifecycle/event coverage is partial (`AWAITING_INPUT`, `SESSION_START`, streaming text events).
5. Timeout kill sequence is direct kill instead of SIGTERM -> grace period -> SIGKILL.
6. System prompt context is missing required git snapshot context and full project-doc discovery precedence.
7. Context window warning signaling is not implemented.

This sprint closes these gaps without regressing existing Attractor coding-agent behavior.

## Phase 1: Session Lifecycle and Event Completeness

### Task 1.1: Add full Session lifecycle states and transitions
**Spec:** 2.3, 9.1
**Files:** `src/CodingAgent/Types.fs`, `src/CodingAgent/Session.fs`

- [ ] Add `AwaitingInput` to `SessionState`
- [ ] Define explicit transitions for:
  - [ ] `Idle -> Processing`
  - [ ] `Processing -> AwaitingInput` (host/user input request path)
  - [ ] `AwaitingInput -> Processing`
  - [ ] `Processing/Idle/AwaitingInput -> Closed`
- [ ] Preserve backward-compatible behavior for existing `ProcessInput` callers

**Definition of Done:**
- Lifecycle tests cover all allowed transitions
- No regression in current sequential input flow

### Task 1.2: Emit full event lifecycle including missing start brackets
**Spec:** 2.9, 9.10
**Files:** `src/CodingAgent/Types.fs`, `src/CodingAgent/Session.fs`

- [ ] Ensure `SessionStart` is emitted once per session creation/start
- [ ] Emit `TurnStart` and `TurnEnd` around each model/tool round
- [ ] Ensure `SessionEnd` includes reason metadata consistently (`closed`, `aborted`, `completed`, `error`)
- [ ] Add missing event kinds for streamed output parity:
  - [ ] `AssistantTextStart`
  - [ ] `AssistantTextDelta`
  - [ ] `ToolCallOutputDelta`

**Definition of Done:**
- Event sequence is deterministic and test-assertable
- Existing event consumers still parse current event payloads

### Task 1.3: Add streaming-aware assistant/tool event emission path
**Spec:** 2.9, 9.10
**Files:** `src/CodingAgent/Session.fs`

- [ ] Add optional streaming loop mode using `Client.stream()` when profile supports streaming
- [ ] Emit start/delta/end events for assistant text in streaming mode
- [ ] Emit output delta events for streaming tool output where applicable
- [ ] Keep non-streaming path as stable fallback

**Definition of Done:**
- Streamed and non-streamed responses both reach identical final `AssistantTurn` content
- Event stream includes delta events during streaming runs

## Phase 2: Tool Registration, Validation, and Dispatch Correctness

### Task 2.1: Auto-register profile tools into Session registry
**Spec:** 3.2, 3.8, 9.2, 9.3
**Files:** `src/CodingAgent/Session.fs`, `src/CodingAgent/ProviderProfile.fs`, `src/CodingAgent/ToolRegistry.fs`

- [ ] Build a default tool-executor binding layer for profile tools
- [ ] Register profile tools automatically during `Session` construction
- [ ] Keep `RegisterTool` override semantics (custom tools override defaults)
- [ ] Remove dependency on external/manual wiring for baseline profile tools

**Definition of Done:**
- Sessions created directly from `CodingAgent` can execute profile tool calls without host-side manual registration
- Custom tool collision behavior remains latest-wins

### Task 2.2: Enforce JSON Schema argument validation before tool execution
**Spec:** 3.8 pipeline step 2, 9.3
**Files:** `src/CodingAgent/ToolRegistry.fs`

- [ ] Parse tool-call argument JSON before dispatch
- [ ] Validate against tool `parameters` JSON schema (root object, required fields, type checks)
- [ ] Return `is_error=true` tool results on validation failure (no hard exception)
- [ ] Include actionable error text for model recovery

**Definition of Done:**
- Invalid args are surfaced as tool errors, not runtime crashes
- Valid args still pass through unchanged

### Task 2.3: Respect profile parallel-tool capability in Session loop
**Spec:** 2.5, 9.3
**Files:** `src/CodingAgent/Session.fs`

- [ ] Replace per-call sequential dispatch in `Session.ProcessInput` with `DispatchAll`
- [ ] Use `profile.SupportsParallelToolCalls` and tool-call count to gate concurrency
- [ ] Preserve result ordering and truncation behavior

**Definition of Done:**
- Multi-tool round executes in parallel when supported
- Output ordering remains deterministic

## Phase 3: Subagents End-to-End

### Task 3.1: Wire subagent executors into real tool dispatch
**Spec:** 7.2, 7.3, 9.9
**Files:** `src/CodingAgent/Session.fs`, `src/CodingAgent/SubAgent.fs`, `src/CodingAgent/ProviderProfile.fs`

- [ ] Implement executable handlers for:
  - [ ] `spawn_agent`
  - [ ] `send_input`
  - [ ] `wait`
  - [ ] `close_agent`
- [ ] Maintain active subagent map on parent session
- [ ] Return serialized `SubAgentResult` payloads compatible with tool-result messages

**Definition of Done:**
- Parent can spawn, communicate with, await, and close subagents through normal tool calls
- No unknown-tool errors for subagent tool names

### Task 3.2: Enforce depth limits and cleanup semantics
**Spec:** 7.3, 9.9
**Files:** `src/CodingAgent/SubAgent.fs`, `src/CodingAgent/Session.fs`

- [ ] Enforce `MaxSubagentDepth` consistently across nested spawn attempts
- [ ] Ensure parent `Close` / `Abort` closes active subagents
- [ ] Ensure subagent status transitions are reflected in tool results

**Definition of Done:**
- Recursive spawn beyond depth limit fails gracefully
- No orphan subagent sessions on parent shutdown

## Phase 4: Execution Environment Timeout and Process Semantics

### Task 4.1: Implement graceful timeout kill sequence
**Spec:** 4.2, 5.4, 9.4, Appendix B
**Files:** `src/CodingAgent/ExecutionEnvironment.fs`

- [ ] Execute shell commands in killable process groups
- [ ] On timeout: send SIGTERM, wait 2s, then SIGKILL if still alive
- [ ] Preserve partial stdout/stderr in timeout result
- [ ] Keep platform-aware handling (Linux/macOS/Windows)

**Definition of Done:**
- Timeout behavior follows spec sequence
- Timed-out command results include captured partial output and timeout metadata

### Task 4.2: Enforce timeout bounds and override behavior
**Spec:** 2.2, 5.4, 9.4
**Files:** `src/CodingAgent/Session.fs`, `src/CodingAgent/ExecutionEnvironment.fs`

- [ ] Clamp per-call `timeout_ms` to `MaxCommandTimeoutMs`
- [ ] Use `DefaultCommandTimeoutMs` when unset
- [ ] Return a clear timeout guidance message for model retries with larger timeout

**Definition of Done:**
- Timeout configuration is predictable and bounded
- Existing shell tool behavior remains compatible

## Phase 5: System Prompt and Context Awareness

### Task 5.1: Add required git context block and layered discovery rules
**Spec:** 6.1-6.5, 9.8
**Files:** `src/CodingAgent/SystemPrompt.fs`, `src/CodingAgent/ProviderProfile.fs`

- [ ] Enrich environment context with:
  - [ ] git-repo detection
  - [ ] current branch
  - [ ] short status summary
  - [ ] recent commit subjects
- [ ] Update project-doc discovery:
  - [ ] walk from git root to working directory
  - [ ] append deeper docs with higher precedence
  - [ ] enforce 32KB budget with truncation marker

**Definition of Done:**
- System prompt contains required environment + git context
- Provider-specific doc loading obeys precedence and file filtering rules

### Task 5.2: Add context-window usage warnings
**Spec:** 5.5, 9.11
**Files:** `src/CodingAgent/Session.fs`

- [ ] Approximate context token usage (`chars / 4`)
- [ ] Emit `Warning` event when usage exceeds 80% of profile context window
- [ ] Include percentage and model/context-window metadata in event payload

**Definition of Done:**
- Long sessions emit warning events before provider hard failures
- No automatic compaction (informational only, per spec)

## Phase 6: Provider Parity and Profile Completeness

### Task 6.1: Bring Gemini baseline profile to declared tool parity
**Spec:** 3.6, 9.2
**Files:** `src/CodingAgent/ProviderProfile.fs`, `src/CodingAgent/ExecutionEnvironment.fs`

- [ ] Add Gemini-parity tools missing from current profile baseline:
  - [ ] `read_many_files`
  - [ ] `list_dir`
- [ ] Implement executors and schemas for new tools
- [ ] Keep optional web tools behind explicit opt-in (provider options / host policy)

**Definition of Done:**
- Gemini profile tool list matches sprint target parity baseline
- New tools are executable in-session, not definition-only

## Phase 7: Conformance and Regression Coverage

### Task 7.1: Expand CodingAgent unit tests to cover all new behavior
**Spec:** 9.1-9.11
**Files:** `tests/CodingAgent.Tests/Tests.fs`

- [ ] Add tests for lifecycle transitions and event emission ordering
- [ ] Add tests for schema validation success/failure paths
- [ ] Add tests for subagent tool lifecycle and depth cap
- [ ] Add timeout-kill behavior tests (with deterministic harness where possible)
- [ ] Add context-window warning event tests

### Task 7.2: Add/expand integration tests for Attractor coding_agent handler compatibility
**Spec:** 9.12, 9.13
**Files:** `tests/Attractor.Tests/Tests.fs`, `conformance/08-coding-agent/*`

- [ ] Verify coding_agent handler works without manual profile tool backfilling
- [ ] Verify subagent tool path through coding_agent node
- [ ] Ensure no regressions in existing coding agent conformance scenarios

**Definition of Done (Phase 7):**
- All existing CodingAgent + Attractor tests pass
- New test coverage explicitly maps to DoD items 9.1-9.11

## Sprint Summary

| Phase | Tasks | Priority | Estimated Effort |
|-------|-------|----------|------------------|
| 1. Lifecycle & Events | 1.1-1.3 | Critical | 3 tasks |
| 2. Tool Dispatch Correctness | 2.1-2.3 | Critical | 3 tasks |
| 3. Subagents | 3.1-3.2 | Critical | 2 tasks |
| 4. Execution Environment | 4.1-4.2 | High | 2 tasks |
| 5. Prompt & Context Awareness | 5.1-5.2 | High | 2 tasks |
| 6. Provider Parity | 6.1 | Medium | 1 task |
| 7. Conformance & Regression | 7.1-7.2 | Critical | 2 tasks |
| **Total** | **15 tasks** | | |

## Dependency Order

```text
Phase 1 + Phase 2 -> Phase 3
Phase 2 -> Phase 6
Phase 4 + Phase 5 -> Phase 7
Phase 3 + Phase 6 -> Phase 7
```

## Key Files

| File | Planned Changes |
|------|-----------------|
| `src/CodingAgent/Session.fs` | lifecycle states, events, dispatch flow, context warning |
| `src/CodingAgent/Types.fs` | state/event enums and event payload support |
| `src/CodingAgent/ToolRegistry.fs` | argument validation and dispatch behavior |
| `src/CodingAgent/SubAgent.fs` | full subagent lifecycle execution wiring |
| `src/CodingAgent/ExecutionEnvironment.fs` | graceful timeout kill semantics |
| `src/CodingAgent/SystemPrompt.fs` | project-doc traversal + git-aware prompt context |
| `src/CodingAgent/ProviderProfile.fs` | profile parity and richer prompt baselines |
| `src/Attractor/Handlers.fs` | integration adjustments for coding_agent handler |
| `tests/CodingAgent.Tests/Tests.fs` | DoD-aligned feature tests |
| `tests/Attractor.Tests/Tests.fs` | integration regression coverage |

## Spec Reference

All work items trace to `coding-agent-loop-spec.md`:
- Section 2: Session lifecycle, loop behavior, events, steering, stop conditions
- Section 3: Provider profiles, tool registry pipeline, custom tool precedence
- Section 4: Local execution environment requirements
- Section 5: Truncation ordering and context-window warnings
- Section 6: System prompt layering + git/project-doc context
- Section 7: Subagent lifecycle and depth limiting
- Section 9: Definition of Done validation matrix
