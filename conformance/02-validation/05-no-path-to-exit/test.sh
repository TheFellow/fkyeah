#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 1 "$VALIDATE_EXIT" "validation should fail"

combined="$VALIDATE_STDOUT$VALIDATE_STDERR"
assert_contains_regex "$combined" "(terminal|exit|reach)" "error mentions unreachable terminal"

pass "nodes with no path to exit detected"
