# Critique: SPRINT-003-CLAUDE-DRAFT (Codex)

## Overall Assessment

The draft addresses several practical failures (CWD, cycle caps, log versioning), but it misses multiple spec-critical conformance items and includes a few assumptions that conflict with current implementation reality.

## Findings

### Critical

1. **No parser fix for qualified keys before tool hooks**
   - The plan adds `tool_hooks.pre/post` behavior (Phase 6), but does not include mandatory DOT parser/lexer work for qualified keys.
   - Without this, `tool_hooks.pre` cannot be reliably expressed in DOT source.

2. **Tool-hook semantics are mis-scoped**
   - Phase 6 defines hooks around node handler execution (lines 105-109), while spec `9.7` defines pre/post hooks around LLM tool calls.
   - This is a semantic mismatch and will not satisfy conformance as written.

3. **Retry conformance gap is missing**
   - Current engine does not retry `FAIL` outcomes under retry policy, but this draft does not plan a fix.
   - Section `11.5` requires retry behavior coverage; this omission is a major miss.

4. **HTTP server mode (`9.5`) is absent from scope**
   - The draft does not include implementation work for server endpoints/SSE pipeline management.
   - This is explicitly listed as a high gap in sprint intent and in spec DoD scope.

### High

5. **CWD phase partially duplicates existing behavior**
   - The plan adds `Graph.GetGraphAttrString("cwd", "")` (line 23), but that accessor already exists.
   - CodingAgent handler already respects node/graph `cwd`; the real gap is consistency in tool-handler and broader execution surfaces.

6. **Artifact store details diverge from spec defaults**
   - Phase 5 uses `>10KB` and `/.artifacts/` (lines 91, 90), while spec `5.5` references 100KB threshold and `{logs_root}/artifacts/`.
   - Divergence is possible, but should be explicit and justified.

7. **CLI "proper --help" is already implemented**
   - Phase 8 includes `--help` as missing work (line 135), but current CLI already provides help output and flags.
   - This is not harmful, but it spends scope on already-complete items.

8. **Node-visit behavior across loop restart is asserted without spec support**
   - Line 59 says visit counts should survive `loop_restart`; spec does not require this and could conflict with restart semantics.

### Medium

9. **Resume degradation rule is simplified too far**
   - Phase 7 applies degradation on first resumed node universally; spec nuance is tied to inability to serialize in-memory session continuity (especially prior `full` fidelity usage).
   - Needs clearer rule definition and tests for both paths.

10. **Event completeness work is missing**
   - Spec event coverage (`9.6`) and related DoD expectations are not explicitly planned (parallel/human/checkpoint emission completeness).

11. **Outdated baseline metrics in DoD**
   - Test-count references are stale vs current repository totals.

## Recommended Adjustments

1. Add an early parser phase for qualified key support (`tool_hooks.pre/post`) before hook execution work.
2. Re-scope tool hooks to LLM tool-call boundaries, not generic node boundaries.
3. Add explicit engine retry fix for `FAIL` outcomes under retry policy.
4. Add a minimal HTTP server mode phase with endpoint contract + SSE events.
5. Rebaseline CWD scope on missing behavior only (tool handler and any remaining execution paths).
6. Align artifact store defaults with spec (or document intentional deviation with rationale).
7. Add explicit event coverage tasks for parallel/human flows and checkpoint observability.

## Verdict

Useful operational draft, but not yet spec-conformance complete. It should be revised before execution to avoid missing core DoD items.
