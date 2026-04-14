#!/usr/bin/env bash
# conformance/lib.sh — Shared helpers for attractor conformance tests
set -euo pipefail

# ── Binary ──────────────────────────────────────────────────────────
ATTRACTOR_BIN="${ATTRACTOR_BIN:-$HOME/bin/attractor}"

# ── Colors ──────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
NC='\033[0m' # No Color

# ── State ───────────────────────────────────────────────────────────
TEST_DIR=""
LOGS_DIR=""
_ASSERT_COUNT=0
_ASSERT_FAIL=0

# ── Temp directory lifecycle ────────────────────────────────────────
setup() {
    TEST_DIR="$(cd "$(dirname "${BASH_SOURCE[1]}")" && pwd)"
    LOGS_DIR="$(mktemp -d "${TMPDIR:-/tmp}/attractor-conformance.XXXXXX")"
    _ASSERT_COUNT=0
    _ASSERT_FAIL=0
}

cleanup() {
    if [[ -n "${LOGS_DIR:-}" && -d "$LOGS_DIR" ]]; then
        rm -rf "$LOGS_DIR"
    fi
}

trap cleanup EXIT

# ── Runners ─────────────────────────────────────────────────────────

# run_pipeline <dot_file> [extra_args...]
# Runs the attractor binary with --simulate --auto-approve --quiet.
# Sets PIPELINE_EXIT, PIPELINE_STDOUT, PIPELINE_STDERR.
run_pipeline() {
    local dot_file="$1"; shift
    local extra_args=("$@")

    PIPELINE_EXIT=0
    PIPELINE_STDOUT=""
    PIPELINE_STDERR=""

    local stdout_file stderr_file
    stdout_file="$(mktemp "${TMPDIR:-/tmp}/att-stdout.XXXXXX")"
    stderr_file="$(mktemp "${TMPDIR:-/tmp}/att-stderr.XXXXXX")"

    set +e
    "$ATTRACTOR_BIN" "$dot_file" \
        --simulate --auto-approve --quiet \
        --logs "$LOGS_DIR" \
        ${extra_args[@]+"${extra_args[@]}"} \
        >"$stdout_file" 2>"$stderr_file"
    PIPELINE_EXIT=$?
    set -e

    PIPELINE_STDOUT="$(cat "$stdout_file")"
    PIPELINE_STDERR="$(cat "$stderr_file")"
    rm -f "$stdout_file" "$stderr_file"
}

# run_pipeline_live <dot_file> [extra_args...]
# Runs the attractor binary WITHOUT --simulate (real API calls).
# Uses --auto-approve --quiet. Sets PIPELINE_EXIT, PIPELINE_STDOUT, PIPELINE_STDERR.
run_pipeline_live() {
    local dot_file="$1"; shift
    local extra_args=("$@")

    PIPELINE_EXIT=0
    PIPELINE_STDOUT=""
    PIPELINE_STDERR=""

    local stdout_file stderr_file
    stdout_file="$(mktemp "${TMPDIR:-/tmp}/att-stdout.XXXXXX")"
    stderr_file="$(mktemp "${TMPDIR:-/tmp}/att-stderr.XXXXXX")"

    set +e
    "$ATTRACTOR_BIN" "$dot_file" \
        --auto-approve --quiet \
        --logs "$LOGS_DIR" \
        ${extra_args[@]+"${extra_args[@]}"} \
        >"$stdout_file" 2>"$stderr_file"
    PIPELINE_EXIT=$?
    set -e

    PIPELINE_STDOUT="$(cat "$stdout_file")"
    PIPELINE_STDERR="$(cat "$stderr_file")"
    rm -f "$stdout_file" "$stderr_file"
}

# wait_for_port <port> [timeout_secs]
# Polls until a TCP connection to 127.0.0.1:<port> succeeds.
wait_for_port() {
    local port="$1"
    local deadline="${2:-10}"
    local elapsed=0
    while ! bash -c "echo >/dev/tcp/127.0.0.1/$port" 2>/dev/null; do
        sleep 0.2
        elapsed=$((elapsed + 1))
        if [[ "$elapsed" -ge $((deadline * 5)) ]]; then
            echo "wait_for_port: 127.0.0.1:$port not ready after ${deadline}s" >&2
            return 1
        fi
    done
}

# require_env <var_name>
# Skips the test if the environment variable is not set.
require_env() {
    local var_name="$1"
    if [[ -z "${!var_name:-}" ]]; then
        skip "$var_name not set"
    fi
}

# run_validate <dot_file>
# Runs attractor --validate on the given file.
# Sets VALIDATE_EXIT, VALIDATE_STDOUT, VALIDATE_STDERR.
run_validate() {
    local dot_file="$1"

    VALIDATE_EXIT=0
    VALIDATE_STDOUT=""
    VALIDATE_STDERR=""

    local stdout_file stderr_file
    stdout_file="$(mktemp "${TMPDIR:-/tmp}/att-stdout.XXXXXX")"
    stderr_file="$(mktemp "${TMPDIR:-/tmp}/att-stderr.XXXXXX")"

    set +e
    "$ATTRACTOR_BIN" --validate "$dot_file" \
        >"$stdout_file" 2>"$stderr_file"
    VALIDATE_EXIT=$?
    set -e

    VALIDATE_STDOUT="$(cat "$stdout_file")"
    VALIDATE_STDERR="$(cat "$stderr_file")"
    rm -f "$stdout_file" "$stderr_file"
}

# ── Assertions ──────────────────────────────────────────────────────

assert_exit_code() {
    local expected="$1"
    local actual="$2"
    local label="${3:-exit code}"
    _ASSERT_COUNT=$((_ASSERT_COUNT + 1))
    if [[ "$actual" -ne "$expected" ]]; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: expected exit code $expected, got $actual" >&2
        return 1
    fi
}

