using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Softphone.AppHost.ViewModels;

namespace Softphone.AppHost
{
    /// <summary>Outbound dialing, incoming-call routing, call completion, history and statistics.</summary>
    public sealed partial class DesktopAppController
    {
        private CallStatisticsDrillDown _historyDrill = CallStatisticsDrillDown.All;
        private string? _historyDrillPhone;
        private readonly Queue<string> _pendingClickToCall = new();

        // ───────────────────────── outbound ─────────────────────────

        /// <summary>
        /// Dial from the main dialer. When both lines are configured and <paramref name="slot"/> is null,
        /// the shell is asked which line to use.
        /// </summary>
        public async Task PlaceCallAsync(string rawNumber, ConnectionSlot? slot = null, long? leadId = null, long? browserLeadId = null)
        {
            string number = PhoneNumberHelper.NormalizeForDial(rawNumber);
            if (string.IsNullOrWhiteSpace(number))
            {
                ShowMessage("Call", "Enter a phone number.");
                return;
            }

            if (_shell == null) return;

            if (_shell.HasActiveCallWindow)
            {
                Log("[Controller] PlaceCall ignored: call window already open");
                _shell.BringToForeground();
                return;
            }

            if (slot == null)
            {
                if (ViewModel.Main.IsConfigured && ViewModel.Secondary.IsConfigured)
                {
                    var pick = await _shell.ShowConnectionSelectionAsync(BuildConnectionSelectionRequest()).ConfigureAwait(false);
                    if (pick.Slot == null) return;
                    slot = pick.Slot;
                    if (!string.IsNullOrEmpty(pick.CallerId))
                        SelectCallerId(pick.CallerId);
                }
                else
                {
                    slot = ViewModel.Secondary.IsConfigured && !ViewModel.Main.IsConfigured
                        ? ConnectionSlot.Secondary
                        : ConnectionSlot.Main;
                }
            }

            var target = slot.Value;
            var vm = target == ConnectionSlot.Main ? ViewModel.Main : ViewModel.Secondary;
            if (!vm.IsOnline)
            {
                ShowMessage("Not connected", $"{vm.DisplayName} is not connected. Check settings or reconnect.");
                return;
            }

            bool webRtc = vm.IsWebRtc;
            var startTime = DateTime.Now;

            History.AddCall(new CallHistoryItem
            {
                PhoneNumber = number,
                CallTime = startTime,
                Status = CallStatus.Calling,
                IsIncoming = false,
                Transport = webRtc ? CallTransport.WebRtc : CallTransport.Sip,
                ConnectionSlot = target == ConnectionSlot.Main ? CallConnectionSlot.Main : CallConnectionSlot.Secondary,
                OutboundCallerId = target == ConnectionSlot.Main ? ViewModel.SelectedCallerId?.Number : null,
                AmoCrmLeadId = leadId ?? browserLeadId,
            });
            RefreshHistory();

            // PBX Originate: main WebRTC line + a CallerID chosen from the gateway list.
            bool useOriginate = webRtc && target == ConnectionSlot.Main && CanUseOriginate();

            _shell.ShowCallWindow(new CallWindowRequest
            {
                PhoneNumber = number,
                IsIncoming = false,
                Slot = target,
                Transport = webRtc ? CallTransport.WebRtc : CallTransport.Sip,
                Sip = webRtc ? null : (target == ConnectionSlot.Main ? _sipMain : _sipSecondary),
                WebRtc = webRtc ? WebRtcService.GetSlot(target == ConnectionSlot.Main ? "main" : "secondary") : null,
                CallStartTime = startTime,
                AmoCrmLeadId = leadId,
                AmoCrmBrowserLeadId = browserLeadId,
                IsOriginateCall = useOriginate,
            });

            UiThread.BeginInvoke(() => ViewModel.PhoneNumber = "");
        }

        // ───────────────────────── incoming ─────────────────────────

