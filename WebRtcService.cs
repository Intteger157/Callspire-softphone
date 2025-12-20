using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;

namespace Softphone
{
    /// <summary>
    /// DTO для событий WebRTC
    /// </summary>
    public class WebRtcEventDto
    {
        public string Type { get; set; } = "";
        public string? SessionId { get; set; }
        public string? CallerNumber { get; set; }
        public string? Cause { get; set; }
        public string? Message { get; set; }
        public string? Name { get; set; } // Имя ошибки (NotAllowedError, NotFoundError, etc.)
        public string? Phase { get; set; } // Фаза ошибки (getUserMedia, ua.call, ice, ws)
        public string? Stack { get; set; } // Stack trace ошибки
        public object? Stats { get; set; }
        public JsonElement? Data { get; set; }
    }

    /// <summary>
    /// Информация об аудиоустройстве WebRTC
    /// </summary>
    public class WebRtcAudioDevice
    {
        public string DeviceId { get; set; } = "";
        public string Label { get; set; } = "";
        public string Kind { get; set; } = ""; // "audioinput" или "audiooutput"
    }

    /// <summary>
    /// Интерфейс WebRTC сервиса
    /// </summary>
    public interface IWebRtcService
    {
        bool IsReadyForCalls { get; }
        string? ActiveSessionId { get; }
        WebRtcCallState CurrentCallState { get; }

        event Action<WebRtcEventDto>? Event;

        Task MakeCallAsync(string number);
        Task AnswerAsync(string? sessionId = null);
        Task HangupAsync(string? sessionId = null);
        Task ResetEngineAsync();
    }

    /// <summary>
    /// Хост для WebRTC engine (WebView2 wrapper)
    /// </summary>
    public class WebRtcEngineHost
    {
        private readonly WebView2 _webView;
        private bool _initialized = false;

        public event Action<string>? EngineEvent;

        public WebRtcEngineHost(WebView2 webView)
        {
            _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        }

        public async Task InitAsync(CoreWebView2Environment env, string url)
        {
            try
            {
                if (_initialized)
                {
                    MainWindow.Log("[WebRtcEngineHost] Already initialized, skipping");
                    return;
                }

                MainWindow.Log("[WebRtcEngineHost] Starting initialization...");
                
                // Проверяем состояние WebView2 перед инициализацией
                MainWindow.Log($"[WebRtcEngineHost] WebView2 state check: IsLoaded={_webView.IsLoaded}, Parent={_webView.Parent?.GetType().Name ?? "null"}, Visibility={_webView.Visibility}, Opacity={_webView.Opacity}");
                
                // Гарантируем вызов на UI thread
                if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
                {
                    MainWindow.Log("[WebRtcEngineHost] WARNING: Not on UI thread, switching...");
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        await EnsureCoreWebView2WithTimeoutAsync(env);
                    });
                }
                else
                {
                    await EnsureCoreWebView2WithTimeoutAsync(env);
                }
                
                // Проверяем результат
                if (_webView.CoreWebView2 == null)
                {
                    throw new InvalidOperationException("CoreWebView2 is null after initialization");
                }
                
                MainWindow.Log($"[WebRtcEngineHost] ✓ CoreWebView2 ensured successfully");
                
                // Автоматически разрешаем микрофон для softphone.local
                // Это критично для скрытого WebView2, иначе getUserMedia вернет NotAllowedError
                _webView.CoreWebView2.PermissionRequested += (sender, e) =>
                {
                    if (e.Uri != null && e.Uri.StartsWith("https://softphone.local", StringComparison.OrdinalIgnoreCase))
                    {
                        if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                        {
                            e.State = CoreWebView2PermissionState.Allow;
                            e.Handled = true;
                            MainWindow.Log($"[WebRtcEngineHost] PermissionRequested: Microphone ALLOWED for {e.Uri}");
                        }
                        else
                        {
                            MainWindow.Log($"[WebRtcEngineHost] PermissionRequested: {e.PermissionKind} requested for {e.Uri} (denying)");
                            e.State = CoreWebView2PermissionState.Deny;
                            e.Handled = true;
                        }
                    }
                    else
                    {
                        MainWindow.Log($"[WebRtcEngineHost] PermissionRequested: {e.PermissionKind} requested for {e.Uri} (denying - not softphone.local)");
                        e.State = CoreWebView2PermissionState.Deny;
                        e.Handled = true;
                    }
                };
                MainWindow.Log("[WebRtcEngineHost] PermissionRequested handler subscribed");
                
                // Настраиваем виртуальный хост
                var webRtcClientPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebRtcClient");
                if (System.IO.Directory.Exists(webRtcClientPath))
                {
                    MainWindow.Log($"[WebRtcEngineHost] Setting virtual host mapping: softphone.local -> {webRtcClientPath}");
                    _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "softphone.local",
                        webRtcClientPath,
                        CoreWebView2HostResourceAccessKind.Allow);
                    MainWindow.Log("[WebRtcEngineHost] Virtual host mapping set");
                }
                else
                {
                    MainWindow.Log($"[WebRtcEngineHost] ERROR: WebRtcClient directory not found: {webRtcClientPath}");
                }

                // Подписываемся на сообщения ДО загрузки страницы
                _webView.CoreWebView2.WebMessageReceived += (sender, e) =>
                {
                    var json = e.TryGetWebMessageAsString();
                    if (!string.IsNullOrEmpty(json))
                    {
                        EngineEvent?.Invoke(json);
                    }
                };
                MainWindow.Log("[WebRtcEngineHost] WebMessageReceived handler subscribed");

                // Ждем загрузки страницы перед установкой флага _initialized
                // ВАЖНО: подписываемся ДО установки Source, иначе событие может быть пропущено
                var navigationCompleted = new System.Threading.Tasks.TaskCompletionSource<bool>();
                bool navigationEventFired = false;
                
                _webView.CoreWebView2.NavigationCompleted += (sender, e) =>
                {
                    navigationEventFired = true;
                    MainWindow.Log($"[WebRtcEngineHost] NavigationCompleted event fired: IsSuccess={e.IsSuccess}, WebErrorStatus={e.WebErrorStatus}");
                    if (e.IsSuccess)
                    {
                        MainWindow.Log($"[WebRtcEngineHost] Navigation completed successfully: {url}");
                        navigationCompleted.SetResult(true);
                    }
                    else
                    {
                        MainWindow.Log($"[WebRtcEngineHost] Navigation failed: {e.WebErrorStatus}");
                        navigationCompleted.SetResult(false);
                    }
                };
                MainWindow.Log("[WebRtcEngineHost] NavigationCompleted handler subscribed");

                // Загружаем страницу ПОСЛЕ подписки на событие
                MainWindow.Log($"[WebRtcEngineHost] Loading page: {url}");
                _webView.Source = new Uri(url);
                MainWindow.Log("[WebRtcEngineHost] Source set, waiting for navigation...");
                
                // Ждем загрузки страницы (таймаут 10 секунд)
                var navTimeoutTask = System.Threading.Tasks.Task.Delay(10000);
                var navCompletedTask = await System.Threading.Tasks.Task.WhenAny(navigationCompleted.Task, navTimeoutTask);
                
                if (navCompletedTask == navTimeoutTask)
                {
                    MainWindow.Log($"[WebRtcEngineHost] WARNING: Page load timeout after 10s (navigationEventFired={navigationEventFired})");
                    // Проверяем, может страница уже загрузилась
                    if (_webView.CoreWebView2 != null)
                    {
                        var currentUrl = _webView.CoreWebView2.Source;
                        MainWindow.Log($"[WebRtcEngineHost] Current URL: {currentUrl}");
                        // Продолжаем, если CoreWebView2 доступен
                        _initialized = true;
                        MainWindow.Log("[WebRtcEngineHost] Initialized despite timeout (CoreWebView2 available)");
                        return;
                    }
                    else
                    {
                        MainWindow.Log("[WebRtcEngineHost] ERROR: CoreWebView2 is null after timeout");
                        throw new InvalidOperationException("WebView2 initialization timeout - CoreWebView2 is null");
                    }
                }
                else if (await navigationCompleted.Task)
                {
                    MainWindow.Log("[WebRtcEngineHost] ✓ Page loaded successfully");
                }
                else
                {
                    MainWindow.Log("[WebRtcEngineHost] WARNING: Navigation completed with error, but continuing...");
                }

