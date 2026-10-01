#!/bin/bash
# Installs the .NET SDK pinned by global.json and restores packages, so a Claude Code on the web
# session can build and test straight away. Does nothing on a local machine.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "$CLAUDE_PROJECT_DIR"

DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_ROOT PATH="$DOTNET_ROOT:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# `dotnet --version` here resolves global.json, so it fails unless an SDK it accepts (10.0.4xx or later) is installed:
# any other 10.0 SDK doesn't count. The installer is idempotent too.
if ! dotnet --version >/dev/null 2>&1; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --jsonfile global.json --install-dir "$DOTNET_ROOT"
fi

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export DOTNET_ROOT=\"$DOTNET_ROOT\""
    echo "export PATH=\"$DOTNET_ROOT:\$PATH\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
  } >> "$CLAUDE_ENV_FILE"
fi

dotnet restore Claudette.slnx
