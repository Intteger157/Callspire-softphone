using System;
using System.Collections.Generic;
using Softphone.AppHost;
using Softphone.AppHost.ViewModels;

namespace Softphone.Service.Contracts
{
    /// <summary>C# → Swift `showCallWindow`: immutable facts about a new call session (no service pointers).</summary>
    public sealed class CallWindowDto
    {
        public string SessionId { get; init; } = "";
        public string PhoneNumber { get; init; } = "";
        public bool IsIncoming { get; init; }
        public bool IsWebRtc { get; init; }
        public string Slot { get; init; } = "main";
        public string TransportLabel { get; init; } = "";
        public string ConnectionLabel { get; init; } = "";
        public string WindowTitle { get; init; } = "";
        public DateTime CallStartTime { get; init; }
        public bool IsOriginateCall { get; init; }
        public CallStateDto State { get; init; } = new();
    }

    /// <summary>C# → Swift `callStateChanged`: bindable CallViewModel state.</summary>
    public sealed class CallStateDto
    {
        public string SessionId { get; init; } = "";
        public string CallerDisplay { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string TimerText { get; init; } = "";
        /// <summary>"neutral" | "good" | "bad"</summary>
        public string StatusTone { get; init; } = "neutral";
        public bool ShowIncomingButtons { get; init; }
        public bool ShowHangupButton { get; init; }
        public bool ShowControls { get; init; }
        public bool IsMuted { get; init; }
        public bool IsOnHold { get; init; }
        public bool IsKeypadVisible { get; init; }
        public bool IsRecordingIndicatorVisible { get; init; }
        public bool WasAnswered { get; init; }

        public static CallStateDto From(string sessionId, CallViewModel vm) => new()
        {
            SessionId = sessionId,
            CallerDisplay = vm.CallerDisplay, StatusText = vm.StatusText, TimerText = vm.TimerText,
            StatusTone = vm.StatusTone switch { CallUiTone.Good => "good", CallUiTone.Bad => "bad", _ => "neutral" },
            ShowIncomingButtons = vm.ShowIncomingButtons, ShowHangupButton = vm.ShowHangupButton, ShowControls = vm.ShowControls,
            IsMuted = vm.IsMuted, IsOnHold = vm.IsOnHold, IsKeypadVisible = vm.IsKeypadVisible,
            IsRecordingIndicatorVisible = vm.IsRecordingIndicatorVisible, WasAnswered = vm.WasAnswered,
        };
    }

    /// <summary>Swift → C# call verbs (`answer`, `hangup`, `toggleMute`, …) address the session by id.</summary>
    public sealed class CallSessionParams
    {
        public string SessionId { get; set; } = "";
        public string? Digit { get; set; }
    }

    public sealed class PlaceCallParams
    {
        public string Number { get; set; } = "";
        /// <summary>"main" | "secondary" | null (ask / auto).</summary>
        public string? Slot { get; set; }
        public long? LeadId { get; set; }
        public long? BrowserLeadId { get; set; }
    }

    // ───────────────────────── modal prompts (C# → Swift requests) ─────────────────────────

    public sealed class ConnectionSelectionDto
    {
        public bool HasMain { get; init; }
        public bool IsMainWebRtc { get; init; }
        public string? MainStatus { get; init; }
        public string? MainName { get; init; }
        public bool HasSecondary { get; init; }
        public bool IsSecondaryWebRtc { get; init; }
        public string? SecondaryStatus { get; init; }
        public string? SecondaryName { get; init; }
        public IReadOnlyList<CallerIdDto> MainCallerIds { get; init; } = Array.Empty<CallerIdDto>();
        public string? SelectedCallerId { get; init; }

