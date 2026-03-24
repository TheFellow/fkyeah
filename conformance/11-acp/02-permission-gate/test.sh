#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

# Patch pipeline.dot to use pre-built fixture binary when available (Docker/CI)
if [[ -n "${MOCK_ACP_AGENT:-}" && -x "$MOCK_ACP_AGENT" ]]; then
    sed -i.bak \
        -e "s|acp_command=\"dotnet\"|acp_command=\"$MOCK_ACP_AGENT\"|" \
        -e 's|acp_args_json="[^"]*"|acp_args_json="[\"--deny-test\"]"|' \
        "$TEST_DIR/pipeline.dot"
fi

export OPENAI_API_KEY="${OPENAI_API_KEY:-mock-key}"

stdout_file="$(mktemp)"
stderr_file="$(mktemp)"
set +e
"$ATTRACTOR_BIN" "$TEST_DIR/pipeline.dot" \
    --simulate --quiet \
    --logs "$LOGS_DIR" \
    >"$stdout_file" 2>"$stderr_file"
PIPELINE_EXIT=$?
set -e
rm -f "$stdout_file" "$stderr_file"

assert_exit_code 0 "$PIPELINE_EXIT" "permission gate should still succeed"
assert_node_outcome "$LOGS_DIR" "gated_task" "success"
assert_file_exists "$LOGS_DIR/gated_task/acp_session.json" "acp session artifact"
assert_json_field_exists "$LOGS_DIR/gated_task/acp_session.json" '.delegate_denials[0]' "delegate denial recorded"

pass "ACP permission gate"
