using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Softphone.AppHost;
using Softphone.AppHost.ViewModels;
using Softphone.Service.Contracts;
using Softphone.Service.Ipc;

namespace Softphone.Service
{
    /// <summary>
    /// Sidecar-side settings window: keeps one <see cref="SettingsViewModel"/>, serves <c>getSettings</c>,
    /// applies <c>saveSettings</c> payloads to the view model (so validation, secret re-encryption and
    /// reconnect stay in shared code) and pushes <c>settingsChanged</c> while async operations
    /// (test connection, update check, OAuth) mutate status fields.
    /// </summary>
    public sealed class SettingsSession
    {
        private readonly IpcServer _ipc;
        private readonly ServiceDispatcher _dispatcher;
        private readonly DesktopAppController _controller;
        private readonly KommoOAuthFlow _oauth;
        private SettingsViewModel? _vm;
        private int _pushPending;

        public SettingsSession(IpcServer ipc, ServiceDispatcher dispatcher, DesktopAppController controller)
        {
            _ipc = ipc;
            _dispatcher = dispatcher;
            _controller = controller;
            _oauth = new KommoOAuthFlow(controller);
            _oauth.StatusChanged += SchedulePush;
        }

        private SettingsViewModel Vm
        {
            get
            {
                if (_vm == null)
                {
                    _vm = new SettingsViewModel(_controller);
                    _vm.PropertyChanged += OnVmChanged;
                }
                return _vm;
            }
        }

        /// <summary>Discard the editor so the next <c>getSettings</c> re-reads settings.json (after save / OAuth).</summary>
        private void Reload()
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = null;
        }

        private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => SchedulePush();

