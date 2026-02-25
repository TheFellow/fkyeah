#!/usr/bin/env bash
# conformance/run-all.sh — Test runner for attractor conformance suite
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TIMEOUT="${CONFORMANCE_TIMEOUT:-30}"
CODING_AGENT_TIMEOUT="${CONFORMANCE_TIMEOUT_CODING_AGENT:-180}"

# ── Colors ──────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
BOLD='\033[1m'
NC='\033[0m'

# ── Counters ────────────────────────────────────────────────────────
TOTAL=0
PASSED=0
FAILED=0
SKIPPED=0
FAILED_TESTS=()

# ── Discover tests ──────────────────────────────────────────────────
discover_tests() {
    local filter="${1:-}"

    if [[ -n "$filter" ]]; then
        # Filter can be "01-parsing" (category) or "03-execution/04-tool-success" (single test)
        if [[ -f "$SCRIPT_DIR/$filter/test.sh" ]]; then
            echo "$SCRIPT_DIR/$filter/test.sh"
        elif [[ -d "$SCRIPT_DIR/$filter" ]]; then
            find "$SCRIPT_DIR/$filter" -name "test.sh" -type f | sort
        else
            echo "Error: no tests match filter '$filter'" >&2
            exit 1
        fi
    else
        find "$SCRIPT_DIR" -path "*/[0-9][0-9]-*/test.sh" -type f | sort
    fi
}

# ── Run a single test ───────────────────────────────────────────────
run_test() {
    local test_script="$1"
    local test_dir
    test_dir="$(dirname "$test_script")"
    local test_name
    test_name="${test_dir#"$SCRIPT_DIR"/}"
    local effective_timeout="$TIMEOUT"
    if [[ "$test_name" == 08-coding-agent/* ]]; then
        effective_timeout="$CODING_AGENT_TIMEOUT"
    fi

    TOTAL=$((TOTAL + 1))

    local exit_code=0
    local output=""

    # Run with timeout
    output="$(cd "$test_dir" && timeout "$effective_timeout" bash test.sh 2>&1)" || exit_code=$?

    if [[ $exit_code -eq 0 ]]; then
        PASSED=$((PASSED + 1))
        echo -e "  ${GREEN}PASS${NC}  $test_name"
    elif [[ $exit_code -eq 77 ]]; then
        SKIPPED=$((SKIPPED + 1))
        echo -e "  ${YELLOW}SKIP${NC}  $test_name"
    elif [[ $exit_code -eq 124 ]]; then
        FAILED=$((FAILED + 1))
        FAILED_TESTS+=("$test_name (TIMEOUT)")
        echo -e "  ${RED}FAIL${NC}  $test_name  ${RED}(timeout after ${effective_timeout}s)${NC}"
    else
        FAILED=$((FAILED + 1))
        FAILED_TESTS+=("$test_name")
        echo -e "  ${RED}FAIL${NC}  $test_name  (exit $exit_code)"
        if [[ -n "$output" ]]; then
            echo "$output" | sed 's/^/        /' | tail -10
        fi
    fi
}

# ── Main ────────────────────────────────────────────────────────────
main() {
    local filter="${1:-}"

    echo -e "${BOLD}Attractor Conformance Suite${NC}"
    echo "Binary: ${ATTRACTOR_BIN:-$HOME/bin/attractor}"
    echo ""

    local current_category=""
    while IFS= read -r test_script; do
        local rel_path
        rel_path="${test_script#"$SCRIPT_DIR"/}"
        local category
        category="${rel_path%%/*}"

        if [[ "$category" != "$current_category" ]]; then
            current_category="$category"
            echo -e "\n${BOLD}$category${NC}"
        fi

        run_test "$test_script"
    done < <(discover_tests "$filter")

    # Summary
    echo ""
    echo -e "${BOLD}────────────────────────────────────${NC}"
    echo -e "Total:   $TOTAL"
    echo -e "Passed:  ${GREEN}$PASSED${NC}"
    echo -e "Failed:  ${RED}$FAILED${NC}"
    echo -e "Skipped: ${YELLOW}$SKIPPED${NC}"

    if [[ ${#FAILED_TESTS[@]} -gt 0 ]]; then
        echo ""
        echo -e "${RED}Failed tests:${NC}"
        for t in "${FAILED_TESTS[@]}"; do
            echo "  - $t"
        done
        echo ""
        exit 1
    fi

    echo ""
    exit 0
}

main "$@"
