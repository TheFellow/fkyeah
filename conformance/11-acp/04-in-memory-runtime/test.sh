#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "in-memory ACP pipeline should succeed"
assert_node_outcome "$LOGS_DIR" "mem_task" "success"
assert_json_field_exists "$LOGS_DIR/mem_task/status.json" '.context_updates["acp.session_id.mem_task"]' "session id context"

pass "ACP in-memory runtime"
