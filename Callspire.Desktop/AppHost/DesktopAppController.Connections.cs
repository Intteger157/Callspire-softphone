using System;
using System.Threading;
using System.Threading.Tasks;
using Softphone.AppHost.ViewModels;

namespace Softphone.AppHost
{
    /// <summary>Connection lifecycle: SIP services per slot, shared WebRTC engine host, status projection.</summary>
    public sealed partial class DesktopAppController
    {
        private SipService? _sipMain;
        private SipService? _sipSecondary;
        private IWebRtcEngineHost? _webRtcHost;
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private bool _webRtcEventsHookedMain;
        private bool _webRtcEventsHookedSecondary;

        public SipService? MainSip => _sipMain;
        public SipService? SecondarySip => _sipSecondary;

        public bool IsMainOnline => ViewModel.Main.IsOnline;
        public bool IsSecondaryOnline => ViewModel.Secondary.IsOnline;

        private bool IsAnySipInCall() => _sipMain?.IsInCall == true || _sipSecondary?.IsInCall == true;

        private async Task ConnectAllAsync(AppSettings settings)
        {
            await ConnectMainAsync(settings).ConfigureAwait(false);
            await ConnectSecondaryAsync(settings).ConfigureAwait(false);
            UpdateAnyOnline();
        }

        // ───────────────────────── main slot ─────────────────────────

        private async Task ConnectMainAsync(AppSettings settings)
        {
            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (MainUsesWebRtc(settings))
                {
                    try { _sipMain?.Dispose(); } catch { }
                    _sipMain = null;

                    if (!HasMainConfig(settings))
                    {
                        SetStatus(ConnectionSlot.Main, ConnectionStatusFormatter.NotConnected, false);
                        return;
                    }
                    await InitializeWebRtcSlotAsync("main", settings).ConfigureAwait(false);
                    return;
                }

                // SIP main — stop WebRTC main slot if it was running.
                StopWebRtcSlot("main");

                if (!HasMainConfig(settings))
                {
                    try { _sipMain?.Dispose(); } catch { }
                    _sipMain = null;
                    SetStatus(ConnectionSlot.Main, ConnectionStatusFormatter.NotConnected, false);
                    return;
                }

                SipEndpointHelper.ParseStoredSipServer(settings.SipServer, out string host, out int port);
                _sipMain = CreateSipService(settings, host, port,
                    settings.SipUsername ?? "", SipPasswordProvider.GetPassword(settings) ?? "",
                    settings.AudioCodec, settings.AudioSampleRate, settings.AudioBitrate,
                    isSecondary: false, useTls: settings.SipUseTls, useSrtp: settings.SipUseSrtp);
                HookSip(_sipMain, ConnectionSlot.Main);

                SetStatus(ConnectionSlot.Main, ConnectionStatusFormatter.Connecting, false);
                await _sipMain.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[Controller] ConnectMainAsync error: {ex.Message}");
                SetStatus(ConnectionSlot.Main, "Connection failed", false, isError: true);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        // ───────────────────────── secondary slot ─────────────────────────

        private async Task ConnectSecondaryAsync(AppSettings settings)
        {
            await _connectLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!HasSecondaryConfig(settings))
                {
                    try { _sipSecondary?.Dispose(); } catch { }
                    _sipSecondary = null;
                    StopWebRtcSlot("secondary");
                    SetStatus(ConnectionSlot.Secondary, ConnectionStatusFormatter.NotConnected, false);
                    return;
                }

                if (AppSettings.SecondaryLineUsesWebRtc(settings))
                {
                    try { _sipSecondary?.Dispose(); } catch { }
                    _sipSecondary = null;
                    await InitializeWebRtcSlotAsync("secondary", settings).ConfigureAwait(false);
                    return;
                }

                StopWebRtcSlot("secondary");

                string? password2 = null;
                try { password2 = string.IsNullOrEmpty(settings.SipPasswordEncrypted2) ? null : TokenEncryption.Decrypt(settings.SipPasswordEncrypted2); }
                catch (Exception ex) { Log($"[Controller] secondary password decrypt failed: {ex.Message}"); }

                if (string.IsNullOrEmpty(password2))
                {
                    SetStatus(ConnectionSlot.Secondary, "Connection failed: Invalid credentials", false, isError: true);
                    return;
                }

                SipEndpointHelper.ParseStoredSipServer(settings.SipServer2, out string host2, out int port2);
                try { _sipSecondary?.Dispose(); } catch { }
                _sipSecondary = CreateSipService(settings, host2, port2,
                    settings.SipUsername2 ?? "", password2,
                    settings.AudioCodec, settings.AudioSampleRate, settings.AudioBitrate,
                    isSecondary: true, useTls: settings.SipUseTls2, useSrtp: settings.SipUseSrtp2);
                HookSip(_sipSecondary, ConnectionSlot.Secondary);

                SetStatus(ConnectionSlot.Secondary, ConnectionStatusFormatter.Connecting, false);
                await _sipSecondary.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"[Controller] ConnectSecondaryAsync error: {ex.Message}");
                SetStatus(ConnectionSlot.Secondary, "Connection failed", false, isError: true);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private static SipService CreateSipService(AppSettings settings, string host, int port,
            string username, string password, string? codec, int sampleRate, int bitrate,
            bool isSecondary, bool useTls, bool useSrtp)
        {
            var sip = new SipService(
                username,
                password,
                host,
                port,
                settings.MicrophoneDeviceNumber,
                settings.SpeakerDeviceNumber,
                string.IsNullOrWhiteSpace(codec) ? "PCMU" : codec,
                sampleRate > 0 ? sampleRate : 16000,
                bitrate > 0 ? bitrate : 64000,
                isSecondaryConnection: isSecondary,
                useTls: useTls,
                useSrtp: useSrtp,
                audioDeviceFactory: Softphone.Audio.AudioDeviceFactory.Default);
            sip.EnableAec = settings.EnableEchoCancellation;
            return sip;
        }

