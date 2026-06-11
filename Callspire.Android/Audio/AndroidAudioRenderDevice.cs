using System;
using SIPSorceryMedia.Abstractions;
using Softphone.Audio;

namespace Softphone.Android.Audio
{
    /// <summary>Wraps <see cref="AndroidAudioTrackSink"/> for <see cref="IAudioRenderDevice"/>.</summary>
    internal sealed class AndroidAudioRenderDevice : IAudioRenderDevice
    {
        private readonly AndroidAudioTrackSink _sink;

        public AndroidAudioRenderDevice(AndroidAudioTrackSink sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public IAudioSink Sink => _sink;

        public void Dispose() => _sink.Dispose();
    }
}
