using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Softphone.AppHost;
using Softphone.Platform;
using Softphone.Service.Contracts;
using Softphone.Service.Ipc;

namespace Softphone.Service
{
    /// <summary>
    /// Wires the controller, the remote shell and every IPC method. One instance per process.
    ///
    /// Swift → C# methods (see README "IPC protocol"):
    ///   lifecycle   ping · getState · shutdown
    ///   dialer      setPhoneNumber · placeCall · selectCallerId
    ///   connections reconnectSlot · reconnectFromSettings
    ///   calls       answer · reject · hangup · toggleMute · toggleHold · toggleKeypad · sendDtmf · callViewClosed · getActiveCall
    ///   history     refreshHistory · clearHistory · setHistoryDrillDown · clearHistoryFilter · getCallDetails · prepareRecording · retryKommo
    ///   statistics  setStatisticsFilter · refreshStatistics · exportStatisticsCsv
    ///   settings    getSettings · saveSettings · testConnection · refreshAudioDevices · checkUpdates · openUpdateUrl ·
    ///               openLogsFolder · openRecordingsFolder · openExternal · previewRingtone · stopRingtone · setTheme ·
    ///               kommoAuthorize · kommoOAuthStatus
    ///   logs        getLogSnapshot · setLogStreaming · clearLog · log
    ///   protocol    handleProtocolUrl
    ///   system      systemWillSleep · systemDidWake · networkAvailable
    ///   webrtc      webRtcEngineEvent · webRtcHostReset
    /// C# → Swift events: stateSnapshot · showCallWindow · callStateChanged · callClosed · bringCallWindowToFront ·
    ///   showSettings · settingsChanged · showMessage · showUpdateAvailable · bringToForeground · logLines · webRtcDestroyHost
    /// C# → Swift requests: showConnectionSelection · showLeadSelection · showKommoLeadPicker · webRtcCreateHost · webRtcInvokeScript
    /// </summary>
    public sealed class ServiceHost : IAsyncDisposable
    {
        private readonly IpcServer _ipc;
        private readonly ServiceDispatcher _dispatcher;
        private readonly DesktopAppController _controller;
        private readonly CallSessionHost _calls;
        private readonly RemoteDesktopShell _shell;
        private readonly StateProjector _state;
        private readonly SettingsSession _settings;
        private readonly CallDetailsProvider _details;
        private readonly LogRelay _log;
        private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebRtcIpcEngineHost? _webRtcHost;
        private bool _started;

        public Task ShutdownRequested => _shutdown.Task;

        public ServiceHost(string socketPath, ServiceDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _ipc = new IpcServer(socketPath);
            _log = new LogRelay(_ipc);
            _controller = DesktopAppController.CreateFromSettings();
            _calls = new CallSessionHost(_ipc, dispatcher, _controller);
            _shell = new RemoteDesktopShell(_ipc, _calls);
            _state = new StateProjector(_ipc, dispatcher, _controller, _calls);
            _settings = new SettingsSession(_ipc, dispatcher, _controller);
            _details = new CallDetailsProvider(_ipc, _controller);

            _controller.AttachShell(_shell);
            _controller.WebRtcHostFactory = CreateWebRtcHostAsync;
            KommoLeadSelectionUi.Handler = _shell.ShowKommoLeadPickerAsync;
            ProtocolActivation.AttachHandler(url => _controller.HandleProtocolUrl(url));

            RegisterHandlers();
            _ipc.ClientConnected += OnClientConnected;
        }

        public void Start()
        {
            _ipc.Start();
        }

        /// <summary>Telephony starts when the first UI connects (the WKWebView host lives there), or immediately in --headless mode.</summary>
        private void OnClientConnected()
        {
            _dispatcher.Post(async () =>
            {
                // Re-sync the new client: full state, current call (if any), and let it know we are up.
                _ = _ipc.SendEventAsync("serviceReady", new { version = UpdateService.GetCurrentVersion(), pid = Environment.ProcessId });
                _state.Schedule();
                var call = _calls.Describe();
                if (call != null) _ = _ipc.SendEventAsync("showCallWindow", call);
                await EnsureStartedAsync().ConfigureAwait(false);
            });
        }

        public async Task EnsureStartedAsync()
        {
            if (_started) return;
            _started = true;
            try { await _controller.StartAsync().ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Log($"[ServiceHost] controller start failed: {ex}"); }
        }

        private async Task<IWebRtcEngineHost?> CreateWebRtcHostAsync()
        {
            if (_webRtcHost is { IsInitialized: true }) return _webRtcHost;
            _webRtcHost?.Dispose();
            _webRtcHost = await WebRtcIpcEngineHost.CreateAsync(_ipc).ConfigureAwait(false);
            return _webRtcHost;
        }

