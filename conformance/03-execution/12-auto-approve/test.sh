#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

# --auto-approve should select the first option (Approve)
assert_completed_nodes "$LOGS_DIR/checkpoint.json" "start" "gate" "proceed"

pass "human gate auto-selects first option with --auto-approve"
