#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

assert_file_exists "$LOGS_DIR/code/prompt.md" "prompt artifact exists"
prompt_text="$(cat "$LOGS_DIR/code/prompt.md")"
assert_contains "$prompt_text" "Build widget" "goal expanded into prompt"

pass "A5 goal expansion works"
