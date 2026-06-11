using System;
using SIPSorceryMedia.Abstractions;

namespace Softphone.Audio
{
    /// <summary>
    /// Speaker/headset playback abstraction. Platform heads provide implementations:
    /// WASAPI/WinMM on Windows, PortAudio on macOS/Linux, AudioTrack on Android.
    /// </summary>
    public interface IAudioRenderDevice : IDisposable
    {
        /// <summary>SIPSorcery audio sink fed into VoIPMediaSession.</summary>
        IAudioSink Sink { get; }
    }
}
