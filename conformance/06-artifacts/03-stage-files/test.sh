#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

# LLM node should produce prompt.md, response.md, status.json
assert_dir_exists "$LOGS_DIR/work" "work stage dir"
assert_file_exists "$LOGS_DIR/work/prompt.md" "prompt.md"
assert_file_exists "$LOGS_DIR/work/response.md" "response.md"
assert_file_exists "$LOGS_DIR/work/status.json" "status.json"

pass "prompt.md, response.md, status.json written per node"
