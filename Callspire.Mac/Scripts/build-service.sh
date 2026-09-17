#!/usr/bin/env bash
# Publish Callspire.Service (self-contained) for embedding in Callspire.app.
# Run on macOS with the .NET 8 SDK. Default arch: arm64.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
ARCH="${1:-arm64}"
CONFIG="${2:-Release}"
RID="osx-${ARCH}"
OUT="$ROOT/publish/macos/Service"
echo "Publishing Callspire.Service ($RID $CONFIG) → $OUT"
dotnet publish "$ROOT/Callspire.Service/Callspire.Service.csproj" \
  -c "$CONFIG" -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:PublishTrimmed=false \
  -o "$OUT"
echo "OK: $OUT/Callspire.Service"
