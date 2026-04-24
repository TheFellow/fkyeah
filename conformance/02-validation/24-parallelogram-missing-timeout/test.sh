#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$VALIDATE_EXIT" "validation should succeed with warning"
combined="$VALIDATE_STDOUT$VALIDATE_STDERR"
assert_contains "$combined" "parallelogram_needs_timeout" "missing timeout warning emitted"
pass "parallelogram missing timeout"