        private void HookSip(SipService sip, ConnectionSlot slot)
        {
            sip.OnStatusChanged += status => HandleSipStatus(sip, slot, status);
            sip.OnIncomingCall += caller => HandleIncomingSipCall(sip, slot, caller);
            sip.OnCallEnded += () => OnSipCallEnded(slot);
        }

        private void HandleSipStatus(SipService sip, ConnectionSlot slot, string status)
        {
            try
            {
                if (ConnectionStatusFormatter.IsRegistrationSuccess(status))
                {
                    SetStatus(slot, ConnectionStatusFormatter.ConnectedSip, true);
                    return;
                }
                if (ConnectionStatusFormatter.IsRegistrationRemoved(status))
                {
                    SetStatus(slot, "Disconnected from server", false);
                    return;
                }
                if (ConnectionStatusFormatter.IsRegistrationFailure(status))
                {
                    SetStatus(slot, ConnectionStatusFormatter.Format(status, false) ?? "Connection failed", false, isError: true);
                    return;
                }

                var text = ConnectionStatusFormatter.Format(status, isWebRtcMode: false);
                if (text != null)
                    SetStatus(slot, text, sip.IsRegistered);
            }
            catch (Exception ex)
            {
                Log($"[Controller] HandleSipStatus error: {ex.Message}");
            }
        }

        // ───────────────────────── WebRTC slots ─────────────────────────

