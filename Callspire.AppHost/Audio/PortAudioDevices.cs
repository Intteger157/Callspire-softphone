using System;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Softphone.Audio;

namespace Softphone
{
    /// <summary>PortAudio microphone capture (primary backend on macOS/Linux).</summary>
    public sealed class PortAudioCaptureDevice : IAudioCaptureDevice
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
    public sealed class PortAudioRenderDevice : IAudioRenderDevice
    {
        private readonly PortAudioSink _sink;

        public PortAudioRenderDevice(PortAudioSink sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public IAudioSink Sink => _sink;

        public void Dispose() => _sink.Dispose();
    }

    /// <summary>
    /// PortAudio-only <see cref="IAudioDeviceFactory"/> (macOS / Linux, and the Windows PortAudio fallback).
    /// Echo cancellation goes through <see cref="EchoCancellerFactory"/> (SoftwareAec off Windows).
    /// </summary>
    public sealed class PortAudioDeviceFactory : IAudioDeviceFactory
    {
        private const int AEC_SAMPLE_RATE = 8000;
        private const int AEC_FRAME_MS = 20;

        public AudioDeviceSession Create(AudioDeviceOptions options, AudioEncoder encoder)
            => CreateSession(options, encoder);

        public static AudioDeviceSession CreateSession(AudioDeviceOptions options, AudioEncoder encoder)
        {
            if (encoder == null) throw new ArgumentNullException(nameof(encoder));
            options ??= new AudioDeviceOptions();

            try
            {
                var source = new PortAudioAudioSource(options.CaptureDeviceIndex, sampleRate: AEC_SAMPLE_RATE);
                var sink = new PortAudioSink(options.RenderDeviceIndex, sampleRate: AEC_SAMPLE_RATE, audioEncoder: encoder);

                if (options.EnableEchoCancellation)
                {
                    var canceller = EchoCancellerFactory.Create(AEC_SAMPLE_RATE, 1, AEC_FRAME_MS);
                    if (canceller != null)
                    {
                        source.EchoCanceller = canceller;
                        sink.EchoCanceller = canceller;
                    }
                }

                return new AudioDeviceSession(
                    new PortAudioCaptureDevice(source),
                    new PortAudioRenderDevice(sink),
                    "PortAudio" + (source.EchoCanceller != null ? " + SoftwareAec" : ""),
                    requiresInboundFiltering: false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to create PortAudio endpoint: {ex.Message}", ex);
            }
        }
    }
}
