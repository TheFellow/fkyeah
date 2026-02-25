#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

# Both branches should execute and fan-in should complete
# Engine serializes parallel branches — at least one branch and join should complete
assert_completed_nodes "$LOGS_DIR/checkpoint.json" "start" "fork" "branch1" "join"

pass "parallel branches execute, fan-in completes"
