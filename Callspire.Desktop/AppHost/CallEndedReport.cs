using System;
using System.Collections.Generic;

namespace Softphone.AppHost
{
    /// <summary>
    /// Everything a call window knows about a finished call. Replaces the 13-argument
    /// <c>OnCallDetailsChanged</c> delegate used by the WPF CallWindow so that the
    /// controller can persist history, fetch CDR data and enqueue Kommo jobs without
    /// depending on a UI type.
    /// </summary>
    public sealed class CallEndedReport
    {
        public string PhoneNumber { get; set; } = "";
        public DateTime CallTime { get; set; }
        public DateTime? RingbackStart { get; set; }
        public DateTime? RingbackEnd { get; set; }
        public DateTime? AnswerTime { get; set; }
        public bool WasAnswered { get; set; }
        public CallEndedBy EndedBy { get; set; } = CallEndedBy.Unknown;
        public List<string> TechnicalDetails { get; set; } = new();
        public TimeSpan? Duration { get; set; }
        public string? RecordingFilePath { get; set; }
        public CallTransport Transport { get; set; } = CallTransport.Sip;
        public string? SipCallId { get; set; }
        public string? WebRtcSessionId { get; set; }
        public string? OutboundCallerId { get; set; }
        public ConnectionSlot Slot { get; set; } = ConnectionSlot.Main;
        public bool IsIncoming { get; set; }
        public long? InboundRtpPackets { get; set; }

        /// <summary>Kommo lead id selected in the call window (manual pick), if any.</summary>
        public long? LeadId { get; set; }

        /// <summary>Kommo lead id detected from the browser (click-to-call from Kommo card), if any.</summary>
        public long? BrowserLeadId { get; set; }

        /// <summary>Session id relevant for Kommo processing (WebRTC session or SIP Call-ID).</summary>
        public string? SessionIdForCrm => Transport == CallTransport.WebRtc ? WebRtcSessionId : SipCallId;

        /// <summary>Final history status derived from the report.</summary>
        public CallStatus FinalStatus
        {
            get
            {
                if (WasAnswered) return CallStatus.Ended;
                if (IsIncoming) return CallStatus.Missed;
                return EndedBy switch
                {
                    CallEndedBy.LocalUser => CallStatus.Cancelled,
                    CallEndedBy.RemoteParty => CallStatus.Failed,
                    _ => CallStatus.Failed,
                };
            }
        }
    }
}
