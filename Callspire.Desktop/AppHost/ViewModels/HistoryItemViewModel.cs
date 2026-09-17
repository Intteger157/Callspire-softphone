using System;

namespace Softphone.AppHost.ViewModels
{
    /// <summary>
    /// Presentation wrapper for <see cref="CallHistoryItem"/>. Pure data (strings/bools) so it is
    /// bindable from Avalonia and WPF alike; colours/icons are resolved by the view via converters.
    /// </summary>
    public sealed class HistoryItemViewModel
    {
        public CallHistoryItem Model { get; }

        public HistoryItemViewModel(CallHistoryItem model, string mainConnectionName, string secondaryConnectionName)
        {
            Model = model;
            ConnectionLabel = CallStatisticsService.FormatConnectionLabel(model, mainConnectionName, secondaryConnectionName);
        }

        public string PhoneNumber => Model.PhoneNumber;
        public string PhoneNumberDisplay => string.IsNullOrWhiteSpace(Model.PhoneNumber) ? "Unknown" : Model.PhoneNumber;
        public bool IsIncoming => Model.IsIncoming;
        public bool IsMissed => Model.IsIncoming && Model.Status == CallStatus.Missed;
        public bool WasAnswered => Model.WasAnswered || Model.AnswerTime.HasValue;
        public DateTime CallTime => Model.CallTime;

        public string CallTimeText
        {
            get
            {
                var t = Model.CallTime;
                if (t.Date == DateTime.Today) return $"Today {t:HH:mm}";
                if (t.Date == DateTime.Today.AddDays(-1)) return $"Yesterday {t:HH:mm}";
                return t.ToString("dd MMM HH:mm");
            }
        }

        public string DurationText
        {
            get
            {
                var d = Model.Duration ?? (WasAnswered ? CallStatisticsService.GetEffectiveTalkDuration(Model) : TimeSpan.Zero);
                return d > TimeSpan.Zero ? CallStatisticsService.FormatDuration(d) : "";
            }
        }

        public string Status => Model.Status switch
        {
            CallStatus.Calling => "In progress",
            CallStatus.Connected => "Connected",
            CallStatus.Ended => WasAnswered ? "Completed" : "No answer",
            CallStatus.Failed => "Failed",
            CallStatus.Cancelled => "Cancelled",
            CallStatus.Missed => "Missed",
            _ => Model.Status.ToString()
        };

        /// <summary>"ok" | "bad" | "neutral" — mapped to brushes by the view.</summary>
        public string StatusKind => Model.Status switch
        {
            CallStatus.Connected => "ok",
            CallStatus.Ended when WasAnswered => "ok",
            CallStatus.Failed => "bad",
            CallStatus.Missed => "bad",
            CallStatus.Cancelled => "neutral",
            _ => "neutral"
        };

        public bool IsStatusOk => StatusKind == "ok";
        public bool IsStatusBad => StatusKind == "bad";
        public bool IsOutgoing => !Model.IsIncoming;
        public bool IsIncomingAnswered => Model.IsIncoming && !IsMissed;

        public string TransportLabel => Model.Transport == CallTransport.WebRtc ? "WebRTC" : "SIP";
        public bool IsWebRtc => Model.Transport == CallTransport.WebRtc;
        public string ConnectionLabel { get; }
        public bool HasRecording => CallStatisticsService.HasLocalRecording(Model);
        public string? OutboundCallerId => Model.OutboundCallerId;
        public string CrmStatus => Model.AmoCrmUploadStatus switch
        {
            AmoCrmUploadStatus.Uploaded => "Kommo ✓",
            AmoCrmUploadStatus.Failed => "Kommo ✕",
            AmoCrmUploadStatus.Cancelled => "Kommo –",
            _ => ""
        };
    }
}
