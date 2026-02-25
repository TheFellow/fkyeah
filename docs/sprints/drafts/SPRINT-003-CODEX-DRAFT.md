# Sprint: Attractor Spec Conformance (Codex Draft)

**Status:** Draft
**Spec:** `attractor-spec.md` Sections 2-11
**Codebase:** `src/Attractor/`, `src/Attractor.Cli/`

## Overview

Attractor already has strong core coverage (DOT parsing, validation, engine traversal, checkpointing, handlers, coding-agent integration), but spec-critical gaps remain in state durability, extensibility hooks, and runtime safety.

Most important current deltas from spec:

1. Artifact store (`5.5`) is not implemented.
2. Tool hooks (`9.7`) are not implemented end-to-end.
3. Resume fidelity degradation (`5.3`) is missing.
4. Normal graph cycle protection/node visit caps are missing.
5. Stage log files are overwritten on node re-execution (no versioning).
6. Retry semantics do not retry `FAIL` outcomes under configured retry policy.
7. Qualified attribute parsing (e.g. `tool_hooks.pre`) is incomplete in DOT parser.
8. HTTP server mode (`9.5`) is missing.
9. CLI argument parsing is hand-rolled and missing `--version`/server-oriented ergonomics.

This sprint targets full conformance for those gaps while preserving current conformance suite behavior.

## Phase 1: Engine Correctness and Runtime Safety

### Task 1.1: Align retry behavior with spec (RETRY and FAIL)
**Spec:** 3.5, 3.6, 11.5
**Files:** `src/Attractor/Engine.fs`

- [ ] Retry `FAIL` outcomes when retry policy allows (not only `RETRY` status)
- [ ] Preserve fail-fast behavior for terminal/non-retryable failures
- [ ] Emit `StageRetrying` consistently for all retry attempts
- [ ] Preserve `allow_partial` semantics after exhaustion

**Definition of Done:**
- `max_retries` behavior matches spec for both `FAIL` and `RETRY`
- Existing retry tests continue to pass; new tests cover `FAIL` retry path

### Task 1.2: Add node visit caps for non-terminating cycles
**Spec:** 3.2, 3.8, 11.3
**Files:** `src/Attractor/Types.fs`, `src/Attractor/Engine.fs`

- [ ] Add graph-level and/or node-level visit cap attributes (implementation-defined, documented)
- [ ] Track per-node visit counts during run
- [ ] Fail deterministically when cap is exceeded with clear error message
- [ ] Include visit metadata in checkpoint/context for observability

**Definition of Done:**
- Infinite loop graphs terminate with actionable failure reason instead of hanging

### Task 1.3: Fix failure routing precedence and consistency
**Spec:** 3.7, 11.3
**Files:** `src/Attractor/Engine.fs`, `src/Attractor/Conditions.fs`

- [ ] Ensure failure routing follows priority: fail edge -> retry_target -> fallback_retry_target -> fail pipeline
- [ ] Keep edge-selection behavior deterministic under mixed conditions/weights
- [ ] Add regression coverage for ambiguous/multi-edge failure routes

**Definition of Done:**
- Failure routing is spec-aligned and test-covered

## Phase 2: Resume Fidelity and Checkpoint Semantics

### Task 2.1: Implement first-hop fidelity degradation on resume
**Spec:** 5.3, 5.4, 11.7
**Files:** `src/Attractor/Engine.fs`, `src/Attractor/Types.fs`

- [ ] When resuming from checkpoint, degrade the first resumed node context to `summary:high`
- [ ] After first resumed hop, restore normal fidelity resolution rules
- [ ] Record resume/degrade metadata in context for debugging

**Definition of Done:**
- Resume path is deterministic and explicitly degrades first resumed node context

### Task 2.2: Align fidelity defaults and precedence behavior
**Spec:** 5.4, 11.7
**Files:** `src/Attractor/Engine.fs`, `src/Attractor/Types.fs`

- [ ] Ensure fidelity fallback order is enforced exactly
- [ ] Reconcile default fidelity behavior with spec-defined default
- [ ] Add explicit tests for edge > node > graph > default precedence

**Definition of Done:**
- Fidelity resolution behavior is fully testable and spec-compliant

## Phase 3: Artifact Store and Log Versioning

