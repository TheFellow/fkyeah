#!/usr/bin/env bash
# _generate.sh — Generate the 9x8 model × scenario test matrix
# Run once from the 07-models/ directory, then optionally delete this script.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# ── Models ─────────────────────────────────────────────────────────
# Format: "num|model_id|graph_slug|api_key_var"
MODELS=(
    "01|claude-opus-4-6|claude_opus|ANTHROPIC_API_KEY"
    "02|claude-sonnet-4-6|claude_sonnet_4_6|ANTHROPIC_API_KEY"
    "03|claude-sonnet-4-5|claude_sonnet|ANTHROPIC_API_KEY"
    "04|gpt-5.2|gpt_5_2|OPENAI_API_KEY"
    "05|gpt-5.1-codex-mini|gpt_5_1_codex_mini|OPENAI_API_KEY"
    "06|gpt-5.2-codex|gpt_5_2_codex|OPENAI_API_KEY"
    "07|gemini-3.1-pro-preview|gemini_3_1_pro|GEMINI_API_KEY"
    "08|gemini-3-flash-preview|gemini_3_flash|GEMINI_API_KEY"
    "09|gpt-5.3-codex|gpt_5_3_codex|OPENAI_API_KEY"
)

# ── Scenarios ──────────────────────────────────────────────────────
# Format: "num|dir_slug|language|pass_label|goal|prompt|assert_lines[|script[|assert_files]]"
# assert_lines uses semicolons to separate multiple assertions
# script: helper script name (default: extract-run), e.g. "extract-write-run"
# assert_files: semicolon-separated filenames that must exist on disk after run
# Prompts must not contain pipe characters
SCENARIOS=(
    '01|01-python-fizzbuzz|python|Python FizzBuzz|Generate and run a Python FizzBuzz program|Write a Python program that prints numbers from 1 to 20, one per line. For multiples of 3 print Fizz instead of the number, for multiples of 5 print Buzz, for multiples of both print FizzBuzz. Output ONLY the code inside a single fenced code block. No explanations.|FizzBuzz;Fizz;Buzz'
    '02|02-javascript-fibonacci|javascript|JavaScript Fibonacci|Generate and run a JavaScript Fibonacci program|Write a JavaScript program that prints the first 10 Fibonacci numbers (0,1,1,2,3,5,8,13,21,34), one number per line. Output ONLY the code inside a single fenced code block. No explanations.|34;13'
    '03|03-go-reverse-string|go|Go reverse-string|Generate and run a Go reverse-string program|Write a Go program that reverses the string "Hello, World!" and prints the result. Output ONLY the code inside a single fenced code block. No explanations.|!dlroW ,olleH'
    '04|04-ruby-factorial|ruby|Ruby factorial|Generate and run a Ruby factorial program|Write a Ruby program that computes the factorial of 10 and prints the result. Output ONLY the code inside a single fenced code block. No explanations.|3628800'
    '05|05-c-sum-integers|c|C sum-of-integers|Generate and run a C sum-of-integers program|Write a C program that sums the integers from 1 to 100 and prints the result. Output ONLY the code inside a single fenced code block. No explanations.|5050'
    '06|06-bash-prime-checker|bash|Bash prime-checker|Generate and run a Bash prime-checker script|Write a Bash script that checks whether 97 is a prime number and prints either "97 is prime" or "97 is not prime". Output ONLY the code inside a single fenced code block. No explanations.|prime'
    '07|07-python-temp-converter|python|Python temp-converter|Generate and run a Python temperature converter|Write a Python program that converts 100 degrees Celsius to Fahrenheit and prints exactly "100C = 212.0F". Output ONLY the code inside a single fenced code block. No explanations.|212'
    '08|08-python-multi-file|python|Python multi-file write|Generate a multi-file Python module and write files to disk|Create two Python files. File 1 named utils.py: define a function called greet that takes a name parameter and returns the string "Hello, " followed by the name followed by "!". File 2 named main.py: import greet from utils and print the result of calling greet with "Attractor" as the argument. Output each file in its own fenced code block. Put a line containing just the filename before each code block. No other explanations.|Hello, Attractor!|extract-write-run|utils.py;main.py'
)

# ── Model dir-slug from model_id ───────────────────────────────────
model_dir_slug() {
    local num="$1" model_id="$2"
    local slug
    slug=$(echo "$model_id" | tr '.' '-')
    echo "${num}-${slug}"
}

# ── Escape double quotes for DOT attribute values ──────────────────
dot_escape() {
    echo "$1" | sed 's/"/\\"/g'
}

# ── Generate ───────────────────────────────────────────────────────
total=0

