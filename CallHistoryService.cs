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

            return list
                .OrderBy(x => x.Status == CallStatus.Calling ? 0 : x.Status == CallStatus.Connected ? 1 : 2)
                .ThenBy(x => Math.Abs((x.CallTime - callTime).TotalSeconds))
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
            MainWindow.Log($"[CallHistoryService] AddCall: PhoneNumber={call.PhoneNumber}, CallTime={call.CallTime:HH:mm:ss.fff}, Status={call.Status}, Transport={call.Transport}, IsIncoming={call.IsIncoming}");
        }

        public void UpdateCallStatus(string phoneNumber, DateTime callTime, CallStatus status, TimeSpan? duration = null, string? errorMessage = null)
        {
            var call = FindMatchingCall(phoneNumber, callTime, transport: null, preferInProgress: true);

            if (call != null)
            {
                call.Status = status;
                call.Duration = duration;
                call.ErrorMessage = errorMessage;
                SaveHistory();
            }
            else
            {
                MainWindow.Log($"[CallHistoryService] UpdateCallStatus: no row for {phoneNumber} near {callTime:O} (status={status})");
            }
        }

        public void UpdateCallDetails(string phoneNumber, DateTime callTime, 
            DateTime? ringbackStartTime = null, DateTime? ringbackEndTime = null, 
            DateTime? answerTime = null, bool wasAnswered = false, 
            CallEndedBy endedBy = CallEndedBy.Unknown,
            List<string>? technicalDetails = null, TimeSpan? duration = null, string? recordingFilePath = null,
            CallTransport? transport = null, string? sipCallId = null, string? webRtcSessionId = null,
            string? outboundCallerId = null)
        {
            var call = FindMatchingCall(phoneNumber, callTime, transport, preferInProgress: true);

            if (call != null)
            {
                MainWindow.Log($"[CallHistoryService] UpdateCallDetails: Found call for {phoneNumber} at {callTime:HH:mm:ss.fff}, updating details...");
                
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
                    
                // ВАЖНО: Обновляем путь к записи всегда, даже если он уже был установлен (может быть обновлен после конвертации)
                if (!string.IsNullOrEmpty(recordingFilePath))
                {
                    string? oldPath = call.RecordingFilePath;
                    call.RecordingFilePath = recordingFilePath;
                    if (oldPath != recordingFilePath)
                    {
                        MainWindow.Log($"[CallHistoryService] UpdateCallDetails: Recording file path updated for {phoneNumber} at {callTime:HH:mm:ss.fff}: {oldPath} -> {recordingFilePath}");
                    }
                    else
                    {
                        MainWindow.Log($"[CallHistoryService] UpdateCallDetails: Recording file path already set for {phoneNumber} at {callTime:HH:mm:ss.fff}: {recordingFilePath}");
                    }
                }
                else
                {
                    MainWindow.Log($"[CallHistoryService] UpdateCallDetails: Recording file path is null or empty for {phoneNumber} at {callTime:HH:mm:ss.fff} (current path: {call.RecordingFilePath ?? "null"})");
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
                
                // Обновляем статус на основе WasAnswered, Duration и EndedBy
                // Если звонок был принят и есть длительность, статус должен быть Ended (не Cancelled или Calling)
                if (call.WasAnswered && call.Duration.HasValue && 
                    (call.Status == CallStatus.Calling || call.Status == CallStatus.Cancelled))
                {
                    call.Status = CallStatus.Ended;
                }
                // Если звонок был принят, но статус еще Calling, обновляем на Ended (даже без Duration)
                else if (call.WasAnswered && call.Status == CallStatus.Calling)
                {
                    call.Status = CallStatus.Ended;
                }
                // Если звонок НЕ был принят и был завершен локальным пользователем - это отмена
                else if (!call.WasAnswered && endedBy == CallEndedBy.LocalUser && call.Status == CallStatus.Calling)
                {
                    call.Status = CallStatus.Cancelled;
                    MainWindow.Log($"[CallHistoryService] UpdateCallDetails: Call cancelled by local user (not answered), updating status to Cancelled");
                }
                // Если звонок НЕ был принят и был завершен удаленной стороной - это может быть Failed или Cancelled
                else if (!call.WasAnswered && endedBy == CallEndedBy.RemoteParty && call.Status == CallStatus.Calling)
                {
                    // Если есть длительность (звонок длился какое-то время), это Failed, иначе Cancelled
                    call.Status = duration.HasValue && duration.Value.TotalSeconds > 1 ? CallStatus.Failed : CallStatus.Cancelled;
                    MainWindow.Log($"[CallHistoryService] UpdateCallDetails: Call ended by remote party (not answered), updating status to {call.Status}");
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
                
                MainWindow.Log($"[CallHistoryService] UpdateCallDetails: Call not found for PhoneNumber={phoneNumber}, CallTime={callTime:HH:mm:ss.fff}");
                if (recentCalls.Any())
                {
                    MainWindow.Log($"[CallHistoryService] Recent calls for {phoneNumber}: {string.Join(", ", recentCalls)}");
                }
                else
                {
                    MainWindow.Log($"[CallHistoryService] No calls found in history for {phoneNumber} (total calls in history: {_history.Count})");
                }
            }
        }

        public CallHistoryItem? GetCall(string phoneNumber, DateTime callTime)
        {
            return FindMatchingCall(phoneNumber, callTime, transport: null, preferInProgress: true);
        }

        /// <summary>
        /// Обновляет ID лида AmoCRM для звонка
        /// </summary>
        public void UpdateAmoCrmLeadId(string phoneNumber, DateTime callTime, long? leadId)
        {
            var call = _history.FirstOrDefault(x => x.PhoneNumber == phoneNumber && 
                Math.Abs((x.CallTime - callTime).TotalSeconds) < 30);
            
            if (call != null && leadId.HasValue)
            {
                call.AmoCrmLeadId = leadId.Value;
                SaveHistory();
                MainWindow.Log($"[CallHistoryService] AmoCrmLeadId updated for {phoneNumber} at {callTime:HH:mm:ss.fff}: {leadId}");
            }
        }

        public void UpdateOutboundCallerId(string phoneNumber, DateTime callTime, string callerId)
        {
            var call = _history.FirstOrDefault(x => x.PhoneNumber == phoneNumber &&
                Math.Abs((x.CallTime - callTime).TotalSeconds) < 30);

            if (call != null)
            {
                call.OutboundCallerId = callerId;
                SaveHistory();
                MainWindow.Log($"[CallHistoryService] OutboundCallerId updated for {phoneNumber} at {callTime:HH:mm:ss.fff}: {callerId}");
            }
        }

        public void UpdateRecordingFilePath(string phoneNumber, DateTime callTime, string recordingFilePath)
        {
            var call = _history.FirstOrDefault(x => x.PhoneNumber == phoneNumber &&
                Math.Abs((x.CallTime - callTime).TotalSeconds) < 30);

            if (call != null)
            {
                call.RecordingFilePath = recordingFilePath;
                SaveHistory();
                MainWindow.Log($"[CallHistoryService] RecordingFilePath updated for {phoneNumber} at {callTime:HH:mm:ss.fff}: {recordingFilePath}");
            }
        }

        /// <summary>
        /// Обновляет статус загрузки записи в AmoCRM для звонка
        /// </summary>
        public void UpdateAmoCrmUploadStatus(string phoneNumber, DateTime callTime, AmoCrmUploadStatus status, string? reason = null)
        {
            var call = _history.FirstOrDefault(x => x.PhoneNumber == phoneNumber && 
                Math.Abs((x.CallTime - callTime).TotalSeconds) < 30);
            
            if (call != null)
            {
                call.AmoCrmUploadStatus = status;
                call.AmoCrmUploadReason = reason;
                SaveHistory();
                MainWindow.Log($"[CallHistoryService] AmoCrmUploadStatus updated for {phoneNumber} at {callTime:HH:mm:ss.fff}: {status}" + 
                    (string.IsNullOrEmpty(reason) ? "" : $" ({reason})"));
            }
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
                            MainWindow.Log($"[CallHistoryService] Repaired {repaired} stale Calling entr(y/ies) on load");
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
        /// Rows stuck in Calling long after start (e.g. WebRTC window closed without SendCallDetails, or bad time match on save).
        /// </summary>
        private static bool RepairStaleCallingEntries(List<CallHistoryItem> list, out int repairedCount)
        {
            repairedCount = 0;
            var now = DateTime.Now;
            foreach (var c in list)
            {
                if (c.Status != CallStatus.Calling) continue;
                if ((now - c.CallTime).TotalMinutes <= 30) continue;

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
                    MainWindow.Log($"[CallHistoryService] CleanupOldHistory: removed {removed} call(s) older than {retentionDays} days");
                }
                else
                {
                    MainWindow.Log($"[CallHistoryService] CleanupOldHistory: no calls older than {retentionDays} days found");
                }

                return removed;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallHistoryService] CleanupOldHistory error: {ex.Message}");
                return 0;
            }
        }
    }
}