        private void RegisterHandlers()
        {
            // ── lifecycle ──
            _ipc.Register("ping", _ => new { pong = true, version = UpdateService.GetCurrentVersion(), pid = Environment.ProcessId });
            _ipc.Register("getState", (_, _) => _dispatcher.InvokeAsync<object?>(() => _state.Build()));
            _ipc.Register("shutdown", _ => { _shutdown.TrySetResult(); return null; });

            // ── dialer ──
            _ipc.Register("setPhoneNumber", (p, _) => _dispatcher.InvokeAsync<object?>(() =>
            {
                _controller.ViewModel.PhoneNumber = Str(p, "value") ?? "";
                return null;
            }));
            _ipc.Register("placeCall", async (p, _) =>
            {
                var args = p.HasValue ? IpcJson.Deserialize<PlaceCallParams>(p.Value) : null;
                var number = args?.Number?.Trim();
                if (string.IsNullOrWhiteSpace(number)) throw new IpcException("number is required", "bad_request");
                await _controller.PlaceCallAsync(number, ParseSlot(args!.Slot), args.LeadId, args.BrowserLeadId).ConfigureAwait(false);
                return null;
            });
            _ipc.Register("selectCallerId", p => { _controller.SelectCallerId(Str(p, "number")); return null; });

            // ── connections ──
            _ipc.Register("reconnectSlot", async (p, _) =>
            {
                await _controller.ReconnectSlotAsync(ParseSlot(Str(p, "slot")) ?? ConnectionSlot.Main).ConfigureAwait(false);
                return null;
            });
            _ipc.Register("reconnectFromSettings", async (_, _) => { await _controller.ReconnectFromSettingsAsync().ConfigureAwait(false); return null; });

            // ── history ──
            _ipc.Register("refreshHistory", _ => { _controller.RefreshHistory(); return null; });
            _ipc.Register("clearHistory", _ => { _controller.ClearHistory(); return null; });
            _ipc.Register("clearHistoryFilter", _ => { _controller.ClearHistoryFilter(); return null; });
            _ipc.Register("setHistoryDrillDown", p =>
            {
                var drill = (Str(p, "drill") ?? "all").ToLowerInvariant() switch
                {
                    "answered" => CallStatisticsDrillDown.Answered,
                    "unanswered" => CallStatisticsDrillDown.Unanswered,
                    "missed" => CallStatisticsDrillDown.Missed,
                    _ => CallStatisticsDrillDown.All,
                };
                _controller.SetHistoryDrillDown(drill, Str(p, "phoneNumber"));
                return null;
            });

            // ── statistics ──
            _ipc.Register("refreshStatistics", _ => { _controller.RefreshStatistics(); return null; });
            _ipc.Register("setStatisticsFilter", (p, _) => _dispatcher.InvokeAsync<object?>(() =>
            {
                var st = _controller.ViewModel.Statistics;
                if (p.HasValue)
                {
                    if (p.Value.TryGetProperty("periodIndex", out var pi) && pi.ValueKind == JsonValueKind.Number) st.PeriodIndex = pi.GetInt32();
                    if (p.Value.TryGetProperty("connectionIndex", out var ci) && ci.ValueKind == JsonValueKind.Number) st.ConnectionIndex = ci.GetInt32();
                    if (p.Value.TryGetProperty("directionIndex", out var di) && di.ValueKind == JsonValueKind.Number) st.DirectionIndex = di.GetInt32();
                    if (p.Value.TryGetProperty("customFrom", out var cf)) st.CustomFrom = cf.ValueKind == JsonValueKind.String ? cf.GetDateTime() : null;
                    if (p.Value.TryGetProperty("customTo", out var ct)) st.CustomTo = ct.ValueKind == JsonValueKind.String ? ct.GetDateTime() : null;
                }
                _controller.RefreshStatistics();
                return null;
            }));
            _ipc.Register("exportStatisticsCsv", p => new { path = _controller.ExportStatisticsCsv(Str(p, "targetPath")) });

            // ── protocol / system ──
            _ipc.Register("handleProtocolUrl", p =>
            {
                var url = Str(p, "url");
                if (!string.IsNullOrWhiteSpace(url)) ProtocolActivation.Enqueue(url);
                return null;
            });
            _ipc.Register("systemWillSleep", _ => { _controller.HandleSystemSleep(); return null; });
            _ipc.Register("systemDidWake", p => { FireAndForget(_controller.HandleSystemResumeAsync("system_wake")); return null; });
            _ipc.Register("networkAvailable", p => { FireAndForget(_controller.HandleSystemResumeAsync("network_available")); return null; });

            // ── WebRTC bridge (Swift WKWebView → engine host) ──
            _ipc.Register("webRtcEngineEvent", p =>
            {
                var json = p.HasValue && p.Value.TryGetProperty("json", out var j)
                    ? (j.ValueKind == JsonValueKind.String ? j.GetString() : j.GetRawText())
                    : p?.GetRawText();
                if (json != null) _webRtcHost?.OnEngineEvent(json);
                return null;
            });
            _ipc.Register("webRtcHostReset", p =>
            {
                // Swift recreated its WKWebView (e.g. web content process crashed): rebuild via resume path.
                _webRtcHost?.OnHostReset();
                FireAndForget(_controller.HandleSystemResumeAsync("webview_process_failed"));
                return null;
            });

            _calls.RegisterHandlers();
            _settings.RegisterHandlers();
            _details.RegisterHandlers();
            _log.RegisterHandlers();
        }

        private static void FireAndForget(Task task)
            => task.ContinueWith(t => AppLog.Log($"[ServiceHost] background task failed: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);

        private static string? Str(JsonElement? p, string name)
            => p.HasValue && p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static ConnectionSlot? ParseSlot(string? s) => s?.ToLowerInvariant() switch
        {
            "main" => ConnectionSlot.Main,
            "secondary" => ConnectionSlot.Secondary,
            _ => null,
        };

        public async ValueTask DisposeAsync()
        {
            try { _controller.ShutdownTelephonyBestEffort(); } catch { }
            try { _webRtcHost?.Dispose(); } catch { }
            _state.Dispose();
            try { _controller.Dispose(); } catch { }
            await _ipc.DisposeAsync().ConfigureAwait(false);
        }
    }
}
