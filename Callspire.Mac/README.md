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

### Correct macOS app vs legacy .NET shell

Current macOS releases are **SwiftUI + sidecar**, not the old Avalonia/.NET app.

| Check | SwiftUI (correct) | Legacy (do not use) |
|-------|-------------------|---------------------|
| Sidecar | `Callspire.app/Contents/MacOS/Service/Callspire.Service` exists | No `Service/` folder |
| Main binary | Small native Mach-O (Swift), no `libcoreclr.dylib` in `Contents/MacOS/` | `libcoreclr.dylib`, `libSkiaSharp.dylib`, `libAvaloniaNative.dylib` next to `Callspire` |
| Typical crash | Sidecar log in `~/Library/Application Support/Callspire/logs/` | Immediate abort on launch (`IL_Throw` / `libcoreclr` on main thread) |

If Crash Reporter shows **`libcoreclr.dylib`** and **`libAvaloniaNative.dylib`** with the main executable at `Contents/MacOS/Callspire`, replace the app with a fresh **GitHub Release** or CI zip built via `Callspire.Mac/Scripts/package-dmg.sh`. Do **not** use `Callspire.Desktop/macOS/package-app.sh` (removed).

### First launch (unsigned CI builds)

GitHub builds are **not notarized**. macOS Gatekeeper may block the app until you allow it once:

1. Move `Callspire.app` to **Applications**.
2. **Right-click → Open** (or System Settings → Privacy & Security → **Open Anyway** after the first blocked attempt).
3. Optional: remove quarantine after download:  
   `xattr -dr com.apple.quarantine /Applications/Callspire.app`

### Updates without repeating System Settings

macOS remembers **microphone** and **Privacy & Security** prompts per app identity (bundle id + **code signature**). To avoid re-approving every release:

1. **Always update in place** — drag the new `Callspire.app` from the DMG onto `/Applications/Callspire.app` and choose **Replace**. Do not run from Downloads or the mounted DMG (App Translocation breaks WebRTC and can look like a “new” app).
2. **Remove quarantine only on the copy in Applications** (after download):  
   `xattr -dr com.apple.quarantine /Applications/Callspire.app`
3. **Unsigned GitHub builds** are ad-hoc signed; each CI build has a different signature, so macOS may treat an update as a new app and ask for **microphone access again**. That is expected until you ship **Developer ID + notarization** with a stable signing identity (same team id every release).
4. **Gatekeeper “Open Anyway”** is usually **once per downloaded build**, not every launch — if it appears every update, you are likely launching a quarantined copy outside `/Applications`.

Theme (light/dark) is stored in `settings.json` and is independent of macOS System Settings → Appearance; choosing **System** in Callspire follows the Mac appearance.

### Microphone prompt shows `127.0.0.1`

WebRTC runs in an embedded WebKit page on loopback (`http://127.0.0.1:17842/`). macOS can show a **site** prompt (“Allow 127.0.0.1…”) in addition to the **Callspire** prompt in System Settings → Privacy → Microphone.

- Allow **Callspire** once in System Settings → Privacy & Security → **Microphone**.
- Current builds use a **fixed loopback port** and auto-grant WebKit when Callspire already has microphone access, so the `127.0.0.1` dialog should not repeat every launch.
- If it still appears every time: confirm the app lives in **`/Applications`**, update to the latest build, and reset stale WebKit permissions: System Settings → Privacy → **Microphone** (toggle Callspire off/on) or delete `~/Library/WebKit/com.callspire.softphone` while Callspire is quit.

For a smooth “just works” install for users, sign and notarize locally:

```bash
export CALLSPIRE_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
Callspire.Mac/Scripts/package-dmg.sh --arch arm64 --sign "$CALLSPIRE_SIGN_IDENTITY"
# then notarize + staple the .dmg with notarytool (Apple Developer account required)
```

Tag `v*` triggers **Release** (`release.yml`):

| Asset | Platform |
|-------|----------|
| `Callspire-*-win-x64-setup.exe` | Windows Intel/AMD64 |
| `Callspire-*-win-arm64-setup.exe` | Windows ARM64 |
| `Callspire-*-macos-arm64-unsigned.{zip,dmg}` | macOS Apple Silicon |
| `Callspire-*-macos-x64-unsigned.{zip,dmg}` | macOS Intel |

CI **Build** (`build.yml`) publishes the same four targets as workflow artifacts on every push/PR. The `.dmg` shows **Callspire.app** and an **Applications** folder shortcut (drag-to-install layout).

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
