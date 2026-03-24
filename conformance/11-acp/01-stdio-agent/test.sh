#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

# Patch pipeline to use pre-built fixture binary in Docker/CI
if [[ -n "${MOCK_ACP_AGENT:-}" && -x "$MOCK_ACP_AGENT" ]]; then
    cat > "$TEST_DIR/pipeline.dot" <<DOTEOF
digraph acp_stdio_test {
    graph [goal="Test ACP agent delegation over stdio"]
    start [shape=Mdiamond]
    done [shape=Msquare]
    agent_task [shape=tab, type="acp.agent",
        acp_transport="stdio",
        acp_command="$MOCK_ACP_AGENT",
        prompt="Implement the greeting feature"]
    start -> agent_task -> done
}
DOTEOF
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
