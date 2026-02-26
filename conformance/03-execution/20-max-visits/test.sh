#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 1 "$PIPELINE_EXIT" "pipeline should fail when max_visits is exceeded"
assert_contains "$PIPELINE_STDERR" "exceeded max_visits" "failure reason should mention max_visits"

pass "A8 max_visits enforces bounded loop visits"
