# 23-manager-loop

Validates `stack.manager_loop` in three modes:

1. Polling mode - max-cycle failure and stop-key success via checkpoint resume
2. Child pipeline mode - loads and executes a child DOT file, verifies context propagation and retry behavior on child failure
3. Carried context - parent seeds work product, child pipeline consumes it and produces output, and child context propagates back to parent