        private void SchedulePush()
        {
            if (Interlocked.Exchange(ref _pushPending, 1) == 1) return;
            _ = Task.Delay(30).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _pushPending, 0);
                _dispatcher.Post(PushChanged);
            }, TaskScheduler.Default);
        }

        private void PushChanged()
        {
            if (_vm == null) return;
            var task = _ipc.SendEventAsync("settingsChanged", Snapshot());
            task.ContinueWith(t => AppLog.Log($"[SettingsSession] push failed: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
        }

        private SettingsDto Snapshot() => SettingsDto.From(Vm, _oauth.Snapshot());

        public void RegisterHandlers()
        {
            _ipc.Register("getSettings", (_, _) => _dispatcher.InvokeAsync<object?>(() => { Reload(); return Snapshot(); }));
            _ipc.Register("refreshAudioDevices", (_, _) => _dispatcher.InvokeAsync<object?>(() => { Vm.RefreshAudioDevices(); return Snapshot(); }));

            _ipc.Register("saveSettings", (p, _) => _dispatcher.RunAsync<object?>(async () =>
            {
                var fields = p.HasValue ? IpcJson.Deserialize<SettingsFieldsDto>(p.Value) : null;
                if (fields == null) throw new IpcException("settings payload is required", "bad_request");
                Apply(Vm, fields);
                var error = await Vm.SaveAsync().ConfigureAwait(false);
                if (error == null) _dispatcher.Post(Reload);
                return new { error };
            }));

            _ipc.Register("testConnection", (p, _) => _dispatcher.RunAsync<object?>(async () =>
            {
                bool secondary = p.HasValue && p.Value.TryGetProperty("secondary", out var s) && s.ValueKind == JsonValueKind.True;
                if (p.HasValue && p.Value.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
                {
                    var fields = IpcJson.Deserialize<SettingsFieldsDto>(f);
                    if (fields != null) Apply(Vm, fields);
                }
                await Vm.TestConnectionAsync(secondary).ConfigureAwait(false);
                return new { statusText = Vm.StatusText, isError = Vm.StatusIsError };
            }));

            _ipc.Register("checkUpdates", (_, _) => _dispatcher.RunAsync<object?>(async () =>
            {
                await Vm.CheckForUpdatesAsync().ConfigureAwait(false);
                return new { status = Vm.UpdateStatus, available = Vm.UpdateAvailable, url = Vm.UpdateUrl };
            }));

            _ipc.Register("openUpdateUrl", _ => { Vm.OpenUpdateUrl(); return null; });
            _ipc.Register("openLogsFolder", _ => { Vm.OpenLogsFolder(); return null; });
            _ipc.Register("openRecordingsFolder", _ => { Vm.OpenRecordingsFolder(); return null; });
            _ipc.Register("openExternal", p =>
            {
                var target = p.HasValue && p.Value.TryGetProperty("target", out var t) ? t.GetString() : null;
                if (!string.IsNullOrWhiteSpace(target)) SettingsViewModel.OpenExternal(target);
                return null;
            });

            _ipc.Register("previewRingtone", (p, _) => _dispatcher.InvokeAsync<object?>(() =>
            {
                // Preview uses the values currently in the editor (volume / device) without saving.
                var s = DesktopAppController.LoadSettingsWithMigrations();
                if (p.HasValue && p.Value.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
                {
                    var fields = IpcJson.Deserialize<SettingsFieldsDto>(f);
                    if (fields != null)
                    {
                        s.RingtoneVolume = fields.RingtoneVolume;
                        s.RingtoneSoundMode = fields.RingtoneWav ? "wav" : "tone";
                        s.SpeakerDeviceNumber = fields.SelectedSpeaker >= 0 ? fields.SelectedSpeaker : null;
                    }
                }
                Softphone.Audio.PlatformRingtone.Configure(s);
                Softphone.Audio.PlatformRingtone.Start();
                return null;
            }));
            _ipc.Register("stopRingtone", _ => { Softphone.Audio.PlatformRingtone.Stop(); return null; });

            _ipc.Register("setTheme", p =>
            {
                var key = p.HasValue && p.Value.TryGetProperty("theme", out var t) ? t.GetString() : null;
                ThemePreferences.SetConfiguredMode(ThemePreferences.ParseMode(key));
                return null;
            });

            _ipc.Register("kommoAuthorize", async (p, ct) =>
            {
                string clientId = "", clientSecret = "", redirect = "";
                if (p.HasValue)
                {
                    if (p.Value.TryGetProperty("clientId", out var a)) clientId = a.GetString() ?? "";
                    if (p.Value.TryGetProperty("clientSecret", out var b)) clientSecret = b.GetString() ?? "";
                    if (p.Value.TryGetProperty("redirectUri", out var c)) redirect = c.GetString() ?? "";
                }
                var error = await _oauth.AuthorizeAsync(clientId, clientSecret, redirect).ConfigureAwait(false);
                if (error == null) _dispatcher.Post(() => { Reload(); PushChanged(); });
                return new { error, status = _oauth.Snapshot() };
            });
            _ipc.Register("kommoOAuthStatus", _ => _oauth.Snapshot());
        }

        /// <summary>Copies the editable fields from the Swift payload onto the shared view model.</summary>
        private static void Apply(SettingsViewModel vm, SettingsFieldsDto f)
        {
            vm.MainName = f.MainName; vm.MainTransport = Choice(SettingsViewModel.TransportOptions, f.MainTransport);
            vm.MainServer = f.MainServer; vm.MainPort = f.MainPort; vm.MainUsername = f.MainUsername; vm.MainPassword = f.MainPassword;
            vm.MainUseTls = f.MainUseTls; vm.MainUseSrtp = f.MainUseSrtp; vm.MainWsUri = f.MainWsUri;
            vm.MainWebRtcUsername = f.MainWebRtcUsername; vm.MainWebRtcPassword = f.MainWebRtcPassword;
            vm.MainTurnUri = f.MainTurnUri; vm.MainTurnUsername = f.MainTurnUsername; vm.MainTurnPassword = f.MainTurnPassword;

            vm.SecondaryName = f.SecondaryName; vm.SecondaryTransport = Choice(SettingsViewModel.TransportOptions, f.SecondaryTransport);
            vm.SecondaryServer = f.SecondaryServer; vm.SecondaryRtpServer = f.SecondaryRtpServer; vm.SecondaryUsername = f.SecondaryUsername;
            vm.SecondaryPassword = f.SecondaryPassword; vm.SecondaryUseTls = f.SecondaryUseTls; vm.SecondaryUseSrtp = f.SecondaryUseSrtp;
            vm.SecondaryWsUri = f.SecondaryWsUri; vm.SecondaryWebRtcUsername = f.SecondaryWebRtcUsername; vm.SecondaryWebRtcPassword = f.SecondaryWebRtcPassword;
            vm.SecondaryTurnUri = f.SecondaryTurnUri; vm.SecondaryTurnUsername = f.SecondaryTurnUsername; vm.SecondaryTurnPassword = f.SecondaryTurnPassword;

            vm.SelectedMicrophone = vm.Microphones.FirstOrDefault(m => m.Index == f.SelectedMicrophone) ?? vm.Microphones.FirstOrDefault();
            vm.SelectedSpeaker = vm.Speakers.FirstOrDefault(m => m.Index == f.SelectedSpeaker) ?? vm.Speakers.FirstOrDefault();
            vm.EchoCancellation = f.EchoCancellation; vm.RingtoneVolume = f.RingtoneVolume; vm.RingtoneWav = f.RingtoneWav;
            vm.SipCodec = Choice(SettingsViewModel.SipCodecOptions, f.SipCodec);
            vm.SipSampleRate = f.SipSampleRate > 0 ? f.SipSampleRate : vm.SipSampleRate;
            vm.SipOpusBitrate = f.SipOpusBitrate > 0 ? f.SipOpusBitrate : vm.SipOpusBitrate;

            vm.GatewayEnabled = f.GatewayEnabled; vm.GatewayUrl = f.GatewayUrl; vm.GatewayToken = f.GatewayToken; vm.GatewayExtension = f.GatewayExtension;
            vm.KommoEnabled = f.KommoEnabled; vm.KommoSubdomain = f.KommoSubdomain; vm.KommoAuthMode = Choice(SettingsViewModel.KommoAuthOptions, f.KommoAuthMode);
            vm.KommoToken = f.KommoToken; vm.KommoClientId = f.KommoClientId; vm.KommoClientSecret = f.KommoClientSecret; vm.KommoRedirectUri = f.KommoRedirectUri;
            vm.KommoLeadSelection = f.KommoLeadSelection; vm.KommoRecordingUpload = f.KommoRecordingUpload;
            vm.KommoSource = Choice(SettingsViewModel.KommoSourceOptions, f.KommoSource);

            vm.Theme = Choice(SettingsViewModel.ThemeOptions, f.Theme);
            vm.CallRecording = f.CallRecording; vm.WebRtcDebug = f.WebRtcDebug;
        }

        private static ChoiceOption Choice(IReadOnlyList<ChoiceOption> options, string? key)
            => options.FirstOrDefault(o => string.Equals(o.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? options[0];
    }
}
