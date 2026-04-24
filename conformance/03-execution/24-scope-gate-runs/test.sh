#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

rm -f "$TEST_DIR/scope_gate_marker.txt"

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should complete"

marker="$TEST_DIR/scope_gate_marker.txt"
assert_file_exists "$marker" "scope_gate marker should be written"
assert_contains "$(cat "$marker")" "scope_gate_ran" "scope_gate command should execute post-success"

rm -f "$marker"
pass "scope_gate command runs after successful primary handler"
