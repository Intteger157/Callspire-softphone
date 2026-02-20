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

namespace Softphone
{
    public partial class MainWindow : Window
    {
        private SipService? _sipService;
        private CallHistoryService _callHistoryService;
        private LogWindow? _logWindow;
        private System.Collections.Generic.List<string> _logHistory = new System.Collections.Generic.List<string>();


        // Статическая ссылка на главное окно для логирования из других классов
        private static MainWindow? _instance;
        
        // Общий экземпляр WebRTC сервиса для автоматического подключения при старте
        private static WebRtcStatusService? _sharedWebRtcStatusService;
        
        // Singleton WebView2 для WebRTC (живет весь runtime приложения)
        private Microsoft.Web.WebView2.Wpf.WebView2? _webRtcEngine;

        // AmoCRM сервис для интеграции
        private static AmoCrmService? _amoCrmService;
        
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
                // Если WebRTC включен, проверяем статус WebRTC вместо SIP
                if (ShouldUseWebRtc())
                {
                    try
                    {
                        var webRtcService = WebRtcService.Instance;
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
        
        public static void Log(string message)
        {
            message = SanitizeLogMessage(message);

            // Always write to file (best-effort). This is crucial when UI is frozen and user can't open LogWindow.
            try
            {
                FileLogService.Instance.Enqueue(message);
            }
            catch
            {
                // never throw from logging
            }

            if (_instance != null)
            {
                _instance.AddToLog(message);
            }
            // Также выводим в Debug для отладки
            System.Diagnostics.Debug.WriteLine(message);
        }

        private static string SanitizeLogMessage(string message)
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

        public MainWindow()
        {
            InitializeComponent();
            _instance = this; // Сохраняем ссылку на экземпляр
            _callHistoryService = new CallHistoryService();
            ShowView(DialerView);

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
            // Воркеры очереди AmoCRM: обрабатывают звонки параллельно (до 3 одновременно), ждёт файл записи до 5 минут (колл-центр)
            for (int i = 0; i < MaxConcurrentAmoCrmWorkers; i++)
            {
                _ = Task.Run(() => AmoCrmWorkerAsync());
            }
            
            // Автоматическая очистка старых записей (старше 7 дней) при старте приложения
            _ = Task.Run(() =>
            {
                try
                {
                    AppDataHelper.CleanupOldRecordings(retentionDays: 7);
                }
                catch (Exception ex)
                {
                    Log($"[MainWindow] Error during recordings cleanup on startup: {ex.Message}");
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
            SourceInitialized += MainWindow_SourceInitialized;
            
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
                    try { WebRtcService.Instance.StopWatchdog(); } catch { }
                    try { WebRtcService.Instance.StopAutoReconnect(); } catch { }
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
                        try { await WebRtcService.Instance.ResetEngineAsync(); } catch { }
                    });
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
                try { WebRtcService.Instance.StopWatchdog(); } catch { }
                try { WebRtcService.Instance.StopAutoReconnect(); } catch { }
                try { WebRtcService.Instance.DetachEngine(resetState: true); } catch { }

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
                            _webViewProcessFailedHandlerRegistered = false;
                        }
                    }
                    catch { }
                }, System.Windows.Threading.DispatcherPriority.Background);

                await InitializeWebRtcServiceAsync();
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
                        var updateWindow = new UpdateAvailableWindow(updateInfo, currentVersion)
                        {
                            Owner = this
                        };
                        updateWindow.ShowDialog();
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
            // Не трогаем WebRTC/WebView2, если WebRTC режим выключен.
            // Инициализация будет выполнена при включении WebRTC (ReconnectFromSettingsAsync / Advanced settings).
            if (!ShouldUseWebRtc())
            {
                return;
            }

