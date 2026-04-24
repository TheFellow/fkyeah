#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should complete"

output_file="$LOGS_DIR/EchoPrompt/tool_output.txt"
assert_file_exists "$output_file" "tool output artifact exists"
output="$(cat "$output_file")"

assert_contains "$output" '${foo}' "escaped interpolation should stay literal"
assert_contains "$output" '${context.missing}' "unresolved interpolation should stay literal"

pass "attribute interpolation escape rule preserves literal placeholders"
