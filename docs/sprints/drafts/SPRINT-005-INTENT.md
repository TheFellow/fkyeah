# Sprint 005 Intent: Test Coverage Hardening

## Seed

Address the gaps identified in `docs/feedback.md` — a QA gap analysis comparing the three specs (attractor-spec.md, unified-llm-spec.md, coding-agent-loop-spec.md) against the existing 384 unit tests and 128 conformance tests.

## Context

All four prior sprints focused on building and conforming the three-layer stack:
- Sprint 001: UnifiedLlm spec conformance (105 unit tests)
- Sprint 002: CodingAgent loop spec conformance (95 unit tests)
- Sprint 003: Attractor spec conformance (184 unit tests)
- Sprint 004: Cross-spec gap closure — 30 gaps across all three layers (added conformance tests to reach 128)

The implementation is complete and all existing tests pass (`make test` = 384 tests, `make conformance` = 128 tests). The feedback document identifies 28 specific holes across 5 categories (A-E) with a prioritized recommendation table. This sprint writes tests only — no production code changes.

## Recent Sprint Context

**Sprint 002 (CodingAgent):** Built subagent tool executors, profile tool auto-registration, system prompt git context, event system completeness, SIGTERM/SIGKILL timeout handling, context-window awareness, Gemini tool parity. 7 phases, 9 files modified.

**Sprint 003 (Attractor):** Built DOT parser qualified keys, CWD control, outcome detection, max_visits, log versioning, artifact store, tool hooks, context fidelity on resume, HTTP server mode, event coverage. 10 phases, 10+ files.

**Sprint 004 (Cross-Spec Gap Closure):** Closed 30 remaining gaps: ModelInfo extensions, StreamAccumulator, StopCondition, error hierarchy, parallel tool dispatch, grep glob_filter, auto_status, loop_restart context reset, checkpoint NodeOutcomes, MultiSelect question types, manager loop supervision. 12 phases across all three layers.

## Relevant Codebase Areas

**Test files (primary targets):**
- `tests/Attractor.Tests/Tests.fs` — 184 tests across 24 modules
- `tests/UnifiedLlm.Tests/Tests.fs` — 105 tests across 14 modules
- `tests/CodingAgent.Tests/Tests.fs` — 95 tests across 20 modules

**Conformance suite (secondary targets):**
- `conformance/01-parsing/` — 9 tests
- `conformance/02-validation/` — 12 tests
- `conformance/03-execution/` — 16 tests (gap at 16-17)
- `conformance/04-context/` — 6 tests
- `conformance/05-parallel/` — 2 tests
- `conformance/06-artifacts/` — 5 tests
- `conformance/07-models/` — 72 tests (9 models x 8 scenarios)
- `conformance/08-coding-agent/` — 6 tests (3 models x 2 scenarios)

**Source modules (read-only for this sprint — reference for test design):**
- `src/UnifiedLlm/` — 11 modules (Client, Types, Generation, Errors, ModelCatalog, etc.)
- `src/CodingAgent/` — 9 modules (Session, ExecutionEnvironment, ProviderProfile, Truncation, etc.)
- `src/Attractor/` — 12 modules (Engine, DotParser, Validation, Handlers, Conditions, Stylesheet, etc.)

## Constraints

- **Tests only** — no production code changes; this is a pure QA sprint
- Must follow existing test patterns: xUnit `[<Fact>]`, double-backtick names, module grouping
- `make test` must pass after all changes (384 + N new tests)
- `make conformance` must pass after all changes (128 + N new tests)
- Conformance tests use `test.sh` + `pipeline.dot` + `README.md` convention
- Mock adapters for unit tests (no real API calls in unit tests)
- Conformance model/coding-agent tests may require API keys (use skip pattern)

## Success Criteria

1. Every P0 gap from `docs/feedback.md` has at least one test
2. Every P1 gap has at least one test
3. Most P2 gaps have at least one test (stretch goal)
4. No existing tests broken
5. `make test` and `make conformance` both pass green

## Verification Strategy

- **Unit tests:** Run `make test` — all new and existing tests pass
- **Conformance tests:** Run `make conformance` — all new and existing tests pass
- **Gap closure:** Cross-reference each new test against the specific gap ID (A1-A10, B1-B9, C1-C6, D1-D4) in feedback.md
- **Regression:** Verify existing 384 unit + 128 conformance tests unchanged

## Uncertainty Assessment

- Correctness uncertainty: **Low** — we're writing tests against existing, passing implementations
- Scope uncertainty: **Low** — the 28 gaps are enumerated with clear priorities
- Architecture uncertainty: **Low** — all test infrastructure (xUnit, conformance harness, mock adapters) already exists
- The main risk is discovering bugs in production code via new tests — those would be documented but not fixed in this sprint (tests would be `Skip`-annotated with a bug reference)

## Open Questions

1. Should we fix production bugs discovered by new tests in this sprint, or leave them as known-failing tests for a future sprint?
2. For conformance tests that require API keys (e.g., model-specific tests), should we add them even though they'll only run when keys are present?
3. How many P2 gaps should we attempt? All, or draw a line?
4. The feedback doc identifies apply_patch executor as P0 — but testing it may require production code if the executor is a stub. Should we implement the executor (crossing the "tests only" line) or just test the interface?
