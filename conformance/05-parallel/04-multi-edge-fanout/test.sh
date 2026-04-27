#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint"
assert_completed_nodes "$checkpoint" "start" "choose" "branch_one" "branch_two" "branch_three" "fan_in"

prompt_file="$LOGS_DIR/fan_in/prompt.txt"
assert_file_exists "$prompt_file" "fan-in prompt exists"
prompt_contents="$(cat "$prompt_file")"
assert_contains "$prompt_contents" "one-done" "fan-in prompt includes branch one update"
assert_contains "$prompt_contents" "two-done" "fan-in prompt includes branch two update"
assert_contains "$prompt_contents" "three-done" "fan-in prompt includes branch three update"

pass "multi-edge fan-out executes all branches and reaches fan-in"
