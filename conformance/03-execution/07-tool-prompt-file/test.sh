#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"

# The engine creates a prompt.txt in the tool's stage directory
assert_file_exists "$LOGS_DIR/tool/prompt.txt" "prompt file written for tool"

pass "ATTRACTOR_PROMPT_FILE set and prompt.txt created"
