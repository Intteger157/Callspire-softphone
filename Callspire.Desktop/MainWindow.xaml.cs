#if WINDOWS
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Net.NetworkInformation;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Softphone
{
    public partial class MainWindow : Window
    {
        private SipService? _sipService;
        private SipService? _sipService2; // Второе SIP подключение (только SIP, независимо от основного)
        private readonly SemaphoreSlim _secondConnectionInitSemaphore = new SemaphoreSlim(1, 1);
        private string? _secondaryConnectionFingerprint;
        private System.Threading.Timer? _secondarySipMaintenanceTimer;
        private static readonly TimeSpan _secondarySipMaintenanceInterval = TimeSpan.FromMinutes(30);
        private CallHistoryService _callHistoryService;
        private CallStatisticsReport? _lastStatisticsReport;
        private List<CallHistoryItem>? _historyViewFilteredCalls;
        private string? _historyFilterCaption;
        private System.Windows.Threading.DispatcherTimer? _statisticsRefreshDebounceTimer;
        private LogWindow? _logWindow;
        private System.Collections.Generic.List<string> _logHistory = new System.Collections.Generic.List<string>();


        // Статическая ссылка на главное окно для логирования из других классов
        private static MainWindow? _instance;
        
        // Общий экземпляр WebRTC сервиса для автоматического подключения при старте
        private static WebRtcStatusService? _sharedWebRtcStatusService;
        
        // Singleton WebView2 для WebRTC (живет весь runtime приложения)
        private Microsoft.Web.WebView2.Wpf.WebView2? _webRtcEngine;

        /// <summary>
        /// Общий WebRtcEngineHost для обоих слотов (main/secondary). Создаётся при первом
        /// слоте, который требует WebRTC; вторые вызовы переиспользуют тот же экземпляр.
        /// </summary>
        private WebRtcEngineHost? _sharedWebRtcHost;

        // AmoCRM сервис для интеграции
        private static AmoCrmService? _amoCrmService;
        private string? _amoCrmConnectionSource;
        private readonly SemaphoreSlim _amoCrmInitSemaphore = new SemaphoreSlim(1, 1);
        private readonly object _kommoWarningLock = new object();
        private bool _kommoIntegrationWarningShown;
        private bool _kommoIntegrationWarningShowing;
        private CancellationTokenSource? _kommoDeferredRetryCts;

        // Callspire PBX Gateway client (JWT API)
        private MikoPbxCdrService? _mikoPbxCdrService;
        private string? _mikoPbxExtension;
        private string? _sipUsername2;
        private KommoGatewayStatus? _cachedKommoGatewayStatus;

        // CallerID selection (PBX Originate)
        private List<string> _allowedCallerIds = new List<string>();
        private List<CallerIdItem> _callerIdItems = new List<CallerIdItem>();
        private readonly SemaphoreSlim _callerIdLoadSemaphore = new SemaphoreSlim(1, 1);
        private System.Threading.Timer? _callerIdListRetryTimer;
        private static readonly TimeSpan _callerIdRetryFirst = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan _callerIdRetryInterval = TimeSpan.FromMinutes(1);
        // Browser click-to-call: prevent delayed/duplicate auto-calls when WebRTC isn't ready yet.
        private readonly object _pendingBrowserCallLock = new object();
        private CancellationTokenSource? _pendingBrowserCallCts;
        private int _pendingBrowserCallSeq = 0;
        private DateTime _pendingBrowserCallCreatedUtc = DateTime.MinValue;
        private const int PendingBrowserCallTtlSeconds = 90;
        /// <summary>When both lines are configured, wait up to this long before auto-picking a single connection (click-to-call).</summary>
        private const int ConnectionReadyWaitSeconds = 20;

        // Protocol click-to-call dedupe (some browsers/extensions may deliver the same protocol URL twice).
        private readonly object _protocolDedupeLock = new object();
        private string? _lastProtocolCallKey;
        private DateTime _lastProtocolCallUtc = DateTime.MinValue;
        private static readonly TimeSpan _protocolCallDedupeWindow = TimeSpan.FromSeconds(15);

        // Prevent surprise/ghost re-dials: only allow call initiation right after user input,
        // unless explicitly marked as programmatic (protocol/click-to-call).
        private long _lastUserInputUtcTicks = DateTime.MinValue.Ticks;
        private volatile bool _allowNextProgrammaticCall = false;
        private static readonly TimeSpan _callRequiresRecentUserInputWindow = TimeSpan.FromMilliseconds(250);
        
        // Дедупликация вызовов ProcessCallInAmoCrm для предотвращения множественных записей одного звонка
        // ИЗОЛЯЦИЯ: Используем sessionId как основной идентификатор для изоляции потоков обработки
        // Каждый sessionId обрабатывается независимо, что предотвращает конфликты между параллельными звонками
        private readonly ConcurrentDictionary<string, DateTime> _processedCalls = new ConcurrentDictionary<string, DateTime>();

        /// <summary>Очередь обработки звонков для AmoCRM: один звонок за раз, с ожиданием файла записи до 5 минут (колл-центр: позвонил → следующий).</summary>
        private readonly BlockingCollection<AmoCrmJob> _amoCrmQueue = new BlockingCollection<AmoCrmJob>();
        private const int MaxConcurrentAmoCrmWorkers = 3; // Параллельная обработка до 3 звонков одновременно

        // Sleep/Resume stability
        private bool _powerHandlersRegistered = false;
        private bool _isRecoveringWebRtcAfterResume = false;
        private DateTime _lastResumeUtc = DateTime.MinValue;
        private DateTime _lastSuspendUtc = DateTime.MinValue;
        private bool _webViewProcessFailedHandlerRegistered = false;

        // Prevent re-entrancy while shutting down windows.
        private bool _isShuttingDown = false;

        // (Maximize/restore disabled; keep window logic simple)

        // Titlebar drag handling (stable drag for borderless window; restore from maximized only on real drag)
        private bool _titleBarDragPending = false;
        private Point _titleBarDownPoint;
        private Point _titleBarDownScreenPoint;
        private double _titleBarDownPercentX = 0.5;

        public bool IsConnected 
        { 
            get 
            {
                // Если основное подключение использует WebRTC, проверяем статус WebRTC.Main вместо SIP
                if (ShouldUseWebRtc())
                {
                    try
                    {
                        var webRtcService = WebRtcService.Main;
                        bool isReady = webRtcService != null && webRtcService.IsReadyForCalls;
                        
                        // ДИАГНОСТИКА: логируем только при изменении состояния (чтобы не спамить)
                        if (isReady && (_lastIsConnected != isReady))
                        {
                            Log($"[MainWindow] IsConnected (WebRTC): {isReady}, IsReadyForCalls={webRtcService?.IsReadyForCalls ?? false}");
                        }
                        
                        return isReady;
                    }
                    catch (Exception ex)
                    {
                        Log($"[MainWindow] ERROR in IsConnected (WebRTC): {ex.Message}");
                        return false;
                    }
                }
                // Иначе проверяем SIP регистрацию
                return _sipService?.IsRegistered ?? false;
            }
        }
        
        private bool _lastIsConnected = false; // Для отслеживания изменений в IsConnected
        private DateTime _webRtcDisconnectedSince = DateTime.MinValue; // for stable "Reconnecting..." UI
        
        static MainWindow()
        {
            // Business services in Callspire.Core log via AppLog; mirror messages into the UI log view.
            AppLog.UiSink = m => _instance?.AddToLog(m);
        }

        public static void Log(string message)
        {
            // Sanitization, file logging and Debug output live in AppLog (Callspire.Core).
            AppLog.Log(message);
        }

        public MainWindow()
        {
            InitializeComponent();
            SingleInstanceManager.RegisterMainWindow(this);
            _instance = this; // Сохраняем ссылку на экземпляр
            _callHistoryService = new CallHistoryService();

            // Track most recent user input to avoid unexpected automatic call triggers.
            try
            {
                System.Threading.Interlocked.Exchange(ref _lastUserInputUtcTicks, DateTime.UtcNow.Ticks);
                System.Windows.Input.InputManager.Current.PreProcessInput += (_, __) =>
                {
                    System.Threading.Interlocked.Exchange(ref _lastUserInputUtcTicks, DateTime.UtcNow.Ticks);
                };
            }
            catch { }
            
            // Устанавливаем фон окна сразу после инициализации, чтобы избежать белой полосы сверху
            try
            {
                if (Application.Current?.TryFindResource("BackgroundDarkBrush") is System.Windows.Media.Brush brush)
                {
                    this.Background = brush;
                }
            }
            catch { }
            
            // Также устанавливаем фон в SourceInitialized, чтобы он точно был установлен до показа окна
            this.SourceInitialized += (s, e) =>
            {
                try
                {
                    if (Application.Current?.TryFindResource("BackgroundDarkBrush") is System.Windows.Media.Brush brush)
                    {
                        this.Background = brush;
                    }
                }
                catch { }
            };
            
            ShowView(DialerView);

            InitializeStatisticsFilters();

            // Centralized Win10/Wpf vs Win11/DWM native appearance.
            // Примечание: для стандартных анимаций минимизации используем SingleBorderWindow на обеих версиях Windows.
            // NativeWindowAppearanceManager настроит AllowsTransparency соответственно версии ОС.
            NativeWindowAppearanceManager.Attach(this);
            
            // Устанавливаем начальный статус
            UpdateConnectionStatus();
            UpdateWebRtcIndicator();
            UpdateUserAccountInfo();
            
            // Выделяем кнопку Dialer при запуске
            UpdateButtonSelection(DialerView);
            
            // Инициализируем AmoCRM сервис при старте, если интеграция включена
            _ = InitializeAmoCrmServiceOnStartup();

            // Инициализируем клиент PBX Gateway при старте
            _ = Task.Run(() =>
            {
                try
                {
                    string settingsPath = AppDataHelper.GetSettingsFilePath();
                    if (System.IO.File.Exists(settingsPath))
                    {
                        string json = System.IO.File.ReadAllText(settingsPath);
                        var s = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(json);
                        if (s != null) InitializeMikoPbxCdrService(s);
                    }
                }
                catch (Exception ex) { Log($"[PBX Gateway] Startup init error: {ex.Message}"); }
            });
            // Воркеры очереди AmoCRM: обрабатывают звонки параллельно (до 3 одновременно), ждёт файл записи до 5 минут (колл-центр)
            for (int i = 0; i < MaxConcurrentAmoCrmWorkers; i++)
            {
                _ = Task.Run(() => AmoCrmWorkerAsync());
            }
            
            // Автоматическая очистка старых записей (старше 7 дней) и истории звонков при старте приложения
            _ = Task.Run(() =>
            {
                try
                {
                    AppDataHelper.CleanupOldRecordings(retentionDays: 7);
                    _callHistoryService.CleanupOldHistory(retentionDays: 7);
                }
                catch (Exception ex)
                {
                    Log($"[MainWindow] Error during cleanup on startup: {ex.Message}");
                }
            });
            
            // Пытаемся подключиться и проверяем статус подключения
            _ = TryConnectAndCheckStatus();
            
            // Регистрируем протокол callspire:// при первом запуске (если еще не зарегистрирован)
            _ = Task.Run(() =>
            {
                try
                {
                    if (!ProtocolRegistrar.IsProtocolRegistered())
                    {
                        Log("[MainWindow] Protocol not registered, registering...");
                        bool registered = ProtocolRegistrar.RegisterProtocol();
                        if (registered)
                        {
                            Log("[MainWindow] Protocol callspire:// registered successfully");
                        }
                        else
                        {
                            Log("[MainWindow] Failed to register protocol (will retry on next startup)");
                        }
                    }
                    else
                    {
                        Log("[MainWindow] Protocol callspire:// already registered");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[MainWindow] Error checking/registering protocol: {ex.Message}");
                }
            });
            
            // Обрабатываем отложенный звонок из браузера, если есть
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var app = Application.Current as App;
                var pendingCall = app?.GetPendingBrowserCall();
                if (pendingCall.HasValue)
                {
                    InitiateCallFromBrowser(pendingCall.Value.phoneNumber, pendingCall.Value.leadId);
                }

                // Same for the provisioning URL, which may have arrived before
                // MainWindow existed (fresh install from a browser click).
                var pendingProv = app?.GetPendingProvision();
                if (pendingProv.HasValue)
                {
                    _ = HandleProvisionTokenAsync(pendingProv.Value.tokenId, pendingProv.Value.proxy);
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            
            // Предварительно инициализируем WebView2 Environment только если WebRTC реально включен.
            // Иначе это создает лишнюю нагрузку и шум в логах при SIP режиме/переключении.
            if (ShouldUseWebRtc())
            {
                _ = PreInitializeWebView2Environment();
            }
            
            // Подписываемся на Loaded для инициализации singleton WebView2
            Loaded += MainWindow_Loaded;

            // КРИТИЧНО: Обработка закрытия окна - предотвращаем закрытие приложения при активных звонках
            Closing += MainWindow_Closing;

            // Fix borderless maximize behavior (do not cover taskbar, allow restore/drag).
            // Work-area clamping is handled by WindowWorkAreaHelper via NativeWindowAppearanceManager.
            
            // NOTE: Win11 DWM (mica + corners) is applied by NativeWindowAppearanceManager on SourceInitialized.

            // Power/network lifecycle hooks (sleep/resume)
            RegisterPowerAndNetworkHandlers();
            
            // Проверяем наличие новой версии при запуске (в фоне, без блокировки UI)
            _ = CheckForUpdatesOnStartupAsync();
        }

        /// <summary>
        /// Обработчик закрытия главного окна - предотвращает закрытие приложения при активных звонках
        /// </summary>
        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_isShuttingDown) return;

            // КРИТИЧНО: Проверяем наличие активных звонков перед закрытием
            var existingCallWindow = CallHandlingHelpers.FindExistingCallWindow();
            bool hasActiveWebRtcCall = CallHandlingHelpers.IsWebRtcCallActive();
            bool hasActiveSipCall = _sipService != null && _sipService.IsInCall;
            
            if (existingCallWindow != null || hasActiveWebRtcCall || hasActiveSipCall)
            {
                Log("[MainWindow] Closing prevented: Active call detected");
                
                // Отменяем закрытие окна
                e.Cancel = true;
                
                // Показываем сообщение пользователю
                CustomMessageBox.Show(
                    "Cannot close application while a call is active. Please end the call first.",
                    "Active Call",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
                
                // Активируем окно звонка, если оно существует
                if (existingCallWindow != null)
                {
                    existingCallWindow.Activate();
                    existingCallWindow.BringIntoView();
                }
                
                return;
            }
            
            // Если нет активных звонков, разрешаем закрытие
            Log("[MainWindow] Closing: No active calls, proceeding with shutdown");

            // Close any non-modal child windows (Settings, Logs, etc.) so they can't outlive MainWindow.
            _isShuttingDown = true;
            try { CloseChildWindowsBestEffort(); } catch { }
        }

        private void CloseChildWindowsBestEffort()
        {
            try
            {
                // Copy first to avoid collection modification during Close().
                var windows = Application.Current?.Windows?.Cast<Window>().ToList();
                if (windows == null) return;

                foreach (var w in windows)
                {
                    if (w == this) continue;
                    if (w is SettingsWindow || w is LogWindow)
                    {
                        try { w.Close(); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Best-effort teardown to avoid "ghost calls" that keep ringing on PBX/carrier
        /// after the desktop app window/process is closed.
        /// </summary>
        public void ShutdownTelephonyBestEffort()
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;

            try { Log("[MainWindow] ShutdownTelephonyBestEffort: stopping telephony services..."); } catch { }

            // WebRTC call (async): fire-and-forget with a short wait budget.
            try
            {
                var t = WebRtcService.Main?.HangupAsync();
                if (t != null && !t.IsCompleted)
                {
                    // Don't block shutdown for long.
                    try { t.Wait(TimeSpan.FromSeconds(2)); } catch { }
                }
            }
            catch { }

            // SIP calls: finalize recordings, Hangup + Dispose.
            try
            {
                var t1 = _sipService?.FinalizeCallRecordingIfActiveAsync(15000);
                var t2 = _sipService2?.FinalizeCallRecordingIfActiveAsync(15000);
                if (t1 != null) try { t1.Wait(TimeSpan.FromSeconds(15)); } catch { }
                if (t2 != null) try { t2.Wait(TimeSpan.FromSeconds(15)); } catch { }
            }
            catch { }
            try
            {
                _sipService?.Hangup();
            }
            catch { }
            try
            {
                _sipService2?.Hangup();
            }
            catch { }

            try
            {
                _sipService?.Dispose();
                _sipService = null;
            }
            catch { }

            try
            {
                _sipService2?.Dispose();
                _sipService2 = null;
            }
            catch { }

            // Stop maintenance timers / watchdogs.
            try { StopSecondarySipMaintenanceTimer(); } catch { }
            try { WebRtcService.Main?.StopWatchdog(); } catch { }
            try { WebRtcService.Main?.StopAutoReconnect(); } catch { }
            try { WebRtcService.Main?.DetachEngine(resetState: true); } catch { }

            // Unregister lifecycle handlers.
            try { UnregisterPowerAndNetworkHandlers(); } catch { }
        }

        private void RegisterPowerAndNetworkHandlers()
        {
            if (_powerHandlersRegistered) return;
            _powerHandlersRegistered = true;

            try { SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged; } catch { }
            try { NetworkChange.NetworkAvailabilityChanged += NetworkChange_NetworkAvailabilityChanged; } catch { }
        }

        private void UnregisterPowerAndNetworkHandlers()
        {
            if (!_powerHandlersRegistered) return;
            _powerHandlersRegistered = false;

            try { SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged; } catch { }
            try { NetworkChange.NetworkAvailabilityChanged -= NetworkChange_NetworkAvailabilityChanged; } catch { }
        }

        private void SystemEvents_PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            try
            {
                // Never block in SystemEvents thread.
                if (e.Mode == PowerModes.Suspend)
                {
                    _lastSuspendUtc = DateTime.UtcNow;
                    Log("[MainWindow] PowerModeChanged: Suspend");

                    // Stop noisy background loops while sleeping.
                    try { WebRtcService.Main.StopWatchdog(); } catch { }
                    try { WebRtcService.Main.StopAutoReconnect(); } catch { }
                }
                else if (e.Mode == PowerModes.Resume)
                {
                    _lastResumeUtc = DateTime.UtcNow;
                    Log("[MainWindow] PowerModeChanged: Resume");

                    // Schedule recovery on UI thread without blocking.
                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try { await HandleResumeAsync("power_resume"); } catch (Exception ex) { Log($"[MainWindow] Resume recovery error: {ex.Message}"); }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] ERROR in PowerModeChanged: {ex.Message}");
            }
        }

        private void NetworkChange_NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        {
            try
            {
                Log($"[MainWindow] NetworkAvailabilityChanged: IsAvailable={e.IsAvailable}");
                if (!e.IsAvailable) return;

                // After network comes back, try a light recovery (no heavy UI work).
                if (ShouldUseWebRtc())
                {
                    _ = Task.Run(async () =>
                    {
                        try { await WebRtcService.Main.ResetEngineAsync(); } catch { }
                    });
                }
                else
                {
                    // Для SIP режима переподключаем оба подключения при восстановлении сети
                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            Log("[MainWindow] NetworkAvailabilityChanged: Reconnecting SIP connections after network recovery");
                            await TryConnectFromSettings();
                            await InitializeSecondConnectionAsync();
                        }
                        catch (Exception ex)
                        {
                            Log($"[MainWindow] NetworkAvailabilityChanged: Error reconnecting: {ex.Message}");
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch { }
        }

        private async Task HandleResumeAsync(string reason)
        {
            // Debounce: Windows can fire multiple resume-related events.
            if ((DateTime.UtcNow - _lastResumeUtc).TotalSeconds < 0.2) { /* ok */ }

            // Give the OS/network stack a moment to stabilize.
            await Task.Delay(600);

            if (!IsLoaded) return;

            // If WebRTC is enabled, re-init the engine to avoid WebView2/WS stuck states after sleep.
            if (ShouldUseWebRtc())
            {
                await RecoverWebRtcEngineAsync(reason);
            }
            else
            {
                // Для SIP режима переподключаем оба подключения после выхода из сна
                try
                {
                    Log($"[MainWindow] HandleResumeAsync: Reconnecting SIP connections after resume (reason={reason})");
                    await TryConnectFromSettings();
                    await InitializeSecondConnectionAsync();
                }
                catch (Exception ex)
                {
                    Log($"[MainWindow] HandleResumeAsync: Error reconnecting SIP: {ex.Message}");
                }
            }

            // Refresh UI status best-effort.
            try { UpdateConnectionStatus(); } catch { }
            try { UpdateWebRtcIndicator(); } catch { }
        }

        private async Task RecoverWebRtcEngineAsync(string reason)
        {
            if (_isRecoveringWebRtcAfterResume) return;
            _isRecoveringWebRtcAfterResume = true;

            try
            {
                Log($"[MainWindow] WebRTC recovery: starting (reason={reason})");

                // Prevent background timers from touching disposed WebView2 while we recreate.
                try { WebRtcService.Main.StopWatchdog(); } catch { }
                try { WebRtcService.Main.StopAutoReconnect(); } catch { }
                try { WebRtcService.Main.DetachEngine(resetState: true); } catch { }

                // Dispose old engine (if any) and recreate via existing initialization path.
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (_webRtcEngine != null)
                        {
                            try
                            {
                                if (WebRtcHostGrid != null && WebRtcHostGrid.Children.Contains(_webRtcEngine))
                                {
                                    WebRtcHostGrid.Children.Remove(_webRtcEngine);
                                }
                            }
                            catch { }

                            try { _webRtcEngine.Dispose(); } catch { }
                            _webRtcEngine = null;
                            _sharedWebRtcHost = null;
                            _webViewProcessFailedHandlerRegistered = false;
                        }
                    }
                    catch { }
                }, System.Windows.Threading.DispatcherPriority.Background);

                await InitializeWebRtcServiceAsync();
                
                // Переподключаем второе подключение после восстановления WebRTC
                try
                {
                    Log("[MainWindow] WebRTC recovery: Reconnecting second connection");
                    await InitializeSecondConnectionAsync();
                }
                catch (Exception ex)
                {
                    Log($"[MainWindow] WebRTC recovery: Error reconnecting second connection: {ex.Message}");
                }
                
                Log("[MainWindow] WebRTC recovery: completed");
            }
            finally
            {
                _isRecoveringWebRtcAfterResume = false;
            }
        }
        
        /// <summary>
        /// Проверяет наличие новой версии при запуске приложения
        /// </summary>
        private async System.Threading.Tasks.Task CheckForUpdatesOnStartupAsync()
        {
            try
            {
                // Небольшая задержка, чтобы не мешать загрузке приложения
                await System.Threading.Tasks.Task.Delay(3000);
                
                Log("[MainWindow] Checking for updates on startup...");
                
                // Используем новый сервис обновлений через собственный сервер
                var updateInfo = await UpdateService.CheckForUpdateAsync();
                
                if (updateInfo != null)
                {
                    Log("[MainWindow] New version available!");
                    
                    string currentVersion = UpdateService.GetCurrentVersion();
                    
                    // Показываем окно уведомления о новой версии
                    Dispatcher.Invoke(() =>
                    {
                        // If already open, bring to front (do not block MainWindow).
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
                        CenterNonOwnedWindowOverThis(updateWindow);
                        updateWindow.Show();
                        try
                        {
                            updateWindow.Activate();
                            updateWindow.Focus();
                        }
                        catch { }
                    });
                }
                else
                {
                    Log("[MainWindow] Application is up to date.");
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error checking for updates on startup: {ex.Message}");
                Log($"[MainWindow] Stack trace: {ex.StackTrace}");
                // Не показываем ошибку пользователю при автоматической проверке
                // Ошибки логируются для отладки
            }
        }
        
        
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // One-time first-load migration: если MainConnectionTransport не выставлен,
            // но legacy UseWebRtcAudio=true — проставляем MainConnectionTransport="WebRtc" и сохраняем.
            try
            {
                MigrateLegacyTransportSettingIfNeeded();
            }
            catch (Exception migEx)
            {
                Log($"[MainWindow] MainWindow_Loaded: Migration check failed: {migEx.Message}");
            }

            // Инициализируем подключения после загрузки окна
            Log("[MainWindow] MainWindow_Loaded: Scheduling connection initialization...");
            Dispatcher.BeginInvoke(new System.Action(async () =>
            {
                // Инициализируем основное подключение в соответствии с режимом
                if (ShouldUseWebRtc())
                {
                    // WebRTC режим: инициализируем WebRTC сервис
                    Log("[MainWindow] MainWindow_Loaded: WebRTC mode detected, initializing WebRTC service...");
                    await InitializeWebRtcServiceAsync();
                }
                else
                {
                    // SIP режим: инициализируем SIP сервис
                    Log("[MainWindow] MainWindow_Loaded: SIP mode detected, initializing SIP service...");
                    await TryConnectFromSettings();
                }
                
                // Параллельно основному подключению инициализируем дополнительное подключение (SIP-only)
                // Оно работает независимо от режима основного подключения
                // Инициализируется только если в настройках заполнены данные для второго подключения
                Log("[MainWindow] MainWindow_Loaded: Initializing second connection (SIP-only) in parallel...");
                await InitializeSecondConnectionAsync();
                
                // Обновляем все статусы после инициализации подключений
                UpdateConnectionStatus();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        
        /// <summary>
        /// Инициализирует WebRTC сервис
        /// </summary>
        private Task InitializeWebRtcServiceAsync()
        {
            return InitializeWebRtcServiceAsync("main");
        }

        /// <summary>
        /// Инициализирует WebRTC слот (main / secondary). Общий WebView2-хост создаётся
        /// один раз — при первом вызове; последующие слоты только подключаются к нему.
        /// </summary>
        private async Task InitializeWebRtcServiceAsync(string slot)
        {
            try
            {
                bool isSecondary = string.Equals(slot, "secondary", StringComparison.OrdinalIgnoreCase);
                Log($"[MainWindow] InitializeWebRtcServiceAsync(slot={slot}): Starting...");

                if (!isSecondary && CallHandlingHelpers.IsWebRtcCallActive())
                {
                    Log("[MainWindow] InitializeWebRtcServiceAsync skipped: WebRTC call is active");
                    return;
                }

                // Проверяем, нужен ли WebRTC для этого слота
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (!File.Exists(settingsFilePath))
                {
                    Log("[MainWindow] Settings file not found, skipping WebRTC service initialization");
                    return;
                }

                Log("[MainWindow] InitializeWebRtcServiceAsync: Settings file found, reading...");
                string json = File.ReadAllText(settingsFilePath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                if (settings == null)
                {
                    Log("[MainWindow] InitializeWebRtcServiceAsync: Settings is null, skipping");
                    return;
                }

                // Best-effort migrate stored secrets to DPAPI (safer at-rest).
                try
                {
                    SipPasswordProvider.MigrateEncryptedToDpapiIfNeeded(settingsFilePath, settings);
                    TurnPasswordProvider.MigrateEncryptedToDpapiIfNeeded(settingsFilePath, settings);
                }
                catch { }

                string? sipPassword = isSecondary
                    ? SipPasswordProvider.GetSecondaryWebRtcPassword(settings)
                    : SipPasswordProvider.GetMainWebRtcPassword(settings);
                string? wsUri = isSecondary ? settings.WebRtcWsUri2 : settings.WebRtcWsUri;
                string? username = isSecondary
                    ? AppSettings.EffectiveSecondaryWebRtcUsername(settings)
                    : AppSettings.EffectiveMainWebRtcUsername(settings);
                bool transportEnabled = isSecondary
                    ? AppSettings.SecondaryLineUsesWebRtc(settings)
                    : ShouldUseWebRtcForConnection(false);
                Log($"[MainWindow] InitializeWebRtcServiceAsync(slot={slot}): transportEnabled={transportEnabled}, wsUri={(string.IsNullOrEmpty(wsUri) ? "empty" : "set")}, user={(string.IsNullOrEmpty(username) ? "empty" : "set")}, pass={(string.IsNullOrEmpty(sipPassword) ? "empty" : "set")}");

                if (!transportEnabled ||
                    string.IsNullOrEmpty(wsUri) ||
                    string.IsNullOrEmpty(username) ||
                    string.IsNullOrEmpty(sipPassword))
                {
                    Log($"[MainWindow] WebRTC not enabled/configured for slot '{slot}', skipping service initialization");
                    return;
                }

                Log($"[MainWindow] Initializing WebRTC service for slot '{slot}'...");

                // Настраиваем уровень логирования WebRTC в соответствии с настройками
                WebRtcService.GetSlot(slot).DebugEnabled = settings.EnableWebRtcDebug;
                
                // Обновляем статус на "Initializing..." (только для основного слота)
                if (!isSecondary)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (MainStatusTextBlock != null)
                        {
                            MainStatusTextBlock.Text = "Initializing WebRTC...";
                            if (MainStatusIndicator != null)
                                MainStatusIndicator.Background = (Brush)FindResource("TextSecondaryBrush");
                        }
                    });
                }

                // Общий WebView2 + WebRtcEngineHost создаются один раз и шарятся между слотами.
                // index.html содержит два iframe (slot-main / slot-secondary), команды роутятся через slot.
                WebRtcEngineHost? sharedHost = _sharedWebRtcHost;
                if (_webRtcEngine == null || sharedHost == null)
                {
                    // Создаем скрытый WebView2 (1x1px). Visibility.Visible для создания HWND.
                    _webRtcEngine = new Microsoft.Web.WebView2.Wpf.WebView2
                    {
                        Visibility = Visibility.Visible,
                        Width = 1,
                        Height = 1,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        IsHitTestVisible = false
                    };

                    if (WebRtcHostGrid == null)
                    {
                        Log("[MainWindow] ERROR: WebRtcHostGrid not found in XAML");
                        return;
                    }

                    Log("[MainWindow] Adding WebView2 to WebRtcHostGrid...");
                    WebRtcHostGrid.Children.Clear();
                    WebRtcHostGrid.Children.Add(_webRtcEngine);
                    Log("[MainWindow] WebRTC engine WebView2 added to WebRtcHostGrid");

                    Log("[MainWindow] Waiting for WebView2 to be loaded in visual tree...");
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

                    var loadCheckStart = DateTime.Now;
                    while (!_webRtcEngine.IsLoaded && (DateTime.Now - loadCheckStart).TotalSeconds < 5)
                    {
                        await Task.Delay(100);
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    }

                    Log($"[MainWindow] WebView2 load check: IsLoaded={_webRtcEngine.IsLoaded}, Parent={_webRtcEngine.Parent?.GetType().Name ?? "null"}, Visibility={_webRtcEngine.Visibility}");

                    var userData = Path.Combine(
                        AppDataHelper.GetAppDataPath(),
                        "WebView2");
                    Directory.CreateDirectory(userData);
                    Log($"[MainWindow] WebView2 UserDataFolder: {userData}");

                    var envOptions = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
                    {
                        AdditionalBrowserArguments =
                            "--force-webrtc-ip-handling-policy=default_public_interface_only " +
                            "--webrtc-ip-handling-policy=default_public_interface_only"
                    };

                    var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                        browserExecutableFolder: null,
                        userDataFolder: userData,
                        options: envOptions);
                    Log("[MainWindow] WebView2 Environment created successfully");

                    Log("[MainWindow] Creating WebRtcEngineHost...");
                    sharedHost = new WebRtcEngineHost(_webRtcEngine);
                    Log("[MainWindow] WebRtcEngineHost created, calling InitAsync...");
                    await sharedHost.InitAsync(env, "https://softphone.local/index.html");
                    Log($"[MainWindow] WebRtcEngineHost.InitAsync completed: IsInitialized={sharedHost.IsInitialized}");

                    try
                    {
                        if (!_webViewProcessFailedHandlerRegistered && _webRtcEngine.CoreWebView2 != null)
                        {
                            _webRtcEngine.CoreWebView2.ProcessFailed += WebRtcEngine_ProcessFailed;
                            _webViewProcessFailedHandlerRegistered = true;
                        }
                    }
                    catch { }

                    _sharedWebRtcHost = sharedHost;
                }
                else
                {
                    Log($"[MainWindow] Reusing existing shared WebRtcEngineHost for slot '{slot}'");
                }

                var slotService = WebRtcService.GetSlot(slot);
                slotService.AttachEngine(sharedHost);
                slotService.Event -= OnWebRtcEvent; // idempotent
                slotService.Event += OnWebRtcEvent;
                slotService.StartWatchdog();

                Log($"[MainWindow] WebRTC service '{slot}' initialized: IsReadyForCalls={slotService.IsReadyForCalls}");

                if (!isSecondary)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (MainStatusTextBlock != null)
                        {
                            MainStatusTextBlock.Text = "Initializing JsSIP...";
                            if (MainStatusIndicator != null)
                                MainStatusIndicator.Background = (Brush)FindResource("TextSecondaryBrush");
                        }
                    });
                }

                // Инициализируем UA для слота через ReinitializeUAAsync
                var config = GetWebRtcConfigForConnection(isSecondary);
                if (config != null)
                {
                    await slotService.ReinitializeUAAsync(
                        config.WsUri,
                        config.SipUri,
                        username ?? "",
                        sipPassword ?? ""
                    );
                    Log($"[MainWindow] initUA command sent via ReinitializeUAAsync (slot={slot})");

                    Dispatcher.Invoke(() =>
                    {
                        UpdateConnectionStatus();
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] ERROR in InitializeWebRtcServiceAsync: {ex.Message}");
                Log($"[MainWindow] Stack trace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Log($"[MainWindow] Inner exception: {ex.InnerException.Message}");
                }
                
                // Обновляем статус при ошибке
                Dispatcher.Invoke(() =>
                {
                    UpdateConnectionStatus();
                });
            }
        }

        private void WebRtcEngine_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            try
            {
                Log($"[MainWindow] WebView2 ProcessFailed: kind={e.ProcessFailedKind}");

                // Schedule a recovery; do not block the WebView2 callback thread.
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { await RecoverWebRtcEngineAsync("webview_process_failed"); } catch { }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { }
        }
        
        /// <summary>
        /// Обработчик событий WebRTC сервиса (UI координатор)
        /// </summary>
        private void OnWebRtcEvent(WebRtcEventDto dto)
        {
            try
            {
                // Never block background threads waiting for UI thread (can deadlock after sleep/resume).
                // Use Normal priority: Background deferred originate auto-answer / state updates behind rendering,
                // which added perceptible lag before AnswerAsync ran.
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => HandleWebRtcEventOnUi(dto)), System.Windows.Threading.DispatcherPriority.Normal);
                    return;
                }

                HandleWebRtcEventOnUi(dto);
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] ERROR in OnWebRtcEvent: {ex.Message}");
            }
        }

        private void HandleWebRtcEventOnUi(WebRtcEventDto dto)
        {
            try
            {
                // Connection-status events for the secondary slot must update the secondary
                // status block instead of the main one. Call/session events fall through and
                // are slot-aware via dto.SessionId / dto.Slot in their own handlers below.
                bool isSecondarySlot = string.Equals(dto.Slot, "secondary", StringComparison.OrdinalIgnoreCase);

                switch (dto.Type)
                {
                        case "ua_started":
                            Log($"[MainWindow] WebRTC UA started (slot={dto.Slot}), connecting...");
                            if (isSecondarySlot)
                            {
                                if (SecondaryStatusTextBlock != null)
                                {
                                    SecondaryStatusTextBlock.Text = "Connecting...";
                                    if (SecondaryStatusIndicator != null)
                                        SecondaryStatusIndicator.Background = (Brush)FindResource("TextSecondaryBrush");
                                }
                            }
                            else if (MainStatusTextBlock != null)
                            {
                                MainStatusTextBlock.Text = "Connecting to WebRTC...";
                                if (MainStatusIndicator != null)
                                    MainStatusIndicator.Background = (Brush)FindResource("TextSecondaryBrush");
                            }
                            break;
                        case "ws_connected":
                        case "ua_connected":
                            Log($"[MainWindow] ✓ WebRTC WebSocket connected (slot={dto.Slot})");
                            if (isSecondarySlot)
                            {
                                if (SecondaryStatusTextBlock != null)
                                {
                                    SecondaryStatusTextBlock.Text = "Connecting...";
                                    if (SecondaryStatusIndicator != null)
                                        SecondaryStatusIndicator.Background = (Brush)FindResource("TextSecondaryBrush");
                                }
                                Dispatcher.BeginInvoke(new Action(UpdateSecondaryConnectionStatus),
                                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                                break;
                            }
                            if (MainStatusTextBlock != null)
                            {
                                MainStatusTextBlock.Text = "Connected to WebRTC...";
                                if (MainStatusIndicator != null)
                                    MainStatusIndicator.Background = (Brush)FindResource("AccentGreenBrush");
                            }
                            // Обновляем статус подключения после подключения WebSocket
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                UpdateConnectionStatus();
                            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            break;
                        case "registered":
                        case "ua_registered":
                            Log($"[MainWindow] ✓ WebRTC UA registered (slot={dto.Slot})");

                            if (isSecondarySlot)
                            {
                                UpdateSecondaryConnectionStatus();
                                UpdateCallButtonMode();
                                break;
                            }

                            // ВАЖНО: WebRtcService уже обновил _registered в OnEngineEvent
                            // Проверяем состояние перед обновлением
                            bool isReady = WebRtcService.Main?.IsReadyForCalls ?? false;
                            bool isConnectedCheck = IsConnected;
                            Log($"[MainWindow] ua_registered event: IsReadyForCalls={isReady}, IsConnected={isConnectedCheck}, _engine={WebRtcService.Main != null}");
                            
                            // Обновляем статус напрямую (мы уже в Dispatcher.Invoke)
                            // UpdateConnectionStatus() проверит IsConnected и обновит статус
                            UpdateConnectionStatus();
                            UpdateWebRtcIndicator();
                            
                            // Проверяем результат обновления
                            string finalStatus = MainStatusTextBlock?.Text ?? "";
                            bool finalIsReady = WebRtcService.Main?.IsReadyForCalls ?? false;
                            bool finalIsConnected = IsConnected;
                            Log($"[MainWindow] After status update: MainStatusTextBlock.Text='{finalStatus}', IsReadyForCalls={finalIsReady}, IsConnected={finalIsConnected}");
                            
                            // ДОПОЛНИТЕЛЬНАЯ ПРОВЕРКА: Если статус все еще "Initializing..." или "Connecting..." или "Connected to WebRTC...", принудительно обновляем
                            if ((finalStatus.Contains("Connecting") || finalStatus.Contains("Initializing") || finalStatus.Contains("Connected to WebRTC")) && finalIsReady && finalIsConnected)
                            {
                                Log($"[MainWindow] WARNING: Status still shows '{finalStatus}' but WebRTC is ready! Forcing update to 'Connected with WebRTC'...");
                                if (MainStatusTextBlock != null)
                                {
                                    MainStatusTextBlock.Text = "Connected with WebRTC";
                                    if (MainStatusIndicator != null)
                                        MainStatusIndicator.Background = (Brush)FindResource("AccentGreenBrush");
                                }
                                UpdateCallButtonMode();
                            }
                            else if (finalIsReady && finalIsConnected && finalStatus != "Connected with WebRTC")
                            {
                                // Если WebRTC готов, но статус не обновился - принудительно обновляем
                                Log($"[MainWindow] WARNING: WebRTC is ready but status is '{finalStatus}', forcing update to 'Connected with WebRTC'...");
                                if (MainStatusTextBlock != null)
                                {
                                    MainStatusTextBlock.Text = "Connected with WebRTC";
                                    if (MainStatusIndicator != null)
                                        MainStatusIndicator.Background = (Brush)FindResource("AccentGreenBrush");
                                }
                                UpdateCallButtonMode();
                            }
                            else if (!finalIsReady && finalStatus == "Connected to WebRTC...")
                            {
                                // Если статус "Connected to WebRTC..." но WebRTC не готов - это промежуточное состояние
                                // Ждем регистрации, не меняем статус
                                Log($"[MainWindow] Status is 'Connected to WebRTC...' but IsReadyForCalls={finalIsReady}, waiting for registration...");
                            }
                            break;
                        case "reg_failed":
                        case "ua_registration_failed":
                            Log($"[MainWindow] ✗ WebRTC UA registration failed (slot={dto.Slot}): {dto.Message}");
                            {
                                string msg = string.IsNullOrWhiteSpace(dto.Message)
                                    ? "Registration failed"
                                    : dto.Message.Trim();
                                if (msg.Length > 96)
                                    msg = msg.Substring(0, 93) + "...";

                                if (isSecondarySlot)
                                {
                                    if (SecondaryStatusTextBlock != null)
                                    {
                                        SecondaryStatusTextBlock.Text = msg;
                                        if (SecondaryStatusIndicator != null)
                                            SecondaryStatusIndicator.Background = (Brush)FindResource("AccentRedBrush");
                                    }
                                    UpdateConnectionStatus();
                                    break;
                                }

                                if (MainStatusTextBlock != null)
                                {
                                    MainStatusTextBlock.Text = msg;
                                    if (MainStatusIndicator != null)
                                        MainStatusIndicator.Background = (Brush)FindResource("AccentRedBrush");
                                }
                            }
                            UpdateConnectionStatus();
                            UpdateWebRtcIndicator();
                            OnConnectionStatusChanged?.Invoke(MainStatusTextBlock?.Text ?? "");
                            break;
                        case "unregistered":
                        case "ua_unregistered":
                        case "ws_disconnected":
                        case "ua_disconnected":
                            if (isSecondarySlot)
                            {
                                UpdateConnectionStatus();
                                UpdateWebRtcIndicator();
                                break;
                            }
                            UpdateConnectionStatus();
                            UpdateWebRtcIndicator();
                            break;
                        case "incoming":
                            // Координатор UI: открываем CallWindow для входящего звонка
                            // Устанавливаем cooldown для блокировки SIP входящих
                            if (_sipService != null)
                            {
                                _sipService.SetIgnoreSipCooldown(3);
                            }
                            HandleIncomingWebRtcCall(dto);
                            break;
                        case "new_session":
                            // Устанавливаем cooldown для блокировки SIP входящих при новой сессии
                            if (_sipService != null)
                            {
                                _sipService.SetIgnoreSipCooldown(3);
                            }

                            // PBX Originate: auto-answer on new_session (before batched "incoming" events).
                            if (!isSecondarySlot
                                && dto.Data is System.Text.Json.JsonElement nsData
                                && nsData.ValueKind == System.Text.Json.JsonValueKind.Object
                                && nsData.TryGetProperty("direction", out var nsDir)
                                && string.Equals(nsDir.GetString(), "incoming", StringComparison.OrdinalIgnoreCase)
                                && nsData.TryGetProperty("callerNumber", out var nsCaller))
                            {
                                dto.CallerNumber = nsCaller.GetString();
                                if (TryHandleOriginateInboundInvite(dto))
                                    break;
                            }
                            break;
                        case "call_accepted":
                        case "call_confirmed":
                            Log($"[MainWindow] WebRTC call {dto.Type} (sessionId: {dto.SessionId})");
                            // Устанавливаем cooldown при установлении соединения
                            if (_sipService != null)
                            {
                                _sipService.SetIgnoreSipCooldown(3);
                            }
                            break;
                        case "call_ended":
                        case "call_failed":
                            Log($"[MainWindow] WebRTC call {dto.Type} (sessionId: {dto.SessionId})");
                            // Устанавливаем cooldown после завершения звонка (для защиты от ретраи INVITE)
                            if (_sipService != null)
                            {
                                _sipService.SetIgnoreSipCooldown(3);
                            }
                            break;
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] ERROR in HandleWebRtcEventOnUi: {ex.Message}");
            }
        }
        
        /// <summary>Digits-only comparison so "+1 702-900-0000" matches "+17029000000" from SIP.</summary>
        private static string NormalizePhoneDigits(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return new string(s.Where(char.IsDigit).ToArray());
        }

        /// <summary>
        /// Compact destination for SIP/AMI/WebRTC: strip spaces and punctuation; add leading +
        /// for E.164 (&gt;= 11 digits or user typed +). Short internal extensions (no +, ≤6 digits) unchanged.
        /// </summary>
        private static string NormalizePhoneForDial(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var trimmed = raw.Trim();
            var digits = NormalizePhoneDigits(trimmed);
            if (digits.Length == 0) return trimmed;
            if (trimmed.StartsWith("+", StringComparison.Ordinal) || digits.Length >= 11)
                return "+" + digits;
            return digits;
        }

        private static bool PhoneNumbersLooselyMatch(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            var da = NormalizePhoneDigits(a);
            var db = NormalizePhoneDigits(b);
            if (da.Length >= 7 && db.Length >= 7 && da == db) return true;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Обрабатывает входящий WebRTC звонок (UI координатор)
        /// </summary>
        private bool IsOwnOutboundCallerId(string? number)
        {
            if (string.IsNullOrEmpty(number) || _allowedCallerIds.Count == 0)
                return false;
            return _allowedCallerIds.Any(id => PhoneNumbersLooselyMatch(number, id));
        }

        private void ClearPendingOriginateState(bool resetAcceptedSession = false)
        {
            OriginateCoordinator.Clear(resetAcceptedSession);
            _ = WebRtcService.Main.NotifyOriginatePendingAsync(false);
        }

        /// <summary>
        /// PBX Originate rings the softphone with an inbound INVITE (From = selected trunk CallerID).
        /// Attach it to the outbound CallWindow instead of opening a fake incoming call UI.
        /// </summary>
        private bool TryHandleOriginateInboundInvite(WebRtcEventDto dto)
        {
            if (string.IsNullOrEmpty(dto.SessionId) || string.IsNullOrEmpty(dto.CallerNumber))
                return false;

            bool callerIsOwnTrunk = IsOwnOutboundCallerId(dto.CallerNumber)
                || (!string.IsNullOrEmpty(OriginateCoordinator.PendingCallerId)
                    && PhoneNumbersLooselyMatch(dto.CallerNumber, OriginateCoordinator.PendingCallerId));

            CallWindow? outgoing = OriginateCoordinator.GetOutgoingCallWindow();

            bool hasOriginateContext = OriginateCoordinator.IsPending
                || (outgoing != null && outgoing.OpenedAsOutgoingCall);

            if (!hasOriginateContext && !callerIsOwnTrunk)
                return false;

            var acceptedSessionId = OriginateCoordinator.AcceptedSessionId;
            if (!string.IsNullOrEmpty(acceptedSessionId))
            {
                if (acceptedSessionId == dto.SessionId)
                    return true;

                Log($"[Call][Originate] Declining duplicate/stale originate INVITE (session={dto.SessionId}, accepted={acceptedSessionId}, caller={dto.CallerNumber})");
                _ = WebRtcService.GetSlot(dto.Slot ?? "main").HangupAsync(dto.SessionId);
                return true;
            }

            bool isOriginateCallback = false;
            if (hasOriginateContext)
            {
                string? originateId = null;
                if (dto.Data is System.Text.Json.JsonElement dataEl
                    && dataEl.ValueKind == System.Text.Json.JsonValueKind.Object
                    && dataEl.TryGetProperty("originateId", out var oidProp)
                    && oidProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    originateId = oidProp.GetString();
                }

                if (!string.IsNullOrEmpty(originateId) && !string.IsNullOrEmpty(OriginateCoordinator.PendingId) && originateId == OriginateCoordinator.PendingId)
                {
                    Log($"[Call][Originate] Matched by X-Callspire-Originate header: {originateId}");
                    isOriginateCallback = true;
                }
                else if (!string.IsNullOrEmpty(OriginateCoordinator.PendingCallerId)
                         && PhoneNumbersLooselyMatch(dto.CallerNumber, OriginateCoordinator.PendingCallerId))
                {
                    Log($"[Call][Originate] Matched by CallerID number: incoming={dto.CallerNumber}, expected={OriginateCoordinator.PendingCallerId}");
                    isOriginateCallback = true;
                }
                else if (!string.IsNullOrEmpty(OriginateCoordinator.PendingDestination)
                         && PhoneNumbersLooselyMatch(dto.CallerNumber, OriginateCoordinator.PendingDestination))
                {
                    Log($"[Call][Originate] Matched by destination number: incoming={dto.CallerNumber}, expected={OriginateCoordinator.PendingDestination}");
                    isOriginateCallback = true;
                }
                else if (OriginateCoordinator.GetPendingCallWindow() != null)
                {
                    Log($"[Call][Originate] Fallback: inbound INVITE while pending outgoing CallWindow (caller={dto.CallerNumber}, session={dto.SessionId})");
                    isOriginateCallback = true;
                }
                else if (outgoing != null && outgoing.OpenedAsOutgoingCall && callerIsOwnTrunk)
                {
                    Log($"[Call][Originate] Fallback2: own-trunk INVITE while outbound CallWindow open (caller={dto.CallerNumber})");
                    isOriginateCallback = true;
                }
                else if (callerIsOwnTrunk)
                {
                    Log($"[Call][Originate] Fallback4: own-trunk INVITE during pending originate (caller={dto.CallerNumber}, session={dto.SessionId})");
                    isOriginateCallback = true;
                }
            }
            else if (callerIsOwnTrunk && outgoing != null && outgoing.OpenedAsOutgoingCall)
            {
                Log($"[Call][Originate] Fallback3: own-trunk INVITE matched open outbound window (caller={dto.CallerNumber})");
                isOriginateCallback = true;
            }

            if (!isOriginateCallback)
                return false;

            Log($"[Call][Originate] Auto-answering PBX callback for originate to {OriginateCoordinator.PendingDestination ?? outgoing?.CallRemoteNumber}");
            var attachWindow = OriginateCoordinator.GetPendingCallWindow() ?? outgoing;
            OriginateCoordinator.AcceptSession(dto.SessionId);
            _ = WebRtcService.GetSlot(dto.Slot ?? "main").NotifyOriginateAcceptedAsync(dto.SessionId);

            _ = WebRtcService.GetSlot(dto.Slot ?? "main").AnswerAsync(dto.SessionId);
            attachWindow?.SetOriginateWebRtcSessionId(dto.SessionId);
            return true;
        }

        private void HandleIncomingWebRtcCall(WebRtcEventDto dto)
        {
            try
            {
                if (string.IsNullOrEmpty(dto.SessionId) || string.IsNullOrEmpty(dto.CallerNumber))
                {
                    Log("[MainWindow] WARNING: Incoming call without sessionId or callerNumber");
                    return;
                }

                if (TryHandleOriginateInboundInvite(dto))
                    return;

                // PBX Originate: пока ждём callback — никакого входящего UI, только auto-answer на нужную сессию.
                if (OriginateCoordinator.IsPending)
                {
                    Log($"[Call][Originate] Suppressing incoming UI while awaiting PBX callback (session={dto.SessionId}, caller={dto.CallerNumber})");
                    _ = WebRtcService.GetSlot(dto.Slot ?? "main").HangupAsync(dto.SessionId);
                    return;
                }

                // Own-trunk INVITE during outbound originate must never become a separate incoming call UI.
                if (IsOwnOutboundCallerId(dto.CallerNumber)
                    && (OriginateCoordinator.GetOutgoingCallWindow() != null
                        || !string.IsNullOrEmpty(OriginateCoordinator.AcceptedSessionId)))
                {
                    Log($"[Call][Originate] Rejecting own-trunk INVITE during originate (session={dto.SessionId}, caller={dto.CallerNumber})");
                    _ = WebRtcService.GetSlot(dto.Slot ?? "main").HangupAsync(dto.SessionId);
                    return;
                }

                Log($"[Call][WebRTC] Handling incoming call (sessionId: {dto.SessionId}, caller: {dto.CallerNumber}, slot: {dto.Slot})");

                // Одно окно активного звонка (как у SIP HandleIncomingCall): защита от гонки UI после BeginInvoke и от второго входящего.
                var duplicateWindowGuard = OriginateCoordinator.GetOutgoingCallWindow()
                    ?? CallHandlingHelpers.FindExistingCallWindow();
                if (duplicateWindowGuard != null && !duplicateWindowGuard.IsClosing())
                {
                    if (IsOwnOutboundCallerId(dto.CallerNumber) && duplicateWindowGuard.OpenedAsOutgoingCall)
                    {
                        Log($"[MainWindow] HandleIncomingWebRtcCall: own-trunk INVITE with outbound window open — attaching (session={dto.SessionId})");
                        OriginateCoordinator.AcceptSession(dto.SessionId);
                        _ = WebRtcService.GetSlot(dto.Slot ?? "main").NotifyOriginateAcceptedAsync(dto.SessionId);
                        _ = WebRtcService.GetSlot(dto.Slot ?? "main").AnswerAsync(dto.SessionId);
                        duplicateWindowGuard.SetOriginateWebRtcSessionId(dto.SessionId);
                        return;
                    }

                    Log($"[MainWindow] HandleIncomingWebRtcCall: активное CallWindow уже есть — второе входящее игнорируем (session={dto.SessionId}, caller={dto.CallerNumber}, slot={dto.Slot})");
                    _ = WebRtcService.GetSlot(dto.Slot ?? "main").HangupAsync(dto.SessionId);
                    return;
                }

                bool incomingIsSecondary = string.Equals(dto.Slot, "secondary", StringComparison.OrdinalIgnoreCase);
                var config = GetWebRtcConfigForConnection(incomingIsSecondary);
                if (config != null)
                {
                    // Сохраняем время начала входящего звонка для истории
                    DateTime incomingCallStartTime = DateTime.Now;
                    
                    // Добавляем запись в историю звонков (входящий WebRTC звонок)
                    var incomingCallItem = new CallHistoryItem
                    {
                        PhoneNumber = dto.CallerNumber,
                        CallTime = incomingCallStartTime,
                        Status = CallStatus.Calling,
                        IsIncoming = true,
                        Transport = CallTransport.WebRtc,
                        WebRtcSessionId = dto.SessionId,
                        ConnectionSlot = incomingIsSecondary ? CallConnectionSlot.Secondary : CallConnectionSlot.Main
                    };
                    _callHistoryService.AddCall(incomingCallItem);
                    LoadCallHistory();
                    
                    var callWindow = new CallWindow(config, dto.CallerNumber, isIncomingCall: true, webRtcSessionId: dto.SessionId, webRtcService: WebRtcService.GetSlot(dto.Slot ?? "main"))
                    {
                        Owner = this
                    };
                    
                    // Подписываемся на события
                    callWindow.OnIncomingCallStatusChanged += (phoneNumber, callTime, status, duration) =>
                    {
                        _callHistoryService.UpdateCallStatus(phoneNumber, callTime, status, duration);
                        RefreshVisibleDashboards();
                    };
                    
                    callWindow.OnCallDetailsChanged += (phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId, inboundRtpPackets) =>
                    {
                        string? outboundCallerId = callWindow.GetOutboundCallerId();
                        _callHistoryService.UpdateCallDetails(phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId, outboundCallerId, inboundRtpPackets: inboundRtpPackets);
                        
                        Dispatcher.Invoke(RefreshVisibleDashboards);

                        if (string.IsNullOrEmpty(outboundCallerId) && endedBy != CallEndedBy.Unknown && wasAnswered)
                            TryFetchOutboundCallerIdFromCdr(phoneNumber, callTime);

                        string? sessionId = transport == CallTransport.WebRtc ? webRtcSessionId : sipCallId;

                        long? foundLeadId = null;
                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            var callWindows = Application.Current.Windows.OfType<CallWindow>().ToList();
                            foreach (var window in callWindows)
                            {
                                try
                                {
                                    var windowLeadId = window.GetAmoCrmLeadId();
                                    if (windowLeadId.HasValue)
                                    {
                                        foundLeadId = windowLeadId;
                                        Log($"[MainWindow] OnCallDetailsChanged: Retrieved leadId from CallWindow: {foundLeadId.Value}");
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }
                        
                        ProcessCallInAmoCrm(phoneNumber, callTime, technicalDetails, recordingFilePath, sessionId, foundLeadId);
                    };
                    
                    callWindow.Show();
                    callWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] ERROR in HandleIncomingWebRtcCall: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Предварительно инициализирует WebView2 Environment для ускорения последующей инициализации
        /// </summary>
        private async System.Threading.Tasks.Task PreInitializeWebView2Environment()
        {
            try
            {
                // Создаем Environment заранее, чтобы он был готов к использованию
                var userData = Path.Combine(
                    AppDataHelper.GetAppDataPath(),
                    "WebView2");
                
                Directory.CreateDirectory(userData);
                Log("[MainWindow] Pre-initializing WebView2 Environment...");
                
                // Создаем Environment асинхронно в фоне
                var envOptions = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments =
                        "--force-webrtc-ip-handling-policy=default_public_interface_only " +
                        "--webrtc-ip-handling-policy=default_public_interface_only"
                };
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userData, envOptions);
                Log("[MainWindow] WebView2 Environment pre-initialized successfully");
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error pre-initializing WebView2 Environment: {ex.Message}");
                // Не критично, инициализация произойдет позже
            }
        }
        
        // WebRTC инициализация при старте отключена - она вызывает проблемы с WebView2
        // WebRTC будет автоматически инициализирован при открытии Advanced настроек,
        // где WebView2 уже готов в визуальном дереве (определен в XAML)
        // Это решает проблему зависания на Step 7 при старте приложения
        private async System.Threading.Tasks.Task InitializeWebRtcOnStartup()
        {
            // Отключено - WebRTC будет инициализирован при открытии Advanced настроек
            // где WebView2 уже готов в визуальном дереве
            await Task.CompletedTask;
        }
        
        /// <summary>
        /// Получает общий экземпляр WebRTC сервиса для использования в SettingsWindow
        /// </summary>
        public static WebRtcStatusService? GetSharedWebRtcStatusService()
        {
            return _sharedWebRtcStatusService;
        }
        
        /// <summary>
        /// Устанавливает общий экземпляр WebRTC сервиса из SettingsWindow
        /// </summary>
        public static void SetSharedWebRtcStatusService(WebRtcStatusService? service)
        {
            _sharedWebRtcStatusService = service;
        }


        // WindowChrome теперь обрабатывает изменение размера автоматически
        // Удалены все кастомные resize grips и Win32 API вызовы

        private async System.Threading.Tasks.Task TryConnectAndCheckStatus()
        {
            try
            {
                // Проверяем, включен ли WebRTC режим
                bool useWebRtc = ShouldUseWebRtc();
                
                // Проверяем настройки подключения в зависимости от режима
                bool hasConnectionSettings = false;
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    string? sipPassword = SipPasswordProvider.GetPassword(settings);
                    string? mainRtcPass = SipPasswordProvider.GetMainWebRtcPassword(settings);
                    string? mainRtcUser = AppSettings.EffectiveMainWebRtcUsername(settings);
                    
                    if (useWebRtc)
                    {
                        // Для WebRTC проверяем WebRTC настройки (отдельные учётные данные или legacy SIP)
                        hasConnectionSettings = settings != null && 
                            !string.IsNullOrEmpty(settings.WebRtcWsUri) && 
                            !string.IsNullOrEmpty(mainRtcUser) && 
                            !string.IsNullOrEmpty(mainRtcPass);
                    }
                    else
                    {
                        // Для SIP проверяем SIP настройки
                        hasConnectionSettings = settings != null && 
                            !string.IsNullOrEmpty(settings.SipServer) && 
                            !string.IsNullOrEmpty(settings.SipUsername) && 
                            !string.IsNullOrEmpty(sipPassword);
                    }
                }
                
                // Если нет настроек подключения, показываем сообщение сразу
                if (!hasConnectionSettings)
                {
                    await System.Threading.Tasks.Task.Delay(300); // Небольшая задержка для загрузки UI
                    Dispatcher.Invoke(() =>
                    {
                        var result = CustomMessageBox.Show(
                            "You are not connected to the PBX server.\n\nClick OK to open connection settings.",
                            "Connection Required",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            this
                        );
                        
                        // При нажатии OK открываем окно настроек
                        if (result == MessageBoxResult.OK)
                        {
                            OpenSettingsWindow();
                        }
                    });
                    return;
                }
                
                if (useWebRtc)
                {
                    // Если WebRTC включен, проверяем статус WebRTC вместо SIP
                    // Даем больше времени на инициализацию WebRTC (может занять до 10-15 секунд)
                    // Проверяем статус несколько раз с интервалами, чтобы не показывать окно слишком рано
                    for (int i = 0; i < 15; i++) // Проверяем до 15 секунд
                    {
                        await System.Threading.Tasks.Task.Delay(1000);
                        
                        // Обновляем статус подключения (он может меняться через события WebRTC)
                        Dispatcher.Invoke(() =>
                        {
                            UpdateConnectionStatus();
                        });
                        
                        // Проверяем, готов ли WebRTC
                        bool webRtcReady = false;
                        try
                        {
                            var webRtcService = WebRtcService.Main;
                            webRtcReady = webRtcService != null && webRtcService.IsReadyForCalls;
                        }
                        catch
                        {
                            webRtcReady = false;
                        }
                        
                        if (webRtcReady)
                        {
                            // WebRTC готов - обновляем статус и выходим без показа окна
                            Dispatcher.Invoke(() =>
                            {
                                UpdateConnectionStatus();
                            });
                            Log("[MainWindow] TryConnectAndCheckStatus: WebRTC is ready, no dialog needed");
                            return; // Не показываем окно, если WebRTC готов
                        }
                    }
                    
                    // Если после всех проверок WebRTC все еще не готов, показываем сообщение
                    // Но только если статус действительно "Not connected" (не "Initializing..." или "Connecting...")
                    Dispatcher.Invoke(() =>
                    {
                        bool isConnected = IsConnected;
                        string currentStatus = MainStatusTextBlock?.Text ?? "";
                        
                        // Не показываем окно, если идет инициализация или подключение
                        bool isInitializing = currentStatus.Contains("Initializing") || 
                                            currentStatus.Contains("Connecting") ||
                                            currentStatus.Contains("Connected to WebRTC");
                        
                        if (!isConnected && !isInitializing)
                        {
                            // WebRTC не готов и не идет инициализация - показываем сообщение
                            var result = CustomMessageBox.Show(
                                "WebRTC is not ready. Please check your WebRTC settings.\n\nClick OK to open settings.",
                                "WebRTC Connection Required",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information,
                                this
                            );
                            
                            if (result == MessageBoxResult.OK)
                            {
                                OpenSettingsWindow();
                            }
                        }
                        
                        // Обновляем статус и состояние кнопки
                        UpdateConnectionStatus();
                    });
                    return;
                }
                
                // SIP: не вызываем TryConnectFromSettings() здесь — это делает MainWindow_Loaded
                // (DispatcherPriority.ApplicationIdle). Иначе два параллельных ConnectWithSettings ломают
                // транспорт, долго грузят UI-поток и дают «зависание» при старте.
                await System.Threading.Tasks.Task.Delay(2500);
                
                // Проверяем, подключены ли мы
                if (!IsConnected)
                {
                    // Показываем сообщение сразу
                    Dispatcher.Invoke(() =>
                    {
                        var result = CustomMessageBox.Show(
                            "You are not connected to the PBX server.\n\nClick OK to open connection settings.",
                            "Connection Required",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            this
                        );
                        
                        // При нажатии OK открываем окно настроек
                        if (result == MessageBoxResult.OK)
                        {
                            OpenSettingsWindow();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                // Игнорируем ошибки при проверке подключения
                Log($"[MainWindow] Error checking connection status: {ex.Message}");
            }
        }
        
        private void OpenSettingsWindow()
        {
            // Если окно настроек уже открыто — просто поднимаем его.
            var existing = Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault();
            if (existing != null)
            {
                try
                {
                    if (existing.WindowState == WindowState.Minimized)
                        existing.WindowState = WindowState.Normal;
                    existing.Activate();
                    existing.Focus();
                    existing.SyncKommoGatewayModuleFromMain();
                }
                catch { }
                return;
            }

            var settingsWindow = new SettingsWindow();

            // Немодально: главное окно остается доступным.
            CenterNonOwnedWindowOverThis(settingsWindow);
            settingsWindow.Show();
            try
            {
                settingsWindow.Activate();
                settingsWindow.Focus();
            }
            catch { }
        }

        private void CenterNonOwnedWindowOverThis(Window child)
        {
            WindowWorkAreaHelper.CenterOverHost(child, this);
        }

        private async System.Threading.Tasks.Task TryConnectFromSettings()
        {
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (settings != null)
                    {
                        SipPasswordProvider.MigratePlaintextToEncryptedIfNeeded(settingsFilePath, settings);
                        TurnPasswordProvider.MigratePlaintextToEncryptedIfNeeded(settingsFilePath, settings);
                        
                        // Kommo via PBX Gateway is initialized in InitializeMikoPbxCdrService.
                        if (!IsPbxGatewayCdrConfigured(settings))
                            InitializeAmoCrmService(settings);
                    }
                    string? sipPassword = SipPasswordProvider.GetPassword(settings);

                    // IMPORTANT: If WebRTC mode is enabled, SIP must be fully disabled (no registration, no SIP calls).
                    if (settings?.UseWebRtcAudio == true)
                    {
                        _sipService?.Dispose();
                        _sipService = null;
                        return;
                    }
                    
                    if (settings != null && !string.IsNullOrEmpty(settings.SipServer) && 
                        !string.IsNullOrEmpty(settings.SipUsername) && !string.IsNullOrEmpty(sipPassword))
                    {
                        // Автоматически подключаемся при запуске, если есть настройки
                        await ConnectWithSettings(settings);
                    }
                    
                    // ПРИМЕЧАНИЕ: Второе подключение теперь инициализируется в MainWindow_Loaded
                    // для обоих режимов (WebRTC и SIP), чтобы избежать дублирования
                }
            }
            catch
            {
                // Игнорируем ошибки при автоподключении
            }
        }

        private async System.Threading.Tasks.Task ConnectWithSettings(AppSettings settings)
        {
            try
            {
                // IMPORTANT: If WebRTC mode is enabled, SIP must be fully disabled.
                if (settings.UseWebRtcAudio)
                {
                    _sipService?.Dispose();
                    _sipService = null;
                    UpdateConnectionStatus();
                    UpdateWebRtcIndicator();
                    return;
                }

                SipEndpointHelper.ParseStoredSipServer(settings.SipServer, out string sipHost, out int port);

                _sipService?.Dispose();

                _sipService = new SipService(
                    settings.SipUsername ?? "", 
                    SipPasswordProvider.GetPassword(settings) ?? "", 
                    sipHost, 
                    port,
                    settings.MicrophoneDeviceNumber, 
                    settings.SpeakerDeviceNumber,
                    settings.AudioCodec ?? "PCMU",
                    settings.AudioSampleRate > 0 ? settings.AudioSampleRate : 16000,
                    settings.AudioBitrate > 0 ? settings.AudioBitrate : 64000,
                    isSecondaryConnection: false,
                    useTls: settings.SipUseTls,
                    useSrtp: settings.SipUseSrtp,
                    audioDeviceFactory: Softphone.Audio.AudioDeviceFactory.Default);
                _sipService.EnableAec = settings.EnableEchoCancellation;
                
                // Если включен WebRTC, пропускаем инициализацию аудио в SIPSorcery
                if (settings.UseWebRtcAudio)
                {
                    _sipService.SkipAudioInitialization();
                }
                _sipService.OnStatusChanged += (status) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            // Преобразуем технические сообщения в понятные для пользователя
                            string? userFriendlyStatus = FormatStatusMessage(status);

                            // Обновляем статус только если FormatStatusMessage вернул не null
                            // (null означает, что это техническое сообщение, которое не нужно показывать)
                            if (userFriendlyStatus != null)
                            {
                                // Для SIP режима обновляем текст статуса
                                // ВАЖНО: НЕ перезаписываем статус для WebRTC режима, чтобы не конфликтовать с UpdateConnectionStatus()
                                if (!ShouldUseWebRtc())
                                {
                                    if (MainStatusTextBlock != null)
                                    {
                                        MainStatusTextBlock.Text = userFriendlyStatus;
                                        if (MainStatusIndicator != null)
                                        {
                                            bool isConnected = _sipService?.IsRegistered ?? false;
                                            MainStatusIndicator.Background = isConnected
                                                ? (Brush)FindResource("AccentGreenBrush")
                                                : (Brush)FindResource("TextSecondaryBrush");
                                        }
                                    }
                                }
                                // Для WebRTC режима НЕ обновляем статус здесь - это делает UpdateConnectionStatus()
                                // Это предотвращает перезапись WebRTC статуса SIP сообщениями
                            }

                            // Обновляем статус подключения (текст и цвет)
                            // ВАЖНО: Для WebRTC режима UpdateConnectionStatus() имеет приоритет над SIP статусами
                            // UpdateConnectionStatus() проверит ShouldUseWebRtc() и обновит статус соответственно
                            UpdateConnectionStatus();
                            UpdateWebRtcIndicator();
                            UpdateUserAccountInfo();

                            OnConnectionStatusChanged?.Invoke(status);

                            // Добавляем в историю логов (полное техническое сообщение)
                            AddToLog(status);
                        }
                        catch (Exception ex)
                        {
                            Log($"[MainWindow] OnStatusChanged UI: {ex.Message}");
                        }
                    }), System.Windows.Threading.DispatcherPriority.Normal);
                };
                
                // Обновляем информацию о пользователе после создания SipService
                UpdateUserAccountInfo();
                
                // Обработка входящих звонков
                _sipService.OnIncomingCall += (callerNumber) =>
                {
                    Log($"[MainWindow] OnIncomingCall event received: {callerNumber}");
                    Log($"[MainWindow] Current thread ID: {System.Threading.Thread.CurrentThread.ManagedThreadId}");
                    Log($"[MainWindow] Dispatcher.CheckAccess: {Dispatcher.CheckAccess()}");
                    
                    // Проверяем, находимся ли мы в UI потоке
                    if (Dispatcher.CheckAccess())
                    {
                        // Уже в UI потоке - вызываем напрямую
                        Log($"[MainWindow] Already in UI thread, calling HandleIncomingCall directly");
                        try
                    {
                        HandleIncomingCall(callerNumber);
                            Log($"[MainWindow] HandleIncomingCall completed successfully");
                        }
                        catch (Exception ex)
                        {
                            Log($"[MainWindow] Error in HandleIncomingCall: {ex.Message}");
                            Log($"[MainWindow] Stack trace: {ex.StackTrace}");
                        }
                    }
                    else
                    {
                        // Не в UI потоке - используем BeginInvoke с высоким приоритетом
                        Log($"[MainWindow] Not in UI thread, using BeginInvoke");
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Log($"[MainWindow] BeginInvoke executed, calling HandleIncomingCall: {callerNumber}");
                            try
                            {
                                HandleIncomingCall(callerNumber);
                                Log($"[MainWindow] HandleIncomingCall completed successfully");
                            }
                            catch (Exception ex)
                            {
                                Log($"[MainWindow] Error in HandleIncomingCall: {ex.Message}");
                                Log($"[MainWindow] Stack trace: {ex.StackTrace}");
                            }
                        }), System.Windows.Threading.DispatcherPriority.Send);
                        Log($"[MainWindow] BeginInvoke called");
                    }
                };

                // ВАЖНО: Устанавливаем статус "Connecting to server..." только для SIP режима
                // Для WebRTC режима статус должен устанавливаться в ReconnectFromSettingsAsync
                if (!ShouldUseWebRtc())
                {
                    if (MainStatusTextBlock != null)
                    {
                        MainStatusTextBlock.Text = "Connecting to server...";
                        if (MainStatusIndicator != null)
                            MainStatusIndicator.Background = (Brush)FindResource("TextSecondaryBrush");
                    }
                }
                // Если WebRTC режим включен, не меняем статус здесь (он уже установлен в ReconnectFromSettingsAsync)

                await _sipService.StartAsync();

                UpdateCallButtonMode();
            }
            catch (Exception ex)
            {
                if (MainStatusTextBlock != null)
                {
                    MainStatusTextBlock.Text = $"Connection error: {ex.Message}";
                    if (MainStatusIndicator != null)
                        MainStatusIndicator.Background = (Brush)FindResource("AccentRedBrush");
                }
            }
        }

        private void ShowView(Grid view)
        {
            DialerView.Visibility = Visibility.Collapsed;
            HistoryView.Visibility = Visibility.Collapsed;
            StatisticsView.Visibility = Visibility.Collapsed;

            view.Visibility = Visibility.Visible;
            
            // Обновляем выделение кнопок в зависимости от активного вида
            UpdateButtonSelection(view);
        }
        
        private void UpdateButtonSelection(Grid activeView)
        {
            // Сбрасываем выделение всех кнопок
            DialerButton.Background = System.Windows.Media.Brushes.Transparent;
            DialerButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            
            HistoryButton.Background = System.Windows.Media.Brushes.Transparent;
            HistoryButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");

            StatisticsButton.Background = System.Windows.Media.Brushes.Transparent;
            StatisticsButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            
            // Выделяем активную кнопку
            if (activeView == DialerView)
            {
                DialerButton.Background = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                DialerButton.Foreground = System.Windows.Media.Brushes.White;
            }
            else if (activeView == HistoryView)
            {
                HistoryButton.Background = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                HistoryButton.Foreground = System.Windows.Media.Brushes.White;
            }
            else if (activeView == StatisticsView)
            {
                StatisticsButton.Background = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                StatisticsButton.Foreground = System.Windows.Media.Brushes.White;
            }
        }

        private void DialerButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(DialerView);
        }

        private async void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            _historyViewFilteredCalls = null;
            _historyFilterCaption = null;
            ShowView(HistoryView);
            await LoadCallHistoryAsync();
        }

        private async void StatisticsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(StatisticsView);
            await LoadCallStatisticsAsync();
        }

        private void InitializeStatisticsFilters()
        {
            if (StatisticsPeriodComboBox == null)
                return;

            StatisticsPeriodComboBox.Items.Clear();
            StatisticsPeriodComboBox.Items.Add(new ComboBoxItem { Content = "Last 7 days", Tag = CallStatisticsPeriod.Last7Days });
            StatisticsPeriodComboBox.Items.Add(new ComboBoxItem { Content = "Today", Tag = CallStatisticsPeriod.Today });
            StatisticsPeriodComboBox.Items.Add(new ComboBoxItem { Content = "Yesterday", Tag = CallStatisticsPeriod.Yesterday });
            StatisticsPeriodComboBox.Items.Add(new ComboBoxItem { Content = "Last 3 days", Tag = CallStatisticsPeriod.Last3Days });
            StatisticsPeriodComboBox.Items.Add(new ComboBoxItem { Content = "Custom range", Tag = CallStatisticsPeriod.Custom });
            StatisticsPeriodComboBox.SelectedIndex = 0;

            if (StatisticsConnectionComboBox != null)
            {
                StatisticsConnectionComboBox.Items.Clear();
                StatisticsConnectionComboBox.Items.Add(new ComboBoxItem { Content = "All connections", Tag = CallStatisticsScope.All });
                StatisticsConnectionComboBox.Items.Add(new ComboBoxItem { Content = "Main", Tag = CallStatisticsScope.Main });
                StatisticsConnectionComboBox.Items.Add(new ComboBoxItem { Content = "Secondary", Tag = CallStatisticsScope.Secondary });
                StatisticsConnectionComboBox.SelectedIndex = 0;
            }

            if (StatisticsDirectionComboBox != null)
            {
                StatisticsDirectionComboBox.Items.Clear();
                StatisticsDirectionComboBox.Items.Add(new ComboBoxItem { Content = "All directions", Tag = CallStatisticsDirection.All });
                StatisticsDirectionComboBox.Items.Add(new ComboBoxItem { Content = "Incoming", Tag = CallStatisticsDirection.Incoming });
                StatisticsDirectionComboBox.Items.Add(new ComboBoxItem { Content = "Outgoing", Tag = CallStatisticsDirection.Outgoing });
                StatisticsDirectionComboBox.SelectedIndex = 0;
            }

            var retentionStart = DateTime.Now.Date.AddDays(-(CallStatisticsService.RetentionDays - 1));
            if (StatisticsCustomFromDatePicker != null)
            {
                StatisticsCustomFromDatePicker.DisplayDateStart = retentionStart;
                StatisticsCustomFromDatePicker.DisplayDateEnd = DateTime.Now.Date;
                StatisticsCustomFromDatePicker.SelectedDate = retentionStart;
            }
            if (StatisticsCustomToDatePicker != null)
            {
                StatisticsCustomToDatePicker.DisplayDateStart = retentionStart;
                StatisticsCustomToDatePicker.DisplayDateEnd = DateTime.Now.Date;
                StatisticsCustomToDatePicker.SelectedDate = DateTime.Now.Date;
            }

            RefreshStatisticsConnectionComboLabels();
        }

        private void RefreshStatisticsConnectionComboLabels()
        {
            if (StatisticsConnectionComboBox == null) return;
            var names = GetStatisticsConnectionNames();
            if (StatisticsConnectionComboBox.Items.Count >= 3)
            {
                ((ComboBoxItem)StatisticsConnectionComboBox.Items[1]).Content = names.MainName;
                ((ComboBoxItem)StatisticsConnectionComboBox.Items[2]).Content = names.SecondaryName;
            }
        }

        private async void StatisticsFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (StatisticsPeriodComboBox?.SelectedItem is ComboBoxItem periodItem &&
                periodItem.Tag is CallStatisticsPeriod period)
            {
                if (StatisticsCustomRangePanel != null)
                    StatisticsCustomRangePanel.Visibility = period == CallStatisticsPeriod.Custom
                        ? Visibility.Visible : Visibility.Collapsed;
            }

            if (StatisticsView?.Visibility == Visibility.Visible)
                await LoadCallStatisticsAsync();
        }

        private async void StatisticsCustomDatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StatisticsView?.Visibility != Visibility.Visible)
                return;
            if (StatisticsPeriodComboBox?.SelectedItem is not ComboBoxItem item ||
                item.Tag is not CallStatisticsPeriod period ||
                period != CallStatisticsPeriod.Custom)
                return;

            await LoadCallStatisticsAsync();
        }

        private CallStatisticsFilter BuildStatisticsFilter()
        {
            var scope = CallStatisticsScope.All;
            var direction = CallStatisticsDirection.All;

            if (StatisticsConnectionComboBox?.SelectedItem is ComboBoxItem connItem &&
                connItem.Tag is CallStatisticsScope selectedScope)
                scope = selectedScope;

            if (StatisticsDirectionComboBox?.SelectedItem is ComboBoxItem dirItem &&
                dirItem.Tag is CallStatisticsDirection selectedDirection)
                direction = selectedDirection;

            return new CallStatisticsFilter
            {
                Period = GetSelectedStatisticsPeriod(),
                Scope = scope,
                Direction = direction,
                CustomFrom = StatisticsCustomFromDatePicker?.SelectedDate,
                CustomTo = StatisticsCustomToDatePicker?.SelectedDate
            };
        }

        private async System.Threading.Tasks.Task LoadCallStatisticsAsync()
        {
            var filter = BuildStatisticsFilter();
            var names = GetStatisticsConnectionNames();
            bool amoEnabled = IsAmoCrmStatisticsEnabled();

            var report = await System.Threading.Tasks.Task.Run(() =>
            {
                var history = _callHistoryService.GetHistory();
                return CallStatisticsService.BuildReport(
                    history,
                    filter,
                    names.MainName,
                    names.SecondaryName,
                    names.SecondaryEnabled);
            });

            _lastStatisticsReport = report;

            Dispatcher.Invoke(() =>
            {
                RefreshStatisticsConnectionComboLabels();
                BindStatisticsReport(report, names.SecondaryEnabled, amoEnabled);
            });
        }

        private bool IsAmoCrmStatisticsEnabled()
        {
            try
            {
                if (!IsAmoCrmIntegrationEnabled())
                    return false;

                if (IsAmoCrmServiceInitialized())
                    return true;

                var settings = AppDataHelper.LoadSettingsOrNew();
                if (IsLocalKommoConfigured(settings))
                    return true;

                // Gateway Kommo: subdomain and tokens live on PBX Gateway, not in local settings.
                var source = ResolveKommoConnectionSource(settings);
                if (source == "local")
                    return false;

                return IsPbxGatewayCdrConfigured(settings);
            }
            catch { return false; }
        }

        private void BindStatisticsReport(CallStatisticsReport report, bool secondaryEnabled, bool amoEnabled)
        {
            var s = report.Summary;

            if (StatisticsPeriodHintTextBlock != null)
                StatisticsPeriodHintTextBlock.Text =
                    $"Showing {s.TotalCompleted} completed call(s) from {report.FromInclusive:yyyy-MM-dd} to {report.ToInclusive:yyyy-MM-dd}";

            StatsKpiTotal.Text = s.TotalCompleted.ToString();
            StatsKpiDirectionHint.Text = s.TotalCompleted > 0 ? $"In {s.Incoming} · Out {s.Outgoing}" : "No calls";

            StatsKpiAnswered.Text = s.Successful.ToString();
            StatsKpiSuccessRate.Text = s.TotalCompleted > 0 ? $"{s.SuccessRatePercent}% success rate" : "—";

            StatsKpiUnanswered.Text = s.Unsuccessful.ToString();
            StatsKpiUnansweredHint.Text = s.Unsuccessful > 0
                ? $"Failed {s.Failed} · Cancelled {s.Cancelled}"
                : "—";

            StatsKpiTalkTime.Text = CallStatisticsService.FormatDuration(s.TotalTalkTime);
            StatsKpiAvgTalk.Text = s.Successful > 0
                ? $"Avg {CallStatisticsService.FormatDuration(s.AverageTalkTime)}"
                : "—";

            StatsKpiContactRate.Text = s.Outgoing > 0 ? $"{s.ContactRatePercent}%" : "—";
            StatsKpiRingTime.Text = s.RingTimeSampleCount > 0
                ? $"Avg ring {CallStatisticsService.FormatDuration(s.AverageRingTime)}"
                : "Avg ring —";

            StatsKpiMissed.Text = s.Missed.ToString();
            StatsKpiFailedCancelled.Text = s.Missed > 0 ? "Incoming · not answered" : "—";

            StatsDailyChartItems.ItemsSource = report.DailyBuckets;
            StatsHourlyChartItems.ItemsSource = report.HourlyBuckets;
            StatsPeakHourText.Text = report.PeakHour >= 0
                ? $"Peak hour: {report.PeakHour:00}:00–{report.PeakHour:00}:59"
                : "No activity";

            StatsOutcomeAnswered.Text = $"● Answered conversations: {s.Successful}";
            StatsOutcomeCancelled.Text = $"● Cancelled / no answer: {s.Cancelled}";
            StatsOutcomeAgentCancel.Text = $"● Hung up before answer (you): {s.AgentCancelledBeforeAnswer}";
            StatsOutcomeFailed.Text = $"● Technical failures: {s.Failed}";
            StatsOutcomeMissed.Text = $"● Missed incoming: {s.Missed}";

            bool showConnectionCompare = report.Filter.Scope == CallStatisticsScope.All && secondaryEnabled;
            StatsConnectionComparePanel.Visibility = showConnectionCompare ? Visibility.Visible : Visibility.Collapsed;
            if (showConnectionCompare)
            {
                StatsConnectionCompareItems.ItemsSource = report.ConnectionCompare.Select(row => new
                {
                    row.Name,
                    Detail = $"{row.Summary.TotalCompleted} calls · {row.Summary.SuccessRatePercent}% answered · talk {CallStatisticsService.FormatDuration(row.Summary.TotalTalkTime)}"
                }).ToList();
            }

            if (report.TransportStats.Count > 0)
            {
                StatsTransportEmptyText.Visibility = Visibility.Collapsed;
                StatsTransportItems.ItemsSource = report.TransportStats.Select(t => new
                {
                    Detail = $"{t.Label}: {t.Summary.TotalCompleted} calls, {t.Summary.SuccessRatePercent}% answered, avg {CallStatisticsService.FormatDuration(t.Summary.AverageTalkTime)}"
                }).ToList();
            }
            else
            {
                StatsTransportItems.ItemsSource = null;
                StatsTransportEmptyText.Visibility = Visibility.Visible;
            }

            StatsCrmPanel.Visibility = amoEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (amoEnabled)
            {
                var crm = report.Crm;
                StatsCrmLinked.Text = $"● Attached to AmoCRM lead: {crm.LinkedToLead} / {crm.TotalCompleted}";
                StatsCrmSentToContact.Text = crm.SentToContact > 0
                    ? $"● Sent to AmoCRM contact (no lead): {crm.SentToContact} / {crm.TotalCompleted}"
                    : "● Sent to AmoCRM contact (no lead): none";
                StatsCrmRecorded.Text = $"● Local recording saved: {crm.WithRecording} / {crm.TotalCompleted}";
                StatsCrmUploaded.Text = crm.WithRecording > 0
                    ? $"● Sent to AmoCRM: {crm.UploadedToAmo} / {crm.WithRecording}"
                    : "● Sent to AmoCRM: — (no recordings)";
                StatsCrmUploadIssues.Text = crm.RecordingNotInAmo > 0
                    ? $"● Recording not in AmoCRM: {crm.RecordingNotInAmo} (failed {crm.UploadFailed}, cancelled {crm.UploadCancelled})"
                    : "● Recording not in AmoCRM: none";
            }

            if (report.CallerIdStats.Count > 0)
            {
                StatsCallerIdEmptyText.Visibility = Visibility.Collapsed;
                StatsCallerIdItems.ItemsSource = report.CallerIdStats.Select(c => new
                {
                    Detail = $"{c.CallerId}: {c.Answered}/{c.Total} answered ({c.SuccessRatePercent}%)"
                }).ToList();
            }
            else
            {
                StatsCallerIdItems.ItemsSource = null;
                StatsCallerIdEmptyText.Visibility = Visibility.Visible;
            }

            if (report.TopNumbers.Count > 0)
            {
                StatsTopNumbersEmptyText.Visibility = Visibility.Collapsed;
                StatsTopNumbersItems.ItemsSource = report.TopNumbers.Select(n => new
                {
                    n.PhoneNumber,
                    Detail = $"{n.TotalCalls} calls · {n.Answered} ans · {CallStatisticsService.FormatDuration(n.TotalTalkTime)}"
                }).ToList();
            }
            else
            {
                StatsTopNumbersItems.ItemsSource = null;
                StatsTopNumbersEmptyText.Visibility = Visibility.Visible;
            }

            if (StatisticsLegacyHintTextBlock != null)
                StatisticsLegacyHintTextBlock.Visibility = report.HasLegacyUnknownConnection
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private CallStatisticsPeriod GetSelectedStatisticsPeriod()
        {
            if (StatisticsPeriodComboBox?.SelectedItem is ComboBoxItem item &&
                item.Tag is CallStatisticsPeriod period)
                return period;
            return CallStatisticsPeriod.Last7Days;
        }

        private (string MainName, string SecondaryName, bool SecondaryEnabled) GetStatisticsConnectionNames()
        {
            string mainName = "Main";
            string secondaryName = "Secondary";
            bool secondaryEnabled = false;

            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsPath))
                {
                    var settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(settingsPath));
                    if (settings != null)
                    {
                        if (!string.IsNullOrWhiteSpace(settings.MainConnectionName))
                            mainName = settings.MainConnectionName.Trim();
                        if (!string.IsNullOrWhiteSpace(settings.SecondaryConnectionName))
                            secondaryName = settings.SecondaryConnectionName.Trim();

                        secondaryEnabled = !string.IsNullOrWhiteSpace(settings.SipServer2) &&
                                           !string.IsNullOrWhiteSpace(settings.SipUsername2);
                    }
                }
            }
            catch { }

            return (mainName, secondaryName, secondaryEnabled);
        }

        private void StatisticsKpi_Click(object sender, MouseButtonEventArgs e)
        {
            if (_lastStatisticsReport == null || sender is not FrameworkElement el)
                return;

            var drill = el.Tag?.ToString() switch
            {
                "Answered" => CallStatisticsDrillDown.Answered,
                "Unanswered" => CallStatisticsDrillDown.Unanswered,
                "Missed" => CallStatisticsDrillDown.Missed,
                _ => CallStatisticsDrillDown.All
            };

            string caption = drill switch
            {
                CallStatisticsDrillDown.Answered => "Answered calls",
                CallStatisticsDrillDown.Unanswered => "Unanswered calls",
                CallStatisticsDrillDown.Missed => "Missed incoming calls",
                _ => "All filtered calls"
            };

            OpenHistoryFromStatistics(drill, caption);
        }

        private void StatisticsTopNumber_Click(object sender, MouseButtonEventArgs e)
        {
            if (_lastStatisticsReport == null || sender is not FrameworkElement { Tag: string phone })
                return;

            var filtered = CallStatisticsService.ApplyDrillDown(_lastStatisticsReport.MatchingCalls, CallStatisticsDrillDown.All, phone).ToList();
            _historyViewFilteredCalls = filtered;
            _historyFilterCaption = $"Number {phone} ({filtered.Count} calls)";
            ShowView(HistoryView);
            BindCallHistory(filtered);
        }

        private void OpenHistoryFromStatistics(CallStatisticsDrillDown drillDown, string caption)
        {
            if (_lastStatisticsReport == null) return;

            var filtered = CallStatisticsService.ApplyDrillDown(_lastStatisticsReport.MatchingCalls, drillDown).ToList();
            _historyViewFilteredCalls = filtered;
            _historyFilterCaption = $"{caption} ({filtered.Count})";
            ShowView(HistoryView);
            BindCallHistory(filtered);
        }

        private void StatisticsCrmMetric_Click(object sender, MouseButtonEventArgs e)
        {
            if (_lastStatisticsReport == null || sender is not FrameworkElement { Tag: string tag })
                return;

            var (drill, caption) = tag switch
            {
                "CrmLinkedToLead" => (CallStatisticsDrillDown.CrmLinkedToLead, "Attached to AmoCRM lead"),
                "CrmSentToContact" => (CallStatisticsDrillDown.CrmSentToContact, "Sent to AmoCRM contact (no lead)"),
                "CrmWithRecording" => (CallStatisticsDrillDown.CrmWithRecording, "Local recording saved"),
                "CrmUploadedToAmo" => (CallStatisticsDrillDown.CrmUploadedToAmo, "Recording sent to AmoCRM"),
                "CrmRecordingNotInAmo" => (CallStatisticsDrillDown.CrmRecordingNotInAmo, "Recording not in AmoCRM"),
                "CrmNoRecording" => (CallStatisticsDrillDown.CrmNoRecording, "No local recording file"),
                "CrmNotLinked" => (CallStatisticsDrillDown.CrmNotLinked, "Not sent to AmoCRM"),
                "CrmUploadProblems" => (CallStatisticsDrillDown.CrmUploadProblems, "AmoCRM upload problems"),
                _ => (CallStatisticsDrillDown.All, "All filtered calls")
            };

            OpenHistoryFromStatistics(drill, caption);
        }

        private void RefreshVisibleDashboards()
        {
            LoadCallHistory();
            ScheduleStatisticsRefresh();
        }

        private void ScheduleStatisticsRefresh()
        {
            if (StatisticsView?.Visibility != Visibility.Visible)
                return;

            if (_statisticsRefreshDebounceTimer == null)
            {
                _statisticsRefreshDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(450)
                };
                _statisticsRefreshDebounceTimer.Tick += StatisticsRefreshDebounceTimer_Tick;
            }

            _statisticsRefreshDebounceTimer.Stop();
            _statisticsRefreshDebounceTimer.Start();
        }

        private async void StatisticsRefreshDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _statisticsRefreshDebounceTimer?.Stop();
            await LoadCallStatisticsAsync();
        }

        private void StatisticsExportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastStatisticsReport == null)
                return;

            var dialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                FileName = $"call-statistics_{_lastStatisticsReport.FromInclusive:yyyy-MM-dd}_{_lastStatisticsReport.ToInclusive:yyyy-MM-dd}.csv"
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                var names = GetStatisticsConnectionNames();
                CallStatisticsService.SaveReportCsv(
                    _lastStatisticsReport,
                    dialog.FileName,
                    names.MainName,
                    names.SecondaryName);
                CustomMessageBox.Show($"Exported {_lastStatisticsReport.MatchingCalls.Count} call(s) to:\n{dialog.FileName}",
                    "Export complete", MessageBoxButton.OK, MessageBoxImage.Information, this);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Export failed: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }

        /// <summary>
        /// Асинхронная загрузка истории звонков, тяжелая часть выполняется в фоне,
        /// на UI потоке только биндинг данных.
        /// </summary>
        private async System.Threading.Tasks.Task LoadCallHistoryAsync()
        {
            var history = await System.Threading.Tasks.Task.Run(() =>
                _historyViewFilteredCalls ?? _callHistoryService.GetHistory());

            Dispatcher.Invoke(() => BindCallHistory(history));
        }

        /// <summary>
        /// Синхронная загрузка истории (для внутренних обновлений, когда изменений немного).
        /// </summary>
        private void LoadCallHistory()
        {
            var history = _historyViewFilteredCalls ?? _callHistoryService.GetHistory();
            BindCallHistory(history);
        }

        /// <summary>
        /// Привязывает коллекцию звонков к UI.
        /// </summary>
        private void BindCallHistory(System.Collections.Generic.List<CallHistoryItem> history)
        {
            if (HistoryFilterHintTextBlock != null)
            {
                if (!string.IsNullOrWhiteSpace(_historyFilterCaption))
                {
                    HistoryFilterHintTextBlock.Text = $"Filtered view: {_historyFilterCaption}";
                    HistoryFilterHintTextBlock.Visibility = Visibility.Visible;
                }
                else
                {
                    HistoryFilterHintTextBlock.Visibility = Visibility.Collapsed;
                }
            }

            if (history.Count == 0)
            {
                HistoryItemsControl.ItemsSource = null;
                EmptyHistoryTextBlock.Visibility = Visibility.Visible;
                return;
            }

            EmptyHistoryTextBlock.Visibility = Visibility.Collapsed;
            
            // Преобразуем в формат для отображения
            var displayItems = history.Select(call => new
            {
                PhoneNumberDisplay = call.PhoneNumber,
                PhoneNumber = call.PhoneNumber,
                TransportLabel = call.Transport == CallTransport.WebRtc ? "WebRTC" : "SIP",
                TransportBrush = call.Transport == CallTransport.WebRtc
                    ? new SolidColorBrush(Color.FromRgb(37, 99, 235))   // синий для WebRTC
                    : new SolidColorBrush(Color.FromRgb(16, 185, 129)), // зелёный для SIP
                Status = CallStatusPresentation.FormatSummary(call),
                StatusColor = CallStatusPresentation.GetForegroundBrush(call),
                CallTimeText = call.CallTime.ToString("yyyy-MM-dd HH:mm:ss"),
                DurationText = call.Duration.HasValue 
                    ? $"{call.Duration.Value.Minutes:D2}:{call.Duration.Value.Seconds:D2}" 
                    : "-",
                CallDirectionIcon = call.IsIncoming ? "CallInbound" : "CallOutbound",
                CallDirectionColor = call.IsIncoming 
                    ? new SolidColorBrush(Color.FromRgb(59, 130, 246)) // Blue for incoming
                    : new SolidColorBrush(Color.FromRgb(34, 197, 94)), // Green for outgoing
                CallItem = call // Сохраняем полный объект для доступа к деталям
            }).ToList();

            HistoryItemsControl.ItemsSource = displayItems;
        }

        private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var result = CustomMessageBox.Show("Are you sure you want to clear all call history?", 
                "Clear History", MessageBoxButton.YesNo, MessageBoxImage.Question, this);
            
            if (result == MessageBoxResult.Yes)
            {
                _callHistoryService.ClearHistory();
                LoadCallHistory();
            }
        }

        private void HistoryItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && sender is Border border && border.Tag != null)
            {
                // Получаем объект с данными из Tag
                var dataContext = border.DataContext;
                if (dataContext != null)
                {
                    // Используем рефлексию для получения CallItem
                    var callItemProperty = dataContext.GetType().GetProperty("CallItem");
                    if (callItemProperty != null)
                    {
                        var callItem = callItemProperty.GetValue(dataContext) as CallHistoryItem;
                        if (callItem != null)
                        {
                            var detailsWindow = new CallDetailsWindow(callItem);
                            CenterNonOwnedWindowOverThis(detailsWindow);
                            detailsWindow.Show();
                            try
                            {
                                detailsWindow.Activate();
                                detailsWindow.Focus();
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        private async void CallFromHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string phoneNumber)
            {
                // КРИТИЧНО: Отключаем кнопку сразу, чтобы предотвратить множественные клики
                button.IsEnabled = false;
                try
                {
                    _allowNextProgrammaticCall = true;
                    // КРИТИЧНО: Проверяем наличие активного вызова перед созданием нового
                    var existingCallWindow = CallHandlingHelpers.FindExistingCallWindow();
                    if (existingCallWindow != null)
                    {
                        MainWindow.Log($"[MainWindow] CallFromHistoryButton_Click: Active call window already exists, ignoring call to {phoneNumber}");
                        CustomMessageBox.Show(
                            "Another call is already in progress. Please end the current call before making a new one.",
                            "Call In Progress",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            this);
                        // Активируем существующее окно звонка
                        existingCallWindow.Activate();
                        existingCallWindow.BringIntoView();
                        return;
                    }

                    // Проверяем состояние WebRTC и SIP сервисов
                    bool useWebRtc = ShouldUseWebRtc();
                    if (useWebRtc)
                    {
                        if (CallHandlingHelpers.IsWebRtcCallActive())
                        {
                            MainWindow.Log($"[MainWindow] CallFromHistoryButton_Click: WebRTC call is active, ignoring call to {phoneNumber}");
                            CustomMessageBox.Show(
                                "A WebRTC call is already in progress. Please end the current call before making a new one.",
                                "Call In Progress",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning,
                                this);
                            return;
                        }
                    }
                    else
                    {
                        if (_sipService != null && _sipService.IsInCall)
                        {
                            MainWindow.Log($"[MainWindow] CallFromHistoryButton_Click: SIP call is active, ignoring call to {phoneNumber}");
                            CustomMessageBox.Show(
                                "A SIP call is already in progress. Please end the current call before making a new one.",
                                "Call In Progress",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning,
                                this);
                            return;
                        }
                    }

                    // ВАЖНО: Сразу инициируем звонок, не переключаясь на главный экран
                    // Это соответствует ожидаемому поведению пользователя
                    await PerformCall(phoneNumber, leadId: null, forceCallerIdSelection: true);
                }
                finally
                {
                    // Включаем кнопку обратно после завершения (если звонок не был создан)
                    // Если звонок был создан, кнопка будет включена после завершения звонка
                    var checkCallWindow = CallHandlingHelpers.FindExistingCallWindow();
                    if (checkCallWindow == null)
                    {
                        button.IsEnabled = true;
                    }
                }
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSettingsWindow();
        }

        private async void MainRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainRefreshButton?.IsEnabled == false || MainStatusTextBlock == null) return;
            MainRefreshButton!.IsEnabled = false;
            try
            {
                MainStatusTextBlock.Text = "Reconnecting...";
                await ReconnectFromSettingsAsync();
            }
            finally
            {
                if (MainRefreshButton != null) MainRefreshButton.IsEnabled = true;
            }
        }

        private async void SecondaryRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (SecondaryRefreshButton?.IsEnabled == false || SecondaryStatusTextBlock == null) return;
            SecondaryRefreshButton!.IsEnabled = false;
            try
            {
                SecondaryStatusTextBlock.Text = "Reconnecting...";
                await InitializeSecondConnectionAsync();
            }
            finally
            {
                if (SecondaryRefreshButton != null) SecondaryRefreshButton.IsEnabled = true;
            }
        }

        private void PhoneNumberTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (PhoneNumberTextBox.Text == "Enter the number")
            {
                PhoneNumberTextBox.Text = "";
                PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
            }
        }

        private void PhoneNumberTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PhoneNumberTextBox.Text))
            {
                PhoneNumberTextBox.Text = "Enter the number";
                PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
            }
        }

        private void ClearDialerPhoneNumber()
        {
            try
            {
                if (PhoneNumberTextBox == null) return;
                PhoneNumberTextBox.Text = "Enter the number";
                PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
            }
            catch
            {
                // ignore UI cleanup failures
            }
        }

        private void PhoneNumberTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Если пользователь начал вводить текст, меняем цвет на белый
            if (PhoneNumberTextBox.Text != "Enter the number" && PhoneNumberTextBox.Text.Length > 0)
            {
                PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
            }
        }

        private void PhoneNumberTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Если это placeholder, очищаем при первом нажатии
            if (PhoneNumberTextBox.Text == "Enter the number")
            {
                PhoneNumberTextBox.Text = "";
                PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
            }

            // Enter для звонка
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                // Если видна сплит-кнопка — звоним через основное подключение по Enter
                if (SplitCallButtonPanel?.Visibility == Visibility.Visible && (SplitCallPrimaryButton?.IsEnabled ?? false))
                {
                    SplitCallPrimary_Click(sender, e);
                }
                else if (CallButton.IsEnabled)
                {
                    CallButton_Click(sender, e);
                }
                e.Handled = true;
                return;
            }

            // Разрешаем все остальные клавиши для нормального ввода
            // TextBox сам обработает ввод цифр, букв и специальных символов
            e.Handled = false;
        }

        private void NumpadButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                // Если текст - это placeholder, очищаем его
                if (PhoneNumberTextBox.Text == "Enter the number")
                {
                    PhoneNumberTextBox.Text = "";
                    PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
                }

                string digit = button.Content.ToString() ?? "";
                PhoneNumberTextBox.Text += digit;
            }
        }

        private void BackspaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (PhoneNumberTextBox.Text == "Enter the number")
            {
                return;
            }

            if (!string.IsNullOrEmpty(PhoneNumberTextBox.Text))
            {
                PhoneNumberTextBox.Text = PhoneNumberTextBox.Text.Substring(0, PhoneNumberTextBox.Text.Length - 1);
                
                // Если текст пустой, возвращаем placeholder
                if (string.IsNullOrEmpty(PhoneNumberTextBox.Text))
                {
                    PhoneNumberTextBox.Text = "Enter the number";
                    PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
                }
            }
        }

        public event Action<string>? OnConnectionStatusChanged;

        public async System.Threading.Tasks.Task ReconnectFromSettingsAsync()
        {
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (!File.Exists(settingsFilePath))
                {
                    Log("[MainWindow] ReconnectFromSettingsAsync: Settings file not found");
                    return;
                }
                
                string json = File.ReadAllText(settingsFilePath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                
                if (settings == null)
                {
                    Log("[MainWindow] ReconnectFromSettingsAsync: Settings is null");
                    return;
                }

                // Migrate legacy plaintext secrets to encrypted (best-effort)
                SipPasswordProvider.MigratePlaintextToEncryptedIfNeeded(settingsFilePath, settings);
                TurnPasswordProvider.MigratePlaintextToEncryptedIfNeeded(settingsFilePath, settings);
                string? sipPassword = SipPasswordProvider.GetPassword(settings);
                string? mainRtcPassword = SipPasswordProvider.GetMainWebRtcPassword(settings);
                
                // КРИТИЧНО: Проверяем активные звонки перед изменением настроек
                // Не прерываем активные звонки при изменении настроек
                var existingCallWindow = CallHandlingHelpers.FindExistingCallWindow();
                bool hasActiveWebRtcCall = CallHandlingHelpers.IsWebRtcCallActive();
                bool hasActiveSipCall = _sipService != null && _sipService.IsInCall;
                
                if (existingCallWindow != null || hasActiveWebRtcCall || hasActiveSipCall || OriginateCoordinator.IsPending)
                {
                    Log("[MainWindow] ReconnectFromSettingsAsync: Active call detected, deferring reconnection until call ends");
                    Dispatcher.Invoke(() =>
                    {
                        if (MainStatusTextBlock != null)
                        {
                            MainStatusTextBlock.Text = "Settings saved. Changes will be applied after current call ends.";
                            if (MainStatusIndicator != null)
                                MainStatusIndicator.Background = (Brush)FindResource("TextSecondaryBrush");
                        }
                    });
                    return;
                }
                
                // Инициализируем AmoCRM сервис, если интеграция включена
                InitializeAmoCrmService(settings);
                
                bool mainUsesWebRtc = ShouldUseWebRtcForConnection(false) &&
                                      !string.IsNullOrEmpty(settings.WebRtcWsUri) &&
                                      !string.IsNullOrEmpty(AppSettings.EffectiveMainWebRtcUsername(settings)) &&
                                      !string.IsNullOrEmpty(SipPasswordProvider.GetMainWebRtcPassword(settings));

                if (mainUsesWebRtc)
                {
                    Log("[MainWindow] ReconnectFromSettingsAsync: Main connection transport=WebRtc, stopping SIP and initializing WebRTC slot...");

                    Dispatcher.Invoke(() =>
                    {
                        if (MainStatusTextBlock != null)
                        {
                            MainStatusTextBlock.Text = "Initializing WebRTC...";
                            if (MainStatusIndicator != null)
                                MainStatusIndicator.Background = (Brush)FindResource("TextSecondaryBrush");
                        }
                    });

                    // Stop SIP for the main slot since main uses WebRTC.
                    _sipService?.Dispose();
                    _sipService = null;

                    bool engineInitialized = WebRtcService.Main.IsReadyForCalls;
                    if (!engineInitialized)
                    {
                        try
                        {
                            var engineField = typeof(WebRtcService).GetField("_engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            var engine = engineField?.GetValue(WebRtcService.Main);
                            if (engine != null)
                            {
                                var isInitializedProp = engine.GetType().GetProperty("IsInitialized");
                                if (isInitializedProp != null)
                                {
                                    engineInitialized = (bool)(isInitializedProp.GetValue(engine) ?? false);
                                }
                            }
                        }
                        catch { }
                    }

                    if (engineInitialized)
                    {
                        var config = GetWebRtcConfigForConnection(false);
                        if (config != null)
                        {
                            await WebRtcService.Main.ReinitializeUAAsync(
                                config.WsUri, config.SipUri, AppSettings.EffectiveMainWebRtcUsername(settings) ?? "", mainRtcPassword ?? ""
                            );
                            Log("[MainWindow] ReconnectFromSettingsAsync: Main WebRTC UA reinitialized");
                        }
                        else
                        {
                            await InitializeWebRtcServiceAsync("main");
                        }
                    }
                    else
                    {
                        await InitializeWebRtcServiceAsync("main");
                    }
                }
                else
                {
                    Log("[MainWindow] ReconnectFromSettingsAsync: Main connection transport=Sip, shutting down main WebRTC (if any) and reconnecting SIP...");

                    // Detach main WebRTC slot so it stops registering on transport switch.
                    try { WebRtcService.Main.StopWatchdog(); } catch { }
                    try { WebRtcService.Main.Event -= OnWebRtcEvent; } catch { }
                    try { WebRtcService.Main.DetachEngine(resetState: true); } catch { }

                    await TryConnectFromSettings();
                }

                // Secondary slot is initialized independently — picks SIP vs WebRTC inside.
                await InitializeSecondConnectionAsync();

                await System.Threading.Tasks.Task.Delay(300);
                Dispatcher.Invoke(() =>
                {
                    UpdateConnectionStatus();
                    UpdateWebRtcIndicator();
                    UpdateUserAccountInfo();
                    Log($"[MainWindow] ReconnectFromSettingsAsync: post-update status — main='{MainStatusTextBlock?.Text ?? ""}', IsConnected={IsConnected}");
                });
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] ERROR in ReconnectFromSettingsAsync: {ex.Message}");
                Log($"[MainWindow] Stack trace: {ex.StackTrace}");
            }
            finally
            {
                // Обновляем информацию о пользователе в любом случае
                Dispatcher.Invoke(() => UpdateUserAccountInfo());
            }
        }

        private async Task ShutdownWebRtcAsync()
        {
            try
            {
                // Stop watchdog first (prevents periodic ping/getStats) on both slots.
                foreach (var svc in WebRtcService.AllSlots)
                {
                    try { svc.StopWatchdog(); } catch { }
                    try { svc.Event -= OnWebRtcEvent; } catch { }
                    try { svc.DetachEngine(resetState: true); } catch { }
                }

                // Dispose WebView2 on UI thread.
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (_webRtcEngine != null)
                        {
                            if (WebRtcHostGrid != null && WebRtcHostGrid.Children.Contains(_webRtcEngine))
                            {
                                WebRtcHostGrid.Children.Remove(_webRtcEngine);
                            }
                            _webRtcEngine.Dispose();
                            _webRtcEngine = null;
                            _sharedWebRtcHost = null;
                            Log("[MainWindow] WebRTC WebView2 disposed (switched to SIP mode)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[MainWindow] Error disposing WebRTC WebView2: {ex.Message}");
                        _webRtcEngine = null;
                        _sharedWebRtcHost = null;
                    }

                    // Refresh indicators after unloading.
                    UpdateWebRtcIndicator();
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error in ShutdownWebRtcAsync: {ex.Message}");
            }
        }

        public async void ReconnectFromSettings()
        {
            await ReconnectFromSettingsAsync();
        }

        /// <summary>
        /// Инициализирует второе SIP подключение (только SIP, независимо от основного)
        /// </summary>
        public async System.Threading.Tasks.Task InitializeSecondConnectionAsync()
        {
            await _secondConnectionInitSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (!File.Exists(settingsFilePath))
                {
                    Log("[MainWindow] InitializeSecondConnectionAsync: Settings file not found");
                    return;
                }

                string json = File.ReadAllText(settingsFilePath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                if (settings == null)
                {
                    Log("[MainWindow] InitializeSecondConnectionAsync: Settings is null");
                    return;
                }

                // Маршрутизация по выбранному транспорту второго подключения (в т.ч. legacy-вывод по WebRtcWsUri2).
                bool secondaryIsWebRtc = AppSettings.SecondaryLineUsesWebRtc(settings);
                if (secondaryIsWebRtc &&
                    !string.Equals(settings.SecondaryConnectionTransport, "WebRtc", StringComparison.OrdinalIgnoreCase))
                {
                    Log("[MainWindow] InitializeSecondConnectionAsync: Secondary WebRTC inferred from stored WSS URI (SecondaryConnectionTransport is not 'WebRtc' on disk)");
                }
                if (secondaryIsWebRtc)
                {
                    Log("[MainWindow] InitializeSecondConnectionAsync: Secondary transport=WebRtc, disposing SIP secondary and initializing WebRTC slot");
                    _sipService2?.Dispose();
                    _sipService2 = null;
                    await InitializeWebRtcServiceAsync("secondary");
                    return;
                }

                // SIP-режим: если ранее WebRTC был активен, остановим его UA через DetachEngine.
                try { WebRtcService.Secondary.DetachEngine(resetState: true); } catch { }

                // Проверяем наличие настроек второго подключения
                if (string.IsNullOrEmpty(settings.SipServer2) || 
                    string.IsNullOrEmpty(settings.SipUsername2) || 
                    string.IsNullOrEmpty(settings.SipPasswordEncrypted2))
                {
                    Log("[MainWindow] InitializeSecondConnectionAsync: Second connection settings not found, skipping initialization");
                    // Если настроек нет, освобождаем существующее подключение
                    _sipService2?.Dispose();
                    _sipService2 = null;
                    return;
                }

                Log($"[MainWindow] InitializeSecondConnectionAsync: Found second connection settings - Server2='{settings.SipServer2}', Username2='{settings.SipUsername2}', UseTls2={settings.SipUseTls2}, UseSrtp2={settings.SipUseSrtp2}");

                string connectionFingerprint = $"{settings.SipServer2}|{settings.SipUsername2}|{settings.SipUseTls2}|{settings.SipUseSrtp2}";
                if (_sipService2 != null
                    && string.Equals(connectionFingerprint, _secondaryConnectionFingerprint, StringComparison.Ordinal)
                    && _sipService2.IsRegistered)
                {
                    Log("[MainWindow] InitializeSecondConnectionAsync: Already registered with same settings — skipping re-init");
                    return;
                }
                _secondaryConnectionFingerprint = connectionFingerprint;

                SipEndpointHelper.ParseStoredSipServer(settings.SipServer2, out string server2, out int port2);

                Log($"[MainWindow] InitializeSecondConnectionAsync: Parsed server2='{server2}', port2={port2}");

                // Парсим username - если содержит '@', используем только часть до '@'
                // Это важно, так как некоторые провайдеры требуют username в формате "user@domain"
                // но при формировании SIP URI домен уже будет добавлен автоматически
                string username2 = settings.SipUsername2;
                if (username2.Contains("@"))
                {
                    var usernameParts = username2.Split('@');
                    username2 = usernameParts[0]; // Используем только часть до '@'
                    Log($"[MainWindow] InitializeSecondConnectionAsync: Username2 contains '@', extracted username='{username2}'");
                }

                Log($"[MainWindow] InitializeSecondConnectionAsync: Final username2='{username2}'");
                _sipUsername2 = username2;

                // Расшифровываем пароль
                string password2 = "";
                try
                {
                    password2 = TokenEncryption.Decrypt(settings.SipPasswordEncrypted2);
                    Log($"[MainWindow] InitializeSecondConnectionAsync: Password2 decrypted successfully (length={password2.Length})");
                }
                catch (Exception ex)
                {
                    Log($"[MainWindow] InitializeSecondConnectionAsync: Error decrypting password2: {ex.Message}");
                    return;
                }

                // Освобождаем существующее подключение
                _sipService2?.Dispose();

                // Создаем новое подключение с флагом isSecondaryConnection = true
                Log($"[MainWindow] InitializeSecondConnectionAsync: Creating SipService2 with username='{username2}', server='{server2}', port={port2}");
                // Используем настройки аудио из файла настроек для второго подключения
                // ВАЖНО: Используем те же настройки, что и для основного подключения, чтобы избежать проблем с качеством
                string audioCodec2 = settings.AudioCodec ?? "PCMU";
                // Используем настройки из файла, если они есть, иначе используем значения по умолчанию
                // По умолчанию используем 16000 Hz для лучшего качества (WindowsAudioEndPoint работает с этой частотой)
                int audioSampleRate2 = settings.AudioSampleRate > 0 ? settings.AudioSampleRate : 16000;
                int audioBitrate2 = settings.AudioBitrate > 0 ? settings.AudioBitrate : 64000;
                
                // ВАЖНО: НЕ принуждаем 8000 Hz для PCMU, так как WindowsAudioEndPoint работает с 16000 Hz
                // Кодек PCMU сам преобразует частоту при необходимости
                // Принудительная установка 8000 Hz может вызывать проблемы с качеством звука
                Log($"[MainWindow] InitializeSecondConnectionAsync: Audio settings - Codec={audioCodec2}, SampleRate={audioSampleRate2}, Bitrate={audioBitrate2}");
                
                _sipService2 = new SipService(
                    username2, 
                    password2, 
                    server2, 
                    port2,
                    settings.MicrophoneDeviceNumber, 
                    settings.SpeakerDeviceNumber,
                    audioCodec2,
                    audioSampleRate2,
                    audioBitrate2,
                    isSecondaryConnection: true, // ВАЖНО: помечаем как вторичное подключение
                    useTls: settings.SipUseTls2,
                    useSrtp: settings.SipUseSrtp2,
                    audioDeviceFactory: Softphone.Audio.AudioDeviceFactory.Default);
                _sipService2.EnableAec = settings.EnableEchoCancellation;
                Log($"[MainWindow] InitializeSecondConnectionAsync: SipService2 created successfully");

                // Обработка событий статуса второго подключения
                _sipService2.OnStatusChanged += (status) =>
                {
                    // Use BeginInvoke so SIP stack thread never blocks on the UI queue (avoids freezes when settings re-init runs).
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            Log($"[MainWindow] [Connection2] {status}");
                            OnConnectionStatusChanged?.Invoke($"[Connection2] {status}");
                            UpdateConnectionStatus();
                            UpdateCallButtonMode();
                        }
                        catch (Exception ex)
                        {
                            Log($"[MainWindow] [Connection2] OnStatusChanged UI: {ex.Message}");
                        }
                    }), System.Windows.Threading.DispatcherPriority.Normal);
                };

                // Обработка входящих звонков на второе подключение
                _sipService2.OnIncomingCall += (callerNumber) =>
                {
                    Log($"[MainWindow] [Connection2] OnIncomingCall event received: {callerNumber}");
                    if (Dispatcher.CheckAccess())
                    {
                        HandleIncomingCall(callerNumber, _sipService2);
                    }
                    else
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HandleIncomingCall(callerNumber, _sipService2);
                        }), System.Windows.Threading.DispatcherPriority.Send);
                    }
                };

                // Запускаем второе подключение
                await _sipService2.StartAsync();
                Log("[MainWindow] InitializeSecondConnectionAsync: Second connection started");
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] ERROR in InitializeSecondConnectionAsync: {ex.Message}");
                Log($"[MainWindow] Stack trace: {ex.StackTrace}");
            }
            finally
            {
                RefreshSecondarySipMaintenanceTimerFromSettings();
                _secondConnectionInitSemaphore.Release();
            }
        }

        private void StopSecondarySipMaintenanceTimer()
        {
            _secondarySipMaintenanceTimer?.Dispose();
            _secondarySipMaintenanceTimer = null;
        }

        /// <summary>
        /// If second-line SIP is configured in settings, run periodic full re-init (Beeline RTP/NAT staleness).
        /// </summary>
        private void RefreshSecondarySipMaintenanceTimerFromSettings()
        {
            try
            {
                string path = AppDataHelper.GetSettingsFilePath();
                if (!File.Exists(path))
                {
                    StopSecondarySipMaintenanceTimer();
                    return;
                }

                var settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path));
                if (settings == null ||
                    AppSettings.SecondaryLineUsesWebRtc(settings) ||
                    string.IsNullOrEmpty(settings.SipServer2) ||
                    string.IsNullOrEmpty(settings.SipUsername2) ||
                    string.IsNullOrEmpty(settings.SipPasswordEncrypted2))
                {
                    StopSecondarySipMaintenanceTimer();
                    return;
                }

                StopSecondarySipMaintenanceTimer();
                _secondarySipMaintenanceTimer = new System.Threading.Timer(_ =>
                {
                    _ = Task.Run(async () =>
                    {
                        try { await TryPeriodicSecondarySipReconnectAsync().ConfigureAwait(false); }
                        catch (Exception ex) { Log($"[Connection2] Periodic maintenance error: {ex.Message}"); }
                    });
                }, null, _secondarySipMaintenanceInterval, _secondarySipMaintenanceInterval);
            }
            catch (Exception ex)
            {
                Log($"[Connection2] RefreshSecondarySipMaintenanceTimerFromSettings: {ex.Message}");
                StopSecondarySipMaintenanceTimer();
            }
        }

        private async Task TryPeriodicSecondarySipReconnectAsync()
        {
            var sip2 = _sipService2;
            if (sip2 != null)
            {
                if (sip2.IsInCall || sip2.IsOccupiedForStackRefresh)
                {
                    Log("[Connection2] Periodic reconnect skipped — call or media/ringback in progress on line 2");
                    return;
                }

                var callWin = CallHandlingHelpers.FindExistingCallWindow();
                if (callWin?.SipCallService != null && ReferenceEquals(callWin.SipCallService, sip2))
                {
                    Log("[Connection2] Periodic reconnect skipped — SIP call window open on line 2");
                    return;
                }
            }

            Log("[Connection2] Periodic reconnect (maintenance)");
            await InitializeSecondConnectionAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Проверяет, подключено ли второе подключение (SIP или WebRTC в зависимости от настроек).
        /// </summary>
        public bool IsSecondConnectionConnected()
        {
            if (ShouldUseWebRtcForConnection(secondary: true))
            {
                return WebRtcService.Secondary?.IsReadyForCalls ?? false;
            }
            return _sipService2?.IsRegistered ?? false;
        }

        private static bool IsSecondaryConfiguredInSettings(AppSettings? settings)
        {
            if (settings == null)
                return false;
            if (AppSettings.SecondaryLineUsesWebRtc(settings))
                return true;
            return !string.IsNullOrWhiteSpace(settings.SipServer2)
                   && !string.IsNullOrWhiteSpace(settings.SipUsername2);
        }

        private bool IsMainConnectionReady()
        {
            if (ShouldUseWebRtcForConnection(secondary: false))
                return WebRtcService.Main?.IsReadyForCalls == true;
            return _sipService?.IsRegistered == true;
        }

        /// <summary>
        /// When both main and secondary are configured, wait until both register (or timeout).
        /// Prevents click-to-call from using whichever line connects first.
        /// </summary>
        private async Task WaitForConfiguredConnectionsAsync(CancellationToken cancellationToken = default)
        {
            AppSettings? settings = null;
            try
            {
                string path = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(path))
                    settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] WaitForConfiguredConnections: settings read error: {ex.Message}");
            }

            if (!IsSecondaryConfiguredInSettings(settings))
                return;

            if (_sipService2 == null && !ShouldUseWebRtcForConnection(secondary: true))
            {
                try
                {
                    await InitializeSecondConnectionAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log($"[MainWindow] WaitForConfiguredConnections: secondary init: {ex.Message}");
                }
            }

            int maxSteps = ConnectionReadyWaitSeconds * 2;
            for (int i = 0; i < maxSteps; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool mainReady = IsMainConnectionReady();
                bool secondaryReady = IsSecondConnectionConnected();
                if (mainReady && secondaryReady)
                {
                    Log("[MainWindow] WaitForConfiguredConnections: both connections ready.");
                    return;
                }

                if (i > 0 && i % 10 == 0)
                {
                    Log($"[MainWindow] WaitForConfiguredConnections: main={mainReady}, secondary={secondaryReady} ({i / 2}s/{ConnectionReadyWaitSeconds}s)");
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            Log($"[MainWindow] WaitForConfiguredConnections: timeout — main={IsMainConnectionReady()}, secondary={IsSecondConnectionConnected()}");
        }

        /// <summary>
        /// Отключает и удаляет второе SIP подключение
        /// </summary>
        public void DisconnectSecondConnection()
        {
            try
            {
                StopSecondarySipMaintenanceTimer();
                Log("[MainWindow] DisconnectSecondConnection: Disposing second SIP service...");
                _sipService2?.Dispose();
                _sipService2 = null;
                Log("[MainWindow] DisconnectSecondConnection: Second connection removed");
                
                // Обновляем UI — переключаем на обычную кнопку вызова
                Dispatcher.Invoke(() => UpdateCallButtonMode());
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] DisconnectSecondConnection error: {ex.Message}");
            }
        }

        private void HandleIncomingCall(string callerNumber, SipService? sipService = null)
        {
            try
            {
                Log($"HandleIncomingCall called with callerNumber: {callerNumber}");

                // Используем переданный сервис или основной по умолчанию
                SipService? serviceToUse = sipService ?? _sipService;
                
                // IMPORTANT: When WebRTC mode is enabled, SIP incoming calls must be ignored/disabled.
                // ВАЖНО: Второе подключение всегда работает через SIP, не проверяет WebRTC
                if (serviceToUse != _sipService2 && ShouldUseWebRtc())
                {
                    Log($"[MainWindow] HandleIncomingCall: SIP incoming call from {callerNumber} ignored: WebRTC mode enabled");
                    return;
                }
                
            if (serviceToUse == null)
                {
                    Log("HandleIncomingCall: sipService is null, cannot handle incoming call");
                return;
                }

            // Гейт: если есть активный WebRTC звонок, игнорируем SIP входящий
            // ВАЖНО: Для второго подключения не блокируем WebRTC, так как оно независимо
            if (serviceToUse == _sipService && CallHandlingHelpers.IsWebRtcCallActive())
            {
                Log($"[MainWindow] HandleIncomingCall: SIP incoming call from {callerNumber} ignored: WebRTC call active (main connection only)");
                return;
            }

            // Проверяем, нет ли уже открытого окна звонка
            Log($"[MainWindow] HandleIncomingCall: Checking for existing CallWindow...");
            var existingCallWindow = CallHandlingHelpers.FindExistingCallWindow();
            if (existingCallWindow != null)
            {
                // Проверяем, действительно ли окно активно (не закрывается)
                bool isWindowActive = existingCallWindow.IsLoaded && existingCallWindow.IsVisible && !existingCallWindow.IsClosing();
                string connectionName = (serviceToUse == _sipService2) ? "Secondary" : "Main";
                if (isWindowActive)
                {
                    Log($"[MainWindow] HandleIncomingCall: Active CallWindow already exists (Connection: {connectionName}), ignoring duplicate call from {callerNumber}");
                    return;
                }
                else
                {
                    Log($"[MainWindow] HandleIncomingCall: Found CallWindow but it's closing/inactive, proceeding with new call from {callerNumber}");
                }
            }
            else
            {
                string connectionName = (serviceToUse == _sipService2) ? "Secondary" : "Main";
                Log($"[MainWindow] HandleIncomingCall: No existing CallWindow found, proceeding with new call from {callerNumber} (Connection: {connectionName})");
            }

            // Логируем хеш-код для диагностики
            int serviceHash = serviceToUse.GetHashCode();
            Log($"[MainWindow] HandleIncomingCall: Creating CallWindow with SipService hash: {serviceHash}, CallerNumber: {callerNumber}");
            
            // Сохраняем время начала входящего звонка для истории
            DateTime incomingCallStartTime = DateTime.Now;
            
            // Открываем окно для входящего звонка с флагом isIncomingCall = true
            Log($"[MainWindow] HandleIncomingCall: Creating CallWindow instance...");
            CallWindow callWindow;
            try
            {
                callWindow = new CallWindow(serviceToUse, callerNumber, isIncomingCall: true)
                {
                    Owner = this
                };
                Log($"[MainWindow] HandleIncomingCall: CallWindow instance created successfully");
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] HandleIncomingCall: ERROR creating CallWindow: {ex.Message}");
                Log($"[MainWindow] HandleIncomingCall: Stack trace: {ex.StackTrace}");
                return;
            }
            
            // Подписываемся на события обновления статуса входящего звонка
            callWindow.OnIncomingCallStatusChanged += (phoneNumber, callTime, status, duration) =>
            {
                _callHistoryService.UpdateCallStatus(phoneNumber, callTime, status, duration);
                LoadCallHistory();
            };
            
                // Подписываемся на события обновления детальной информации о звонке
                callWindow.OnCallDetailsChanged += (phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId, inboundRtpPackets) =>
                {
                    string? outboundCallerId = callWindow.GetOutboundCallerId();
                    _callHistoryService.UpdateCallDetails(phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId, outboundCallerId, inboundRtpPackets: inboundRtpPackets);

                    if (string.IsNullOrEmpty(outboundCallerId) && endedBy != CallEndedBy.Unknown && wasAnswered)
                        TryFetchOutboundCallerIdFromCdr(phoneNumber, callTime);
                    
                    string? sessionId = transport == CallTransport.WebRtc ? webRtcSessionId : sipCallId;
                    ProcessCallInAmoCrm(phoneNumber, callTime, technicalDetails, recordingFilePath, sessionId);
                };
                
                var showStartTime = DateTime.Now;
                Log($"HandleIncomingCall: About to show CallWindow at {showStartTime:HH:mm:ss.fff}");
                
                // Настраиваем окно для немедленного отображения
                CallHandlingHelpers.PrepareCallWindowForDisplay(callWindow, isIncomingCall: true);
                
                // Показываем окно
            callWindow.Show();
                
                var showEndTime = DateTime.Now;
                var showDuration = (showEndTime - showStartTime).TotalMilliseconds;
                Log($"HandleIncomingCall: CallWindow.Show() completed in {showDuration}ms at {showEndTime:HH:mm:ss.fff}");
                
                // Принудительно активируем и поднимаем окно
                callWindow.Activate();
                callWindow.BringIntoView();
                callWindow.Focus();
                
                // Проверяем видимость окна
                Log($"HandleIncomingCall: Window Visibility={callWindow.Visibility}, IsVisible={callWindow.IsVisible}, IsActive={callWindow.IsActive}, Topmost={callWindow.Topmost}");
                
                // Возвращаем нормальное поведение Topmost через небольшую задержку
                _ = System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        callWindow.Topmost = false;
                        Log($"HandleIncomingCall: Topmost reset to false");
                    });
                });
            
            // Добавляем запись в историю звонков (входящий SIP звонок)
            var incomingCallItem = new CallHistoryItem
            {
                PhoneNumber = callerNumber,
                CallTime = incomingCallStartTime,
                Status = CallStatus.Calling,
                IsIncoming = true,
                Transport = CallTransport.Sip,
                ConnectionSlot = serviceToUse == _sipService2 ? CallConnectionSlot.Secondary : CallConnectionSlot.Main
            };
            _callHistoryService.AddCall(incomingCallItem);
            LoadCallHistory();
            }
            catch (Exception ex)
            {
                Log($"HandleIncomingCall ERROR: {ex.Message}");
                Log($"HandleIncomingCall StackTrace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Log($"HandleIncomingCall InnerException: {ex.InnerException.Message}");
                }
            }
        }

        private async void CallButton_Click(object sender, RoutedEventArgs e)
        {
            _allowNextProgrammaticCall = true;
            // КРИТИЧНО: Проверяем наличие активного вызова перед созданием нового
            var existingCallWindow = CallHandlingHelpers.FindExistingCallWindow();
            if (existingCallWindow != null)
            {
                MainWindow.Log($"[MainWindow] CallButton_Click: Active call window already exists, ignoring call");
                CustomMessageBox.Show(
                    "Another call is already in progress. Please end the current call before making a new one.",
                    "Call In Progress",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
                // Активируем существующее окно звонка
                existingCallWindow.Activate();
                existingCallWindow.BringIntoView();
                return;
            }

            await PerformCall();
        }

        private async void SplitCallPrimary_Click(object sender, RoutedEventArgs e)
        {
            _allowNextProgrammaticCall = true;
            var existingCallWindow = CallHandlingHelpers.FindExistingCallWindow();
            if (existingCallWindow != null)
            {
                MainWindow.Log($"[MainWindow] SplitCallPrimary_Click: Active call window already exists, ignoring call");
                existingCallWindow.Activate();
                return;
            }

            string number = PhoneNumberTextBox.Text.Trim();
            if (string.IsNullOrEmpty(number) || number == "Enter the number")
            {
                CustomMessageBox.Show("Please enter a phone number.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                return;
            }

            Log($"[MainWindow] SplitCallPrimary_Click: Calling {number} via primary connection");
            await PerformCallWithConnection(number, null, ConnectionSelectionWindow.ConnectionType.Main);
        }

        private async void SplitCallSecondary_Click(object sender, RoutedEventArgs e)
        {
            _allowNextProgrammaticCall = true;
            var existingCallWindow = CallHandlingHelpers.FindExistingCallWindow();
            if (existingCallWindow != null)
            {
                MainWindow.Log($"[MainWindow] SplitCallSecondary_Click: Active call window already exists, ignoring call");
                existingCallWindow.Activate();
                return;
            }

            string number = PhoneNumberTextBox.Text.Trim();
            if (string.IsNullOrEmpty(number) || number == "Enter the number")
            {
                CustomMessageBox.Show("Please enter a phone number.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                return;
            }

            Log($"[MainWindow] SplitCallSecondary_Click: Calling {number} via secondary connection");
            await PerformCallWithConnection(number, null, ConnectionSelectionWindow.ConnectionType.Secondary);
        }

        /// <summary>
        /// Устанавливает доступность всех кнопок вызова (обычной и сплит)
        /// </summary>
        private void SetCallButtonsEnabled(bool enabled)
        {
            try
            {
                if (CallButton != null) CallButton.IsEnabled = enabled;
                if (SplitCallPrimaryButton != null) SplitCallPrimaryButton.IsEnabled = enabled;
                if (SplitCallSecondaryButton != null) SplitCallSecondaryButton.IsEnabled = enabled;
            }
            catch { }
        }

        /// <summary>
        /// Находит элемент в визуальном дереве по имени
        /// </summary>
        private T? FindChildByName<T>(DependencyObject parent, string name) where T : DependencyObject
        {
            if (parent == null) return null;
            
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                
                if (child is T t && (child as FrameworkElement)?.Name == name)
                {
                    return t;
                }
                
                var result = FindChildByName<T>(child, name);
                if (result != null) return result;
            }
            
            return null;
        }
        
        private void TryLoadConnectionDisplayNames(out string mainDisplayName, out string secondaryDisplayName)
        {
            mainDisplayName = "PBX";
            secondaryDisplayName = "SIP";
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (!File.Exists(settingsFilePath))
                    return;

                string json = File.ReadAllText(settingsFilePath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                if (settings == null)
                    return;

                if (!string.IsNullOrWhiteSpace(settings.MainConnectionName))
                    mainDisplayName = settings.MainConnectionName.Trim();
                if (!string.IsNullOrWhiteSpace(settings.SecondaryConnectionName))
                    secondaryDisplayName = settings.SecondaryConnectionName.Trim();
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] TryLoadConnectionDisplayNames: {ex.Message}");
            }
        }

        private void ApplyConnectionStatusLabelText(string mainDisplayName, string secondaryDisplayName)
        {
            if (MainConnectionStatusLabelTextBlock != null)
                MainConnectionStatusLabelTextBlock.Text = $"{mainDisplayName}:";
            if (SecondaryConnectionStatusLabelTextBlock != null)
                SecondaryConnectionStatusLabelTextBlock.Text = $"{secondaryDisplayName}:";
        }

        /// <summary>
        /// Обновляет подписи строк статуса (имена подключений) при изменении файла настроек.
        /// </summary>
        private void EnsureConnectionStatusLabelsCurrent()
        {
            try
            {
                string path = AppDataHelper.GetSettingsFilePath();
                long ticks = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : -1L;
                if (ticks == _connectionDisplayNamesSettingsTicks)
                    return;

                _connectionDisplayNamesSettingsTicks = ticks;
                TryLoadConnectionDisplayNames(out string mainName, out string secondaryName);
                ApplyConnectionStatusLabelText(mainName, secondaryName);
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] EnsureConnectionStatusLabelsCurrent: {ex.Message}");
            }
        }

        /// <summary>
        /// Обновляет названия подключений в сплит-кнопке вызова
        /// </summary>
        private void UpdateSplitCallButtonLabels()
        {
            try
            {
                if (SplitCallPrimaryButton == null || SplitCallSecondaryButton == null) return;

                string path = AppDataHelper.GetSettingsFilePath();
                _connectionDisplayNamesSettingsTicks = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : -1L;

                TryLoadConnectionDisplayNames(out string mainName, out string secondaryName);

                // Обновляем через Dispatcher, чтобы кнопки были отрендерены
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var primaryLabel = FindChildByName<TextBlock>(SplitCallPrimaryButton, "SplitCallPrimaryLabel");
                        if (primaryLabel != null)
                        {
                            primaryLabel.Text = mainName;
                        }

                        var secondaryLabel = FindChildByName<TextBlock>(SplitCallSecondaryButton, "SplitCallSecondaryLabel");
                        if (secondaryLabel != null)
                        {
                            secondaryLabel.Text = secondaryName;
                        }

                        ApplyConnectionStatusLabelText(mainName, secondaryName);
                    }
                    catch (Exception ex)
                    {
                        Log($"[MainWindow] UpdateSplitCallButtonLabels (dispatcher) error: {ex.Message}");
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] UpdateSplitCallButtonLabels error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Обновляет видимость кнопок вызова: одна кнопка или сплит-кнопка
        /// Вызывается при изменении статуса подключений
        /// </summary>
        public void UpdateCallButtonMode()
        {
            try
            {
                if (SplitCallButtonPanel == null || CallButton == null) return;

                bool mainWebRtc = ShouldUseWebRtcForConnection(false);
                bool secondaryWebRtc = ShouldUseWebRtcForConnection(true);
                bool hasMainConnection = mainWebRtc
                    ? (WebRtcService.Main?.IsReadyForCalls ?? false)
                    : (_sipService?.IsRegistered ?? false);
                bool hasSecondaryConnection = secondaryWebRtc
                    ? (WebRtcService.Secondary?.IsReadyForCalls ?? false)
                    : (_sipService2?.IsRegistered ?? false);

                if (hasMainConnection && hasSecondaryConnection)
                {
                    // Оба подключения активны — показываем сплит-кнопку
                    CallButton.Visibility = Visibility.Collapsed;
                    SplitCallButtonPanel.Visibility = Visibility.Visible;
                    SplitCallPrimaryButton.IsEnabled = true;
                    SplitCallSecondaryButton.IsEnabled = true;
                    
                    // Обновляем названия подключений в сплит-кнопке
                    UpdateSplitCallButtonLabels();
                }
                else
                {
                    // Одно или ни одного подключения — показываем обычную кнопку
                    SplitCallButtonPanel.Visibility = Visibility.Collapsed;
                    CallButton.Visibility = Visibility.Visible;
                    CallButton.IsEnabled = hasMainConnection || hasSecondaryConnection;
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] UpdateCallButtonMode error: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task PerformCall()
        {
            string number = PhoneNumberTextBox.Text.Trim();
            if (string.IsNullOrEmpty(number) || number == "Enter the number")
            {
                CustomMessageBox.Show("Please enter a phone number.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                return;
            }
            await PerformCall(number);
        }

        /// <summary>
        /// Обрабатывает сообщение протокола от другого экземпляра приложения
        /// </summary>
        public void HandleProtocolMessage(string protocolUrl)
        {
            try
            {
                Log($"[MainWindow] HandleProtocolMessage called: {LogSanitizer.RedactUrlWithToken(protocolUrl)}");
                
                // Парсим URL протокола
                if (!Uri.TryCreate(protocolUrl, UriKind.Absolute, out Uri? uri))
                {
                    Log($"[MainWindow] Invalid protocol URL format: {LogSanitizer.RedactUrlWithToken(protocolUrl)}");
                    return;
                }
                
                if (uri.Scheme != "callspire")
                {
                    Log($"[MainWindow] Invalid protocol scheme: {uri.Scheme}");
                    return;
                }

                if (uri.Host == "cdr-auth")
                {
                    var cdrParams = ParseQueryString(uri.Query);
                    string? token = cdrParams.ContainsKey("token") ? cdrParams["token"] : null;
                    if (!string.IsNullOrEmpty(token))
                    {
                        HandleCdrAuthToken(token);
                    }
                    return;
                }

                if (uri.Host == "provision")
                {
                    var provParams = ParseQueryString(uri.Query);
                    string? tokenId = provParams.ContainsKey("token") ? provParams["token"] : null;
                    string? proxy = provParams.ContainsKey("proxy") ? provParams["proxy"] : null;
                    if (!string.IsNullOrEmpty(tokenId) && !string.IsNullOrEmpty(proxy))
                    {
                        _ = HandleProvisionTokenAsync(tokenId, proxy);
                    }
                    else
                    {
                        Log("[MainWindow] provision URL missing token or proxy");
                    }
                    return;
                }

                if (uri.Host != "call")
                {
                    Log($"[MainWindow] Unknown protocol host: {uri.Host}");
                    return;
                }
                
                var queryParams = ParseQueryString(uri.Query);
                string? phoneNumber = queryParams.ContainsKey("phone") ? queryParams["phone"] : null;
                string? leadIdStr = queryParams.ContainsKey("leadId") ? queryParams["leadId"] : null;
                
                if (string.IsNullOrEmpty(phoneNumber))
                {
                    Log("[MainWindow] Phone number is missing in protocol URL");
                    return;
                }
                
                long? leadId = null;
                if (!string.IsNullOrEmpty(leadIdStr) && long.TryParse(leadIdStr, out long parsedLeadId))
                {
                    leadId = parsedLeadId;
                }
                
                Log($"[MainWindow] Parsed protocol: phone={phoneNumber}, leadId={leadId}");

                // Dedupe identical protocol call requests arriving close together.
                try
                {
                    // Normalize similarly to dial path to avoid "+7..." vs "7..." duplicates.
                    var normalized = NormalizePhoneForDial(phoneNumber);
                    var key = $"{normalized}|{(leadId.HasValue ? leadId.Value.ToString() : "")}";
                    var nowUtc = DateTime.UtcNow;
                    lock (_protocolDedupeLock)
                    {
                        if (_lastProtocolCallKey == key && (nowUtc - _lastProtocolCallUtc) <= _protocolCallDedupeWindow)
                        {
                            Log($"[MainWindow] Protocol call deduped: key='{key}', window={_protocolCallDedupeWindow.TotalSeconds:0}s");
                            return;
                        }
                        _lastProtocolCallKey = key;
                        _lastProtocolCallUtc = nowUtc;
                    }
                }
                catch { }
                
                // Инициируем звонок
                InitiateCallFromBrowser(phoneNumber, leadId);
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error handling protocol message: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Парсит query string в словарь ключ-значение
        /// </summary>
        private Dictionary<string, string> ParseQueryString(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query))
                return result;
            
            // Убираем ведущий '?' если есть
            if (query.StartsWith("?"))
                query = query.Substring(1);
            
            string[] pairs = query.Split('&');
            foreach (string pair in pairs)
            {
                if (string.IsNullOrEmpty(pair))
                    continue;
                
                int equalIndex = pair.IndexOf('=');
                if (equalIndex > 0)
                {
                    string key = Uri.UnescapeDataString(pair.Substring(0, equalIndex));
                    string value = Uri.UnescapeDataString(pair.Substring(equalIndex + 1));
                    result[key] = value;
                }
                else
                {
                    result[Uri.UnescapeDataString(pair)] = string.Empty;
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Инициирует звонок из браузера AmoCRM с указанным номером и leadId
        /// </summary>
        public async void InitiateCallFromBrowser(string phoneNumber, long? leadId = null)
        {
            Log($"[MainWindow] InitiateCallFromBrowser called: phone={phoneNumber}, leadId={leadId}");

            var activeCallWindow = CallHandlingHelpers.FindExistingCallWindow();
            if (activeCallWindow != null && !activeCallWindow.IsClosing())
            {
                Log($"[MainWindow] InitiateCallFromBrowser skipped: call already active ({activeCallWindow.CallRemoteNumber})");
                WindowForegroundHelper.RequestUserAttention(this);
                WindowForegroundHelper.ShowAboveAll(activeCallWindow, this);
                return;
            }

            WindowForegroundHelper.BeginElevatedForegroundSession(this);
            try
            {
                await InitiateCallFromBrowserCoreAsync(phoneNumber, leadId);
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] InitiateCallFromBrowser failed: {ex.Message}");
            }
            finally
            {
                WindowForegroundHelper.ForceReleaseElevatedForeground(this);
            }
        }

        private async System.Threading.Tasks.Task InitiateCallFromBrowserCoreAsync(string phoneNumber, long? leadId)
        {
            // BeginElevatedForegroundSession already raised MainWindow; avoid RequestUserAttention
            // here — its delayed Topmost reset would re-pin MainWindow on top after the session ends.

            // Cancel any previously queued "call when ready" request.
            int mySeq;
            CancellationToken myToken;
            lock (_pendingBrowserCallLock)
            {
                try { _pendingBrowserCallCts?.Cancel(); } catch { }
                try { _pendingBrowserCallCts?.Dispose(); } catch { }
                _pendingBrowserCallCts = new CancellationTokenSource();
                _pendingBrowserCallSeq++;
                mySeq = _pendingBrowserCallSeq;
                myToken = _pendingBrowserCallCts.Token;
                _pendingBrowserCallCreatedUtc = DateTime.UtcNow;
            }
            
            // Если используется WebRTC, ждем его готовности перед инициацией звонка
            if (ShouldUseWebRtc())
            {
                Log("[MainWindow] WebRTC mode detected, waiting for WebRTC to be ready...");
                
                // Ждем готовности WebRTC до 30 секунд
                int maxWaitSeconds = 30;
                int waitedSeconds = 0;
                
                while (!WebRtcService.Main.IsReadyForCalls && waitedSeconds < maxWaitSeconds)
                {
                    await Task.Delay(500); // Проверяем каждые 500мс
                    waitedSeconds += 1;
                    
                    if (waitedSeconds % 5 == 0)
                    {
                        Log($"[MainWindow] Still waiting for WebRTC... ({waitedSeconds}/{maxWaitSeconds}s)");
                    }
                }
                
                if (!WebRtcService.Main.IsReadyForCalls)
                {
                    Log("[MainWindow] WebRTC not ready after waiting, showing error");
                    CustomMessageBox.Show(
                        "WebRTC service is not ready yet. Please wait for initialization to complete.\n\n" +
                        "The call will be initiated automatically once WebRTC is ready.",
                        "WebRTC Initializing",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information,
                        this);
                    
                    // Продолжаем ждать в фоне и инициируем звонок когда будет готово
                    _ = Task.Run(async () =>
                    {
                        while (!WebRtcService.Main.IsReadyForCalls)
                        {
                            if (myToken.IsCancellationRequested)
                            {
                                Log($"[MainWindow] Pending browser call cancelled (seq={mySeq}).");
                                return;
                            }

                            // Drop very old pending requests to avoid surprise re-dials later.
                            DateTime createdUtc;
                            lock (_pendingBrowserCallLock) { createdUtc = _pendingBrowserCallCreatedUtc; }
                            if (createdUtc != DateTime.MinValue &&
                                (DateTime.UtcNow - createdUtc).TotalSeconds > PendingBrowserCallTtlSeconds)
                            {
                                Log($"[MainWindow] Pending browser call expired (seq={mySeq}, ttl={PendingBrowserCallTtlSeconds}s).");
                                return;
                            }

                            await Task.Delay(1000);
                        }
                        
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            if (myToken.IsCancellationRequested)
                            {
                                Log($"[MainWindow] Pending browser call cancelled before invoke (seq={mySeq}).");
                                return;
                            }

                            // If a call is already active, do not auto-trigger a new one.
                            if (CallHandlingHelpers.FindExistingCallWindow() != null)
                            {
                                Log($"[MainWindow] Pending browser call skipped: call already active (seq={mySeq}).");
                                return;
                            }

                            // Only run the most recent pending request.
                            lock (_pendingBrowserCallLock)
                            {
                                if (mySeq != _pendingBrowserCallSeq)
                                {
                                    Log($"[MainWindow] Pending browser call superseded (seq={mySeq}, latest={_pendingBrowserCallSeq}).");
                                    return;
                                }
                            }

                            Log("[MainWindow] WebRTC is now ready, initiating call from browser");
                            WindowForegroundHelper.BeginElevatedForegroundSession(this);
                            try
                            {
                                await WaitForConfiguredConnectionsAsync(myToken).ConfigureAwait(true);
                                _allowNextProgrammaticCall = true;
                                await PerformCall(phoneNumber, leadId);
                            }
                            finally
                            {
                                WindowForegroundHelper.ForceReleaseElevatedForeground(this);
                            }
                        });
                    });
                    return;
                }
                
                Log("[MainWindow] WebRTC is ready, proceeding with call");
            }

            await WaitForConfiguredConnectionsAsync(myToken).ConfigureAwait(false);

            _allowNextProgrammaticCall = true;
            if (Dispatcher.CheckAccess())
                await PerformCall(phoneNumber, leadId).ConfigureAwait(true);
            else
                await Dispatcher.InvokeAsync(() => PerformCall(phoneNumber, leadId)).Task.Unwrap().ConfigureAwait(false);
        }
        
        private async System.Threading.Tasks.Task PerformCall(string number, long? leadId = null, bool forceCallerIdSelection = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(async () =>
                    await PerformCall(number, leadId, forceCallerIdSelection)).Task.Unwrap();
                return;
            }

            if (string.IsNullOrEmpty(number))
            {
                CustomMessageBox.Show("Please enter a phone number.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                return;
            }

            // Starting a call cancels any pending delayed browser auto-call.
            lock (_pendingBrowserCallLock)
            {
                try { _pendingBrowserCallCts?.Cancel(); } catch { }
            }

            // Guard: avoid unexpected re-dials not tied to user input.
            // If something calls PerformCall() from a background path, require a recent input event.
            try
            {
                bool allowProgrammatic = _allowNextProgrammaticCall;
                _allowNextProgrammaticCall = false; // consume
                var lastTicks = System.Threading.Interlocked.Read(ref _lastUserInputUtcTicks);
                var sinceInput = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);
                if (!allowProgrammatic && sinceInput > _callRequiresRecentUserInputWindow)
                {
                    Log($"[MainWindow] PerformCall blocked: no recent user input (sinceInput={sinceInput.TotalSeconds:0.0}s, window={_callRequiresRecentUserInputWindow.TotalSeconds:0.0}s, number={number}).");
                    return;
                }
            }
            catch { }

            // КРИТИЧНО: Проверяем наличие активного вызова перед созданием нового
            var existingCallWindow = CallHandlingHelpers.FindExistingCallWindow();
            if (existingCallWindow != null)
            {
                MainWindow.Log($"[MainWindow] PerformCall: Active call window already exists, ignoring call to {number}");
                CustomMessageBox.Show(
                    "Another call is already in progress. Please end the current call before making a new one.",
                    "Call In Progress",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
                // Активируем существующее окно звонка
                existingCallWindow.Activate();
                existingCallWindow.BringIntoView();
                return;
            }

            // Определяем доступные подключения и показываем окно выбора, если нужно
            bool useWebRtc = ShouldUseWebRtcForConnection(secondary: false);
            bool useSecondaryWebRtc = ShouldUseWebRtcForConnection(secondary: true);

            // По факту наличия/готовности сервисов определяем активность подключений.
            // Важно: не полагаться только на “configured” из файла настроек, т.к. во время click-to-call
            // сервис может уже быть инициализирован и зарегистрирован даже при расхождении настроек на диске.
            bool hasMainConnectionConfigured = useWebRtc
                ? (WebRtcService.Main != null)
                : (_sipService != null);

            bool hasSecondaryConnectionConfigured = useSecondaryWebRtc
                ? (WebRtcService.Secondary != null)
                : (_sipService2 != null);

            Log($"[MainWindow] PerformCall: Connection check - Main configured={hasMainConnectionConfigured} (webrtc={useWebRtc}), Secondary configured={hasSecondaryConnectionConfigured} (webrtc={useSecondaryWebRtc})");

            if (hasMainConnectionConfigured && hasSecondaryConnectionConfigured)
            {
                await WaitForConfiguredConnectionsAsync().ConfigureAwait(true);
            }

            string? mainStatus = null;
            string? secondaryStatus = null;

            if (hasMainConnectionConfigured)
            {
                if (useWebRtc)
                {
                    mainStatus = WebRtcService.Main?.IsReadyForCalls == true ? "Connected with WebRTC" : "Not connected";
                }
                else
                {
                    mainStatus = _sipService?.IsRegistered == true ? "Connected" : "Not connected";
                }
            }

            if (hasSecondaryConnectionConfigured)
            {
                if (useSecondaryWebRtc)
                {
                    secondaryStatus = WebRtcService.Secondary?.IsReadyForCalls == true ? "Connected with WebRTC" : "Not connected";
                }
                else
                {
                    secondaryStatus = _sipService2?.IsRegistered == true ? "Connected" : "Not connected";
                }
            }

            // Проверяем активность подключений
            bool isMainConnectionActive = hasMainConnectionConfigured
                ? (useWebRtc
                    ? WebRtcService.Main?.IsReadyForCalls == true
                    : _sipService?.IsRegistered == true)
                : false;

            bool isSecondaryConnectionActive = hasSecondaryConnectionConfigured
                ? (useSecondaryWebRtc
                    ? WebRtcService.Secondary?.IsReadyForCalls == true
                    : _sipService2?.IsRegistered == true)
                : false;
            
            // Показываем окно выбора подключения, только если оба подключения активны
            ConnectionSelectionWindow.ConnectionType? selectedConnectionType = null;
            string? selectedMainCallerId = null;
            
            // Caller ID выбор имеет смысл только для Main подключения в WebRTC режиме
            // (там используется PBX Originate).
            bool canSelectCallerId = useWebRtc && _callerIdItems.Count > 0 && _mikoPbxCdrService != null;
            List<CallerIdItem>? mainCallerIdItemsForDialog = canSelectCallerId ? _callerIdItems : null;
            string? currentSelectedCallerId = null;
            try
            {
                if (CallerIdComboBox?.SelectedItem is CallerIdItem ci && !string.IsNullOrWhiteSpace(ci.Number))
                {
                    currentSelectedCallerId = ci.Number;
                }
            }
            catch { }
            if (canSelectCallerId && mainCallerIdItemsForDialog != null && !string.IsNullOrWhiteSpace(currentSelectedCallerId) &&
                !mainCallerIdItemsForDialog.Any(i => i.Number == currentSelectedCallerId))
            {
                currentSelectedCallerId = null;
            }
            
            if (isMainConnectionActive && isSecondaryConnectionActive)
            {
                // Загружаем названия подключений из настроек
                string? mainConnectionName = null;
                string? secondaryConnectionName = null;
                try
                {
                    string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsFilePath))
                    {
                        string json = File.ReadAllText(settingsFilePath);
                        var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                        if (settings != null)
                        {
                            mainConnectionName = settings.MainConnectionName;
                            secondaryConnectionName = settings.SecondaryConnectionName;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[MainWindow] PerformCall: Error loading connection names: {ex.Message}");
                }
                
                // Оба подключения активны - показываем окно выбора
                Log($"[MainWindow] PerformCall: Both connections active, showing selection window");
                var selectionWindow = new ConnectionSelectionWindow(
                    hasMainConnection: hasMainConnectionConfigured,
                    isMainWebRtc: useWebRtc,
                    mainConnectionStatus: mainStatus,
                    hasSecondaryConnection: hasSecondaryConnectionConfigured,
                    secondaryConnectionStatus: secondaryStatus,
                    mainConnectionName: mainConnectionName,
                    secondaryConnectionName: secondaryConnectionName,
                    mainCallerIdItems: mainCallerIdItemsForDialog,
                    selectedMainCallerId: currentSelectedCallerId,
                    isSecondaryWebRtc: useSecondaryWebRtc);
                CenterNonOwnedWindowOverThis(selectionWindow);

                if (WindowForegroundHelper.ShowDialogAboveAll(selectionWindow, this) == true && selectionWindow.SelectedConnection.HasValue)
                {
                    selectedConnectionType = selectionWindow.SelectedConnection.Value;
                    selectedMainCallerId = selectionWindow.SelectedCallerId;
                    Log($"[MainWindow] PerformCall: User selected connection: {selectedConnectionType}");
                }
                else
                {
                    // Пользователь отменил выбор
                    Log($"[MainWindow] PerformCall: User cancelled connection selection");
                    return;
                }
            }
            else if (isMainConnectionActive)
            {
                // Только основное подключение активно.
                // Если Caller ID не выбран (частый сценарий при звонке из истории), просим выбрать его явно.
                bool shouldPromptCallerId =
                    canSelectCallerId &&
                    (forceCallerIdSelection || (_callerIdItems.Count > 1 && string.IsNullOrWhiteSpace(currentSelectedCallerId)));

                if (shouldPromptCallerId)
                {
                    Log("[MainWindow] PerformCall: Main connection only, prompting Caller ID selection");
                    var callerIdSelectionWindow = new ConnectionSelectionWindow(
                        hasMainConnection: hasMainConnectionConfigured,
                        isMainWebRtc: useWebRtc,
                        mainConnectionStatus: mainStatus,
                        hasSecondaryConnection: false,
                        secondaryConnectionStatus: null,
                        mainConnectionName: "Main connection",
                        secondaryConnectionName: null,
                        mainCallerIdItems: mainCallerIdItemsForDialog,
                        selectedMainCallerId: currentSelectedCallerId,
                        forceShowForSingleConnection: true);
                    CenterNonOwnedWindowOverThis(callerIdSelectionWindow);

                    if (WindowForegroundHelper.ShowDialogAboveAll(callerIdSelectionWindow, this) == true && callerIdSelectionWindow.SelectedConnection.HasValue)
                    {
                        selectedConnectionType = callerIdSelectionWindow.SelectedConnection.Value;
                        selectedMainCallerId = callerIdSelectionWindow.SelectedCallerId;
                    }
                    else
                    {
                        Log("[MainWindow] PerformCall: User cancelled Caller ID selection");
                        return;
                    }
                }
                else
                {
                    selectedConnectionType = ConnectionSelectionWindow.ConnectionType.Main;
                    Log($"[MainWindow] PerformCall: Using main connection automatically (only main active)");
                    selectedMainCallerId = currentSelectedCallerId;
                }
            }
            else if (isSecondaryConnectionActive)
            {
                // Только второе подключение активно - используем его автоматически
                selectedConnectionType = ConnectionSelectionWindow.ConnectionType.Secondary;
                Log($"[MainWindow] PerformCall: Using secondary connection automatically (only secondary active)");
                selectedMainCallerId = null;
            }
            else
            {
                // Нет активных подключений
                Log($"[MainWindow] PerformCall: No active connections");
                CustomMessageBox.Show(
                    "No active connections are available. Please check your connection settings and ensure at least one connection is connected.",
                    "No Active Connection",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
                return;
            }
            
            // Используем выбранное подключение для звонка
            await PerformCallWithConnection(number, leadId, selectedConnectionType.Value, selectedMainCallerId);
        }
        
        /// <summary>
        /// Wires OnCallDetailsChanged and Closed for outbound CallWindow.
        /// Must run before slow PBX Originate HTTP — window can close before await returns.
        /// </summary>
        private void TryAttachOutgoingCallCoreHandlers(
            CallWindow callWindow,
            string number,
            DateTime callStartTime,
            CallHistoryItem? currentCallHistoryItem,
            SipService? sipServiceToUse,
            WebRtcService? webRtcSlotService,
            ref bool handlersAttached)
        {
            if (handlersAttached)
                return;
            handlersAttached = true;

            callWindow.OnCallDetailsChanged += (phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId, inboundRtpPackets) =>
            {
                string? outboundCallerId = callWindow.GetOutboundCallerId();
                _callHistoryService.UpdateCallDetails(phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId, outboundCallerId, inboundRtpPackets: inboundRtpPackets);

                Dispatcher.Invoke(RefreshVisibleDashboards);

                if (string.IsNullOrEmpty(outboundCallerId) && endedBy != CallEndedBy.Unknown && wasAnswered)
                    TryFetchOutboundCallerIdFromCdr(phoneNumber, callTime);

                string? sessionId = transport == CallTransport.WebRtc ? webRtcSessionId : sipCallId;
                long? callLeadId = callWindow.GetAmoCrmLeadId();
                Log($"[MainWindow] OnCallDetailsChanged: Retrieved leadId from CallWindow: {callLeadId?.ToString() ?? "null"}");
                ProcessCallInAmoCrm(phoneNumber, callTime, technicalDetails, recordingFilePath, sessionId, callLeadId);
            };

            callWindow.Closed += (s, e) =>
            {
                ClearPendingOriginateState(resetAcceptedSession: true);
                try { webRtcSlotService?.ResumeAutoReconnect("outbound_call_ended"); } catch { }

                if (sipServiceToUse != null && sipServiceToUse.IsInCall)
                    sipServiceToUse.Hangup();

                if (currentCallHistoryItem != null &&
                    (currentCallHistoryItem.Status == CallStatus.Calling ||
                     currentCallHistoryItem.Status == CallStatus.Connected))
                {
                    if (currentCallHistoryItem.WasAnswered)
                    {
                        TimeSpan? duration = currentCallHistoryItem.Duration;
                        currentCallHistoryItem.Status = CallStatus.Ended;
                        _callHistoryService.UpdateCallStatus(number, callStartTime, CallStatus.Ended, duration);
                    }
                    else
                    {
                        currentCallHistoryItem.Status = CallStatus.Cancelled;
                        _callHistoryService.UpdateCallStatus(number, callStartTime, CallStatus.Cancelled);
                    }

                    LoadCallHistory();
                }

                ClearDialerPhoneNumber();
                SetCallButtonsEnabled(true);
            };
        }

        private async System.Threading.Tasks.Task PerformCallWithConnection(string number, long? leadId, ConnectionSelectionWindow.ConnectionType connectionType, string? selectedOutboundCallerIdForOriginate = null)
        {
            number = NormalizePhoneForDial(number);

            // Определяем, какое подключение использовать
            SipService? sipServiceToUse = null;
            
            // Транспорт определяется per-connection: Main/Secondary могут независимо быть SIP или WebRTC.
            bool isSecondary = connectionType == ConnectionSelectionWindow.ConnectionType.Secondary;
            bool useWebRtc = ShouldUseWebRtcForConnection(isSecondary);
            var webRtcSlotService = isSecondary ? WebRtcService.Secondary : WebRtcService.Main;
            
            // Проверяем состояние WebRTC и SIP сервисов
            if (useWebRtc)
            {
                if (CallHandlingHelpers.IsWebRtcCallActive(webRtcSlotService))
                {
                    MainWindow.Log($"[MainWindow] PerformCall: WebRTC call is active, ignoring call to {number}");
                    CustomMessageBox.Show(
                        "A WebRTC call is already in progress. Please end the current call before making a new one.",
                        "Call In Progress",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }
            }
            else
            {
                SipService? sipBusyCheck = connectionType == ConnectionSelectionWindow.ConnectionType.Secondary
                    ? _sipService2
                    : _sipService;
                if (sipBusyCheck != null && sipBusyCheck.IsOutboundBusy)
                {
                    MainWindow.Log($"[MainWindow] PerformCall: SIP line busy (active call or teardown), ignoring call to {number}");
                    CustomMessageBox.Show(
                        "A SIP call is still finishing on this connection. Please wait a moment before calling again.",
                        "Call In Progress",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }
            }
            
            // Проверяем подключение в зависимости от режима
            if (useWebRtc)
            {
                // WebRTC режим - проверяем готовность WebRTC для выбранного слота
                if (webRtcSlotService == null || !webRtcSlotService.IsReadyForCalls)
                {
                    Log($"[Call] ERROR: WebRTC mode enabled for slot '{webRtcSlotService?.Slot ?? "?"}' but service is not ready (IsReadyForCalls={webRtcSlotService?.IsReadyForCalls ?? false})");
                    CustomMessageBox.Show(
                        "WebRTC service is not ready. Please wait for initialization or check settings.",
                        "WebRTC Not Ready",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }
            }
            else
            {
                // SIP режим - определяем, какое подключение использовать
                if (connectionType == ConnectionSelectionWindow.ConnectionType.Secondary)
                {
                    // Используем второе подключение
                    if (_sipService2 == null || !IsSecondConnectionConnected())
                    {
                        CustomMessageBox.Show(
                            "The second connection is not available. Please check your connection settings.",
                            "Connection Not Available",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            this);
                        return;
                    }
                    sipServiceToUse = _sipService2;
                    Log($"[Call] Using secondary connection (SIP) for call to {number}");
                }
                else
                {
                    // Используем основное подключение
                    if (_sipService == null || !IsConnected)
                    {
                        CustomMessageBox.Show(
                            "The main connection is not available. Please check your connection settings.",
                            "Connection Not Available",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            this);
                        return;
                    }
                    sipServiceToUse = _sipService;
                    Log($"[Call] Using main connection (SIP) for call to {number}");
                }
            }

            DateTime callStartTime = DateTime.Now;
            CallHistoryItem? currentCallHistoryItem = null;
            bool outgoingCallHandlersAttached = false;
            bool callConnected = false;
            DateTime? callConnectedTime = null;

            try
            {
                SetCallButtonsEnabled(false);

                Log($"[Call] Making call to {number} - Transport: {(useWebRtc ? "WebRTC" : "SIP")}");
                
                // Открываем окно звонка
                CallWindow callWindow;
                bool callWindowShownEarly = false;
                
                if (useWebRtc)
                {
                    // Устанавливаем cooldown для блокировки SIP входящих при исходящем WebRTC звонке
                    if (_sipService != null)
                    {
                        _sipService.SetIgnoreSipCooldown(3);
                    }
                    var webRtcConfig = GetWebRtcConfigForConnection(isSecondary);
                    if (webRtcConfig != null)
                    {
                        Log($"[Call][WebRTC] Making outgoing call to {number}");
                        Log($"[Call][WebRTC] Config: WsUri={webRtcConfig.WsUri}, SipUri={webRtcConfig.SipUri}, Password={(string.IsNullOrEmpty(webRtcConfig.Password) ? "NOT SET" : "***")}");
                        
                        // Добавляем запись в историю звонков (исходящий WebRTC звонок)
                        var outgoingCallItem = new CallHistoryItem
                        {
                            PhoneNumber = number,
                            CallTime = callStartTime,
                            Status = CallStatus.Calling,
                            IsIncoming = false,
                            Transport = CallTransport.WebRtc,
                            ConnectionSlot = isSecondary ? CallConnectionSlot.Secondary : CallConnectionSlot.Main
                        };
                        _callHistoryService.AddCall(outgoingCallItem);
                        LoadCallHistory();
                        currentCallHistoryItem = outgoingCallItem;
                        
                        callWindow = new CallWindow(webRtcConfig, number, isIncomingCall: false, callStartTime: callStartTime, amoCrmLeadId: leadId, webRtcService: webRtcSlotService)
                {
                    Owner = this
                        };
                        
                        // CallerID Originate is only supported on the main MikoPBX gateway connection.
                        // Secondary slots (plain Asterisk etc.) must use direct WebRTC INVITE via their own UA.
                        bool useOriginate = !isSecondary && _allowedCallerIds.Count > 0 && _mikoPbxCdrService != null;
                        if (useOriginate)
                        {
                            string selectedCallerId =
                                !string.IsNullOrWhiteSpace(selectedOutboundCallerIdForOriginate) &&
                                _allowedCallerIds.Contains(selectedOutboundCallerIdForOriginate)
                                    ? selectedOutboundCallerIdForOriginate
                                    : (_allowedCallerIds.Count == 1
                                        ? _allowedCallerIds[0]
                                        : ((CallerIdComboBox.SelectedItem as CallerIdItem)?.Number ?? _allowedCallerIds[0]));

                            Log($"[Call][Originate] Using PBX Originate: dst={number}, callerId={selectedCallerId}");

                            // Register pending originate BEFORE the HTTP call — PBX can INVITE us before the API returns.
                            OriginateCoordinator.BeginPending(number, selectedCallerId, callWindow);
                            _ = webRtcSlotService.NotifyOriginatePendingAsync(true);
                            callWindow.MarkAsOriginateCall();
                            callWindow.SetOutboundCallerId(selectedCallerId);
                            CallHandlingHelpers.PrepareCallWindowForDisplay(callWindow, isIncomingCall: false);
                            WindowForegroundHelper.ShowAboveAll(callWindow, this);
                            callWindowShownEarly = true;

                            TryAttachOutgoingCallCoreHandlers(
                                callWindow, number, callStartTime, currentCallHistoryItem,
                                sipServiceToUse, webRtcSlotService, ref outgoingCallHandlersAttached);
                            try { webRtcSlotService.PauseAutoReconnect("originate_pending"); } catch { }

                            try
                            {
                                var origResult = await _mikoPbxCdrService!.OriginateCallAsync(number, selectedCallerId);
                                if (origResult.Success)
                                {
                                    OriginateCoordinator.SetPendingId(origResult.OriginateId);
                                    Log($"[Call][Originate] Success. Waiting for PBX callback, originateId={origResult.OriginateId}, callerId={selectedCallerId}");

                                    // Save last selected CallerID
                                    try
                                    {
                                        string settingsPath = System.IO.Path.Combine(
                                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                            "Callspire", "settings.json");
                                        if (System.IO.File.Exists(settingsPath))
                                        {
                                            string json = System.IO.File.ReadAllText(settingsPath);
                                            var sett = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(json);
                                            if (sett != null)
                                            {
                                                sett.SelectedOutboundCallerId = selectedCallerId;
                                                System.IO.File.WriteAllText(settingsPath,
                                                    Newtonsoft.Json.JsonConvert.SerializeObject(sett, Newtonsoft.Json.Formatting.Indented));
                                            }
                                        }
                                    }
                                    catch { }

                                    // Don't call MakeCallAsync — PBX will ring our extension via INVITE
                                    // CallWindow is already created, waiting for auto-answer in HandleIncomingWebRtcCall
                                    goto afterCallWindowCreated;
                                }
                                else
                                {
                                    Log($"[Call][Originate] Failed: {origResult.Error}. Falling back to direct WebRTC call.");
                                    ClearPendingOriginateState(resetAcceptedSession: true);
                                }
                            }
                            catch (Exception ex)
                            {
                                Log($"[Call][Originate] Exception: {ex.Message}. Falling back to direct WebRTC call.");
                                ClearPendingOriginateState(resetAcceptedSession: true);
                            }
                        }

                        // Direct WebRTC call (standard path or fallback from Originate failure)
                        try
                        {
                            await webRtcSlotService.MakeCallAsync(number);
                        }
                        catch (Exception ex)
                        {
                            Log($"[Call][WebRTC] ERROR: MakeCallAsync failed for {number}: {ex.Message}");
                            try { callWindow.Close(); } catch { }
                            SetCallButtonsEnabled(true);
                            return;
                        }
                    }
                    else
                    {
                        Log($"[Call][WebRTC] ERROR: WebRTC mode enabled but WebRTC config is missing. Blocking call to {number}.");
                        CustomMessageBox.Show(
                            "WebRTC is enabled, but WebRTC settings are incomplete.\n\n" +
                            "Please open Settings → Advanced and configure WebSocket URI and credentials.",
                            "WebRTC Not Configured",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            this);
                        SetCallButtonsEnabled(true);
                        return;
                    }
                }
                else
                {
                    Log($"[Call][SIP] Making outgoing call to {number}");
                    if (sipServiceToUse == null)
                    {
                        Log($"[Call][SIP] ERROR: sipServiceToUse is null, cannot create CallWindow");
                        SetCallButtonsEnabled(true);
                        return;
                    }
                    
                    // Добавляем запись в историю звонков (исходящий SIP звонок)
                    var outgoingCallItem = new CallHistoryItem
                    {
                        PhoneNumber = number,
                        CallTime = callStartTime,
                        Status = CallStatus.Calling,
                        IsIncoming = false,
                        Transport = CallTransport.Sip,
                        ConnectionSlot = isSecondary ? CallConnectionSlot.Secondary : CallConnectionSlot.Main
                    };
                    _callHistoryService.AddCall(outgoingCallItem);
                    LoadCallHistory();
                    currentCallHistoryItem = outgoingCallItem;
                    
                    callWindow = new CallWindow(sipServiceToUse, number, isIncomingCall: false, callStartTime: callStartTime, amoCrmLeadId: leadId)
                    {
                        Owner = this
                    };
                }
                
                afterCallWindowCreated:
                TryAttachOutgoingCallCoreHandlers(
                    callWindow, number, callStartTime, currentCallHistoryItem,
                    sipServiceToUse, webRtcSlotService, ref outgoingCallHandlersAttached);

                // Подписываемся на события статуса для отслеживания состояния звонка (только для SIP звонков)
                if (sipServiceToUse != null && !useWebRtc)
                {
                    // ВАЖНО: SipService общий для всех звонков одного соединения. Обработчики ниже захватывают
                    // currentCallHistoryItem/callConnected конкретного звонка. Если их не отписывать при завершении,
                    // они накапливаются и срабатывают на события ПОСЛЕДУЮЩИХ звонков (тот же номер/сервис),
                    // «перетекая» в чужую запись истории — из-за этого отклонённые (603) звонки задним числом
                    // помечались как Completed, когда следующий звонок реально соединялся.
                    var sipForHandlers = sipServiceToUse;
                    Action<string>? statusHandler = null;
                    Action? endedHandler = null;

                    statusHandler = (status) =>
                    {
                        // Ignore stale handlers from previous calls on the shared SipService instance.
                        string? activeDial = sipForHandlers.ActiveDialNumber;
                        if (!string.IsNullOrEmpty(activeDial)
                            && !string.Equals(activeDial, number, StringComparison.OrdinalIgnoreCase))
                            return;

                        // Только INVITE 200 OK / явное «connected» — не CANCEL/REGISTER 200 OK (иначе WasAnswered=true без разговора).
                        if (status.Contains("Call connected") || status.Contains("Call answered") ||
                            status.Contains("Call progress: 200 OK"))
                        {
                            callConnected = true;
                            callConnectedTime = DateTime.Now;
                            if (currentCallHistoryItem != null)
                            {
                                currentCallHistoryItem.Status = CallStatus.Connected;
                                currentCallHistoryItem.WasAnswered = true;
                                currentCallHistoryItem.AnswerTime ??= callConnectedTime;
                                _callHistoryService.UpdateCallStatus(number, callStartTime, CallStatus.Connected);
                                _callHistoryService.UpdateCallDetails(
                                    number,
                                    callStartTime,
                                    answerTime: callConnectedTime,
                                    wasAnswered: true,
                                    transport: CallTransport.Sip);
                            }
                        }
                    };

                    // Подписываемся на событие завершения звонка для обновления состояния кнопки
                    endedHandler = () =>
                    {
                        // Сразу отписываем обработчики этого звонка, чтобы они не реагировали
                        // на события следующих звонков по общему SipService.
                        try { sipForHandlers.OnStatusChanged -= statusHandler; } catch { }
                        try { sipForHandlers.OnCallEnded -= endedHandler; } catch { }

                        Dispatcher.Invoke(() =>
                        {
                            SetCallButtonsEnabled(true);
                            try { WebRtcService.Main?.ResumeAutoReconnect("sip_call_ended"); } catch { }

                            // Обновляем историю звонка
                            if (currentCallHistoryItem != null)
                            {
                                TimeSpan? duration = null;
                                if (callConnected && callConnectedTime.HasValue)
                                {
                                    duration = DateTime.Now - callConnectedTime.Value;
                                }
                                
                                currentCallHistoryItem.Status = callConnected ? CallStatus.Ended : CallStatus.Failed;
                                currentCallHistoryItem.Duration = duration;
                                _callHistoryService.UpdateCallStatus(number, callStartTime, 
                                    callConnected ? CallStatus.Ended : CallStatus.Failed, duration);
                                LoadCallHistory();
                            }
                        });
                    };

                    sipServiceToUse.OnStatusChanged += statusHandler;
                    sipServiceToUse.OnCallEnded += endedHandler;
                }
                
                if (!callWindowShownEarly)
                {
                    CallHandlingHelpers.PrepareCallWindowForDisplay(callWindow, isIncomingCall: false);
                    if (WindowForegroundHelper.IsElevatedForegroundActive)
                        WindowForegroundHelper.ShowAboveAll(callWindow, this);
                    else
                        callWindow.Show();
                }

                WindowForegroundHelper.CompleteElevatedForegroundSession(this);
                if (!useWebRtc && sipServiceToUse != null)
                {
                    try { WebRtcService.Main?.PauseAutoReconnect("sip_call_active"); } catch { }
                    await sipServiceToUse.CallAsync(number);
                }
            }
            catch (Exception ex)
            {
                // Обновляем историю при ошибке
                if (currentCallHistoryItem != null)
                {
                    currentCallHistoryItem.Status = CallStatus.Failed;
                    currentCallHistoryItem.ErrorMessage = ex.Message;
                    _callHistoryService.UpdateCallStatus(number, callStartTime, CallStatus.Failed, null, ex.Message);
                    LoadCallHistory();
                }
                
                CustomMessageBox.Show($"Call error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
                SetCallButtonsEnabled(true);
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            // Для Windows 11 явно включаем анимации перед минимизацией
            // Это гарантирует, что анимации не были случайно отключены
            if (NativeWindowAppearanceManager.IsWindows11OrGreater())
            {
                Windows11BackdropService.EnsureTransitionsEnabled(this);
            }
            
            // Используем SystemCommands.MinimizeWindow() - это стандартный способ для WPF
            // который правильно работает с WindowChrome и SingleBorderWindow
            SystemCommands.MinimizeWindow(this);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            // Maximize via double-click disabled by request.

            _titleBarDragPending = true;
            _titleBarDownPoint = Mouse.GetPosition(this);
            _titleBarDownScreenPoint = PointToScreen(_titleBarDownPoint);
            _titleBarDownPercentX = ActualWidth > 0 ? _titleBarDownPoint.X / ActualWidth : 0.5;
            _titleBarDownPercentX = Math.Max(0.0, Math.Min(1.0, _titleBarDownPercentX));

            try { if (sender is UIElement el) el.CaptureMouse(); } catch { }
        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_titleBarDragPending) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _titleBarDragPending = false;
                try { if (sender is UIElement el) el.ReleaseMouseCapture(); } catch { }
                return;
            }

            // Start drag only after the user moves a bit (Windows-like behavior)
            var pos = Mouse.GetPosition(this);
            if (Math.Abs(pos.X - _titleBarDownPoint.X) < 6 && Math.Abs(pos.Y - _titleBarDownPoint.Y) < 6)
            {
                return;
            }

            _titleBarDragPending = false;
            try { if (sender is UIElement el) el.ReleaseMouseCapture(); } catch { }

            if (WindowState == WindowState.Maximized)
            {
                RestoreFromMaximizedUnderCursor(_titleBarDownPercentX, _titleBarDownScreenPoint);
            }

            try { DragMove(); } catch { }
        }

        private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _titleBarDragPending = false;
            try { if (sender is UIElement el) el.ReleaseMouseCapture(); } catch { }
        }

        private void RestoreFromMaximizedUnderCursor(double percentX, Point screenPosPx)
        {
            try
            {
                // Convert to DIPs (window coords are DIPs)
                var screenPos = ScreenPxToDip(screenPosPx);

                WindowState = WindowState.Normal; // no animation: user is dragging

                // Place window so cursor stays at same relative position
                Left = screenPos.X - (RestoreBounds.Width * percentX);
                Top = Math.Max(0, screenPos.Y - 12);
            }
            catch { }
        }

        private Point ScreenPxToDip(Point screenPx)
        {
            try
            {
                var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
                return m.Transform(screenPx);
            }
            catch
            {
                return screenPx;
            }
        }

        // Maximize/restore disabled by request.
        // Work-area clamping + maximize block: WindowWorkAreaHelper via NativeWindowAppearanceManager.

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _sipService?.Dispose();
            _sipService2?.Dispose();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                UnregisterPowerAndNetworkHandlers();

                // Закрываем только окно активного звонка (остальные окна — независимые).
                var windowsToClose = new List<Window>();
                foreach (Window window in Application.Current.Windows)
                {
                    if (window is CallWindow)
                        windowsToClose.Add(window);
                }
                
                foreach (var window in windowsToClose)
                {
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                        // Игнорируем ошибки закрытия дочерних окон
                    }
                }
                
                StopSecondarySipMaintenanceTimer();

                // Останавливаем SIP сервисы
                _sipService?.Dispose();
                _sipService = null;
                _sipService2?.Dispose();
                _sipService2 = null;
                
                // Останавливаем watchdog timer WebRTC
                WebRtcService.Main.StopWatchdog();

                // Останавливаем auto-reconnect и отвязываем engine (важно: иначе таймеры могут жить после Dispose WebView2).
                try { WebRtcService.Main.StopAutoReconnect(); } catch { }
                try { WebRtcService.Main.DetachEngine(resetState: true); } catch { }
                
                // Отписываемся от событий WebRTC
                WebRtcService.Main.Event -= OnWebRtcEvent;
                
                // Очищаем WebView2 (критично для предотвращения утечки handles)
                if (_webRtcEngine != null)
                {
                    try
                    {
                        // Удаляем из визуального дерева перед Dispose
                        if (WebRtcHostGrid != null && WebRtcHostGrid.Children.Contains(_webRtcEngine))
                        {
                            WebRtcHostGrid.Children.Remove(_webRtcEngine);
                        }
                        
                        // Dispose WebView2 (освобождает все ресурсы и handles, включая CoreWebView2)
                        _webRtcEngine.Dispose();
                        _webRtcEngine = null;
                        _sharedWebRtcHost = null;
                        Log("[MainWindow] WebView2 disposed");
                    }
                    catch (Exception ex)
                    {
                        Log($"[MainWindow] Error disposing WebView2: {ex.Message}");
                        // Пытаемся обнулить ссылку даже при ошибке
                        _webRtcEngine = null;
                        _sharedWebRtcHost = null;
                    }
                }
                
                // Очищаем shared WebRTC status service
                var sharedService = GetSharedWebRtcStatusService();
                if (sharedService != null)
                {
                    try
                    {
                        sharedService.Dispose();
                        SetSharedWebRtcStatusService(null);
                        Log("[MainWindow] Shared WebRTC status service disposed");
                    }
                    catch (Exception ex)
                    {
                        Log($"[MainWindow] Error disposing shared WebRTC status service: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error in OnClosed: {ex.Message}");
            }
            finally
            {
                SingleInstanceManager.UnregisterMainWindow(this);
                base.OnClosed(e);
            }
        }

        private void AddToLog(string message)
        {
            _logHistory.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            
            // Ограничиваем размер истории (последние 5000 записей)
            if (_logHistory.Count > 5000)
            {
                _logHistory.RemoveAt(0);
            }
            
            // Обновляем окно логов, если оно открыто (с проверкой потока)
            if (_logWindow != null)
            {
                // Проверяем, что мы в UI потоке
                if (Dispatcher.CheckAccess())
                {
                    // Мы в UI потоке, можно обращаться напрямую
                    if (_logWindow.IsLoaded)
                    {
                        _logWindow.AddLog(message);
                    }
                }
                else
                {
                    // Мы не в UI потоке, используем Dispatcher
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
            if (_logWindow != null && _logWindow.IsLoaded)
            {
                _logWindow.AddLog(message);
                        }
                    }));
                }
            }
        }

        private void ShowLogsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_logWindow == null || !_logWindow.IsLoaded)
            {
                _logWindow = new LogWindow();
                CenterNonOwnedWindowOverThis(_logWindow);
                
                // Предпочитаем загружать из файла лога — там полная история за день.
                // Если файл недоступен, fallback на in-memory буфер.
                string initialLogs = LoadLogsFromFileForWindow();
                _logWindow.SetLogs(initialLogs);
                
                _logWindow.Closed += (s, args) => { _logWindow = null; };
                _logWindow.Show();
            }
            else
            {
                _logWindow.Activate();
            }
        }

        /// <summary>
        /// Читает последние 5000 строк из сегодняшнего файла лога.
        /// Если файл недоступен, возвращает in-memory буфер.
        /// </summary>
        private string LoadLogsFromFileForWindow()
        {
            try
            {
                string logFilePath = FileLogService.Instance.GetCurrentLogFilePath();
                if (File.Exists(logFilePath))
                {
                    // Читаем с FileShare.ReadWrite, чтобы не мешать текущей записи
                    using var fs = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new System.IO.StreamReader(fs, System.Text.Encoding.UTF8);
                    string content = reader.ReadToEnd();

                    // Берём последние 5000 строк, чтобы не перегружать UI
                    var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 5000)
                        lines = lines[^5000..];

                    return string.Join("\n", lines);
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] LoadLogsFromFileForWindow: could not read log file: {ex.Message}");
            }

            // Fallback: in-memory буфер
            return string.Join("\n", _logHistory);
        }

        private string? FormatStatusMessage(string technicalStatus)
        {
            if (string.IsNullOrEmpty(technicalStatus))
                return "Not connected";

            // Преобразуем технические сообщения в понятные для пользователя
            string status = technicalStatus.ToLower();

            // Статусы подключения к серверу
            // Registration success should be reflected by UpdateConnectionStatus() as "Connected with SIP".
            // Returning null prevents this helper from overwriting the connection-status label.
            if (status.Contains("registration successful"))
                return null;
            
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
            
            // ВАЖНО: Не возвращаем "Connecting to server..." для WebRTC режима
            // Это может перезаписать статус WebRTC после регистрации
            if (status.Contains("registering on sip server") || status.Contains("initializing sip"))
            {
                // Для WebRTC режима не показываем SIP статусы
                if (ShouldUseWebRtc())
                {
                    return null; // Не обновляем статус для WebRTC режима
                }
                return "Connecting to server...";
            }
            
            if (status.Contains("sip transport listening"))
                return "Starting connection...";
            
            // Фильтруем все технические SIP сообщения (звонки, ответы и т.д.)
            // Не показываем их в статусе, только в логах
            if (status.Contains("sip response:") || 
                status.Contains("sip request:") ||
                status.Contains("invite") ||
                status.Contains("200 ok") ||
                status.Contains("487 request terminated") ||
                status.Contains("180 ringing") ||
                status.Contains("183 session progress") ||
                status.Contains("100 trying") ||
                status.Contains("call progress:") ||
                status.Contains("call initiated") ||
                status.Contains("call connected") ||
                status.Contains("call answered") ||
                status.Contains("call ended") ||
                status.Contains("call failed") ||
                status.Contains("call cancelled") ||
                status.Contains("hanging up") ||
                status.Contains("cancelling call") ||
                status.Contains("trace:") ||
                status.Contains("responded to options") ||
                status.Contains("call-id:") ||
                status.Contains("method: unknown"))
            {
                // Не обновляем статус для технических сообщений о звонках
                // Возвращаем null или пустую строку, чтобы сохранить текущий статус
                return null; // Вернем null, чтобы не обновлять статус
            }
            
            // Все сообщения о звонках уже отфильтрованы выше
            // Не показываем их в статусе - показываем только статус подключения к серверу
            // Возвращаем null, чтобы сохранить текущий статус подключения
            return null;
        }

        /// <summary>Заглушка: индикатор WebRTC на дайлере убран вместе со строкой «Status:».</summary>
        public void UpdateWebRtcIndicator()
        {
        }

        // Throttling для UpdateConnectionStatus - предотвращает слишком частые вызовы
        private DateTime _lastStatusUpdateTime = DateTime.MinValue;
        private const int STATUS_UPDATE_THROTTLE_MS = 500; // Минимум 500мс между обновлениями

        /// <summary>Last settings file write time (UTC ticks) used for connection name labels; -1 if file missing.</summary>
        private long _connectionDisplayNamesSettingsTicks = long.MinValue;
        
        /// <summary>
        /// Публичное свойство для получения текущего статуса подключения (для синхронизации с SettingsWindow)
        /// </summary>
        public string ConnectionStatus
        {
            get
            {
                return MainStatusTextBlock?.Text ?? "Not connected";
            }
        }
        
        /// <summary>
        /// Обновляет статус подключения с учетом типа подключения (WebRTC или SIP)
        /// Оптимизировано для предотвращения зависаний UI
        /// </summary>
        public void UpdateConnectionStatus()
        {
            try
            {
                EnsureConnectionStatusLabelsCurrent();

                // Обновляем статус основного подключения
                UpdateMainConnectionStatus();
                
                // Обновляем статус второго подключения
                UpdateSecondaryConnectionStatus();
                
                // Обновляем статус интеграции AmoCRM
                UpdateAmoCrmConnectionStatus();
                
                // Обновляем кнопки вызова (single vs split)
                UpdateCallButtonMode();
            }
            catch (Exception ex)
            {
                // Обрабатываем ошибки без блокировки UI
                Log($"[MainWindow] Error in UpdateConnectionStatus: {ex.Message}");
            }
        }
        
        private void UpdateMainConnectionStatus()
        {
            try
            {
                if (MainStatusTextBlock == null || MainStatusIndicator == null) return;
                
                bool useWebRtc = ShouldUseWebRtc();
                bool isConnected = IsConnected;
                string newStatus = "";
                Brush statusColor;
                
                if (useWebRtc)
                {
                    // WebRTC режим
                    if (isConnected)
                    {
                        _webRtcDisconnectedSince = DateTime.MinValue;
                        newStatus = "Connected with WebRTC";
                        statusColor = (Brush)FindResource("AccentGreenBrush");
                    }
                    else
                    {
                        // WebRTC не подключен — не используем широкий Contains("Connected"): строка
                        // "Connected to WebRTC..." (WSS открыт, SIP ещё нет) содержит "Connected" и
                        // иначе навсегда блокировала бы сброс при reg_failed.
                        string currentStatus = MainStatusTextBlock.Text ?? "";
                        bool inProgress =
                            currentStatus.Contains("Initializing", StringComparison.OrdinalIgnoreCase) ||
                            currentStatus.Contains("Connecting", StringComparison.OrdinalIgnoreCase) ||
                            currentStatus.StartsWith("Connected to WebRTC", StringComparison.OrdinalIgnoreCase);
                        bool regOrAuthError =
                            currentStatus.Contains("Registration failed", StringComparison.OrdinalIgnoreCase) ||
                            currentStatus.Contains("Authentication", StringComparison.OrdinalIgnoreCase) ||
                            currentStatus.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);

                        if (regOrAuthError)
                        {
                            newStatus = currentStatus;
                            statusColor = (Brush)FindResource("AccentRedBrush");
                        }
                        else if (!inProgress)
                        {
                            if (_lastIsConnected)
                            {
                                if (_webRtcDisconnectedSince == DateTime.MinValue)
                                    _webRtcDisconnectedSince = DateTime.Now;

                                if ((DateTime.Now - _webRtcDisconnectedSince).TotalSeconds < 6)
                                    newStatus = "Reconnecting...";
                                else
                                    newStatus = "Not connected";
                            }
                            else
                            {
                                newStatus = "Not connected";
                            }
                            statusColor = (Brush)FindResource("TextSecondaryBrush");
                        }
                        else
                        {
                            newStatus = currentStatus;
                            statusColor = (Brush)FindResource("TextSecondaryBrush");
                        }
                    }
                }
                else
                {
                    // SIP режим
                    if (isConnected)
                    {
                        newStatus = "Connected";
                        statusColor = (Brush)FindResource("AccentGreenBrush");
                    }
                    else
                    {
                        newStatus = "Not connected";
                        statusColor = (Brush)FindResource("TextSecondaryBrush");
                    }
                }
                
                // Обновляем UI только если статус изменился
                if (MainStatusTextBlock.Text != newStatus)
                {
                    MainStatusTextBlock.Text = newStatus;
                    MainStatusIndicator.Background = statusColor;
                    _lastIsConnected = isConnected;
                    
                    // Уведомляем SettingsWindow об изменении статуса для синхронизации
                    string fullStatus = useWebRtc ? newStatus : $"Connected with SIP";
                    OnConnectionStatusChanged?.Invoke(fullStatus);
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error in UpdateMainConnectionStatus: {ex.Message}");
            }
        }
        
        private void UpdateSecondaryConnectionStatus()
        {
            try
            {
                if (SecondaryStatusPanel == null || SecondaryStatusTextBlock == null || SecondaryStatusIndicator == null) return;
                
                // Проверяем, настроено ли второе подключение
                AppSettings? settings = null;
                try
                {
                    string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsFilePath))
                    {
                        string json = File.ReadAllText(settingsFilePath);
                        settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    }
                }
                catch { }

                bool secondaryUsesWebRtc = settings != null && AppSettings.SecondaryLineUsesWebRtc(settings);

                // Panel is visible if either SIP or WebRTC secondary is configured.
                bool hasSipConfig = settings != null &&
                                    !string.IsNullOrEmpty(settings.SipServer2) &&
                                    !string.IsNullOrEmpty(settings.SipUsername2) &&
                                    !string.IsNullOrEmpty(settings.SipPasswordEncrypted2);
                bool hasWebRtcConfig = settings != null &&
                                       AppSettings.HasMeaningfulWebRtcWsUri(settings.WebRtcWsUri2) &&
                                       !string.IsNullOrEmpty(AppSettings.EffectiveSecondaryWebRtcUsername(settings)) &&
                                       SipPasswordProvider.GetSecondaryWebRtcPassword(settings) != null;

                bool hasSecondaryConfig = secondaryUsesWebRtc ? hasWebRtcConfig : hasSipConfig;
                
                if (!hasSecondaryConfig)
                {
                    SecondaryStatusPanel.Visibility = Visibility.Collapsed;
                    return;
                }
                
                SecondaryStatusPanel.Visibility = Visibility.Visible;

                bool isConnected = secondaryUsesWebRtc
                    ? (WebRtcService.Secondary?.IsReadyForCalls ?? false)
                    : (_sipService2?.IsRegistered ?? false);

                string newStatus = isConnected
                    ? (secondaryUsesWebRtc ? "Connected with WebRTC" : "Connected")
                    : "Not connected";
                Brush statusColor = isConnected 
                    ? (Brush)FindResource("AccentGreenBrush")
                    : (Brush)FindResource("TextSecondaryBrush");
                
                if (SecondaryStatusTextBlock.Text != newStatus)
                {
                    SecondaryStatusTextBlock.Text = newStatus;
                    SecondaryStatusIndicator.Background = statusColor;
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error in UpdateSecondaryConnectionStatus: {ex.Message}");
            }
        }
        
        private void UpdateAmoCrmConnectionStatus()
        {
            try
            {
                if (AmoCrmStatusPanel == null || AmoCrmStatusTextBlock == null || AmoCrmStatusIndicator == null) return;
                
                // Проверяем, включена ли интеграция AmoCRM
                AppSettings? settings = null;
                try
                {
                    string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsFilePath))
                    {
                        string json = File.ReadAllText(settingsFilePath);
                        settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    }
                }
                catch { }
                
                bool isEnabled = settings != null && settings.EnableAmoCrmIntegration;
                
                if (!isEnabled)
                {
                    AmoCrmStatusPanel.Visibility = Visibility.Collapsed;
                    return;
                }
                
                AmoCrmStatusPanel.Visibility = Visibility.Visible;
                
                bool isConnected = IsAmoCrmServiceInitialized();
                string newStatus = isConnected ? "Connected" : "Not connected";
                Brush statusColor = isConnected 
                    ? (Brush)FindResource("AccentGreenBrush")
                    : (Brush)FindResource("TextSecondaryBrush");
                
                if (AmoCrmStatusTextBlock.Text != newStatus)
                {
                    AmoCrmStatusTextBlock.Text = newStatus;
                    AmoCrmStatusIndicator.Background = statusColor;
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error in UpdateAmoCrmConnectionStatus: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Обновляет информацию о пользователе в заголовке окна
        /// </summary>
        private void UpdateUserAccountInfo()
        {
            try
            {
                if (UserAccountTextBlock == null) return;
                
                bool isConnected = IsConnected;
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                
                // Показываем/скрываем индикатор онлайн в зависимости от статуса подключения
                if (OnlineStatusIndicator != null)
                {
                    OnlineStatusIndicator.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
                }
                
                if (!File.Exists(settingsFilePath))
                {
                    UserAccountTextBlock.Text = "Not connected";
                    if (UserAccountTextBlock.ToolTip is ToolTip toolTip && toolTip.Content is StackPanel panel)
                    {
                        var textBlock = panel.Children.OfType<TextBlock>().FirstOrDefault();
                        if (textBlock != null)
                            textBlock.Text = "Not connected";
                    }
                    return;
                }
                
                string json = File.ReadAllText(settingsFilePath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                
                if (settings == null)
                {
                    UserAccountTextBlock.Text = "Not connected";
                    if (UserAccountTextBlock.ToolTip is ToolTip toolTip && toolTip.Content is StackPanel panel)
                    {
                        var textBlock = panel.Children.OfType<TextBlock>().FirstOrDefault();
                        if (textBlock != null)
                            textBlock.Text = "Not connected";
                    }
                    return;
                }

                bool mainWebRtc = ShouldUseWebRtcForConnection(false);
                string username;
                string server;
                if (mainWebRtc)
                {
                    username = AppSettings.EffectiveMainWebRtcUsername(settings) ?? "";
                    server = "";
                    try
                    {
                        if (AppSettings.HasMeaningfulWebRtcWsUri(settings.WebRtcWsUri))
                            server = new Uri(settings.WebRtcWsUri!.Trim()).Host;
                    }
                    catch { }
                }
                else
                {
                    username = settings.SipUsername ?? "";
                    server = SipEndpointHelper.GetHostOnly(settings.SipServer ?? "");
                }

                if (string.IsNullOrEmpty(username))
                {
                    UserAccountTextBlock.Text = "Not connected";
                    if (UserAccountTextBlock.ToolTip is ToolTip toolTip0 && toolTip0.Content is StackPanel panel0)
                    {
                        var textBlock = panel0.Children.OfType<TextBlock>().FirstOrDefault();
                        if (textBlock != null)
                            textBlock.Text = "Not connected";
                    }
                    return;
                }
                
                // Формируем текст в формате "username@server"
                string displayText;
                if (string.IsNullOrEmpty(server))
                {
                    displayText = username;
                }
                else
                {
                    displayText = $"{username}@{server}";
                }
                
                // В title bar можно показать больше текста, но все равно ограничим для красоты
                if (displayText.Length > 30)
                {
                    displayText = displayText.Substring(0, 30) + "...";
                }
                
                UserAccountTextBlock.Text = displayText;
                
                // Обновляем ToolTip с полной информацией
                string fullInfo = string.IsNullOrEmpty(server) 
                    ? $"Account: {username}" 
                    : $"Account: {username}\nServer: {server}";
                
                if (UserAccountTextBlock.ToolTip is ToolTip toolTip2 && toolTip2.Content is StackPanel panel2)
                {
                    var textBlock = panel2.Children.OfType<TextBlock>().FirstOrDefault();
                    if (textBlock != null)
                        textBlock.Text = fullInfo;
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error in UpdateUserAccountInfo: {ex.Message}");
                if (UserAccountTextBlock != null)
                {
                    UserAccountTextBlock.Text = "Error";
                }
                if (OnlineStatusIndicator != null)
                {
                    OnlineStatusIndicator.Visibility = Visibility.Collapsed;
                }
            }
        }
        
        // WebRTC helper methods

        /// <summary>
        /// Возвращает true, если основное подключение использует WebRTC (для обратной совместимости).
        /// Учитывает миграцию legacy-флага UseWebRtcAudio.
        /// </summary>
        private bool ShouldUseWebRtc()
        {
            return ShouldUseWebRtcForConnection(false);
        }

        /// <summary>
        /// First-load migration: если в настройках отсутствует MainConnectionTransport,
        /// но legacy UseWebRtcAudio=true, ставим MainConnectionTransport="WebRtc" и сохраняем файл.
        /// Это нужно, чтобы пользователи, обновлённые со старой версии, не теряли WebRTC-режим.
        /// </summary>
        private static void MigrateLegacyTransportSettingIfNeeded()
        {
            string settingsFilePath = AppDataHelper.GetSettingsFilePath();
            if (!File.Exists(settingsFilePath)) return;

            string json = File.ReadAllText(settingsFilePath);
            var settings = JsonConvert.DeserializeObject<AppSettings>(json);
            if (settings == null) return;

            bool migrated = false;

            // Main: legacy UseWebRtcAudio=true → MainConnectionTransport="WebRtc"
            if (string.IsNullOrWhiteSpace(settings.MainConnectionTransport))
            {
                if (settings.UseWebRtcAudio)
                {
                    settings.MainConnectionTransport = "WebRtc";
                    migrated = true;
                    Log("[MainWindow] Migration: legacy UseWebRtcAudio=true → MainConnectionTransport=WebRtc");
                }
                else
                {
                    settings.MainConnectionTransport = "Sip";
                    migrated = true;
                }
            }

            // Secondary: при отсутствии поля — WebRTC, если в файле только WSS-конфиг, иначе Sip.
            if (string.IsNullOrWhiteSpace(settings.SecondaryConnectionTransport))
            {
                settings.SecondaryConnectionTransport =
                    AppSettings.SecondaryLineUsesWebRtc(settings) ? "WebRtc" : "Sip";
                migrated = true;
            }

            if (migrated)
            {
                string updated = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsFilePath, updated);
                Log("[MainWindow] Migration: per-connection transport fields persisted to settings file");
            }
        }

        /// <summary>
        /// Возвращает true, если указанное подключение (Main / Secondary) использует WebRTC.
        /// Читает MainConnectionTransport / SecondaryConnectionTransport из AppSettings.
        /// </summary>
        internal static bool ShouldUseWebRtcForConnection(bool secondary)
        {
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (settings == null) return false;
                    if (secondary)
                    {
                        return AppSettings.SecondaryLineUsesWebRtc(settings);
                    }
                    // Legacy fallback: если MainConnectionTransport не выставлен, смотрим на UseWebRtcAudio.
                    if (string.IsNullOrWhiteSpace(settings.MainConnectionTransport) || string.Equals(settings.MainConnectionTransport, "Sip", StringComparison.OrdinalIgnoreCase))
                    {
                        return settings.UseWebRtcAudio;
                    }
                    return string.Equals(settings.MainConnectionTransport, "WebRtc", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Игнорируем ошибки
            }
            return false;
        }
        
        private WebRtcConfig? GetWebRtcConfig()
        {
            return GetWebRtcConfigForConnection(false);
        }

        /// <summary>
        /// Собирает WebRtcConfig для указанного слота. secondary=true читает второй набор полей.
        /// </summary>
        private WebRtcConfig? GetWebRtcConfigForConnection(bool secondary)
        {
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (!File.Exists(settingsFilePath)) return null;

                string json = File.ReadAllText(settingsFilePath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                if (settings == null) return null;

                string? wsUriValue = secondary ? settings.WebRtcWsUri2 : settings.WebRtcWsUri;
                string? username = secondary
                    ? AppSettings.EffectiveSecondaryWebRtcUsername(settings)
                    : AppSettings.EffectiveMainWebRtcUsername(settings);
                string? sipServer = secondary ? settings.SipServer2 : settings.SipServer;
                string? sipPassword = secondary
                    ? SipPasswordProvider.GetSecondaryWebRtcPassword(settings)
                    : SipPasswordProvider.GetMainWebRtcPassword(settings);

                string? turnUri = secondary ? settings.SecondaryWebRtcTurnUri : (settings.MainWebRtcTurnUri ?? settings.WebRtcTurnUri);
                string? turnUser = secondary ? settings.SecondaryWebRtcTurnUsername : (settings.MainWebRtcTurnUsername ?? settings.WebRtcTurnUsername);
                string? turnPass = secondary
                    ? TurnPasswordProvider.GetSecondaryTurnPassword(settings)
                    : TurnPasswordProvider.GetMainTurnPassword(settings);

                if (string.IsNullOrEmpty(wsUriValue) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(sipPassword))
                {
                    Log($"[MainWindow] GetWebRtcConfigForConnection(secondary={secondary}): Missing required settings - wsUri={(string.IsNullOrEmpty(wsUriValue) ? "empty" : "set")}, user={(string.IsNullOrEmpty(username) ? "empty" : "set")}, pass={(string.IsNullOrEmpty(sipPassword) ? "empty" : "set")}");
                    return null;
                }

                // Формируем PBX-адрес: предпочтительно из WS URI, иначе из SIP-сервера.
                string pbxAddress = "";
                try
                {
                    var uri = new Uri(wsUriValue);
                    pbxAddress = uri.Host;
                }
                catch { }

                if (string.IsNullOrEmpty(pbxAddress))
                {
                    pbxAddress = SipEndpointHelper.GetHostOnly(sipServer);
                }

                if (string.IsNullOrEmpty(pbxAddress))
                {
                    return null;
                }

                // MikoPBX WebRTC AoR с суффиксом -WS (см. историю комментариев в проекте).
                string wsAor = username.EndsWith("-WS", StringComparison.OrdinalIgnoreCase) ? username : $"{username}-WS";
                string sipUri = $"sip:{wsAor}@{pbxAddress}";

                var config = new WebRtcConfig
                {
                    WsUri = wsUriValue,
                    SipUri = sipUri,
                    Password = sipPassword,
                    EnableDebug = settings.EnableWebRtcDebug,
                    TurnServer = turnUri,
                    TurnUsername = turnUser,
                    TurnPassword = turnPass
                };
                Log($"[MainWindow] GetWebRtcConfigForConnection(secondary={secondary}): WsUri={config.WsUri}, SipUri={config.SipUri}, TurnServer={(string.IsNullOrWhiteSpace(config.TurnServer) ? "<none>" : config.TurnServer)}");
                return config;
            }
            catch
            {
                // Игнорируем ошибки
            }
            return null;
        }

        /// <summary>
        /// Проверяет, инициализирован ли AmoCRM сервис
        /// </summary>
        public bool IsAmoCrmServiceInitialized()
        {
            return IsAmoCrmIntegrationEnabled() && _amoCrmService != null && _amoCrmService.IsInitialized;
        }

        private static bool IsAmoCrmIntegrationEnabled()
        {
            try
            {
                var settings = AppDataHelper.LoadSettingsOrNew();
                return settings.EnableAmoCrmIntegration;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Возвращает экземпляр AmoCRM сервиса (для использования в CallWindow)
        /// </summary>
        public AmoCrmService? GetAmoCrmService()
        {
            return _amoCrmService;
        }

        /// <summary>
        /// Инициализирует AmoCRM сервис при старте приложения (асинхронно, не блокирует UI)
        /// </summary>
        private static bool IsLocalKommoConfigured(AppSettings settings)
        {
            if (!settings.EnableAmoCrmIntegration || string.IsNullOrWhiteSpace(settings.AmoCrmSubdomain))
                return false;

            string authMode = settings.AmoCrmAuthMode ?? "manual";
            if (authMode == "oauth")
                return !string.IsNullOrEmpty(settings.AmoCrmOAuthAccessTokenEncrypted);

            return !string.IsNullOrEmpty(settings.AmoCrmAccessTokenEncrypted);
        }

        private static bool IsPbxGatewayCdrConfigured(AppSettings settings)
        {
            return settings.EnableMikoPbxCdr
                && !string.IsNullOrEmpty(settings.MikoPbxCdrServiceUrl)
                && !string.IsNullOrEmpty(settings.MikoPbxCdrTokenEncrypted)
                && !string.IsNullOrEmpty(settings.MikoPbxExtension);
        }

        /// <summary>
        /// Read the latest saved Kommo source from disk so queued inits do not use a stale snapshot.
        /// </summary>
        private static string? LoadKommoConnectionSourceFromDisk()
        {
            try
            {
                var source = AppDataHelper.LoadSettingsOrNew().AmoCrmConnectionSource?.Trim().ToLowerInvariant();
                return source is "gateway" or "local" ? source : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? ResolveKommoConnectionSource(AppSettings settings)
        {
            return LoadKommoConnectionSourceFromDisk()
                ?? settings.AmoCrmConnectionSource?.Trim().ToLowerInvariant();
        }

        private static bool IsKommoGatewayModuleOfferedToClient(KommoGatewayStatus? status)
        {
            return status != null
                && !status.Excluded
                && (status.OfferGateway || status.Enabled);
        }

        public KommoGatewayStatus? GetCachedKommoGatewayStatus() => _cachedKommoGatewayStatus;

        private async Task RefreshCachedKommoGatewayStatusAsync()
        {
            if (_mikoPbxCdrService == null)
                return;

            try
            {
                var status = await _mikoPbxCdrService.GetKommoStatusAsync().ConfigureAwait(false);
                _cachedKommoGatewayStatus = status;
                if (IsKommoGatewayModuleOfferedToClient(status))
                {
                    Log("[MainWindow] Gateway Kommo module offered to this extension (cached for settings UI)");
                }

                await Dispatcher.InvokeAsync(NotifySettingsKommoGatewayModuleStatus);
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Kommo gateway status prefetch error: {ex.Message}");
            }
        }

        private void NotifySettingsKommoGatewayModuleStatus()
        {
            var settingsWindow = Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault();
            settingsWindow?.SyncKommoGatewayModuleFromMain(refreshFromGateway: false);
        }

        private async Task<bool> IsGatewayKommoAvailableAsync()
        {
            if (_mikoPbxCdrService == null)
                return false;

            var status = await _mikoPbxCdrService.GetKommoStatusAsync().ConfigureAwait(false);
            _cachedKommoGatewayStatus = status;
            return status?.Available == true;
        }

        private async Task<bool> IsGatewayKommoOfferedToUserAsync()
        {
            if (_mikoPbxCdrService == null)
                return false;

            var status = await _mikoPbxCdrService.GetKommoStatusAsync().ConfigureAwait(false);
            _cachedKommoGatewayStatus = status;
            return IsKommoGatewayModuleOfferedToClient(status);
        }

        private async Task<bool> IsCurrentUserExcludedFromGatewayKommoAsync()
        {
            if (_mikoPbxCdrService == null)
                return false;

            var status = await _mikoPbxCdrService.GetKommoStatusAsync().ConfigureAwait(false);
            _cachedKommoGatewayStatus = status;
            return status?.Excluded == true;
        }

        private static void SaveAmoCrmConnectionSourceSetting(string source)
        {
            try
            {
                AppDataHelper.SetKommoConnectionSource(source);
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Failed to save AmoCrmConnectionSource: {ex.Message}");
            }
        }

        private string? PromptKommoConnectionSource()
        {
            var result = CustomMessageBox.Show(
                "Kommo is configured both in PBX Gateway and in this softphone.\n\nUse Gateway (shared company account) or Local (this app settings)?",
                "Choose Kommo connection",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                this,
                yesButtonText: "Gateway",
                noButtonText: "Local");

            if (result == MessageBoxResult.Yes)
                return "gateway";
            if (result == MessageBoxResult.No)
                return "local";
            return null;
        }

        private async Task<(bool Success, string? Error)> InitializeAmoCrmFromGatewayAsync()
        {
            if (_mikoPbxCdrService == null)
                return (false, "PBX Gateway client is not initialized");

            var (session, sessionError) = await _mikoPbxCdrService.GetKommoSessionAsync().ConfigureAwait(false);
            if (session == null || string.IsNullOrWhiteSpace(session.AccessToken))
                return (false, sessionError ?? "PBX Gateway Kommo session is unavailable");

            DateTime? expires = ParseKommoExpiresAt(session.ExpiresAt);

            if (_amoCrmService != null)
            {
                _amoCrmService.Dispose();
                _amoCrmService = null;
            }

            _amoCrmService = new AmoCrmService();
            var gateway = _mikoPbxCdrService;
            MainWindow.Log(
                $"[MainWindow] Gateway Kommo session: subdomain={session.Subdomain}, " +
                $"tokenLen={session.AccessToken.Length}, base={session.AccountBaseUrl ?? "(default)"}, " +
                $"kommoUserId={session.KommoUserId?.ToString() ?? "?"}, source={session.KommoUserIdSource ?? "?"}");
            await _amoCrmService.InitializeGatewayOAuthAsync(
                session.Subdomain,
                session.AccessToken,
                expires,
                async (forceRefresh) =>
                {
                    var (refreshed, _) = await gateway.GetKommoSessionAsync(forceRefresh).ConfigureAwait(false);
                    if (refreshed == null || string.IsNullOrWhiteSpace(refreshed.AccessToken))
                        return (string.Empty, null);
                    return (refreshed.AccessToken, ParseKommoExpiresAt(refreshed.ExpiresAt));
                },
                session.AccountBaseUrl,
                session.KommoUserId,
                session.KommoUserName).ConfigureAwait(false);

            return (true, null);
        }

        private async Task<(bool Success, string? Error)> InitializeAmoCrmFromGatewayWithRetryAsync()
        {
            const int maxAttempts = 3;
            int[] delaysMs = { 0, 2000, 5000 };
            string? lastError = null;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(delaysMs[attempt]).ConfigureAwait(false);

                try
                {
                    var result = await InitializeAmoCrmFromGatewayAsync().ConfigureAwait(false);
                    if (result.Success)
                        return result;

                    lastError = result.Error;
                    var kind = KommoInitHelper.ClassifyFailureMessage(result.Error);
                    if (kind != KommoInitFailureKind.Network || attempt == maxAttempts - 1)
                        return result;

                    await Dispatcher.InvokeAsync(() =>
                        Log($"[MainWindow] Gateway Kommo network error (attempt {attempt + 1}/{maxAttempts}): {result.Error}. Retrying..."));
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    var kind = KommoInitHelper.ClassifyFailure(ex);
                    if (kind == KommoInitFailureKind.Auth || attempt == maxAttempts - 1)
                        throw;

                    await Dispatcher.InvokeAsync(() =>
                        Log($"[MainWindow] Gateway Kommo network error (attempt {attempt + 1}/{maxAttempts}): {ex.Message}. Retrying..."));
                }
            }

            return (false, lastError);
        }

        private void ScheduleDeferredKommoInitRetry()
        {
            _kommoDeferredRetryCts?.Cancel();
            _kommoDeferredRetryCts = new CancellationTokenSource();
            var token = _kommoDeferredRetryCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || IsAmoCrmServiceInitialized())
                        return;

                    string settingsPath = AppDataHelper.GetSettingsFilePath();
                    if (!File.Exists(settingsPath))
                        return;

                    var settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(settingsPath));
                    if (settings?.EnableAmoCrmIntegration != true)
                        return;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        Log("[MainWindow] Deferred Kommo init retry after network error...");
                        InitializeAmoCrmService(settings);
                    });
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer retry or app shutdown.
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                        Log($"[MainWindow] Deferred Kommo init retry failed: {ex.Message}"));
                }
            }, token);
        }

        private static DateTime? ParseKommoExpiresAt(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
            return null;
        }

        private async Task InitializeAmoCrmServiceOnStartup()
        {
            try
            {
                // Небольшая задержка, чтобы настройки точно были загружены
                await Task.Delay(500);
                
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (settings != null)
                    {
                        Log($"[MainWindow] Loading AmoCRM settings on startup: EnableIntegration={settings.EnableAmoCrmIntegration}, AuthMode={settings.AmoCrmAuthMode}, HasOAuthToken={!string.IsNullOrEmpty(settings.AmoCrmOAuthAccessTokenEncrypted)}, HasManualToken={!string.IsNullOrEmpty(settings.AmoCrmAccessTokenEncrypted)}");

                        // When PBX Gateway is configured, Kommo init runs from InitializeMikoPbxCdrService only.
                        if (!IsPbxGatewayCdrConfigured(settings))
                            InitializeAmoCrmService(settings);
                        
                        // Проверяем результат инициализации через небольшую задержку
                        // (инициализация происходит асинхронно в Task.Run)
                        await Task.Delay(2000);
                        
                        if (settings.EnableAmoCrmIntegration && !IsAmoCrmServiceInitialized())
                        {
                            string authMode = settings.AmoCrmAuthMode ?? "manual";
                            Log($"[MainWindow] Kommo integration enabled but service not initialized after startup. AuthMode={authMode}");
                        }
                    }
                }
                else
                {
                    Log("[MainWindow] Settings file not found, skipping AmoCRM initialization");
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error initializing AmoCRM service on startup: {ex.Message}");
            }
        }

        /// <summary>
        /// Инициализирует AmoCRM сервис на основе настроек (асинхронно, не блокирует UI)
        /// </summary>
        public void InitializeAmoCrmService(AppSettings settings)
        {
            // Запускаем инициализацию асинхронно, чтобы не блокировать UI
            // КРИТИЧНО: Используем InvokeAsync (не Invoke!) чтобы не вызвать deadlock,
            // если UI поток занят модальным диалогом (например, OAuth success message).
            _ = Task.Run(async () =>
            {
                await _amoCrmInitSemaphore.WaitAsync().ConfigureAwait(false);
                var warningShownThisInit = false;
                async Task ShowKommoWarningIfNeededAsync(string authMode, KommoInitFailureKind failureKind, string? errorDetails = null)
                {
                    if (warningShownThisInit)
                        return;

                    if (failureKind == KommoInitFailureKind.Network)
                    {
                        await Dispatcher.InvokeAsync(() =>
                            Log($"[MainWindow] Kommo temporarily unreachable (network). No popup. {errorDetails}"));
                        ScheduleDeferredKommoInitRetry();
                        return;
                    }

                    warningShownThisInit = true;
                    await Dispatcher.InvokeAsync(() => ShowKommoIntegrationWarning(authMode, failureKind, errorDetails)).Task.ConfigureAwait(false);
                }

                try
                {
                    if (!settings.EnableAmoCrmIntegration)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            Log("[MainWindow] Kommo integration disabled in settings — disconnecting service");
                            DisconnectAmoCrmService();
                        }).Task.ConfigureAwait(false);
                        return;
                    }

                    bool gatewayCdrConfigured = IsPbxGatewayCdrConfigured(settings);
                    var savedSource = ResolveKommoConnectionSource(settings);
                    bool excludedFromGatewayKommo = await IsCurrentUserExcludedFromGatewayKommoAsync().ConfigureAwait(false);

                    if (excludedFromGatewayKommo)
                    {
                        savedSource = "local";
                        _amoCrmConnectionSource = "local";
                        if (LoadKommoConnectionSourceFromDisk() != "local")
                            SaveAmoCrmConnectionSourceSetting("local");
                        await Dispatcher.InvokeAsync(() =>
                            Log("[MainWindow] Gateway Kommo excluded for this extension — local-only (no shared Kommo)"));
                    }

                    // User may switch to local while a gateway init was queued — always prefer latest disk value.
                    if (!excludedFromGatewayKommo
                        && savedSource == "gateway"
                        && LoadKommoConnectionSourceFromDisk() == "local")
                        savedSource = "local";

                    // Local mode: skip gateway entirely (even if PBX Gateway CDR is configured).
                    if (savedSource == "local")
                    {
                        _amoCrmConnectionSource = "local";
                    }
                    else if (savedSource == "gateway")
                    {
                        _amoCrmConnectionSource = "gateway";
                        if (gatewayCdrConfigured && _mikoPbxCdrService == null)
                        {
                            await Dispatcher.InvokeAsync(() =>
                                Log("[MainWindow] Deferring gateway Kommo init until PBX Gateway client is ready"));
                            return;
                        }

                        await Dispatcher.InvokeAsync(() =>
                            Log("[MainWindow] Initializing Kommo via PBX Gateway (shared company account)"));

                        try
                        {
                            var (gatewayOk, gatewayError) = await InitializeAmoCrmFromGatewayWithRetryAsync().ConfigureAwait(false);
                            if (gatewayOk)
                            {
                                ResetKommoIntegrationWarningShown();
                                _kommoDeferredRetryCts?.Cancel();
                                await RefreshCachedKommoGatewayStatusAsync().ConfigureAwait(false);
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    Log("[MainWindow] Kommo gateway initialization completed successfully");
                                    UpdateConnectionStatus();
                                });
                            }
                            else
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    Log($"[MainWindow] Kommo gateway session unavailable: {gatewayError}");
                                    DisconnectAmoCrmService();
                                });
                                await ShowKommoWarningIfNeededAsync("gateway",
                                    KommoInitHelper.ClassifyFailureMessage(gatewayError), gatewayError).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                Log($"[MainWindow] Kommo gateway init error: {ex.Message}");
                                DisconnectAmoCrmService();
                            });
                            await ShowKommoWarningIfNeededAsync("gateway",
                                KommoInitHelper.ClassifyFailure(ex), ex.Message).ConfigureAwait(false);
                        }
                        return;
                    }

                    if (gatewayCdrConfigured && _mikoPbxCdrService == null && savedSource != "local")
                    {
                        await Dispatcher.InvokeAsync(() =>
                            Log("[MainWindow] Deferring Kommo init until PBX Gateway client is ready"));
                        return;
                    }

                    bool localConfigured = IsLocalKommoConfigured(settings);
                    bool gatewayAvailable = await IsGatewayKommoAvailableAsync().ConfigureAwait(false);
                    bool gatewayOffered = await IsGatewayKommoOfferedToUserAsync().ConfigureAwait(false);

                    string? source = savedSource;
                    if (string.IsNullOrEmpty(source))
                    {
                        // Shared gateway is the company default when offered — never for excluded extensions.
                        if (!excludedFromGatewayKommo && gatewayOffered && gatewayAvailable)
                        {
                            source = "gateway";
                            SaveAmoCrmConnectionSourceSetting(source);
                            await Dispatcher.InvokeAsync(() =>
                                Log("[MainWindow] Kommo: auto-selected PBX Gateway (shared account). Switch to Local in Settings if needed."));
                        }
                        else if (localConfigured)
                        {
                            source = "local";
                        }
                        else if (excludedFromGatewayKommo)
                        {
                            source = "local";
                        }
                    }

                    // Excluded users must never use gateway Kommo, even if a stale setting says gateway.
                    if (excludedFromGatewayKommo && source == "gateway")
                    {
                        source = "local";
                        SaveAmoCrmConnectionSourceSetting("local");
                    }

                    _amoCrmConnectionSource = source;

                    if (string.IsNullOrEmpty(source))
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            Log("[MainWindow] Kommo not initialized — no gateway session and no local configuration");
                            DisconnectAmoCrmService();
                        });
                        return;
                    }

                    if (source == "gateway")
                    {
                        await Dispatcher.InvokeAsync(() =>
                            Log("[MainWindow] Initializing Kommo via PBX Gateway (shared company account)"));

                        try
                        {
                            var (gatewayOk, gatewayError) = await InitializeAmoCrmFromGatewayWithRetryAsync().ConfigureAwait(false);
                            if (gatewayOk)
                            {
                                ResetKommoIntegrationWarningShown();
                                _kommoDeferredRetryCts?.Cancel();
                                await RefreshCachedKommoGatewayStatusAsync().ConfigureAwait(false);
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    Log("[MainWindow] Kommo gateway initialization completed successfully");
                                    UpdateConnectionStatus();
                                });
                            }
                            else
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    Log($"[MainWindow] Kommo gateway session unavailable: {gatewayError}");
                                    DisconnectAmoCrmService();
                                });
                                await ShowKommoWarningIfNeededAsync("gateway",
                                    KommoInitHelper.ClassifyFailureMessage(gatewayError), gatewayError).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                Log($"[MainWindow] Kommo gateway init error: {ex.Message}");
                                DisconnectAmoCrmService();
                            });
                            await ShowKommoWarningIfNeededAsync("gateway",
                                KommoInitHelper.ClassifyFailure(ex), ex.Message).ConfigureAwait(false);
                        }
                        return;
                    }

                    if (source != "local")
                    {
                        await Dispatcher.InvokeAsync(() => DisconnectAmoCrmService());
                        return;
                    }

                    // Local Kommo only — gateway mode must never reach this block.
                    if (settings.EnableAmoCrmIntegration && !string.IsNullOrEmpty(settings.AmoCrmSubdomain))
                    {
                        string authMode = settings.AmoCrmAuthMode ?? "manual";
                        bool initialized = false;
                        
                        await Dispatcher.InvokeAsync(() =>
                        {
                            Log($"[MainWindow] Initializing AmoCRM service: EnableIntegration={settings.EnableAmoCrmIntegration}, Subdomain={settings.AmoCrmSubdomain}, AuthMode={authMode}");
                        });
                        
                        // КРИТИЧНО: Подключаем только выбранный режим, другой режим игнорируем
                        if (authMode == "oauth")
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                Log($"[MainWindow] OAuth mode detected. Checking tokens: HasAccessToken={!string.IsNullOrEmpty(settings.AmoCrmOAuthAccessTokenEncrypted)}, HasRefreshToken={!string.IsNullOrEmpty(settings.AmoCrmOAuthRefreshTokenEncrypted)}, HasClientId={!string.IsNullOrEmpty(settings.AmoCrmClientId)}, HasClientSecret={!string.IsNullOrEmpty(settings.AmoCrmClientSecretEncrypted)}");
                            });
                            
                            // OAuth режим - подключаем только если есть OAuth токены
                            // Игнорируем Manual Token токены даже если они заполнены
                            if (!string.IsNullOrEmpty(settings.AmoCrmOAuthAccessTokenEncrypted))
                            {
                                try
                                {
                                    string accessToken = TokenEncryption.Decrypt(settings.AmoCrmOAuthAccessTokenEncrypted);
                                    string? refreshToken = null;
                                    if (!string.IsNullOrEmpty(settings.AmoCrmOAuthRefreshTokenEncrypted))
                                    {
                                        refreshToken = TokenEncryption.Decrypt(settings.AmoCrmOAuthRefreshTokenEncrypted);
                                    }
                                    string? clientId = settings.AmoCrmClientId;
                                    string? clientSecret = null;
                                    if (!string.IsNullOrEmpty(settings.AmoCrmClientSecretEncrypted))
                                    {
                                        clientSecret = TokenEncryption.Decrypt(settings.AmoCrmClientSecretEncrypted);
                                    }
                                    
                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        Log($"[MainWindow] Tokens decrypted: AccessToken length={accessToken?.Length ?? 0}, RefreshToken length={refreshToken?.Length ?? 0}, ClientId={clientId}, ClientSecret length={clientSecret?.Length ?? 0}");
                                    });
                                    
                                    if (!string.IsNullOrEmpty(accessToken))
                                    {
                                        // Отключаем предыдущий сервис если был
                                        if (_amoCrmService != null)
                                        {
                                            _amoCrmService.Dispose();
                                            _amoCrmService = null;
                                        }
                                        
                                        _amoCrmService = new AmoCrmService();
                                        
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            Log($"[MainWindow] Calling InitializeOAuthAsync with subdomain={settings.AmoCrmSubdomain}, expiresAt={settings.AmoCrmOAuthTokenExpiresAt}");
                                        });
                                        
                                        // Инициализируем с OAuth токенами (retry on transient network errors)
                                        await KommoInitHelper.ExecuteWithNetworkRetryAsync(
                                            () => _amoCrmService!.InitializeOAuthAsync(
                                                settings.AmoCrmSubdomain,
                                                accessToken,
                                                refreshToken,
                                                settings.AmoCrmOAuthTokenExpiresAt,
                                                clientId,
                                                clientSecret,
                                                settings.AmoCrmRedirectUri),
                                            msg => Dispatcher.Invoke(() => Log($"[MainWindow] {msg}")));
                                        
                                        initialized = true;
                                        
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            Log("[MainWindow] OAuth initialization completed successfully");
                                        });
                                    }
                                    else
                                    {
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            Log("[MainWindow] Access token is empty after decryption");
                                        });
                                    }
                                }
                                catch (UnauthorizedAccessException ex)
                                {
                                    // КРИТИЧНО: Токен недействителен - показываем предупреждение пользователю
                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        Log($"[MainWindow] OAuth token is invalid or expired: {ex.Message}");
                                        Log($"[MainWindow] Stack trace: {ex.StackTrace}");
                                        DisconnectAmoCrmService();
                                    });
                                    await ShowKommoWarningIfNeededAsync("oauth", KommoInitFailureKind.Auth,
                                        "Token is invalid or expired. Please re-authorize the application.").ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        Log($"[MainWindow] Error decrypting or initializing OAuth tokens: {ex.Message}");
                                        Log($"[MainWindow] Stack trace: {ex.StackTrace}");
                                        DisconnectAmoCrmService();
                                    });
                                    await ShowKommoWarningIfNeededAsync("oauth",
                                        KommoInitHelper.ClassifyFailure(ex), ex.Message).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    Log("[MainWindow] OAuth access token not found in settings");
                                });
                            }
                        }
                        else
                        {
                            // Manual Token режим - подключаем только если есть Manual Token
                            // Игнорируем OAuth токены даже если они заполнены
                            if (!string.IsNullOrEmpty(settings.AmoCrmAccessTokenEncrypted))
                            {
                                string accessToken = TokenEncryption.Decrypt(settings.AmoCrmAccessTokenEncrypted);
                                
                                if (!string.IsNullOrEmpty(accessToken))
                                {
                                    // Отключаем предыдущий сервис если был
                                    if (_amoCrmService != null)
                                    {
                                        _amoCrmService.Dispose();
                                        _amoCrmService = null;
                                    }
                                    
                                    _amoCrmService = new AmoCrmService();
                                    
                                    try
                                    {
                                        // Инициализируем асинхронно
                                        await KommoInitHelper.ExecuteWithNetworkRetryAsync(
                                            () => _amoCrmService!.InitializeAsync(settings.AmoCrmSubdomain, accessToken),
                                            msg => Dispatcher.Invoke(() => Log($"[MainWindow] {msg}")));
                                        
                                        initialized = true;
                                    }
                                    catch (UnauthorizedAccessException ex)
                                    {
                                        // КРИТИЧНО: Токен недействителен - показываем предупреждение пользователю
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            Log($"[MainWindow] Manual token is invalid or expired: {ex.Message}");
                                            DisconnectAmoCrmService();
                                        });
                                        await ShowKommoWarningIfNeededAsync("manual", KommoInitFailureKind.Auth,
                                            "Token is invalid or expired. Please check your access token in settings.").ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            Log($"[MainWindow] Error initializing Manual token: {ex.Message}");
                                            Log($"[MainWindow] Stack trace: {ex.StackTrace}");
                                            DisconnectAmoCrmService();
                                        });
                                        await ShowKommoWarningIfNeededAsync("manual",
                                            KommoInitHelper.ClassifyFailure(ex), ex.Message).ConfigureAwait(false);
                                    }
                                }
                            }
                        }
                        
                        if (initialized)
                        {
                            ResetKommoIntegrationWarningShown();
                            _kommoDeferredRetryCts?.Cancel();
                            // Логируем в UI потоке и обновляем статус в настройках
                            await Dispatcher.InvokeAsync(() =>
                            {
                                Log($"[MainWindow] AmoCRM service initialized for subdomain: {settings.AmoCrmSubdomain} (mode: {authMode})");
                                Log($"[MainWindow] IsAmoCrmServiceInitialized check: {IsAmoCrmServiceInitialized()}");
                                
                                // Обновляем статус в окне настроек, если оно открыто
                                var settingsWindow = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
                                if (settingsWindow != null)
                                {
                                    Log("[MainWindow] SettingsWindow found, updating status");
                                    // Для OAuth показываем "Authorized", для Manual Token - "Connected"
                                    string statusText = authMode == "oauth" ? "Authorized" : "Connected";
                                    settingsWindow.UpdateAmoCrmStatus(statusText, true);
                                }
                                else
                                {
                                    Log("[MainWindow] SettingsWindow not found, status will be updated when window opens");
                                }
                                
                                // Обновляем статусы в главном окне
                                UpdateConnectionStatus();
                            });
                        }
                        else
                        {
                            // Интеграция включена, но инициализация не удалась - показываем предупреждение
                            if (authMode == "oauth")
                            {
                                Log("[MainWindow] OAuth initialization failed: token not found or decryption failed");
                            }
                            else
                            {
                                Log("[MainWindow] Manual token initialization failed: token not found or decryption failed");
                            }

                            await ShowKommoWarningIfNeededAsync(authMode, KommoInitFailureKind.Other,
                                "Kommo credentials were not found or could not be decrypted.").ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            Log($"[MainWindow] AmoCRM integration not enabled or subdomain missing: EnableIntegration={settings.EnableAmoCrmIntegration}, Subdomain={settings.AmoCrmSubdomain}");
                        });
                        
                        // Отключаем сервис, если интеграция выключена
                        DisconnectAmoCrmService();
                    }
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        Log($"[MainWindow] Error initializing AmoCRM service: {ex.Message}");
                        Log($"[MainWindow] Stack trace: {ex.StackTrace}");
                        
                        // Обновляем статус в окне настроек, если оно открыто
                        var settingsWindow = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
                        if (settingsWindow != null)
                        {
                            settingsWindow.UpdateAmoCrmStatus($"Error: {ex.Message}", false);
                        }
                    });

                    if (settings.EnableAmoCrmIntegration || _amoCrmConnectionSource == "gateway")
                    {
                        string authMode = _amoCrmConnectionSource == "gateway"
                            ? "gateway"
                            : (settings.AmoCrmAuthMode ?? "manual");
                        await ShowKommoWarningIfNeededAsync(authMode,
                            KommoInitHelper.ClassifyFailure(ex), ex.Message).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _amoCrmInitSemaphore.Release();
                }
            });
        }
        
        private void ResetKommoIntegrationWarningShown()
        {
            lock (_kommoWarningLock)
            {
                _kommoIntegrationWarningShown = false;
            }
        }

        /// <summary>
        /// Показывает предупреждение о проблеме с интеграцией Kommo
        /// </summary>
        private void ShowKommoIntegrationWarning(string authMode, KommoInitFailureKind failureKind, string? errorDetails = null)
        {
            lock (_kommoWarningLock)
            {
                if (_kommoIntegrationWarningShown || _kommoIntegrationWarningShowing)
                {
                    Log("[MainWindow] Kommo integration warning skipped (already shown or showing)");
                    return;
                }
                _kommoIntegrationWarningShowing = true;
            }

            try
            {
                if (_amoCrmConnectionSource == "gateway" && authMode != "gateway")
                    authMode = "gateway";

                string message;
                if (authMode == "gateway")
                {
                    message = "Kommo via PBX Gateway failed.\n\n";
                    message += "The gateway returned a token, but Kommo rejected API access (401 Unauthorized).\n";
                    message += "OAuth on the gateway can succeed while API calls still fail — usually the gateway integration ";
                    message += "(Client ID dbdc7f97-...) lacks «Access to account data» or is not fully installed in mdkb.\n\n";
                    message += "Your Local Kommo OAuth (different integration) may still work — switch to Local in Settings → Integrations as a workaround.\n\n";
                    message += "Admin: Kommo → Integrations → gateway app → verify permissions → Disconnect + re-Authorize in gateway admin.\n\n";
                    if (!string.IsNullOrEmpty(errorDetails))
                    {
                        message += $"Error details: {errorDetails}\n\n";
                    }
                }
                else if (failureKind == KommoInitFailureKind.Auth && authMode == "oauth")
                {
                    message = "Kommo authorization failed.\n\n";
                    message += "Your OAuth session is invalid or expired.\n";
                    message += "Open Settings → Integrations and authorize Kommo again.\n\n";
                    if (!string.IsNullOrEmpty(errorDetails))
                    {
                        message += $"Details: {errorDetails}\n\n";
                    }
                    message += "Click OK to open integration settings.";
                }
                else if (authMode == "oauth")
                {
                    message = "Kommo integration is enabled, but connection failed.\n\n";
                    message += "Please check your OAuth settings:\n";
                    message += "• Client ID and Client Secret\n";
                    message += "• Redirect URI\n";
                    message += "• Subdomain\n\n";
                    if (!string.IsNullOrEmpty(errorDetails))
                    {
                        message += $"Error details: {errorDetails}\n\n";
                    }
                    message += "Click OK to open integration settings.";
                }
                else if (failureKind == KommoInitFailureKind.Auth)
                {
                    message = "Kommo access token is invalid or expired.\n\n";
                    message += "Update the token in Settings → Integrations.\n\n";
                    if (!string.IsNullOrEmpty(errorDetails))
                    {
                        message += $"Details: {errorDetails}\n\n";
                    }
                    message += "Click OK to open integration settings.";
                }
                else
                {
                    message = "Kommo integration is enabled, but connection failed.\n\n";
                    message += "Please check your Kommo settings:\n";
                    message += "• Subdomain\n";
                    message += "• Access Token\n\n";
                    if (!string.IsNullOrEmpty(errorDetails))
                    {
                        message += $"Error details: {errorDetails}\n\n";
                    }
                    message += "Click OK to open integration settings.";
                }
                
                var result = CustomMessageBox.Show(
                    message,
                    "Kommo Integration Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this
                );
                
                // Если пользователь нажал OK, открываем окно настроек на вкладке Integrations
                if (result == MessageBoxResult.OK)
                {
                    // Открываем окно настроек
                    var settingsWindow = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
                    if (settingsWindow == null)
                    {
                        OpenSettingsWindow();
                        settingsWindow = Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault();
                    }
                    else
                    {
                        settingsWindow.Show();
                    }
                    settingsWindow.Activate();
                    
                    // Находим и нажимаем кнопку Integrations программно
                    var integrationsButton = settingsWindow.FindName("IntegrationsButton") as System.Windows.Controls.Button;
                    if (integrationsButton != null)
                    {
                        integrationsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error showing Kommo integration warning: {ex.Message}");
            }
            finally
            {
                lock (_kommoWarningLock)
                {
                    _kommoIntegrationWarningShown = true;
                    _kommoIntegrationWarningShowing = false;
                }
            }
        }

        /// <summary>
        /// Отключает AmoCRM сервис
        /// </summary>
        public void DisconnectAmoCrmService()
        {
            if (_amoCrmService != null)
            {
                _amoCrmService.Dispose();
                _amoCrmService = null;
                Log("[MainWindow] AmoCRM service disconnected");
                // Обновляем статусы в главном окне
                UpdateConnectionStatus();
            }
        }

        /// <summary>
        /// Получает имя контакта из AmoCRM по номеру телефона
        /// </summary>
        public async Task<string?> GetAmoCrmContactNameAsync(string phoneNumber)
        {
            if (_amoCrmService == null || !_amoCrmService.IsInitialized)
            {
                return null;
            }
            
            try
            {
                return await _amoCrmService.GetContactNameByPhoneAsync(phoneNumber);
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] Error getting AmoCRM contact name: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Обрабатывает завершенный звонок в AmoCRM с дедупликацией для предотвращения множественных записей
        /// </summary>
        private void ProcessCallInAmoCrm(string phoneNumber, DateTime callTime, List<string>? technicalDetails, string? recordingFilePath, string? sessionId = null, long? leadId = null)
        {
            Log($"[MainWindow] ProcessCallInAmoCrm called: phone={phoneNumber}, leadId={leadId?.ToString() ?? "null"}, sessionId={sessionId ?? "null"}");
            if (!IsAmoCrmIntegrationEnabled() || _amoCrmService == null || !_amoCrmService.IsInitialized)
            {
                return; // Интеграция не включена или не инициализирована
            }

            // PBX Originate callback leg arrives as inbound INVITE From=our trunk CallerID — not a real client call.
            if (IsOwnOutboundCallerId(phoneNumber))
            {
                Log($"[MainWindow] AmoCRM: Skipping originate callback leg (own CallerID {phoneNumber})");
                return;
            }

            // КРИТИЧНО: Создаем уникальный ключ для дедупликации
            // Если есть sessionId - используем его как основной идентификатор (без callTime),
            // т.к. sessionId уникален для каждого звонка и не меняется между вызовами SendCallDetails.
            // Если sessionId нет - используем phoneNumber + callTime (до секунды) для старых звонков/SIP.
            // Это предотвращает создание дубликатов при множественных вызовах SendCallDetails с разными CallTime.
            string dedupKey = string.IsNullOrEmpty(sessionId) 
                ? $"{phoneNumber}_{callTime:yyyy-MM-dd HH:mm:ss}"
                : $"{phoneNumber}_{sessionId}";
            
            // Проверяем, не обрабатывался ли уже этот звонок
            // КРИТИЧНО: Проверяем наличие в словаре БЕЗ учета времени - если звонок уже был добавлен в очередь, пропускаем
            // Это предотвращает создание дубликатов даже если между вызовами прошло много времени
            if (_processedCalls.ContainsKey(dedupKey))
            {
                Log($"[MainWindow] AmoCRM: Call {dedupKey} already in processing queue, skipping duplicate");
                return;
            }
            
            // Отмечаем звонок как обрабатываемый (добавляем в словарь с текущим временем)
            _processedCalls.AddOrUpdate(dedupKey, DateTime.Now, (key, oldValue) => DateTime.Now);
            
            // Очищаем старые записи из словаря (старше 1 часа) для предотвращения утечки памяти
            var cutoffTime = DateTime.Now.AddHours(-1);
            var keysToRemove = _processedCalls.Where(kvp => kvp.Value < cutoffTime).Select(kvp => kvp.Key).ToList();
            foreach (var key in keysToRemove)
            {
                _processedCalls.TryRemove(key, out _);
            }

                // Ставим в очередь: воркер обработает по одному, подождёт файл до 5 минут (колл-центр: позвонил → следующий)
            try
            {
                Log($"[MainWindow] Adding AmoCrmJob to queue: phone={phoneNumber}, leadId={leadId?.ToString() ?? "null"}, dedupKey={dedupKey}");
                _amoCrmQueue.Add(new AmoCrmJob
                {
                    PhoneNumber = phoneNumber,
                    CallTime = callTime,
                    TechnicalDetails = technicalDetails,
                    RecordingFilePath = recordingFilePath,
                    DedupKey = dedupKey,
                    SessionId = sessionId,
                    AmoCrmLeadId = leadId // ID лида из браузера, если звонок инициирован из AmoCRM
                });
                Log($"[MainWindow] AmoCrmJob added successfully with leadId={leadId?.ToString() ?? "null"}");
            }
            catch (InvalidOperationException)
            {
                // Очередь завершена (приложение закрывается)
            }
        }

        /// <summary>Обрабатывает очередь AmoCRM параллельно: ждёт файл записи до 5 минут, затем отправляет в AmoCRM. Запускается в нескольких экземплярах для параллельной обработки.</summary>
        private async Task AmoCrmWorkerAsync()
        {
            while (true)
            {
                AmoCrmJob job;
                try
                {
                    job = _amoCrmQueue.Take();
                }
                catch (InvalidOperationException)
                {
                    break; // Очередь завершена
                }

                if (!IsAmoCrmIntegrationEnabled() || _amoCrmService == null || !_amoCrmService.IsInitialized)
                {
                    _processedCalls.TryRemove(job.DedupKey, out _);
                    continue;
                }

                try
                {
                    CallHistoryItem? call = _callHistoryService.GetCallForAmoCrmUpload(job.PhoneNumber, job.CallTime, job.SessionId);
                    if (call == null)
                        Log($"[MainWindow] AmoCrmWorkerAsync: no history row for {job.PhoneNumber} near {job.CallTime:HH:mm:ss.fff} (sessionId={job.SessionId ?? "none"})");

                    bool isIncoming = call?.IsIncoming ?? false;
                    int durationSeconds = call?.Duration?.TotalSeconds != null ? (int)call.Duration.Value.TotalSeconds : 0;
                    // КРИТИЧНО: Если есть AnswerTime, значит произошло подключение (connect) - 
                    // либо абонент ответил, либо IVR/робот ответил. В этом случае считаем звонок принятым.
                    bool wasAnswered = call?.WasAnswered ?? false;
                    if (call?.AnswerTime.HasValue == true)
                    {
                        wasAnswered = true; // Если есть AnswerTime, значит был connect
                    }
                    // Логируем для диагностики
                    Log($"[MainWindow] AmoCrmWorkerAsync: call?.WasAnswered={call?.WasAnswered}, call?.AnswerTime.HasValue={call?.AnswerTime.HasValue}, final wasAnswered={wasAnswered}, duration={durationSeconds}s");
                    var endedBy = call?.EndedBy ?? CallEndedBy.Unknown;
                    var ringbackStart = call?.RingbackStartTime;
                    var ringbackEnd = call?.RingbackEndTime;

                    // SIP: history may be updated before hangup (e.g. false 480 in timestamp). Wait until call ends.
                    if (call?.Transport == CallTransport.Sip)
                    {
                        SipService? activeSip = call.ConnectionSlot == CallConnectionSlot.Secondary
                            ? _sipService2
                            : _sipService;
                        for (int wait = 0;
                             wait < 300
                             && activeSip?.IsInCall == true
                             && string.Equals(activeSip.ActiveDialNumber, job.PhoneNumber, StringComparison.OrdinalIgnoreCase);
                             wait++)
                        {
                            if (wait == 0)
                                Log($"[MainWindow] AmoCRM: SIP call still active for {job.PhoneNumber}, waiting for hangup before upload...");
                            await Task.Delay(1000).ConfigureAwait(false);
                            call = _callHistoryService.GetCallForAmoCrmUpload(job.PhoneNumber, job.CallTime, job.SessionId) ?? call;
                            durationSeconds = call?.Duration?.TotalSeconds != null ? (int)call.Duration.Value.TotalSeconds : durationSeconds;
                            endedBy = call?.EndedBy ?? endedBy;
                            ringbackStart = call?.RingbackStartTime ?? ringbackStart;
                            ringbackEnd = call?.RingbackEndTime ?? ringbackEnd;
                            if (call?.AnswerTime.HasValue == true)
                                wasAnswered = true;
                        }
                    }

                    string? callLog = job.TechnicalDetails != null && job.TechnicalDetails.Count > 0
                        ? string.Join("\n", job.TechnicalDetails)
                        : null;

                    string? finalRecordingPath = job.RecordingFilePath;

                    string? FindMatchingCallFromHistory()
                    {
                        return _callHistoryService.GetCallForAmoCrmUpload(job.PhoneNumber, job.CallTime, job.SessionId)
                            ?.RecordingFilePath;
                    }

                    // Защита от "пустых" исходящих звонков:
                    // Если исходящий звонок НЕ был принят, пользователь сам завершил его,
                    // и с момента начала вызова прошло <= 5 секунд, то пропускаем загрузку записи,
                    // НО все равно обрабатываем для создания заметки в лиде/контакте (если есть открытый лид).
                    // Исключение: если нет файла записи - пропускаем полностью только если нет открытого лида.
                    // Если есть открытый лид - обрабатываем для создания заметки "Не дозвонился".
                    if (!isIncoming && !wasAnswered && endedBy == CallEndedBy.LocalUser)
                    {
                        DateTime startTime = call?.CallTime ?? job.CallTime;
                        DateTime endApprox = ringbackEnd ?? startTime;
                        var totalSeconds = (endApprox - startTime).TotalSeconds;

                        if (totalSeconds > 0 && totalSeconds <= 5)
                        {
                            // Для коротких звонков без файла записи:
                            // - Если есть файл записи - обрабатываем всегда
                            // - Если нет файла записи - все равно обрабатываем (ProcessCallAsync сам проверит наличие открытого лида)
                            //   и создаст заметку "Не дозвонился" в лиде или контакте
                            if (string.IsNullOrEmpty(finalRecordingPath))
                            {
                                // Нет файла записи - обрабатываем для создания заметки, но без файла
                                Log($"[MainWindow] AmoCRM: Outgoing call cancelled within {totalSeconds:F1}s, processing for note creation (no recording file)");
                                finalRecordingPath = null; // Убеждаемся, что файл не будет загружаться
                            }
                            else
                            {
                                // Есть файл записи - обрабатываем нормально
                                Log($"[MainWindow] AmoCRM: Outgoing call cancelled within {totalSeconds:F1}s, but has recording file - processing normally");
                            }
                        }
                    }
                    // SIP only: ensure in-flight RTP finalize / orphan PCM recovery before waiting for WAV.
                    bool expectRecording = wasAnswered || !string.IsNullOrEmpty(job.RecordingFilePath);
                    string? pathToWait = !string.IsNullOrEmpty(job.RecordingFilePath)
                        ? job.RecordingFilePath
                        : call?.RecordingFilePath;

                    bool zeroRtpDeadMedia = call?.Transport == CallTransport.Sip
                        && call.InboundRtpPackets == 0
                        && (call.WasAnswered || call.AnswerTime.HasValue);
                    if (zeroRtpDeadMedia)
                    {
                        Log($"[MainWindow] AmoCRM: 0 inbound RTP — treating as missed call (SIP answered but no media)");
                        wasAnswered = false;
                        durationSeconds = 0;
                        expectRecording = false;
                        finalRecordingPath = null;
                        pathToWait = null;
                    }
                    else if (call != null
                        && expectRecording
                        && (string.IsNullOrEmpty(finalRecordingPath) || !File.Exists(finalRecordingPath)))
                    {
                        var pbxRecording = await TryDownloadPbxRecordingForCallAsync(call).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(pbxRecording) && File.Exists(pbxRecording))
                        {
                            finalRecordingPath = pbxRecording;
                            pathToWait = pbxRecording;
                            Log($"[MainWindow] AmoCRM: using PBX server recording: {pbxRecording}");
                        }
                    }

                    if (expectRecording && call?.Transport == CallTransport.Sip)
                    {
                        try
                        {
                            await (_sipService?.FinalizeCallRecordingIfActiveAsync(90000) ?? Task.CompletedTask).ConfigureAwait(false);
                            await (_sipService2?.FinalizeCallRecordingIfActiveAsync(90000) ?? Task.CompletedTask).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log($"[MainWindow] AmoCRM queue: SIP recording finalize: {ex.Message}");
                        }

                        call = _callHistoryService.GetCallForAmoCrmUpload(job.PhoneNumber, job.CallTime, job.SessionId) ?? call;
                        if (string.IsNullOrEmpty(pathToWait) && !string.IsNullOrEmpty(call?.RecordingFilePath))
                            pathToWait = call.RecordingFilePath;
                        if (!string.IsNullOrEmpty(pathToWait) && CallWindowHelpers.IsRecordingWavPlausible(pathToWait, out _))
                            finalRecordingPath = pathToWait;
                    }

                    // Ждём появления файла записи до 5 минут (колл-центр: конвертация идёт по одной, предыдущий успеет)
                    if (expectRecording && !string.IsNullOrEmpty(pathToWait)
                        && !CallWindowHelpers.IsRecordingWavPlausible(finalRecordingPath, out _))
                    {
                        const int maxWaitSeconds = 300; // 5 минут
                        // Для отвеченных звонков ждем минимум 60 секунд (даже если звонок был коротким),
                        // так как конвертация записи может занять время, особенно для звонков с роботом
                        int minWaitSeconds = wasAnswered ? 60 : 30;
                        int waitSeconds = Math.Max(minWaitSeconds, Math.Min(maxWaitSeconds, Math.Max(durationSeconds, 30)));
                        Log($"[MainWindow] AmoCRM queue: waiting for recording file (up to {waitSeconds}s, wasAnswered={wasAnswered}, duration={durationSeconds}s): {pathToWait}");
                        long lastStableSize = -1;
                        int stableObservations = 0;
                        for (int i = 0; i < waitSeconds; i++)
                        {
                            await Task.Delay(1000).ConfigureAwait(false);

                            if (CallWindowHelpers.ObserveRecordingWavStability(pathToWait, ref lastStableSize, ref stableObservations))
                            {
                                finalRecordingPath = pathToWait;
                                Log($"[MainWindow] AmoCRM queue: job path file ready after {i + 1}s ({lastStableSize / 1024} KB, stable)");
                                break;
                            }

                            var histPath = FindMatchingCallFromHistory();
                            if (CallWindowHelpers.ObserveRecordingWavStability(histPath, ref lastStableSize, ref stableObservations))
                            {
                                finalRecordingPath = histPath;
                                pathToWait = histPath;
                                Log($"[MainWindow] AmoCRM queue: file ready from history after {i + 1}s: {histPath} ({lastStableSize / 1024} KB, stable)");
                                break;
                            }
                        }
                        if (!CallWindowHelpers.IsRecordingWavPlausible(finalRecordingPath, out _))
                            Log($"[MainWindow] AmoCRM queue: file still not ready after {waitSeconds}s, sending without recording");
                    }
                    else if (expectRecording && string.IsNullOrEmpty(pathToWait))
                    {
                        const int historyPathWaitSeconds = 90;
                        Log($"[MainWindow] AmoCRM queue: answered call without recording path yet, polling history (up to {historyPathWaitSeconds}s)");
                        for (int i = 0; i < historyPathWaitSeconds; i++)
                        {
                            await Task.Delay(1000).ConfigureAwait(false);
                            var histPath = FindMatchingCallFromHistory();
                            long lastStableSize = -1;
                            int stableObservations = 0;
                            if (CallWindowHelpers.ObserveRecordingWavStability(histPath, ref lastStableSize, ref stableObservations))
                            {
                                finalRecordingPath = histPath;
                                pathToWait = histPath;
                                Log($"[MainWindow] AmoCRM queue: recording path from history after {i + 1}s: {histPath} ({lastStableSize / 1024} KB, stable)");
                                break;
                            }
                        }
                    }
                    
                    // КРИТИЧНО: Перечитываем историю после ожидания файла, чтобы получить актуальные данные
                    // (WasAnswered и AnswerTime могут быть обновлены после первого чтения)
                    call = _callHistoryService.GetCallForAmoCrmUpload(job.PhoneNumber, job.CallTime, job.SessionId);
                    
                    // Обновляем данные из актуальной истории
                    if (call != null)
                    {
                        isIncoming = call.IsIncoming;
                        durationSeconds = call.Duration?.TotalSeconds != null ? (int)call.Duration.Value.TotalSeconds : 0;
                        wasAnswered = call.WasAnswered;
                        if (call.AnswerTime.HasValue)
                        {
                            wasAnswered = true; // Если есть AnswerTime, значит был connect
                        }
                        if (call.Transport == CallTransport.Sip && call.InboundRtpPackets == 0
                            && (call.WasAnswered || call.AnswerTime.HasValue))
                        {
                            wasAnswered = false;
                            durationSeconds = 0;
                            Log("[MainWindow] AmoCrmWorkerAsync: re-read — 0 RTP, treating as missed call");
                        }
                        endedBy = call.EndedBy;
                        Log($"[MainWindow] AmoCrmWorkerAsync: After re-reading history - call?.WasAnswered={call.WasAnswered}, call?.AnswerTime.HasValue={call.AnswerTime.HasValue}, final wasAnswered={wasAnswered}, duration={durationSeconds}s");

                        if (!string.IsNullOrEmpty(call.RecordingFilePath))
                        {
                            finalRecordingPath = call.RecordingFilePath;
                        }
                    }

                    TimeSpan? ringbackSpan = null;
                    if (ringbackStart.HasValue && ringbackEnd.HasValue)
                        ringbackSpan = ringbackEnd.Value - ringbackStart.Value;
                    bool isOriginateJob = job.TechnicalDetails?.Any(d =>
                        d.IndexOf("Originate", StringComparison.OrdinalIgnoreCase) >= 0) == true;
                    CallWindowHelpers.ApplyRecordingMetrics(
                        ref durationSeconds,
                        ref wasAnswered,
                        finalRecordingPath,
                        endedBy,
                        ringbackSpan,
                        isOriginateCall: isOriginateJob);

                    // Failed / missed calls must not inherit talk time from a mismatched history row.
                    if (!wasAnswered && (string.IsNullOrEmpty(finalRecordingPath) || !File.Exists(finalRecordingPath)))
                        durationSeconds = 0;
                    if (wasAnswered && call != null && !call.WasAnswered)
                    {
                        Log($"[MainWindow] AmoCrmWorkerAsync: inferred wasAnswered=true from recording (duration={durationSeconds}s)");
                        var inferredAnswer = call.AnswerTime
                            ?? ringbackEnd
                            ?? job.CallTime.AddSeconds(Math.Max(3, ringbackSpan?.TotalSeconds ?? 3));
                        _callHistoryService.UpdateCallDetails(
                            job.PhoneNumber,
                            job.CallTime,
                            answerTime: inferredAnswer,
                            wasAnswered: true,
                            endedBy: endedBy,
                            duration: TimeSpan.FromSeconds(Math.Max(durationSeconds, 1)),
                            recordingFilePath: finalRecordingPath);
                    }

                    bool enableLeadSelection = false;
                    try
                    {
                        string settingsPath = AppDataHelper.GetSettingsFilePath();
                        if (File.Exists(settingsPath))
                        {
                            string json = File.ReadAllText(settingsPath);
                            var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                            enableLeadSelection = settings?.EnableAmoCrmLeadSelection ?? false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[MainWindow] Error loading lead selection setting: {ex.Message}");
                    }

                    // КРИТИЧНО: Проверяем статус загрузки в истории звонков перед обработкой
                    // Если звонок уже был успешно загружен, пропускаем обработку
                    if (call != null && call.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded)
                    {
                        Log($"[MainWindow] AmoCRM: Call {job.DedupKey} already uploaded (status: Uploaded), skipping duplicate");
                        _processedCalls.TryRemove(job.DedupKey, out _);
                        continue;
                    }
                    
                    // Используем leadId из браузера, если он был передан, иначе используем автоматический поиск
                    Log($"[MainWindow] AmoCrmWorkerAsync: Processing job with leadId={job.AmoCrmLeadId?.ToString() ?? "null"}, phone={job.PhoneNumber}");
                    string? callFromSubtitleForAmo = await ResolveCallFromSubtitleForAmoAsync(call, job.PhoneNumber, job.CallTime).ConfigureAwait(false);
                    ProcessCallResult result;
                    if (job.AmoCrmLeadId.HasValue)
                    {
                        // Звонок инициирован из браузера - используем указанный leadId
                        Log($"[MainWindow] AmoCRM: Using leadId from browser: {job.AmoCrmLeadId.Value}");
                        result = await _amoCrmService.ProcessCallForSpecificLeadAsync(
                            job.AmoCrmLeadId.Value,
                            job.PhoneNumber,
                            isIncoming,
                            durationSeconds,
                            wasAnswered,
                            callLog,
                            finalRecordingPath,
                            job.CallTime,
                            callFromSubtitleForAmo).ConfigureAwait(false);
                    }
                    else
                    {
                        // Обычный звонок - используем автоматический поиск лида
                        result = await _amoCrmService.ProcessCallAsync(job.PhoneNumber, isIncoming, durationSeconds, wasAnswered, callLog, finalRecordingPath, enableLeadSelection, job.CallTime, callFromSubtitleForAmo).ConfigureAwait(false);
                    }
                    
                    // Обновляем статус загрузки в истории звонков
                    if (call != null)
                    {
                        _callHistoryService.UpdateAmoCrmUploadStatus(job.PhoneNumber, call.CallTime, result.UploadStatus, result.Reason);
                        
                        try
                        {
                            Dispatcher.Invoke(RefreshVisibleDashboards);
                        }
                        catch { /* window may be closing */ }
                        
                        bool uploadSucceeded = result.Success
                            || result.UploadStatus == AmoCrmUploadStatus.Uploaded;

                        if (uploadSucceeded)
                        {
                            if (result.LeadId.HasValue)
                                _callHistoryService.UpdateAmoCrmLeadId(job.PhoneNumber, call.CallTime, result.LeadId.Value);

                            Log($"[MainWindow] AmoCRM: Successfully processed call for {job.PhoneNumber} (callTime: {call.CallTime:HH:mm:ss.fff}), Status: {result.UploadStatus}"
                                + (string.IsNullOrEmpty(result.Reason) ? "" : $", Reason: {result.Reason}"));
                            
                            // КРИТИЧНО: НЕ удаляем запись из словаря после успешной обработки
                            // Это предотвращает повторную обработку того же звонка при повторных вызовах SendCallDetails
                            // Запись будет удалена автоматически через 1 час при очистке старых записей
                        }
                        else
                        {
                            Log($"[MainWindow] AmoCRM: Failed to process call for {job.PhoneNumber}, Status: {result.UploadStatus}, Reason: {result.Reason}");

                            // Специальный кейс: контакт найден, но все лиды на других пользователях.
                            // Показываем пользователю уведомление и даём перейти в Call Details для ручной загрузки.
                            if (result.Reason == "Leads exist but none assigned to current user" && !isIncoming && !wasAnswered)
                            {
                                try
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        string message =
                                            "Contact was found in AmoCRM, but all leads are assigned to another user.\n\n" +
                                            "To upload the recording or create a missed call card, open Call Details and use the buttons there.";

                                        var mbResult = CustomMessageBox.Show(
                                            message,
                                            "AmoCRM Lead Not Assigned To You",
                                            MessageBoxButton.OKCancel,
                                            MessageBoxImage.Information,
                                            this);

                                        if (mbResult == MessageBoxResult.OK && call != null)
                                        {
                                            // Открываем Call Details для этого звонка
                                            var detailsWindow = new CallDetailsWindow(call);
                                            CenterNonOwnedWindowOverThis(detailsWindow);
                                            detailsWindow.Show();
                                        }
                                    });
                                }
                                catch (Exception ex)
                                {
                                    Log($"[MainWindow] Error showing 'lead not on current user' message: {ex.Message}");
                                }
                            }

                            // Удаляем из словаря только при ошибке, чтобы можно было повторить попытку
                            _processedCalls.TryRemove(job.DedupKey, out _);
                        }
                    }
                    else
                    {
                        // Если звонок не найден в истории, удаляем из словаря
                        _processedCalls.TryRemove(job.DedupKey, out _);
                    }
                }
                catch (Exception ex)
                {
                    Log($"[MainWindow] AmoCRM: Error processing call: {ex.Message}");
                    // Удаляем из словаря даже при ошибке, чтобы не блокировать повторную попытку через длительное время
                    _processedCalls.TryRemove(job.DedupKey, out _);
                }
            }
        }

        private sealed class AmoCrmJob
        {
            public string PhoneNumber { get; set; } = "";
            public DateTime CallTime { get; set; }
            public List<string>? TechnicalDetails { get; set; }
            public string? RecordingFilePath { get; set; }
            public string DedupKey { get; set; } = "";
            public string? SessionId { get; set; }
            public long? AmoCrmLeadId { get; set; }
        }

        // ===== Callspire PBX Gateway integration =====

        /// <summary>
        /// Redeems a one-time provisioning token issued by the admin panel
        /// (via <c>callspire://provision?token=...&amp;proxy=...</c>) and wires
        /// the returned SIP credentials into <c>settings.json</c>, then kicks
        /// SIP re-registration so the user is on the phone right away.
        ///
        /// The token is single-use on the proxy side — replays intentionally
        /// return 404. We never hand-roll the secret: MikoPBX sends it to us
        /// decrypted once, we persist it DPAPI-encrypted, done.
        /// </summary>
        public async Task HandleProvisionTokenAsync(string tokenId, string proxyUrl)
        {
            Log($"[Provision] Redeeming token (proxy={proxyUrl})");
            try
            {
                string baseUrl = (proxyUrl ?? "").Trim().TrimEnd('/');
                if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(tokenId))
                {
                    Log("[Provision] Missing proxy or token");
                    return;
                }

                using var handler = new System.Net.Http.HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                    SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                };
                using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

                string redeemUrl = $"{baseUrl}/api/provision/redeem?token={Uri.EscapeDataString(tokenId)}";
                var resp = await http.GetAsync(redeemUrl).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"[Provision] Redeem failed HTTP {(int)resp.StatusCode}: {json}");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show(
                            resp.StatusCode == System.Net.HttpStatusCode.NotFound
                                ? "This setup link is invalid, already used, or expired. Ask your administrator for a new one."
                                : $"Failed to fetch SIP credentials from the PBX.\n\nHTTP {(int)resp.StatusCode}",
                            "Callspire — setup link",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    });
                    return;
                }

                var payload = Newtonsoft.Json.Linq.JObject.Parse(json);
                string extension = (string?)payload["extension"] ?? "";
                string sipUser = (string?)payload["sip_username"] ?? extension;
                string sipPassword = (string?)payload["sip_password"] ?? "";
                string sipServer = (string?)payload["sip_server"] ?? "";
                string wsUrl = (string?)payload["ws_url"] ?? "";
                string returnedProxy = (string?)payload["proxy_url"] ?? "";

                if (string.IsNullOrEmpty(sipUser) || string.IsNullOrEmpty(sipPassword))
                {
                    Log("[Provision] Payload missing sip_username / sip_password");
                    return;
                }

                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                if (System.IO.File.Exists(settingsPath))
                {
                    try
                    {
                        settings = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(
                            System.IO.File.ReadAllText(settingsPath));
                    }
                    catch (Exception ex)
                    {
                        Log($"[Provision] settings.json parse error: {ex.Message} (starting fresh)");
                    }
                }
                settings ??= new AppSettings();

                settings.SipUsername = sipUser;
                settings.SipPasswordEncrypted = TokenEncryption.Encrypt(sipPassword);
                settings.SipPassword = null; // never persist plaintext
                if (!string.IsNullOrEmpty(sipServer))
                    settings.SipServer = sipServer;
                if (!string.IsNullOrEmpty(wsUrl))
                {
                    settings.WebRtcWsUri = wsUrl;
                    // Provisioning targets a cloud PBX via WSS. A fresh install
                    // has UseWebRtcAudio=false and would otherwise ignore the
                    // WebSocket we just wrote — flip the switch so the user is
                    // online immediately without opening Settings.
                    settings.UseWebRtcAudio = true;
                }

                // Wire up PBX Gateway (same JWT service) so the
                // softphone gets CDR, CallerIDs and originate for free — the
                // admin URL the user just clicked is also the proxy base URL.
                settings.MikoPbxExtension = extension;
                string cdrUrl = string.IsNullOrEmpty(returnedProxy) ? baseUrl : returnedProxy.TrimEnd('/');
                if (!string.IsNullOrEmpty(cdrUrl))
                {
                    settings.MikoPbxCdrServiceUrl = cdrUrl;
                    settings.EnableMikoPbxCdr = true;
                }

                string updated = Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(settingsPath, updated);
                Log($"[Provision] settings.json updated for ext={extension}, server={sipServer}");

                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        $"Callspire was configured automatically for extension {extension}.\n\n" +
                        $"SIP server: {(string.IsNullOrEmpty(sipServer) ? "(existing)" : sipServer)}\n" +
                        $"User: {sipUser}\n\n" +
                        "The softphone will reconnect now. Open Settings if you need to change anything.",
                        "Callspire — setup complete",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                // Re-initialize both SIP and CDR services so the user is on-line
                // immediately without manually restarting the app.
                try { InitializeMikoPbxCdrService(settings); } catch (Exception ex) { Log($"[Provision] CDR re-init: {ex.Message}"); }
                try { await RestartSipRegistrationAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log($"[Provision] SIP re-register: {ex.Message}"); }
                WindowForegroundHelper.RequestForCdrAuthCallback();
            }
            catch (Exception ex)
            {
                Log($"[Provision] HandleProvisionTokenAsync error: {ex}");
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        "Could not complete automatic setup.\n\n" + ex.Message,
                        "Callspire — setup link",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
            }
        }

        /// <summary>
        /// Kicks a SIP re-registration after <c>settings.json</c> changed.
        /// Safe to call from the UI thread. Reads the fresh settings from
        /// disk so we pick up whatever <c>HandleProvisionTokenAsync</c> just
        /// wrote, then reuses <c>ConnectWithSettings</c> (the same codepath
        /// startup uses) so behaviour stays identical.
        /// </summary>
        private async Task RestartSipRegistrationAsync()
        {
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                if (!System.IO.File.Exists(settingsPath))
                    return;
                var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(
                    System.IO.File.ReadAllText(settingsPath));
                if (settings == null) return;

                try { _sipService?.Dispose(); } catch { }
                _sipService = null;

                await ConnectWithSettings(settings).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[Provision] SIP re-register error: {ex.Message}");
            }
        }

        public void HandleCdrAuthToken(string token)
        {
            Log($"[PBX Gateway] Auth token received (length={token.Length})");
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                if (System.IO.File.Exists(settingsPath))
                {
                    string json = System.IO.File.ReadAllText(settingsPath);
                    settings = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(json);
                }
                settings ??= new AppSettings();

                settings.MikoPbxCdrTokenEncrypted = TokenEncryption.Encrypt(token);

                string updatedJson = Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(settingsPath, updatedJson);

                InitializeMikoPbxCdrService(settings);

                Dispatcher.Invoke(() =>
                {
                    var settingsWindow = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
                    settingsWindow?.RefreshMikoGatewayConnectionStatus();
                });

                WindowForegroundHelper.RequestForCdrAuthCallback();

                Log("[PBX Gateway] Token saved and service initialized");
            }
            catch (Exception ex)
            {
                Log($"[PBX Gateway] Error saving token: {ex.Message}");
            }
        }

        public void InitializeMikoPbxCdrService(AppSettings settings)
        {
            StopCallerIdListRetryTimer();

            if (!settings.EnableMikoPbxCdr
                || string.IsNullOrEmpty(settings.MikoPbxCdrServiceUrl)
                || string.IsNullOrEmpty(settings.MikoPbxCdrTokenEncrypted)
                || string.IsNullOrEmpty(settings.MikoPbxExtension))
            {
                return;
            }

            try
            {
                string token = TokenEncryption.Decrypt(settings.MikoPbxCdrTokenEncrypted);
                if (string.IsNullOrEmpty(token))
                {
                    Log("[PBX Gateway] Failed to decrypt token");
                    return;
                }

                _mikoPbxCdrService = new MikoPbxCdrService(
                    settings.MikoPbxCdrServiceUrl,
                    token,
                    settings.MikoPbxExtension);

                _mikoPbxExtension = settings.MikoPbxExtension;

                Log($"[PBX Gateway] Service initialized (url={settings.MikoPbxCdrServiceUrl}, ext={settings.MikoPbxExtension})");

                _ = LoadCallerIdsAsync(settings);
                // Also start the SIP auth-failure warning poll — piggybacking on
                // PBX Gateway means no extra credentials and no extra config.
                StartSipAuthFailurePolling();

                // Re-evaluate Kommo source now that gateway client is available (gateway-only or dual config).
                InitializeAmoCrmService(settings);
                _ = RefreshCachedKommoGatewayStatusAsync();
            }
            catch (Exception ex)
            {
                Log($"[PBX Gateway] Init error: {ex.Message}");
            }
        }

        public void DisableMikoPbxCdrService()
        {
            StopCallerIdListRetryTimer();
            _mikoPbxCdrService?.Dispose();
            _mikoPbxCdrService = null;
            _mikoPbxExtension = null;
            StopSipAuthFailurePolling();
            Log("[PBX Gateway] Service disabled");
        }

        private void StopCallerIdListRetryTimer()
        {
            _callerIdListRetryTimer?.Dispose();
            _callerIdListRetryTimer = null;
        }

        /// <summary>
        /// When the gateway is enabled but /api/my-callerids fails (offline, 401, etc.), poll until we get a successful HTTP response.
        /// </summary>
        private void StartCallerIdListRetryTimer()
        {
            try
            {
                if (_callerIdListRetryTimer != null) return;

                _callerIdListRetryTimer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        string settingsPath = AppDataHelper.GetSettingsFilePath();
                        if (!File.Exists(settingsPath)) return;
                        string json = File.ReadAllText(settingsPath);
                        var s = JsonConvert.DeserializeObject<AppSettings>(json);
                        if (s == null || !s.EnableMikoPbxCdr) return;
                        if (_mikoPbxCdrService == null) return;

                        _ = Task.Run(async () =>
                        {
                            try { await LoadCallerIdsAsync(s).ConfigureAwait(false); }
                            catch (Exception ex) { Log($"[CallerID] Retry load error: {ex.Message}"); }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log($"[CallerID] Retry timer: {ex.Message}");
                    }
                }, null, _callerIdRetryFirst, _callerIdRetryInterval);

                Log("[CallerID] Scheduled retries until PBX Gateway returns CallerID list");
            }
            catch (Exception ex)
            {
                Log($"[CallerID] StartCallerIdListRetryTimer: {ex.Message}");
            }
        }

        // ===== SIP auth-failure warning (early Fail2Ban detector) =====
        //
        // MikoPBX exposes /sip:getSipAuthFailureStats which lists every recent
        // rejected REGISTER attempt with username + source IP. We poll it over
        // PBX Gateway once a minute; if our extension shows up, we show a
        // banner in the dialer so the user knows to fix their creds BEFORE the
        // server's Fail2Ban bans their IP.

        private System.Threading.Timer? _sipAuthFailureTimer;
        private int _lastSipAuthFailureCount = 0;
        // TimeSpan.Zero = first tick fires immediately, so the user sees the
        // banner right after startup without waiting a full minute.
        private static readonly TimeSpan _sipAuthFailurePollFirst = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan _sipAuthFailurePollInterval = TimeSpan.FromMinutes(1);

        private void StartSipAuthFailurePolling()
        {
            StopSipAuthFailurePolling();
            _sipAuthFailureTimer = new System.Threading.Timer(async _ =>
            {
                try { await PollSipAuthFailuresAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log($"[SipAuthWarn] poll error: {ex.Message}"); }
            }, null, _sipAuthFailurePollFirst, _sipAuthFailurePollInterval);
            Log("[SipAuthWarn] Polling started (1 min interval)");
        }

        private void StopSipAuthFailurePolling()
        {
            _sipAuthFailureTimer?.Dispose();
            _sipAuthFailureTimer = null;
            _lastSipAuthFailureCount = 0;
            try { Dispatcher.Invoke(HideSipAuthFailureBanner); } catch { }
        }

        private async Task PollSipAuthFailuresAsync()
        {
            var svc = _mikoPbxCdrService;
            if (svc == null) return;
            var stats = await svc.GetSipAuthFailuresAsync().ConfigureAwait(false);
            if (!stats.Supported)
            {
                // REST disabled on the proxy — quietly give up, no point polling.
                StopSipAuthFailurePolling();
                return;
            }
            int count = stats.FailuresForExtension;
            if (count == _lastSipAuthFailureCount && count == 0) return;
            _lastSipAuthFailureCount = count;
            await Dispatcher.InvokeAsync(() =>
            {
                if (count <= 0) { HideSipAuthFailureBanner(); return; }
                ShowSipAuthFailureBanner(count);
            });
        }

        private void ShowSipAuthFailureBanner(int count)
        {
            try
            {
                if (SipAuthFailureBanner == null) return;
                SipAuthFailureBannerText.Text = count == 1
                    ? "1 recent SIP authentication failure detected for your extension. If you didn't just log in, someone may be trying your password — check settings before the PBX bans your IP."
                    : $"{count} recent SIP authentication failures detected for your extension. If you didn't just log in, someone may be trying your password — check settings before the PBX bans your IP.";
                SipAuthFailureBanner.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { Log($"[SipAuthWarn] banner show: {ex.Message}"); }
        }

        private void HideSipAuthFailureBanner()
        {
            try
            {
                if (SipAuthFailureBanner == null) return;
                SipAuthFailureBanner.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        public MikoPbxCdrService? GetMikoPbxCdrService() => _mikoPbxCdrService;

        /// <summary>PBX Gateway CDR extension: line-2 SIP username for Connection2, main MikoPBX ext otherwise.</summary>
        public string? GetPbxCdrExtensionForCall(CallHistoryItem? call)
        {
            if (call != null
                && CallStatisticsService.GetEffectiveConnectionSlot(call) == CallConnectionSlot.Secondary
                && !string.IsNullOrWhiteSpace(_sipUsername2))
                return _sipUsername2;
            return _mikoPbxExtension;
        }

        private async Task<string?> TryDownloadPbxRecordingForCallAsync(CallHistoryItem call)
        {
            if (_mikoPbxCdrService == null)
                return null;

            var ext = GetPbxCdrExtensionForCall(call);
            if (string.IsNullOrWhiteSpace(ext))
                return null;

            try
            {
                var records = await _mikoPbxCdrService.GetCdrAsync(
                    call.CallTime.AddHours(-2),
                    call.CallTime.AddHours(2),
                    dst: call.PhoneNumber,
                    limit: 50,
                    extensionOverride: ext).ConfigureAwait(false);

                CdrRecord? best = null;
                double bestDiff = double.MaxValue;
                foreach (var r in records)
                {
                    if (string.IsNullOrEmpty(r.Recording) || string.IsNullOrEmpty(r.LinkedId))
                        continue;
                    if (!DateTime.TryParse(r.Start, out var recTime))
                        continue;
                    double diff = Math.Abs((recTime - call.CallTime).TotalSeconds);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        best = r;
                    }
                }

                if (best == null)
                {
                    Log($"[MainWindow] PBX: no CDR row with recording for {call.PhoneNumber} (ext={ext})");
                    return null;
                }

                string destFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Callspire", "Recordings", "PBX");
                string? downloaded = await _mikoPbxCdrService.DownloadRecordingAsync(best.LinkedId, destFolder).ConfigureAwait(false);
                if (string.IsNullOrEmpty(downloaded) || !File.Exists(downloaded) || new FileInfo(downloaded).Length == 0)
                    return null;

                Log($"[MainWindow] PBX recording downloaded for {call.PhoneNumber} (ext={ext}): {downloaded}");
                _callHistoryService.UpdateRecordingFilePath(call.PhoneNumber, call.CallTime, downloaded);
                return downloaded;
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] PBX recording download failed (ext={ext}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Fetches the CallerID list from the proxy and populates the CallerIdComboBox.
        /// </summary>
        private async Task LoadCallerIdsAsync(AppSettings settings)
        {
            await _callerIdLoadSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_mikoPbxCdrService == null) return;

                var (items, requestOk) = await _mikoPbxCdrService.GetMyCallerIdItemsAsync().ConfigureAwait(false);

                if (requestOk)
                {
                    StopCallerIdListRetryTimer();
                    _callerIdItems = items;
                    _allowedCallerIds = _callerIdItems.Select(i => i.Number).ToList();
                    settings.CachedOutboundCallerIds = _allowedCallerIds.Count > 0 ? _allowedCallerIds : null;

                    Dispatcher.Invoke(() =>
                    {
                        CallerIdComboBox.Items.Clear();
                        CallerIdComboBox.DisplayMemberPath = "";

                        if (_allowedCallerIds.Count >= 2)
                        {
                            foreach (var item in _callerIdItems)
                                CallerIdComboBox.Items.Add(item);

                            var restored = _callerIdItems.FirstOrDefault(i => i.Number == settings.SelectedOutboundCallerId);
                            if (restored != null)
                                CallerIdComboBox.SelectedItem = restored;
                            else
                                CallerIdComboBox.SelectedIndex = 0;

                            CallerIdPanel.Visibility = Visibility.Visible;
                            Log($"[CallerID] Showing dropdown with {_allowedCallerIds.Count} CallerIDs");
                        }
                        else
                        {
                            CallerIdPanel.Visibility = Visibility.Collapsed;
                            if (_allowedCallerIds.Count == 1)
                                Log($"[CallerID] Single CallerID assigned: {_allowedCallerIds[0]} (auto-selected, no dropdown)");
                            else
                                Log("[CallerID] No CallerIDs assigned, using standard WebRTC calling");
                        }
                    });
                    return;
                }

                Log("[CallerID] Gateway did not return CallerID list (network, auth, or server error); using cache if any and retrying");

                if (settings.CachedOutboundCallerIds?.Count > 0)
                {
                    _allowedCallerIds = new List<string>(settings.CachedOutboundCallerIds);
                    _callerIdItems = _allowedCallerIds.Select(n => new CallerIdItem(n, "")).ToList();
                    Log($"[CallerID] Using {_allowedCallerIds.Count} cached CallerID(s) until gateway responds");
                    Dispatcher.Invoke(() =>
                    {
                        CallerIdComboBox.Items.Clear();
                        CallerIdComboBox.DisplayMemberPath = "";
                        if (_callerIdItems.Count >= 2)
                        {
                            foreach (var item in _callerIdItems)
                                CallerIdComboBox.Items.Add(item);
                            var restored = _callerIdItems.FirstOrDefault(i => i.Number == settings.SelectedOutboundCallerId);
                            if (restored != null)
                                CallerIdComboBox.SelectedItem = restored;
                            else
                                CallerIdComboBox.SelectedIndex = 0;
                            CallerIdPanel.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            CallerIdPanel.Visibility = Visibility.Collapsed;
                        }
                    });
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        CallerIdComboBox.Items.Clear();
                        CallerIdPanel.Visibility = Visibility.Collapsed;
                    });
                }

                if (settings.EnableMikoPbxCdr && _mikoPbxCdrService != null)
                    StartCallerIdListRetryTimer();
            }
            catch (Exception ex)
            {
                Log($"[CallerID] LoadCallerIdsAsync error: {ex.Message}");

                if (settings.CachedOutboundCallerIds?.Count > 0)
                {
                    _allowedCallerIds = new List<string>(settings.CachedOutboundCallerIds);
                    _callerIdItems = _allowedCallerIds.Select(n => new CallerIdItem(n, "")).ToList();
                    Log($"[CallerID] Using {_allowedCallerIds.Count} cached CallerID(s)");
                    Dispatcher.Invoke(() =>
                    {
                        CallerIdComboBox.Items.Clear();
                        CallerIdComboBox.DisplayMemberPath = "";
                        if (_callerIdItems.Count >= 2)
                        {
                            foreach (var item in _callerIdItems)
                                CallerIdComboBox.Items.Add(item);
                            var restored = _callerIdItems.FirstOrDefault(i => i.Number == settings.SelectedOutboundCallerId);
                            if (restored != null)
                                CallerIdComboBox.SelectedItem = restored;
                            else
                                CallerIdComboBox.SelectedIndex = 0;
                            CallerIdPanel.Visibility = Visibility.Visible;
                        }
                    });
                }

                if (settings.EnableMikoPbxCdr && _mikoPbxCdrService != null)
                    StartCallerIdListRetryTimer();
            }
            finally
            {
                _callerIdLoadSemaphore.Release();
            }
        }

        /// <summary>Subtitle suffix for Amo «Call from …» — Main: outbound number; Secondary: connection display name.</summary>
        private async System.Threading.Tasks.Task<string?> ResolveCallFromSubtitleForAmoAsync(CallHistoryItem? call, string phoneNumber, DateTime callTime)
        {
            if (call == null || call.IsIncoming)
                return null;

            string? fromHistory = AmoCallFromLabelResolver.Resolve(call);
            if (!string.IsNullOrEmpty(fromHistory))
            {
                if (CallStatisticsService.GetEffectiveConnectionSlot(call) == CallConnectionSlot.Secondary)
                    Log($"[MainWindow] AmoCRM: Secondary connection label for Amo card: {fromHistory}");
                return fromHistory;
            }

            if (CallStatisticsService.GetEffectiveConnectionSlot(call) != CallConnectionSlot.Main)
                return null;

            if (_mikoPbxCdrService == null)
                return null;

            int[] delaysMs = { 3000, 5000, 10000, 15000 };
            for (int attempt = 0; attempt < delaysMs.Length; attempt++)
            {
                await System.Threading.Tasks.Task.Delay(delaysMs[attempt]).ConfigureAwait(false);

                var history = _callHistoryService.GetHistory();
                var refreshed = history
                    .Where(x => x.PhoneNumber == phoneNumber && Math.Abs((x.CallTime - callTime).TotalSeconds) < 2)
                    .OrderByDescending(x => x.CallTime)
                    .FirstOrDefault()
                    ?? history.Where(x => x.PhoneNumber == phoneNumber).OrderByDescending(x => x.CallTime).FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(refreshed?.OutboundCallerId))
                {
                    Log($"[MainWindow] AmoCRM: CallerID from history after wait: {refreshed.OutboundCallerId}");
                    return refreshed.OutboundCallerId.Trim();
                }

                try
                {
                    string? fromCdr = await _mikoPbxCdrService.GetCallCallerIdAsync(phoneNumber, callTime).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(fromCdr))
                    {
                        _callHistoryService.UpdateOutboundCallerId(phoneNumber, callTime, fromCdr);
                        Log($"[MainWindow] AmoCRM: CallerID from CDR for Amo card: {fromCdr}");
                        return fromCdr.Trim();
                    }
                }
                catch (Exception ex)
                {
                    Log($"[MainWindow] AmoCRM: CDR CallerID lookup failed: {ex.Message}");
                }

                Log($"[MainWindow] AmoCRM: CDR CallerID attempt {attempt + 1}/{delaysMs.Length} — not ready for {phoneNumber}");
            }

            return null;
        }

        private void TryFetchOutboundCallerIdFromCdr(string phoneNumber, DateTime callTime)
        {
            if (_mikoPbxCdrService == null) return;

            var historyCall = _callHistoryService.GetCall(phoneNumber, callTime);
            string? cdrExt = GetPbxCdrExtensionForCall(historyCall);

            _ = Task.Run(async () =>
            {
                try
                {
                    string? callerId = null;
                    for (int attempt = 1; attempt <= 4; attempt++)
                    {
                        int delayMs = attempt == 1 ? 3000 : attempt == 2 ? 5000 : attempt == 3 ? 10000 : 15000;
                        await Task.Delay(delayMs);

                        callerId = await _mikoPbxCdrService.GetCallCallerIdAsync(phoneNumber, callTime, cdrExt);
                        if (!string.IsNullOrEmpty(callerId))
                        {
                            Log($"[PBX Gateway] Got CallerID from CDR: {callerId} for call to {phoneNumber} (attempt {attempt})");
                            _callHistoryService.UpdateOutboundCallerId(phoneNumber, callTime, callerId);
                            Dispatcher.Invoke(RefreshVisibleDashboards);
                            return;
                        }

                        Log($"[PBX Gateway] Attempt {attempt}/4: no CDR yet for call to {phoneNumber}, retrying...");
                    }

                    Log($"[PBX Gateway] No CallerID found in CDR for call to {phoneNumber} after 4 attempts");
                }
                catch (Exception ex)
                {
                    Log($"[PBX Gateway] CDR lookup failed: {ex.Message}");
                }
            });
        }
    }
}


#endif // WINDOWS
