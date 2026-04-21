INSTALL_DIR := $(HOME)/bin
CLI_PROJECT := src/Attractor.Cli/Attractor.Cli.fsproj
PUBLISH_DIR := $(shell mktemp -d)
RID := osx-arm64
ANALYZERS_PATH := $(abspath analyzers/.analyzerpackages/g-research.fsharp.analyzers/0.22.0)
ANALYZER_PROJECTS := src/JsonRpc/JsonRpc.fsproj src/UnifiedLlm/UnifiedLlm.fsproj src/McpClient/McpClient.fsproj src/AcpRuntime/AcpRuntime.fsproj src/CodingAgent/CodingAgent.fsproj src/Attractor/Attractor.fsproj src/Attractor.Cli/Attractor.Cli.fsproj

.PHONY: build test publish install conformance clean format format-check lint analyze analyzers-restore tools

tools:
	dotnet tool restore

format: tools
	dotnet fantomas src tests

format-check: tools
	dotnet fantomas --check src tests

lint: tools
	dotnet dotnet-fsharplint lint --lint-config fsharplint.json --file-type solution Attractor.slnx

analyzers-restore:
	dotnet restore analyzers/analyzers.fsproj

# Run G-Research analyzers across every project. Excluded analyzers are
# documented in the `style: adopt FSharpLint and analyzers` commit.
analyze: tools analyzers-restore
	@fail=0; \
	for proj in $(ANALYZER_PROJECTS); do \
	  echo "-- $$proj"; \
	  dotnet fsharp-analyzers --project $$proj \
	    --analyzers-path $(ANALYZERS_PATH) \
	    --exclude-analyzers TypedInterpolatedStringsAnalyzer UnionCaseAnalyzer \
	    --treat-as-error GRA-STRING-001 GRA-STRING-002 GRA-TYPE-ANNOTATE-001 \
	    || fail=1; \
	done; \
	exit $$fail

build:
	dotnet build

test:
	dotnet test

publish:
	dotnet publish $(CLI_PROJECT) -c Release -r $(RID) --self-contained -o $(PUBLISH_DIR)
	@echo "Published to $(PUBLISH_DIR)/Attractor.Cli"

install: publish
	cp $(PUBLISH_DIR)/Attractor.Cli $(INSTALL_DIR)/attractor
	codesign -f -s - $(INSTALL_DIR)/attractor
	@echo "Installed attractor to $(INSTALL_DIR)/attractor"

conformance: install
	conformance/run-all.sh

clean:
	dotnet clean
	rm -rf $(PUBLISH_DIR)
