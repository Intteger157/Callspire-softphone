using System;
using System.IO;
using System.Threading.Tasks;

namespace Softphone.AppHost
{
    /// <summary>
    /// Kommo post-call processing in <b>local mode</b>: the desktop talks to the Kommo API directly through
    /// <see cref="AmoCrmService"/> (OAuth or manual token) and uploads the locally recorded file.
    /// UI-agnostic — the manual lead picker is provided by the shell via <see cref="KommoLeadSelectionUi"/>.
    /// </summary>
    public sealed partial class DesktopAppController
    {
        private AmoCrmService? _amoCrm;
        private readonly object _amoCrmGate = new();

        public AmoCrmService? LocalKommo => _amoCrm;
        public bool IsLocalKommoInitialized => _amoCrm?.IsInitialized == true;

        private static bool IsLocalKommoMode(AppSettings s) => s.EnableAmoCrmIntegration && !IsGatewayKommoMode(s);

        private void DisposeLocalKommo()
        {
            AmoCrmService? old;
            lock (_amoCrmGate) { old = _amoCrm; _amoCrm = null; }
            try { old?.Dispose(); } catch { }
        }

        /// <summary>
        /// (Re)creates the local Kommo client from settings. Safe to call on every settings reload;
        /// no-op when local mode is off.
        /// </summary>
        /// <summary>Re-read settings and re-create the local Kommo client only (after OAuth completes; no line reconnect).</summary>
        public Task ReloadLocalKommoAsync()
        {
            _settings = LoadSettingsWithMigrations();
            return InitializeLocalKommoAsync(_settings);
        }

        private async Task InitializeLocalKommoAsync(AppSettings s)
        {
            DisposeLocalKommo();
            if (!IsLocalKommoMode(s)) return;
            if (string.IsNullOrWhiteSpace(s.AmoCrmSubdomain))
            {
                Log("[Kommo/local] subdomain is empty — skipping");
                return;
            }

            string authMode = s.AmoCrmAuthMode ?? "manual";
            var svc = new AmoCrmService();
            lock (_amoCrmGate) { _amoCrm = svc; }

            SetLocalKommoIndicator(online: false, "Kommo: connecting…");
            try
            {
                if (authMode == "oauth")
                {
                    if (string.IsNullOrEmpty(s.AmoCrmOAuthAccessTokenEncrypted))
                    {
                        Log("[Kommo/local] OAuth mode but no access token stored");
                        SetLocalKommoIndicator(false, "Kommo: not authorized");
                        return;
                    }
                    string accessToken = TokenEncryption.Decrypt(s.AmoCrmOAuthAccessTokenEncrypted);
                    string? refreshToken = string.IsNullOrEmpty(s.AmoCrmOAuthRefreshTokenEncrypted) ? null : TokenEncryption.Decrypt(s.AmoCrmOAuthRefreshTokenEncrypted);
                    string? clientSecret = string.IsNullOrEmpty(s.AmoCrmClientSecretEncrypted) ? null : TokenEncryption.Decrypt(s.AmoCrmClientSecretEncrypted);
                    if (string.IsNullOrEmpty(accessToken))
                    {
                        SetLocalKommoIndicator(false, "Kommo: not authorized");
                        return;
                    }
                    await KommoInitHelper.ExecuteWithNetworkRetryAsync(
                        () => svc.InitializeOAuthAsync(s.AmoCrmSubdomain, accessToken, refreshToken, s.AmoCrmOAuthTokenExpiresAt,
                                                       s.AmoCrmClientId, clientSecret, s.AmoCrmRedirectUri),
                        msg => Log($"[Kommo/local] {msg}")).ConfigureAwait(false);
                }
                else
                {
                    if (string.IsNullOrEmpty(s.AmoCrmAccessTokenEncrypted))
                    {
                        Log("[Kommo/local] manual mode but no token stored");
                        SetLocalKommoIndicator(false, "Kommo: token missing");
                        return;
                    }
                    string token = TokenEncryption.Decrypt(s.AmoCrmAccessTokenEncrypted);
                    if (string.IsNullOrEmpty(token))
                    {
                        SetLocalKommoIndicator(false, "Kommo: token missing");
                        return;
                    }
                    await KommoInitHelper.ExecuteWithNetworkRetryAsync(
                        () => svc.InitializeAsync(s.AmoCrmSubdomain, token),
                        msg => Log($"[Kommo/local] {msg}")).ConfigureAwait(false);
                }

                if (!ReferenceEquals(_amoCrm, svc)) return; // superseded by a newer init
                Log($"[Kommo/local] initialized for {s.AmoCrmSubdomain} (mode={authMode})");
                SetLocalKommoIndicator(true, "Kommo (local)");
            }
            catch (UnauthorizedAccessException ex)
            {
                Log($"[Kommo/local] token invalid or expired: {ex.Message}");
                if (ReferenceEquals(_amoCrm, svc)) DisposeLocalKommo();
                SetLocalKommoIndicator(false, "Kommo: token invalid — re-authorize in Settings");
            }
            catch (Exception ex)
            {
                Log($"[Kommo/local] init error ({KommoInitHelper.ClassifyFailure(ex)}): {ex.Message}");
                if (ReferenceEquals(_amoCrm, svc)) DisposeLocalKommo();
                SetLocalKommoIndicator(false, "Kommo: connection failed");
            }
        }

