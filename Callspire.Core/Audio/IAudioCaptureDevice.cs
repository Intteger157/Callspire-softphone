using System;
using SIPSorceryMedia.Abstractions;

namespace Softphone.Audio
{
    /// <summary>
    /// Microphone capture abstraction. Platform heads provide implementations:
    /// WASAPI/WinMM on Windows, PortAudio on macOS/Linux, AudioRecord on Android.
    /// Business logic (SipService) consumes only this interface — never NAudio/WASAPI types.
    /// </summary>
    public interface IAudioCaptureDevice : IDisposable
    {
        /// <summary>SIPSorcery audio source fed into VoIPMediaSession.</summary>
        IAudioSource Source { get; }

        /// <summary>
        /// Optional tap of raw 8 kHz PCM frames BEFORE codec encoding (used for call
        /// recording at full 16-bit quality). Backends that cannot provide it may ignore it.
        /// </summary>
        Action<short[]>? OnRawPcmFrameTap { get; set; }
    }
}
