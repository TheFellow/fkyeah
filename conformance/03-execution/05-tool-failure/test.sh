#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"

# Tool with "exit 1" should cause pipeline failure
assert_exit_code 1 "$PIPELINE_EXIT"

pass "non-zero tool exit causes pipeline failure"
