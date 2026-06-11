using System;
using SIPSorceryMedia.Abstractions;
using Softphone.Audio;

namespace Softphone.Android.Audio
{
    /// <summary>Wraps <see cref="AndroidAudioRecordSource"/> for <see cref="IAudioCaptureDevice"/>.</summary>
    internal sealed class AndroidAudioCaptureDevice : IAudioCaptureDevice
    {
        private readonly AndroidAudioRecordSource _source;

        public AndroidAudioCaptureDevice(AndroidAudioRecordSource source)
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
}
