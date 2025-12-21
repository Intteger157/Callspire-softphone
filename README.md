# Callspire Softphone

**Callspire** — Windows softphone application built with **.NET 8 (WPF)** that supports **SIP and WebRTC calling**, designed for modern corporate telephony and integrations.

> Status: **Stable (v1.0.0)**  
> Platform: **Windows x64**  
> Tech stack: **.NET 8 · WPF · WebRTC · SIP · ffmpeg**

---

## ✨ Features

- ✅ SIP calling
- ✅ WebRTC calling (WSS)
- ✅ Modern dark UI (WPF)
- ✅ Call history & detailed call info
- ✅ Incoming / outgoing calls
- ✅ Call recording  
  - Records remote RTP audio  
  - Local microphone recording — **work in progress**
- ✅ Audio device selection (mic / speaker)
- ✅ ffmpeg-based audio conversion

---

## 🖼️ Screenshots

### Dial pad
<img width="1095" height="698" alt="image" src="https://github.com/user-attachments/assets/3213305d-4a80-4913-8435-d47b16811d73" />


### Active call
<img width="376" height="760" alt="image" src="https://github.com/user-attachments/assets/ebe2c7d9-831b-411d-9866-07141268a3c9" />


### Call details
<img width="891" height="690" alt="image" src="https://github.com/user-attachments/assets/39c4d059-5bae-4b6f-8f1f-fe8f202a14b8" />


---

📦 Download

Latest Windows installer is available in **Releases**:

➡ **Releases → Callspire v1.0.0 → Callspire-setup.exe**

---

🚀 Development

Requirements
- Windows 10 / 11 (x64)
- .NET SDK **8.0**
- WebView2 Runtime (Evergreen)

Build
```bash
dotnet restore
dotnet build -c Release
