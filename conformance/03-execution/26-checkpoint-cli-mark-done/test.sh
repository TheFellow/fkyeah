#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

rm -f "$TEST_DIR/resumed_marker.txt"

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 1 "$PIPELINE_EXIT" "initial run should stop at failing ManualGate"

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint" "checkpoint exists after failed run"
assert_json_field "$checkpoint" '.current_node' "ManualGate" "checkpoint should stop at ManualGate"

set +e
"$ATTRACTOR_BIN" checkpoint mark-done "$LOGS_DIR" ManualGate --outcome=success --note="manual override" > /tmp/att-checkpoint.stdout.$$ 2> /tmp/att-checkpoint.stderr.$$
mark_done_exit=$?
set -e
assert_exit_code 0 "$mark_done_exit" "checkpoint mark-done should succeed"
assert_file_exists "$LOGS_DIR/checkpoint.json.bak" "mark-done should create backup"

set +e
"$ATTRACTOR_BIN" --resume "$LOGS_DIR" "$TEST_DIR/pipeline.dot" --simulate --auto-approve --quiet > /tmp/att-resume.stdout.$$ 2> /tmp/att-resume.stderr.$$
resume_exit=$?
set -e
assert_exit_code 0 "$resume_exit" "resume should continue past ManualGate"

assert_file_exists "$TEST_DIR/resumed_marker.txt" "Step3 should run after resume"
assert_contains "$(cat "$TEST_DIR/resumed_marker.txt")" "resumed_marker" "resume marker contents"

rm -f "$TEST_DIR/resumed_marker.txt" /tmp/att-checkpoint.stdout.$$ /tmp/att-checkpoint.stderr.$$ /tmp/att-resume.stdout.$$ /tmp/att-resume.stderr.$$
pass "checkpoint mark-done enables successful resume at next node"
