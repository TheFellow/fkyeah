#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../../lib.sh"
setup

require_env ANTHROPIC_API_KEY

WORKDIR="$LOGS_DIR/workdir"
mkdir -p "$WORKDIR"
RUNTIME_PIPELINE="$LOGS_DIR/pipeline.dot"
sed -e "s|__MODEL__|claude-sonnet-4-6|g" -e "s|__WORKDIR__|$WORKDIR|g" "$TEST_DIR/pipeline.dot" > "$RUNTIME_PIPELINE"

run_pipeline_live "$RUNTIME_PIPELINE"
if [[ "$PIPELINE_EXIT" -ne 0 ]]; then
    if echo "$PIPELINE_STDERR" | grep -qiE "does not exist|not supported|not a chat model|model_not_found|not found|INTERNAL|Internal error|RESOURCE_EXHAUSTED|rate limit|quota|overloaded|500|503"; then
        skip "claude-sonnet-4-6: model not available"
    fi
fi

assert_exit_code 0 "$PIPELINE_EXIT"
assert_file_exists "$LOGS_DIR/agent/response.md" "agent response exists"
assert_node_outcome "$LOGS_DIR" "agent" "success"
assert_file_exists "$WORKDIR/calculator/__init__.py" "calculator __init__ exists"
assert_file_exists "$WORKDIR/calculator/math_ops.py" "calculator math_ops exists"
assert_file_exists "$WORKDIR/test_calculator.py" "test_calculator exists"

signal=""
if [[ -f "$LOGS_DIR/agent/tool_output.txt" ]]; then
    signal="$signal\n$(cat "$LOGS_DIR/agent/tool_output.txt")"
fi
signal="$signal\n$(cat "$LOGS_DIR/agent/response.md")"
assert_contains "$signal" "ALL TESTS PASSED" "agent ran tests and reported success"

pass "claude-sonnet-4-6: coding agent file write scenario passed"