### Task 3.1: Add ArtifactStore with file-backing threshold
**Spec:** 5.5, 5.6, 11.7
**Files:** `src/Attractor/Types.fs`, `src/Attractor/Engine.fs` (or new `src/Attractor/Artifacts.fs`)

- [ ] Implement `ArtifactStore` API (`store`, `retrieve`, `has`, `list`, `remove`, `clear`)
- [ ] Add in-memory vs file-backed behavior with default threshold (100KB)
- [ ] Persist file-backed artifacts under `{logs_root}/artifacts/`
- [ ] Expose artifact metadata in run result and events where applicable

**Definition of Done:**
- Large artifacts do not inflate context/checkpoint payloads
- Artifact retrieval works after process restart from filesystem

### Task 3.2: Version stage logs per execution iteration
**Spec:** 5.6, 11.3, 11.7
**Files:** `src/Attractor/Handlers.fs`, `src/Attractor/Engine.fs`

- [ ] Prevent overwrite of `prompt.md`, `response.md`, `status.json` on re-executed nodes
- [ ] Introduce deterministic per-attempt/stage versioning layout (e.g. attempt folders or suffixes)
- [ ] Ensure resume and loop-restart paths preserve historical artifacts

**Definition of Done:**
- Repeated node executions produce append-only artifact history

## Phase 4: DOT Parser and Tool Hook Extensibility

### Task 4.1: Fix qualified attribute key parsing (`tool_hooks.pre`)
**Spec:** 2.2, 2.5, 9.7, 11.1
**Files:** `src/Attractor/DotParser.fs`

- [ ] Update lexer/parser to support qualified keys with dots
- [ ] Preserve existing parsing behavior for unqualified keys
- [ ] Add parser tests for graph/node attributes with dotted keys

**Definition of Done:**
- `tool_hooks.pre` and `tool_hooks.post` parse into correct graph attributes

### Task 4.2: Implement tool pre/post hook execution
**Spec:** 9.7, 11.11
**Files:** `src/Attractor/Handlers.fs`, `src/Attractor/Engine.fs` (and/or coding-agent integration layer)

- [ ] Read hook commands from graph/node attrs (`tool_hooks.pre`, `tool_hooks.post`)
- [ ] Execute pre-hook before each LLM tool call with metadata payload/env
- [ ] Execute post-hook after each LLM tool call with result payload/env
- [ ] Record hook failures in logs without crashing pipeline
- [ ] Enforce pre-hook non-zero policy (skip/block tool call behavior) per sprint decision

**Definition of Done:**
- Hooks are observable, logged, and non-disruptive to core loop stability

## Phase 5: Working Directory and Execution Control

### Task 5.1: Make CWD control consistent across handlers
**Spec:** 2.6, 4.10, 11.6
**Files:** `src/Attractor/Handlers.fs`, `src/Attractor/Types.fs`

- [ ] Standardize graph-level and node-level `cwd` behavior for `tool` and `coding_agent` handlers
- [ ] Ensure relative paths resolve against effective CWD, not process cwd
- [ ] Validate nonexistent CWD early with clear diagnostics

**Definition of Done:**
- Pipeline-wide `cwd` works predictably for command and coding-agent stages

### Task 5.2: Add outcome fail-pattern support for codergen/coding-agent outputs
**Spec:** 3.7, 4.5, 11.3
**Files:** `src/Attractor/Handlers.fs`, `src/Attractor/Types.fs`

- [ ] Add configurable pattern matching for model responses that should force `FAIL`
- [ ] Detect blocked/refusal/failure sentinel outputs before marking stage success
- [ ] Store detection reason in `failure_reason` and `status.json`

**Definition of Done:**
- Response-level failures are routable through failure edges/retry targets

## Phase 6: HTTP Server Mode and CLI Modernization

### Task 6.1: Deliver minimal HTTP server mode for pipeline orchestration
**Spec:** 9.5, 11.11
**Files:** new server module under `src/Attractor/` or `src/Attractor.Server/`

- [ ] Implement minimal endpoints:
  - [ ] `POST /pipelines`
  - [ ] `GET /pipelines/{id}`
  - [ ] `GET /pipelines/{id}/events` (SSE)
  - [ ] `POST /pipelines/{id}/cancel`
  - [ ] `GET /pipelines/{id}/questions`
  - [ ] `POST /pipelines/{id}/questions/{qid}/answer`
