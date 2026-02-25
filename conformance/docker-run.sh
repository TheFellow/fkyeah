#!/usr/bin/env bash
# docker-run.sh — Build and run the conformance suite in Docker
# Usage:
#   ./conformance/docker-run.sh                          # Full suite
#   ./conformance/docker-run.sh 07-models                # One category
#   ./conformance/docker-run.sh 07-models/01-claude-opus-4-6  # One model
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

IMAGE_NAME="attractor-conformance"

echo "Building $IMAGE_NAME..."
docker build -f "$SCRIPT_DIR/Dockerfile" -t "$IMAGE_NAME" "$REPO_ROOT"

echo ""
echo "Running conformance suite..."
docker run --rm \
    -e ANTHROPIC_API_KEY="${ANTHROPIC_API_KEY:-}" \
    -e OPENAI_API_KEY="${OPENAI_API_KEY:-}" \
    -e GEMINI_API_KEY="${GEMINI_API_KEY:-}" \
    "$IMAGE_NAME" \
    "$@"
