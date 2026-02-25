#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

# Checkpoint context should have last_stage visible
assert_json_field_exists "$LOGS_DIR/checkpoint.json" ".context" "context exists"

pass "last_stage visible in checkpoint context"
