#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

# Patch pipeline to use pre-built fixture binary in Docker/CI
if [[ -n "${MOCK_ACP_AGENT:-}" && -x "$MOCK_ACP_AGENT" ]]; then
    cat > "$TEST_DIR/pipeline.dot" <<DOTEOF
digraph acp_parallel_two {
    graph [goal="Test ACP parallel fan-out/fan-in"]
    start [shape=Mdiamond]
    fork [shape=component]
    agent_a [shape=tab, type="acp.agent",
        acp_transport="stdio",
        acp_command="$MOCK_ACP_AGENT",
        prompt="Handle task A"]
    agent_b [shape=tab, type="acp.agent",
        acp_transport="stdio",
        acp_command="$MOCK_ACP_AGENT",
        prompt="Handle task B"]
    join [shape=tripleoctagon]
    done [shape=Msquare]
    start -> fork
    fork -> agent_a
    fork -> agent_b
    agent_a -> join
    agent_b -> join
    join -> done
}
DOTEOF
fi

export OPENAI_API_KEY="${OPENAI_API_KEY:-mock-key}"

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "parallel ACP pipeline should succeed"
assert_node_outcome "$LOGS_DIR" "agent_a" "success"
assert_node_outcome "$LOGS_DIR" "agent_b" "success"
assert_file_exists "$LOGS_DIR/checkpoint.json" "checkpoint artifact"
assert_completed_nodes "$LOGS_DIR/checkpoint.json" "join"
assert_json_field "$LOGS_DIR/checkpoint.json" '.context["parallel.success_count"]' "2" "parallel success count"

pass "ACP parallel two agents"
