using System;
using System.Text.RegularExpressions;

namespace Softphone
{
    /// <summary>
    /// Platform-agnostic logging facade (extracted from MainWindow.Log so business
    /// services can live in Callspire.Core without referencing the UI).
    /// UI heads attach themselves via <see cref="UiSink"/> (e.g. MainWindow's log view).
    /// </summary>
    public static class AppLog
    {
        /// <summary>Optional UI sink; set by the platform head (e.g. WPF/Avalonia main window).</summary>
        public static Action<string>? UiSink;

        public static void Log(string message)
        {
            message = Sanitize(message);

            // Always write to file (best-effort). This is crucial when UI is frozen and user can't open LogWindow.
            try
            {
                FileLogService.Instance.Enqueue(message);
            }
            catch
            {
                // never throw from logging
            }

            try
            {
                UiSink?.Invoke(message);
            }
            catch
            {
                // never throw from logging
            }

            System.Diagnostics.Debug.WriteLine(message);
        }

        public static string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            string sanitized = message;

            // Replace explicit token patterns
            sanitized = Regex.Replace(sanitized, @"\bghp_[A-Za-z0-9]{8,}\b", "ghp_***REDACTED***");
            sanitized = Regex.Replace(sanitized, @"\bgithub_pat_[A-Za-z0-9_]{8,}\b", "github_pat_***REDACTED***");

            // Replace password/pass JSON field values
            sanitized = Regex.Replace(sanitized, "(?i)(\"pass\"\\s*:\\s*\")([^\"]+)(\")", "$1***REDACTED***$3");
            sanitized = Regex.Replace(sanitized, "(?i)(\"password\"\\s*:\\s*\")([^\"]+)(\")", "$1***REDACTED***$3");

            // Replace Authorization header values
            sanitized = Regex.Replace(sanitized, @"(?i)\bAuthorization\s*:\s*Bearer\s+[A-Za-z0-9\-\._~\+\/]+=*\b", "Authorization: Bearer ***REDACTED***");
            sanitized = Regex.Replace(sanitized, @"(?i)\bAuthorization\s*:\s*token\s+[A-Za-z0-9\-_]{8,}\b", "Authorization: token ***REDACTED***");

            // Replace encrypted SIP password blobs if they appear
            sanitized = Regex.Replace(sanitized, "(?i)(SipPasswordEncrypted=)([^\\s]+)", "$1***REDACTED***");

            return sanitized;
        }
    }
}
