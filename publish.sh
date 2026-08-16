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
  # AOT is deliberately OFF: for this app it inflates the download (the console
  # reported 8.8 MB) without a meaningful runtime win. Trimming is what matters.
  dotnet publish TraktorGoogleDrive.Server/TraktorGoogleDrive.Server.csproj \
    -c Release -o out \
    -p:RunAOTCompilation=false \
    -p:PublishTrimmed=true
fi

echo "✅ Published to ./out"
echo "   wasm payload:"
du -ch out/wwwroot/_framework/*.wasm 2>/dev/null | tail -1 || true
