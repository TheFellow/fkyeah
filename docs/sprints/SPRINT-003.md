# Sprint 003: Attractor Spec Conformance

**Status:** Planned
**Spec:** `attractor-spec.md` Sections 2-11
**Codebase:** `src/Attractor/`, `src/Attractor.Cli/`

## Overview

The Attractor engine is the pipeline orchestrator — it parses DOT files, validates graphs, executes nodes through handlers, manages context, and supports checkpoint/resume. The core is ~80% complete, but an audit against attractor-spec.md reveals gaps in artifact storage, tool hooks, resume fidelity, CWD control, cycle safety, log versioning, outcome detection, retry behavior, HTTP server mode, and event coverage. This sprint addresses every gap including the 5 issues from the sprint-exec failure analysis.

## Use Cases

1. **CWD control**: A pipeline targeting a different repo sets `cwd="/path/to/project"`. Tool nodes and coding agent nodes execute there.
2. **Outcome detection**: An LLM responds "BLOCKED — cannot proceed." The engine detects this via `outcome_fail_pattern` and routes to the failure edge.
3. **Cycle safety**: A review→implement→test loop hits `max_visits` and fails gracefully instead of burning tokens.
4. **Log versioning**: Debug a 5-iteration loop by reading `implement/001/` through `implement/005/`.
5. **Tool hooks**: Pre/post hooks fire around individual LLM tool calls for logging, validation, or transformation.
6. **HTTP server**: Run a pipeline remotely via POST, observe events via SSE, cancel via API.

## Implementation Plan

### Phase 1: DOT Parser Qualified Keys

**Spec:** 2.2, 2.5
**Files:** `src/Attractor/DotParser.fs`

Prerequisite for tool hooks — `tool_hooks.pre` requires dotted key parsing.

- [ ] Update lexer to support qualified attribute keys with dots (e.g., `tool_hooks.pre`)
- [ ] Preserve existing unqualified key behavior
- [ ] Parser tests for dotted keys in graph, node, and edge attributes

**Definition of Done:**
- `node [tool_hooks.pre="echo pre"]` parses correctly
- Existing parser tests still pass
- Unit test: parse dotted key → attribute map contains `tool_hooks.pre`

### Phase 2: CWD Control for Tool Handler

**Spec:** Failure analysis issue #1
**Files:** `src/Attractor/Handlers.fs`

CodingAgentHandler already resolves CWD (node → graph → process). Apply same pattern to ToolHandler.

- [ ] ToolHandler: resolve CWD from node `cwd` → graph `cwd` → process CWD
- [ ] Set `ProcessStartInfo.WorkingDirectory` to resolved CWD
- [ ] Set `ATTRACTOR_CWD` environment variable for tool commands

**Definition of Done:**
- Pipeline with `graph [cwd="/tmp/test"]` → tool commands execute in /tmp/test
- Unit test: tool node with graph cwd writes file to correct directory

### Phase 3: Outcome Detection from Response

**Spec:** Failure analysis issue #3
**Files:** `src/Attractor/Handlers.fs`, `src/Attractor/Types.fs`

- [ ] Add `outcome_fail_pattern` node attribute (pipe-separated substrings)
- [ ] CodergenHandler: check response text against pattern; if matched, Outcome.Fail
- [ ] CodingAgentHandler: same check on final response text
- [ ] Support patterns like `outcome_fail_pattern="BLOCKED|CANNOT_PROCEED|FATAL"`

**Definition of Done:**
- Node with pattern + LLM response "BLOCKED" → Outcome.Fail with descriptive notes
- Node without pattern → existing behavior unchanged
- Unit tests for match and non-match cases

### Phase 4: Node Visit Cap and Engine Retry Fix

**Spec:** Failure analysis issue #4, 11.5
**Files:** `src/Attractor/Engine.fs`, `src/Attractor/Types.fs`

