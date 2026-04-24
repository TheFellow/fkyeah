#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/../../../examples/sdlc_grind_subtask.dot"
assert_exit_code 0 "$VALIDATE_EXIT" "sdlc_grind_subtask example should validate"

pass "sdlc_grind_subtask example validates"
