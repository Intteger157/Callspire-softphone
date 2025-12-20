using System;
using System.IO;

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
                        MainWindow.Log($"[AppDataHelper] Created app data directory: {_appDataPath}");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[AppDataHelper] ERROR: Failed to get/create app data directory: {ex.Message}");
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
                    MainWindow.Log($"[AppDataHelper] Created recordings directory: {recordingsPath}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AppDataHelper] ERROR: Failed to create recordings directory: {ex.Message}");
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
                    MainWindow.Log($"[AppDataHelper] Created logs directory: {logsPath}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AppDataHelper] ERROR: Failed to create logs directory: {ex.Message}");
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
                    MainWindow.Log($"[AppDataHelper] Created cache directory: {cachePath}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AppDataHelper] ERROR: Failed to create cache directory: {ex.Message}");
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
                    MainWindow.Log($"[AppDataHelper] Created updates directory: {updatesPath}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AppDataHelper] ERROR: Failed to create updates directory: {ex.Message}");
            }

            return updatesPath;
        }
    }
}


