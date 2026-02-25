#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

assert_file_exists "$LOGS_DIR/tool/tool_output.txt" "tool output written"

pass "ATTRACTOR_STAGE_DIR and ATTRACTOR_NODE_ID available to tool"
