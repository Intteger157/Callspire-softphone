using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Softphone
{
    /// <summary>
    /// Сервис для проверки обновлений через собственный сервер
    /// </summary>
    public class UpdateService
    {
        private static readonly HttpClient _httpClient;
        
        static UpdateService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30); // Устанавливаем таймаут один раз при создании
        }
        
        private const string UpdateServerBaseUrl = "https://callspire.update.portalhm.cc";
        private const string UpdateInfoEndpoint = "/update.json"; // или /latest.json
        private const int UpdateCheckIntervalHours = 24; // Проверка раз в 24 часа
        
        /// <summary>
        /// Получает путь к файлу с временем последней проверки обновлений
        /// </summary>
        private static string GetLastCheckFilePath()
        {
            return Path.Combine(AppDataHelper.GetAppDataPath(), "last_update_check.json");
        }
        
        /// <summary>
        /// Сохраняет время последней проверки обновлений
        /// </summary>
        private static void SaveLastCheckTime(DateTime checkTime)
        {
            try
            {
                var data = new { LastCheckTime = checkTime };
                string json = JsonConvert.SerializeObject(data);
                File.WriteAllText(GetLastCheckFilePath(), json);
                AppLog.Log($"[UpdateService] Saved last check time: {checkTime}");
            }
            catch (Exception ex)
            {
                AppLog.Log($"[UpdateService] Error saving last check time: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Получает время последней проверки обновлений
        /// </summary>
        private static DateTime? GetLastCheckTime()
        {
            try
            {
                string filePath = GetLastCheckFilePath();
                if (!File.Exists(filePath))
                {
                    return null;
                }
                
                string json = File.ReadAllText(filePath);
                var data = JsonConvert.DeserializeObject<dynamic>(json);
                if (data != null)
                {
                    var lastCheckTime = data.LastCheckTime;
                    if (lastCheckTime != null)
                    {
                        DateTime lastCheck = lastCheckTime.ToObject<DateTime>();
                        AppLog.Log($"[UpdateService] Last check time: {lastCheck}");
                        return lastCheck;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[UpdateService] Error reading last check time: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Проверяет, нужно ли выполнять проверку обновлений (прошло ли 24 часа с последней проверки)
        /// </summary>
        public static bool ShouldCheckForUpdates()
        {
            DateTime? lastCheck = GetLastCheckTime();
            
            if (lastCheck == null)
            {
                AppLog.Log("[UpdateService] No previous check found, will check for updates");
                return true; // Первая проверка
            }
            
            TimeSpan timeSinceLastCheck = DateTime.Now - lastCheck.Value;
            bool shouldCheck = timeSinceLastCheck.TotalHours >= UpdateCheckIntervalHours;
            
            if (shouldCheck)
            {
                AppLog.Log($"[UpdateService] Last check was {timeSinceLastCheck.TotalHours:F1} hours ago, will check for updates");
            }
            else
            {
                AppLog.Log($"[UpdateService] Last check was {timeSinceLastCheck.TotalHours:F1} hours ago, skipping check (check interval: {UpdateCheckIntervalHours} hours)");
            }
            
            return shouldCheck;
        }
        
        /// <summary>
        /// Получает текущую версию приложения из AssemblyInfo
        /// </summary>
        public static string GetCurrentVersion()
        {
            try
            {
                // Entry assembly = Callspire.exe (Desktop); Core alone defaults to 1.0.0.
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var versionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                
                if (versionAttribute != null && !string.IsNullOrEmpty(versionAttribute.InformationalVersion))
                {
                    // Убираем commit hash, если есть (формат: "1.0.0+commit")
                    string version = versionAttribute.InformationalVersion;
                    int plusIndex = version.IndexOf('+');
                    if (plusIndex > 0)
                    {
                        version = version.Substring(0, plusIndex);
                    }
                    return version;
                }
                
                // Fallback на AssemblyVersion
                var assemblyVersion = assembly.GetName().Version;
                if (assemblyVersion != null)
                {
                    return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[UpdateService] Error getting current version: {ex.Message}");
            }
            
            return "1.0.0"; // Fallback версия
        }
        
        /// <summary>
        /// Проверяет наличие новой версии на сервере обновлений
        /// </summary>
        /// <param name="forceCheck">Если true, проверяет независимо от времени последней проверки</param>
        /// <returns>Информация об обновлении или null, если ошибка или нет обновлений</returns>
        public static async Task<UpdateInfo?> CheckForUpdateAsync(bool forceCheck = false)
        {
            // Проверяем, нужно ли выполнять проверку (если не принудительная)
            if (!forceCheck && !ShouldCheckForUpdates())
            {
                return null;
            }
            
            try
            {
                string url = $"{UpdateServerBaseUrl}{UpdateInfoEndpoint}";
                
                AppLog.Log($"[UpdateService] Checking for updates: {url}");
                
                HttpResponseMessage response = await _httpClient.GetAsync(url);
                
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    UpdateInfo? updateInfo = null;
                    
                    try
                    {
                        updateInfo = JsonConvert.DeserializeObject<UpdateInfo>(json);
                    }
                    catch (JsonException jsonEx)
                    {
                        AppLog.Log($"[UpdateService] JSON parsing error: {jsonEx.Message}");
                        AppLog.Log($"[UpdateService] JSON content (first 500 chars): {json.Substring(0, Math.Min(500, json.Length))}");
                        AppLog.Log($"[UpdateService] Please check your update.json file on the server for syntax errors.");
                        // Не сохраняем время проверки при ошибке парсинга
                        return null;
                    }
                    
                    // Сохраняем время проверки только после успешного получения ответа
                    SaveLastCheckTime(DateTime.Now);
                    
                    if (updateInfo != null && !string.IsNullOrEmpty(updateInfo.Version))
                    {
                        AppLog.Log($"[UpdateService] Latest version found: {updateInfo.Version}");
                        
                        // Проверяем, есть ли новая версия
                        string currentVersion = GetCurrentVersion();
                        int comparison = CompareVersions(currentVersion, updateInfo.Version);
                        
                        if (comparison < 0)
                        {
                            AppLog.Log($"[UpdateService] New version available: {currentVersion} -> {updateInfo.Version}");
                            return updateInfo;
                        }
                        else
                        {
                            AppLog.Log($"[UpdateService] Application is up to date. Current: {currentVersion}, Latest: {updateInfo.Version}");
                            return null;
                        }
                    }
                    else
                    {
                        AppLog.Log("[UpdateService] Update info received but version field is empty");
                    }
                }
                else
                {
                    AppLog.Log($"[UpdateService] Server returned status: {response.StatusCode}");
                    // Не сохраняем время проверки при ошибке, чтобы можно было повторить
                }
            }
            catch (TaskCanceledException)
            {
                AppLog.Log("[UpdateService] Update check timed out");
                // Не сохраняем время проверки при таймауте
            }
            catch (HttpRequestException ex)
            {
                AppLog.Log($"[UpdateService] Network error checking for updates: {ex.Message}");
                // Не сохраняем время проверки при сетевой ошибке
            }
            catch (Exception ex)
            {
                AppLog.Log($"[UpdateService] Error checking for updates: {ex.Message}");
                // Не сохраняем время проверки при ошибке
            }
            
            return null;
        }
        
        /// <summary>
        /// Сравнивает две версии в формате "x.y.z"
        /// </summary>
        /// <returns>1 если version1 > version2, -1 если version1 < version2, 0 если равны</returns>
        public static int CompareVersions(string version1, string version2)
        {
            try
            {
                // Убираем префикс "v" если есть
                version1 = version1.TrimStart('v', 'V');
                version2 = version2.TrimStart('v', 'V');
                
                var v1Parts = version1.Split('.');
                var v2Parts = version2.Split('.');
                
                int maxLength = Math.Max(v1Parts.Length, v2Parts.Length);
                
                for (int i = 0; i < maxLength; i++)
                {
                    int v1Part = i < v1Parts.Length && int.TryParse(v1Parts[i], out int v1) ? v1 : 0;
                    int v2Part = i < v2Parts.Length && int.TryParse(v2Parts[i], out int v2) ? v2 : 0;
                    
                    if (v1Part > v2Part) return 1;
                    if (v1Part < v2Part) return -1;
                }
                
                return 0;
            }
            catch
            {
                return 0;
            }
        }
        
        /// <summary>
        /// Вычисляет SHA256 хеш файла
        /// </summary>
        public static string CalculateFileSha256(string filePath)
        {
            try
            {
                using (var sha256 = SHA256.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] hashBytes = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[UpdateService] Error calculating SHA256: {ex.Message}");
                return string.Empty;
            }
        }
        
        /// <summary>
        /// Проверяет SHA256 хеш скачанного файла
        /// </summary>
        public static bool VerifyFileHash(string filePath, string expectedHash)
        {
            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                AppLog.Log("[UpdateService] No SHA256 hash provided in update info, skipping verification");
                return true; // Если хеш не указан, пропускаем проверку
            }
            
            string actualHash = CalculateFileSha256(filePath);
            bool isValid = string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
            
            if (!isValid)
            {
                AppLog.Log($"[UpdateService] SHA256 hash mismatch! Expected: {expectedHash}, Actual: {actualHash}");
            }
            else
            {
                AppLog.Log("[UpdateService] SHA256 hash verification passed");
            }
            
            return isValid;
        }
    }
    
    /// <summary>
    /// Информация об обновлении с сервера
    /// </summary>
    public class UpdateInfo
    {
        [JsonProperty("version")]
        public string Version { get; set; } = "";
        
        [JsonProperty("url")]
        public string Url { get; set; } = "";
        
        [JsonProperty("sha256")]
        public string? Sha256 { get; set; }
        
        [JsonProperty("notes")]
        public string? Notes { get; set; }
        
        [JsonProperty("mandatory")]
        public bool Mandatory { get; set; } = false;

        /// <summary>Optional macOS package (.dmg/.zip). When absent, macOS falls back to <see cref="Url"/>.</summary>
        [JsonProperty("mac_url")]
        public string? MacUrl { get; set; }

        [JsonProperty("mac_sha256")]
        public string? MacSha256 { get; set; }

        /// <summary>Optional Linux package. When absent, Linux falls back to <see cref="Url"/>.</summary>
        [JsonProperty("linux_url")]
        public string? LinuxUrl { get; set; }

        [JsonProperty("linux_sha256")]
        public string? LinuxSha256 { get; set; }

        /// <summary>Download URL for the current OS (platform-specific field first, then the generic <see cref="Url"/>).</summary>
        [JsonIgnore]
        public string PlatformUrl =>
            OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(MacUrl) ? MacUrl! :
            OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(LinuxUrl) ? LinuxUrl! :
            Url;

        /// <summary>SHA-256 matching <see cref="PlatformUrl"/>.</summary>
        [JsonIgnore]
        public string? PlatformSha256 =>
            OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(MacUrl) ? MacSha256 :
            OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(LinuxUrl) ? LinuxSha256 :
            Sha256;
    }
}
