using System;
using System.Threading.Tasks;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Threading;
using Softphone.AppHost;
using Softphone.AppHost.ViewModels;
using Softphone.WebRtc;

namespace Softphone.Avalonia
{
    /// <summary>
    /// Avalonia main window: a thin shell over <see cref="DesktopAppController"/>.
    /// Binds to <see cref="MainViewModel"/> and implements <see cref="IDesktopShell"/> so the
    /// controller can open call / picker / message windows without knowing the UI framework.
    /// </summary>
    public partial class MainWindow : Window, IDesktopShell
    {
        private readonly DesktopAppController _controller;
        private CallWindow? _activeCallWindow;
        private SettingsWindow? _settingsWindow;
        private LogWindow? _logWindow;
        private bool _closingForShutdown;

        public MainViewModel ViewModel => _controller.ViewModel;

        /// <summary>XAML-compiler / previewer constructor only. Runtime code must use <see cref="MainWindow(DesktopAppController)"/>.</summary>
        [Obsolete("Design-time only", error: true)]
        public MainWindow()
        {
            _controller = null!;
            InitializeComponent();
        }

        public MainWindow(DesktopAppController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            DataContext = _controller.ViewModel;
            InitializeComponent();
            PlatformWindowChrome.Apply(this, preferAccent: false);

            LogBuffer.Attach();
            _controller.AttachShell(this);
            _controller.WebRtcHostFactory = CreateWebRtcHostAsync;
            KommoLeadSelectionUi.Handler = req => Dialogs.ShowKommoLeadPickerAsync(this, req);
            ShowView("DialerView");
        }

        // ══════════════════════════ WebRTC engine host ══════════════════════════

        private NativeWebView? _webRtcView;
        private AvaloniaWebRtcEngineHost? _webRtcHost;

        /// <summary>
        /// Lazily creates the hidden <see cref="NativeWebView"/> and the <see cref="AvaloniaWebRtcEngineHost"/>
        /// on first WebRTC use (called by the controller on the UI thread). Returns null when the platform
        /// has no usable web engine; the controller then reports WebRTC lines as unavailable.
        /// </summary>
        private async Task<IWebRtcEngineHost?> CreateWebRtcHostAsync()
        {
            if (_webRtcHost is { IsInitialized: true }) return _webRtcHost;

            try
            {
                var container = this.FindControl<Border>("WebRtcEngineHostContainer");
                if (container == null) return null;

                if (_webRtcView == null)
                {
                    _webRtcView = new NativeWebView();
                    container.Child = _webRtcView;
                }
                container.IsVisible = true;

                _webRtcHost?.Dispose();
                _webRtcHost = await AvaloniaWebRtcEngineHost.CreateAsync(_webRtcView);
                if (_webRtcHost == null)
                {
                    AppLog.Log("[MainWindow] WebRTC engine host unavailable on this platform");
                    container.IsVisible = false;
                }
                return _webRtcHost;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[MainWindow] CreateWebRtcHostAsync failed: {ex.Message}");
                return null;
            }
        }

        // ══════════════════════════ IDesktopShell ══════════════════════════

        public bool HasActiveCallWindow => _activeCallWindow != null;
        public bool IsWebRtcCallActive => _activeCallWindow?.ViewModel.IsWebRtc == true;

        public void ShowCallWindow(CallWindowRequest request)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_activeCallWindow != null)
                {
                    _activeCallWindow.Activate();
                    return;
                }

                var win = new CallWindow(_controller, request);
                win.CallCompleted += report => _controller.OnCallCompleted(report);
                win.Closed += (_, _) => { if (ReferenceEquals(_activeCallWindow, win)) _activeCallWindow = null; };
                _activeCallWindow = win;

