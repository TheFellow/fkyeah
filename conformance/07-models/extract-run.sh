#!/usr/bin/env bash
# extract-run.sh — Extract code from an LLM response and run it
# Usage: extract-run.sh <language> <source_node_id>
#
# Reads $ATTRACTOR_LOGS_ROOT/<source_node>/response.md, extracts the first
# fenced code block (```...```), saves it to a file, and executes it.
# Falls back to using the entire response if no fenced block is found.
set -euo pipefail

LANG="$1"
SOURCE_NODE="$2"
RESPONSE="$ATTRACTOR_LOGS_ROOT/$SOURCE_NODE/response.md"
WORKDIR="$ATTRACTOR_STAGE_DIR"

# Extract code between first pair of ``` markers, or use whole response
CODE=$(awk '/^```/{if(f){exit}else{f=1;next}}f' "$RESPONSE")
if [ -z "$CODE" ]; then
    CODE=$(cat "$RESPONSE")
fi

case "$LANG" in
    python)
        echo "$CODE" > "$WORKDIR/program.py"
        python3 "$WORKDIR/program.py"
        ;;
    javascript|js)
        echo "$CODE" > "$WORKDIR/program.js"
        node "$WORKDIR/program.js"
        ;;
    go)
        echo "$CODE" > "$WORKDIR/main.go"
        (cd "$WORKDIR" && go run main.go)
        ;;
    ruby)
        echo "$CODE" > "$WORKDIR/program.rb"
        ruby "$WORKDIR/program.rb"
        ;;
    bash|sh)
        echo "$CODE" > "$WORKDIR/program.sh"
        bash "$WORKDIR/program.sh"
        ;;
    c)
        echo "$CODE" > "$WORKDIR/program.c"
        gcc -o "$WORKDIR/program" "$WORKDIR/program.c" -lm && "$WORKDIR/program"
        ;;
esac
