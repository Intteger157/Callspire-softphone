# Callspire Softphone

**Callspire** is a cross-platform softphone built on **.NET 8 + Avalonia UI** that supports **SIP and WebRTC calling**, designed for modern corporate telephony and integrations.

> Status: **Active Development (v1.1.x)**
> Platforms: **Windows · macOS · Linux · Android**
> Tech stack: **.NET 8 · Avalonia 12 · WPF (Windows legacy) · SIPSorcery · WebRTC · NAudio · PortAudio**

[![Build](https://github.com/your-org/softphone-crossplatform/actions/workflows/build.yml/badge.svg)](https://github.com/your-org/softphone-crossplatform/actions/workflows/build.yml)

---

## Features

- SIP calling (G.711, G.722, Opus via Concentus)
- WebRTC calling (WSS / TURN)
- Avalonia cross-platform UI (dark / light / system theme)
- Call history and detailed call statistics
- Incoming / outgoing calls with call recording
- Audio device selection (mic / speaker)
- Acoustic echo cancellation: native `webrtc_apm.dll` on Windows, pure-C# `SoftwareAec` on macOS / Linux / Android
- AmoCRM / Kommo CRM integration
- МиКО АТС CDR / caller-ID lookup

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
│   ├── Audio/               #   IAudioCaptureDevice, IAudioRenderDevice, AEC
│   ├── WebRtc/              #   IWebRtcEngineHost, WebRtcService
│   └── *.cs                 #   SipService, AppSettings, CallHistory, …
│
├── Callspire.Desktop/       # Windows + macOS + Linux desktop head
│   ├── Avalonia/            #   Avalonia window ports (Phase 5)
│   ├── Audio/               #   WASAPI (Windows) / PortAudio (macOS, Linux)
│   ├── WebRtc/              #   WebView2 engine (Windows) / headless stub
│   └── *.xaml / *.axaml     #   WPF (Windows legacy) + Avalonia windows
│
└── Callspire.Android/       # Android head (Avalonia 11.3.x / net8.0-android)
    ├── Audio/               #   AudioRecord / AudioTrack devices
    └── Services/            #   CallForegroundService
```

### Target frameworks

| Project | TFM(s) |
|---|---|
| `Callspire.Core` | `net8.0` |
| `Callspire.Desktop` | `net8.0-windows10.0.17763` (WPF + WinExe) · `net8.0` (Avalonia, macOS/Linux) |
| `Callspire.Android` | `net8.0-android34.0` |

---

## Building

### Prerequisites

| Platform | Requirements |
|---|---|
| **Windows** | .NET SDK 8.0, WebView2 Runtime |
| **macOS** | .NET SDK 8.0, PortAudio (`brew install portaudio`) |
| **Linux** | .NET SDK 8.0, `libportaudio2` (`apt install libportaudio2`) |
| **Android** | .NET SDK 8.0, `dotnet workload install android`, Android SDK |

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
./build.sh                        # build all targets
./build.sh macos                  # macOS Desktop only
./build.sh linux --publish        # build + produce publish/ output
./build.sh android                # Android APK (requires workload + SDK)
```

### Manual per-project commands

```bash
# Core (all platforms)
dotnet build Callspire.Core/Callspire.Core.csproj

# Desktop — Windows
dotnet build Callspire.Desktop/Callspire.Desktop.csproj -f net8.0-windows10.0.17763

# Desktop — macOS / Linux
dotnet build Callspire.Desktop/Callspire.Desktop.csproj -f net8.0

# Android
dotnet workload install android
dotnet build Callspire.Android/Callspire.Android.csproj -f net8.0-android34.0
```

### Publish (self-contained examples)

```bash
# Windows x64
dotnet publish Callspire.Desktop/Callspire.Desktop.csproj \
    -f net8.0-windows10.0.17763 -c Release -r win-x64 --no-self-contained \
    -o publish/windows

# macOS — .app bundle (self-contained; Info.plist with callspire:// scheme + microphone usage)
Callspire.Desktop/macOS/package-app.sh --arch arm64            # or x64 | universal
Callspire.Desktop/macOS/package-app.sh --arch universal --dmg \
    --sign "Developer ID Application: Your Name (TEAMID)" \
    --notarize-profile callspire-notary                         # signed + notarized .dmg
# → publish/macos/Callspire.app  (+ Callspire-<version>-<arch>.dmg)
# Checklist for certificates/notarytool is in the script header.

# Linux x64
dotnet publish Callspire.Desktop/Callspire.Desktop.csproj \
    -f net8.0 -c Release -r linux-x64 --no-self-contained \
    -o publish/linux

# Android APK
dotnet publish Callspire.Android/Callspire.Android.csproj \
    -f net8.0-android34.0 -c Release -o publish/android
```

---

## CI / GitHub Actions

The workflow at `.github/workflows/build.yml` runs four independent jobs on every push:

| Job | Runner | TFM |
|---|---|---|
| Windows Desktop | `windows-latest` | `net8.0-windows10.0.17763` |
| macOS Desktop | `macos-latest` | `net8.0` |
| Linux Desktop | `ubuntu-latest` | `net8.0` |
| Android APK | `ubuntu-latest` | `net8.0-android34.0` |

Compiled artifacts are uploaded via `actions/upload-artifact` and available for 30 days after each run.

---

## Optional native components

| Component | Location | Purpose |
|---|---|---|
| `webrtc_apm.dll` | `native-aec/build/Release/` | Windows native AEC (webrtc audio processing module). Falls back to pure-C# SoftwareAec if absent. |
| ffmpeg | `tools/` | Audio file conversion for call recordings. |
| WebRtcClient JS bundle | `WebRtcClient/` | WebRTC JS engine loaded in WebView2 (Windows only). |

---

## Android (legacy Kotlin scaffold)

A minimal Android-only Kotlin scaffold is available in `android-softphone/`. It has been superseded by the cross-platform `Callspire.Android` .NET project described above.
