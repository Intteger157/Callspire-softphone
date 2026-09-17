using System;

namespace Softphone.AppHost
{
    /// <summary>
    /// Maps technical SIP/WebRTC status strings coming from <see cref="Callspire.Core"/> services
    /// to short, user-facing connection status text. Returns <c>null</c> when the message is
    /// call-related noise that must not overwrite the connection status label.
    /// Ported from WPF <c>MainWindow.FormatStatusMessage</c>.
    /// </summary>
    public static class ConnectionStatusFormatter
    {
        public const string NotConnected = "Not connected";
        public const string Connecting = "Connecting to server...";
        public const string ConnectedSip = "Connected with SIP";
        public const string ConnectedWebRtc = "Connected with WebRTC";

        public static string? Format(string? technicalStatus, bool isWebRtcMode)
        {
            if (string.IsNullOrEmpty(technicalStatus))
                return NotConnected;

            var status = technicalStatus.ToLowerInvariant();

            if (status.Contains("registration successful"))
                return null; // handled by explicit "registered" transitions

            if (status.Contains("registration failed") || status.Contains("registration temporary failure"))
            {
                if (status.Contains("could not resolve")) return "Connection failed: Cannot reach server";
                if (status.Contains("timeout")) return "Connection failed: Timeout";
                if (status.Contains("unauthorized") || status.Contains("401") || status.Contains("403"))
                    return "Connection failed: Invalid credentials";
                return "Connection failed";
            }

            if (status.Contains("registration removed"))
                return "Disconnected from server";

            if (status.Contains("registering on sip server") || status.Contains("initializing sip"))
                return isWebRtcMode ? null : Connecting;

            if (status.Contains("sip transport listening"))
                return "Starting connection...";

            // Everything else is call progress / trace noise → keep current label.
            return null;
        }

        public static bool IsRegistrationFailure(string? technicalStatus)
        {
            if (string.IsNullOrEmpty(technicalStatus)) return false;
            var s = technicalStatus.ToLowerInvariant();
            return s.Contains("registration failed") || s.Contains("registration temporary failure");
        }

        public static bool IsRegistrationSuccess(string? technicalStatus)
        {
            if (string.IsNullOrEmpty(technicalStatus)) return false;
            return technicalStatus.Contains("registration successful", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsRegistrationRemoved(string? technicalStatus)
        {
            if (string.IsNullOrEmpty(technicalStatus)) return false;
            return technicalStatus.Contains("registration removed", StringComparison.OrdinalIgnoreCase);
        }
    }
}
