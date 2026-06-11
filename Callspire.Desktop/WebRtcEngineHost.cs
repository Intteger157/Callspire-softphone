#if WINDOWS
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;

namespace Softphone
{
    /// <summary>
    /// ���� ��� WebRTC engine (WebView2 wrapper).
    /// Windows-���������� <see cref="IWebRtcEngineHost"/>: JS-������ (sip.js bundle)
    /// ����������� � ������� WebView2; �������/������� ����� ����� ExecuteScript/WebMessage.
    /// </summary>
    public class WebRtcEngineHost : IWebRtcEngineHost
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
                    AppLog.Log("[WebRtcEngineHost] Already initialized, skipping");
                    return;
                }

                AppLog.Log("[WebRtcEngineHost] Starting initialization...");
                
                // ��������� ��������� WebView2 ����� ��������������
                AppLog.Log($"[WebRtcEngineHost] WebView2 state check: IsLoaded={_webView.IsLoaded}, Parent={_webView.Parent?.GetType().Name ?? "null"}, Visibility={_webView.Visibility}, Opacity={_webView.Opacity}");
                
                // ����������� ����� �� UI thread
                if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
                {
                    AppLog.Log("[WebRtcEngineHost] WARNING: Not on UI thread, switching...");
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
                
                // ��������� ���������
                if (_webView.CoreWebView2 == null)
                {
                    throw new InvalidOperationException("CoreWebView2 is null after initialization");
                }
                
                AppLog.Log($"[WebRtcEngineHost] ? CoreWebView2 ensured successfully");
                
                // ������������� ��������� �������� ��� softphone.local
                // ��� �������� ��� �������� WebView2, ����� getUserMedia ������ NotAllowedError
                _webView.CoreWebView2.PermissionRequested += (sender, e) =>
                {
                    if (e.Uri != null && e.Uri.StartsWith("https://softphone.local", StringComparison.OrdinalIgnoreCase))
                    {
                        if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                        {
                            e.State = CoreWebView2PermissionState.Allow;
                            e.Handled = true;
                            AppLog.Log($"[WebRtcEngineHost] PermissionRequested: Microphone ALLOWED for {e.Uri}");
                        }
                        else
                        {
                            AppLog.Log($"[WebRtcEngineHost] PermissionRequested: {e.PermissionKind} requested for {e.Uri} (denying)");
                            e.State = CoreWebView2PermissionState.Deny;
                            e.Handled = true;
                        }
                    }
                    else
                    {
                        AppLog.Log($"[WebRtcEngineHost] PermissionRequested: {e.PermissionKind} requested for {e.Uri} (denying - not softphone.local)");
                        e.State = CoreWebView2PermissionState.Deny;
                        e.Handled = true;
                    }
                };
                AppLog.Log("[WebRtcEngineHost] PermissionRequested handler subscribed");
                
                // ����������� ����������� ����
                // ��������: � single-file ������ AppDomain.CurrentDomain.BaseDirectory ��������� �� ��������� ����� ����������
                // ���������� AppContext.BaseDirectory ��� ����������� ���� � exe � single-file ������
                // � single-file ������ Assembly.Location ������ ������ ������, ������� ���������� AppContext.BaseDirectory
                string baseDirectory = AppContext.BaseDirectory;
                
                // �������������� ��������: ���� AppContext.BaseDirectory ��������� �� ��������� �����,
                // ������� �������� ���� � exe ����� Process.GetCurrentProcess().MainModule.FileName
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
                        // ��������� AppContext.BaseDirectory
                    }
                }
                
                var webRtcClientPath = System.IO.Path.Combine(baseDirectory, "WebRtcClient");
                AppLog.Log($"[WebRtcEngineHost] Base directory: {baseDirectory}");
                AppLog.Log($"[WebRtcEngineHost] WebRtcClient path: {webRtcClientPath}");
                
                if (System.IO.Directory.Exists(webRtcClientPath))
                {
                    AppLog.Log($"[WebRtcEngineHost] Setting virtual host mapping: softphone.local -> {webRtcClientPath}");
                    _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "softphone.local",
                        webRtcClientPath,
                        CoreWebView2HostResourceAccessKind.Allow);
                    AppLog.Log("[WebRtcEngineHost] Virtual host mapping set");
                }
                else
                {
                    AppLog.Log($"[WebRtcEngineHost] ERROR: WebRtcClient directory not found: {webRtcClientPath}");
                    AppLog.Log($"[WebRtcEngineHost] Checking if directory exists: {System.IO.Directory.Exists(webRtcClientPath)}");
                    
                    // ������� �������������� ����
                    var altPath1 = System.IO.Path.Combine(AppContext.BaseDirectory, "WebRtcClient");
                    var altPath2 = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebRtcClient");
                    AppLog.Log($"[WebRtcEngineHost] Alternative path 1 (AppContext): {altPath1}, exists: {System.IO.Directory.Exists(altPath1)}");
                    AppLog.Log($"[WebRtcEngineHost] Alternative path 2 (AppDomain): {altPath2}, exists: {System.IO.Directory.Exists(altPath2)}");
                }

                // ������������� �� ��������� �� �������� ��������
                _webView.CoreWebView2.WebMessageReceived += (sender, e) =>
                {
                    var json = e.TryGetWebMessageAsString();
                    if (!string.IsNullOrEmpty(json))
                    {
                        EngineEvent?.Invoke(json);
                    }
                };
                AppLog.Log("[WebRtcEngineHost] WebMessageReceived handler subscribed");
                
                // ��������: ��� ��������� console.log �� JavaScript ���������� DevTools Protocol
                // �� ��� ������� �������������� ���������. ������ ����� ���������� �� postMessage ������� (js_log)
                // ������� ��� ����������� � phone.js ����� sendEvent('js_log', { message: ... })

                // ���� �������� �������� ����� ���������� ����� _initialized
                // �����: ������������� �� ��������� Source, ����� ������� ����� ���� ���������
                var navigationCompleted = new System.Threading.Tasks.TaskCompletionSource<bool>();
                bool navigationEventFired = false;
                
                _webView.CoreWebView2.NavigationCompleted += (sender, e) =>
                {
                    navigationEventFired = true;
                    AppLog.Log($"[WebRtcEngineHost] NavigationCompleted event fired: IsSuccess={e.IsSuccess}, WebErrorStatus={e.WebErrorStatus}");
                    if (e.IsSuccess)
                    {
                        AppLog.Log($"[WebRtcEngineHost] Navigation completed successfully: {url}");
                        navigationCompleted.SetResult(true);
                    }
                    else
                    {
                        AppLog.Log($"[WebRtcEngineHost] Navigation failed: {e.WebErrorStatus}");
                        navigationCompleted.SetResult(false);
                    }
                };
                AppLog.Log("[WebRtcEngineHost] NavigationCompleted handler subscribed");

                // ��������� �������� ����� �������� �� �������
                AppLog.Log($"[WebRtcEngineHost] Loading page: {url}");
                _webView.Source = new Uri(url);
                AppLog.Log("[WebRtcEngineHost] Source set, waiting for navigation...");
                
                // ���� �������� �������� (������� 10 ������)
                var navTimeoutTask = System.Threading.Tasks.Task.Delay(10000);
                var navCompletedTask = await System.Threading.Tasks.Task.WhenAny(navigationCompleted.Task, navTimeoutTask);
                
                if (navCompletedTask == navTimeoutTask)
                {
                    AppLog.Log($"[WebRtcEngineHost] WARNING: Page load timeout after 10s (navigationEventFired={navigationEventFired})");
                    // ���������, ����� �������� ��� �����������
                    if (_webView.CoreWebView2 != null)
                    {
                        var currentUrl = _webView.CoreWebView2.Source;
                        AppLog.Log($"[WebRtcEngineHost] Current URL: {currentUrl}");
                        // ����������, ���� CoreWebView2 ��������
                        _initialized = true;
                        AppLog.Log("[WebRtcEngineHost] Initialized despite timeout (CoreWebView2 available)");
                        return;
                    }
                    else
                    {
                        AppLog.Log("[WebRtcEngineHost] ERROR: CoreWebView2 is null after timeout");
                        throw new InvalidOperationException("WebView2 initialization timeout - CoreWebView2 is null");
                    }
                }
                else if (await navigationCompleted.Task)
                {
                    AppLog.Log("[WebRtcEngineHost] ? Page loaded successfully");
                }
                else
                {
                    AppLog.Log("[WebRtcEngineHost] WARNING: Navigation completed with error, but continuing...");
                }

                _initialized = true;
                AppLog.Log($"[WebRtcEngineHost] ? Initialized: IsInitialized={IsInitialized}");
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcEngineHost] ERROR in InitAsync: {ex.Message}");
                AppLog.Log($"[WebRtcEngineHost] Stack trace: {ex.StackTrace}");
                throw;
            }
        }
        
        private async Task EnsureCoreWebView2WithTimeoutAsync(CoreWebView2Environment env)
        {
            // ���������� ��������������� ������� �� ������ EnsureCoreWebView2Async
            var initCompletedTcs = new TaskCompletionSource<bool>();
            Exception? initException = null;
            
            _webView.CoreWebView2InitializationCompleted += (sender, e) =>
            {
                AppLog.Log($"[WebRtcEngineHost] CoreWebView2InitializationCompleted: IsSuccess={e.IsSuccess}, Exception={e.InitializationException?.Message ?? "none"}");
                if (e.InitializationException != null)
                {
                    initException = e.InitializationException;
                    AppLog.Log($"[WebRtcEngineHost] InitializationException details: {e.InitializationException}");
                }
                initCompletedTcs.SetResult(e.IsSuccess);
            };
            
            // ��������� ������� ��� EnsureCoreWebView2Async (15 ������)
            var ensureInitStartTime = DateTime.Now;
            var ensureCoreTask = _webView.EnsureCoreWebView2Async(env);
            var ensureTimeoutTask = Task.Delay(15000); // 15 ������ �������
            
            var ensureCompletedTask = await Task.WhenAny(ensureCoreTask, ensureTimeoutTask);
            
            if (ensureCompletedTask == ensureTimeoutTask)
            {
                var elapsed = (DateTime.Now - ensureInitStartTime).TotalSeconds;
                AppLog.Log($"[WebRtcEngineHost] ERROR: EnsureCoreWebView2Async timeout after {elapsed:F1}s");
                AppLog.Log($"[WebRtcEngineHost] WebView2 state: IsLoaded={_webView.IsLoaded}, Parent={_webView.Parent?.GetType().Name ?? "null"}, Visibility={_webView.Visibility}");
                throw new TimeoutException($"WebView2 initialization timeout after {elapsed:F1} seconds");
            }
            
            // ���� ���������� �������������
            await ensureCoreTask;
            
            // ���� ������� InitializationCompleted (� ��������� 1 �������)
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
            AppLog.Log($"[WebRtcEngineHost] CoreWebView2 ensured (took {elapsedTime:F2}s)");
        }

        public async Task SendAsync(object command)
        {
            if (!_initialized)
                return;

            try
            {
                // ��������� ��� ��������� ��������, ��� � Dictionary
                string? cmdValue = null;
                string slotValue = "main";
                
                if (command is Dictionary<string, object> dictCmd)
                {
                    // ���� ������� ������ ��� Dictionary
                    if (dictCmd.TryGetValue("cmd", out var cmdObj))
                    {
                        cmdValue = cmdObj?.ToString();
                    }
                    if (dictCmd.TryGetValue("slot", out var slotObj))
                    {
                        slotValue = slotObj?.ToString() ?? "main";
                    }
                }
                else
                {
                    // ���� ������� ������ ��� ��������� ������, ���������� ���������
                    var cmdType = command.GetType();
                    var cmdProp = cmdType.GetProperty("cmd");
                    if (cmdProp != null)
                    {
                        cmdValue = cmdProp.GetValue(command)?.ToString();
                    }
                    var slotProp = cmdType.GetProperty("slot");
                    if (slotProp != null)
                    {
                        slotValue = slotProp.GetValue(command)?.ToString() ?? "main";
                    }
                }
                
                if (string.IsNullOrEmpty(cmdValue))
                {
                    AppLog.Log($"[WebRtcEngineHost] SendAsync: Command without 'cmd' property or key");
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
                        // �������� ������������ ��� ����������� (��� ������)
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
                            AppLog.Log($"[WebRtcEngineHost] SendAsync: initUA config: {string.Join(", ", safeParts)}");
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
                        AppLog.Log($"[WebRtcEngineHost] SendAsync: Sending makeCall command for number '{number}'");
                        // ��������: ������� ��� ������ ������ ����� ����� ������� ��� �������������� ����������
                        script = $"window.SoftphoneWebRtc.cleanupSessions(); window.SoftphoneWebRtc.makeCall('{number}');";
                        AppLog.Log($"[WebRtcEngineHost] SendAsync: Script prepared: {script}");
                        break;
                    case "cleanupSessions":
                        script = "window.SoftphoneWebRtc.cleanupSessions();";
                        break;
                    case "answer":
                        script = "window.SoftphoneWebRtc.answer();";
                        break;
                    case "hangup":
                        // ��������: �������� sessionId � hangup ��� ����������� ���������� ������
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
                            // ���������� sessionId ��� JavaScript
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
                            AppLog.Log("[WebRtcEngineHost] SendAsync: setHold command missing 'hold' property/key");
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
                        // ���������� ������������� JavaScript ���� (��� ��������� ��������)
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
                            AppLog.Log("[WebRtcEngineHost] SendAsync: executeScript command missing 'script' property");
                            return;
                        }
                        // script ��� ����������, ����� �������� ����
                        break;
                    case "setMute":
                        // ������� setMute �������� ��� Dictionary � ����� mute
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
                            // ������� ����� ��������� ��� ��������� ��������
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
                            AppLog.Log($"[WebRtcEngineHost] SendAsync: setMute script prepared: {script}");
                        }
                        else
                        {
                            AppLog.Log("[WebRtcEngineHost] SendAsync: setMute command missing 'mute' property/key");
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
                            AppLog.Log("[WebRtcEngineHost] SendAsync: sendDtmf command missing 'digit' property/key");
                            return;
                        }
                        // ���������� ����������� ������� ��� JavaScript
                        string escapedDigit = digit.Replace("'", "\\'").Replace("\"", "\\\"");
                        AppLog.Log($"[WebRtcEngineHost] SendAsync: Sending sendDtmf command for digit '{digit}'");
                        script = $"window.SoftphoneWebRtc.sendDtmf('{escapedDigit}');";
                        AppLog.Log($"[WebRtcEngineHost] SendAsync: Script prepared: {script}");
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
                    // Route to the correct iframe slot. For "main" the legacy
                    // window.SoftphoneWebRtc.X pattern resolves through the index.html
                    // property getter; for "secondary" we rewrite to the explicit router.
                    if (!string.Equals(slotValue, "main", StringComparison.OrdinalIgnoreCase))
                    {
                        var safeSlot = slotValue.Replace("'", "\\'");
                        script = script.Replace("window.SoftphoneWebRtc.", $"window.Soft('{safeSlot}').");
                    }

                    // Avoid log spam from watchdog heartbeat (ping/pong) and periodic stats polling.
                    // These run frequently and make the log window unusable.
                    bool isNoisyHeartbeat =
                        cmdValue == "ping" || cmdValue == "getStats" ||
                        script.Contains(".ping()", StringComparison.OrdinalIgnoreCase) ||
                        script.Contains(".getStats()", StringComparison.OrdinalIgnoreCase);

                    // Never log initUA script: it includes credentials in JSON.
                    bool isSensitive =
                        cmdValue == "initUA" ||
                        script.Contains(".initUA(", StringComparison.OrdinalIgnoreCase);

                    if (!isNoisyHeartbeat && !isSensitive)
                    {
                        AppLog.Log($"[WebRtcEngineHost] SendAsync: Executing script: {script}");
                    }
                    
                    try
                    {
                        // �����: ��������� ExecuteScriptAsync �� UI thread
                        // ��� ����������� ������������ ��� ������ �� watchdog ��� ������ �������
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
                                            AppLog.Log("[WebRtcEngineHost] SendAsync: CoreWebView2 is disposed");
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
                                        AppLog.Log($"[WebRtcEngineHost] SendAsync: WARNING - ExecuteScriptAsync timed out after {timeout.TotalSeconds:F1}s (cmd={cmdValue})");
                                    }
                                    return;
                                }
                                result = await execTask;
                            }
                            catch (ObjectDisposedException)
                            {
                                AppLog.Log("[WebRtcEngineHost] SendAsync: WebView2 Dispatcher is disposed");
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
                                            AppLog.Log($"[WebRtcEngineHost] SendAsync: WARNING - ExecuteScriptAsync timed out after {timeout.TotalSeconds:F1}s (cmd={cmdValue})");
                                        }
                                        return;
                                    }
                                    result = await execTask;
                                }
                            }
                            catch (ObjectDisposedException)
                            {
                                AppLog.Log("[WebRtcEngineHost] SendAsync: CoreWebView2 is disposed");
                                return;
                            }
                        }
                        
                        if (!isNoisyHeartbeat)
                        {
                            AppLog.Log($"[WebRtcEngineHost] SendAsync: Script executed, result: {result ?? "null"}");
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        AppLog.Log("[WebRtcEngineHost] SendAsync: WebView2 object is disposed, cannot execute script");
                        return;
                    }
                }
                else
                {
                    // �� �������� �������������� ��� �������, ������� �� �������� ��������� (��������, pong)
                    if (cmdValue != "pong")
                    {
                        AppLog.Log($"[WebRtcEngineHost] SendAsync: WARNING - script is null for command '{cmdValue}'");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcEngineHost] ERROR in SendAsync: {ex.Message}");
                AppLog.Log($"[WebRtcEngineHost] ERROR stack trace: {ex.StackTrace}");
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
                    // �����: �������� CoreWebView2 ������ ���� �� UI thread
                    // ����� ���������, ��� _webView �� disposed
                    if (_webView == null)
                        return false;
                    
                    // ��������� disposed ��������� ����� Dispatcher
                    if (!_webView.Dispatcher.CheckAccess())
                    {
                        try
                        {
                            var t = _webView.Dispatcher.InvokeAsync(() =>
                            {
                                try { return _webView.CoreWebView2 != null; }
                                catch (ObjectDisposedException)
                                {
                                    AppLog.Log("[WebRtcEngineHost] IsInitialized: WebView2 is disposed");
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
                            AppLog.Log("[WebRtcEngineHost] IsInitialized: WebView2 Dispatcher is disposed");
                            return false;
                        }
                    }
                    
                    try
                    {
                        return _webView.CoreWebView2 != null;
                    }
                    catch (ObjectDisposedException)
                    {
                        AppLog.Log("[WebRtcEngineHost] IsInitialized: CoreWebView2 is disposed");
                        return false;
                    }
                }
                catch (ObjectDisposedException)
                {
                    AppLog.Log("[WebRtcEngineHost] IsInitialized: WebView2 object is disposed");
                    return false;
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[WebRtcEngineHost] IsInitialized: Unexpected error: {ex.Message}");
                    return false;
                }
            }
        }
    }
}

#endif // WINDOWS
