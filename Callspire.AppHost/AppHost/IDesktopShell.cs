using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Softphone.AppHost
{
    /// <summary>Which telephony line a connection or call belongs to.</summary>
    public enum ConnectionSlot
    {
        Main,
        Secondary
    }

    /// <summary>
    /// Request to open the active-call UI. Exactly one of <see cref="Sip"/> / <see cref="WebRtc"/> is set.
    /// </summary>
    public sealed class CallWindowRequest
    {
        public string PhoneNumber { get; init; } = "";
        public bool IsIncoming { get; init; }
        public ConnectionSlot Slot { get; init; } = ConnectionSlot.Main;
        public CallTransport Transport { get; init; } = CallTransport.Sip;
        public SipService? Sip { get; init; }
        public WebRtcService? WebRtc { get; init; }
        public DateTime CallStartTime { get; init; } = DateTime.Now;
        public long? AmoCrmLeadId { get; init; }
        public long? AmoCrmBrowserLeadId { get; init; }
        public bool IsOriginateCall { get; init; }
        public string? IncomingSessionId { get; init; }
    }

    /// <summary>Data needed by the "which line to call from" picker.</summary>
    public sealed class ConnectionSelectionRequest
    {
        public bool HasMain { get; init; }
        public bool IsMainWebRtc { get; init; }
        public string? MainStatus { get; init; }
        public string? MainName { get; init; }
        public bool HasSecondary { get; init; }
        public bool IsSecondaryWebRtc { get; init; }
        public string? SecondaryStatus { get; init; }
        public string? SecondaryName { get; init; }
        public IReadOnlyList<CallerIdItem> MainCallerIds { get; init; } = Array.Empty<CallerIdItem>();
        public string? SelectedCallerId { get; init; }
    }

    public sealed class ConnectionSelectionResult
    {
        public ConnectionSlot? Slot { get; init; }
        public string? CallerId { get; init; }
    }

    /// <summary>Result of a manual Kommo lead pick (gateway process-call flow).</summary>
    public sealed class LeadSelectionResult
    {
        public bool Proceed { get; init; }
        public bool Cancelled { get; init; }
        public long? LeadId { get; init; }

        public static LeadSelectionResult CancelledResult => new() { Proceed = false, Cancelled = true };
        public static LeadSelectionResult Contact => new() { Proceed = true, LeadId = null };
    }

    /// <summary>
    /// UI callbacks the <see cref="DesktopAppController"/> needs from the hosting shell
    /// (Avalonia today, WPF later). Implementations must be safe to call from any thread:
    /// they marshal to the UI thread themselves.
    /// </summary>
    public interface IDesktopShell
    {
        /// <summary>Open (or bring forward) the active-call window.</summary>
        void ShowCallWindow(CallWindowRequest request);

        /// <summary>True while an active/ringing call window is open.</summary>
        bool HasActiveCallWindow { get; }

        /// <summary>True if the active call window (if any) is a WebRTC call.</summary>
        bool IsWebRtcCallActive { get; }

        void ShowSettings();

        Task<ConnectionSelectionResult> ShowConnectionSelectionAsync(ConnectionSelectionRequest request);

        Task<LeadSelectionResult> ShowLeadSelectionAsync(string phoneNumber);

        void ShowMessage(string title, string text);

        /// <summary>Non-blocking "new version available" notification (startup update check).</summary>
        void ShowUpdateAvailable(UpdateInfo info, string currentVersion);

        /// <summary>Bring the main window to the foreground (click-to-call, incoming call).</summary>
        void BringToForeground();
    }
}
