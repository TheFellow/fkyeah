# 06-gpt-5-2-codex / 08-python-multi-file

E2E smoke test: `gpt-5.2-codex` generates multiple Python files, a tool node extracts and writes them to disk, runs the entry point, and the test validates both the written files and expected output. Skips if `OPENAI_API_KEY` is not set.
