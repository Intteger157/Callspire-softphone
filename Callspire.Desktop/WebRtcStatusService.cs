#if WINDOWS
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Softphone;

namespace Softphone
{
    /// <summary>
    /// Сервис для проверки статуса подключения WebRTC
    /// </summary>
    public class WebRtcStatusService : IDisposable
    {
        private WebView2? _webView;
        private Window? _parentWindow;
        private WebRtcConnectionStatus _currentStatus = WebRtcConnectionStatus.NotConnected;
        private WebRtcConfig? _config;
        private DateTime? _navigationStartTime;
        
        // Синхронизация инициализации
        private Task? _initTask;
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? _cts;
        private bool _disposed = false;
        private bool _isStopped = false; // Флаг для предотвращения множественных остановок

        // Keep delegates so we can unsubscribe (prevents WebView2/event-handler leaks when reattaching views).
        private EventHandler<CoreWebView2InitializationCompletedEventArgs>? _coreInitCompletedHandler;
        
        // Singleton для Environment
        private static CoreWebView2Environment? _sharedEnv;
        private static readonly SemaphoreSlim _envLock = new SemaphoreSlim(1, 1);
        
        public event Action<WebRtcConnectionStatus>? OnStatusChanged;
        
        public WebRtcConnectionStatus CurrentStatus => _currentStatus;
        
        public WebRtcConfig? CurrentConfig => _config;
        
        public void SetParentWindow(Window? parent)
        {
            _parentWindow = parent;
        }
        
        public void AttachWebView(WebView2 webView)
        {
            // If we are reattaching to a different WebView2 (e.g. SettingsWindow reopened),
            // detach handlers from the previous instance to avoid leaks and duplicate events.
            try
            {
                DetachWebViewHandlersNoThrow();
            }
            catch { }

            _webView = webView;
            MainWindow.Log("[WebRTC] WebView2 attached from XAML");
        }

        private void DetachWebViewHandlersNoThrow()
        {
            try
            {
                if (_webView == null) return;

                try { _webView.NavigationCompleted -= WebView_NavigationCompleted; } catch { }

                try
                {
                    if (_coreInitCompletedHandler != null)
                    {
                        _webView.CoreWebView2InitializationCompleted -= _coreInitCompletedHandler;
                    }
                }
                catch { }

                try
                {
                    var core = _webView.CoreWebView2;
                    if (core != null)
                    {
                        core.WebMessageReceived -= WebView_WebMessageReceived;
                    }
                }
                catch { }
            }
            catch
            {
                // ignore
            }
        }
        
        private static async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (_sharedEnv != null) return _sharedEnv;
            
            await _envLock.WaitAsync();
            try
            {
                if (_sharedEnv != null) return _sharedEnv;
                
                // MUST differ from MainWindow's WebRTC WebView2 folder (…\WebView2). Two concurrent
                // environments on the same user-data path deadlock or hang EnsureCoreWebView2Async.
                var userData = Path.Combine(
                    AppDataHelper.GetAppDataPath(),
                    "WebView2SettingsTest");
                
                Directory.CreateDirectory(userData);
                MainWindow.Log($"[WebRTC] Creating Settings-test WebView2 Environment in: {userData}");
                
                // WebRTC in Chromium/WebView2 can pick VPN/virtual interfaces (e.g. 198.18.0.0/15),
                // which breaks TURN allocation/connection (no relay candidates) and causes initial IVR audio loss.
                // These flags reduce that by limiting which interfaces are exposed/used by WebRTC.
                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments =
                        "--force-webrtc-ip-handling-policy=default_public_interface_only " +
                        "--webrtc-ip-handling-policy=default_public_interface_only"
                };
                _sharedEnv = await CoreWebView2Environment.CreateAsync(null, userData, options);
                MainWindow.Log("[WebRTC] Settings-test WebView2 Environment created successfully");
                
