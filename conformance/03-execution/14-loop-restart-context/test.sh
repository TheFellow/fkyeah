#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should succeed after restart"

restart_checkpoint="$LOGS_DIR/restart-1/checkpoint.json"
assert_file_exists "$restart_checkpoint" "restart checkpoint exists"
assert_json_field "$restart_checkpoint" ".context.last_stage // \"\"" "" "last_stage cleared after loop_restart"
assert_completed_nodes "$restart_checkpoint" "check"

completed="$(jq -r '.completed_nodes[]' "$restart_checkpoint" 2>/dev/null || true)"
assert_not_contains "$completed" "leaked" "leaked branch was not taken"

pass "loop_restart uses fresh context with only graph.* keys"
