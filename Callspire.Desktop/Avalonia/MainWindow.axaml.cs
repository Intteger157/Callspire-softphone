using System;
using System.Collections.Generic;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Media;
using global::Avalonia.Threading;
using Softphone.Audio;

namespace Softphone.Avalonia
{
    /// <summary>
    /// Avalonia port of MainWindow. Phase 5 skeleton — business logic wiring
    /// delegated to Phase 6 (cross-platform validation).
    /// </summary>
    public partial class MainWindow : Window
    {
        // ── Services ──────────────────────────────────────────────────────────
        private SipService?    _mainSipService;
        private SipService?    _secondarySipService;
        private WebRtcService? _mainWebRtcService;
        private WebRtcService? _secondaryWebRtcService;

        // ── State ─────────────────────────────────────────────────────────────
        private string      _phoneNumber               = string.Empty;
        private CallWindow?    _activeCallWindow;
        private SettingsWindow? _settingsWindow;

        // ── Constructor ───────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            AvaloniaWindowChromeHelper.Apply(this, preferAccent: false);
            InitializeComboBoxes();
        }

        public MainWindow(
            SipService?    mainSipService,
            SipService?    secondarySipService,
            WebRtcService? mainWebRtcService      = null,
            WebRtcService? secondaryWebRtcService = null)
        {
            InitializeComponent();
            _mainSipService         = mainSipService;
            _secondarySipService    = secondarySipService;
            _mainWebRtcService      = mainWebRtcService;
            _secondaryWebRtcService = secondaryWebRtcService;

            AvaloniaWindowChromeHelper.Apply(this, preferAccent: false);
            InitializeComboBoxes();
            WireUpServiceEvents();
        }

        // ── Service event wiring ──────────────────────────────────────────────
        private void WireUpServiceEvents()
        {
            if (_mainSipService != null)
            {
                _mainSipService.OnIncomingCall   += OnIncomingCall;
                _mainSipService.OnStatusChanged  += OnMainStatusChanged;
            }
        }

        private void OnIncomingCall(string phoneNumber)
        {
            UiThread.BeginInvoke(() =>
            {
                AppLog.Log($"[MainWindow] Incoming call from {phoneNumber}");
                OpenCallWindow(phoneNumber, isIncoming: true);
            });
        }

        private void OnMainStatusChanged(string status)
        {
            UiThread.BeginInvoke(() =>
            {
                var isOnline = status.Contains("Registered", StringComparison.OrdinalIgnoreCase);
                UpdateConnectionStatus(status, isOnline);
            });
        }

        // ── UI helpers ────────────────────────────────────────────────────────
        private void UpdateConnectionStatus(string status, bool isOnline)
        {
            var tb  = this.FindControl<TextBlock>("MainStatusTextBlock");
            var dot = this.FindControl<Border>("MainStatusIndicator");
            if (tb  != null) tb.Text = status;
            if (dot != null) dot.Background = isOnline
                ? new SolidColorBrush(global::Avalonia.Media.Color.Parse("#23A55A"))
                : new SolidColorBrush(global::Avalonia.Media.Color.Parse("#9CA3AF"));

            var indicator = this.FindControl<Border>("OnlineStatusIndicator");
            if (indicator != null) indicator.IsVisible = isOnline;

            var userTb = this.FindControl<TextBlock>("UserAccountTextBlock");
            if (userTb != null) userTb.Text = isOnline ? status : "Not connected";
        }

        private void ShowView(string viewName)
        {
            foreach (var name in new[] { "DialerView", "HistoryView", "StatisticsView" })
            {
                var ctrl = this.FindControl<Control>(name);
                if (ctrl != null) ctrl.IsVisible = ctrl.Name == viewName;
            }
        }

        private void InitializeComboBoxes()
        {
            var periodCombo = this.FindControl<ComboBox>("StatisticsPeriodComboBox");
            if (periodCombo != null)
            {
                periodCombo.ItemsSource   = new[] { "Last 7 days", "Last 30 days", "Last 90 days", "Custom" };
                periodCombo.SelectedIndex = 0;
            }
            var dirCombo = this.FindControl<ComboBox>("StatisticsDirectionComboBox");
            if (dirCombo != null)
            {
                dirCombo.ItemsSource   = new[] { "All", "Inbound", "Outbound" };
                dirCombo.SelectedIndex = 0;
            }
        }

        // ── Call management ───────────────────────────────────────────────────
        private void OpenCallWindow(
            string    phoneNumber,
            bool      isIncoming   = false,
            bool      useWebRtc    = false,
            bool      useSecondary = false)
        {
            if (_activeCallWindow != null) { _activeCallWindow.Activate(); return; }

            CallWindow callWindow;
            if (useWebRtc)
            {
                var svc = useSecondary ? _secondaryWebRtcService : _mainWebRtcService;
                if (svc == null) { AppLog.Log("WebRTC service not available"); return; }
                callWindow = new CallWindow(svc, phoneNumber, isIncoming);
            }
            else
            {
                var svc = useSecondary ? _secondarySipService : _mainSipService;
                if (svc == null) { AppLog.Log("SIP service not available"); return; }
                callWindow = new CallWindow(svc, phoneNumber, isIncoming);
            }

            callWindow.OnCallDetailsChanged += OnCallDetailsReceived;
            callWindow.Closed += (_, _) => { _activeCallWindow = null; };
            _activeCallWindow = callWindow;
            callWindow.Show(this);
        }

