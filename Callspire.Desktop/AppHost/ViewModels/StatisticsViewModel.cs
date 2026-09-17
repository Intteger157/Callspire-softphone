using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Softphone.AppHost.ViewModels
{
    public sealed class StatsRow
    {
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public string PhoneNumber { get; init; } = "";
    }

    /// <summary>
    /// Bindable projection of <see cref="CallStatisticsReport"/> + filter state for the Statistics view.
    /// </summary>
    public sealed class StatisticsViewModel : ObservableObject
    {
        public static readonly IReadOnlyList<string> PeriodOptions = new[] { "Last 7 days", "Today", "Yesterday", "Last 3 days", "Custom range" };
        public static readonly IReadOnlyList<string> DirectionOptions = new[] { "All", "Incoming", "Outgoing" };

        private int _periodIndex;
        private int _connectionIndex;
        private int _directionIndex;
        private DateTime? _customFrom = DateTime.Today.AddDays(-6);
        private DateTime? _customTo = DateTime.Today;
        private string _periodHint = "";
        private bool _hasLegacyUnknown;
        private bool _secondaryEnabled;
        private CallStatisticsReport? _report;

        public ObservableCollection<string> ConnectionOptions { get; } = new() { "All connections", "Main", "Secondary" };
        public ObservableCollection<CallDayBucket> Daily { get; } = new();
        public ObservableCollection<CallHourBucket> Hourly { get; } = new();
        public ObservableCollection<StatsRow> ConnectionCompare { get; } = new();
        public ObservableCollection<StatsRow> CallerIds { get; } = new();
        public ObservableCollection<StatsRow> TopNumbers { get; } = new();

        public int PeriodIndex { get => _periodIndex; set { if (Set(ref _periodIndex, value)) OnPropertyChanged(nameof(IsCustomPeriod)); } }
        public int ConnectionIndex { get => _connectionIndex; set => Set(ref _connectionIndex, value); }
        public int DirectionIndex { get => _directionIndex; set => Set(ref _directionIndex, value); }
        public DateTime? CustomFrom { get => _customFrom; set => Set(ref _customFrom, value); }
        public DateTime? CustomTo { get => _customTo; set => Set(ref _customTo, value); }
        public bool IsCustomPeriod => Period == CallStatisticsPeriod.Custom;
        public bool SecondaryEnabled { get => _secondaryEnabled; set => Set(ref _secondaryEnabled, value); }
        public string PeriodHint { get => _periodHint; private set => Set(ref _periodHint, value); }
        public bool HasLegacyUnknownConnection { get => _hasLegacyUnknown; private set => Set(ref _hasLegacyUnknown, value); }
        public CallStatisticsReport? Report { get => _report; private set => Set(ref _report, value); }

        public CallStatisticsPeriod Period => _periodIndex switch
        {
            1 => CallStatisticsPeriod.Today,
            2 => CallStatisticsPeriod.Yesterday,
            3 => CallStatisticsPeriod.Last3Days,
            4 => CallStatisticsPeriod.Custom,
            _ => CallStatisticsPeriod.Last7Days
        };

        public CallStatisticsFilter BuildFilter() => new()
        {
            Period = Period,
            Scope = _connectionIndex switch { 1 => CallStatisticsScope.Main, 2 => CallStatisticsScope.Secondary, _ => CallStatisticsScope.All },
            Direction = _directionIndex switch { 1 => CallStatisticsDirection.Incoming, 2 => CallStatisticsDirection.Outgoing, _ => CallStatisticsDirection.All },
            CustomFrom = Period == CallStatisticsPeriod.Custom ? _customFrom : null,
            CustomTo = Period == CallStatisticsPeriod.Custom ? _customTo : null,
        };

        // KPI strings
        public string KpiTotal => (Report?.Summary.TotalCompleted ?? 0).ToString();
        public string KpiDirectionHint => Report == null ? "" : $"{Report.Summary.Incoming} in · {Report.Summary.Outgoing} out";
        public string KpiAnswered => (Report?.Summary.Successful ?? 0).ToString();
        public string KpiSuccessRate => Report == null ? "" : $"{Report.Summary.SuccessRatePercent:0.#}% success";
        public string KpiUnanswered => (Report?.Summary.Unsuccessful ?? 0).ToString();
        public string KpiUnansweredHint => Report == null ? "" : $"{Report.Summary.Cancelled} cancelled · {Report.Summary.Failed} failed";
        public string KpiTalkTime => Report == null ? "0:00" : CallStatisticsService.FormatDuration(Report.Summary.TotalTalkTime);
        public string KpiAvgTalk => Report == null ? "" : $"avg {CallStatisticsService.FormatDuration(Report.Summary.AverageTalkTime)}";
        public string KpiContactRate => Report == null ? "0%" : $"{Report.Summary.ContactRatePercent:0.#}%";
        public string KpiRingTime => Report == null ? "" : $"avg ring {CallStatisticsService.FormatDuration(Report.Summary.AverageRingTime)}";
        public string KpiMissed => (Report?.Summary.Missed ?? 0).ToString();
        public string KpiFailedCancelled => Report == null ? "" : $"{Report.Summary.AgentCancelledBeforeAnswer} hung up before answer";
        public string PeakHourText => Report == null || Report.PeakHour < 0 ? "No activity" : $"Peak hour: {Report.PeakHour:00}:00–{Report.PeakHour + 1:00}:00";
        public string OutcomeAnswered => $"Answered: {Report?.Summary.Successful ?? 0}";
        public string OutcomeCancelled => $"Cancelled by caller: {Report?.Summary.Cancelled ?? 0}";
        public string OutcomeAgentCancel => $"Agent hung up before answer: {Report?.Summary.AgentCancelledBeforeAnswer ?? 0}";
        public string OutcomeFailed => $"Failed: {Report?.Summary.Failed ?? 0}";
        public string OutcomeMissed => $"Missed incoming: {Report?.Summary.Missed ?? 0}";
        public bool HasCallerIds => CallerIds.Count > 0;
        public bool HasTopNumbers => TopNumbers.Count > 0;

        public void Apply(CallStatisticsReport report, string mainName, string secondaryName)
        {
            Report = report;
            PeriodHint = $"{report.FromInclusive:dd MMM} – {report.ToInclusive:dd MMM yyyy} · {report.Summary.TotalCompleted} calls";
            HasLegacyUnknownConnection = report.HasLegacyUnknownConnection;

            Replace(Daily, report.DailyBuckets);
            Replace(Hourly, report.HourlyBuckets);
            Replace(ConnectionCompare, report.ConnectionCompare.Select(r => new StatsRow
            {
                Name = r.Name,
                Detail = $"{r.Summary.TotalCompleted} calls · {r.Summary.Successful} answered · {r.Summary.SuccessRatePercent:0.#}% · talk {CallStatisticsService.FormatDuration(r.Summary.TotalTalkTime)}"
            }));
            Replace(CallerIds, report.CallerIdStats.Select(c => new StatsRow
            {
                Name = c.CallerId,
                Detail = $"{c.CallerId}: {c.Total} calls · {c.Answered} answered ({c.SuccessRatePercent:0.#}%)"
            }));
            Replace(TopNumbers, report.TopNumbers.Select(t => new StatsRow
            {
                PhoneNumber = t.PhoneNumber,
                Name = t.PhoneNumber,
                Detail = $"{t.TotalCalls} calls · {t.Answered} answered · {CallStatisticsService.FormatDuration(t.TotalTalkTime)}"
            }));

            ConnectionOptions[1] = mainName;
            ConnectionOptions[2] = secondaryName;

            foreach (var p in new[]
            {
                nameof(KpiTotal), nameof(KpiDirectionHint), nameof(KpiAnswered), nameof(KpiSuccessRate),
                nameof(KpiUnanswered), nameof(KpiUnansweredHint), nameof(KpiTalkTime), nameof(KpiAvgTalk),
                nameof(KpiContactRate), nameof(KpiRingTime), nameof(KpiMissed), nameof(KpiFailedCancelled),
                nameof(PeakHourText), nameof(OutcomeAnswered), nameof(OutcomeCancelled), nameof(OutcomeAgentCancel),
                nameof(OutcomeFailed), nameof(OutcomeMissed), nameof(HasCallerIds), nameof(HasTopNumbers)
            })
                OnPropertyChanged(p);
        }

        private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
        {
            target.Clear();
            foreach (var item in source) target.Add(item);
        }
    }
}
