using System;
using System.Collections.Generic;

namespace Softphone
{
    public enum CallStatus
    {
        Calling,
        Connected,
        Ended,
        Failed,
        Cancelled,
        Missed
    }

    public enum CallEndedBy
    {
        Unknown,
        LocalUser,
        RemoteParty
    }

    public enum CallTransport
    {
        Sip,
        WebRtc
    }

    /// <summary>Which app connection handled the call (main or secondary line).</summary>
    public enum CallConnectionSlot
    {
        Unknown = 0,
        Main = 1,
        Secondary = 2
    }

    public enum AmoCrmUploadStatus
    {
        NotUploaded,    // Не загружено (по умолчанию)
        Uploaded,       // Успешно загружено
        Cancelled,      // Отменено пользователем (закрыл окно выбора лида)
        Failed          // Ошибка загрузки
    }

    /// <summary>
    /// Контекст звонка с информацией о транспорте и идентификаторах
    /// </summary>
    public sealed class CallContext
    {
        public CallTransport Transport { get; init; }
        public string? SipCallId { get; init; }
        public string? WebRtcSessionId { get; set; }
        public string RemoteNumber { get; init; } = "";
        public DateTime StartedAt { get; init; }
        public long? AmoCrmLeadId { get; init; } // ID лида в AmoCRM, если звонок инициирован из браузера
        
        public CallContext(CallTransport transport, string remoteNumber, DateTime startedAt, string? sipCallId = null, string? webRtcSessionId = null, long? amoCrmLeadId = null)
        {
            Transport = transport;
            RemoteNumber = remoteNumber;
            StartedAt = startedAt;
            SipCallId = sipCallId;
            WebRtcSessionId = webRtcSessionId;
            AmoCrmLeadId = amoCrmLeadId;
            
            // Логируем создание контекста с leadId для диагностики
            AppLog.Log($"[CallContext] Created: Transport={transport}, RemoteNumber={remoteNumber}, AmoCrmLeadId={amoCrmLeadId?.ToString() ?? "null"}");
        }
    }

    public class CallHistoryItem
    {
        public string PhoneNumber { get; set; } = string.Empty;
        public DateTime CallTime { get; set; }
        public CallStatus Status { get; set; }
        public TimeSpan? Duration { get; set; }
        public string? ErrorMessage { get; set; }
        public bool IsIncoming { get; set; } = false; // false = исходящий, true = входящий
        
        // Информация о транспорте звонка
        public CallTransport Transport { get; set; } = CallTransport.Sip; // SIP или WebRTC
        public CallConnectionSlot ConnectionSlot { get; set; } = CallConnectionSlot.Unknown;
        public string? SipCallId { get; set; } // Call-ID для SIP звонков
        public string? WebRtcSessionId { get; set; } // Session ID для WebRTC звонков
        
        // Детальная информация о вызове
        public DateTime? RingbackStartTime { get; set; } // Когда начались гудки
        public DateTime? RingbackEndTime { get; set; } // Когда закончились гудки
        public DateTime? AnswerTime { get; set; } // Когда абонент взял трубку
        public bool WasAnswered { get; set; } = false; // Взял ли абонент трубку
        public CallEndedBy EndedBy { get; set; } = CallEndedBy.Unknown; // Кто завершил звонок
        public List<string> TechnicalDetails { get; set; } = new List<string>(); // Технические детали из логов
        public string? RecordingFilePath { get; set; } // Путь к файлу записи звонка
        public long? AmoCrmLeadId { get; set; } // ID лида в AmoCRM/Kommo, к которому прикреплён звонок
        public AmoCrmUploadStatus AmoCrmUploadStatus { get; set; } = AmoCrmUploadStatus.NotUploaded; // Статус загрузки записи в AmoCRM
        public string? AmoCrmUploadReason { get; set; } // Причина статуса (например, "File not found", "User cancelled", "Upload failed: ...")

        // Outbound CallerID from P-Asserted-Identity header (the number PBX presents to the remote party)
        public string? OutboundCallerId { get; set; }

        // На будущее: можно добавить
        // public string? AudioCodec { get; set; } // opus / pcmu / g722
        // public string? MediaSource { get; set; } // RTP / RTCPeerConnection
    }
}

