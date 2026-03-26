using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Softphone
{
    /// <summary>
    /// Результат обработки звонка в AmoCRM
    /// </summary>
    public class ProcessCallResult
    {
        public bool Success { get; set; }
        public AmoCrmUploadStatus UploadStatus { get; set; }
        public string? Reason { get; set; }
        public long? LeadId { get; set; }
    }

    /// <summary>
    /// Оценка лида для выбора лучшего кандидата.
    /// Используется для приоритизации лидов при автоматическом выборе.
    /// Реализует IComparable — "лучший" лид имеет МЕНЬШЕЕ значение при сортировке.
    /// 
    /// Приоритеты (от высшего к низшему):
    /// 1. IsOpen — открытые лиды предпочтительнее закрытых
    /// 2. IsOurContactWithPhone — наш контакт с нужным номером есть в лиде (критично для правильного выбора)
    /// 3. IsMain — контакт является главным (is_main) в лиде
    /// 4. IsSingleContact — лид содержит только 1 контакт (наш)
    /// 5. IsPrimary — наш контакт первый в списке контактов лида
    /// 6. UpdatedAt — при прочих равных берём самый свежий лид
    /// </summary>
    public struct LeadScore : IComparable<LeadScore>
    {
        public long LeadId { get; set; }
        public bool IsOpen { get; set; }
        public bool IsResponsible { get; set; } // Лид принадлежит текущему пользователю
        public bool IsOurContactWithPhone { get; set; } // Наш контакт с нужным номером есть в лиде
        public bool IsMain { get; set; }
        public bool IsSingleContact { get; set; }
        public bool IsPrimary { get; set; }
        public DateTime UpdatedAt { get; set; }

        public int CompareTo(LeadScore other)
        {
            // 1. Открытые лиды лучше закрытых
            int cmp = other.IsOpen.CompareTo(IsOpen);
            if (cmp != 0) return cmp;

            // 2. Лиды текущего пользователя предпочтительнее лидов других пользователей
            cmp = other.IsResponsible.CompareTo(IsResponsible);
            if (cmp != 0) return cmp;

            // 3. Наш контакт с нужным номером лучше (критично для правильного выбора)
            cmp = other.IsOurContactWithPhone.CompareTo(IsOurContactWithPhone);
            if (cmp != 0) return cmp;

            // 4. is_main лучше
            cmp = other.IsMain.CompareTo(IsMain);
            if (cmp != 0) return cmp;

            // 5. Единственный контакт лучше
            cmp = other.IsSingleContact.CompareTo(IsSingleContact);
            if (cmp != 0) return cmp;

            // 6. Первый в списке лучше
            cmp = other.IsPrimary.CompareTo(IsPrimary);
            if (cmp != 0) return cmp;

            // 7. Более свежий лучше
            cmp = other.UpdatedAt.CompareTo(UpdatedAt);
            if (cmp != 0) return cmp;
            
            // 8. При одинаковой дате выбираем лид с большим ID (более новый лид)
            return other.LeadId.CompareTo(LeadId);
        }

        public override string ToString()
        {
            return $"Lead {LeadId}: open={IsOpen}, responsible={IsResponsible}, ourContactWithPhone={IsOurContactWithPhone}, main={IsMain}, single={IsSingleContact}, primary={IsPrimary}, updated={UpdatedAt:yyyy-MM-dd HH:mm}";
        }
    }

    /// <summary>
    /// Сервис для работы с AmoCRM API v4
    /// Основан на официальной документации: https://www.amocrm.ru/developers/content/crm_platform/contacts-api
    /// 
    /// Основные используемые эндпоинты:
    /// - GET /api/v4/contacts?query={phone} - поиск контактов по телефону
    /// - GET /api/v4/contacts/{id} - получение контакта по ID
    /// - GET /api/v4/leads?filter[contacts][0]={contact_id}&limit=50&order[updated_at]=desc&with=contacts - поиск лидов по контакту
        /// - POST /api/v4/leads/{id}/notes - добавление примечаний (call_in/call_out для звонков с link на MP3)
        /// - POST /api/v4/calls - добавление звонков с call_result (например "No Answer"); привязка по номеру телефона
    /// - GET /api/v4/account?with=drive_url - получение URL файлового сервиса и current_user_id
    /// - POST {drive_url}/v1.0/sessions - создание сессии для загрузки файла
    /// - GET {drive_url}/v1.0/files/{file_uuid} - получение download.href для загруженного файла
    /// - PUT /api/v4/leads/{id}/files - прикрепление файлов к лиду
    /// </summary>
    public class AmoCrmService
    {
        private readonly HttpClient _httpClient;
        private string? _subdomain;
        private string? _accessToken;
        private string? _driveUrl;
        private long? _currentUserId;
        private long? _lastProcessedLeadId;

        // OAuth support
        private string? _clientId;
        private string? _clientSecret;
        private string? _refreshToken;
        private DateTime? _tokenExpiresAt;
        private readonly SemaphoreSlim _tokenRefreshLock = new SemaphoreSlim(1, 1);
        private AmoCrmOAuthService? _oauthService;

        // Блокировка для предотвращения параллельного создания записей с одинаковым uniq
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _callNoteLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        // Блокировка для предотвращения параллельного вызова UpdateLeadAsync для одного лида
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _updateLeadLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        // Rate limiter: Kommo API — макс 7 запросов/сек, используем 6 с запасом
        private readonly SemaphoreSlim _rateLimitLock = new SemaphoreSlim(1, 1);
        private readonly Queue<DateTime> _requestTimestamps = new Queue<DateTime>();
        private const int MaxRequestsPerSecond = 6;

        // Максимальное количество лидов для обработки (оптимизация производительности)
        private const int MaxLeadsToProcess = 30;
        private const int MaxOpenLeadsToSearch = 10; // Ищем только последние 10 открытых лидов

        /// <summary>
        /// Префиксы названий лидов, которые Kommo генерирует при копировании сделок.
        /// "Копия" — русскоязычные аккаунты, "Copy of" — англоязычные.
        /// Проверяется только StartsWith, чтобы не отфильтровать лиды вроде "Копия паспорта для визы".
        /// </summary>
        private static readonly string[] CopyLeadPrefixes = { "Копия", "Copy of" };

        public AmoCrmService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(10);
        }

        #region Initialization

        /// <summary>
        /// Инициализирует сервис с учетными данными (Manual Token mode)
        /// </summary>
        public async Task InitializeAsync(string subdomain, string accessToken)
        {
            _subdomain = subdomain?.Trim();
            _accessToken = accessToken?.Trim();

            if (string.IsNullOrEmpty(_subdomain) || string.IsNullOrEmpty(_accessToken))
            {
                throw new ArgumentException("Subdomain and access token are required");
            }

            // Сбрасываем OAuth поля
            _clientId = null;
            _clientSecret = null;
            _refreshToken = null;
            _tokenExpiresAt = null;

            SetHttpHeaders();
            await LoadAccountInfoAsync();
        }

        /// <summary>
        /// Инициализирует сервис с OAuth токенами
        /// </summary>
        public async Task InitializeOAuthAsync(string subdomain, string accessToken, string? refreshToken, DateTime? tokenExpiresAt, string? clientId = null, string? clientSecret = null)
        {
            try
            {
                MainWindow.Log($"[AmoCrmService] InitializeOAuthAsync called: subdomain={subdomain}, hasAccessToken={!string.IsNullOrEmpty(accessToken)}, hasRefreshToken={!string.IsNullOrEmpty(refreshToken)}, hasClientId={!string.IsNullOrEmpty(clientId)}, hasClientSecret={!string.IsNullOrEmpty(clientSecret)}");

                _subdomain = subdomain?.Trim();
                _accessToken = accessToken?.Trim();
                _refreshToken = refreshToken?.Trim();
                _tokenExpiresAt = tokenExpiresAt;
                _clientId = clientId?.Trim();
                _clientSecret = clientSecret?.Trim();

                if (string.IsNullOrEmpty(_subdomain) || string.IsNullOrEmpty(_accessToken))
                {
                    string error = $"Subdomain and access token are required. Subdomain: {(_subdomain != null ? "present" : "null")}, AccessToken: {(_accessToken != null ? "present" : "null")}";
                    MainWindow.Log($"[AmoCrmService] InitializeOAuthAsync failed: {error}");
                    throw new ArgumentException(error);
                }

                if (!string.IsNullOrEmpty(_refreshToken) && !string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret))
                {
                    _oauthService = new AmoCrmOAuthService();
                    MainWindow.Log("[AmoCrmService] OAuth service created for token refresh");
                }
                else
                {
                    MainWindow.Log("[AmoCrmService] OAuth service not created (missing refresh token, client ID, or client secret)");
                }

                SetHttpHeaders();
                MainWindow.Log("[AmoCrmService] HTTP headers set, loading account info...");

                await LoadAccountInfoAsync();
                MainWindow.Log("[AmoCrmService] InitializeOAuthAsync completed successfully");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] InitializeOAuthAsync error: {ex.Message}");
                MainWindow.Log($"[AmoCrmService] Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        private void SetHttpHeaders()
        {
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Callspire-Softphone/1.0");
        }

        public bool IsInitialized => !string.IsNullOrEmpty(_subdomain) && !string.IsNullOrEmpty(_accessToken);

        public (string? accessToken, string? refreshToken, DateTime? expiresAt) GetOAuthTokens()
        {
            return (_accessToken, _refreshToken, _tokenExpiresAt);
        }

        #endregion

        #region Token Management

        /// <summary>
        /// Проверяет и обновляет токен если он истек (только для OAuth режима)
        /// </summary>
        private async Task EnsureValidTokenAsync(bool forceRefresh = false)
        {
            if (string.IsNullOrEmpty(_refreshToken) || _oauthService == null || string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_clientSecret))
            {
                return;
            }

            // Обновляем за 5 минут до истечения или принудительно при ошибке Unauthorized
            bool shouldRefresh = forceRefresh || (_tokenExpiresAt.HasValue && DateTime.UtcNow.AddMinutes(5) >= _tokenExpiresAt.Value);
            
            if (shouldRefresh)
            {
                await _tokenRefreshLock.WaitAsync();
                try
                {
                    // Double-check pattern
                    bool stillNeedsRefresh = forceRefresh || (_tokenExpiresAt.HasValue && DateTime.UtcNow.AddMinutes(5) >= _tokenExpiresAt.Value);
                    if (stillNeedsRefresh)
                    {
                        MainWindow.Log($"[AmoCrmService] Access token {(forceRefresh ? "invalid/expired (forced refresh)" : "expired or expiring soon")}, refreshing...");

                        var result = await _oauthService.RefreshTokenAsync(_subdomain!, _clientId!, _clientSecret!, _refreshToken!);

                        if (result.accessToken != null)
                        {
                            _accessToken = result.accessToken;
                            _refreshToken = result.refreshToken ?? _refreshToken;

                            if (result.expiresIn.HasValue)
                            {
                                _tokenExpiresAt = DateTime.UtcNow.AddSeconds(result.expiresIn.Value);
                            }

                            // КРИТИЧНО: Обновляем заголовок Authorization после обновления токена
                            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                            MainWindow.Log("[AmoCrmService] Token refreshed successfully, Authorization header updated");

                            await SaveOAuthTokensToSettingsAsync();
                        }
                        else
                        {
                            MainWindow.Log("[AmoCrmService] Failed to refresh token - refresh token may be invalid or expired");
                            // КРИТИЧНО: Если refresh token не работает, выбрасываем исключение для уведомления пользователя
                            throw new UnauthorizedAccessException("Failed to refresh access token. Refresh token may be invalid or expired. Please re-authorize the application.");
                        }
                    }
                }
                finally
                {
                    _tokenRefreshLock.Release();
                }
            }
        }

        private async Task SaveOAuthTokensToSettingsAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_accessToken))
                    return;

                string settingsPath = AppDataHelper.GetSettingsFilePath();
                if (!File.Exists(settingsPath))
                    return;

                await Task.Run(() =>
                {
                    string json = File.ReadAllText(settingsPath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    if (settings == null || settings.AmoCrmAuthMode != "oauth")
                        return;

                    settings.AmoCrmOAuthAccessTokenEncrypted = TokenEncryption.Encrypt(_accessToken);
                    if (!string.IsNullOrEmpty(_refreshToken))
                    {
                        settings.AmoCrmOAuthRefreshTokenEncrypted = TokenEncryption.Encrypt(_refreshToken);
                    }
                    if (_tokenExpiresAt.HasValue)
                    {
                        settings.AmoCrmOAuthTokenExpiresAt = _tokenExpiresAt.Value;
                    }

                    string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                    File.WriteAllText(settingsPath, updatedJson);

                    MainWindow.Log("[AmoCrmService] OAuth tokens saved to settings after refresh");
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error saving OAuth tokens to settings: {ex.Message}");
            }
        }

        #endregion

        #region Rate Limiting

        /// <summary>
        /// Ограничивает скорость запросов к API Kommo (макс 6 запросов/сек).
        /// Delay выполняется ВНЕ lock, чтобы не блокировать параллельные запросы.
        /// </summary>
        private async Task RateLimitAsync()
        {
            int waitMs = 0;

            // Фаза 1: под lock считаем, нужно ли ждать
            await _rateLimitLock.WaitAsync().ConfigureAwait(false);
            try
            {
                DateTime now = DateTime.UtcNow;

                while (_requestTimestamps.Count > 0 && (now - _requestTimestamps.Peek()).TotalMilliseconds > 1000)
                {
                    _requestTimestamps.Dequeue();
                }

                if (_requestTimestamps.Count >= MaxRequestsPerSecond)
                {
                    DateTime oldest = _requestTimestamps.Peek();
                    waitMs = (int)(1000 - (now - oldest).TotalMilliseconds) + 50;
                }
            }
            finally
            {
                _rateLimitLock.Release();
            }

            // Фаза 2: ждём ВНЕ lock
            if (waitMs > 0)
            {
                MainWindow.Log($"[AmoCrmService] Rate limit: ожидание {waitMs}ms");
                await Task.Delay(waitMs).ConfigureAwait(false);
            }

            // Фаза 3: под lock регистрируем запрос
            await _rateLimitLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _requestTimestamps.Enqueue(DateTime.UtcNow);
            }
            finally
            {
                _rateLimitLock.Release();
            }
        }

        #endregion

        #region Account Info

        private async Task LoadAccountInfoAsync()
        {
            try
            {
                await EnsureValidTokenAsync();

                string apiUrl = $"{GetApiBaseUrl()}/account?with=drive_url";
                var response = await _httpClient.GetAsync(apiUrl);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] Failed to get account info: {response.StatusCode} - {responseContent}");
                    
                    // КРИТИЧНО: Если получили Unauthorized, токен недействителен - пытаемся обновить
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        MainWindow.Log($"[AmoCrmService] ⚠️ Unauthorized when loading account info - token is invalid, attempting refresh...");
                        try
                        {
                            await EnsureValidTokenAsync(forceRefresh: true);
                            // Повторяем запрос после обновления токена
                            response = await _httpClient.GetAsync(apiUrl);
                            responseContent = await response.Content.ReadAsStringAsync();
                            if (!response.IsSuccessStatusCode)
                            {
                                MainWindow.Log($"[AmoCrmService] ❌ Retry after token refresh failed: {response.StatusCode} - {responseContent}");
                                throw new UnauthorizedAccessException($"Failed to load account info after token refresh: {response.StatusCode}");
                            }
                            MainWindow.Log($"[AmoCrmService] ✅ Retry after token refresh succeeded");
                        }
                        catch (Exception refreshEx)
                        {
                            MainWindow.Log($"[AmoCrmService] ❌ Error refreshing token in LoadAccountInfoAsync: {refreshEx.Message}");
                            throw new UnauthorizedAccessException("Token is invalid and refresh failed", refreshEx);
                        }
                    }
                    else
                    {
                        // Для других ошибок просто возвращаемся - возможно, временная проблема
                        return;
                    }
                }

                var json = JObject.Parse(responseContent);
                _driveUrl = json["drive_url"]?.Value<string>();
                _currentUserId = json["current_user_id"]?.Value<long>();

                if (string.IsNullOrEmpty(_driveUrl))
                {
                    MainWindow.Log("[AmoCrmService] Warning: drive_url not found in account info, file upload may fail");
                }
                else
                {
                    MainWindow.Log($"[AmoCrmService] Drive URL loaded: {_driveUrl}");
                }

                if (!_currentUserId.HasValue)
                {
                    MainWindow.Log("[AmoCrmService] Warning: current_user_id not found in account info");
                }
                else
                {
                    MainWindow.Log($"[AmoCrmService] Current user ID loaded: {_currentUserId}");
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Пробрасываем UnauthorizedAccessException дальше - это означает, что токен недействителен
                throw;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error loading account info: {ex.Message}");
                if (ex.InnerException != null)
                {
                    MainWindow.Log($"[AmoCrmService] Inner exception: {ex.InnerException.Message}");
                }
                MainWindow.Log($"[AmoCrmService] Attempted URL: {GetApiBaseUrl()}/account?with=drive_url");
                // Пробрасываем исключение дальше, чтобы вызывающий код знал, что инициализация не удалась
                throw;
            }
        }

        private string GetApiBaseUrl()
        {
            if (string.IsNullOrEmpty(_subdomain))
            {
                throw new InvalidOperationException("Service not initialized. Call Initialize() first.");
            }

            if (_subdomain.Contains("."))
            {
                string domain = _subdomain;
                if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    domain = domain.Substring("https://".Length);
                else if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    domain = domain.Substring("http://".Length);
                domain = domain.TrimEnd('/');
                return $"https://{domain}/api/v4";
            }
            else
            {
                return $"https://{_subdomain}.amocrm.ru/api/v4";
            }
        }

        #endregion

        #region Contact Search

        /// <summary>
        /// Находит контакт по номеру телефона
        /// </summary>
        public async Task<long?> FindContactByPhoneAsync(string phoneNumber)
        {
            if (!IsInitialized)
            {
                MainWindow.Log("[AmoCrmService] Service not initialized");
                return null;
            }

            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                string normalizedPhone = NormalizePhoneNumber(phoneNumber);
                if (string.IsNullOrEmpty(normalizedPhone))
                {
                    MainWindow.Log($"[AmoCrmService] Invalid phone number: {phoneNumber}");
                    return null;
                }

                string apiUrl = $"{GetApiBaseUrl()}/contacts";
                string query = $"query={Uri.EscapeDataString(normalizedPhone)}";

                var response = await _httpClient.GetAsync($"{apiUrl}?{query}");
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] Failed to search contact: {response.StatusCode} - {responseContent}");
                    return null;
                }

                var json = JObject.Parse(responseContent);
                var contacts = json["_embedded"]?["contacts"] as JArray;

                if (contacts == null || contacts.Count == 0)
                {
                    MainWindow.Log($"[AmoCrmService] Contact not found for phone: {phoneNumber}");
                    return null;
                }

                foreach (var contact in contacts)
                {
                    var contactId = contact["id"]?.Value<long>();
                    if (contactId == null) continue;

                    var fullContact = await GetContactByIdAsync(contactId.Value);
                    if (fullContact != null && HasMatchingPhone(fullContact, normalizedPhone))
                    {
                        MainWindow.Log($"[AmoCrmService] Found contact ID: {contactId} for phone: {phoneNumber}");
                        return contactId;
                    }
                }

                MainWindow.Log($"[AmoCrmService] Contact not found with matching phone: {phoneNumber}");
                return null;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error finding contact: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Находит ВСЕ контакты по номеру телефона.
        /// Используется для сценариев ручной загрузки, когда один и тот же номер
        /// может быть привязан к нескольким контактам, а нужный лид "сидит" не на первом.
        /// </summary>
        public async Task<List<long>> FindAllContactsByPhoneAsync(string phoneNumber)
        {
            var result = new List<long>();

            if (!IsInitialized)
            {
                MainWindow.Log("[AmoCrmService] Service not initialized");
                return result;
            }

            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                string normalizedPhone = NormalizePhoneNumber(phoneNumber);
                if (string.IsNullOrEmpty(normalizedPhone))
                {
                    MainWindow.Log($"[AmoCrmService] Invalid phone number: {phoneNumber}");
                    return result;
                }

                string apiUrl = $"{GetApiBaseUrl()}/contacts";
                string query = $"query={Uri.EscapeDataString(normalizedPhone)}";

                var response = await _httpClient.GetAsync($"{apiUrl}?{query}");
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] Failed to search contacts: {response.StatusCode} - {responseContent}");
                    return result;
                }

                var json = JObject.Parse(responseContent);
                var contacts = json["_embedded"]?["contacts"] as JArray;

                if (contacts == null || contacts.Count == 0)
                {
                    MainWindow.Log($"[AmoCrmService] Contacts not found for phone: {phoneNumber}");
                    return result;
                }

                foreach (var contact in contacts)
                {
                    var contactId = contact["id"]?.Value<long>();
                    if (contactId == null) continue;

                    var fullContact = await GetContactByIdAsync(contactId.Value);
                    if (fullContact != null && HasMatchingPhone(fullContact, normalizedPhone))
                    {
                        result.Add(contactId.Value);
                        MainWindow.Log($"[AmoCrmService] Found contact ID: {contactId} for phone: {phoneNumber} (all-matches search)");
                    }
                }

                if (result.Count == 0)
                {
                    MainWindow.Log($"[AmoCrmService] Contacts not found with matching phone: {phoneNumber} (all-matches search)");
                }

                return result;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error finding contacts (all-matches): {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Получает имя контакта по номеру телефона
        /// </summary>
        public async Task<string?> GetContactNameByPhoneAsync(string phoneNumber)
        {
            if (!IsInitialized)
                return null;

            try
            {
                long? contactId = await FindContactByPhoneAsync(phoneNumber);
                if (!contactId.HasValue)
                    return null;

                var contact = await GetContactByIdAsync(contactId.Value);
                if (contact == null)
                    return null;

                string? name = contact["name"]?.Value<string>();
                if (!string.IsNullOrEmpty(name))
                {
                    MainWindow.Log($"[AmoCrmService] Found contact name: {name} for phone: {phoneNumber}");
                    return name;
                }

                return null;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error getting contact name: {ex.Message}");
                return null;
            }
        }

        private async Task<JObject?> GetContactByIdAsync(long contactId)
        {
            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();
                string apiUrl = $"{GetApiBaseUrl()}/contacts/{contactId}";
                var response = await _httpClient.GetAsync(apiUrl);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return null;

                return JObject.Parse(responseContent);
            }
            catch
            {
                return null;
            }
        }

        private bool HasMatchingPhone(JObject contact, string normalizedPhone)
        {
            try
            {
                var customFields = contact["custom_fields_values"] as JArray;
                if (customFields == null) return false;

                foreach (var field in customFields)
                {
                    var fieldCode = field["field_code"]?.Value<string>();
                    if (fieldCode == "PHONE")
                    {
                        var values = field["values"] as JArray;
                        if (values != null)
                        {
                            foreach (var value in values)
                            {
                                var phone = value["value"]?.Value<string>();
                                if (!string.IsNullOrEmpty(phone))
                                {
                                    string normalized = NormalizePhoneNumber(phone);
                                    if (normalized == normalizedPhone)
                                        return true;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors
            }

            return false;
        }

        private string NormalizePhoneNumber(string phoneNumber)
        {
            if (string.IsNullOrEmpty(phoneNumber))
                return string.Empty;

            string normalized = new string(phoneNumber.Where(char.IsDigit).ToArray());

            // 8XXXXXXXXXX → 7XXXXXXXXXX (Россия)
            if (normalized.StartsWith("8") && normalized.Length > 1)
            {
                normalized = "7" + normalized.Substring(1);
            }

            return normalized;
        }

        #endregion

        #region Lead Selection (Refactored)

        /// <summary>
        /// Проверяет, является ли название лида копией сделки.
        /// Kommo генерирует копии с префиксами "Копия" (RU) и "Copy of" (EN).
        /// Проверяется только StartsWith, чтобы не отфильтровать настоящие лиды
        /// с похожими словами в середине названия (например "Копия паспорта для визы").
        /// </summary>
        public static bool IsCopyLead(string? leadName)
        {
            if (string.IsNullOrEmpty(leadName))
                return false;

            string trimmedName = leadName.Trim();
            foreach (var prefix in CopyLeadPrefixes)
            {
                if (trimmedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Вычисляет оценку лида для выбора лучшего кандидата.
        /// Pure function — не зависит от HTTP, можно тестировать отдельно.
        /// </summary>
        /// <param name="lead">JSON-объект лида из API</param>
        /// <param name="contactId">ID нашего контакта</param>
        /// <param name="contacts">Массив контактов лида (_embedded.contacts)</param>
        /// <param name="isOurContactWithPhone">Наш контакт с нужным номером есть в лиде</param>
        public static LeadScore ScoreLeadForSelection(JToken lead, long contactId, JArray contacts, bool isOurContactWithPhone = false)
        {
            var leadId = lead["id"]?.Value<long>() ?? 0;

            // Проверяем, открыта ли сделка
            var closedAt = lead["closed_at"];
            bool isOpen = closedAt == null || closedAt.Type == JTokenType.Null;

            // Извлекаем список ID контактов
            var contactIds = contacts
                .Select(c => c["id"]?.Value<long>())
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();

            // Проверяем is_main для нашего контакта
            bool isMain = false;
            foreach (var contact in contacts)
            {
                var cid = contact["id"]?.Value<long>();
                if (cid == contactId)
                {
                    isMain = contact["is_main"]?.Value<bool>() ?? false;
                    break;
                }
            }

            // Наш контакт единственный?
            bool isSingleContact = contactIds.Count == 1 && contactIds[0] == contactId;

            // Наш контакт первый в списке?
            bool isPrimary = contactIds.Count > 0 && contactIds[0] == contactId;

            // Дата обновления для tiebreaker
            DateTime updatedAt = DateTime.MinValue;
            var updatedAtToken = lead["updated_at"];
            if (updatedAtToken != null && updatedAtToken.Type != JTokenType.Null)
            {
                // Kommo возвращает updated_at как Unix timestamp
                var updatedAtValue = updatedAtToken.Value<long?>();
                if (updatedAtValue.HasValue)
                {
                    updatedAt = DateTimeOffset.FromUnixTimeSeconds(updatedAtValue.Value).UtcDateTime;
                }
            }

            return new LeadScore
            {
                LeadId = leadId,
                IsOpen = isOpen,
                IsOurContactWithPhone = isOurContactWithPhone,
                IsMain = isMain,
                IsSingleContact = isSingleContact,
                IsPrimary = isPrimary,
                UpdatedAt = updatedAt
            };
        }

        /// <summary>
        /// Ищет лид для авто‑прикрепления записи по алгоритму CallGear:
        /// 1. Ищем ВСЕ активные (открытые) сделки контакта
        /// 2. Из всех активных выбираем ту, которая создана последней по времени (created_at desc)
        /// 3. Если активных нет — возвращаем null (запись будет прикреплена к контакту)
        ///
        /// ОПТИМИЗИРОВАНО: Используем /contacts/{id}?with=leads для гарантированного получения лидов конкретного контакта.
        /// Фильтр filter[contacts][0] работает некорректно и возвращает лиды других контактов.
        /// 
        /// Стратегия оптимизации:
        /// - Запрашиваем лиды через /contacts/{id}?with=leads с limit=100 и order[created_at]=desc
        /// - API может игнорировать limit и вернуть все лиды, поэтому ограничиваем обработку на клиенте
        /// - Обрабатываем только первые 100 лидов (достаточно для поиска открытого, т.к. они отсортированы по дате создания)
        /// - Фильтруем открытые лиды на стороне клиента (closed_at == null)
        /// - Выбираем самый свежий открытый лид по created_at
        /// 
        /// Это гарантирует правильную работу даже если у контакта тысячи закрытых лидов,
        /// т.к. мы обрабатываем только первые 100 самых свежих лидов.
        /// </summary>
        /// <summary>
        /// Публичный метод для поиска лида по контакту (используется для оптимизации - поиск во время звонка)
        /// </summary>
        public async Task<long?> FindLeadByContactIdForCallAsync(long contactId, string phoneNumber)
        {
            return await FindLeadByContactIdAsync(contactId, phoneNumber);
        }
        
        private async Task<long?> FindLeadByContactIdAsync(long contactId, string phoneNumber)
        {
            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                // Алгоритм CallGear: ищем ВСЕ активные (открытые) сделки контакта
                // Используем /contacts/{id}?with=leads для гарантированного получения лидов конкретного контакта
                // ОПТИМИЗИРОВАНО: Запрашиваем только 50 лидов для быстрого поиска открытого
                int limit = 50;
                string apiUrl = $"{GetApiBaseUrl()}/contacts/{contactId}?with=leads&limit={limit}&order[created_at]=desc";
                MainWindow.Log($"[AmoCrmService] 🔍 Ищем ВСЕ активные (открытые) сделки контакта {contactId} через /contacts/{contactId}?with=leads");
                MainWindow.Log($"[AmoCrmService] 🔍 URL: {apiUrl}");
                var response = await _httpClient.GetAsync(apiUrl);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] ❌ Ошибка загрузки лидов контакта ({response.StatusCode}): {responseContent}");
                    MainWindow.Log($"[AmoCrmService] ⚠️ Не удалось загрузить лиды контакта — будем прикреплять запись к контакту");
                    return null;
                }

                var contactJson = JObject.Parse(responseContent);
                var leadsArray = contactJson["_embedded"]?["leads"] as JArray;
                if (leadsArray == null || leadsArray.Count == 0)
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ У контакта {contactId} нет лидов — будем прикреплять запись к контакту");
                    return null;
                }
                int leadsToProcess = Math.Min(limit, leadsArray.Count);
                if (leadsArray.Count > limit)
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ API вернул {leadsArray.Count} лидов вместо запрошенных {limit}, обрабатываем только первые {leadsToProcess}");
                }

                MainWindow.Log($"[AmoCrmService] 📊 Найдено {leadsArray.Count} лид(ов) у контакта {contactId} (обрабатываем первые {leadsToProcess} для поиска активных сделок):");
                
                // ОПТИМИЗИРОВАНО: Ранний выход - собираем открытые лиды и останавливаемся, если нашли достаточно
                // Обрабатываем максимум 30 лидов для поиска открытого (достаточно, т.к. они отсортированы по created_at desc)
                var openLeads = new List<(long id, string name, DateTime createdAt)>();
                int maxLeadsToCheck = Math.Min(30, leadsToProcess); // Ранний выход после 30 лидов
                
                for (int i = 0; i < maxLeadsToCheck; i++)
                {
                    var shortLead = leadsArray[i];
                    var leadId = shortLead["id"]?.Value<long?>();
                    if (!leadId.HasValue) continue;

                    string leadName = shortLead["name"]?.Value<string>() ?? $"Lead #{leadId.Value}";
                    if (IsCopyLead(leadName))
                    {
                        MainWindow.Log($"[AmoCrmService]   ⏭️ Lead #{leadId.Value} '{leadName}' — пропускаем (копия)");
                        continue;
                    }

                    // Проверяем, есть ли closed_at и created_at в shortLead
                    var closedAt = shortLead["closed_at"];
                    var createdAtToken = shortLead["created_at"];
                    
                    // Если в shortLead нет нужных полей - загружаем полный лид
                    JToken? lead = shortLead;
                    if (closedAt == null && createdAtToken == null)
                    {
                        var fullLead = await GetLeadByIdAsync(leadId.Value);
                        if (fullLead == null)
                        {
                            MainWindow.Log($"[AmoCrmService]   ⚠️ Lead #{leadId.Value} — не удалось загрузить полный лид, пропускаем");
                            continue;
                        }
                        lead = fullLead;
                        closedAt = lead["closed_at"];
                        createdAtToken = lead["created_at"];
                        leadName = lead["name"]?.Value<string>() ?? leadName;
                    }

                    // Проверяем статус лида: открыт если closed_at == null (активная сделка)
                    bool isOpen = closedAt == null || closedAt.Type == JTokenType.Null;
                    
                    DateTime createdAt = DateTime.MinValue;
                    if (createdAtToken != null && createdAtToken.Type != JTokenType.Null)
                    {
                        if (createdAtToken.Type == JTokenType.Integer)
                        {
                            var createdAtValue = createdAtToken.Value<long?>();
                            if (createdAtValue.HasValue)
                            {
                                createdAt = DateTimeOffset.FromUnixTimeSeconds(createdAtValue.Value).UtcDateTime;
                            }
                        }
                        else if (createdAtToken.Type == JTokenType.String)
                        {
                            string? createdAtStr = createdAtToken.Value<string>();
                            if (!string.IsNullOrEmpty(createdAtStr))
                            {
                                if (DateTime.TryParse(createdAtStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedDate))
                                {
                                    createdAt = parsedDate.ToUniversalTime();
                                }
                            }
                        }
                    }

                    string status = isOpen ? "ОТКРЫТ" : "ЗАКРЫТ";
                    string dateStr = createdAt == DateTime.MinValue ? "дата неизвестна" : createdAt.ToString("yyyy-MM-dd HH:mm:ss");
                    MainWindow.Log($"[AmoCrmService]   📋 Lead #{leadId.Value} '{leadName}' — {status}, created_at={dateStr}");
                    
                    // Если лид открыт - добавляем в список
                    if (isOpen)
                    {
                        openLeads.Add((leadId.Value, leadName, createdAt));
                    }
                }

                // Алгоритм CallGear: из всех активных (открытых) выбираем ту, которая создана последней по времени
                if (openLeads.Count == 0)
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ У контакта {contactId} нет активных (открытых) сделок среди {maxLeadsToCheck} проверенных лидов — будем прикреплять запись к контакту");
                    return null;
                }

                MainWindow.Log($"[AmoCrmService] 🔍 Найдено {openLeads.Count} активных (открытых) сделок из {maxLeadsToCheck} проверенных, ищем самую свежую:");
                
                // Выбираем самую свежую активную сделку по дате создания
                var latestOpenLead = openLeads.OrderByDescending(l => l.createdAt).First();
                
                string latestDateStr = latestOpenLead.createdAt == DateTime.MinValue ? "дата неизвестна" : latestOpenLead.createdAt.ToString("yyyy-MM-dd HH:mm:ss");
                MainWindow.Log($"[AmoCrmService] ✅ Выбрана самая свежая активная сделка {latestOpenLead.id} '{latestOpenLead.name}' для контакта {contactId} (created_at={latestDateStr})");
                return latestOpenLead.id;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] ❌ Error in FindLeadByContactIdAsync for contact {contactId}: {ex.Message}");
                MainWindow.Log($"[AmoCrmService] Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Fallback метод: ищет открытые лиды перебором (используется если фильтр API не поддерживается).
        /// ВРЕМЕННО ОТКЛЮЧЕН для тестирования фильтра filter[closed_at]=null
        /// </summary>
        private async Task<long?> FindLeadByContactIdAsyncFallback(long contactId, string phoneNumber)
        {
            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                // Берём лиды контакта через /leads с фильтром по contactId и сортировкой по created_at desc.
                // filter[contacts][0]={contactId} - правильный формат фильтрации по контакту в AmoCRM API v4.
                // Ограничиваемся MaxLeadsToProcess — нам нужен только самый свежий открытый лид.
                string apiUrl = $"{GetApiBaseUrl()}/leads?filter[contacts][0]={contactId}&limit={MaxLeadsToProcess}&order[created_at]=desc";
                var response = await _httpClient.GetAsync(apiUrl);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] Failed to load leads for contact {contactId}: {response.StatusCode} - {responseContent}");
                    return null;
                }

                var json = JObject.Parse(responseContent);
                var leadsArray = json["_embedded"]?["leads"] as JArray;
                if (leadsArray == null || leadsArray.Count == 0)
                {
                    MainWindow.Log($"[AmoCrmService] No leads found for contact {contactId}");
                    return null;
                }

                MainWindow.Log($"[AmoCrmService] 📊 Найдено {leadsArray.Count} лидов по контакту {contactId} (fallback: ищем последний созданный открытый)");

                foreach (var lead in leadsArray)
                {
                    var leadId = lead["id"]?.Value<long?>();
                    if (!leadId.HasValue) continue;

                    string leadName = lead["name"]?.Value<string>() ?? $"Lead #{leadId.Value}";
                    if (IsCopyLead(leadName))
                    {
                        continue;
                    }

                    var closedAt = lead["closed_at"];
                    bool isOpen = closedAt == null || closedAt.Type == JTokenType.Null;
                    if (!isOpen)
                    {
                        continue;
                    }

                    var createdAtToken = lead["created_at"];
                    DateTime createdAt = DateTime.MinValue;
                    if (createdAtToken != null && createdAtToken.Type != JTokenType.Null)
                    {
                        var createdAtValue = createdAtToken.Value<long?>();
                        if (createdAtValue.HasValue)
                        {
                            createdAt = DateTimeOffset.FromUnixTimeSeconds(createdAtValue.Value).UtcDateTime;
                        }
                    }

                    MainWindow.Log($"[AmoCrmService] ✅ Выбран последний созданный открытый лид {leadId.Value} (created_at={createdAt:yyyy-MM-dd HH:mm:ss}) для контакта {contactId} (fallback)");
                    return leadId.Value;
                }

                MainWindow.Log($"[AmoCrmService] ⚠️ У контакта {contactId} нет открытых лидов — будем прикреплять запись к контакту");
                return null;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] ❌ Error in FindLeadByContactIdAsyncFallback for contact {contactId}: {ex.Message}");
                return null;
            }
        }

        private async Task<JObject?> GetLeadByIdAsync(long leadId)
        {
            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();
                string apiUrl = $"{GetApiBaseUrl()}/leads/{leadId}?with=contacts";
                var response = await _httpClient.GetAsync(apiUrl);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return null;

                return JObject.Parse(responseContent);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Получает список активных лидов для контакта (для диалога выбора).
        /// Оптимизированная версия с ограничением количества обрабатываемых лидов.
        /// ВАЖНО: больше не ходим в общий список /leads с фильтрами и пагинацией,
        /// чтобы не «перебирать миллион лидов». Вместо этого:
        /// 1) берём /contacts/{id}?with=leads&limit={MaxLeadsToProcess}&order[updated_at]=desc;
        /// 2) по каждому найденному лиду подтягиваем полный лид с контактами;
        /// 3) фильтруем копии, закрытые и лиды, где НЕТ этого contactId.
        /// </summary>
        public async Task<List<LeadSelectionWindow.LeadInfo>> GetLeadsForContactAsync(long contactId, int maxLeads = 30)
        {
            var leads = new List<LeadSelectionWindow.LeadInfo>();

            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                // 1. Берём контакт с вложенными лидами (ограничиваем количество и сортируем по created_at desc для получения самых свежих)
                int limit = Math.Min(maxLeads, MaxLeadsToProcess);
                string apiUrl = $"{GetApiBaseUrl()}/contacts/{contactId}?with=leads&limit={limit}&order[created_at]=desc";
                var response = await _httpClient.GetAsync(apiUrl);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] Failed to load contact {contactId} with leads for manual selection: {response.StatusCode}");
                    return leads;
                }

                var contactJson = JObject.Parse(responseContent);
                var leadsArray = contactJson["_embedded"]?["leads"] as JArray;

                if (leadsArray == null || leadsArray.Count == 0)
                {
                    MainWindow.Log($"[AmoCrmService] No leads linked to contact {contactId} for manual selection");
                    return leads;
                }

                MainWindow.Log($"[AmoCrmService] 📊 Найдено {leadsArray.Count} лидов для контакта {contactId} (обрабатываем до {limit})");

                int processedCount = 0;
                foreach (var shortLead in leadsArray)
                {
                    // Ограничиваем количество обрабатываемых лидов
                    if (processedCount >= limit)
                    {
                        MainWindow.Log($"[AmoCrmService] ⚠️ Достигнут лимит обработки лидов ({limit}), останавливаем перебор");
                        break;
                    }
                    processedCount++;
                    
                    var leadId = shortLead["id"]?.Value<long>();
                    if (!leadId.HasValue) continue;

                    // Не добавляем один и тот же лид дважды
                    if (leads.Any(l => l.Id == leadId.Value))
                        continue;

                    // 2. Подгружаем полный лид с контактами, чтобы корректно фильтровать
                    var lead = await GetLeadByIdAsync(leadId.Value);
                    if (lead == null)
                    {
                        MainWindow.Log($"[AmoCrmService] ⚠️ Не удалось загрузить полный лид {leadId} для ручного выбора");
                        continue;
                    }

                    var leadName = lead["name"]?.Value<string>() ?? $"Lead #{leadId}";

                    // Пропускаем копии
                    if (IsCopyLead(leadName))
                    {
                        MainWindow.Log($"[AmoCrmService] Пропускаем копию сделки {leadId}: '{leadName}' при ручном выборе");
                        continue;
                    }

                    // В ручном режиме показываем ВСЕ лиды (открытые и закрытые), ограничиваем только количеством
                    var closedAt = lead["closed_at"];
                    bool isOpen = closedAt == null || closedAt.Type == JTokenType.Null;

                    // Контакты лида
                    var embedded = lead["_embedded"];
                    var contacts = embedded?["contacts"] as JArray;
                    if (contacts == null || contacts.Count == 0)
                    {
                        MainWindow.Log($"[AmoCrmService] Пропускаем лид {leadId}: '{leadName}' при ручном выборе — нет контактов в _embedded");
                        continue;
                    }

                    var contactIds = contacts
                        .Select(c => c["id"]?.Value<long>())
                        .Where(id => id.HasValue)
                        .Select(id => id!.Value)
                        .ToList();

                    // Гарантируем, что этот лид действительно относится к нашему contactId
                    if (!contactIds.Contains(contactId))
                    {
                        MainWindow.Log($"[AmoCrmService] Пропускаем лид {leadId}: '{leadName}' при ручном выборе — не содержит контакт {contactId}");
                        continue;
                    }

                    var price = lead["price"]?.Value<long?>();
                    var responsibleUserId = lead["responsible_user_id"]?.Value<long?>();
                    int contactCount = contactIds.Count;

                    string description = isOpen ? "Open" : "Closed";
                    description += $", Contacts: {contactCount}";
                    if (price.HasValue && price.Value > 0)
                    {
                        description += $", Price: {price.Value:N0}";
                    }

                    leads.Add(new LeadSelectionWindow.LeadInfo
                    {
                        Id = leadId.Value,
                        Name = leadName,
                        Description = description,
                        ResponsibleUserId = responsibleUserId
                    });
                }

                MainWindow.Log($"[AmoCrmService] For contact {contactId} prepared {leads.Count} lead(s) (open + closed) for manual selection");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error getting leads for contact: {ex.Message}");
            }

            return leads;
        }

        /// <summary>
        /// Получает список закрытых лидов для контакта (для диалога выбора при отсутствии открытых лидов).
        /// Возвращает последние 30 закрытых лидов, отсортированных по дате обновления.
        /// </summary>
        public async Task<List<LeadSelectionWindow.LeadInfo>> GetClosedLeadsForContactAsync(long contactId)
        {
            var leads = new List<LeadSelectionWindow.LeadInfo>();

            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                // Берём контакт с вложенными лидами (ограничиваем до 30 и сортируем по updated_at desc)
                string apiUrl = $"{GetApiBaseUrl()}/contacts/{contactId}?with=leads&limit={MaxLeadsToProcess}&order[updated_at]=desc";
                var response = await _httpClient.GetAsync(apiUrl);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] Failed to load contact {contactId} with leads for closed leads selection: {response.StatusCode}");
                    return leads;
                }

                var contactJson = JObject.Parse(responseContent);
                var leadsArray = contactJson["_embedded"]?["leads"] as JArray;

                if (leadsArray == null || leadsArray.Count == 0)
                {
                    MainWindow.Log($"[AmoCrmService] No leads linked to contact {contactId} for closed leads selection");
                    return leads;
                }

                MainWindow.Log($"[AmoCrmService] 📊 Найдено {leadsArray.Count} лидов для контакта {contactId} (ищем закрытые среди первых {MaxLeadsToProcess})");

                int processedCount = 0;
                foreach (var shortLead in leadsArray)
                {
                    if (processedCount >= MaxLeadsToProcess)
                    {
                        MainWindow.Log($"[AmoCrmService] ⚠️ Достигнут лимит обработки закрытых лидов ({MaxLeadsToProcess})");
                        break;
                    }
                    processedCount++;
                    
                    var leadId = shortLead["id"]?.Value<long>();
                    if (!leadId.HasValue) continue;

                    if (leads.Any(l => l.Id == leadId.Value))
                        continue;

                    var lead = await GetLeadByIdAsync(leadId.Value);
                    if (lead == null) continue;

                    var leadName = lead["name"]?.Value<string>() ?? $"Lead #{leadId}";

                    if (IsCopyLead(leadName))
                        continue;

                    // Только закрытые лиды
                    var closedAt = lead["closed_at"];
                    bool isOpen = closedAt == null || closedAt.Type == JTokenType.Null;
                    if (isOpen)
                        continue; // Пропускаем открытые

                    var embedded = lead["_embedded"];
                    var contacts = embedded?["contacts"] as JArray;
                    if (contacts == null || contacts.Count == 0)
                        continue;

                    var contactIds = contacts
                        .Select(c => c["id"]?.Value<long>())
                        .Where(id => id.HasValue)
                        .Select(id => id!.Value)
                        .ToList();

                    if (!contactIds.Contains(contactId))
                        continue;

                    var price = lead["price"]?.Value<long?>();
                    var responsibleUserId = lead["responsible_user_id"]?.Value<long?>();
                    int contactCount = contactIds.Count;

                    string description = $"Contacts: {contactCount}";
                    if (price.HasValue && price.Value > 0)
                    {
                        description += $", Price: {price.Value:N0}";
                    }

                    leads.Add(new LeadSelectionWindow.LeadInfo
                    {
                        Id = leadId.Value,
                        Name = leadName,
                        Description = description,
                        ResponsibleUserId = responsibleUserId
                    });
                }

                MainWindow.Log($"[AmoCrmService] For contact {contactId} prepared {leads.Count} closed lead(s) for manual selection");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error getting closed leads for contact: {ex.Message}");
            }

            return leads;
        }

        /// <summary>
        /// Ищет лиды по номеру телефона (для ручной загрузки записи).
        /// Оптимизированная версия с ограничением количества обрабатываемых лидов.
        /// Логика:
        /// 1. Ищем лиды через общий поиск Kommo по query=phone (ограничено до MaxLeadsToProcess, сортировка по updated_at desc).
        /// 2. Для каждого лида проверяем, что среди его контактов (с подгрузкой полных контактов)
        ///    есть хотя бы один контакт с точно этим номером.
        /// 3. Фильтруем копии и закрытые лиды.
        /// </summary>
        public async Task<List<LeadSelectionWindow.LeadInfo>> GetLeadsByPhoneAsync(string phoneNumber)
        {
            var result = new List<LeadSelectionWindow.LeadInfo>();

            if (!IsInitialized)
            {
                MainWindow.Log("[AmoCrmService] Service not initialized");
                return result;
            }

            try
            {
                string normalizedPhone = NormalizePhoneNumber(phoneNumber);
                if (string.IsNullOrEmpty(normalizedPhone))
                {
                    MainWindow.Log($"[AmoCrmService] Invalid phone number for GetLeadsByPhoneAsync: {phoneNumber}");
                    return result;
                }

                // Общий поиск по лидам (ограничиваем количество и сортируем по updated_at desc)
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                string apiUrl = $"{GetApiBaseUrl()}/leads";
                string query = $"query={Uri.EscapeDataString(normalizedPhone)}&limit={MaxLeadsToProcess}&order[updated_at]=desc&with=contacts";

                var response = await _httpClient.GetAsync($"{apiUrl}?{query}");
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] Failed to search leads by phone {phoneNumber}: {response.StatusCode} - {responseContent}");
                    return result;
                }

                var json = JObject.Parse(responseContent);
                var leadsArray = json["_embedded"]?["leads"] as JArray;
                if (leadsArray == null || leadsArray.Count == 0)
                {
                    MainWindow.Log($"[AmoCrmService] No leads found by phone {phoneNumber}");
                    return result;
                }

                MainWindow.Log($"[AmoCrmService] 📊 Найдено {leadsArray.Count} лидов по номеру {normalizedPhone} (обрабатываем до {MaxLeadsToProcess})");

                // Кэш полных контактов, чтобы не дергать один и тот же contactId много раз
                var contactCache = new Dictionary<long, JObject?>();

                int processedCount = 0;
                foreach (var lead in leadsArray)
                {
                    // Ограничиваем количество обрабатываемых лидов
                    if (processedCount >= MaxLeadsToProcess)
                    {
                        MainWindow.Log($"[AmoCrmService] ⚠️ Достигнут лимит обработки лидов ({MaxLeadsToProcess}), останавливаем перебор");
                        break;
                    }
                    processedCount++;
                    
                    var leadId = lead["id"]?.Value<long>();
                    if (!leadId.HasValue) continue;

                    var leadName = lead["name"]?.Value<string>() ?? $"Lead #{leadId}";

                    // Пропускаем копии
                    if (IsCopyLead(leadName))
                    {
                        MainWindow.Log($"[AmoCrmService] Пропускаем копию сделки {leadId}: '{leadName}' при поиске по телефону");
                        continue;
                    }

                    // Пропускаем закрытые
                    var closedAt = lead["closed_at"];
                    bool isOpen = closedAt == null || closedAt.Type == JTokenType.Null;
                    if (!isOpen)
                    {
                        MainWindow.Log($"[AmoCrmService] Пропускаем закрытый лид {leadId}: '{leadName}' при поиске по телефону");
                        continue;
                    }

                    // Получаем контакты лида
                    var embedded = lead["_embedded"];
                    var contacts = embedded?["contacts"] as JArray;
                    if (contacts == null || contacts.Count == 0)
                    {
                        var fullLead = await GetLeadByIdAsync(leadId.Value);
                        contacts = fullLead?["_embedded"]?["contacts"] as JArray;
                    }

                    if (contacts == null || contacts.Count == 0)
                    {
                        MainWindow.Log($"[AmoCrmService] Пропускаем лид {leadId}: '{leadName}' — нет контактов при поиске по телефону");
                        continue;
                    }

                    bool hasContactWithPhone = false;
                    int contactCount = 0;

                    foreach (var contact in contacts)
                    {
                        var cid = contact["id"]?.Value<long>();
                        if (!cid.HasValue) continue;
                        contactCount++;

                        if (!contactCache.TryGetValue(cid.Value, out var fullContact))
                        {
                            fullContact = await GetContactByIdAsync(cid.Value);
                            contactCache[cid.Value] = fullContact;
                        }

                        if (fullContact != null && HasMatchingPhone(fullContact, normalizedPhone))
                        {
                            hasContactWithPhone = true;
                            break;
                        }
                    }

                    if (!hasContactWithPhone)
                    {
                        MainWindow.Log($"[AmoCrmService] Пропускаем лид {leadId}: '{leadName}' при поиске по телефону — ни один контакт не содержит номер {normalizedPhone}");
                        continue;
                    }

                    var price = lead["price"]?.Value<long?>();
                    string description = $"Contacts: {contactCount}";
                    if (price.HasValue && price.Value > 0)
                    {
                        description += $", Price: {price.Value:N0}";
                    }

                    result.Add(new LeadSelectionWindow.LeadInfo
                    {
                        Id = leadId.Value,
                        Name = leadName,
                        Description = description
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error getting leads by phone: {ex.Message}");
                return result;
            }
        }

        #endregion

        #region Call Processing

        /// <summary>
        /// Обрабатывает завершенный звонок: находит контакт → находит лид → загружает запись
        /// </summary>
        public async Task<ProcessCallResult> ProcessCallAsync(string phoneNumber, bool isIncoming, int durationSeconds, bool wasAnswered = false, string? callLog = null, string? audioFilePath = null, bool enableLeadSelection = false, DateTime? callTime = null)
        {
            if (!IsInitialized)
            {
                MainWindow.Log("[AmoCrmService] Service not initialized, skipping AmoCRM integration");
                return new ProcessCallResult
                {
                    Success = false,
                    UploadStatus = AmoCrmUploadStatus.Failed,
                    Reason = "AmoCRM service not initialized"
                };
            }

            try
            {
                // Если по сигнализации длительность = 0, но у нас есть WAV-файл,
                // попробуем оценить длительность по размеру файла.
                if (durationSeconds <= 0 && !string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath))
                {
                    try
                    {
                        var fileInfo = new FileInfo(audioFilePath);
                        long fileLength = fileInfo.Length;

                        // Предполагаемый формат: pcm_s16le, 48 kHz, mono (см. WebRtcCallRecorder / ffmpeg)
                        double bytesPerSecond = 48000.0 * 2.0; // 2 байта на сэмпл * 48000 сэмплов
                        double seconds = fileLength / bytesPerSecond;
                        int estimatedDuration = (int)Math.Round(seconds);
                        if (estimatedDuration < 1)
                            estimatedDuration = 1;

                        MainWindow.Log($"[AmoCrmService] Длительность по сигнализации = 0, оценена по WAV: {estimatedDuration} сек (file={fileLength} bytes)");
                        durationSeconds = estimatedDuration;
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[AmoCrmService] Не удалось оценить длительность по WAV-файлу: {ex.Message}");
                    }
                }

                MainWindow.Log($"[AmoCrmService] ===== Начало обработки звонка =====");
                MainWindow.Log($"[AmoCrmService] Телефон: {phoneNumber}");
                MainWindow.Log($"[AmoCrmService] Тип: {(isIncoming ? "Входящий" : "Исходящий")}");
                MainWindow.Log($"[AmoCrmService] Длительность: {durationSeconds} сек");
                MainWindow.Log($"[AmoCrmService] Файл записи: {(string.IsNullOrEmpty(audioFilePath) ? "нет" : audioFilePath)}");

                // Находим контакт
                MainWindow.Log($"[AmoCrmService] Поиск контакта по телефону: {phoneNumber}");
                long? contactId = await FindContactByPhoneAsync(phoneNumber);
                if (!contactId.HasValue)
                {
                    MainWindow.Log($"[AmoCrmService] ❌ Контакт не найден для телефона: {phoneNumber}");
                    MainWindow.Log($"[AmoCrmService] ===== Обработка завершена с ошибкой =====");
                    return new ProcessCallResult
                    {
                        Success = false,
                        UploadStatus = AmoCrmUploadStatus.NotUploaded,
                        Reason = "Contact not found in AmoCRM"
                    };
                }
                MainWindow.Log($"[AmoCrmService] ✅ Контакт найден: ID = {contactId}");

                DateTime callTimeForProcessing = callTime ?? DateTime.UtcNow;

                ProcessCallResult result = await ProcessCallForExistingLeadAsync(contactId.Value, phoneNumber, isIncoming, durationSeconds, wasAnswered, callLog, audioFilePath, null, enableLeadSelection, callTimeForProcessing);

                if (result.Success && result.LeadId.HasValue)
                {
                    MainWindow.Log($"[AmoCrmService] ✅ Успешно обработан звонок");
                    MainWindow.Log($"[AmoCrmService] Контакт ID: {contactId}");
                    MainWindow.Log($"[AmoCrmService] Лид ID: {result.LeadId}");
                    MainWindow.Log($"[AmoCrmService] Статус загрузки: {result.UploadStatus}" + (string.IsNullOrEmpty(result.Reason) ? "" : $" ({result.Reason})"));

                    _lastProcessedLeadId = result.LeadId;
                    MainWindow.Log($"[AmoCrmService] ===== Обработка завершена успешно =====");
                }
                else
                {
                    MainWindow.Log($"[AmoCrmService] ❌ Не удалось обработать звонок: {result.Reason}");
                    MainWindow.Log($"[AmoCrmService] ===== Обработка завершена (статус: {result.UploadStatus}) =====");
                }

                return result;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error processing call: {ex.Message}");
                return new ProcessCallResult
                {
                    Success = false,
                    UploadStatus = AmoCrmUploadStatus.Failed,
                    Reason = $"Exception: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Обрабатывает звонок для конкретного лида (используется при звонке из браузера AmoCRM)
        /// </summary>
        public async Task<ProcessCallResult> ProcessCallForSpecificLeadAsync(long leadId, string phoneNumber, bool isIncoming, int durationSeconds, bool wasAnswered = false, string? callLog = null, string? audioFilePath = null, DateTime? callTime = null)
        {
            if (!IsInitialized)
            {
                MainWindow.Log("[AmoCrmService] Service not initialized");
                return new ProcessCallResult
                {
                    Success = false,
                    UploadStatus = AmoCrmUploadStatus.Failed,
                    Reason = "AmoCRM service not initialized"
                };
            }

            try
            {
                MainWindow.Log($"[AmoCrmService] Processing call for specific lead ID: {leadId} (from browser)");

                // Проверяем, существует ли лид
                var lead = await GetLeadByIdAsync(leadId);
                if (lead == null)
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ Lead {leadId} not found");
                    return new ProcessCallResult
                    {
                        Success = false,
                        UploadStatus = AmoCrmUploadStatus.Failed,
                        Reason = $"Lead {leadId} not found"
                    };
                }

                MainWindow.Log($"[AmoCrmService] ✅ Lead {leadId} found, uploading call record");

                bool hasFile = !string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath);
                DateTime callTimeForUpdate = callTime ?? DateTime.UtcNow;

                // Проверяем, является ли это недозвоном ДО вызова UpdateLeadAsync
                // Недозвон = звонок не был принят (wasAnswered=false) И нет записи
                // КРИТИЧНО: Если был connect (wasAnswered=true) - значит был ответ (абонент или IVR), это НЕ недозвон,
                // даже если нет записи (например, из-за проблем с RTP пакетами)
                // ВАЖНО: Не проверяем durationSeconds==0, так как звонок может быть отклонен после гудков (486 Busy Here)
                bool isMissedCall = !wasAnswered && !hasFile;
                
                if (isMissedCall)
                {
                    MainWindow.Log($"[AmoCrmService] Missed call detected for specific lead {leadId} (no recording, wasAnswered=false, duration=0) — creating missed call note");
                    bool success = await ManuallyUploadMissedCallToLeadAsync(leadId, phoneNumber, isIncoming);
                    return new ProcessCallResult
                    {
                        Success = success,
                        UploadStatus = success ? AmoCrmUploadStatus.Uploaded : AmoCrmUploadStatus.Failed,
                        Reason = success ? null : "Failed to create missed call note",
                        LeadId = leadId
                    };
                }

                try
                {
                    string? downloadLink = await UpdateLeadAsync(leadId, phoneNumber, isIncoming, durationSeconds, wasAnswered, callLog, audioFilePath, null, callTimeForUpdate);

                    // Если UpdateLeadAsync вернул null, это может быть недозвон (обработанный внутри UpdateLeadAsync)
                    // Но мы уже обработали недозвон выше, поэтому если downloadLink == null и нет файла - это нормально
                    if (hasFile)
                    {
                        if (!string.IsNullOrEmpty(downloadLink))
                        {
                            return new ProcessCallResult
                            {
                                Success = true,
                                UploadStatus = AmoCrmUploadStatus.Uploaded,
                                Reason = null,
                                LeadId = leadId
                            };
                        }
                        else
                        {
                            return new ProcessCallResult
                            {
                                Success = true,
                                UploadStatus = AmoCrmUploadStatus.Failed,
                                Reason = "File upload failed (file exists but download link not received)",
                                LeadId = leadId
                            };
                        }
                    }
                    else if (!string.IsNullOrEmpty(audioFilePath))
                    {
                        return new ProcessCallResult
                        {
                            Success = true,
                            UploadStatus = AmoCrmUploadStatus.NotUploaded,
                            Reason = "Recording file not found (may still be processing)",
                            LeadId = leadId
                        };
                    }
                    else
                    {
                        return new ProcessCallResult
                        {
                            Success = true,
                            UploadStatus = AmoCrmUploadStatus.NotUploaded,
                            Reason = "No recording file",
                            LeadId = leadId
                        };
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[AmoCrmService] Error uploading to lead: {ex.Message}");
                    return new ProcessCallResult
                    {
                        Success = false,
                        UploadStatus = AmoCrmUploadStatus.Failed,
                        Reason = $"Upload failed: {ex.Message}",
                        LeadId = leadId
                    };
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error processing call for specific lead: {ex.Message}");
                return new ProcessCallResult
                {
                    Success = false,
                    UploadStatus = AmoCrmUploadStatus.Failed,
                    Reason = $"Error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Обрабатывает звонок для существующего лида контакта (не создает новые лиды)
        /// </summary>
        public async Task<ProcessCallResult> ProcessCallForExistingLeadAsync(long contactId, string phoneNumber, bool isIncoming, int durationSeconds, bool wasAnswered = false, string? callLog = null, string? audioFilePath = null, string? audioFileLink = null, bool enableLeadSelection = false, DateTime? callTime = null)
        {
            if (!IsInitialized)
            {
                MainWindow.Log("[AmoCrmService] Service not initialized");
                return new ProcessCallResult
                {
                    Success = false,
                    UploadStatus = AmoCrmUploadStatus.Failed,
                    Reason = "AmoCRM service not initialized"
                };
            }

            try
            {
                MainWindow.Log($"[AmoCrmService] Поиск лида для контакта {contactId} (ответственный: user_id={_currentUserId?.ToString() ?? "?"})...");

                long? existingLeadId = null;
                bool userCancelled = false;

                if (enableLeadSelection)
                {
                    // Ручной режим: показываем окно выбора лидов, автоматическая загрузка отключена
                    var leads = await GetLeadsForContactAsync(contactId, maxLeads: 15);
                    if (leads.Count > 0)
                    {
                        MainWindow.Log($"[AmoCrmService] Found {leads.Count} lead(s), showing manual selection dialog...");

                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                var selectionWindow = new LeadSelectionWindow(leads, _subdomain, false, phoneNumber, audioFilePath, isIncoming, durationSeconds, wasAnswered, callLog, callTime)
                                {
                                    Owner = Application.Current.MainWindow
                                };

                                bool? result = selectionWindow.ShowDialog();
                                if (result == true && selectionWindow.SelectedLeadId.HasValue)
                                {
                                    existingLeadId = selectionWindow.SelectedLeadId.Value;
                                    MainWindow.Log($"[AmoCrmService] User selected lead ID: {existingLeadId} and uploaded recording");
                                }
                                else
                                {
                                    userCancelled = true;
                                    MainWindow.Log($"[AmoCrmService] User cancelled lead selection");
                                }
                            }
                            catch (Exception ex)
                            {
                                MainWindow.Log($"[AmoCrmService] Error showing lead selection dialog: {ex.Message}");
                            }
                        });
                    }
                    else
                    {
                        MainWindow.Log($"[AmoCrmService] ⚠️ No leads found for contact {contactId} (manual selection enabled but no leads available)");
                        // В ручном режиме, если лидов нет, не загружаем автоматически
                        return new ProcessCallResult
                        {
                            Success = false,
                            UploadStatus = AmoCrmUploadStatus.NotUploaded,
                            Reason = "No leads found for contact (manual selection enabled)"
                        };
                    }
                }
                else
                {
                    // Автоматический режим (CallGear): ищем последний созданный ОТКРЫТЫЙ лид; иначе прикрепляем к контакту.
                    existingLeadId = await FindLeadByContactIdAsync(contactId, phoneNumber);
                }

                if (userCancelled)
                {
                    return new ProcessCallResult
                    {
                        Success = false,
                        UploadStatus = AmoCrmUploadStatus.Cancelled,
                        Reason = "User cancelled lead selection"
                    };
                }

                // В ручном режиме, если пользователь выбрал лид и загрузил запись через окно, existingLeadId уже установлен
                // В автоматическом режиме: если открытого лида нет — по алгоритму CallGear прикрепляем запись/звонок к контакту.
                if (!existingLeadId.HasValue && !enableLeadSelection)
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ Открытый лид для контакта {contactId} не найден — прикрепляем к контакту");

                    bool hasFileForContact = !string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath);
                    DateTime callTimeForContactUpdate = callTime ?? DateTime.UtcNow;

                    try
                    {
                        string? downloadLink = await UpdateContactAsync(contactId, phoneNumber, isIncoming, durationSeconds, wasAnswered, callLog, audioFilePath, audioFileLink, callTimeForContactUpdate);

                        if (hasFileForContact)
                        {
                            return new ProcessCallResult
                            {
                                Success = true,
                                UploadStatus = !string.IsNullOrEmpty(downloadLink) ? AmoCrmUploadStatus.Uploaded : AmoCrmUploadStatus.Failed,
                                Reason = !string.IsNullOrEmpty(downloadLink) ? null : "File upload failed (file exists but download link not received)",
                                LeadId = null
                            };
                        }

                        if (!string.IsNullOrEmpty(audioFilePath))
                        {
                            return new ProcessCallResult
                            {
                                Success = true,
                                UploadStatus = AmoCrmUploadStatus.NotUploaded,
                                Reason = "Recording file not found (may still be processing)",
                                LeadId = null
                            };
                        }

                        return new ProcessCallResult
                        {
                            Success = true,
                            UploadStatus = AmoCrmUploadStatus.NotUploaded,
                            Reason = "No recording file (saved to contact)",
                            LeadId = null
                        };
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[AmoCrmService] Error uploading to contact: {ex.Message}");
                        return new ProcessCallResult
                        {
                            Success = false,
                            UploadStatus = AmoCrmUploadStatus.Failed,
                            Reason = $"Upload to contact failed: {ex.Message}",
                            LeadId = null
                        };
                    }
                }

                // Если лид не найден (в ручном режиме пользователь не выбрал или отменил)
                if (!existingLeadId.HasValue)
                {
                    return new ProcessCallResult
                    {
                        Success = false,
                        UploadStatus = AmoCrmUploadStatus.Cancelled,
                        Reason = "No lead selected",
                        LeadId = null
                    };
                }

                // После проверки выше existingLeadId гарантированно не null - извлекаем значение
                long leadId = existingLeadId.Value;
                MainWindow.Log($"[AmoCrmService] ✅ Найден лид: ID = {leadId}");

                bool hasFile = !string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath);
                DateTime callTimeForUpdate = callTime ?? DateTime.UtcNow;

                try
                {
                    string? downloadLink = await UpdateLeadAsync(leadId, phoneNumber, isIncoming, durationSeconds, wasAnswered, callLog, audioFilePath, audioFileLink, callTimeForUpdate);

                    if (hasFile)
                    {
                        if (!string.IsNullOrEmpty(downloadLink))
                        {
                            return new ProcessCallResult
                            {
                                Success = true,
                                UploadStatus = AmoCrmUploadStatus.Uploaded,
                                Reason = null,
                                LeadId = leadId
                            };
                        }
                        else
                        {
                            return new ProcessCallResult
                            {
                                Success = true,
                                UploadStatus = AmoCrmUploadStatus.Failed,
                                Reason = "File upload failed (file exists but download link not received)",
                                LeadId = leadId
                            };
                        }
                    }
                    else if (!string.IsNullOrEmpty(audioFilePath))
                    {
                        return new ProcessCallResult
                        {
                            Success = true,
                            UploadStatus = AmoCrmUploadStatus.NotUploaded,
                            Reason = "Recording file not found (may still be processing)",
                            LeadId = leadId
                        };
                    }
                    else
                    {
                        return new ProcessCallResult
                        {
                            Success = true,
                            UploadStatus = AmoCrmUploadStatus.NotUploaded,
                            Reason = "No recording file",
                            LeadId = leadId
                        };
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[AmoCrmService] Error uploading to lead: {ex.Message}");
                    return new ProcessCallResult
                    {
                        Success = false,
                        UploadStatus = AmoCrmUploadStatus.Failed,
                        Reason = $"Upload failed: {ex.Message}",
                        LeadId = leadId
                    };
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error processing call for existing lead: {ex.Message}");
                return new ProcessCallResult
                {
                    Success = false,
                    UploadStatus = AmoCrmUploadStatus.Failed,
                    Reason = $"Error: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Обновляет лид: загружает файл и создаёт примечание звонка.
        /// callLog НЕ добавляется в AmoCRM — остаётся только в логах софтфона.
        /// </summary>
        /// <returns>downloadLink если файл был успешно загружен, null иначе</returns>
        private async Task<string?> UpdateLeadAsync(long leadId, string phoneNumber, bool isIncoming, int durationSeconds, bool wasAnswered, string? callLog, string? audioFilePath, string? audioFileLink, DateTime? callTime = null)
        {
            DateTime callTimeForLock = callTime ?? DateTime.UtcNow;
            string lockKey = $"{leadId}_{phoneNumber}_{callTimeForLock:yyyyMMddHHmmss}";
            SemaphoreSlim lockObj = _updateLeadLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

            await lockObj.WaitAsync().ConfigureAwait(false);
            try
            {
                MainWindow.Log($"[AmoCrmService] Обновление лида: ID = {leadId} (lockKey={lockKey})");

                // Определяем недозвон: звонок не был принят (failed до подключения)
                // КРИТИЧНО: Если был connect (wasAnswered=true), значит произошло подключение - 
                // либо абонент ответил, либо IVR/робот ответил. В этом случае это НЕ недозвон, даже если нет записи.
                // Недозвон = звонок не был принят (wasAnswered=false) И нет записи
                // ВАЖНО: Не проверяем durationSeconds==0, так как звонок может быть отклонен после гудков (486 Busy Here)
                // В этом случае длительность > 0, но звонок все равно не был принят - это недозвон
                bool hasRecording = !string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath);
                
                // Недозвон определяется как: звонок не был принят (wasAnswered=false) И нет записи
                // КРИТИЧНО: Если был connect (wasAnswered=true) - значит был ответ (абонент или IVR), это НЕ недозвон,
                // даже если нет записи (например, из-за проблем с RTP пакетами)
                // Если есть запись - значит был connect, запись должна загружаться
                // Не проверяем durationSeconds, так как звонок может быть отклонен после гудков (486 Busy Here)
                bool isMissedCall = !wasAnswered && !hasRecording;

                // 1) Недозвон: добавляем звонок через POST /api/v4/calls с call_result (текст под карточкой 00:00).
                if (isMissedCall)
                {
                    MainWindow.Log("[AmoCrmService] Missed call detected — adding via /api/v4/calls with call_result='No Answer'");
                    await AddCallViaCallsApiAsync(phoneNumber, isIncoming, 0, "No Answer", 6, callTime); // 6 = нет связи
                    return null;
                }
                
                // 2) Если есть запись - значит был connect (абонент или IVR ответил), загружаем запись
                if (hasRecording)
                {
                    MainWindow.Log($"[AmoCrmService] Call was connected (has recording), uploading recording file (wasAnswered={wasAnswered}, duration={durationSeconds}s)");
                }

                // 2) Обычный звонок: загружаем файл при наличии и создаём call_in / call_out
                string? downloadLink = await AttachFilesToLeadAsync(leadId, audioFilePath);
                if (!string.IsNullOrEmpty(downloadLink))
                {
                    MainWindow.Log($"[AmoCrmService] ✅ Файл прикреплен, download.href: {downloadLink}");
                }
                else if (!string.IsNullOrEmpty(audioFilePath))
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ Файл не был прикреплен или download link не получен");
                    // КРИТИЧНО: Если файл существует, но downloadLink не получен, это ошибка
                    // Не создаем примечание без ссылки, чтобы пользователь мог вручную загрузить файл
                    MainWindow.Log($"[AmoCrmService] ⚠️ Пропускаем создание примечания без ссылки - файл существует, но download link не получен");
                    return null; // Возвращаем null, чтобы вызывающий код знал, что загрузка не удалась
                }

                if (!string.IsNullOrEmpty(downloadLink))
                {
                    // Файл загружен сейчас — call_in/call_out с ссылкой
                    MainWindow.Log($"[AmoCrmService] Добавление {(isIncoming ? "call_in" : "call_out")} с файлом...");
                    await AddCallNoteToLeadAsync(leadId, phoneNumber, isIncoming, durationSeconds, wasAnswered, downloadLink, callTime);
                }
                else if (!string.IsNullOrEmpty(audioFileLink))
                {
                    // Файл загружен ранее — используем переданную ссылку
                    MainWindow.Log($"[AmoCrmService] Добавление {(isIncoming ? "call_in" : "call_out")} с ранее загруженным файлом...");
                    await AddCallNoteToLeadAsync(leadId, phoneNumber, isIncoming, durationSeconds, wasAnswered, audioFileLink, callTime);
                }
                else if (!hasRecording)
                {
                    // Отвеченный звонок без записи - создаем примечание только если файла действительно нет
                    MainWindow.Log($"[AmoCrmService] Добавление {(isIncoming ? "call_in" : "call_out")} без записи (duration={durationSeconds})...");
                    await AddCallNoteToLeadAsync(leadId, phoneNumber, isIncoming, durationSeconds, wasAnswered, null, callTime);
                }
                else
                {
                    // Если файл есть, но downloadLink не получен - не создаем примечание без ссылки
                    MainWindow.Log($"[AmoCrmService] ⚠️ Файл записи существует, но download link не получен - примечание не создано");
                }

                return downloadLink;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error updating lead: {ex.Message}");
                throw;
            }
            finally
            {
                lockObj.Release();
                _updateLeadLocks.TryRemove(lockKey, out _);
            }
        }

        /// <summary>
        /// Обновляет контакт: загружает файл (если есть) и создаёт примечание звонка.
        /// Если у контакта нет открытых сделок — по алгоритму CallGear запись кладём в контакт.
        /// </summary>
        /// <returns>downloadLink если файл был успешно загружен, null иначе</returns>
        private async Task<string?> UpdateContactAsync(long contactId, string phoneNumber, bool isIncoming, int durationSeconds, bool wasAnswered, string? callLog, string? audioFilePath, string? audioFileLink, DateTime? callTime = null)
        {
            DateTime callTimeForLock = callTime ?? DateTime.UtcNow;
            string lockKey = $"contact_{contactId}_{phoneNumber}_{callTimeForLock:yyyyMMddHHmmss}";
            SemaphoreSlim lockObj = _updateLeadLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

            await lockObj.WaitAsync().ConfigureAwait(false);
            try
            {
                MainWindow.Log($"[AmoCrmService] Обновление контакта: ID = {contactId} (lockKey={lockKey})");

                bool hasRecording = !string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath);
                // Недозвон = звонок не был принят (wasAnswered=false) И нет записи
                // ВАЖНО: Не проверяем durationSeconds==0, так как звонок может быть отклонен после гудков (486 Busy Here)
                bool isMissedCall = !wasAnswered && !hasRecording;

                if (isMissedCall)
                {
                    MainWindow.Log("[AmoCrmService] Missed call detected — adding via /api/v4/calls with call_result='No Answer'");
                    await AddCallViaCallsApiAsync(phoneNumber, isIncoming, 0, "No Answer", 6, callTime); // 6 = нет связи
                    return null;
                }

                if (hasRecording)
                {
                    MainWindow.Log($"[AmoCrmService] Call was connected (has recording), uploading recording file to contact (wasAnswered={wasAnswered}, duration={durationSeconds}s)");
                }

                string? downloadLink = await AttachFilesToContactAsync(contactId, audioFilePath);
                if (!string.IsNullOrEmpty(downloadLink))
                {
                    MainWindow.Log($"[AmoCrmService] ✅ Файл прикреплен к контакту, download.href: {downloadLink}");
                }
                else if (!string.IsNullOrEmpty(audioFilePath))
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ Файл не был прикреплен к контакту или download link не получен");
                    // КРИТИЧНО: Если файл существует, но downloadLink не получен, это ошибка
                    // Не создаем примечание без ссылки, чтобы пользователь мог вручную загрузить файл
                    MainWindow.Log($"[AmoCrmService] ⚠️ Пропускаем создание примечания без ссылки - файл существует, но download link не получен");
                    return null; // Возвращаем null, чтобы вызывающий код знал, что загрузка не удалась
                }

                if (!string.IsNullOrEmpty(downloadLink))
                {
                    MainWindow.Log($"[AmoCrmService] Добавление {(isIncoming ? "call_in" : "call_out")} к контакту с файлом...");
                    await AddCallNoteToContactAsync(contactId, phoneNumber, isIncoming, durationSeconds, wasAnswered, downloadLink, callTime);
                }
                else if (!string.IsNullOrEmpty(audioFileLink))
                {
                    MainWindow.Log($"[AmoCrmService] Добавление {(isIncoming ? "call_in" : "call_out")} к контакту с ранее загруженным файлом...");
                    await AddCallNoteToContactAsync(contactId, phoneNumber, isIncoming, durationSeconds, wasAnswered, audioFileLink, callTime);
                }
                else if (!hasRecording)
                {
                    // Создаем примечание без записи только если файла действительно нет
                    MainWindow.Log($"[AmoCrmService] Добавление {(isIncoming ? "call_in" : "call_out")} к контакту без записи (duration={durationSeconds})...");
                    await AddCallNoteToContactAsync(contactId, phoneNumber, isIncoming, durationSeconds, wasAnswered, null, callTime);
                }
                else
                {
                    // Если файл есть, но downloadLink не получен - не создаем примечание без ссылки
                    MainWindow.Log($"[AmoCrmService] ⚠️ Файл записи существует, но download link не получен - примечание не создано");
                }

                return downloadLink;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error updating contact: {ex.Message}");
                throw;
            }
            finally
            {
                lockObj.Release();
                _updateLeadLocks.TryRemove(lockKey, out _);
            }
        }

        public Task<long?> GetLastProcessedLeadIdAsync()
        {
            return Task.FromResult(_lastProcessedLeadId);
        }

        #endregion

        #region Call Notes

        /// <summary>
        /// Добавляет примечание типа common к лиду
        /// </summary>
        private async Task AddNoteToLeadAsync(long leadId, string noteText)
        {
            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                var notesArray = new JArray
                {
                    new JObject
                    {
                        ["note_type"] = "common",
                        ["params"] = new JObject
                        {
                            ["text"] = noteText
                        }
                    }
                };

                string json = notesArray.ToString(Formatting.None);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                string apiUrl = $"{GetApiBaseUrl()}/leads/{leadId}/notes";
                var response = await _httpClient.PostAsync(apiUrl, content);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] Failed to add note to lead: {response.StatusCode} - {responseContent}");
                }
                else
                {
                    MainWindow.Log($"[AmoCrmService] Successfully added note to lead {leadId}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error adding note to lead: {ex.Message}");
            }
        }

        /// <summary>
        /// Добавляет примечание типа common к контакту
        /// </summary>
        private async Task AddNoteToContactAsync(long contactId, string noteText)
        {
            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                var notesArray = new JArray
                {
                    new JObject
                    {
                        ["note_type"] = "common",
                        ["params"] = new JObject
                        {
                            ["text"] = noteText
                        }
                    }
                };

                string json = notesArray.ToString(Formatting.None);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                string apiUrl = $"{GetApiBaseUrl()}/contacts/{contactId}/notes";
                var response = await _httpClient.PostAsync(apiUrl, content);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] Failed to add note to contact: {response.StatusCode} - {responseContent}");
                }
                else
                {
                    MainWindow.Log($"[AmoCrmService] Successfully added note to contact {contactId}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error adding note to contact: {ex.Message}");
            }
        }

        /// <summary>
        /// Добавляет звонок через POST /api/v4/calls (как CallGear).
        /// Позволяет передать call_result (текст под карточкой, например "No Answer") и call_status.
        /// Привязка к сделке/контакту выполняется по алгоритму Kommo по номеру телефона (entity_id указать нельзя).
        /// Документация: https://developers.kommo.com/reference/add-calls
        /// </summary>
        /// <param name="callStatus">6 = нет связи, 7 = линия занята (остальные: 1–5 см. документацию)</param>
        private async Task AddCallViaCallsApiAsync(string phoneNumber, bool isIncoming, int durationSeconds, string? callResult = null, int? callStatus = null, DateTime? callTime = null)
        {
            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                string direction = isIncoming ? "inbound" : "outbound";
                string uniqSource = $"{direction}_{NormalizePhoneNumber(phoneNumber)}_{(callTime ?? DateTime.UtcNow):yyyyMMddHHmmss}";
                string uniq;
                using (var sha = SHA256.Create())
                {
                    byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(uniqSource));
                    uniq = BitConverter.ToString(hashBytes, 0, 8).Replace("-", "").ToLowerInvariant();
                }

                var callPayload = new JObject
                {
                    ["direction"] = direction,
                    ["duration"] = durationSeconds,
                    ["source"] = "Callspire",
                    ["phone"] = phoneNumber,
                    ["uniq"] = uniq
                };
                if (!string.IsNullOrEmpty(callResult))
                    callPayload["call_result"] = callResult;
                if (callStatus.HasValue)
                    callPayload["call_status"] = callStatus.Value;
                if (_currentUserId.HasValue)
                {
                    callPayload["responsible_user_id"] = _currentUserId.Value;
                    callPayload["created_by"] = _currentUserId.Value;
                }
                if (callTime.HasValue)
                    callPayload["created_at"] = (int)(callTime.Value.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

                var body = new JArray { callPayload };
                var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                string apiUrl = $"{GetApiBaseUrl()}/calls";
                MainWindow.Log($"[AmoCrmService] 📝 POST /api/v4/calls (call_result={callResult ?? "null"}, call_status={callStatus})");
                var response = await _httpClient.PostAsync(apiUrl, content);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] ❌ Ошибка добавления звонка через /calls: {response.StatusCode} - {responseContent}");
                }
                else
                {
                    MainWindow.Log($"[AmoCrmService] ✅ Звонок добавлен через /api/v4/calls (uniq={uniq})");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error adding call via /calls: {ex.Message}");
            }
        }

        /// <summary>
        /// Добавляет примечание типа call_in или call_out к лиду.
        /// Использует params.link для кнопок "Прослушать" и "Скачать" в AmoCRM.
        /// uniq генерируется через SHA256 от стабильных полей для идемпотентности.
        /// </summary>
        private async Task AddCallNoteToLeadAsync(long leadId, string phoneNumber, bool isIncoming, int durationSeconds, bool wasAnswered, string? audioFileLink = null, DateTime? callTime = null)
        {
            try
            {
                if (!_currentUserId.HasValue)
                {
                    MainWindow.Log("[AmoCrmService] Current user ID not available, cannot create call note");
                    return;
                }

                // Генерируем стабильный уникальный идентификатор звонка (SHA256)
                DateTime callTimeForUniq = callTime ?? DateTime.UtcNow;
                string direction = isIncoming ? "in" : "out";
                string normalizedForUniq = NormalizePhoneNumber(phoneNumber);
                string uniqSource = $"{leadId}_{direction}_{normalizedForUniq}_{callTimeForUniq:yyyyMMddHHmmss}";
                string uniq;
                using (var sha = SHA256.Create())
                {
                    byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(uniqSource));
                    uniq = BitConverter.ToString(hashBytes, 0, 8).Replace("-", "").ToLowerInvariant();
                }

                // Блокировка для предотвращения дубликатов
                string lockKey = $"{leadId}_{uniq}";
                SemaphoreSlim lockObj = _callNoteLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

                await lockObj.WaitAsync().ConfigureAwait(false);
                try
                {
                    await EnsureValidTokenAsync();
                    await RateLimitAsync();
                    MainWindow.Log($"[AmoCrmService] 📝 Создание заметки звонка: leadId={leadId}, phone={phoneNumber}, callTime={callTimeForUniq:yyyy-MM-dd HH:mm:ss}, uniq={uniq}, type={(isIncoming ? "call_in" : "call_out")}");

                    string noteType = isIncoming ? "call_in" : "call_out";

                    var callParams = new JObject
                    {
                        ["uniq"] = uniq,
                        ["duration"] = durationSeconds,
                        ["source"] = "Callspire",
                        ["phone"] = phoneNumber
                    };

                    if (!string.IsNullOrEmpty(audioFileLink))
                    {
                        callParams["link"] = audioFileLink;
                    }

                    // Комментарий (comment) НЕ поддерживается API Kommo для call_in/call_out — возвращает 400 FieldNotExpected

                    var noteObject = new JObject
                    {
                        ["note_type"] = noteType,
                        ["created_by"] = _currentUserId.Value,
                        ["responsible_user_id"] = _currentUserId.Value,
                        ["params"] = callParams
                    };

                    var notesArray = new JArray { noteObject };

                    string json = notesArray.ToString(Formatting.None);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    string apiUrl = $"{GetApiBaseUrl()}/leads/{leadId}/notes";
                    MainWindow.Log($"[AmoCrmService] 📝 POST {apiUrl}");
                    var response = await _httpClient.PostAsync(apiUrl, content);
                    string responseContent = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        MainWindow.Log($"[AmoCrmService] ❌ Ошибка создания заметки в лиде {leadId}: {response.StatusCode} - {responseContent}");
                    }
                    else
                    {
                        MainWindow.Log($"[AmoCrmService] ✅ Заметка {noteType} успешно создана в лиде {leadId} (uniq={uniq})");
                    }
                }
                finally
                {
                    lockObj.Release();
                    _callNoteLocks.TryRemove(lockKey, out _);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error adding call note to lead: {ex.Message}");
            }
        }

        /// <summary>
        /// Добавляет примечание типа call_in или call_out к контакту.
        /// Использует params.link для кнопок "Прослушать" и "Скачать" в AmoCRM.
        /// </summary>
        private async Task AddCallNoteToContactAsync(long contactId, string phoneNumber, bool isIncoming, int durationSeconds, bool wasAnswered, string? audioFileLink = null, DateTime? callTime = null)
        {
            try
            {
                if (!_currentUserId.HasValue)
                {
                    MainWindow.Log("[AmoCrmService] Current user ID not available, cannot create call note for contact");
                    return;
                }

                DateTime callTimeForUniq = callTime ?? DateTime.UtcNow;
                string direction = isIncoming ? "in" : "out";
                string normalizedForUniq = NormalizePhoneNumber(phoneNumber);
                string uniqSource = $"contact_{contactId}_{direction}_{normalizedForUniq}_{callTimeForUniq:yyyyMMddHHmmss}";
                string uniq;
                using (var sha = SHA256.Create())
                {
                    byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(uniqSource));
                    uniq = BitConverter.ToString(hashBytes, 0, 8).Replace("-", "").ToLowerInvariant();
                }

                string lockKey = $"contact_{contactId}_{uniq}";
                SemaphoreSlim lockObj = _callNoteLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
                await lockObj.WaitAsync().ConfigureAwait(false);
                try
                {
                    await EnsureValidTokenAsync();
                    await RateLimitAsync();

                    string noteType = isIncoming ? "call_in" : "call_out";
                    var callParams = new JObject
                    {
                        ["uniq"] = uniq,
                        ["duration"] = durationSeconds,
                        ["source"] = "Callspire",
                        ["phone"] = phoneNumber
                    };

                    if (!string.IsNullOrEmpty(audioFileLink))
                    {
                        callParams["link"] = audioFileLink;
                    }

                    var noteObject = new JObject
                    {
                        ["note_type"] = noteType,
                        ["created_by"] = _currentUserId.Value,
                        ["responsible_user_id"] = _currentUserId.Value,
                        ["params"] = callParams
                    };

                    var notesArray = new JArray { noteObject };
                    string json = notesArray.ToString(Formatting.None);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    string apiUrl = $"{GetApiBaseUrl()}/contacts/{contactId}/notes";
                    var response = await _httpClient.PostAsync(apiUrl, content);
                    string responseContent = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        MainWindow.Log($"[AmoCrmService] Failed to add call note to contact: {response.StatusCode} - {responseContent}");
                    }
                    else
                    {
                        MainWindow.Log($"[AmoCrmService] Successfully added {noteType} note to contact {contactId} (uniq={uniq})");
                    }
                }
                finally
                {
                    lockObj.Release();
                    _callNoteLocks.TryRemove(lockKey, out _);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error adding call note to contact: {ex.Message}");
            }
        }

        #endregion

        #region File Upload

        /// <summary>
        /// Прикрепляет файл записи к лиду. Ждёт до 30 секунд, пока файл появится на диске.
        /// </summary>
        private async Task<string?> AttachFilesToLeadAsync(long leadId, string? audioFilePath)
        {
            if (string.IsNullOrEmpty(audioFilePath))
            {
                MainWindow.Log($"[AmoCrmService] AttachFilesToLeadAsync: audioFilePath is null or empty");
                return null;
            }

            // Ждем файл до 30 секунд (запись может конвертироваться асинхронно)
            int maxAttempts = 30;
            int attempt = 0;
            while (attempt < maxAttempts && !File.Exists(audioFilePath))
            {
                attempt++;
                if (attempt % 5 == 0 || attempt == maxAttempts)
                {
                    MainWindow.Log($"[AmoCrmService] Waiting for recording file (attempt {attempt}/{maxAttempts}): {Path.GetFileName(audioFilePath)}");
                }
                await Task.Delay(1000);
            }

            if (!File.Exists(audioFilePath))
            {
                MainWindow.Log($"[AmoCrmService] ⚠️ Recording file not found after {maxAttempts} attempts: {Path.GetFileName(audioFilePath)}");
                if (Directory.Exists(Path.GetDirectoryName(audioFilePath)))
                {
                    var files = Directory.GetFiles(Path.GetDirectoryName(audioFilePath)!, "*" + Path.GetFileNameWithoutExtension(audioFilePath) + "*");
                    if (files.Any())
                    {
                        MainWindow.Log($"[AmoCrmService] Found files in directory: {string.Join(", ", files.Select(f => Path.GetFileName(f)))}");
                    }
                }
                return null;
            }

            var fileInfo = new FileInfo(audioFilePath);
            
            // КРИТИЧНО: Проверяем размер файла - пропускаем пустые файлы
            if (fileInfo.Length == 0)
            {
                MainWindow.Log($"[AmoCrmService] ⚠️ Recording file is empty (0 bytes), skipping upload to lead {leadId}");
                return null;
            }
            
            MainWindow.Log($"[AmoCrmService] ✅ Recording file found ({fileInfo.Length / 1024} KB), attaching to lead");
            return await UploadFileToLeadAsync(leadId, audioFilePath);
        }

        /// <summary>
        /// Прикрепляет файл записи к контакту. Ждёт до 30 секунд, пока файл появится на диске.
        /// </summary>
        private async Task<string?> AttachFilesToContactAsync(long contactId, string? audioFilePath)
        {
            if (string.IsNullOrEmpty(audioFilePath))
            {
                MainWindow.Log($"[AmoCrmService] AttachFilesToContactAsync: audioFilePath is null or empty");
                return null;
            }

            int maxAttempts = 30;
            int attempt = 0;
            while (attempt < maxAttempts && !File.Exists(audioFilePath))
            {
                attempt++;
                if (attempt % 5 == 0 || attempt == maxAttempts)
                {
                    MainWindow.Log($"[AmoCrmService] Waiting for recording file (attempt {attempt}/{maxAttempts}): {Path.GetFileName(audioFilePath)}");
                }
                await Task.Delay(1000);
            }

            if (!File.Exists(audioFilePath))
            {
                MainWindow.Log($"[AmoCrmService] ⚠️ Recording file not found after {maxAttempts} attempts: {Path.GetFileName(audioFilePath)}");
                return null;
            }

            var fileInfo = new FileInfo(audioFilePath);
            
            // КРИТИЧНО: Проверяем размер файла - пропускаем пустые файлы
            if (fileInfo.Length == 0)
            {
                MainWindow.Log($"[AmoCrmService] ⚠️ Recording file is empty (0 bytes), skipping upload to contact {contactId}");
                return null;
            }
            
            MainWindow.Log($"[AmoCrmService] ✅ Recording file found ({fileInfo.Length / 1024} KB), attaching to contact");
            return await UploadFileToContactAsync(contactId, audioFilePath);
        }

        /// <summary>
        /// Публичный метод для ручной загрузки файла записи к лиду
        /// </summary>
        public async Task<bool> ManuallyUploadRecordingToLeadAsync(long leadId, string filePath, string phoneNumber, bool isIncoming, int durationSeconds, bool wasAnswered = false)
        {
            try
            {
                if (!IsInitialized)
                {
                    MainWindow.Log("[AmoCrmService] Service not initialized, cannot upload recording");
                    return false;
                }

                if (!File.Exists(filePath))
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ File does not exist: {Path.GetFileName(filePath)}");
                    return false;
                }

                MainWindow.Log($"[AmoCrmService] Manual upload: Uploading recording file to lead {leadId}: {Path.GetFileName(filePath)}");
                string? downloadLink = await UploadFileToLeadAsync(leadId, filePath);

                if (!string.IsNullOrEmpty(downloadLink))
                {
                    MainWindow.Log($"[AmoCrmService] ✅ File uploaded successfully, download link: {downloadLink}");
                    MainWindow.Log($"[AmoCrmService] Adding call note ({(isIncoming ? "call_in" : "call_out")}) with file link...");
                    await AddCallNoteToLeadAsync(leadId, phoneNumber, isIncoming, durationSeconds, wasAnswered, downloadLink, null);
                    MainWindow.Log($"[AmoCrmService] ✅ Successfully uploaded recording and created call note for lead {leadId}");

                    return true;
                }
                else
                {
                    MainWindow.Log($"[AmoCrmService] ❌ Failed to upload recording file to lead {leadId}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error in manual upload: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Публичный метод для ручного создания карточки недозвона (без записи).
        /// Создаёт карточку звонка call_in/call_out с duration=0, чтобы AmoCRM фиксировала как проделанную работу.
        /// </summary>
        public async Task<bool> ManuallyUploadMissedCallToLeadAsync(long leadId, string phoneNumber, bool isIncoming)
        {
            try
            {
                if (!IsInitialized)
                {
                    MainWindow.Log("[AmoCrmService] Service not initialized, cannot upload missed call");
                    return false;
                }

                MainWindow.Log($"[AmoCrmService] Manual upload: creating missed call card (call_in/call_out, duration=0) for lead {leadId}, phone={phoneNumber}");

                await AddCallNoteToLeadAsync(leadId, phoneNumber, isIncoming, 0, false, null, null);

                MainWindow.Log($"[AmoCrmService] ✅ Successfully created missed call card for lead {leadId}");

                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error in manual missed call upload: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Загружает файл к лиду через drive_url и возвращает download.href
        /// </summary>
        private async Task<string?> UploadFileToLeadAsync(long leadId, string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ File does not exist: {Path.GetFileName(filePath)}");
                    return null;
                }

                // Шаг 1: Загружаем файл и получаем file_uuid
                string? fileUuid = await UploadFileAsync(filePath);
                if (string.IsNullOrEmpty(fileUuid))
                {
                    MainWindow.Log($"[AmoCrmService] ❌ Failed to upload file: {Path.GetFileName(filePath)}");
                    return null;
                }

                // Шаг 2: Получаем download.href
                string? downloadLink = await GetFileDownloadLinkAsync(fileUuid);
                if (string.IsNullOrEmpty(downloadLink))
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ Failed to get download link for UUID: {fileUuid}");
                }

                // Шаг 3: Прикрепляем файл к лиду
                await AttachUploadToLeadAsync(leadId, fileUuid);

                return downloadLink;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] ❌ Error uploading file to lead: {ex.Message}");
                MainWindow.Log($"[AmoCrmService] Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Загружает файл к контакту через drive_url и возвращает download.href
        /// </summary>
        private async Task<string?> UploadFileToContactAsync(long contactId, string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ File does not exist: {Path.GetFileName(filePath)}");
                    return null;
                }

                string? fileUuid = await UploadFileAsync(filePath);
                if (string.IsNullOrEmpty(fileUuid))
                {
                    MainWindow.Log($"[AmoCrmService] ❌ Failed to upload file: {Path.GetFileName(filePath)}");
                    return null;
                }

                string? downloadLink = await GetFileDownloadLinkAsync(fileUuid);
                if (string.IsNullOrEmpty(downloadLink))
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ Failed to get download link for UUID: {fileUuid}");
                }

                await AttachUploadToContactAsync(contactId, fileUuid);
                return downloadLink;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] ❌ Error uploading file to contact: {ex.Message}");
                MainWindow.Log($"[AmoCrmService] Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Получает download.href через GET /v1.0/files/{file_uuid}
        /// </summary>
        private async Task<string?> GetFileDownloadLinkAsync(string fileUuid)
        {
            try
            {
                if (string.IsNullOrEmpty(_driveUrl))
                {
                    MainWindow.Log("[AmoCrmService] Drive URL not loaded, cannot get file download link");
                    return null;
                }

                await EnsureValidTokenAsync();
                await RateLimitAsync();
                string fileInfoUrl = $"{_driveUrl}/v1.0/files/{fileUuid}";
                var response = await _httpClient.GetAsync(fileInfoUrl);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] ❌ Failed to get file info: {response.StatusCode}");
                    MainWindow.Log($"[AmoCrmService] Response content: {responseContent}");
                    
                    // Если Unauthorized, пытаемся принудительно обновить токен и повторить запрос
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        MainWindow.Log($"[AmoCrmService] ⚠️ Unauthorized error when getting download link - forcing token refresh...");
                        try
                        {
                            // КРИТИЧНО: Принудительно обновляем токен при ошибке Unauthorized
                            await EnsureValidTokenAsync(forceRefresh: true);
                            
                            // Повторяем запрос после обновления токена
                            var retryResponse = await _httpClient.GetAsync(fileInfoUrl);
                            string retryResponseContent = await retryResponse.Content.ReadAsStringAsync();
                            if (retryResponse.IsSuccessStatusCode)
                            {
                                MainWindow.Log($"[AmoCrmService] ✅ Retry after token refresh succeeded");
                                response = retryResponse;
                                responseContent = retryResponseContent;
                            }
                            else
                            {
                                MainWindow.Log($"[AmoCrmService] ❌ Retry after token refresh failed: {retryResponse.StatusCode}");
                                MainWindow.Log($"[AmoCrmService] Retry response content: {retryResponseContent}");
                                return null;
                            }
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"[AmoCrmService] ❌ Error refreshing token: {ex.Message}");
                            MainWindow.Log($"[AmoCrmService] Stack trace: {ex.StackTrace}");
                            return null;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }

                var fileData = JObject.Parse(responseContent);

                // Пробуем разные пути в ответе
                string? downloadLink = fileData["_links"]?["download"]?["href"]?.Value<string>();
                if (string.IsNullOrEmpty(downloadLink))
                    downloadLink = fileData["download"]?["href"]?.Value<string>();
                if (string.IsNullOrEmpty(downloadLink))
                    downloadLink = fileData["href"]?.Value<string>();
                
                if (string.IsNullOrEmpty(downloadLink))
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ Download link not found in response. Response structure: {fileData.ToString(Formatting.Indented)}");
                }
                else
                {
                    MainWindow.Log($"[AmoCrmService] ✅ Download link retrieved: {downloadLink}");
                }

                return downloadLink;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error getting file download link: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Загружает файл частями (chunked upload) через drive_url и возвращает file_uuid.
        /// Retry с экспоненциальным backoff для каждой части.
        /// </summary>
        private async Task<string?> UploadFileAsync(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(_driveUrl))
                {
                    MainWindow.Log("[AmoCrmService] Drive URL not loaded, cannot upload file");
                    return null;
                }

                await EnsureValidTokenAsync();
                string fileName = Path.GetFileName(filePath);
                long fileSize = new FileInfo(filePath).Length;
                
                // КРИТИЧНО: Проверяем размер файла перед загрузкой - пропускаем пустые файлы
                if (fileSize == 0)
                {
                    MainWindow.Log($"[AmoCrmService] ⚠️ File is empty (0 bytes), skipping upload: {fileName}");
                    return null;
                }
                
                string fileExtension = Path.GetExtension(filePath).ToLowerInvariant();

                string contentType = fileExtension switch
                {
                    ".mp3" => "audio/mpeg",
                    ".wav" => "audio/wav",
                    ".webm" => "audio/webm",
                    ".m4a" => "audio/mp4",
                    _ => "audio/mpeg"
                };

                // Шаг 1: Создаем сессию загрузки
                await RateLimitAsync();
                string sessionUrl = $"{_driveUrl}/v1.0/sessions";
                var sessionRequest = new
                {
                    file_name = fileName,
                    file_size = fileSize,
                    content_type = contentType
                };

                string sessionJson = JsonConvert.SerializeObject(sessionRequest);
                var sessionContent = new StringContent(sessionJson, Encoding.UTF8, "application/json");

                var sessionResponse = await _httpClient.PostAsync(sessionUrl, sessionContent);
                string sessionResponseContent = await sessionResponse.Content.ReadAsStringAsync();

                if (!sessionResponse.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] ❌ Failed to create upload session: {sessionResponse.StatusCode}");
                    MainWindow.Log($"[AmoCrmService] Response content: {sessionResponseContent}");
                    // Если Unauthorized, возможно токен истек или недействителен
                    if (sessionResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        MainWindow.Log($"[AmoCrmService] ⚠️ Unauthorized error - token may be expired or invalid. Forcing token refresh...");
                        // КРИТИЧНО: Принудительно обновляем токен при ошибке Unauthorized
                        try
                        {
                            await EnsureValidTokenAsync(forceRefresh: true);
                            // Повторяем запрос после обновления токена
                            // КРИТИЧНО: Создаем новый StringContent, так как старый может быть уже прочитан
                            var retrySessionContent = new StringContent(sessionJson, Encoding.UTF8, "application/json");
                            var retrySessionResponse = await _httpClient.PostAsync(sessionUrl, retrySessionContent);
                            string retrySessionResponseContent = await retrySessionResponse.Content.ReadAsStringAsync();
                            if (retrySessionResponse.IsSuccessStatusCode)
                            {
                                MainWindow.Log($"[AmoCrmService] ✅ Retry after token refresh succeeded");
                                sessionResponse = retrySessionResponse;
                                sessionResponseContent = retrySessionResponseContent;
                            }
                            else
                            {
                                MainWindow.Log($"[AmoCrmService] ❌ Retry after token refresh failed: {retrySessionResponse.StatusCode}");
                                MainWindow.Log($"[AmoCrmService] Retry response content: {retrySessionResponseContent}");
                                return null;
                            }
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"[AmoCrmService] ❌ Error refreshing token: {ex.Message}");
                            MainWindow.Log($"[AmoCrmService] Stack trace: {ex.StackTrace}");
                            return null;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }

                var sessionData = JObject.Parse(sessionResponseContent);
                string? uploadUrl = sessionData["upload_url"]?.Value<string>();
                long maxPartSize = sessionData["max_part_size"]?.Value<long>() ?? 524288;

                if (string.IsNullOrEmpty(uploadUrl))
                {
                    MainWindow.Log($"[AmoCrmService] ❌ upload_url not found in session response");
                    return null;
                }

                // Шаг 2: Загружаем файл частями с retry
                using (var fileStream = File.OpenRead(filePath))
                {
                    long totalUploaded = 0;
                    string? currentUploadUrl = uploadUrl;
                    int partNumber = 0;
                    const int maxRetries = 3;

                    while (totalUploaded < fileSize)
                    {
                        partNumber++;
                        long remainingBytes = fileSize - totalUploaded;
                        long currentPartSize = Math.Min(maxPartSize, remainingBytes);

                        byte[] buffer = new byte[currentPartSize];
                        int bytesRead = await fileStream.ReadAsync(buffer, 0, (int)currentPartSize);

                        if (bytesRead == 0)
                            break;

                        bool partUploaded = false;
                        for (int retry = 0; retry < maxRetries; retry++)
                        {
                            try
                            {
                                var partContent = new ByteArrayContent(buffer, 0, bytesRead);
                                partContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                                var partResponse = await _httpClient.PostAsync(currentUploadUrl, partContent);
                                string partResponseContent = await partResponse.Content.ReadAsStringAsync();

                                if (partResponse.IsSuccessStatusCode)
                                {
                                    partUploaded = true;

                                    if (totalUploaded + bytesRead < fileSize)
                                    {
                                        // Промежуточная часть — получаем next_url
                                        var partData = JObject.Parse(partResponseContent);
                                        currentUploadUrl = partData["next_url"]?.Value<string>();
                                        if (string.IsNullOrEmpty(currentUploadUrl))
                                        {
                                            MainWindow.Log($"[AmoCrmService] ❌ next_url not found in part response");
                                            return null;
                                        }
                                    }
                                    else
                                    {
                                        // Последняя часть — получаем file_uuid
                                        var finalData = JObject.Parse(partResponseContent);
                                        string? fileUuid = finalData["uuid"]?.Value<string>()
                                                        ?? finalData["file_uuid"]?.Value<string>();
                                        if (!string.IsNullOrEmpty(fileUuid))
                                        {
                                            MainWindow.Log($"[AmoCrmService] ✅ File uploaded successfully, UUID: {fileUuid}");
                                            return fileUuid;
                                        }
                                        else
                                        {
                                            MainWindow.Log($"[AmoCrmService] ❌ File UUID not found in final response");
                                            return null;
                                        }
                                    }
                                    break;
                                }
                                else
                                {
                                    if (retry < maxRetries - 1)
                                    {
                                        MainWindow.Log($"[AmoCrmService] ⚠️ Failed to upload part {partNumber} (attempt {retry + 1}/{maxRetries}): {partResponse.StatusCode}, retrying...");
                                        await Task.Delay(1000 * (retry + 1));
                                    }
                                    else
                                    {
                                        MainWindow.Log($"[AmoCrmService] ❌ Failed to upload part {partNumber} after {maxRetries} attempts: {partResponse.StatusCode} - {partResponseContent}");
                                        return null;
                                    }
                                }
                            }
                            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                            {
                                if (retry < maxRetries - 1)
                                {
                                    MainWindow.Log($"[AmoCrmService] ⚠️ Timeout uploading part {partNumber} (attempt {retry + 1}/{maxRetries}), retrying...");
                                    await Task.Delay(2000 * (retry + 1));
                                }
                                else
                                {
                                    MainWindow.Log($"[AmoCrmService] ❌ Timeout uploading part {partNumber} after {maxRetries} attempts");
                                    return null;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (retry < maxRetries - 1)
                                {
                                    MainWindow.Log($"[AmoCrmService] ⚠️ Error uploading part {partNumber} (attempt {retry + 1}/{maxRetries}): {ex.Message}, retrying...");
                                    await Task.Delay(1000 * (retry + 1));
                                }
                                else
                                {
                                    MainWindow.Log($"[AmoCrmService] ❌ Error uploading part {partNumber} after {maxRetries} attempts: {ex.Message}");
                                    return null;
                                }
                            }
                        }

                        if (!partUploaded)
                            return null;

                        totalUploaded += bytesRead;

                        if (partNumber % 5 == 0 || totalUploaded >= fileSize)
                        {
                            MainWindow.Log($"[AmoCrmService] Uploaded {partNumber} part(s): {totalUploaded}/{fileSize} bytes ({totalUploaded * 100 / fileSize}%)");
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error uploading file: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Прикрепляет загруженный файл к лиду (PUT /api/v4/leads/{id}/files)
        /// </summary>
        private async Task AttachUploadToLeadAsync(long leadId, string fileUuid)
        {
            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                MainWindow.Log($"[AmoCrmService] 🔗 Прикрепление файла (UUID: {fileUuid}) к лиду {leadId}...");

                var attachmentData = new[]
                {
                    new { file_uuid = fileUuid }
                };

                string json = JsonConvert.SerializeObject(attachmentData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                string apiUrl = $"{GetApiBaseUrl()}/leads/{leadId}/files";
                MainWindow.Log($"[AmoCrmService] 🔗 PUT {apiUrl}");
                var response = await _httpClient.PutAsync(apiUrl, content);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == System.Net.HttpStatusCode.Accepted || response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] ✅ Файл успешно прикреплен к лиду {leadId}");
                }
                else
                {
                    MainWindow.Log($"[AmoCrmService] ❌ Ошибка прикрепления файла к лиду {leadId}: {response.StatusCode} - {responseContent}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] ❌ Ошибка прикрепления файла к лиду {leadId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Прикрепляет загруженный файл к контакту (PUT /api/v4/contacts/{id}/files)
        /// </summary>
        private async Task AttachUploadToContactAsync(long contactId, string fileUuid)
        {
            try
            {
                await EnsureValidTokenAsync();
                await RateLimitAsync();

                var attachmentData = new[]
                {
                    new { file_uuid = fileUuid }
                };

                string json = JsonConvert.SerializeObject(attachmentData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                string apiUrl = $"{GetApiBaseUrl()}/contacts/{contactId}/files";
                var response = await _httpClient.PutAsync(apiUrl, content);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == System.Net.HttpStatusCode.Accepted || response.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[AmoCrmService] Successfully attached file to contact {contactId}");
                }
                else
                {
                    MainWindow.Log($"[AmoCrmService] Failed to attach file to contact: {response.StatusCode} - {responseContent}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmService] Error attaching upload to contact: {ex.Message}");
            }
        }

        #endregion

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}