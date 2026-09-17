using System;
using SIPSorcery.Media;
using Softphone.Audio;

namespace Softphone
{
    /// <summary>
    /// Desktop audio backend selection (replaces the inline TRY1/TRY2 logic that lived
    /// in SipService.InitializeAudio):
    ///   Windows:      WASAPI Communications mode (AEC/NS/AGC) → PortAudio fallback.
    ///   macOS/Linux:  PortAudio with SoftwareAec (via EchoCancellerFactory).
    /// </summary>
    public sealed class DesktopAudioDeviceFactory : IAudioDeviceFactory
    {
        private const int AEC_SAMPLE_RATE = 8000;
        private const int AEC_FRAME_MS = 20;

        public AudioDeviceSession Create(AudioDeviceOptions options, AudioEncoder encoder)
        {
            if (encoder == null) throw new ArgumentNullException(nameof(encoder));
            options ??= new AudioDeviceOptions();

#if WINDOWS
            if (OperatingSystem.IsWindows())
                return CreateWindowsSession(options, encoder);
#endif
            return CreatePortAudioSession(options, encoder);
        }

#if WINDOWS
        private static AudioDeviceSession CreateWindowsSession(AudioDeviceOptions options, AudioEncoder encoder)
        {
            // TRY 1: WASAPI Communications mode (provides AEC/NS/AGC).
            try
            {
                var wasapi = new WasapiAudioEndPoint(encoder,
                    audioOutDeviceIndex: options.RenderDeviceIndex,
                    audioInDeviceIndex: options.CaptureDeviceIndex);

                wasapi.AecEnabled = options.EnableEchoCancellation;

                if (options.PreferredPayloadFormatId is int pt)
                {
                    wasapi.PrioritizeFormat(pt);
                }

                var devices = new WasapiAudioDevices(wasapi);
                return new AudioDeviceSession(devices, devices,
                    "WASAPI Communications (AEC/NS/AGC)",
                    requiresInboundFiltering: false);
            }
            catch (Exception wasapiEx)
            {
                AppLog.Log($"[DesktopAudioDeviceFactory] WASAPI failed ({wasapiEx.GetType().Name}: {wasapiEx.Message}), falling back to PortAudio");
                try
                {
                    return CreatePortAudioSession(options, encoder);
                }
                catch (Exception portAudioEx)
                {
                    throw new InvalidOperationException(
                        $"WASAPI failed ({wasapiEx.Message}); PortAudio fallback also failed ({portAudioEx.Message}). " +
                        "Check microphone/speaker devices and that no other app is holding them exclusively.",
                        portAudioEx);
                }
            }
        }
#endif

        private static AudioDeviceSession CreatePortAudioSession(AudioDeviceOptions options, AudioEncoder encoder)
        {
            try
            {
                var source = new PortAudioAudioSource(options.CaptureDeviceIndex, sampleRate: AEC_SAMPLE_RATE);
                var sink = new PortAudioSink(options.RenderDeviceIndex, sampleRate: AEC_SAMPLE_RATE, audioEncoder: encoder);

                if (options.EnableEchoCancellation)
                {
                    // Non-Windows → EchoCancellerFactory returns SoftwareAec (never loads webrtc_apm.dll)
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
