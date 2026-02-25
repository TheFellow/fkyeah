# Sprint 003: Attractor Spec Conformance

## Overview

The Attractor engine is the pipeline orchestrator — it parses DOT files, validates graphs, executes nodes through handlers, manages context, and supports checkpoint/resume. An audit against attractor-spec.md shows the core execution is solid (~80%) but several spec features are unimplemented: artifact store for large outputs, tool hooks for pre/post-execution, context fidelity degradation on resume, CWD control, node visit caps for cycle safety, log file versioning, and outcome detection from LLM response content. This sprint also addresses the 5 issues from the sprint-exec failure analysis.

## Use Cases

1. **CWD control**: A pipeline targeting a different repo sets `cwd="/path/to/project"` as a graph attribute. All tool nodes and coding agent nodes execute in that directory.
2. **Outcome detection**: An LLM node responds with "BLOCKED — I cannot proceed." The engine detects this via `outcome_fail_pattern` and routes to the failure edge instead of treating it as success.
3. **Cycle safety**: A review→implement→test loop runs 10 times with no progress. The engine hits the `max_visits` cap and fails gracefully instead of burning tokens forever.
4. **Log versioning**: Debug a 5-iteration loop by reading `implement/001/response.md` through `implement/005/response.md` instead of only seeing the last overwrite.
5. **Tool hooks**: Before every shell command, a pre-hook logs the command; after every LLM call, a post-hook validates output format.

## Implementation Plan

### Phase 1: CWD Control (~15%)

**Spec:** Failure analysis issue #1
**Files:** `src/Attractor/Types.fs`, `src/Attractor/Handlers.fs`, `src/Attractor/Engine.fs`

- [ ] Add `cwd` to recognized graph attributes in Types.fs
- [ ] Add `Graph.GetGraphAttrString("cwd", "")` accessor
- [ ] ToolHandler: use `cwd` (node → graph → process CWD) for ProcessStartInfo.WorkingDirectory
- [ ] CodingAgentHandler: already uses cwd chain — verify graph-level cwd works
- [ ] Environment variable: set `ATTRACTOR_CWD` for tool nodes

**Definition of Done:**
- Pipeline with `graph [cwd="/tmp/test"]` → tool commands execute in /tmp/test
- Unit test: tool node with graph cwd attribute writes file to correct directory
- Conformance test: pipeline with cwd attribute creates files in specified directory

### Phase 2: Outcome Detection from Response (~15%)

**Spec:** Failure analysis issue #3
**Files:** `src/Attractor/Types.fs`, `src/Attractor/Handlers.fs`

- [ ] Add `outcome_fail_pattern` node attribute (regex or substring list)
- [ ] In CodergenHandler: after receiving LLM response, check against fail pattern
- [ ] If matched: set outcome to Fail with descriptive notes
- [ ] In CodingAgentHandler: same check on final response text
- [ ] Support pipe-separated patterns: `outcome_fail_pattern="BLOCKED|CANNOT_PROCEED|FATAL"`

**Definition of Done:**
- Node with `outcome_fail_pattern="BLOCKED"` + LLM response containing "BLOCKED" → Outcome.Fail
- Node without pattern → existing behavior unchanged
- Unit test with mock adapter returning "BLOCKED" → fail outcome
- Unit test with mock adapter returning normal text → success outcome

### Phase 3: Node Visit Cap for Cycle Safety (~15%)

**Spec:** Failure analysis issue #4
**Files:** `src/Attractor/Engine.fs`, `src/Attractor/Types.fs`

- [ ] Track visit count per node in engine execution loop (Dictionary<string, int>)
- [ ] Add `max_visits` node attribute (default: 50, matching default_max_retry)
- [ ] Add `max_cycle_iterations` graph attribute (default: 50)
- [ ] When a node exceeds its visit limit: Outcome.Fail with descriptive message
- [ ] Visit counts survive loop_restart (they track total, not per-restart)

**Definition of Done:**
- Pipeline with a cycle: node visited > max_visits → pipeline fails gracefully
- Default 50 visits allows reasonable work but prevents runaway
- Unit test: cycle with max_visits=3 → fails after 3rd visit
- Existing loop_restart tests still pass (loop_restart cap is separate mechanism)

### Phase 4: Log File Versioning (~10%)

**Spec:** Failure analysis issue #5
**Files:** `src/Attractor/Handlers.fs`

