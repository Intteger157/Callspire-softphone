using System;
using SIPSorcery.Media;
using Softphone.Audio;

namespace Softphone.Android.Audio
{
    /// <summary>
    /// Android implementation of <see cref="IAudioDeviceFactory"/>.
    /// Uses <see cref="Android.Media.AudioRecord"/> / <see cref="Android.Media.AudioTrack"/>
    /// with <see cref="EchoCancellerFactory"/> (SoftwareAec on Android).
    /// </summary>
    public sealed class AndroidAudioDeviceFactory : IAudioDeviceFactory
    {
        private const int SampleRate = 8000;
        private const int FrameMs    = 20;

        public AudioDeviceSession Create(AudioDeviceOptions options, AudioEncoder encoder)
        {
            if (encoder == null) throw new ArgumentNullException(nameof(encoder));
            options ??= new AudioDeviceOptions();

            try
            {
                var source = new AndroidAudioRecordSource(SampleRate, channels: 1);
                var sink   = new AndroidAudioTrackSink(SampleRate, channels: 1);

                if (options.EnableEchoCancellation)
                {
                    var canceller = EchoCancellerFactory.Create(SampleRate, 1, FrameMs);
                    if (canceller != null)
                    {
                        source.EchoCanceller = canceller;
                        sink.EchoCanceller   = canceller;
                    }
                }

                var capture = new AndroidAudioCaptureDevice(source);
                var render  = new AndroidAudioRenderDevice(sink);

                var description = "Android AudioRecord/AudioTrack"
                    + (source.EchoCanceller != null ? " + SoftwareAec" : "");

                return new AudioDeviceSession(capture, render, description, requiresInboundFiltering: false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to create Android audio session: {ex.Message}", ex);
            }
        }
    }
}
