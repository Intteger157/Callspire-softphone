using System;
using SIPSorceryMedia.Abstractions;
using Softphone.Audio;

namespace Softphone
{
#if WINDOWS
    /// <summary>
    /// WASAPI Communications-mode endpoint adapted to the Core audio device interfaces.
    /// One underlying object provides both capture and render (shared AEC loop).
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

    // ── PortAudio device wrappers — cross-platform (macOS / Linux / Windows) ──

    /// <summary>PortAudio microphone capture (primary backend on macOS/Linux).</summary>
    internal sealed class PortAudioCaptureDevice : IAudioCaptureDevice
    {
        private readonly PortAudioAudioSource _source;

        public PortAudioCaptureDevice(PortAudioAudioSource source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public IAudioSource Source => _source;

        public Action<short[]>? OnRawPcmFrameTap
        {
            get => _source.OnRawPcmFrameTap;
            set => _source.OnRawPcmFrameTap = value;
        }

        public void Dispose() => _source.Dispose();
    }

    /// <summary>PortAudio speaker playback (primary backend on macOS/Linux).</summary>
    internal sealed class PortAudioRenderDevice : IAudioRenderDevice
    {
        private readonly PortAudioSink _sink;

        public PortAudioRenderDevice(PortAudioSink sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public IAudioSink Sink => _sink;

        public void Dispose() => _sink.Dispose();
    }
}
