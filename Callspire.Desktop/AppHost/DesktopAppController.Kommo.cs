using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Softphone.AppHost
{
    /// <summary>
    /// Kommo post-call processing in <b>gateway mode</b>: desktop submits <c>POST /api/kommo/process-call</c>
    /// (stable desktop ↔ gateway contract), gateway does CDR match, Miko recording download and upload.
    /// Local mode (direct Kommo API via <see cref="AmoCrmService"/>) lives in <c>DesktopAppController.KommoLocal.cs</c>.
    /// </summary>
    public sealed partial class DesktopAppController
    {
        private sealed class KommoJob
        {
            public string PhoneNumber = "";
            public DateTime CallTime;
            public string? SessionId;
            public string? CallLog;
            public string DedupKey = "";
            public long? LeadId;
            public long? BrowserLeadId;
        }

        private readonly BlockingCollection<KommoJob> _kommoQueue = new();
        private readonly ConcurrentDictionary<string, DateTime> _kommoProcessed = new();
        private Task? _kommoWorker;
        private CancellationTokenSource? _kommoCts;

        private const int KommoPollIntervalMs = 2000;
        private const int KommoMaxPolls = 180;
        private const int KommoManualLeadMaxPolls = 30;
        private const int KommoManualLeadSubmitDelayMs = 3000;

        private static bool IsGatewayKommoMode(AppSettings s) =>
            s.EnableAmoCrmIntegration &&
            string.Equals(s.AmoCrmRecordingUploadSource?.Trim(), "gateway", StringComparison.OrdinalIgnoreCase);

        private void UpdateAmoCrmIndicator(AppSettings s)
        {
            bool configured = s.EnableAmoCrmIntegration;
            bool gatewayMode = IsGatewayKommoMode(s);
            if (configured && !gatewayMode)
                return; // local mode indicator is driven by InitializeLocalKommoAsync
            bool online = configured && gatewayMode && _gateway != null;
            string text = !configured ? "Not connected"
                : _gateway != null ? "Kommo via PBX Gateway" : "Gateway not configured";
            UiThread.BeginInvoke(() =>
            {
                ViewModel.AmoCrmConfigured = configured;
                ViewModel.AmoCrmOnline = online;
                ViewModel.AmoCrmStatusText = text;
            });

            if (configured && gatewayMode && _gateway != null)
                _ = RefreshKommoGatewayStatusAsync();
        }

        private async Task RefreshKommoGatewayStatusAsync()
        {
            var gw = _gateway;
            if (gw == null) return;
            try
            {
                var status = await gw.GetKommoStatusAsync().ConfigureAwait(false);
                bool ok = status != null && status.Available && status.Enabled;
                UiThread.BeginInvoke(() =>
                {
                    ViewModel.AmoCrmOnline = ok;
                    ViewModel.AmoCrmStatusText = ok ? "Kommo via PBX Gateway" : "Kommo unavailable on gateway";
                });
            }
            catch { }
        }

        private void StartKommoWorker()
        {
            if (_kommoWorker != null) return;
            _kommoCts = new CancellationTokenSource();
            var token = _kommoCts.Token;
            _kommoWorker = Task.Run(async () =>
            {
                try
                {
                    foreach (var job in _kommoQueue.GetConsumingEnumerable(token))
                    {
                        try { await ProcessKommoJobAsync(job).ConfigureAwait(false); }
                        catch (Exception ex) { Log($"[Kommo] worker error: {ex.Message}"); }
                    }
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        private void StopKommoWorker()
        {
            try { _kommoQueue.CompleteAdding(); } catch { }
            try { _kommoCts?.Cancel(); } catch { }
        }

        private void EnqueueKommoJob(CallEndedReport report)
        {
            var s = _settings;
            if (!s.EnableAmoCrmIntegration) return;
            bool gatewayMode = IsGatewayKommoMode(s);
            if (gatewayMode && _gateway == null)
            {
                Log("[Kommo] gateway client not initialized — skipping");
                return;
            }
            if (!gatewayMode && !IsLocalKommoInitialized)
            {
                Log("[Kommo] local Kommo client not initialized — skipping");
                return;
            }
            if (IsOwnOutboundCallerId(report.PhoneNumber))
            {
                Log($"[Kommo] skipping originate callback leg ({report.PhoneNumber})");
                return;
            }

            string sessionId = report.SessionIdForCrm ?? "";
            string dedupKey = string.IsNullOrEmpty(sessionId)
                ? $"{report.PhoneNumber}_{report.CallTime:yyyy-MM-dd HH:mm:ss}"
                : $"{report.PhoneNumber}_{sessionId}";

            if (_kommoProcessed.ContainsKey(dedupKey))
            {
                Log($"[Kommo] {dedupKey} already queued — skipping duplicate");
                return;
            }
            _kommoProcessed[dedupKey] = DateTime.Now;
            foreach (var old in _kommoProcessed.Where(kv => kv.Value < DateTime.Now.AddHours(-1)).Select(kv => kv.Key).ToList())
                _kommoProcessed.TryRemove(old, out _);

            try
            {
                _kommoQueue.Add(new KommoJob
                {
                    PhoneNumber = report.PhoneNumber,
                    CallTime = report.CallTime,
                    SessionId = string.IsNullOrEmpty(sessionId) ? null : sessionId,
                    CallLog = report.TechnicalDetails.Count > 0 ? string.Join("\n", report.TechnicalDetails) : null,
                    DedupKey = dedupKey,
                    LeadId = report.LeadId,
                    BrowserLeadId = report.BrowserLeadId,
                });
            }
            catch (InvalidOperationException) { /* shutting down */ }
        }

        /// <summary>Manual retry from Call details (gateway mode).</summary>
        public async Task<(bool Ok, string? Error)> RetryKommoAsync(CallHistoryItem call)
        {
            var gw = _gateway;
            if (gw == null) return (false, "PBX Gateway not configured");
            var request = new KommoProcessCallRetryRequest
            {
                Phone = call.PhoneNumber,
                CallTime = CallWindowHelpers.FormatCallTimeUtcIso(call.CallTime),
                SessionId = call.Transport == CallTransport.WebRtc ? call.WebRtcSessionId : call.SipCallId,
                IsIncoming = call.IsIncoming,
                DurationSeconds = (int)(call.Duration?.TotalSeconds ?? 0),
                WasAnswered = call.WasAnswered || call.AnswerTime.HasValue,
                LeadId = call.AmoCrmLeadId,
                ClientRecordingEnabled = false,
                ConnectionSlot = CallStatisticsService.GetEffectiveConnectionSlot(call) == CallConnectionSlot.Secondary ? "secondary" : "main",
                EnableRecordingUpload = _settings.EnableAmoCrmRecordingUpload,
                AnswerTime = call.AnswerTime?.ToUniversalTime().ToString("o"),
                CallEndTime = CallWindowHelpers.FormatCallEndTimeIso(call),
            };
            var (job, error) = await gw.RetryKommoProcessCallAsync(request).ConfigureAwait(false);
            if (job == null || string.IsNullOrWhiteSpace(job.Id)) return (false, error ?? "submit failed");
            var final = await PollKommoJobAsync(gw, job.Id, KommoMaxPolls).ConfigureAwait(false);
            ApplyKommoJobResult(call.PhoneNumber, call.CallTime, request.SessionId, final);
            return (final != null && MapGatewayJobStatus(final.Status) == AmoCrmUploadStatus.Uploaded, final?.Reason);
        }

        private async Task ProcessKommoJobAsync(KommoJob job)
        {
            if (!IsGatewayKommoMode(_settings))
            {
                await ProcessLocalKommoJobAsync(job).ConfigureAwait(false);
                return;
            }

            var gw = _gateway;
            if (gw == null) { _kommoProcessed.TryRemove(job.DedupKey, out _); return; }

            try
            {
                var call = History.GetCallForAmoCrmUpload(job.PhoneNumber, job.CallTime, job.SessionId);
                var settings = _settings;
                bool enableRecordingUpload = settings.EnableAmoCrmRecordingUpload;
                bool enableLeadSelection = settings.EnableAmoCrmLeadSelection;

                bool manualLeadUsed = false;
                long? leadId = job.LeadId;

                if (enableLeadSelection && _shell != null)
                {
                    if (job.BrowserLeadId.HasValue)
                    {
                        leadId = job.BrowserLeadId;
                    }
                    else if (!leadId.HasValue)
                    {
                        var selection = await _shell.ShowLeadSelectionAsync(job.PhoneNumber).ConfigureAwait(false);
                        if (!selection.Proceed)
                        {
                            if (call != null)
                            {
                                History.UpdateAmoCrmUploadStatus(job.PhoneNumber, call.CallTime, AmoCrmUploadStatus.Cancelled,
                                    selection.Cancelled ? "User cancelled lead selection" : "Lead selection failed");
                                RefreshHistory();
                            }
                            _kommoProcessed.TryRemove(job.DedupKey, out _);
                            return;
                        }
                        leadId = selection.LeadId;
                        manualLeadUsed = true;
                    }
                }

                // SIP: wait for hangup to be fully processed.
                if (call?.Transport == CallTransport.Sip)
                {
                    var sip = call.ConnectionSlot == CallConnectionSlot.Secondary ? _sipSecondary : _sipMain;
                    for (int wait = 0; wait < 300 && sip?.IsInCall == true
                         && string.Equals(sip.ActiveDialNumber, job.PhoneNumber, StringComparison.OrdinalIgnoreCase); wait++)
                    {
                        await Task.Delay(1000).ConfigureAwait(false);
                    }
                }

                call = History.GetCallForAmoCrmUpload(job.PhoneNumber, job.CallTime, job.SessionId) ?? call;
                if (call != null && call.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded)
                {
                    Log($"[Kommo] already uploaded: {job.DedupKey}");
                    return;
                }

                bool wasAnswered = (call?.WasAnswered ?? false) || call?.AnswerTime.HasValue == true;
                string connectionSlot = call != null && CallStatisticsService.GetEffectiveConnectionSlot(call) == CallConnectionSlot.Secondary ? "secondary" : "main";
                string? callFromLabel = call?.OutboundCallerId
                    ?? (connectionSlot == "secondary" ? ViewModel.Secondary.DisplayName : ViewModel.Main.DisplayName);

                if (manualLeadUsed)
                    await Task.Delay(KommoManualLeadSubmitDelayMs).ConfigureAwait(false);

                var request = new KommoProcessCallRequest
                {
                    Phone = job.PhoneNumber,
                    CallTime = CallWindowHelpers.FormatCallTimeUtcIso(call?.CallTime ?? job.CallTime),
                    SessionId = job.SessionId,
                    IsIncoming = call?.IsIncoming ?? false,
                    DurationSeconds = (int)(call?.Duration?.TotalSeconds ?? 0),
                    WasAnswered = wasAnswered,
                    LeadId = leadId,
                    ClientRecordingEnabled = false, // gateway fetches recording from Miko CDR
                    ConnectionSlot = connectionSlot,
                    CallFromLabel = callFromLabel,
                    CallLog = job.CallLog,
                    EnableRecordingUpload = enableRecordingUpload,
                    AnswerTime = call?.AnswerTime?.ToUniversalTime().ToString("o"),
                    CallEndTime = CallWindowHelpers.FormatCallEndTimeIso(call),
                };

                Log($"[Kommo] submitting process-call for {job.PhoneNumber} (lead={leadId?.ToString() ?? "auto"})");
                var (submitted, submitError) = await gw.SubmitKommoProcessCallAsync(request).ConfigureAwait(false);
                if (submitted == null || string.IsNullOrWhiteSpace(submitted.Id))
                {
                    Log($"[Kommo] submit failed: {submitError ?? "unknown"}");
                    if (call != null)
                        History.UpdateAmoCrmUploadStatus(job.PhoneNumber, call.CallTime, AmoCrmUploadStatus.Failed, submitError);
                    RefreshHistory();
                    _kommoProcessed.TryRemove(job.DedupKey, out _);
                    return;
                }

                var final = await PollKommoJobAsync(gw, submitted.Id, manualLeadUsed ? KommoManualLeadMaxPolls : KommoMaxPolls).ConfigureAwait(false);
                if (final == null)
                {
                    Log("[Kommo] status polling timed out");
                    if (call != null)
                        History.UpdateAmoCrmUploadStatus(job.PhoneNumber, call.CallTime, AmoCrmUploadStatus.Failed, "Gateway status timeout");
                    RefreshHistory();
                    _kommoProcessed.TryRemove(job.DedupKey, out _);
                    return;
                }

                ApplyKommoJobResult(job.PhoneNumber, call?.CallTime ?? job.CallTime, job.SessionId, final);
                if (MapGatewayJobStatus(final.Status) != AmoCrmUploadStatus.Uploaded)
                    _kommoProcessed.TryRemove(job.DedupKey, out _);
            }
            catch (Exception ex)
            {
                Log($"[Kommo] error: {ex.Message}");
                _kommoProcessed.TryRemove(job.DedupKey, out _);
            }
        }

        private static async Task<KommoProcessCallJobStatus?> PollKommoJobAsync(MikoPbxCdrService gw, string jobId, int maxPolls)
        {
            KommoProcessCallJobStatus? status = null;
            for (int i = 0; i < maxPolls; i++)
            {
                await Task.Delay(KommoPollIntervalMs).ConfigureAwait(false);
                status = await gw.GetKommoProcessCallStatusAsync(jobId).ConfigureAwait(false);
                if (status == null) continue;
                string st = status.Status?.ToLowerInvariant() ?? "";
                if (st is "uploaded" or "failed" or "skipped") break;
            }
            return status;
        }

        private void ApplyKommoJobResult(string phoneNumber, DateTime callTime, string? sessionId, KommoProcessCallJobStatus? final)
        {
            if (final == null) return;
            var call = History.GetCallForAmoCrmUpload(phoneNumber, callTime, sessionId);
            if (call == null) return;

            var uploadStatus = MapGatewayJobStatus(final.Status);
            History.UpdateAmoCrmUploadStatus(phoneNumber, call.CallTime, uploadStatus, final.Reason, MapGatewayUploadSource(final.UploadSource));
            if (final.LeadId.HasValue && uploadStatus == AmoCrmUploadStatus.Uploaded)
                History.UpdateAmoCrmLeadId(phoneNumber, call.CallTime, final.LeadId.Value);
            History.ApplyPbxTruthFromGateway(phoneNumber, call.CallTime, final.PbxWasAnswered, final.PbxDurationSeconds, sessionId);

            Log(uploadStatus == AmoCrmUploadStatus.Uploaded
                ? $"[Kommo] success for {phoneNumber}, lead={final.LeadId}"
                : $"[Kommo] {final.Status} for {phoneNumber}: {final.Reason}");

            RefreshHistory();
            RefreshStatistics();
        }

        private static AmoCrmUploadStatus MapGatewayJobStatus(string? status) => (status ?? "").ToLowerInvariant() switch
        {
            "uploaded" => AmoCrmUploadStatus.Uploaded,
            "failed" => AmoCrmUploadStatus.Failed,
            _ => AmoCrmUploadStatus.NotUploaded,
        };

        private static AmoCrmUploadedRecordingSource? MapGatewayUploadSource(string? source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            if (source.Equals("miko_pbx", StringComparison.OrdinalIgnoreCase)) return AmoCrmUploadedRecordingSource.MikoPbx;
            if (source.Equals("local", StringComparison.OrdinalIgnoreCase) || source.Equals("client", StringComparison.OrdinalIgnoreCase))
                return AmoCrmUploadedRecordingSource.Local;
            return null;
        }
    }
}
