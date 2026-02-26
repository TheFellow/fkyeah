#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should complete fan-in with partial branch failure"

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint" "checkpoint exists"
assert_json_field "$checkpoint" '.context["parallel.branch.ok.status"]' "success" "ok branch status"
assert_json_field "$checkpoint" '.context["parallel.branch.bad.status"]' "fail" "bad branch status"
assert_json_field_exists "$checkpoint" '.context["parallel.fail_count"]' "parallel fail count present"

pass "A6 parallel branch failure is captured while fan-in completes"