        public static ConnectionSelectionDto From(ConnectionSelectionRequest r)
        {
            var ids = new List<CallerIdDto>();
            foreach (var c in r.MainCallerIds) ids.Add(CallerIdDto.From(c));
            return new ConnectionSelectionDto
            {
                HasMain = r.HasMain, IsMainWebRtc = r.IsMainWebRtc, MainStatus = r.MainStatus, MainName = r.MainName,
                HasSecondary = r.HasSecondary, IsSecondaryWebRtc = r.IsSecondaryWebRtc, SecondaryStatus = r.SecondaryStatus,
                SecondaryName = r.SecondaryName, MainCallerIds = ids, SelectedCallerId = r.SelectedCallerId,
            };
        }
    }

    public sealed class ConnectionSelectionResultDto
    {
        /// <summary>"main" | "secondary" | null (cancelled).</summary>
        public string? Slot { get; set; }
        public string? CallerId { get; set; }
    }

    /// <summary>Gateway-mode lead prompt ("attach to contact" / "use lead ID" / "skip").</summary>
    public sealed class LeadSelectionResultDto
    {
        public bool Proceed { get; set; }
        public bool Cancelled { get; set; }
        public long? LeadId { get; set; }
    }

    /// <summary>Local-mode lead picker request: the leads AmoCrmService found for the contact.</summary>
    public sealed class KommoLeadPickerDto
    {
        public string? Subdomain { get; init; }
        public string? PhoneNumber { get; init; }
        public bool IsIncoming { get; init; }
        public int DurationSeconds { get; init; }
        public bool WasAnswered { get; init; }
        public DateTime? CallTime { get; init; }
        public IReadOnlyList<KommoLeadDto> Leads { get; init; } = Array.Empty<KommoLeadDto>();
    }

    public sealed class KommoLeadDto
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public long? ResponsibleUserId { get; init; }
    }

    public sealed class KommoLeadPickerResultDto
    {
        /// <summary>null / 0 → skipped.</summary>
        public long? LeadId { get; set; }
    }

    public sealed class MessageDto
    {
        public string Title { get; init; } = "";
        public string Text { get; init; } = "";
    }

    public sealed class UpdateAvailableDto
    {
        public string Version { get; init; } = "";
        public string CurrentVersion { get; init; } = "";
        public string Url { get; init; } = "";
        public string? Sha256 { get; init; }
        public string? Notes { get; init; }
    }

    // ───────────────────────── call details (Swift → C# `getCallDetails`) ─────────────────────────

    public sealed class HistoryKeyParams
    {
        public string PhoneNumber { get; set; } = "";
        public DateTime CallTime { get; set; }
    }

    public sealed class CallDetailsDto
    {
        public string PhoneNumber { get; init; } = "";
        public DateTime CallTime { get; init; }
        public DateTime? RingbackStart { get; init; }
        public DateTime? RingbackEnd { get; init; }
        public DateTime? AnswerTime { get; init; }
        public bool WasAnswered { get; init; }
        public string EndedBy { get; init; } = "";
        public TimeSpan? Duration { get; init; }
        public string DurationText { get; init; } = "";
        public string? RecordingFilePath { get; init; }
        public bool HasRecording { get; init; }
        public string TransportLabel { get; init; } = "";
        public string? OutboundCallerId { get; init; }
        public string? ConnectionName { get; init; }
        public bool IsIncoming { get; init; }
        public string Status { get; init; } = "";
        public IReadOnlyList<string> TechnicalDetails { get; init; } = Array.Empty<string>();
        public bool KommoEnabled { get; init; }
        public string KommoUploadStatus { get; init; } = "";
        public string? KommoUploadReason { get; init; }
        public long? KommoLeadId { get; init; }
        public string? KommoSubdomain { get; init; }
        public bool CanRetryKommo { get; init; }
    }

    /// <summary>Recording prepared for playback in Swift (WAV/MP3 path; non-native formats transcoded via ffmpeg).</summary>
    public sealed class RecordingPlaybackDto
    {
        public string? FilePath { get; init; }
        public string? Error { get; init; }
    }
}
