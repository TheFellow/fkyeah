#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 1 "$VALIDATE_EXIT" "validation should fail"

combined="$VALIDATE_STDOUT$VALIDATE_STDERR"
assert_contains "$combined" "Msquare" "error mentions Msquare"

pass "missing exit node (Msquare) detected"
