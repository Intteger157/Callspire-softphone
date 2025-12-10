using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Softphone
{
    public class CallHistoryService
    {
        private const string HistoryFileName = "call_history.json";
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

        private List<CallHistoryItem> LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryFileName))
                {
                    string json = File.ReadAllText(HistoryFileName);
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
                File.WriteAllText(HistoryFileName, json);
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

