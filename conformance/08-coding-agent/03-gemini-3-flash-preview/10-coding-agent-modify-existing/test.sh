#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../../lib.sh"
setup

require_env GEMINI_API_KEY

WORKDIR="$LOGS_DIR/workdir"
mkdir -p "$WORKDIR"
cat > "$WORKDIR/buggy.py" <<'PY'
def fibonacci(n):
    if n <= 0:
        return 0
    if n == 1:
        return 1
    return fibonacci(n - 1) + fibonacci(n - 3)  # Bug: should be n-2
PY

RUNTIME_PIPELINE="$LOGS_DIR/pipeline.dot"
sed -e "s|__MODEL__|gemini-3-flash-preview|g" -e "s|__WORKDIR__|$WORKDIR|g" "$TEST_DIR/pipeline.dot" > "$RUNTIME_PIPELINE"

run_pipeline_live "$RUNTIME_PIPELINE"
if [[ "$PIPELINE_EXIT" -ne 0 ]]; then
    if echo "$PIPELINE_STDERR" | grep -qiE "does not exist|not supported|not a chat model|model_not_found|not found|INTERNAL|Internal error|RESOURCE_EXHAUSTED|rate limit|quota|overloaded|500|503"; then
        skip "gemini-3-flash-preview: model not available"
    fi
fi

assert_exit_code 0 "$PIPELINE_EXIT"
assert_node_outcome "$LOGS_DIR" "agent" "success"
assert_file_exists "$WORKDIR/buggy.py" "buggy.py exists"

# Verify the fix behaviorally -- fibonacci(10) must equal 55
fix_check="$(python3 -c 'import sys; sys.path.insert(0, "'"$WORKDIR"'"); from buggy import fibonacci; assert fibonacci(10) == 55, f"Got {fibonacci(10)}"; print("FIX_VERIFIED")' 2>&1 || true)"
assert_contains "$fix_check" "FIX_VERIFIED" "fibonacci fix produces correct results"

signal=""
if [[ -f "$LOGS_DIR/agent/tool_output.txt" ]]; then
    signal="$signal\n$(cat "$LOGS_DIR/agent/tool_output.txt")"
fi
signal="$signal\n$(cat "$LOGS_DIR/agent/response.md")"
assert_contains "$signal" "FIBONACCI OK" "agent verified fix"

pass "gemini-3-flash-preview: coding agent modify-existing scenario passed"
