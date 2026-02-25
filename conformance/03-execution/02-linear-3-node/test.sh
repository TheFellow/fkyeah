#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

# Verify stages executed in order
assert_completed_nodes "$LOGS_DIR/checkpoint.json" "start" "step1" "step2"

# Verify artifacts written
assert_dir_exists "$LOGS_DIR/step1"
assert_dir_exists "$LOGS_DIR/step2"

pass "3-node linear chain: stages in order, artifacts written"
