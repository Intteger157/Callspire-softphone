using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Softphone.Audio;

namespace Softphone.AppHost.ViewModels
{
    /// <summary>One selectable audio device (PortAudio index, or -1 = system default).</summary>
    public sealed record AudioDeviceOption(int Index, string Name)
    {
        public override string ToString() => Name;
    }

    public sealed record ChoiceOption(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// UI-agnostic settings editor: loads <see cref="AppSettings"/>, exposes bindable fields for all
    /// panels (connections, audio, Kommo/gateway, appearance, advanced, about), validates and saves,
    /// then asks <see cref="DesktopAppController"/> to reconnect. Secrets are decrypted for editing
    /// and re-encrypted via <see cref="TokenEncryption"/> only when changed.
    /// </summary>
    public sealed class SettingsViewModel : ObservableObject
    {
        private readonly DesktopAppController _controller;
        private AppSettings _settings;

        public static IReadOnlyList<ChoiceOption> TransportOptions { get; } = new[]
        {
            new ChoiceOption("Sip", "SIP (UDP/TCP/TLS)"),
            new ChoiceOption("WebRtc", "WebRTC (WSS)"),
        };

        public static IReadOnlyList<ChoiceOption> ThemeOptions { get; } = new[]
        {
            new ChoiceOption("dark", "Dark"),
            new ChoiceOption("light", "Light"),
            new ChoiceOption("system", "System"),
        };

        public static IReadOnlyList<ChoiceOption> KommoAuthOptions { get; } = new[]
        {
            new ChoiceOption("manual", "Long-lived token"),
            new ChoiceOption("oauth", "OAuth (browser)"),
        };

        public SettingsViewModel(DesktopAppController controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _settings = DesktopAppController.LoadSettingsWithMigrations();
            Load(_settings);
        }

        // ───────────────────────── main line ─────────────────────────

        private string _mainName = "";
        private ChoiceOption _mainTransport = TransportOptions[0];
        private string _mainServer = "";
        private string _mainPort = "5060";
        private string _mainUsername = "";
        private string _mainPassword = "";
        private bool _mainPasswordChanged;
        private bool _mainUseTls;
        private bool _mainUseSrtp;
        private string _mainWsUri = "";
        private string _mainWebRtcUsername = "";
        private string _mainWebRtcPassword = "";
        private bool _mainWebRtcPasswordChanged;
        private string _mainTurnUri = "";
        private string _mainTurnUsername = "";
        private string _mainTurnPassword = "";
        private bool _mainTurnPasswordChanged;

        public string MainName { get => _mainName; set => Set(ref _mainName, value); }
        public ChoiceOption MainTransport { get => _mainTransport; set { if (Set(ref _mainTransport, value)) { OnPropertyChanged(nameof(MainIsSip)); OnPropertyChanged(nameof(MainIsWebRtc)); } } }
        public bool MainIsSip => _mainTransport.Key == "Sip";
        public bool MainIsWebRtc => _mainTransport.Key == "WebRtc";
        public string MainServer { get => _mainServer; set => Set(ref _mainServer, value); }
        public string MainPort { get => _mainPort; set => Set(ref _mainPort, value); }
        public string MainUsername { get => _mainUsername; set => Set(ref _mainUsername, value); }
        public string MainPassword { get => _mainPassword; set { if (Set(ref _mainPassword, value)) _mainPasswordChanged = true; } }
        public bool MainUseTls { get => _mainUseTls; set => Set(ref _mainUseTls, value); }
        public bool MainUseSrtp { get => _mainUseSrtp; set => Set(ref _mainUseSrtp, value); }
        public string MainWsUri { get => _mainWsUri; set => Set(ref _mainWsUri, value); }
        public string MainWebRtcUsername { get => _mainWebRtcUsername; set => Set(ref _mainWebRtcUsername, value); }
        public string MainWebRtcPassword { get => _mainWebRtcPassword; set { if (Set(ref _mainWebRtcPassword, value)) _mainWebRtcPasswordChanged = true; } }
        public string MainTurnUri { get => _mainTurnUri; set => Set(ref _mainTurnUri, value); }
        public string MainTurnUsername { get => _mainTurnUsername; set => Set(ref _mainTurnUsername, value); }
        public string MainTurnPassword { get => _mainTurnPassword; set { if (Set(ref _mainTurnPassword, value)) _mainTurnPasswordChanged = true; } }

        // ───────────────────────── secondary line ─────────────────────────

        private string _secName = "";
        private ChoiceOption _secTransport = TransportOptions[0];
        private string _secServer = "";
        private string _secRtpServer = "";
        private string _secUsername = "";
        private string _secPassword = "";
        private bool _secPasswordChanged;
        private bool _secUseTls;
        private bool _secUseSrtp;
        private string _secWsUri = "";
        private string _secWebRtcUsername = "";
        private string _secWebRtcPassword = "";
        private bool _secWebRtcPasswordChanged;
        private string _secTurnUri = "";
        private string _secTurnUsername = "";
        private string _secTurnPassword = "";
        private bool _secTurnPasswordChanged;

        public string SecondaryName { get => _secName; set => Set(ref _secName, value); }
        public ChoiceOption SecondaryTransport { get => _secTransport; set { if (Set(ref _secTransport, value)) { OnPropertyChanged(nameof(SecondaryIsSip)); OnPropertyChanged(nameof(SecondaryIsWebRtc)); } } }
        public bool SecondaryIsSip => _secTransport.Key == "Sip";
        public bool SecondaryIsWebRtc => _secTransport.Key == "WebRtc";
        public string SecondaryServer { get => _secServer; set => Set(ref _secServer, value); }
        public string SecondaryRtpServer { get => _secRtpServer; set => Set(ref _secRtpServer, value); }
        public string SecondaryUsername { get => _secUsername; set => Set(ref _secUsername, value); }
        public string SecondaryPassword { get => _secPassword; set { if (Set(ref _secPassword, value)) _secPasswordChanged = true; } }
        public bool SecondaryUseTls { get => _secUseTls; set => Set(ref _secUseTls, value); }
        public bool SecondaryUseSrtp { get => _secUseSrtp; set => Set(ref _secUseSrtp, value); }
        public string SecondaryWsUri { get => _secWsUri; set => Set(ref _secWsUri, value); }
        public string SecondaryWebRtcUsername { get => _secWebRtcUsername; set => Set(ref _secWebRtcUsername, value); }
        public string SecondaryWebRtcPassword { get => _secWebRtcPassword; set { if (Set(ref _secWebRtcPassword, value)) _secWebRtcPasswordChanged = true; } }
        public string SecondaryTurnUri { get => _secTurnUri; set => Set(ref _secTurnUri, value); }
        public string SecondaryTurnUsername { get => _secTurnUsername; set => Set(ref _secTurnUsername, value); }
        public string SecondaryTurnPassword { get => _secTurnPassword; set { if (Set(ref _secTurnPassword, value)) _secTurnPasswordChanged = true; } }

        // ───────────────────────── audio ─────────────────────────

        public ObservableCollection<AudioDeviceOption> Microphones { get; } = new();
        public ObservableCollection<AudioDeviceOption> Speakers { get; } = new();

        private AudioDeviceOption? _selectedMicrophone;
        private AudioDeviceOption? _selectedSpeaker;
        private bool _echoCancellation = true;
        private double _ringtoneVolume = 0.7;
        private bool _ringtoneWav = true;
        private string _audioBackendInfo = "";

        public AudioDeviceOption? SelectedMicrophone { get => _selectedMicrophone; set => Set(ref _selectedMicrophone, value); }
        public AudioDeviceOption? SelectedSpeaker { get => _selectedSpeaker; set => Set(ref _selectedSpeaker, value); }
        public bool EchoCancellation { get => _echoCancellation; set => Set(ref _echoCancellation, value); }
        public double RingtoneVolume { get => _ringtoneVolume; set { if (Set(ref _ringtoneVolume, Math.Clamp(value, 0, 1))) OnPropertyChanged(nameof(RingtoneVolumeText)); } }
        public string RingtoneVolumeText => $"{(int)Math.Round(_ringtoneVolume * 100)}%";
        public bool RingtoneWav { get => _ringtoneWav; set => Set(ref _ringtoneWav, value); }
        public string AudioBackendInfo { get => _audioBackendInfo; private set => Set(ref _audioBackendInfo, value); }

        // ───────────────────────── Kommo / gateway ─────────────────────────

        private bool _gatewayEnabled;
        private string _gatewayUrl = "";
        private string _gatewayToken = "";
        private bool _gatewayTokenChanged;
        private string _gatewayExtension = "";
        private bool _kommoEnabled;
        private string _kommoSubdomain = "";
        private ChoiceOption _kommoAuthMode = KommoAuthOptions[0];
        private string _kommoToken = "";
        private bool _kommoTokenChanged;
        private string _kommoClientId = "";
        private string _kommoClientSecret = "";
        private bool _kommoClientSecretChanged;
        private string _kommoRedirectUri = "";
        private bool _kommoLeadSelection;
        private bool _kommoRecordingUpload = true;

        public bool GatewayEnabled { get => _gatewayEnabled; set => Set(ref _gatewayEnabled, value); }
        public string GatewayUrl { get => _gatewayUrl; set => Set(ref _gatewayUrl, value); }
        public string GatewayToken { get => _gatewayToken; set { if (Set(ref _gatewayToken, value)) _gatewayTokenChanged = true; } }
        public string GatewayExtension { get => _gatewayExtension; set => Set(ref _gatewayExtension, value); }
        public bool KommoEnabled { get => _kommoEnabled; set => Set(ref _kommoEnabled, value); }
        public string KommoSubdomain { get => _kommoSubdomain; set => Set(ref _kommoSubdomain, value); }
        public ChoiceOption KommoAuthMode { get => _kommoAuthMode; set { if (Set(ref _kommoAuthMode, value)) { OnPropertyChanged(nameof(KommoIsManual)); OnPropertyChanged(nameof(KommoIsOAuth)); } } }
        public bool KommoIsManual => _kommoAuthMode.Key == "manual";
        public bool KommoIsOAuth => _kommoAuthMode.Key == "oauth";
        public string KommoToken { get => _kommoToken; set { if (Set(ref _kommoToken, value)) _kommoTokenChanged = true; } }
        public string KommoClientId { get => _kommoClientId; set => Set(ref _kommoClientId, value); }
        public string KommoClientSecret { get => _kommoClientSecret; set { if (Set(ref _kommoClientSecret, value)) _kommoClientSecretChanged = true; } }
        public string KommoRedirectUri { get => _kommoRedirectUri; set => Set(ref _kommoRedirectUri, value); }
        public bool KommoLeadSelection { get => _kommoLeadSelection; set => Set(ref _kommoLeadSelection, value); }
        public bool KommoRecordingUpload { get => _kommoRecordingUpload; set => Set(ref _kommoRecordingUpload, value); }

        // ───────────────────────── appearance / advanced / about ─────────────────────────

        private ChoiceOption _theme = ThemeOptions[2];
        private bool _callRecording;
        private bool _webRtcDebug = true;
        private string _recordingsFolder = "";
        private string _versionText = "";
        private string _updateStatus = "";
        private bool _updateAvailable;
        private string _updateUrl = "";

        public ChoiceOption Theme
        {
            get => _theme;
            set
            {
                if (Set(ref _theme, value))
                    ThemeService.SetConfiguredMode(ThemeService.ParseMode(value.Key)); // live preview
            }
        }
        public bool CallRecording { get => _callRecording; set => Set(ref _callRecording, value); }
        public bool WebRtcDebug { get => _webRtcDebug; set => Set(ref _webRtcDebug, value); }
        public string RecordingsFolder { get => _recordingsFolder; private set => Set(ref _recordingsFolder, value); }
        public string LogsFolder => AppDataHelper.GetLogsDirectory();
        public string SettingsFile => AppDataHelper.GetSettingsFilePath();
        public string VersionText { get => _versionText; private set => Set(ref _versionText, value); }
        public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
        public bool UpdateAvailable { get => _updateAvailable; private set => Set(ref _updateAvailable, value); }
        public string UpdateUrl { get => _updateUrl; private set => Set(ref _updateUrl, value); }

        // ───────────────────────── status ─────────────────────────

        private string _statusText = "";
        private bool _statusIsError;
        private bool _isBusy;

        public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
        public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }
        public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

        private void SetStatus(string text, bool error = false)
        {
            UiThread.BeginInvoke(() => { StatusText = text; StatusIsError = error; });
        }

        // ───────────────────────── load ─────────────────────────

        private void Load(AppSettings s)
        {
            MainName = s.MainConnectionName ?? "";
            MainTransport = Pick(TransportOptions, s.MainConnectionTransport, MainUsesWebRtcLegacy(s) ? "WebRtc" : "Sip");
            MainServer = s.SipServer ?? "";
            MainPort = ParsePort(s.SipServer, s.SipUseTls);
            MainUsername = s.SipUsername ?? "";
            MainPassword = SipPasswordProvider.GetPassword(s) ?? "";
            MainUseTls = s.SipUseTls;
            MainUseSrtp = s.SipUseSrtp;
            MainWsUri = s.WebRtcWsUri ?? "";
            MainWebRtcUsername = s.WebRtcUsername ?? "";
            MainWebRtcPassword = SipPasswordProvider.GetMainWebRtcPassword(s) ?? "";
            MainTurnUri = s.MainWebRtcTurnUri ?? s.WebRtcTurnUri ?? "";
            MainTurnUsername = s.MainWebRtcTurnUsername ?? s.WebRtcTurnUsername ?? "";
            MainTurnPassword = TurnPasswordProvider.GetMainTurnPassword(s) ?? "";

            SecondaryName = s.SecondaryConnectionName ?? "";
            SecondaryTransport = Pick(TransportOptions, s.SecondaryConnectionTransport, AppSettings.SecondaryLineUsesWebRtc(s) ? "WebRtc" : "Sip");
            SecondaryServer = s.SipServer2 ?? "";
            SecondaryRtpServer = s.RtpServer2 ?? "";
            SecondaryUsername = s.SipUsername2 ?? "";
            SecondaryPassword = Decrypt(s.SipPasswordEncrypted2);
            SecondaryUseTls = s.SipUseTls2;
            SecondaryUseSrtp = s.SipUseSrtp2;
            SecondaryWsUri = s.WebRtcWsUri2 ?? "";
            SecondaryWebRtcUsername = s.WebRtcUsername2 ?? "";
            SecondaryWebRtcPassword = SipPasswordProvider.GetSecondaryWebRtcPassword(s) ?? "";
            SecondaryTurnUri = s.SecondaryWebRtcTurnUri ?? "";
            SecondaryTurnUsername = s.SecondaryWebRtcTurnUsername ?? "";
            SecondaryTurnPassword = TurnPasswordProvider.GetSecondaryTurnPassword(s) ?? "";

            EchoCancellation = s.EnableEchoCancellation;
            RingtoneVolume = s.RingtoneVolume;
            RingtoneWav = !string.Equals(s.RingtoneSoundMode, "tone", StringComparison.OrdinalIgnoreCase);
            RefreshAudioDevices(s.MicrophoneDeviceNumber ?? -1, s.SpeakerDeviceNumber ?? -1);

            GatewayEnabled = s.EnableMikoPbxCdr;
            GatewayUrl = s.MikoPbxCdrServiceUrl ?? "";
            GatewayToken = Decrypt(s.MikoPbxCdrTokenEncrypted);
            GatewayExtension = s.MikoPbxExtension ?? "";
            KommoEnabled = s.EnableAmoCrmIntegration;
            KommoSubdomain = s.AmoCrmSubdomain ?? "";
            KommoAuthMode = Pick(KommoAuthOptions, s.AmoCrmAuthMode, "manual");
            KommoToken = Decrypt(s.AmoCrmAccessTokenEncrypted);
            KommoClientId = s.AmoCrmClientId ?? "";
            KommoClientSecret = Decrypt(s.AmoCrmClientSecretEncrypted);
            KommoRedirectUri = s.AmoCrmRedirectUri ?? "";
            KommoLeadSelection = s.EnableAmoCrmLeadSelection;
            KommoRecordingUpload = s.EnableAmoCrmRecordingUpload;

            _theme = Pick(ThemeOptions, s.ThemeMode, "system"); // no live-preview on load
            OnPropertyChanged(nameof(Theme));
            CallRecording = s.EnableCallRecording;
            WebRtcDebug = s.EnableWebRtcDebug;
            RecordingsFolder = SafeRecordingsFolder();
            VersionText = $"Version {UpdateService.GetCurrentVersion()}";

            // All *Changed flags were tripped by the setters above; reset so unchanged secrets are not re-encrypted.
            _mainPasswordChanged = _mainWebRtcPasswordChanged = _mainTurnPasswordChanged = false;
            _secPasswordChanged = _secWebRtcPasswordChanged = _secTurnPasswordChanged = false;
            _gatewayTokenChanged = _kommoTokenChanged = _kommoClientSecretChanged = false;
        }

        private static bool MainUsesWebRtcLegacy(AppSettings s) =>
            string.Equals(s.MainConnectionTransport, "WebRtc", StringComparison.OrdinalIgnoreCase) ||
            (s.UseWebRtcAudio && AppSettings.HasMeaningfulWebRtcWsUri(s.WebRtcWsUri));

        private static ChoiceOption Pick(IReadOnlyList<ChoiceOption> options, string? key, string fallback)
        {
            var k = string.IsNullOrWhiteSpace(key) ? fallback : key.Trim();
            return options.FirstOrDefault(o => string.Equals(o.Key, k, StringComparison.OrdinalIgnoreCase))
                ?? options.First(o => o.Key == fallback);
        }

        private static string ParsePort(string? server, bool tls)
        {
            if (!string.IsNullOrWhiteSpace(server))
            {
                int idx = server.LastIndexOf(':');
                if (idx > 0 && idx < server.Length - 1 && int.TryParse(server[(idx + 1)..], out int p) && p > 0 && p < 65536)
                    return p.ToString();
            }
            return tls ? "5061" : "5060";
        }

        private static string Decrypt(string? enc)
        {
            if (string.IsNullOrWhiteSpace(enc)) return "";
            try { return TokenEncryption.Decrypt(enc) ?? ""; } catch { return ""; }
        }

        private static string? EncryptOrKeep(bool changed, string plain, string? existing)
        {
            if (!changed) return existing;
            var t = plain?.Trim() ?? "";
            return t.Length == 0 ? null : TokenEncryption.Encrypt(t);
        }

        private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string SafeRecordingsFolder()
        {
            try { return AppDataHelper.GetRecordingsDirectory(); }
            catch { return Path.Combine(AppDataHelper.GetAppDataPath(), "recordings"); }
        }

        public void RefreshAudioDevices() => RefreshAudioDevices(SelectedMicrophone?.Index ?? -1, SelectedSpeaker?.Index ?? -1);

        private void RefreshAudioDevices(int micIndex, int spkIndex)
        {
            Microphones.Clear();
            Speakers.Clear();
            Microphones.Add(new AudioDeviceOption(-1, "System default"));
            Speakers.Add(new AudioDeviceOption(-1, "System default"));

            if (OperatingSystem.IsWindows())
            {
                AudioBackendInfo = "Backend: WASAPI (Windows). Devices are managed by the system.";
            }
            else if (PortAudioRuntime.IsAvailable)
            {
                var devices = PortAudioRuntime.EnumerateDevices();
                foreach (var d in devices)
                {
                    if (d.IsInput) Microphones.Add(new AudioDeviceOption(d.Index, d.IsDefaultInput ? $"{d.Name} (default)" : d.Name));
                    if (d.IsOutput) Speakers.Add(new AudioDeviceOption(d.Index, d.IsDefaultOutput ? $"{d.Name} (default)" : d.Name));
                }
                AudioBackendInfo = $"Backend: PortAudio ({(OperatingSystem.IsMacOS() ? "CoreAudio" : "ALSA/Pulse")}), {devices.Count} device(s). Software AEC.";
            }
            else
            {
                AudioBackendInfo = $"PortAudio is not available: {PortAudioRuntime.LoadError}";
            }

            SelectedMicrophone = Microphones.FirstOrDefault(m => m.Index == micIndex) ?? Microphones[0];
            SelectedSpeaker = Speakers.FirstOrDefault(m => m.Index == spkIndex) ?? Speakers[0];
        }

        // ───────────────────────── save ─────────────────────────

        /// <summary>Validate, persist to settings.json and reconnect. Returns null on success or an error message.</summary>
        public async Task<string?> SaveAsync()
        {
            var error = Validate();
            if (error != null) { SetStatus(error, error: true); return error; }

            var s = DesktopAppController.LoadSettingsWithMigrations(); // start from disk to keep unknown fields
            ApplyTo(s);

            try
            {
                _controller.SaveSettings(s);
                _settings = s;
                PlatformRingtone.Configure(s);
                SetStatus("Settings saved. Reconnecting…");
                IsBusy = true;
                await _controller.ReconnectFromSettingsAsync().ConfigureAwait(false);
                SetStatus("Settings saved.");
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SettingsVM] save failed: {ex}");
                SetStatus($"Save failed: {ex.Message}", error: true);
                return ex.Message;
            }
            finally
            {
                UiThread.BeginInvoke(() => IsBusy = false);
            }
        }