        private void OnCallDetailsReceived(
            string phoneNumber, DateTime callStartTime,
            DateTime? ringbackStartTime, DateTime? ringbackEndTime, DateTime? answerTime,
            bool wasAnswered, CallEndedBy endedBy, List<string>? technicalDetails,
            TimeSpan? duration, string? recordingPath, CallTransport? transport,
            string? outboundCallerId, string? connectionName)
        {
            AppLog.Log($"[MainWindow] Call ended: {phoneNumber}, answered={wasAnswered}, duration={duration}");
        }

        // ── Title bar ─────────────────────────────────────────────────────────
        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void TitleBar_PointerReleased(object? sender, PointerReleasedEventArgs e) { }
        private void TitleBar_PointerMoved(object? sender, PointerEventArgs e) { }

        private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void CloseButton_Click(object? sender, RoutedEventArgs e)
            => Close();

        // ── Sidebar navigation ────────────────────────────────────────────────
        private void DialerButton_Click(object? sender, RoutedEventArgs e)
            => ShowView("DialerView");

        private void HistoryButton_Click(object? sender, RoutedEventArgs e)
            => ShowView("HistoryView");

        private void StatisticsButton_Click(object? sender, RoutedEventArgs e)
            => ShowView("StatisticsView");

        private void SettingsButton_Click(object? sender, RoutedEventArgs e)
            => OpenSettingsWindow();

        private void OpenSettingsWindow()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            // Немодально: главное окно остаётся доступным (не ShowDialog).
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        private void ShowLogsButton_Click(object? sender, RoutedEventArgs e)
            => AppLog.Log("Log window not yet ported to Avalonia (Phase 5.6)");

        // ── Dialer handlers ───────────────────────────────────────────────────
        private void PhoneNumberTextBox_GotFocus(object? sender, global::Avalonia.Input.FocusChangedEventArgs e) { }

        private void PhoneNumberTextBox_LostFocus(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) { }

        private void PhoneNumberTextBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; InitiateCall(); }
        }

        private void PhoneNumberTextBox_TextChanged(object? sender, global::Avalonia.Controls.TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                _phoneNumber = tb.Text ?? string.Empty;
                var callBtn = this.FindControl<Button>("CallButton");
                if (callBtn != null) callBtn.IsEnabled = !string.IsNullOrWhiteSpace(_phoneNumber);
            }
        }

        private void NumpadButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                var tb = this.FindControl<TextBox>("PhoneNumberTextBox");
                if (tb != null) tb.Text = (tb.Text ?? string.Empty) + (btn.Content?.ToString() ?? string.Empty);
            }
        }

        private void BackspaceButton_Click(object? sender, RoutedEventArgs e)
        {
            var tb = this.FindControl<TextBox>("PhoneNumberTextBox");
            if (tb?.Text?.Length > 0) tb.Text = tb.Text[..^1];
        }

        private void CallButton_Click(object? sender, RoutedEventArgs e) => InitiateCall();
        private void SplitCallPrimary_Click(object? sender, RoutedEventArgs e) => InitiateCall(useSecondary: false);
        private void SplitCallSecondary_Click(object? sender, RoutedEventArgs e) => InitiateCall(useSecondary: true);

        private void InitiateCall(bool useSecondary = false)
        {
            var tb = this.FindControl<TextBox>("PhoneNumberTextBox");
            var number = tb?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(number)) return;
            bool useWebRtc = useSecondary
                ? _secondaryWebRtcService != null
                : _mainWebRtcService != null && _mainSipService == null;
            OpenCallWindow(number, isIncoming: false, useWebRtc: useWebRtc, useSecondary: useSecondary);
        }

        private void MainRefreshButton_Click(object? sender, RoutedEventArgs e)
            => AppLog.Log("[MainWindow] Manual reconnect main (TODO Phase 6)");

        private void SecondaryRefreshButton_Click(object? sender, RoutedEventArgs e)
            => AppLog.Log("[MainWindow] Manual reconnect secondary (TODO Phase 6)");

        // ── History ───────────────────────────────────────────────────────────
        private void HistoryItem_PointerPressed(object? sender, PointerPressedEventArgs e) { }
        private void CallFromHistoryButton_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is string number)
            {
                var tb = this.FindControl<TextBox>("PhoneNumberTextBox");
                if (tb != null) tb.Text = number;
                InitiateCall();
            }
        }
        private void ClearHistoryButton_Click(object? sender, RoutedEventArgs e)
            => AppLog.Log("[MainWindow] Clear history (TODO Phase 6)");

        // ── Statistics ────────────────────────────────────────────────────────
        private void StatisticsFilter_Changed(object? sender, SelectionChangedEventArgs e) { }
        private void StatisticsCustomDatePicker_SelectedDateChanged(
            object? sender, global::Avalonia.Controls.SelectionChangedEventArgs e) { }
        private void StatisticsKpi_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if ((sender as Border)?.Tag is string filter)
            { AppLog.Log($"[MainWindow] Stats KPI filter: {filter}"); ShowView("HistoryView"); }
        }
        private void StatisticsTopNumber_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if ((sender as Border)?.Tag is string number)
            { var tb = this.FindControl<TextBox>("PhoneNumberTextBox"); if (tb != null) tb.Text = number; ShowView("DialerView"); }
        }
        private void StatisticsExportCsvButton_Click(object? sender, RoutedEventArgs e)
            => AppLog.Log("[MainWindow] Export CSV (TODO Phase 6)");

        // ── Lifecycle ─────────────────────────────────────────────────────────
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (_mainSipService != null)
            {
                _mainSipService.OnIncomingCall  -= OnIncomingCall;
                _mainSipService.OnStatusChanged -= OnMainStatusChanged;
            }
            AppLog.Log("[MainWindow Avalonia] Closed");
        }
    }
}
