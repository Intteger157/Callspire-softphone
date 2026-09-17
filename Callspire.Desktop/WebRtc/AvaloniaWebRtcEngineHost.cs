using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia.Controls;
using global::Avalonia.Platform;
using global::Avalonia.Threading;
using Softphone.AppHost;

namespace Softphone.WebRtc
{
    /// <summary>
    /// <see cref="IWebRtcEngineHost"/> over Avalonia <see cref="NativeWebView"/>:
    /// WKWebView on macOS, WebKitGTK/WPE on Linux, WebView2 on Windows (Avalonia shell).
    ///
    /// Loads the same <c>WebRtcClient/index.html</c> (two slot iframes + <c>phone.js</c>) that the WPF
    /// WebView2 host uses, served from a loopback <see cref="WebRtcAssetServer"/>. Commands are executed
    /// via <see cref="NativeWebView.InvokeScript"/>; events arrive through <see cref="NativeWebView.WebMessageReceived"/>
    /// (page side: <c>invokeCSharpAction(json)</c>, see <c>index.html</c>).
    /// </summary>
    public sealed class AvaloniaWebRtcEngineHost : IWebRtcEngineHost, ISlotReadyAwaitable, IDisposable
    {
        private static readonly TimeSpan DefaultScriptTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan HeartbeatScriptTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan StatsScriptTimeout = TimeSpan.FromSeconds(3);

        private readonly NativeWebView _webView;
        private readonly WebRtcAssetServer _server;
        private readonly HashSet<string> _readySlots = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _slotLock = new();
        private readonly TaskCompletionSource<bool> _navigated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _initialized;
        private bool _disposed;

        public event Action<string>? EngineEvent;

        public bool IsInitialized => _initialized && !_disposed;

        public string? PageUrl { get; private set; }

        public AvaloniaWebRtcEngineHost(NativeWebView webView, WebRtcAssetServer server)
        {
            _webView = webView ?? throw new ArgumentNullException(nameof(webView));
            _server = server ?? throw new ArgumentNullException(nameof(server));

            _webView.EnvironmentRequested += OnEnvironmentRequested;
            _webView.WebMessageReceived += OnWebMessageReceived;
            _webView.NavigationCompleted += OnNavigationCompleted;
        }

        /// <summary>
        /// Creates the host: starts the loopback asset server, navigates the web view to index.html and
        /// waits for the page (or a timeout). Returns null when NativeWebView is not supported on this machine.
        /// Must be called on the UI thread.
        /// </summary>
        public static async Task<AvaloniaWebRtcEngineHost?> CreateAsync(NativeWebView webView)
        {
            try
            {
                var info = webView.AdapterInfo;
                AppLog.Log($"[AvaloniaWebRtcHost] adapter: {info?.Type} / {info?.Engine} {info?.Version}");
                if (info is DetailedWebViewAdapterInfo d && !d.IsSupported)
                {
                    AppLog.Log($"[AvaloniaWebRtcHost] NativeWebView not supported: {d.UnavailableReason}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[AvaloniaWebRtcHost] AdapterInfo: {ex.Message}");
            }

            var dir = WebRtcAssetServer.LocateClientDirectory();
            if (dir == null)
            {
                AppLog.Log("[AvaloniaWebRtcHost] WebRtcClient directory not found — WebRTC disabled");
                return null;
            }

            var server = new WebRtcAssetServer(dir);
            try { server.Start(); }
            catch (Exception ex)
            {
                AppLog.Log($"[AvaloniaWebRtcHost] asset server failed: {ex.Message}");
                server.Dispose();
                return null;
            }

            var host = new AvaloniaWebRtcEngineHost(webView, server);
            await host.InitAsync(server.BaseUrl + "index.html").ConfigureAwait(true);
            return host;
        }

        private async Task InitAsync(string url)
        {
            PageUrl = url;
            AppLog.Log($"[AvaloniaWebRtcHost] navigating to {url}");
            try
            {
                _webView.Navigate(new Uri(url));
            }
            catch (Exception ex)
            {
                AppLog.Log($"[AvaloniaWebRtcHost] Navigate failed: {ex.Message}");
            }

            var done = await Task.WhenAny(_navigated.Task, Task.Delay(TimeSpan.FromSeconds(12))).ConfigureAwait(true);
            if (done != _navigated.Task)
                AppLog.Log("[AvaloniaWebRtcHost] WARNING: page load timeout (12 s) — continuing; slot readiness will be polled");
            _initialized = true;
            AppLog.Log("[AvaloniaWebRtcHost] initialized");
        }

        // ───────────────────────── events ─────────────────────────

        private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
        {
            try
            {
                var s = DesktopAppController.LoadSettingsWithMigrations();
                e.EnableDevTools = s.EnableWebRtcDebug;
            }
            catch { }

            switch (e)
            {
                case AppleWKWebViewEnvironmentRequestedEventArgs wk:
                    // Persistent store keeps mediaDevices permissions/deviceIds stable between runs.
                    wk.NonPersistentDataStore = false;
                    wk.ApplicationNameForUserAgent = "CallspireSoftphone";
                    wk.LimitsNavigationsToAppBoundDomains = false;
                    break;
                case WindowsWebView2EnvironmentRequestedEventArgs wv2:
                    wv2.UserDataFolder = System.IO.Path.Combine(AppDataHelper.GetAppDataPath(), "WebView2");
                    wv2.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
                    break;
                case GtkWebViewEnvironmentRequestedEventArgs gtk:
                    gtk.ApplicationNameForUserAgent = "CallspireSoftphone";
                    break;
            }
        }

        private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
        {
            AppLog.Log($"[AvaloniaWebRtcHost] NavigationCompleted: success={e.IsSuccess} url={e.Request}");
            _navigated.TrySetResult(e.IsSuccess);
        }

        private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
        {
            var json = e.Body;
            if (string.IsNullOrEmpty(json)) return;
            TryMarkSlotReady(json);
            try { EngineEvent?.Invoke(json); }
            catch (Exception ex) { AppLog.Log($"[AvaloniaWebRtcHost] EngineEvent handler: {ex.Message}"); }
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
                    if (_readySlots.Add(slot)) AppLog.Log($"[AvaloniaWebRtcHost] slot '{slot}' ready");
                }
            }
            catch { }
        }

