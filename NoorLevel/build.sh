#!/usr/bin/env bash
# Builds NoorLevel into ./dist (Windows x64 binaries, from any OS). ./build.sh full = self-contained.
set -euo pipefail
cd "$(dirname "$0")"
SC=(--self-contained false)
[ "${1:-}" = full ] && SC=(--self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true)
rm -rf dist
dotnet test tests/NoorLevel.Tests -c Release
dotnet publish src/NoorLevel.Previewer -c Release -r win-x64 "${SC[@]}" -p:DebugType=none -o dist/Previewer
dotnet publish src/NoorLevel.Cli -c Release -r win-x64 "${SC[@]}" -p:DebugType=none -o dist/Cli
dotnet build src/NoorLevel -c Release -o dist/Library
rm -f dist/Previewer/*.xml
echo "Done: dist/Previewer/NoorLevelPreviewer.exe, dist/Cli/noorlevel-cli.exe, dist/Library/NoorLevel.dll"
