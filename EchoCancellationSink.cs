using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;

namespace Softphone
{
    /// <summary>
    /// Обертка для AudioSink, которая перехватывает reference сигнал для эхоподавления
    /// </summary>
    public class EchoCancellationSink : IAudioSink
    {
        private readonly IAudioSink _baseSink;
        private readonly EchoCancelledAudioSource? _echoCancelledSource;

        public EchoCancellationSink(IAudioSink baseSink, EchoCancelledAudioSource? echoCancelledSource = null)
        {
            _baseSink = baseSink ?? throw new ArgumentNullException(nameof(baseSink));
            _echoCancelledSource = echoCancelledSource;
        }

        public event SourceErrorDelegate? OnAudioSinkError
        {
            add => _baseSink.OnAudioSinkError += value;
            remove => _baseSink.OnAudioSinkError -= value;
        }

        public Task CloseAudioSink()
        {
            return _baseSink.CloseAudioSink();
        }

        public List<AudioFormat> GetAudioSinkFormats()
        {
            return _baseSink.GetAudioSinkFormats();
        }

        public Task StartAudioSink()
        {
            return _baseSink.StartAudioSink();
        }

        public Task PauseAudioSink()
        {
            return _baseSink.PauseAudioSink();
        }

        public Task ResumeAudioSink()
        {
            return _baseSink.ResumeAudioSink();
        }

        public void RestrictFormats(Func<AudioFormat, bool> filter)
        {
            _baseSink.RestrictFormats(filter);
        }

        public void SetAudioSinkFormat(AudioFormat audioFormat)
        {
            _baseSink.SetAudioSinkFormat(audioFormat);
        }

        public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload)
        {
            // Перехватываем RTP пакеты
            // Для эхоподавления нужно декодировать RTP в raw samples, но это сложно
            // Поэтому используем более простой подход - перехватываем на уровне Windows API
            // Пока просто пробрасываем дальше
            _baseSink.GotAudioRtp(remoteEndPoint, ssrc, seqnum, timestamp, payloadID, marker, payload);
        }
    }
}