                if (IsVisible) win.Show(this); else win.Show();
                win.Activate();
                if (request.IsIncoming) BringToForeground();
            });
        }

        public void ShowSettings() => Dispatcher.UIThread.Post(OpenSettingsWindow);

        public Task<ConnectionSelectionResult> ShowConnectionSelectionAsync(ConnectionSelectionRequest request)
        {
            var tcs = new TaskCompletionSource<ConnectionSelectionResult>();
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var dlg = new ConnectionSelectionWindow(
                        request.HasMain, request.IsMainWebRtc, request.MainStatus,
                        request.HasSecondary, request.SecondaryStatus,
                        request.MainName, request.SecondaryName,
                        new System.Collections.Generic.List<CallerIdItem>(request.MainCallerIds),
                        request.SelectedCallerId,
                        isSecondaryWebRtc: request.IsSecondaryWebRtc);
                    await dlg.ShowDialog(this);
                    tcs.TrySetResult(new ConnectionSelectionResult
                    {
                        Slot = dlg.SelectedConnection switch
                        {
                            ConnectionSelectionWindow.ConnectionType.Main => ConnectionSlot.Main,
                            ConnectionSelectionWindow.ConnectionType.Secondary => ConnectionSlot.Secondary,
                            _ => null,
                        },
                        CallerId = dlg.SelectedCallerId,
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[MainWindow] connection picker failed: {ex.Message}");
                    tcs.TrySetResult(new ConnectionSelectionResult());
                }
            });
            return tcs.Task;
        }

        public Task<LeadSelectionResult> ShowLeadSelectionAsync(string phoneNumber)
            => Dialogs.ShowLeadSelectionAsync(this, phoneNumber);

        public void ShowMessage(string title, string text)
            => _ = Dialogs.ShowMessageAsync(this, title, text);

        public void ShowUpdateAvailable(UpdateInfo info, string currentVersion)
            => _ = Dialogs.ShowUpdateAvailableAsync(this, info, currentVersion);

        public void BringToForeground()
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    if (!IsVisible) Show();
                    Activate();
                }
                catch (Exception ex) { AppLog.Log($"[MainWindow] BringToForeground: {ex.Message}"); }
            });
        }

        // ══════════════════════════ navigation ══════════════════════════

        private void ShowView(string viewName)
        {
            foreach (var name in new[] { "DialerView", "HistoryView", "StatisticsView" })
            {
                var ctrl = this.FindControl<Control>(name);
                if (ctrl != null) ctrl.IsVisible = ctrl.Name == viewName;
            }
            foreach (var (btn, view) in new[] { ("DialerButton", "DialerView"), ("HistoryButton", "HistoryView"), ("StatisticsButton", "StatisticsView") })
            {
                var b = this.FindControl<Button>(btn);
                if (b == null) continue;
                if (view == viewName) b.Classes.Add("active"); else b.Classes.Remove("active");
            }
            if (viewName == "StatisticsView") _controller.RefreshStatistics();
        }

        private void DialerButton_Click(object? sender, RoutedEventArgs e) => ShowView("DialerView");
        private void HistoryButton_Click(object? sender, RoutedEventArgs e) => ShowView("HistoryView");
        private void StatisticsButton_Click(object? sender, RoutedEventArgs e) => ShowView("StatisticsView");
        private void SettingsButton_Click(object? sender, RoutedEventArgs e) => OpenSettingsWindow();

        private void OpenSettingsWindow()
        {
            if (_settingsWindow != null) { _settingsWindow.Activate(); return; }

            _settingsWindow = new SettingsWindow(_controller);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
            };
            _settingsWindow.Show(this);
            _settingsWindow.Activate();
        }

        private void ShowLogsButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_logWindow != null) { _logWindow.Activate(); return; }
            _logWindow = new LogWindow();
            _logWindow.Closed += (_, _) => _logWindow = null;
            _logWindow.Show(this);
        }

        // ══════════════════════════ title bar (custom chrome only) ══════════════════════════

        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
                BeginMoveDrag(e);
            }
        }

        private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

        // ══════════════════════════ dialer ══════════════════════════

        private void PhoneNumberTextBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = InitiateCallAsync(); }
        }

        private void NumpadButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Content: string digit })
                ViewModel.PhoneNumber += digit;
        }

        private void BackspaceButton_Click(object? sender, RoutedEventArgs e)
        {
            var text = ViewModel.PhoneNumber;
            if (!string.IsNullOrEmpty(text)) ViewModel.PhoneNumber = text[..^1];
        }

        private void CallButton_Click(object? sender, RoutedEventArgs e) => _ = InitiateCallAsync();
        private void SplitCallPrimary_Click(object? sender, RoutedEventArgs e) => _ = InitiateCallAsync(ConnectionSlot.Main);
        private void SplitCallSecondary_Click(object? sender, RoutedEventArgs e) => _ = InitiateCallAsync(ConnectionSlot.Secondary);

        private async Task InitiateCallAsync(ConnectionSlot? slot = null)
        {
            var number = ViewModel.PhoneNumber?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(number)) return;
            try { await _controller.PlaceCallAsync(number, slot); }
            catch (Exception ex) { AppLog.Log($"[MainWindow] PlaceCall failed: {ex.Message}"); ShowMessage("Call", ex.Message); }
        }

        private void CallerIdComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox { SelectedItem: CallerIdItem item })
                _controller.SelectCallerId(item.Number);
        }

        private void MainRefreshButton_Click(object? sender, RoutedEventArgs e) => _ = _controller.ReconnectSlotAsync(ConnectionSlot.Main);
        private void SecondaryRefreshButton_Click(object? sender, RoutedEventArgs e) => _ = _controller.ReconnectSlotAsync(ConnectionSlot.Secondary);

        // ══════════════════════════ history ══════════════════════════

        private void HistoryItem_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if ((sender as Control)?.Tag is not HistoryItemViewModel vm) return;
            OpenCallDetails(vm.Model);
        }

        private void OpenCallDetails(CallHistoryItem call)
        {
            var mainName = ViewModel.Main.DisplayName;
            var secondaryName = ViewModel.Secondary.DisplayName;
            string? connectionName = call.ConnectionSlot switch
            {
                CallConnectionSlot.Main => mainName,
                CallConnectionSlot.Secondary => secondaryName,
                _ => null,
            };

            var details = new CallDetailsWindow(
                call.PhoneNumber, call.CallTime,
                call.RingbackStartTime, call.RingbackEndTime, call.AnswerTime,
                call.WasAnswered, call.EndedBy, call.TechnicalDetails, call.Duration,
                call.RecordingFilePath, call.Transport, call.OutboundCallerId, connectionName);
            if (_controller.Settings.EnableAmoCrmIntegration)
            {
                details.ConfigureKommo(call.AmoCrmUploadStatus, call.AmoCrmUploadReason, call.AmoCrmLeadId,
                    _controller.Settings.AmoCrmSubdomain,
                    _controller.CanRetryKommo ? () => _controller.RetryKommoForCallAsync(call) : null);
            }
            details.CallBackRequested += number =>
            {
                ViewModel.PhoneNumber = number;
                ShowView("DialerView");
                _ = InitiateCallAsync();
            };
            details.Show(this);
        }

        private void CallFromHistoryButton_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is string number && !string.IsNullOrWhiteSpace(number))
            {
                ViewModel.PhoneNumber = number;
                _ = InitiateCallAsync();
            }
        }

        private void ClearHistoryFilter_Click(object? sender, RoutedEventArgs e) => _controller.ClearHistoryFilter();

        private async void ClearHistoryButton_Click(object? sender, RoutedEventArgs e)
        {
            // Destructive: confirm first.
            var confirm = new Window
            {
                Title = "Clear history",
                Width = 380, Height = 170, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            PlatformWindowChrome.Apply(confirm);
            bool ok = false;
            var yes = new Button { Content = "Clear", Padding = new global::Avalonia.Thickness(16, 8) };
            var no = new Button { Content = "Cancel", Padding = new global::Avalonia.Thickness(16, 8) };
            yes.Click += (_, _) => { ok = true; confirm.Close(); };
            no.Click += (_, _) => confirm.Close();
            confirm.Content = new StackPanel
            {
                Margin = new global::Avalonia.Thickness(24), Spacing = 16,
                Children =
                {
                    new TextBlock { Text = "Remove all call history entries? This cannot be undone.", TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, Children = { no, yes } },
                }
            };
            await confirm.ShowDialog(this);
            if (ok) _controller.ClearHistory();
        }

        // ══════════════════════════ statistics ══════════════════════════

        private void StatisticsFilter_Changed(object? sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _controller.RefreshStatistics();
        }

        private void StatisticsCustomDatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _controller.RefreshStatistics();
        }

        private void StatisticsKpi_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if ((sender as Border)?.Tag is not string filter) return;
            var drill = filter switch
            {
                "Answered" => CallStatisticsDrillDown.Answered,
                "Unanswered" => CallStatisticsDrillDown.Unanswered,
                "Missed" => CallStatisticsDrillDown.Missed,
                _ => CallStatisticsDrillDown.All,
            };
            _controller.SetHistoryDrillDown(drill);
            ShowView("HistoryView");
        }

        private void StatisticsTopNumber_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            string? number = (sender as Border)?.Tag as string
                ?? ((sender as Border)?.DataContext as StatsRow)?.PhoneNumber;
            if (string.IsNullOrWhiteSpace(number)) return;
            _controller.SetHistoryDrillDown(CallStatisticsDrillDown.All, number);
            ShowView("HistoryView");
        }

        private void StatisticsExportCsvButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var path = _controller.ExportStatisticsCsv();
                ShowMessage("Export CSV", string.IsNullOrEmpty(path) ? "Nothing to export." : $"Saved to:\n{path}");
            }
            catch (Exception ex)
            {
                ShowMessage("Export CSV", $"Export failed: {ex.Message}");
            }
        }

        // ══════════════════════════ lifecycle ══════════════════════════

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (_closingForShutdown) return;
            _closingForShutdown = true;
            try { _activeCallWindow?.Close(); } catch { }
            try { _settingsWindow?.Close(); } catch { }
            try { _logWindow?.Close(); } catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { _webRtcHost?.Dispose(); } catch { }
            _webRtcHost = null;
            AppLog.Log("[MainWindow Avalonia] Closed");
        }
    }
}
