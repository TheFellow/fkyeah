#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

SCHEMA_EXIT=0
SCHEMA_STDOUT=""
SCHEMA_STDERR=""

stdout_file="$(mktemp "${TMPDIR:-/tmp}/att-schema-out.XXXXXX")"
stderr_file="$(mktemp "${TMPDIR:-/tmp}/att-schema-err.XXXXXX")"
set +e
"$ATTRACTOR_BIN" schema >"$stdout_file" 2>"$stderr_file"
SCHEMA_EXIT=$?
set -e
SCHEMA_STDOUT="$(cat "$stdout_file")"
SCHEMA_STDERR="$(cat "$stderr_file")"
rm -f "$stdout_file" "$stderr_file"

assert_exit_code 0 "$SCHEMA_EXIT" "schema command should succeed"
combined="$SCHEMA_STDOUT$SCHEMA_STDERR"
assert_contains "$combined" "loop_session_pollution"
assert_contains "$combined" "scope_gate_coverage"
assert_contains "$combined" "partial_commit_needs_build_gate"
assert_contains "$combined" "parallelogram_needs_timeout"
assert_contains "$combined" "validate_measure_only"
assert_contains "$combined" "review_gate_first_line_strict"
assert_contains "$combined" "scratch_path_consistency"
assert_contains "$combined" "terminal_exit_on_empty_backlog"
pass "schema lists all new sprint-014 rules"
