#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline_live "$TEST_DIR/pipeline-invalid-tool.dot"
assert_exit_code 1 "$PIPELINE_EXIT" "invalid tool pipeline should fail"
assert_node_outcome "$LOGS_DIR" "mcp" "fail"
assert_contains "$PIPELINE_STDOUT $PIPELINE_STDERR" "nonexistent" "failure output should mention nonexistent tool"
if [[ -f "$LOGS_DIR/mcp/mcp_response.json" ]]; then
    fail "mcp_response.json should not exist when tool validation fails before invocation"
fi

cleanup
setup

run_pipeline_live "$TEST_DIR/pipeline-missing-server.dot"
assert_exit_code 1 "$PIPELINE_EXIT" "missing server pipeline should fail"
assert_node_outcome "$LOGS_DIR" "mcp" "fail"
assert_contains "$PIPELINE_STDOUT $PIPELINE_STDERR" "missing" "failure output should mention missing server"
if [[ -f "$LOGS_DIR/mcp/mcp_response.json" ]]; then
    fail "mcp_response.json should not exist when server lookup fails before invocation"
fi

pass "MCP discovery failures: invalid tool and missing server verified"
