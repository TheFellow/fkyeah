#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

assert_file_exists "$LOGS_DIR/free/prompt.md" "freeform gate prompt artifact exists"
assert_file_exists "$LOGS_DIR/free/response.md" "freeform gate response artifact exists"
assert_json_field_exists "$LOGS_DIR/checkpoint.json" '.context["human.gate.selected"]' "human gate selection captured"
assert_json_field_exists "$LOGS_DIR/checkpoint.json" '.context["human.gate.label"]' "human gate label captured"

pass "A9 freeform and choice gate artifacts are produced under auto-approve"
