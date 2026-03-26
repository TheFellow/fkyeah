#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$VALIDATE_EXIT" "validation should succeed with warning"

combined="$VALIDATE_STDOUT$VALIDATE_STDERR"
assert_contains "$combined" "attribute_known" "warning emitted by attribute_known rule"
assert_contains "$combined" "max_agent_turns" "warning references unrecognized max_agent_turns attribute"
assert_contains "$combined" "max_turns" "warning includes did-you-mean suggestion"

pass "unrecognized max_agent_turns warns with max_turns suggestion"
