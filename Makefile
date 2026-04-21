INSTALL_DIR := $(HOME)/bin
CLI_PROJECT := src/Attractor.Cli/Attractor.Cli.fsproj
PUBLISH_DIR := $(shell mktemp -d)
RID := osx-arm64

.PHONY: build test publish install conformance clean format format-check lint tools

tools:
	dotnet tool restore

format: tools
	dotnet fantomas src tests

format-check: tools
	dotnet fantomas --check src tests

lint: tools
	dotnet dotnet-fsharplint lint --lint-config fsharplint.json --file-type solution Attractor.slnx

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
