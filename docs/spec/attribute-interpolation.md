# Attribute Interpolation and Session Attributes

Sprint 015 adds runtime interpolation for selected node attributes. Interpolation happens at execution time, immediately before the handler runs.

## Supported Attributes

Interpolation is applied only to:

- `thread_id`
- `prompt`
- `cwd`
- `tool_command`

Raw attribute values in the parsed DOT graph are not rewritten globally.

## Syntax

Use `${...}` placeholders.

- `${context.foo}` resolves key `context.foo`
- `${internal.loop_restart_count}` resolves key `internal.loop_restart_count`
- `${foo}` is shorthand for `${context.foo}`

If a key is missing, the placeholder is preserved literally.

## Escape Rule

Use `$${...}` to emit a literal `${...}` without resolution.

Examples:

- `$${foo}` -> `${foo}`
- `echo "$${foo} ${context.value}"` with `context.value=ok` -> `echo "${foo} ok"`

## `fresh_session`

`fresh_session=true` generates a unique thread ID per node invocation:

`<node-id>-<utc-epoch-ms>-<pid>`

When `fresh_session=true`, the generated value overrides any explicit `thread_id`.

Validation rule:

- `conflicting_session_attrs` (error): emitted when `fresh_session=true` and a non-empty explicit `thread_id` are both set on the same node.
