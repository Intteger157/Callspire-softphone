using System;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Softphone.Audio
{
    /// <summary>Options for opening a capture/render device pair for a call.</summary>
    public sealed class AudioDeviceOptions
    {
        /// <summary>Microphone device index; -1 = system default.</summary>
        public int CaptureDeviceIndex { get; init; } = -1;

        /// <summary>Speaker device index; -1 = system default.</summary>
        public int RenderDeviceIndex { get; init; } = -1;

        /// <summary>Request acoustic echo cancellation if the backend supports it.</summary>
        public bool EnableEchoCancellation { get; init; }

        /// <summary>
        /// RTP payload format to prioritise in offers (e.g. 8 = PCMA). The backend reorders
        /// its supported-format list so VoIPMediaSession picks this codec first.
        /// </summary>
        public int? PreferredPayloadFormatId { get; init; }
    }

    /// <summary>
    /// Creates platform audio devices for a call. Implemented per-head
    /// (DesktopAudioDeviceFactory: WASAPI/WinMM/PortAudio; Android head: AudioRecord/AudioTrack).
    /// </summary>
    public interface IAudioDeviceFactory
    {
        /// <summary>Opens a linked capture/render pair. Throws if no audio backend is usable.</summary>
        AudioDeviceSession Create(AudioDeviceOptions options, AudioEncoder encoder);
    }

    /// <summary>
    /// A live capture/render device pair for the duration of one audio session (call).
    /// Disposing closes both devices (a backend may use a single object for both roles).
    /// </summary>
    public sealed class AudioDeviceSession : IDisposable
    {
        public AudioDeviceSession(
            IAudioCaptureDevice capture,
            IAudioRenderDevice render,
            string description,
            bool requiresInboundFiltering = false)
        {
            Capture = capture ?? throw new ArgumentNullException(nameof(capture));
            Render = render ?? throw new ArgumentNullException(nameof(render));
            Description = description ?? "";
            RequiresInboundFiltering = requiresInboundFiltering;
        }

        public IAudioCaptureDevice Capture { get; }

        public IAudioRenderDevice Render { get; }

        /// <summary>Human-readable backend description for logs (e.g. "WASAPI Communications").</summary>
        public string Description { get; }

        /// <summary>
        /// True when the backend delivers unfiltered inbound RTP that needs FilteringAudioSink
        /// (DTMF/comfort-noise suppression), e.g. the WinMM fallback path.
        /// </summary>
        public bool RequiresInboundFiltering { get; }

        public MediaEndPoints ToMediaEndPoints() => new MediaEndPoints
        {
            AudioSource = Capture.Source,
            AudioSink = Render.Sink
        };

        public void Dispose()
        {
            try { Capture.Dispose(); } catch { }
            // A single backend object may implement both interfaces — don't dispose twice.
            if (!ReferenceEquals(Capture, Render))
            {
                try { Render.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Process-wide default factory, set once by the platform head at startup
    /// (e.g. App.OnStartup → DesktopAudioDeviceFactory). Services resolve it lazily
    /// when no factory was passed to their constructor.
    /// </summary>
    public static class AudioDeviceFactory
    {
        public static IAudioDeviceFactory? Default { get; set; }
    }
}
