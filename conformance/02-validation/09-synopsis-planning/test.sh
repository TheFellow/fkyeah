#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$VALIDATE_EXIT" "validation should succeed"

combined="$VALIDATE_STDOUT$VALIDATE_STDERR"
assert_contains "$combined" "PLANNING" "classified as PLANNING"

pass "LLM-only pipeline classified as PLANNING"