        private void HandleIncomingSipCall(SipService sip, ConnectionSlot slot, string callerNumber)
        {
            try
            {
                Log($"[Controller] Incoming SIP call from {callerNumber} on {slot}");
                if (_shell == null) return;

                if (_shell.HasActiveCallWindow)
                {
                    Log("[Controller] Incoming SIP call rejected: another call window is open");
                    _ = sip.RejectIncomingCallAsync();
                    return;
                }

                var startTime = DateTime.Now;
                History.AddCall(new CallHistoryItem
                {
                    PhoneNumber = callerNumber,
                    CallTime = startTime,
                    Status = CallStatus.Calling,
                    IsIncoming = true,
                    Transport = CallTransport.Sip,
                    ConnectionSlot = slot == ConnectionSlot.Main ? CallConnectionSlot.Main : CallConnectionSlot.Secondary,
                });
                RefreshHistory();

                _shell.ShowCallWindow(new CallWindowRequest
                {
                    PhoneNumber = callerNumber,
                    IsIncoming = true,
                    Slot = slot,
                    Transport = CallTransport.Sip,
                    Sip = sip,
                    CallStartTime = startTime,
                });
            }
            catch (Exception ex)
            {
                Log($"[Controller] HandleIncomingSipCall error: {ex.Message}");
            }
        }

        private void HandleIncomingWebRtcCall(WebRtcEventDto dto)
        {
            try
            {
                if (string.IsNullOrEmpty(dto.SessionId) || string.IsNullOrEmpty(dto.CallerNumber))
                {
                    Log("[Controller] Incoming WebRTC call without sessionId/callerNumber — ignored");
                    return;
                }
                if (_shell == null) return;

                string slotName = dto.Slot ?? "main";
                var service = WebRtcService.GetSlot(slotName);
                bool secondary = slotName == "secondary";

                // PBX Originate callback (our own trunk CallerID calling us back).
                if (TryAttachOriginateCallback(dto, service))
                    return;

                if (IsOwnOutboundCallerId(dto.CallerNumber))
                {
                    Log($"[Controller] Silently rejecting foreign originate callback from {dto.CallerNumber}");
                    _ = service.HangupAsync(dto.SessionId);
                    return;
                }

                if (_shell.HasActiveCallWindow)
                {
                    Log("[Controller] Incoming WebRTC call rejected: another call window is open");
                    _ = service.HangupAsync(dto.SessionId);
                    return;
                }

                var startTime = DateTime.Now;
                History.AddCall(new CallHistoryItem
                {
                    PhoneNumber = dto.CallerNumber,
                    CallTime = startTime,
                    Status = CallStatus.Calling,
                    IsIncoming = true,
                    Transport = CallTransport.WebRtc,
                    WebRtcSessionId = dto.SessionId,
                    ConnectionSlot = secondary ? CallConnectionSlot.Secondary : CallConnectionSlot.Main,
                });
                RefreshHistory();

                _shell.ShowCallWindow(new CallWindowRequest
                {
                    PhoneNumber = dto.CallerNumber,
                    IsIncoming = true,
                    Slot = secondary ? ConnectionSlot.Secondary : ConnectionSlot.Main,
                    Transport = CallTransport.WebRtc,
                    WebRtc = service,
                    IncomingSessionId = dto.SessionId,
                    CallStartTime = startTime,
                });
            }
            catch (Exception ex)
            {
                Log($"[Controller] HandleIncomingWebRtcCall error: {ex.Message}");
            }
        }

        private void OnSipCallEnded(ConnectionSlot slot)
        {
            Log($"[Controller] SIP call ended on {slot}");
            AfterCallEnded();
        }

        private void OnWebRtcCallEnded(WebRtcEventDto dto)
        {
            Log($"[Controller] WebRTC call ended (slot={dto.Slot}, cause={dto.Cause})");
            AfterCallEnded();
        }

