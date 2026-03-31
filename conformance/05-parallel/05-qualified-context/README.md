# Qualified Context

Two parallel branches both write `tool.output`. The engine should retain the raw key for compatibility and also write `parallel.{nodeId}.{branchId}.{key}` qualified keys so each branch result remains addressable after fan-in.
