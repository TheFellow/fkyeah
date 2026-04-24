#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$VALIDATE_EXIT" "validation should succeed"
combined="$VALIDATE_STDOUT$VALIDATE_STDERR"
assert_not_contains "$combined" "scope_gate_coverage" "scope gate coverage should not warn"
assert_not_contains "$combined" "partial_commit_needs_build_gate" "partial commit build gate should not warn"
pass "scope gate coverage satisfied"
