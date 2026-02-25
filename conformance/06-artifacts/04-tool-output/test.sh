#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

assert_dir_exists "$LOGS_DIR/tool" "tool stage dir"
assert_file_exists "$LOGS_DIR/tool/tool_output.txt" "tool_output.txt"

pass "tool_output.txt written for tool node"
