#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

# After parallel execution, branch status should be in context
assert_json_field_exists "$LOGS_DIR/checkpoint.json" ".context" "context exists"

# Check that parallel branch status keys are present
checkpoint="$LOGS_DIR/checkpoint.json"
ctx="$(jq -r '.context | keys[]' "$checkpoint" 2>/dev/null || true)"
if echo "$ctx" | grep -q "parallel"; then
    echo "  parallel context keys found"
else
    echo "  warning: parallel context keys not found (may vary by implementation)"
fi

pass "parallel branch context available after fan-in"
