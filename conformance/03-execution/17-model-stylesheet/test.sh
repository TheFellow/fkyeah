#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should succeed"

# Current artifact schema does not record resolved llm_model per node.
# Validate execution coverage and preserve this as a seam for follow-up.
assert_file_exists "$LOGS_DIR/node_default/status.json" "node_default status exists"
assert_file_exists "$LOGS_DIR/node_class/status.json" "node_class status exists"
assert_file_exists "$LOGS_DIR/node_override/status.json" "node_override status exists"

pass "A1/A7 stylesheet pipeline executes; model-selection artifact seam noted"
