# Checkpoint CLI

Sprint 015 adds first-class checkpoint operations:

```bash
attractor checkpoint inspect <run-dir>
attractor checkpoint mark-done <run-dir> <node-id> [--outcome=success|fail] [--note=...] [--no-backup]
attractor checkpoint set-outcome <run-dir> <node-id> <outcome> [--tool-stdout=...] [--no-backup]
attractor checkpoint diff <run-dir>
attractor checkpoint backup <run-dir>
```

## Run Directory Resolution

- If `<run-dir>` is an explicit `restart-N/` directory, it is used directly.
- Otherwise, if `restart-N/` subdirectories exist, the newest by mtime is used.
- If no restart subdirs exist, `<run-dir>` is used directly.

## Backup Behavior

- `mark-done` and `set-outcome` automatically create `checkpoint.json.bak` unless `--no-backup` is set.
- `backup` creates/overwrites `checkpoint.json.bak` explicitly.

## Mutation Semantics

Mutating commands reserialize through `Engine.saveCheckpoint` and remain loadable by `Engine.loadCheckpoint`.

`mark-done` updates these fields atomically:

- `current_node`
- `completed_nodes`
- `node_outcomes[<node-id>]`
- `context` (including `outcome` and `last_stage`)

`set-outcome` updates:

- `node_outcomes[<node-id>]`
- `context.outcome`
- optional tool output keys when `--tool-stdout` is passed