        private void SetLocalKommoIndicator(bool online, string text)
        {
            UiThread.BeginInvoke(() =>
            {
                ViewModel.AmoCrmConfigured = true;
                ViewModel.AmoCrmOnline = online;
                ViewModel.AmoCrmStatusText = text;
            });
        }

        /// <summary>Local-mode counterpart of the gateway job processor.</summary>
        private async Task ProcessLocalKommoJobAsync(KommoJob job)
        {
            var svc = _amoCrm;
            if (svc == null || !svc.IsInitialized)
            {
                Log("[Kommo/local] service not initialized — skipping job");
                _kommoProcessed.TryRemove(job.DedupKey, out _);
                return;
            }

            try
            {
                var settings = _settings;
                var call = History.GetCallForAmoCrmUpload(job.PhoneNumber, job.CallTime, job.SessionId);

                // SIP: wait for hangup/recording finalisation.
                if (call?.Transport == CallTransport.Sip)
                {
                    var sip = call.ConnectionSlot == CallConnectionSlot.Secondary ? _sipSecondary : _sipMain;
                    for (int wait = 0; wait < 300 && sip?.IsInCall == true
                         && string.Equals(sip.ActiveDialNumber, job.PhoneNumber, StringComparison.OrdinalIgnoreCase); wait++)
                    {
                        await Task.Delay(1000).ConfigureAwait(false);
                    }
                }
                else
                {
                    // WebRTC recordings are flushed asynchronously by the engine; give it a moment.
                    await Task.Delay(1500).ConfigureAwait(false);
                }

                call = History.GetCallForAmoCrmUpload(job.PhoneNumber, job.CallTime, job.SessionId) ?? call;
                if (call != null && call.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded)
                {
                    Log($"[Kommo/local] already uploaded: {job.DedupKey}");
                    return;
                }

                bool isIncoming = call?.IsIncoming ?? false;
                bool wasAnswered = (call?.WasAnswered ?? false) || call?.AnswerTime.HasValue == true;
                int durationSeconds = (int)(call?.Duration?.TotalSeconds ?? 0);

                string? recordingPath = settings.EnableAmoCrmRecordingUpload
                    ? CallWindowHelpers.ResolveLocalClientRecordingPath(call)
                    : null;
                if (recordingPath != null && (!File.Exists(recordingPath) || !CallWindowHelpers.IsRecordingUsableForUpload(recordingPath)))
                    recordingPath = null;
                if (!wasAnswered && recordingPath == null)
                    durationSeconds = 0;

                string? callFromLabel = call?.OutboundCallerId
                    ?? (call != null && CallStatisticsService.GetEffectiveConnectionSlot(call) == CallConnectionSlot.Secondary
                        ? ViewModel.Secondary.DisplayName : ViewModel.Main.DisplayName);

                long? leadId = job.BrowserLeadId ?? job.LeadId;
                Log($"[Kommo/local] processing {job.PhoneNumber} (lead={leadId?.ToString() ?? "auto"}, rec={(recordingPath != null ? "yes" : "no")})");

                ProcessCallResult result = leadId.HasValue
                    ? await svc.ProcessCallForSpecificLeadAsync(leadId.Value, job.PhoneNumber, isIncoming, durationSeconds, wasAnswered,
                          job.CallLog, recordingPath, job.CallTime, callFromLabel).ConfigureAwait(false)
                    : await svc.ProcessCallAsync(job.PhoneNumber, isIncoming, durationSeconds, wasAnswered, job.CallLog, recordingPath,
                          settings.EnableAmoCrmLeadSelection, job.CallTime, callFromLabel).ConfigureAwait(false);

                if (call != null)
                {
                    bool ok = result.Success || result.UploadStatus == AmoCrmUploadStatus.Uploaded;
                    AmoCrmUploadedRecordingSource? source = ok && recordingPath != null
                        ? CallWindowHelpers.DetectRecordingUploadSource(recordingPath) : null;
                    History.UpdateAmoCrmUploadStatus(job.PhoneNumber, call.CallTime, result.UploadStatus, result.Reason, source);
                    if (ok && result.LeadId.HasValue)
                        History.UpdateAmoCrmLeadId(job.PhoneNumber, call.CallTime, result.LeadId.Value);
                    if (!ok) _kommoProcessed.TryRemove(job.DedupKey, out _);
                    Log(ok
                        ? $"[Kommo/local] success for {job.PhoneNumber}, lead={result.LeadId}"
                        : $"[Kommo/local] {result.UploadStatus} for {job.PhoneNumber}: {result.Reason}");
                }

                RefreshHistory();
                RefreshStatistics();
            }
            catch (Exception ex)
            {
                Log($"[Kommo/local] error: {ex.Message}");
                _kommoProcessed.TryRemove(job.DedupKey, out _);
            }
        }

