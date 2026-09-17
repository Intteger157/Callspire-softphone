#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# Callspire local build helper  (bash / macOS / Linux)
#
# Usage:
#   ./build.sh                    # Build all targets
#   ./build.sh windows            # Windows cross-target (compile only, no .exe)
#   ./build.sh macos              # macOS Avalonia binary
#   ./build.sh linux              # Linux Avalonia binary
#   ./build.sh android            # Android APK
#   ./build.sh all --publish      # Build + publish all
#   ./build.sh linux --debug      # Debug configuration
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET="${1:-all}"
CONFIGURATION="Release"
PUBLISH=0

for arg in "$@"; do
  case "$arg" in
    --debug)   CONFIGURATION="Debug" ;;
    --publish) PUBLISH=1 ;;
  esac
done

cyan()   { printf '\033[0;36m> %s\033[0m\n' "$*"; }
yellow() { printf '\033[0;33m=== %s ===\033[0m\n' "$*"; }
green()  { printf '\033[0;32m  %s\033[0m\n' "$*"; }

run() { cyan "dotnet $*"; dotnet "$@"; }

build_core() {
  yellow "Callspire.Core"
  run build "$ROOT/Callspire.Core/Callspire.Core.csproj" -c "$CONFIGURATION"
}

build_windows() {
  yellow "Desktop (Windows — compile verification)"
  # Compiles on non-Windows (no WPF binary produced; Windows-only code is #if WINDOWS)
  run build "$ROOT/Callspire.Desktop/Callspire.Desktop.csproj" \
      -f net8.0-windows10.0.17763 -c "$CONFIGURATION"
}

build_macos() {
  yellow "Desktop (macOS)"
  run build "$ROOT/Callspire.Desktop/Callspire.Desktop.csproj" \
      -f net8.0 -c "$CONFIGURATION"
  if [[ $PUBLISH -eq 1 ]]; then
    # Produces publish/macos/Callspire.app (self-contained, Info.plist with callspire:// scheme,
    # NSMicrophoneUsageDescription, icon). Add --sign/--notarize-profile/--dmg via env or by
    # calling macOS/package-app.sh directly. Arch: CALLSPIRE_MAC_ARCH=arm64|x64|universal.
    bash "$ROOT/Callspire.Desktop/macOS/package-app.sh" \
        --arch "${CALLSPIRE_MAC_ARCH:-arm64}" --config "$CONFIGURATION"
    green "Artifact: $ROOT/publish/macos/Callspire.app"
  fi
}

build_linux() {
  yellow "Desktop (Linux)"
  run build "$ROOT/Callspire.Desktop/Callspire.Desktop.csproj" \
      -f net8.0 -c "$CONFIGURATION"
  if [[ $PUBLISH -eq 1 ]]; then
    run publish "$ROOT/Callspire.Desktop/Callspire.Desktop.csproj" \
        -f net8.0 -c "$CONFIGURATION" -r linux-x64 --no-self-contained \
        -o "$ROOT/publish/linux"
    green "Artifact: $ROOT/publish/linux"
  fi
}

build_android() {
  yellow "Android APK"
  if ! dotnet workload list 2>/dev/null | grep -q android; then
    echo "  Installing .NET android workload..."
    dotnet workload install android
  fi
  run build "$ROOT/Callspire.Android/Callspire.Android.csproj" \
      -f net8.0-android34.0 -c "$CONFIGURATION"
  if [[ $PUBLISH -eq 1 ]]; then
    run publish "$ROOT/Callspire.Android/Callspire.Android.csproj" \
        -f net8.0-android34.0 -c "$CONFIGURATION" \
        -o "$ROOT/publish/android"
    green "Artifact: $ROOT/publish/android"
  fi
}

echo "Callspire build  |  target=$TARGET  config=$CONFIGURATION  publish=$PUBLISH"

case "$TARGET" in
  core)    build_core ;;
  windows) build_core; build_windows ;;
  macos)   build_core; build_macos ;;
  linux)   build_core; build_linux ;;
  android) build_core; build_android ;;
  all)
    build_core
    build_macos
    build_linux
    build_android
    ;;
  *)
    echo "Unknown target: $TARGET. Use: all | core | windows | macos | linux | android"
    exit 1
    ;;
esac

echo ""
green "Build complete."
