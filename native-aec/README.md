# Native AEC library for Callspire

This directory builds `webrtc_apm.dll` — a native acoustic echo cancellation
library that Callspire loads via P/Invoke (`NativeAec.cs`).

Currently uses **SpeexDSP** (BSD-licensed, battle-tested in FreeSWITCH, Ooma,
PulseAudio, etc.). Can later be swapped to WebRTC APM without any C# changes.

## Prerequisites

- **CMake** ≥ 3.16
- **Visual Studio 2022** (or MSVC Build Tools) with C/C++ workload
- **SpeexDSP source code**

## Build steps

### 1. Download SpeexDSP

```powershell
cd native-aec
git clone https://gitlab.xiph.org/xiph/speexdsp.git speexdsp
```

Or download the archive:
https://gitlab.xiph.org/xiph/speexdsp/-/archive/master/speexdsp-master.tar.gz
and extract to `native-aec/speexdsp/`.

### 2. Configure and build

```powershell
cmake -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

### 3. Deploy

The build automatically copies `webrtc_apm.dll` to the Callspire output
directory. If it doesn't, copy manually:

```powershell
copy build\Release\webrtc_apm.dll "..\bin\Debug\net8.0-windows10.0.17763\win-x64\"
```

## How it works

1. If `webrtc_apm.dll` is missing → AEC is off; the app still runs.
2. Far-end reference is taken from **WasapiOut’s Read path** (samples that actually
   go to the DAC), via `AecTapWaveProvider` — not at RTP decode time.
3. **`apm_process_render`** accumulates full 20 ms frames and calls
   **`speex_echo_playback`** (Speex async API).
4. **`apm_process_capture`** calls **`speex_echo_capture`** then light preprocessor
   NLP. Speex aligns playback vs capture internally; a **fixed extra delay in samples**
   was removed after it caused a strong “second voice” / talking-to-yourself artefact
   when the guess (~300 ms) did not match the real path.

## Upgrading to WebRTC APM

To use WebRTC's AEC3 instead of SpeexDSP:
1. Build `webrtc-audio-processing` (https://gitlab.freedesktop.org/pulseaudio/webrtc-audio-processing)
2. Replace the SpeexDSP calls in `webrtc_apm.c` with WebRTC APM calls
3. Keep the same C API (`apm_create`, `apm_destroy`, `apm_process_render`, `apm_process_capture`)
4. Rebuild — no C# changes needed
