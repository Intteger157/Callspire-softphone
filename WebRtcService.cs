using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
        Task SetHoldAsync(bool hold, string? sessionId = null);
        Task ResetEngineAsync();
    }

    /// <summary>
    /// Хост для WebRTC engine (WebView2 wrapper)
    /// </summary>
    public class WebRtcEngineHost
    {
        private readonly WebView2 _webView;
        private bool _initialized = false;
        private static readonly TimeSpan DefaultScriptTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan HeartbeatScriptTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan StatsScriptTimeout = TimeSpan.FromSeconds(3);

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
                    // IMPORTANT: don't pass an async lambda to InvokeAsync without unwrapping;
                    // otherwise we'd only await the delegate *scheduling*, not its completion.
                    await System.Windows.Application.Current.Dispatcher
                        .InvokeAsync(() => EnsureCoreWebView2WithTimeoutAsync(env))
                        .Task
                        .Unwrap();
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
                            var exeDirectory = System.IO.Path.GetDirectoryName(processPath);
                            if (!string.IsNullOrEmpty(exeDirectory) && System.IO.Directory.Exists(exeDirectory))
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
                
                var webRtcClientPath = System.IO.Path.Combine(baseDirectory, "WebRtcClient");
                MainWindow.Log($"[WebRtcEngineHost] Base directory: {baseDirectory}");
                MainWindow.Log($"[WebRtcEngineHost] WebRtcClient path: {webRtcClientPath}");
                
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
                    MainWindow.Log($"[WebRtcEngineHost] Checking if directory exists: {System.IO.Directory.Exists(webRtcClientPath)}");
                    
                    // Пробуем альтернативные пути
                    var altPath1 = System.IO.Path.Combine(AppContext.BaseDirectory, "WebRtcClient");
                    var altPath2 = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebRtcClient");
                    MainWindow.Log($"[WebRtcEngineHost] Alternative path 1 (AppContext): {altPath1}, exists: {System.IO.Directory.Exists(altPath1)}");
                    MainWindow.Log($"[WebRtcEngineHost] Alternative path 2 (AppDomain): {altPath2}, exists: {System.IO.Directory.Exists(altPath2)}");
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
                
                // КРИТИЧНО: Для перехвата console.log из JavaScript используем DevTools Protocol
                // Но это требует дополнительной настройки. Вместо этого полагаемся на postMessage события (js_log)
                // которые уже реализованы в phone.js через sendEvent('js_log', { message: ... })

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
                        // КРИТИЧНО: Очищаем все старые сессии перед новым звонком для предотвращения блокировок
                        script = $"window.SoftphoneWebRtc.cleanupSessions(); window.SoftphoneWebRtc.makeCall('{number}');";
                        MainWindow.Log($"[WebRtcEngineHost] SendAsync: Script prepared: {script}");
                        break;
                    case "cleanupSessions":
                        script = "window.SoftphoneWebRtc.cleanupSessions();";
                        break;
                    case "answer":
                        script = "window.SoftphoneWebRtc.answer();";
                        break;
                    case "hangup":
                        // КРИТИЧНО: Передаем sessionId в hangup для правильного завершения сессии
                        string? hangupSessionId = null;
                        if (command is Dictionary<string, object> hangupDict && hangupDict.TryGetValue("sessionId", out var hangupSessionIdObj))
                        {
                            hangupSessionId = hangupSessionIdObj?.ToString() ?? "";
                        }
                        else
                        {
                            var hangupCmdType = command.GetType();
                            var sessionIdProp = hangupCmdType.GetProperty("sessionId");
                            hangupSessionId = sessionIdProp?.GetValue(command)?.ToString() ?? "";
                        }
                        
                        if (!string.IsNullOrEmpty(hangupSessionId))
                        {
                            // Экранируем sessionId для JavaScript
                            var escapedSid = hangupSessionId.Replace("\\", "\\\\").Replace("'", "\\'");
                            script = $"window.SoftphoneWebRtc.hangup('{escapedSid}');";
                        }
                        else
                        {
                            script = "window.SoftphoneWebRtc.hangup();";
                        }
                        break;
                    case "setHold":
                        bool holdValue = false;
                        bool holdFound = false;
                        string? holdSessionId = null;

                        if (command is Dictionary<string, object> holdDictCmd)
                        {
                            if (holdDictCmd.TryGetValue("hold", out var holdObj))
                            {
                                holdValue = Convert.ToBoolean(holdObj);
                                holdFound = true;
                            }
                            if (holdDictCmd.TryGetValue("sessionId", out var sidObj))
                            {
                                holdSessionId = sidObj?.ToString();
                            }
                        }
                        else
                        {
                            var holdCmdType = command.GetType();
                            var holdProp = holdCmdType.GetProperty("hold");
                            if (holdProp != null)
                            {
                                var v = holdProp.GetValue(command);
                                if (v != null)
                                {
                                    holdValue = Convert.ToBoolean(v);
                                    holdFound = true;
                                }
                            }
                            var sidProp = holdCmdType.GetProperty("sessionId");
                            if (sidProp != null)
                            {
                                holdSessionId = sidProp.GetValue(command)?.ToString();
                            }
                        }

                        if (!holdFound)
                        {
                            MainWindow.Log("[WebRtcEngineHost] SendAsync: setHold command missing 'hold' property/key");
                            return;
                        }

                        if (!string.IsNullOrEmpty(holdSessionId))
                        {
                            // Escape for JS string literal
                            var escapedSid = holdSessionId.Replace("\\", "\\\\").Replace("'", "\\'");
                            script = $"window.SoftphoneWebRtc.setHold({holdValue.ToString().ToLowerInvariant()}, '{escapedSid}');";
                        }
                        else
                        {
                            script = $"window.SoftphoneWebRtc.setHold({holdValue.ToString().ToLowerInvariant()});";
                        }
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
                    case "checkCallActivity":
                        string? checkSessionId = null;
                        if (command is Dictionary<string, object> checkDict && checkDict.TryGetValue("sessionId", out var sessionIdObj))
                        {
                            checkSessionId = sessionIdObj?.ToString() ?? "";
                        }
                        else
                        {
                            var checkCmdType = command.GetType();
                            var sessionIdProp = checkCmdType.GetProperty("sessionId");
                            checkSessionId = sessionIdProp?.GetValue(command)?.ToString() ?? "";
                        }
                        if (!string.IsNullOrEmpty(checkSessionId))
                        {
                            var escapedSid = checkSessionId.Replace("\\", "\\\\").Replace("'", "\\'");
                            script = $"window.SoftphoneWebRtc.checkCallActivity('{escapedSid}');";
                        }
                        else
                        {
                            script = "window.SoftphoneWebRtc.checkCallActivity();";
                        }
                        break;
                    case "executeScript":
                        // Выполнение произвольного JavaScript кода (для критичных операций)
                        if (command is Dictionary<string, object> execDict && execDict.TryGetValue("script", out var scriptObj))
                        {
                            script = scriptObj?.ToString() ?? "";
                        }
                        else
                        {
                            var execCmdType = command.GetType();
                            var scriptProp = execCmdType.GetProperty("script");
                            script = scriptProp?.GetValue(command)?.ToString() ?? "";
                        }
                        if (string.IsNullOrEmpty(script))
                        {
                            MainWindow.Log("[WebRtcEngineHost] SendAsync: executeScript command missing 'script' property");
                            return;
                        }
                        // script уже установлен, будет выполнен ниже
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
                    // Avoid log spam from watchdog heartbeat (ping/pong) and periodic stats polling.
                    // These run frequently and make the log window unusable.
                    bool isNoisyHeartbeat =
                        cmdValue == "ping" || cmdValue == "getStats" ||
                        script.Contains("window.SoftphoneWebRtc.ping()", StringComparison.OrdinalIgnoreCase) ||
                        script.Contains("window.SoftphoneWebRtc.getStats()", StringComparison.OrdinalIgnoreCase);

                    // Never log initUA script: it includes credentials in JSON.
                    bool isSensitive =
                        cmdValue == "initUA" ||
                        script.Contains("window.SoftphoneWebRtc.initUA(", StringComparison.OrdinalIgnoreCase);

                    if (!isNoisyHeartbeat && !isSensitive)
                    {
                        MainWindow.Log($"[WebRtcEngineHost] SendAsync: Executing script: {script}");
                    }
                    
                    try
                    {
                        // ВАЖНО: выполнять ExecuteScriptAsync на UI thread
                        // Это гарантирует безопасность при вызове из watchdog или других потоков
                        string? result = null;
                        var timeout =
                            cmdValue == "ping" ? HeartbeatScriptTimeout :
                            cmdValue == "getStats" ? StatsScriptTimeout :
                            DefaultScriptTimeout;
                        if (!_webView.Dispatcher.CheckAccess())
                        {
                            try
                            {
                                // IMPORTANT: never block the UI thread by synchronously waiting on ExecuteScriptAsync.
                                // Use InvokeAsync + Unwrap to keep the UI responsive and avoid deadlocks.
                                var execTask = _webView.Dispatcher
                                    .InvokeAsync(async () =>
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
                                    })
                                    .Task
                                    .Unwrap();
                                var completed = await Task.WhenAny(execTask, Task.Delay(timeout));
                                if (completed != execTask)
                                {
                                    if (!isNoisyHeartbeat)
                                    {
                                        MainWindow.Log($"[WebRtcEngineHost] SendAsync: WARNING - ExecuteScriptAsync timed out after {timeout.TotalSeconds:F1}s (cmd={cmdValue})");
                                    }
                                    return;
                                }
                                result = await execTask;
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
                                    var execTask = _webView.CoreWebView2.ExecuteScriptAsync(script);
                                    var completed = await Task.WhenAny(execTask, Task.Delay(timeout));
                                    if (completed != execTask)
                                    {
                                        if (!isNoisyHeartbeat)
                                        {
                                            MainWindow.Log($"[WebRtcEngineHost] SendAsync: WARNING - ExecuteScriptAsync timed out after {timeout.TotalSeconds:F1}s (cmd={cmdValue})");
                                        }
                                        return;
                                    }
                                    result = await execTask;
                                }
                            }
                            catch (ObjectDisposedException)
                            {
                                MainWindow.Log("[WebRtcEngineHost] SendAsync: CoreWebView2 is disposed");
                                return;
                            }
                        }
                        
                        if (!isNoisyHeartbeat)
                        {
                            MainWindow.Log($"[WebRtcEngineHost] SendAsync: Script executed, result: {result ?? "null"}");
                        }
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
                            var t = _webView.Dispatcher.InvokeAsync(() =>
                            {
                                try { return _webView.CoreWebView2 != null; }
                                catch (ObjectDisposedException)
                                {
                                    MainWindow.Log("[WebRtcEngineHost] IsInitialized: WebView2 is disposed");
                                    return false;
                                }
                            }).Task;

                            // Never block indefinitely waiting for UI thread.
                            if (!t.Wait(TimeSpan.FromMilliseconds(250)))
                            {
                                return false;
                            }
                            return t.Result;
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
        private DateTime _lastEngineEventUtc = DateTime.MinValue; // any event from JS (fallback liveness)
        private bool _isResetting = false;
        private DateTime _lastResetUtc = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, DateTime> _incomingCallDedup = new();
        private System.Threading.Timer? _watchdogTimer;
        private int _watchdogInFlight = 0;
        private int _reconnectInFlight = 0;

        // Auto-reconnect (keeps WebRTC "ironclad" connected after network blips).
        private int _reinitInFlight = 0; // prevents duplicate initUA loops (e.g., Save&Connect + timer)
        private DateTime _suppressAutoReconnectUntilUtc = DateTime.MinValue; // grace window after initUA
        private DateTime _lastInitUaSentUtc = DateTime.MinValue;

        private sealed class UaConfigSnapshot
        {
            public string WsUri { get; init; } = "";
            public string SipUri { get; init; } = "";
            public string Username { get; init; } = "";
            public string Password { get; init; } = ""; // in-memory only
        }

        private UaConfigSnapshot? _lastUaConfig;
        private System.Threading.Timer? _reconnectTimer;
        private int _reconnectAttempt = 0;
        private DateTime _nextReconnectUtc = DateTime.MinValue;
        private readonly Random _rng = new Random();

        public bool AutoReconnectEnabled { get; set; } = true;
        /// <summary>
        /// Управляет объемом логирования WebRTC.
        /// При false подавляются подробные js_log и часть детализированных сообщений.
        /// По умолчанию выключено, чтобы логи не захламляли файл — для отладки можно включить вручную.
        /// </summary>
        public bool DebugEnabled { get; set; } = false;

        private void CancelScheduledReconnectLocked(string reason)
        {
            try
            {
                _nextReconnectUtc = DateTime.MinValue;
                // Stop any pending timer firing; keep the timer instance for reuse.
                _reconnectTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            catch { }
        }

        private void TriggerReconnectNow(string reason)
        {
            // Fire-and-forget reconnect attempt with reentrancy guard.
            UaConfigSnapshot? cfg;
            WebRtcEngineHost? engine;
            WebRtcCallState state;
            bool canAttempt;
            var nowUtc = DateTime.UtcNow;

            lock (_lock)
            {
                cfg = _lastUaConfig;
                engine = _engine;
                state = _state;
                canAttempt =
                    AutoReconnectEnabled &&
                    cfg != null &&
                    engine != null &&
                    !_isResetting &&
                    state != WebRtcCallState.Connected &&
                    !_registered &&
                    nowUtc >= _suppressAutoReconnectUntilUtc;
            }

            if (!canAttempt) return;

            if (Interlocked.Exchange(ref _reconnectInFlight, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    MainWindow.Log($"[WebRtcService] AutoReconnect: starting immediate attempt (reason={reason})");
                    await TryReconnectAsync();
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[WebRtcService] AutoReconnect: immediate attempt failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _reconnectInFlight, 0);
                }
            });
        }

        private void ScheduleReconnect(string reason, bool immediate = false)
        {
            UaConfigSnapshot? cfg;
            WebRtcCallState state;
            bool enginePresent;
            string? activeSessionId;
            WebRtcEngineHost? engine;

            lock (_lock)
            {
                if (DateTime.UtcNow < _suppressAutoReconnectUntilUtc)
                {
                    // We are in a grace window right after initUA; ignore transient unregistered/disconnect events.
                    return;
                }

                cfg = _lastUaConfig;
                state = _state;
                activeSessionId = _activeSessionId;
                // After sleep/resume, IsInitialized can transiently report false; don't block reconnect on that.
                enginePresent = _engine != null;
                engine = _engine;

                // Don't spam if disabled or we have no credentials yet.
                if (!AutoReconnectEnabled || cfg == null || string.IsNullOrEmpty(cfg.WsUri) || string.IsNullOrEmpty(cfg.SipUri) || string.IsNullOrEmpty(cfg.Username) || string.IsNullOrEmpty(cfg.Password))
                {
                    return;
                }

                // Don't kill an active connected call: wait for call to end.
                // Проверяем реальную активность звонка асинхронно вне lock блока
                if (state == WebRtcCallState.Connected || state == WebRtcCallState.Calling || state == WebRtcCallState.Ringing)
                {
                    // Проверяем реальную активность звонка асинхронно (вне lock)
                    _ = CheckCallActivityAndScheduleReconnect(reason, immediate, state, activeSessionId, engine);
                    return; // Временно откладываем переподключение до проверки активности
                }

                // If we are already registered/ready, do not schedule reconnect loops.
                if (_registered)
                {
                    return;
                }

                // If engine isn't initialized, we can't do anything here; MainWindow initialization will handle it.
                if (!enginePresent)
                {
                    return;
                }

                // "Immediate" still gets a tiny delay to prevent tight reconnect loops on flapping networks/servers.
                var delay = immediate ? TimeSpan.FromMilliseconds(250) : ComputeReconnectDelayLocked();
                var dueUtc = DateTime.UtcNow + delay;

                // If we already scheduled a sooner reconnect, keep it.
                if (_nextReconnectUtc != DateTime.MinValue && _nextReconnectUtc <= dueUtc)
                {
                    return;
                }

                _nextReconnectUtc = dueUtc;

                _reconnectTimer ??= new System.Threading.Timer(_ =>
                {
                    // Never overlap reconnect attempts (timer can re-enter if init is slow).
                    TriggerReconnectNow("timer");
                });

                _reconnectTimer.Change(delay, System.Threading.Timeout.InfiniteTimeSpan);

                MainWindow.Log($"[WebRtcService] AutoReconnect: scheduled in {delay.TotalSeconds:F1}s (attempt={_reconnectAttempt}, state={state}, reason={reason})");
            }
        }

        // Асинхронная проверка активности звонка перед переподключением
        private async Task CheckCallActivityAndScheduleReconnect(string reason, bool immediate, WebRtcCallState state, string? activeSessionId, WebRtcEngineHost? engine)
        {
            bool isActuallyActive = false;
            
            try
            {
                if (engine != null && engine.IsInitialized && !string.IsNullOrEmpty(activeSessionId))
                {
                    // Используем SendAsync для проверки активности через новую команду
                    var checkCommand = new { cmd = "checkCallActivity", sessionId = activeSessionId };
                    
                    // Создаем TaskCompletionSource для получения результата
                    var tcs = new TaskCompletionSource<bool>();
                    
                    // Подписываемся на событие один раз для получения результата
                    Action<WebRtcEventDto>? handler = null;
                    handler = (dto) =>
                    {
                        if (dto.Type == "call_activity_check" && dto.SessionId == activeSessionId)
                        {
                            if (dto.Data.HasValue && dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (dto.Data.Value.TryGetProperty("active", out var activeEl))
                                {
                                    isActuallyActive = activeEl.GetBoolean();
                                }
                            }
                            Event -= handler;
                            tcs.TrySetResult(true);
                        }
                    };
                    
                    Event += handler;
                    
                    // Отправляем команду проверки
                    await engine.SendAsync(checkCommand);
                    
                    // Ждем результат с таймаутом
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
                    var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        Event -= handler;
                        MainWindow.Log($"[WebRtcService] CheckCallActivity: Timeout waiting for activity check result");
                        // При таймауте считаем звонок активным для безопасности
                        isActuallyActive = true;
                    }
                    else
                    {
                        // Результат получен через событие, isActuallyActive уже установлен в handler
                        await tcs.Task; // Ждем завершения обработки
                    }
                }
            }
            catch (Exception checkEx)
            {
                MainWindow.Log($"[WebRtcService] CheckCallActivity: Error checking call activity: {checkEx.Message}");
                // В случае ошибки проверки, полагаемся на состояние из кода
                isActuallyActive = true; // Безопаснее предположить, что звонок активен
            }
            
            if (isActuallyActive)
            {
                MainWindow.Log($"[WebRtcService] CheckCallActivity: Active call detected (state={state}, sessionId={activeSessionId}), deferring reconnect");
                return; // Не переподключаемся, если звонок активен
            }
            else
            {
                MainWindow.Log($"[WebRtcService] CheckCallActivity: Call state is {state} but call is NOT actually active, proceeding with reconnect");
                // Звонок не активен реально, можно переподключаться
                ScheduleReconnect(reason, immediate);
            }
        }

        private TimeSpan ComputeReconnectDelayLocked()
        {
            // Exponential-ish backoff with cap + jitter.
            // 0, 1, 2, 4, 8, 15, 30, 30, ...
            int attempt = Math.Max(0, _reconnectAttempt);
            int baseSeconds = attempt switch
            {
                // Avoid 0s loops; even the first retry should yield a small pause to prevent reconnect storms.
                0 => 1,
                1 => 1,
                2 => 2,
                3 => 4,
                4 => 8,
                5 => 15,
                _ => 30
            };

            // ±20% jitter
            double jitter = 0.8 + (_rng.NextDouble() * 0.4);
            var seconds = Math.Min(30, Math.Max(0, (int)Math.Round(baseSeconds * jitter)));
            return TimeSpan.FromSeconds(seconds);
        }

        private async Task TryReconnectAsync()
        {
            UaConfigSnapshot? cfg;
            WebRtcEngineHost? engine;
            WebRtcCallState state;
            bool shouldReconnect;
            int attemptSnapshot;

            lock (_lock)
            {
                cfg = _lastUaConfig;
                engine = _engine;
                state = _state;
                shouldReconnect =
                    AutoReconnectEnabled &&
                    cfg != null &&
                    engine != null &&
                    !_isResetting &&
                    state != WebRtcCallState.Connected &&
                    !_registered &&
                    DateTime.UtcNow >= _suppressAutoReconnectUntilUtc;

                // Clear schedule marker so subsequent failures can schedule again.
                _nextReconnectUtc = DateTime.MinValue;

                if (shouldReconnect)
                {
                    _reconnectAttempt = Math.Min(_reconnectAttempt + 1, 1000);
                }
                attemptSnapshot = _reconnectAttempt;
            }

            if (!shouldReconnect || cfg == null)
            {
                return;
            }

            try
            {
                MainWindow.Log($"[WebRtcService] AutoReconnect: attempting initUA (attempt={_reconnectAttempt}, state={state}, wsUri={(string.IsNullOrEmpty(cfg.WsUri) ? "empty" : "set")}, sipUri={cfg.SipUri})");
                await ReinitializeUAAsync(cfg.WsUri, cfg.SipUri, cfg.Username, cfg.Password);

                // If no events arrive (e.g., WS down or JS stuck), schedule a follow-up attempt only if
                // we are still not registered after a short grace period.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(6));
                        lock (_lock)
                        {
                            if (!_registered && _engine != null && !_isResetting && _state != WebRtcCallState.Connected && _reconnectAttempt == attemptSnapshot)
                            {
                                ScheduleReconnect("post_initUA_check", immediate: false);
                            }
                        }
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRtcService] AutoReconnect: attempt failed: {ex.Message}");
                ScheduleReconnect("exception", immediate: false);
            }
        }

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

        /// <summary>
        /// Detaches the current engine and resets WebRTC runtime state (used when switching to SIP mode).
        /// </summary>
        public void DetachEngine(bool resetState = true)
        {
            System.Threading.Timer? watchdogToDispose = null;
            System.Threading.Timer? reconnectToDispose = null;

            lock (_lock)
            {
                try
                {
                    if (_engine != null)
                    {
                        _engine.EngineEvent -= OnEngineEvent;
                    }
                }
                catch
                {
                    // ignore
                }

                _engine = null;

                // Stop timers when detaching engine (prevents idle leaks and reconnect loops after switching to SIP).
                watchdogToDispose = _watchdogTimer;
                _watchdogTimer = null;
                reconnectToDispose = _reconnectTimer;
                _reconnectTimer = null;
                _nextReconnectUtc = DateTime.MinValue;
                _reconnectAttempt = 0;
                System.Threading.Interlocked.Exchange(ref _watchdogInFlight, 0);
                System.Threading.Interlocked.Exchange(ref _reconnectInFlight, 0);

                if (resetState)
                {
                    _registered = false;
                    _activeSessionId = null;
                    _ringingSessionId = null;
                    _state = WebRtcCallState.Idle;
                }
            }

            try { watchdogToDispose?.Dispose(); } catch { }
            try { reconnectToDispose?.Dispose(); } catch { }
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
                // Any event from JS indicates the WebView2 runtime is alive (even if pong is missing).
                lock (_lock)
                {
                    _lastEngineEventUtc = DateTime.UtcNow;
                }

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
                
                // Логируем только критичные события (без полного JSON)
                if (type == "reg_failed" || type == "ua_registration_failed" || type == "error" || type == "ws_disconnected")
                {
                    MainWindow.Log($"[WebRtcService] Event: {type}");
                }
                
                // Обработка события js_log для логирования сообщений из JavaScript
                // КРИТИЧНО: Всегда логируем js_log с level='critical', даже если DebugEnabled=false
                if (type == "js_log")
                {
                    try
                    {
                        if (evt.TryGetProperty("data", out var logDataEl) && logDataEl.ValueKind == JsonValueKind.Object)
                        {
                            string? logLevel = null;
                            if (logDataEl.TryGetProperty("level", out var levelEl))
                            {
                                logLevel = GetAsString(levelEl);
                            }
                            
                            if (logDataEl.TryGetProperty("message", out var msgEl))
                            {
                                string? logMessage = GetAsString(msgEl);
                                if (!string.IsNullOrEmpty(logMessage))
                                {
                                    // КРИТИЧНО: Всегда логируем critical сообщения, остальные только при DebugEnabled
                                    if (logLevel == "critical" || DebugEnabled)
                                    {
                                        MainWindow.Log($"[WebRTC JS] {logMessage}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception logEx)
                    {
                        MainWindow.Log($"[WebRtcService] Error processing js_log event: {logEx.Message}");
                    }
                }
                
                // Логируем только важные события (регистрация, ошибки, аудио для диагностики)
                // ВАЖНО: Добавляем "ua_registered" и "ua_connected" для полного логирования.
                // При выключенном DebugEnabled оставляем только самые критичные события.
                if (type == "registered" || type == "ua_registered" || type == "reg_failed" || type == "unregistered" || 
                    type == "ua_connected" || type == "ws_disconnected" || type == "error" || 
                    type == "audio_connected" || type == "audio_playing" || type == "audio_track_info" ||
                    type == "call_accepted" || type == "call_confirmed")
                {
                    if (DebugEnabled ||
                        type == "error" ||
                        type == "reg_failed" ||
                        type == "ws_disconnected")
                    {
                        MainWindow.Log($"[WebRtcService] OnEngineEvent: {type}");
                    }
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
                            _reconnectAttempt = 0;
                            _nextReconnectUtc = DateTime.MinValue;
                            CancelScheduledReconnectLocked("registered");
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
                            
                            // КРИТИЧНО: Если есть активный звонок, проверяем реальную активность перед отменой переподключения
                            // Активные звонки могут продолжаться даже при временных проблемах с регистрацией
                            if (_state == WebRtcCallState.Connected || _state == WebRtcCallState.Calling || _state == WebRtcCallState.Ringing)
                            {
                                // Проверяем реальную активность звонка асинхронно (вне обработчика события)
                                var currentState = _state;
                                var currentSessionId = _activeSessionId;
                                var currentEngine = _engine;
                                _ = Task.Run(async () =>
                                {
                                    bool isActuallyActive = false;
                                    try
                                    {
                                        if (currentEngine != null && currentEngine.IsInitialized && !string.IsNullOrEmpty(currentSessionId))
                                        {
                                            var checkCommand = new { cmd = "checkCallActivity", sessionId = currentSessionId };
                                            await currentEngine.SendAsync(checkCommand);
                                            // Результат будет получен через событие call_activity_check
                                            // Пока что полагаемся на базовую проверку состояния
                                            isActuallyActive = true; // Безопаснее предположить активность
                                        }
                                    }
                                    catch (Exception checkEx)
                                    {
                                        MainWindow.Log($"[WebRtcService] Error checking call activity: {checkEx.Message}");
                                        isActuallyActive = true; // Безопаснее предположить, что звонок активен
                                    }
                                    
                                    if (!isActuallyActive)
                                    {
                                        MainWindow.Log($"[WebRtcService] OnEngineEvent: UA registration failed ({type}) during call (state={currentState}), but call is NOT actually active, proceeding with reconnect");
                                        ScheduleReconnect(type ?? "reg_failed", immediate: true);
                                    }
                                });
                                
                                MainWindow.Log($"[WebRtcService] OnEngineEvent: UA registration failed ({type}) during active call (state={_state}), deferring reconnect to avoid call interruption");
                                // НЕ сбрасываем _registered, чтобы звонок мог продолжаться
                                // Запланируем переподключение после завершения звонка
                                dto.Message = regErrorDetails ?? "Registration failed (deferred - active call)";
                                // НЕ вызываем ScheduleReconnect здесь синхронно - это переинициализирует UA и прервет звонок
                                break;
                            }
                            
                            // Если регистрации не было недавно и нет активного звонка, сбрасываем статус
                            _registered = false;
                            _lastRegisteredUtc = DateTime.MinValue;
                            MainWindow.Log($"[WebRtcService] OnEngineEvent: UA registration failed ({type})" + 
                                (string.IsNullOrEmpty(regErrorDetails) ? "" : $" - {regErrorDetails}"));
                            dto.Message = regErrorDetails ?? "Registration failed";
                            // Auto-reconnect quickly if credentials are known.
                            // Schedule a reconnect; ScheduleReconnect already dedupes earlier attempts.
                            // Avoid also triggering an immediate attempt here to prevent reconnect storms.
                            ScheduleReconnect(type ?? "reg_failed", immediate: true);
                            break;
                        case "unregistered":
                        case "ua_unregistered":
                            // Ignore transient unregistered events right after an intentional initUA.
                            if (DateTime.UtcNow < _suppressAutoReconnectUntilUtc)
                            {
                                MainWindow.Log("[WebRtcService] AutoReconnect: suppressed (ua_unregistered during initUA grace window)");
                                break;
                            }
                            _registered = false;
                            _lastRegisteredUtc = DateTime.MinValue;
                            MainWindow.Log($"[WebRtcService] OnEngineEvent: UA unregistered ({type})");
                            ScheduleReconnect(type ?? "ua_unregistered", immediate: true);
                            break;
                        case "ws_disconnected":
                            // Ignore transient WS disconnect events right after an intentional initUA.
                            if (DateTime.UtcNow < _suppressAutoReconnectUntilUtc)
                            {
                                MainWindow.Log("[WebRtcService] AutoReconnect: suppressed (ws_disconnected during initUA grace window)");
                                break;
                            }
                            // WS disconnect means we are not healthy/ready for calls. Always drop registered state so
                            // auto-reconnect can proceed deterministically (prevents reconnect loops while "registered=true").
                            _registered = false;
                            _lastRegisteredUtc = DateTime.MinValue;
                            MainWindow.Log($"[WebRtcService] OnEngineEvent: ws_disconnected - resetting registered state");
                            ScheduleReconnect("ws_disconnected", immediate: true);
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
                            JsonElement acceptedDataEl = default;
                            if (string.IsNullOrEmpty(sessionId) && evt.TryGetProperty("data", out acceptedDataEl) && acceptedDataEl.ValueKind == JsonValueKind.Object)
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
                                
                                // Извлекаем source для логирования
                                string? source = null;
                                if (evt.TryGetProperty("data", out acceptedDataEl) && acceptedDataEl.ValueKind == JsonValueKind.Object && acceptedDataEl.TryGetProperty("source", out var sourceEl))
                                {
                                    source = GetAsString(sourceEl);
                                }
                                if (!string.IsNullOrEmpty(source))
                                {
                                    MainWindow.Log($"[WebRtcService] OnEngineEvent: call_accepted source: {source}");
                                }
                            }
                            break;
                        case "audio_connected":
                            // КРИТИЧНО: Для исходящих звонков audio_connected означает, что удаленный аудио трек подключен
                            // Это первый признак принятия звонка - отправляем call_accepted напрямую
                            if (string.IsNullOrEmpty(sessionId) && evt.TryGetProperty("data", out var audioDataEl) && audioDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (audioDataEl.TryGetProperty("sessionId", out var audioSessionIdEl))
                                {
                                    sessionId = GetAsString(audioSessionIdEl);
                                    dto.SessionId = sessionId;
                                }
                            }
                            
                            // Если это активная сессия и звонок еще не принят, отправляем call_accepted напрямую
                            if ((sessionId == _activeSessionId || string.IsNullOrEmpty(_activeSessionId)) && 
                                (_state == WebRtcCallState.Calling || _state == WebRtcCallState.Ringing))
                            {
                                MainWindow.Log($"[WebRtcService] OnEngineEvent: audio_connected for session {sessionId} during calling state, sending call_accepted directly");
                                
                                // Отправляем call_accepted напрямую как событие (JavaScript уже должен был отправить, но на всякий случай)
                                // Создаем событие call_accepted и отправляем его подписчикам
                                JsonElement? audioConnectedData = null;
                                if (evt.TryGetProperty("data", out var audioConnectedDataEl))
                                {
                                    audioConnectedData = audioConnectedDataEl;
                                }
                                
                                var acceptedDto = new WebRtcEventDto
                                {
                                    Type = "call_accepted",
                                    SessionId = sessionId ?? _activeSessionId,
                                    Data = audioConnectedData
                                };
                                
                                // Отправляем событие подписчикам
                                var acceptedHandlers = Event;
                                if (acceptedHandlers != null)
                                {
                                    foreach (var d in acceptedHandlers.GetInvocationList())
                                    {
                                        if (d is Action<WebRtcEventDto> h)
                                        {
                                            try
                                            {
                                                h(acceptedDto);
                                            }
                                            catch (Exception cbEx)
                                            {
                                                MainWindow.Log($"[WebRtcService] ERROR: WebRTC event subscriber threw for call_accepted: {cbEx.Message}");
                                            }
                                        }
                                    }
                                }
                            }
                            break;
                        case "call_failed":
                        case "call_ended":
                            // КРИТИЧНО: Останавливаем все звуки при завершении/неудаче звонка
                            try { RingtoneService.Instance.Stop(); } catch { }
                            try { RingbackToneService.Instance.Stop(); } catch { }

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
                                    dto.Cause = causeStr;
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
                                if (failedDataEl.TryGetProperty("message", out var msgEl))
                                {
                                    dto.Message = GetAsString(msgEl);
                                }
                            }
                            
                            // Детальное логирование причины завершения звонка
                            var reasonParts = new System.Collections.Generic.List<string>();
                            if (!string.IsNullOrEmpty(causeStr)) reasonParts.Add($"cause={causeStr}");
                            if (!string.IsNullOrEmpty(originatorStr)) reasonParts.Add($"originator={originatorStr}");
                            if (!string.IsNullOrEmpty(statusCodeStr)) reasonParts.Add($"status_code={statusCodeStr}");
                            if (!string.IsNullOrEmpty(reasonPhraseStr)) reasonParts.Add($"reason={reasonPhraseStr}");
                            var reasonDetails = reasonParts.Count > 0 ? " (" + string.Join(", ", reasonParts) + ")" : "";
                            
                            // КРИТИЧНО: Всегда сбрасываем состояние при call_ended или call_failed
                            // Это важно для возможности нового звонка
                            MainWindow.Log($"[WebRtcService] Call {type} for session {sessionId ?? "null"}{reasonDetails}, resetting state to Idle");
                            lock (_lock)
                            {
                                // КРИТИЧНО: Принудительно очищаем все состояния при завершении звонка
                                _state = WebRtcCallState.Idle;
                                _activeSessionId = null;
                                _ringingSessionId = null;
                                
                                // Также очищаем дедупликацию входящих звонков для этого sessionId
                                if (!string.IsNullOrEmpty(sessionId))
                                {
                                    _incomingCallDedup.TryRemove(sessionId, out _);
                                }
                            }
                            
                            // После завершения звонка проверяем, нужно ли переподключение
                            // Если была проблема с регистрацией во время звонка, теперь можно переподключиться
                            if (!_registered && _lastRegisteredUtc == DateTime.MinValue)
                            {
                                MainWindow.Log("[WebRtcService] Call ended, scheduling reconnect if needed");
                                ScheduleReconnect("post_call_reconnect", immediate: false);
                            }
                            break;
                        case "ping":
                            // Событие ping от JS не должно обрабатываться здесь
                            // Ping отправляется из C# в JS, а pong приходит обратно как событие
                            // Если это событие пришло, просто игнорируем его
                            MainWindow.Log("[WebRtcService] OnEngineEvent: Received ping event from JS (unexpected, ignoring)");
                            break;
                        case "call_activity_check":
                            // Результат проверки активности звонка
                            // Это событие используется для асинхронной проверки реальной активности звонка
                            // перед переподключением при reg_failed
                            if (evt.TryGetProperty("data", out var activityDataEl) && activityDataEl.ValueKind == JsonValueKind.Object)
                            {
                                bool? isActive = null;
                                if (activityDataEl.TryGetProperty("active", out var activeEl))
                                {
                                    if (activeEl.ValueKind == JsonValueKind.True || activeEl.ValueKind == JsonValueKind.False)
                                    {
                                        isActive = activeEl.GetBoolean();
                                    }
                                }
                                MainWindow.Log($"[WebRtcService] OnEngineEvent: call_activity_check - active={isActive}, sessionId={sessionId}");
                            }
                            // Это событие обрабатывается асинхронно в CheckCallActivityAndScheduleReconnect
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
                                    // Don't log successful pongs: watchdog runs frequently and this is very noisy.
                                }
                                else
                                {
                                    // Don't log pongs at all; watchdog will handle missing pongs.
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
                                    try { RingbackToneService.Instance.Stop(); } catch { }
                                    try { RingtoneService.Instance.Stop(); } catch { }
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

                // Event handlers are user-code; one buggy subscriber must not prevent others from receiving critical events
                // (e.g., CallWindow must always receive call_failed/call_ended to stop ringback and close).
                var handlers = Event;
                if (handlers != null)
                {
                    // DIAGNOSTICS: log only for a few important event types to avoid spam.
                    if (dto.Type == "registered" || dto.Type == "ua_registered" || dto.Type == "reg_failed" || dto.Type == "ua_registration_failed" ||
                        dto.Type == "ua_connected" || dto.Type == "ws_connected" || dto.Type == "call_failed" || dto.Type == "call_ended")
                    {
                        MainWindow.Log($"[WebRtcService] OnEngineEvent: Dispatching {handlers.GetInvocationList().Length} subscriber(s) for type='{dto.Type}', registered={_registered}, IsReadyForCalls={IsReadyForCalls}");
                    }

                    foreach (var d in handlers.GetInvocationList())
                    {
                        if (d is Action<WebRtcEventDto> h)
                        {
                            try
                            {
                                h(dto);
                            }
                            catch (Exception cbEx)
                            {
                                MainWindow.Log($"[WebRtcService] ERROR: WebRTC event subscriber threw for type='{dto.Type}': {cbEx.Message}");
                            }
                        }
                    }
                }
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

            // КРИТИЧНО: Если предыдущий звонок завершился некорректно, очищаем состояние перед новым входящим звонком
            lock (_lock)
            {
                if (_state == WebRtcCallState.Ending || _state == WebRtcCallState.Ended)
                {
                    MainWindow.Log($"[WebRtcService] HandleIncomingCall: Resetting state from {_state} to Idle before processing incoming call");
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                    _ringingSessionId = null;
                }
            }

            // КРИТИЧНО: Проверяем активный звонок и принудительно очищаем состояние Ringing/Connected если оно "зависло"
            lock (_lock)
            {
                // Если состояние Ringing или Connected, но нет активной сессии - это "зависшее" состояние
                // Очищаем его перед обработкой нового входящего звонка
                if ((_state == WebRtcCallState.Ringing || _state == WebRtcCallState.Connected || _state == WebRtcCallState.Calling) 
                    && string.IsNullOrEmpty(_activeSessionId) && string.IsNullOrEmpty(_ringingSessionId))
                {
                    MainWindow.Log($"[WebRtcService] HandleIncomingCall: Clearing stuck state {_state} (no active session)");
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                    _ringingSessionId = null;
                }
                
                if (_state != WebRtcCallState.Idle && _state != WebRtcCallState.Ended)
                {
                    MainWindow.Log($"[WebRtcService] Incoming call {sessionId} ignored (active call in state {_state})");
                    return;
                }
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
            // Extra guard: MainWindow should already block calls when IsReadyForCalls=false, but keep this here
            // so calls from other places can't create a "ghost" outgoing call that never reaches PBX.
            // Also log enough state to diagnose "PBX sees no attempt".
            lock (_lock)
            {
                // КРИТИЧНО: Если состояние Ending, Ended, Calling или Ringing - принудительно очищаем его
                // Это важно для случаев, когда предыдущий звонок не завершился корректно
                if (_state == WebRtcCallState.Ending || _state == WebRtcCallState.Ended || 
                    _state == WebRtcCallState.Calling || _state == WebRtcCallState.Ringing)
                {
                    MainWindow.Log($"[WebRtcService] MakeCallAsync: Resetting state from {_state} to Idle (previous call may not have completed properly)");
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                    _ringingSessionId = null;
                    
                    // КРИТИЧНО: Принудительно очищаем сессии в JavaScript перед новым звонком
                    // Это предотвращает блокировку нового звонка из-за "зависших" сессий
                    if (_engine != null && _engine.IsInitialized)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _engine.SendAsync(new { cmd = "cleanupSessions" });
                                MainWindow.Log("[WebRtcService] MakeCallAsync: cleanupSessions command sent");
                            }
                            catch (Exception cleanupEx)
                            {
                                MainWindow.Log($"[WebRtcService] MakeCallAsync: Error sending cleanupSessions: {cleanupEx.Message}");
                            }
                        });
                    }
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
                try
                {
                    await _engine.SendAsync(new { cmd = "makeCall", number });
                    MainWindow.Log($"[WebRtcService] MakeCallAsync: Command sent successfully");
                }
                catch (Exception ex)
                {
                    // If we fail to execute the JS call, PBX will not see any INVITE attempt.
                    lock (_lock)
                    {
                        _state = WebRtcCallState.Idle;
                        _activeSessionId = null;
                    }
                    MainWindow.Log($"[WebRtcService] MakeCallAsync: ERROR sending makeCall command: {ex.Message}");
                    throw;
                }
            }
            else
            {
                MainWindow.Log($"[WebRtcService] MakeCallAsync: ERROR - engine is null");
                lock (_lock)
                {
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                }
                throw new InvalidOperationException("WebRTC engine is null");
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
            // Stop app-side ringtone immediately when we initiate hangup.
            try { RingtoneService.Instance.Stop(); } catch { }
            try { RingbackToneService.Instance.Stop(); } catch { }

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
                            try { RingbackToneService.Instance.Stop(); } catch { }
                            try { RingtoneService.Instance.Stop(); } catch { }
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
                try { RingbackToneService.Instance.Stop(); } catch { }
                try { RingtoneService.Instance.Stop(); } catch { }
                lock (_lock)
                {
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                    _ringingSessionId = null;
                }
            }
        }

        public async Task SetHoldAsync(bool hold, string? sessionId = null)
        {
            if (_engine == null || !_engine.IsInitialized)
            {
                throw new InvalidOperationException("WebRTC engine not initialized");
            }

            // Prefer explicit session id; otherwise fallback to the service-tracked session.
            sessionId ??= _activeSessionId ?? _ringingSessionId;

            var cmd = new Dictionary<string, object>
            {
                { "cmd", "setHold" },
                { "hold", hold }
            };
            if (!string.IsNullOrEmpty(sessionId))
            {
                cmd["sessionId"] = sessionId!;
            }

            await _engine.SendAsync(cmd);
            MainWindow.Log($"[WebRtcService] SetHoldAsync: {(hold ? "hold" : "resume")} command sent (sessionId={sessionId ?? "null"})");
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
            // After sleep/resume IsInitialized can transiently return false; try anyway.
            if (_engine == null)
            {
                MainWindow.Log("[WebRtcService] ReinitializeUAAsync: Engine not initialized, skipping");
                return;
            }

            // Guard against duplicate initUA loops (e.g. Save&Connect triggers initUA while reconnect timer is firing).
            if (System.Threading.Interlocked.Exchange(ref _reinitInFlight, 1) == 1)
            {
                MainWindow.Log("[WebRtcService] ReinitializeUAAsync: initUA already in-flight, skipping");
                return;
            }

            try
            {
                // Store last config for auto-reconnect.
                lock (_lock)
                {
                    _lastUaConfig = new UaConfigSnapshot
                    {
                        WsUri = wsUri ?? "",
                        SipUri = sipUri ?? "",
                        Username = username ?? "",
                        Password = password ?? ""
                    };

                    // Grace window: JsSIP often emits ua_unregistered/ws_disconnected during intentional re-init.
                    // During this window we must not schedule auto-reconnect attempts, otherwise we create loops.
                    _lastInitUaSentUtc = DateTime.UtcNow;
                    _suppressAutoReconnectUntilUtc = _lastInitUaSentUtc + TimeSpan.FromSeconds(4);

                    // Cancel any scheduled reconnect timers from previous failures.
                    CancelScheduledReconnectLocked("manual_reinit");
                    _reconnectAttempt = 0;
                }

                MainWindow.Log($"[WebRtcService] ReinitializeUAAsync: Reinitializing UA with new credentials (user={username}, sipUri={sipUri}, debug={DebugEnabled})");
                await _engine.SendAsync(new
                {
                    cmd = "initUA",
                    wsUri = wsUri,
                    sipUri = sipUri,
                    user = username,
                    pass = password,
                    enableDebug = DebugEnabled
                });
                MainWindow.Log("[WebRtcService] ReinitializeUAAsync: initUA command sent");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRtcService] ERROR in ReinitializeUAAsync: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _reinitInFlight, 0);
            }
        }

        public void StartWatchdog()
        {
            lock (_lock)
            {
                if (_watchdogTimer != null) return; // idempotent

                // Avoid false "no pong" resets after sleep/resume: treat watchdog start as a fresh baseline.
                _lastPongTimeUtc = DateTime.UtcNow;
                _lastEngineEventUtc = DateTime.UtcNow;

                _watchdogTimer = new System.Threading.Timer(_ =>
                {
                    // Timer callbacks must never be async-void; run async work on a Task and guard reentrancy.
                    if (System.Threading.Interlocked.Exchange(ref _watchdogInFlight, 1) == 1) return;

                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            WebRtcEngineHost? engine;
                            WebRtcCallState state;
                            DateTime lastPong;
                            DateTime lastAnyEvent;
                            bool resetting;
                            DateTime lastReset;

                            lock (_lock)
                            {
                                engine = _engine;
                                state = _state;
                                lastPong = _lastPongTimeUtc;
                                lastAnyEvent = _lastEngineEventUtc;
                                resetting = _isResetting;
                                lastReset = _lastResetUtc;
                            }

                            if (engine == null || !engine.IsInitialized) return;

                            // Ping (SendAsync has its own timeout).
                            await engine.SendAsync(new { cmd = "ping" });

                            // Cooldown: don't spam reset if we're already resetting.
                            if (resetting || (DateTime.UtcNow - lastReset).TotalSeconds < 20) return;

                            // Consider both pong and any JS event as liveness: after resume ping may fail briefly
                            // while WS/registration events still flow, and we must not reset the engine in that case.
                            var lastAlive = lastPong > lastAnyEvent ? lastPong : lastAnyEvent;
                            if (lastAlive != DateTime.MinValue && (DateTime.UtcNow - lastAlive).TotalSeconds > 30)
                            {
                                MainWindow.Log($"[WebRtcService] WARNING: No WebRTC liveness received for {(DateTime.UtcNow - lastAlive).TotalSeconds:F1} seconds, resetting engine...");
                                // Force registered=false so auto-reconnect can proceed deterministically after reset.
                                lock (_lock)
                                {
                                    _registered = false;
                                    _lastRegisteredUtc = DateTime.MinValue;
                                }
                                _ = ResetEngineAsync();
                                TriggerReconnectNow("watchdog_no_pong");
                            }

                            // Request stats only during active calls.
                            if (state == WebRtcCallState.Connected)
                            {
                                _ = engine.SendAsync(new { cmd = "getStats" });
                            }
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"[WebRtcService] ERROR in watchdog: {ex.Message}");
                        }
                        finally
                        {
                            System.Threading.Interlocked.Exchange(ref _watchdogInFlight, 0);
                        }
                    });
                }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
            }
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
                    System.Threading.Interlocked.Exchange(ref _watchdogInFlight, 0);
                    MainWindow.Log("[WebRtcService] Watchdog timer stopped");
                }
            }
        }

        /// <summary>
        /// Stops auto-reconnect attempts and clears any scheduled reconnect.
        /// Useful when switching to SIP mode or shutting down.
        /// </summary>
        public void StopAutoReconnect()
        {
            System.Threading.Timer? toDispose = null;
            lock (_lock)
            {
                toDispose = _reconnectTimer;
                _reconnectTimer = null;
                _nextReconnectUtc = DateTime.MinValue;
                _reconnectAttempt = 0;
                System.Threading.Interlocked.Exchange(ref _reconnectInFlight, 0);
            }
            try { toDispose?.Dispose(); } catch { }
        }
    }
}


