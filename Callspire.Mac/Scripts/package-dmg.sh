#!/usr/bin/env bash
# Build Callspire.Service, generate the Xcode project (if xcodegen is installed), archive, optional DMG.
# Usage (macOS):  Callspire.Mac/Scripts/package-dmg.sh [--arch arm64] [--sign "Developer ID Application: …"]
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
MAC="$ROOT/Callspire.Mac"
ARCH="arm64"
SIGN="${CALLSPIRE_SIGN_IDENTITY:-}"
CI_BUILD=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --sign) SIGN="$2"; shift 2 ;;
    --ci) CI_BUILD=1; shift ;;
    *) shift ;;
  esac
done
if [[ -n "${CI:-}" && "$CI_BUILD" -eq 0 ]]; then CI_BUILD=1; fi

bash "$HERE/build-service.sh" "$ARCH" Release

if command -v xcodegen >/dev/null 2>&1; then
  (cd "$MAC" && xcodegen generate)
fi

APP_OUT="$ROOT/publish/macos/Callspire.app"
mkdir -p "$ROOT/publish/macos"

if [[ -d "$MAC/Callspire.xcodeproj" ]]; then
  XCB=(xcodebuild -project "$MAC/Callspire.xcodeproj" -scheme Callspire -configuration Release
    -destination "platform=macOS,arch=${ARCH}"
    CONFIGURATION_BUILD_DIR="$ROOT/publish/macos")
  if [[ "$CI_BUILD" -eq 1 ]]; then
    # GitHub Actions / local smoke: no Apple Developer account required.
    XCB+=(CODE_SIGN_IDENTITY=- CODE_SIGNING_REQUIRED=NO CODE_SIGNING_ALLOWED=NO)
  fi
  "${XCB[@]}" build
else
  echo "Callspire.xcodeproj not found. Install xcodegen (brew install xcodegen) and re-run, or open project.yml in XcodeGen."
  echo "Sidecar is already published at publish/macos/Service/"
  exit 1
fi

if [[ ! -d "$APP_OUT" ]]; then
  FOUND="$(find "$ROOT/publish/macos" -maxdepth 3 -name 'Callspire.app' -type d 2>/dev/null | head -1 || true)"
  if [[ -n "$FOUND" ]]; then APP_OUT="$FOUND"; fi
fi

embed_app_icon() {
  local app="$1"
  local appiconset="$MAC/Callspire/Assets.xcassets/AppIcon.appiconset"
  local resources="$app/Contents/Resources"
  [[ -d "$appiconset" ]] || return 0
  mkdir -p "$resources"
  local tmp iconset
  tmp="$(mktemp -d)"
  iconset="$tmp/AppIcon.iconset"
  mkdir -p "$iconset"
  cp "$appiconset"/*.png "$iconset/" 2>/dev/null || true
  if [[ -n "$(ls -A "$iconset"/*.png 2>/dev/null)" ]]; then
    iconutil -c icns -o "$resources/AppIcon.icns" "$iconset"
    echo "App icon: $resources/AppIcon.icns"
  fi
  rm -rf "$tmp"
}

if [[ -d "$APP_OUT" ]]; then
  embed_app_icon "$APP_OUT"
fi

if [[ -d "$APP_OUT" ]]; then
  if [[ -n "$SIGN" ]]; then
    codesign --force --deep --options runtime --entitlements "$MAC/Callspire/Callspire.entitlements" --sign "$SIGN" "$APP_OUT"
  elif [[ "$CI_BUILD" -eq 1 ]]; then
    # Ad-hoc sign so the bundle is coherent; Gatekeeper still requires notarization or first Open for quarantined downloads.
    codesign --force --deep --sign - "$APP_OUT" 2>/dev/null || true
  fi
fi

create_dmg() {
  local app_path="$1"
  local dmg_path="$2"
  local volname="Callspire"
  local staging
  staging="$(mktemp -d)"
  trap 'rm -rf "$staging"' RETURN

  cp -R "$app_path" "$staging/"
  ln -sf /Applications "$staging/Applications"

  local rw_dmg="${dmg_path%.dmg}-rw.dmg"
  rm -f "$rw_dmg" "$dmg_path"
  hdiutil create -size 240m -volname "$volname" -fs HFS+ -ov "$rw_dmg" >/dev/null
  local mount_dir
  mount_dir="$(hdiutil attach "$rw_dmg" -nobrowse -readwrite | awk '/\/Volumes\// {print $3; exit}')"
  if [[ -z "$mount_dir" || ! -d "$mount_dir" ]]; then
    echo "DMG mount failed — falling back to flat image"
    hdiutil create -volname "$volname" -srcfolder "$staging" -ov -format UDZO "$dmg_path"
    return
  fi

  rm -rf "$mount_dir"/*
  cp -R "$staging/Callspire.app" "$mount_dir/"
  ln -sf /Applications "$mount_dir/Applications"

  /usr/bin/osascript <<EOF || true
tell application "Finder"
  tell disk "$volname"
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {200, 120, 720, 420}
    set viewOptions to the icon view options of container window
    set arrangement of viewOptions to not arranged
    set icon size of viewOptions to 128
    set position of item "Callspire.app" of container window to {130, 160}
    set position of item "Applications" of container window to {410, 160}
    close
    open
    update without registering applications
    delay 1
  end tell
end tell
EOF

  chmod -Rf go-w "$mount_dir" || true
  sync
  hdiutil detach "$mount_dir" >/dev/null
  hdiutil convert "$rw_dmg" -format UDZO -o "$dmg_path" >/dev/null
  rm -f "$rw_dmg"
}

if command -v hdiutil >/dev/null 2>&1 && [[ -d "$APP_OUT" ]]; then
  DMG="$ROOT/publish/macos/Callspire-${ARCH}.dmg"
  create_dmg "$APP_OUT" "$DMG"
  echo "DMG: $DMG"
fi
echo "App: $APP_OUT"
