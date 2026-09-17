using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Softphone.AppHost;
using Softphone.Service.Ipc;
using Softphone.WebRtc;

namespace Softphone.Service
{
    /// <summary>
    /// <see cref="IWebRtcEngineHost"/> whose WKWebView lives in the Swift process.
    ///
    /// The sidecar serves <c>WebRtcClient/</c> via <see cref="WebRtcAssetServer"/> (loopback HTTP = secure
    /// context for getUserMedia). Creating the host asks Swift (<c>webRtcCreateHost</c>) to load
    /// <c>index.html</c> in a hidden WKWebView; commands are translated by <see cref="WebRtcCommandScript"/>
    /// and evaluated through <c>webRtcInvokeScript</c>; JS events posted by the page arrive as
    /// <c>webRtcEngineEvent</c> requests and are re-raised through <see cref="EngineEvent"/>.
    /// Port of <c>AvaloniaWebRtcEngineHost</c> with the WebView replaced by an IPC round-trip.
    /// </summary>
    public sealed class WebRtcIpcEngineHost : IWebRtcEngineHost, ISlotReadyAwaitable, IDisposable
    {
        private static readonly TimeSpan DefaultScriptTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan HeartbeatScriptTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan StatsScriptTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan PageLoadTimeout = TimeSpan.FromSeconds(15);

        private readonly IpcServer _ipc;
        private readonly WebRtcAssetServer _server;
        private readonly HashSet<string> _readySlots = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _slotLock = new();
        private volatile bool _initialized;
        private bool _disposed;

        public event Action<string>? EngineEvent;
        public bool IsInitialized => _initialized && !_disposed;
        public string? PageUrl { get; private set; }

        private WebRtcIpcEngineHost(IpcServer ipc, WebRtcAssetServer server)
        {
            _ipc = ipc;
            _server = server;
        }

        /// <summary>Starts the asset server and asks Swift to load the engine page. Returns null when Swift cannot host it.</summary>
        public static async Task<WebRtcIpcEngineHost?> CreateAsync(IpcServer ipc)
        {
            var dir = WebRtcAssetServer.LocateClientDirectory();
            if (dir == null)
            {
                AppLog.Log("[WebRtcIpcHost] WebRtcClient directory not found — WebRTC disabled");
                return null;
            }

            var server = new WebRtcAssetServer(dir);
            try { server.Start(); }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcIpcHost] asset server failed: {ex.Message}");
                server.Dispose();
                return null;
            }

            var host = new WebRtcIpcEngineHost(ipc, server);
            host.PageUrl = server.BaseUrl + "index.html";
            AppLog.Log($"[WebRtcIpcHost] requesting Swift WKWebView for {host.PageUrl}");

            bool enableDevTools = false;
            try { enableDevTools = DesktopAppController.LoadSettingsWithMigrations().EnableWebRtcDebug; } catch { }

            try
            {
                var result = await ipc.RequestAsync("webRtcCreateHost", new { url = host.PageUrl, enableDevTools }, PageLoadTimeout).ConfigureAwait(false);
                bool ok = result?.ValueKind == JsonValueKind.Object && result.Value.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                if (!ok) AppLog.Log("[WebRtcIpcHost] WARNING: Swift reported page load failure/timeout — continuing; slot readiness will be polled");
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcIpcHost] webRtcCreateHost failed: {ex.Message}");
                server.Dispose();
                return null;
            }

            host._initialized = true;
            AppLog.Log("[WebRtcIpcHost] initialized");
            return host;
        }

        /// <summary>Swift → C#: raw JSON event posted by phone.js via <c>window.webkit.messageHandlers</c>.</summary>
        public void OnEngineEvent(string json)
        {
            if (string.IsNullOrEmpty(json) || _disposed) return;
            TryMarkSlotReady(json);
            try { EngineEvent?.Invoke(json); }
            catch (Exception ex) { AppLog.Log($"[WebRtcIpcHost] EngineEvent handler: {ex.Message}"); }
        }

        private void TryMarkSlotReady(string json)
        {
            try
            {
                if (!json.Contains("slot_ready", StringComparison.Ordinal)) return;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "slot_ready") return;
                if (!root.TryGetProperty("slot", out var s)) return;
                var slot = s.GetString();
                if (string.IsNullOrWhiteSpace(slot)) return;
                lock (_slotLock)
                {
                    if (_readySlots.Add(slot)) AppLog.Log($"[WebRtcIpcHost] slot '{slot}' ready");
                }
            }
            catch { }
        }

        public async Task<bool> WaitForSlotReadyAsync(string slot, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(slot)) return true;
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !_disposed)
            {
                lock (_slotLock) { if (_readySlots.Contains(slot)) return true; }

                var probe = $"(() => {{ try {{ return !!(window._slotReady && window._slotReady['{WebRtcCommandScript.Js(slot)}']); }} catch (e) {{ return false; }} }})()";
                var r = await EvaluateAsync(probe, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (IsTrue(r))
                {
                    lock (_slotLock) _readySlots.Add(slot);
                    AppLog.Log($"[WebRtcIpcHost] slot '{slot}' ready (polled)");
                    return true;
                }
                await Task.Delay(150).ConfigureAwait(false);
            }
            AppLog.Log($"[WebRtcIpcHost] WARNING: slot '{slot}' not ready after {timeout.TotalSeconds:F0}s — proceeding");
            return false;
        }

        private static bool IsTrue(string? r) =>
            r != null && (r.Equals("true", StringComparison.OrdinalIgnoreCase) || r.Equals("\"true\"", StringComparison.OrdinalIgnoreCase) || r == "1");

        public async Task SendAsync(object command)
        {
            if (!IsInitialized) return;

            var built = WebRtcCommandScript.Build(command, AppLog.Log);
            if (built == null) return;

            if (!built.IsNoisy && !built.IsSensitive)
                AppLog.Log($"[WebRtcIpcHost] exec: {built.Script}");
            else if (built.IsSensitive)
                AppLog.Log($"[WebRtcIpcHost] exec: {built.Cmd} (slot={built.Slot})");

            var timeout = built.Cmd == "ping" ? HeartbeatScriptTimeout : built.Cmd == "getStats" ? StatsScriptTimeout : DefaultScriptTimeout;
            var result = await EvaluateAsync(built.Script, timeout).ConfigureAwait(false);

            if (!built.IsNoisy && result != null)
                AppLog.Log($"[WebRtcIpcHost] result: {Truncate(result, 200)}");
        }

        /// <summary>Evaluates JS in the Swift WKWebView; never throws.</summary>
        private async Task<string?> EvaluateAsync(string script, TimeSpan timeout)
        {
            if (_disposed) return null;
            try
            {
                var el = await _ipc.RequestAsync("webRtcInvokeScript", new { script }, timeout).ConfigureAwait(false);
                if (el == null) return null;
                return el.Value.ValueKind switch
                {
                    JsonValueKind.String => el.Value.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => el.Value.GetRawText(),
                };
            }
            catch (IpcException ex) when (ex.Code == "timeout")
            {
                AppLog.Log($"[WebRtcIpcHost] InvokeScript timed out after {timeout.TotalSeconds:F0}s");
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcIpcHost] InvokeScript error: {ex.Message}");
                return null;
            }
        }

        /// <summary>Swift recreated its WKWebView (wake from sleep / crash) — forget slot readiness so it is re-probed.</summary>
        public void OnHostReset()
        {
            lock (_slotLock) _readySlots.Clear();
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _initialized = false;
            _ = _ipc.SendEventAsync("webRtcDestroyHost");
            try { _server.Dispose(); } catch { }
        }
    }
}
