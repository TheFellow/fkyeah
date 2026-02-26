#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

stdout_file="$(mktemp "${TMPDIR:-/tmp}/att-stdout.XXXXXX")"
stderr_file="$(mktemp "${TMPDIR:-/tmp}/att-stderr.XXXXXX")"

"$ATTRACTOR_BIN" "$TEST_DIR/pipeline.dot" \
  --simulate --auto-approve --quiet --logs "$LOGS_DIR" \
  >"$stdout_file" 2>"$stderr_file" &
pid=$!

checkpoint="$LOGS_DIR/checkpoint.json"
for _ in $(seq 1 120); do
  if [[ -f "$checkpoint" ]] && jq -e '.completed_nodes | index("step1")' "$checkpoint" >/dev/null 2>&1; then
    break
  fi
  sleep 0.1
done

assert_file_exists "$checkpoint" "checkpoint exists before kill"
assert_json_array_contains "$checkpoint" ".completed_nodes" "step1" "step1 completed before interruption"

artifact_file="$LOGS_DIR/step1/tool_output.txt"
assert_file_exists "$artifact_file" "step1 tool_output exists"
before_mtime="$(date -r "$artifact_file" +%s)"

kill "$pid" >/dev/null 2>&1 || true
wait "$pid" >/dev/null 2>&1 || true

set +e
"$ATTRACTOR_BIN" --resume "$LOGS_DIR" "$TEST_DIR/pipeline.dot" --simulate --auto-approve --quiet \
  >"$stdout_file" 2>"$stderr_file"
resume_exit=$?
set -e
assert_exit_code 0 "$resume_exit" "resume run should succeed"

assert_json_array_contains "$checkpoint" ".completed_nodes" "step2" "step2 completed after resume"
after_mtime="$(date -r "$artifact_file" +%s)"
if [[ "$before_mtime" != "$after_mtime" ]]; then
  fail "step1 appears to have been re-executed (artifact timestamp changed)"
fi

clean_logs="$(mktemp -d "${TMPDIR:-/tmp}/att-clean.XXXXXX")"
set +e
"$ATTRACTOR_BIN" "$TEST_DIR/pipeline.dot" --simulate --auto-approve --quiet --logs "$clean_logs" > /dev/null 2>&1
clean_exit=$?
set -e
assert_exit_code 0 "$clean_exit" "clean baseline run should succeed"

assert_json_array_contains "$clean_logs/checkpoint.json" ".completed_nodes" "step2" "clean baseline reached step2"

rm -f "$stdout_file" "$stderr_file"
rm -rf "$clean_logs"
pass "A2 checkpoint resume succeeds without re-running completed stage"
