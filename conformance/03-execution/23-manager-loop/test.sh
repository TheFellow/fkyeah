#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

# Max-cycles failure
run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 1 "$PIPELINE_EXIT" "manager loop should fail without stop signal"
assert_contains "$PIPELINE_STDERR" "max_cycles" "failure should mention max_cycles"

# Stop-key success via context preseed through checkpoint resume seam:
# we seed manager.stop in checkpoint context by running pipeline-stop and then patching context.
run_pipeline "$TEST_DIR/pipeline-stop.dot"
assert_exit_code 1 "$PIPELINE_EXIT" "pipeline-stop should fail before manual stop signal"

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint" "checkpoint exists"
jq '.context["manager.stop"]="true"' "$checkpoint" > "$checkpoint.tmp"
mv "$checkpoint.tmp" "$checkpoint"

set +e
"$ATTRACTOR_BIN" --resume "$LOGS_DIR" "$TEST_DIR/pipeline-stop.dot" --simulate --auto-approve --quiet > /dev/null 2>&1
resume_exit=$?
set -e
assert_exit_code 0 "$resume_exit" "resume should succeed when manager.stop=true"

pass "A4 manager loop enforces max-cycles and respects stop key on resume"
