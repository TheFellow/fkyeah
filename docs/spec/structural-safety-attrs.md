# Structural Safety Attributes

Sprint 015 introduces node attributes that enforce pre/post safety checks without extra boilerplate graph nodes.

## `requires_green_build`

`requires_green_build="<command>"`

Execution order:

1. Run `<command>` before the node's primary handler.
2. If the command exits non-zero, the node fails with:
   `pre-condition failed: <command> exited <code>`
3. The primary handler is skipped.

Command output is captured in context via tool keys, including `tool_output` / `tool_stderr` and existing `tool_*` variants.

## `scope_gate`, `scope_revert`, `scope_gate_max_retries`

`scope_gate="<command>"`
`scope_revert="<command>"` (optional)
`scope_gate_max_retries=<int>` (default `1`)

Execution order:

1. Run the node's primary handler.
2. If the primary handler succeeds, run `scope_gate`.
3. If `scope_gate` fails:
   - run `scope_revert` when provided (best effort),
   - re-run the primary handler while retry budget remains,
   - re-check `scope_gate` after each successful re-run.
4. If all attempts fail, outcome is:
   `scope_gate rejected changes after N attempts`

If `scope_gate` is unset, no post-success gate behavior is added.

## Validation Interaction

Two Sprint 014 warnings are suppressed when structural attributes are present:

- `scope_gate_coverage` is suppressed when the relevant file-editing node declares non-empty `scope_gate`.
- `partial_commit_needs_build_gate` is suppressed when the commit-like target node declares non-empty `requires_green_build`.
