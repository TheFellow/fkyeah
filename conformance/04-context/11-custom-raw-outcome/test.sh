#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should complete"

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint" "checkpoint exists"
assert_completed_nodes "$checkpoint" "start" "writer" "custom_path"
assert_json_field "$checkpoint" '.node_outcomes.writer.raw_outcome' "needs_dod" "writer raw_outcome is preserved"

pass "custom outcome routes through outcome == \"needs_dod\""
