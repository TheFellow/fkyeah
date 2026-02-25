#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

# In simulate mode, LLM returns success, so diamond should route to success path
assert_completed_nodes "$LOGS_DIR/checkpoint.json" "start" "work" "check" "success"

pass "diamond routes on outcome condition"
