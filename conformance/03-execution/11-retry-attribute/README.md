# 11-retry-attribute

Validates that tool failures (non-zero exit) produce `Outcome.Fail` and are **not retried**, even when `max_retries` is set. Fail outcomes are deterministic and stop immediately.