- [ ] Track per-node visit counts in engine (Dictionary<string, int>), reset on loop_restart
- [ ] Add `max_visits` node attribute (default: 50)
- [ ] When node exceeds max_visits: Outcome.Fail with descriptive message
- [ ] Fix engine retry: retry FAIL outcomes when retry policy configured (currently only retries RETRY status)
- [ ] Ensure failure routing priority: fail edge → retry_target → fallback_retry_target → fail pipeline

**Definition of Done:**
- Cycle with max_visits=3 → fails after 3rd visit
- Engine retries FAIL under retry policy
- Failure routing follows spec priority order
- Existing loop_restart tests still pass (visit counts per-restart, not cumulative)

### Phase 5: Log File Versioning

**Spec:** Failure analysis issue #5
**Files:** `src/Attractor/Handlers.fs`, `src/Attractor/Engine.fs`

- [ ] Track execution count per node from engine visit count
- [ ] First visit: write to `{node_id}/` (unchanged, backward compat)
- [ ] Subsequent visits: write to `{node_id}/{visit_number:03d}/` AND copy to `{node_id}/` root
- [ ] Pass visit count from engine to handler via context key `node.visit_count`

**Definition of Done:**
- Node executed 3 times → dirs `{node_id}/001/`, `{node_id}/002/`, `{node_id}/003/` exist
- Latest artifacts also at `{node_id}/` root
- Unit test: handler called 3 times → all 3 response.md files preserved

### Phase 6: Artifact Store

**Spec:** 5.5
**Files:** `src/Attractor/Types.fs`, `src/Attractor/Engine.fs` (or new `Artifacts.fs`)

- [ ] Define ArtifactStore: Store(key, data), Retrieve(key), Has(key), List(), Remove(key), Clear()
- [ ] File-backed implementation: `{logsRoot}/artifacts/{key}`
- [ ] Context integration: values >100KB auto-offloaded to artifact store
- [ ] Context.Get returns full value transparently (loads from file if offloaded)
- [ ] Artifact references in context use `artifact:{key}` prefix

**Definition of Done:**
- Context value >100KB → stored as file, context holds reference
- Context.Get("key") returns full value regardless of storage location
- Artifacts persist after process restart (file-backed)
- Unit test: set large value → snapshot shows reference → get returns full value

### Phase 7: Tool Hooks

**Spec:** 9.7
**Files:** `src/Attractor/Handlers.fs`, `src/Attractor/Engine.fs`

Tool hooks fire around individual LLM tool calls (not node boundaries). This matches spec 9.7.

- [ ] Add `tool_hooks.pre` and `tool_hooks.post` node attributes (requires Phase 1 parser)
- [ ] Pre-hook fires before each tool call dispatch with env vars: TOOL_NAME, TOOL_ARGS, NODE_ID
- [ ] Post-hook fires after each tool call with env vars: TOOL_NAME, TOOL_RESULT, EXIT_CODE, NODE_ID
- [ ] Hook failure (non-zero exit) → log warning, don't fail the node
- [ ] Apply to both ToolHandler and CodingAgentHandler tool dispatch

**Definition of Done:**
- Pre-hook fires before tool call, receives correct env vars
- Post-hook fires after, receives result
- Hook failure doesn't crash pipeline
- Unit test: verify hooks execute in correct order around tool calls

### Phase 8: Context Fidelity on Resume

**Spec:** 5.3
**Files:** `src/Attractor/Engine.fs`

- [ ] On checkpoint resume, degrade context fidelity to `summary:high` for first resumed node
- [ ] After first resumed node completes, restore normal fidelity resolution
- [ ] Record resume/degrade metadata in context for debugging

**Definition of Done:**
- Resume → first node's context projection is SummaryHigh
- Second node after resume → normal fidelity
- Unit test: save checkpoint, resume, verify fidelity

### Phase 9: HTTP Server Mode

**Spec:** 9.5
**Files:** new `src/Attractor.Server/` project or module in CLI

Full REST API per user interview decision.

