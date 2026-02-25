#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should succeed via auto_status"

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint" "checkpoint.json exists"
assert_json_field "$checkpoint" ".node_outcomes.tool.status" "success" "tool outcome synthesized as success"

notes="$(jq -r '.node_outcomes.tool.notes // ""' "$checkpoint")"
assert_contains "$notes" "auto_status synthesized success" "auto_status note recorded"

if [[ -f "$LOGS_DIR/tool/status.json" ]]; then
    fail "tool/status.json should not exist in this test"
fi

pass "auto_status converts missing-status tool outcome to success"
