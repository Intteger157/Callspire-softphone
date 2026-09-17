using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Softphone.AppHost.ViewModels;

namespace Softphone.AppHost
{
    /// <summary>
    /// UI-agnostic orchestration of the desktop softphone: connection lifecycle (SIP / WebRTC,
    /// main + secondary), incoming-call routing, call history and statistics, PBX Gateway
    /// (CallerIDs, TURN sync, CDR) and Kommo post-call jobs.
    ///
    /// The Avalonia shell (and later WPF) implements <see cref="IDesktopShell"/> and binds to
    /// <see cref="ViewModel"/>. All ViewModel mutations are marshalled through <see cref="UiThread"/>.
    /// Logic here is ported from the WPF <c>MainWindow.xaml.cs</c> in a WPF-free form.
    /// </summary>
    public sealed partial class DesktopAppController : IDisposable
    {
        private readonly object _gate = new();
        private IDesktopShell? _shell;
        private bool _started;
        private bool _disposed;
        private AppSettings _settings;

        public MainViewModel ViewModel { get; } = new();
        public CallHistoryService History { get; }

        /// <summary>
        /// Creates the platform WebRTC engine host (hidden WebView). Set by the shell before
        /// <see cref="StartAsync"/>; when null, WebRTC slots stay offline with an explanatory status.
        /// The host must be created on the UI thread — the factory is invoked via <see cref="UiThread"/>.
        /// </summary>
        public Func<Task<IWebRtcEngineHost?>>? WebRtcHostFactory { get; set; }

        /// <summary>Raised (any thread) whenever history changed and views should refresh.</summary>
        public event Action? HistoryChanged;

        public AppSettings Settings => _settings;

        private DesktopAppController(AppSettings settings)
        {
            _settings = settings;
            History = new CallHistoryService();
        }

        public static DesktopAppController CreateFromSettings()
        {
            var settings = LoadSettingsWithMigrations();
            return new DesktopAppController(settings);
        }

        /// <summary>Attach the UI shell. Must be called before <see cref="StartAsync"/>.</summary>
        public void AttachShell(IDesktopShell shell)
        {
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        }

        // ───────────────────────── lifecycle ─────────────────────────

        public async Task StartAsync()
        {
            lock (_gate)
            {
                if (_started || _disposed) return;
                _started = true;
            }

            Log("[Controller] StartAsync");
            try { AppDataHelper.CleanupOldRecordings(); } catch { }
            try { History.CleanupOldHistory(7); } catch { }

            ApplyConnectionNames();
            RefreshHistory();
            RefreshStatistics();
            UpdateAccountInfo();

            InitializeGateway(_settings);
            _ = InitializeLocalKommoAsync(_settings);
            StartKommoWorker();
            _ = CheckForUpdatesOnStartupAsync();

            await ConnectAllAsync(_settings).ConfigureAwait(false);
        }

