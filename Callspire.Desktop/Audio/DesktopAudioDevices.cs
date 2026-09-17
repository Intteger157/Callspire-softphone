using System;
using SIPSorceryMedia.Abstractions;
using Softphone.Audio;

namespace Softphone
{
#if WINDOWS
    /// <summary>
    /// WASAPI Communications-mode endpoint adapted to the Core audio device interfaces.
    /// One underlying object provides both capture and render (shared AEC loop).
    /// PortAudio wrappers live in Callspire.AppHost (PortAudioDevices.cs).
    /// </summary>
    internal sealed class WasapiAudioDevices : IAudioCaptureDevice, IAudioRenderDevice
    {
        private readonly WasapiAudioEndPoint _endPoint;

        public WasapiAudioDevices(WasapiAudioEndPoint endPoint)
        {
            _endPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));
        }

        public IAudioSource Source => _endPoint;
        public IAudioSink   Sink   => _endPoint;

        public Action<short[]>? OnRawPcmFrameTap
        {
            get => _endPoint.OnRawPcmFrameTap;
            set => _endPoint.OnRawPcmFrameTap = value;
        }

        public void Dispose() => _endPoint.Dispose();
    }
#endif
}
