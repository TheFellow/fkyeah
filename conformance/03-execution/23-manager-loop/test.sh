#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

# Test 1: Polling mode - max-cycles failure
run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 1 "$PIPELINE_EXIT" "polling mode: manager loop should fail without stop signal"
assert_contains "$PIPELINE_STDERR" "max_cycles" "polling mode: failure should mention max_cycles"

# Test 2: Polling mode - stop key via checkpoint resume
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

# Test 3: Child pipeline mode - success
tmp_dot="$(mktemp "${TMPDIR:-/tmp}/manager-child-XXXXXX")"
sed "s|__TEST_DIR__|$TEST_DIR|g" "$TEST_DIR/pipeline-child.dot" > "$tmp_dot"
LOGS_DIR="$(mktemp -d "${TMPDIR:-/tmp}/attractor-conformance.XXXXXX")"
run_pipeline "$tmp_dot"
assert_exit_code 0 "$PIPELINE_EXIT" "child pipeline mode: should succeed"

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint" "child mode: checkpoint exists"
assert_completed_nodes "$checkpoint" "start" "manager"
assert_dir_exists "$LOGS_DIR/manager/cycle_0" "child cycle_0 logs directory"
assert_json_field "$checkpoint" '.context["manager.cycle_count"]' "1" "cycle_count is 1"
assert_json_field "$checkpoint" '.context["manager.child_status"]' "success" "child status is success"
rm -f "$tmp_dot"

# Test 4: Child pipeline mode - failure after max_cycles
tmp_dot_fail="$(mktemp "${TMPDIR:-/tmp}/manager-child-fail-XXXXXX")"
sed "s|__TEST_DIR__|$TEST_DIR|g" "$TEST_DIR/pipeline-child-fail.dot" > "$tmp_dot_fail"
LOGS_DIR="$(mktemp -d "${TMPDIR:-/tmp}/attractor-conformance.XXXXXX")"
run_pipeline "$tmp_dot_fail"
assert_exit_code 1 "$PIPELINE_EXIT" "child pipeline mode: should fail after max_cycles"
assert_contains "$PIPELINE_STDERR" "cycle" "child failure mentions cycles"
assert_dir_exists "$LOGS_DIR/manager/cycle_0" "fail: child cycle_0 logs"
assert_dir_exists "$LOGS_DIR/manager/cycle_1" "fail: child cycle_1 logs"
rm -f "$tmp_dot_fail"

# Test 5: Child pipeline carries context from parent work
tmp_dot_ctx="$(mktemp "${TMPDIR:-/tmp}/manager-child-ctx-XXXXXX")"
LOGS_DIR="$(mktemp -d "${TMPDIR:-/tmp}/attractor-conformance.XXXXXX")"
sed -e "s|__TEST_DIR__|$TEST_DIR|g" -e "s|__LOGS_DIR__|$LOGS_DIR|g" "$TEST_DIR/pipeline-child-context.dot" > "$tmp_dot_ctx"
run_pipeline "$tmp_dot_ctx"
assert_exit_code 0 "$PIPELINE_EXIT" "context e2e: pipeline should succeed"

checkpoint="$LOGS_DIR/checkpoint.json"
assert_file_exists "$checkpoint" "context e2e: checkpoint exists"
assert_completed_nodes "$checkpoint" "start" "seed" "manager" "verify"

child_keys="$(jq -r '.context | keys[] | select(startswith("child."))' "$checkpoint" 2>/dev/null || true)"
if [[ -z "$child_keys" ]]; then
    fail "context e2e: no child.* keys found in parent checkpoint context"
fi

assert_dir_exists "$LOGS_DIR/manager/cycle_0" "context e2e: child cycle_0 logs"
rm -f "$tmp_dot_ctx"

pass "A4 manager loop: polling mode + child pipeline mode (success/failure/context)"
