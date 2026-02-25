# 02-claude-sonnet-4-6 / 08-python-multi-file

E2E smoke test: `claude-sonnet-4-6` generates multiple Python files, a tool node extracts and writes them to disk, runs the entry point, and the test validates both the written files and expected output. Skips if `ANTHROPIC_API_KEY` is not set.
