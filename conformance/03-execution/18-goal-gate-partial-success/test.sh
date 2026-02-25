#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should succeed with goal_gate partial_success"

assert_file_exists "$LOGS_DIR/gated/status.json" "tool wrote status.json"
assert_json_field "$LOGS_DIR/checkpoint.json" ".node_outcomes.gated.status" "partial_success" "goal gate recorded partial_success"

pass "goal gate accepts partial_success outcomes"
