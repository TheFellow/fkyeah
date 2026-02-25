#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint" "checkpoint.json exists"
assert_json_field_exists "$checkpoint" ".node_outcomes" "node_outcomes present"
assert_completed_nodes "$checkpoint" "start" "step1" "step2"

for node in start step1 step2; do
    assert_json_field_exists "$checkpoint" ".node_outcomes[\"$node\"]" "node_outcomes entry for $node"
    assert_json_field "$checkpoint" ".node_outcomes[\"$node\"].status" "success" "outcome status for $node"
done

pass "checkpoint node_outcomes records completed node statuses"
