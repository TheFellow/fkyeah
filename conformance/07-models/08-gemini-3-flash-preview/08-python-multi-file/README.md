# 08-gemini-3-flash-preview / 08-python-multi-file

E2E smoke test: `gemini-3-flash-preview` generates multiple Python files, a tool node extracts and writes them to disk, runs the entry point, and the test validates both the written files and expected output. Skips if `GEMINI_API_KEY` is not set.
