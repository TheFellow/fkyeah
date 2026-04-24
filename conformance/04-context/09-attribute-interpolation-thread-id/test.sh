#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should complete via loop_restart path"

assert_dir_exists "$LOGS_DIR/thread-review-0" "iteration 0 thread directory exists"
assert_dir_exists "$LOGS_DIR/restart-1/thread-review-1" "iteration 1 thread directory exists"

thread_count="$(find "$LOGS_DIR" -type d -name 'thread-review-*' | wc -l | tr -d ' ')"
if [[ "$thread_count" -lt 2 ]]; then
  fail "expected at least two distinct thread directories, found $thread_count"
fi

pass "attribute interpolation in thread_id creates per-iteration sessions"
