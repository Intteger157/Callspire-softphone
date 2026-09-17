using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Softphone.AppHost.ViewModels
{
    /// <summary>
    /// State of the main window: connection rows, dialer, caller-ID picker, history, statistics.
    /// Updated by <see cref="DesktopAppController"/> on the UI thread; bound by the Avalonia MainWindow.
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        private string _phoneNumber = "";
        private string _accountText = "Not connected";
        private string _accountToolTip = "Not connected";
        private bool _anyOnline;
        private bool _showSplitCallButtons;
        private string _splitPrimaryLabel = "PBX";
        private string _splitSecondaryLabel = "SIP";
        private string? _sipAuthFailureText;
        private bool _showCallerIdPicker;
        private CallerIdItem? _selectedCallerId;
        private string? _historyFilterHint;
        private bool _amoCrmConfigured;
        private string _amoCrmStatusText = "Not connected";
        private bool _amoCrmOnline;

        public ConnectionStatusViewModel Main { get; } = new(ConnectionSlot.Main);
        public ConnectionStatusViewModel Secondary { get; } = new(ConnectionSlot.Secondary);
        public StatisticsViewModel Statistics { get; } = new();

        public ObservableCollection<HistoryItemViewModel> History { get; } = new();
        public ObservableCollection<CallerIdItem> CallerIds { get; } = new();

        public string PhoneNumber { get => _phoneNumber; set { if (Set(ref _phoneNumber, value)) OnPropertyChanged(nameof(CanCall)); } }
        public bool CanCall => !string.IsNullOrWhiteSpace(_phoneNumber) && _anyOnline;

        /// <summary>Title-bar account label ("204 @ pbx.example.com").</summary>
        public string AccountText { get => _accountText; set => Set(ref _accountText, value); }
        public string AccountToolTip { get => _accountToolTip; set => Set(ref _accountToolTip, value); }
        public bool AnyOnline { get => _anyOnline; set { if (Set(ref _anyOnline, value)) OnPropertyChanged(nameof(CanCall)); } }

        public bool ShowSplitCallButtons { get => _showSplitCallButtons; set { if (Set(ref _showSplitCallButtons, value)) OnPropertyChanged(nameof(ShowSingleCallButton)); } }
        public bool ShowSingleCallButton => !ShowSplitCallButtons;
        public string SplitPrimaryLabel { get => _splitPrimaryLabel; set => Set(ref _splitPrimaryLabel, value); }
        public string SplitSecondaryLabel { get => _splitSecondaryLabel; set => Set(ref _splitSecondaryLabel, value); }

        public string? SipAuthFailureText { get => _sipAuthFailureText; set { if (Set(ref _sipAuthFailureText, value)) OnPropertyChanged(nameof(ShowSipAuthFailureBanner)); } }
        public bool ShowSipAuthFailureBanner => !string.IsNullOrWhiteSpace(_sipAuthFailureText);

        public bool ShowCallerIdPicker { get => _showCallerIdPicker; set => Set(ref _showCallerIdPicker, value); }
        public CallerIdItem? SelectedCallerId { get => _selectedCallerId; set => Set(ref _selectedCallerId, value); }

        public string? HistoryFilterHint { get => _historyFilterHint; set { if (Set(ref _historyFilterHint, value)) OnPropertyChanged(nameof(HasHistoryFilter)); } }
        public bool HasHistoryFilter => !string.IsNullOrWhiteSpace(_historyFilterHint);
        public bool HistoryIsEmpty => History.Count == 0;

        public bool AmoCrmConfigured { get => _amoCrmConfigured; set => Set(ref _amoCrmConfigured, value); }
        public string AmoCrmStatusText { get => _amoCrmStatusText; set => Set(ref _amoCrmStatusText, value); }
        public bool AmoCrmOnline { get => _amoCrmOnline; set => Set(ref _amoCrmOnline, value); }

        public void ReplaceHistory(IEnumerable<HistoryItemViewModel> items)
        {
            History.Clear();
            foreach (var i in items) History.Add(i);
            OnPropertyChanged(nameof(HistoryIsEmpty));
        }

        public void ReplaceCallerIds(IEnumerable<CallerIdItem> items, string? selectedNumber)
        {
            CallerIds.Clear();
            CallerIdItem? selected = null;
            foreach (var i in items)
            {
                CallerIds.Add(i);
                if (selected == null && !string.IsNullOrEmpty(selectedNumber) && i.Number == selectedNumber) selected = i;
            }
            ShowCallerIdPicker = CallerIds.Count > 1;
            SelectedCallerId = selected ?? (CallerIds.Count > 0 ? CallerIds[0] : null);
        }
    }
}