assert_file_exists() {
    local path="$1"
    local label="${2:-file exists}"
    _ASSERT_COUNT=$((_ASSERT_COUNT + 1))
    if [[ ! -f "$path" ]]; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: file not found: $path" >&2
        return 1
    fi
}

assert_dir_exists() {
    local path="$1"
    local label="${2:-dir exists}"
    _ASSERT_COUNT=$((_ASSERT_COUNT + 1))
    if [[ ! -d "$path" ]]; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: directory not found: $path" >&2
        return 1
    fi
}

# assert_contains <haystack_string> <needle_pattern>
# Checks that haystack contains the pattern (grep -qF for fixed strings).
assert_contains() {
    local haystack="$1"
    local needle="$2"
    local label="${3:-contains}"
    _ASSERT_COUNT=$((_ASSERT_COUNT + 1))
    if ! echo "$haystack" | grep -qF "$needle"; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: output does not contain '$needle'" >&2
        echo "  Output was: $(echo "$haystack" | head -5)" >&2
        return 1
    fi
}

# assert_contains_regex <haystack_string> <pattern>
assert_contains_regex() {
    local haystack="$1"
    local pattern="$2"
    local label="${3:-contains regex}"
    _ASSERT_COUNT=$((_ASSERT_COUNT + 1))
    if ! echo "$haystack" | grep -qE "$pattern"; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: output does not match regex '$pattern'" >&2
        echo "  Output was: $(echo "$haystack" | head -5)" >&2
        return 1
    fi
}

# assert_not_contains <haystack_string> <needle_pattern>
assert_not_contains() {
    local haystack="$1"
    local needle="$2"
    local label="${3:-not contains}"
    _ASSERT_COUNT=$((_ASSERT_COUNT + 1))
    if echo "$haystack" | grep -qF "$needle"; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: output unexpectedly contains '$needle'" >&2
        return 1
    fi
}

# assert_json_field <json_file> <jq_expression> <expected_value>
assert_json_field() {
    local json_file="$1"
    local jq_expr="$2"
    local expected="$3"
    local label="${4:-json field}"
    _ASSERT_COUNT=$((_ASSERT_COUNT + 1))

    if [[ ! -f "$json_file" ]]; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: JSON file not found: $json_file" >&2
        return 1
    fi

    local actual
    actual="$(jq -r "$jq_expr" "$json_file" 2>/dev/null || echo "__JQ_ERROR__")"
    if [[ "$actual" == "__JQ_ERROR__" ]]; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: jq error on expression '$jq_expr'" >&2
        return 1
    fi

    if [[ "$actual" != "$expected" ]]; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: expected '$expected', got '$actual' (expr: $jq_expr)" >&2
        return 1
    fi
}

# assert_json_field_exists <json_file> <jq_expression>
assert_json_field_exists() {
    local json_file="$1"
    local jq_expr="$2"
    local label="${3:-json field exists}"
    _ASSERT_COUNT=$((_ASSERT_COUNT + 1))

    if [[ ! -f "$json_file" ]]; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: JSON file not found: $json_file" >&2
        return 1
    fi

    local actual
    actual="$(jq -e "$jq_expr" "$json_file" 2>/dev/null)" || {
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: field '$jq_expr' not found or null" >&2
        return 1
    }
}

# assert_json_array_contains <json_file> <jq_array_expr> <value>
assert_json_array_contains() {
    local json_file="$1"
    local jq_expr="$2"
    local value="$3"
    local label="${4:-json array contains}"
    _ASSERT_COUNT=$((_ASSERT_COUNT + 1))

    if [[ ! -f "$json_file" ]]; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        echo -e "${RED}ASSERT FAILED:${NC} $label: JSON file not found: $json_file" >&2
        return 1
    fi

    local found
    found="$(jq -r "$jq_expr | index(\"$value\") // empty" "$json_file" 2>/dev/null)"
    if [[ -z "$found" ]]; then
        _ASSERT_FAIL=$((_ASSERT_FAIL + 1))
        local contents
        contents="$(jq -r "$jq_expr" "$json_file" 2>/dev/null)"
        echo -e "${RED}ASSERT FAILED:${NC} $label: '$value' not in array ($jq_expr)" >&2
        echo "  Array was: $contents" >&2
        return 1
    fi
}

# assert_completed_nodes <checkpoint_file> <node1> [node2] ...
assert_completed_nodes() {
    local checkpoint_file="$1"; shift
    local nodes=("$@")

    for node in "${nodes[@]}"; do
        assert_json_array_contains "$checkpoint_file" ".completed_nodes" "$node" \
            "completed node '$node'"
    done
}

# assert_node_outcome <logs_dir> <node_id> <expected_outcome>
# Checks the status.json in the node's stage directory.
assert_node_outcome() {
    local logs_dir="$1"
    local node_id="$2"
    local expected="$3"

    local status_file="$logs_dir/$node_id/status.json"
    assert_file_exists "$status_file" "status.json for $node_id"
    assert_json_field "$status_file" ".outcome" "$expected" \
        "outcome of $node_id"
}

# ── Test result helpers ─────────────────────────────────────────────

pass() {
    local msg="${1:-}"
    echo -e "${GREEN}PASS${NC}${msg:+: $msg}"
    exit 0
}

fail() {
    local msg="${1:-}"
    echo -e "${RED}FAIL${NC}${msg:+: $msg}" >&2
    exit 1
}

skip() {
    local msg="${1:-}"
    echo -e "${YELLOW}SKIP${NC}${msg:+: $msg}"
    exit 77
}