- [ ] Back endpoints with existing engine/interviewer/event primitives
- [ ] Keep server mode optional (no regression for CLI-only usage)

**Definition of Done:**
- Pipelines can run and be observed/cancelled remotely over HTTP/SSE

### Task 6.2: Replace manual CLI parsing with proper option parser + `--version`
**Spec:** sprint intent (CLI polish), 11.11
**Files:** `src/Attractor.Cli/Program.fs`

- [ ] Move from manual `while`/`match` parsing to a CLI parser library
- [ ] Add `--version` output
- [ ] Keep `--help`, `--validate`, `--resume`, `--simulate`, `--quiet` behavior stable
- [ ] Add command surface for server mode startup

**Definition of Done:**
- CLI UX is robust, self-describing, and easier to extend

## Phase 7: Event Coverage and Conformance Expansion

### Task 7.1: Complete event emission coverage for parallel/human flows
**Spec:** 9.6, 11.3, 11.8
**Files:** `src/Attractor/Events.fs`, `src/Attractor/Handlers.fs`, `src/Attractor/Engine.fs`

- [ ] Emit `ParallelStarted`, `ParallelBranchStarted`, `ParallelBranchCompleted`, `ParallelCompleted`
- [ ] Emit `InterviewStarted`, `InterviewCompleted`, `InterviewTimeout`
- [ ] Ensure event payload data is sufficient for UI and replay

**Definition of Done:**
- Event stream fully describes branch and human-gate lifecycle

### Task 7.2: Add conformance tests for new spec-critical behaviors
**Spec:** 11.1-11.13
**Files:** `tests/Attractor.Tests/Tests.fs`, `conformance/*`

- [ ] Parser tests for dotted qualified attributes
- [ ] Engine tests for retry-on-fail, visit caps, resume degradation
- [ ] Artifact store and stage-versioning tests
- [ ] Tool hook execution tests
- [ ] HTTP server smoke tests (if server mode in scope)

**Definition of Done (Phase 7):**
- Existing tests remain green
- New tests directly map to unresolved DoD checkboxes

## Sprint Summary

| Phase | Tasks | Priority | Estimated Effort |
|-------|-------|----------|------------------|
| 1. Engine Safety | 1.1-1.3 | Critical | 3 tasks |
| 2. Resume Fidelity | 2.1-2.2 | High | 2 tasks |
| 3. Artifact & Logs | 3.1-3.2 | Critical | 2 tasks |
| 4. Parser + Hooks | 4.1-4.2 | Critical | 2 tasks |
| 5. Execution Control | 5.1-5.2 | High | 2 tasks |
| 6. HTTP + CLI | 6.1-6.2 | High | 2 tasks |
| 7. Events + Tests | 7.1-7.2 | Critical | 2 tasks |
| **Total** | **15 tasks** | | |

## Dependency Order

```text
Phase 4.1 -> Phase 4.2
Phase 1 + Phase 2 -> Phase 3
Phase 1 + Phase 4 + Phase 5 -> Phase 7
Phase 6 can proceed in parallel after Phase 1 baseline hardening
```

## Key Files

| File | Planned Changes |
|------|-----------------|
| `src/Attractor/DotParser.fs` | qualified attribute parsing support |
| `src/Attractor/Engine.fs` | retry semantics, visit caps, resume degradation, checkpoint behavior |
| `src/Attractor/Types.fs` | artifact/cycle/fidelity support types |
| `src/Attractor/Handlers.fs` | tool hooks, cwd parity, outcome fail-pattern logic, log versioning |
| `src/Attractor/Events.fs` | event coverage extensions and payload alignment |
| `src/Attractor/Validation.fs` | any new validation for added attributes/constraints |
| `src/Attractor.Cli/Program.fs` | parser refactor, `--version`, server-mode command plumbing |
| `tests/Attractor.Tests/Tests.fs` | DoD-aligned unit and integration tests |
| `conformance/` | behavior-level conformance additions |

## Spec Reference

All work items trace to `attractor-spec.md`:
- Section 2: DOT schema and qualified attributes
- Section 3: execution loop, retries, failure routing, cycle safety
- Section 4: handler contracts and behavior
- Section 5: checkpoint semantics, fidelity, artifact store
- Section 9: transforms/extensibility, HTTP mode, tool hooks, events
- Section 11: Definition of Done conformance targets
