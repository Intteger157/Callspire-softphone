#!/usr/bin/env bash
# Build Callspire.Service, generate the Xcode project (if xcodegen is installed), archive, optional DMG.
# Usage (macOS):  Callspire.Mac/Scripts/package-dmg.sh [--arch arm64] [--sign "Developer ID Application: …"]
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
MAC="$ROOT/Callspire.Mac"
ARCH="arm64"
SIGN="${CALLSPIRE_SIGN_IDENTITY:-}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --sign) SIGN="$2"; shift 2 ;;
    *) shift ;;
  esac
done

bash "$HERE/build-service.sh" "$ARCH" Release

if command -v xcodegen >/dev/null 2>&1; then
  (cd "$MAC" && xcodegen generate)
fi

APP_OUT="$ROOT/publish/macos/Callspire.app"
mkdir -p "$ROOT/publish/macos"

if [[ -d "$MAC/Callspire.xcodeproj" ]]; then
  xcodebuild -project "$MAC/Callspire.xcodeproj" -scheme Callspire -configuration Release \
    -destination "platform=macOS,arch=${ARCH}" \
    CONFIGURATION_BUILD_DIR="$ROOT/publish/macos" \
    build
else
  echo "Callspire.xcodeproj not found. Install xcodegen (brew install xcodegen) and re-run, or open project.yml in XcodeGen."
  echo "Sidecar is already published at publish/macos/Service/"
  exit 1
fi

if [[ -n "$SIGN" && -d "$APP_OUT" ]]; then
  codesign --force --deep --options runtime --entitlements "$MAC/Callspire/Callspire.entitlements" --sign "$SIGN" "$APP_OUT"
fi

if command -v hdiutil >/dev/null 2>&1 && [[ -d "$APP_OUT" ]]; then
  DMG="$ROOT/publish/macos/Callspire-${ARCH}.dmg"
  rm -f "$DMG"
  hdiutil create -volname Callspire -srcfolder "$APP_OUT" -ov -format UDZO "$DMG"
  echo "DMG: $DMG"
fi
echo "App: $APP_OUT"
