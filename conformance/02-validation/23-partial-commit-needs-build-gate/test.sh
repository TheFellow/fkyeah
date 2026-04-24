#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$VALIDATE_EXIT" "validation should succeed with warning"
combined="$VALIDATE_STDOUT$VALIDATE_STDERR"
assert_contains "$combined" "partial_commit_needs_build_gate" "partial commit needs build gate warning emitted"
pass "partial commit requires build gate"
