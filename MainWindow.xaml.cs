using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

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
        
        public static void Log(string message)
        {
            if (_instance != null)
            {
                _instance.AddToLog(message);
            }
            // Также выводим в Debug для отладки
            System.Diagnostics.Debug.WriteLine(message);
        }

        public MainWindow()
        {
            InitializeComponent();
            _instance = this; // Сохраняем ссылку на экземпляр
            _callHistoryService = new CallHistoryService();
            ShowView(DialerView);
            
            // Устанавливаем начальный статус
            UpdateConnectionStatus();
            UpdateWebRtcIndicator();
            
            // Выделяем кнопку Dialer при запуске
            UpdateButtonSelection(DialerView);
            
            // Пытаемся подключиться и проверяем статус подключения
            _ = TryConnectAndCheckStatus();
            
            // Предварительно инициализируем WebView2 Environment для ускорения последующей инициализации
            _ = PreInitializeWebView2Environment();
            
            // Подписываемся на Loaded для инициализации singleton WebView2
            Loaded += MainWindow_Loaded;
            
            // Проверяем наличие новой версии при запуске (в фоне, без блокировки UI)
            _ = CheckForUpdatesOnStartupAsync();
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
                
                // Получаем данные репозитория
                string repositoryOwner = GetRepositoryOwner();
                string repositoryName = GetRepositoryName();
                
                // Пропускаем проверку, если репозиторий не настроен
                if (repositoryOwner == "YOUR_GITHUB_USERNAME" || repositoryName == "YOUR_REPOSITORY_NAME")
                {
                    Log("[MainWindow] GitHub repository not configured, skipping update check.");
                    return;
                }
                
                Log("[MainWindow] Checking for updates on startup...");
                
                // Получаем GitHub токен из защищенного провайдера
                string? githubToken = GitHubTokenProvider.GetToken();
                
                bool isNewVersionAvailable = await GitHubVersionService.IsNewVersionAvailableAsync(
                    repositoryOwner, repositoryName, githubToken);
                
                if (isNewVersionAvailable)
                {
                    Log("[MainWindow] New version available!");
                    
                    // Получаем информацию о последнем релизе
                    var latestRelease = await GitHubVersionService.CheckForUpdateAsync(
                        repositoryOwner, repositoryName, githubToken);
                    
                    if (latestRelease != null)
                    {
                        string currentVersion = GitHubVersionService.GetCurrentVersion();
                        string repositoryUrl = $"https://github.com/YOUR_Repository";
                        
                        // Показываем окно уведомления о новой версии
                        Dispatcher.Invoke(() =>
                        {
                            var updateWindow = new UpdateAvailableWindow(
                                latestRelease, currentVersion, repositoryUrl)
                            {
                                Owner = this
                            };
                            updateWindow.ShowDialog();
                        });
                    }
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
        
        /// <summary>
        /// Получает владельца репозитория GitHub
        /// </summary>
        private string GetRepositoryOwner()
        {
            return "USERNAME";
        }
        
        /// <summary>
        /// Получает название репозитория GitHub
        /// </summary>
        private string GetRepositoryName()
        {
            return "Softphone";
        }
        
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
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
                
                Log($"[MainWindow] InitializeWebRtcServiceAsync: Settings loaded - UseWebRtcAudio={settings.UseWebRtcAudio}, WebRtcWsUri={(string.IsNullOrEmpty(settings.WebRtcWsUri) ? "empty" : "set")}, SipUsername={(string.IsNullOrEmpty(settings.SipUsername) ? "empty" : "set")}, SipPassword={(string.IsNullOrEmpty(settings.SipPassword) ? "empty" : "set")}");
                
                if (!settings.UseWebRtcAudio || 
                    string.IsNullOrEmpty(settings.WebRtcWsUri) ||
                    string.IsNullOrEmpty(settings.SipUsername) || 
                    string.IsNullOrEmpty(settings.SipPassword))
                {
                    Log("[MainWindow] WebRTC not enabled or not configured, skipping service initialization");
                    return;
                }
                
                Log("[MainWindow] Initializing WebRTC service...");
                
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
                        settings.SipPassword ?? ""
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
        
        /// <summary>
        /// Обработчик событий WebRTC сервиса (UI координатор)
        /// </summary>
        private void OnWebRtcEvent(WebRtcEventDto dto)
        {
            try
            {
                Dispatcher.Invoke(() =>
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
                });
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] ERROR in OnWebRtcEvent: {ex.Message}");
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
                    
                    if (useWebRtc)
                    {
                        // Для WebRTC проверяем WebRTC настройки
                        hasConnectionSettings = settings != null && 
                            !string.IsNullOrEmpty(settings.WebRtcWsUri) && 
                            !string.IsNullOrEmpty(settings.SipUsername) && 
                            !string.IsNullOrEmpty(settings.SipPassword);
                    }
                    else
                    {
                        // Для SIP проверяем SIP настройки
                        hasConnectionSettings = settings != null && 
                            !string.IsNullOrEmpty(settings.SipServer) && 
                            !string.IsNullOrEmpty(settings.SipUsername) && 
                            !string.IsNullOrEmpty(settings.SipPassword);
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
                    
                    if (settings != null && !string.IsNullOrEmpty(settings.SipServer) && 
                        !string.IsNullOrEmpty(settings.SipUsername) && !string.IsNullOrEmpty(settings.SipPassword))
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
                    settings.SipPassword ?? "", 
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
                        
                        OnConnectionStatusChanged?.Invoke(status);
                        
                        // Добавляем в историю логов (полное техническое сообщение)
                        AddToLog(status);
                    });
                };
                
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
                // ВАЖНО: Сразу инициируем звонок, не переключаясь на главный экран
                // Это соответствует ожидаемому поведению пользователя
                await PerformCall(phoneNumber);
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
                
                bool useWebRtc = settings.UseWebRtcAudio && 
                                 !string.IsNullOrEmpty(settings.WebRtcWsUri) &&
                                 !string.IsNullOrEmpty(settings.SipUsername) && 
                                 !string.IsNullOrEmpty(settings.SipPassword);
                
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
                    
                    // Останавливаем SIP регистрацию (если была активна)
                    // При следующем вызове StartAsync() SIP не зарегистрируется (проверка ShouldUseWebRtc())
                    // Но старая регистрация может остаться активной, поэтому лучше явно переподключить
                    // Это вызовет StartAsync(), который проверит WebRTC режим и не зарегистрируется
                    if (_sipService != null)
                    {
                        try
                        {
                            Log("[MainWindow] ReconnectFromSettingsAsync: Reconnecting SIP to stop registration (WebRTC mode enabled)...");
                            // Вызываем TryConnectFromSettings, который вызовет StartAsync()
                            // StartAsync() проверит ShouldUseWebRtc() и не зарегистрируется
                            await TryConnectFromSettings();
                            Log("[MainWindow] ReconnectFromSettingsAsync: SIP reconnected (registration skipped due to WebRTC mode)");
                        }
                        catch (Exception ex)
                        {
                            Log($"[MainWindow] ReconnectFromSettingsAsync: Error reconnecting SIP: {ex.Message}");
                        }
                    }
                    
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
                                settings.SipPassword ?? ""
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
                    
                    // Переподключаем SIP сервис
                    await TryConnectFromSettings();
                    
                    // Обновляем статус подключения
                    Dispatcher.Invoke(() =>
                    {
                        UpdateConnectionStatus();
                        UpdateWebRtcIndicator();
                    });
                }
            }
            catch (Exception ex)
            {
                Log($"[MainWindow] ERROR in ReconnectFromSettingsAsync: {ex.Message}");
                Log($"[MainWindow] Stack trace: {ex.StackTrace}");
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
            // ВАЖНО: Входящие звонки через SIPSorcery всегда обрабатываются через SIPSorcery,
            // даже если включен WebRTC режим. WebRTC используется только для исходящих звонков.
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

        private async System.Threading.Tasks.Task PerformCall(string number)
        {
            if (string.IsNullOrEmpty(number))
            {
                CustomMessageBox.Show("Please enter a phone number.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                return;
            }

            // Проверяем, нужно ли использовать WebRTC
            bool useWebRtc = ShouldUseWebRtc();
            
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
                        
                        callWindow = new CallWindow(webRtcConfig, number, isIncomingCall: false, callStartTime: callStartTime)
                {
                    Owner = this
                        };
                        
                        // Инициируем звонок через WebRTC сервис
                        _ = WebRtcService.Instance.MakeCallAsync(number);
                    }
                    else
                    {
                        Log($"[Call][SIP] WebRTC config is null, falling back to SIPSorcery for call to {number}");
                        // Fallback на SIPSorcery
                        if (_sipService == null)
                        {
                            Log("[Call][SIP] ERROR: _sipService is null, cannot create CallWindow");
                            CallButton.IsEnabled = true;
                            return;
                        }
                        
                        // Добавляем запись в историю звонков (исходящий SIP звонок через fallback)
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
                        
                        callWindow = new CallWindow(_sipService, number, isIncomingCall: false, callStartTime: callStartTime)
                        {
                            Owner = this
                        };
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
                    
                    callWindow = new CallWindow(_sipService, number, isIncomingCall: false, callStartTime: callStartTime)
                    {
                        Owner = this
                    };
                }
                
                // Подписываемся на события обновления детальной информации о звонке для исходящих звонков
                callWindow.OnCallDetailsChanged += (phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId) =>
                {
                    _callHistoryService.UpdateCallDetails(phoneNumber, callTime, ringbackStart, ringbackEnd, answerTime, wasAnswered, endedBy, technicalDetails, duration, recordingFilePath, transport, sipCallId, webRtcSessionId);
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
                }
                
                CustomMessageBox.Show($"Call error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
                CallButton.IsEnabled = true;
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
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
        /// </summary>
        public void UpdateConnectionStatus()
        {
            bool useWebRtc = ShouldUseWebRtc();
            bool isConnected = IsConnected;
            string newStatus = "";
            
            if (useWebRtc)
            {
                // WebRTC режим
                if (isConnected)
                {
                    // ВАЖНО: Если WebRTC подключен и зарегистрирован, всегда обновляем статус на "Connected with WebRTC"
                    // независимо от текущего статуса (даже если он "Connecting...")
                    newStatus = "Connected with WebRTC";
                    
                    // ДИАГНОСТИКА: логируем перед обновлением
                    string oldStatus = StatusTextBlock.Text;
                    bool isReady = WebRtcService.Instance?.IsReadyForCalls ?? false;
                    Log($"[MainWindow] UpdateConnectionStatus (WebRTC connected): oldStatus='{oldStatus}', newStatus='{newStatus}', IsReadyForCalls={isReady}, IsConnected={isConnected}");
                    
                    StatusTextBlock.Text = newStatus;
                    UpdateStatusColor(true);
                    // Включаем кнопку вызова при готовности WebRTC
                    CallButton.IsEnabled = true;
                }
                else
                {
                    // WebRTC не подключен
                    // Проверяем, не идет ли инициализация (чтобы не перезаписать статус "Initializing..." или "Connecting...")
                    string currentStatus = StatusTextBlock.Text;
                    
                    // ДИАГНОСТИКА: логируем состояние для отладки
                    bool isReady = WebRtcService.Instance?.IsReadyForCalls ?? false;
                    Log($"[MainWindow] UpdateConnectionStatus (WebRTC not connected): currentStatus='{currentStatus}', IsReadyForCalls={isReady}, IsConnected={isConnected}");
                    
                    if (!currentStatus.Contains("Initializing") && 
                        !currentStatus.Contains("Connecting") && 
                        !currentStatus.Contains("Connected to WebRTC"))
                    {
                        newStatus = "Not connected";
                        StatusTextBlock.Text = newStatus;
                        UpdateStatusColor(false);
                    }
                    else
                    {
                        newStatus = currentStatus; // Сохраняем текущий статус инициализации
                    }
                    // Отключаем кнопку вызова если WebRTC не готов
                    CallButton.IsEnabled = false;
                }
            }
            else
            {
                // SIP режим
                if (isConnected)
                {
                    newStatus = "Connected with SIP";
                    StatusTextBlock.Text = newStatus;
                    UpdateStatusColor(true);
                    // Включаем кнопку вызова при подключении SIP
                    CallButton.IsEnabled = true;
                }
                else
                {
                    newStatus = "Not connected";
                    StatusTextBlock.Text = newStatus;
                    UpdateStatusColor(false);
                    // Отключаем кнопку вызова если SIP не подключен
                    CallButton.IsEnabled = false;
                }
            }
            
            // Обновляем флаг последнего состояния подключения
            _lastIsConnected = isConnected;
            
            // Логируем только при изменении статуса (для уменьшения избыточности логов)
            if (_lastConnectionStatus != newStatus)
            {
                Log($"[MainWindow] UpdateConnectionStatus: Status changed to '{newStatus}' (useWebRtc={useWebRtc}, isConnected={isConnected}, IsReadyForCalls={WebRtcService.Instance?.IsReadyForCalls ?? false})");
                _lastConnectionStatus = newStatus;
                
                // ВАЖНО: Уведомляем SettingsWindow об изменении статуса для синхронизации
                // Это гарантирует, что статус в Connection tab обновляется синхронно с MainWindow
                OnConnectionStatusChanged?.Invoke(newStatus);
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
                    
                    if (settings != null && !string.IsNullOrEmpty(settings.WebRtcWsUri) &&
                        !string.IsNullOrEmpty(settings.SipUsername) && !string.IsNullOrEmpty(settings.SipPassword))
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
                            Password = settings.SipPassword
                        };
                        Log($"[MainWindow] GetWebRtcConfig: Created config - WsUri={config.WsUri}, SipUri={config.SipUri}");
                        return config;
                    }
                    else
                    {
                        Log($"[MainWindow] GetWebRtcConfig: Missing required settings - WebRtcWsUri={(string.IsNullOrEmpty(settings?.WebRtcWsUri) ? "empty" : "set")}, SipUsername={(string.IsNullOrEmpty(settings?.SipUsername) ? "empty" : "set")}, SipPassword={(string.IsNullOrEmpty(settings?.SipPassword) ? "empty" : "set")}");
                    }
                }
            }
            catch
            {
                // Игнорируем ошибки
            }
            return null;
        }
    }
}

