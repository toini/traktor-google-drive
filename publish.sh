#!/bin/bash
set -euo pipefail

# The server project now has a ProjectReference to the client, so publishing the
# server pulls the Blazor output in on its own — no hand-copying into
# TraktorGoogleDrive.Server/wwwroot (that directory is generated; don't commit it).

MODE="${1:-release}"

if [[ "$MODE" == "--dev" ]]; then
  echo "🚧 Dev publish (no AOT, debugging symbols)"
  dotnet publish TraktorGoogleDrive.Server/TraktorGoogleDrive.Server.csproj \
    -c Debug -o out \
    -p:RunAOTCompilation=false \
    -p:BlazorEnableDebugging=true \
    -p:DebugType=portable
else
  echo "📦 Release publish"
  # AOT is deliberately OFF: for this app it inflates the download without a
  # meaningful runtime win.
  #
  # Do NOT add PublishTrimmed here. On the *server* project it forces a
  # RID-specific self-contained publish, which on macOS emits a Mach-O apphost
  # and .dylib files that cannot run in the linux container. The Blazor client
  # is trimmed by default in Release regardless (2.3 MB brotli), which is the
  # payload that actually matters.
  dotnet publish TraktorGoogleDrive.Server/TraktorGoogleDrive.Server.csproj \
    -c Release -o out \
    -p:RunAOTCompilation=false
fi

echo "✅ Published to ./out"
echo "   wasm payload:"
du -ch out/wwwroot/_framework/*.wasm 2>/dev/null | tail -1 || true
