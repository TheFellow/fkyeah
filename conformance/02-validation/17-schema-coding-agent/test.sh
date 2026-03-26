#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

set +e
schema_output="$("$ATTRACTOR_BIN" schema 2>&1)"
schema_exit=$?
set -e

assert_exit_code 0 "$schema_exit" "schema command should succeed"
assert_contains "$schema_output" "tab               coding_agent" "schema documents tab coding_agent shape"
assert_contains "$schema_output" "CODING AGENT ATTRIBUTES" "schema has coding agent section"
assert_contains "$schema_output" "max_turns" "schema lists max_turns attribute"
assert_contains "$schema_output" "max_tool_rounds" "schema lists max_tool_rounds attribute"
assert_contains "$schema_output" "command_timeout" "schema lists command_timeout attribute"
assert_contains "$schema_output" "system_prompt" "schema lists system_prompt attribute"

pass "schema includes tab/coding_agent and coding agent attributes"
