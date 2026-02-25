#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

# Unconditional edges should always be taken
assert_completed_nodes "$LOGS_DIR/checkpoint.json" "start" "work" "next"

pass "unconditional edges always taken"
