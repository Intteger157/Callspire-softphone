using System;
using System.Windows.Media;

namespace Softphone
{
    /// <summary>
    /// User-facing strings and colors for <see cref="CallHistoryItem"/> in list and Call Details (English UI).
    /// Combines coarse <see cref="CallStatus"/> with WasAnswered, direction, EndedBy, and ErrorMessage where useful.
    /// </summary>
    public static class CallStatusPresentation
    {
        public static string FormatSummary(CallHistoryItem c)
        {
            switch (c.Status)
            {
                case CallStatus.Calling:
                    return "Connecting…";
                case CallStatus.Connected:
                    return "In call…";
                case CallStatus.Failed:
                    if (!string.IsNullOrWhiteSpace(c.ErrorMessage))
                    {
                        var m = c.ErrorMessage.Trim().Replace(Environment.NewLine, " ");
                        if (m.Length > 90)
                            m = m.Substring(0, 87) + "…";
                        return $"Failed — {m}";
                    }
                    return "Failed";

                case CallStatus.Cancelled:
                    return c.IsIncoming
                        ? "Declined / cancelled"
                        : "Hung up before answer";

                case CallStatus.Missed:
                    return "Missed call";

                case CallStatus.Ended:
                    if (c.WasAnswered)
                    {
                        return c.EndedBy switch
                        {
                            CallEndedBy.LocalUser => "Completed · You hung up",
                            CallEndedBy.RemoteParty => "Completed · Remote hung up",
                            _ => "Completed"
                        };
                    }
                    return c.IsIncoming
                        ? "Not answered"
                        : "No answer · Hung up";

                default:
                    return c.Status.ToString();
            }
        }

        /// <summary>Brush for status text in history row and Call Details header.</summary>
        public static Brush GetForegroundBrush(CallHistoryItem c)
        {
            switch (c.Status)
            {
                case CallStatus.Connected:
                    return new SolidColorBrush(Color.FromRgb(34, 197, 94));
                case CallStatus.Calling:
                    return new SolidColorBrush(Color.FromRgb(59, 130, 246));
                case CallStatus.Ended:
                    return c.WasAnswered
                        ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                        : new SolidColorBrush(Color.FromRgb(234, 179, 8));
                case CallStatus.Failed:
                case CallStatus.Cancelled:
                case CallStatus.Missed:
                    return new SolidColorBrush(Color.FromRgb(239, 68, 68));
                default:
                    return new SolidColorBrush(Color.FromRgb(156, 163, 175));
            }
        }
    }
}
