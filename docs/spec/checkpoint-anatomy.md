# Checkpoint Anatomy

This document describes the checkpoint format and resume behavior implemented in `src/Attractor/Engine.fs` as of Sprint 014.

For operational mutation commands, see [checkpoint-cli.md](./checkpoint-cli.md).

## Logs Layout and Resume Source

A run writes logs under the configured `logs_root`:

- `manifest.json`
- `checkpoint.json`
- `<node-id>/...` stage artifacts
- `restart-N/` subdirectories created when an edge has `loop_restart=true`

On loop restart, the engine switches its active logs directory to `logs_root/restart-N` and writes subsequent `checkpoint.json` files there.

`attractor --resume <dir> <file.dot>` reads exactly `<dir>/checkpoint.json` via `Engine.loadCheckpoint`. It does not auto-pick the latest `restart-N`. If the last execution happened inside `restart-3/`, resume must point at that directory.

`attractor checkpoint ...` does auto-detect the latest `restart-N/` subdirectory (by mtime) when a parent run directory is passed.

## Fields That Matter for Manual Patching

Serialized checkpoint keys (from `Engine.saveCheckpoint`):

- `current_node`
- `completed_nodes`
- `node_retries`
- `node_outcomes`
- `context`
- `logs`
- `timestamp`

### `current_node` vs `context.current_node`

- `current_node` (top-level) is authoritative for resume routing.
- `context.current_node` is just a context value set during execution. It is not used by `resumeFromCheckpoint` to choose where to continue.

### `completed_nodes`

- Used to reconstruct prior progress display/state.
- On resume, these entries are copied into the in-memory completed list.

### `node_outcomes[<node-id>]`

Each outcome object may include:

- `status`
- `preferred_label`
- `suggested_next_ids`
- `context_updates`
- `notes`
- `failure_reason`

On resume, the engine selects the successor edge from `current_node` using `node_outcomes[current_node]` when present; if missing, it assumes success for routing.

### `context.outcome` and `context.tool_*`

`context` is restored verbatim before resume.

Common keys relevant to manual patching include:

- `outcome`
- `tool_stdout`
- `tool_stderr`
- `tool_exit_code`
- `tool.output`
- `tool.stderr`

These are context values only; they do not replace top-level `node_outcomes` for edge routing.

## Resume Semantics

`resumeFromCheckpoint` computes the next edge from `current_node` and advances to that successor node.

In other words, resume continues *after* `current_node` by evaluating its outgoing edges against the recorded/restored outcome and context.

If no edge matches, resume falls back to re-entering `current_node`.

## Disambiguation vs Pitfall #7

`~/.claude/skills/attractor/SKILL.md` pitfall #7 says to never shell out to LLM CLIs inside in-graph `parallelogram` `tool_command` nodes.

That rule still applies. This checkpoint guide is about post-failure recovery mechanics, not a recommendation to embed external LLM CLI calls into the graph.