                return _sharedEnv;
            }
            finally 
            { 
                _envLock.Release(); 
            }
        }
        
        public async Task InitializeAsync(WebRtcConfig config)
        {
            await _initLock.WaitAsync();
            try
            {
                if (_disposed)
                {
                    MainWindow.Log("[WebRTC] Service is disposed, cannot initialize");
                    return;
                }
                
                // Сбрасываем флаг остановки при новой инициализации
                _isStopped = false;
                
                // Если уже идет инициализация, ждем ее завершения
                if (_initTask != null && !_initTask.IsCompleted)
                {
                    MainWindow.Log("[WebRTC] Initialization already in progress, waiting for completion...");
                    await _initTask;
                    return;
                }
                
                // Отменяем предыдущую инициализацию, если она была
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                
                // Запускаем новую инициализацию
                _initTask = InitializeInternalAsync(config, _cts.Token);
            }
            finally 
            { 
                _initLock.Release(); 
            }
            
            try
            {
                await _initTask;
            }
            catch (OperationCanceledException)
            {
                MainWindow.Log("[WebRTC] Initialization was cancelled");
            }
        }
        
        private async Task InitializeInternalAsync(WebRtcConfig config, CancellationToken cancellationToken)
        {
            var totalStartTime = DateTime.Now;
            _config = config;
            
            // Логируем только кратко для тестирования в настройках (не засоряем логи подробными шагами)
            MainWindow.Log($"[WebRTC] Testing connection: {config.SipUri}");
            
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Устанавливаем статус "Инициализация WebView2"
                UpdateStatus(WebRtcConnectionStatus.InitializingWebView2);
                
                // Проверяем доступность WebView2 Runtime
                try
                {
                    var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                    // Не логируем версию - это не критично для обычного использования
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[WebRTC] ERROR: WebView2 Runtime not found: {ex.Message}");
                    UpdateStatus(WebRtcConnectionStatus.Error);
                    return;
                }
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // Получаем общий Environment
                var env = await GetEnvironmentAsync();
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // Проверяем, что WebView2 прикреплен из XAML
                if (_webView == null)
                {
                    MainWindow.Log("[WebRTC] ERROR: WebView2 is not attached");
                    UpdateStatus(WebRtcConnectionStatus.Error);
                    return;
                }
                
                if (_disposed)
                {
                    MainWindow.Log("[WebRTC] ERROR: Service is disposed");
                    UpdateStatus(WebRtcConnectionStatus.Error);
                    return;
                }
                
                // Подписываемся на событие завершения инициализации (до Ensure).
                // Держим delegate в поле, чтобы потом корректно отписаться в Dispose/reattach.
                _coreInitCompletedHandler ??= WebView_CoreWebView2InitializationCompleted;
                try { _webView.CoreWebView2InitializationCompleted -= _coreInitCompletedHandler; } catch { }
                _webView.CoreWebView2InitializationCompleted += _coreInitCompletedHandler;
                
                // Подписываемся на NavigationCompleted на уровне WebView2 (до Source)
                _webView.NavigationCompleted += WebView_NavigationCompleted;
                
                // Проверяем, что WebView2 загружен и готов к инициализации
                var visibilityStartTime = DateTime.Now;
                bool isWebViewReady = false;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || cancellationToken.IsCancellationRequested)
                        return;
                    
                    // Проверяем, что WebView2 в визуальном дереве
                    if (_webView != null)
                    {
                        // Если WebView2 скрыт (Hidden), делаем его видимым на время init
                        if (_webView.Visibility == Visibility.Hidden || _webView.Visibility == Visibility.Collapsed)
                        {
                            _webView.Visibility = Visibility.Visible; // На время init
                        }
                        
                        // Принудительно обновляем layout несколько раз для надежности
                        _webView.UpdateLayout();
                        
                        // Проверяем, что элемент действительно в визуальном дереве и загружен
                        isWebViewReady = _webView.Parent != null && (_webView.IsLoaded || _webView.Parent is FrameworkElement parent && parent.IsLoaded);
                        
                        if (!isWebViewReady)
                        {
                            // Пытаемся еще раз обновить layout и проверить
                            _webView.UpdateLayout();
                            isWebViewReady = _webView.Parent != null;
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Loaded);
                
                if (!isWebViewReady)
                {
                    MainWindow.Log("[WebRTC] WARNING: WebView2 may not be ready, waiting a bit more...");
                    // Ждем еще немного и проверяем снова
                    await Task.Delay(200);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_webView != null)
                        {
                            _webView.UpdateLayout();
                            isWebViewReady = _webView.Parent != null && (_webView.IsLoaded || _webView.Parent is FrameworkElement parent && parent.IsLoaded);
                        }
                    });
                }
                
                // WebView2 готов к инициализации (не логируем подробности)
                
                cancellationToken.ThrowIfCancellationRequested();
                
                var initStartTime = DateTime.Now;
                
                // Статус уже установлен в InitializingWebView2 выше
                
                // Инициализируем с явным Environment
                if (_webView == null)
                {
                    throw new InvalidOperationException("WebView2 is null");
                }
                var initTask = _webView.EnsureCoreWebView2Async(env);
                var timeoutTask = Task.Delay(15000, cancellationToken); // 15 секунд таймаут (достаточно для нормальной инициализации)
                
                var completedTask = await Task.WhenAny(initTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    var elapsed = (DateTime.Now - initStartTime).TotalSeconds;
                    MainWindow.Log($"[WebRTC] WARNING: WebView2 initialization timeout after {elapsed:F1} seconds, but continuing...");
                    // Не устанавливаем Error сразу - возможно инициализация еще продолжается
                    // Продолжаем ждать инициализацию в фоне, но не меняем статус на Error
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Ждем еще до 10 секунд (всего до 25 секунд)
                            var extendedTimeout = Task.Delay(10000);
                            var finalTask = await Task.WhenAny(initTask, extendedTimeout);
                            
                            if (finalTask == extendedTimeout)
                            {
                                // Проверяем состояние WebView2 через Dispatcher
                                bool isInitialized = false;
                                await Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    isInitialized = _webView?.CoreWebView2 != null;
                                });
                                
                                if (!isInitialized)
                                {
                                    var totalElapsed = (DateTime.Now - initStartTime).TotalSeconds;
                                    MainWindow.Log($"[WebRTC] WARNING: WebView2 still not initialized after {totalElapsed:F1} seconds total");
                                    // Не устанавливаем Error - оставляем статус InitializingWebView2
                                    // Возможно, инициализация все еще продолжается
                                }
                                else
                                {
                                    var totalElapsed = (DateTime.Now - initStartTime).TotalSeconds;
                                    MainWindow.Log($"[WebRTC] WebView2 initialized after {totalElapsed:F1} seconds, continuing...");
                                    // Продолжаем инициализацию через Dispatcher
                                    await Application.Current.Dispatcher.InvokeAsync(async () =>
                                    {
                                        if (!_disposed && !cancellationToken.IsCancellationRequested && _webView?.CoreWebView2 != null)
                                        {
                                            await ContinueInitializationAfterWebView2Ready(cancellationToken);
                                        }
                                    });
                                }
                            }
                            else
                            {
                                // Инициализация завершилась успешно
                                var totalElapsed = (DateTime.Now - initStartTime).TotalSeconds;
                                MainWindow.Log($"[WebRTC] WebView2 initialized after {totalElapsed:F1} seconds, continuing...");
                                await Application.Current.Dispatcher.InvokeAsync(async () =>
                                {
                                    if (!_disposed && !cancellationToken.IsCancellationRequested && _webView?.CoreWebView2 != null)
                                    {
                                        await ContinueInitializationAfterWebView2Ready(cancellationToken);
                                    }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"[WebRTC] Error in extended initialization wait: {ex.Message}");
                            MainWindow.Log($"[WebRTC] Exception type: {ex.GetType().Name}");
                            if (ex.StackTrace != null)
                            {
                                MainWindow.Log($"[WebRTC] Stack trace: {ex.StackTrace}");
                            }
                            // НЕ устанавливаем Error - оставляем текущий статус
                            // Возможно, инициализация все еще продолжается
                        }
                    });
                    // Не возвращаемся сразу - оставляем статус InitializingWebView2
                    return;
                }
                
                cancellationToken.ThrowIfCancellationRequested();
                
                try
                {
                    await initTask; // Получаем результат
                    // WebView2 инициализирован успешно (не логируем подробности)
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[WebRTC] WARNING: WebView2 initialization exception: {ex.Message}");
                    MainWindow.Log($"[WebRTC] Exception type: {ex.GetType().Name}");
                    // Не устанавливаем Error сразу - проверяем, может быть WebView2 все же инициализирован
                    // Проверяем через Dispatcher для безопасности потоков
                    bool isInitialized = false;
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        isInitialized = _webView?.CoreWebView2 != null;
                    });
                    
                    if (!isInitialized)
                    {
                        MainWindow.Log("[WebRTC] WebView2 CoreWebView2 is not available, but not setting Error - initialization may continue");
                        // Не устанавливаем Error - оставляем текущий статус InitializingWebView2
                        return;
                    }
                    else
                    {
                        MainWindow.Log("[WebRTC] WebView2 CoreWebView2 is available despite exception, continuing...");
                        // Продолжаем инициализацию
                    }
                }
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // Продолжаем инициализацию после успешной инициализации WebView2
                await ContinueInitializationAfterWebView2Ready(cancellationToken);
                // Инициализация WebRTC завершена (не логируем подробности)
            }
            catch (OperationCanceledException)
            {
                MainWindow.Log("[WebRTC] Initialization cancelled");
                // Не устанавливаем Error при отмене - возможно, это временная отмена
                // Скрываем обратно при отмене
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_webView != null)
                        _webView.Visibility = Visibility.Hidden;
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] WARNING: Exception during initialization: {ex.Message}");
                MainWindow.Log($"[WebRTC] Exception type: {ex.GetType().Name}");
                if (ex.StackTrace != null)
                {
                    MainWindow.Log($"[WebRTC] Stack trace: {ex.StackTrace}");
                }
                // Не устанавливаем Error сразу - проверяем, может быть WebView2 все же инициализирован
                bool isInitialized = false;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    isInitialized = _webView?.CoreWebView2 != null;
                });
                
                if (!isInitialized)
                {
                    MainWindow.Log("[WebRTC] WebView2 not initialized, but not setting Error - may continue later");
                    // НЕ устанавливаем Error - оставляем текущий статус InitializingWebView2
                }
                else
                {
                    MainWindow.Log("[WebRTC] WebView2 is initialized despite exception, continuing...");
                    // Пытаемся продолжить инициализацию
                    try
                    {
                        await ContinueInitializationAfterWebView2Ready(cancellationToken);
                    }
                    catch
                    {
                        // Игнорируем ошибки при продолжении
                    }
                }
                
                // Скрываем обратно при ошибке
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_webView != null)
                        _webView.Visibility = Visibility.Hidden;
                });
            }
        }
        
        private async Task ContinueInitializationAfterWebView2Ready(CancellationToken cancellationToken)
        {
            // Проверяем, что инициализация прошла успешно
            // Используем Dispatcher для безопасного доступа к UI элементу
            bool isInitialized = false;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                isInitialized = _webView?.CoreWebView2 != null;
            });
            
            if (!isInitialized)
            {
                MainWindow.Log("[WebRTC] WARNING: WebView2 CoreWebView2 is null, waiting a bit more...");
                // Ждем еще немного - возможно, инициализация еще не завершилась
                await Task.Delay(500, cancellationToken);
                
                // Проверяем еще раз через Dispatcher
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    isInitialized = _webView?.CoreWebView2 != null;
                });
                
                if (!isInitialized)
                {
                    MainWindow.Log("[WebRTC] WARNING: WebView2 CoreWebView2 is still null after wait");
                    // НЕ устанавливаем Error - оставляем текущий статус InitializingWebView2
                    // Возможно, инициализация все еще продолжается
                    return;
                }
            }
            
            // Настраиваем виртуальный хост
            // КРИТИЧНО: В single-file режиме AppDomain.CurrentDomain.BaseDirectory указывает на временную папку распаковки
            // Используем AppContext.BaseDirectory для правильного пути к exe в single-file режиме
            // В single-file режиме Assembly.Location всегда пустая строка, поэтому используем AppContext.BaseDirectory
            string baseDirectory = AppContext.BaseDirectory;
            
            // Дополнительная проверка: если AppContext.BaseDirectory указывает на временную папку,
            // пробуем получить путь к exe через Process.GetCurrentProcess().MainModule.FileName
            if (baseDirectory.Contains("Temp") || baseDirectory.Contains(".net"))
            {
                try
                {
                    var processPath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(processPath))
                    {
                        var exeDirectory = Path.GetDirectoryName(processPath);
                        if (!string.IsNullOrEmpty(exeDirectory) && Directory.Exists(exeDirectory))
                        {
                            baseDirectory = exeDirectory;
                        }
                    }
                }
                catch
                {
                    // Оставляем AppContext.BaseDirectory
                }
            }
            
            var webRtcClientPath = Path.Combine(baseDirectory, "WebRtcClient");
            MainWindow.Log($"[WebRTC] Base directory: {baseDirectory}");
            MainWindow.Log($"[WebRTC] WebRtcClient path: {webRtcClientPath}");
            
            if (Directory.Exists(webRtcClientPath) && _webView?.CoreWebView2 != null)
            {
                MainWindow.Log($"[WebRTC] Setting virtual host mapping: softphone.local -> {webRtcClientPath}");
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "softphone.local",
                    webRtcClientPath,
                    CoreWebView2HostResourceAccessKind.Allow);
                
                _navigationStartTime = DateTime.Now;
                // WebRtcStatusService runs an isolated test session, so it loads the slot
                // page directly (bypassing the multi-slot index.html iframe container).
                _webView.Source = new Uri("https://softphone.local/slot.html?slot=test");
                MainWindow.Log("[WebRTC] Loading WebRTC client page...");
            }
            else
            {
                MainWindow.Log($"[WebRTC] WARNING: WebRtcClient folder not found at: {webRtcClientPath}");
                MainWindow.Log($"[WebRTC] Checking if directory exists: {Directory.Exists(webRtcClientPath)}");
                
                // Пробуем альтернативные пути
                var altPath1 = Path.Combine(AppContext.BaseDirectory, "WebRtcClient");
                var altPath2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebRtcClient");
                MainWindow.Log($"[WebRTC] Alternative path 1 (AppContext): {altPath1}, exists: {Directory.Exists(altPath1)}");
                MainWindow.Log($"[WebRTC] Alternative path 2 (AppDomain): {altPath2}, exists: {Directory.Exists(altPath2)}");
                // Не устанавливаем Error - возможно, папка появится позже
                // Статус остается InitializingWebView2
                // Скрываем обратно при ошибке
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_webView != null)
                        _webView.Visibility = Visibility.Hidden;
                });
                return;
            }
            
            // Подписываемся на сообщения
            try { _webView.CoreWebView2.WebMessageReceived -= WebView_WebMessageReceived; } catch { }
            _webView.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;
            
            // Статус будет обновлен в InitWebRtcClientAsync -> InitializingJsSIP
            // и затем в WebView_WebMessageReceived при получении ua_started -> ConnectingToWebSocket
            MainWindow.Log("[WebRTC] Status: Loading WebRTC client page...");
        }

        private void WebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            try
            {
                if (!e.IsSuccess)
                {
                    MainWindow.Log($"[WebRTC] ERROR: WebView2 initialization failed: {e.InitializationException?.Message ?? "Unknown error"}");
                    UpdateStatus(WebRtcConnectionStatus.Error);
                }
                // Success is logged in NavigationCompleted.
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] ERROR in CoreWebView2InitializationCompleted handler: {ex.Message}");
            }
        }
        
        private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                if (e.IsSuccess)
                {
                    var elapsed = _navigationStartTime.HasValue ? (DateTime.Now - _navigationStartTime.Value).TotalSeconds : 0;
                    // WebRTC client page loaded (не логируем подробности)
                    if (_config != null)
                    {
                        await InitWebRtcClientAsync(_config);
                        // JsSIP client initialization completed (не логируем подробности)
                    }
                }
                else
                {
                    MainWindow.Log($"[WebRTC] WARNING: Failed to load WebRTC client page. HTTP Status: {e.HttpStatusCode}");
                    // Не устанавливаем Error - возможно, это временная ошибка загрузки
                    // Статус остается InitializingJsSIP или ConnectingToWebSocket
                }
                
                // Скрываем обратно после загрузки
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_webView != null)
                        _webView.Visibility = Visibility.Hidden;
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] ERROR in NavigationCompleted handler: {ex.Message}");
            }
        }
        
        private async Task InitWebRtcClientAsync(WebRtcConfig config)
        {
            try
            {
                if (_webView?.CoreWebView2 == null || _disposed)
                {
                    MainWindow.Log("[WebRTC] Cannot initialize JsSIP client: WebView2 is null or disposed");
                    return;
                }
                
                // Устанавливаем статус "Инициализация JsSIP"
                UpdateStatus(WebRtcConnectionStatus.InitializingJsSIP);
                
                MainWindow.Log("[WebRTC] Initializing JsSIP client in WebView2...");
                
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                
                var script = $"window.SoftphoneWebRtc.init({json});";
                // Executing init script (не логируем подробности)
                await _webView.CoreWebView2.ExecuteScriptAsync(script);
                // Init script executed (не логируем подробности)
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] WARNING: Failed to initialize WebRTC client: {ex.Message}");
                MainWindow.Log($"[WebRTC] Exception type: {ex.GetType().Name}");
                if (ex.StackTrace != null)
                {
                    MainWindow.Log($"[WebRTC] Stack trace: {ex.StackTrace}");
                }
                // Не устанавливаем Error - возможно, это временная ошибка
                // Статус остается InitializingJsSIP или ConnectingToWebSocket
            }
        }
        
        private void WebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string? json = null;
            try
            {
                if (_disposed) return;
                
                json = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(json))
                {
                    MainWindow.Log("[WebRTC] Received empty message from WebView2");
                    return;
                }
                
                WebRtcEvent? evt = null;
                try
                {
                    evt = JsonSerializer.Deserialize<WebRtcEvent>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch (JsonException jsonEx)
                {
                    MainWindow.Log($"[WebRTC] JSON deserialization error: {jsonEx.Message}");
                    MainWindow.Log($"[WebRTC] JSON content: {json}");
                    return;
                }
                
                if (evt == null)
                {
                    MainWindow.Log("[WebRTC] Failed to deserialize event (result is null)");
                    return;
                }
                
                if (string.IsNullOrEmpty(evt.Type))
                {
                    MainWindow.Log("[WebRTC] Event type is null or empty");
                    return;
                }
                
                switch (evt.Type)
                {
                    case "ua_started":
                        MainWindow.Log("[WebRTC] JsSIP UA started, attempting to connect to WebSocket...");
                        // Устанавливаем статус "Подключение к WebSocket"
                        UpdateStatus(WebRtcConnectionStatus.ConnectingToWebSocket);
                        break;
                    case "ua_connected":
                        MainWindow.Log("[WebRTC] ✓ WebSocket connection established to ATS server");
                        UpdateStatus(WebRtcConnectionStatus.Connected);
                        break;
                    case "ua_registered":
                        MainWindow.Log($"[WebRTC] ✓ Successfully registered on ATS server as {_config?.SipUri}");
                        UpdateStatus(WebRtcConnectionStatus.Registered);
                        break;
                    case "ua_unregistered":
                        MainWindow.Log("[WebRTC] Unregistered from ATS server");
                        UpdateStatus(WebRtcConnectionStatus.Disconnected);
                        break;
                    case "ua_registration_failed":
                        var errorData = evt.Data.HasValue ? evt.Data.Value.ToString() : "Unknown error";
                        MainWindow.Log($"[WebRTC] ✗ Registration failed on ATS server: {errorData}");
                        UpdateStatus(WebRtcConnectionStatus.RegistrationFailed);
                        break;
                    case "ua_disconnected":
                        var disconnectDetails = evt.Data.HasValue ? evt.Data.Value.ToString() : "No details";
                        MainWindow.Log($"[WebRTC] ✗ WebSocket disconnected from ATS server. Details: {disconnectDetails}");
                        UpdateStatus(WebRtcConnectionStatus.Disconnected);
                        break;
                    case "ws_test_open":
                        MainWindow.Log("[WebRTC] ✓ WebSocket test: WSS connection is accessible");
                        break;
                    case "ws_test_close":
                        var closeDetails = evt.Data.HasValue ? evt.Data.Value.ToString() : "No details";
                        MainWindow.Log($"[WebRTC] WebSocket test closed. Details: {closeDetails}");
                        break;
                    case "ws_test_error":
                        try
                        {
                            string wsError = "Unknown error";
                            if (evt.Data.HasValue)
                            {
                                var dataValue = evt.Data.Value;
                                if (dataValue.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    wsError = dataValue.GetString() ?? "Unknown error";
                                }
                                else
                                {
                                    wsError = dataValue.ToString();
                                }
                            }
                            MainWindow.Log($"[WebRTC] ✗ WebSocket test error: {wsError}");
                            // НЕ меняем статус на Error при ошибке тестового WebSocket,
                            // так как JsSIP UA может успешно подключиться позже
                            // UpdateStatus(WebRtcConnectionStatus.Error);
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"[WebRTC] ✗ WebSocket test error (failed to parse): {ex.Message}");
                            // НЕ меняем статус на Error при ошибке тестового WebSocket
                            // UpdateStatus(WebRtcConnectionStatus.Error);
                        }
                        break;
                    case "ws_test_exception":
                        var wsException = evt.Data.HasValue ? evt.Data.Value.ToString() : "Unknown exception";
                        MainWindow.Log($"[WebRTC] ✗ WebSocket test exception: {wsException}");
                        UpdateStatus(WebRtcConnectionStatus.Error);
                        break;
                    case "error":
                        var errorMsg = evt.Data.HasValue ? evt.Data.Value.ToString() : "Unknown error";
                        MainWindow.Log($"[WebRTC] ✗ ERROR: {errorMsg}");
                        UpdateStatus(WebRtcConnectionStatus.Error);
                        break;
                    default:
                        MainWindow.Log($"[WebRTC] Unknown event type: {evt.Type}");
                        break;
                }
            }
            catch (JsonException jsonEx)
            {
                MainWindow.Log($"[WebRTC] JSON deserialization error: {jsonEx.Message}");
                MainWindow.Log($"[WebRTC] JSON: {json}");
                // Не падаем при ошибке десериализации, просто логируем
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] ERROR: Failed to process WebRTC message: {ex.Message}");
                MainWindow.Log($"[WebRTC] Exception type: {ex.GetType().Name}");
                if (ex.StackTrace != null)
                {
                    MainWindow.Log($"[WebRTC] Stack trace: {ex.StackTrace}");
                }
                // Не падаем при ошибке обработки, просто логируем
            }
        }
        
        private void UpdateStatus(WebRtcConnectionStatus status)
        {
            try
            {
                if (_currentStatus != status && !_disposed)
                {
                    _currentStatus = status;
                    // Безопасный вызов события с обработкой исключений
                    try
                    {
                        OnStatusChanged?.Invoke(status);
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[WebRTC] ERROR in OnStatusChanged handler: {ex.Message}");
                        MainWindow.Log($"[WebRTC] Exception type: {ex.GetType().Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] ERROR in UpdateStatus: {ex.Message}");
                MainWindow.Log($"[WebRTC] Exception type: {ex.GetType().Name}");
            }
        }
        
        public async Task CheckConnectionAsync(WebRtcConfig config)
        {
            await InitializeAsync(config);
        }
        
        /// <summary>
        /// Останавливает WebRTC подключение
        /// </summary>
        public async Task StopAsync()
        {
            // Делаем метод идемпотентным - если уже остановлен, не делаем ничего
            if (_isStopped || _disposed)
            {
                return;
            }
            
            try
            {
                if (_webView?.CoreWebView2 != null)
                {
                    _isStopped = true; // Устанавливаем флаг до выполнения остановки
                    MainWindow.Log("[WebRTC] Stopping WebRTC connection...");
                    // Вызываем JavaScript метод stop() для остановки UA
                    await _webView.CoreWebView2.ExecuteScriptAsync("if (window.SoftphoneWebRtc && window.SoftphoneWebRtc.stop) { window.SoftphoneWebRtc.stop(); }");
                    UpdateStatus(WebRtcConnectionStatus.Disconnected);
                    MainWindow.Log("[WebRTC] WebRTC connection stopped");
                }
                else
                {
                    _isStopped = true; // Устанавливаем флаг даже если WebView не готов
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] Error stopping WebRTC connection: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Синхронная версия Stop для использования в UI обработчиках
        /// </summary>
        public void Stop()
        {
            // Делаем метод идемпотентным - если уже остановлен, не делаем ничего
            if (_isStopped || _disposed)
            {
                return;
            }
            
            if (_webView?.CoreWebView2 != null)
            {
                _isStopped = true; // Устанавливаем флаг до вызова StopAsync
                _ = StopAsync(); // Запускаем асинхронно без ожидания
            }
            else
            {
                _isStopped = true; // Устанавливаем флаг даже если WebView не готов
            }
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            
            try
            {
                _disposed = true;
                
                // Отменяем текущую инициализацию
                _cts?.Cancel();

                // Best-effort: don't block UI thread waiting for init; cancel + give it a short window off-UI.
                if (_initTask != null && !_initTask.IsCompleted)
                {
                    try
                    {
                        if (!Application.Current.Dispatcher.CheckAccess())
                        {
                            _initTask.Wait(TimeSpan.FromMilliseconds(500));
                        }
                    }
                    catch { }
                }
                
                if (_webView != null)
                {
                    // Detach handlers to avoid leaks / duplicate events on reattach.
                    try { DetachWebViewHandlersNoThrow(); } catch { }

                    // Удаляем WebView2 из визуального дерева перед Dispose
                    if (_parentWindow != null && _parentWindow.IsLoaded)
                    {
                        try
                        {
                            if (Application.Current.Dispatcher.CheckAccess())
                            {
                                if (_parentWindow.Content is System.Windows.Controls.Grid mainGrid)
                                {
                                    for (int i = mainGrid.Children.Count - 1; i >= 0; i--)
                                    {
                                        if (mainGrid.Children[i] is System.Windows.Controls.Grid container &&
                                            container.Children.Contains(_webView))
                                        {
                                            container.Children.Remove(_webView);
                                            mainGrid.Children.Remove(container);
                                            break;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Don't deadlock: schedule removal and wait briefly at most.
                                var removeTask = Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    if (_parentWindow?.Content is System.Windows.Controls.Grid mainGrid)
                                    {
                                        for (int i = mainGrid.Children.Count - 1; i >= 0; i--)
                                        {
                                            if (mainGrid.Children[i] is System.Windows.Controls.Grid container &&
                                                _webView != null &&
                                                container.Children.Contains(_webView))
                                            {
                                                container.Children.Remove(_webView);
                                                mainGrid.Children.Remove(container);
                                                break;
                                            }
                                        }
                                    }
                                }, DispatcherPriority.Background);

                                try { removeTask.Task.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
                            }
                        }
                        catch { }
                    }
                    
                    try { _webView.Dispose(); } catch { }
                    _webView = null;
                }
                
                _parentWindow = null;
                _cts?.Dispose();
                MainWindow.Log("[WebRTC] WebRtcStatusService disposed");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] Error disposing WebRtcStatusService: {ex.Message}");
            }
        }
    }
    
    public enum WebRtcConnectionStatus
    {
        NotConnected,
        InitializingWebView2,      // Инициализация WebView2
        InitializingJsSIP,          // Инициализация JsSIP клиента
        ConnectingToWebSocket,      // Подключение к WebSocket
        Connecting,                 // Общий статус подключения (для обратной совместимости)
        Connected,                  // WebSocket подключен
        Registered,                 // Зарегистрирован на сервере
        Disconnected,
        RegistrationFailed,
        Error
    }
}

#endif // WINDOWS
