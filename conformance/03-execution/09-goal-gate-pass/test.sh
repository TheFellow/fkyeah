#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

# In simulate mode, LLM node succeeds, so goal gate should be satisfied
assert_node_outcome "$LOGS_DIR" "gated" "success"

pass "goal gate satisfied → pipeline exits successfully"