            // Инициализируем WebRTC сервис после загрузки окна с ApplicationIdle приоритетом
            // Это гарантирует, что окно полностью загружено и визуальное дерево готово
            Log("[MainWindow] MainWindow_Loaded: Scheduling WebRTC initialization with ApplicationIdle priority...");
            Dispatcher.BeginInvoke(new System.Action(async () =>
            {
                await InitializeWebRtcServiceAsync();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        
        /// <summary>
        /// Инициализирует WebRTC сервис
        /// </summary>
        private async Task InitializeWebRtcServiceAsync()
        {
            try
            {
                Log("[MainWindow] InitializeWebRtcServiceAsync: Starting...");
                
                // Проверяем, нужен ли WebRTC
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
                
                string? sipPassword = SipPasswordProvider.GetPassword(settings);
                // Best-effort migrate stored secrets to DPAPI (safer at-rest).
                try
                {
                    SipPasswordProvider.MigrateEncryptedToDpapiIfNeeded(settingsFilePath, settings);
                }
                catch { }
                Log($"[MainWindow] InitializeWebRtcServiceAsync: Settings loaded - UseWebRtcAudio={settings.UseWebRtcAudio}, WebRtcWsUri={(string.IsNullOrEmpty(settings.WebRtcWsUri) ? "empty" : "set")}, SipUsername={(string.IsNullOrEmpty(settings.SipUsername) ? "empty" : "set")}, SipPasswordEncrypted={(string.IsNullOrEmpty(settings.SipPasswordEncrypted) ? "empty" : "set")}");
                
                if (!settings.UseWebRtcAudio || 
                    string.IsNullOrEmpty(settings.WebRtcWsUri) ||
                    string.IsNullOrEmpty(settings.SipUsername) || 
                    string.IsNullOrEmpty(sipPassword))
                {
                    Log("[MainWindow] WebRTC not enabled or not configured, skipping service initialization");
                    return;
                }
                
                Log("[MainWindow] Initializing WebRTC service...");
                
                // Настраиваем уровень логирования WebRTC в соответствии с настройками
                WebRtcService.Instance.DebugEnabled = settings.EnableWebRtcDebug;
                
                // Обновляем статус на "Initializing..."
                Dispatcher.Invoke(() =>
                {
                    StatusTextBlock.Text = "Initializing WebRTC...";
                    UpdateStatusColor(false);
                });
                
                // Создаем скрытый WebView2 (1x1px)
                // ВАЖНО: Используем Visibility.Visible вместо Visibility.Hidden
                // Это гарантирует создание HWND и правильную инициализацию
                _webRtcEngine = new Microsoft.Web.WebView2.Wpf.WebView2
                {
                    Visibility = Visibility.Visible, // Visible для создания HWND (не Hidden!)
                    Width = 1,
                    Height = 1,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false
                };
                
                // Добавляем в визуальное дерево через специальный контейнер WebRtcHostGrid
                if (WebRtcHostGrid == null)
                {
                    Log("[MainWindow] ERROR: WebRtcHostGrid not found in XAML");
                    return;
                }
                
                Log("[MainWindow] Adding WebView2 to WebRtcHostGrid...");
                WebRtcHostGrid.Children.Clear();
                WebRtcHostGrid.Children.Add(_webRtcEngine);
                Log("[MainWindow] WebRTC engine WebView2 added to WebRtcHostGrid");
                
                // Ждем, чтобы WebView2 был полностью загружен в визуальном дереве
                Log("[MainWindow] Waiting for WebView2 to be loaded in visual tree...");
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                
                // Дополнительная проверка: ждем, пока WebView2.IsLoaded станет true
                var loadCheckStart = DateTime.Now;
                while (!_webRtcEngine.IsLoaded && (DateTime.Now - loadCheckStart).TotalSeconds < 5)
                {
                    await Task.Delay(100);
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                
                Log($"[MainWindow] WebView2 load check: IsLoaded={_webRtcEngine.IsLoaded}, Parent={_webRtcEngine.Parent?.GetType().Name ?? "null"}, Visibility={_webRtcEngine.Visibility}");
                
                // Получаем Environment с отдельной папкой профиля
                // Это убирает конфликты профиля, прав доступа и блокировок папки
                var userData = Path.Combine(
                    AppDataHelper.GetAppDataPath(),
                    "WebView2");
                Directory.CreateDirectory(userData);
                Log($"[MainWindow] WebView2 UserDataFolder: {userData}");
                
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userData);
                Log("[MainWindow] WebView2 Environment created successfully");
                
                // Создаем host и прикрепляем к сервису
                Log("[MainWindow] Creating WebRtcEngineHost...");
                var host = new WebRtcEngineHost(_webRtcEngine);
                Log("[MainWindow] WebRtcEngineHost created, calling InitAsync...");
                await host.InitAsync(env, "https://softphone.local/index.html");
                Log($"[MainWindow] WebRtcEngineHost.InitAsync completed: IsInitialized={host.IsInitialized}");

                // WebView2 can crash/hang after sleep/resume; listen for ProcessFailed and recover.
                try
                {
                    if (!_webViewProcessFailedHandlerRegistered && _webRtcEngine.CoreWebView2 != null)
                    {
                        _webRtcEngine.CoreWebView2.ProcessFailed += WebRtcEngine_ProcessFailed;
                        _webViewProcessFailedHandlerRegistered = true;
                    }
                }
                catch { }
                
                WebRtcService.Instance.AttachEngine(host);
                WebRtcService.Instance.Event += OnWebRtcEvent;
                WebRtcService.Instance.StartWatchdog();
                
                Log($"[MainWindow] WebRTC service initialized: IsReadyForCalls={WebRtcService.Instance.IsReadyForCalls}");
                
                // Обновляем статус на "Initializing JsSIP..."
                Dispatcher.Invoke(() =>
                {
                    StatusTextBlock.Text = "Initializing JsSIP...";
                    UpdateStatusColor(false);
                });
                
                // Инициализируем UA (фаза 2)
                // ВАЖНО: Используем ReinitializeUAAsync вместо прямого SendAsync, чтобы события обрабатывались через WebRtcService
                var config = GetWebRtcConfig();
                if (config != null)
                {
                    await WebRtcService.Instance.ReinitializeUAAsync(
                        config.WsUri,
                        config.SipUri,
                        settings.SipUsername ?? "",
                        sipPassword ?? ""
                    );
                    Log("[MainWindow] initUA command sent via ReinitializeUAAsync");
                    
                    // Обновляем статус после отправки команды инициализации
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
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => HandleWebRtcEventOnUi(dto)), System.Windows.Threading.DispatcherPriority.Background);
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
                switch (dto.Type)
                {
                        case "ua_started":
                            Log("[MainWindow] WebRTC UA started, connecting...");
                            StatusTextBlock.Text = "Connecting to WebRTC...";
                            UpdateStatusColor(false);
                            break;
                        case "ws_connected":
                        case "ua_connected":
                            Log("[MainWindow] ✓ WebRTC WebSocket connected");
                            StatusTextBlock.Text = "Connected to WebRTC...";
                            UpdateStatusColor(true);
                            // Обновляем статус подключения после подключения WebSocket
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                UpdateConnectionStatus();
                            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                            break;
                        case "registered":
                        case "ua_registered":
                            Log("[MainWindow] ✓ WebRTC UA registered");
                            
                            // ВАЖНО: WebRtcService уже обновил _registered в OnEngineEvent
                            // Проверяем состояние перед обновлением
                            bool isReady = WebRtcService.Instance?.IsReadyForCalls ?? false;
                            bool isConnectedCheck = IsConnected;
                            Log($"[MainWindow] ua_registered event: IsReadyForCalls={isReady}, IsConnected={isConnectedCheck}, _engine={WebRtcService.Instance != null}");
                            
                            // Обновляем статус напрямую (мы уже в Dispatcher.Invoke)
                            // UpdateConnectionStatus() проверит IsConnected и обновит статус
                            UpdateConnectionStatus();
                            UpdateWebRtcIndicator();
                            
                            // Проверяем результат обновления
                            string finalStatus = StatusTextBlock.Text;
                            bool finalIsReady = WebRtcService.Instance?.IsReadyForCalls ?? false;
                            bool finalIsConnected = IsConnected;
                            Log($"[MainWindow] After status update: StatusTextBlock.Text='{finalStatus}', IsReadyForCalls={finalIsReady}, IsConnected={finalIsConnected}");
                            
                            // ДОПОЛНИТЕЛЬНАЯ ПРОВЕРКА: Если статус все еще "Initializing..." или "Connecting..." или "Connected to WebRTC...", принудительно обновляем
                            if ((finalStatus.Contains("Connecting") || finalStatus.Contains("Initializing") || finalStatus.Contains("Connected to WebRTC")) && finalIsReady && finalIsConnected)
                            {
                                Log($"[MainWindow] WARNING: Status still shows '{finalStatus}' but WebRTC is ready! Forcing update to 'Connected with WebRTC'...");
                                StatusTextBlock.Text = "Connected with WebRTC";
                                UpdateStatusColor(true);
                                CallButton.IsEnabled = true;
                            }
                            else if (finalIsReady && finalIsConnected && finalStatus != "Connected with WebRTC")
                            {
                                // Если WebRTC готов, но статус не обновился - принудительно обновляем
                                Log($"[MainWindow] WARNING: WebRTC is ready but status is '{finalStatus}', forcing update to 'Connected with WebRTC'...");
                                StatusTextBlock.Text = "Connected with WebRTC";
                                UpdateStatusColor(true);
                                CallButton.IsEnabled = true;
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
                            Log($"[MainWindow] ✗ WebRTC UA registration failed: {dto.Message}");
                            UpdateConnectionStatus();
                            UpdateWebRtcIndicator();
                            break;
                        case "unregistered":
                        case "ua_unregistered":
                        case "ws_disconnected":
                        case "ua_disconnected":
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
        
        /// <summary>
        /// Обрабатывает входящий WebRTC звонок (UI координатор)
        /// </summary>
        private void HandleIncomingWebRtcCall(WebRtcEventDto dto)
        {
            try
            {
                if (string.IsNullOrEmpty(dto.SessionId) || string.IsNullOrEmpty(dto.CallerNumber))
                {
                    Log("[MainWindow] WARNING: Incoming call without sessionId or callerNumber");
                    return;
                }
                
                Log($"[Call][WebRTC] Handling incoming call (sessionId: {dto.SessionId}, caller: {dto.CallerNumber})");
                
                var config = GetWebRtcConfig();
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
                        WebRtcSessionId = dto.SessionId
                    };
                    _callHistoryService.AddCall(incomingCallItem);
                    LoadCallHistory();
                    
                    var callWindow = new CallWindow(config, dto.CallerNumber, isIncomingCall: true, webRtcSessionId: dto.SessionId)
                    {
                        Owner = this
                    };
                    
                    // Подписываемся на события
                    callWindow.OnIncomingCallStatusChanged += (phoneNumber, callTime, status, duration) =>
                    {
                        _callHistoryService.UpdateCallStatus(phoneNumber, callTime, status, duration);
                        LoadCallHistory();
                    };
                    
                    callWindow.OnCallDetailsChanged += (phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId) =>
                    {
                        _callHistoryService.UpdateCallDetails(phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId);
                        
                        // Обновляем UI истории после обновления деталей звонка (чтобы статус обновился в списке)
                        Dispatcher.Invoke(() => LoadCallHistory());
                        
                        // Обрабатываем звонок в AmoCRM (если интеграция включена)
                        string? sessionId = transport == CallTransport.WebRtc ? webRtcSessionId : sipCallId;
                        
                        // ОПТИМИЗАЦИЯ: Используем найденный лид из CallWindow (если поиск был выполнен во время звонка)
                        // Ищем CallWindow по sessionId для получения найденного leadId
                        long? foundLeadId = null;
                        if (!string.IsNullOrEmpty(sessionId))
                        {
                            // Ищем CallWindow, который соответствует этому звонку через sessionId
                            // Используем существующий механизм получения leadId из CallWindow
                            var callWindows = Application.Current.Windows.OfType<CallWindow>().ToList();
                            foreach (var window in callWindows)
                            {
                                try
                                {
                                    // Получаем leadId из CallWindow (может быть из браузера или найденный во время звонка)
                                    var windowLeadId = window.GetAmoCrmLeadId();
                                    if (windowLeadId.HasValue)
                                    {
                                        foundLeadId = windowLeadId;
                                        Log($"[MainWindow] OnCallDetailsChanged: Retrieved leadId from CallWindow: {foundLeadId.Value}");
                                        break;
                                    }
                                }
                                catch
                                {
                                    // Игнорируем ошибки при получении leadId
                                }
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
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userData);
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
                    
                    if (useWebRtc)
                    {
                        // Для WebRTC проверяем WebRTC настройки
                        hasConnectionSettings = settings != null && 
                            !string.IsNullOrEmpty(settings.WebRtcWsUri) && 
                            !string.IsNullOrEmpty(settings.SipUsername) && 
                            !string.IsNullOrEmpty(sipPassword);
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
                            var webRtcService = WebRtcService.Instance;
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
                        string currentStatus = StatusTextBlock.Text;
                        
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
                
                // Если WebRTC не включен, используем обычную логику SIP
                // Если есть настройки, пытаемся подключиться
                await TryConnectFromSettings();
                
                // Даем время на попытку подключения (1 секунда вместо 3)
                await System.Threading.Tasks.Task.Delay(1000);
                
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
            // Сохраняем текущий активный вид перед открытием настроек
            Grid currentActiveView = DialerView.Visibility == Visibility.Visible ? DialerView : HistoryView;
            
            var settingsWindow = new SettingsWindow
            {
                Owner = this
            };
            settingsWindow.ShowDialog();
            
            // После закрытия настроек активируем главное окно и восстанавливаем фокус
            // Используем BeginInvoke с ApplicationIdle для гарантированной активации после закрытия диалога
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // Если окно свернуто, восстанавливаем его
                    if (WindowState == WindowState.Minimized)
                    {
                        WindowState = WindowState.Normal;
                    }
                    
                    // Активируем окно
                    Activate();
                    Focus();
                    BringIntoView();
                    
                    // Убеждаемся, что окно видимо и активно
                    if (!IsActive)
                    {
                        // Пробуем еще раз через небольшую задержку
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Activate();
                            Focus();
                        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                }
                catch
                {
                    // Игнорируем ошибки активации
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            
            // Восстанавливаем выделение активной кнопки
            UpdateButtonSelection(currentActiveView);
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
                        
                        // Инициализируем AmoCRM сервис, если интеграция включена
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

                int port = 5060;
                if (!string.IsNullOrEmpty(settings.SipServer) && settings.SipServer.Contains(":"))
                {
                    var parts = settings.SipServer.Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int parsedPort))
                    {
                        port = parsedPort;
                    }
                }

                _sipService?.Dispose();

                _sipService = new SipService(
                    settings.SipUsername ?? "", 
                    SipPasswordProvider.GetPassword(settings) ?? "", 
                    settings.SipServer?.Split(':')[0] ?? settings.SipServer ?? "", 
                    port,
                    settings.MicrophoneDeviceNumber, 
                    settings.SpeakerDeviceNumber,
                    settings.AudioCodec ?? "PCMU",
                    settings.AudioSampleRate > 0 ? settings.AudioSampleRate : 16000,
                    settings.AudioBitrate > 0 ? settings.AudioBitrate : 64000);
                
                // Если включен WebRTC, пропускаем инициализацию аудио в SIPSorcery
                if (settings.UseWebRtcAudio)
                {
                    _sipService.SkipAudioInitialization();
                }
                _sipService.OnStatusChanged += (status) =>
                {
                    Dispatcher.Invoke(() =>
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
                                StatusTextBlock.Text = userFriendlyStatus;
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
                    });
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
                    StatusTextBlock.Text = "Connecting to server...";
                    UpdateStatusColor(false);
                }
                // Если WebRTC режим включен, не меняем статус здесь (он уже установлен в ReconnectFromSettingsAsync)

                await _sipService.StartAsync();

                CallButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Connection error: {ex.Message}";
                UpdateStatusColor(false);
            }
        }

        private void ShowView(Grid view)
        {
            DialerView.Visibility = Visibility.Collapsed;
            HistoryView.Visibility = Visibility.Collapsed;

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
        }

        private void DialerButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(DialerView);
        }

        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(HistoryView);
            LoadCallHistory();
        }

        private void LoadCallHistory()
        {
            var history = _callHistoryService.GetHistory();
            
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
                PhoneNumber = call.PhoneNumber,
                Status = GetStatusText(call.Status),
                StatusColor = GetStatusColor(call.Status),
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

        private string GetStatusText(CallStatus status)
        {
            return status switch
            {
                CallStatus.Calling => "Calling...",
                CallStatus.Connected => "Connected",
                CallStatus.Ended => "Ended",
                CallStatus.Failed => "Failed",
                CallStatus.Cancelled => "Cancelled",
                CallStatus.Missed => "Missed",
                _ => "Unknown"
            };
        }

        private Brush GetStatusColor(CallStatus status)
        {
            return status switch
            {
                CallStatus.Connected => new SolidColorBrush(Color.FromRgb(34, 197, 94)), // Green
                CallStatus.Ended => new SolidColorBrush(Color.FromRgb(156, 163, 175)), // Gray
                CallStatus.Failed => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                CallStatus.Cancelled => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                CallStatus.Missed => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                CallStatus.Calling => new SolidColorBrush(Color.FromRgb(59, 130, 246)), // Blue
                _ => new SolidColorBrush(Color.FromRgb(156, 163, 175)) // Gray
            };
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
                            detailsWindow.Owner = this;
                            detailsWindow.ShowDialog();
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
                    await PerformCall(phoneNumber);
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
                if (CallButton.IsEnabled)
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

                // Migrate legacy plaintext SIP password to encrypted (best-effort)
                SipPasswordProvider.MigratePlaintextToEncryptedIfNeeded(settingsFilePath, settings);
                string? sipPassword = SipPasswordProvider.GetPassword(settings);
                
                // КРИТИЧНО: Проверяем активные звонки перед изменением настроек
                // Не прерываем активные звонки при изменении настроек
                var existingCallWindow = CallHandlingHelpers.FindExistingCallWindow();
                bool hasActiveWebRtcCall = CallHandlingHelpers.IsWebRtcCallActive();
                bool hasActiveSipCall = _sipService != null && _sipService.IsInCall;
                
                if (existingCallWindow != null || hasActiveWebRtcCall || hasActiveSipCall)
                {
                    Log("[MainWindow] ReconnectFromSettingsAsync: Active call detected, deferring reconnection until call ends");
                    Dispatcher.Invoke(() =>
                    {
                        StatusTextBlock.Text = "Settings saved. Changes will be applied after current call ends.";
                        UpdateStatusColor(false);
                    });
                    return;
                }
                
                // Инициализируем AmoCRM сервис, если интеграция включена
                InitializeAmoCrmService(settings);
                
                bool useWebRtc = settings.UseWebRtcAudio && 
                                 !string.IsNullOrEmpty(settings.WebRtcWsUri) &&
                                 !string.IsNullOrEmpty(settings.SipUsername) && 
                                 !string.IsNullOrEmpty(sipPassword);
                
                if (useWebRtc)
                {
                    // WebRTC режим включен
                    Log("[MainWindow] ReconnectFromSettingsAsync: WebRTC mode enabled, stopping SIP and initializing WebRTC...");
                    
                    // ВАЖНО: Сбрасываем статус ПЕРЕД переключением на WebRTC режим
                    // Это предотвращает отображение старого SIP статуса "Connecting to server..."
                    Dispatcher.Invoke(() =>
                    {
                        StatusTextBlock.Text = "Initializing WebRTC...";
                        UpdateStatusColor(false);
                        Log("[MainWindow] ReconnectFromSettingsAsync: Status reset to 'Initializing WebRTC...' for WebRTC mode");
                    });
                    
                    // IMPORTANT: Fully stop SIP in WebRTC mode (no transport, no registration, no incoming/outgoing SIP).
                    _sipService?.Dispose();
                    _sipService = null;
                    
                    // Инициализируем или переинициализируем WebRTC
                    Log("[MainWindow] ReconnectFromSettingsAsync: Initializing/Reinitializing WebRTC UA...");
                    
                    // ВАЖНО: Проверяем, инициализирован ли WebRTC engine
                    // Если engine не инициализирован, вызываем полную инициализацию
                    // Если engine инициализирован, переинициализируем UA
                    bool engineInitialized = WebRtcService.Instance?.IsReadyForCalls ?? false;
                    
                    // Дополнительная проверка: если IsReadyForCalls=false, но engine может быть инициализирован,
                    // проверяем через рефлексию
                    if (!engineInitialized && WebRtcService.Instance != null)
                    {
                        try
                        {
                            var engineField = WebRtcService.Instance.GetType().GetField("_engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            if (engineField != null)
                            {
                                var engine = engineField.GetValue(WebRtcService.Instance);
                                if (engine != null)
                                {
                                    var isInitializedProp = engine.GetType().GetProperty("IsInitialized");
                                    if (isInitializedProp != null)
                                    {
                                        engineInitialized = (bool)(isInitializedProp.GetValue(engine) ?? false);
                                        Log($"[MainWindow] ReconnectFromSettingsAsync: Engine IsInitialized={engineInitialized} (checked via reflection)");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[MainWindow] ReconnectFromSettingsAsync: Error checking engine status: {ex.Message}");
                        }
                    }
                    
                    if (engineInitialized && WebRtcService.Instance != null)
                    {
                        // Engine инициализирован - переинициализируем UA
                        var config = GetWebRtcConfig();
                        if (config != null)
                        {
                            Log("[MainWindow] ReconnectFromSettingsAsync: Engine is initialized, reinitializing UA...");
                            await WebRtcService.Instance.ReinitializeUAAsync(
                                config.WsUri,
                                config.SipUri,
                                settings.SipUsername ?? "",
                                sipPassword ?? ""
                            );
                            Log("[MainWindow] ReconnectFromSettingsAsync: WebRTC UA reinitialized");
                        }
                        else
                        {
                            Log("[MainWindow] ReconnectFromSettingsAsync: WebRtc config is null, initializing WebRTC service...");
                            await InitializeWebRtcServiceAsync();
                        }
                    }
                    else
                    {
                        // Engine не инициализирован - выполняем полную инициализацию
                        Log("[MainWindow] ReconnectFromSettingsAsync: Engine not initialized (IsReadyForCalls=false), performing full initialization...");
                        await InitializeWebRtcServiceAsync();
                    }
                    
                    // Обновляем статус подключения после инициализации WebRTC
                    // Даем небольшую задержку для завершения инициализации
                    await System.Threading.Tasks.Task.Delay(300);
                    Dispatcher.Invoke(() =>
                    {
                        UpdateConnectionStatus();
                        UpdateWebRtcIndicator();
                        Log($"[MainWindow] ReconnectFromSettingsAsync: Status updated after WebRTC init - StatusTextBlock.Text='{StatusTextBlock.Text}', IsReadyForCalls={WebRtcService.Instance?.IsReadyForCalls ?? false}, IsConnected={IsConnected}");
                    });
                }
                else
                {
                    // SIP режим - отключаем WebRTC и подключаем SIP
                    Log("[MainWindow] ReconnectFromSettingsAsync: SIP mode enabled, connecting SIP...");

                    // Stop/unload WebRTC in parallel so SIP can connect quickly and UI isn't spammed by WebRTC logs.
                    _ = ShutdownWebRtcAsync();
                    
                    // Переподключаем SIP сервис
                    await TryConnectFromSettings();
                    
                    // Обновляем статус подключения
                    Dispatcher.Invoke(() =>
                    {
                        UpdateConnectionStatus();
                        UpdateWebRtcIndicator();
                        UpdateUserAccountInfo();
                    });
                }
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
                // Stop watchdog first (prevents periodic ping/getStats).
                try { WebRtcService.Instance.StopWatchdog(); } catch { }

                // Unsubscribe events + detach engine state.
                try { WebRtcService.Instance.Event -= OnWebRtcEvent; } catch { }
                try { WebRtcService.Instance.DetachEngine(resetState: true); } catch { }

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
                            Log("[MainWindow] WebRTC WebView2 disposed (switched to SIP mode)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[MainWindow] Error disposing WebRTC WebView2: {ex.Message}");
                        _webRtcEngine = null;
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

        private void HandleIncomingCall(string callerNumber)
        {
            try
            {
                Log($"HandleIncomingCall called with callerNumber: {callerNumber}");

                // IMPORTANT: When WebRTC mode is enabled, SIP incoming calls must be ignored/disabled.
                if (ShouldUseWebRtc())
                {
                    Log($"[MainWindow] HandleIncomingCall: SIP incoming call from {callerNumber} ignored: WebRTC mode enabled");
                    return;
                }
                
            if (_sipService == null)
                {
                    Log("HandleIncomingCall: _sipService is null, cannot handle incoming call");
                return;
                }

            // Гейт: если есть активный WebRTC звонок, игнорируем SIP входящий
            if (CallHandlingHelpers.IsWebRtcCallActive())
            {
                Log($"[MainWindow] HandleIncomingCall: SIP incoming call from {callerNumber} ignored: WebRTC call active");
                return;
            }

            // Проверяем, нет ли уже открытого окна звонка
            Log($"[MainWindow] HandleIncomingCall: Checking for existing CallWindow...");
            var existingCallWindow = CallHandlingHelpers.FindExistingCallWindow();
            if (existingCallWindow != null)
            {
                Log($"[MainWindow] HandleIncomingCall: CallWindow already exists, ignoring duplicate call from {callerNumber}");
                return;
            }
            Log($"[MainWindow] HandleIncomingCall: No existing CallWindow found, proceeding with new call");

            // Логируем хеш-код для диагностики
            int serviceHash = _sipService.GetHashCode();
            Log($"[MainWindow] HandleIncomingCall: Creating CallWindow with SipService hash: {serviceHash}, CallerNumber: {callerNumber}");
            
            // Сохраняем время начала входящего звонка для истории
            DateTime incomingCallStartTime = DateTime.Now;
            
            // Открываем окно для входящего звонка с флагом isIncomingCall = true
            Log($"[MainWindow] HandleIncomingCall: Creating CallWindow instance...");
            CallWindow callWindow;
            try
            {
                callWindow = new CallWindow(_sipService, callerNumber, isIncomingCall: true)
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
                callWindow.OnCallDetailsChanged += (phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId) =>
                {
                    _callHistoryService.UpdateCallDetails(phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId);
                    
                    // Обрабатываем звонок в AmoCRM (если интеграция включена)
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
                Transport = CallTransport.Sip // Входящие через SipService всегда SIP
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
                Log($"[MainWindow] HandleProtocolMessage called: {protocolUrl}");
                
                // Парсим URL протокола
                if (!Uri.TryCreate(protocolUrl, UriKind.Absolute, out Uri? uri))
                {
                    Log($"[MainWindow] Invalid protocol URL format: {protocolUrl}");
                    return;
                }
                
                // Проверяем схему и хост
                if (uri.Scheme != "callspire" || uri.Host != "call")
                {
                    Log($"[MainWindow] Invalid protocol scheme or host: {uri.Scheme}://{uri.Host}");
                    return;
                }
                
                // Парсим query параметры
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
            
            // Если используется WebRTC, ждем его готовности перед инициацией звонка
            if (ShouldUseWebRtc())
            {
                Log("[MainWindow] WebRTC mode detected, waiting for WebRTC to be ready...");
                
                // Ждем готовности WebRTC до 30 секунд
                int maxWaitSeconds = 30;
                int waitedSeconds = 0;
                
                while (!WebRtcService.Instance.IsReadyForCalls && waitedSeconds < maxWaitSeconds)
                {
                    await Task.Delay(500); // Проверяем каждые 500мс
                    waitedSeconds += 1;
                    
                    if (waitedSeconds % 5 == 0)
                    {
                        Log($"[MainWindow] Still waiting for WebRTC... ({waitedSeconds}/{maxWaitSeconds}s)");
                    }
                }
                
                if (!WebRtcService.Instance.IsReadyForCalls)
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
                        while (!WebRtcService.Instance.IsReadyForCalls)
                        {
                            await Task.Delay(1000);
                        }
                        
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            Log("[MainWindow] WebRTC is now ready, initiating call from browser");
                            await PerformCall(phoneNumber, leadId);
                        });
                    });
                    return;
                }
                
                Log("[MainWindow] WebRTC is ready, proceeding with call");
            }
            
            await PerformCall(phoneNumber, leadId);
        }
        
        private async System.Threading.Tasks.Task PerformCall(string number, long? leadId = null)
        {
            if (string.IsNullOrEmpty(number))
            {
                CustomMessageBox.Show("Please enter a phone number.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                return;
            }

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

            // Проверяем, нужно ли использовать WebRTC
            bool useWebRtc = ShouldUseWebRtc();
            
            // Проверяем состояние WebRTC и SIP сервисов
            if (useWebRtc)
            {
                if (CallHandlingHelpers.IsWebRtcCallActive())
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
                if (_sipService != null && _sipService.IsInCall)
                {
                    MainWindow.Log($"[MainWindow] PerformCall: SIP call is active, ignoring call to {number}");
                    CustomMessageBox.Show(
                        "A SIP call is already in progress. Please end the current call before making a new one.",
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
                // WebRTC режим - проверяем готовность WebRTC
                if (WebRtcService.Instance == null || !WebRtcService.Instance.IsReadyForCalls)
                {
                    Log($"[Call] ERROR: WebRTC mode enabled but WebRTC service is not ready (IsReadyForCalls={WebRtcService.Instance?.IsReadyForCalls ?? false})");
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
                // SIP режим - проверяем наличие SIP сервиса и подключение
                if (_sipService == null || !IsConnected)
                {
                    CustomMessageBox.Show("Please connect first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    return;
                }
            }

            DateTime callStartTime = DateTime.Now;
            CallHistoryItem? currentCallHistoryItem = null;

            try
            {
                CallButton.IsEnabled = false;

                Log($"[Call] Making call to {number} - Transport: {(useWebRtc ? "WebRTC" : "SIP")}");
                
                // Открываем окно звонка
                CallWindow callWindow;
                
                if (useWebRtc)
                {
                    // Устанавливаем cooldown для блокировки SIP входящих при исходящем WebRTC звонке
                    if (_sipService != null)
                    {
                        _sipService.SetIgnoreSipCooldown(3);
                    }
                    var webRtcConfig = GetWebRtcConfig();
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
                            Transport = CallTransport.WebRtc
                        };
                        _callHistoryService.AddCall(outgoingCallItem);
                        LoadCallHistory();
                        currentCallHistoryItem = outgoingCallItem;
                        
                        callWindow = new CallWindow(webRtcConfig, number, isIncomingCall: false, callStartTime: callStartTime, amoCrmLeadId: leadId)
                {
                    Owner = this
                        };
                        
                        // Инициируем звонок через WebRTC сервис.
                        // IMPORTANT: await here to surface "WebRTC not ready" and avoid ghost calls that never reach PBX.
                        try
                        {
                            await WebRtcService.Instance.MakeCallAsync(number);
                        }
                        catch (Exception ex)
                        {
                            Log($"[Call][WebRTC] ERROR: MakeCallAsync failed for {number}: {ex.Message}");
                            try { callWindow.Close(); } catch { }
                            CallButton.IsEnabled = true;
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
                        CallButton.IsEnabled = true;
                        return;
                    }
                }
                else
                {
                    Log($"[Call][SIP] Making outgoing call to {number}");
                    if (_sipService == null)
                    {
                        Log($"[Call][SIP] ERROR: _sipService is null, cannot create CallWindow");
                        CallButton.IsEnabled = true;
                        return;
                    }
                    
                    // Добавляем запись в историю звонков (исходящий SIP звонок)
                    var outgoingCallItem = new CallHistoryItem
                    {
                        PhoneNumber = number,
                        CallTime = callStartTime,
                        Status = CallStatus.Calling,
                        IsIncoming = false,
                        Transport = CallTransport.Sip
                    };
                    _callHistoryService.AddCall(outgoingCallItem);
                    LoadCallHistory();
                    currentCallHistoryItem = outgoingCallItem;
                    
                    callWindow = new CallWindow(_sipService, number, isIncomingCall: false, callStartTime: callStartTime, amoCrmLeadId: leadId)
                    {
                        Owner = this
                    };
                }
                
                // Подписываемся на события обновления детальной информации о звонке для исходящих звонков
                callWindow.OnCallDetailsChanged += (phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId) =>
                {
                    _callHistoryService.UpdateCallDetails(phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId);
                    
                    // Обновляем UI истории после обновления деталей звонка (чтобы статус обновился в списке)
                    Dispatcher.Invoke(() => LoadCallHistory());
                    
                    // КРИТИЧНО: Обрабатываем звонок в AmoCRM только один раз для каждого звонка
                    // Используем дедупликацию по ключу (phoneNumber + callTime + sessionId) для предотвращения множественных записей
                    // sessionId позволяет различать разные звонки, даже если они происходят в одну секунду
                    string? sessionId = transport == CallTransport.WebRtc ? webRtcSessionId : sipCallId;
                    // Получаем leadId из контекста звонка, если он был передан из браузера
                    long? callLeadId = callWindow.GetAmoCrmLeadId();
                    Log($"[MainWindow] OnCallDetailsChanged: Retrieved leadId from CallWindow: {callLeadId?.ToString() ?? "null"}");
                    ProcessCallInAmoCrm(phoneNumber, callTime, technicalDetails, recordingFilePath, sessionId, callLeadId);
                };
                
                bool callConnected = false;
                DateTime? callConnectedTime = null;
                
                // Подписываемся на события статуса для отслеживания состояния звонка (только для SIP звонков)
                if (_sipService != null && !useWebRtc)
                {
                    _sipService.OnStatusChanged += (status) =>
                    {
                        // Устанавливаем callConnected только при реальном подключении, а не при начале звонка
                        if (status.Contains("Call connected") || status.Contains("Call answered") || 
                            status.Contains("200 OK") || status.Contains("Call progress: 200 OK"))
                        {
                            callConnected = true;
                            callConnectedTime = DateTime.Now;
                            if (currentCallHistoryItem != null)
                            {
                                currentCallHistoryItem.Status = CallStatus.Connected;
                                _callHistoryService.UpdateCallStatus(number, callStartTime, CallStatus.Connected);
                            }
                        }
                    };
                    
                    // Подписываемся на событие завершения звонка для обновления состояния кнопки
                    _sipService.OnCallEnded += () =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            CallButton.IsEnabled = true;

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
                }
                
                // Обрабатываем закрытие окна звонка
                callWindow.Closed += (s, e) =>
                {
                    // Если окно закрылось, но звонок еще активен, завершаем его
                    if (_sipService != null && _sipService.IsInCall)
                    {
                        _sipService.Hangup();
                    }
                    
                    // Обновляем историю, если звонок был отменен (только если он не был принят)
                    // Проверяем, был ли звонок принят через детали звонка
                    if (currentCallHistoryItem != null && currentCallHistoryItem.Status == CallStatus.Calling)
                    {
                        // Если звонок был принят (WasAnswered = true), статус должен быть "Ended", а не "Cancelled"
                        // Проверяем через детали звонка, которые уже должны быть обновлены через OnCallDetailsChanged
                        if (currentCallHistoryItem.WasAnswered)
                        {
                            // Звонок был принят, но статус не обновился - обновляем на Ended
                            TimeSpan? duration = currentCallHistoryItem.Duration;
                            currentCallHistoryItem.Status = CallStatus.Ended;
                            _callHistoryService.UpdateCallStatus(number, callStartTime, CallStatus.Ended, duration);
                        }
                        else
                        {
                            // Звонок не был принят - это действительно отмена
                            currentCallHistoryItem.Status = CallStatus.Cancelled;
                            _callHistoryService.UpdateCallStatus(number, callStartTime, CallStatus.Cancelled);
                        }

                        // После любого обновления статуса перерисовываем список истории,
                        // иначе последний завершённый звонок остаётся со статусом "Calling..."
                        LoadCallHistory();
                    }
                    
                    CallButton.IsEnabled = true;
                };
                
                callWindow.Show();

                // Инициируем звонок только для SIPSorcery (не для WebRTC)
                if (!useWebRtc && _sipService != null)
                {
                    await _sipService.CallAsync(number);
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
                CallButton.IsEnabled = true;
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

        // --- Borderless maximize should not cover taskbar: WM_GETMINMAXINFO hook ---
        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProc);
            }
            catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SYSCOMMAND = 0x0112;
            const int SC_MAXIMIZE = 0xF030;
            const int WM_GETMINMAXINFO = 0x0024;

            // Block maximize entirely (Win+Up, system menu, etc.)
            if (msg == WM_SYSCOMMAND)
            {
                try
                {
                    int cmd = (int)(wParam.ToInt64() & 0xFFF0);
                    if (cmd == SC_MAXIMIZE)
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
                catch { }
            }

            if (msg == WM_GETMINMAXINFO)
            {
                try
                {
                    WmGetMinMaxInfo(hwnd, lParam);
                    handled = true;
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            const int MONITOR_DEFAULTTONEAREST = 0x00000002;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    RECT workArea = monitorInfo.rcWork;
                    RECT monitorArea = monitorInfo.rcMonitor;

                    mmi.ptMaxPosition.X = workArea.left - monitorArea.left;
                    mmi.ptMaxPosition.Y = workArea.top - monitorArea.top;
                    mmi.ptMaxSize.X = workArea.right - workArea.left;
                    mmi.ptMaxSize.Y = workArea.bottom - workArea.top;
                }
            }

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _sipService?.Dispose();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                UnregisterPowerAndNetworkHandlers();

                // Закрываем все дочерние окна перед очисткой ресурсов
                var windowsToClose = new List<Window>();
                foreach (Window window in Application.Current.Windows)
                {
                    if (window != this && window.Owner == this)
                    {
                        windowsToClose.Add(window);
                    }
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
                
                // Останавливаем SIP сервис
                _sipService?.Dispose();
                _sipService = null;
                
                // Останавливаем watchdog timer WebRTC
                WebRtcService.Instance.StopWatchdog();

                // Останавливаем auto-reconnect и отвязываем engine (важно: иначе таймеры могут жить после Dispose WebView2).
                try { WebRtcService.Instance.StopAutoReconnect(); } catch { }
                try { WebRtcService.Instance.DetachEngine(resetState: true); } catch { }
                
                // Отписываемся от событий WebRTC
                WebRtcService.Instance.Event -= OnWebRtcEvent;
                
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
                        Log("[MainWindow] WebView2 disposed");
                    }
                    catch (Exception ex)
                    {
                        Log($"[MainWindow] Error disposing WebView2: {ex.Message}");
                        // Пытаемся обнулить ссылку даже при ошибке
                        _webRtcEngine = null;
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
                base.OnClosed(e);
            }
        }

        private void AddToLog(string message)
        {
            _logHistory.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            
            // Ограничиваем размер истории (последние 1000 записей)
            if (_logHistory.Count > 1000)
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
                _logWindow = new LogWindow
                {
                    Owner = this
                };
                
                // Загружаем историю логов
                _logWindow.SetLogs(string.Join("\n", _logHistory));
                
                _logWindow.Closed += (s, args) => { _logWindow = null; };
                _logWindow.Show();
            }
            else
            {
                _logWindow.Activate();
            }
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

        public void UpdateWebRtcIndicator()
        {
            try
            {
                bool useWebRtc = ShouldUseWebRtc();
                if (WebRtcIndicator != null)
                {
                    WebRtcIndicator.Visibility = useWebRtc ? Visibility.Visible : Visibility.Collapsed;
                    if (useWebRtc)
                    {
                        // Зеленый индикатор если WebRTC активен
                        WebRtcIndicator.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    }
                }
            }
            catch
            {
                // Игнорируем ошибки
            }
        }

        // Сохраняем предыдущий статус для предотвращения избыточного логирования
        private string? _lastConnectionStatus = null;
        
        // Throttling для UpdateConnectionStatus - предотвращает слишком частые вызовы
        private DateTime _lastStatusUpdateTime = DateTime.MinValue;
        private const int STATUS_UPDATE_THROTTLE_MS = 500; // Минимум 500мс между обновлениями
        
        /// <summary>
        /// Публичное свойство для получения текущего статуса подключения (для синхронизации с SettingsWindow)
        /// </summary>
        public string ConnectionStatus
        {
            get
            {
                return StatusTextBlock?.Text ?? "Not connected";
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
                bool useWebRtc = ShouldUseWebRtc();
                bool isConnected = IsConnected;
                string newStatus = "";
                
                if (useWebRtc)
                {
                    // WebRTC режим
                    if (isConnected)
                    {
                        _webRtcDisconnectedSince = DateTime.MinValue;
                        newStatus = "Connected with WebRTC";
                    }
                    else
                    {
                        // WebRTC не подключен
                        // Проверяем, не идет ли инициализация (чтобы не перезаписать статус "Initializing..." или "Connecting...")
                        string currentStatus = StatusTextBlock?.Text ?? "";
                        
                        if (!currentStatus.Contains("Initializing") && 
                            !currentStatus.Contains("Connecting") && 
                            !currentStatus.Contains("Connected to WebRTC"))
                        {
                            // If we were connected recently, show a stable "Reconnecting..." state instead of flickering.
                            if (_lastIsConnected)
                            {
                                if (_webRtcDisconnectedSince == DateTime.MinValue)
                                    _webRtcDisconnectedSince = DateTime.Now;

                                if ((DateTime.Now - _webRtcDisconnectedSince).TotalSeconds < 6)
                                    newStatus = "Reconnecting to WebRTC...";
                                else
                                    newStatus = "Not connected";
                            }
                            else
                            {
                                newStatus = "Not connected";
                            }
                        }
                        else
                        {
                            newStatus = currentStatus; // Сохраняем текущий статус инициализации
                        }
                    }
                }
                else
                {
                    // SIP режим
                    if (isConnected)
                    {
                        newStatus = "Connected with SIP";
                    }
                    else
                    {
                        newStatus = "Not connected";
                    }
                }
                
                // Throttling: skip only if we would not change anything and the call is too frequent.
                var now = DateTime.Now;
                if (_lastConnectionStatus == newStatus &&
                    (now - _lastStatusUpdateTime).TotalMilliseconds < STATUS_UPDATE_THROTTLE_MS)
                {
                    return;
                }
                _lastStatusUpdateTime = now;

                // Обновляем UI только если статус изменился
                if (_lastConnectionStatus != newStatus && StatusTextBlock != null)
                {
                    StatusTextBlock.Text = newStatus;
                    UpdateStatusColor(isConnected);
                    
                    // Обновляем кнопку вызова
                    if (CallButton != null)
                    {
                        CallButton.IsEnabled = isConnected;
                    }
                    
                    // Обновляем флаг последнего состояния подключения
                    _lastIsConnected = isConnected;
                    
                    // Логируем только при изменении статуса
                    Log($"[MainWindow] UpdateConnectionStatus: Status changed to '{newStatus}' (useWebRtc={useWebRtc}, isConnected={isConnected})");
                    _lastConnectionStatus = newStatus;
                    
                    // Уведомляем SettingsWindow об изменении статуса для синхронизации
                    OnConnectionStatusChanged?.Invoke(newStatus);
                }
            }
            catch (Exception ex)
            {
                // Обрабатываем ошибки без блокировки UI
                Log($"[MainWindow] Error in UpdateConnectionStatus: {ex.Message}");
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
                
                if (settings == null || string.IsNullOrEmpty(settings.SipUsername))
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
                
                // Формируем текст в формате "username@server"
                string username = settings.SipUsername;
                string server = settings.SipServer ?? "";
                
                // Убираем порт из сервера, если он есть (например, "server:5060" -> "server")
                if (!string.IsNullOrEmpty(server) && server.Contains(":"))
                {
                    server = server.Split(':')[0];
                }
                
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
        
        private void UpdateStatusColor(bool isConnected)
        {
            // Находим TextBlock с именем StatusLabel и меняем его цвет
            var statusLabel = FindName("StatusLabel") as System.Windows.Controls.TextBlock;
            
            if (statusLabel != null)
            {
                if (isConnected)
                {
                    statusLabel.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                }
                else
                {
                    statusLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                }
            }
        }
        
        // WebRTC helper methods
        private bool ShouldUseWebRtc()
        {
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    return settings?.UseWebRtcAudio ?? false;
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
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    string? sipPassword = SipPasswordProvider.GetPassword(settings);
                    
                    if (settings != null && !string.IsNullOrEmpty(settings.WebRtcWsUri) &&
                        !string.IsNullOrEmpty(settings.SipUsername) && !string.IsNullOrEmpty(sipPassword))
                    {
                        // Формируем SIP URI из настроек
                        // Для MikoPBX WebRTC нужно добавить суффикс -WS к имени пользователя
                        // Формат: sip:username@pbx_address, где pbx_address - это адрес сервера PBX
                        string pbxAddress = "";
                        
                        // Пытаемся извлечь адрес PBX из WebSocket URI (например, из wss://pbx.intermark.global:8089/asterisk/ws)
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
                        
                        // Если не удалось извлечь из WebSocket URI, используем SipServer
                        if (string.IsNullOrEmpty(pbxAddress))
                        {
                            pbxAddress = settings.SipServer?.Split(':')[0] ?? "";
                        }
                        
                        if (string.IsNullOrEmpty(pbxAddress))
                        {
                            // Если адрес PBX не указан, не можем создать конфиг
                            return null;
                        }
                        
                        string username = settings.SipUsername;
                        // Формируем SIP URI: sip:username@pbx_address
                        string sipUri = $"sip:{username}@{pbxAddress}";
                        
                        var config = new WebRtcConfig
                        {
                            WsUri = settings.WebRtcWsUri,
                            SipUri = sipUri,
                            Password = sipPassword,
                            EnableDebug = settings.EnableWebRtcDebug
                        };
                        Log($"[MainWindow] GetWebRtcConfig: Created config - WsUri={config.WsUri}, SipUri={config.SipUri}");
                        return config;
                    }
                    else
                    {
                        Log($"[MainWindow] GetWebRtcConfig: Missing required settings - WebRtcWsUri={(string.IsNullOrEmpty(settings?.WebRtcWsUri) ? "empty" : "set")}, SipUsername={(string.IsNullOrEmpty(settings?.SipUsername) ? "empty" : "set")}, SipPassword={(string.IsNullOrEmpty(sipPassword) ? "empty" : "set")}");
                    }
                }
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
            return _amoCrmService != null && _amoCrmService.IsInitialized;
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
                        InitializeAmoCrmService(settings);
                        
                        // Проверяем результат инициализации через небольшую задержку
                        // (инициализация происходит асинхронно в Task.Run)
                        await Task.Delay(2000);
                        
                        // Проверяем, была ли инициализация успешной
                        if (settings.EnableAmoCrmIntegration && !IsAmoCrmServiceInitialized())
                        {
                            Dispatcher.Invoke(() =>
                            {
                                string authMode = settings.AmoCrmAuthMode ?? "manual";
                                Log($"[MainWindow] Kommo integration enabled but service not initialized after startup. AuthMode={authMode}");
                                ShowKommoIntegrationWarning(authMode);
                            });
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
                try
                {
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
                                        
                                        // Инициализируем с OAuth токенами
                                        await _amoCrmService.InitializeOAuthAsync(
                                            settings.AmoCrmSubdomain,
                                            accessToken,
                                            refreshToken,
                                            settings.AmoCrmOAuthTokenExpiresAt,
                                            clientId,
                                            clientSecret
                                        );
                                        
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
                                        
                                        // Отключаем сервис, так как токен недействителен
                                        DisconnectAmoCrmService();
                                        
                                        // Показываем предупреждение пользователю
                                        ShowKommoIntegrationWarning("oauth", "Token is invalid or expired. Please re-authorize the application.");
                                    });
                                }
                                catch (Exception ex)
                                {
                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        Log($"[MainWindow] Error decrypting or initializing OAuth tokens: {ex.Message}");
                                        Log($"[MainWindow] Stack trace: {ex.StackTrace}");
                                        
                                        // Отключаем сервис при ошибке инициализации
                                        DisconnectAmoCrmService();
                                        
                                        // Показываем предупреждение пользователю
                                        ShowKommoIntegrationWarning("oauth", ex.Message);
                                    });
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
                                        await _amoCrmService.InitializeAsync(settings.AmoCrmSubdomain, accessToken);
                                        
                                        initialized = true;
                                    }
                                    catch (UnauthorizedAccessException ex)
                                    {
                                        // КРИТИЧНО: Токен недействителен - показываем предупреждение пользователю
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            Log($"[MainWindow] Manual token is invalid or expired: {ex.Message}");
                                            
                                            // Отключаем сервис, так как токен недействителен
                                            DisconnectAmoCrmService();
                                            
                                            // Показываем предупреждение пользователю
                                            ShowKommoIntegrationWarning("manual", "Token is invalid or expired. Please check your access token in settings.");
                                        });
                                    }
                                    catch (Exception ex)
                                    {
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            Log($"[MainWindow] Error initializing Manual token: {ex.Message}");
                                            Log($"[MainWindow] Stack trace: {ex.StackTrace}");
                                            
                                            // Отключаем сервис при ошибке инициализации
                                            DisconnectAmoCrmService();
                                            
                                            // Показываем предупреждение пользователю
                                            ShowKommoIntegrationWarning("manual", ex.Message);
                                        });
                                    }
                                }
                            }
                        }
                        
                        if (initialized)
                        {
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
                            });
                        }
                        else
                        {
                            // Интеграция включена, но инициализация не удалась - показываем предупреждение
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (authMode == "oauth")
                                {
                                    Log("[MainWindow] OAuth initialization failed: token not found or decryption failed");
                                }
                                else
                                {
                                    Log("[MainWindow] Manual token initialization failed: token not found or decryption failed");
                                }
                                
                                // Показываем предупреждение пользователю
                                ShowKommoIntegrationWarning(authMode);
                            });
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
                        
                        // Если интеграция была включена, но произошла ошибка - показываем предупреждение
                        if (settings.EnableAmoCrmIntegration)
                        {
                            string authMode = settings.AmoCrmAuthMode ?? "manual";
                            ShowKommoIntegrationWarning(authMode, ex.Message);
                        }
                    });
                }
            });
        }
        
        /// <summary>
        /// Показывает предупреждение о проблеме с интеграцией Kommo
        /// </summary>
        private void ShowKommoIntegrationWarning(string authMode, string? errorDetails = null)
        {
            try
            {
                string message;
                if (authMode == "oauth")
                {
                    message = "Kommo integration is enabled, but authorization failed.\n\n";
                    message += "Please check your OAuth settings:\n";
                    message += "• Client ID\n";
                    message += "• Client Secret\n";
                    message += "• Redirect URI\n";
                    message += "• Make sure you have authorized the application\n\n";
                    if (!string.IsNullOrEmpty(errorDetails))
                    {
                        message += $"Error details: {errorDetails}\n\n";
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
                        settingsWindow = new SettingsWindow();
                        settingsWindow.Owner = this;
                    }
                    
                    // Переключаемся на вкладку Integrations
                    settingsWindow.Show();
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
            if (_amoCrmService == null || !_amoCrmService.IsInitialized)
            {
                return; // Интеграция не включена или не инициализирована
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

                if (_amoCrmService == null || !_amoCrmService.IsInitialized)
                {
                    _processedCalls.TryRemove(job.DedupKey, out _);
                    continue;
                }

                try
                {
                    var callHistory = _callHistoryService.GetHistory();
                    var call = callHistory
                        .Where(x => x.PhoneNumber == job.PhoneNumber && Math.Abs((x.CallTime - job.CallTime).TotalSeconds) < 1)
                        .OrderByDescending(x => x.CallTime)
                        .FirstOrDefault();
                    if (call == null)
                    {
                        call = callHistory
                            .Where(x => x.PhoneNumber == job.PhoneNumber)
                            .OrderByDescending(x => x.CallTime)
                            .FirstOrDefault();
                    }

                    bool isIncoming = call?.IsIncoming ?? false;
                    int durationSeconds = call?.Duration?.TotalSeconds != null ? (int)call.Duration.Value.TotalSeconds : 0;
                    // КРИТИЧНО: Если есть AnswerTime, значит произошло подключение (connect) - 
                    // либо абонент ответил, либо IVR/робот ответил. В этом случае считаем звонок принятым.
                    bool wasAnswered = call?.WasAnswered ?? false;
                    if (call?.AnswerTime.HasValue == true)
                    {
                        wasAnswered = true; // Если есть AnswerTime, значит был connect
                    }
                    var endedBy = call?.EndedBy ?? CallEndedBy.Unknown;
                    var ringbackStart = call?.RingbackStartTime;
                    var ringbackEnd = call?.RingbackEndTime;
                    string? callLog = job.TechnicalDetails != null && job.TechnicalDetails.Count > 0
                        ? string.Join("\n", job.TechnicalDetails)
                        : null;

                    string? finalRecordingPath = job.RecordingFilePath;

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
                    // Ждём появления файла записи до 5 минут (колл-центр: конвертация идёт по одной, предыдущий успеет)
                    if (!string.IsNullOrEmpty(job.RecordingFilePath) && !File.Exists(job.RecordingFilePath))
                    {
                        const int maxWaitSeconds = 300; // 5 минут
                        // Для отвеченных звонков ждем минимум 60 секунд (даже если звонок был коротким),
                        // так как конвертация записи может занять время, особенно для звонков с роботом
                        int minWaitSeconds = wasAnswered ? 60 : 30;
                        int waitSeconds = Math.Max(minWaitSeconds, Math.Min(maxWaitSeconds, Math.Max(durationSeconds, 30)));
                        Log($"[MainWindow] AmoCRM queue: waiting for recording file (up to {waitSeconds}s, wasAnswered={wasAnswered}, duration={durationSeconds}s): {job.RecordingFilePath}");
                        for (int i = 0; i < waitSeconds; i++)
                        {
                            await Task.Delay(1000).ConfigureAwait(false);
                            if (File.Exists(job.RecordingFilePath))
                            {
                                Log($"[MainWindow] AmoCRM queue: file ready after {i + 1}s");
                                break;
                            }
                        }
                        if (!File.Exists(job.RecordingFilePath))
                            Log($"[MainWindow] AmoCRM queue: file still not found after {waitSeconds}s, sending without recording");
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
                            job.CallTime).ConfigureAwait(false);
                    }
                    else
                    {
                        // Обычный звонок - используем автоматический поиск лида
                        result = await _amoCrmService.ProcessCallAsync(job.PhoneNumber, isIncoming, durationSeconds, wasAnswered, callLog, finalRecordingPath, enableLeadSelection, job.CallTime).ConfigureAwait(false);
                    }
                    
                    // Обновляем статус загрузки в истории звонков
                    if (call != null)
                    {
                        _callHistoryService.UpdateAmoCrmUploadStatus(job.PhoneNumber, call.CallTime, result.UploadStatus, result.Reason);
                        
                        if (result.Success && result.LeadId.HasValue)
                        {
                            _callHistoryService.UpdateAmoCrmLeadId(job.PhoneNumber, call.CallTime, result.LeadId.Value);
                            Log($"[MainWindow] AmoCRM: Successfully processed call for {job.PhoneNumber} (callTime: {job.CallTime:HH:mm:ss.fff}), Status: {result.UploadStatus}");
                            
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
                                            var detailsWindow = new CallDetailsWindow(call)
                                            {
                                                Owner = this
                                            };
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
                catch (UnauthorizedAccessException ex)
                {
                    // КРИТИЧНО: Токен истек или недействителен во время обработки звонка
                    Log($"[MainWindow] AmoCRM: UnauthorizedAccessException during call processing: {ex.Message}");
                    
                    // Отключаем сервис, так как токен недействителен
                    DisconnectAmoCrmService();
                    
                    // Определяем режим авторизации для правильного сообщения
                    string authMode = "oauth";
                    try
                    {
                        string settingsPath = AppDataHelper.GetSettingsFilePath();
                        if (File.Exists(settingsPath))
                        {
                            string json = File.ReadAllText(settingsPath);
                            var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                            authMode = settings?.AmoCrmAuthMode ?? "oauth";
                        }
                    }
                    catch
                    {
                        // Используем значение по умолчанию
                    }
                    
                    // Показываем предупреждение пользователю в UI потоке
                    Dispatcher.Invoke(() =>
                    {
                        string errorMessage = authMode == "oauth"
                            ? "Token is invalid or expired. Please re-authorize the application."
                            : "Token is invalid or expired. Please check your access token in settings.";
                        
                        ShowKommoIntegrationWarning(authMode, errorMessage);
                    });
                    
                    // Удаляем из словаря даже при ошибке, чтобы не блокировать повторную попытку через длительное время
                    _processedCalls.TryRemove(job.DedupKey, out _);
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
            public string? SessionId { get; set; } // WebRtcSessionId или SipCallId для различения разных звонков
            public long? AmoCrmLeadId { get; set; } // ID лида из браузера AmoCRM, если звонок инициирован из браузера
        }
    }
}

