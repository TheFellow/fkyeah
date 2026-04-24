#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 1 "$VALIDATE_EXIT" "validation should fail on conflicting session attributes"

combined="$VALIDATE_STDOUT$VALIDATE_STDERR"
assert_contains "$combined" "conflicting_session_attrs" "expected conflicting_session_attrs diagnostic"
assert_contains "$combined" "[ERROR]" "diagnostic should be error severity"

pass "conflicting_session_attrs emits error and fails validation"
