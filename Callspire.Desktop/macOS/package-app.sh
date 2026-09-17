#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────────────
# Callspire — macOS .app bundle packager
#
# Usage (run on macOS with the .NET 8 SDK installed):
#   Callspire.Desktop/macOS/package-app.sh [--arch arm64|x64|universal] [--config Release]
#                                          [--sign "Developer ID Application: Name (TEAMID)"]
#                                          [--notarize-profile <keychain-profile>] [--dmg]
#
# Environment overrides: CALLSPIRE_SIGN_IDENTITY, CALLSPIRE_NOTARY_PROFILE
#
# Output: publish/macos/Callspire.app  (+ publish/macos/Callspire-<version>-<arch>.dmg with --dmg)
#
# Codesign / notarization checklist
#   1. Xcode command line tools installed (codesign, iconutil, hdiutil, xcrun notarytool).
#   2. A "Developer ID Application" certificate in the login keychain (for distribution outside
#      the App Store). Check:  security find-identity -v -p codesigning
#   3. Store notarization credentials once:
#        xcrun notarytool store-credentials "callspire-notary" \
#             --apple-id you@example.com --team-id TEAMID --password <app-specific-password>
#   4. Run this script with --sign and --notarize-profile callspire-notary --dmg.
#   5. Verify:  spctl -a -vv publish/macos/Callspire.app  → "accepted, source=Notarized Developer ID"
#   6. Upload the .dmg and publish mac_url / mac_sha256 in update-server/update.json.
# ──────────────────────────────────────────────────────────────────────────────
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
PROJECT="$ROOT/Callspire.Desktop/Callspire.Desktop.csproj"

ARCH="arm64"
CONFIG="Release"
SIGN_IDENTITY="${CALLSPIRE_SIGN_IDENTITY:-}"
NOTARY_PROFILE="${CALLSPIRE_NOTARY_PROFILE:-}"
MAKE_DMG=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch) ARCH="$2"; shift 2 ;;
    --config) CONFIG="$2"; shift 2 ;;
    --sign) SIGN_IDENTITY="$2"; shift 2 ;;
    --notarize-profile) NOTARY_PROFILE="$2"; shift 2 ;;
    --dmg) MAKE_DMG=1; shift ;;
    -h|--help) sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done

say() { printf '\033[0;36m> %s\033[0m\n' "$*"; }
ok()  { printf '\033[0;32m  %s\033[0m\n' "$*"; }

VERSION="$(sed -n 's:.*<AppVersion>\(.*\)</AppVersion>.*:\1:p' "$ROOT/Directory.Build.props" | head -n1)"
VERSION="${VERSION:-1.0.0}"
BUILD_NUMBER="${VERSION//./}"

OUT="$ROOT/publish/macos"
APP="$OUT/Callspire.app"
CONTENTS="$APP/Contents"
MACOS_DIR="$CONTENTS/MacOS"
RES_DIR="$CONTENTS/Resources"

publish_rid() {
  local rid="$1" dest="$2"
  say "dotnet restore + publish ($rid)"
  dotnet restore "$PROJECT" -p:TargetFramework=net8.0 -r "$rid"
  dotnet publish "$PROJECT" -f net8.0 -c "$CONFIG" -r "$rid" \
      --self-contained true --no-restore \
      -p:PublishSingleFile=false -p:PublishTrimmed=false \
      -p:UseAppHost=true -p:IncludeNativeLibrariesForSelfExtract=true \
      -o "$dest"
}

rm -rf "$APP"
mkdir -p "$MACOS_DIR" "$RES_DIR"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

case "$ARCH" in
  arm64) publish_rid osx-arm64 "$STAGE/app" ;;
  x64)   publish_rid osx-x64   "$STAGE/app" ;;
  universal)
    publish_rid osx-arm64 "$STAGE/arm64"
    publish_rid osx-x64   "$STAGE/x64"
    say "lipo — merging Mach-O binaries into a universal payload"
    cp -R "$STAGE/arm64" "$STAGE/app"
    while IFS= read -r -d '' f; do
      rel="${f#"$STAGE/arm64/"}"
      other="$STAGE/x64/$rel"
      if [[ -f "$other" ]] && file "$f" | grep -q "Mach-O"; then
        lipo -create "$f" "$other" -output "$STAGE/app/$rel"
      fi
    done < <(find "$STAGE/arm64" -type f -print0)
    ;;
  *) echo "Unknown arch: $ARCH (arm64|x64|universal)"; exit 1 ;;
