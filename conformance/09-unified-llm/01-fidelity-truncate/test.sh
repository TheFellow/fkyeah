#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

SERVER_DIR="$(mktemp -d "${TMPDIR:-/tmp}/mock-llm.XXXXXX")"
PORT=$((8800 + RANDOM % 200))
SERVER_LOG="$SERVER_DIR/server.log"

if [[ -n "${MOCK_LLM_SERVER:-}" && -x "$MOCK_LLM_SERVER" ]]; then
  "$MOCK_LLM_SERVER" --port "$PORT" --record-dir "$SERVER_DIR" --scenario truncate >"$SERVER_LOG" 2>&1 &
else
  dotnet run --project "$TEST_DIR/../../../tests/Fixtures/MockLlmServer/MockLlmServer.fsproj" -- \
    --port "$PORT" --record-dir "$SERVER_DIR" --scenario truncate >"$SERVER_LOG" 2>&1 &
fi
SERVER_PID=$!
trap 'kill "$SERVER_PID" >/dev/null 2>&1 || true' EXIT
wait_for_port "$PORT" 15

export OPENAI_API_KEY="mock-key"
export OPENAI_BASE_URL="http://127.0.0.1:$PORT/v1/responses"

run_pipeline_live "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT" "pipeline should complete"

budget_file="$LOGS_DIR/implement/context_budget.json"
assert_file_exists "$budget_file" "context budget artifact exists"
assert_json_field "$budget_file" ".fidelity_mode" "truncate" "truncate fidelity recorded"
assert_json_field "$budget_file" ".token_budget" "100" "truncate token budget"
assert_json_field "$budget_file" ".char_budget" "400" "truncate char budget"

instructions_len="$(python3 - <<PY
import json, pathlib
for path in sorted(pathlib.Path("$SERVER_DIR").glob("*.json")):
    body = json.loads(path.read_text())
    prompt_hits = [item for item in body.get("input", []) if "Write the code based on the plan" in json.dumps(item)]
    if prompt_hits:
        print(len(body.get("instructions", "")))
        break
PY
)"

if [[ -z "$instructions_len" || "$instructions_len" -ge 2000 ]]; then
    fail "implement request instructions should be truncated (<2000 chars), got ${instructions_len:-missing}"
fi

pass "truncate fidelity: request budget and artifact verified"
