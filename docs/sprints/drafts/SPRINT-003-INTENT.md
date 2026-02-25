# Sprint 003 Intent: Attractor Spec Conformance

## Seed

Close the gaps between attractor-spec.md and the Attractor engine implementation. The engine is ~80% complete but missing artifact store, tool hooks, context fidelity degradation on resume, HTTP server mode, and CLI polish.

## Context

The Attractor engine (src/Attractor/) implements a DOT-based AI pipeline runner with full parsing, validation, execution, checkpoint/resume, model stylesheets, parallel execution, and 10 handler types including the newly integrated CodingAgent handler. It passes 123 conformance tests (45 deterministic + 72 model smoke + 6 coding agent).

An audit against attractor-spec.md reveals the following gaps:

### Critical Gaps
1. **Artifact store** (spec 5.5) — file-backed storage for large outputs, not implemented
2. **Tool hooks** (spec 9.7) — `tool_hooks.pre` and `tool_hooks.post` attributes for pre/post-execution hooks
3. **Context fidelity degradation on resume** (spec 5.3) — first resumed node should degrade to `summary:high`
4. **CWD control** (identified in sprint-exec failure analysis) — no `cwd` graph attribute for pipeline-wide working directory

### High Gaps
5. **HTTP server mode** (spec 9.5) — REST endpoints for web-based pipeline management
6. **Node visit tracking for cycle caps** (failure analysis issue #4) — unbounded normal graph cycles
7. **Log file versioning** (failure analysis issue #5) — node artifacts overwritten on re-execution
8. **Outcome detection from response** (failure analysis issue #3) — LLM "BLOCKED" response treated as success

### Medium Gaps
9. CLI argument parsing — manual string parsing, needs proper library
10. `--help`, `--version` flags missing
11. In-code XML documentation for public APIs

## Recent Sprint Context

- **SPRINT-001**: UnifiedLlm Spec Conformance — LLM client layer improvements (in progress)
- **SPRINT-002**: Coding Agent Loop Spec Conformance — CodingAgent library completeness
- Today's work: CodingAgent handler integration, tool-use API for all adapters, gpt-5.3-codex model support

## Relevant Codebase Areas

| File | Role |
|------|------|
| `src/Attractor/Engine.fs` | Core execution loop, checkpoint, resume |
| `src/Attractor/Handlers.fs` | All node handlers including CodingAgent |
| `src/Attractor/Types.fs` | Node, Edge, Graph, Context, ShapeMapping |
| `src/Attractor/Validation.fs` | Lint rules and synopsis |
| `src/Attractor/Events.fs` | Pipeline event system |
| `src/Attractor.Cli/Program.fs` | CLI entry point and LLM backend |
| `tests/Attractor.Tests/Tests.fs` | 170 existing unit tests |
| `conformance/` | 123 conformance tests |

## Constraints

- Must not break existing 170 unit tests or 123 conformance tests
- CWD control must work for both tool nodes and coding_agent nodes
- HTTP server mode should be optional (not required for CLI usage)
- Artifact store must be transparent to existing handlers

## Success Criteria

- All attractor-spec.md Definition of Done items (Section 11) pass
- CWD graph attribute works in sprint-exec pipeline
- Node visits capped to prevent infinite loops
- Log files versioned per execution iteration
- Outcome detection allows LLM to signal failure via response content

## Verification Strategy

- Unit tests for all new features
- Existing conformance suite must pass
- New conformance tests: CWD control, cycle cap, log versioning
- The sprint-exec pipeline from the failure analysis should run successfully

## Uncertainty Assessment

- Correctness uncertainty: **Low** — gaps are mechanical, well-defined by spec
- Scope uncertainty: **Medium** — HTTP server mode could expand significantly; recommend minimal viable endpoints
- Architecture uncertainty: **Low** — extends existing engine patterns

## Open Questions

1. HTTP server mode scope: full REST API or minimal SSE event stream?
2. Should artifact store use the filesystem (simple) or SQLite (queryable)?
3. For outcome_fail_pattern: regex or simple substring match?
4. Node visit cap: per-node attribute (`max_visits`) or graph-level (`max_cycle_iterations`)?
