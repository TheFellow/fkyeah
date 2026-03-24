#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should complete"

# The gen tool outputs 5000 'X' chars.
# The edge gen->sink has fidelity="Compact".
# The sink stage should receive a context.json with compact-projected values.
context_file="$LOGS_DIR/sink/context.json"
assert_file_exists "$context_file" "sink stage context.json artifact exists"

# Verify context values stay within the current compact budget (~3200 chars)
max_val_len=$(python3 -c "
import json, sys
with open('$context_file') as f:
    ctx = json.load(f)
vals = [v for v in ctx.values() if isinstance(v, str)]
print(max(len(v) for v in vals) if vals else 0)
")

if [ "$max_val_len" -gt 3200 ]; then
    fail "Fidelity Compact: context value length $max_val_len exceeds compact limit 3200"
fi

pass "Fidelity Compact: downstream context values are compacted"
