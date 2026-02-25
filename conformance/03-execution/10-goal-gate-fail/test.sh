#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"

# Pipeline should fail because goal gate is unsatisfied
assert_exit_code 1 "$PIPELINE_EXIT" "pipeline should fail"

combined="$PIPELINE_STDOUT$PIPELINE_STDERR"
assert_contains_regex "$combined" "(goal|gate|unsatisfied|Goal)" "error mentions goal gate"

pass "goal gate unsatisfied → pipeline fails"
