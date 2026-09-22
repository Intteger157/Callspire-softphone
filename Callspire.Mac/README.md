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

## IPC (NDJSON over `~/Library/Application Support/Callspire/service.sock`)

See `Callspire.Service/ServiceHost.cs` for the method table (`placeCall`, `getSettings`, `webRtcCreateHost`, …).
C# → Swift requests: `showConnectionSelection`, `showLeadSelection`, `showKommoLeadPicker`, `webRtcInvokeScript`.

## Sleep / URL schemes

`NSWorkspace.willSleepNotification` / `didWakeNotification` → `systemWillSleep` / `systemDidWake`.
`callspire://` and `tel:` are registered in `Info.plist` and forwarded as `handleProtocolUrl`.
