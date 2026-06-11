#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using System.Globalization;
using System.Windows.Data;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentIcons.Common;

namespace Softphone
{
    public partial class SettingsWindow : Window
    {
        private bool _suppressRecordingToggleEvent = false;
        private bool _suppressSecondaryTransportPersist;
        private CancellationTokenSource? _secondaryTransportReinitCts;
        private WebRtcStatusService? _webRtcStatusService;
        private static WebRtcStatusService? _sharedWebRtcStatusService; // Общий экземпляр для всех окон

        private string? _primaryConnectionIssueDetail;
        private string? _secondaryConnectionIssueDetail;

        private CancellationTokenSource? _mainTurnStatusCts;
        private CancellationTokenSource? _secondaryTurnStatusCts;

        /// <summary>True when /api/kommo/status reports enabled on PBX Gateway.</summary>
        private bool _gatewayKommoModuleActive;
        private bool _suppressKommoSourceUiEvents;

        /// <summary>
        /// Получает или создает общий экземпляр WebRTC сервиса
        /// </summary>
        private WebRtcStatusService GetOrCreateSharedWebRtcService()
        {
            // Сначала пытаемся получить из MainWindow
            if (_sharedWebRtcStatusService == null)
            {
                _sharedWebRtcStatusService = MainWindow.GetSharedWebRtcStatusService();
            }
            
            // Если все еще null, создаем новый
            if (_sharedWebRtcStatusService == null)
            {
                _sharedWebRtcStatusService = new WebRtcStatusService();
                _sharedWebRtcStatusService.AttachWebView(WebRtcStatusWebView);
                _sharedWebRtcStatusService.SetParentWindow(this);
                MainWindow.SetSharedWebRtcStatusService(_sharedWebRtcStatusService);
            }
            else
            {
                // Если сервис уже существует, прикрепляем WebView2 из SettingsWindow
                _sharedWebRtcStatusService.AttachWebView(WebRtcStatusWebView);
                _sharedWebRtcStatusService.SetParentWindow(this);
            }
            
            return _sharedWebRtcStatusService;
        }

        public SettingsWindow()
        {
            InitializeComponent();

            // Centralized Win10/Wpf vs Win11/DWM native appearance.
            NativeWindowAppearanceManager.Attach(this);
            
            // Убеждаемся, что поля доступны для редактирования
            if (SipServerTextBox != null)
            {
                SipServerTextBox.IsReadOnly = false;
                SipServerTextBox.IsEnabled = true;
                SipServerTextBox.Focusable = true;
            }
            if (SipPortTextBox != null)
            {
                SipPortTextBox.IsReadOnly = false;
                SipPortTextBox.IsEnabled = true;
                SipPortTextBox.Focusable = true;
            }
            if (SipUsernameTextBox != null)
            {
                SipUsernameTextBox.IsReadOnly = false;
                SipUsernameTextBox.IsEnabled = true;
                SipUsernameTextBox.Focusable = true;
            }
            if (SipPasswordBox != null)
            {
                SipPasswordBox.IsEnabled = true;
                SipPasswordBox.Focusable = true;
            }

            HookTurnStatusHandlers();
            
            ShowView(ConnectionSettingsView);
            
            // Откладываем загрузку аудиоустройств до перехода на вкладку Audio
            // LoadAudioDevices() вызывается только при клике на AudioButton
            
            // Откладываем инициализацию WebRTC до перехода на вкладку Advanced
            // WebRTC инициализируется только при клике на AdvancedButton
            
            // Подписываемся на события статуса из MainWindow после загрузки окна
            Loaded += SettingsWindow_Loaded;
            Activated += SettingsWindow_Activated;
            
            // Выделяем кнопку Connection при загрузке окна
            Loaded += (s, e) =>
            {
                UpdateButtonSelection(ConnectionButton);
            };
            
            // Обновляем статус WebRTC при изменении чекбокса (только если WebRTC уже инициализирован)
            UseWebRtcCheckBox.Checked += (s, e) => 
            {
                if (_sharedWebRtcStatusService != null)
                {
                    UpdateWebRtcStatus();
                }
                TestWebRtcConnectionButton.IsEnabled = true;
            };
            UseWebRtcCheckBox.Unchecked += (s, e) => 
            {
                if (_sharedWebRtcStatusService != null)
                {
                    UpdateWebRtcStatus();
                }
                TestWebRtcConnectionButton.IsEnabled = false;

                // IMPORTANT: Call recording is available only in WebRTC mode.
                // If WebRTC is turned off, force-disable call recording immediately and persist it.
                DisableCallRecordingBecauseWebRtcIsOff();
            };
            WebRtcWsUriTextBox.TextChanged += (s, e) => 
            {
                // Если поле пустое или не начинается с "wss://" или "ws://", предзаполняем "wss://"
                if (WebRtcWsUriTextBox != null)
                {
                    string currentText = WebRtcWsUriTextBox.Text ?? "";
                    if (string.IsNullOrWhiteSpace(currentText))
                    {
                        WebRtcWsUriTextBox.Text = "wss://";
                        WebRtcWsUriTextBox.CaretIndex = WebRtcWsUriTextBox.Text.Length; // Устанавливаем курсор в конец
                    }
                    else if (!currentText.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) && 
                             !currentText.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
                    {
                        // Если текст не начинается с протокола, добавляем "wss://"
                        int caretPos = WebRtcWsUriTextBox.CaretIndex;
                        WebRtcWsUriTextBox.Text = "wss://" + currentText;
                        WebRtcWsUriTextBox.CaretIndex = Math.Min(caretPos + 6, WebRtcWsUriTextBox.Text.Length); // Сохраняем позицию курсора
                    }
                }
                
                if (_sharedWebRtcStatusService != null)
                {
                    UpdateWebRtcStatus();
                }
            };
            
            // Обработка потери фокуса - если поле пустое, предзаполняем "wss://"
            WebRtcWsUriTextBox.LostFocus += (s, e) =>
            {
                if (WebRtcWsUriTextBox != null)
                {
                    string currentText = WebRtcWsUriTextBox.Text?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(currentText))
                    {
                        WebRtcWsUriTextBox.Text = "wss://";
                    }
                }
            };

            // Initialize TURN status UI once we have loaded settings into the fields.
            Loaded += (_, __) =>
            {
                ScheduleTurnStatusCheck(isMain: true);
            };

            // Auto-prefill "wss://" for the per-connection WebSocket URI fields.
            HookWsUriPrefill(MainWsUriTextBox);
            HookWsUriPrefill(SecondaryWsUriTextBox);
            if (MainWsUriTextBox != null)
            {
                MainWsUriTextBox.TextChanged += (_, __) => UpdateMainWebRtcStatusText();
            }
            if (MainWebRtcUsernameTextBox != null)
            {
                MainWebRtcUsernameTextBox.TextChanged += (_, __) => UpdateMainWebRtcStatusText();
            }
        }

