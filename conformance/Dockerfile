# conformance/Dockerfile — Reproducible test environment for attractor conformance suite
# Build from repo root: docker build -f conformance/Dockerfile .

# ── Stage 1: Build attractor binary ───────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build

WORKDIR /src
COPY Attractor.slnx ./
COPY src/ src/
COPY tests/ tests/

RUN dotnet publish src/Attractor.Cli/Attractor.Cli.fsproj \
    -c Release --self-contained -o /app/publish

# Build mock fixtures for conformance tests
RUN dotnet publish tests/Fixtures/MockLlmServer/MockLlmServer.fsproj \
    -c Release --self-contained -o /app/mock-llm-server
RUN dotnet publish tests/Fixtures/MockMcpServer/MockMcpServer.fsproj \
    -c Release --self-contained -o /app/mock-mcp-server
RUN dotnet publish tests/Fixtures/MockAcpAgent/MockAcpAgent.fsproj \
    -c Release --self-contained -o /app/mock-acp-agent

# ── Stage 2: Runtime with all language runtimes ───────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0-preview

# Install language runtimes and tools
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 \
    python3-venv \
    nodejs \
    npm \
    golang-go \
    ruby \
    gcc \
    libc6-dev \
    bash \
    jq \
    coreutils \
    && rm -rf /var/lib/apt/lists/*

# Copy built attractor binary
COPY --from=build /app/publish /app/attractor
RUN chmod +x /app/attractor/Attractor.Cli
ENV ATTRACTOR_BIN=/app/attractor/Attractor.Cli

# Copy mock fixture binaries for conformance tests
COPY --from=build /app/mock-llm-server /app/mock-llm-server
COPY --from=build /app/mock-mcp-server /app/mock-mcp-server
COPY --from=build /app/mock-acp-agent /app/mock-acp-agent
RUN chmod +x /app/mock-llm-server/MockLlmServer /app/mock-mcp-server/MockMcpServer /app/mock-acp-agent/MockAcpAgent
ENV MOCK_LLM_SERVER=/app/mock-llm-server/MockLlmServer
ENV MOCK_MCP_SERVER=/app/mock-mcp-server/MockMcpServer
ENV MOCK_ACP_AGENT=/app/mock-acp-agent/MockAcpAgent

# Copy example pipelines (some conformance tests validate the shipped examples)
COPY examples/ /examples/

# Copy conformance tests
WORKDIR /conformance
COPY conformance/ .

# Make scripts executable
RUN find . -name "*.sh" -exec chmod +x {} +

# Default: run full suite with 90s timeout for e2e tests
ENV CONFORMANCE_TIMEOUT=90

ENTRYPOINT ["bash", "run-all.sh"]