        /// <summary>Throttled (24h) update check; shows the shell's update notification when a newer build exists.</summary>
        private async Task CheckForUpdatesOnStartupAsync()
        {
            try
            {
                await Task.Delay(3000).ConfigureAwait(false);
                var info = await UpdateService.CheckForUpdateAsync().ConfigureAwait(false);
                if (info == null || _disposed) return;
                Log($"[Controller] Update available: {UpdateService.GetCurrentVersion()} -> {info.Version}");
                _shell?.ShowUpdateAvailable(info, UpdateService.GetCurrentVersion());
            }
            catch (Exception ex)
            {
                Log($"[Controller] Update check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Re-read settings from disk and reconnect both lines. Called by the Settings window after save.
        /// Defers while a call is active (mirrors WPF behaviour).
        /// </summary>
        public async Task ReconnectFromSettingsAsync()
        {
            var settings = LoadSettingsWithMigrations();
            _settings = settings;

            if (_shell?.HasActiveCallWindow == true || IsAnySipInCall())
            {
                Log("[Controller] ReconnectFromSettingsAsync: active call detected, deferring until it ends");
                UiThread.BeginInvoke(() => ViewModel.Main.Update("Settings saved. Applied after current call ends.", ViewModel.Main.IsOnline));
                _reconnectPendingAfterCall = true;
                return;
            }

            ApplyConnectionNames();
            UpdateAccountInfo();
            InitializeGateway(settings);
            _ = InitializeLocalKommoAsync(settings);
            await ConnectAllAsync(settings).ConfigureAwait(false);
            RefreshStatistics();
        }

        private bool _reconnectPendingAfterCall;

        /// <summary>Manual reconnect of one line (status-dot click).</summary>
        public Task ReconnectSlotAsync(ConnectionSlot slot)
        {
            _settings = LoadSettingsWithMigrations();
            return slot == ConnectionSlot.Main
                ? ConnectMainAsync(_settings)
                : ConnectSecondaryAsync(_settings);
        }

        /// <summary>Best-effort unregister/stop, used on app shutdown (never throws).</summary>
        public void ShutdownTelephonyBestEffort()
        {
            Log("[Controller] ShutdownTelephonyBestEffort");
            try { StopWebRtcSlot("main"); } catch { }
            try { StopWebRtcSlot("secondary"); } catch { }
            try { _sipMain?.Dispose(); } catch { }
            try { _sipSecondary?.Dispose(); } catch { }
            _sipMain = null;
            _sipSecondary = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            ShutdownTelephonyBestEffort();
            StopKommoWorker();
            DisposeLocalKommo();
            DisposeGateway();
            try { (_webRtcHost as IDisposable)?.Dispose(); } catch { }
            _webRtcHost = null;
        }

        // ───────────────────────── settings ─────────────────────────

        public static AppSettings LoadSettingsWithMigrations()
        {
            var settings = AppDataHelper.LoadSettingsOrNew();
            try
            {
                string path = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(path))
                {
                    SipPasswordProvider.MigratePlaintextToEncryptedIfNeeded(path, settings);
                    TurnPasswordProvider.MigratePlaintextToEncryptedIfNeeded(path, settings);
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[Controller] settings migration skipped: {ex.Message}");
            }
            return settings;
        }

        public void SaveSettings(AppSettings settings)
        {
            AppDataHelper.SaveSettings(settings);
            _settings = settings;
        }

        private void ApplyConnectionNames()
        {
            var s = _settings;
            string mainName = string.IsNullOrWhiteSpace(s.MainConnectionName) ? "Main" : s.MainConnectionName.Trim();
            string secName = string.IsNullOrWhiteSpace(s.SecondaryConnectionName) ? "Secondary" : s.SecondaryConnectionName.Trim();
            bool mainWebRtc = MainUsesWebRtc(s);
            bool secWebRtc = AppSettings.SecondaryLineUsesWebRtc(s);
            bool secConfigured = HasSecondaryConfig(s);

            UiThread.BeginInvoke(() =>
            {
                ViewModel.Main.DisplayName = mainName;
                ViewModel.Main.Label = string.IsNullOrWhiteSpace(s.MainConnectionName) ? (mainWebRtc ? "PBX:" : "SIP:") : mainName + ":";
                ViewModel.Main.IsWebRtc = mainWebRtc;
                ViewModel.Main.IsConfigured = HasMainConfig(s);

                ViewModel.Secondary.DisplayName = secName;
                ViewModel.Secondary.Label = string.IsNullOrWhiteSpace(s.SecondaryConnectionName) ? (secWebRtc ? "PBX 2:" : "SIP 2:") : secName + ":";
                ViewModel.Secondary.IsWebRtc = secWebRtc;
                ViewModel.Secondary.IsConfigured = secConfigured;

                ViewModel.ShowSplitCallButtons = secConfigured && HasMainConfig(s);
                ViewModel.SplitPrimaryLabel = mainName;
                ViewModel.SplitSecondaryLabel = secName;
                ViewModel.Statistics.SecondaryEnabled = secConfigured;
            });
        }

        private void UpdateAccountInfo()
        {
            var s = _settings;
            string user = MainUsesWebRtc(s)
                ? (AppSettings.EffectiveMainWebRtcUsername(s) ?? "")
                : (s.SipUsername ?? "");
            string host = MainUsesWebRtc(s) ? TryGetHost(s.WebRtcWsUri) : SipEndpointHelper.GetHostOnly(s.SipServer);
            string text = string.IsNullOrWhiteSpace(user) ? "Not configured" : (string.IsNullOrWhiteSpace(host) ? user : $"{user} @ {host}");
            UiThread.BeginInvoke(() =>
            {
                ViewModel.AccountText = text;
                ViewModel.AccountToolTip = text;
            });
        }

        private static string TryGetHost(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return "";
            try { return new Uri(uri).Host; } catch { return ""; }
        }

        internal static bool MainUsesWebRtc(AppSettings s)
        {
            if (string.IsNullOrWhiteSpace(s.MainConnectionTransport) ||
                string.Equals(s.MainConnectionTransport, "Sip", StringComparison.OrdinalIgnoreCase))
                return s.UseWebRtcAudio;
            return string.Equals(s.MainConnectionTransport, "WebRtc", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool HasMainConfig(AppSettings s)
        {
            if (MainUsesWebRtc(s))
                return AppSettings.HasMeaningfulWebRtcWsUri(s.WebRtcWsUri)
                    && !string.IsNullOrEmpty(AppSettings.EffectiveMainWebRtcUsername(s))
                    && SipPasswordProvider.GetMainWebRtcPassword(s) != null;
            return !string.IsNullOrEmpty(s.SipServer) && !string.IsNullOrEmpty(s.SipUsername)
                && !string.IsNullOrEmpty(SipPasswordProvider.GetPassword(s));
        }

        internal static bool HasSecondaryConfig(AppSettings s)
        {
            if (AppSettings.SecondaryLineUsesWebRtc(s)) return true;
            return !string.IsNullOrEmpty(s.SipServer2) && !string.IsNullOrEmpty(s.SipUsername2)
                && !string.IsNullOrEmpty(s.SipPasswordEncrypted2);
        }

        // ───────────────────────── misc ─────────────────────────

        private static void Log(string message) => AppLog.Log(message);

        private void ShowMessage(string title, string text)
        {
            try { _shell?.ShowMessage(title, text); } catch { }
        }
    }
}
