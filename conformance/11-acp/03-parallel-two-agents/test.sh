#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "parallel ACP pipeline should succeed"
assert_node_outcome "$LOGS_DIR" "agent_a" "success"
assert_node_outcome "$LOGS_DIR" "agent_b" "success"
assert_file_exists "$LOGS_DIR/checkpoint.json" "checkpoint artifact"
assert_completed_nodes "$LOGS_DIR/checkpoint.json" "join"
assert_json_field "$LOGS_DIR/checkpoint.json" '.context["parallel.success_count"]' "2" "parallel success count"

pass "ACP parallel two agents"
