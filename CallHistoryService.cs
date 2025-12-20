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
            var call = _history.FirstOrDefault(x => x.PhoneNumber == phoneNumber && 
                Math.Abs((x.CallTime - callTime).TotalSeconds) < 5);
            
            if (call != null)
            {
                call.Status = status;
                call.Duration = duration;
                call.ErrorMessage = errorMessage;
                SaveHistory();
            }
        }

        public void UpdateCallDetails(string phoneNumber, DateTime callTime, 
            DateTime? ringbackStartTime = null, DateTime? ringbackEndTime = null, 
            DateTime? answerTime = null, bool wasAnswered = false, 
            CallEndedBy endedBy = CallEndedBy.Unknown,
            List<string>? technicalDetails = null, TimeSpan? duration = null, string? recordingFilePath = null,
            CallTransport? transport = null, string? sipCallId = null, string? webRtcSessionId = null)
        {
            // Увеличиваем допуск до 30 секунд, чтобы учесть возможные задержки
            var call = _history.FirstOrDefault(x => x.PhoneNumber == phoneNumber && 
                Math.Abs((x.CallTime - callTime).TotalSeconds) < 30);
            
            if (call != null)
            {
                MainWindow.Log($"[CallHistoryService] UpdateCallDetails: Found call for {phoneNumber} at {callTime:HH:mm:ss.fff}, updating details...");
                
                if (ringbackStartTime.HasValue)
                    call.RingbackStartTime = ringbackStartTime;
                if (ringbackEndTime.HasValue)
                    call.RingbackEndTime = ringbackEndTime;
                if (answerTime.HasValue)
                    call.AnswerTime = answerTime;
                
                // Обновляем WasAnswered только если передано true (чтобы не перезаписывать true на false)
                if (wasAnswered)
                    call.WasAnswered = true;
                    
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
                
                // Обновляем статус на основе WasAnswered и Duration
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
            return _history.FirstOrDefault(x => x.PhoneNumber == phoneNumber && 
                Math.Abs((x.CallTime - callTime).TotalSeconds) < 5);
        }

        private List<CallHistoryItem> LoadHistory()
        {
            try
            {
                string historyFilePath = AppDataHelper.GetCallHistoryFilePath();
                if (File.Exists(historyFilePath))
                {
                    string json = File.ReadAllText(historyFilePath);
                    var history = JsonConvert.DeserializeObject<List<CallHistoryItem>>(json);
                    return history ?? new List<CallHistoryItem>();
                }
            }
            catch
            {
                // Если не удалось загрузить, возвращаем пустой список
            }
            return new List<CallHistoryItem>();
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
    }
}

