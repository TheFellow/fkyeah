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

# Copy conformance tests
WORKDIR /conformance
COPY conformance/ .

# Make scripts executable
RUN find . -name "*.sh" -exec chmod +x {} +

# Default: run full suite with 90s timeout for e2e tests
ENV CONFORMANCE_TIMEOUT=90

ENTRYPOINT ["bash", "run-all.sh"]