        /// <summary>True when Kommo is enabled and a retry can be attempted in the configured mode.</summary>
        public bool CanRetryKommo =>
            _settings.EnableAmoCrmIntegration && (IsGatewayKommoMode(_settings) ? _gateway != null : IsLocalKommoInitialized);

        /// <summary>Manual retry from Call details — dispatches to gateway or local mode based on settings.</summary>
        public Task<(bool Ok, string? Error)> RetryKommoForCallAsync(CallHistoryItem call)
            => IsGatewayKommoMode(_settings) ? RetryKommoAsync(call) : RetryLocalKommoAsync(call);

        /// <summary>Manual retry from Call details (local mode): upload the recording or a missed-call note to the lead/contact.</summary>
        public async Task<(bool Ok, string? Error)> RetryLocalKommoAsync(CallHistoryItem call)
        {
            var svc = _amoCrm;
            if (svc == null || !svc.IsInitialized) return (false, "Kommo is not connected");
            try
            {
                string? recordingPath = _settings.EnableAmoCrmRecordingUpload ? CallWindowHelpers.ResolveLocalClientRecordingPath(call) : null;
                if (recordingPath != null && !File.Exists(recordingPath)) recordingPath = null;
                bool wasAnswered = call.WasAnswered || call.AnswerTime.HasValue;
                int duration = (int)(call.Duration?.TotalSeconds ?? 0);

                ProcessCallResult result = call.AmoCrmLeadId.HasValue
                    ? await svc.ProcessCallForSpecificLeadAsync(call.AmoCrmLeadId.Value, call.PhoneNumber, call.IsIncoming, duration, wasAnswered,
                          null, recordingPath, call.CallTime, call.OutboundCallerId).ConfigureAwait(false)
                    : await svc.ProcessCallAsync(call.PhoneNumber, call.IsIncoming, duration, wasAnswered, null, recordingPath,
                          _settings.EnableAmoCrmLeadSelection, call.CallTime, call.OutboundCallerId).ConfigureAwait(false);

                bool ok = result.Success || result.UploadStatus == AmoCrmUploadStatus.Uploaded;
                History.UpdateAmoCrmUploadStatus(call.PhoneNumber, call.CallTime, result.UploadStatus, result.Reason,
                    ok && recordingPath != null ? CallWindowHelpers.DetectRecordingUploadSource(recordingPath) : null);
                if (ok && result.LeadId.HasValue) History.UpdateAmoCrmLeadId(call.PhoneNumber, call.CallTime, result.LeadId.Value);
                RefreshHistory();
                RefreshStatistics();
                return (ok, result.Reason);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
