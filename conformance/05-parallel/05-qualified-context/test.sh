#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint"
assert_json_field "$checkpoint" '.context["parallel.fan_out.branch_a.tool.output"]' "A-output"
assert_json_field "$checkpoint" '.context["parallel.fan_out.branch_b.tool.output"]' "B-output"
assert_json_field "$checkpoint" '.context["parallel.fan_out.lanes"]' "alpha,beta"

raw_output="$(jq -r '.context["tool.output"]' "$checkpoint")"
if [[ "$raw_output" != "A-output" && "$raw_output" != "B-output" ]]; then
    echo "ASSERT FAILED: raw tool.output missing expected last-writer value" >&2
    exit 1
fi

pass "parallel qualified context keys preserved"