        private async Task InitializeWebRtcSlotAsync(string slot, AppSettings settings)
        {
            bool isSecondary = slot == "secondary";
            var uiSlot = isSecondary ? ConnectionSlot.Secondary : ConnectionSlot.Main;

            var config = WebRtcConnectionConfig.FromSettings(settings, isSecondary);
            if (config == null)
            {
                SetStatus(uiSlot, ConnectionStatusFormatter.NotConnected, false);
                return;
            }

            SetStatus(uiSlot, "Initializing WebRTC...", false);

            var host = await EnsureWebRtcHostAsync().ConfigureAwait(false);
            if (host == null)
            {
                SetStatus(uiSlot, "WebRTC engine unavailable on this platform", false, isError: true);
                return;
            }

            var service = WebRtcService.GetSlot(slot);
            service.DebugEnabled = settings.EnableWebRtcDebug;
            service.AttachEngine(host);

            if (isSecondary ? !_webRtcEventsHookedSecondary : !_webRtcEventsHookedMain)
            {
                service.Event += OnWebRtcEvent;
                if (isSecondary) _webRtcEventsHookedSecondary = true; else _webRtcEventsHookedMain = true;
            }
            service.StartWatchdog();

            SetStatus(uiSlot, "Initializing JsSIP...", false);

            if (host is ISlotReadyAwaitable awaitable)
            {
                try { await awaitable.WaitForSlotReadyAsync(slot, TimeSpan.FromSeconds(8)).ConfigureAwait(false); } catch { }
            }

            await service.ReinitializeUAAsync(config.WsUri, config.SipUri, config.Username, config.Password).ConfigureAwait(false);
            Log($"[Controller] WebRTC slot '{slot}' initUA sent ({config.SipUri})");
        }

        private async Task<IWebRtcEngineHost?> EnsureWebRtcHostAsync()
        {
            if (_webRtcHost != null && _webRtcHost.IsInitialized) return _webRtcHost;
            var factory = WebRtcHostFactory;
            if (factory == null)
            {
                Log("[Controller] WebRtcHostFactory not set — WebRTC disabled");
                return null;
            }

            var tcs = new TaskCompletionSource<IWebRtcEngineHost?>(TaskCreationOptions.RunContinuationsAsynchronously);
            UiThread.BeginInvoke(async () =>
            {
                try { tcs.TrySetResult(await factory().ConfigureAwait(true)); }
                catch (Exception ex) { Log($"[Controller] WebRTC host creation failed: {ex.Message}"); tcs.TrySetResult(null); }
            });

            var host = await tcs.Task.ConfigureAwait(false);
            if (host != null) _webRtcHost = host;
            return host;
        }

        private void StopWebRtcSlot(string slot)
        {
            try
            {
                var service = WebRtcService.GetSlot(slot);
                service.StopWatchdog();
                service.StopAutoReconnect();
                service.DetachEngine(resetState: true);
            }
            catch (Exception ex)
            {
                Log($"[Controller] StopWebRtcSlot({slot}) error: {ex.Message}");
            }
        }

        private void OnWebRtcEvent(WebRtcEventDto dto)
        {
            try
            {
                bool secondary = string.Equals(dto.Slot, "secondary", StringComparison.OrdinalIgnoreCase);
                var uiSlot = secondary ? ConnectionSlot.Secondary : ConnectionSlot.Main;

                switch (dto.Type)
                {
                    case "ua_started":
                    case "ws_connected":
                    case "ua_connected":
                        SetStatus(uiSlot, "Connecting...", false);
                        break;
                    case "registered":
                    case "ua_registered":
                        SetStatus(uiSlot, ConnectionStatusFormatter.ConnectedWebRtc, true);
                        break;
                    case "reg_failed":
                    case "ua_registration_failed":
                        SetStatus(uiSlot, "Registration failed", false, isError: true);
                        break;
                    case "unregistered":
                    case "ua_unregistered":
                    case "ws_disconnected":
                        SetStatus(uiSlot, "Disconnected", false);
                        break;
                    case "incoming":
                        HandleIncomingWebRtcCall(dto);
                        break;
                    case "call_ended":
                    case "call_failed":
                        OnWebRtcCallEnded(dto);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"[Controller] OnWebRtcEvent error: {ex.Message}");
            }
        }

        // ───────────────────────── status projection ─────────────────────────

        private void SetStatus(ConnectionSlot slot, string text, bool online, bool isError = false)
        {
            UiThread.BeginInvoke(() =>
            {
                var vm = slot == ConnectionSlot.Main ? ViewModel.Main : ViewModel.Secondary;
                bool wasOnline = vm.IsOnline;
                vm.Update(text, online, isError);
                ViewModel.AnyOnline = ViewModel.Main.IsOnline || ViewModel.Secondary.IsOnline;
                if (online && !wasOnline) FlushPendingClickToCall();
            });
        }

