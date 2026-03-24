#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

# Patch mcp.json to use pre-built fixture binary when available (Docker/CI)
_patch_mcp_pipelines() {
    if [[ -n "${MOCK_MCP_SERVER:-}" && -x "$MOCK_MCP_SERVER" ]]; then
        MCP_CONFIG="$LOGS_DIR/mcp.json"
        python3 -c "
import json
with open('$TEST_DIR/mcp.json') as f:
    cfg = json.load(f)
for s in cfg.get('servers', []):
    s['command'] = '$MOCK_MCP_SERVER'
    s['args'] = []
json.dump(cfg, open('$MCP_CONFIG', 'w'), indent=2)
"
        for dotfile in pipeline-invalid-tool.dot pipeline-missing-server.dot; do
            sed "s|mcp_config_file=\"mcp.json\"|mcp_config_file=\"$MCP_CONFIG\"|" "$TEST_DIR/$dotfile" > "$LOGS_DIR/$dotfile"
        done
    fi
}
_patch_mcp_pipelines

_dot() {
    local name="$1"
    if [[ -f "$LOGS_DIR/$name" ]]; then echo "$LOGS_DIR/$name"; else echo "$TEST_DIR/$name"; fi
}

run_pipeline_live "$(_dot pipeline-invalid-tool.dot)"
assert_exit_code 1 "$PIPELINE_EXIT" "invalid tool pipeline should fail"
assert_node_outcome "$LOGS_DIR" "mcp" "fail"
assert_contains "$PIPELINE_STDOUT $PIPELINE_STDERR" "nonexistent" "failure output should mention nonexistent tool"
if [[ -f "$LOGS_DIR/mcp/mcp_response.json" ]]; then
    fail "mcp_response.json should not exist when tool validation fails before invocation"
fi

cleanup
setup
_patch_mcp_pipelines

run_pipeline_live "$(_dot pipeline-missing-server.dot)"
assert_exit_code 1 "$PIPELINE_EXIT" "missing server pipeline should fail"
assert_node_outcome "$LOGS_DIR" "mcp" "fail"
assert_contains "$PIPELINE_STDOUT $PIPELINE_STDERR" "missing" "failure output should mention missing server"
if [[ -f "$LOGS_DIR/mcp/mcp_response.json" ]]; then
    fail "mcp_response.json should not exist when server lookup fails before invocation"
fi

pass "MCP discovery failures: invalid tool and missing server verified"
