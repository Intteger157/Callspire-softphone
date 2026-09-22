# Callspire Softphone

**Callspire** is a corporate softphone with **SIP and WebRTC**, Kommo CRM, and PBX Gateway integration.

> Tech stack: **.NET 8 · WPF (Windows) · SwiftUI (macOS) · SIPSorcery · WebRTC · NAudio · PortAudio**

[![Build](https://github.com/Intteger157/Callspire-softphone/actions/workflows/build.yml/badge.svg)](https://github.com/Intteger157/Callspire-softphone/actions/workflows/build.yml)

---

## Features

- SIP calling (G.711, G.722, Opus via Concentus)
- WebRTC calling (WSS / TURN)
- Windows WPF UI and macOS SwiftUI UI (dark / light / system)
- Call history and detailed call statistics
- Incoming / outgoing calls with call recording
- Audio device selection (mic / speaker)
- Acoustic echo cancellation: native `webrtc_apm.dll` on Windows, PortAudio + SoftwareAec on macOS
- AmoCRM / Kommo CRM integration
- MikoPBX CDR / caller-ID lookup

---

## Screenshots

### Dial pad
<img width="1100" height="700" alt="Dial pad" src="https://github.com/user-attachments/assets/6cd4e26c-13b3-4c08-b2f0-6b35eaec1ca2" />

### Active call
<img width="380" height="750" alt="Active call" src="https://github.com/user-attachments/assets/7809ed47-d1e9-4a85-a3d1-03975669829f" />

### Call details
<img width="1103" height="807" alt="Call details" src="https://github.com/user-attachments/assets/06823fe3-25d1-46de-8847-906b50be6381" />

---

## Project structure

```
Callspire.sln
├── Callspire.Core/          # Platform-agnostic business logic
├── Callspire.AppHost/       # DesktopAppController + ViewModels (shared)
├── Callspire.Desktop/       # Windows WPF + WebView2
├── Callspire.Service/       # macOS telephony sidecar (JSON IPC)
├── Callspire.Mac/           # SwiftUI app (WKWebView + sidecar)
└── WebRtcClient/            # phone.js engine (Windows WebView2 / macOS WKWebView)
```

### Target frameworks

| Project | TFM(s) |
|---|---|
| `Callspire.Core` | `net8.0` |
| `Callspire.AppHost` | `net8.0` |
| `Callspire.Desktop` | `net8.0-windows10.0.17763` (WPF) |
| `Callspire.Service` | `net8.0` (`osx-arm64` / `osx-x64` self-contained) |

---

## Building

### Prerequisites

| Platform | Requirements |
|---|---|
| **Windows** | .NET SDK 8.0, WebView2 Runtime |
| **macOS** | .NET SDK 8.0, Xcode 15+, [XcodeGen](https://github.com/yonaskolb/XcodeGen) (`brew install xcodegen`) |

### Quick start

```powershell
# Windows — PowerShell
.\build.ps1                       # build all targets
.\build.ps1 -Target windows       # Windows Desktop only
.\build.ps1 -Target windows -Publish  # build + produce publish/ output
```

```bash
# macOS / Linux — Bash
chmod +x build.sh
./build.sh windows                # compile-check Windows Desktop (from any OS with the SDK)
./build.sh macos --publish        # sidecar + SwiftUI package (Mac only)
```

### Manual per-project commands

```bash
# Core (all platforms)
dotnet build Callspire.Core/Callspire.Core.csproj

# Desktop — Windows WPF
dotnet build Callspire.Desktop/Callspire.Desktop.csproj

# macOS sidecar
dotnet build Callspire.Service/Callspire.Service.csproj
Callspire.Mac/Scripts/build-service.sh arm64
```

### Publish (self-contained examples)

```bash
# Windows x64
dotnet publish Callspire.Desktop/Callspire.Desktop.csproj \
    -c Release -r win-x64 --no-self-contained \
    -o publish/windows

# macOS — SwiftUI .app + sidecar (see Callspire.Mac/README.md)
Callspire.Mac/Scripts/package-dmg.sh --arch arm64
# → publish/macos/Callspire.app  (+ Callspire-arm64.dmg)
```

---

## CI / GitHub Actions

The workflow at `.github/workflows/build.yml` runs two independent jobs on every push:

| Job | Runner | TFM |
|---|---|---|
| Windows Desktop | `windows-latest` | `net8.0-windows10.0.17763` |
| macOS sidecar | `macos-latest` | `Callspire.Service` (`net8.0`) |

Compiled artifacts are uploaded via `actions/upload-artifact` and available for 30 days after each run.

### GitHub Releases (Windows `.exe`)

Push a version tag to publish a Windows zip to [GitHub Releases](https://github.com/Intteger157/Callspire-softphone/releases):

```bash
git tag v1.0.0
git push origin v1.0.0
```

Workflow `.github/workflows/release.yml` builds `Callspire.exe` (plus dependencies) and attaches `Callspire-<version>-win-x64.zip` to the release. Tags with a hyphen (e.g. `v1.0.0-beta.1`) are marked as pre-releases.

---

## Optional native components

| Component | Location | Purpose |
|---|---|---|
| `webrtc_apm.dll` | `native-aec/build/Release/` | Windows native AEC (webrtc audio processing module). Falls back to pure-C# SoftwareAec if absent. |
| ffmpeg | `tools/` | Audio file conversion for call recordings. |
| WebRtcClient JS bundle | `WebRtcClient/` | WebRTC JS engine: WebView2 on Windows, WKWebView on macOS. |
