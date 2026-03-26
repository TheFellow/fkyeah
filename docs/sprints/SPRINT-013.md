# Sprint 013: Attribute Validation & Schema Completeness

**Status:** Ready
**Codebase:** `src/Attractor/Validation.fs`, `src/Attractor/Types.fs`, `src/Attractor.Cli/Program.fs`
**Depends on:** None (independent of Sprint 012)

## Motivation

A cedar-dotnet semport pipeline used four wrong attribute names (`llm_prompt`, `is_codergen`, `max_agent_turns`, `node_type`) — all silently ignored. The LLM received only the 6-word node label instead of the detailed prompt, hallucinated an entire agent session with fake `<tool_call>`/`<tool_response>` blocks, and attractor treated it as a successful codergen completion. This burned $0.25 and produced zero real work.

Two fkyeah gaps allowed this:
1. `--validate` checks structure (reachability, edge targets, condition syntax) but not attribute names.
2. `attractor schema` omits the `tab`/`coding_agent` handler and its attributes (`max_turns`, `max_tool_rounds`, `command_timeout`, `system_prompt`).

## Phases

### Phase 1: Central attribute registry (Types.fs)

Collect all recognized attribute names into a single source of truth. Currently they're split between:
- Node property accessors: `Types.fs:209-334` (21 attributes: `label`, `shape`, `type`, `prompt`, `max_retries`, `goal_gate`, `retry_target`, `fallback_retry_target`, `fidelity`, `thread_id`, `class`, `timeout`, `llm_model`, `llm_provider`, `reasoning_effort`, `auto_status`, `allow_partial`, `max_visits`, `outcome_fail_pattern`, `tool_hooks.pre`, `tool_hooks.post`)
- Graph property accessors: `Types.fs:394-450` (9 attributes: `goal`, `label`, `model_stylesheet`, `default_fidelity`, `default_max_retries`, `retry_target`, `fallback_retry_target`, `stack.child_dotfile`, `stack.child_workdir`)
- Edge property accessors: `Types.fs:351-385` (6 attributes: `label`, `condition`, `weight`, `fidelity`, `thread_id`, `loop_restart`)
- Handler-specific via `GetAttrString()`: `tool_command`, `human.default_choice`, `system_prompt`, `stop_condition_key`, `observe_key`, `lane`, `cwd`, `max_turns`, `max_tool_rounds`, `command_timeout`, `max_cycles`
- ACP/MCP attributes: `acp_command`, `acp_url`, `acp_transport`, `acp_args_json`, `mcp_server`, `mcp_tool`, `mcp_config_file`
- Graphviz visual attributes (passthrough, never warn): `color`, `fillcolor`, `fontname`, `fontsize`, `style`, `penwidth`, `margin`, `rankdir`, `bgcolor`, `fontcolor`, `width`, `height`, `fixedsize`

Add three `Set<string>` constants to `Types.fs`:
```fsharp
module KnownAttributes =
    let node = set [ "label"; "shape"; "type"; "prompt"; ... ]
    let edge = set [ "label"; "condition"; "weight"; ... ]
    let graph = set [ "goal"; "label"; "model_stylesheet"; ... ]
    let graphvizPassthrough = set [ "color"; "fillcolor"; "fontname"; "fontsize"; "style"; "penwidth"; "margin"; "rankdir"; ... ]
```

**Tests:** Unit test that each property accessor's attribute name appears in the corresponding set.

### Phase 2: Validation warning for unrecognized attributes (Validation.fs)

Add a new warning rule `attribute_known` (after existing rule 18, `max_visits_valid` at line 342):

For each node, check every attribute key against `KnownAttributes.node + KnownAttributes.graphvizPassthrough`. For unrecognized keys, emit a warning with a "did you mean?" suggestion using Levenshtein distance (or simple prefix match) against the known set.

Example output:
```
warning[attribute_known] node "FetchUpstreamSonnet": unrecognized attribute "llm_prompt" (did you mean "prompt"?)
warning[attribute_known] node "FetchUpstreamSonnet": unrecognized attribute "is_codergen" (shape=box already routes to codergen handler)
warning[attribute_known] node "FetchUpstreamSonnet": unrecognized attribute "max_agent_turns" (did you mean "max_turns"?)
warning[attribute_known] node "FetchUpstreamSonnet": unrecognized attribute "node_type" (did you mean "type"?)
```

Same for edge and graph attributes.

**Tests:**
- Conformance test: DOT with `llm_prompt` on a box node emits warning mentioning `prompt`
- Conformance test: DOT with `max_agent_turns` on a tab node emits warning mentioning `max_turns`
- Conformance test: DOT with valid attributes emits no attribute warnings
- Conformance test: DOT with Graphviz visual attributes (`color`, `fillcolor`, etc.) emits no warnings
- Unit test: "did you mean" logic picks correct suggestion for common typos

### Phase 3: Schema completeness (Program.fs)

Update `printSchema()` at `Program.fs:272-285` to add the missing `tab` shape row:

```
#   tab               coding_agent         LLM agent with tool execution (max_turns attr)
```

Add a new section after node attributes (after line 316) documenting coding_agent-specific attributes:

```
# CODING AGENT ATTRIBUTES (tab shape only)
#
#   max_turns          Integer   Maximum agent turns (default: 20)
#   max_tool_rounds    Integer   Maximum tool rounds per input (default: 25)
#   command_timeout    Duration  Timeout per shell command (default: "120s")
#   system_prompt      String    System instructions for the agent session
```

**Tests:** Conformance test that `attractor schema` output contains "tab" and "coding_agent".

## Definition of Done

- [ ] `KnownAttributes` module in Types.fs with node/edge/graph/graphviz sets
- [ ] `attribute_known` validation rule emitting warnings with "did you mean?" suggestions
- [ ] `attractor schema` documents tab shape, coding_agent handler, and its four attributes
- [ ] All existing tests pass (512+)
- [ ] New tests cover: unrecognized attribute warning, suggestion accuracy, graphviz passthrough, schema completeness
- [ ] Zero compiler warnings
