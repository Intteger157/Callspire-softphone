using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Softphone
{
    /// <summary>
    /// Централизованный класс для работы с папкой приложения в %LOCALAPPDATA%\Callspire
    /// Обеспечивает единую точку доступа к путям для логов, записей, настроек и кеша
    /// </summary>
    public static class AppDataHelper
    {
        private static string? _appDataPath;
        private static readonly object _lock = new object();
        private static readonly object _settingsFileLock = new object();
        private static DateTime _lastCleanupCheck = DateTime.MinValue;
        private static readonly TimeSpan CleanupCheckInterval = TimeSpan.FromHours(24); // Проверяем раз в день

        /// <summary>
        /// Получает базовую папку приложения: %LOCALAPPDATA%\Callspire
        /// Создает папку автоматически, если она не существует
        /// </summary>
        public static string GetAppDataPath()
        {
            if (_appDataPath != null)
            {
                return _appDataPath;
            }

            lock (_lock)
            {
                if (_appDataPath != null)
                {
                    return _appDataPath;
                }

                try
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    _appDataPath = Path.Combine(localAppData, "Callspire");

                    // Создаем папку, если она не существует
                    if (!Directory.Exists(_appDataPath))
                    {
                        Directory.CreateDirectory(_appDataPath);
                        AppLog.Log($"[AppDataHelper] Created app data directory: {_appDataPath}");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[AppDataHelper] ERROR: Failed to get/create app data directory: {ex.Message}");
                    // Fallback на текущую директорию приложения (для совместимости)
                    _appDataPath = AppDomain.CurrentDomain.BaseDirectory;
                }
            }

            return _appDataPath;
        }

        /// <summary>
        /// Получает путь к файлу настроек: %LOCALAPPDATA%\Callspire\settings.json
        /// </summary>
        public static string GetSettingsFilePath()
        {
            return Path.Combine(GetAppDataPath(), "settings.json");
        }

        /// <summary>Thread-safe load of settings.json (returns empty defaults if missing).</summary>
        public static AppSettings LoadSettingsOrNew()
        {
            lock (_settingsFileLock)
            {
                string path = GetSettingsFilePath();
                if (!File.Exists(path))
                    return new AppSettings();

                try
                {
                    string json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[AppDataHelper] Failed to load settings: {ex.Message}");
                    return new AppSettings();
                }
            }
        }

        /// <summary>Thread-safe atomic write of settings.json.</summary>
        public static void SaveSettings(AppSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            lock (_settingsFileLock)
            {
                string path = GetSettingsFilePath();
                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
            }
        }

        /// <summary>Persist Kommo source choice: "local" or "gateway".</summary>
        public static void SetKommoConnectionSource(string source)
        {
            var normalized = (source ?? "").Trim().ToLowerInvariant();
            if (normalized is not ("local" or "gateway"))
                return;

            var settings = LoadSettingsOrNew();
            settings.AmoCrmConnectionSource = normalized;
            SaveSettings(settings);
            AppLog.Log($"[AppDataHelper] AmoCrmConnectionSource saved: {normalized}");
        }

        /// <summary>
        /// Получает путь к файлу истории звонков: %LOCALAPPDATA%\Callspire\call_history.json
        /// </summary>
        public static string GetCallHistoryFilePath()
        {
            return Path.Combine(GetAppDataPath(), "call_history.json");
        }

        /// <summary>
        /// Получает путь к папке записей звонков: %LOCALAPPDATA%\Callspire\Recordings
        /// Создает папку автоматически, если она не существует
        /// </summary>
        public static string GetRecordingsDirectory()
        {
            string recordingsPath = Path.Combine(GetAppDataPath(), "Recordings");
            
            try
            {
                if (!Directory.Exists(recordingsPath))
                {
                    Directory.CreateDirectory(recordingsPath);
                    AppLog.Log($"[AppDataHelper] Created recordings directory: {recordingsPath}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[AppDataHelper] ERROR: Failed to create recordings directory: {ex.Message}");
            }

            return recordingsPath;
        }

        /// <summary>
        /// Получает путь к папке логов: %LOCALAPPDATA%\Callspire\Logs
        /// Создает папку автоматически, если она не существует
        /// </summary>
        public static string GetLogsDirectory()
        {
            string logsPath = Path.Combine(GetAppDataPath(), "Logs");
            
            try
            {
                if (!Directory.Exists(logsPath))
                {
                    Directory.CreateDirectory(logsPath);
                    AppLog.Log($"[AppDataHelper] Created logs directory: {logsPath}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[AppDataHelper] ERROR: Failed to create logs directory: {ex.Message}");
            }

            return logsPath;
        }

        /// <summary>
        /// Получает путь к папке кеша: %LOCALAPPDATA%\Callspire\Cache
        /// Создает папку автоматически, если она не существует
        /// </summary>
        public static string GetCacheDirectory()
        {
            string cachePath = Path.Combine(GetAppDataPath(), "Cache");
            
            try
            {
                if (!Directory.Exists(cachePath))
                {
                    Directory.CreateDirectory(cachePath);
                    AppLog.Log($"[AppDataHelper] Created cache directory: {cachePath}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[AppDataHelper] ERROR: Failed to create cache directory: {ex.Message}");
            }

            return cachePath;
        }

        /// <summary>
        /// Gets the updates download directory: %LOCALAPPDATA%\Callspire\Updates
        /// Creates the directory if it does not exist.
        /// </summary>
        public static string GetUpdatesDirectory()
        {
            string updatesPath = Path.Combine(GetAppDataPath(), "Updates");

            try
            {
                if (!Directory.Exists(updatesPath))
                {
                    Directory.CreateDirectory(updatesPath);
                    AppLog.Log($"[AppDataHelper] Created updates directory: {updatesPath}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[AppDataHelper] ERROR: Failed to create updates directory: {ex.Message}");
            }

            return updatesPath;
        }

        /// <summary>
        /// Очищает старые файлы записей (старше 7 дней) из папки Recordings
        /// Вызывается автоматически при старте приложения и периодически (раз в день)
        /// </summary>
        public static void CleanupOldRecordings(int retentionDays = 7)
        {
            try
            {
                // Проверяем, не выполнялась ли очистка недавно (чтобы не делать это слишком часто)
                if (_lastCleanupCheck != DateTime.MinValue && 
                    DateTime.UtcNow - _lastCleanupCheck < CleanupCheckInterval)
                {
                    return; // Пропускаем, если недавно уже проверяли
                }

                _lastCleanupCheck = DateTime.UtcNow;

                string recordingsPath = GetRecordingsDirectory();
                if (!Directory.Exists(recordingsPath))
                {
                    return; // Папка не существует, нечего очищать
                }

                DateTime cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
                var filesToDelete = Directory.GetFiles(recordingsPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(file =>
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);
                            // Проверяем время последнего изменения (LastWriteTimeUtc)
                            return fileInfo.LastWriteTimeUtc < cutoffDate;
                        }
                        catch
                        {
                            return false; // Пропускаем файлы, к которым нет доступа
                        }
                    })
                    .ToList();

                if (filesToDelete.Count == 0)
                {
                    AppLog.Log($"[AppDataHelper] CleanupOldRecordings: No files older than {retentionDays} days found");
                    return;
                }

                int deletedCount = 0;
                long totalSizeDeleted = 0;

                foreach (string filePath in filesToDelete)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        long fileSize = fileInfo.Length;
                        
                        File.Delete(filePath);
                        deletedCount++;
                        totalSizeDeleted += fileSize;
                        
                        AppLog.Log($"[AppDataHelper] CleanupOldRecordings: Deleted old recording: {Path.GetFileName(filePath)} (age: {(DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalDays:F1} days)");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log($"[AppDataHelper] CleanupOldRecordings: Failed to delete {Path.GetFileName(filePath)}: {ex.Message}");
                    }
                }

                if (deletedCount > 0)
                {
                    AppLog.Log($"[AppDataHelper] CleanupOldRecordings: ✅ Cleanup completed - deleted {deletedCount} file(s), freed {totalSizeDeleted / 1024 / 1024:F2} MB");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[AppDataHelper] CleanupOldRecordings: ERROR during cleanup: {ex.Message}");
            }
        }
    }
}


