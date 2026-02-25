#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_validate "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$VALIDATE_EXIT" "validation should succeed"

# Engine may emit a WARN for LLM-only pipelines (no tool nodes) — that's OK
# The key assertion is exit code 0 (no errors)

pass "valid pipeline passes validation"