        // ───────────────────────── ISlotReadyAwaitable ─────────────────────────

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
                    AppLog.Log($"[AvaloniaWebRtcHost] slot '{slot}' ready (polled)");
                    return true;
                }
                await Task.Delay(150).ConfigureAwait(false);
            }
            AppLog.Log($"[AvaloniaWebRtcHost] WARNING: slot '{slot}' not ready after {timeout.TotalSeconds:F0}s — proceeding");
            return false;
        }

        private static bool IsTrue(string? r) =>
            r != null && (r.Equals("true", StringComparison.OrdinalIgnoreCase) || r.Equals("\"true\"", StringComparison.OrdinalIgnoreCase) || r == "1");

        // ───────────────────────── IWebRtcEngineHost ─────────────────────────

        public async Task SendAsync(object command)
        {
            if (!IsInitialized) return;

            var built = WebRtcCommandScript.Build(command, AppLog.Log);
            if (built == null) return;

            if (!built.IsNoisy && !built.IsSensitive)
                AppLog.Log($"[AvaloniaWebRtcHost] exec: {built.Script}");
            else if (built.IsSensitive)
                AppLog.Log($"[AvaloniaWebRtcHost] exec: {built.Cmd} (slot={built.Slot})");

            var timeout = built.Cmd == "ping" ? HeartbeatScriptTimeout : built.Cmd == "getStats" ? StatsScriptTimeout : DefaultScriptTimeout;
            var result = await EvaluateAsync(built.Script, timeout).ConfigureAwait(false);

            if (!built.IsNoisy && result != null)
                AppLog.Log($"[AvaloniaWebRtcHost] result: {Truncate(result, 200)}");
        }

        /// <summary>Runs script on the UI thread with a timeout; never throws.</summary>
        private async Task<string?> EvaluateAsync(string script, TimeSpan timeout)
        {
            if (_disposed) return null;
            try
            {
                Task<string?> exec = Dispatcher.UIThread.CheckAccess()
                    ? _webView.InvokeScript(script)!
                    : Dispatcher.UIThread.InvokeAsync(() => _webView.InvokeScript(script)!);

                var done = await Task.WhenAny(exec, Task.Delay(timeout)).ConfigureAwait(false);
                if (done != exec)
                {
                    AppLog.Log($"[AvaloniaWebRtcHost] InvokeScript timed out after {timeout.TotalSeconds:F0}s");
                    return null;
                }
                return await exec.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[AvaloniaWebRtcHost] InvokeScript error: {ex.Message}");
                return null;
            }
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _initialized = false;
            try
            {
                _webView.EnvironmentRequested -= OnEnvironmentRequested;
                _webView.WebMessageReceived -= OnWebMessageReceived;
                _webView.NavigationCompleted -= OnNavigationCompleted;
            }
            catch { }
            try { _server.Dispose(); } catch { }
        }
    }
}
