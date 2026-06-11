using System;
using System.Net;
using System.Threading.Tasks;

namespace Softphone.Audio
{
    /// <summary>
    /// Платформо-независимый контракт рекордера SIP-звонков (запись inbound RTP + outbound PCM в файл).
    /// Desktop-реализация — RtpCallRecorder (NAudio: ресемплинг/микширование/MP3).
    /// </summary>
    public interface IRtpCallRecorder : IDisposable
    {
        bool IsRecording { get; }

        /// <summary>Путь к итоговому файлу записи (после остановки — финальный MP3/WAV).</summary>
        string? RecordingFilePath { get; }

        void StartRecording(string phoneNumber, DateTime callStartTime, string recordingsDirectory);

        Task StopRecordingAsync(DateTime? callEndTime = null);

        /// <summary>Обрабатывает входящий RTP-пакет (удалённая сторона).</summary>
        void ProcessInboundRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload);

        /// <summary>Обрабатывает исходящий RTP payload (наша сторона, закодированный).</summary>
        void ProcessOutboundRtpPayload(byte payloadType, byte[] payload, int sourceRateHz);

        /// <summary>Обрабатывает сырой исходящий PCM 48 кГц (до кодирования, путь WASAPI/AEC tap).</summary>
        void ProcessOutboundRawPcm48k(short[] pcm48k);
    }

    /// <summary>
    /// Точка подключения платформенной реализации <see cref="IRtpCallRecorder"/>.
    /// Регистрируется на старте приложения (Desktop: App.xaml.cs).
    /// </summary>
    public static class RtpCallRecorderFactory
    {
        /// <summary>Фабрика рекордера. null — запись звонков недоступна на платформе.</summary>
        public static Func<IRtpCallRecorder?>? Create { get; set; }

        /// <summary>Восстановление WAV из «осиротевшего» PCM-файла после краша (path → success).</summary>
        public static Func<string, bool>? TryRecoverWavFromOrphanPcm { get; set; }
    }
}
