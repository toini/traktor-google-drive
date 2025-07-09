#!/bin/bash
set -e

echo "📦 Publishing Blazor WebAssembly client..."

if [[ "$1" == "--dev" ]]; then
  echo "🚧 Dev mode: skipping AOT"
  #dotnet publish TraktorGoogleDrive/TraktorGoogleDrive.csproj -c Release -o client-out -p:BlazorEnableDebugging=true
  #dotnet publish TraktorGoogleDrive/TraktorGoogleDrive.csproj -c Release -o client-out
  dotnet publish TraktorGoogleDrive/TraktorGoogleDrive.csproj -c Debug -o client-out \
    -p:RunAOTCompilation=false \
    -p:BlazorEnableDebugging=true \
    -p:DebugType=portable
else
  echo "📦 Publishing Blazor WebAssembly client with AOT..."
  dotnet publish TraktorGoogleDrive/TraktorGoogleDrive.csproj -c Release -o client-out \
    -p:RunAOTCompilation=true \
    -p:BlazorEnableDebugging=true \
    -p:EmitCompilerGeneratedFiles=true \
    -p:DebugType=portable
fi

echo "📁 Copying built client into server wwwroot..."
rm -rf TraktorGoogleDrive.Server/wwwroot
mkdir -p TraktorGoogleDrive.Server/wwwroot
cp -r client-out/wwwroot/* TraktorGoogleDrive.Server/wwwroot/

echo "🚀 Publishing server..."
dotnet publish TraktorGoogleDrive.Server/TraktorGoogleDrive.Server.csproj -c Release -o out

echo "✅ Done. Published to ./out"
