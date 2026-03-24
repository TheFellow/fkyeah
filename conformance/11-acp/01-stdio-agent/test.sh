#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "stdio ACP pipeline should succeed"
assert_node_outcome "$LOGS_DIR" "agent_task" "success"

assert_file_exists "$LOGS_DIR/agent_task/status.json" "agent status"
assert_file_exists "$LOGS_DIR/agent_task/response.md" "agent response"
assert_file_exists "$LOGS_DIR/agent_task/acp_session.json" "agent session artifact"
assert_json_field_exists "$LOGS_DIR/agent_task/status.json" '.context_updates["acp.output.agent_task"]' "acp output context"

pass "ACP stdio agent"
