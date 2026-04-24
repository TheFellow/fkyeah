#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

rm -f "$TEST_DIR/requires_build_marker.txt"

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 1 "$PIPELINE_EXIT" "pre-condition failure should fail the run"

if [[ -f "$TEST_DIR/requires_build_marker.txt" ]]; then
  fail "guarded tool command executed even though requires_green_build failed"
fi

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint" "checkpoint should exist"
assert_json_field "$checkpoint" '.context.tool_exit_code' "9" "pre-condition exit code captured"

pass "requires_green_build failure skips primary handler execution"
