# Callspire.Mac — SwiftUI shell + C# sidecar

Part of the **[Callspire-softphone](https://github.com/Intteger157/Callspire-softphone)** desktop repository (Windows WPF + macOS SwiftUI).

Native macOS UI (SwiftUI) that talks to `Callspire.Service` over a Unix-domain JSON socket.
Telephony, Kommo, gateway and history stay in .NET (`Callspire.AppHost` + `Callspire.Core`).
WebRTC `phone.js` runs in a hidden `WKWebView` hosted by this app.

## Layout

```
Callspire.Mac/
  Callspire/                 Swift sources, Info.plist, entitlements, Assets
  project.yml                XcodeGen spec → Callspire.xcodeproj
  Scripts/build-service.sh   dotnet publish sidecar
  Scripts/package-dmg.sh     service + xcodebuild + optional DMG
```

## Build (on a Mac)

```bash
# 1. Sidecar
Callspire.Mac/Scripts/build-service.sh arm64

# 2. Xcode project
brew install xcodegen
(cd Callspire.Mac && xcodegen generate)
open Callspire.Mac/Callspire.xcodeproj

# 3. Or one-shot package
Callspire.Mac/Scripts/package-dmg.sh --arch arm64
```

The published sidecar is copied into `Callspire.app/Contents/MacOS/Service/` by the Xcode post-build script.

## CI (GitHub Actions)

On every push, workflow **Build** (`/.github/workflows/build.yml`) publishes:

- Windows desktop (`callspire-windows-x64` artifact)
- macOS `Callspire.app` as an unsigned zip (`callspire-macos-arm64-unsigned`)

Download the zip from the Actions run → unzip → **drag `Callspire.app` to `/Applications`** (do not run from the DMG or Downloads folder — macOS App Translocation breaks WebRTC and icons).

### First launch (unsigned CI builds)

GitHub builds are **not notarized**. macOS Gatekeeper may block the app until you allow it once:

1. Move `Callspire.app` to **Applications**.
2. **Right-click → Open** (or System Settings → Privacy & Security → **Open Anyway** after the first blocked attempt).
3. Optional: remove quarantine after download:  
   `xattr -dr com.apple.quarantine /Applications/Callspire.app`

For a smooth “just works” install for users, sign and notarize locally:

```bash
export CALLSPIRE_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
Callspire.Mac/Scripts/package-dmg.sh --arch arm64 --sign "$CALLSPIRE_SIGN_IDENTITY"
# then notarize + staple the .dmg with notarytool (Apple Developer account required)
```

Tag `v*` triggers **Release** (`release.yml`): Windows Inno Setup installer + macOS unsigned `.zip` / `.dmg` on the GitHub Release page. The `.dmg` shows **Callspire.app** and an **Applications** folder shortcut (drag-to-install layout).

## IPC (NDJSON over `~/Library/Application Support/Callspire/service.sock`)

See `Callspire.Service/ServiceHost.cs` for the method table (`placeCall`, `getSettings`, `webRtcCreateHost`, …).
C# → Swift requests: `showConnectionSelection`, `showLeadSelection`, `showKommoLeadPicker`, `webRtcInvokeScript`.

## Sleep / URL schemes

`NSWorkspace.willSleepNotification` / `didWakeNotification` → `systemWillSleep` / `systemDidWake`.
`callspire://` and `tel:` are registered in `Info.plist` and forwarded as `handleProtocolUrl`.

### PBX Gateway auto-setup (same as Windows)

In the gateway admin panel, copy the **provision link** and open it in Safari/Chrome.  
Format: `callspire://provision?token=…&proxy=https://…`  
The app redeems the token, writes `settings.json`, reconnects WebRTC/SIP, and shows a confirmation dialog.

Requires Callspire in **`/Applications`** and Launch Services knowing the `callspire://` handler:

1. Launch Callspire once (registers the URL scheme on each start).
2. Prefer **Open** in the gateway admin modal, or click a `callspire://` link — not the Chrome address bar (it searches Google).
3. If `open callspire://…` returns **-10814**, pass the URL as a launch argument (works on unsigned CI builds):  
   `open -a /Applications/Callspire.app --args 'callspire://provision?token=…&proxy=…'`
4. Optional:  
   `/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/LaunchServices.framework/Versions/A/Support/lsregister -f -R -trusted /Applications/Callspire.app`
