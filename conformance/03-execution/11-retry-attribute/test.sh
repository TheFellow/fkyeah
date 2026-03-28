#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

start_ts="$(date +%s)"
run_pipeline "$TEST_DIR/pipeline.dot"
end_ts="$(date +%s)"
elapsed=$((end_ts - start_ts))

# Tool exit 1 produces Outcome.Fail which is deterministic — not retried
# even when max_retries is set. The pipeline should fail on the first attempt.
assert_exit_code 1 "$PIPELINE_EXIT"

attempt_count="$(cat "$LOGS_DIR/retry-attempt-count" 2>/dev/null || echo "0")"
if [[ "$attempt_count" != "1" ]]; then
    fail "expected 1 attempt (Fail is not retried), got '$attempt_count'"
fi

# No retries means no backoff — should complete quickly.
if (( elapsed > 3 )); then
    fail "expected fast failure (no backoff), got ${elapsed}s"
fi

pass "tool Fail not retried despite max_retries (elapsed=${elapsed}s, attempts=${attempt_count})"
