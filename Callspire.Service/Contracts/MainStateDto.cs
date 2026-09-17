using System;
using System.Collections.Generic;
using System.Linq;
using Softphone.AppHost;
using Softphone.AppHost.ViewModels;

namespace Softphone.Service.Contracts
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  C# → Swift: `stateSnapshot` event / `getState` result.
    //  Mirrors MainViewModel one-to-one so the SwiftUI views bind to a plain struct.
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class ConnectionStatusDto
    {
        public string Slot { get; init; } = "main";
        public string Label { get; init; } = "";
        public string Text { get; init; } = "";
        public bool IsOnline { get; init; }
        public bool IsError { get; init; }
        public bool IsConfigured { get; init; }
        public bool IsWebRtc { get; init; }
        public string DisplayName { get; init; } = "";

        public static ConnectionStatusDto From(ConnectionStatusViewModel vm) => new()
        {
            Slot = vm.Slot == ConnectionSlot.Main ? "main" : "secondary",
            Label = vm.Label, Text = vm.Text, IsOnline = vm.IsOnline, IsError = vm.IsError,
            IsConfigured = vm.IsConfigured, IsWebRtc = vm.IsWebRtc, DisplayName = vm.DisplayName,
        };
    }

    public sealed class CallerIdDto
    {
        public string Number { get; init; } = "";
        public string Name { get; init; } = "";
        public string DisplayText { get; init; } = "";

        public static CallerIdDto From(CallerIdItem c) => new() { Number = c.Number, Name = c.Name, DisplayText = c.DisplayText };
    }

    public sealed class HistoryItemDto
    {
        /// <summary>Stable key for the row: phone number + call time ticks (used by getCallDetails / retryKommo).</summary>
        public string PhoneNumber { get; init; } = "";
        public DateTime CallTime { get; init; }
        public string PhoneNumberDisplay { get; init; } = "";
        public string CallTimeText { get; init; } = "";
        public string DurationText { get; init; } = "";
        public string Status { get; init; } = "";
        public string StatusKind { get; init; } = "neutral";
        public bool IsIncoming { get; init; }
        public bool IsMissed { get; init; }
        public bool WasAnswered { get; init; }
        public string TransportLabel { get; init; } = "";
        public bool IsWebRtc { get; init; }
        public string ConnectionLabel { get; init; } = "";
        public bool HasRecording { get; init; }
        public string? OutboundCallerId { get; init; }
        public string CrmStatus { get; init; } = "";

        public static HistoryItemDto From(HistoryItemViewModel vm) => new()
        {
            PhoneNumber = vm.PhoneNumber, CallTime = vm.CallTime, PhoneNumberDisplay = vm.PhoneNumberDisplay,
            CallTimeText = vm.CallTimeText, DurationText = vm.DurationText, Status = vm.Status, StatusKind = vm.StatusKind,
            IsIncoming = vm.IsIncoming, IsMissed = vm.IsMissed, WasAnswered = vm.WasAnswered,
            TransportLabel = vm.TransportLabel, IsWebRtc = vm.IsWebRtc, ConnectionLabel = vm.ConnectionLabel,
            HasRecording = vm.HasRecording, OutboundCallerId = vm.OutboundCallerId, CrmStatus = vm.CrmStatus,
        };
    }

    public sealed class StatsRowDto
    {
        public string Name { get; init; } = "";
        public string Detail { get; init; } = "";
        public string PhoneNumber { get; init; } = "";
        public static StatsRowDto From(StatsRow r) => new() { Name = r.Name, Detail = r.Detail, PhoneNumber = r.PhoneNumber };
    }

    public sealed class DayBucketDto
    {
        public DateTime Date { get; init; }
        public string Label { get; init; } = "";
        public int Total { get; init; }
        public int Answered { get; init; }
        public double BarHeight { get; init; }
        public string ToolTipText { get; init; } = "";
    }

    public sealed class HourBucketDto
    {
        public int Hour { get; init; }
        public string Label { get; init; } = "";
        public int Total { get; init; }
        public double BarHeight { get; init; }
    }

    public sealed class StatisticsDto
    {
        public IReadOnlyList<string> PeriodOptions { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> DirectionOptions { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ConnectionOptions { get; init; } = Array.Empty<string>();
        public int PeriodIndex { get; init; }
        public int ConnectionIndex { get; init; }
        public int DirectionIndex { get; init; }
        public DateTime? CustomFrom { get; init; }
        public DateTime? CustomTo { get; init; }
        public bool IsCustomPeriod { get; init; }
        public bool SecondaryEnabled { get; init; }
        public string PeriodHint { get; init; } = "";
        public bool HasLegacyUnknownConnection { get; init; }
        public bool HasReport { get; init; }

        public string KpiTotal { get; init; } = "";
        public string KpiDirectionHint { get; init; } = "";
        public string KpiAnswered { get; init; } = "";
        public string KpiSuccessRate { get; init; } = "";
        public string KpiUnanswered { get; init; } = "";
        public string KpiUnansweredHint { get; init; } = "";
        public string KpiTalkTime { get; init; } = "";
        public string KpiAvgTalk { get; init; } = "";
        public string KpiContactRate { get; init; } = "";
        public string KpiRingTime { get; init; } = "";
        public string KpiMissed { get; init; } = "";
        public string KpiFailedCancelled { get; init; } = "";
        public string PeakHourText { get; init; } = "";
        public string OutcomeAnswered { get; init; } = "";
        public string OutcomeCancelled { get; init; } = "";
        public string OutcomeAgentCancel { get; init; } = "";
        public string OutcomeFailed { get; init; } = "";
        public string OutcomeMissed { get; init; } = "";

        public IReadOnlyList<DayBucketDto> Daily { get; init; } = Array.Empty<DayBucketDto>();
        public IReadOnlyList<HourBucketDto> Hourly { get; init; } = Array.Empty<HourBucketDto>();
        public IReadOnlyList<StatsRowDto> ConnectionCompare { get; init; } = Array.Empty<StatsRowDto>();
        public IReadOnlyList<StatsRowDto> CallerIds { get; init; } = Array.Empty<StatsRowDto>();
        public IReadOnlyList<StatsRowDto> TopNumbers { get; init; } = Array.Empty<StatsRowDto>();

        public static StatisticsDto From(StatisticsViewModel vm) => new()
        {
            PeriodOptions = StatisticsViewModel.PeriodOptions,
            DirectionOptions = StatisticsViewModel.DirectionOptions,
            ConnectionOptions = vm.ConnectionOptions.ToList(),
            PeriodIndex = vm.PeriodIndex, ConnectionIndex = vm.ConnectionIndex, DirectionIndex = vm.DirectionIndex,
            CustomFrom = vm.CustomFrom, CustomTo = vm.CustomTo, IsCustomPeriod = vm.IsCustomPeriod,
            SecondaryEnabled = vm.SecondaryEnabled, PeriodHint = vm.PeriodHint,
            HasLegacyUnknownConnection = vm.HasLegacyUnknownConnection, HasReport = vm.Report != null,
            KpiTotal = vm.KpiTotal, KpiDirectionHint = vm.KpiDirectionHint, KpiAnswered = vm.KpiAnswered,
            KpiSuccessRate = vm.KpiSuccessRate, KpiUnanswered = vm.KpiUnanswered, KpiUnansweredHint = vm.KpiUnansweredHint,
            KpiTalkTime = vm.KpiTalkTime, KpiAvgTalk = vm.KpiAvgTalk, KpiContactRate = vm.KpiContactRate,
            KpiRingTime = vm.KpiRingTime, KpiMissed = vm.KpiMissed, KpiFailedCancelled = vm.KpiFailedCancelled,
            PeakHourText = vm.PeakHourText, OutcomeAnswered = vm.OutcomeAnswered, OutcomeCancelled = vm.OutcomeCancelled,
            OutcomeAgentCancel = vm.OutcomeAgentCancel, OutcomeFailed = vm.OutcomeFailed, OutcomeMissed = vm.OutcomeMissed,
            Daily = vm.Daily.Select(d => new DayBucketDto { Date = d.Date, Label = d.Label, Total = d.Total, Answered = d.Answered, BarHeight = d.BarHeight, ToolTipText = d.ToolTipText }).ToList(),
            Hourly = vm.Hourly.Select(h => new HourBucketDto { Hour = h.Hour, Label = h.Label, Total = h.Total, BarHeight = h.BarHeight }).ToList(),
            ConnectionCompare = vm.ConnectionCompare.Select(StatsRowDto.From).ToList(),
            CallerIds = vm.CallerIds.Select(StatsRowDto.From).ToList(),
            TopNumbers = vm.TopNumbers.Select(StatsRowDto.From).ToList(),
        };
    }

    public sealed class MainStateDto
    {
        public string PhoneNumber { get; init; } = "";
        public bool CanCall { get; init; }
        public string AccountText { get; init; } = "";
        public string AccountToolTip { get; init; } = "";
        public bool AnyOnline { get; init; }
        public bool ShowSplitCallButtons { get; init; }
        public string SplitPrimaryLabel { get; init; } = "";
        public string SplitSecondaryLabel { get; init; } = "";
        public string? SipAuthFailureText { get; init; }
        public bool ShowCallerIdPicker { get; init; }
        public string? SelectedCallerId { get; init; }
        public string? HistoryFilterHint { get; init; }
        public bool AmoCrmConfigured { get; init; }
        public string AmoCrmStatusText { get; init; } = "";
        public bool AmoCrmOnline { get; init; }
        public bool HasActiveCall { get; init; }
        public string ThemeMode { get; init; } = "system";

        public ConnectionStatusDto Main { get; init; } = new();
        public ConnectionStatusDto Secondary { get; init; } = new();
        public IReadOnlyList<CallerIdDto> CallerIds { get; init; } = Array.Empty<CallerIdDto>();
        public IReadOnlyList<HistoryItemDto> History { get; init; } = Array.Empty<HistoryItemDto>();
        public StatisticsDto Statistics { get; init; } = new();

        public static MainStateDto From(MainViewModel vm, bool hasActiveCall, string themeMode) => new()
        {
            PhoneNumber = vm.PhoneNumber, CanCall = vm.CanCall, AccountText = vm.AccountText, AccountToolTip = vm.AccountToolTip,
            AnyOnline = vm.AnyOnline, ShowSplitCallButtons = vm.ShowSplitCallButtons,
            SplitPrimaryLabel = vm.SplitPrimaryLabel, SplitSecondaryLabel = vm.SplitSecondaryLabel,
            SipAuthFailureText = vm.SipAuthFailureText, ShowCallerIdPicker = vm.ShowCallerIdPicker,
            SelectedCallerId = vm.SelectedCallerId?.Number, HistoryFilterHint = vm.HistoryFilterHint,
            AmoCrmConfigured = vm.AmoCrmConfigured, AmoCrmStatusText = vm.AmoCrmStatusText, AmoCrmOnline = vm.AmoCrmOnline,
            HasActiveCall = hasActiveCall, ThemeMode = themeMode,
            Main = ConnectionStatusDto.From(vm.Main), Secondary = ConnectionStatusDto.From(vm.Secondary),
            CallerIds = vm.CallerIds.Select(CallerIdDto.From).ToList(),
            History = vm.History.Select(HistoryItemDto.From).ToList(),
            Statistics = StatisticsDto.From(vm.Statistics),
        };
    }
}
