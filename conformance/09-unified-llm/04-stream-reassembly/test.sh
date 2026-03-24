#!/usr/bin/env bash
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/../../lib.sh"
setup

dotnet test "$TEST_DIR/../../../Attractor.slnx" --filter "FullyQualifiedName~stream accumulator accumulates reasoning independently from text|FullyQualifiedName~stream accumulator marks balanced tool call json as complete|FullyQualifiedName~stream accumulator handles orphan tool call deltas and preserves metadata on end|FullyQualifiedName~Codergen handler writes context_budget artifact" >/tmp/stream-reassembly.log 2>&1

pass "stream reassembly: targeted streaming and budget diagnostics tests passed"
