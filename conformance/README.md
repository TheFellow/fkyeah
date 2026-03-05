# Attractor Conformance Test Suite

External black-box conformance tests for the `attractor` CLI binary. Categories 01-06 run in `--simulate` mode (no API keys, deterministic). Categories 07-08 perform live API smoke tests, skipping gracefully when API keys aren't set.

## Quick Start

```bash
# Run the full suite
./conformance/run-all.sh

# Run a single category
./conformance/run-all.sh 01-parsing

# Run a single test
./conformance/run-all.sh 03-execution/04-tool-success

# Run one model's tests
./conformance/run-all.sh 07-models/01-claude-opus-4-6
```

## Docker (Recommended for CI)

The Docker container pins all language runtimes for reproducible runs.

```bash
# Build and run the full suite
./conformance/docker-run.sh

# Run just the model matrix
./conformance/docker-run.sh 07-models

# Run one model's tests
./conformance/docker-run.sh 07-models/01-claude-opus-4-6

# Run a single scenario across one model
./conformance/docker-run.sh 07-models/03-gpt-5-2/05-c-sum-integers
```

API keys are forwarded automatically from the host environment.

## Requirements

### Local (without Docker)
- `bash` 4+
- `jq` for JSON assertions
- `timeout` (coreutils)
- `attractor` binary (default: `~/bin/attractor`, override with `ATTRACTOR_BIN`)
- For 07-models e2e tests: `python3`, `node`, `go`, `ruby`, `gcc`, `bash`

### Docker
- Docker

## Structure

```
conformance/
  lib.sh              # Shared helpers (setup, assertions, cleanup)
  run-all.sh          # Runner: discovers tests, runs them, prints summary
  Dockerfile          # Multi-stage: build attractor + all runtimes
  docker-run.sh       # Convenience: build image, forward API keys, run
  README.md           # This file
  01-parsing/         # 9 tests — DOT parser acceptance
  02-validation/      # 12 tests — lint rules and synopsis classification
  03-execution/       # 16 tests — engine behavior (tools, gates, routing)
  04-context/         # 6 tests — context propagation and edge conditions
  05-parallel/        # 2 tests — fan-out/fan-in
  06-artifacts/       # 5 tests — manifest, checkpoint, stage files, outcomes
  07-models/          # 72 tests — model matrix using codergen + tool extract/run
  08-coding-agent/    # 6 tests — live coding-agent tests (real file I/O via shape=tab)
    extract-run.sh    # Shared code extractor for all model tests
    01-claude-opus-4-6/
      01-python-fizzbuzz/
      02-javascript-fibonacci/
      03-go-reverse-string/
      04-ruby-factorial/
      05-c-sum-integers/
      06-bash-prime-checker/
      07-python-temp-converter/
      08-python-multi-file/
    02-claude-sonnet-4-6/
      ...  (same 8 scenarios)
    03-claude-sonnet-4-5/
    04-gpt-5-2/
    05-gpt-5-1-codex-mini/
    06-gpt-5-2-codex/
    07-gemini-3.1-pro-preview/
    08-gemini-3-flash-preview/
    09-gpt-5-3-codex/
```

## Test Matrix (07-models)

Every model proves it can generate working software in every language/scenario:

| Model | Provider | API Key |
|-------|----------|---------|
| claude-opus-4-6 | Anthropic | `ANTHROPIC_API_KEY` |
| claude-sonnet-4-6 | Anthropic | `ANTHROPIC_API_KEY` |
| claude-sonnet-4-5 | Anthropic | `ANTHROPIC_API_KEY` |
| gpt-5.2 | OpenAI | `OPENAI_API_KEY` |
| gpt-5.1-codex-mini | OpenAI | `OPENAI_API_KEY` |
| gpt-5.2-codex | OpenAI | `OPENAI_API_KEY` |
| gemini-3.1-pro-preview | Gemini | `GEMINI_API_KEY` |
| gemini-3-flash-preview | Gemini | `GEMINI_API_KEY` |
| gpt-5.3-codex | OpenAI | `OPENAI_API_KEY` |
| gpt-5.4 | OpenAI | `OPENAI_API_KEY` |