        private void UpdateAnyOnline() =>
            UiThread.BeginInvoke(() => ViewModel.AnyOnline = ViewModel.Main.IsOnline || ViewModel.Secondary.IsOnline);

        /// <summary>Snapshot used by the connection picker.</summary>
        public ConnectionSelectionRequest BuildConnectionSelectionRequest() => new()
        {
            HasMain = ViewModel.Main.IsConfigured,
            IsMainWebRtc = ViewModel.Main.IsWebRtc,
            MainStatus = ViewModel.Main.Text,
            MainName = ViewModel.Main.DisplayName,
            HasSecondary = ViewModel.Secondary.IsConfigured,
            IsSecondaryWebRtc = ViewModel.Secondary.IsWebRtc,
            SecondaryStatus = ViewModel.Secondary.Text,
            SecondaryName = ViewModel.Secondary.DisplayName,
            MainCallerIds = ViewModel.CallerIds,
            SelectedCallerId = ViewModel.SelectedCallerId?.Number,
        };
    }

    /// <summary>
    /// Optional capability of a WebRTC engine host: wait until the per-slot iframe finished loading
    /// before sending <c>initUA</c>. Implemented by <c>AvaloniaWebRtcEngineHost</c>.
    /// </summary>
    public interface ISlotReadyAwaitable
    {
        Task<bool> WaitForSlotReadyAsync(string slot, TimeSpan timeout);
    }

    /// <summary>Resolved WebRTC UA parameters for one slot (port of WPF <c>GetWebRtcConfigForConnection</c>).</summary>
    public sealed class WebRtcConnectionConfig
    {
        public string WsUri { get; init; } = "";
        public string SipUri { get; init; } = "";
        public string Username { get; init; } = "";
        public string Password { get; init; } = "";
        public string? TurnServer { get; init; }
        public string? TurnUsername { get; init; }
        public string? TurnPassword { get; init; }
        public bool EnableDebug { get; init; }

        public static WebRtcConnectionConfig? FromSettings(AppSettings settings, bool secondary)
        {
            string? ws = secondary ? settings.WebRtcWsUri2 : settings.WebRtcWsUri;
            string? username = secondary
                ? AppSettings.EffectiveSecondaryWebRtcUsername(settings)
                : AppSettings.EffectiveMainWebRtcUsername(settings);
            string? password = secondary
                ? SipPasswordProvider.GetSecondaryWebRtcPassword(settings)
                : SipPasswordProvider.GetMainWebRtcPassword(settings);
            string? sipServer = secondary ? settings.SipServer2 : settings.SipServer;

            if (!AppSettings.HasMeaningfulWebRtcWsUri(ws) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return null;

            string pbx = "";
            try { pbx = new Uri(ws!).Host; } catch { }
            if (string.IsNullOrEmpty(pbx)) pbx = SipEndpointHelper.GetHostOnly(sipServer);
            if (string.IsNullOrEmpty(pbx)) return null;

            string aor = username.EndsWith("-WS", StringComparison.OrdinalIgnoreCase) ? username : $"{username}-WS";

            return new WebRtcConnectionConfig
            {
                WsUri = ws!,
                SipUri = $"sip:{aor}@{pbx}",
                Username = username,
                Password = password,
                TurnServer = secondary ? settings.SecondaryWebRtcTurnUri : (settings.MainWebRtcTurnUri ?? settings.WebRtcTurnUri),
                TurnUsername = secondary ? settings.SecondaryWebRtcTurnUsername : (settings.MainWebRtcTurnUsername ?? settings.WebRtcTurnUsername),
                TurnPassword = secondary ? TurnPasswordProvider.GetSecondaryTurnPassword(settings) : TurnPasswordProvider.GetMainTurnPassword(settings),
                EnableDebug = settings.EnableWebRtcDebug,
            };
        }
    }
}
