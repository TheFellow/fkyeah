# External Shim Recovery (Manual)

This is a break-glass recovery protocol for runs that cannot self-recover in-graph (session pollution, repeated parser failures, provider instability).

Use this as an operational recovery tool, not as a normal design pattern.

## Protocol

1. Stop the running attractor process.
2. Preserve and inspect plan/scope artifacts in the active logs directory.
3. Run the coding step externally (for example `codex exec` or another out-of-graph tool) against the same plan.
4. Verify the working tree builds/tests before resuming.
5. Patch `checkpoint.json` to mark the stuck node as completed/successful.
6. Resume with `attractor --resume <logs-dir> <pipeline.dot>`.

## Minimal Checkpoint Patch Script

```python
#!/usr/bin/env python3
import json
import pathlib
import sys


def patch_checkpoint(path: str) -> None:
    checkpoint_path = pathlib.Path(path)
    data = json.loads(checkpoint_path.read_text())

    current = data.get("current_node")
    if not current:
        raise SystemExit("checkpoint missing current_node")

    completed = data.setdefault("completed_nodes", [])
    if current not in completed:
        completed.append(current)

    outcomes = data.setdefault("node_outcomes", {})
    prior = outcomes.get(current, {}) if isinstance(outcomes.get(current, {}), dict) else {}
    outcomes[current] = {
        "status": "success",
        "preferred_label": prior.get("preferred_label", ""),
        "suggested_next_ids": prior.get("suggested_next_ids", []),
        "context_updates": prior.get("context_updates", {}),
        "notes": "manual external shim recovery",
        "failure_reason": "",
    }

    context = data.setdefault("context", {})
    context["outcome"] = "success"
    context["tool_exit_code"] = "0"

    checkpoint_path.write_text(json.dumps(data, indent=2) + "\n")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: patch_checkpoint.py <checkpoint.json>")

    patch_checkpoint(sys.argv[1])
```

## Safety Notes

- Always keep a backup copy of `checkpoint.json` before patching.
- Patch only the active logs directory you will pass to `--resume`.
- Re-run build/test checks after any external shim edits.

## Disambiguation vs Pitfall #7

`~/.claude/skills/attractor/SKILL.md` pitfall #7 forbids embedding LLM CLI shells inside graph `parallelogram` `tool_command` nodes.

This recovery shim is different: it is an out-of-graph manual operation used after a run has already failed/stalled.
