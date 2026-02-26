#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

pre="$(find "$LOGS_DIR/tool" -name 'pre-hook.txt' | head -n 1)"
post="$(find "$LOGS_DIR/tool" -name 'post-hook.txt' | head -n 1)"
assert_file_exists "$pre" "pre-hook marker exists"
assert_file_exists "$post" "post-hook marker exists"

pre_text="$(cat "$pre")"
post_text="$(cat "$post")"
assert_contains "$pre_text" "shell:tool" "pre hook has TOOL_NAME and NODE_ID"
assert_contains "$post_text" "shell:tool:0" "post hook has TOOL_NAME NODE_ID EXIT_CODE"

pass "A10 tool hook pre/post commands executed with expected env"