        private static void HookWsUriPrefill(System.Windows.Controls.TextBox? tb)
        {
            if (tb == null) return;
            tb.TextChanged += (s, e) =>
            {
                string currentText = tb.Text ?? "";
                if (string.IsNullOrWhiteSpace(currentText))
                {
                    tb.Text = "wss://";
                    tb.CaretIndex = tb.Text.Length;
                }
                else if (!currentText.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) &&
                         !currentText.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
                {
                    int caretPos = tb.CaretIndex;
                    tb.Text = "wss://" + currentText;
                    tb.CaretIndex = Math.Min(caretPos + 6, tb.Text.Length);
                }
            };
            tb.LostFocus += (s, e) =>
            {
                string currentText = tb.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(currentText))
                {
                    tb.Text = "wss://";
                }
            };
        }

        private void HookTurnStatusHandlers()
        {
            if (MainTurnUriTextBox != null) MainTurnUriTextBox.TextChanged += (_, __) => ScheduleTurnStatusCheck(isMain: true);
            if (MainTurnUsernameTextBox != null) MainTurnUsernameTextBox.TextChanged += (_, __) => ScheduleTurnStatusCheck(isMain: true);
            if (MainTurnPasswordBox != null) MainTurnPasswordBox.PasswordChanged += (_, __) => ScheduleTurnStatusCheck(isMain: true);
            if (MainTurnPortTextBox != null) MainTurnPortTextBox.TextChanged += (_, __) => ScheduleTurnStatusCheck(isMain: true);
            if (MainTurnTransportComboBox != null) MainTurnTransportComboBox.SelectionChanged += (_, __) => ScheduleTurnStatusCheck(isMain: true);
            if (MainTurnTlsCheckBox != null) MainTurnTlsCheckBox.Checked += (_, __) => ScheduleTurnStatusCheck(isMain: true);
            if (MainTurnTlsCheckBox != null) MainTurnTlsCheckBox.Unchecked += (_, __) => ScheduleTurnStatusCheck(isMain: true);
        }

        private void ScheduleTurnStatusCheck(bool isMain)
        {
            try
            {
                var cts = new CancellationTokenSource();
                if (isMain)
                {
                    _mainTurnStatusCts?.Cancel();
                    _mainTurnStatusCts = cts;
                }
                else
                {
                    _secondaryTurnStatusCts?.Cancel();
                    _secondaryTurnStatusCts = cts;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(400, cts.Token);
                        if (cts.Token.IsCancellationRequested) return;

                        // TURN status is only supported for Primary (WebRTC) connection.
                        if (isMain)
                        {
                            var builtUri = await Dispatcher.InvokeAsync(() => BuildTurnUriFromUi(isMain: true));
                            MainWindow.Log($"[SettingsWindow][TURN] ScheduleTurnStatusCheck: builtUri='{builtUri ?? "<null>"}'");
                            await UpdateTurnStatusAsync(isMain: true, builtUri, cts.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[SettingsWindow][TURN] ScheduleTurnStatusCheck error: {ex.Message}");
                    }
                }, cts.Token);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow][TURN] ScheduleTurnStatusCheck setup error: {ex.Message}");
            }
        }

        private string? BuildTurnUriFromUi(bool isMain)
        {
            if (!isMain) return null; // TURN is supported only for Primary (WebRTC) connection.

            string? input = MainTurnUriTextBox?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                MainWindow.Log("[SettingsWindow][TURN] BuildTurnUriFromUi: empty input");
                return null;
            }

            string portText = MainTurnPortTextBox?.Text?.Trim() ?? "";
            int port = 3478;
            if (!string.IsNullOrWhiteSpace(portText) && int.TryParse(portText, out var p) && p >= 1 && p <= 65535)
                port = p;

            string transport = GetTurnTransportFromUi(isMain: true);

            // NOTE: System.Uri does not parse "turn:host:3478?transport=udp" correctly
            // (unknown scheme -> "host" becomes path, Host is empty). Parse manually.
            var parsed = ParseTurnLike(input);
            if (string.IsNullOrWhiteSpace(parsed.Host))
            {
                MainWindow.Log($"[SettingsWindow][TURN] BuildTurnUriFromUi: parsed host is empty for input='{input}'");
                return null;
            }

            bool tls = GetTurnTlsFromUi(isMain: true) || parsed.Scheme == "turns" || port == 5349;
            string scheme = tls ? "turns" : "turn";
            string built = $"{scheme}:{parsed.Host}:{port}?transport={transport}";
            MainWindow.Log($"[SettingsWindow][TURN] BuildTurnUriFromUi: input='{input}', built='{built}', tls={tls}, transport={transport}");
            return built;
        }

        private sealed class TurnLikeParsed
        {
            public string Scheme { get; init; } = ""; // "turn" | "turns" | ""
            public string Host { get; init; } = "";
            public int? Port { get; init; }
            public string? Transport { get; init; } // "udp" | "tcp" | null
        }

        private static TurnLikeParsed ParseTurnLike(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new TurnLikeParsed();

            string raw = input.Trim();
            string scheme = "";
            if (raw.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "turns";
                raw = raw.Substring("turns:".Length);
            }
            else if (raw.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "turn";
                raw = raw.Substring("turn:".Length);
            }

            // Strip leading slashes if user typed turns://host...
            raw = raw.TrimStart('/');

            // Split query
            string query = "";
            int qIdx = raw.IndexOf("?", StringComparison.Ordinal);
            if (qIdx >= 0)
            {
                query = raw.Substring(qIdx + 1);
                raw = raw.Substring(0, qIdx);
            }

            // Trim any path
            int slashIdx = raw.IndexOf("/", StringComparison.Ordinal);
            if (slashIdx >= 0) raw = raw.Substring(0, slashIdx);

            // Drop userinfo if present
            int atIdx = raw.LastIndexOf("@", StringComparison.Ordinal);
            if (atIdx >= 0) raw = raw.Substring(atIdx + 1);

            raw = raw.Trim();

            // Parse transport=...
            string? transport = null;
            try
            {
                foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length == 2 && kv[0].Equals("transport", StringComparison.OrdinalIgnoreCase))
                    {
                        var v = kv[1].Trim().ToLowerInvariant();
                        if (v == "udp" || v == "tcp") transport = v;
                        break;
                    }
                }
            }
            catch { }

            // Parse host[:port] (handle IPv6 in [::1]:3478)
            string host = raw;
            int? port = null;
            if (host.StartsWith("[", StringComparison.Ordinal))
            {
                int close = host.IndexOf("]", StringComparison.Ordinal);
                if (close > 0)
                {
                    string inside = host.Substring(1, close - 1);
                    string rest = host.Substring(close + 1);
                    host = inside;
                    if (rest.StartsWith(":", StringComparison.Ordinal) && int.TryParse(rest.Substring(1), out var p))
                        port = p;
                }
            }
            else
            {
                int colon = host.LastIndexOf(":", StringComparison.Ordinal);
                if (colon > 0 && colon < host.Length - 1 && int.TryParse(host.Substring(colon + 1), out var p))
                {
                    port = p;
                    host = host.Substring(0, colon);
                }
            }

            return new TurnLikeParsed
            {
                Scheme = scheme,
                Host = host.Trim(),
                Port = port,
                Transport = transport
            };
        }

        private string GetTurnTransportFromUi(bool isMain)
        {
            var combo = MainTurnTransportComboBox;
            if (combo?.SelectedItem is ComboBoxItem cbi)
            {
                var v = (cbi.Content?.ToString() ?? "").Trim().ToLowerInvariant();
                if (v == "tcp") return "tcp";
            }
            return "udp";
        }

        private bool GetTurnTlsFromUi(bool isMain)
        {
            return MainTurnTlsCheckBox?.IsChecked == true;
        }

        private async Task UpdateTurnStatusAsync(bool isMain, string? turnUri, CancellationToken ct)
        {
            MainWindow.Log($"[SettingsWindow][TURN] UpdateTurnStatusAsync: uri='{turnUri ?? "<null>"}'");
            await Dispatcher.InvokeAsync(() =>
            {
                SetTurnStatusUi(isMain, string.IsNullOrWhiteSpace(turnUri) ? "Not configured" : "Checking...",
                    string.IsNullOrWhiteSpace(turnUri)
                        ? (Brush)FindResource("TextSecondaryBrush")
                        : (Brush)FindResource("AccentBlueBrush"));
            });

            if (string.IsNullOrWhiteSpace(turnUri))
                return;

            var result = await ProbeTurnAsync(turnUri, ct);
            MainWindow.Log($"[SettingsWindow][TURN] UpdateTurnStatusAsync result: kind={result.Kind}, message='{result.Message ?? ""}'");

            await Dispatcher.InvokeAsync(() =>
            {
                switch (result.Kind)
                {
                    case TurnProbeKind.NotConfigured:
                        SetTurnStatusUi(isMain, "Not configured", (Brush)FindResource("TextSecondaryBrush"));
                        break;
                    case TurnProbeKind.Reachable:
                        SetTurnStatusUi(isMain, result.Message ?? "Reachable", (Brush)FindResource("AccentGreenBrush"));
                        break;
                    default:
                        SetTurnStatusUi(isMain, result.Message ?? "Unreachable", (Brush)FindResource("AccentRedBrush"));
                        break;
                }
            });
        }

        private void SetTurnStatusUi(bool isMain, string text, Brush color)
        {
            if (isMain)
            {
                if (MainTurnStatusTextBlock != null) MainTurnStatusTextBlock.Text = text;
                if (MainTurnStatusTextBlock != null) MainTurnStatusTextBlock.Foreground = color;
                if (MainTurnStatusDot != null) MainTurnStatusDot.Fill = color;
            }
        }

        private enum TurnProbeKind { NotConfigured, Reachable, Unreachable }

        private sealed class TurnProbeResult
        {
            public TurnProbeKind Kind { get; init; }
            public string? Message { get; init; }
        }

        private static TurnProbeResult NotConfigured() => new TurnProbeResult { Kind = TurnProbeKind.NotConfigured };
        private static TurnProbeResult Reachable(string? message = null) => new TurnProbeResult { Kind = TurnProbeKind.Reachable, Message = message };
        private static TurnProbeResult Unreachable(string message) => new TurnProbeResult { Kind = TurnProbeKind.Unreachable, Message = message };

        private async Task<TurnProbeResult> ProbeTurnAsync(string? turnUri, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(turnUri))
                return NotConfigured();

            var parsed = ParseTurnLike(turnUri);
            if (string.IsNullOrWhiteSpace(parsed.Host))
                return Unreachable("Invalid URL");

            bool tls = parsed.Scheme == "turns";
            int port = parsed.Port ?? (tls ? 5349 : 3478);
            string transport = parsed.Transport ?? "";

            bool tcpProbe = tls || transport == "tcp" || transport == "";

            // DNS resolve (common failure mode)
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(parsed.Host);
                if (addresses == null || addresses.Length == 0)
                    return Unreachable("DNS failed");
            }
            catch
            {
                return Unreachable("DNS failed");
            }

            if (!tcpProbe && transport == "udp")
            {
                // UDP probing would require a TURN/STUN handshake; keep it best-effort.
                return Reachable("Configured (UDP)");
            }

            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(parsed.Host, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(2.5), ct));
                if (completed != connectTask)
                    return Unreachable("Timeout");

                await connectTask; // propagate exception if any
                return Reachable("Reachable");
            }
            catch (OperationCanceledException)
            {
                return Unreachable("Cancelled");
            }
            catch
            {
                return Unreachable("Unreachable");
            }
        }

        private void ApplyTurnUiFromStoredUri(bool isMain, string? storedUri)
        {
            if (!isMain) return;
            if (string.IsNullOrWhiteSpace(storedUri))
                return;

            var parsed = ParseTurnLike(storedUri);

            // Port
            int port = parsed.Port ?? (parsed.Scheme == "turns" ? 5349 : 3478);
            var portBox = MainTurnPortTextBox;
            if (portBox != null) portBox.Text = port.ToString();

            // Transport query
            string transport = parsed.Transport ?? "udp";

            var combo = MainTurnTransportComboBox;
            if (combo != null)
            {
                // turns: implies TLS-over-TCP
                bool tcp = transport == "tcp" || parsed.Scheme == "turns";
                combo.SelectedIndex = tcp ? 1 : 0;
            }

            // TLS scheme
            var tlsCb = MainTurnTlsCheckBox;
            if (tlsCb != null)
            {
                tlsCb.IsChecked = parsed.Scheme == "turns";
            }
        }

        private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateConnectionStatus();
            UpdateConnection2Status(); // Обновляем статус второго подключения при загрузке

            // Owner часто не задан (немодальный Show без Owner) — берём главное приложения
            if (TryGetMainWindow() is MainWindow mainWindow)
            {
                mainWindow.OnConnectionStatusChanged += UpdateConnectionStatusFromMainWindow;

                // Подписываемся на события WebRTC для динамического обновления статуса
                if (WebRtcService.Main != null)
                {
                    WebRtcService.Main.Event += OnWebRtcEvent;
                }
                if (WebRtcService.Secondary != null)
                {
                    WebRtcService.Secondary.Event += OnWebRtcEvent;
                }
            }

            // Загружаем настройки после полной загрузки окна
            // Это гарантирует, что все UI элементы инициализированы
            LoadSettings();

            // Gateway Kommo status is prefetched on app startup — apply cache so Connection source
            // is visible on Integrations without waiting for a manual tab refresh.
            SyncKommoGatewayModuleFromMain();
        }

        /// <summary>
        /// Главное окно: <see cref="OpenSettingsWindow"/> открывает настройки немодально и не задаёт <see cref="Window.Owner"/>,
        /// поэтому ориентироваться только на Owner нельзя — статус и подписки «молча» ломались.
        /// </summary>
        private MainWindow? TryGetMainWindow()
        {
            if (Owner is MainWindow owned)
                return owned;
            if (Application.Current?.MainWindow is MainWindow appMain)
                return appMain;
            return Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
        }
        
        private void SettingsWindow_Activated(object? sender, EventArgs e)
        {
            // Обновляем статус подключения при активации окна
            // Это гарантирует, что статус всегда актуален, когда пользователь открывает настройки
            UpdateConnectionStatus();
            UpdateConnection2Status();
        }
        
        private void OnWebRtcEvent(WebRtcEventDto dto)
        {
            // Обновляем статусы при событиях WebRTC (registered, ws_connected, etc.)
            // Синхронизируем все три места: MainWindow, Connection tab, Advanced tab
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Обновляем статус в Connection tab (синхронизируется с MainWindow)
                UpdateConnectionStatus();
                UpdateConnection2Status();
                
                // Обновляем статус в Advanced tab (WebRTC Status) при важных событиях
                if (dto.Type == "registered" || dto.Type == "ua_registered" || 
                    dto.Type == "reg_failed" || dto.Type == "ua_registration_failed" ||
                    dto.Type == "ua_connected" || dto.Type == "ws_connected" ||
                    dto.Type == "unregistered" || dto.Type == "ua_unregistered")
                {
                    // Обновляем WebRTC статус в Advanced tab через WebRtcStatusService
                    if (_sharedWebRtcStatusService != null)
                    {
                        var currentStatus = _sharedWebRtcStatusService.CurrentStatus;
                        UpdateWebRtcStatusDisplay(currentStatus);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private void UpdateConnectionStatus()
        {
            if (TryGetMainWindow() is MainWindow mainWindow)
            {
                bool isConnected = mainWindow.IsConnected;
                string mainWindowStatus = mainWindow.ConnectionStatus ?? "";
                TryExtractPrimaryConnectionIssue(mainWindowStatus, isConnected, out string? issueDetail);
                ApplyPrimaryConnectionStatusUi(isConnected, issueDetail);
            }
            else
            {
                if (ConnectionStatusTextBlock != null)
                {
                    ConnectionStatusTextBlock.Text = "Disconnected";
                    ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                }
                if (PrimaryConnectionStatusPill != null)
                    PrimaryConnectionStatusPill.Background = (System.Windows.Media.Brush)FindResource("ConnStatusPillIdleBrush");
                if (PrimaryConnectionStatusDot != null)
                    PrimaryConnectionStatusDot.Fill = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                if (PrimaryConnectionIssueButton != null)
                    PrimaryConnectionIssueButton.Visibility = Visibility.Collapsed;
                _primaryConnectionIssueDetail = null;
            }
        }

        private void ApplyPrimaryConnectionStatusUi(bool isConnected, string? issueDetail)
        {
            _primaryConnectionIssueDetail = issueDetail;
            if (ConnectionStatusTextBlock == null)
                return;

            Brush okPill = (Brush)FindResource("ConnStatusPillOkBrush");
            Brush idlePill = (Brush)FindResource("ConnStatusPillIdleBrush");
            Brush green = (Brush)FindResource("AccentGreenBrush");
            Brush dim = (Brush)FindResource("TextSecondaryBrush");

            if (isConnected)
            {
                ConnectionStatusTextBlock.Text = "Connected";
                ConnectionStatusTextBlock.Foreground = green;
                if (PrimaryConnectionStatusPill != null)
                    PrimaryConnectionStatusPill.Background = okPill;
                if (PrimaryConnectionStatusDot != null)
                    PrimaryConnectionStatusDot.Fill = green;
                if (PrimaryConnectionIssueButton != null)
                    PrimaryConnectionIssueButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ConnectionStatusTextBlock.Text = "Disconnected";
                ConnectionStatusTextBlock.Foreground = dim;
                if (PrimaryConnectionStatusPill != null)
                    PrimaryConnectionStatusPill.Background = idlePill;
                if (PrimaryConnectionStatusDot != null)
                    PrimaryConnectionStatusDot.Fill = dim;
                if (PrimaryConnectionIssueButton != null)
                {
                    PrimaryConnectionIssueButton.Visibility = string.IsNullOrEmpty(_primaryConnectionIssueDetail)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }
            }
        }

        private static bool IsTransientMainDisconnectedStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return true;
            var t = status.Trim();
            if (t.Contains("Initializing", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Contains("Connecting", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("Connected to WebRTC", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Contains("Reconnecting", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Contains("Settings saved", StringComparison.OrdinalIgnoreCase) &&
                t.Contains("call ends", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool LooksLikePrimaryConnectionIssue(string status)
        {
            var t = status.ToLowerInvariant();
            return t.Contains("registration failed")
                || t.Contains("authentication")
                || t.Contains("forbidden")
                || t.Contains("connection error:")
                || t.Contains("unauthorized")
                || t.Contains("invalid credential")
                || t.Contains("403 forbidden")
                || t.Contains(" 403")
                || t.Contains("401 ")
                || t.Contains("service unavailable")
                || t.Contains("could not resolve");
        }

        private static void TryExtractPrimaryConnectionIssue(string mainWindowStatus, bool isConnected, out string? issueDetail)
        {
            issueDetail = null;
            if (isConnected)
                return;
            if (string.IsNullOrWhiteSpace(mainWindowStatus))
                return;
            var s = mainWindowStatus.Trim();
            if (IsTransientMainDisconnectedStatus(s))
                return;
            if (string.Equals(s, "Not connected", StringComparison.OrdinalIgnoreCase))
                return;
            if (string.Equals(s, "Disconnected", StringComparison.OrdinalIgnoreCase))
                return;
            if (LooksLikePrimaryConnectionIssue(s))
            {
                issueDetail = s;
                return;
            }
        }

        private static bool SecondaryLineIndicatesSuccess(string msg)
        {
            var t = msg.ToLowerInvariant();
            if (t.Contains("registration successful")) return true;
            if (t.Contains("sip transport listening") || t.Contains("listening on")) return true;
            if (t.Contains("registered") && !t.Contains("fail") && !t.Contains("unregistered")) return true;
            return false;
        }

        private static bool SecondaryLineIndicatesProblem(string msg)
        {
            if (SecondaryLineIndicatesSuccess(msg))
                return false;
            var t = msg.ToLowerInvariant();
            return t.Contains("fail") || t.Contains("403") || t.Contains("401") || t.Contains("forbidden")
                || t.Contains("denied") || t.Contains("timeout")
                || t.Contains("could not resolve");
        }

        private void PrimaryConnectionIssueButton_Click(object sender, RoutedEventArgs e)
        {
            if (PrimaryConnectionIssueDetailText != null)
            {
                PrimaryConnectionIssueDetailText.Text = string.IsNullOrWhiteSpace(_primaryConnectionIssueDetail)
                    ? "No additional details."
                    : _primaryConnectionIssueDetail;
            }
            PrimaryConnectionIssuePopup.IsOpen = true;
        }

        private void SecondaryConnectionIssueButton_Click(object sender, RoutedEventArgs e)
        {
            if (SecondaryConnectionIssueDetailText != null)
            {
                SecondaryConnectionIssueDetailText.Text = string.IsNullOrWhiteSpace(_secondaryConnectionIssueDetail)
                    ? "No additional details."
                    : _secondaryConnectionIssueDetail;
            }
            SecondaryConnectionIssuePopup.IsOpen = true;
        }

        private void UpdateConnectionStatusFromMainWindow(string status)
        {
            Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrEmpty(status) && status.StartsWith("[Connection2]", StringComparison.OrdinalIgnoreCase))
                {
                    var msg = status.Substring("[Connection2]".Length).Trim();
                    if (SecondaryLineIndicatesSuccess(msg))
                        _secondaryConnectionIssueDetail = null;
                    else if (SecondaryLineIndicatesProblem(msg))
                        _secondaryConnectionIssueDetail = msg;
                }
                UpdateConnectionStatus();
                UpdateConnection2Status();
            });
        }
        
        private string? FormatStatusMessage(string technicalStatus)
        {
            if (string.IsNullOrEmpty(technicalStatus))
                return "Not connected";

            // Преобразуем технические сообщения в понятные для пользователя
            string status = technicalStatus.ToLower();

            // Статусы подключения к серверу
            if (status.Contains("registration successful"))
                return "Connected to server";
            
            if (status.Contains("registration failed") || status.Contains("registration temporary failure"))
            {
                // Извлекаем причину ошибки, если есть
                if (status.Contains("could not resolve"))
                    return "Connection failed: Cannot reach server";
                if (status.Contains("timeout"))
                    return "Connection failed: Timeout";
                if (status.Contains("unauthorized") || status.Contains("401"))
                    return "Connection failed: Invalid credentials";
                return "Connection failed";
            }
            
            if (status.Contains("registration removed"))
                return "Disconnected from server";
            
            if (status.Contains("registering on sip server") || status.Contains("initializing sip"))
                return "Connecting to server...";
            
            if (status.Contains("sip transport listening"))
                return "Starting connection...";
            
            // Фильтруем все технические SIP сообщения
            if (status.Contains("responded to options") ||
                status.Contains("sip response:") || 
                status.Contains("sip request:") ||
                status.Contains("invite") ||
                status.Contains("200 ok") ||
                status.Contains("call"))
            {
                // Не обновляем статус для технических сообщений
                return null;
            }
            
            return null;
        }

        private void ShowView(FrameworkElement view)
        {
            ConnectionSettingsView.Visibility = Visibility.Collapsed;
            AudioSettingsView.Visibility = Visibility.Collapsed;
            GeneralSettingsView.Visibility = Visibility.Collapsed;
            AppearanceSettingsView.Visibility = Visibility.Collapsed;
            AdvancedSettingsView.Visibility = Visibility.Collapsed;
            IntegrationsSettingsView.Visibility = Visibility.Collapsed;
            AboutSettingsView.Visibility = Visibility.Collapsed;

            view.Visibility = Visibility.Visible;
            
            // Загружаем настройки при переключении на Advanced
            if (view == AdvancedSettingsView)
            {
                LoadGeneralSettings();
                
                // Сначала пытаемся получить сервис из MainWindow, если он еще не инициализирован локально
                if (_sharedWebRtcStatusService == null)
                {
                    _sharedWebRtcStatusService = MainWindow.GetSharedWebRtcStatusService();
                }
                
                // Если WebRTC сервис уже инициализирован, восстанавливаем текущий статус сразу
                if (_sharedWebRtcStatusService != null)
                {
                    // Восстанавливаем текущий статус из сервиса синхронно (без задержки)
                    var currentStatus = _sharedWebRtcStatusService.CurrentStatus;
                    
                    // Обновляем статус сразу, до подписки на события
                    UpdateWebRtcStatusDisplay(currentStatus);
                    
                    // Подписываемся на изменения статуса, если еще не подписаны
                    if (_webRtcStatusService == null)
                    {
                        _webRtcStatusService = _sharedWebRtcStatusService;
                        _webRtcStatusService.OnStatusChanged += UpdateWebRtcStatusDisplay;
                    }
                    
                    // НЕ запускаем автоматическую проверку при открытии вкладки
                    // Проверка запускается только по кнопке "Test Connection"
                    MainWindow.Log($"[SettingsWindow] ShowView: Restored WebRTC status: {currentStatus}");
                }
                else
                {
                    // Если сервис не инициализирован, проверяем настройки и показываем соответствующий статус
                    bool useWebRtc = UseWebRtcCheckBox?.IsChecked ?? false;
                    string wsUri = WebRtcWsUriTextBox?.Text?.Trim() ?? "";
                    
                    if (!useWebRtc)
                    {
                        WebRtcStatusTextBlock.Text = "WebRTC Status: Disabled";
                        WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                    }
                    else if (string.IsNullOrWhiteSpace(wsUri) || wsUri == "wss://")
                    {
                        WebRtcStatusTextBlock.Text = "WebRTC Status: Not configured (WebSocket URI missing or invalid)";
                        WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    }
                    else
                    {
                        // Если WebRTC включен и URI указан, но сервис еще не создан,
                        // возможно подключение еще инициализируется при старте приложения
                        // Показываем "Connecting..." вместо "Not configured"
                        WebRtcStatusTextBlock.Text = "WebRTC Status: Connecting...";
                        WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    }
                }
            }
        }
        
        private void UpdateButtonSelection(Button selectedButton)
        {
            // FindResource бросает исключение, если ресурс не успел инициализироваться (например, при открытии About/Updates).
            // TryFindResource позволяет отрисовать окно даже при проблемах со словарями темы.
            var textSecondary = TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush
                                ?? Application.Current?.TryFindResource("TextSecondaryBrush") as System.Windows.Media.Brush
                                ?? System.Windows.Media.Brushes.Gray;
            var accentBlue = TryFindResource("AccentBlueBrush") as System.Windows.Media.Brush
                              ?? Application.Current?.TryFindResource("AccentBlueBrush") as System.Windows.Media.Brush
                              ?? System.Windows.Media.Brushes.DodgerBlue;

            // Сбрасываем выделение всех кнопок
            ConnectionButton.Background = System.Windows.Media.Brushes.Transparent;
            ConnectionButton.Foreground = textSecondary;
            AudioButton.Background = System.Windows.Media.Brushes.Transparent;
            AudioButton.Foreground = textSecondary;
            GeneralButton.Background = System.Windows.Media.Brushes.Transparent;
            GeneralButton.Foreground = textSecondary;
            AppearanceButton.Background = System.Windows.Media.Brushes.Transparent;
            AppearanceButton.Foreground = textSecondary;
            AdvancedButton.Background = System.Windows.Media.Brushes.Transparent;
            AdvancedButton.Foreground = textSecondary;
            IntegrationsButton.Background = System.Windows.Media.Brushes.Transparent;
            IntegrationsButton.Foreground = textSecondary;
            AboutButton.Background = System.Windows.Media.Brushes.Transparent;
            AboutButton.Foreground = textSecondary;

            // Выделяем выбранную кнопку
            if (selectedButton != null)
            {
                selectedButton.Background = accentBlue;
                selectedButton.Foreground = System.Windows.Media.Brushes.White;
            }
        }

        private void ConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(ConnectionButton);
            ShowView(ConnectionSettingsView);
            // Обновляем статусы подключений при переключении на вкладку Connection
            UpdateConnectionStatus();
            UpdateConnection2Status();
        }

        private void AudioButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(AudioButton);
            ShowView(AudioSettingsView);
            // Загружаем аудиоустройства асинхронно только при первом переходе на вкладку Audio
            if (MicrophoneComboBox.ItemsSource == null || MicrophoneComboBox.Items.Count == 0)
            {
                _ = LoadAudioDevicesAsync();
            }
        }

        private void GeneralButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(GeneralButton);
            ShowView(GeneralSettingsView);
            LoadGeneralSettingsForGeneralTab();
        }

        private void AppearanceButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(AppearanceButton);
            ShowView(AppearanceSettingsView);
            LoadAppearanceSettings();
        }
        
        private void LoadGeneralSettingsForGeneralTab()
        {
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    if (settings != null)
                    {
                        // Загружаем настройки записи звонков
                        if (EnableCallRecordingCheckBox != null)
                        {
                            _suppressRecordingToggleEvent = true;
                            EnableCallRecordingCheckBox.IsChecked = settings.EnableCallRecording;
                            UpdateCallRecordingToggleColor();
                            _suppressRecordingToggleEvent = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadGeneralSettingsForGeneralTab: Error - {ex.Message}");
            }
        }
        
        private void LoadIntegrationsSettings()
        {
            try
            {
                SyncKommoGatewayModuleFromMain(refreshFromGateway: false);

                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    if (settings != null)
                    {
                        // Загружаем настройки Kommo
                        if (EnableAmoCrmIntegrationCheckBox != null)
                        {
                            EnableAmoCrmIntegrationCheckBox.IsChecked = settings.EnableAmoCrmIntegration;
                            UpdateAmoCrmSettingsVisibility(settings.EnableAmoCrmIntegration);
                        }
                        if (EnableAmoCrmLeadSelectionCheckBox != null)
                        {
                            EnableAmoCrmLeadSelectionCheckBox.IsChecked = settings.EnableAmoCrmLeadSelection;
                        }
                        
                        if (AmoCrmSubdomainTextBox != null && !string.IsNullOrEmpty(settings.AmoCrmSubdomain))
                        {
                            AmoCrmSubdomainTextBox.Text = settings.AmoCrmSubdomain;
                        }
                        
                        // Загружаем режим аутентификации
                        string authMode = settings.AmoCrmAuthMode ?? "manual";
                        bool isOAuth = authMode == "oauth";
                        
                        // Обновляем визуальное состояние кнопок сегментированного контрола
                        var accentBlueBrush = (Brush)FindResource("AccentBlueBrush");
                        var textPrimaryBrush = (Brush)FindResource("TextPrimaryBrush");
                        
                        if (AmoCrmAuthModeManualButton != null)
                        {
                            AmoCrmAuthModeManualButton.Tag = isOAuth ? "manual" : "manual_selected";
                            AmoCrmAuthModeManualButton.Background = isOAuth ? Brushes.Transparent : accentBlueBrush;
                            AmoCrmAuthModeManualButton.Foreground = isOAuth ? textPrimaryBrush : Brushes.White;
                        }
                        
                        if (AmoCrmAuthModeOAuthButton != null)
                        {
                            AmoCrmAuthModeOAuthButton.Tag = isOAuth ? "oauth_selected" : "oauth";
                            AmoCrmAuthModeOAuthButton.Background = isOAuth ? accentBlueBrush : Brushes.Transparent;
                            AmoCrmAuthModeOAuthButton.Foreground = isOAuth ? Brushes.White : textPrimaryBrush;
                        }
                        
                        UpdateAmoCrmAuthModeVisibility(isOAuth);
                        
                        // Загружаем Manual Token настройки
                        if (AmoCrmAccessTokenPasswordBox != null && !string.IsNullOrEmpty(settings.AmoCrmAccessTokenEncrypted))
                        {
                            // Расшифровываем токен для отображения (только если он есть)
                            string decryptedToken = TokenEncryption.Decrypt(settings.AmoCrmAccessTokenEncrypted);
                            if (!string.IsNullOrEmpty(decryptedToken))
                            {
                                AmoCrmAccessTokenPasswordBox.Password = decryptedToken;
                            }
                        }
                        
                        // Загружаем OAuth настройки
                        if (AmoCrmClientIdTextBox != null && !string.IsNullOrEmpty(settings.AmoCrmClientId))
                        {
                            AmoCrmClientIdTextBox.Text = settings.AmoCrmClientId;
                        }
                        if (AmoCrmClientSecretPasswordBox != null && !string.IsNullOrEmpty(settings.AmoCrmClientSecretEncrypted))
                        {
                            string decryptedSecret = TokenEncryption.Decrypt(settings.AmoCrmClientSecretEncrypted);
                            if (!string.IsNullOrEmpty(decryptedSecret))
                            {
                                AmoCrmClientSecretPasswordBox.Password = decryptedSecret;
                            }
                        }
                        if (AmoCrmRedirectUriTextBox != null)
                        {
                            AmoCrmRedirectUriTextBox.Text = settings.AmoCrmRedirectUri ?? "http://localhost:8080/callback";
                        }
                        
                        // Обновляем статусы в зависимости от режима
                        // Проверяем реальное состояние сервиса, а не только наличие токенов в настройках
                        // Используем небольшую задержку, чтобы дать время сервису инициализироваться
                        _ = Task.Delay(500).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                CheckAmoCrmConnectionStatus();
                            });
                        }, TaskContinuationOptions.OnlyOnRanToCompletion);

                        // Callspire PBX Gateway settings
                        if (EnableMikoPbxCdrCheckBox != null)
                        {
                            EnableMikoPbxCdrCheckBox.IsChecked = settings.EnableMikoPbxCdr;
                            UpdateMikoPbxCdrSettingsVisibility(settings.EnableMikoPbxCdr);
                        }
                        if (MikoPbxCdrServiceUrlTextBox != null && !string.IsNullOrEmpty(settings.MikoPbxCdrServiceUrl))
                        {
                            MikoPbxCdrServiceUrlTextBox.Text = settings.MikoPbxCdrServiceUrl;
                        }
                        if (MikoPbxCdrExtensionTextBox != null && !string.IsNullOrEmpty(settings.MikoPbxExtension))
                        {
                            MikoPbxCdrExtensionTextBox.Text = settings.MikoPbxExtension;
                        }

                        _ = Task.Delay(600).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                CheckMikoPbxCdrConnectionStatus();
                            });
                        }, TaskContinuationOptions.OnlyOnRanToCompletion);

                        _ = RefreshKommoGatewayModuleStatusAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadIntegrationsSettings: Error - {ex.Message}");
            }
        }
        
        /// <summary>
        /// Проверяет и обновляет статус подключения Kommo
        /// </summary>
        private void CheckAmoCrmConnectionStatus()
        {
            try
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                string authMode = "manual";
                bool isAmoCrmInitialized = false;
                
                try
                {
                    string settingsPath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsPath))
                    {
                        string json = File.ReadAllText(settingsPath);
                        var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                        authMode = settings?.AmoCrmAuthMode ?? "manual";
                        MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: authMode={authMode}, EnableIntegration={settings?.EnableAmoCrmIntegration ?? false}");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: Error reading settings: {ex.Message}");
                }
                
                // КРИТИЧНО: Проверяем реальный статус AmoCRM сервиса, а не WebRTC/SIP подключение
                if (mainWindow != null)
                {
                    isAmoCrmInitialized = mainWindow.IsAmoCrmServiceInitialized();
                    MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: IsAmoCrmServiceInitialized={isAmoCrmInitialized}");
                    
                    // Для OAuth показываем "Authorized/Not authorized", для Manual Token - "Connected/Not connected"
                    if (isAmoCrmInitialized)
                    {
                        string statusText = authMode == "oauth" ? "Authorized" : "Connected";
                        MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: Setting status to '{statusText}' (AmoCRM initialized)");
                        UpdateAmoCrmStatus(statusText, true);
                    }
                    else
                    {
                        string statusText = authMode == "oauth" ? "Not authorized" : "Not connected";
                        MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: Setting status to '{statusText}' (AmoCRM not initialized)");
                        UpdateAmoCrmStatus(statusText, false);
                    }
                }
                else
                {
                    MainWindow.Log("[SettingsWindow] CheckAmoCrmConnectionStatus: MainWindow is null");
                    try
                    {
                        string settingsPath = AppDataHelper.GetSettingsFilePath();
                        if (File.Exists(settingsPath))
                        {
                            string json = File.ReadAllText(settingsPath);
                            var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                            authMode = settings?.AmoCrmAuthMode ?? "manual";
                        }
                    }
                    catch { }
                    string statusText = authMode == "oauth" ? "Not authorized" : "Not connected";
                    UpdateAmoCrmStatus(statusText, false);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: Exception: {ex.Message}");
                string authMode = "manual";
                try
                {
                    string settingsPath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsPath))
                    {
                        string json = File.ReadAllText(settingsPath);
                        var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                        authMode = settings?.AmoCrmAuthMode ?? "manual";
                    }
                }
                catch { }
                string statusText = authMode == "oauth" ? "Not authorized" : "Not connected";
                UpdateAmoCrmStatus(statusText, false);
            }
        }
        
        /// <summary>
        /// Обновляет отображение статуса подключения Kommo (публичный метод для вызова из MainWindow)
        /// </summary>
        public void UpdateAmoCrmStatus(string statusText, bool isConnected)
        {
            if (IsKommoGatewaySourceSelected())
            {
                if (AmoCrmOAuthStatusPanel != null)
                    AmoCrmOAuthStatusPanel.Visibility = Visibility.Collapsed;
                if (AmoCrmManualTokenStatusPanel != null)
                    AmoCrmManualTokenStatusPanel.Visibility = Visibility.Collapsed;
                return;
            }

            // Определяем текущий режим аутентификации из настроек (более надежно, чем из Tag кнопок)
            bool isOAuth = false;
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    isOAuth = settings?.AmoCrmAuthMode == "oauth";
                    MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Determined authMode from settings: {(isOAuth ? "oauth" : "manual")}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Error reading settings, using button tags: {ex.Message}");
                
                // Fallback: проверяем Tag кнопок если настройки не удалось прочитать
                if (AmoCrmAuthModeOAuthButton != null && AmoCrmAuthModeOAuthButton.Tag is string oauthTag)
                {
                    isOAuth = oauthTag == "oauth_selected";
                }
                else if (AmoCrmAuthModeManualButton != null && AmoCrmAuthModeManualButton.Tag is string manualTag)
                {
                    isOAuth = manualTag != "manual_selected";
                }
            }
            
            MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: statusText='{statusText}', isConnected={isConnected}, isOAuth={isOAuth}");
            
            if (isOAuth)
            {
                // Для OAuth режима показываем статус "Authorized/Not authorized"
                // Убеждаемся, что панель OAuth статуса видима
                if (AmoCrmOAuthStatusPanel != null)
                {
                    AmoCrmOAuthStatusPanel.Visibility = Visibility.Visible;
                }
                if (AmoCrmManualTokenStatusPanel != null)
                {
                    AmoCrmManualTokenStatusPanel.Visibility = Visibility.Collapsed;
                }
                
                if (AmoCrmOAuthStatusTextBlock != null)
                {
                    // Обрабатываем как "Authorized", так и "Connected" (для совместимости)
                    if (statusText == "Authorized" || statusText == "Connected")
                    {
                        AmoCrmOAuthStatusTextBlock.Text = "Authorized";
                        AmoCrmOAuthStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                        MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Set OAuth status to 'Authorized' (green)");
                    }
                    else if (statusText == "Connecting..." || statusText == "Authorizing...")
                    {
                        AmoCrmOAuthStatusTextBlock.Text = "Authorizing...";
                        AmoCrmOAuthStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow
                        MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Set OAuth status to 'Authorizing...' (yellow)");
                    }
                    else if (statusText.StartsWith("Error"))
                    {
                        AmoCrmOAuthStatusTextBlock.Text = "Authorization failed";
                        AmoCrmOAuthStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
                        MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Set OAuth status to 'Authorization failed' (red)");
                    }
                    else
                    {
                        AmoCrmOAuthStatusTextBlock.Text = "Not authorized";
                        AmoCrmOAuthStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(106, 106, 106)); // Gray
                        MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Set OAuth status to 'Not authorized' (gray), statusText was: '{statusText}'");
                    }
                }
                else
                {
                    MainWindow.Log("[SettingsWindow] UpdateAmoCrmStatus: AmoCrmOAuthStatusTextBlock is null!");
                }
            }
            else
            {
                // Для Manual Token режима показываем статус "Connected/Not connected"
                // Убеждаемся, что панель Manual Token статуса видима
                if (AmoCrmManualTokenStatusPanel != null)
                {
                    AmoCrmManualTokenStatusPanel.Visibility = Visibility.Visible;
                }
                if (AmoCrmOAuthStatusPanel != null)
                {
                    AmoCrmOAuthStatusPanel.Visibility = Visibility.Collapsed;
                }
                
                if (AmoCrmStatusTextBlock != null)
                {
                    AmoCrmStatusTextBlock.Text = statusText;
                }
                
                if (AmoCrmStatusIndicator != null)
                {
                    // Green - connected, gray - not connected, yellow - connecting...
                    if (statusText == "Connected")
                    {
                        AmoCrmStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                    }
                    else if (statusText == "Connecting...")
                    {
                        AmoCrmStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow
                    }
                    else if (statusText.StartsWith("Error"))
                    {
                        AmoCrmStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
                    }
                    else
                    {
                        AmoCrmStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(106, 106, 106)); // Gray
                    }
                }
            }
        }
        
        private void UpdateAmoCrmSettingsVisibility(bool isEnabled)
        {
            if (AmoCrmConnectionSourcePanel != null)
            {
                AmoCrmConnectionSourcePanel.Visibility =
                    isEnabled && _gatewayKommoModuleActive ? Visibility.Visible : Visibility.Collapsed;
            }

            if (AmoCrmLeadSelectionGrid != null)
            {
                AmoCrmLeadSelectionGrid.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
            }

            if (!isEnabled)
            {
                if (AmoCrmSettingsPanel != null)
                    AmoCrmSettingsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            ApplyKommoConnectionSourcePanels();
        }

        private string GetSelectedKommoConnectionSource()
        {
            if (AmoCrmConnectionSourceGatewayButton?.Tag is string g && g == "gateway_selected")
                return "gateway";
            if (AmoCrmConnectionSourceLocalButton?.Tag is string l && l == "local_selected")
                return "local";
            return AppDataHelper.LoadSettingsOrNew().AmoCrmConnectionSource?.Trim().ToLowerInvariant() ?? "local";
        }

        private bool IsKommoGatewaySourceSelected()
        {
            return _gatewayKommoModuleActive && GetSelectedKommoConnectionSource() == "gateway";
        }

        private void SetKommoConnectionSourceUi(string source, bool persistAndReinit)
        {
            source = (source ?? "").Trim().ToLowerInvariant();
            if (source is not ("gateway" or "local"))
                source = _gatewayKommoModuleActive ? "gateway" : "local";

            _suppressKommoSourceUiEvents = true;
            try
            {
                var accentBlueBrush = (Brush)FindResource("AccentBlueBrush");
                var textPrimaryBrush = (Brush)FindResource("TextPrimaryBrush");
                bool isGateway = source == "gateway";

                if (AmoCrmConnectionSourceGatewayButton != null)
                {
                    AmoCrmConnectionSourceGatewayButton.Tag = isGateway ? "gateway_selected" : "gateway";
                    AmoCrmConnectionSourceGatewayButton.Background = isGateway ? accentBlueBrush : Brushes.Transparent;
                    AmoCrmConnectionSourceGatewayButton.Foreground = isGateway ? Brushes.White : textPrimaryBrush;
                }

                if (AmoCrmConnectionSourceLocalButton != null)
                {
                    AmoCrmConnectionSourceLocalButton.Tag = isGateway ? "local" : "local_selected";
                    AmoCrmConnectionSourceLocalButton.Background = isGateway ? Brushes.Transparent : accentBlueBrush;
                    AmoCrmConnectionSourceLocalButton.Foreground = isGateway ? textPrimaryBrush : Brushes.White;
                }
            }
            finally
            {
                _suppressKommoSourceUiEvents = false;
            }

            ApplyKommoConnectionSourcePanels();

            if (!persistAndReinit)
                return;

            AppDataHelper.SetKommoConnectionSource(source);

            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow != null && EnableAmoCrmIntegrationCheckBox?.IsChecked == true)
            {
                mainWindow.InitializeAmoCrmService(AppDataHelper.LoadSettingsOrNew());
                _ = Task.Delay(1500).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(CheckAmoCrmConnectionStatus);
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }

        private void ApplyKommoConnectionSourcePanels()
        {
            bool integrationEnabled = EnableAmoCrmIntegrationCheckBox?.IsChecked == true;
            if (!integrationEnabled)
                return;

            bool useGateway = IsKommoGatewaySourceSelected();

            if (AmoCrmSettingsPanel != null)
                AmoCrmSettingsPanel.Visibility = useGateway ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AmoCrmConnectionSourceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressKommoSourceUiEvents)
                return;

            if (sender is not Button button || button.Tag is not string tag)
                return;

            string source = tag.StartsWith("gateway", StringComparison.Ordinal) ? "gateway" : "local";
            SetKommoConnectionSourceUi(source, persistAndReinit: true);
        }

        private static bool ComputeKommoGatewayModuleActive(KommoGatewayStatus? status, bool currentActive)
        {
            if (status == null)
                return currentActive;

            if (status.Excluded)
                return false;

            if (status.OfferGateway || status.Enabled)
                return true;

            return false;
        }

        private void ApplyKommoGatewayModuleUi(KommoGatewayStatus? kommoStatus)
        {
            _gatewayKommoModuleActive = ComputeKommoGatewayModuleActive(kommoStatus, _gatewayKommoModuleActive);

            bool integrationEnabled = EnableAmoCrmIntegrationCheckBox?.IsChecked == true;
            if (AmoCrmConnectionSourcePanel != null)
            {
                AmoCrmConnectionSourcePanel.Visibility =
                    integrationEnabled && _gatewayKommoModuleActive ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_gatewayKommoModuleActive)
            {
                var saved = AppDataHelper.LoadSettingsOrNew().AmoCrmConnectionSource?.Trim().ToLowerInvariant();
                string source = saved is "gateway" or "local" ? saved : "gateway";
                SetKommoConnectionSourceUi(source, persistAndReinit: false);
            }
            else
            {
                ApplyKommoConnectionSourcePanels();
                if (AmoCrmSettingsPanel != null && integrationEnabled)
                    AmoCrmSettingsPanel.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Applies gateway Kommo module status cached by MainWindow at startup (if available).
        /// </summary>
        public void SyncKommoGatewayModuleFromMain(bool refreshFromGateway = true)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SyncKommoGatewayModuleFromMain(refreshFromGateway));
                return;
            }

            var status = TryGetMainWindow()?.GetCachedKommoGatewayStatus();
            if (status != null)
                ApplyKommoGatewayModuleUi(status);

            if (refreshFromGateway)
                _ = RefreshKommoGatewayModuleStatusAsync();
        }

        private async Task RefreshKommoGatewayModuleStatusAsync()
        {
            KommoGatewayStatus? kommoStatus = null;

            try
            {
                var settings = AppDataHelper.LoadSettingsOrNew();
                if (settings.EnableMikoPbxCdr
                    && !string.IsNullOrWhiteSpace(settings.MikoPbxCdrServiceUrl)
                    && !string.IsNullOrWhiteSpace(settings.MikoPbxExtension)
                    && !string.IsNullOrEmpty(settings.MikoPbxCdrTokenEncrypted))
                {
                    string tokenPlain = TokenEncryption.Decrypt(settings.MikoPbxCdrTokenEncrypted);
                    if (!string.IsNullOrEmpty(tokenPlain))
                    {
                        using var svc = new MikoPbxCdrService(
                            settings.MikoPbxCdrServiceUrl,
                            tokenPlain,
                            settings.MikoPbxExtension);
                        kommoStatus = await svc.GetKommoStatusAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] RefreshKommoGatewayModuleStatus error: {ex.Message}");
            }

            await Dispatcher.InvokeAsync(() => ApplyKommoGatewayModuleUi(kommoStatus));
        }

        private void PersistKommoConnectionSourceFromUi(AppSettings settings)
        {
            if (!settings.EnableAmoCrmIntegration)
                return;

            if (_gatewayKommoModuleActive)
            {
                string source = GetSelectedKommoConnectionSource();
                if (source is "gateway" or "local")
                    settings.AmoCrmConnectionSource = source;
            }
            else
            {
                settings.AmoCrmConnectionSource = "local";
            }
        }
        
        private void EnableAmoCrmIntegrationCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            // Показываем настройки при включении
            UpdateAmoCrmSettingsVisibility(true);
            // Сохраняем состояние, но НЕ стираем токен и домен
            SaveAmoCrmIntegrationToggle();
        }
        
        private void EnableAmoCrmIntegrationCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            // Скрываем настройки при выключении
            UpdateAmoCrmSettingsVisibility(false);
            // Сохраняем состояние и отключаем сервис, но НЕ стираем токен и домен
            SaveAmoCrmIntegrationToggle();
        }
        
        /// <summary>
        /// Сохраняет состояние переключателя Kommo интеграции
        /// </summary>
        private void SaveAmoCrmIntegrationToggle()
        {
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                
                if (settings == null)
                {
                    settings = new AppSettings();
                }
                
                // Сохраняем состояние интеграции
                settings.EnableAmoCrmIntegration = EnableAmoCrmIntegrationCheckBox?.IsChecked ?? false;
                PersistKommoConnectionSourceFromUi(settings);
                // ВАЖНО: не сбрасываем настройку выбора лида, если чекбокс ещё не создан (другая вкладка / ранний вызов)
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                }
                // ...удалено: ShowFirstLeadAfterCallCheckBox/ShowFirstLeadAfterCall...
                
                // Сохраняем настройки (токен и домен остаются в файле)
                AppDataHelper.SaveSettings(settings);
                
                MainWindow.Log($"[SettingsWindow] Kommo integration toggle saved: {settings.EnableAmoCrmIntegration}, source={settings.AmoCrmConnectionSource ?? "auto"}");

                var freshSettings = AppDataHelper.LoadSettingsOrNew();
                var mainWindowForInit = Application.Current.MainWindow as MainWindow;

                // Если интеграция выключена, отключаем сервис (НЕ стираем токен и домен)
                if (!settings.EnableAmoCrmIntegration)
                {
                    if (mainWindowForInit != null)
                        mainWindowForInit.DisconnectAmoCrmService();
                    
                    UpdateAmoCrmStatus("Not connected", false);
                }
                else
                {
                    // Если интеграция включена, проверяем наличие необходимых данных для подключения
                    string authMode = freshSettings.AmoCrmAuthMode ?? "manual";
                    bool hasManualToken = !string.IsNullOrEmpty(freshSettings.AmoCrmSubdomain) && 
                                         !string.IsNullOrEmpty(freshSettings.AmoCrmAccessTokenEncrypted);
                    bool hasOAuth = authMode == "oauth" && 
                                   !string.IsNullOrEmpty(freshSettings.AmoCrmSubdomain) &&
                                   !string.IsNullOrEmpty(freshSettings.AmoCrmClientId) &&
                                   !string.IsNullOrEmpty(freshSettings.AmoCrmClientSecretEncrypted) &&
                                   (!string.IsNullOrEmpty(freshSettings.AmoCrmOAuthAccessTokenEncrypted) || 
                                    !string.IsNullOrEmpty(freshSettings.AmoCrmOAuthRefreshTokenEncrypted));
                    
                    if (!string.IsNullOrEmpty(freshSettings.AmoCrmSubdomain) && (hasManualToken || hasOAuth))
                    {
                        if (mainWindowForInit != null)
                        {
                            MainWindow.Log($"[SettingsWindow] Initializing AmoCRM service (authMode={authMode}, hasManualToken={hasManualToken}, hasOAuth={hasOAuth}, source=local)");
                            mainWindowForInit.InitializeAmoCrmService(freshSettings);
                        }
                    }
                    else
                    {
                        MainWindow.Log($"[SettingsWindow] Cannot initialize AmoCRM: missing required credentials (subdomain={!string.IsNullOrEmpty(freshSettings.AmoCrmSubdomain)}, manualToken={hasManualToken}, oauth={hasOAuth})");
                        UpdateAmoCrmStatus("Not configured", false);
                    }
                }
                
                return;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error saving Kommo integration toggle: {ex.Message}");
            }
        }
        
        private void AmoCrmAccessTokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            // Можно добавить валидацию токена здесь, если нужно
        }
        
        /// <summary>
        /// Обработчик переключения режима аутентификации (современный сегментированный контрол)
        /// </summary>
        private void AmoCrmAuthModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string mode)
            {
                // Определяем выбранный режим: если нажата OAuth кнопка, то OAuth, иначе Manual Token
                bool isOAuth = (button == AmoCrmAuthModeOAuthButton);
                
                // Обновляем визуальное состояние кнопок
                var accentBlueBrush = (Brush)FindResource("AccentBlueBrush");
                var textPrimaryBrush = (Brush)FindResource("TextPrimaryBrush");
                
                if (AmoCrmAuthModeManualButton != null)
                {
                    AmoCrmAuthModeManualButton.Tag = isOAuth ? "manual" : "manual_selected";
                    AmoCrmAuthModeManualButton.Background = isOAuth ? Brushes.Transparent : accentBlueBrush;
                    AmoCrmAuthModeManualButton.Foreground = isOAuth ? textPrimaryBrush : Brushes.White;
                }
                
                if (AmoCrmAuthModeOAuthButton != null)
                {
                    AmoCrmAuthModeOAuthButton.Tag = isOAuth ? "oauth_selected" : "oauth";
                    AmoCrmAuthModeOAuthButton.Background = isOAuth ? accentBlueBrush : Brushes.Transparent;
                    AmoCrmAuthModeOAuthButton.Foreground = isOAuth ? Brushes.White : textPrimaryBrush;
                }
                
                // Обновляем видимость панелей и статусов
                UpdateAmoCrmAuthModeVisibility(isOAuth);
                
                // Отключаем сервис для неактивного режима и подключаем для активного
                DisconnectInactiveAuthMode(isOAuth);
            }
        }
        
        /// <summary>
        /// Отключает сервис для неактивного режима аутентификации
        /// </summary>
        private void DisconnectInactiveAuthMode(bool isOAuth)
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow == null) return;
            
            // Отключаем текущий сервис
            mainWindow.DisconnectAmoCrmService();
            
            // Загружаем настройки и подключаем только активный режим
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null && settings.EnableAmoCrmIntegration)
                    {
                        // Обновляем режим в настройках
                        settings.AmoCrmAuthMode = isOAuth ? "oauth" : "manual";
                        
                        // Сохраняем изменения
                        string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                        File.WriteAllText(settingsPath, updatedJson);
                        
                        // Подключаем только активный режим
                        mainWindow.InitializeAmoCrmService(settings);
                        
                        // Обновляем статус через небольшую задержку
                        _ = Task.Delay(2000).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                CheckAmoCrmConnectionStatus();
                            });
                        }, TaskContinuationOptions.OnlyOnRanToCompletion);
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error switching auth mode: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Обновляет видимость панелей в зависимости от режима аутентификации
        /// </summary>
        private void UpdateAmoCrmAuthModeVisibility(bool isOAuth)
        {
            // Subdomain поле показывается ВСЕГДА (нужен для обоих режимов)
            if (AmoCrmSubdomainPanel != null)
            {
                AmoCrmSubdomainPanel.Visibility = Visibility.Visible;
            }
            
            if (AmoCrmManualTokenPanel != null)
            {
                AmoCrmManualTokenPanel.Visibility = isOAuth ? Visibility.Collapsed : Visibility.Visible;
            }
            if (AmoCrmOAuthPanel != null)
            {
                AmoCrmOAuthPanel.Visibility = isOAuth ? Visibility.Visible : Visibility.Collapsed;
            }
            
            // Кнопки Save + Clear показываются только для Manual Token режима
            if (AmoCrmManualTokenButtonsPanel != null)
            {
                AmoCrmManualTokenButtonsPanel.Visibility = isOAuth ? Visibility.Collapsed : Visibility.Visible;
            }
            
            // Authorize + Clear кнопки управляются видимостью AmoCrmOAuthPanel (внутри него)
            
            // Статусы: Manual Token показывает "Connected/Not connected", OAuth показывает "Authorized/Not authorized"
            if (AmoCrmManualTokenStatusPanel != null)
            {
                AmoCrmManualTokenStatusPanel.Visibility = isOAuth ? Visibility.Collapsed : Visibility.Visible;
            }
            if (AmoCrmOAuthStatusPanel != null)
            {
                AmoCrmOAuthStatusPanel.Visibility = isOAuth ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        
        /// <summary>
        /// Обработчик кнопки "Authorize with Kommo"
        /// </summary>
        private async void AmoCrmAuthorizeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AmoCrmSubdomainTextBox == null || string.IsNullOrWhiteSpace(AmoCrmSubdomainTextBox.Text))
                {
                    CustomMessageBox.Show("Please enter Kommo subdomain first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (AmoCrmClientIdTextBox == null || string.IsNullOrWhiteSpace(AmoCrmClientIdTextBox.Text))
                {
                    CustomMessageBox.Show("Please enter Client ID.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (AmoCrmClientSecretPasswordBox == null || string.IsNullOrWhiteSpace(AmoCrmClientSecretPasswordBox.Password))
                {
                    CustomMessageBox.Show("Please enter Client Secret.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                string redirectUri = AmoCrmRedirectUriTextBox?.Text?.Trim() ?? "http://localhost:8080/callback";
                if (string.IsNullOrWhiteSpace(redirectUri))
                {
                    redirectUri = "http://localhost:8080/callback";
                }
                
                // Нормализуем Redirect URI - должен быть полный путь с портом
                if (redirectUri == "http://localhost" || redirectUri == "http://localhost/")
                {
                    redirectUri = "http://localhost:8080/callback";
                    if (AmoCrmRedirectUriTextBox != null)
                    {
                        AmoCrmRedirectUriTextBox.Text = redirectUri;
                    }
                }
                
                // Убеждаемся, что есть путь (если только домен и порт)
                if (!redirectUri.Contains("/", StringComparison.Ordinal) || redirectUri.EndsWith(":8080", StringComparison.OrdinalIgnoreCase))
                {
                    redirectUri = redirectUri.TrimEnd('/') + "/callback";
                    if (AmoCrmRedirectUriTextBox != null)
                    {
                        AmoCrmRedirectUriTextBox.Text = redirectUri;
                    }
                }
                
                MainWindow.Log("[SettingsWindow] Starting OAuth authorization...");
                
                // КРИТИЧНО: Захватываем ВСЕ значения из UI-элементов ДО ConfigureAwait(false),
                // потому что после ConfigureAwait(false) мы можем оказаться на фоновом потоке
                // и доступ к UI-элементам вызовет InvalidOperationException.
                string clientIdValue = AmoCrmClientIdTextBox.Text.Trim();
                string clientSecretValue = AmoCrmClientSecretPasswordBox.Password;
                
                // Отключаем кнопку на время авторизации
                if (AmoCrmAuthorizeButton != null)
                {
                    AmoCrmAuthorizeButton.IsEnabled = false;
                    AmoCrmAuthorizeButton.Content = "Authorizing...";
                }
                
                // Запускаем OAuth flow (асинхронно, не блокируя UI)
                var oauthService = new AmoCrmOAuthService();
                // Для OAuth subdomain не нужен для авторизации (используется единый www.amocrm.ru/oauth)
                // Subdomain будет извлечен из referer после авторизации
                var (code, referer) = await oauthService.AuthorizeAsync(
                    string.Empty, // Subdomain не нужен для OAuth авторизации
                    clientIdValue,
                    redirectUri
                ).ConfigureAwait(false);
                
                if (string.IsNullOrEmpty(code))
                {
                    MainWindow.Log("[SettingsWindow] OAuth authorization failed or was cancelled");
                    
                    // Обновляем UI асинхронно, не блокируя
                    await Dispatcher.InvokeAsync(() =>
                    {
                        CustomMessageBox.Show("Authorization failed or was cancelled. Please try again.", "Authorization Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        
                        if (AmoCrmAuthorizeButton != null)
                        {
                            AmoCrmAuthorizeButton.IsEnabled = true;
                            AmoCrmAuthorizeButton.Content = "Authorize with Kommo";
                        }
                    });
                    return;
                }
                
                MainWindow.Log($"[SettingsWindow] Authorization code received, exchanging for tokens...");
                
                // Обмениваем code на токены
                // Для OAuth subdomain извлекается из referer (обязательный параметр после авторизации)
                if (string.IsNullOrEmpty(referer))
                {
                    MainWindow.Log("[SettingsWindow] Referer not found in OAuth response - cannot determine subdomain");
                    
                    // Обновляем UI асинхронно, не блокируя
                    await Dispatcher.InvokeAsync(() =>
                    {
                        CustomMessageBox.Show("Failed to determine your Kommo subdomain from authorization response. Please try again.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        
                        if (AmoCrmAuthorizeButton != null)
                        {
                            AmoCrmAuthorizeButton.IsEnabled = true;
                            AmoCrmAuthorizeButton.Content = "Authorize with Kommo";
                        }
                    });
                    return;
                }
                
                // Извлекаем subdomain из referer
                // referer может быть в формате "https://mdkb.amocrm.ru" или "mdkb.amocrm.ru"
                string refererDomain = referer.Replace("https://", "").Replace("http://", "").Trim();
                string subdomainForTokenExchange;
                
                if (refererDomain.Contains("."))
                {
                    subdomainForTokenExchange = refererDomain.Split('.')[0];
                }
                else
                {
                    subdomainForTokenExchange = refererDomain;
                }
                
                MainWindow.Log($"[SettingsWindow] Extracted subdomain from referer: {subdomainForTokenExchange}");
                
                var (accessToken, refreshToken, expiresIn) = await oauthService.ExchangeCodeForTokensAsync(
                    subdomainForTokenExchange,
                    clientIdValue,
                    clientSecretValue,
                    code,
                    redirectUri
                ).ConfigureAwait(false);
                
                if (string.IsNullOrEmpty(accessToken))
                {
                    MainWindow.Log("[SettingsWindow] Failed to exchange authorization code for tokens");
                    
                    // Обновляем UI асинхронно, не блокируя
                    await Dispatcher.InvokeAsync(() =>
                    {
                        CustomMessageBox.Show("Failed to get access token. Please check your Client ID and Client Secret.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        
                        if (AmoCrmAuthorizeButton != null)
                        {
                            AmoCrmAuthorizeButton.IsEnabled = true;
                            AmoCrmAuthorizeButton.Content = "Authorize with Kommo";
                        }
                    });
                    return;
                }
                
                // Используем значения, захваченные из UI до ConfigureAwait(false)
                string subdomainToSave = subdomainForTokenExchange;
                string clientIdToSave = clientIdValue;
                string clientSecretToSave = clientSecretValue;
                
                // Сохраняем OAuth токены в настройки (в фоне, чтобы не блокировать UI)
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                
                // Читаем настройки асинхронно (не блокируя UI)
                await Task.Run(() =>
                {
                    if (File.Exists(settingsPath))
                    {
                        string json = File.ReadAllText(settingsPath);
                        settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    }
                    
                    if (settings == null)
                    {
                        settings = new AppSettings();
                    }
                }).ConfigureAwait(false);
                
                // Обновляем настройки OAuth
                // Subdomain сохраняется из referer (извлечен выше)
                if (settings == null)
                {
                    settings = new AppSettings();
                }
                
                // КРИТИЧНО: Убеждаемся, что интеграция включена; OAuth всегда локальный режим
                settings.EnableAmoCrmIntegration = true;
                settings.AmoCrmConnectionSource = "local";
                settings.AmoCrmSubdomain = subdomainToSave;
                settings.AmoCrmAuthMode = "oauth";
                settings.AmoCrmClientId = clientIdToSave;
                settings.AmoCrmClientSecretEncrypted = TokenEncryption.Encrypt(clientSecretToSave);
                settings.AmoCrmRedirectUri = redirectUri;
                settings.AmoCrmOAuthAccessTokenEncrypted = TokenEncryption.Encrypt(accessToken);
                settings.AmoCrmOAuthRefreshTokenEncrypted = refreshToken != null ? TokenEncryption.Encrypt(refreshToken) : null;
                if (expiresIn.HasValue)
                {
                    settings.AmoCrmOAuthTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn.Value);
                }
                
                // Сохраняем файл асинхронно, чтобы не блокировать UI
                await Task.Run(() =>
                {
                    try
                    {
                        AppDataHelper.SaveSettings(settings);
                        MainWindow.Log($"[SettingsWindow] OAuth tokens saved to file: {settingsPath}");
                        MainWindow.Log($"[SettingsWindow] Access token encrypted: {!string.IsNullOrEmpty(settings.AmoCrmOAuthAccessTokenEncrypted)}");
                        MainWindow.Log($"[SettingsWindow] Refresh token encrypted: {!string.IsNullOrEmpty(settings.AmoCrmOAuthRefreshTokenEncrypted)}");
                        MainWindow.Log($"[SettingsWindow] Token expires at: {settings.AmoCrmOAuthTokenExpiresAt}");
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[SettingsWindow] Error saving OAuth tokens to file: {ex.Message}");
                        throw;
                    }
                }).ConfigureAwait(false);
                
                MainWindow.Log("[SettingsWindow] OAuth tokens saved successfully");
                
                // Обновляем UI в UI потоке асинхронно, не блокируя
                await Dispatcher.InvokeAsync(() =>
                {
                    SetKommoConnectionSourceUi("local", persistAndReinit: false);
                    UpdateAmoCrmStatus("Connected", true);
                    
                    if (AmoCrmAuthorizeButton != null)
                    {
                        AmoCrmAuthorizeButton.IsEnabled = true;
                        AmoCrmAuthorizeButton.Content = "Authorize with Kommo";
                    }
                });
                
                // Инициализируем Kommo сервис с OAuth токенами (в UI потоке, т.к. может обращаться к UI)
                await Dispatcher.InvokeAsync(() =>
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    if (mainWindow != null)
                    {
                        var freshSettings = AppDataHelper.LoadSettingsOrNew();
                        mainWindow.InitializeAmoCrmService(freshSettings);
                    }
                });
                
                // Проверяем статус через небольшую задержку (InvokeAsync — не блокирует фоновый поток)
                _ = Task.Delay(2000).ContinueWith(t =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        CheckAmoCrmConnectionStatus();
                    });
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
                
                // Выводим SettingsWindow на передний план, чтобы пользователь увидел обновлённый статус.
                // Модальный диалог (CustomMessageBox) НЕ используем — он появляется ЗА окном браузера
                // и блокирует интерфейс, пока пользователь не закроет браузер и не нажмёт OK.
                await Dispatcher.InvokeAsync(() =>
                {
                    this.Activate();
                    this.Topmost = true;
                    this.Topmost = false;
                    this.Focus();
                    MainWindow.Log("[SettingsWindow] OAuth authorization completed, window activated");
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error during OAuth authorization: {ex.Message}");
                MainWindow.Log($"[SettingsWindow] Stack trace: {ex.StackTrace}");
                
                // Обновляем UI асинхронно, не блокируя
                await Dispatcher.InvokeAsync(() =>
                {
                    CustomMessageBox.Show($"Error during authorization: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    
                    if (AmoCrmAuthorizeButton != null)
                    {
                        AmoCrmAuthorizeButton.IsEnabled = true;
                        AmoCrmAuthorizeButton.Content = "Authorize with Kommo";
                    }
                });
            }
        }
        
        private void SaveAmoCrmSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                
                if (settings == null)
                {
                    settings = new AppSettings();
                }
                
                // Убеждаемся, что интеграция включена при сохранении настроек
                settings.EnableAmoCrmIntegration = true;
                if (EnableAmoCrmIntegrationCheckBox != null)
                {
                    EnableAmoCrmIntegrationCheckBox.IsChecked = true;
                }
                // ВАЖНО: не сбрасываем настройку выбора лида, если чекбокс ещё не создан
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                }
                
                // Сохраняем настройки Kommo
                settings.AmoCrmSubdomain = AmoCrmSubdomainTextBox?.Text?.Trim();
                
                // Определяем режим аутентификации из состояния кнопок
                bool isOAuth = false;
                if (AmoCrmAuthModeOAuthButton != null && AmoCrmAuthModeOAuthButton.Tag is string oauthTag)
                {
                    isOAuth = oauthTag == "oauth_selected";
                }
                else if (AmoCrmAuthModeManualButton != null && AmoCrmAuthModeManualButton.Tag is string manualTag)
                {
                    isOAuth = manualTag != "manual_selected";
                }
                else
                {
                    // Fallback: проверяем текущее состояние кнопок по Background
                    if (AmoCrmAuthModeOAuthButton != null && AmoCrmAuthModeOAuthButton.Background != Brushes.Transparent)
                    {
                        isOAuth = true;
                    }
                }
                settings.AmoCrmAuthMode = isOAuth ? "oauth" : "manual";
                PersistKommoConnectionSourceFromUi(settings);
                
                if (isOAuth)
                {
                    // Сохраняем OAuth настройки
                    settings.AmoCrmClientId = AmoCrmClientIdTextBox?.Text?.Trim();
                    if (AmoCrmClientSecretPasswordBox != null && !string.IsNullOrEmpty(AmoCrmClientSecretPasswordBox.Password))
                    {
                        settings.AmoCrmClientSecretEncrypted = TokenEncryption.Encrypt(AmoCrmClientSecretPasswordBox.Password);
                    }
                    settings.AmoCrmRedirectUri = AmoCrmRedirectUriTextBox?.Text?.Trim() ?? "http://localhost:8080/callback";
                    
                    // OAuth токены сохраняются через кнопку Authorize, здесь не трогаем их
                }
                else
                {
                    // Сохраняем Manual Token
                    if (AmoCrmAccessTokenPasswordBox != null && !string.IsNullOrEmpty(AmoCrmAccessTokenPasswordBox.Password))
                    {
                        settings.AmoCrmAccessTokenEncrypted = TokenEncryption.Encrypt(AmoCrmAccessTokenPasswordBox.Password);
                    }
                    else
                    {
                        // Если токен не указан, очищаем зашифрованный токен
                        settings.AmoCrmAccessTokenEncrypted = null;
                    }
                }
                
                // Сохраняем настройки
                AppDataHelper.SaveSettings(settings);
                
                MainWindow.Log("[SettingsWindow] Kommo settings saved successfully");
                
                // Обновляем статус на "Connecting..."
                UpdateAmoCrmStatus("Connecting...", false);
                
                // Переинициализируем Kommo сервис в MainWindow (асинхронно, не блокирует UI)
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    var freshSettings = AppDataHelper.LoadSettingsOrNew();
                    mainWindow.InitializeAmoCrmService(freshSettings);
                    
                    // Проверяем статус через небольшую задержку (после начала инициализации)
                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            CheckAmoCrmConnectionStatus();
                        });
                    }, TaskContinuationOptions.OnlyOnRanToCompletion);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error saving Kommo settings: {ex.Message}");
                CustomMessageBox.Show($"Error saving Kommo settings:\n\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }
        
        /// <summary>
        /// Очищает настройки Kommo (токен и домен) из UI и файла settings.json
        /// </summary>
        private void ClearAmoCrmSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Подтверждение удаления
                var result = CustomMessageBox.Show(
                    "Are you sure you want to clear all Kommo settings?\n\nThis will remove the domain and access token from both the UI and settings file.",
                    "Clear Kommo Settings",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    this);
                
                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
                
                // Очищаем UI поля
                if (AmoCrmSubdomainTextBox != null)
                {
                    AmoCrmSubdomainTextBox.Text = string.Empty;
                }
                
                if (AmoCrmAccessTokenPasswordBox != null)
                {
                    AmoCrmAccessTokenPasswordBox.Password = string.Empty;
                }
                
                // Очищаем OAuth поля
                if (AmoCrmClientIdTextBox != null)
                {
                    AmoCrmClientIdTextBox.Text = string.Empty;
                }
                if (AmoCrmClientSecretPasswordBox != null)
                {
                    AmoCrmClientSecretPasswordBox.Password = string.Empty;
                }
                if (AmoCrmRedirectUriTextBox != null)
                {
                    AmoCrmRedirectUriTextBox.Text = "http://localhost:8080/callback";
                }
                if (AmoCrmOAuthStatusTextBlock != null)
                {
                    AmoCrmOAuthStatusTextBlock.Visibility = Visibility.Collapsed;
                }
                
                // Переключаем на Manual Token режим
                var accentBlueBrush = (Brush)FindResource("AccentBlueBrush");
                var textPrimaryBrush = (Brush)FindResource("TextPrimaryBrush");
                
                if (AmoCrmAuthModeManualButton != null)
                {
                    AmoCrmAuthModeManualButton.Tag = "manual_selected";
                    AmoCrmAuthModeManualButton.Background = accentBlueBrush;
                    AmoCrmAuthModeManualButton.Foreground = Brushes.White;
                }
                if (AmoCrmAuthModeOAuthButton != null)
                {
                    AmoCrmAuthModeOAuthButton.Tag = "oauth";
                    AmoCrmAuthModeOAuthButton.Background = Brushes.Transparent;
                    AmoCrmAuthModeOAuthButton.Foreground = textPrimaryBrush;
                }
                UpdateAmoCrmAuthModeVisibility(false);
                
                // Отключаем OAuth сервис при переключении на Manual Token
                DisconnectInactiveAuthMode(false);
                
                // Очищаем настройки в файле
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                
                if (settings == null)
                {
                    settings = new AppSettings();
                }
                
                // Очищаем все AmoCRM настройки
                settings.AmoCrmSubdomain = null;
                settings.AmoCrmAccessTokenEncrypted = null;
                settings.AmoCrmAuthMode = "manual";
                settings.AmoCrmClientId = null;
                settings.AmoCrmClientSecretEncrypted = null;
                settings.AmoCrmRedirectUri = null;
                settings.AmoCrmOAuthAccessTokenEncrypted = null;
                settings.AmoCrmOAuthRefreshTokenEncrypted = null;
                settings.AmoCrmOAuthTokenExpiresAt = null;
                settings.EnableAmoCrmIntegration = false;
                
                if (EnableAmoCrmIntegrationCheckBox != null)
                {
                    EnableAmoCrmIntegrationCheckBox.IsChecked = false;
                    UpdateAmoCrmSettingsVisibility(false);
                }
                
                // Сохраняем изменения
                string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsPath, updatedJson);
                
                MainWindow.Log("[SettingsWindow] Kommo settings cleared");
                
                // Отключаем Kommo сервис
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.DisconnectAmoCrmService();
                }
                
                UpdateAmoCrmStatus("Not connected", false);
                
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                
                if (settings == null)
                {
                    settings = new AppSettings();
                }
                
                // Очищаем токен и домен
                settings.AmoCrmSubdomain = null;
                settings.AmoCrmAccessTokenEncrypted = null;
                // Выключаем интеграцию
                settings.EnableAmoCrmIntegration = false;
                
                // Обновляем UI
                if (EnableAmoCrmIntegrationCheckBox != null)
                {
                    EnableAmoCrmIntegrationCheckBox.IsChecked = false;
                }
                UpdateAmoCrmSettingsVisibility(false);
                
                // Отключаем сервис в MainWindow
                var mainWindowDisconnect = Application.Current.MainWindow as MainWindow;
                if (mainWindowDisconnect != null)
                {
                    mainWindowDisconnect.DisconnectAmoCrmService();
                }
                
                // Обновляем статус
                UpdateAmoCrmStatus("Not connected", false);
                
                // Сохраняем настройку выбора лида Kommo (если была установлена ранее)
                // При очистке настроек мы не сбрасываем эту настройку, так как она не связана с токеном/доменом
                // Но если чекбокс доступен, сохраняем его текущее состояние
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                }
                
                // Сохраняем настройки
                string clearedSettingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsPath, clearedSettingsJson);
                
                MainWindow.Log("[SettingsWindow] AmoCRM settings cleared successfully");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error clearing Kommo settings: {ex.Message}");
                CustomMessageBox.Show($"Error clearing Kommo settings:\n\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }

        private void LoadAppearanceSettings()
        {
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    if (ThemeModeComboBox != null)
                    {
                        var mode = ThemeService.ParseMode(settings?.ThemeMode);
                        ThemeModeComboBox.SelectedIndex = mode switch
                        {
                            ThemeMode.Dark => 1,
                            ThemeMode.Light => 2,
                            _ => 0
                        };
                    }
                }
                else
                {
                    if (ThemeModeComboBox != null)
                    {
                        ThemeModeComboBox.SelectedIndex = 0; // system
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadAppearanceSettings: Error - {ex.Message}");
            }
        }

        private void ApplyThemeButton_Click(object sender, RoutedEventArgs e)
        {
            if (ThemeModeComboBox == null) return;

            try
            {
                var selected = (ThemeModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
                var mode = ThemeService.ParseMode(selected);
                ThemeService.SetConfiguredMode(mode);
                
                CustomMessageBox.Show("Theme applied successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information, this);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error applying theme: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }
        
        private void EnableCallRecordingCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressRecordingToggleEvent) return;

            // Recording is supported only in WebRTC mode
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    bool useWebRtc = settings?.UseWebRtcAudio ?? false;

                    if (!useWebRtc)
                    {
                        CustomMessageBox.Show(
                            "Call recording is available in WebRTC mode.",
                            "Recording Not Available",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            this);

                        _suppressRecordingToggleEvent = true;
                        EnableCallRecordingCheckBox.IsChecked = false;
                        UpdateCallRecordingToggleColor();
                        _suppressRecordingToggleEvent = false;
                        return;
                    }
                }
            }
            catch
            {
                // If we can't read settings, fail safe: don't enable.
                _suppressRecordingToggleEvent = true;
                EnableCallRecordingCheckBox.IsChecked = false;
                UpdateCallRecordingToggleColor();
                _suppressRecordingToggleEvent = false;
                return;
            }

            UpdateCallRecordingToggleColor();
            SaveCallRecordingSetting();
        }
        
        private void EnableCallRecordingCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressRecordingToggleEvent) return;
            UpdateCallRecordingToggleColor();
            SaveCallRecordingSetting();
        }
        
        private void UpdateCallRecordingToggleColor()
        {
            if (EnableCallRecordingCheckBox == null) return;
            
            if (EnableCallRecordingCheckBox.IsChecked == true)
            {
                // Зеленый цвет когда включено
                EnableCallRecordingCheckBox.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
            }
            else
            {
                // Серый цвет когда выключено
                EnableCallRecordingCheckBox.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            }
        }
        
        private void SaveCallRecordingSetting()
        {
            try
            {
                AppSettings settings;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                if (EnableCallRecordingCheckBox != null)
                {
                    settings.EnableCallRecording = EnableCallRecordingCheckBox.IsChecked ?? false;
                }
                
                // Сохраняем настройку выбора лида AmoCRM
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                }

                string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), settingsJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveCallRecordingSetting: Error - {ex.Message}");
            }
        }
        
        private void OpenRecordingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string recordingsPath = AppDataHelper.GetRecordingsDirectory();
                
                // Открываем папку в проводнике
                System.Diagnostics.Process.Start("explorer.exe", recordingsPath);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error opening recordings folder: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning, this);
            }
        }

        private void AdvancedButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(AdvancedButton);
            
            // Получаем или создаем общий WebRTC сервис ПЕРЕД вызовом ShowView,
            // чтобы статус мог быть восстановлен сразу при открытии вкладки
            GetOrCreateSharedWebRtcService();
            
            ShowView(AdvancedSettingsView);
            // LoadGeneralSettings уже вызывается в ShowView, не нужно вызывать дважды
        }

        private void IntegrationsButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(IntegrationsButton);
            ShowView(IntegrationsSettingsView);
            LoadIntegrationsSettings();
            _ = RefreshKommoGatewayModuleStatusAsync();
            
            // Проверяем статус с задержками
            // (если окно открывается сразу после запуска приложения)
            // Первая проверка через 500мс
            _ = Task.Delay(500).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    MainWindow.Log("[SettingsWindow] IntegrationsButton_Click: First status check (500ms delay)");
                    CheckAmoCrmConnectionStatus();
                    CheckMikoPbxCdrConnectionStatus();
                });
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
            
            // Вторая проверка через 2 секунды (на случай если сервис инициализируется дольше)
            _ = Task.Delay(2000).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    MainWindow.Log("[SettingsWindow] IntegrationsButton_Click: Second status check (2000ms delay)");
                    CheckAmoCrmConnectionStatus();
                    CheckMikoPbxCdrConnectionStatus();
                });
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(AboutButton);
            ShowView(AboutSettingsView);
            
            // Загружаем текущую версию приложения
            string currentVersion = UpdateService.GetCurrentVersion();
            VersionTextBlock.Text = $"Version: {currentVersion}";
        }
        
        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CheckForUpdatesButton.IsEnabled = false;
                CheckForUpdatesButton.Content = "Checking...";
                
                MainWindow.Log("[SettingsWindow] Manual update check initiated");
                
                // Используем новый сервис обновлений через собственный сервер
                // forceCheck = true, чтобы проверить независимо от времени последней проверки
                var updateInfo = await UpdateService.CheckForUpdateAsync(forceCheck: true);
                
                if (updateInfo != null)
                {
                    // Новая версия доступна
                    string currentVersion = UpdateService.GetCurrentVersion();

                    // If already open, bring to front (do not block Settings/Main windows).
                    var existing = Application.Current?.Windows.OfType<UpdateAvailableWindow>().FirstOrDefault();
                    if (existing != null)
                    {
                        try
                        {
                            if (existing.WindowState == WindowState.Minimized)
                                existing.WindowState = WindowState.Normal;
                            existing.Activate();
                            existing.Focus();
                        }
                        catch { }
                        return;
                    }

                    var updateWindow = new UpdateAvailableWindow(updateInfo, currentVersion);
                    updateWindow.Show();
                    try
                    {
                        updateWindow.Activate();
                        updateWindow.Focus();
                    }
                    catch { }
                }
                else
                {
                    string currentVersion = UpdateService.GetCurrentVersion();
                    CustomMessageBox.Show($"You are using the latest version ({currentVersion}).", 
                        "No Updates Available", MessageBoxButton.OK, MessageBoxImage.Information, this);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error checking for updates: {ex.Message}");
                CustomMessageBox.Show(
                    $"Could not check for updates:\n\n{ex.Message}\n\nPlease check your internet connection and try again.",
                    "Update Check Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
            }
            finally
            {
                CheckForUpdatesButton.IsEnabled = true;
                CheckForUpdatesButton.Content = "Check for Updates";
            }
        }
        
        private void LoadGeneralSettings()
        {
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null)
                    {
                        UseWebRtcCheckBox.IsChecked = settings.UseWebRtcAudio;
                        // Используем сохраненное значение или предзаполняем "wss://"
                        if (!string.IsNullOrWhiteSpace(settings.WebRtcWsUri))
                        {
                            // Если значение не начинается с "wss://", добавляем префикс
                            string wsUri = settings.WebRtcWsUri;
                            if (!wsUri.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) && 
                                !wsUri.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
                            {
                                wsUri = "wss://" + wsUri;
                            }
                            wsUri = SipEndpointHelper.NormalizeWebRtcWsUri(wsUri);
                            WebRtcWsUriTextBox.Text = wsUri;
                            System.Diagnostics.Debug.WriteLine($"LoadGeneralSettings: Loaded WebRtcWsUri from file: '{settings.WebRtcWsUri}' -> '{wsUri}'");
                        }
                        else
                        {
                            WebRtcWsUriTextBox.Text = "wss://";
                            System.Diagnostics.Debug.WriteLine("LoadGeneralSettings: WebRtcWsUri was empty in file, prefilled with 'wss://'");
                        }
                        System.Diagnostics.Debug.WriteLine($"LoadGeneralSettings: UseWebRtcAudio={settings.UseWebRtcAudio}, WebRtcWsUri='{settings.WebRtcWsUri}'");

                        // Обновляем состояние кнопки тестирования
                        if (TestWebRtcConnectionButton != null)
                        {
                            TestWebRtcConnectionButton.IsEnabled = settings.UseWebRtcAudio;
                        }
                    }
                }
                else
                {
                    // Если файла нет, устанавливаем значения по умолчанию
                    UseWebRtcCheckBox.IsChecked = false;
                    WebRtcWsUriTextBox.Text = "wss://";
                    System.Diagnostics.Debug.WriteLine("LoadGeneralSettings: Settings file not found, prefilled with 'wss://'");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadGeneralSettings: Error - {ex.Message}");
                CustomMessageBox.Show($"Error loading general settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning, this);
            }
        }
        
        private bool _isUpdatingWebRtcStatus = false; // Флаг для предотвращения множественных вызовов
        private System.Threading.CancellationTokenSource? _updateWebRtcStatusCts; // Для отмены отложенных вызовов
        
        private void UpdateWebRtcStatus()
        {
            // Отменяем предыдущий отложенный вызов, если он есть
            _updateWebRtcStatusCts?.Cancel();
            _updateWebRtcStatusCts = new System.Threading.CancellationTokenSource();
            var token = _updateWebRtcStatusCts.Token;
            
            // Откладываем выполнение на 500мс (debounce)
            System.Threading.Tasks.Task.Delay(500, token).ContinueWith(async t =>
            {
                if (t.IsCanceled || token.IsCancellationRequested)
                {
                    return;
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateWebRtcStatusInternal();
                });
            });
        }
        
        private void UpdateWebRtcStatusInternal()
        {
            // Предотвращаем множественные одновременные вызовы
            if (_isUpdatingWebRtcStatus)
            {
                MainWindow.Log("[WebRTC] UpdateWebRtcStatus already in progress, skipping...");
                return;
            }
            
            try
            {
                _isUpdatingWebRtcStatus = true;
                
                // Проверяем, что элементы UI инициализированы
                if (UseWebRtcCheckBox == null || WebRtcWsUriTextBox == null || WebRtcStatusTextBlock == null)
                {
                    System.Diagnostics.Debug.WriteLine("UpdateWebRtcStatus: UI elements not initialized yet");
                    return;
                }
                
                // Используем текущие значения из UI
                bool useWebRtc = UseWebRtcCheckBox.IsChecked ?? false;
                string wsUri = WebRtcWsUriTextBox.Text?.Trim() ?? "";
                
                System.Diagnostics.Debug.WriteLine($"UpdateWebRtcStatus: useWebRtc={useWebRtc}, wsUri='{wsUri}', wsUri.Length={wsUri.Length}, TextBox.IsLoaded={WebRtcWsUriTextBox.IsLoaded}");
                
                // Загружаем настройки из файла для получения SIP credentials
                AppSettings? settings = null;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    System.Diagnostics.Debug.WriteLine($"UpdateWebRtcStatus: Loaded settings from file, WebRtcWsUri='{settings?.WebRtcWsUri}'");
                }
                
                // Если значение в TextBox пустое, но есть в настройках, используем значение из настроек
                if (string.IsNullOrWhiteSpace(wsUri) && settings != null && !string.IsNullOrWhiteSpace(settings.WebRtcWsUri))
                {
                    wsUri = settings.WebRtcWsUri;
                    WebRtcWsUriTextBox.Text = wsUri;
                    System.Diagnostics.Debug.WriteLine($"UpdateWebRtcStatus: Using URI from settings file: '{wsUri}'");
                }
                
                if (!useWebRtc)
                {
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Disabled";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                    StopWebRtcStatusCheck();
                    // НЕ удаляем общий сервис здесь, только отписываемся
                    if (TestWebRtcConnectionButton != null)
                        TestWebRtcConnectionButton.IsEnabled = false;
                    return;
                }
                
                // Включаем кнопку тестирования если WebRTC включен и настроен
                if (TestWebRtcConnectionButton != null)
                {
                    string? rtcPass = settings != null ? SipPasswordProvider.GetMainWebRtcPassword(settings) : null;
                    string? rtcUser = settings != null ? AppSettings.EffectiveMainWebRtcUsername(settings) : null;
                    bool canTest = !string.IsNullOrWhiteSpace(wsUri) && 
                                   wsUri != "wss://pbx.example.com:8089/ws" &&
                                   settings != null && 
                                   !string.IsNullOrWhiteSpace(rtcUser) && 
                                   !string.IsNullOrWhiteSpace(rtcPass);
                    TestWebRtcConnectionButton.IsEnabled = canTest;
                }
                
                // Проверяем, что URI указан и не является дефолтным примером
                // Проверяем только на точное совпадение с дефолтным примером
                bool isDefaultExample = wsUri == "wss://pbx.example.com:8089/ws";
                
                System.Diagnostics.Debug.WriteLine($"UpdateWebRtcStatus: Checking URI - wsUri='{wsUri}', IsNullOrWhiteSpace={string.IsNullOrWhiteSpace(wsUri)}, isDefaultExample={isDefaultExample}");
                
                if (string.IsNullOrWhiteSpace(wsUri) || isDefaultExample)
                {
                    System.Diagnostics.Debug.WriteLine($"UpdateWebRtcStatus: URI is empty or default example. wsUri='{wsUri}', isDefaultExample={isDefaultExample}");
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Not configured (WebSocket URI missing or invalid)";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    StopWebRtcStatusCheck();
                    // НЕ удаляем общий сервис здесь, только отписываемся
                    return;
                }
                
                string? sipPasswordForConfig = settings != null ? SipPasswordProvider.GetMainWebRtcPassword(settings) : null;
                string? rtcUserForConfig = settings != null ? AppSettings.EffectiveMainWebRtcUsername(settings) : null;
                if (settings == null || string.IsNullOrWhiteSpace(rtcUserForConfig) || string.IsNullOrWhiteSpace(sipPasswordForConfig))
                {
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Not configured (WebRTC credentials missing)";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    StopWebRtcStatusCheck();
                    // НЕ удаляем общий сервис здесь, только отписываемся
                    return;
                }
                
                // Проверяем текущий статус подключения перед запуском новой проверки
                if (_sharedWebRtcStatusService != null)
                {
                    var currentStatus = _sharedWebRtcStatusService.CurrentStatus;
                    
                    // Если подключение уже активно и конфигурация не изменилась, не запускаем новую проверку
                    if (currentStatus == WebRtcConnectionStatus.Registered || 
                        currentStatus == WebRtcConnectionStatus.Connected)
                    {
                        // Проверяем, что конфигурация не изменилась
                        string currentWsUri = _sharedWebRtcStatusService.CurrentConfig?.WsUri ?? "";
                        string currentSipUri = _sharedWebRtcStatusService.CurrentConfig?.SipUri ?? "";
                        
                        // Формируем ожидаемый SIP URI для сравнения
                        string pbxAddress = "";
                        if (!string.IsNullOrEmpty(wsUri))
                        {
                            try
                            {
                                var uri = new Uri(wsUri);
                                pbxAddress = uri.Host;
                            }
                            catch { }
                        }
                        if (string.IsNullOrEmpty(pbxAddress))
                        {
                            pbxAddress = SipEndpointHelper.GetHostOnly(settings.SipServer);
                        }
                        string rtcUser = AppSettings.EffectiveMainWebRtcUsername(settings) ?? "";
                        string wsAor = rtcUser.EndsWith("-WS", StringComparison.OrdinalIgnoreCase) ? rtcUser : $"{rtcUser}-WS";
                        string expectedSipUri = $"sip:{wsAor}@{pbxAddress}";
                        
                        // Если конфигурация не изменилась, просто восстанавливаем статус и подписываемся на события
                        if (currentWsUri == wsUri && currentSipUri == expectedSipUri)
                        {
                            MainWindow.Log($"[WebRTC] Connection already active ({currentStatus}), skipping new connection check");
                            UpdateWebRtcStatusDisplay(currentStatus);
                            
                            // Подписываемся на изменения статуса, если еще не подписаны
                            if (_webRtcStatusService == null)
                            {
                                _webRtcStatusService = _sharedWebRtcStatusService;
                                _webRtcStatusService.OnStatusChanged += UpdateWebRtcStatusDisplay;
                            }
                            
                            return;
                        }
                        else
                        {
                            MainWindow.Log($"[WebRTC] Configuration changed (URI or SIP credentials), reconnecting...");
                            MainWindow.Log($"[WebRTC]   Current: WsUri={currentWsUri}, SipUri={currentSipUri}");
                            MainWindow.Log($"[WebRTC]   New: WsUri={wsUri}, SipUri={expectedSipUri}");
                        }
                    }
                }
                
                // Обновляем настройки для проверки подключения
                var checkSettings = new AppSettings
                {
                    UseWebRtcAudio = true,
                    WebRtcWsUri = wsUri,
                    SipUsername = rtcUserForConfig,
                    SipPassword = sipPasswordForConfig,
                    SipServer = settings.SipServer
                };
                
                // Запускаем проверку подключения
                StartWebRtcStatusCheck(checkSettings);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] Error updating WebRTC status: {ex.Message}");
                WebRtcStatusTextBlock.Text = "WebRTC Status: Unknown";
                WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                StopWebRtcStatusCheck();
            }
            finally
            {
                _isUpdatingWebRtcStatus = false;
            }
        }
        
        private async void StartWebRtcStatusCheck(AppSettings settings)
        {
            try
            {
                // Проверяем, не инициализирован ли уже WebRTC сервис в MainWindow (main slot)
                var isReady = WebRtcService.Main.IsReadyForCalls;
                MainWindow.Log($"[WebRTC] Checking WebRTC service status: IsReadyForCalls={isReady}");
                
                if (isReady)
                {
                    // WebRTC уже работает - просто показываем статус без повторной инициализации
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Connected and Ready";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                    MainWindow.Log("[WebRTC] Using existing WebRTC service status (already initialized)");
                    return;
                }
                
                MainWindow.Log("[WebRTC] WebRTC service not ready, initializing test connection in Settings");
                
                // Формируем конфиг
                // Извлекаем адрес PBX из WebSocket URI или используем SipServer
                string pbxAddress = "";
                if (!string.IsNullOrEmpty(settings.WebRtcWsUri))
                {
                    try
                    {
                        var uri = new Uri(settings.WebRtcWsUri);
                        pbxAddress = uri.Host; // Извлекаем домен/IP из WebSocket URI
                    }
                    catch
                    {
                        // Если не удалось распарсить WebSocket URI, используем SipServer
                    }
                }
                if (string.IsNullOrEmpty(pbxAddress))
                {
                    pbxAddress = SipEndpointHelper.GetHostOnly(settings.SipServer);
                }
                if (string.IsNullOrEmpty(pbxAddress))
                {
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Not configured (PBX address missing)";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    return;
                }
                string username = settings.SipUsername ?? "";
                string wsAor = username.EndsWith("-WS", StringComparison.OrdinalIgnoreCase) ? username : $"{username}-WS";
                string sipUri = $"sip:{wsAor}@{pbxAddress}";
                string wsUri = settings.WebRtcWsUri ?? "";
                
                WebRtcStatusService shared;
                try
                {
                    shared = GetOrCreateSharedWebRtcService();
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[WebRTC] ERROR: Could not initialize WebRtcStatusService: {ex.Message}");
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Error (service not initialized)";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    SyncMainWebRtcStatusLabelFromLegacy();
                    return;
                }
                
                // Отписываемся от старого обработчика
                StopWebRtcStatusCheck();
                
                _webRtcStatusService = shared;
                _webRtcStatusService.OnStatusChanged += (status) =>
                {
                    Dispatcher.Invoke(() => UpdateWebRtcStatusDisplay(status));
                };
                
                var config = new WebRtcConfig
                {
                    WsUri = wsUri,
                    SipUri = sipUri,
                    Password = settings.SipPassword ?? SipPasswordProvider.GetPassword(settings) ?? "",
                    // Используем тот же флаг, что и основное приложение
                    EnableDebug = settings?.EnableWebRtcDebug ?? true
                };
                
                // Логируем только кратко для тестирования в настройках
                MainWindow.Log($"[WebRTC] Testing connection in Settings (WsUri={wsUri}, SipUri={sipUri})");
                
                // Запускаем проверку подключения (InitializeAsync сам обработает множественные вызовы)
                await _webRtcStatusService.CheckConnectionAsync(config);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] Error starting WebRTC status check: {ex.Message}");
                WebRtcStatusTextBlock.Text = "WebRTC Status: Error checking connection";
                WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
            }
        }
        
        private void StopWebRtcStatusCheck()
        {
            try
            {
                if (_webRtcStatusService != null)
                {
                    // Не удаляем сервис, если он еще инициализируется
                    // Просто отписываемся от событий
                    _webRtcStatusService.OnStatusChanged -= UpdateWebRtcStatusDisplay;
                    _webRtcStatusService = null;
                }
                
                // НЕ останавливаем подключение в shared сервисе при закрытии окна настроек
                // Подключение должно оставаться активным, если WebRTC включен
                // Остановка подключения происходит только при выключении WebRTC или закрытии приложения
            }
            catch
            {
                // Игнорируем ошибки
            }
        }
        
        private void DisposeWebRtcStatusService()
        {
            try
            {
                if (_sharedWebRtcStatusService != null)
                {
                    MainWindow.Log("[WebRTC] Disposing shared WebRtcStatusService");
                    _sharedWebRtcStatusService.Dispose();
                    _sharedWebRtcStatusService = null;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] Error disposing shared service: {ex.Message}");
            }
        }
        
        private void UpdateWebRtcStatusDisplay(WebRtcConnectionStatus status)
        {
            switch (status)
            {
                case WebRtcConnectionStatus.NotConnected:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Not connected";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                    break;
                case WebRtcConnectionStatus.InitializingWebView2:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Initializing WebView2...";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    break;
                case WebRtcConnectionStatus.InitializingJsSIP:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Initializing JsSIP...";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    break;
                case WebRtcConnectionStatus.ConnectingToWebSocket:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Connecting to WebSocket...";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    break;
                case WebRtcConnectionStatus.Connecting:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Connecting...";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    break;
                case WebRtcConnectionStatus.Connected:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Connected";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    break;
                case WebRtcConnectionStatus.Registered:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Registered";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                    break;
                case WebRtcConnectionStatus.Disconnected:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Disconnected";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                    break;
                case WebRtcConnectionStatus.RegistrationFailed:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Registration failed";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    break;
                case WebRtcConnectionStatus.Error:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Error";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    break;
            }
            SyncMainWebRtcStatusLabelFromLegacy();
        }

        /// <summary>Mirrors legacy WebRtcStatusTextBlock into the visible primary-connection WebRTC line when WebRTC transport is selected.</summary>
        private void SyncMainWebRtcStatusLabelFromLegacy()
        {
            try
            {
                if (MainWebRtcStatusTextBlock == null || WebRtcStatusTextBlock == null) return;
                if (MainTransportWebRtcRadio?.IsChecked != true) return;
                MainWebRtcStatusTextBlock.Text = WebRtcStatusTextBlock.Text;
                MainWebRtcStatusTextBlock.Foreground = WebRtcStatusTextBlock.Foreground;
            }
            catch
            {
                // best-effort
            }
        }
        
        private void SaveGeneralSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Загружаем существующие настройки
                AppSettings settings;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                // Сохраняем WebRTC настройки
                settings.UseWebRtcAudio = UseWebRtcCheckBox.IsChecked ?? false;
                settings.WebRtcWsUri = SipEndpointHelper.NormalizeWebRtcWsUri(WebRtcWsUriTextBox.Text.Trim());
                // TURN is stored per-connection (see SaveConnectionSettings_Click / SaveConnection2Settings_Click)

                // IMPORTANT: Recording is available only in WebRTC mode.
                // When WebRTC is disabled, ensure recording is disabled and the UI toggle is reset.
                if (!settings.UseWebRtcAudio)
                {
                    settings.EnableCallRecording = false;
                    _suppressRecordingToggleEvent = true;
                    if (EnableCallRecordingCheckBox != null)
                    {
                        EnableCallRecordingCheckBox.IsChecked = false;
                        UpdateCallRecordingToggleColor();
                    }
                    _suppressRecordingToggleEvent = false;
                }
                
                // ВАЖНО: Сохраняем настройку выбора лида AmoCRM, чтобы она не терялась при сохранении общих настроек
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                }
                
                System.Diagnostics.Debug.WriteLine($"SaveGeneralSettings: Saving UseWebRtcAudio={settings.UseWebRtcAudio}, WebRtcWsUri='{settings.WebRtcWsUri}'");

                // Сохраняем в файл
                string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), settingsJson);
                
                System.Diagnostics.Debug.WriteLine($"SaveGeneralSettings: Settings saved to file");

                // Обновляем статус после сохранения с небольшой задержкой
                Dispatcher.BeginInvoke(new Action(async () => 
                {
                    System.Diagnostics.Debug.WriteLine($"SaveGeneralSettings: Calling UpdateWebRtcStatus after save, TextBox.Text='{WebRtcWsUriTextBox?.Text}'");
                    UpdateWebRtcStatus();
                    
                    // Обновляем индикатор WebRTC в главном окне
                    if (TryGetMainWindow() is MainWindow mainWindow)
                    {
                        mainWindow.UpdateWebRtcIndicator();
                        
                        // Переподключаемся с учетом нового режима (SIP или WebRTC)
                        await mainWindow.ReconnectFromSettingsAsync();
                        
                        // Обновляем статус подключения
                        mainWindow.UpdateConnectionStatus();
                        UpdateConnectionStatus(); // Обновляем статус в окне настроек
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                CustomMessageBox.Show("General settings saved!", "Success", 
                    MessageBoxButton.OK, MessageBoxImage.Information, this);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error saving general settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }

        private void DisableCallRecordingBecauseWebRtcIsOff()
        {
            try
            {
                // Update UI toggle (if available)
                if (EnableCallRecordingCheckBox != null && EnableCallRecordingCheckBox.IsChecked == true)
                {
                    _suppressRecordingToggleEvent = true;
                    EnableCallRecordingCheckBox.IsChecked = false;
                    UpdateCallRecordingToggleColor();
                    _suppressRecordingToggleEvent = false;
                }

                // Persist setting off
                AppSettings settings;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                if (settings.EnableCallRecording)
                {
                    settings.EnableCallRecording = false;
                    // Сохраняем настройку выбора лида AmoCRM
                    if (EnableAmoCrmLeadSelectionCheckBox != null)
                    {
                        settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                    }
                    string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                    File.WriteAllText(AppDataHelper.GetSettingsFilePath(), settingsJson);
                }
            }
            catch
            {
                // best-effort; ignore
            }
        }

        private void SetWebRtcTestButtonsState(bool enabled, string label)
        {
            if (MainTestWebRtcButton != null)
            {
                MainTestWebRtcButton.IsEnabled = enabled;
                MainTestWebRtcButton.Content = label;
            }
            if (TestWebRtcConnectionButton != null)
            {
                TestWebRtcConnectionButton.IsEnabled = enabled;
                TestWebRtcConnectionButton.Content = label;
            }
        }

        private async void TestWebRtcConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetWebRtcTestButtonsState(false, "Testing...");
                
                MainWindow.Log("[WebRTC] ===== Manual connection test initiated =====");
                
                // Останавливаем предыдущую проверку
                StopWebRtcStatusCheck();
                
                // Primary connection: use visible transport toggle + WebSocket field (legacy hidden checkboxes are not synced).
                bool useWebRtc = MainTransportWebRtcRadio?.IsChecked == true;
                string wsUri = MainWsUriTextBox?.Text?.Trim() ?? "";
                
                if (!useWebRtc)
                {
                    CustomMessageBox.Show(
                        "Select WebRTC (not SIP) with the transport toggle on this connection, then try again.",
                        "WebRTC Test",
                        MessageBoxButton.OK, MessageBoxImage.Information, this);
                    SetWebRtcTestButtonsState(true, "Test Connection");
                    return;
                }
                
                if (string.IsNullOrWhiteSpace(wsUri) || wsUri == "wss://" || wsUri == "wss://pbx.example.com:8089/ws")
                {
                    CustomMessageBox.Show("Please enter a valid WebSocket URI.", "WebRTC Test", 
                        MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    SetWebRtcTestButtonsState(true, "Test Connection");
                    return;
                }
                
                // Загружаем настройки для TURN и резервных учётных данных
                AppSettings? settings = null;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                settings ??= new AppSettings();
                
                string? rtcUser = MainWebRtcUsernameTextBox?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(rtcUser))
                    rtcUser = AppSettings.EffectiveMainWebRtcUsername(settings);

                string? sipPassword = MainWebRtcPasswordBox?.Password;
                if (string.IsNullOrWhiteSpace(sipPassword))
                    sipPassword = SipPasswordProvider.GetMainWebRtcPassword(settings);

                if (string.IsNullOrWhiteSpace(rtcUser) || string.IsNullOrWhiteSpace(sipPassword))
                {
                    CustomMessageBox.Show("Please configure WebRTC username and password in the Connection tab (WebRTC section) first.", "WebRTC Test", 
                        MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    SetWebRtcTestButtonsState(true, "Test Connection");
                    return;
                }

                wsUri = SipEndpointHelper.NormalizeWebRtcWsUri(wsUri);
                
                // Показываем начальный статус
                WebRtcStatusTextBlock.Text = "WebRTC Status: Testing connection...";
                WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                SyncMainWebRtcStatusLabelFromLegacy();
                
                // Формируем конфиг
                // Извлекаем адрес PBX из WebSocket URI или используем SipServer
                string pbxAddress = "";
                if (!string.IsNullOrEmpty(wsUri))
                {
                    try
                    {
                        var uri = new Uri(wsUri);
                        pbxAddress = uri.Host; // Извлекаем домен/IP из WebSocket URI
                    }
                    catch
                    {
                        // Если не удалось распарсить WebSocket URI, используем SipServer
                    }
                }
                if (string.IsNullOrEmpty(pbxAddress))
                {
                    pbxAddress = SipEndpointHelper.GetHostOnly(settings.SipServer);
                }
                string username = rtcUser ?? "";
                // For WebRTC, register as "<EXT>-WS" AoR (auth user remains "<EXT>").
                string wsAor = username.EndsWith("-WS", StringComparison.OrdinalIgnoreCase)
                    ? username
                    : $"{username}-WS";
                // Формируем SIP URI: sip:username-WS@pbx_address
                string sipUri = $"sip:{wsAor}@{pbxAddress}";
                
                MainWindow.Log($"[WebRTC] Test config:");
                MainWindow.Log($"[WebRTC]   WebSocket URI: {wsUri}");
                MainWindow.Log($"[WebRTC]   SIP URI: {sipUri}");
                MainWindow.Log($"[WebRTC]   PBX Address: {pbxAddress}");
                MainWindow.Log($"[WebRTC]   Username: {rtcUser} -> AoR={wsAor}");
                
                if (WebRtcService.Main.IsReadyForCalls &&
                    WebRtcService.Main.MatchesActiveRegistration(wsUri, sipUri, rtcUser ?? "", sipPassword ?? ""))
                {
                    MainWindow.Log("[WebRTC] Test skipped: main slot already registered with identical WebRTC config (second REGISTER often gets 401 from PBX).");
                    UpdateWebRtcStatusDisplay(WebRtcConnectionStatus.Registered);
                    SyncMainWebRtcStatusLabelFromLegacy();
                    CustomMessageBox.Show(
                        "The softphone is already connected with WebRTC using these exact settings.\n\n" +
                        "Running \"Test\" starts a second, separate SIP registration for the same extension. Many PBXs reject that with 401 Unauthorized even though the password is correct.\n\n" +
                        "Because your WebSocket URI, SIP identity, username, and password match the live client, the configuration is valid.",
                        "WebRTC Test Result",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information,
                        this);
                    SetWebRtcTestButtonsState(true, "Test Connection");
                    return;
                }
                
                var config = new WebRtcConfig
                {
                    WsUri = wsUri,
                    SipUri = sipUri,
                    Password = sipPassword ?? "",
                    TurnServer = settings.MainWebRtcTurnUri ?? settings.WebRtcTurnUri,
                    TurnUsername = settings.MainWebRtcTurnUsername ?? settings.WebRtcTurnUsername,
                    TurnPassword = TurnPasswordProvider.GetMainTurnPassword(settings)
                };
                
                WebRtcStatusService testService;
                try
                {
                    testService = GetOrCreateSharedWebRtcService();
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[WebRTC] ERROR: Could not initialize WebRtcStatusService: {ex.Message}");
                    CustomMessageBox.Show(
                        "Could not start the WebRTC connection test. Open Settings → Advanced once, then try again.\n\n" +
                        ex.Message,
                        "WebRTC Test",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        this);
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Error (could not start test)";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    SyncMainWebRtcStatusLabelFromLegacy();
                    SetWebRtcTestButtonsState(true, "Test Connection");
                    return;
                }
                bool testCompleted = false;
                WebRtcConnectionStatus finalStatus = WebRtcConnectionStatus.NotConnected;
                
                // Отписываемся от предыдущих обработчиков
                StopWebRtcStatusCheck();
                
                testService.OnStatusChanged += (status) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        MainWindow.Log($"[WebRTC] Test status changed: {status}");
                        finalStatus = status;
                        
                        // Обновляем статус в UI
                        UpdateWebRtcStatusDisplay(status);
                        
                        // Если получили финальный статус (Registered или ошибка), завершаем тест
                        if (status == WebRtcConnectionStatus.Registered || 
                            status == WebRtcConnectionStatus.RegistrationFailed ||
                            status == WebRtcConnectionStatus.Error)
                        {
                            // Проверяем, что тест еще не завершен, чтобы не показывать сообщение дважды
                            if (testCompleted)
                            {
                                MainWindow.Log($"[WebRTC] Test already completed, ignoring status change: {status}");
                                return;
                            }
                            
                            testCompleted = true;
                            
                            // Показываем результат теста
                            string resultMessage = "";
                            MessageBoxImage icon = MessageBoxImage.Information;
                            
                            switch (status)
                            {
                                case WebRtcConnectionStatus.Registered:
                                    resultMessage = $"✓ WebRTC connection test successful!\n\n" +
                                                   $"Connected to: {wsUri}\n" +
                                                   $"Registered as: {sipUri}\n\n" +
                                                   $"WebRTC is ready to use.";
                                    icon = MessageBoxImage.Information;
                                    break;
                                case WebRtcConnectionStatus.RegistrationFailed:
                                    resultMessage = $"✗ WebRTC registration failed.\n\n" +
                                                   $"WebSocket URI: {wsUri}\n" +
                                                   $"SIP URI: {sipUri}\n\n" +
                                                   $"Please check:\n" +
                                                   $"• SIP username and password\n" +
                                                   $"• WebSocket URI is correct\n" +
                                                   $"• Server supports WebRTC endpoints\n" +
                                                   $"• If the main window is already registered as this user, the PBX may reject a second test registration (try testing before connecting, or ignore if calls work).";
                                    icon = MessageBoxImage.Warning;
                                    break;
                                case WebRtcConnectionStatus.Error:
                                    // Показываем ошибку только если не было успешной регистрации
                                    resultMessage = $"✗ WebRTC connection error.\n\n" +
                                                   $"Please check:\n" +
                                                   $"• WebSocket URI is accessible\n" +
                                                   $"• Server supports WebSocket connections\n" +
                                                   $"• Check Debug Output for details";
                                    icon = MessageBoxImage.Error;
                                    break;
                            }
                            
                            // Проверяем, что окно еще открыто перед показом MessageBox
                            if (IsLoaded && IsVisible)
                            {
                                try
                                {
                                    // Используем Dispatcher.BeginInvoke для асинхронного показа сообщения,
                                    // чтобы избежать конфликтов при быстрой смене статусов
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        try
                                        {
                                            CustomMessageBox.Show(resultMessage, "WebRTC Test Result", 
                                                MessageBoxButton.OK, icon, this);
                                        }
                                        catch (Exception msgEx)
                                        {
                                            MainWindow.Log($"[WebRTC] Error showing message box: {msgEx.Message}");
                                            // Показываем результат в статусе вместо MessageBox
                                            WebRtcStatusTextBlock.Text = status == WebRtcConnectionStatus.Registered 
                                                ? "WebRTC Status: Test successful ✓" 
                                                : "WebRTC Status: Test failed ✗";
                                            SyncMainWebRtcStatusLabelFromLegacy();
                                        }
                                    }), System.Windows.Threading.DispatcherPriority.Normal);
                                }
                                catch (Exception msgEx)
                                {
                                    MainWindow.Log($"[WebRTC] Error scheduling message box: {msgEx.Message}");
                                    // Показываем результат в статусе вместо MessageBox
                                    WebRtcStatusTextBlock.Text = status == WebRtcConnectionStatus.Registered 
                                        ? "WebRTC Status: Test successful ✓" 
                                        : "WebRTC Status: Test failed ✗";
                                    SyncMainWebRtcStatusLabelFromLegacy();
                                }
                            }
                            else
                            {
                                MainWindow.Log("[WebRTC] Settings window closed, skipping message box");
                            }
                            
                            // НЕ удаляем сервис - он общий и может использоваться дальше
                            SetWebRtcTestButtonsState(true, "Test Connection");
                        }
                    });
                };
                
                // Запускаем тест с таймаутом
                var testTask = testService.CheckConnectionAsync(config);
                var timeoutTask = System.Threading.Tasks.Task.Delay(30000); // 30 секунд таймаут (увеличено)
                
                var completedTask = await System.Threading.Tasks.Task.WhenAny(testTask, timeoutTask);
                
                if (completedTask == timeoutTask && !testCompleted)
                {
                    // Таймаут - НЕ удаляем сервис, он общий
                    MainWindow.Log("[WebRTC] Test timed out after 30 seconds.");
                    if (IsLoaded && IsVisible)
                    {
                        CustomMessageBox.Show("WebRTC test timed out after 30 seconds.\nThis might indicate:\n• WebSocket server is not accessible\n• Network connectivity issues\n• WebView2 initialization is still in progress",
                            "WebRTC Test Result", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    }
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Test timeout (check connection)";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    SyncMainWebRtcStatusLabelFromLegacy();
                    
                    SetWebRtcTestButtonsState(true, "Test Connection");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] ERROR during manual test: {ex.Message}");
                MainWindow.Log($"[WebRTC] Stack trace: {ex.StackTrace}");
                
                CustomMessageBox.Show($"Error testing WebRTC connection:\n\n{ex.Message}\n\nCheck Debug Output for details.", 
                    "WebRTC Test Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error, this);
                
                SetWebRtcTestButtonsState(true, "Test Connection");
            }
        }

        private void LoadSettings()
        {
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                MainWindow.Log($"[SettingsWindow] LoadSettings: Checking file at {settingsPath}");
                
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    MainWindow.Log($"[SettingsWindow] LoadSettings: File exists, JSON length={json.Length}");
                    
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null)
                    {
                        // Migrate legacy plaintext secrets to encrypted (best-effort)
                        SipPasswordProvider.MigratePlaintextToEncryptedIfNeeded(settingsPath, settings);
                        TurnPasswordProvider.MigratePlaintextToEncryptedIfNeeded(settingsPath, settings);
                        SipPasswordProvider.MigrateEncryptedToDpapiIfNeeded(settingsPath, settings);
                        TurnPasswordProvider.MigrateEncryptedToDpapiIfNeeded(settingsPath, settings);
                        
                        MainWindow.Log($"[SettingsWindow] LoadSettings: Settings loaded - SipServer='{settings.SipServer}', SipUsername='{settings.SipUsername}', SipPasswordEncrypted={(string.IsNullOrEmpty(settings.SipPasswordEncrypted) ? "empty" : "set")}");
                        
                        SipEndpointHelper.ParseStoredSipServer(settings.SipServer, out string server, out int portNum);
                        string port = portNum.ToString(CultureInfo.InvariantCulture);
                        
                        MainWindow.Log($"[SettingsWindow] LoadSettings: Parsed - server='{server}', port='{port}'");
                        
                        // Сохраняем значения для установки
                        string finalServer = server;
                        string finalPort = port;
                        string finalUsername = settings.SipUsername ?? "";
                        string finalPassword = SipPasswordProvider.GetPassword(settings) ?? "";
                        
                        // Устанавливаем значения напрямую, так как мы уже в UI потоке (вызвано из Loaded)
                        if (SipServerTextBox != null)
                        {
                            SipServerTextBox.Text = finalServer;
                            MainWindow.Log($"[SettingsWindow] LoadSettings: Set SipServerTextBox.Text='{SipServerTextBox.Text}'");
                        }
                        else
                        {
                            MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - SipServerTextBox is null!");
                        }
                        
                        if (SipPortTextBox != null)
                        {
                            SipPortTextBox.Text = finalPort;
                            MainWindow.Log($"[SettingsWindow] LoadSettings: Set SipPortTextBox.Text='{SipPortTextBox.Text}'");
                        }
                        else
                        {
                            MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - SipPortTextBox is null!");
                        }
                        
                        if (SipUsernameTextBox != null)
                        {
                            SipUsernameTextBox.Text = finalUsername;
                            MainWindow.Log($"[SettingsWindow] LoadSettings: Set SipUsernameTextBox.Text='{SipUsernameTextBox.Text}'");
                        }
                        else
                        {
                            MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - SipUsernameTextBox is null!");
                        }
                        
                        if (SipPasswordBox != null)
                        {
                            SipPasswordBox.Password = finalPassword;
                            MainWindow.Log($"[SettingsWindow] LoadSettings: Set SipPasswordBox.Password (length={SipPasswordBox.Password.Length})");
                        }
                        else
                        {
                            MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - SipPasswordBox is null!");
                        }

                        if (MainWebRtcUsernameTextBox != null)
                        {
                            string u = settings.WebRtcUsername ?? "";
                            if (string.IsNullOrWhiteSpace(u)) u = settings.SipUsername ?? "";
                            MainWebRtcUsernameTextBox.Text = u;
                        }
                        if (MainWebRtcPasswordBox != null)
                        {
                            MainWebRtcPasswordBox.Password = ResolveWebRtcPasswordForUi(settings) ?? "";
                        }
                        
                        if (MainConnectionNameTextBox != null)
                        {
                            MainConnectionNameTextBox.Text = settings.MainConnectionName ?? "";
                        }

                        if (SipTlsCheckBox != null)
                        {
                            SipTlsCheckBox.IsChecked = settings.SipUseTls;
                        }
                        if (SipSrtpCheckBox != null)
                        {
                            SipSrtpCheckBox.IsChecked = settings.SipUseSrtp;
                        }

                        // Transport toggle for primary connection. Legacy UseWebRtcAudio is honored
                        // when MainConnectionTransport is unset (first-run migration).
                        bool mainUsesWebRtc;
                        if (!string.IsNullOrWhiteSpace(settings.MainConnectionTransport))
                        {
                            mainUsesWebRtc = string.Equals(settings.MainConnectionTransport, "WebRtc", StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            mainUsesWebRtc = settings.UseWebRtcAudio;
                        }
                        if (MainTransportSipRadio != null) MainTransportSipRadio.IsChecked = !mainUsesWebRtc;
                        if (MainTransportWebRtcRadio != null) MainTransportWebRtcRadio.IsChecked = mainUsesWebRtc;
                        if (MainWsUriTextBox != null)
                        {
                            string ws = settings.WebRtcWsUri ?? "";
                            MainWsUriTextBox.Text = string.IsNullOrEmpty(ws) ? "wss://" : ws;
                        }
                        ApplyMainTransportVisibility(mainUsesWebRtc);
                        UpdateMainWebRtcStatusText();

                        // Per-connection TURN (main). Fallback to legacy WebRtcTurn* for migration.
                        if (MainTurnUriTextBox != null)
                            MainTurnUriTextBox.Text = settings.MainWebRtcTurnUri ?? settings.WebRtcTurnUri ?? "";
                        if (MainTurnUsernameTextBox != null)
                            MainTurnUsernameTextBox.Text = settings.MainWebRtcTurnUsername ?? settings.WebRtcTurnUsername ?? "";
                        if (MainTurnPasswordBox != null)
                            MainTurnPasswordBox.Password = TurnPasswordProvider.GetMainTurnPassword(settings) ?? "";
                        ApplyTurnUiFromStoredUri(isMain: true, storedUri: settings.MainWebRtcTurnUri ?? settings.WebRtcTurnUri);
                        // Run an explicit status refresh after fields are fully populated from disk.
                        ScheduleTurnStatusCheck(isMain: true);

                        // Загружаем настройки второго подключения
                        if (!string.IsNullOrEmpty(settings.SipServer2) || !string.IsNullOrEmpty(settings.SipUsername2) ||
                            AppSettings.HasMeaningfulWebRtcWsUri(settings.WebRtcWsUri2) ||
                            !string.IsNullOrEmpty(settings.WebRtcUsername2))
                        {
                            _suppressSecondaryTransportPersist = true;
                            try
                            {
                            // Парсим server:port для второго подключения
                            SipEndpointHelper.ParseStoredSipServer(settings.SipServer2, out string server2, out int portNum2);
                            string port2 = portNum2.ToString(CultureInfo.InvariantCulture);

                            if (SipServer2TextBox != null)
                            {
                                SipServer2TextBox.Text = server2;
                            }
                            if (SipPort2TextBox != null)
                            {
                                SipPort2TextBox.Text = port2;
                            }
                            if (SipUsername2TextBox != null)
                            {
                                SipUsername2TextBox.Text = settings.SipUsername2 ?? "";
                            }
                            if (SipPassword2Box != null && !string.IsNullOrEmpty(settings.SipPasswordEncrypted2))
                            {
                                try
                                {
                                    SipPassword2Box.Password = TokenEncryption.Decrypt(settings.SipPasswordEncrypted2);
                                }
                                catch (Exception ex)
                                {
                                    MainWindow.Log($"[SettingsWindow] LoadSettings: Error decrypting password2: {ex.Message}");
                                }
                            }
                            
                            if (SecondaryConnectionNameTextBox != null)
                            {
                                SecondaryConnectionNameTextBox.Text = settings.SecondaryConnectionName ?? "";
                            }

                            if (SipTls2CheckBox != null)
                            {
                                SipTls2CheckBox.IsChecked = settings.SipUseTls2;
                            }
                            if (SipSrtp2CheckBox != null)
                            {
                                SipSrtp2CheckBox.IsChecked = settings.SipUseSrtp2;
                            }

                            // Secondary transport toggle (disk flag + WebRTC-only legacy inference)
                            bool secondaryUsesWebRtc = AppSettings.SecondaryLineUsesWebRtc(settings);
                            if (SecondaryTransportSipRadio != null) SecondaryTransportSipRadio.IsChecked = !secondaryUsesWebRtc;
                            if (SecondaryTransportWebRtcRadio != null) SecondaryTransportWebRtcRadio.IsChecked = secondaryUsesWebRtc;
                            if (SecondaryWsUriTextBox != null)
                            {
                                string ws2 = settings.WebRtcWsUri2 ?? "";
                                SecondaryWsUriTextBox.Text = string.IsNullOrEmpty(ws2) ? "wss://" : ws2;
                            }
                            if (SecondaryWebRtcUsername2TextBox != null)
                            {
                                string u2 = settings.WebRtcUsername2 ?? "";
                                if (string.IsNullOrWhiteSpace(u2)) u2 = settings.SipUsername2 ?? "";
                                SecondaryWebRtcUsername2TextBox.Text = u2;
                            }
                            if (SecondaryWebRtcPassword2Box != null)
                            {
                                SecondaryWebRtcPassword2Box.Password = "";
                                string? encW2 = settings.WebRtcPasswordEncrypted2;
                                if (string.IsNullOrWhiteSpace(encW2)) encW2 = settings.SipPasswordEncrypted2;
                                if (!string.IsNullOrEmpty(encW2))
                                {
                                    try
                                    {
                                        SecondaryWebRtcPassword2Box.Password = TokenEncryption.Decrypt(encW2);
                                    }
                                    catch (Exception ex)
                                    {
                                        MainWindow.Log($"[SettingsWindow] LoadSettings: Error decrypting WebRTC password2: {ex.Message}");
                                    }
                                }
                            }
                            ApplySecondaryTransportVisibility(secondaryUsesWebRtc);

                            // Показываем секцию второго подключения и скрываем кнопку "Add"
                            if (SecondConnectionGrid != null)
                            {
                                SecondConnectionGrid.Visibility = Visibility.Visible;
                                AddAnotherConnectionButton.Visibility = Visibility.Collapsed;
                            }
                            }
                            finally
                            {
                                _suppressSecondaryTransportPersist = false;
                            }
                        }
                    }
                    else
                    {
                        MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - Settings deserialized as null!");
                    }
                }
                else
                {
                    MainWindow.Log($"[SettingsWindow] LoadSettings: File does not exist at {settingsPath}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - {ex.Message}");
                MainWindow.Log($"[SettingsWindow] LoadSettings: Stack trace - {ex.StackTrace}");
                CustomMessageBox.Show($"Error loading settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
            }
            finally
            {
                // Убеждаемся, что поля доступны для редактирования после загрузки
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (SipServerTextBox != null)
                    {
                        SipServerTextBox.IsReadOnly = false;
                        SipServerTextBox.IsEnabled = true;
                        SipServerTextBox.Focusable = true;
                    }
                    if (SipPortTextBox != null)
                    {
                        SipPortTextBox.IsReadOnly = false;
                        SipPortTextBox.IsEnabled = true;
                        SipPortTextBox.Focusable = true;
                    }
                    if (SipUsernameTextBox != null)
                    {
                        SipUsernameTextBox.IsReadOnly = false;
                        SipUsernameTextBox.IsEnabled = true;
                        SipUsernameTextBox.Focusable = true;
                    }
                    if (SipPasswordBox != null)
                    {
                        SipPasswordBox.IsEnabled = true;
                        SipPasswordBox.Focusable = true;
                    }
                    
                    // Обновляем статус подключения после загрузки настроек
                    // Это гарантирует, что статус синхронизирован с MainWindow
                    UpdateConnectionStatus();
                    UpdateConnection2Status();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>Обработчик переключения транспорта основного подключения (SIP/WebRTC).</summary>
        private void MainTransportRadio_Changed(object sender, RoutedEventArgs e)
        {
            bool webRtc = MainTransportWebRtcRadio?.IsChecked == true;
            ApplyMainTransportVisibility(webRtc);
            UpdateMainWebRtcStatusText();
        }

        /// <summary>Обработчик переключения транспорта второго подключения (SIP/WebRTC).</summary>
        private async void SecondaryTransportRadio_Changed(object sender, RoutedEventArgs e)
        {
            bool webRtc = SecondaryTransportWebRtcRadio?.IsChecked == true;
            if (_suppressSecondaryTransportPersist)
                return;

            ApplySecondaryTransportVisibility(webRtc);
            _secondaryConnectionIssueDetail = null;
            if (SecondaryConnectionIssueButton != null)
                SecondaryConnectionIssueButton.Visibility = Visibility.Collapsed;

            // Debounce: rapid SIP/WebRTC toggles used to queue many full teardown/rebuild cycles and could wedge the UI.
            try
            {
                _secondaryTransportReinitCts?.Cancel();
                _secondaryTransportReinitCts?.Dispose();
            }
            catch { }
            _secondaryTransportReinitCts = new CancellationTokenSource();
            var token = _secondaryTransportReinitCts.Token;

            try
            {
                await Task.Delay(450, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
                return;

            try
            {
                string path = AppDataHelper.GetSettingsFilePath();
                AppSettings settings;
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                else
                    settings = new AppSettings();

                bool finalWebRtc = SecondaryTransportWebRtcRadio?.IsChecked == true;
                settings.SecondaryConnectionTransport = finalWebRtc ? "WebRtc" : "Sip";
                File.WriteAllText(path, JsonConvert.SerializeObject(settings, Formatting.Indented));

                if (TryGetMainWindow() is MainWindow mainWindow)
                    await mainWindow.InitializeSecondConnectionAsync().ConfigureAwait(true);

                UpdateConnection2Status();
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] SecondaryTransportRadio_Changed: {ex.Message}");
            }
        }

        private void ApplyMainTransportVisibility(bool webRtc)
        {
            if (MainSipFields != null)
                MainSipFields.Visibility = webRtc ? Visibility.Collapsed : Visibility.Visible;
            if (MainWebRtcFields != null)
                MainWebRtcFields.Visibility = webRtc ? Visibility.Visible : Visibility.Collapsed;
            // TURN expander is only meaningful for WebRTC.
            if (MainTurnSettingsExpander != null)
                MainTurnSettingsExpander.Visibility = webRtc ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplySecondaryTransportVisibility(bool webRtc)
        {
            if (SecondarySipFields != null)
                SecondarySipFields.Visibility = webRtc ? Visibility.Collapsed : Visibility.Visible;
            if (SecondaryWebRtcFields != null)
                SecondaryWebRtcFields.Visibility = webRtc ? Visibility.Visible : Visibility.Collapsed;
            if (SecondaryTransportBadgeText != null)
                SecondaryTransportBadgeText.Text = webRtc ? "WebRTC" : "SIP";
        }

        private void UpdateMainWebRtcStatusText()
        {
            if (MainWebRtcStatusTextBlock == null || MainWsUriTextBox == null || MainTestWebRtcButton == null)
                return;

            string wsUri = MainWsUriTextBox.Text?.Trim() ?? "";
            bool webRtcSelected = MainTransportWebRtcRadio?.IsChecked == true;
            bool hasUri = !string.IsNullOrEmpty(wsUri) && wsUri != "wss://";
            if (!webRtcSelected)
            {
                MainWebRtcStatusTextBlock.Text = "WebRTC Status: SIP transport selected";
                MainTestWebRtcButton.IsEnabled = false;
            }
            else if (!hasUri)
            {
                MainWebRtcStatusTextBlock.Text = "WebRTC Status: Not configured";
                MainTestWebRtcButton.IsEnabled = false;
            }
            else
            {
                MainWebRtcStatusTextBlock.Text = "WebRTC Status: Ready to test";
                MainTestWebRtcButton.IsEnabled = true;
            }
        }

        private static string? ResolveWebRtcPasswordForUi(AppSettings settings)
        {
            return SipPasswordProvider.GetMainWebRtcPassword(settings);
        }

        private async void SaveConnectionSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool useWebRtc = MainTransportWebRtcRadio?.IsChecked == true;
                string username = SipUsernameTextBox.Text.Trim();
                string password = SipPasswordBox.Password;
                string rawServer = SipServerTextBox?.Text?.Trim() ?? "";
                string server = SipEndpointHelper.NormalizeServerInput(rawServer, out int? portFromUrl);
                bool pastedUrl = rawServer.Contains("://", StringComparison.Ordinal);
                string port = SipPortTextBox?.Text?.Trim() ?? "5060";
                string wsUriRaw = MainWsUriTextBox?.Text?.Trim() ?? "";

                if (useWebRtc)
                {
                    string wUser = MainWebRtcUsernameTextBox?.Text?.Trim() ?? "";
                    string wPass = MainWebRtcPasswordBox?.Password ?? "";
                    if (string.IsNullOrEmpty(wUser) || string.IsNullOrEmpty(wPass))
                    {
                        CustomMessageBox.Show("Please enter WebRTC username and password.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                        return;
                    }
                    if (string.IsNullOrEmpty(wsUriRaw) || wsUriRaw == "wss://")
                    {
                        CustomMessageBox.Show("Please enter a valid WebSocket URI (wss://...).", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                        return;
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                    {
                        CustomMessageBox.Show("Please enter SIP username and password.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                        return;
                    }
                    if (string.IsNullOrEmpty(server))
                    {
                        CustomMessageBox.Show("Please enter the SIP server address.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                        return;
                    }
                }

                int portNum = SipEndpointHelper.DefaultSipPort;
                if (!useWebRtc)
                {
                    if (pastedUrl && portFromUrl.HasValue)
                        portNum = portFromUrl.Value;
                    else if (int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort) && parsedPort >= 1 && parsedPort <= 65535)
                        portNum = parsedPort;
                    else if (portFromUrl.HasValue)
                        portNum = portFromUrl.Value;
                    else
                    {
                        CustomMessageBox.Show("Invalid port number. Using default port 5060.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    }
                }

                AppSettings settings;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string existingJson = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    settings = JsonConvert.DeserializeObject<AppSettings>(existingJson) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                // Transport selection (also keep legacy UseWebRtcAudio synced for compat).
                settings.MainConnectionTransport = useWebRtc ? "WebRtc" : "Sip";
                settings.UseWebRtcAudio = useWebRtc;

                if (useWebRtc)
                {
                    settings.WebRtcWsUri = SipEndpointHelper.NormalizeWebRtcWsUri(wsUriRaw);
                    string rtcUser = MainWebRtcUsernameTextBox?.Text?.Trim() ?? "";
                    // MikoPBX auth uses bare extension; AoR gets -WS appended in GetWebRtcConfigForConnection.
                    if (rtcUser.EndsWith("-WS", StringComparison.OrdinalIgnoreCase))
                        rtcUser = rtcUser[..^3];
                    settings.WebRtcUsername = rtcUser;
                    string rtcPass = (MainWebRtcPasswordBox?.Password ?? "").Trim();
                    settings.WebRtcPasswordEncrypted = TokenEncryption.Encrypt(rtcPass);
                    // Keep SIP password in sync (UI says same creds for both transports).
                    settings.SipPasswordEncrypted = TokenEncryption.Encrypt(rtcPass);
                    settings.SipPassword = null;
                    if (!string.IsNullOrEmpty(rtcUser))
                        settings.SipUsername = rtcUser;
                }
                else
                {
                    settings.SipServer = $"{server}:{portNum}";
                    settings.SipUseTls = SipTlsCheckBox?.IsChecked ?? false;
                    settings.SipUseSrtp = SipSrtpCheckBox?.IsChecked ?? false;
                    settings.SipUsername = username;
                    string sipPass = password.Trim();
                    settings.SipPasswordEncrypted = TokenEncryption.Encrypt(sipPass);
                    settings.SipPassword = null;
                    // Keep WebRTC password in sync so switching transport does not reuse a stale secret.
                    settings.WebRtcPasswordEncrypted = TokenEncryption.Encrypt(sipPass);
                    if (!string.IsNullOrEmpty(username))
                        settings.WebRtcUsername = username.EndsWith("-WS", StringComparison.OrdinalIgnoreCase)
                            ? username[..^3]
                            : username;
                }

                settings.MainConnectionName = MainConnectionNameTextBox?.Text?.Trim();

                // TURN is saved via the dedicated TURN button in Turn settings.
                
                // Сохраняем настройку выбора лида AmoCRM
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                }

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), json);

                SaveAndConnectButton.IsEnabled = false;
                UpdateConnectionStatus();

                // Уведомляем главное окно о необходимости переподключения
                if (TryGetMainWindow() is MainWindow mainWindow)
                {
                    await mainWindow.ReconnectFromSettingsAsync();
                    
                    // Обновляем статус после переподключения
                    UpdateConnectionStatus();
                    
                    // Обновляем названия в сплит-кнопке
                    mainWindow.UpdateCallButtonMode();
                }
                else
                {
                    UpdateConnectionStatus();
                }

                SaveAndConnectButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                ConnectionStatusTextBlock.Text = $"Error: {ex.Message}";
                ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                if (PrimaryConnectionStatusPill != null)
                    PrimaryConnectionStatusPill.Background = (System.Windows.Media.Brush)FindResource("ConnStatusPillErrBrush");
                if (PrimaryConnectionStatusDot != null)
                    PrimaryConnectionStatusDot.Fill = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                SaveAndConnectButton.IsEnabled = true;
                CustomMessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }

        private async void SaveTurnSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SaveAndConnectTurnButton != null)
                    SaveAndConnectTurnButton.IsEnabled = false;

                // Load existing settings and update ONLY main TURN fields.
                var settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings settings;
                if (File.Exists(settingsPath))
                {
                    string existingJson = File.ReadAllText(settingsPath);
                    settings = JsonConvert.DeserializeObject<AppSettings>(existingJson) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                settings.MainWebRtcTurnUri = BuildTurnUriFromUi(isMain: true);
                settings.MainWebRtcTurnUsername = MainTurnUsernameTextBox?.Text?.Trim();
                TurnPasswordProvider.SetMainTurnPassword(settings, MainTurnPasswordBox?.Password);

                // Keep legacy TURN cleared to avoid split-brain configs.
                settings.WebRtcTurnUri = null;
                settings.WebRtcTurnUsername = null;
                settings.WebRtcTurnPassword = null;

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsPath, json);

                // Refresh status in UI and reconnect to apply new TURN immediately.
                ScheduleTurnStatusCheck(isMain: true);

                if (TryGetMainWindow() is MainWindow mainWindow)
                {
                    await mainWindow.ReconnectFromSettingsAsync();
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error saving TURN settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
            finally
            {
                if (SaveAndConnectTurnButton != null)
                    SaveAndConnectTurnButton.IsEnabled = true;
            }
        }

        private void AddAnotherConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            // Показываем карточку второго подключения и скрываем кнопку "Add"
            // (ограничиваемся двумя подключениями максимум)
            if (SecondConnectionGrid != null)
            {
                SecondConnectionGrid.Visibility = Visibility.Visible;
                AddAnotherConnectionButton.Visibility = Visibility.Collapsed;
            }
        }

        private async void SaveConnection2Settings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool useWebRtc = SecondaryTransportWebRtcRadio?.IsChecked == true;
                string sipUsername2 = SipUsername2TextBox?.Text?.Trim() ?? "";
                string sipPassword2 = SipPassword2Box.Password;
                string webRtcUser2 = SecondaryWebRtcUsername2TextBox?.Text?.Trim() ?? "";
                string webRtcPass2 = SecondaryWebRtcPassword2Box.Password;
                string rawServer2 = SipServer2TextBox?.Text?.Trim() ?? "";
                string server = SipEndpointHelper.NormalizeServerInput(rawServer2, out int? portFromUrl2);
                bool pastedUrl2 = rawServer2.Contains("://", StringComparison.Ordinal);
                string port = SipPort2TextBox?.Text?.Trim() ?? "5060";
                string wsUriRaw = SecondaryWsUriTextBox?.Text?.Trim() ?? "";

                if (useWebRtc)
                {
                    if (string.IsNullOrEmpty(webRtcUser2) || string.IsNullOrEmpty(webRtcPass2))
                    {
                        CustomMessageBox.Show("Please enter WebRTC username and password for the secondary connection.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                        return;
                    }
                    if (string.IsNullOrEmpty(wsUriRaw) || wsUriRaw == "wss://")
                    {
                        CustomMessageBox.Show("Please enter a valid WebSocket URI (wss://...) for the secondary connection.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                        return;
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(sipUsername2) || string.IsNullOrEmpty(sipPassword2))
                    {
                        CustomMessageBox.Show("Please enter SIP username and password for the secondary connection.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                        return;
                    }
                    if (string.IsNullOrEmpty(server))
                    {
                        CustomMessageBox.Show("Please enter the SIP server address for the secondary connection.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                        return;
                    }
                }

                int portNum = SipEndpointHelper.DefaultSipPort;
                string serverWithPort = "";
                if (!useWebRtc)
                {
                    if (pastedUrl2 && portFromUrl2.HasValue)
                        portNum = portFromUrl2.Value;
                    else if (int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort2) && parsedPort2 >= 1 && parsedPort2 <= 65535)
                        portNum = parsedPort2;
                    else if (portFromUrl2.HasValue)
                        portNum = portFromUrl2.Value;
                    else
                    {
                        CustomMessageBox.Show("Invalid port number. Using default port 5060.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    }
                    serverWithPort = $"{server}:{portNum}";

                    // Conflict check is only meaningful for SIP-SIP combinations.
                    string mainServer = SipServerTextBox?.Text?.Trim() ?? "";
                    string mainPort = SipPortTextBox?.Text?.Trim() ?? "5060";
                    string mainServerWithPort = $"{mainServer}:{mainPort}";
                    string mainUsername = SipUsernameTextBox?.Text?.Trim() ?? "";
                    bool mainIsSip = MainTransportSipRadio?.IsChecked == true;

                    if (mainIsSip && serverWithPort.Equals(mainServerWithPort, StringComparison.OrdinalIgnoreCase) &&
                        sipUsername2.Equals(mainUsername, StringComparison.OrdinalIgnoreCase))
                    {
                        var result = CustomMessageBox.Show(
                            "Warning: You are using the same server and username as the main connection.\n\n" +
                            "This may cause registration conflicts (403 Forbidden error).\n\n" +
                            "For the second connection, you should use:\n" +
                            "• A different username on the same server, OR\n" +
                            "• A different server\n\n" +
                            "Do you want to continue anyway?",
                            "Registration Conflict Warning",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning,
                            this);

                        if (result == MessageBoxResult.No)
                        {
                            return;
                        }
                    }
                }

                AppSettings settings;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string existingJson = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    settings = JsonConvert.DeserializeObject<AppSettings>(existingJson) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                settings.SecondaryConnectionTransport = useWebRtc ? "WebRtc" : "Sip";

                if (useWebRtc)
                {
                    settings.WebRtcWsUri2 = SipEndpointHelper.NormalizeWebRtcWsUri(wsUriRaw);
                    settings.SipServer2 = null;
                    settings.RtpServer2 = null;
                    settings.WebRtcUsername2 = webRtcUser2;
                    settings.WebRtcPasswordEncrypted2 = TokenEncryption.Encrypt(webRtcPass2);
                }
                else
                {
                    settings.SipServer2 = serverWithPort;
                    settings.SipUseTls2 = SipTls2CheckBox?.IsChecked ?? false;
                    settings.SipUseSrtp2 = SipSrtp2CheckBox?.IsChecked ?? false;
                    settings.WebRtcWsUri2 = null;
                    settings.WebRtcUsername2 = null;
                    settings.WebRtcPasswordEncrypted2 = null;
                    settings.SecondaryWebRtcTurnUri = null;
                    settings.SecondaryWebRtcTurnUsername = null;
                    TurnPasswordProvider.ClearSecondaryTurnPassword(settings);
                    settings.SipUsername2 = sipUsername2;
                    settings.SipPasswordEncrypted2 = TokenEncryption.Encrypt(sipPassword2);
                }
                settings.SecondaryConnectionName = SecondaryConnectionNameTextBox?.Text?.Trim();

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), json);

                SaveAndConnect2Button.IsEnabled = false;
                UpdateConnection2Status();

                // Уведомляем главное окно о необходимости переподключения
                if (TryGetMainWindow() is MainWindow mainWindow)
                {
                    await mainWindow.InitializeSecondConnectionAsync();
                    
                    // Обновляем статус после переподключения
                    UpdateConnection2Status();
                    
                    // Обновляем названия в сплит-кнопке
                    mainWindow.UpdateCallButtonMode();
                }
                else
                {
                    UpdateConnection2Status();
                }

                SaveAndConnect2Button.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Connection2StatusTextBlock.Text = $"Error: {ex.Message}";
                Connection2StatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                if (SecondaryConnectionStatusPill != null)
                    SecondaryConnectionStatusPill.Background = (System.Windows.Media.Brush)FindResource("ConnStatusPillErrBrush");
                if (SecondaryConnectionStatusDot != null)
                    SecondaryConnectionStatusDot.Fill = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                SaveAndConnect2Button.IsEnabled = true;
                CustomMessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }

        private void UpdateConnection2Status()
        {
            try
            {
                if (TryGetMainWindow() is MainWindow mainWindow)
                {
                    bool isConnected2 = mainWindow.IsSecondConnectionConnected();
                    if (Connection2StatusTextBlock == null)
                        return;
                    if (isConnected2)
                    {
                        Connection2StatusTextBlock.Text = "Connected";
                        Connection2StatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                        if (SecondaryConnectionStatusPill != null)
                            SecondaryConnectionStatusPill.Background = (System.Windows.Media.Brush)FindResource("ConnStatusPillOkBrush");
                        if (SecondaryConnectionStatusDot != null)
                            SecondaryConnectionStatusDot.Fill = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                        if (SecondaryConnectionIssueButton != null)
                            SecondaryConnectionIssueButton.Visibility = Visibility.Collapsed;
                        _secondaryConnectionIssueDetail = null;
                    }
                    else
                    {
                        Connection2StatusTextBlock.Text = "Disconnected";
                        Connection2StatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                        if (SecondaryConnectionStatusPill != null)
                            SecondaryConnectionStatusPill.Background = (System.Windows.Media.Brush)FindResource("ConnStatusPillIdleBrush");
                        if (SecondaryConnectionStatusDot != null)
                            SecondaryConnectionStatusDot.Fill = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                        if (SecondaryConnectionIssueButton != null)
                        {
                            SecondaryConnectionIssueButton.Visibility = string.IsNullOrEmpty(_secondaryConnectionIssueDetail)
                                ? Visibility.Collapsed
                                : Visibility.Visible;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] UpdateConnection2Status error: {ex.Message}");
            }
        }

        private void DeleteConnection2_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = CustomMessageBox.Show(
                    "Are you sure you want to remove the additional connection?\nThis will disconnect and delete all its settings.",
                    "Remove Connection",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    this);

                if (result != MessageBoxResult.Yes)
                    return;

                // 1. Disconnect second SIP service on MainWindow
                if (TryGetMainWindow() is MainWindow mainWindow)
                {
                    mainWindow.DisconnectSecondConnection();
                    // Обновляем названия в сплит-кнопке
                    mainWindow.UpdateCallButtonMode();
                }

                // 2. Clear settings from file
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();

                    settings.SipServer2 = null;
                    settings.SipUsername2 = null;
                    settings.SipPasswordEncrypted2 = null;
                    settings.RtpServer2 = null;
                    settings.WebRtcUsername2 = null;
                    settings.WebRtcPasswordEncrypted2 = null;
                    settings.WebRtcWsUri2 = null;
                    settings.SecondaryConnectionName = null;
                    settings.SecondaryWebRtcTurnUri = null;
                    settings.SecondaryWebRtcTurnUsername = null;
                    TurnPasswordProvider.ClearSecondaryTurnPassword(settings);
                    settings.SecondaryConnectionTransport = "Sip";
                    // Legacy cleanup from older builds that stored TURN for Secondary.

                    string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                    File.WriteAllText(settingsPath, updatedJson);

                    MainWindow.Log("[SettingsWindow] Second connection settings removed from settings.json");
                }

                // 3. Clear UI fields
                if (SipServer2TextBox != null) SipServer2TextBox.Text = "";
                if (SipUsername2TextBox != null) SipUsername2TextBox.Text = "";
                if (SipPassword2Box != null) SipPassword2Box.Password = "";
                if (SecondaryWebRtcUsername2TextBox != null) SecondaryWebRtcUsername2TextBox.Text = "";
                if (SecondaryWebRtcPassword2Box != null) SecondaryWebRtcPassword2Box.Password = "";
                if (SecondaryWsUriTextBox != null) SecondaryWsUriTextBox.Text = "wss://";
                if (SecondaryTransportSipRadio != null) SecondaryTransportSipRadio.IsChecked = true;
                if (SecondaryTransportWebRtcRadio != null) SecondaryTransportWebRtcRadio.IsChecked = false;
                ApplySecondaryTransportVisibility(webRtc: false);

                // 4. Collapse the card & show "Add" button again
                if (SecondConnectionGrid != null)
                    SecondConnectionGrid.Visibility = Visibility.Collapsed;
                if (AddAnotherConnectionButton != null)
                    AddAnotherConnectionButton.Visibility = Visibility.Visible;

                MainWindow.Log("[SettingsWindow] Additional connection removed successfully");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] DeleteConnection2_Click error: {ex.Message}");
                CustomMessageBox.Show($"Error removing connection: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private async System.Threading.Tasks.Task LoadAudioDevicesAsync()
        {
            try
            {
                // Показываем индикатор загрузки
                Dispatcher.Invoke(() =>
                {
                    MicrophoneComboBox.IsEnabled = false;
                    SpeakerComboBox.IsEnabled = false;
                    if (RingtoneDeviceComboBox != null) RingtoneDeviceComboBox.IsEnabled = false;
                    // Показываем placeholder во время загрузки
                    var loadingPlaceholder = new List<AudioDeviceInfo> { new AudioDeviceInfo { Name = "Loading...", DeviceNumber = -1 } };
                    MicrophoneComboBox.ItemsSource = loadingPlaceholder;
                    SpeakerComboBox.ItemsSource = loadingPlaceholder;
                });
                
                // Загружаем устройства в фоновом потоке параллельно
                var microphonesTask = System.Threading.Tasks.Task.Run(() => AudioDeviceHelper.GetMicrophones());
                var speakersTask = System.Threading.Tasks.Task.Run(() => AudioDeviceHelper.GetSpeakers());
                var ringtoneDevicesTask = System.Threading.Tasks.Task.Run(() => RingtoneDeviceHelper.GetOutputDevices());
                
                // Ждем завершения обеих задач
                await System.Threading.Tasks.Task.WhenAll(microphonesTask, speakersTask, ringtoneDevicesTask);
                
                var microphones = await microphonesTask;
                var speakers = await speakersTask;
                var ringtoneDevices = await ringtoneDevicesTask;
                
                // Обновляем UI в UI потоке
                Dispatcher.Invoke(() =>
                {
                    MicrophoneComboBox.ItemsSource = microphones;
                    SpeakerComboBox.ItemsSource = speakers;
                    MicrophoneComboBox.IsEnabled = true;
                    SpeakerComboBox.IsEnabled = true;

                    if (RingtoneDeviceComboBox != null)
                    {
                        RingtoneDeviceComboBox.ItemsSource = ringtoneDevices;
                        RingtoneDeviceComboBox.IsEnabled = true;
                    }
                    
                    // Выбираем сохраненные устройства
                    LoadAudioSettings();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    MicrophoneComboBox.IsEnabled = true;
                    SpeakerComboBox.IsEnabled = true;
                    if (RingtoneDeviceComboBox != null) RingtoneDeviceComboBox.IsEnabled = true;
                    CustomMessageBox.Show($"Error loading audio devices: {ex.Message}", "Error", 
                        MessageBoxButton.OK, MessageBoxImage.Warning, this);
                });
            }
        }
        
        private void LoadAudioDevices()
        {
            // Синхронная версия для обратной совместимости (если где-то еще используется)
            _ = LoadAudioDevicesAsync();
        }

        private void LoadAudioSettings()
        {
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null)
                    {
                        // Выбираем микрофон
                        if (MicrophoneComboBox.ItemsSource != null && 
                            !string.IsNullOrEmpty(settings.MicrophoneDeviceGuid))
                        {
                            foreach (AudioDeviceInfo device in MicrophoneComboBox.ItemsSource)
                            {
                                if (device.Guid == settings.MicrophoneDeviceGuid)
                                {
                                    MicrophoneComboBox.SelectedItem = device;
                                    break;
                                }
                            }
                        }
                        else if (MicrophoneComboBox.ItemsSource != null && MicrophoneComboBox.Items.Count > 0)
                        {
                            // Выбираем устройство по умолчанию, если ничего не выбрано
                            MicrophoneComboBox.SelectedIndex = 0;
                        }

                        // Выбираем динамик
                        if (SpeakerComboBox.ItemsSource != null && 
                            !string.IsNullOrEmpty(settings.SpeakerDeviceGuid))
                        {
                            foreach (AudioDeviceInfo device in SpeakerComboBox.ItemsSource)
                            {
                                if (device.Guid == settings.SpeakerDeviceGuid)
                                {
                                    SpeakerComboBox.SelectedItem = device;
                                    break;
                                }
                            }
                        }
                        else if (SpeakerComboBox.ItemsSource != null && SpeakerComboBox.Items.Count > 0)
                        {
                            // Выбираем устройство по умолчанию, если ничего не выбрано
                            SpeakerComboBox.SelectedIndex = 0;
                        }

                        // Загружаем настройки кодеков
                        if (AudioCodecComboBox != null)
                        {
                            string codec = settings.AudioCodec ?? "PCMU";
                            foreach (ComboBoxItem item in AudioCodecComboBox.Items)
                            {
                                if (item.Tag?.ToString() == codec)
                                {
                                    AudioCodecComboBox.SelectedItem = item;
                                    break;
                                }
                            }
                        }

                        if (SampleRateComboBox != null)
                        {
                            int sampleRate = settings.AudioSampleRate > 0 ? settings.AudioSampleRate : 16000;
                            foreach (ComboBoxItem item in SampleRateComboBox.Items)
                            {
                                if (item.Tag != null && int.TryParse(item.Tag.ToString(), out int rate) && rate == sampleRate)
                                {
                                    SampleRateComboBox.SelectedItem = item;
                                    break;
                                }
                            }
                        }

                        // Ringtone settings
                        if (RingtoneSoundComboBox != null)
                        {
                            string mode = (settings.RingtoneSoundMode ?? "wav").Trim().ToLowerInvariant();
                            foreach (ComboBoxItem item in RingtoneSoundComboBox.Items)
                            {
                                if ((item.Tag?.ToString() ?? "") == mode)
                                {
                                    RingtoneSoundComboBox.SelectedItem = item;
                                    break;
                                }
                            }
                        }

                        if (RingtoneDeviceComboBox?.ItemsSource != null)
                        {
                            string selectedId = settings.RingtoneOutputDeviceId ?? "";
                            foreach (RingtoneOutputDeviceInfo device in RingtoneDeviceComboBox.ItemsSource)
                            {
                                if ((device.Id ?? "") == selectedId)
                                {
                                    RingtoneDeviceComboBox.SelectedItem = device;
                                    break;
                                }
                            }
                            if (RingtoneDeviceComboBox.SelectedItem == null && RingtoneDeviceComboBox.Items.Count > 0)
                            {
                                RingtoneDeviceComboBox.SelectedIndex = 0; // default
                            }
                        }

                        if (RingtoneVolumeSlider != null)
                        {
                            double v = settings.RingtoneVolume;
                            if (v < 0) v = 0;
                            if (v > 1) v = 1;
                            RingtoneVolumeSlider.Value = v * 100.0;
                        }
                        if (RingtoneVolumeValueTextBlock != null && RingtoneVolumeSlider != null)
                        {
                            RingtoneVolumeValueTextBlock.Text = $"{(int)Math.Round(RingtoneVolumeSlider.Value)}%";
                        }

                        ApplyRingtoneSettingsLive(settings);
                    }
                }
                else
                {
                    // Если файла настроек нет, выбираем устройства по умолчанию
                    if (MicrophoneComboBox.ItemsSource != null && MicrophoneComboBox.Items.Count > 0)
                    {
                        MicrophoneComboBox.SelectedIndex = 0;
                    }
                    if (SpeakerComboBox.ItemsSource != null && SpeakerComboBox.Items.Count > 0)
                    {
                        SpeakerComboBox.SelectedIndex = 0;
                    }

                    if (RingtoneDeviceComboBox?.ItemsSource != null && RingtoneDeviceComboBox.Items.Count > 0)
                    {
                        RingtoneDeviceComboBox.SelectedIndex = 0;
                    }
                    if (RingtoneVolumeSlider != null)
                    {
                        RingtoneVolumeSlider.Value = 70;
                    }
                    if (RingtoneVolumeValueTextBlock != null)
                    {
                        RingtoneVolumeValueTextBlock.Text = "70%";
                    }
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error loading audio settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning, this);
            }
        }

        private void SaveAudioSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Загружаем существующие настройки
                AppSettings settings;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                // Сохраняем выбранные аудиоустройства
                if (MicrophoneComboBox.SelectedItem is AudioDeviceInfo mic)
                {
                    settings.MicrophoneDeviceGuid = mic.Guid;
                    settings.MicrophoneDeviceNumber = mic.DeviceNumber;
                }

                if (SpeakerComboBox.SelectedItem is AudioDeviceInfo speaker)
                {
                    settings.SpeakerDeviceGuid = speaker.Guid;
                    settings.SpeakerDeviceNumber = speaker.DeviceNumber;
                }

                // Сохраняем настройки кодеков
                if (AudioCodecComboBox?.SelectedItem is ComboBoxItem codecItem && codecItem.Tag != null)
                {
                    settings.AudioCodec = codecItem.Tag.ToString() ?? "PCMU";
                }

                if (SampleRateComboBox?.SelectedItem is ComboBoxItem sampleRateItem && sampleRateItem.Tag != null)
                {
                    if (int.TryParse(sampleRateItem.Tag.ToString(), out int sampleRate))
                    {
                        settings.AudioSampleRate = sampleRate;
                    }
                }

                // Ringtone settings
                if (RingtoneDeviceComboBox?.SelectedItem is RingtoneOutputDeviceInfo ringDev)
                {
                    settings.RingtoneOutputDeviceId = string.IsNullOrWhiteSpace(ringDev.Id) ? null : ringDev.Id;
                }
                if (RingtoneVolumeSlider != null)
                {
                    settings.RingtoneVolume = Math.Max(0.0, Math.Min(1.0, RingtoneVolumeSlider.Value / 100.0));
                }
                if (RingtoneSoundComboBox?.SelectedItem is ComboBoxItem soundItem && soundItem.Tag != null)
                {
                    settings.RingtoneSoundMode = soundItem.Tag.ToString() ?? "wav";
                }

                // Сохраняем в файл
                string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), settingsJson);

                ApplyRingtoneSettingsLive(settings);

                CustomMessageBox.Show("Audio settings saved!", "Success", 
                    MessageBoxButton.OK, MessageBoxImage.Information, this);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error saving audio settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }

        private void ApplyRingtoneSettingsLive(AppSettings settings)
        {
            try
            {
                float volume = (float)Math.Max(0.0, Math.Min(1.0, settings.RingtoneVolume));
                RingtoneService.Instance.Configure(settings.RingtoneOutputDeviceId, volume, settings.RingtoneSoundMode);
                HoldMusicService.Instance.Configure(settings.RingtoneOutputDeviceId, volume);
            }
            catch
            {
                // best-effort
            }
        }

        private void MicrophoneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Можно добавить тестирование микрофона здесь
        }

        private void SpeakerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Можно добавить тестирование динамика здесь
        }

        private void AudioCodecComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Можно добавить информацию о выбранном кодеке
        }

        private void SampleRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Можно добавить информацию о выбранной частоте дискретизации
        }

        private void RingtoneDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string? id = (RingtoneDeviceComboBox?.SelectedItem as RingtoneOutputDeviceInfo)?.Id;
                float volume = (float)Math.Max(0.0, Math.Min(1.0, (RingtoneVolumeSlider?.Value ?? 70) / 100.0));
                string mode = ((RingtoneSoundComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "wav");
                RingtoneService.Instance.Configure(id, volume, mode);
            }
            catch
            {
                // ignore
            }
        }

        private void RingtoneVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (RingtoneVolumeValueTextBlock != null)
                {
                    RingtoneVolumeValueTextBlock.Text = $"{(int)Math.Round(e.NewValue)}%";
                }

                string? id = (RingtoneDeviceComboBox?.SelectedItem as RingtoneOutputDeviceInfo)?.Id;
                float volume = (float)Math.Max(0.0, Math.Min(1.0, e.NewValue / 100.0));
                string mode = ((RingtoneSoundComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "wav");
                RingtoneService.Instance.Configure(id, volume, mode);
            }
            catch
            {
                // ignore
            }
        }

        private void RingtoneSoundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string mode = ((RingtoneSoundComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "wav");
                string? id = (RingtoneDeviceComboBox?.SelectedItem as RingtoneOutputDeviceInfo)?.Id;
                float volume = (float)Math.Max(0.0, Math.Min(1.0, (RingtoneVolumeSlider?.Value ?? 70) / 100.0));
                RingtoneService.Instance.Configure(id, volume, mode);
            }
            catch
            {
                // ignore
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _secondaryTransportReinitCts?.Cancel();
                _secondaryTransportReinitCts?.Dispose();
                _secondaryTransportReinitCts = null;
            }
            catch { }

            // Отписываемся от событий при закрытии окна
            if (TryGetMainWindow() is MainWindow mainWindow)
            {
                mainWindow.OnConnectionStatusChanged -= UpdateConnectionStatusFromMainWindow;
                
                // Активируем главное окно после закрытия настроек
                // Используем BeginInvoke для выполнения после полного закрытия окна
                mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        mainWindow.Activate();
                        mainWindow.Focus();
                        mainWindow.BringIntoView();
                        
                        // Если окно свернуто, восстанавливаем его
                        if (mainWindow.WindowState == WindowState.Minimized)
                        {
                            mainWindow.WindowState = WindowState.Normal;
                        }
                    }
                    catch
                    {
                        // Игнорируем ошибки активации
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            
            // Отписываемся от событий сервиса, но НЕ удаляем shared service
            // Shared service должен жить дольше SettingsWindow
            StopWebRtcStatusCheck();
            
            // НЕ вызываем DisposeWebRtcStatusService() здесь - shared service используется приложением
            // Dispose будет вызван только при закрытии приложения или отключении WebRTC
            
            base.OnClosed(e);
        }

        // ===== MikoPBX gateway (Callspire proxy) =====

        private void EnableMikoPbxCdrCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            UpdateMikoPbxCdrSettingsVisibility(true);
            SaveMikoPbxCdrSettings();
        }

        private void EnableMikoPbxCdrCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateMikoPbxCdrSettingsVisibility(false);
            SaveMikoPbxCdrSettings();
        }

        private void UpdateMikoPbxCdrSettingsVisibility(bool visible)
        {
            if (MikoPbxCdrSettingsPanel != null)
            {
                MikoPbxCdrSettingsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void MikoPbxCdrAuthorizeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveMikoPbxCdrSettings();

                string serviceUrl = MikoPbxCdrServiceUrlTextBox?.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(serviceUrl) || serviceUrl == "https://")
                {
                    CustomMessageBox.Show(
                        "Please enter the proxy service URL first.",
                        "MikoPBX gateway",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }

                string callbackUri = "callspire://cdr-auth";
                string loginUrl = $"{serviceUrl.TrimEnd('/')}/login?callback={Uri.EscapeDataString(callbackUri)}";

                MainWindow.Log($"[PBX Gateway] Opening browser for authorization: {loginUrl}");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = loginUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[PBX Gateway] Authorize error: {ex.Message}");
                CustomMessageBox.Show(
                    $"Failed to open browser: {ex.Message}",
                    "MikoPBX gateway",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
            }
        }

        private void ClearMikoPbxCdrSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = CustomMessageBox.Show(
                    "Are you sure you want to clear all MikoPBX gateway settings?\n\nThis will remove the service URL, extension, and authorization token.",
                    "Clear MikoPBX gateway settings",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    this);

                if (result != MessageBoxResult.Yes) return;

                if (MikoPbxCdrServiceUrlTextBox != null) MikoPbxCdrServiceUrlTextBox.Text = "https://";
                if (MikoPbxCdrExtensionTextBox != null) MikoPbxCdrExtensionTextBox.Text = string.Empty;
                if (EnableMikoPbxCdrCheckBox != null) EnableMikoPbxCdrCheckBox.IsChecked = false;

                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                settings ??= new AppSettings();

                settings.EnableMikoPbxCdr = false;
                settings.MikoPbxCdrServiceUrl = null;
                settings.MikoPbxCdrTokenEncrypted = null;
                settings.MikoPbxExtension = null;

                string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsPath, updatedJson);

                var mainWindow = Application.Current.MainWindow as MainWindow;
                mainWindow?.DisableMikoPbxCdrService();

                UpdateMikoPbxCdrStatus("Not connected", false);
                MainWindow.Log("[PBX Gateway] Settings cleared");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[PBX Gateway] Clear settings error: {ex.Message}");
            }
        }

        private void SaveMikoPbxCdrSettings()
        {
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                settings ??= new AppSettings();

                settings.EnableMikoPbxCdr = EnableMikoPbxCdrCheckBox?.IsChecked ?? false;
                string url = MikoPbxCdrServiceUrlTextBox?.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(url) && url != "https://")
                    settings.MikoPbxCdrServiceUrl = url;
                string ext = MikoPbxCdrExtensionTextBox?.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(ext))
                    settings.MikoPbxExtension = ext;

                string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsPath, updatedJson);

                if (settings.EnableMikoPbxCdr && !string.IsNullOrEmpty(settings.MikoPbxCdrServiceUrl)
                    && !string.IsNullOrEmpty(settings.MikoPbxCdrTokenEncrypted))
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    mainWindow?.InitializeMikoPbxCdrService(settings);
                }

                CheckMikoPbxCdrConnectionStatus();
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[PBX Gateway] Save settings error: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-checks gateway URL + JWT against the proxy (HTTP). Safe to call from any thread.
        /// </summary>
        private void CheckMikoPbxCdrConnectionStatus()
        {
            _ = RunMikoPbxCdrConnectionCheckAsync();
        }

        private async Task RunMikoPbxCdrConnectionCheckAsync()
        {
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                if (!File.Exists(settingsPath))
                {
                    await Dispatcher.InvokeAsync(() => UpdateMikoPbxCdrStatus("Not connected", false));
                    return;
                }

                string json = File.ReadAllText(settingsPath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                if (settings == null || !settings.EnableMikoPbxCdr)
                {
                    await Dispatcher.InvokeAsync(() => UpdateMikoPbxCdrStatus("Not connected", false));
                    return;
                }

                bool hasUrl = !string.IsNullOrEmpty(settings.MikoPbxCdrServiceUrl?.Trim());
                bool hasExt = !string.IsNullOrEmpty(settings.MikoPbxExtension?.Trim());
                if (!hasUrl || !hasExt)
                {
                    await Dispatcher.InvokeAsync(() => UpdateMikoPbxCdrStatus("Not configured", false));
                    return;
                }

                string? tokenPlain = null;
                try
                {
                    if (!string.IsNullOrEmpty(settings.MikoPbxCdrTokenEncrypted))
                        tokenPlain = TokenEncryption.Decrypt(settings.MikoPbxCdrTokenEncrypted);
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[PBX Gateway] Token decrypt failed: {ex.Message}");
                    await Dispatcher.InvokeAsync(() => UpdateMikoPbxCdrStatus("Not connected", false));
                    return;
                }

                if (string.IsNullOrEmpty(tokenPlain))
                {
                    await Dispatcher.InvokeAsync(() => UpdateMikoPbxCdrStatus("Not authorized", false));
                    return;
                }

                var (text, ok) = await MikoPbxCdrService.ProbeGatewayAsync(settings.MikoPbxCdrServiceUrl, tokenPlain)
                    .ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() => UpdateMikoPbxCdrStatus(text, ok));
                await RefreshKommoGatewayModuleStatusAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[PBX Gateway] Check status error: {ex.Message}");
                await Dispatcher.InvokeAsync(() => UpdateMikoPbxCdrStatus("Error", false));
            }
        }

        /// <summary>After OAuth callback or manual refresh — probes /health + JWT API.</summary>
        public void RefreshMikoGatewayConnectionStatus()
        {
            CheckMikoPbxCdrConnectionStatus();
        }

        public void UpdateMikoPbxCdrStatus(string statusText, bool isConnected)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateMikoPbxCdrStatus(statusText, isConnected));
                return;
            }

            if (MikoPbxCdrStatusTextBlock != null)
                MikoPbxCdrStatusTextBlock.Text = statusText;
            if (MikoPbxCdrStatusIndicator != null)
                MikoPbxCdrStatusIndicator.Fill = isConnected
                    ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
                    : new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
        }

        public void OnMikoPbxCdrTokenReceived(string token)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    string settingsPath = AppDataHelper.GetSettingsFilePath();
                    AppSettings? settings = null;
                    if (File.Exists(settingsPath))
                    {
                        string json = File.ReadAllText(settingsPath);
                        settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    }
                    settings ??= new AppSettings();

                    settings.MikoPbxCdrTokenEncrypted = TokenEncryption.Encrypt(token);

                    string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                    File.WriteAllText(settingsPath, updatedJson);

                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    mainWindow?.InitializeMikoPbxCdrService(settings);
                    RefreshMikoGatewayConnectionStatus();

                    MainWindow.Log("[PBX Gateway] Token received and saved");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[PBX Gateway] Token save error: {ex.Message}");
                    UpdateMikoPbxCdrStatus("Token save failed", false);
                }
            });
        }
    }

    // Converter для иконок темы
    public class ThemeIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var tag = value?.ToString() ?? "system";
            return tag switch
            {
                "system" => Symbol.Desktop,
                "dark" => Symbol.WeatherMoon,
                "light" => Symbol.WeatherSunny,
                _ => Symbol.Desktop
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Converter для описаний темы
    public class ThemeDescriptionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var tag = value?.ToString() ?? "system";
            return tag switch
            {
                "system" => "Follows your Windows theme",
                "dark" => "Dark colors everywhere",
                "light" => "Light and bright",
                _ => ""
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}


#endif // WINDOWS
