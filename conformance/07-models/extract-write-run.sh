#!/usr/bin/env bash
# extract-write-run.sh — Extract named files from LLM response, write to disk, run entry point
# Usage: extract-write-run.sh <language> <source_node_id>
#
# Parses the LLM response for multiple fenced code blocks with filename annotations.
# Recognizes:
#   utils.py           ← filename on preceding line
#   ```python
#   ...code...
#   ```
# or:
#   ```python main.py  ← filename on fence line
#   ...code...
#   ```
set -euo pipefail

LANG="$1"
SOURCE_NODE="$2"
RESPONSE="$ATTRACTOR_LOGS_ROOT/$SOURCE_NODE/response.md"
WORKDIR="$ATTRACTOR_STAGE_DIR"

# Use awk to split on filename-annotated code blocks and write each file
awk '
/^```/ {
    if (in_block) {
        in_block = 0
        if (filename != "") close(outdir "/" filename)
        filename = ""
        next
    }
    in_block = 1
    # Check for filename on fence line: ```lang filename.ext
    n = split($0, parts, /[[:space:]]+/)
    for (i = 2; i <= n; i++) {
        if (parts[i] ~ /^[a-zA-Z0-9_.-]+\.[a-zA-Z]+$/) {
            filename = parts[i]
            break
        }
    }
    # If no filename on fence line, use the pending one from a preceding line
    if (filename == "" && pending != "") filename = pending
    pending = ""
    next
}
in_block && filename != "" {
    print $0 >> (outdir "/" filename)
    next
}
!in_block {
    # Look for a filename-like pattern: "main.py", "# main.py", "**utils.py**", "`main.py`:"
    line = $0
    gsub(/^[#*` ]+/, "", line)
    gsub(/[*:` ]+$/, "", line)
    if (line ~ /^[a-zA-Z0-9_.-]+\.[a-zA-Z]+$/) {
        pending = line
    } else {
        pending = ""
    }
}
' outdir="$WORKDIR" "$RESPONSE"

# Log written files for debugging
echo "Written files:" >&2
ls -1 "$WORKDIR" >&2 2>/dev/null || true

# Run the entry point
case "$LANG" in
    python)
        if [[ -f "$WORKDIR/main.py" ]]; then
            (cd "$WORKDIR" && python3 main.py)
        else
            first=$(ls "$WORKDIR"/*.py 2>/dev/null | head -1)
            [[ -n "$first" ]] && (cd "$WORKDIR" && python3 "$(basename "$first")")
        fi
        ;;
    javascript|js)
        if [[ -f "$WORKDIR/main.js" ]]; then
            (cd "$WORKDIR" && node main.js)
        else
            first=$(ls "$WORKDIR"/*.js 2>/dev/null | head -1)
            [[ -n "$first" ]] && (cd "$WORKDIR" && node "$(basename "$first")")
        fi
        ;;
esac