esac

say "Assembling $APP"
cp -R "$STAGE/app/." "$MACOS_DIR/"
chmod +x "$MACOS_DIR/Callspire"

# WebRTC engine assets (served from the loopback asset server at runtime).
if [[ -d "$ROOT/WebRtcClient" ]]; then
  rm -rf "$MACOS_DIR/WebRtcClient"
  cp -R "$ROOT/WebRtcClient" "$MACOS_DIR/WebRtcClient"
fi

# Info.plist with version substituted.
sed -e "s/__VERSION__/$VERSION/g" -e "s/__BUILD__/$BUILD_NUMBER/g" "$HERE/Info.plist" > "$CONTENTS/Info.plist"
printf 'APPL????' > "$CONTENTS/PkgInfo"

# Icon: build Callspire.icns from Assets/icon.png (needs sips + iconutil).
ICON_PNG="$ROOT/Callspire.Desktop/Assets/icon.png"
if [[ -f "$ICON_PNG" ]] && command -v iconutil >/dev/null 2>&1; then
  ICONSET="$STAGE/Callspire.iconset"
  mkdir -p "$ICONSET"
  for s in 16 32 128 256 512; do
    sips -z $s $s "$ICON_PNG" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    d=$((s*2))
    sips -z $d $d "$ICON_PNG" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$RES_DIR/Callspire.icns"
  ok "Icon: $RES_DIR/Callspire.icns"
else
  echo "  (icon skipped — Assets/icon.png or iconutil missing)"
fi

# Code signing (deep, hardened runtime, entitlements for JIT + microphone).
if [[ -n "$SIGN_IDENTITY" ]]; then
  say "codesign ($SIGN_IDENTITY)"
  ENT="$HERE/Callspire.entitlements"
  # Sign every Mach-O (dylibs and the .NET native libs) first, then the bundle.
  while IFS= read -r -d '' f; do
    if file "$f" | grep -q "Mach-O"; then
      codesign --force --options runtime --timestamp --entitlements "$ENT" --sign "$SIGN_IDENTITY" "$f"
    fi
  done < <(find "$MACOS_DIR" -type f -print0)
  codesign --force --options runtime --timestamp --entitlements "$ENT" --sign "$SIGN_IDENTITY" "$APP"
  codesign --verify --deep --strict --verbose=2 "$APP"
  ok "Signed"
else
  say "ad-hoc signing (no --sign): app will run locally but Gatekeeper will warn on other Macs"
  codesign --force --deep --sign - "$APP" || true
fi

ok "Bundle: $APP"

if [[ $MAKE_DMG -eq 1 ]]; then
  DMG="$OUT/Callspire-$VERSION-$ARCH.dmg"
  say "hdiutil → $DMG"
  rm -f "$DMG"
  DMG_STAGE="$STAGE/dmg"
  mkdir -p "$DMG_STAGE"
  cp -R "$APP" "$DMG_STAGE/"
  ln -s /Applications "$DMG_STAGE/Applications"
  hdiutil create -volname "Callspire $VERSION" -srcfolder "$DMG_STAGE" -ov -format UDZO "$DMG" >/dev/null
  if [[ -n "$SIGN_IDENTITY" ]]; then
    codesign --force --timestamp --sign "$SIGN_IDENTITY" "$DMG"
  fi
  if [[ -n "$NOTARY_PROFILE" ]]; then
    say "notarytool submit (profile: $NOTARY_PROFILE)"
    xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG"
    xcrun stapler staple "$APP" || true
    ok "Notarized + stapled"
  fi
  shasum -a 256 "$DMG" | tee "$DMG.sha256"
  ok "DMG: $DMG"
fi