        private void AfterCallEnded()
        {
            if (_reconnectPendingAfterCall)
            {
                _reconnectPendingAfterCall = false;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500).ConfigureAwait(false);
                    try { await ReconnectFromSettingsAsync().ConfigureAwait(false); } catch { }
                });
            }
        }

        // ───────────────────────── call completed ─────────────────────────

        /// <summary>
        /// Called by the call window when a call has finished. Persists details, refreshes views,
        /// fetches the outbound CallerID from CDR when missing and enqueues the Kommo job.
        /// </summary>
        public void OnCallCompleted(CallEndedReport report)
        {
            if (report == null) return;
            try
            {
                Log($"[Controller] OnCallCompleted: {CallWindowHelpers.FormatCallDetailsString(report.PhoneNumber, report.CallTime, report.RingbackStart, report.RingbackEnd, report.AnswerTime, report.WasAnswered, report.Duration, report.EndedBy, report.TechnicalDetails.Count)}");

                History.UpdateCallDetails(
                    report.PhoneNumber, report.CallTime,
                    report.RingbackStart, report.RingbackEnd, report.AnswerTime, report.WasAnswered,
                    report.EndedBy, report.TechnicalDetails, report.Duration, report.RecordingFilePath,
                    report.Transport, report.SipCallId, report.WebRtcSessionId, report.OutboundCallerId,
                    report.Slot == ConnectionSlot.Main ? CallConnectionSlot.Main : CallConnectionSlot.Secondary,
                    report.InboundRtpPackets.HasValue ? (int?)Math.Min(int.MaxValue, report.InboundRtpPackets.Value) : null);

                History.UpdateCallStatus(report.PhoneNumber, report.CallTime, report.FinalStatus, report.Duration);

                if ((report.LeadId ?? report.BrowserLeadId).HasValue)
                    History.UpdateAmoCrmLeadId(report.PhoneNumber, report.CallTime, report.LeadId ?? report.BrowserLeadId);

                RefreshHistory();
                RefreshStatistics();

                if (!report.IsIncoming && string.IsNullOrEmpty(report.OutboundCallerId) && report.WasAnswered && report.EndedBy != CallEndedBy.Unknown)
                    TryFetchOutboundCallerIdFromCdr(report.PhoneNumber, report.CallTime);

                EnqueueKommoJob(report);
            }
            catch (Exception ex)
            {
                Log($"[Controller] OnCallCompleted error: {ex.Message}");
            }
        }

        /// <summary>Lightweight in-call status update (ringing → connected) for the history list.</summary>
        public void OnCallStatusChanged(string phoneNumber, DateTime callTime, CallStatus status, TimeSpan? duration = null)
        {
            History.UpdateCallStatus(phoneNumber, callTime, status, duration);
            RefreshHistory();
        }

        // ───────────────────────── history ─────────────────────────

        public void RefreshHistory()
        {
            try
            {
                var all = History.GetHistory();
                IEnumerable<CallHistoryItem> filtered = all;
                if (_historyDrill != CallStatisticsDrillDown.All || !string.IsNullOrWhiteSpace(_historyDrillPhone))
                {
                    var filter = ViewModel.Statistics.BuildFilter();
                    var (from, to) = CallStatisticsService.ResolvePeriodRange(filter.Period, filter.CustomFrom, filter.CustomTo);
                    filtered = CallStatisticsService.ApplyDrillDown(CallStatisticsService.FilterByPeriod(all, from, to), _historyDrill, _historyDrillPhone);
                }

                string mainName = ViewModel.Main.DisplayName;
                string secName = ViewModel.Secondary.DisplayName;
                var items = filtered
                    .OrderByDescending(c => c.CallTime)
                    .Take(200)
                    .Select(c => new HistoryItemViewModel(c, mainName, secName))
                    .ToList();

                UiThread.BeginInvoke(() => ViewModel.ReplaceHistory(items));
                HistoryChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log($"[Controller] RefreshHistory error: {ex.Message}");
            }
        }

        public void SetHistoryDrillDown(CallStatisticsDrillDown drill, string? phoneNumber = null)
        {
            _historyDrill = drill;
            _historyDrillPhone = phoneNumber;
            string? hint = null;
            if (drill != CallStatisticsDrillDown.All || !string.IsNullOrWhiteSpace(phoneNumber))
                hint = string.IsNullOrWhiteSpace(phoneNumber) ? $"Filter: {drill}" : $"Filter: {phoneNumber}";
            UiThread.BeginInvoke(() => ViewModel.HistoryFilterHint = hint);
            RefreshHistory();
        }

        public void ClearHistoryFilter() => SetHistoryDrillDown(CallStatisticsDrillDown.All);

        public void ClearHistory()
        {
            History.ClearHistory();
            RefreshHistory();
            RefreshStatistics();
        }

        public CallHistoryItem? FindHistoryItem(string phoneNumber, DateTime callTime) => History.GetCall(phoneNumber, callTime);

        // ───────────────────────── statistics ─────────────────────────

        public void RefreshStatistics()
        {
            try
            {
                var filter = ViewModel.Statistics.BuildFilter();
                string mainName = ViewModel.Main.DisplayName;
                string secName = ViewModel.Secondary.DisplayName;
                bool secEnabled = ViewModel.Secondary.IsConfigured;
                var report = CallStatisticsService.BuildReport(History.GetHistory(), filter, mainName, secName, secEnabled);
                UiThread.BeginInvoke(() => ViewModel.Statistics.Apply(report, mainName, secName));
            }
            catch (Exception ex)
            {
                Log($"[Controller] RefreshStatistics error: {ex.Message}");
            }
        }

        public string ExportStatisticsCsv(string? targetPath = null)
        {
            var filter = ViewModel.Statistics.BuildFilter();
            var report = CallStatisticsService.BuildReport(History.GetHistory(), filter, ViewModel.Main.DisplayName, ViewModel.Secondary.DisplayName, ViewModel.Secondary.IsConfigured);
            string path = targetPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"Callspire-stats-{DateTime.Now:yyyyMMdd-HHmm}.csv");
            CallStatisticsService.SaveReportCsv(report, path, ViewModel.Main.DisplayName, ViewModel.Secondary.DisplayName);
            return path;
        }

        // ───────────────────────── click-to-call ─────────────────────────

        /// <summary>
        /// Handle a <c>callspire://call?number=...</c> / <c>tel:</c> URL. Queued until a line is online.
        /// </summary>
        public void HandleProtocolUrl(string url)
        {
            string? number = ExtractNumberFromUrl(url);
            if (string.IsNullOrWhiteSpace(number)) return;
            Log($"[Controller] click-to-call: {number}");
            _shell?.BringToForeground();

            if (ViewModel.AnyOnline)
            {
                _ = PlaceCallAsync(number);
                return;
            }

            lock (_pendingClickToCall) _pendingClickToCall.Enqueue(number);
            UiThread.BeginInvoke(() => ViewModel.PhoneNumber = number);
        }

        internal void FlushPendingClickToCall()
        {
            string? next = null;
            lock (_pendingClickToCall)
            {
                if (_pendingClickToCall.Count > 0) next = _pendingClickToCall.Dequeue();
            }
            if (next != null) _ = PlaceCallAsync(next);
        }

        internal static string? ExtractNumberFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                if (url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(url.Substring(4));

                var uri = new Uri(url);
                var query = uri.Query.TrimStart('?');
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2 && (kv[0] == "number" || kv[0] == "phone" || kv[0] == "to"))
                        return Uri.UnescapeDataString(kv[1]);
                }
                // callspire://+79991234567 or callspire:+7999
                string hostPart = uri.Host + uri.AbsolutePath;
                if (string.Equals(uri.Host, "call", StringComparison.OrdinalIgnoreCase))
                    hostPart = uri.AbsolutePath.Trim('/');
                return string.IsNullOrWhiteSpace(hostPart) ? null : Uri.UnescapeDataString(hostPart.Trim('/'));
            }
            catch
            {
                return null;
            }
        }
    }
}
