#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

# After step1, context.last_stage should be "step1"
assert_completed_nodes "$LOGS_DIR/checkpoint.json" "start" "step1" "check" "found"

pass "context.last_stage=step1 condition works"
