using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Softphone
{
    /// <summary>
    /// Сервис для проверки новой версии приложения на GitHub
    /// </summary>
    public class GitHubVersionService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private const string GitHubApiBaseUrl = "https://api.github.com";
        
        /// <summary>
        /// Получает текущую версию приложения из AssemblyInfo
        /// </summary>
        public static string GetCurrentVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
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
                MainWindow.Log($"[GitHubVersionService] Error getting current version: {ex.Message}");
            }
            
            return "1.0.0"; // Fallback версия
        }
        
        /// <summary>
        /// Проверяет наличие новой версии на GitHub
        /// </summary>
        /// <param name="repositoryOwner">Владелец репозитория (например, "username")</param>
        /// <param name="repositoryName">Название репозитория (например, "softphone")</param>
        /// <param name="githubToken">Personal Access Token для доступа к приватным репозиториям (опционально)</param>
        /// <returns>Информация о последнем релизе или null, если ошибка</returns>
        public static async Task<GitHubReleaseInfo?> CheckForUpdateAsync(string repositoryOwner, string repositoryName, string? githubToken = null)
        {
            try
            {
                string url = $"{GitHubApiBaseUrl}/repos/{repositoryOwner}/{repositoryName}/releases/latest";
                
                // Добавляем заголовки для GitHub API
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "Callspire-Softphone");
                _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                
                // Добавляем токен аутентификации, если он указан (для приватных репозиториев)
                if (!string.IsNullOrWhiteSpace(githubToken))
                {
                    // GitHub API для Personal Access Tokens использует формат "token <token>"
                    // Это работает для всех типов PAT (ghp_, github_pat_)
                    _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", githubToken);
                    
                    string tokenPreview = githubToken.Length > 15 
                        ? githubToken.Substring(0, 15) + "..." 
                        : githubToken.Substring(0, Math.Min(githubToken.Length, 15));
                    MainWindow.Log($"[GitHubVersionService] Using GitHub token for authentication (preview: {tokenPreview}, length: {githubToken.Length})");
                }
                
                MainWindow.Log($"[GitHubVersionService] Checking for updates: {url}");
                
                HttpResponseMessage response = await _httpClient.GetAsync(url);
                
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    var release = JsonConvert.DeserializeObject<GitHubReleaseInfo>(json);
                    
                    if (release != null)
                    {
                        MainWindow.Log($"[GitHubVersionService] Latest release found: {release.TagName} (Published: {release.PublishedAt})");
                        return release;
                    }
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    MainWindow.Log($"[GitHubVersionService] Repository or release not found (404). Repository: {repositoryOwner}/{repositoryName}");
                    MainWindow.Log($"[GitHubVersionService] Response body: {errorContent}");
                    
                    // Пытаемся проверить, существует ли репозиторий вообще
                    // Если репозиторий существует, но нет релизов, это нормально
                    bool repositoryExists = await CheckRepositoryExistsAsync(repositoryOwner, repositoryName, githubToken);
                    
                    if (repositoryExists)
                    {
                        MainWindow.Log($"[GitHubVersionService] ✓ Repository exists, but /releases/latest returned 404.");
                        MainWindow.Log($"[GitHubVersionService] Trying to get all releases as fallback...");
                        
                        // Попробуем получить список всех релизов (не только latest)
                        // Это работает для приватных репозиториев, когда /latest не работает
                        var allReleases = await GetAllReleasesAsync(repositoryOwner, repositoryName, githubToken);
                        if (allReleases != null && allReleases.Count > 0)
                        {
                            MainWindow.Log($"[GitHubVersionService] ✓ Found {allReleases.Count} release(s) total!");
                            MainWindow.Log($"[GitHubVersionService] Using first release as latest: {allReleases[0].TagName}");
                            
                            // Возвращаем первый релиз как latest (они отсортированы по дате создания)
                            return allReleases[0];
                        }
                        else
                        {
                            MainWindow.Log($"[GitHubVersionService] No releases found. Please create a release on GitHub.");
                        }
                    }
                    else
                    {
                        MainWindow.Log($"[GitHubVersionService] ✗ Repository not found or access denied.");
                        MainWindow.Log($"[GitHubVersionService] Possible reasons:");
                        MainWindow.Log($"[GitHubVersionService]   1. Repository does not exist");
                        MainWindow.Log($"[GitHubVersionService]   2. Repository is private and token doesn't have access");
                        MainWindow.Log($"[GitHubVersionService]   3. Repository name or owner is incorrect");
                        if (!string.IsNullOrWhiteSpace(githubToken))
                        {
                            MainWindow.Log($"[GitHubVersionService]   4. Token may not have 'repo' scope for private repositories");
                        }
                    }
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    MainWindow.Log($"[GitHubVersionService] Authentication failed (401 Unauthorized)");
                    MainWindow.Log($"[GitHubVersionService] Response: {errorContent}");
                    MainWindow.Log($"[GitHubVersionService] Possible reasons:");
                    MainWindow.Log($"[GitHubVersionService]   1. GitHub token is invalid or expired");
                    MainWindow.Log($"[GitHubVersionService]   2. Token does not have required permissions (need 'repo' scope)");
                    MainWindow.Log($"[GitHubVersionService]   3. Token was revoked or deleted on GitHub");
                    MainWindow.Log($"[GitHubVersionService] Please check your token in Settings → About and create a new one if needed");
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    MainWindow.Log($"[GitHubVersionService] GitHub API returned status: {response.StatusCode}");
                    MainWindow.Log($"[GitHubVersionService] Response: {errorContent}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[GitHubVersionService] Error checking for updates: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Проверяет, существует ли репозиторий
        /// </summary>
        private static async Task<bool> CheckRepositoryExistsAsync(string repositoryOwner, string repositoryName, string? githubToken = null)
        {
            try
            {
                string url = $"{GitHubApiBaseUrl}/repos/{repositoryOwner}/{repositoryName}";
                
                // Добавляем заголовки для GitHub API
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "Callspire-Softphone");
                _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                
                // Добавляем токен аутентификации, если он указан
                if (!string.IsNullOrWhiteSpace(githubToken))
                {
                    _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", githubToken);
                }
                
                HttpResponseMessage response = await _httpClient.GetAsync(url);
                
                MainWindow.Log($"[GitHubVersionService] Repository check: Status={response.StatusCode}, URL={url}");
                
                // Если репозиторий существует (200 OK) или доступ запрещен (403), значит репозиторий существует
                // Если 404 - репозиторий не существует или нет доступа
                bool exists = response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Forbidden;
                MainWindow.Log($"[GitHubVersionService] Repository exists: {exists}");
                return exists;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[GitHubVersionService] Error checking repository existence: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Получает все релизы (не только latest) - для диагностики
        /// </summary>
        private static async Task<List<GitHubReleaseInfo>?> GetAllReleasesAsync(string repositoryOwner, string repositoryName, string? githubToken = null)
        {
            try
            {
                string url = $"{GitHubApiBaseUrl}/repos/{repositoryOwner}/{repositoryName}/releases";
                
                // Добавляем заголовки для GitHub API
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "Callspire-Softphone");
                _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                
                // Добавляем токен аутентификации, если он указан
                if (!string.IsNullOrWhiteSpace(githubToken))
                {
                    _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", githubToken);
                }
                
                MainWindow.Log($"[GitHubVersionService] Checking all releases: {url}");
                HttpResponseMessage response = await _httpClient.GetAsync(url);
                
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    var releases = JsonConvert.DeserializeObject<List<GitHubReleaseInfo>>(json);
                    MainWindow.Log($"[GitHubVersionService] Found {releases?.Count ?? 0} release(s) total");
                    return releases;
                }
                else
                {
                    string errorContent = await response.Content.ReadAsStringAsync();
                    MainWindow.Log($"[GitHubVersionService] Failed to get all releases: {response.StatusCode}");
                    MainWindow.Log($"[GitHubVersionService] Response: {errorContent}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[GitHubVersionService] Error getting all releases: {ex.Message}");
                return null;
            }
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
        /// Проверяет, есть ли новая версия
        /// </summary>
        /// <param name="repositoryOwner">Владелец репозитория</param>
        /// <param name="repositoryName">Название репозитория</param>
        /// <param name="githubToken">Personal Access Token для доступа к приватным репозиториям (опционально)</param>
        public static async Task<bool> IsNewVersionAvailableAsync(string repositoryOwner, string repositoryName, string? githubToken = null)
        {
            var latestRelease = await CheckForUpdateAsync(repositoryOwner, repositoryName, githubToken);
            
            if (latestRelease == null)
            {
                return false;
            }
            
            string currentVersion = GetCurrentVersion();
            string latestVersion = latestRelease.TagName.TrimStart('v', 'V');
            
            int comparison = CompareVersions(currentVersion, latestVersion);
            
            MainWindow.Log($"[GitHubVersionService] Version comparison: Current={currentVersion}, Latest={latestVersion}, Comparison={comparison}");
            
            return comparison < 0; // Текущая версия меньше последней
        }
    }
    
    /// <summary>
    /// Информация о релизе на GitHub
    /// </summary>
    public class GitHubReleaseInfo
    {
        [JsonProperty("tag_name")]
        public string TagName { get; set; } = "";
        
        [JsonProperty("name")]
        public string Name { get; set; } = "";
        
        [JsonProperty("body")]
        public string Body { get; set; } = "";
        
        [JsonProperty("published_at")]
        public DateTime PublishedAt { get; set; }
        
        [JsonProperty("html_url")]
        public string HtmlUrl { get; set; } = "";
        
        [JsonProperty("assets")]
        public GitHubAsset[] Assets { get; set; } = Array.Empty<GitHubAsset>();
    }
    
    /// <summary>
    /// Информация об ассете (файле) релиза
    /// </summary>
    public class GitHubAsset
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = "";
        
        // GitHub API URL for the asset (used for authenticated downloads via API)
        [JsonProperty("url")]
        public string ApiUrl { get; set; } = "";
        
        [JsonProperty("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
        
        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("content_type")]
        public string ContentType { get; set; } = "";
    }
}

