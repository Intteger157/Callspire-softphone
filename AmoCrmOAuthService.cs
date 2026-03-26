using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Softphone
{
    /// <summary>
    /// Сервис для OAuth 2.0 авторизации в Kommo (AmoCRM)
    /// Реализует Authorization Code Flow согласно документации Kommo API
    /// </summary>
    public class AmoCrmOAuthService
    {
        private HttpListener? _httpListener;
        private string? _authorizationCode;
        private string? _referer;
        private readonly object _lockObject = new object();
        private bool _isListening = false;

        /// <summary>
        /// Начинает OAuth авторизацию: открывает браузер с URL авторизации и ожидает redirect
        /// </summary>
        /// <param name="subdomain">Поддомен Kommo (опционально, не используется для авторизации - извлекается из referer)</param>
        /// <param name="clientId">OAuth Client ID</param>
        /// <param name="redirectUri">Redirect URI (например: "http://localhost:8080/callback")</param>
        /// <param name="state">State parameter для безопасности (опционально)</param>
        /// <returns>Authorization code и referer (поддомен пользователя)</returns>
        public async Task<(string? code, string? referer)> AuthorizeAsync(string subdomain, string clientId, string redirectUri, string? state = null)
        {
            try
            {
                // Генерируем state если не передан
                if (string.IsNullOrEmpty(state))
                {
                    state = Guid.NewGuid().ToString("N");
                }

                // Формируем URL авторизации
                // По документации AmoCRM: https://www.amocrm.ru/oauth (для всех аккаунтов)
                // После авторизации пользователь будет перенаправлен на redirect_uri с параметром referer (subdomain аккаунта)
                string authUrl = $"https://www.amocrm.ru/oauth?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}";

                MainWindow.Log($"[AmoCrmOAuthService] Starting OAuth authorization...");
                MainWindow.Log($"[AmoCrmOAuthService] Authorization URL: {authUrl}");
                MainWindow.Log($"[AmoCrmOAuthService] Note: Authorization always uses www.amocrm.ru, subdomain will be returned in 'referer' parameter");

                // Запускаем локальный HTTP сервер для обработки redirect
                // ConfigureAwait(false) — чтобы НЕ захватывать UI SynchronizationContext.
                // Без этого StopLocalServer() (HttpListener.Stop/Close) выполняется на UI потоке
                // и блокирует его на 30-60 секунд, пока TCP-соединение от браузера не закроется.
                string? localRedirectUri = await StartLocalServerAsync(redirectUri).ConfigureAwait(false);
                if (localRedirectUri == null)
                {
                    MainWindow.Log("[AmoCrmOAuthService] Failed to start local HTTP server");
                    return (null, null);
                }

                // Если redirectUri отличается от локального, используем локальный
                if (localRedirectUri != redirectUri)
                {
                    authUrl = authUrl.Replace(Uri.EscapeDataString(redirectUri), Uri.EscapeDataString(localRedirectUri));
                    MainWindow.Log($"[AmoCrmOAuthService] Using local redirect URI: {localRedirectUri}");
                }

                // Открываем браузер с URL авторизации
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = authUrl,
                        UseShellExecute = true
                    });
                    MainWindow.Log("[AmoCrmOAuthService] Browser opened, waiting for authorization...");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[AmoCrmOAuthService] Failed to open browser: {ex.Message}");
                    StopLocalServer();
                    return (null, null);
                }

                // Ждем получения authorization code (таймаут 5 минут)
                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5));
                var codeReceivedTask = WaitForAuthorizationCodeAsync();

                // ConfigureAwait(false) — критично! Без него continuation (StopLocalServer)
                // выполнится на UI потоке, а HttpListener.Stop() блокирует поток на десятки секунд.
                var completedTask = await Task.WhenAny(codeReceivedTask, timeoutTask).ConfigureAwait(false);
                
                if (completedTask == timeoutTask)
                {
                    MainWindow.Log("[AmoCrmOAuthService] Authorization timeout (5 minutes)");
                    StopLocalServer();
                    return (null, null);
                }

                var result = await codeReceivedTask.ConfigureAwait(false);
                StopLocalServer();
                
                if (result.code != null)
                {
                    MainWindow.Log($"[AmoCrmOAuthService] Authorization code received successfully");
                    MainWindow.Log($"[AmoCrmOAuthService] Referer: {result.referer}");
                }
                else
                {
                    MainWindow.Log("[AmoCrmOAuthService] Authorization failed or was cancelled");
                }

                return result;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmOAuthService] Error during authorization: {ex.Message}");
                StopLocalServer();
                return (null, null);
            }
        }

        /// <summary>
        /// Обменивает authorization code на access_token и refresh_token
        /// </summary>
        public async Task<(string? accessToken, string? refreshToken, int? expiresIn)> ExchangeCodeForTokensAsync(string subdomain, string clientId, string clientSecret, string authorizationCode, string redirectUri)
        {
            try
            {
                // Для обмена кода на токены используем subdomain пользователя (из параметра referer)
                // Нормализуем subdomain для формирования правильного URL
                string normalizedSubdomain = NormalizeSubdomain(subdomain);
                string tokenUrl = $"https://{normalizedSubdomain}.amocrm.ru/oauth2/access_token";

                MainWindow.Log($"[AmoCrmOAuthService] Exchanging authorization code for tokens...");
                MainWindow.Log($"[AmoCrmOAuthService] Token URL: {tokenUrl}");

                using (var httpClient = new HttpClient())
                {
                    // Kommo/Amo ожидет параметры в формате application/x-www-form-urlencoded
                    // (иначе параметры могут не распознаться корректно).
                    var requestData = new Dictionary<string, string>
                    {
                        { "client_id", clientId },
                        { "client_secret", clientSecret },
                        { "grant_type", "authorization_code" },
                        { "code", authorizationCode },
                        { "redirect_uri", redirectUri }
                    };

                    var content = new FormUrlEncodedContent(requestData);

                    var response = await httpClient.PostAsync(tokenUrl, content).ConfigureAwait(false);
                    string responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        MainWindow.Log($"[AmoCrmOAuthService] Token exchange failed: {response.StatusCode} - {responseContent}");
                        return (null, null, null);
                    }

                    var json = JObject.Parse(responseContent);
                    string? accessToken = json["access_token"]?.Value<string>();
                    string? refreshToken = json["refresh_token"]?.Value<string>();
                    int? expiresIn = json["expires_in"]?.Value<int>();

                    if (accessToken != null)
                    {
                        MainWindow.Log($"[AmoCrmOAuthService] Tokens received successfully (expires in {expiresIn} seconds)");
                    }
                    else
                    {
                        MainWindow.Log("[AmoCrmOAuthService] Access token not found in response");
                    }

                    return (accessToken, refreshToken, expiresIn);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmOAuthService] Error exchanging code for tokens: {ex.Message}");
                return (null, null, null);
            }
        }

        /// <summary>
        /// Обновляет access_token используя refresh_token
        /// </summary>
        public async Task<(string? accessToken, string? refreshToken, int? expiresIn)> RefreshTokenAsync(string subdomain, string clientId, string clientSecret, string refreshToken)
        {
            try
            {
                // Для обновления токена используем subdomain пользователя
                string normalizedSubdomain = NormalizeSubdomain(subdomain);
                string tokenUrl = $"https://{normalizedSubdomain}.amocrm.ru/oauth2/access_token";

                MainWindow.Log($"[AmoCrmOAuthService] Refreshing access token...");

                using (var httpClient = new HttpClient())
                {
                    var requestData = new Dictionary<string, string>
                    {
                        { "client_id", clientId },
                        { "client_secret", clientSecret },
                        { "grant_type", "refresh_token" },
                        { "refresh_token", refreshToken }
                    };

                    var content = new FormUrlEncodedContent(requestData);

                    var response = await httpClient.PostAsync(tokenUrl, content).ConfigureAwait(false);
                    string responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        MainWindow.Log($"[AmoCrmOAuthService] Token refresh failed: {response.StatusCode} - {responseContent}");
                        return (null, null, null);
                    }

                    var json = JObject.Parse(responseContent);
                    string? accessToken = json["access_token"]?.Value<string>();
                    string? newRefreshToken = json["refresh_token"]?.Value<string>() ?? refreshToken; // Если новый refresh_token не вернулся, используем старый
                    int? expiresIn = json["expires_in"]?.Value<int>();

                    if (accessToken != null)
                    {
                        MainWindow.Log($"[AmoCrmOAuthService] Token refreshed successfully (expires in {expiresIn} seconds)");
                    }
                    else
                    {
                        MainWindow.Log("[AmoCrmOAuthService] Access token not found in refresh response");
                    }

                    return (accessToken, newRefreshToken, expiresIn);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmOAuthService] Error refreshing token: {ex.Message}");
                return (null, null, null);
            }
        }

        /// <summary>
        /// Запускает локальный HTTP сервер для обработки OAuth redirect
        /// </summary>
        private async Task<string?> StartLocalServerAsync(string redirectUri)
        {
            try
            {
                // Парсим redirect URI
                if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri? uri))
                {
                    MainWindow.Log($"[AmoCrmOAuthService] Invalid redirect URI format: {redirectUri}");
                    return null;
                }

                int port = uri.Port > 0 ? uri.Port : 8080;
                string path = uri.AbsolutePath;
                
                // Если путь пустой или только "/", используем "/callback"
                if (string.IsNullOrEmpty(path) || path == "/")
                {
                    path = "/callback";
                }

                lock (_lockObject)
                {
                    if (_isListening)
                    {
                        StopLocalServer();
                    }

                    _httpListener = new HttpListener();
                    string prefix = $"http://localhost:{port}/";
                    _httpListener.Prefixes.Add(prefix);
                    
                    try
                    {
                        _httpListener.Start();
                        _isListening = true;
                        MainWindow.Log($"[AmoCrmOAuthService] Local HTTP server started on port {port}, path: {path}");
                    }
                    catch (HttpListenerException ex)
                    {
                        MainWindow.Log($"[AmoCrmOAuthService] Failed to start HTTP server on port {port}: {ex.Message}");
                        MainWindow.Log($"[AmoCrmOAuthService] Error code: {ex.ErrorCode}. Port may be in use or requires admin rights.");
                        
                        // Пробуем другой порт если текущий занят
                        if (ex.ErrorCode == 32 || ex.ErrorCode == 183) // Port already in use
                        {
                            for (int tryPort = 8081; tryPort <= 8090; tryPort++)
                            {
                                try
                                {
                                    _httpListener = new HttpListener();
                                    _httpListener.Prefixes.Add($"http://localhost:{tryPort}/");
                                    _httpListener.Start();
                                    _isListening = true;
                                    port = tryPort;
                                    MainWindow.Log($"[AmoCrmOAuthService] Started HTTP server on alternative port {tryPort}");
                                    break;
                                }
                                catch
                                {
                                    // Пробуем следующий порт
                                }
                            }
                            
                            if (!_isListening)
                            {
                                return null;
                            }
                        }
                        else
                        {
                            return null;
                        }
                    }
                }

                // Запускаем обработку запросов в фоне
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (_isListening && _httpListener != null)
                        {
                            var context = await _httpListener.GetContextAsync();
                            await HandleRequestAsync(context);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_isListening)
                        {
                            MainWindow.Log($"[AmoCrmOAuthService] Error in HTTP listener: {ex.Message}");
                        }
                    }
                });

                await Task.CompletedTask; // Метод асинхронный, но не требует ожидания
                return $"http://localhost:{port}{path}";
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmOAuthService] Error starting local server: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Обрабатывает HTTP запрос от Kommo redirect
        /// </summary>
        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                MainWindow.Log($"[AmoCrmOAuthService] Received request: {request.Url}");
                MainWindow.Log($"[AmoCrmOAuthService] Request path: {request.Url?.AbsolutePath}");

                // Парсим query параметры
                string queryString = request.Url?.Query ?? string.Empty;
                if (queryString.StartsWith("?"))
                    queryString = queryString.Substring(1);
                var queryParams = ParseQueryString(queryString);
                
                MainWindow.Log($"[AmoCrmOAuthService] Query params: code={(!string.IsNullOrEmpty(queryParams["code"]) ? "present" : "missing")}, referer={queryParams["referer"]}, error={queryParams["error"]}");

                string? code = queryParams["code"];
                string? referer = queryParams["referer"];
                string? error = queryParams["error"];

                if (!string.IsNullOrEmpty(error))
                {
                    MainWindow.Log($"[AmoCrmOAuthService] Authorization error: {error}");
                    string errorDescription = queryParams["error_description"] ?? "Unknown error";
                    
                    // Отправляем HTML ответ с ошибкой
                    string errorHtml = $@"
<html>
<head><title>Authorization Failed</title></head>
<body>
    <h2>Authorization Failed</h2>
    <p>Error: {error}</p>
    <p>Description: {errorDescription}</p>
    <p>You can close this window.</p>
</body>
</html>";
                    byte[] buffer = Encoding.UTF8.GetBytes(errorHtml);
                    response.ContentType = "text/html; charset=utf-8";
                    response.StatusCode = 400;
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    response.Close();

                    lock (_lockObject)
                    {
                        _authorizationCode = null;
                        _referer = null;
                    }
                    return;
                }

                if (!string.IsNullOrEmpty(code))
                {
                    lock (_lockObject)
                    {
                        _authorizationCode = code;
                        _referer = referer;
                    }

                    MainWindow.Log($"[AmoCrmOAuthService] Authorization code received: {code.Substring(0, Math.Min(20, code.Length))}...");

                    // Отправляем HTML ответ с успехом
                    string successHtml = @"
<html>
<head><title>Authorization Successful</title></head>
<body>
    <h2>Authorization Successful!</h2>
    <p>You can close this window and return to the application.</p>
</body>
</html>";
                    byte[] buffer = Encoding.UTF8.GetBytes(successHtml);
                    response.ContentType = "text/html; charset=utf-8";
                    response.StatusCode = 200;
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    response.Close();
                }
                else
                {
                    // Неизвестный запрос
                    string notFoundHtml = @"
<html>
<head><title>Not Found</title></head>
<body>
    <h2>Not Found</h2>
    <p>This endpoint is used for OAuth authorization.</p>
</body>
</html>";
                    byte[] buffer = Encoding.UTF8.GetBytes(notFoundHtml);
                    response.ContentType = "text/html; charset=utf-8";
                    response.StatusCode = 404;
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    response.Close();
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[AmoCrmOAuthService] Error handling request: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
            }
        }

        /// <summary>
        /// Ожидает получения authorization code
        /// </summary>
        private async Task<(string? code, string? referer)> WaitForAuthorizationCodeAsync()
        {
            int maxWaitTime = 300000; // 5 минут в миллисекундах
            int checkInterval = 100; // Проверяем каждые 100мс (чаще для более отзывчивого UI)
            int elapsed = 0;

            while (elapsed < maxWaitTime)
            {
                lock (_lockObject)
                {
                    if (_authorizationCode != null)
                    {
                        return (_authorizationCode, _referer);
                    }
                }

                // Используем ConfigureAwait(false) чтобы не захватывать контекст синхронизации
                await Task.Delay(checkInterval).ConfigureAwait(false);
                elapsed += checkInterval;
            }

            return (null, null);
        }

        /// <summary>
        /// Останавливает локальный HTTP сервер
        /// </summary>
        private void StopLocalServer()
        {
            lock (_lockObject)
            {
                if (_httpListener != null && _isListening)
                {
                    try
                    {
                        _isListening = false;
                        _httpListener.Stop();
                        _httpListener.Close();
                        MainWindow.Log("[AmoCrmOAuthService] Local HTTP server stopped");
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[AmoCrmOAuthService] Error stopping server: {ex.Message}");
                    }
                    finally
                    {
                        _httpListener = null;
                        _authorizationCode = null;
                        _referer = null;
                    }
                }
            }
        }

        /// <summary>
        /// Парсит query string в NameValueCollection
        /// </summary>
        private NameValueCollection ParseQueryString(string queryString)
        {
            var result = new NameValueCollection();
            if (string.IsNullOrEmpty(queryString))
                return result;

            // Убираем ведущий '?' если есть
            if (queryString.StartsWith("?"))
                queryString = queryString.Substring(1);

            string[] pairs = queryString.Split('&');
            foreach (string pair in pairs)
            {
                if (string.IsNullOrEmpty(pair))
                    continue;

                int equalIndex = pair.IndexOf('=');
                if (equalIndex > 0)
                {
                    string key = Uri.UnescapeDataString(pair.Substring(0, equalIndex));
                    string value = Uri.UnescapeDataString(pair.Substring(equalIndex + 1));
                    result.Add(key, value);
                }
                else
                {
                    result.Add(Uri.UnescapeDataString(pair), string.Empty);
                }
            }

            return result;
        }

        /// <summary>
        /// Нормализует subdomain (убирает протокол и домен если есть)
        /// </summary>
        private string NormalizeSubdomain(string subdomain)
        {
            if (string.IsNullOrEmpty(subdomain))
                return subdomain;

            string normalized = subdomain.Trim();

            // Убираем протокол
            if (normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring("https://".Length);
            else if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring("http://".Length);

            // Убираем домен, оставляем только поддомен
            if (normalized.Contains("."))
            {
                normalized = normalized.Split('.')[0];
            }

            return normalized;
        }
    }
}
