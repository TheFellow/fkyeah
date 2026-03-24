#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

SERVER_DIR="$(mktemp -d "${TMPDIR:-/tmp}/mock-llm.XXXXXX")"
PORT=$((9200 + RANDOM % 200))
SERVER_LOG="$SERVER_DIR/server.log"

dotnet run --project "$TEST_DIR/../../../tests/Fixtures/MockLlmServer/MockLlmServer.fsproj" -- \
  --port "$PORT" \
  --record-dir "$SERVER_DIR" \
  --scenario edge_override >"$SERVER_LOG" 2>&1 &
SERVER_PID=$!
trap 'kill "$SERVER_PID" >/dev/null 2>&1 || true' EXIT
sleep 1

export OPENAI_API_KEY="mock-key"
export OPENAI_BASE_URL="http://127.0.0.1:$PORT/v1/responses"

run_pipeline_live "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should complete"

budget_file="$LOGS_DIR/implement/context_budget.json"
assert_file_exists "$budget_file" "context budget artifact exists"
assert_json_field "$budget_file" ".fidelity_mode" "truncate" "edge override recorded"

instructions_len="$(python3 - <<PY
import json, pathlib
for path in sorted(pathlib.Path("$SERVER_DIR").glob("*.json")):
    body = json.loads(path.read_text())
    prompt_hits = [item for item in body.get("input", []) if "Implement using the incoming context" in json.dumps(item)]
    if prompt_hits:
        print(len(body.get("instructions", "")))
        break
PY
)"

if [[ -z "$instructions_len" || "$instructions_len" -ge 2000 ]]; then
    fail "edge override should keep implement request truncated, got ${instructions_len:-missing}"
fi

pass "edge fidelity override: truncate beats node full"
