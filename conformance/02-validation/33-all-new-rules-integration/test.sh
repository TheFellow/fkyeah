#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$VALIDATE_EXIT" "validation should succeed with warnings/infos"
combined="$VALIDATE_STDOUT$VALIDATE_STDERR"
assert_contains "$combined" "loop_session_pollution"
assert_contains "$combined" "scope_gate_coverage"
assert_contains "$combined" "partial_commit_needs_build_gate"
assert_contains "$combined" "parallelogram_needs_timeout"
assert_contains "$combined" "validate_measure_only"
assert_contains "$combined" "review_gate_first_line_strict"
assert_contains "$combined" "scratch_path_consistency"
assert_contains "$combined" "terminal_exit_on_empty_backlog"
pass "integration fixture triggers all sprint-014 rules"
