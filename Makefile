# Usage: make build | test | run | publish | install | uninstall
#        make install PREFIX=/usr/local   (may need sudo)
#
# Vibe UI needs Mistral Vibe installed: curl -LsSf https://mistral.ai/vibe/install.sh | bash

PREFIX    ?= $(HOME)/.local
BINDIR    ?= $(PREFIX)/bin
LIBDIR    ?= $(PREFIX)/lib/vibe-ui
CONFIG    ?= Release
RID       ?= $(shell dotnet --info | awk '/RID:/ { print $$2; exit }')
OUT       := artifacts/stage/$(RID)

# A complete, isolated publish tree keeps Avalonia's native libraries next to the app.
PUBLISH   := dotnet publish -c $(CONFIG) -r $(RID) --self-contained \
             -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None

.PHONY: all build test run publish install uninstall clean

all: build

build:
	dotnet build vibe-ui.slnx -c $(CONFIG)

test:
	dotnet test vibe-ui.slnx -c $(CONFIG)

# Runs the desktop app from source.
run:
	dotnet run --project VibeUi.Desktop

publish:
	$(PUBLISH) VibeUi.Desktop -o "$(OUT)/vibe-ui"

install: publish
	install -d "$(BINDIR)"
	install -d "$(LIBDIR)"
	rm -rf "$(LIBDIR)/vibe-ui"
	cp -R "$(OUT)/vibe-ui" "$(LIBDIR)/"
	ln -sfn "$(abspath $(LIBDIR))/vibe-ui/vibe-ui" "$(BINDIR)/vibe-ui"
	@echo "Installed vibe-ui into $(BINDIR)"
	@case ":$$PATH:" in *":$(BINDIR):"*) ;; *) echo "Note: $(BINDIR) is not on your PATH." ;; esac

uninstall:
	rm -f "$(BINDIR)/vibe-ui"
	rm -rf "$(LIBDIR)/vibe-ui"

clean:
	dotnet clean vibe-ui.slnx
	rm -rf artifacts