- [ ] Track execution count per node (from engine's visit count)
- [ ] When writing node artifacts (prompt.md, response.md, status.json, tool_output.txt):
  - First visit: write to `{node_id}/prompt.md` (unchanged)
  - Subsequent visits: write to `{node_id}/{visit_number}/prompt.md`
  - Also copy to `{node_id}/prompt.md` (latest always at root for backward compat)
- [ ] Pass visit count from engine to handler via context or parameter

**Definition of Done:**
- Node executed 3 times → directories `{node_id}/001/`, `{node_id}/002/`, `{node_id}/003/` exist
- Latest artifacts also at `{node_id}/` root for backward compatibility
- Unit test: handler called 3 times → all 3 response.md files preserved

### Phase 5: Artifact Store (~10%)

**Spec:** 5.5
**Files:** `src/Attractor/Types.fs`, `src/Attractor/Engine.fs` (new module or extend Context)

- [ ] Define artifact store interface: Store(key, data), Retrieve(key) → data
- [ ] File-backed implementation: write to `{logsRoot}/.artifacts/{key}`
- [ ] Context integration: large values (>10KB) automatically offloaded to artifact store
- [ ] Context.Get returns the value transparently (loads from file if offloaded)
- [ ] Artifact references in context use `artifact:{key}` prefix

**Definition of Done:**
- Context value > 10KB → stored as file, context holds reference
- Context.Get("key") returns full value regardless of storage location
- Unit test: set large value → snapshot shows reference → get returns full value

### Phase 6: Tool Hooks (~10%)

**Spec:** 9.7
**Files:** `src/Attractor/Types.fs`, `src/Attractor/Handlers.fs`

- [ ] Add `tool_hooks.pre` node attribute: shell command to run before handler execution
- [ ] Add `tool_hooks.post` node attribute: shell command to run after handler execution
- [ ] Pre-hook receives: node_id, handler_type, prompt (via env vars)
- [ ] Post-hook receives: node_id, handler_type, outcome, response_file (via env vars)
- [ ] Hook failure (non-zero exit) → log warning but don't fail the node

**Definition of Done:**
- Node with `tool_hooks.pre="echo PRE >> /tmp/hooks.log"` → log file contains "PRE" before execution
- Node with `tool_hooks.post="echo POST >> /tmp/hooks.log"` → log file contains "POST" after execution
- Hook failure doesn't crash the pipeline
- Unit test: verify pre/post hooks execute in correct order

### Phase 7: Context Fidelity on Resume (~5%)

**Spec:** 5.3
**Files:** `src/Attractor/Engine.fs`

- [ ] On checkpoint resume, degrade context fidelity to `summary:high` for the first resumed node
- [ ] After first resumed node completes, restore normal fidelity

**Definition of Done:**
- Resume from checkpoint → first node's context projection is SummaryHigh
- Second node after resume → normal fidelity
- Unit test: save checkpoint, resume, verify fidelity mode of first execution

### Phase 8: CLI Polish (~10%)

**Spec:** General
**Files:** `src/Attractor.Cli/Program.fs`

- [ ] Proper `--help` flag with usage text
- [ ] `--version` flag
- [ ] `--cwd <dir>` CLI flag as alternative to graph attribute
- [ ] Improve error messages for missing API keys
- [ ] Exit code conventions: 0=success, 1=pipeline failure, 2=validation error, 3=config error

**Definition of Done:**
- `attractor --help` prints usage
- `attractor --version` prints version
- `attractor --cwd /path pipeline.dot` works
- Exit codes documented and consistent

## Files Summary

| File | Action | Purpose |
|------|--------|---------|
| `src/Attractor/Engine.fs` | Modify | Visit tracking, cycle caps, resume fidelity, artifact store |
| `src/Attractor/Handlers.fs` | Modify | CWD resolution, outcome detection, log versioning, tool hooks |
| `src/Attractor/Types.fs` | Modify | New attributes (cwd, max_visits, outcome_fail_pattern, tool_hooks) |
| `src/Attractor/Validation.fs` | Modify | Validate new attributes |
| `src/Attractor.Cli/Program.fs` | Modify | CLI flags, --help, --version, --cwd |
| `tests/Attractor.Tests/Tests.fs` | Modify | All new unit tests |
| `conformance/` | Add | New conformance tests for cwd, cycle caps, log versioning |

## Definition of Done

- [ ] All attractor-spec.md Section 11 DoD items pass
- [ ] CWD graph attribute works for tool and coding_agent nodes
- [ ] outcome_fail_pattern detects soft failures from LLM responses
- [ ] Node visit cap prevents infinite loops in normal graph cycles
- [ ] Log files versioned per node visit
- [ ] Artifact store offloads large context values
- [ ] Tool hooks execute pre/post node handlers
- [ ] Context degrades to summary:high on first resumed node
- [ ] CLI has --help, --version, --cwd flags
- [ ] Existing tests pass: 170+ Attractor unit tests, 123 conformance tests
- [ ] `make test` passes
- [ ] `make conformance` passes

## Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Log versioning breaks checkpoint resume | Medium | High | Version directories created alongside root files, not instead of |
| Visit cap too low breaks legitimate long pipelines | Low | Medium | Default 50 is generous; configurable per-node |
| Artifact store adds latency | Low | Low | Only triggers for values >10KB; file I/O is fast |
| Tool hooks security risk (arbitrary shell execution) | Medium | Medium | Hooks run in same security context as tool commands; document risk |

## Dependencies

- SPRINT-001 (UnifiedLlm): Streaming support benefits codergen handler
- SPRINT-002 (CodingAgent): Profile tool auto-registration simplifies CodingAgentHandler
