#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

# Verify the prompt was preserved with comment-like content
assert_file_exists "$LOGS_DIR/work/prompt.md"

pass "// and /* preserved inside quoted strings"
