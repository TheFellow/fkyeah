#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should complete"

# The gen tool outputs 1000 'X' chars which gets stored in context as tool output.
# The edge gen->sink has fidelity="truncate" (default limit 500 chars).
# The sink stage should receive a context.json with truncated values.
context_file="$LOGS_DIR/sink/context.json"
assert_file_exists "$context_file" "sink stage context.json artifact exists"

# Verify no value in context.json exceeds 500 chars (truncation applied)
max_val_len=$(python3 -c "
import json, sys
with open('$context_file') as f:
    ctx = json.load(f)
print(max(len(v) for v in ctx.values()) if ctx else 0)
")

if [ "$max_val_len" -gt 500 ]; then
    fail "A3 fidelity projection: context value length $max_val_len exceeds truncation limit 500"
fi

pass "A3 fidelity projection: downstream context values are truncated"
