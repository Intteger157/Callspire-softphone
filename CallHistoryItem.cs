using System;

namespace Softphone
{
    public enum CallStatus
    {
        Calling,
        Connected,
        Ended,
        Failed,
        Cancelled
    }

    public class CallHistoryItem
    {
        public string PhoneNumber { get; set; } = string.Empty;
        public DateTime CallTime { get; set; }
        public CallStatus Status { get; set; }
        public TimeSpan? Duration { get; set; }
        public string? ErrorMessage { get; set; }
        public bool IsIncoming { get; set; } = false; // false = исходящий, true = входящий
    }
}

