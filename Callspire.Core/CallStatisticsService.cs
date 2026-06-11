using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Softphone
{
    public enum CallStatisticsPeriod
    {
        Last7Days,
        Today,
        Yesterday,
        Last3Days,
        Custom
    }

    public enum CallStatisticsScope
    {
        All,
        Main,
        Secondary
    }

    public enum CallStatisticsDirection
    {
        All,
        Incoming,
        Outgoing
    }

    public enum CallStatisticsDrillDown
    {
        All,
        Answered,
        Unanswered,
        Missed,
        Failed,
        Cancelled,
        Incoming,
        Outgoing,
        CrmNotLinked,
        CrmLinkedToLead,
        CrmSentToContact,
        CrmWithRecording,
        CrmNoRecording,
        CrmUploadedToAmo,
        CrmRecordingNotInAmo,
        CrmUploadFailed,
        CrmUploadCancelled,
        CrmUploadProblems
    }

    public sealed class CallStatisticsFilter
    {
        public CallStatisticsPeriod Period { get; init; } = CallStatisticsPeriod.Last7Days;
        public CallStatisticsScope Scope { get; init; } = CallStatisticsScope.All;
        public CallStatisticsDirection Direction { get; init; } = CallStatisticsDirection.All;
        public DateTime? CustomFrom { get; init; }
        public DateTime? CustomTo { get; init; }
    }

    public sealed class CallStatisticsSummary
    {
        public int TotalCompleted { get; init; }
        public int Successful { get; init; }
        public int Unsuccessful { get; init; }
        public int Incoming { get; init; }
        public int Outgoing { get; init; }
        public int Missed { get; init; }
        public int Failed { get; init; }
        public int Cancelled { get; init; }
        public int AgentCancelledBeforeAnswer { get; init; }
        public TimeSpan TotalTalkTime { get; init; }
        public TimeSpan TotalRingTime { get; init; }
        public int RingTimeSampleCount { get; init; }

        public double SuccessRatePercent =>
            TotalCompleted > 0 ? Math.Round(100.0 * Successful / TotalCompleted, 1) : 0;

        public TimeSpan AverageTalkTime =>
            Successful > 0 ? TimeSpan.FromTicks(TotalTalkTime.Ticks / Successful) : TimeSpan.Zero;

        public TimeSpan AverageRingTime =>
            RingTimeSampleCount > 0 ? TimeSpan.FromTicks(TotalRingTime.Ticks / RingTimeSampleCount) : TimeSpan.Zero;

        public double ContactRatePercent =>
            Outgoing > 0 ? Math.Round(100.0 * Successful / Outgoing, 1) : 0;
    }

    public sealed class CallDayBucket
    {
        public DateTime Date { get; init; }
        public string Label { get; init; } = "";
        public int Total { get; init; }
        public int Answered { get; init; }
        public double BarHeight { get; init; }
        public string ToolTipText => $"{Total} calls ({Answered} answered)";
    }

    public sealed class CallHourBucket
    {
        public int Hour { get; init; }
        public string Label { get; init; } = "";
        public int Total { get; init; }
        public double BarHeight { get; init; }
    }

    public sealed class CallTopNumberEntry
    {
        public string PhoneNumber { get; init; } = "";
        public int TotalCalls { get; init; }
        public int Answered { get; init; }
        public TimeSpan TotalTalkTime { get; init; }
    }

    public sealed class CallTransportStats
    {
        public CallTransport Transport { get; init; }
        public string Label { get; init; } = "";
        public CallStatisticsSummary Summary { get; init; } = new();
    }

    public sealed class CallConnectionCompareRow
    {
        public string Name { get; init; } = "";
        public CallStatisticsSummary Summary { get; init; } = new();
    }

    public sealed class CallCallerIdStats
    {
        public string CallerId { get; init; } = "";
        public int Total { get; init; }
        public int Answered { get; init; }
        public double SuccessRatePercent { get; init; }
    }

    public sealed class CallCrmStatistics
    {
        public int TotalCompleted { get; init; }
        public int LinkedToLead { get; init; }
        public int SentToContact { get; init; }
        public int WithRecording { get; init; }
        public int UploadedToAmo { get; init; }
        public int RecordingNotInAmo { get; init; }
        public int UploadFailed { get; init; }
        public int UploadCancelled { get; init; }
    }

    public sealed class CallStatisticsReport
    {
        public DateTime FromInclusive { get; init; }
        public DateTime ToInclusive { get; init; }
        public CallStatisticsFilter Filter { get; init; } = new();
        public CallStatisticsSummary Summary { get; init; } = new();
        public IReadOnlyList<CallDayBucket> DailyBuckets { get; init; } = Array.Empty<CallDayBucket>();
        public IReadOnlyList<CallHourBucket> HourlyBuckets { get; init; } = Array.Empty<CallHourBucket>();
        public IReadOnlyList<CallTopNumberEntry> TopNumbers { get; init; } = Array.Empty<CallTopNumberEntry>();
        public IReadOnlyList<CallTransportStats> TransportStats { get; init; } = Array.Empty<CallTransportStats>();
        public IReadOnlyList<CallConnectionCompareRow> ConnectionCompare { get; init; } = Array.Empty<CallConnectionCompareRow>();
        public IReadOnlyList<CallCallerIdStats> CallerIdStats { get; init; } = Array.Empty<CallCallerIdStats>();
        public CallCrmStatistics Crm { get; init; } = new();
        public int PeakHour { get; init; } = -1;
        public bool HasLegacyUnknownConnection { get; init; }
        public IReadOnlyList<CallHistoryItem> MatchingCalls { get; init; } = Array.Empty<CallHistoryItem>();
    }

    public static class CallStatisticsService
    {
        public const int RetentionDays = 7;
        private const int TopNumbersLimit = 10;

        public static (DateTime FromInclusive, DateTime ToExclusive) ResolvePeriodRange(
            CallStatisticsPeriod period,
            DateTime? customFrom = null,
            DateTime? customTo = null,
            DateTime? now = null)
        {
            var anchor = now ?? DateTime.Now;
            var todayStart = anchor.Date;
            var tomorrowStart = todayStart.AddDays(1);
            var retentionStart = todayStart.AddDays(-(RetentionDays - 1));

            switch (period)
            {
                case CallStatisticsPeriod.Today:
                    return (todayStart, tomorrowStart);

                case CallStatisticsPeriod.Yesterday:
                    return (todayStart.AddDays(-1), todayStart);

                case CallStatisticsPeriod.Last3Days:
                    return (todayStart.AddDays(-2), tomorrowStart);

                case CallStatisticsPeriod.Custom:
                {
                    var from = (customFrom ?? retentionStart).Date;
                    var toExclusive = (customTo ?? anchor).Date.AddDays(1);
                    if (from < retentionStart) from = retentionStart;
                    if (toExclusive > tomorrowStart) toExclusive = tomorrowStart;
                    if (toExclusive <= from) toExclusive = from.AddDays(1);
                    return (from, toExclusive);
                }

                case CallStatisticsPeriod.Last7Days:
                default:
                    return (retentionStart, tomorrowStart);
            }
        }

        public static CallStatisticsReport BuildReport(
            IEnumerable<CallHistoryItem> history,
            CallStatisticsFilter filter,
            string mainConnectionName = "Main",
            string secondaryConnectionName = "Secondary",
            bool secondaryEnabled = false)
        {
            var (fromInclusive, toExclusive) = ResolvePeriodRange(filter.Period, filter.CustomFrom, filter.CustomTo);
            var inPeriod = FilterByPeriod(history, fromInclusive, toExclusive).ToList();
            var matching = ApplyFilter(inPeriod, filter).ToList();

            var summary = ComputeSummary(matching);
            var daily = BuildDailyBuckets(matching, fromInclusive, toExclusive);
            var hourly = BuildHourlyBuckets(matching);
            int peakHour = hourly.Count > 0 ? hourly.OrderByDescending(h => h.Total).First().Hour : -1;

            var transportStats = new List<CallTransportStats>
            {
                BuildTransportStats(matching, CallTransport.Sip, "SIP"),
                BuildTransportStats(matching, CallTransport.WebRtc, "WebRTC")
            }.Where(t => t.Summary.TotalCompleted > 0).ToList();

            var connectionCompare = new List<CallConnectionCompareRow>();
            if (filter.Scope == CallStatisticsScope.All && secondaryEnabled)
            {
                connectionCompare.Add(new CallConnectionCompareRow
                {
                    Name = mainConnectionName,
                    Summary = ComputeSummary(ApplyScope(inPeriod, CallStatisticsScope.Main))
                });
                connectionCompare.Add(new CallConnectionCompareRow
                {
                    Name = secondaryConnectionName,
                    Summary = ComputeSummary(ApplyScope(inPeriod, CallStatisticsScope.Secondary))
                });
            }

            return new CallStatisticsReport
            {
                FromInclusive = fromInclusive,
                ToInclusive = toExclusive.AddDays(-1),
                Filter = filter,
                Summary = summary,
                DailyBuckets = daily,
                HourlyBuckets = hourly,
                TopNumbers = BuildTopNumbers(matching),
                TransportStats = transportStats,
                ConnectionCompare = connectionCompare,
                CallerIdStats = BuildCallerIdStats(matching),
                Crm = BuildCrmStats(matching),
                PeakHour = peakHour,
                HasLegacyUnknownConnection = inPeriod.Any(c => c.ConnectionSlot == CallConnectionSlot.Unknown),
                MatchingCalls = matching
            };
        }

        public static IEnumerable<CallHistoryItem> ApplyDrillDown(
            IEnumerable<CallHistoryItem> calls,
            CallStatisticsDrillDown drillDown,
            string? phoneNumber = null)
        {
            IEnumerable<CallHistoryItem> q = calls;

            if (!string.IsNullOrWhiteSpace(phoneNumber))
                q = q.Where(c => c.PhoneNumber == phoneNumber);

            return drillDown switch
            {
                CallStatisticsDrillDown.Answered => q.Where(IsSuccessful),
                CallStatisticsDrillDown.Unanswered => q.Where(c => IsUnsuccessful(c)),
                CallStatisticsDrillDown.Missed => q.Where(c => c.IsIncoming && c.Status == CallStatus.Missed),
                CallStatisticsDrillDown.Failed => q.Where(c => c.Status == CallStatus.Failed),
                CallStatisticsDrillDown.Cancelled => q.Where(c => c.Status == CallStatus.Cancelled),
                CallStatisticsDrillDown.Incoming => q.Where(c => c.IsIncoming),
                CallStatisticsDrillDown.Outgoing => q.Where(c => !c.IsIncoming),
                CallStatisticsDrillDown.CrmNotLinked => q.Where(IsNotAttachedToAmoCrm),
                CallStatisticsDrillDown.CrmLinkedToLead => q.Where(c => c.AmoCrmLeadId.HasValue),
                CallStatisticsDrillDown.CrmSentToContact => q.Where(c =>
                    !c.AmoCrmLeadId.HasValue && c.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded),
                CallStatisticsDrillDown.CrmWithRecording => q.Where(HasLocalRecording),
                CallStatisticsDrillDown.CrmNoRecording => q.Where(c => !HasLocalRecording(c)),
                CallStatisticsDrillDown.CrmUploadedToAmo => q.Where(HasRecordingUploadedToAmo),
                CallStatisticsDrillDown.CrmRecordingNotInAmo => q.Where(HasRecordingNotInAmo),
                CallStatisticsDrillDown.CrmUploadFailed => q.Where(c => c.AmoCrmUploadStatus == AmoCrmUploadStatus.Failed),
                CallStatisticsDrillDown.CrmUploadCancelled => q.Where(c => c.AmoCrmUploadStatus == AmoCrmUploadStatus.Cancelled),
                CallStatisticsDrillDown.CrmUploadProblems => q.Where(HasCrmUploadProblem),
                _ => q
            };
        }

        public static bool HasLocalRecording(CallHistoryItem call) =>
            !string.IsNullOrWhiteSpace(call.RecordingFilePath) && File.Exists(call.RecordingFilePath);

        public static bool IsAttachedToAmoCrm(CallHistoryItem call) =>
            call.AmoCrmLeadId.HasValue || call.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded;

        public static bool IsNotAttachedToAmoCrm(CallHistoryItem call) => !IsAttachedToAmoCrm(call);

        public static bool HasRecordingUploadedToAmo(CallHistoryItem call) =>
            HasLocalRecording(call) && call.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded;

        public static bool HasRecordingNotInAmo(CallHistoryItem call) =>
            HasLocalRecording(call) && call.AmoCrmUploadStatus != AmoCrmUploadStatus.Uploaded;

        public static bool HasCrmUploadProblem(CallHistoryItem call) =>
            call.AmoCrmUploadStatus == AmoCrmUploadStatus.Failed
            || call.AmoCrmUploadStatus == AmoCrmUploadStatus.Cancelled
            || (HasLocalRecording(call) && call.AmoCrmUploadStatus == AmoCrmUploadStatus.NotUploaded);

        public static IEnumerable<CallHistoryItem> FilterByPeriod(
            IEnumerable<CallHistoryItem> calls,
            DateTime fromInclusive,
            DateTime toExclusive)
        {
            return calls.Where(c => c.CallTime >= fromInclusive && c.CallTime < toExclusive);
        }

        public static CallStatisticsSummary ComputeSummary(
            IEnumerable<CallHistoryItem> calls,
            CallStatisticsScope scope = CallStatisticsScope.All)
        {
            return ComputeSummaryFromList(FilterCompleted(calls, scope).ToList());
        }

        private static CallStatisticsSummary ComputeSummaryFromList(List<CallHistoryItem> completed)
        {
            int successful = 0, unsuccessful = 0, incoming = 0, outgoing = 0;
            int missed = 0, failed = 0, cancelled = 0, agentCancelled = 0;
            var talkTime = TimeSpan.Zero;
            var ringTime = TimeSpan.Zero;
            int ringSamples = 0;

            foreach (var call in completed)
            {
                if (call.IsIncoming) incoming++; else outgoing++;

                if (IsSuccessful(call))
                {
                    successful++;
                    talkTime += GetEffectiveTalkDuration(call);
                }
                else
                {
                    unsuccessful++;
                    switch (call.Status)
                    {
                        case CallStatus.Missed:
                            if (call.IsIncoming) missed++;
                            break;
                        case CallStatus.Failed: failed++; break;
                        case CallStatus.Cancelled: cancelled++; break;
                    }
                    if (!call.WasAnswered && !call.IsIncoming && call.EndedBy == CallEndedBy.LocalUser)
                        agentCancelled++;
                }

                var ring = GetRingDuration(call);
                if (ring.HasValue)
                {
                    ringTime += ring.Value;
                    ringSamples++;
                }
            }

            return new CallStatisticsSummary
            {
                TotalCompleted = completed.Count,
                Successful = successful,
                Unsuccessful = unsuccessful,
                Incoming = incoming,
                Outgoing = outgoing,
                Missed = missed,
                Failed = failed,
                Cancelled = cancelled,
                AgentCancelledBeforeAnswer = agentCancelled,
                TotalTalkTime = talkTime,
                TotalRingTime = ringTime,
                RingTimeSampleCount = ringSamples
            };
        }

        private static IEnumerable<CallHistoryItem> ApplyFilter(IEnumerable<CallHistoryItem> calls, CallStatisticsFilter filter)
        {
            var q = FilterCompleted(calls, filter.Scope);
            if (filter.Direction == CallStatisticsDirection.Incoming)
                q = q.Where(c => c.IsIncoming);
            else if (filter.Direction == CallStatisticsDirection.Outgoing)
                q = q.Where(c => !c.IsIncoming);
            return q;
        }

        private static IEnumerable<CallHistoryItem> ApplyScope(IEnumerable<CallHistoryItem> calls, CallStatisticsScope scope)
        {
            return FilterCompleted(calls, scope);
        }

        private static IEnumerable<CallHistoryItem> FilterCompleted(IEnumerable<CallHistoryItem> calls, CallStatisticsScope scope)
        {
            foreach (var call in calls)
            {
                if (IsInProgress(call.Status)) continue;
                var slot = GetEffectiveConnectionSlot(call);
                if (scope == CallStatisticsScope.Main && slot != CallConnectionSlot.Main) continue;
                if (scope == CallStatisticsScope.Secondary && slot != CallConnectionSlot.Secondary) continue;
                yield return call;
            }
        }

        public static CallConnectionSlot GetEffectiveConnectionSlot(CallHistoryItem call) =>
            call.ConnectionSlot == CallConnectionSlot.Unknown ? CallConnectionSlot.Main : call.ConnectionSlot;

        public static string FormatConnectionLabel(
            CallHistoryItem call,
            string mainConnectionName = "Main",
            string secondaryConnectionName = "Secondary")
        {
            return GetEffectiveConnectionSlot(call) switch
            {
                CallConnectionSlot.Secondary => secondaryConnectionName,
                _ => mainConnectionName
            };
        }

        private static List<CallDayBucket> BuildDailyBuckets(
            List<CallHistoryItem> calls,
            DateTime fromInclusive,
            DateTime toExclusive)
        {
            var days = new List<CallDayBucket>();
            int maxTotal = 0;

            for (var d = fromInclusive.Date; d < toExclusive.Date; d = d.AddDays(1))
            {
                var dayCalls = calls.Where(c => c.CallTime.Date == d).ToList();
                int total = dayCalls.Count;
                int answered = dayCalls.Count(IsSuccessful);
                if (total > maxTotal) maxTotal = total;

                days.Add(new CallDayBucket
                {
                    Date = d,
                    Label = d.ToString("ddd\ndd.MM", CultureInfo.CurrentCulture),
                    Total = total,
                    Answered = answered
                });
            }

            const double maxBar = 100;
            return days.Select(b => new CallDayBucket
            {
                Date = b.Date,
                Label = b.Label,
                Total = b.Total,
                Answered = b.Answered,
                BarHeight = maxTotal > 0 ? Math.Max(4, b.Total * maxBar / maxTotal) : 4
            }).ToList();
        }

        private static List<CallHourBucket> BuildHourlyBuckets(List<CallHistoryItem> calls)
        {
            var groups = calls.GroupBy(c => c.CallTime.Hour).ToDictionary(g => g.Key, g => g.Count());
            int max = groups.Values.DefaultIfEmpty(0).Max();
            const double maxBar = 80;

            return Enumerable.Range(0, 24).Select(h =>
            {
                int total = groups.TryGetValue(h, out int c) ? c : 0;
                return new CallHourBucket
                {
                    Hour = h,
                    Label = h.ToString("00", CultureInfo.InvariantCulture),
                    Total = total,
                    BarHeight = max > 0 ? Math.Max(2, total * maxBar / max) : 2
                };
            }).ToList();
        }

        private static List<CallTopNumberEntry> BuildTopNumbers(List<CallHistoryItem> calls)
        {
            return calls
                .GroupBy(c => c.PhoneNumber)
                .Select(g => new CallTopNumberEntry
                {
                    PhoneNumber = g.Key,
                    TotalCalls = g.Count(),
                    Answered = g.Count(IsSuccessful),
                    TotalTalkTime = TimeSpan.FromTicks(g.Where(IsSuccessful)
                        .Sum(c => GetEffectiveTalkDuration(c).Ticks))
                })
                .OrderByDescending(x => x.TotalCalls)
                .ThenByDescending(x => x.Answered)
                .Take(TopNumbersLimit)
                .ToList();
        }

        private static CallTransportStats BuildTransportStats(List<CallHistoryItem> calls, CallTransport transport, string label)
        {
            var subset = calls.Where(c => c.Transport == transport).ToList();
            return new CallTransportStats
            {
                Transport = transport,
                Label = label,
                Summary = ComputeSummaryFromList(subset)
            };
        }

        private static List<CallCallerIdStats> BuildCallerIdStats(List<CallHistoryItem> calls)
        {
            return calls
                .Where(c => !c.IsIncoming && !string.IsNullOrWhiteSpace(c.OutboundCallerId))
                .GroupBy(c => c.OutboundCallerId!.Trim())
                .Select(g =>
                {
                    int total = g.Count();
                    int answered = g.Count(IsSuccessful);
                    return new CallCallerIdStats
                    {
                        CallerId = g.Key,
                        Total = total,
                        Answered = answered,
                        SuccessRatePercent = total > 0 ? Math.Round(100.0 * answered / total, 1) : 0
                    };
                })
                .OrderByDescending(x => x.Total)
                .Take(8)
                .ToList();
        }

        private static CallCrmStatistics BuildCrmStats(List<CallHistoryItem> calls)
        {
            int withRecording = calls.Count(HasLocalRecording);
            int uploadedWithRecording = calls.Count(HasRecordingUploadedToAmo);
            return new CallCrmStatistics
            {
                TotalCompleted = calls.Count,
                LinkedToLead = calls.Count(c => c.AmoCrmLeadId.HasValue),
                SentToContact = calls.Count(c => !c.AmoCrmLeadId.HasValue && c.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded),
                WithRecording = withRecording,
                UploadedToAmo = uploadedWithRecording,
                RecordingNotInAmo = calls.Count(HasRecordingNotInAmo),
                UploadFailed = calls.Count(c => c.AmoCrmUploadStatus == AmoCrmUploadStatus.Failed),
                UploadCancelled = calls.Count(c => c.AmoCrmUploadStatus == AmoCrmUploadStatus.Cancelled)
            };
        }

        public static TimeSpan GetEffectiveTalkDuration(CallHistoryItem call)
        {
            if (call.Duration.HasValue && call.Duration.Value.TotalSeconds > 0)
                return call.Duration.Value;

            if (call.WasAnswered || call.AnswerTime.HasValue)
            {
                var fromRecording = TryGetRecordingDuration(call.RecordingFilePath);
                if (fromRecording.HasValue && fromRecording.Value.TotalSeconds > 0)
                    return fromRecording.Value;
            }

            return TimeSpan.Zero;
        }

        /// <summary>
        /// Set by the platform head to read media file durations
        /// (e.g. NAudio's AudioFileReader on Windows desktop).
        /// </summary>
        public static Func<string, TimeSpan?>? RecordingDurationResolver { get; set; }

        private static TimeSpan? TryGetRecordingDuration(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            try
            {
                return RecordingDurationResolver?.Invoke(path);
            }
            catch
            {
                return null;
            }
        }

        public static TimeSpan? GetRingDuration(CallHistoryItem call)
        {
            if (call.IsIncoming) return null;
            if (call.RingbackStartTime.HasValue && call.RingbackEndTime.HasValue)
                return call.RingbackEndTime.Value - call.RingbackStartTime.Value;
            if (call.AnswerTime.HasValue)
            {
                var span = call.AnswerTime.Value - call.CallTime;
                return span.TotalSeconds >= 0 ? span : null;
            }
            return null;
        }

        public static bool IsInProgress(CallStatus status) =>
            status == CallStatus.Calling || status == CallStatus.Connected;

        public static bool IsSuccessful(CallHistoryItem call) =>
            call.WasAnswered && call.Status == CallStatus.Ended;

        public static bool IsUnsuccessful(CallHistoryItem call) =>
            !IsSuccessful(call) && !IsInProgress(call.Status);

        public static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
                return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
            return $"{duration.Minutes:D2}:{duration.Seconds:D2}";
        }

        public static string ExportToCsv(
            CallStatisticsReport report,
            string mainConnectionName = "Main",
            string secondaryConnectionName = "Secondary")
        {
            var sb = new StringBuilder();
            sb.AppendLine("PhoneNumber,CallTime,Direction,Status,WasAnswered,DurationSec,Connection,Transport,OutboundCallerId,AmoCrmLeadId,Recording,AmoCrmUpload");
            foreach (var c in report.MatchingCalls.OrderByDescending(x => x.CallTime))
            {
                sb.Append(EscapeCsv(c.PhoneNumber)).Append(',');
                sb.Append(c.CallTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(c.IsIncoming ? "Incoming" : "Outgoing").Append(',');
                sb.Append(c.Status).Append(',');
                sb.Append(c.WasAnswered ? "Yes" : "No").Append(',');
                sb.Append(c.Duration.HasValue ? ((int)c.Duration.Value.TotalSeconds).ToString(CultureInfo.InvariantCulture) : "").Append(',');
                sb.Append(EscapeCsv(FormatConnectionLabel(c, mainConnectionName, secondaryConnectionName))).Append(',');
                sb.Append(c.Transport).Append(',');
                sb.Append(EscapeCsv(c.OutboundCallerId ?? "")).Append(',');
                sb.Append(c.AmoCrmLeadId?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',');
                sb.Append(!string.IsNullOrWhiteSpace(c.RecordingFilePath) ? "Yes" : "No").Append(',');
                sb.AppendLine(c.AmoCrmUploadStatus.ToString());
            }
            return sb.ToString();
        }

        public static void SaveReportCsv(
            CallStatisticsReport report,
            string filePath,
            string mainConnectionName = "Main",
            string secondaryConnectionName = "Secondary")
        {
            File.WriteAllText(filePath, ExportToCsv(report, mainConnectionName, secondaryConnectionName), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains('"') || value.Contains(',') || value.Contains('\n'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
