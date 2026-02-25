#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint" "checkpoint.json exists"
assert_json_field_exists "$checkpoint" ".timestamp" "timestamp present"
assert_json_field_exists "$checkpoint" ".completed_nodes" "completed_nodes present"
assert_json_field_exists "$checkpoint" ".context" "context present"
assert_json_field_exists "$checkpoint" ".node_outcomes" "node_outcomes present"

while IFS= read -r node; do
    assert_json_field_exists "$checkpoint" ".node_outcomes[\"$node\"]" "node_outcomes entry for $node"
done < <(jq -r '.completed_nodes[]' "$checkpoint")

pass "checkpoint.json has timestamp, completed_nodes, context, and node_outcomes entries"
