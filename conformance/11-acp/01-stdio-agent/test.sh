#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

# Patch pipeline.dot to use pre-built fixture binary when available (Docker/CI)
if [[ -n "${MOCK_ACP_AGENT:-}" && -x "$MOCK_ACP_AGENT" ]]; then
    sed -i.bak \
        -e "s|acp_command=\"dotnet\"|acp_command=\"$MOCK_ACP_AGENT\"|" \
        -e 's|acp_args_json="[^"]*"|acp_args_json="[]"|' \
        "$TEST_DIR/pipeline.dot"
fi

export OPENAI_API_KEY="${OPENAI_API_KEY:-mock-key}"

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "stdio ACP pipeline should succeed"
assert_node_outcome "$LOGS_DIR" "agent_task" "success"

assert_file_exists "$LOGS_DIR/agent_task/status.json" "agent status"
assert_file_exists "$LOGS_DIR/agent_task/response.md" "agent response"
assert_file_exists "$LOGS_DIR/agent_task/acp_session.json" "agent session artifact"
assert_json_field_exists "$LOGS_DIR/agent_task/status.json" '.context_updates["acp.output.agent_task"]' "acp output context"

pass "ACP stdio agent"
