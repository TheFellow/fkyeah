#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

assert_file_exists "$LOGS_DIR/manifest.json" "manifest.json exists"
assert_json_field "$LOGS_DIR/manifest.json" ".name" "manifest_test" "name field"
assert_json_field "$LOGS_DIR/manifest.json" ".goal" "Test manifest output" "goal field"
assert_json_field_exists "$LOGS_DIR/manifest.json" ".start_time" "start_time present"

pass "manifest.json has name, goal, start_time"
