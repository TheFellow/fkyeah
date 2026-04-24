#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$VALIDATE_EXIT" "validation should succeed with info"
combined="$VALIDATE_STDOUT$VALIDATE_STDERR"
assert_contains "$combined" "terminal_exit_on_empty_backlog" "terminal backlog info emitted"
pass "terminal exit on empty backlog"
