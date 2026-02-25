#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

start_ts="$(date +%s)"
run_pipeline "$TEST_DIR/pipeline.dot"
end_ts="$(date +%s)"
elapsed=$((end_ts - start_ts))

assert_exit_code 0 "$PIPELINE_EXIT"

# max_retries should retry failures and eventually succeed on the third attempt.
assert_completed_nodes "$LOGS_DIR/checkpoint.json" "start" "work"
assert_json_field "$LOGS_DIR/checkpoint.json" ".node_outcomes.work.status" "success" "final work status"

attempt_count="$(cat "$LOGS_DIR/retry-attempt-count" 2>/dev/null || true)"
if [[ "$attempt_count" != "3" ]]; then
    fail "expected 3 attempts with max_retries=2, got '$attempt_count'"
fi

# A single attempt sleeps ~1s; with two retries + backoff this should be materially longer.
if (( elapsed < 3 )); then
    fail "expected retry run to take >= 3s, got ${elapsed}s"
fi

pass "max_retries retries with backoff (elapsed=${elapsed}s, attempts=${attempt_count})"
