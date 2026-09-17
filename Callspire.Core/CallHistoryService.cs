using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Softphone
{
    public class CallHistoryService
    {
        private List<CallHistoryItem> _history;

        public CallHistoryService()
        {
            _history = LoadHistory();
        }

        /// <summary>
        /// Finds the history row for an in-flight update. Uses a wide time window and, when several
        /// rows match (same number, close timestamps), prefers Calling/Connected so we do not leave
        /// duplicate "Calling..." rows stuck when <see cref="UpdateCallStatus"/> used to match only ±5s.
        /// </summary>
        private CallHistoryItem? FindMatchingCall(string phoneNumber, DateTime callTime, CallTransport? transport, bool preferInProgress)
        {
            const double primaryWindowSec = 180;
            IEnumerable<CallHistoryItem> inWindow = _history.Where(x =>
                x.PhoneNumber == phoneNumber &&
                (!transport.HasValue || x.Transport == transport.Value) &&
                Math.Abs((x.CallTime - callTime).TotalSeconds) <= primaryWindowSec);

            var list = inWindow.ToList();
            if (list.Count == 0)
            {
                return _history.FirstOrDefault(x =>
                    x.PhoneNumber == phoneNumber &&
                    (!transport.HasValue || x.Transport == transport.Value) &&
                    Math.Abs((x.CallTime - callTime).TotalSeconds) <= 600);
            }

            if (!preferInProgress)
                return list.OrderBy(x => Math.Abs((x.CallTime - callTime).TotalSeconds)).First();

            // Среди активных звонков берём ближайший по CallTime — иначе второй звонок на тот же
            // номер «перетягивает» обновления первого (AnswerTime/WasAnswered попадают не в ту строку).
            var inProgress = list
                .Where(x => x.Status == CallStatus.Calling || x.Status == CallStatus.Connected)
                .ToList();
            if (inProgress.Count > 0)
            {
                return inProgress
                    .OrderBy(x => Math.Abs((x.CallTime - callTime).TotalSeconds))
                    .First();
            }

            return list
                .OrderBy(x => Math.Abs((x.CallTime - callTime).TotalSeconds))
                .First();
        }

        public List<CallHistoryItem> GetHistory()
        {
            return _history.OrderByDescending(x => x.CallTime).ToList();
        }

        public void AddCall(CallHistoryItem call)
        {
            _history.Add(call);
            SaveHistory();
            AppLog.Log($"[CallHistoryService] AddCall: PhoneNumber={call.PhoneNumber}, CallTime={call.CallTime:HH:mm:ss.fff}, Status={call.Status}, Transport={call.Transport}, IsIncoming={call.IsIncoming}");
        }

        public void UpdateCallStatus(string phoneNumber, DateTime callTime, CallStatus status, TimeSpan? duration = null, string? errorMessage = null)
        {
            var call = FindMatchingCall(phoneNumber, callTime, transport: null, preferInProgress: true);

            if (call != null)
            {
                call.Status = status;
                if (duration.HasValue)
                    call.Duration = duration;
                if (errorMessage != null)
                    call.ErrorMessage = errorMessage;
                SaveHistory();
            }
            else
            {
                AppLog.Log($"[CallHistoryService] UpdateCallStatus: no row for {phoneNumber} near {callTime:O} (status={status})");
            }
        }

        public void UpdateCallDetails(string phoneNumber, DateTime callTime, 
            DateTime? ringbackStartTime = null, DateTime? ringbackEndTime = null, 
            DateTime? answerTime = null, bool wasAnswered = false, 
            CallEndedBy endedBy = CallEndedBy.Unknown,
            List<string>? technicalDetails = null, TimeSpan? duration = null, string? recordingFilePath = null,
            CallTransport? transport = null, string? sipCallId = null, string? webRtcSessionId = null,
            string? outboundCallerId = null, CallConnectionSlot? connectionSlot = null,
            int? inboundRtpPackets = null)
        {
            var call = FindMatchingCall(phoneNumber, callTime, transport, preferInProgress: true);

            if (call != null)
            {
                AppLog.Log($"[CallHistoryService] UpdateCallDetails: Found call for {phoneNumber} at {callTime:HH:mm:ss.fff}, updating details...");
                
                if (ringbackStartTime.HasValue)
                    call.RingbackStartTime = ringbackStartTime;
                if (ringbackEndTime.HasValue)
                    call.RingbackEndTime = ringbackEndTime;
                if (answerTime.HasValue)
                    call.AnswerTime = answerTime;
                
                // Обновляем WasAnswered: если передано true, всегда устанавливаем true
                // Если передано false, НЕ перезаписываем существующее true (чтобы не потерять информацию о принятом звонке)
                if (wasAnswered)
                {
                    call.WasAnswered = true;
                }
                // Если wasAnswered=false, но AnswerTime установлен, значит звонок был принят
                else if (answerTime.HasValue && !call.WasAnswered)
                {
                    call.WasAnswered = true;
                }
                    
                if (endedBy != CallEndedBy.Unknown)
                    call.EndedBy = endedBy;
                if (duration.HasValue)
                    call.Duration = duration;
                else if (endedBy != CallEndedBy.Unknown && call.WasAnswered && !call.Duration.HasValue)
                {
                    var talkStart = answerTime ?? call.AnswerTime;
                    if (talkStart.HasValue)
                    {
                        var inferred = DateTime.Now - talkStart.Value;
                        if (inferred.TotalSeconds >= 0)
                            call.Duration = inferred;
                    }
                }
                    
                // ВАЖНО: null = не трогаем путь; "" = явно сбросить (SIP запись не создала WAV).
                // Непустая строка = установить/обновить путь.
                if (recordingFilePath != null)
                {
                    if (recordingFilePath.Length == 0)
                    {
                        if (!string.IsNullOrEmpty(call.RecordingFilePath))
                        {
                            AppLog.Log($"[CallHistoryService] UpdateCallDetails: Clearing recording file path for {phoneNumber} at {callTime:HH:mm:ss.fff} (no output file).");
                            call.RecordingFilePath = null;
                        }
                    }
                    else
                    {
                        string? oldPath = call.RecordingFilePath;
                        call.RecordingFilePath = recordingFilePath;
                        if (oldPath != recordingFilePath)
                        {
                            AppLog.Log($"[CallHistoryService] UpdateCallDetails: Recording file path updated for {phoneNumber} at {callTime:HH:mm:ss.fff}: {oldPath} -> {recordingFilePath}");
                        }
                        else
                        {
                            AppLog.Log($"[CallHistoryService] UpdateCallDetails: Recording file path already set for {phoneNumber} at {callTime:HH:mm:ss.fff}: {recordingFilePath}");
                        }
                    }
                }
                
                // Обновляем транспорт и идентификаторы, если они переданы
                if (transport.HasValue)
                    call.Transport = transport.Value;
                if (!string.IsNullOrEmpty(sipCallId))
                    call.SipCallId = sipCallId;
                if (!string.IsNullOrEmpty(webRtcSessionId))
                    call.WebRtcSessionId = webRtcSessionId;
                if (!string.IsNullOrEmpty(outboundCallerId))
                    call.OutboundCallerId = outboundCallerId;
                if (connectionSlot.HasValue && connectionSlot.Value != CallConnectionSlot.Unknown)
                    call.ConnectionSlot = connectionSlot.Value;
                if (inboundRtpPackets.HasValue)
                    call.InboundRtpPackets = inboundRtpPackets.Value;
                
                // Обновляем статус на основе WasAnswered, Duration и EndedBy
                bool inProgressStatus = call.Status == CallStatus.Calling || call.Status == CallStatus.Connected;

                // Завершённый разговор: SIP исходящие часто остаются в Connected ("In call…") до SendCallDetails.
                if (endedBy != CallEndedBy.Unknown && call.WasAnswered &&
                    (inProgressStatus || call.Status == CallStatus.Cancelled))
                {
                    call.Status = CallStatus.Ended;
                }
                // Если звонок был принят и есть длительность, статус должен быть Ended (не Cancelled или Calling)
                else if (call.WasAnswered && call.Duration.HasValue &&
                    (inProgressStatus || call.Status == CallStatus.Cancelled))
                {
                    call.Status = CallStatus.Ended;
                }
                // Если звонок был принят, но статус ещё in-progress — Ended (даже без Duration)
                else if (call.WasAnswered && inProgressStatus)
                {
                    call.Status = CallStatus.Ended;
                }
                // Если звонок НЕ был принят и был завершен локальным пользователем - это отмена
                else if (!call.WasAnswered && endedBy == CallEndedBy.LocalUser && inProgressStatus)
                {
                    call.Status = CallStatus.Cancelled;
                    AppLog.Log($"[CallHistoryService] UpdateCallDetails: Call cancelled by local user (not answered), updating status to Cancelled");
                }
                // Если звонок НЕ был принят и был завершен удаленной стороной - это может быть Failed или Cancelled
                else if (!call.WasAnswered && endedBy == CallEndedBy.RemoteParty && inProgressStatus)
                {
                    // Если есть длительность (звонок длился какое-то время), это Failed, иначе Cancelled
                    call.Status = duration.HasValue && duration.Value.TotalSeconds > 1 ? CallStatus.Failed : CallStatus.Cancelled;
                    AppLog.Log($"[CallHistoryService] UpdateCallDetails: Call ended by remote party (not answered), updating status to {call.Status}");
                }
                
                if (technicalDetails != null && technicalDetails.Count > 0)
                {
                    // Добавляем только новые детали, которых еще нет в списке
                    foreach (var detail in technicalDetails)
                    {
                        if (!call.TechnicalDetails.Contains(detail))
                        {
                            call.TechnicalDetails.Add(detail);
                        }
                    }
                }
                SaveHistory();
            }
            else
            {
                // Логируем, если звонок не найден, с информацией о доступных звонках для диагностики
                var recentCalls = _history.Where(x => x.PhoneNumber == phoneNumber)
                    .OrderByDescending(x => x.CallTime)
                    .Take(5)
                    .Select(x => $"{x.CallTime:HH:mm:ss.fff} (diff: {Math.Abs((x.CallTime - callTime).TotalSeconds):F1}s)")
                    .ToList();
                
                AppLog.Log($"[CallHistoryService] UpdateCallDetails: Call not found for PhoneNumber={phoneNumber}, CallTime={callTime:HH:mm:ss.fff}");
                if (recentCalls.Any())
                {
                    AppLog.Log($"[CallHistoryService] Recent calls for {phoneNumber}: {string.Join(", ", recentCalls)}");
                }
                else
                {
                    AppLog.Log($"[CallHistoryService] No calls found in history for {phoneNumber} (total calls in history: {_history.Count})");
                }
            }
        }

        public CallHistoryItem? GetCall(string phoneNumber, DateTime callTime)
        {
            return FindMatchingCall(phoneNumber, callTime, transport: null, preferInProgress: false);
        }

        /// <summary>
        /// Resolves the history row for an AmoCRM upload job. Prefers session id when available,
        /// otherwise the closest CallTime within <paramref name="maxDiffSeconds"/>.
        /// Does not fall back to "latest call to this number" — wrong row = wrong duration/recording.
        /// </summary>
        public CallHistoryItem? GetCallForAmoCrmUpload(string phoneNumber, DateTime callTime, string? sessionId = null, double maxDiffSeconds = 3)
        {
            var candidates = _history.Where(x => x.PhoneNumber == phoneNumber).ToList();
            if (candidates.Count == 0)
                return null;

            if (!string.IsNullOrEmpty(sessionId))
            {
                var bySession = candidates.FirstOrDefault(x =>
                    string.Equals(x.SipCallId, sessionId, StringComparison.Ordinal)
                    || string.Equals(x.WebRtcSessionId, sessionId, StringComparison.Ordinal));
                if (bySession != null)
                    return bySession;
            }

            return candidates
                .Where(x => Math.Abs((x.CallTime - callTime).TotalSeconds) <= maxDiffSeconds)
                .OrderBy(x => Math.Abs((x.CallTime - callTime).TotalSeconds))
                .FirstOrDefault();
        }

        /// <summary>
        /// Обновляет ID лида AmoCRM для звонка
        /// </summary>
        public void UpdateAmoCrmLeadId(string phoneNumber, DateTime callTime, long? leadId)
        {
            var call = FindMatchingCall(phoneNumber, callTime, transport: null, preferInProgress: false);

            if (call != null)
            {
                call.AmoCrmLeadId = leadId;
                SaveHistory();
                AppLog.Log($"[CallHistoryService] AmoCrmLeadId updated for {phoneNumber} at {callTime:HH:mm:ss.fff}: {leadId?.ToString() ?? "null"}");
            }
            else
            {
                AppLog.Log($"[CallHistoryService] UpdateAmoCrmLeadId: no row for {phoneNumber} near {callTime:O}");
            }
        }

        public void UpdateOutboundCallerId(string phoneNumber, DateTime callTime, string callerId)
        {
            var call = FindMatchingCall(phoneNumber, callTime, transport: null, preferInProgress: false);

            if (call != null)
            {
                call.OutboundCallerId = callerId;
                SaveHistory();
                AppLog.Log($"[CallHistoryService] OutboundCallerId updated for {phoneNumber} at {callTime:HH:mm:ss.fff}: {callerId}");
            }
            else
            {
                AppLog.Log($"[CallHistoryService] UpdateOutboundCallerId: no row for {phoneNumber} near {callTime:O}");
            }
        }

        public void UpdateRecordingFilePath(string phoneNumber, DateTime callTime, string recordingFilePath)
        {
            var call = FindMatchingCall(phoneNumber, callTime, transport: null, preferInProgress: false);

            if (call != null)
            {
                call.RecordingFilePath = recordingFilePath;
                SaveHistory();
                AppLog.Log($"[CallHistoryService] RecordingFilePath updated for {phoneNumber} at {callTime:HH:mm:ss.fff}: {recordingFilePath}");
            }
            else
            {
                AppLog.Log($"[CallHistoryService] UpdateRecordingFilePath: no row for {phoneNumber} near {callTime:O}");
            }
        }

        /// <summary>
        /// Обновляет статус загрузки записи в AmoCRM для звонка
        /// </summary>
        public void UpdateAmoCrmUploadStatus(
            string phoneNumber,
            DateTime callTime,
            AmoCrmUploadStatus status,
            string? reason = null,
            AmoCrmUploadedRecordingSource? uploadedRecordingSource = null)
        {
            var call = FindMatchingCall(phoneNumber, callTime, transport: null, preferInProgress: false);

            if (call != null)
            {
                call.AmoCrmUploadStatus = status;
                call.AmoCrmUploadReason = reason;
                if (uploadedRecordingSource.HasValue)
                    call.AmoCrmUploadedRecordingSource = uploadedRecordingSource.Value;
                else if (status != AmoCrmUploadStatus.Uploaded)
                    call.AmoCrmUploadedRecordingSource = AmoCrmUploadedRecordingSource.None;
                SaveHistory();
                AppLog.Log($"[CallHistoryService] AmoCrmUploadStatus updated for {phoneNumber} at {callTime:HH:mm:ss.fff}: {status}" +
                    (string.IsNullOrEmpty(reason) ? "" : $" ({reason})") +
                    (uploadedRecordingSource.HasValue ? $", recordingSource={uploadedRecordingSource.Value}" : ""));
            }
            else
            {
                AppLog.Log($"[CallHistoryService] UpdateAmoCrmUploadStatus: no row for {phoneNumber} near {callTime:O} (status={status})");
            }
        }

        /// <summary>
        /// Applies PBX CDR truth from gateway process-call job status (optional fields on GET /api/kommo/process-call/{id}).
        /// Fixes desktop "Hung up before answer" when Miko trunk was ANSWERED (robot/voicemail) but WebRTC did not detect B-leg.
        /// </summary>
        public bool ApplyPbxTruthFromGateway(
            string phoneNumber,
            DateTime callTime,
            bool? pbxWasAnswered,
            int? pbxDurationSeconds,
            string? sessionId = null,
            double maxDiffSeconds = 5)
        {
            if (pbxWasAnswered != true && (pbxDurationSeconds ?? 0) <= 0)
                return false;

            var call = GetCallForAmoCrmUpload(phoneNumber, callTime, sessionId, maxDiffSeconds)
                ?? FindMatchingCall(phoneNumber, callTime, transport: null, preferInProgress: false);
            if (call == null)
            {
                AppLog.Log($"[CallHistoryService] ApplyPbxTruthFromGateway: no row for {phoneNumber} near {callTime:O}");
                return false;
            }

            bool changed = false;

            if (pbxWasAnswered == true && !call.WasAnswered)
            {
                call.WasAnswered = true;
                changed = true;
            }

            if (pbxDurationSeconds.HasValue && pbxDurationSeconds.Value > 0)
            {
                var pbxDuration = TimeSpan.FromSeconds(pbxDurationSeconds.Value);
                if (!call.Duration.HasValue
                    || Math.Abs(call.Duration.Value.TotalSeconds - pbxDurationSeconds.Value) > 2)
                {
                    call.Duration = pbxDuration;
                    changed = true;
                }
            }

            if (call.WasAnswered && call.Status == CallStatus.Cancelled)
            {
                call.Status = CallStatus.Ended;
                changed = true;
            }

            if (call.WasAnswered && !call.AnswerTime.HasValue && call.RingbackEndTime.HasValue)
            {
                call.AnswerTime = call.RingbackEndTime;
                changed = true;
            }

            if (changed)
            {
                const string detailPrefix = "PBX CDR status sync:";
                string detail = $"{detailPrefix} answered={pbxWasAnswered}, duration={pbxDurationSeconds}s";
                if (!call.TechnicalDetails.Any(d => d.StartsWith(detailPrefix, StringComparison.Ordinal)))
                    call.TechnicalDetails.Add(detail);
                SaveHistory();
                AppLog.Log($"[CallHistoryService] ApplyPbxTruthFromGateway: updated {phoneNumber} at {callTime:HH:mm:ss.fff} " +
                    $"(WasAnswered={call.WasAnswered}, Status={call.Status}, Duration={call.Duration})");
            }

            return changed;
        }

        private List<CallHistoryItem> LoadHistory()
        {
            try
            {
                string historyFilePath = AppDataHelper.GetCallHistoryFilePath();
                if (File.Exists(historyFilePath))
                {
                    string json = File.ReadAllText(historyFilePath);
                    var history = JsonConvert.DeserializeObject<List<CallHistoryItem>>(json) ?? new List<CallHistoryItem>();
                    if (RepairStaleCallingEntries(history, out int repaired))
                    {
                        try
                        {
                            string outJson = JsonConvert.SerializeObject(history, Formatting.Indented);
                            File.WriteAllText(historyFilePath, outJson);
                            AppLog.Log($"[CallHistoryService] Repaired {repaired} stale Calling entr(y/ies) on load");
                        }
                        catch { /* ignore */ }
                    }
                    if (RepairMissingTalkDuration(history, out int durationRepaired))
                    {
                        try
                        {
                            string outJson = JsonConvert.SerializeObject(history, Formatting.Indented);
                            File.WriteAllText(historyFilePath, outJson);
                            AppLog.Log($"[CallHistoryService] Repaired talk duration on {durationRepaired} entr(y/ies) on load");
                        }
                        catch { /* ignore */ }
                    }
                    return history;
                }
            }
            catch
            {
                // Если не удалось загрузить, возвращаем пустой список
            }
            return new List<CallHistoryItem>();
        }

        /// <summary>
        /// Rows stuck in Calling/Connected long after start (e.g. SIP Connected never finalized, or WebRTC closed without SendCallDetails).
        /// </summary>
        private static bool RepairStaleCallingEntries(List<CallHistoryItem> list, out int repairedCount)
        {
            repairedCount = 0;
            var now = DateTime.Now;
            foreach (var c in list)
            {
                if (c.Status != CallStatus.Calling && c.Status != CallStatus.Connected) continue;

                // History is loaded only at app start — any Connected row is stale.
                if (c.Status == CallStatus.Connected)
                {
                    if (c.WasAnswered || c.AnswerTime.HasValue || c.Duration.HasValue)
                        c.Status = CallStatus.Ended;
                    else
                        c.Status = CallStatus.Cancelled;
                    repairedCount++;
                    continue;
                }

                bool hasCompletionEvidence = c.WasAnswered || c.Duration.HasValue || c.AnswerTime.HasValue
                    || c.EndedBy != CallEndedBy.Unknown;
                if (!hasCompletionEvidence && (now - c.CallTime).TotalMinutes <= 30) continue;

                if (c.WasAnswered || c.Duration.HasValue || c.AnswerTime.HasValue)
                {
                    c.Status = CallStatus.Ended;
                    if (!c.Duration.HasValue && c.AnswerTime.HasValue)
                        c.Duration = now - c.AnswerTime.Value;
                }
                else
                    c.Status = CallStatus.Cancelled;

                repairedCount++;
            }

            return repairedCount > 0;
        }

        /// <summary>
        /// Ended answered calls that lost Duration (e.g. Connected status update cleared it).
        /// Uses recording file length when available.
        /// </summary>
        private static bool RepairMissingTalkDuration(List<CallHistoryItem> list, out int repairedCount)
        {
            repairedCount = 0;
            foreach (var c in list)
            {
                if (c.Status != CallStatus.Ended) continue;
                if (c.Duration.HasValue && c.Duration.Value.TotalSeconds > 0) continue;
                if (!c.WasAnswered && !c.AnswerTime.HasValue) continue;

                var talk = CallStatisticsService.GetEffectiveTalkDuration(c);
                if (talk.TotalSeconds <= 0) continue;

                c.Duration = talk;
                repairedCount++;
            }

            return repairedCount > 0;
        }

        private void SaveHistory()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_history, Formatting.Indented);
                string historyFilePath = AppDataHelper.GetCallHistoryFilePath();
                File.WriteAllText(historyFilePath, json);
            }
            catch
            {
                // Игнорируем ошибки сохранения
            }
        }

        public void ClearHistory()
        {
            _history.Clear();
            SaveHistory();
        }

        /// <summary>
        /// Удаляет записи истории старше указанного количества дней.
        /// Возвращает количество удалённых записей.
        /// </summary>
        public int CleanupOldHistory(int retentionDays)
        {
            try
            {
                if (retentionDays <= 0)
                {
                    return 0;
                }

                DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
                int beforeCount = _history.Count;

                _history = _history
                    .Where(c => c.CallTime >= cutoff)
                    .ToList();

                int removed = beforeCount - _history.Count;

                if (removed > 0)
                {
                    SaveHistory();
                    AppLog.Log($"[CallHistoryService] CleanupOldHistory: removed {removed} call(s) older than {retentionDays} days");
                }
                else
                {
                    AppLog.Log($"[CallHistoryService] CleanupOldHistory: no calls older than {retentionDays} days found");
                }

                return removed;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[CallHistoryService] CleanupOldHistory error: {ex.Message}");
                return 0;
            }
        }
    }
}