        private string? Validate()
        {
            if (MainIsSip)
            {
                if (!string.IsNullOrWhiteSpace(MainServer) || !string.IsNullOrWhiteSpace(MainUsername))
                {
                    if (string.IsNullOrWhiteSpace(MainServer)) return "Main connection: server host is required.";
                    if (string.IsNullOrWhiteSpace(MainUsername)) return "Main connection: username is required.";
                    if (!int.TryParse(MainPort, out int p) || p <= 0 || p > 65535) return "Main connection: port must be 1–65535.";
                }
            }
            else if (!string.IsNullOrWhiteSpace(MainWsUri) && !AppSettings.HasMeaningfulWebRtcWsUri(MainWsUri))
            {
                return "Main connection: WebSocket URI must start with wss:// or ws://.";
            }

            if (SecondaryIsWebRtc && !string.IsNullOrWhiteSpace(SecondaryWsUri) && !AppSettings.HasMeaningfulWebRtcWsUri(SecondaryWsUri))
                return "Secondary connection: WebSocket URI must start with wss:// or ws://.";

            if (GatewayEnabled)
            {
                if (string.IsNullOrWhiteSpace(GatewayUrl) || !Uri.TryCreate(GatewayUrl.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
                    return "PBX Gateway: URL must be an absolute http(s) URL.";
            }

            if (KommoEnabled && string.IsNullOrWhiteSpace(KommoSubdomain))
                return "Kommo: subdomain is required.";

            return null;
        }

        private void ApplyTo(AppSettings s)
        {
            // Main line
            s.MainConnectionName = Trimmed(MainName);
            s.MainConnectionTransport = MainTransport.Key;
            s.UseWebRtcAudio = MainIsWebRtc;
            s.SipServer = ComposeServer(MainServer, MainPort, MainUseTls);
            s.SipUsername = Trimmed(MainUsername);
            s.SipPasswordEncrypted = EncryptOrKeep(_mainPasswordChanged, MainPassword, s.SipPasswordEncrypted);
            s.SipPassword = null;
            s.SipUseTls = MainUseTls;
            s.SipUseSrtp = MainUseSrtp;
            s.WebRtcWsUri = Trimmed(MainWsUri);
            s.WebRtcUsername = Trimmed(MainWebRtcUsername);
            s.WebRtcPasswordEncrypted = EncryptOrKeep(_mainWebRtcPasswordChanged, MainWebRtcPassword, s.WebRtcPasswordEncrypted);
            s.MainWebRtcTurnUri = Trimmed(MainTurnUri);
            s.MainWebRtcTurnUsername = Trimmed(MainTurnUsername);
            s.MainWebRtcTurnPasswordEncrypted = EncryptOrKeep(_mainTurnPasswordChanged, MainTurnPassword, s.MainWebRtcTurnPasswordEncrypted);
            s.MainWebRtcTurnPassword = null;
            s.WebRtcTurnPassword = null;

            // Secondary line
            s.SecondaryConnectionName = Trimmed(SecondaryName);
            s.SecondaryConnectionTransport = SecondaryTransport.Key;
            s.SipServer2 = Trimmed(SecondaryServer);
            s.RtpServer2 = Trimmed(SecondaryRtpServer);
            s.SipUsername2 = Trimmed(SecondaryUsername);
            s.SipPasswordEncrypted2 = EncryptOrKeep(_secPasswordChanged, SecondaryPassword, s.SipPasswordEncrypted2);
            s.SipUseTls2 = SecondaryUseTls;
            s.SipUseSrtp2 = SecondaryUseSrtp;
            s.WebRtcWsUri2 = Trimmed(SecondaryWsUri);
            s.WebRtcUsername2 = Trimmed(SecondaryWebRtcUsername);
            s.WebRtcPasswordEncrypted2 = EncryptOrKeep(_secWebRtcPasswordChanged, SecondaryWebRtcPassword, s.WebRtcPasswordEncrypted2);
            s.SecondaryWebRtcTurnUri = Trimmed(SecondaryTurnUri);
            s.SecondaryWebRtcTurnUsername = Trimmed(SecondaryTurnUsername);
            s.SecondaryWebRtcTurnPasswordEncrypted = EncryptOrKeep(_secTurnPasswordChanged, SecondaryTurnPassword, s.SecondaryWebRtcTurnPasswordEncrypted);
            s.SecondaryWebRtcTurnPassword = null;

            // Audio
            s.EnableEchoCancellation = EchoCancellation;
            s.RingtoneVolume = RingtoneVolume;
            s.RingtoneSoundMode = RingtoneWav ? "wav" : "tone";
            if (!OperatingSystem.IsWindows())
            {
                s.MicrophoneDeviceNumber = SelectedMicrophone is { Index: >= 0 } m ? m.Index : null;
                s.SpeakerDeviceNumber = SelectedSpeaker is { Index: >= 0 } sp ? sp.Index : null;
            }

            // Gateway / Kommo
            s.EnableMikoPbxCdr = GatewayEnabled;
            s.MikoPbxCdrServiceUrl = Trimmed(GatewayUrl)?.TrimEnd('/');
            s.MikoPbxCdrTokenEncrypted = EncryptOrKeep(_gatewayTokenChanged, GatewayToken, s.MikoPbxCdrTokenEncrypted);
            s.MikoPbxExtension = Trimmed(GatewayExtension);
            s.EnableAmoCrmIntegration = KommoEnabled;
            s.AmoCrmSubdomain = Trimmed(KommoSubdomain);
            s.AmoCrmAuthMode = KommoAuthMode.Key;
            s.AmoCrmAccessTokenEncrypted = EncryptOrKeep(_kommoTokenChanged, KommoToken, s.AmoCrmAccessTokenEncrypted);
            s.AmoCrmClientId = Trimmed(KommoClientId);
            s.AmoCrmClientSecretEncrypted = EncryptOrKeep(_kommoClientSecretChanged, KommoClientSecret, s.AmoCrmClientSecretEncrypted);
            s.AmoCrmRedirectUri = Trimmed(KommoRedirectUri);
            s.EnableAmoCrmLeadSelection = KommoLeadSelection;
            s.EnableAmoCrmRecordingUpload = KommoRecordingUpload;

            // Appearance / advanced
            s.ThemeMode = Theme.Key;
            s.EnableCallRecording = CallRecording;
            s.EnableWebRtcDebug = WebRtcDebug;
        }

        /// <summary>Keeps "host" or "host:port" (port omitted when it is the default for the transport).</summary>
        private static string? ComposeServer(string host, string port, bool tls)
        {
            var h = Trimmed(host);
            if (h == null) return null;
            int idx = h.LastIndexOf(':');
            if (idx > 0 && int.TryParse(h[(idx + 1)..], out _)) h = h[..idx];
            int defaultPort = tls ? 5061 : 5060;
            return int.TryParse(port, out int p) && p > 0 && p != defaultPort ? $"{h}:{p}" : h;
        }

        // ───────────────────────── connection test ─────────────────────────

        /// <summary>Cheap reachability check (DNS + TCP/UDP) — does not register. Real registration happens on save.</summary>
        public async Task TestConnectionAsync(bool secondary)
        {
            string host; int port; bool tcp;
            if ((secondary ? SecondaryIsWebRtc : MainIsWebRtc))
            {
                var ws = secondary ? SecondaryWsUri : MainWsUri;
                if (!Uri.TryCreate(ws?.Trim(), UriKind.Absolute, out var u)) { SetStatus("WebSocket URI is invalid.", true); return; }
                host = u.Host; port = u.IsDefaultPort ? (u.Scheme == "wss" ? 443 : 80) : u.Port; tcp = true;
            }
            else
            {
                var server = secondary ? SecondaryServer : MainServer;
                var tls = secondary ? SecondaryUseTls : MainUseTls;
                if (string.IsNullOrWhiteSpace(server)) { SetStatus("Server host is empty.", true); return; }
                host = server.Trim();
                int idx = host.LastIndexOf(':');
                port = tls ? 5061 : 5060;
                if (idx > 0 && int.TryParse(host[(idx + 1)..], out int p)) { port = p; host = host[..idx]; }
                else if (!secondary && int.TryParse(MainPort, out int mp)) port = mp;
                tcp = tls;
            }

            IsBusy = true;
            SetStatus($"Testing {host}:{port}…");
            var sw = Stopwatch.StartNew();
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                if (addrs.Length == 0) { SetStatus($"DNS: no address for {host}", true); return; }

                if (tcp)
                {
                    using var client = new TcpClient(addrs[0].AddressFamily);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await client.ConnectAsync(addrs[0], port, cts.Token).ConfigureAwait(false);
                    SetStatus($"Reachable: {host} ({addrs[0]}) TCP {port} in {sw.ElapsedMilliseconds} ms.");
                }
                else
                {
                    // UDP has no handshake; send a SIP OPTIONS and wait briefly for any reply.
                    using var udp = new UdpClient(addrs[0].AddressFamily);
                    udp.Connect(addrs[0], port);
                    string callId = Guid.NewGuid().ToString("N");
                    string msg = $"OPTIONS sip:{host} SIP/2.0\r\nVia: SIP/2.0/UDP 0.0.0.0;branch=z9hG4bK{callId[..8]};rport\r\nMax-Forwards: 70\r\nFrom: <sip:probe@{host}>;tag={callId[..8]}\r\nTo: <sip:{host}>\r\nCall-ID: {callId}\r\nCSeq: 1 OPTIONS\r\nContent-Length: 0\r\n\r\n";
                    var bytes = System.Text.Encoding.ASCII.GetBytes(msg);
                    await udp.SendAsync(bytes, bytes.Length).ConfigureAwait(false);
                    var recv = udp.ReceiveAsync();
                    if (await Task.WhenAny(recv, Task.Delay(3000)).ConfigureAwait(false) == recv)
                        SetStatus($"Reachable: {host} ({addrs[0]}) UDP {port} answered in {sw.ElapsedMilliseconds} ms.");
                    else
                        SetStatus($"DNS OK ({addrs[0]}), no UDP reply from port {port} within 3 s (may still work — firewalls drop OPTIONS).");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Unreachable: {ex.Message}", true);
            }
            finally
            {
                UiThread.BeginInvoke(() => IsBusy = false);
            }
        }

        // ───────────────────────── updates ─────────────────────────

        public async Task CheckForUpdatesAsync()
        {
            IsBusy = true;
            UiThread.BeginInvoke(() => { UpdateStatus = "Checking…"; UpdateAvailable = false; });
            try
            {
                var info = await UpdateService.CheckForUpdateAsync(forceCheck: true).ConfigureAwait(false);
                UiThread.BeginInvoke(() =>
                {
                    if (info == null)
                    {
                        UpdateStatus = "You are up to date.";
                    }
                    else
                    {
                        UpdateStatus = $"Version {info.Version} is available." + (string.IsNullOrWhiteSpace(info.Notes) ? "" : $"\n{info.Notes}");
                        UpdateUrl = info.PlatformUrl ?? "";
                        UpdateAvailable = !string.IsNullOrWhiteSpace(UpdateUrl);
                    }
                });
            }
            catch (Exception ex)
            {
                UiThread.BeginInvoke(() => UpdateStatus = $"Update check failed: {ex.Message}");
            }
            finally
            {
                UiThread.BeginInvoke(() => IsBusy = false);
            }
        }

        public void OpenUpdateUrl()
        {
            if (string.IsNullOrWhiteSpace(UpdateUrl)) return;
            OpenExternal(UpdateUrl);
        }

        public void OpenLogsFolder() => OpenExternal(LogsFolder);
        public void OpenRecordingsFolder() => OpenExternal(RecordingsFolder);

        public static void OpenExternal(string target)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                else if (OperatingSystem.IsMacOS())
                    Process.Start("open", target);
                else
                    Process.Start("xdg-open", target);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SettingsVM] OpenExternal('{target}') failed: {ex.Message}");
            }
        }
    }
}
