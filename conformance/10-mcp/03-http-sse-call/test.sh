#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

if ! command -v python3 >/dev/null 2>&1; then
    skip "python3 not available for local HTTP+SSE fixture"
fi

if [[ "${ENABLE_MCP_HTTP_SSE_FIXTURE:-0}" != "1" ]]; then
    skip "local HTTP+SSE fixture not enabled"
fi

PORT=$((18880 + RANDOM % 500))
SERVER_LOG="$LOGS_DIR/http-sse.log"

cat >"$TEST_DIR/mcp.json" <<JSON
{
  "servers": [
    {
      "name": "remote",
      "transport": "sse",
      "url": "http://127.0.0.1:$PORT/events",
      "requestUrl": "http://127.0.0.1:$PORT/rpc"
    }
  ]
}
JSON

python3 - "$PORT" <<'PY' >"$SERVER_LOG" 2>&1 &
import json
import queue
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

port = int(sys.argv[1])
events = queue.Queue()

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def do_GET(self):
        if self.path != "/events":
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()
        for _ in range(3):
            payload = events.get(timeout=10)
            self.wfile.write(f"data: {payload}\n\n".encode("utf-8"))
            self.wfile.flush()

    def do_POST(self):
        if self.path != "/rpc":
            self.send_error(404)
            return
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length).decode("utf-8")
        request = json.loads(body)
        method = request["method"]
        if method == "initialize":
            result = {"protocolVersion": "2025-03-26", "capabilities": {}, "serverInfo": {"name": "http-mock"}}
        elif method == "tools/list":
            result = {"tools": [{"name": "echo_upper", "description": "Uppercase", "inputSchema": {"type": "object"}}]}
        elif method == "tools/call":
            text = request.get("params", {}).get("arguments", {}).get("text", "").upper()
            result = {"content": [{"type": "text", "text": text}], "isError": False}
        else:
            result = {"error": {"code": -32601, "message": "Method not found"}}

        if "error" in result:
            response = {"jsonrpc": "2.0", "id": request["id"], "error": result["error"]}
        else:
            response = {"jsonrpc": "2.0", "id": request["id"], "result": result}
        events.put(json.dumps(response))

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", "17")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(b'{"accepted":true}')

server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
server.serve_forever()
PY
SERVER_PID=$!
trap 'kill "$SERVER_PID" >/dev/null 2>&1 || true; rm -f "$TEST_DIR/mcp.json"' EXIT
sleep 1

run_pipeline_live "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "HTTP+SSE MCP pipeline should succeed"
assert_node_outcome "$LOGS_DIR" "mcp" "success"

request_file="$LOGS_DIR/mcp/mcp_request.json"
response_file="$LOGS_DIR/mcp/mcp_response.json"
status_file="$LOGS_DIR/mcp/status.json"

assert_file_exists "$request_file" "mcp request artifact"
assert_file_exists "$response_file" "mcp response artifact"
assert_json_field "$status_file" '.context_updates["tool.output"]' "HELLO" "tool output should be uppercased"

request_json="$(cat "$request_file")"
assert_contains "$request_json" "initialize" "initialize should be exercised"
assert_contains "$request_json" "tools/list" "tools/list should be exercised"
assert_contains "$request_json" "tools/call" "tools/call should be exercised"

pass "HTTP+SSE MCP tool call: SSE receive and HTTP POST send verified"