- [ ] `POST /pipelines` — submit DOT source, start pipeline run
- [ ] `GET /pipelines/{id}` — get run status, current node, context
- [ ] `GET /pipelines/{id}/events` — SSE stream of pipeline events
- [ ] `POST /pipelines/{id}/cancel` — cancel a running pipeline
- [ ] `GET /pipelines/{id}/questions` — list pending human gate questions
- [ ] `POST /pipelines/{id}/questions/{qid}/answer` — answer a human gate question
- [ ] Back endpoints with existing engine/interviewer/event primitives
- [ ] Keep server mode optional (no regression for CLI-only usage)
- [ ] Add `attractor serve --port 8080` CLI command

**Definition of Done:**
- Pipeline can be submitted, observed, and cancelled over HTTP
- SSE stream delivers real-time events matching engine EventEmitter output
- Human gate questions can be answered via API
- Server mode does not affect CLI-only operation

### Phase 10: Event Coverage and CLI Polish

**Spec:** 9.6, general
**Files:** `src/Attractor/Events.fs`, `src/Attractor/Handlers.fs`, `src/Attractor.Cli/Program.fs`

- [ ] Emit ParallelStarted, ParallelBranchStarted, ParallelBranchCompleted, ParallelCompleted
- [ ] Emit InterviewStarted, InterviewCompleted, InterviewTimeout
- [ ] Emit CheckpointSaved with sufficient data for replay
- [ ] Replace manual CLI string parsing with proper argument parser
- [ ] Add `--version` flag
- [ ] Exit code conventions: 0=success, 1=pipeline failure, 2=validation error, 3=config error

**Definition of Done:**
- Event stream fully describes parallel and human-gate lifecycle
- `attractor --version` prints version string
- Exit codes documented and consistent

## Files Summary

| File | Action | Purpose |
|------|--------|---------|
| `src/Attractor/DotParser.fs` | Modify | Qualified key parsing |
| `src/Attractor/Engine.fs` | Modify | Visit tracking, retry fix, resume fidelity, artifact store |
| `src/Attractor/Handlers.fs` | Modify | CWD for ToolHandler, outcome detection, log versioning, tool hooks |
| `src/Attractor/Types.fs` | Modify | New attributes, ArtifactStore type |
| `src/Attractor/Events.fs` | Modify | Parallel/human event coverage |
| `src/Attractor/Validation.fs` | Modify | Validate new attributes |
| `src/Attractor.Cli/Program.fs` | Modify | CLI parser, --version, serve command |
| `src/Attractor.Server/` | Create | HTTP server mode (optional project) |
| `tests/Attractor.Tests/Tests.fs` | Modify | All new unit tests |
| `conformance/` | Add | New conformance tests |

## Definition of Done

- [ ] All attractor-spec.md Section 11 DoD items pass
- [ ] DOT parser supports qualified keys (tool_hooks.pre)
- [ ] CWD control works for both tool and coding_agent nodes
- [ ] outcome_fail_pattern detects soft failures
- [ ] Node visit cap prevents infinite loops
- [ ] Engine retries FAIL under retry policy
- [ ] Log files versioned per node visit
- [ ] Artifact store offloads values >100KB
- [ ] Tool hooks fire at tool-call boundaries
- [ ] Context degrades to summary:high on first resumed node
- [ ] HTTP server mode with full REST API
- [ ] Event stream covers parallel/human/checkpoint lifecycle
- [ ] CLI has --version and proper argument parser
- [ ] Existing tests pass; conformance 123/123
- [ ] `make test` passes
- [ ] `make conformance` passes

## Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| HTTP server scope creep | Medium | High | Minimal viable endpoints; expand in future sprint |
| Log versioning breaks checkpoint resume | Medium | High | Version dirs alongside root, not instead of |
| Tool hooks security (arbitrary shell) | Medium | Medium | Same security context as tool commands; document |
| Qualified key parsing ambiguity | Low | Medium | Only support single-level dots (tool_hooks.pre), not arbitrary nesting |

## Dependencies

- SPRINT-001 (UnifiedLlm): Streaming support
- SPRINT-002 (CodingAgent): Profile auto-registration simplifies CodingAgentHandler