for model_entry in "${MODELS[@]}"; do
    IFS='|' read -r m_num model_id graph_slug api_key_var <<< "$model_entry"
    m_dir="$(model_dir_slug "$m_num" "$model_id")"

    for scenario_entry in "${SCENARIOS[@]}"; do
        IFS='|' read -r s_num s_dir language pass_label goal prompt assertions script_name assert_files <<< "$scenario_entry"

        # Defaults for optional fields
        script_name="${script_name:-extract-run}"
        assert_files="${assert_files:-}"

        test_dir="$SCRIPT_DIR/$m_dir/$s_dir"
        mkdir -p "$test_dir"

        # Escape quotes for DOT format
        dot_prompt="$(dot_escape "$prompt")"

        # Determine env var name for the helper script
        script_env_var="EXTRACT_RUN"
        if [[ "$script_name" == "extract-write-run" ]]; then
            script_env_var="EXTRACT_WRITE_RUN"
        fi

        # ── pipeline.dot ──
        cat > "$test_dir/pipeline.dot" <<DOTEOF
digraph smoke_${graph_slug}_${s_dir//-/_} {
    graph [goal="$goal"]

    start [shape=Mdiamond]
    code  [shape=box, llm_model="$model_id", prompt="$dot_prompt"]
    run   [shape=parallelogram, tool_command="bash \"\$$script_env_var\" $language code"]
    done  [shape=Msquare]

    start -> code
    code  -> run
    run   -> done
}
DOTEOF

        # ── test.sh ──
        # Build output assertion lines from semicolon-separated list
        assert_block=""
        IFS=';' read -ra assert_values <<< "$assertions"
        for val in "${assert_values[@]}"; do
            assert_block+="assert_contains \"\$output\" \"$val\" \"output contains $val\"
"
        done

        # Build file existence assertions for write scenarios
        file_assert_block=""
        if [[ -n "$assert_files" ]]; then
            IFS=';' read -ra file_values <<< "$assert_files"
            for fname in "${file_values[@]}"; do
                file_assert_block+="assert_file_exists \"\$LOGS_DIR/run/$fname\" \"$fname written to disk\"
"
            done
        fi

        # Build export lines for helper scripts
        export_block="export EXTRACT_RUN=\"\$(cd \"\$(dirname \"\$TEST_DIR\")/..\" && pwd)/extract-run.sh\""
        if [[ "$script_name" == "extract-write-run" ]]; then
            export_block+="
export EXTRACT_WRITE_RUN=\"\$(cd \"\$(dirname \"\$TEST_DIR\")/..\" && pwd)/extract-write-run.sh\""
        fi

        cat > "$test_dir/test.sh" <<TESTEOF
#!/usr/bin/env bash
set -euo pipefail
source "\$(dirname "\${BASH_SOURCE[0]}")/../../../lib.sh"
setup

require_env $api_key_var
$export_block

run_pipeline_live "\$TEST_DIR/pipeline.dot"
if [[ "\$PIPELINE_EXIT" -ne 0 ]]; then
    if echo "\$PIPELINE_STDERR" | grep -qiE "does not exist|not supported|not a chat model|model_not_found|not found"; then
        skip "$model_id: model not available"
    fi
fi
assert_exit_code 0 "\$PIPELINE_EXIT"
assert_file_exists "\$LOGS_DIR/code/response.md" "LLM generated code"
assert_node_outcome "\$LOGS_DIR" "code" "success"
assert_file_exists "\$LOGS_DIR/run/tool_output.txt" "program produced output"
${file_assert_block}
output="\$(cat "\$LOGS_DIR/run/tool_output.txt")"
${assert_block}
pass "$model_id: $pass_label generated and validated"
TESTEOF
        chmod +x "$test_dir/test.sh"

        # ── README.md ──
        lang_cap="$(echo "$language" | awk '{print toupper(substr($0,1,1)) substr($0,2)}')"
        scenario_name="${s_dir#*-}"
        if [[ -n "$assert_files" ]]; then
            cat > "$test_dir/README.md" <<READMEEOF
# $m_dir / $s_dir

E2E smoke test: \`$model_id\` generates multiple $lang_cap files, a tool node extracts and writes them to disk, runs the entry point, and the test validates both the written files and expected output. Skips if \`$api_key_var\` is not set.
READMEEOF
        else
            cat > "$test_dir/README.md" <<READMEEOF
# $m_dir / $s_dir

E2E smoke test: \`$model_id\` generates a $lang_cap $scenario_name program, a tool node extracts and runs it, and the test validates the expected output. Skips if \`$api_key_var\` is not set.
READMEEOF
        fi

        total=$((total + 1))
    done
done

echo "Generated $total test directories."
