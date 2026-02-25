#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../../lib.sh"
setup

require_env ANTHROPIC_API_KEY
export EXTRACT_RUN="$(cd "$(dirname "$TEST_DIR")/.." && pwd)/extract-run.sh"

run_pipeline_live "$TEST_DIR/pipeline.dot"
if [[ "$PIPELINE_EXIT" -ne 0 ]]; then
    if echo "$PIPELINE_STDERR" | grep -qiE "does not exist|not supported|not a chat model|model_not_found|not found"; then
        skip "claude-opus-4-6: model not available"
    fi
fi
assert_exit_code 0 "$PIPELINE_EXIT"
assert_file_exists "$LOGS_DIR/code/response.md" "LLM generated code"
assert_node_outcome "$LOGS_DIR" "code" "success"
assert_file_exists "$LOGS_DIR/run/tool_output.txt" "program produced output"

output="$(cat "$LOGS_DIR/run/tool_output.txt")"
assert_contains "$output" "!dlroW ,olleH" "output contains !dlroW ,olleH"

pass "claude-opus-4-6: Go reverse-string generated and validated"