                _initialized = true;
                MainWindow.Log($"[WebRtcEngineHost] ✓ Initialized: IsInitialized={IsInitialized}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRtcEngineHost] ERROR in InitAsync: {ex.Message}");
                MainWindow.Log($"[WebRtcEngineHost] Stack trace: {ex.StackTrace}");
                throw;
            }
        }
        
        private async Task EnsureCoreWebView2WithTimeoutAsync(CoreWebView2Environment env)
        {
            // Подключаем диагностические события ДО вызова EnsureCoreWebView2Async
            var initCompletedTcs = new TaskCompletionSource<bool>();
            Exception? initException = null;
            
            _webView.CoreWebView2InitializationCompleted += (sender, e) =>
            {
                MainWindow.Log($"[WebRtcEngineHost] CoreWebView2InitializationCompleted: IsSuccess={e.IsSuccess}, Exception={e.InitializationException?.Message ?? "none"}");
                if (e.InitializationException != null)
                {
                    initException = e.InitializationException;
                    MainWindow.Log($"[WebRtcEngineHost] InitializationException details: {e.InitializationException}");
                }
                initCompletedTcs.SetResult(e.IsSuccess);
            };
            
            // Добавляем таймаут для EnsureCoreWebView2Async (15 секунд)
            var ensureInitStartTime = DateTime.Now;
            var ensureCoreTask = _webView.EnsureCoreWebView2Async(env);
            var ensureTimeoutTask = Task.Delay(15000); // 15 секунд таймаут
            
            var ensureCompletedTask = await Task.WhenAny(ensureCoreTask, ensureTimeoutTask);
            
            if (ensureCompletedTask == ensureTimeoutTask)
            {
                var elapsed = (DateTime.Now - ensureInitStartTime).TotalSeconds;
                MainWindow.Log($"[WebRtcEngineHost] ERROR: EnsureCoreWebView2Async timeout after {elapsed:F1}s");
                MainWindow.Log($"[WebRtcEngineHost] WebView2 state: IsLoaded={_webView.IsLoaded}, Parent={_webView.Parent?.GetType().Name ?? "null"}, Visibility={_webView.Visibility}");
                throw new TimeoutException($"WebView2 initialization timeout after {elapsed:F1} seconds");
            }
            
            // Ждем завершения инициализации
            await ensureCoreTask;
            
            // Ждем события InitializationCompleted (с таймаутом 1 секунда)
            var initCompletedTask = await Task.WhenAny(initCompletedTcs.Task, Task.Delay(1000));
            if (initCompletedTask == initCompletedTcs.Task)
            {
                var isSuccess = await initCompletedTcs.Task;
                if (!isSuccess && initException != null)
                {
                    throw new InvalidOperationException($"WebView2 initialization failed: {initException.Message}", initException);
                }
            }
            
            var elapsedTime = (DateTime.Now - ensureInitStartTime).TotalSeconds;
            MainWindow.Log($"[WebRtcEngineHost] CoreWebView2 ensured (took {elapsedTime:F2}s)");
        }

        public async Task SendAsync(object command)
        {
            if (!_initialized)
                return;

            try
            {
                // Поддержка как анонимных объектов, так и Dictionary
                string? cmdValue = null;
                
                if (command is Dictionary<string, object> dictCmd)
                {
                    // Если команда пришла как Dictionary
                    if (dictCmd.TryGetValue("cmd", out var cmdObj))
                    {
                        cmdValue = cmdObj?.ToString();
                    }
                }
                else
                {
                    // Если команда пришла как анонимный объект, используем рефлексию
                    var cmdType = command.GetType();
                    var cmdProp = cmdType.GetProperty("cmd");
                    if (cmdProp != null)
                    {
                        cmdValue = cmdProp.GetValue(command)?.ToString();
                    }
                }
                
                if (string.IsNullOrEmpty(cmdValue))
                {
                    MainWindow.Log($"[WebRtcEngineHost] SendAsync: Command without 'cmd' property or key");
                    return;
                }

                string? script = null;

                switch (cmdValue)
                {
                    case "initUA":
                        var initJson = System.Text.Json.JsonSerializer.Serialize(command, new System.Text.Json.JsonSerializerOptions 
                        { 
                            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase 
                        });
                        // Логируем конфигурацию для диагностики (без пароля)
                        try
                        {
                            var initObj = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(initJson);
                            var safeParts = new List<string>();
                            if (initObj.TryGetProperty("cmd", out var cmdEl)) safeParts.Add($"cmd={cmdEl.GetString() ?? ""}");
                            if (initObj.TryGetProperty("wsUri", out var wsUriEl)) safeParts.Add($"wsUri={wsUriEl.GetString() ?? ""}");
                            if (initObj.TryGetProperty("sipUri", out var sipUriEl)) safeParts.Add($"sipUri={sipUriEl.GetString() ?? ""}");
                            if (initObj.TryGetProperty("user", out var userEl)) safeParts.Add($"user={userEl.GetString() ?? ""}");
                            if (initObj.TryGetProperty("pass", out var passEl))
                            {
                                var passStr = passEl.GetString() ?? "";
                                safeParts.Add($"pass={(passStr.Length > 0 ? "***" : "NOT SET")}");
                            }
                            MainWindow.Log($"[WebRtcEngineHost] SendAsync: initUA config: {string.Join(", ", safeParts)}");
                        }
                        catch { }
                        script = $"window.SoftphoneWebRtc.initUA({initJson});";
                        break;
                    case "makeCall":
                        string? number = null;
                        if (command is Dictionary<string, object> makeCallDict && makeCallDict.TryGetValue("number", out var numberObj))
                        {
                            number = numberObj?.ToString() ?? "";
                        }
                        else
                        {
                            var makeCallCmdType = command.GetType();
                            var numberProp = makeCallCmdType.GetProperty("number");
                            number = numberProp?.GetValue(command)?.ToString() ?? "";
                        }
                        MainWindow.Log($"[WebRtcEngineHost] SendAsync: Sending makeCall command for number '{number}'");
                        script = $"window.SoftphoneWebRtc.makeCall('{number}');";
                        MainWindow.Log($"[WebRtcEngineHost] SendAsync: Script prepared: {script}");
                        break;
                    case "answer":
                        script = "window.SoftphoneWebRtc.answer();";
                        break;
                    case "hangup":
                        script = "window.SoftphoneWebRtc.hangup();";
                        break;
                    case "resetEngine":
                        script = "window.SoftphoneWebRtc.resetEngine();";
                        break;
                    case "ping":
                        script = "window.SoftphoneWebRtc.ping();";
                        break;
                    case "getStats":
                        script = "window.SoftphoneWebRtc.getStats();";
                        break;
                    case "setMute":
                        // Команда setMute приходит как Dictionary с полем mute
                        bool muteValue = false;
                        bool muteFound = false;
                        
                        if (command is Dictionary<string, object> muteDictCmd && muteDictCmd.TryGetValue("mute", out var muteObj))
                        {
                            muteValue = Convert.ToBoolean(muteObj);
                            muteFound = true;
                        }
                        else if (command is System.Text.Json.JsonElement muteJsonCmd && muteJsonCmd.TryGetProperty("mute", out var muteEl))
                        {
                            muteValue = muteEl.GetBoolean();
                            muteFound = true;
                        }
                        else
                        {
                            // Пробуем через рефлексию для анонимных объектов
                            var muteCmdType = command.GetType();
                            var muteProp = muteCmdType.GetProperty("mute");
                            if (muteProp != null)
                            {
                                var mutePropValue = muteProp.GetValue(command);
                                if (mutePropValue != null)
                                {
                                    muteValue = Convert.ToBoolean(mutePropValue);
                                    muteFound = true;
                                }
                            }
                        }
                        
                        if (muteFound)
                        {
                            script = $"window.SoftphoneWebRtc.setMute({muteValue.ToString().ToLowerInvariant()});";
                            MainWindow.Log($"[WebRtcEngineHost] SendAsync: setMute script prepared: {script}");
                        }
                        else
                        {
                            MainWindow.Log("[WebRtcEngineHost] SendAsync: setMute command missing 'mute' property/key");
                            return;
                        }
                        break;
                    case "sendDtmf":
                        string? digit = null;
                        if (command is Dictionary<string, object> dtmfDict && dtmfDict.TryGetValue("digit", out var digitObj))
                        {
                            digit = digitObj?.ToString() ?? "";
                        }
                        else
                        {
                            var dtmfCmdType = command.GetType();
                            var digitProp = dtmfCmdType.GetProperty("digit");
                            digit = digitProp?.GetValue(command)?.ToString() ?? "";
                        }
                        if (string.IsNullOrEmpty(digit))
                        {
                            MainWindow.Log("[WebRtcEngineHost] SendAsync: sendDtmf command missing 'digit' property/key");
                            return;
                        }
                        // Экранируем специальные символы для JavaScript
                        string escapedDigit = digit.Replace("'", "\\'").Replace("\"", "\\\"");
                        MainWindow.Log($"[WebRtcEngineHost] SendAsync: Sending sendDtmf command for digit '{digit}'");
                        script = $"window.SoftphoneWebRtc.sendDtmf('{escapedDigit}');";
                        MainWindow.Log($"[WebRtcEngineHost] SendAsync: Script prepared: {script}");
                        break;
                    case "enumerateAudioDevices":
                        script = "window.SoftphoneWebRtc.enumerateAudioDevices();";
                        break;
                    case "switchAudioDevice":
                        if (command is System.Text.Json.JsonElement switchCmd)
                        {
                            string? inputDeviceId = null;
                            string? outputDeviceId = null;
                            
                            if (switchCmd.TryGetProperty("inputDeviceId", out var inputEl) && inputEl.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                inputDeviceId = inputEl.GetString();
                            }
                            if (switchCmd.TryGetProperty("outputDeviceId", out var outputEl) && outputEl.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                outputDeviceId = outputEl.GetString();
                            }
                            
                            var inputParam = inputDeviceId != null ? $"'{inputDeviceId}'" : "null";
                            var outputParam = outputDeviceId != null ? $"'{outputDeviceId}'" : "null";
                            script = $"window.SoftphoneWebRtc.switchAudioDevice({inputParam}, {outputParam});";
                        }
                        else
                        {
                            script = "window.SoftphoneWebRtc.switchAudioDevice(null, null);";
                        }
                        break;
                    case "startRecording":
                        script = "window.SoftphoneWebRtc.startRecording();";
                        break;
                    case "stopRecording":
                        script = "window.SoftphoneWebRtc.stopRecording();";
                        break;
                }

                if (script != null)
                {
                    MainWindow.Log($"[WebRtcEngineHost] SendAsync: Executing script: {script}");
                    
                    try
                    {
                        // ВАЖНО: выполнять ExecuteScriptAsync на UI thread
                        // Это гарантирует безопасность при вызове из watchdog или других потоков
                        string? result = null;
                        if (!_webView.Dispatcher.CheckAccess())
                        {
                            try
                            {
                                // Используем Invoke для синхронного выполнения на UI thread
                                // Внутри вызываем async метод и ждем его завершения
                                result = _webView.Dispatcher.Invoke(async () =>
                                {
                                    try
                                    {
                                        if (_webView.CoreWebView2 == null)
                                            return (string?)null;
                                        return await _webView.CoreWebView2.ExecuteScriptAsync(script);
                                    }
                                    catch (ObjectDisposedException)
                                    {
                                        MainWindow.Log("[WebRtcEngineHost] SendAsync: CoreWebView2 is disposed");
                                        return (string?)null;
                                    }
                                }).GetAwaiter().GetResult();
                            }
                            catch (ObjectDisposedException)
                            {
                                MainWindow.Log("[WebRtcEngineHost] SendAsync: WebView2 Dispatcher is disposed");
                                return;
                            }
                        }
                        else
                        {
                            try
                            {
                                if (_webView.CoreWebView2 != null)
                                {
                                    result = await _webView.CoreWebView2.ExecuteScriptAsync(script);
                                }
                            }
                            catch (ObjectDisposedException)
                            {
                                MainWindow.Log("[WebRtcEngineHost] SendAsync: CoreWebView2 is disposed");
                                return;
                            }
                        }
                        
                        MainWindow.Log($"[WebRtcEngineHost] SendAsync: Script executed, result: {result ?? "null"}");
                    }
                    catch (ObjectDisposedException)
                    {
                        MainWindow.Log("[WebRtcEngineHost] SendAsync: WebView2 object is disposed, cannot execute script");
                        return;
                    }
                }
                else
                {
                    // Не логируем предупреждение для событий, которые не являются командами (например, pong)
                    if (cmdValue != "pong")
                    {
                        MainWindow.Log($"[WebRtcEngineHost] SendAsync: WARNING - script is null for command '{cmdValue}'");
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRtcEngineHost] ERROR in SendAsync: {ex.Message}");
                MainWindow.Log($"[WebRtcEngineHost] ERROR stack trace: {ex.StackTrace}");
            }
        }

        public bool IsInitialized
        {
            get
            {
                if (!_initialized)
                    return false;
                
                try
                {
                    // ВАЖНО: проверка CoreWebView2 должна быть на UI thread
                    // Также проверяем, что _webView не disposed
                    if (_webView == null)
                        return false;
                    
                    // Проверяем disposed состояние через Dispatcher
                    if (!_webView.Dispatcher.CheckAccess())
                    {
                        try
                        {
                            return _webView.Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    return _webView.CoreWebView2 != null;
                                }
                                catch (ObjectDisposedException)
                                {
                                    MainWindow.Log("[WebRtcEngineHost] IsInitialized: WebView2 is disposed");
                                    return false;
                                }
                            });
                        }
                        catch (ObjectDisposedException)
                        {
                            MainWindow.Log("[WebRtcEngineHost] IsInitialized: WebView2 Dispatcher is disposed");
                            return false;
                        }
                    }
                    
                    try
                    {
                        return _webView.CoreWebView2 != null;
                    }
                    catch (ObjectDisposedException)
                    {
                        MainWindow.Log("[WebRtcEngineHost] IsInitialized: CoreWebView2 is disposed");
                        return false;
                    }
                }
                catch (ObjectDisposedException)
                {
                    MainWindow.Log("[WebRtcEngineHost] IsInitialized: WebView2 object is disposed");
                    return false;
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[WebRtcEngineHost] IsInitialized: Unexpected error: {ex.Message}");
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// WebRTC сервис (singleton)
    /// </summary>
    public sealed class WebRtcService : IWebRtcService
    {
        public static WebRtcService Instance { get; } = new WebRtcService();
        private WebRtcService() { }

        private WebRtcEngineHost? _engine;
        private readonly object _lock = new object();
        private string? _activeSessionId;
        private string? _ringingSessionId;
        private WebRtcCallState _state = WebRtcCallState.Idle;
        private bool _registered = false;
        private DateTime _lastRegisteredUtc = DateTime.MinValue; // Время последней успешной регистрации
        private DateTime _lastPongTimeUtc = DateTime.MinValue;
        private bool _isResetting = false;
        private DateTime _lastResetUtc = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, DateTime> _incomingCallDedup = new();
        private System.Threading.Timer? _watchdogTimer;

        public bool IsReadyForCalls
        {
            get
            {
                lock (_lock)
                {
                    return _engine != null && _engine.IsInitialized && _registered;
                }
            }
        }
        
        /// <summary>
        /// Отправляет команду в JavaScript engine
        /// </summary>
        public async Task SendCommandAsync(object command)
        {
            if (_engine != null)
            {
                await _engine.SendAsync(command);
            }
        }

        public string? ActiveSessionId
        {
            get
            {
                lock (_lock)
                {
                    return _activeSessionId;
                }
            }
        }

        public WebRtcCallState CurrentCallState
        {
            get
            {
                lock (_lock)
                {
                    return _state;
                }
            }
        }

        public event Action<WebRtcEventDto>? Event;

        public void AttachEngine(WebRtcEngineHost engine)
        {
            lock (_lock)
            {
                _engine = engine;
                _engine.EngineEvent += OnEngineEvent;
                MainWindow.Log($"[WebRtcService] AttachEngine: engine attached, IsInitialized={engine.IsInitialized}");
            }
        }

        // Безопасное чтение строки из JsonElement
        private static string? GetAsString(JsonElement e)
        {
            return e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number => e.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                JsonValueKind.Object => e.GetRawText(),   // объект -> raw json
                JsonValueKind.Array => e.GetRawText(),
                _ => null
            };
        }

        private void OnEngineEvent(string json)
        {
            try
            {
                var evt = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(json);
                
                // Поддержка обоих форматов: type и cmd (для совместимости)
                string? type = null;
                if (evt.TryGetProperty("type", out var typeEl))
                {
                    type = GetAsString(typeEl);
                }
                else if (evt.TryGetProperty("cmd", out var cmdEl))
                {
                    type = GetAsString(cmdEl);
                }
                
                if (string.IsNullOrEmpty(type))
                {
                    MainWindow.Log($"[WebRtcService] OnEngineEvent: Event without type/cmd field: {json}");
                    return;
                }
                
                // ДИАГНОСТИКА: логируем только важные события (регистрация, ошибки)
                if (type == "registered" || type == "ua_registered" || type == "reg_failed" || type == "ua_registration_failed" || 
                    type == "ua_connected" || type == "ws_connected" || type == "error")
                {
                    MainWindow.Log($"[WebRtcService] OnEngineEvent: Received JSON: {json}");
                    MainWindow.Log($"[WebRtcService] OnEngineEvent: Processing event type='{type}'");
                }
                
                // Логируем только важные события (регистрация, ошибки, pong, аудио для диагностики)
                // ВАЖНО: Добавляем "ua_registered" и "ua_connected" для полного логирования
                if (type == "registered" || type == "ua_registered" || type == "reg_failed" || type == "unregistered" || 
                    type == "ua_connected" || type == "ws_disconnected" || type == "error" || type == "pong" || 
                    type == "audio_connected" || type == "audio_track_info")
                {
                    MainWindow.Log($"[WebRtcService] OnEngineEvent: {type}");
                }
                
                // Логируем все события записи для отладки (без вывода всего JSON, чтобы не логировать массивы audioData)
                if (type.StartsWith("recording_"))
                {
                    // Извлекаем только метаданные, не логируя весь JSON (может содержать большие массивы)
                    string? logMessage = null;
                    if (evt.TryGetProperty("data", out var recordingDataEl) && recordingDataEl.ValueKind == JsonValueKind.Object)
                    {
                        var parts = new List<string>();
                        if (recordingDataEl.TryGetProperty("size", out var sizeEl))
                        {
                            parts.Add($"size={sizeEl.GetInt32()}");
                        }
                        if (recordingDataEl.TryGetProperty("totalSize", out var totalSizeEl))
                        {
                            parts.Add($"totalSize={totalSizeEl.GetInt32()}");
                        }
                        if (recordingDataEl.TryGetProperty("totalChunks", out var totalChunksEl))
                        {
                            parts.Add($"totalChunks={totalChunksEl.GetInt32()}");
                        }
                        if (recordingDataEl.TryGetProperty("chunkIndex", out var chunkIndexEl))
                        {
                            parts.Add($"chunkIndex={chunkIndexEl.GetInt32()}");
                        }
                        if (recordingDataEl.TryGetProperty("hash", out var hashEl))
                        {
                            var hash = hashEl.GetString();
                            if (!string.IsNullOrEmpty(hash))
                            {
                                parts.Add($"hash={hash.Substring(0, Math.Min(8, hash.Length))}...");
                            }
                        }
                        if (recordingDataEl.TryGetProperty("reason", out var reasonEl))
                        {
                            parts.Add($"reason={reasonEl.GetString()}");
                        }
                        if (recordingDataEl.TryGetProperty("message", out var msgEl))
                        {
                            parts.Add($"message={msgEl.GetString()}");
                        }
                        // НЕ логируем audioData, chunk data и другие большие массивы
                        if (parts.Count > 0)
                        {
                            logMessage = string.Join(", ", parts);
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(logMessage))
                    {
                        MainWindow.Log($"[WebRtcService] OnEngineEvent: {type} - {logMessage}");
                    }
                    else
                    {
                        MainWindow.Log($"[WebRtcService] OnEngineEvent: {type}");
                    }
                }

                // Извлекаем sessionId из разных мест
                string? sessionId = null;
                if (evt.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
                {
                    // Пробуем извлечь sessionId из data
                    if (dataEl.TryGetProperty("sessionId", out var sessionIdEl))
                    {
                        sessionId = GetAsString(sessionIdEl);
                    }
                }

                var dto = new WebRtcEventDto
                {
                    Type = type ?? "",
                    SessionId = sessionId
                };

                lock (_lock)
                {
                    switch (type)
                    {
                        case "registered":
                        case "ua_registered":
                            _registered = true;
                            _lastRegisteredUtc = DateTime.UtcNow;
                            MainWindow.Log($"[WebRtcService] OnEngineEvent: UA registered ({type}), setting _registered=true, _lastRegisteredUtc={_lastRegisteredUtc:HH:mm:ss.fff}, IsReadyForCalls={IsReadyForCalls}");
                            break;
                        case "reg_failed":
                        case "ua_registration_failed":
                            // Извлекаем детали ошибки регистрации
                            string? regErrorDetails = null;
                            if (evt.TryGetProperty("data", out var regErrorDataEl))
                            {
                                if (regErrorDataEl.ValueKind == JsonValueKind.String)
                                {
                                    regErrorDetails = GetAsString(regErrorDataEl);
                                }
                                else if (regErrorDataEl.ValueKind == JsonValueKind.Object)
                                {
                                    if (regErrorDataEl.TryGetProperty("message", out var regMsgEl))
                                    {
                                        regErrorDetails = GetAsString(regMsgEl);
                                    }
                                    if (regErrorDataEl.TryGetProperty("cause", out var regCauseEl))
                                    {
                                        var cause = GetAsString(regCauseEl);
                                        regErrorDetails = string.IsNullOrEmpty(regErrorDetails) ? cause : $"{cause}: {regErrorDetails}";
                                    }
                                }
                            }
                            
                            // ВАЖНО: Не сбрасываем _registered, если регистрация была недавно успешной
                            // Это защищает от ложных reg_failed событий, которые могут прийти из SettingsWindow
                            // или из-за временных проблем с сетью
                            if (_lastRegisteredUtc != DateTime.MinValue)
                            {
                                var secondsSinceRegistration = (DateTime.UtcNow - _lastRegisteredUtc).TotalSeconds;
                                if (secondsSinceRegistration < 5)
                                {
                                    // Регистрация была недавно успешной (менее 5 секунд назад)
                                    // Не сбрасываем статус, возможно это ложное событие или временная проблема
                                    MainWindow.Log($"[WebRtcService] OnEngineEvent: UA registration failed ({type}) - but recent successful registration ({secondsSinceRegistration:F1}s ago), keeping registered state");
                                    // НЕ сбрасываем _registered и _lastRegisteredUtc
                                    dto.Message = regErrorDetails ?? "Registration failed (ignored - recent success)";
                                    break;
                                }
                            }
                            
                            // Если регистрации не было недавно, сбрасываем статус
                            _registered = false;
                            _lastRegisteredUtc = DateTime.MinValue;
                            MainWindow.Log($"[WebRtcService] OnEngineEvent: UA registration failed ({type})" + 
                                (string.IsNullOrEmpty(regErrorDetails) ? "" : $" - {regErrorDetails}"));
                            dto.Message = regErrorDetails ?? "Registration failed";
                            break;
                        case "unregistered":
                        case "ua_unregistered":
                            _registered = false;
                            _lastRegisteredUtc = DateTime.MinValue;
                            MainWindow.Log($"[WebRtcService] OnEngineEvent: UA unregistered ({type})");
                            break;
                        case "ws_disconnected":
                            // ws_disconnected не сбрасывает _registered сразу, если регистрация была недавно
                            // Это позволяет переподключению WebSocket не сбрасывать состояние готовности
                            // Увеличиваем окно до 10 секунд для более стабильной работы
                            if (_lastRegisteredUtc != DateTime.MinValue)
                            {
                                var secondsSinceRegistration = (DateTime.UtcNow - _lastRegisteredUtc).TotalSeconds;
                                if (secondsSinceRegistration > 10)
                                {
                                    _registered = false;
                                    MainWindow.Log($"[WebRtcService] OnEngineEvent: ws_disconnected - registration was old ({secondsSinceRegistration:F1}s ago), resetting registered state");
                                }
                                else
                                {
                                    // Сохраняем статус регистрации при временном отключении WebSocket
                                    MainWindow.Log($"[WebRtcService] OnEngineEvent: ws_disconnected - keeping registered state (recent registration, {secondsSinceRegistration:F1}s ago)");
                                }
                            }
                            else
                            {
                                // Если регистрации никогда не было или она была сброшена, сбрасываем статус
                                _registered = false;
                                MainWindow.Log($"[WebRtcService] OnEngineEvent: ws_disconnected - no recent registration, resetting registered state");
                            }
                            break;
                        case "incoming":
                            HandleIncomingCall(evt, sessionId, dto);
                            break;
                        case "makeCall_started":
                            MainWindow.Log($"[WebRtcService] OnEngineEvent: makeCall started");
                            break;
                        case "makeCall_initiated":
                            // Извлекаем sessionId из события makeCall_initiated
                            if (evt.TryGetProperty("data", out var initDataEl) && initDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (initDataEl.TryGetProperty("sessionId", out var initSessionIdEl))
                                {
                                    var initSessionId = GetAsString(initSessionIdEl);
                                    if (!string.IsNullOrEmpty(initSessionId))
                                    {
                                        _activeSessionId = initSessionId;
                                        dto.SessionId = initSessionId;
                                        MainWindow.Log($"[WebRtcService] OnEngineEvent: makeCall initiated with sessionId: {initSessionId}");
                                    }
                                }
                            }
                            break;
                        case "new_session":
                            // Извлекаем sessionId из события new_session
                            if (evt.TryGetProperty("data", out var newSessionDataEl) && newSessionDataEl.ValueKind == JsonValueKind.Object)
                            {
                                // Пробуем разные варианты получения sessionId
                                if (newSessionDataEl.TryGetProperty("sessionId", out var newSessionIdEl))
                                {
                                    sessionId = GetAsString(newSessionIdEl);
                                }
                                
                                // Проверяем направление звонка (incoming/outgoing)
                                string? direction = null;
                                if (newSessionDataEl.TryGetProperty("direction", out var directionEl))
                                {
                                    direction = GetAsString(directionEl);
                                }
                                string? originator = null;
                                if (newSessionDataEl.TryGetProperty("originator", out var originatorEl))
                                {
                                    originator = GetAsString(originatorEl);
                                }
                                
                                // Если sessionId не найден в data, пробуем получить из самого объекта сессии через JS
                                if (!string.IsNullOrEmpty(sessionId))
                                {
                                    _activeSessionId = sessionId;
                                    
                                    // Для входящих звонков устанавливаем Ringing, для исходящих - Calling
                                    if (direction == "incoming" || originator == "remote")
                                    {
                                        _ringingSessionId = sessionId;
                                        _state = WebRtcCallState.Ringing;
                                        MainWindow.Log($"[WebRtcService] OnEngineEvent: new_session (incoming) with sessionId: {sessionId}");
                                    }
                                    else
                                    {
                                        _state = WebRtcCallState.Calling;
                                        MainWindow.Log($"[WebRtcService] OnEngineEvent: new_session (outgoing) with sessionId: {sessionId}");
                                    }
                                    
                                    dto.SessionId = sessionId;
                                }
                                else
                                {
                                    MainWindow.Log($"[WebRtcService] OnEngineEvent: new_session without sessionId");
                                }
                            }
                            break;
                        case "call_progress":
                            // Если sessionId не был извлечен из data, пробуем еще раз
                            if (string.IsNullOrEmpty(sessionId) && evt.TryGetProperty("data", out var progressDataEl) && progressDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (progressDataEl.TryGetProperty("sessionId", out var progressSessionIdEl))
                                {
                                    sessionId = GetAsString(progressSessionIdEl);
                                    dto.SessionId = sessionId;
                                }
                            }
                            if (sessionId == _activeSessionId || string.IsNullOrEmpty(_activeSessionId))
                            {
                                if (string.IsNullOrEmpty(_activeSessionId) && !string.IsNullOrEmpty(sessionId))
                                {
                                    _activeSessionId = sessionId;
                                }
                                _state = WebRtcCallState.Calling;
                                MainWindow.Log($"[WebRtcService] OnEngineEvent: call_progress for sessionId: {sessionId}");
                            }
                            break;
                        case "call_accepted":
                        case "call_confirmed":
                            // Если sessionId не был извлечен из data, пробуем еще раз
                            if (string.IsNullOrEmpty(sessionId) && evt.TryGetProperty("data", out var acceptedDataEl) && acceptedDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (acceptedDataEl.TryGetProperty("sessionId", out var acceptedSessionIdEl))
                                {
                                    sessionId = GetAsString(acceptedSessionIdEl);
                                    dto.SessionId = sessionId;
                                }
                            }
                            if (sessionId == _activeSessionId || string.IsNullOrEmpty(_activeSessionId))
                            {
                                if (string.IsNullOrEmpty(_activeSessionId) && !string.IsNullOrEmpty(sessionId))
                                {
                                    _activeSessionId = sessionId;
                                }
                                _state = WebRtcCallState.Connected;
                                MainWindow.Log($"[WebRtcService] OnEngineEvent: {type} for sessionId: {sessionId}");
                            }
                            break;
                        case "call_failed":
                        case "call_ended":
                            // Если sessionId не был извлечен из data, пробуем еще раз
                            if (string.IsNullOrEmpty(sessionId) && evt.TryGetProperty("data", out var endedDataEl) && endedDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (endedDataEl.TryGetProperty("sessionId", out var endedSessionIdEl))
                                {
                                    sessionId = GetAsString(endedSessionIdEl);
                                    dto.SessionId = sessionId;
                                }
                            }
                            
                            // Извлекаем детали для логирования
                            string? causeStr = null;
                            string? originatorStr = null;
                            string? statusCodeStr = null;
                            string? reasonPhraseStr = null;
                            if (evt.TryGetProperty("data", out var failedDataEl) && failedDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (failedDataEl.TryGetProperty("cause", out var causeEl))
                                {
                                    causeStr = GetAsString(causeEl);
                                }
                                if (failedDataEl.TryGetProperty("originator", out var originatorEl))
                                {
                                    originatorStr = GetAsString(originatorEl);
                                }
                                if (failedDataEl.TryGetProperty("status_code", out var statusCodeEl))
                                {
                                    statusCodeStr = GetAsString(statusCodeEl);
                                }
                                if (failedDataEl.TryGetProperty("reason_phrase", out var reasonPhraseEl))
                                {
                                    reasonPhraseStr = GetAsString(reasonPhraseEl);
                                }
                            }
                            
                            // Детальное логирование причины завершения звонка
                            var reasonParts = new System.Collections.Generic.List<string>();
                            if (!string.IsNullOrEmpty(causeStr)) reasonParts.Add($"cause={causeStr}");
                            if (!string.IsNullOrEmpty(originatorStr)) reasonParts.Add($"originator={originatorStr}");
                            if (!string.IsNullOrEmpty(statusCodeStr)) reasonParts.Add($"status_code={statusCodeStr}");
                            if (!string.IsNullOrEmpty(reasonPhraseStr)) reasonParts.Add($"reason={reasonPhraseStr}");
                            var reasonDetails = reasonParts.Count > 0 ? " (" + string.Join(", ", reasonParts) + ")" : "";
                            
                            // Всегда сбрасываем состояние при call_ended или call_failed
                            MainWindow.Log($"[WebRtcService] Call {type} for session {sessionId ?? "null"}{reasonDetails}, resetting state to Idle");
                            _state = WebRtcCallState.Idle;
                            _activeSessionId = null;
                            _ringingSessionId = null;
                            break;
                        case "ping":
                            // Событие ping от JS не должно обрабатываться здесь
                            // Ping отправляется из C# в JS, а pong приходит обратно как событие
                            // Если это событие пришло, просто игнорируем его
                            MainWindow.Log("[WebRtcService] OnEngineEvent: Received ping event from JS (unexpected, ignoring)");
                            break;
                        case "mute_applied":
                        case "mute_changed":
                            // Логируем детали mute события
                            if (evt.TryGetProperty("data", out var muteDataEl) && muteDataEl.ValueKind == JsonValueKind.Object)
                            {
                                string? mutedStr = null;
                                string? tracksCountStr = null;
                                string? methodStr = null;
                                if (muteDataEl.TryGetProperty("muted", out var mutedEl))
                                {
                                    mutedStr = GetAsString(mutedEl);
                                }
                                if (muteDataEl.TryGetProperty("tracksCount", out var tracksCountEl))
                                {
                                    tracksCountStr = GetAsString(tracksCountEl);
                                }
                                if (muteDataEl.TryGetProperty("method", out var methodEl))
                                {
                                    methodStr = GetAsString(methodEl);
                                }
                                MainWindow.Log($"[WebRtcService] OnEngineEvent: {type} - muted={mutedStr ?? "unknown"}, tracksCount={tracksCountStr ?? "unknown"}, method={methodStr ?? "unknown"}");
                            }
                            else
                            {
                                MainWindow.Log($"[WebRtcService] OnEngineEvent: {type} (no data)");
                            }
                            break;
                        case "pong":
                            // Защита 1: Pong должен приходить только от активного engine
                            lock (_lock)
                            {
                                if (_engine != null && _engine.IsInitialized)
                                {
                                    _lastPongTimeUtc = DateTime.UtcNow;
                                    // Диагностическое сообщение хотя бы 1 раз в минуту (в первые 5 секунд каждой минуты)
                                    if (DateTime.UtcNow.Second < 5)
                                    {
                                        MainWindow.Log($"[WebRtcService] OnEngineEvent: pong received, lastPong updated");
                                    }
                                }
                                else
                                {
                                    MainWindow.Log("[WebRtcService] OnEngineEvent: pong ignored (engine not initialized or null)");
                                }
                            }
                            break;
                        case "error":
                            // Детальное логирование ошибок с name, message, phase, stack
                            string? errorName = null;
                            string? errorMessage = null;
                            string? errorPhase = null;
                            string? errorStack = null;
                            bool isNonFatal = false;
                            
                            // Извлекаем детали ошибки из data (вне lock для минимизации времени блокировки)
                            if (evt.TryGetProperty("data", out var errorDataEl) && errorDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (errorDataEl.TryGetProperty("name", out var nameEl))
                                {
                                    errorName = GetAsString(nameEl);
                                    dto.Name = errorName;
                                }
                                if (errorDataEl.TryGetProperty("message", out var errorMsgEl))
                                {
                                    errorMessage = GetAsString(errorMsgEl);
                                    dto.Message = errorMessage;
                                }
                                if (errorDataEl.TryGetProperty("phase", out var phaseEl))
                                {
                                    errorPhase = GetAsString(phaseEl);
                                    dto.Phase = errorPhase;
                                }
                                if (errorDataEl.TryGetProperty("stack", out var stackEl))
                                {
                                    errorStack = GetAsString(stackEl);
                                    dto.Stack = errorStack;
                                }
                            }
                            
                            // Логируем все детали ошибки
                            MainWindow.Log($"[WebRtcService] OnEngineEvent: JS ERROR - Name={errorName ?? "unknown"}, Message={errorMessage ?? "no message"}, Phase={errorPhase ?? "unknown"}, Stack={errorStack ?? "no stack"}");
                            
                            // Фильтруем нефатальные ошибки (не сбрасываем состояние звонка)
                            // AudioPlayError - ошибка воспроизведения аудио (не критично для звонка)
                            // EnumerateDevicesError - ошибка перечисления устройств (не критично для звонка)
                            // ВАЖНО: PeerConnectionError, ICEFailed, WebSocketDisconnected и подобные - ФАТАЛЬНЫЕ, их фильтровать нельзя!
                            isNonFatal =
                                string.Equals(errorName, "AudioPlayError", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(errorName, "EnumerateDevicesError", StringComparison.OrdinalIgnoreCase);
                            
                            if (isNonFatal)
                            {
                                MainWindow.Log($"[WebRtcService] Non-fatal error ignored: {errorName} (phase={errorPhase ?? "unknown"})");
                                break; // Выходим из case, не сбрасывая состояние звонка
                            }
                            
                            // Если ошибка произошла во время звонка, сбрасываем состояние (только для фатальных ошибок)
                            lock (_lock)
                            {
                                if (_state == WebRtcCallState.Calling || _state == WebRtcCallState.Connected)
                                {
                                    MainWindow.Log($"[WebRtcService] Fatal error during call, resetting state from {_state} to Idle");
                                    _state = WebRtcCallState.Idle;
                                    _activeSessionId = null;
                                    _ringingSessionId = null;
                                }
                            }
                            break;
                        case "engine_reset":
                            _registered = false;
                            _activeSessionId = null;
                            _ringingSessionId = null;
                            _state = WebRtcCallState.Idle;
                            break;
                    }

                    // Извлекаем дополнительные данные для DTO (для всех событий)
                    if (evt.TryGetProperty("data", out var dataEl2))
                    {
                        if (dataEl2.ValueKind == JsonValueKind.Object)
                        {
                            if (dataEl2.TryGetProperty("callerNumber", out var callerEl))
                            {
                                dto.CallerNumber = GetAsString(callerEl);
                            }
                            if (dataEl2.TryGetProperty("cause", out var causeEl))
                            {
                                dto.Cause = GetAsString(causeEl);
                            }
                            if (dataEl2.TryGetProperty("message", out var msgEl) && string.IsNullOrEmpty(dto.Message))
                            {
                                dto.Message = GetAsString(msgEl);
                            }
                            if (dataEl2.TryGetProperty("name", out var nameEl2) && string.IsNullOrEmpty(dto.Name))
                            {
                                dto.Name = GetAsString(nameEl2);
                            }
                            if (dataEl2.TryGetProperty("phase", out var phaseEl2) && string.IsNullOrEmpty(dto.Phase))
                            {
                                dto.Phase = GetAsString(phaseEl2);
                            }
                            if (dataEl2.TryGetProperty("stack", out var stackEl2) && string.IsNullOrEmpty(dto.Stack))
                            {
                                dto.Stack = GetAsString(stackEl2);
                            }
                        }
                        dto.Data = dataEl2;
                    }
                }

                // ДИАГНОСТИКА: логируем перед вызовом Event?.Invoke только для важных событий
                if (dto.Type == "registered" || dto.Type == "ua_registered" || dto.Type == "reg_failed" || dto.Type == "ua_registration_failed" ||
                    dto.Type == "ua_connected" || dto.Type == "ws_connected")
                {
                    MainWindow.Log($"[WebRtcService] OnEngineEvent: Invoking Event for type='{dto.Type}', registered={_registered}, IsReadyForCalls={IsReadyForCalls}");
                }
                
                Event?.Invoke(dto);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRtcService] ERROR processing engine event: {ex.Message}");
                MainWindow.Log($"[WebRtcService] ERROR stack trace: {ex.StackTrace}");
            }
        }

        private void HandleIncomingCall(JsonElement evt, string? sessionId, WebRtcEventDto dto)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                MainWindow.Log("[WebRtcService] WARNING: Incoming call without sessionId, ignoring");
                return;
            }

            // Дедуп
            if (_incomingCallDedup.TryGetValue(sessionId, out var lastTime))
            {
                var timeSinceLastCall = DateTime.Now - lastTime;
                if (timeSinceLastCall.TotalMilliseconds < 1000)
                {
                    MainWindow.Log($"[WebRtcService] Incoming call {sessionId} ignored (dedup)");
                    return;
                }
            }

            // Проверяем активный звонок
            if (_state != WebRtcCallState.Idle && _state != WebRtcCallState.Ended)
            {
                MainWindow.Log($"[WebRtcService] Incoming call {sessionId} ignored (active call in state {_state})");
                return;
            }

            _incomingCallDedup[sessionId] = DateTime.Now;

            // Очищаем старые записи
            var keysToRemove = _incomingCallDedup.Where(kvp => (DateTime.Now - kvp.Value).TotalSeconds > 10).Select(kvp => kvp.Key).ToList();
            foreach (var key in keysToRemove)
            {
                _incomingCallDedup.TryRemove(key, out _);
            }

            _ringingSessionId = sessionId;
            _activeSessionId = sessionId;
            _state = WebRtcCallState.Ringing;

            // Извлекаем callerNumber
            if (evt.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
            {
                if (dataEl.TryGetProperty("callerNumber", out var callerEl))
                {
                    dto.CallerNumber = GetAsString(callerEl);
                }
            }
        }

        public async Task MakeCallAsync(string number)
        {
            lock (_lock)
            {
                // Если состояние Ending или Ended, сбрасываем в Idle (предыдущий звонок завершен)
                if (_state == WebRtcCallState.Ending || _state == WebRtcCallState.Ended)
                {
                    MainWindow.Log($"[WebRtcService] MakeCallAsync: Resetting state from {_state} to Idle");
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                }
                
                if (_state != WebRtcCallState.Idle)
                {
                    MainWindow.Log($"[WebRtcService] MakeCallAsync: Cannot make call in state {_state}");
                    throw new InvalidOperationException($"Cannot make call in state {_state}");
                }

                if (!IsReadyForCalls)
                {
                    var engineNull = _engine == null;
                    var initialized = _engine?.IsInitialized ?? false;
                    MainWindow.Log($"[WebRtcService] MakeCallAsync: WebRTC not ready (engine={!engineNull}, initialized={initialized}, registered={_registered})");
                    MainWindow.Log($"[WebRtcService] MakeCallAsync: Details - engine type: {_engine?.GetType().Name ?? "null"}, IsInitialized property: {initialized}");
                    throw new InvalidOperationException("WebRTC not ready");
                }

                _state = WebRtcCallState.Calling;
            }

            MainWindow.Log($"[WebRtcService] MakeCallAsync: Sending makeCall command for number {number}");
            if (_engine != null)
            {
                await _engine.SendAsync(new { cmd = "makeCall", number });
                MainWindow.Log($"[WebRtcService] MakeCallAsync: Command sent successfully");
            }
            else
            {
                MainWindow.Log($"[WebRtcService] MakeCallAsync: ERROR - engine is null");
            }
        }

        public async Task AnswerAsync(string? sessionId = null)
        {
            lock (_lock)
            {
                // Разрешаем ответить в состоянии Ringing или Calling (для входящих звонков)
                // Calling может быть установлен событием new_session для входящих звонков
                if (_state != WebRtcCallState.Ringing && _state != WebRtcCallState.Calling)
                {
                    MainWindow.Log($"[WebRtcService] Answer ignored (current state: {_state})");
                    return;
                }

                // Для входящих звонков проверяем, что есть активная сессия
                if (_state == WebRtcCallState.Calling && string.IsNullOrEmpty(_activeSessionId) && string.IsNullOrEmpty(_ringingSessionId))
                {
                    MainWindow.Log($"[WebRtcService] Answer ignored (Calling state but no active/ringing session)");
                    return;
                }

                if (!IsReadyForCalls)
                {
                    throw new InvalidOperationException("WebRTC not ready");
                }

                _state = WebRtcCallState.Connected;
            }

            if (_engine != null)
            {
                await _engine.SendAsync(new { cmd = "answer" });
                MainWindow.Log($"[WebRtcService] AnswerAsync: Answer command sent");
            }
        }

        public async Task SetMuteAsync(bool mute)
        {
            if (_engine == null || !_engine.IsInitialized)
            {
                MainWindow.Log("[WebRtcService] SetMuteAsync: Engine not initialized");
                return;
            }

            try
            {
                // ВАЖНО: отправляем команду как Dictionary, чтобы WebRtcEngineHost правильно распарсил поле mute
                var cmd = new Dictionary<string, object> 
                { 
                    { "cmd", "setMute" },
                    { "mute", mute }
                };
                
                // Логируем перед отправкой - ожидаем увидеть "Executing script" в WebRtcEngineHost
                MainWindow.Log($"[WebRtcService] SetMuteAsync: Sending mute command (mute={mute}), expecting 'Executing script: window.SoftphoneWebRtc.setMute(...)' log");
                
                await _engine.SendAsync(cmd);
                MainWindow.Log($"[WebRtcService] SetMuteAsync: Mute command sent (mute={mute})");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRtcService] SetMuteAsync: ERROR - {ex.Message}");
            }
        }

        public async Task SendDTMFAsync(char digit)
        {
            if (_engine == null || !_engine.IsInitialized)
            {
                MainWindow.Log("[WebRtcService] SendDTMFAsync: Engine not initialized");
                return;
            }

            try
            {
                var cmd = new Dictionary<string, object> 
                { 
                    { "cmd", "sendDtmf" },
                    { "digit", digit.ToString() }
                };
                MainWindow.Log($"[WebRtcService] SendDTMFAsync: Sending DTMF command (digit='{digit}')");

                await _engine.SendAsync(cmd);
                MainWindow.Log($"[WebRtcService] SendDTMFAsync: DTMF command sent (digit='{digit}')");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRtcService] SendDTMFAsync: ERROR - {ex.Message}");
            }
        }

        public async Task<List<WebRtcAudioDevice>> EnumerateAudioDevicesAsync()
        {
            var devices = new List<WebRtcAudioDevice>();
            
            if (_engine == null || !_engine.IsInitialized)
            {
                MainWindow.Log("[WebRtcService] EnumerateAudioDevicesAsync: Engine not initialized");
                return devices;
            }

            try
            {
                // Подписываемся на событие audio_devices_list
                var tcs = new TaskCompletionSource<List<WebRtcAudioDevice>>();
                Action<WebRtcEventDto>? handler = null;
                
                handler = (dto) =>
                {
                    if (dto.Type == "audio_devices_list")
                    {
                        if (handler != null) Event -= handler;
                        try
                        {
                            if (dto.Data.HasValue && dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                var data = dto.Data.Value;
                                
                                if (data.TryGetProperty("inputs", out var inputsEl) && inputsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var input in inputsEl.EnumerateArray())
                                    {
                                        if (input.TryGetProperty("deviceId", out var deviceIdEl) && input.TryGetProperty("label", out var labelEl))
                                        {
                                            devices.Add(new WebRtcAudioDevice
                                            {
                                                DeviceId = GetAsString(deviceIdEl) ?? "",
                                                Label = GetAsString(labelEl) ?? "Unknown",
                                                Kind = "audioinput"
                                            });
                                        }
                                    }
                                }
                                
                                if (data.TryGetProperty("outputs", out var outputsEl) && outputsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var output in outputsEl.EnumerateArray())
                                    {
                                        if (output.TryGetProperty("deviceId", out var deviceIdEl) && output.TryGetProperty("label", out var labelEl))
                                        {
                                            devices.Add(new WebRtcAudioDevice
                                            {
                                                DeviceId = GetAsString(deviceIdEl) ?? "",
                                                Label = GetAsString(labelEl) ?? "Unknown",
                                                Kind = "audiooutput"
                                            });
                                        }
                                    }
                                }
                            }
                            
                            tcs.SetResult(devices);
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"[WebRtcService] EnumerateAudioDevicesAsync: Error parsing devices: {ex.Message}");
                            tcs.SetResult(devices);
                        }
                    }
                    else if (dto.Type == "error")
                    {
                        if (handler != null) Event -= handler;
                        MainWindow.Log($"[WebRtcService] EnumerateAudioDevicesAsync: Error from JS: {dto.Message ?? "unknown"}");
                        tcs.SetResult(devices);
                    }
                };
                
                Event += handler;
                
                // Отправляем команду
                await _engine.SendAsync(new { cmd = "enumerateAudioDevices" });
                
                // Ждем ответа с таймаутом 5 секунд
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    if (handler != null) Event -= handler;
                    MainWindow.Log("[WebRtcService] EnumerateAudioDevicesAsync: Timeout waiting for devices list");
                    return devices;
                }
                
                return await tcs.Task;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRtcService] EnumerateAudioDevicesAsync: ERROR - {ex.Message}");
                return devices;
            }
        }

        public async Task SwitchAudioDeviceAsync(string? inputDeviceId, string? outputDeviceId)
        {
            if (_engine == null || !_engine.IsInitialized)
            {
                MainWindow.Log("[WebRtcService] SwitchAudioDeviceAsync: Engine not initialized");
                return;
            }

            try
            {
                var cmd = new Dictionary<string, object> { { "cmd", "switchAudioDevice" } };
                if (!string.IsNullOrEmpty(inputDeviceId))
                {
                    cmd["inputDeviceId"] = inputDeviceId;
                }
                if (!string.IsNullOrEmpty(outputDeviceId))
                {
                    cmd["outputDeviceId"] = outputDeviceId;
                }
                
                await _engine.SendAsync(cmd);
                MainWindow.Log($"[WebRtcService] SwitchAudioDeviceAsync: Switch command sent (input={inputDeviceId ?? "null"}, output={outputDeviceId ?? "null"})");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRtcService] SwitchAudioDeviceAsync: ERROR - {ex.Message}");
            }
        }

        public async Task HangupAsync(string? sessionId = null)
        {
            lock (_lock)
            {
                if (_state == WebRtcCallState.Ending || _state == WebRtcCallState.Ended)
                {
                    MainWindow.Log($"[WebRtcService] HangupAsync: Already hanging up or ended (state: {_state}), resetting to Idle");
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                    return;
                }

                MainWindow.Log($"[WebRtcService] HangupAsync: Setting state to Ending (current: {_state})");
                _state = WebRtcCallState.Ending;
            }

            if (_engine != null)
            {
                await _engine.SendAsync(new { cmd = "hangup", sessionId });
                MainWindow.Log($"[WebRtcService] HangupAsync: Hangup command sent for session {sessionId ?? "null"}");
                
                // Сбрасываем состояние через небольшую задержку, если событие call_ended не придет
                _ = System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
                {
                    lock (_lock)
                    {
                        if (_state == WebRtcCallState.Ending)
                        {
                            MainWindow.Log("[WebRtcService] HangupAsync: Timeout - call_ended event not received, resetting state to Idle");
                            _state = WebRtcCallState.Idle;
                            _activeSessionId = null;
                            _ringingSessionId = null;
                        }
                    }
                });
            }
            else
            {
                MainWindow.Log("[WebRtcService] HangupAsync: Engine is null, resetting state to Idle");
                lock (_lock)
                {
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                    _ringingSessionId = null;
                }
            }
        }

        public async Task ResetEngineAsync()
        {
            // Защита 2: Не делай resetEngine() если уже в процессе Resetting
            lock (_lock)
            {
                if (_isResetting)
                {
                    MainWindow.Log("[WebRtcService] ResetEngineAsync: Already resetting, skipping");
                    return;
                }
                
                // Проверяем cooldown после последнего reset
                if ((DateTime.UtcNow - _lastResetUtc).TotalSeconds < 20)
                {
                    MainWindow.Log($"[WebRtcService] ResetEngineAsync: Cooldown active (last reset {(DateTime.UtcNow - _lastResetUtc).TotalSeconds:F1}s ago), skipping");
                    return;
                }
                
                _isResetting = true;
                _lastResetUtc = DateTime.UtcNow;
                _state = WebRtcCallState.Idle;
                _activeSessionId = null;
                _ringingSessionId = null;
            }

            try
            {
                if (_engine != null)
                {
                    await _engine.SendAsync(new { cmd = "resetEngine" });
                }
            }
            finally
            {
                lock (_lock)
                {
                    _isResetting = false;
                }
            }
        }

        /// <summary>
        /// Переинициализирует UA с новыми учетными данными (для изменения настроек подключения)
        /// </summary>
        public async Task ReinitializeUAAsync(string wsUri, string sipUri, string username, string password)
        {
            if (_engine == null || !_engine.IsInitialized)
            {
                MainWindow.Log("[WebRtcService] ReinitializeUAAsync: Engine not initialized, skipping");
                return;
            }

            try
            {
                MainWindow.Log($"[WebRtcService] ReinitializeUAAsync: Reinitializing UA with new credentials (user={username}, sipUri={sipUri})");
                await _engine.SendAsync(new
                {
                    cmd = "initUA",
                    wsUri = wsUri,
                    sipUri = sipUri,
                    user = username,
                    pass = password
                });
                MainWindow.Log("[WebRtcService] ReinitializeUAAsync: initUA command sent");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRtcService] ERROR in ReinitializeUAAsync: {ex.Message}");
            }
        }

        public void StartWatchdog()
        {
            _watchdogTimer = new System.Threading.Timer(async (state) =>
            {
                try
                {
                    if (_engine == null || !_engine.IsInitialized)
                        return;

                    // Ping
                    await _engine.SendAsync(new { cmd = "ping" });

                    // Проверяем pong
                    lock (_lock)
                    {
                        // Защита 2: Watchdog должен быть идемпотентным и не спамить reset
                        if (_isResetting || (DateTime.UtcNow - _lastResetUtc).TotalSeconds < 20)
                        {
                            // Cooldown активен, пропускаем проверку
                            return;
                        }
                        
                        if (_lastPongTimeUtc != DateTime.MinValue && (DateTime.UtcNow - _lastPongTimeUtc).TotalSeconds > 30)
                        {
                            MainWindow.Log($"[WebRtcService] WARNING: No pong received for {(DateTime.UtcNow - _lastPongTimeUtc).TotalSeconds:F1} seconds, resetting engine...");
                            _ = ResetEngineAsync();
                        }

                        // Логируем состояние только при изменении или раз в минуту (для уменьшения нагрузки)
                        // Убрано избыточное логирование для предотвращения зависаний

                        // Запрашиваем статистику для активных звонков
                        if (_state == WebRtcCallState.Connected)
                        {
                            _ = _engine?.SendAsync(new { cmd = "getStats" });
                        }
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[WebRtcService] ERROR in watchdog: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
        }
        
        /// <summary>
        /// Останавливает watchdog timer и очищает ресурсы
        /// </summary>
        public void StopWatchdog()
        {
            lock (_lock)
            {
                if (_watchdogTimer != null)
                {
                    _watchdogTimer.Dispose();
                    _watchdogTimer = null;
                    MainWindow.Log("[WebRtcService] Watchdog timer stopped");
                }
            }
        }
    }
}


