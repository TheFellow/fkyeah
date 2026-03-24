#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

# Patch mcp.json to use pre-built fixture binary when available (Docker/CI)
MCP_CONFIG="$TEST_DIR/mcp.json"
if [[ -n "${MOCK_MCP_SERVER:-}" && -x "$MOCK_MCP_SERVER" ]]; then
    MCP_CONFIG="$LOGS_DIR/mcp.json"
    python3 -c "
import json, sys
with open('$TEST_DIR/mcp.json') as f:
    cfg = json.load(f)
for s in cfg.get('servers', []):
    s['command'] = '$MOCK_MCP_SERVER'
    s['args'] = []
json.dump(cfg, open('$MCP_CONFIG', 'w'), indent=2)
"
    # Also patch the pipeline DOT to point to the rewritten config
    sed "s|mcp_config_file=\"mcp.json\"|mcp_config_file=\"$MCP_CONFIG\"|" "$TEST_DIR/pipeline.dot" > "$LOGS_DIR/pipeline.dot"
    run_pipeline_live "$LOGS_DIR/pipeline.dot"
else
    run_pipeline_live "$TEST_DIR/pipeline.dot"
fi
assert_exit_code 0 "$PIPELINE_EXIT" "stdio MCP pipeline should succeed"
assert_node_outcome "$LOGS_DIR" "mcp" "success"

request_file="$LOGS_DIR/mcp/mcp_request.json"
response_file="$LOGS_DIR/mcp/mcp_response.json"
status_file="$LOGS_DIR/mcp/status.json"

assert_file_exists "$request_file" "mcp request artifact"
assert_file_exists "$response_file" "mcp response artifact"
assert_file_exists "$status_file" "mcp status artifact"

request_json="$(cat "$request_file")"
response_json="$(cat "$response_file")"

assert_contains "$request_json" "initialize" "initialize should be exercised"
assert_contains "$request_json" "tools/list" "tools/list should be exercised"
assert_contains "$request_json" "tools/call" "tools/call should be exercised"
assert_contains "$request_json" "echo_upper" "tool name should be recorded"
assert_contains "$response_json" "HELLO" "response should contain uppercased content"
assert_json_field "$status_file" '.context_updates["tool.output"]' "HELLO" "tool output should be captured in context"

pass "stdio MCP tool call: request/response artifacts and context verified"
