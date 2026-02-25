#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 1 "$VALIDATE_EXIT" "validation should fail"

combined="$VALIDATE_STDOUT$VALIDATE_STDERR"
assert_contains_regex "$combined" "(exit node|terminal_node)" "error mentions exit-node rule"
assert_contains "$combined" "found 2" "error reports multiple exit nodes"

pass "multiple exit nodes are rejected"
