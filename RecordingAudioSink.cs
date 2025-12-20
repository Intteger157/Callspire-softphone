using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;

namespace Softphone
{
    /// <summary>
    /// Обертка для AudioSink, которая перехватывает inbound RTP пакеты для записи
    /// </summary>
    public class RecordingAudioSink : IAudioSink
    {
        private readonly IAudioSink _originalSink;
        private readonly SipService _sipService;
        private int _packetCount = 0;

        public RecordingAudioSink(IAudioSink originalSink, SipService sipService)
        {
            _originalSink = originalSink ?? throw new ArgumentNullException(nameof(originalSink));
            _sipService = sipService ?? throw new ArgumentNullException(nameof(sipService));
        }

        public event SourceErrorDelegate? OnAudioSinkError
        {
            add => _originalSink.OnAudioSinkError += value;
            remove => _originalSink.OnAudioSinkError -= value;
        }

        public Task CloseAudioSink() => _originalSink.CloseAudioSink();
        public List<AudioFormat> GetAudioSinkFormats() => _originalSink.GetAudioSinkFormats();
        public Task StartAudioSink() => _originalSink.StartAudioSink();
        public Task PauseAudioSink() => _originalSink.PauseAudioSink();
        public Task ResumeAudioSink() => _originalSink.ResumeAudioSink();
        public void SetAudioSinkFormat(AudioFormat audioFormat) => _originalSink.SetAudioSinkFormat(audioFormat);
        public void RestrictFormats(Func<AudioFormat, bool> filter) => _originalSink.RestrictFormats(filter);
        
        public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[]? payload)
        {
            // Логируем первые несколько вызовов для диагностики
            if (++_packetCount <= 10)
            {
                MainWindow.Log($"[RecordingAudioSink] GotAudioRtp called #{_packetCount}: remoteEndPoint={remoteEndPoint}, timestamp={timestamp}, payloadID={payloadID}, payloadSize={payload?.Length ?? 0}");
            }
            
            // Перехватываем inbound RTP для записи
            if (payload != null && payload.Length > 0)
            {
                _sipService?.RecordInboundRtp(remoteEndPoint, ssrc, seqnum, timestamp, payloadID, marker, payload);
            }
            
            // Передаем оригинальному sink для воспроизведения
            ForwardToOriginalSink(remoteEndPoint, ssrc, seqnum, timestamp, payloadID, marker, payload);
        }

        /// <summary>
        /// Передает RTP пакет оригинальному sink для воспроизведения
        /// </summary>
        private void ForwardToOriginalSink(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[]? payload)
        {
            try
            {
                _originalSink?.GotAudioRtp(remoteEndPoint, ssrc, seqnum, timestamp, payloadID, marker, payload);
                
                if (_packetCount <= 10)
                {
                    MainWindow.Log($"[RecordingAudioSink] Forwarded packet #{_packetCount} to original sink OK");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[RecordingAudioSink] ERROR calling original sink GotAudioRtp: {ex.Message}");
            }
        }
    }
}