| Scenario | Language | Problem | Expected Output |
|----------|----------|---------|-----------------|
| 01-python-fizzbuzz | Python | FizzBuzz 1-20 | "FizzBuzz", "Fizz", "Buzz" |
| 02-javascript-fibonacci | JavaScript | First 10 Fibonacci | "34", "13" |
| 03-go-reverse-string | Go | Reverse "Hello, World!" | "!dlroW ,olleH" |
| 04-ruby-factorial | Ruby | Factorial of 10 | "3628800" |
| 05-c-sum-integers | C | Sum 1-100 | "5050" |
| 06-bash-prime-checker | Bash | Is 97 prime? | "prime" |
| 07-python-temp-converter | Python | 100C to F | "212" |
| 08-python-multi-file | Python | Multi-file project | imports work across files |

Each test is a folder containing:

| File | Purpose |
|------|---------|
| `pipeline.dot` | The DOT pipeline under test |
| `test.sh` | Test script (sources `lib.sh`, runs assertions) |
| `README.md` | What this test validates |

## Environment Variables

| Variable | Default | Purpose |
|----------|---------|---------|
| `ATTRACTOR_BIN` | `~/bin/attractor` | Path to attractor binary |
| `CONFORMANCE_TIMEOUT` | `30` | Per-test timeout in seconds |
| `ANTHROPIC_API_KEY` | — | Required for Anthropic model smoke tests (07-models/08-coding-agent) |
| `OPENAI_API_KEY` | — | Required for OpenAI model smoke tests (07-models/08-coding-agent) |
| `GEMINI_API_KEY` | — | Required for Gemini model smoke tests (07-models/08-coding-agent) |

## Adding a New Test

1. Create a folder under the appropriate category: `conformance/03-execution/13-my-test/`
2. Add `pipeline.dot` with the pipeline to test
3. Add `test.sh` sourcing the shared library:

```bash
#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

run_pipeline "$TEST_DIR/pipeline.dot"
assert_exit_code 0 "$PIPELINE_EXIT"
assert_file_exists "$LOGS_DIR/manifest.json"

pass "my test description"
```

4. Add `README.md` documenting what the test validates
5. Run it: `./conformance/run-all.sh 03-execution/13-my-test`

## Regenerating the Model Matrix

To regenerate all 72 model tests from the template:

```bash
cd conformance/07-models
bash _generate.sh
```

## Available Assertions

| Function | Arguments | Description |
|----------|-----------|-------------|
| `run_pipeline` | `<dot_file> [args...]` | Run attractor with `--simulate --auto-approve --quiet` |
| `run_pipeline_live` | `<dot_file> [args...]` | Run attractor with `--auto-approve --quiet` (real API calls) |
| `run_validate` | `<dot_file>` | Run `attractor --validate` |
| `require_env` | `<var_name>` | Skip test if environment variable is not set |
| `assert_exit_code` | `<expected> <actual>` | Check process exit code |
| `assert_file_exists` | `<path>` | Check file exists |
| `assert_dir_exists` | `<path>` | Check directory exists |
| `assert_contains` | `<string> <needle>` | Fixed-string match in output |
| `assert_contains_regex` | `<string> <pattern>` | Regex match in output |
| `assert_not_contains` | `<string> <needle>` | Absence check |
| `assert_json_field` | `<file> <jq_expr> <value>` | JSON field equals value |
| `assert_json_field_exists` | `<file> <jq_expr>` | JSON field is present and non-null |
| `assert_json_array_contains` | `<file> <jq_expr> <value>` | Value is in JSON array |
| `assert_completed_nodes` | `<checkpoint> <node...>` | Nodes appear in completed_nodes |
| `assert_node_outcome` | `<logs_dir> <node> <outcome>` | Node status.json has expected outcome |
| `pass` | `[message]` | Mark test as passed (exit 0) |
| `fail` | `[message]` | Mark test as failed (exit 1) |
| `skip` | `[message]` | Mark test as skipped (exit 77) |

## CI

The suite runs nightly via `.github/workflows/conformance.yml` using Docker for reproducible builds. Any failure fails the workflow.
