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

    /// <summary>
    /// Контекст звонка с информацией о транспорте и идентификаторах
    /// </summary>
    public sealed class CallContext
    {
        public CallTransport Transport { get; init; }
        public string? SipCallId { get; init; }
        public string? WebRtcSessionId { get; init; }
        public string RemoteNumber { get; init; } = "";
        public DateTime StartedAt { get; init; }
        
        public CallContext(CallTransport transport, string remoteNumber, DateTime startedAt, string? sipCallId = null, string? webRtcSessionId = null)
        {
            Transport = transport;
            RemoteNumber = remoteNumber;
            StartedAt = startedAt;
            SipCallId = sipCallId;
            WebRtcSessionId = webRtcSessionId;
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
        
        // На будущее: можно добавить
        // public string? AudioCodec { get; set; } // opus / pcmu / g722
        // public string? MediaSource { get; set; } // RTP / RTCPeerConnection
    }
}

