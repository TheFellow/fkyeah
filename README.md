[![CI](https://github.com/TheFellow/fkyeah/actions/workflows/ci.yml/badge.svg)](https://github.com/TheFellow/fkyeah/actions/workflows/ci.yml)

# F#kYeah

An F# implementation of the [StrongDM Attractor](https://github.com/strongdm/attractor) spec — a DOT-based pipeline runner that orchestrates multi-stage AI workflows using directed graphs.

Write a `.dot` file. Each node is a stage (LLM call, shell command, human gate, conditional branch). Edges define the flow. The engine executes it, calls real LLMs, checkpoints after every stage, and lets you resume if anything fails.

Built from three [NLSpecs](https://github.com/strongdm/attractor#terminology):
- [Attractor Specification](https://github.com/strongdm/attractor/blob/main/attractor-spec.md) — the pipeline engine
- [Coding Agent Loop Specification](https://github.com/strongdm/attractor/blob/main/coding-agent-loop-spec.md) — agentic loop with tool execution
- [Unified LLM Client Specification](https://github.com/strongdm/attractor/blob/main/unified-llm-spec.md) — multi-provider LLM client

**~1,020 tests (unit + 208-test conformance suite). Zero warnings. One binary.**

## Quick Start

```bash
# Set at least one API key
export ANTHROPIC_API_KEY=sk-ant-...
export OPENAI_API_KEY=sk-...
export GEMINI_API_KEY=AI...
# OpenRouter model IDs use vendor/model plus llm_provider="openrouter"
export OPENROUTER_API_KEY=sk-or-...

# Learn the DOT schema
attractor schema

# List known models (and their aliases) for llm_model=
attractor models

# See a working example
attractor example

# Validate a pipeline
attractor --validate my-pipeline.dot

# Run it
attractor my-pipeline.dot

# Run fully autonomous (no human prompts)
attractor my-pipeline.dot --auto-approve

# Run without API keys (mock LLM responses)
attractor my-pipeline.dot --simulate

# Resume from checkpoint after interruption
attractor my-pipeline.dot --resume .ai/attractor-logs/20260217-160000
```

## Write a Pipeline

Pipelines are [Graphviz DOT](https://graphviz.org/doc/info/lang.html) digraphs. Nodes have shapes that determine their behavior:

```dot
digraph my_pipeline {
    graph [goal="Build and test a new feature"]

    start [shape=Mdiamond]                              // entry point
    done  [shape=Msquare]                               // exit point

    plan [shape=box, prompt="Plan how to: $goal"]       // LLM call
    implement [shape=box, prompt="Write the code",
               goal_gate=true, retry_target="plan"]     // must succeed to exit
    test [shape=parallelogram,                          // shell command
          tool_command="dotnet test 2>&1 | tail -10",
          timeout="60s"]
    review [shape=hexagon, label="Approve?"]            // human gate
    gate [shape=diamond]                                // conditional branch

    start -> plan -> implement -> test -> gate
    gate -> review      [condition="outcome=success"]
    gate -> implement   [condition="outcome=fail"]
    review -> done      [label="[A] Approve"]
    review -> plan      [label="[R] Revise"]
}
```

Run `attractor schema` for the complete attribute reference.

### Node Shapes

| Shape | Type | What it does |
|-------|------|-------------|
| `Mdiamond` | start | Entry point (exactly one required) |
| `Msquare` | exit | Terminal node (exactly one required) |
| `box` | codergen | Single-turn LLM call — sends `prompt` to the model, writes response to logs. Auto-promoted to coding agent when agent attributes are present (`max_turns`, `cwd`, `thread_id`) |
| `tab` | coding_agent | Multi-turn LLM agent with tool execution (file I/O, shell, grep). Use for any node that needs to read/write files or run commands |
| `parallelogram` | tool | Runs `tool_command` in a shell, captures stdout/stderr |
| `hexagon` | wait.human | Pauses for human input, shows options from outgoing edge labels |
| `diamond` | conditional | Pass-through — engine evaluates edge conditions to pick next node |
| `component` | parallel | Fan-out — executes all branch targets concurrently |
| `tripleoctagon` | fan_in | Fan-in — consolidates parallel results |
| `house` | manager_loop | Bounded supervisor loop (observe/steer/wait cycles) |

### Key Attributes

```
graph [goal="...", model_stylesheet="* { llm_model: claude-sonnet-5; }",
       default_fidelity="compact", default_max_retry=2]
node  [prompt="...", goal_gate=true, retry_target="node_id", timeout="30s", auto_status=true]
edge  [condition="outcome=success", label="[A] Approve", weight=10, loop_restart=true]
```

Coding agent nodes (`tab`, or `box` with auto-promotion) additionally support:

```
node [max_turns=40, max_tool_rounds=25, cwd=".", thread_id="review", system_prompt="...",
      acp_preset="codex", command_timeout=120000]
```

LLM nodes can declare a post-stage contract via `success_criteria_command`. When a
stage would otherwise succeed, the engine runs the command via `/bin/sh -c` in the
node's working directory. Exit 0 keeps Success and captures stdout into
`contract_check_output`; non-zero exit or `success_criteria_timeout_ms` expiry
overrides the outcome to Fail with the command's combined output recorded. The
subprocess inherits `ATTRACTOR_*` env vars plus every graph-level attribute.

```
node [prompt="...", success_criteria_command="dotnet test ./MyPackage.Tests",
      success_criteria_timeout_ms=60000]
```

`thread_id` is a session partition key — nodes sharing the same `thread_id` share a persistent session (conversation history, tool state). Nodes with different `thread_id` values get independent sessions. Give late-pipeline nodes their own `thread_id` to avoid cumulative turn exhaustion.

### Edge Conditions

```
condition="outcome=success"                                          // status check
condition="outcome=fail"
condition="outcome=partial_success"                                  // partial success
condition="outcome=success && context.tests=passed"                  // compound
condition="context.ready=true"                                       // context variable
condition="internal.loop_restart_count=3"                            // bail after 3 restarts
condition="tool_exit_code=0"                                         // tool output alias
```

### Context Reset with `loop_restart`

Edges with `loop_restart=true` restart the pipeline with fresh logs and context. All context keys are cleared except `graph.*` keys, which persist across restarts. This is useful for retry-from-scratch loops.

### Auto Status

Tool nodes with `auto_status=true` automatically synthesize a Success outcome if the tool exits successfully but doesn't write a `status.json` file. This simplifies pipelines where tools don't need to communicate structured results back to the engine.

### Graph attributes as env vars

`tool_command` subprocesses inherit every graph-level attribute as an environment variable, so you can parameterize pipelines by declaring config at the top and referencing it as `$my_var` in shell commands (built-in `ATTRACTOR_*` vars win on collision):

```dot
graph [goal="...", target_package="Billing", planning_dir=".ai/billing"]
init [shape=parallelogram, tool_command="mkdir -p \"$planning_dir\" && echo $target_package"]
```

### Model Stylesheet

Assign different LLM models per node using CSS-like selectors:

```
model_stylesheet="* { llm_model: claude-sonnet-5; }
                  .critical { llm_model: claude-opus-4-8; }
                  #review { llm_model: gemini-3.1-pro-preview-customtools; }"
```

Specificity: `*` < shape < `.class` < `#id`. Node attributes override stylesheet.

## Writing Clean DOT Files

A few conventions that keep pipelines readable and maintainable as they grow.

### Put configuration at the top

Collect `graph` attributes, `node` defaults, `edge` defaults, and the model stylesheet into a single block at the top. Readers should see the pipeline's goal, model routing, and defaults before any node declarations.

```dot
digraph my_pipeline {
    // --- Configuration ---
    graph [
        goal="Implement the billing API",
        label="Billing Sprint",
        model_stylesheet="* { llm_model: claude-sonnet-5; }
                          .critical { llm_model: claude-opus-4-8; }
                          #final_review { llm_model: gemini-3.1-pro-preview-customtools; }",
        default_fidelity="truncate",
        default_max_retry=2
    ]
    node [shape=box]
    edge [weight=1]

    // --- Lifecycle ---
    start [shape=Mdiamond]
    done  [shape=Msquare]

    // --- Stages ---
    ...
}
```

### Group nodes by phase

Use `//` comments to separate phases visually. Name nodes by what they do, not what they are (`orient` not `llm_step_1`).

```dot
    // Phase 1: Discovery
    gather_context [shape=parallelogram, tool_command="..."]
    orient [shape=box, class="critical", prompt="..."]

    // Phase 2: Planning
    decompose [shape=box, prompt="..."]
    critique [shape=box, class="critical", prompt="..."]

    // Phase 3: Execution
    implement [shape=tab, class="deep", cwd=".", max_turns="60", prompt="Implement the plan"]
    run_tests [shape=parallelogram, tool_command="dotnet test ...", timeout="120s"]
```

### Put edges at the bottom

Separate node declarations from edge declarations. This makes the flow scannable at a glance — nodes describe *what*, edges describe *when*.

```dot
    // --- Flow ---
    start -> gather_context -> orient -> decompose -> critique
    critique -> implement        [condition="outcome=success"]
    critique -> decompose        [condition="outcome=fail", label="Revise"]
    implement -> run_tests -> done
```

### Quote duration values

Attractor parses bare durations like `timeout=60s`, but Graphviz's `dot` renderer doesn't. Always quote them so the same file works with both `attractor --validate` and `dot -Tpng`:

```dot
    timeout="60s"     // works everywhere
    timeout=60s       // breaks Graphviz rendering
```

### Comments inside quoted strings are safe

The parser respects quoted strings — `//` and `/*` inside `"..."` are preserved, not stripped as comments. URLs, globs, and shell patterns work correctly:

```dot
    tool_command="curl https://example.com/api"     // works
    tool_command="find . -name '*.go'"               // works
    tool_command="find . -path '*/vendor/*'"         // works
```

### Use the stylesheet instead of per-node model attributes

Don't scatter `llm_model` across every node. Use `model_stylesheet` to assign models by class, shape, or ID. This makes it easy to change models in one place.

```dot
    // BAD — model repeated on every node
    plan [shape=box, llm_model="claude-opus-4-8", prompt="..."]
    implement [shape=box, llm_model="claude-opus-4-8", prompt="..."]
    review [shape=box, llm_model="claude-sonnet-5", prompt="..."]

    // GOOD — one stylesheet, nodes just declare their class
    graph [model_stylesheet=".deep { llm_model: claude-opus-4-8; } * { llm_model: claude-sonnet-5; }"]
    plan [shape=box, class="deep", prompt="..."]
    implement [shape=box, class="deep", prompt="..."]
    review [shape=box, prompt="..."]
```

### Use goal gates and retry targets for quality control

Mark critical stages with `goal_gate=true` so the pipeline can't exit until they succeed. Pair with `retry_target` to create automatic recovery loops:

```dot
    implement [shape=box, prompt="...", goal_gate=true, retry_target="plan"]
    // If implement fails, pipeline redirects to plan instead of exiting
```

### Use `--validate` before running

Always validate before execution. The validator catches structural issues (unreachable nodes, dead ends, missing terminals), syntax errors, and warns about common misconfigurations. The synopsis tells you whether the pipeline will actually produce code changes or just generate documents.

```bash
attractor --validate my-pipeline.dot
```

## Validation

```bash
$ attractor --validate sprint-execute.dot

Nodes: 21 | Edges: 27 | Goal: Implement a new feature

Synopsis:
  EXECUTION pipeline — will invoke coding agents and run commands to produce code changes
  Capabilities: [LLM | TOOLS | CODE_CHANGES | HUMAN_GATES | GOAL_GATES | FEEDBACK_LOOPS | CONDITIONALS]
  Stages: 7 LLM, 7 tool, 2 human, 3 conditional, 0 parallel
```

The validator checks structure (reachability, dead ends, terminal paths), syntax (conditions, stylesheet), and classifies the pipeline:

- **EXECUTION** — invokes coding agents (`tab` nodes or ACP presets) to produce code
- **PLANNING** — LLM-only, generates docs/plans but no code changes
- **HYBRID** — runs tool commands but no coding agent detected

## Examples

The `examples/` directory contains 17 reference pipelines imported from [Swift OmniKit](https://github.com/navanchauhan/swift-omnikit), ranging from a zero-LLM vulnerability scanner to a 52-node megaplan with 6-branch cross-model critique:

| Start with | What it shows |
|------------|---------------|
| `vulnerability_analyzer.dot` | Pure tool pipeline, no LLM. Best "hello world" |
| `consensus_task.dot` | Multi-model planning with goal gate and retry |
| `fix_sheets.dot` | Simplest instance of the 9-phase execution template |
| `cedar_spec_port.dot` | Advanced: model stylesheet, nested critique, complex retry |
| `megaplan_quality.dot` | Advanced: human gates, 6-branch fan-out, quality gates |

Also available:

```bash
attractor example       # print a template pipeline to stdout
attractor schema        # print the complete DOT attribute reference
```

## Artifacts

Each run writes to `.ai/attractor-logs/<timestamp>/`:

```
manifest.json                  // pipeline name, goal, start time
checkpoint.json                // crash recovery (resume with --resume)
<node_id>/prompt.md            // LLM prompt sent
<node_id>/response.md          // LLM response received
<node_id>/status.json          // outcome, context updates
<node_id>/tool_output.txt      // full tool stdout
<node_id>/tool_stderr.txt      // tool stderr (if any)
```

## Architecture

Six F# libraries targeting .NET 10.0:

```
src/Attractor/          16 modules — pipeline engine, DOT parser, handlers, validation
src/UnifiedLlm/         16 modules — multi-provider LLM client (Anthropic, OpenAI, Gemini, OpenRouter)
src/CodingAgent/        10 modules — agentic loop, tool execution, provider profiles
src/AcpRuntime/          6 modules — Agent Client Protocol (stdio/WebSocket/HTTP+SSE)
src/McpClient/           5 modules — Model Context Protocol client
src/JsonRpc/             3 modules — JSON-RPC transport layer
src/Attractor.Cli/       1 module  — CLI binary
tests/                   812 tests across 6 test projects
conformance/             208 tests — black-box CLI conformance suite
examples/                17 reference DOT pipelines
```

### Build from Source

```bash
dotnet build Attractor.slnx          # build everything
dotnet test Attractor.slnx           # run all unit tests
make install                          # publish + install to ~/bin/attractor
make format                           # Fantomas formatter (auto-fix)
make lint                             # FSharpLint (style/quality rules)
make analyze                          # G-Research.FSharp.Analyzers
make conformance                      # full 208-test conformance suite
```

CI gates `format-check`, `lint`, and `analyze` in addition to build + test; PRs with unformatted code, lint warnings, or analyzer findings fail.

### Runtime tuning

```
ATTRACTOR_LLM_INACTIVITY_TIMEOUT_SECONDS     SSE idle timeout per stream.
                                              Default 120. Also aliased as
                                              ATTRACTOR_AGENT_INACTIVITY_TIMEOUT_SECONDS.
```

## For LLM Agents

If you're a coding agent and need to generate an Attractor pipeline:

1. Run `attractor schema` to get the complete DOT schema reference
2. Run `attractor models` to see supported `llm_model=` values and aliases
3. Run `attractor example` to see a valid pipeline that exercises most features
4. Write your `.dot` file
5. Run `attractor --validate your-file.dot` to check it
6. Run `attractor your-file.dot --auto-approve` to execute it

The schema output is designed to fit in your context window and covers every attribute, shape, condition syntax, edge selection algorithm, validation rule, and artifact path.

## License

MIT. See [LICENSE](./LICENSE).

Built on the [StrongDM Attractor](https://github.com/strongdm/attractor) specification.
