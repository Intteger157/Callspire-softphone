using System;
using System.IO;
using System.Threading.Tasks;

namespace Softphone
{
    /// <summary>
    /// Базовый класс для рекордеров звонков с общей логикой
    /// </summary>
    public abstract class CallRecorderBase : IDisposable
    {
        protected bool _isRecording = false;
        protected readonly object _lockObject = new object();
        protected string? _recordingFilePath;
        protected string? _recordingsDirectory;

        public bool IsRecording => _isRecording;
        public string? RecordingFilePath => _recordingFilePath;

        /// <summary>
        /// Начинает запись звонка
        /// </summary>
        public virtual void StartRecording(string phoneNumber, DateTime callStartTime, string recordingsDirectory)
        {
            lock (_lockObject)
            {
                if (_isRecording)
                {
                    return;
                }

                try
                {
                    if (!Directory.Exists(recordingsDirectory))
                    {
                        Directory.CreateDirectory(recordingsDirectory);
                    }

                    _recordingsDirectory = recordingsDirectory;
                    
                    // Генерируем базовое имя файла
                    string baseFileName = GenerateBaseFileName(phoneNumber, callStartTime);
                    
                    // Генерируем пути к файлам (переопределяется в наследниках)
                    GenerateRecordingPaths(baseFileName, recordingsDirectory);
                    
                    _isRecording = true;
                    AppLog.Log($"[{GetLogPrefix()}] Recording started: {Path.GetFileName(_recordingFilePath)}");
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[{GetLogPrefix()}] Error starting recording: {ex.Message}");
                    _isRecording = false;
                    _recordingFilePath = null;
                }
            }
        }

        /// <summary>
        /// Останавливает запись звонка
        /// </summary>
        public abstract Task StopRecordingAsync(DateTime? callEndTime = null);

        /// <summary>
        /// Синхронная версия остановки записи
        /// </summary>
        public virtual void StopRecording(DateTime? callEndTime = null)
        {
            StopRecordingAsync(callEndTime).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Генерирует базовое имя файла для записи
        /// </summary>
        protected virtual string GenerateBaseFileName(string phoneNumber, DateTime callStartTime)
        {
            return $"{phoneNumber}_{callStartTime:yyyyMMdd_HHmmss}";
        }

        /// <summary>
        /// Генерирует пути к файлам записи (переопределяется в наследниках)
        /// </summary>
        protected abstract void GenerateRecordingPaths(string baseFileName, string recordingsDirectory);

        /// <summary>
        /// Генерирует уникальный путь к файлу, добавляя суффикс если файл уже существует
        /// </summary>
        protected string GenerateUniqueFilePath(string basePath, Func<string, bool> fileExistsCheck)
        {
            string path = basePath;
            int suffix = 1;
            
            while (fileExistsCheck(path))
            {
                string directory = Path.GetDirectoryName(basePath) ?? "";
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(basePath);
                string extension = Path.GetExtension(basePath);
                path = Path.Combine(directory, $"{fileNameWithoutExt}_{suffix}{extension}");
                suffix++;
            }
            
            return path;
        }

        /// <summary>
        /// Возвращает префикс для логов (имя класса)
        /// </summary>
        protected abstract string GetLogPrefix();

        public virtual void Dispose()
        {
            try
            {
                if (!_isRecording) return;

                // Best-effort stop: don't block indefinitely in Dispose (can freeze UI during window close).
                var t = StopRecordingAsync();
                if (!t.IsCompleted)
                {
                    if (!t.Wait(TimeSpan.FromSeconds(2)))
                    {
                        // Continue in background; any errors are logged by recorder implementations.
                        _ = Task.Run(async () =>
                        {
                            try { await t; } catch { }
                        });
                    }
                }
            }
            catch
            {
                // never throw from Dispose
            }
        }
    }
}
