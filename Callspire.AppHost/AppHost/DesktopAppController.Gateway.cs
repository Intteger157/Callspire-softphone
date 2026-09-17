using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Softphone.AppHost
{
    /// <summary>PBX Gateway: CallerID list, TURN sync, SIP auth-failure banner, CDR lookups, PBX originate.</summary>
    public sealed partial class DesktopAppController
    {
        private MikoPbxCdrService? _gateway;
        private string? _gatewayExtension;
        private List<CallerIdItem> _callerIdItems = new();
        private Timer? _callerIdRetryTimer;
        private Timer? _sipAuthFailureTimer;
        private readonly SemaphoreSlim _callerIdLoadLock = new(1, 1);

        // Originate state (port of WPF OriginateCoordinator, kept inside the controller).
        private string? _originatePendingDestination;
        private string? _originatePendingCallerId;
        private string? _originateAcceptedSessionId;
        private Action<string>? _originateSessionAttached;

        public MikoPbxCdrService? Gateway => _gateway;
        public bool IsGatewayConfigured => _gateway != null;
        public IReadOnlyList<CallerIdItem> CallerIds => _callerIdItems;

        private void InitializeGateway(AppSettings settings)
        {
            StopCallerIdRetryTimer();
            StopSipAuthFailurePolling();
            try { _gateway?.Dispose(); } catch { }
            _gateway = null;
            _gatewayExtension = null;

            if (!settings.EnableMikoPbxCdr
                || string.IsNullOrEmpty(settings.MikoPbxCdrServiceUrl)
                || string.IsNullOrEmpty(settings.MikoPbxCdrTokenEncrypted)
                || string.IsNullOrEmpty(settings.MikoPbxExtension))
            {
                UiThread.BeginInvoke(() => ViewModel.ReplaceCallerIds(Array.Empty<CallerIdItem>(), null));
                UpdateAmoCrmIndicator(settings);
                return;
            }

            try
            {
                string token = TokenEncryption.Decrypt(settings.MikoPbxCdrTokenEncrypted);
                if (string.IsNullOrEmpty(token))
                {
                    Log("[PBX Gateway] Failed to decrypt token");
                    return;
                }

                _gateway = new MikoPbxCdrService(settings.MikoPbxCdrServiceUrl, token, settings.MikoPbxExtension);
                _gatewayExtension = settings.MikoPbxExtension;
                Log($"[PBX Gateway] Service initialized (url={settings.MikoPbxCdrServiceUrl}, ext={settings.MikoPbxExtension})");

                _ = LoadCallerIdsAsync(settings);
                StartSipAuthFailurePolling();
                _ = SyncTurnFromGatewayAsync(settings);
                UpdateAmoCrmIndicator(settings);
            }
            catch (Exception ex)
            {
                Log($"[PBX Gateway] Init error: {ex.Message}");
            }
        }

        private void DisposeGateway()
        {
            StopCallerIdRetryTimer();
            StopSipAuthFailurePolling();
            try { _gateway?.Dispose(); } catch { }
            _gateway = null;
        }

        // ───────────────────────── CallerIDs ─────────────────────────

        private async Task LoadCallerIdsAsync(AppSettings settings)
        {
            var gateway = _gateway;
            if (gateway == null) return;
            await _callerIdLoadLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var (items, ok) = await gateway.GetMyCallerIdItemsAsync().ConfigureAwait(false);
                if (ok)
                {
                    StopCallerIdRetryTimer();
                    _callerIdItems = items;
                    var numbers = items.Select(i => i.Number).ToList();
                    settings.CachedOutboundCallerIds = numbers.Count > 0 ? numbers : null;
                    try { AppDataHelper.SaveSettings(settings); } catch { }
                    UiThread.BeginInvoke(() => ViewModel.ReplaceCallerIds(items, settings.SelectedOutboundCallerId));
                    Log($"[CallerID] {items.Count} CallerID(s) loaded from gateway");
                    return;
                }

                Log("[CallerID] Gateway did not return CallerID list; using cache and retrying");
                if (settings.CachedOutboundCallerIds?.Count > 0)
                {
                    _callerIdItems = settings.CachedOutboundCallerIds.Select(n => new CallerIdItem(n, "")).ToList();
                    UiThread.BeginInvoke(() => ViewModel.ReplaceCallerIds(_callerIdItems, settings.SelectedOutboundCallerId));
                }
                StartCallerIdRetryTimer();
            }
            catch (Exception ex)
            {
                Log($"[CallerID] LoadCallerIdsAsync error: {ex.Message}");
            }
            finally
            {
                _callerIdLoadLock.Release();
            }
        }

        private void StartCallerIdRetryTimer()
        {
            if (_callerIdRetryTimer != null) return;
            _callerIdRetryTimer = new Timer(_ =>
            {
                if (_gateway == null || !_settings.EnableMikoPbxCdr) return;
                _ = LoadCallerIdsAsync(_settings);
            }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        private void StopCallerIdRetryTimer()
        {
            _callerIdRetryTimer?.Dispose();
            _callerIdRetryTimer = null;
        }

        /// <summary>Persist the CallerID chosen in the dropdown.</summary>
        public void SelectCallerId(string? number)
        {
            var item = _callerIdItems.FirstOrDefault(i => i.Number == number);
            UiThread.BeginInvoke(() => ViewModel.SelectedCallerId = item ?? ViewModel.SelectedCallerId);
            if (item == null || _settings.SelectedOutboundCallerId == item.Number) return;
            _settings.SelectedOutboundCallerId = item.Number;
            try { AppDataHelper.SaveSettings(_settings); } catch { }
        }

        internal bool IsOwnOutboundCallerId(string? number)
        {
            if (string.IsNullOrWhiteSpace(number) || _callerIdItems.Count == 0) return false;
            string digits = PhoneNumberHelper.DigitsOnly(number);
            if (digits.Length < 6) return false;
            return _callerIdItems.Any(i =>
            {
                string d = PhoneNumberHelper.DigitsOnly(i.Number);
                return d.Length >= 6 && (d.EndsWith(digits, StringComparison.Ordinal) || digits.EndsWith(d, StringComparison.Ordinal));
            });
        }

        // ───────────────────────── SIP auth failures ─────────────────────────

        private void StartSipAuthFailurePolling()
        {
            if (_sipAuthFailureTimer != null) return;
            _sipAuthFailureTimer = new Timer(async _ =>
            {
                var gw = _gateway;
                if (gw == null) return;
                try
                {
                    var stats = await gw.GetSipAuthFailuresAsync().ConfigureAwait(false);
                    string? text = null;
                    if (stats.Supported && !stats.TransientServerError && stats.FailuresForExtension > 0)
                        text = $"⚠ {stats.FailuresForExtension} failed SIP login attempt(s) for extension {_gatewayExtension} from another device. Check old softphones.";
                    UiThread.BeginInvoke(() => ViewModel.SipAuthFailureText = text);
                }
                catch { }
            }, null, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(5));
        }

        private void StopSipAuthFailurePolling()
        {
            _sipAuthFailureTimer?.Dispose();
            _sipAuthFailureTimer = null;
            UiThread.BeginInvoke(() => ViewModel.SipAuthFailureText = null);
        }

        // ───────────────────────── TURN sync ─────────────────────────

        private async Task SyncTurnFromGatewayAsync(AppSettings settings)
        {
            try
            {
                bool changed = await GatewayTurnSync.TrySyncAsync(_gateway, settings, AppDataHelper.GetSettingsFilePath()).ConfigureAwait(false);
                if (!changed) return;
                Log("[PBX Gateway] TURN settings updated from gateway");
                _settings = AppDataHelper.LoadSettingsOrNew();

                if (MainUsesWebRtc(_settings) && WebRtcService.Main.IsReadyForCalls && _shell?.IsWebRtcCallActive != true)
                {
                    var cfg = WebRtcConnectionConfig.FromSettings(_settings, false);
                    if (cfg != null)
                    {
                        await WebRtcService.Main.ReinitializeUAAsync(cfg.WsUri, cfg.SipUri, cfg.Username, cfg.Password).ConfigureAwait(false);
                        Log("[PBX Gateway] WebRTC UA reinitialized with gateway TURN");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[PBX Gateway] TURN sync error: {ex.Message}");
            }
        }

        // ───────────────────────── CDR lookups ─────────────────────────

        private void TryFetchOutboundCallerIdFromCdr(string phoneNumber, DateTime callTime)
        {
            var gw = _gateway;
            if (gw == null) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    for (int attempt = 1; attempt <= 4; attempt++)
                    {
                        await Task.Delay(attempt switch { 1 => 3000, 2 => 5000, 3 => 10000, _ => 15000 }).ConfigureAwait(false);
                        var callerId = await gw.GetCallCallerIdAsync(phoneNumber, callTime).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(callerId))
                        {
                            Log($"[PBX Gateway] CallerID from CDR: {callerId} for {phoneNumber}");
                            History.UpdateOutboundCallerId(phoneNumber, callTime, callerId);
                            RefreshHistory();
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"[PBX Gateway] CDR lookup failed: {ex.Message}");
                }
            });
        }

        // ───────────────────────── PBX originate ─────────────────────────

        /// <summary>True when the main line is WebRTC, the gateway is up and a CallerID list is available.</summary>
        public bool CanUseOriginate() =>
            _gateway != null && _callerIdItems.Count > 0 && ViewModel.SelectedCallerId != null;

        /// <summary>
        /// Start a PBX originate: the PBX rings our WebRTC extension first, then bridges to the destination
        /// with the selected CallerID. The call window calls this instead of <c>MakeCallAsync</c>.
        /// </summary>
        public async Task<OriginateResult?> BeginOriginateAsync(string destination, Action<string> onSessionAttached)
        {
            var gw = _gateway;
            var callerId = ViewModel.SelectedCallerId?.Number;
            if (gw == null || string.IsNullOrEmpty(callerId)) return null;

            _originatePendingDestination = destination;
            _originatePendingCallerId = callerId;
            _originateAcceptedSessionId = null;
            _originateSessionAttached = onSessionAttached;

            try
            {
                var result = await gw.OriginateCallAsync(destination, callerId).ConfigureAwait(false);
                if (result == null || !result.Success)
                {
                    Log($"[Originate] failed: {result?.Error ?? "no response"}");
                    ClearOriginate();
                }
                return result;
            }
            catch (Exception ex)
            {
                Log($"[Originate] error: {ex.Message}");
                ClearOriginate();
                return null;
            }
        }

        public void ClearOriginate()
        {
            _originatePendingDestination = null;
            _originatePendingCallerId = null;
            _originateAcceptedSessionId = null;
            _originateSessionAttached = null;
        }

        private bool TryAttachOriginateCallback(WebRtcEventDto dto, WebRtcService service)
        {
            if (_originatePendingDestination == null || dto.SessionId == null) return false;
            if (!string.IsNullOrEmpty(_originateAcceptedSessionId))
            {
                // Already attached — Miko forks INVITE to every -WS registration; drop duplicates.
                _ = service.HangupAsync(dto.SessionId);
                return true;
            }

            Log($"[Originate] Attaching callback session {dto.SessionId} (caller={dto.CallerNumber})");
            _originateAcceptedSessionId = dto.SessionId;
            _ = service.NotifyOriginateAcceptedAsync(dto.SessionId);
            _ = service.AnswerAsync(dto.SessionId);
            try { _originateSessionAttached?.Invoke(dto.SessionId); } catch { }
            return true;
        }
    }
}
