using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Softphone
{
    public sealed class MikoPbxCdrService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _extension;

        public MikoPbxCdrService(string serviceUrl, string jwtToken, string extension)
        {
            _baseUrl = serviceUrl.TrimEnd('/');
            _extension = extension;

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                // Оставляем только современные версии TLS.
                // Это устраняет ошибки компиляции с Tls10/Tls11 и снижает риск "SSL connection could not be established".
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };
            // На некоторых сетях запросы к PBX Gateway могут выполняться дольше 30 секунд
            // (очереди/задержки на стороне PBX/Nginx/DB lock),
            // особенно когда мы делаем сразу несколько попыток подряд.
            // Увеличиваем таймаут, чтобы CallerID и скачивание записей не срывались.
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtToken);
        }

        /// <summary>
        /// Queries Callspire PBX Gateway for CDR records matching the given criteria.
        /// </summary>
        public async Task<List<CdrRecord>> GetCdrAsync(
            DateTime from,
            DateTime to,
            string? dst = null,
            int limit = 50,
            string? extensionOverride = null)
        {
            var ext = string.IsNullOrWhiteSpace(extensionOverride) ? _extension : extensionOverride.Trim();
            string url = $"{_baseUrl}/api/cdr?ext={Uri.EscapeDataString(ext)}" +
                         $"&from={Uri.EscapeDataString(from.ToString("yyyy-MM-ddTHH:mm:ss"))}" +
                         $"&to={Uri.EscapeDataString(to.ToString("yyyy-MM-ddTHH:mm:ss"))}" +
                         $"&limit={limit}";

            if (!string.IsNullOrEmpty(dst))
                url += $"&dst={Uri.EscapeDataString(dst)}";

            try
            {
                var resp = await _http.GetAsync(url).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var envelope = JsonConvert.DeserializeObject<JObject>(json);
                var data = envelope?["data"];

                if (data == null || data.Type != JTokenType.Array)
                    return new List<CdrRecord>();

                return data.ToObject<List<CdrRecord>>() ?? new List<CdrRecord>();
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] GetCdrAsync error: {ex}");
                return new List<CdrRecord>();
            }
        }

        /// <summary>
        /// Convenience wrapper: looks up the outbound CallerID for a specific call.
        /// Searches CDR ±2 minutes around the call start time for the matching dst number.
        /// </summary>
        public async Task<string?> GetCallCallerIdAsync(string calledNumber, DateTime callTime, string? extensionOverride = null)
        {
            try
            {
                var from = callTime.AddMinutes(-5);
                var to = callTime.AddMinutes(5);
                var ext = string.IsNullOrWhiteSpace(extensionOverride) ? _extension : extensionOverride.Trim();

                AppLog.Log($"[PBX Gateway] Querying CDR: url={_baseUrl}, ext={ext}, dst={calledNumber}, from={from:yyyy-MM-ddTHH:mm:ss}, to={to:yyyy-MM-ddTHH:mm:ss}");

                var records = await GetCdrAsync(from, to, dst: calledNumber, limit: 10, extensionOverride: ext);

                AppLog.Log($"[PBX Gateway] Got {records.Count} CDR record(s)");

                CdrRecord? best = null;
                double bestDiff = double.MaxValue;
                foreach (var r in records)
                {
                    AppLog.Log($"[PBX Gateway]   record: src={r.SrcNum}, dst={r.DstNum}, callerId={r.CallerId}, start={r.Start}");
                    if (DateTime.TryParse(r.Start, out var recTime))
                    {
                        double diff = Math.Abs((recTime - callTime).TotalSeconds);
                        if (diff < bestDiff)
                        {
                            bestDiff = diff;
                            best = r;
                        }
                    }
                }

                if (best != null && !string.IsNullOrEmpty(best.CallerId) && best.CallerId != _extension)
                    return best.CallerId;

                return null;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] GetCallCallerIdAsync error: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Downloads the recording file for a specific call (by linkedid) from MikoPBX.
        /// Returns the local file path on success, or null if not found.
        /// </summary>
        public async Task<string?> DownloadRecordingAsync(string linkedId, string destinationFolder)
        {
            try
            {
                string url = $"{_baseUrl}/api/recording?linkedid={Uri.EscapeDataString(linkedId)}";
                AppLog.Log($"[PBX Gateway] Downloading recording: linkedId={linkedId}");

                var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    AppLog.Log($"[PBX Gateway] No recording found on server for linkedId={linkedId}");
                    return null;
                }

                resp.EnsureSuccessStatusCode();

                string fileName = $"mikopbx_{linkedId}.mp3";
                if (resp.Content.Headers.ContentDisposition?.FileName is string serverName)
                    fileName = serverName.Trim('"');

                Directory.CreateDirectory(destinationFolder);
                string localPath = Path.Combine(destinationFolder, fileName);

                using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await stream.CopyToAsync(fs).ConfigureAwait(false);
                }

                var fileSize = new FileInfo(localPath).Length;
                AppLog.Log($"[PBX Gateway] Recording downloaded: {localPath} ({fileSize} bytes)");
                return localPath;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] DownloadRecordingAsync error: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Simple health check against the proxy service.
        /// </summary>
        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{_baseUrl}/health").ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Fetches the list of CallerID numbers assigned to this user's extension.
        /// </summary>
        public async Task<List<string>> GetMyCallerIdsAsync()
        {
            try
            {
                string url = $"{_baseUrl}/api/my-callerids";
                AppLog.Log($"[PBX Gateway] Fetching CallerIDs for extension {_extension}");
                var resp = await _http.GetAsync(url).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var envelope = JsonConvert.DeserializeObject<JObject>(json);
                var data = envelope?["callerids"];

                if (data == null || data.Type != JTokenType.Array)
                    return new List<string>();

                var list = data.ToObject<List<string>>() ?? new List<string>();
                AppLog.Log($"[PBX Gateway] Got {list.Count} CallerID(s): {string.Join(", ", list)}");
                return list;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] GetMyCallerIdsAsync error: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Fetches CallerIDs with display names assigned to this user.
        /// Returns <c>RequestSucceeded == false</c> when the HTTP request failed or the body could not be read (network, TLS, 401/5xx, etc.).
        /// When <c>RequestSucceeded == true</c>, <c>Items</c> may still be empty if none are configured on the PBX.
        /// </summary>
        public async Task<(List<CallerIdItem> Items, bool RequestSucceeded)> GetMyCallerIdItemsAsync()
        {
            try
            {
                string url = $"{_baseUrl}/api/my-callerids";
                AppLog.Log($"[PBX Gateway] Fetching CallerIDs for extension {_extension}");
                var resp = await _http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    AppLog.Log($"[PBX Gateway] GetMyCallerIdItemsAsync HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                    return (new List<CallerIdItem>(), false);
                }

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var envelope = JsonConvert.DeserializeObject<JObject>(json);

                var items = new List<CallerIdItem>();
                var arr = envelope?["callerid_items"];
                if (arr != null && arr.Type == JTokenType.Array)
                {
                    foreach (var tok in arr)
                    {
                        var number = tok["number"]?.ToString() ?? "";
                        var name = tok["name"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(number))
                            items.Add(new CallerIdItem(number, name));
                    }
                }

                if (items.Count == 0)
                {
                    var legacy = envelope?["callerids"];
                    if (legacy != null && legacy.Type == JTokenType.Array)
                        foreach (var s in legacy.ToObject<List<string>>() ?? new List<string>())
                            items.Add(new CallerIdItem(s, ""));
                }

                AppLog.Log($"[PBX Gateway] Got {items.Count} CallerID(s): {string.Join(", ", items.Select(i => i.DisplayText))}");
                return (items, true);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] GetMyCallerIdItemsAsync error: {ex.Message}");
                return (new List<CallerIdItem>(), false);
            }
        }

        /// <summary>
        /// Fetches MikoPBX's recent SIP auth-failure stats for this user's extension.
        ///
        /// The proxy (<c>/api/sip-auth-failures</c>) proxies MikoPBX's
        /// <c>/sip:getSipAuthFailureStats</c> endpoint and filters the failures
        /// to just this extension. We use it to warn the user that someone is
        /// hammering the SIP password from another IP — usually an old desktop
        /// or a misconfigured phone — before MikoPBX's Fail2Ban bans the source.
        /// </summary>
        public async Task<SipAuthFailureStats> GetSipAuthFailuresAsync()
        {
            try
            {
                string url = $"{_baseUrl}/api/sip-auth-failures?ext={Uri.EscapeDataString(_extension)}";
                var resp = await _http.GetAsync(url).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotImplemented)
                {
                    // Proxy has REST API disabled — feature simply not available
                    // on this PBX. Quiet "not supported" result, no log spam.
                    return new SipAuthFailureStats { Supported = false };
                }
                if (!resp.IsSuccessStatusCode)
                {
                    if (resp.StatusCode is HttpStatusCode.BadGateway
                        or HttpStatusCode.ServiceUnavailable
                        or HttpStatusCode.GatewayTimeout)
                    {
                        return new SipAuthFailureStats { Supported = true, TransientServerError = true };
                    }

                    AppLog.Log($"[PBX Gateway] GetSipAuthFailuresAsync HTTP {(int)resp.StatusCode}");
                    return new SipAuthFailureStats { Supported = false };
                }
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var envelope = JsonConvert.DeserializeObject<JObject>(json);
                int failureCount = envelope?["failure_count"]?.Value<int?>() ?? 0;
                int totalFailures = envelope?["total_failures"]?.Value<int?>() ?? 0;
                return new SipAuthFailureStats
                {
                    Supported = true,
                    FailuresForExtension = failureCount,
                    TotalFailuresAllPeers = totalFailures,
                };
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] GetSipAuthFailuresAsync error: {ex.Message}");
                return new SipAuthFailureStats { Supported = false };
            }
        }

        /// <summary>
        /// Initiates an outbound call via PBX AMI Originate with the specified CallerID.
        /// The PBX will ring the user's extension first, then bridge to the destination.
        /// </summary>
        public async Task<OriginateResult> OriginateCallAsync(string destination, string callerId, string? ringExtension = null)
        {
            AppLog.Log($"[PBX Gateway] Originate: dst={destination}, callerId={callerId}, ringExt={(string.IsNullOrWhiteSpace(ringExtension) ? "<jwt>" : ringExtension)}");

            string? lastError = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0)
                {
                    int delayMs = attempt switch { 1 => 2000, 2 => 5000, _ => 10000 };
                    AppLog.Log($"[PBX Gateway] OriginateCallAsync retry {attempt}/3 in {delayMs}ms ({lastError})");
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                var result = await OriginateCallOnceAsync(destination, callerId, ringExtension).ConfigureAwait(false);
                if (result.Success)
                    return result;

                lastError = result.Error;
                if (!IsTransientGatewayError(lastError))
                    break;
            }

            AppLog.Log($"[PBX Gateway] OriginateCallAsync error: {lastError}");
            return new OriginateResult { Success = false, Error = lastError ?? "Originate failed" };
        }

        private async Task<OriginateResult> OriginateCallOnceAsync(string destination, string callerId, string? ringExtension)
        {
            try
            {
                string url = $"{_baseUrl}/api/originate";
                object body = string.IsNullOrWhiteSpace(ringExtension)
                    ? new { destination, callerid = callerId }
                    : new { destination, callerid = callerId, ring_extension = ringExtension.Trim() };
                var content = new StringContent(
                    JsonConvert.SerializeObject(body),
                    Encoding.UTF8,
                    "application/json");

                var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    string? detail = TryExtractApiErrorDetail(json);
                    string error = detail ?? json;
                    if (string.IsNullOrWhiteSpace(error))
                        error = $"HTTP {(int)resp.StatusCode}";

                    if (IsTransientGatewayError(error) || IsTransientHttpStatusCode((int)resp.StatusCode))
                        return new OriginateResult { Success = false, Error = error };

                    AppLog.Log($"[PBX Gateway] Originate failed ({resp.StatusCode}): {json}");
                    return new OriginateResult { Success = false, Error = error };
                }

                var result = JsonConvert.DeserializeObject<OriginateResult>(json) ?? new OriginateResult();
                AppLog.Log($"[PBX Gateway] Originate result: success={result.Success}, originateId={result.OriginateId}");
                return result;
            }
            catch (Exception ex)
            {
                string detail = FormatGatewayException(ex);
                return new OriginateResult { Success = false, Error = detail };
            }
        }

        /// <summary>
        /// Pull desktop softphone TURN defaults from PBX Gateway (GET /api/v1/softphone-app-settings).
        /// </summary>
        public async Task<(SoftphoneAppSettings? Settings, string? Error)> GetSoftphoneAppSettingsAsync()
        {
            string? lastError = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0)
                {
                    int delayMs = attempt switch { 1 => 2000, 2 => 5000, _ => 10000 };
                    AppLog.Log($"[PBX Gateway] GetSoftphoneAppSettingsAsync retry {attempt}/3 in {delayMs}ms ({lastError})");
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                var (settings, error) = await GetSoftphoneAppSettingsOnceAsync().ConfigureAwait(false);
                if (settings != null)
                    return (settings, null);

                lastError = error;
                if (!IsTransientGatewayError(error))
                    break;
            }

            return (null, lastError);
        }

        private async Task<(SoftphoneAppSettings? Settings, string? Error)> GetSoftphoneAppSettingsOnceAsync()
        {
            try
            {
                string url = $"{_baseUrl}/api/v1/softphone-app-settings";
                var resp = await _http.GetAsync(url).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string? detail = TryExtractApiErrorDetail(body);
                    string error = detail ?? $"HTTP {(int)resp.StatusCode}";
                    if (IsTransientGatewayError(error) || IsTransientHttpStatusCode((int)resp.StatusCode))
                        return (null, error);
                    AppLog.Log($"[PBX Gateway] GetSoftphoneAppSettingsAsync error: {error}");
                    return (null, error);
                }

                var settings = JsonConvert.DeserializeObject<SoftphoneAppSettings>(body) ?? new SoftphoneAppSettings();
                return (settings, null);
            }
            catch (Exception ex)
            {
                string detail = FormatGatewayException(ex);
                if (IsTransientGatewayError(detail))
                    return (null, detail);
                AppLog.Log($"[PBX Gateway] GetSoftphoneAppSettingsAsync error: {detail}");
                return (null, detail);
            }
        }

        /// <summary>
        /// Settings UI / diagnostics: verifies the configured base URL reaches the proxy (<c>GET /health</c>)
        /// and the JWT still works (<c>GET /api/my-callerids</c>). Avoids showing "Connected" when the URL or token is stale.
        /// </summary>
        public static async Task<(string StatusText, bool IsConnected)> ProbeGatewayAsync(string? serviceUrl, string? bearerToken)
        {
            if (string.IsNullOrWhiteSpace(serviceUrl))
                return ("Not configured", false);

            string baseUrl = serviceUrl.Trim().TrimEnd('/');

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };

            HttpResponseMessage healthResp;
            try
            {
                healthResp = await http.GetAsync($"{baseUrl}/health").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] Probe: /health failed: {ex.Message}");
                return ("Unreachable", false);
            }

            if (healthResp.StatusCode == HttpStatusCode.NotFound)
                return ("Wrong URL", false);

            if (!healthResp.IsSuccessStatusCode)
                return ($"HTTP {(int)healthResp.StatusCode}", false);

            if (string.IsNullOrEmpty(bearerToken))
                return ("Not authorized", false);

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken.Trim());

            HttpResponseMessage apiResp;
            try
            {
                apiResp = await http.GetAsync($"{baseUrl}/api/my-callerids").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] Probe: /api/my-callerids failed: {ex.Message}");
                return ("Unreachable", false);
            }

            if (apiResp.StatusCode == HttpStatusCode.Unauthorized)
                return ("Session expired", false);

            if (!apiResp.IsSuccessStatusCode)
                return ($"API {(int)apiResp.StatusCode}", false);

            return ("Connected", true);
        }

        public async Task<KommoGatewayStatus?> GetKommoStatusAsync()
        {
            try
            {
                var resp = await _http.GetAsync($"{_baseUrl}/api/kommo/status").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return null;
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<KommoGatewayStatus>(json);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] GetKommoStatusAsync error: {ex.Message}");
                return null;
            }
        }

        public async Task<(KommoGatewaySession? Session, string? Error)> GetKommoSessionAsync(bool forceRefresh = false)
        {
            try
            {
                string url = $"{_baseUrl}/api/kommo/session";
                if (forceRefresh)
                    url += "?force_refresh=true";

                var resp = await _http.GetAsync(url).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string? detail = TryExtractApiErrorDetail(json);
                    string message = detail ?? $"HTTP {(int)resp.StatusCode}";
                    AppLog.Log($"[PBX Gateway] GetKommoSessionAsync failed: {message}");
                    return (null, message);
                }
                var session = JsonConvert.DeserializeObject<KommoGatewaySession>(json);
                if (session == null || string.IsNullOrWhiteSpace(session.AccessToken))
                    return (null, "Gateway returned an empty Kommo session");
                return (session, null);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] GetKommoSessionAsync error: {ex.Message}");
                return (null, ex.Message);
            }
        }

        public Task<(KommoProcessCallJobStatus? Job, string? Error)> SubmitKommoProcessCallAsync(KommoProcessCallRequest request)
        {
            return PostKommoProcessCallWithTransientRetryAsync(
                $"{_baseUrl}/api/kommo/process-call",
                request,
                "SubmitKommoProcessCallAsync");
        }

        public async Task<KommoProcessCallJobStatus?> GetKommoProcessCallStatusAsync(string jobId)
        {
            try
            {
                var resp = await _http.GetAsync($"{_baseUrl}/api/kommo/process-call/{Uri.EscapeDataString(jobId)}").ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return null;
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<KommoProcessCallJobStatus>(json);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] GetKommoProcessCallStatusAsync error: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> UploadKommoProcessCallRecordingAsync(string jobId, string filePath)
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;
            try
            {
                using var form = new MultipartFormDataContent();
                await using var stream = File.OpenRead(filePath);
                var fileContent = new StreamContent(stream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(
                    filePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav" : "audio/mpeg");
                form.Add(fileContent, "file", Path.GetFileName(filePath));

                var resp = await _http.PutAsync(
                    $"{_baseUrl}/api/kommo/process-call/{Uri.EscapeDataString(jobId)}/recording",
                    form).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PBX Gateway] UploadKommoProcessCallRecordingAsync error: {ex.Message}");
                return false;
            }
        }

        public Task<(KommoProcessCallJobStatus? Job, string? Error)> RetryKommoProcessCallAsync(KommoProcessCallRetryRequest request)
        {
            return PostKommoProcessCallWithTransientRetryAsync(
                $"{_baseUrl}/api/kommo/process-call/retry",
                request,
                "RetryKommoProcessCallAsync");
        }

        private async Task<(KommoProcessCallJobStatus? Job, string? Error)> PostKommoProcessCallWithTransientRetryAsync<T>(
            string url,
            T request,
            string operationName)
        {
            string? lastError = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0)
                {
                    int delayMs = attempt switch { 1 => 2000, 2 => 5000, _ => 10000 };
                    AppLog.Log($"[PBX Gateway] {operationName} retry {attempt}/3 in {delayMs}ms ({lastError})");
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }

                var (job, error) = await PostKommoProcessCallOnceAsync(url, request, operationName).ConfigureAwait(false);
                if (job != null)
                    return (job, null);

                lastError = error;
                if (!IsTransientGatewayError(error))
                    break;
            }

            return (null, lastError);
        }

        private async Task<(KommoProcessCallJobStatus? Job, string? Error)> PostKommoProcessCallOnceAsync<T>(
            string url,
            T request,
            string operationName)
        {
            try
            {
                string json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string? detail = TryExtractApiErrorDetail(body);
                    string error = detail ?? $"HTTP {(int)resp.StatusCode}";
                    if (IsTransientGatewayError(error) || IsTransientHttpStatusCode((int)resp.StatusCode))
                        return (null, error);
                    AppLog.Log($"[PBX Gateway] {operationName} error: {error}");
                    return (null, error);
                }

                var job = JsonConvert.DeserializeObject<KommoProcessCallJobStatus>(body);
                return (job, null);
            }
            catch (Exception ex)
            {
                string detail = FormatGatewayException(ex);
                if (IsTransientGatewayError(detail))
                    return (null, detail);

                AppLog.Log($"[PBX Gateway] {operationName} error: {detail}");
                return (null, detail);
            }
        }

        private static string FormatGatewayException(Exception ex)
        {
            string message = ex.Message;
            if (ex.InnerException != null)
                message += $" ({ex.InnerException.Message})";
            return message;
        }

        private static bool IsTransientHttpStatusCode(int statusCode)
            => statusCode is 408 or 429 or 502 or 503 or 504;

        private static bool IsTransientGatewayError(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("SSL connection", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("Unable to read data from the transport connection", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("An existing connection was forcibly closed", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("connection attempt failed", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("HTTP 502", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("HTTP 504", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("HTTP 408", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase);
        }

        private static string? TryExtractApiErrorDetail(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var envelope = JsonConvert.DeserializeObject<JObject>(json);
                return envelope?["detail"]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// Compact view of MikoPBX's SIP auth-failure counters for this extension.
    /// <c>Supported</c> is false when the proxy's REST integration is disabled
    /// or the call simply failed — callers should check that before reading
    /// the counters, otherwise zero is indistinguishable from "unknown".
    /// </summary>
    public class SipAuthFailureStats
    {
        public bool Supported { get; set; }
        public bool TransientServerError { get; set; }
        public int FailuresForExtension { get; set; }
        public int TotalFailuresAllPeers { get; set; }
    }

    public class CallerIdItem
    {
        public string Number { get; set; }
        public string Name { get; set; }
        public string DisplayText => string.IsNullOrEmpty(Name) ? Number : $"{Name} ({Number})";

        public CallerIdItem(string number, string name)
        {
            Number = number;
            Name = name ?? "";
        }

        public override string ToString() => DisplayText;
    }

    public class OriginateResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("originate_id")]
        public string OriginateId { get; set; } = "";

        [JsonProperty("error")]
        public string? Error { get; set; }

        /// <summary>Extension AMI actually rang (echoed by gateway when ring_extension support is deployed).</summary>
        [JsonProperty("ring_extension")]
        public string? RingExtension { get; set; }
    }

    public class SoftphoneAppSettings
    {
        [JsonProperty("turn_uri")]
        public string TurnUri { get; set; } = "";

        [JsonProperty("turn_username")]
        public string TurnUsername { get; set; } = "";

        [JsonProperty("turn_password")]
        public string TurnPassword { get; set; } = "";

        [JsonProperty("config_revision")]
        public string ConfigRevision { get; set; } = "";
    }

    public class KommoGatewayStatus
    {
        [JsonProperty("available")]
        public bool Available { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; }

        [JsonProperty("configured")]
        public bool Configured { get; set; }

        [JsonProperty("authorized")]
        public bool Authorized { get; set; }

        [JsonProperty("subdomain")]
        public string Subdomain { get; set; } = "";

        [JsonProperty("token_expires_at")]
        public string? TokenExpiresAt { get; set; }

        [JsonProperty("token_expired")]
        public bool TokenExpired { get; set; }

        /// <summary>True when this extension is excluded from gateway Kommo in admin.</summary>
        [JsonProperty("excluded")]
        public bool Excluded { get; set; }

        /// <summary>True when gateway Kommo should be offered to this client (enabled and not excluded).</summary>
        [JsonProperty("offer_gateway")]
        public bool OfferGateway { get; set; }

        /// <summary>True when admin mapped a Kommo user for this extension (shared gateway uploads).</summary>
        [JsonProperty("upload_enabled")]
        public bool UploadEnabled { get; set; }

        [JsonProperty("kommo_user_id")]
        public long? KommoUserId { get; set; }

        [JsonProperty("kommo_user_name")]
        public string? KommoUserName { get; set; }
    }

    public class KommoGatewaySession
    {
        [JsonProperty("subdomain")]
        public string Subdomain { get; set; } = "";

        [JsonProperty("access_token")]
        public string AccessToken { get; set; } = "";

        [JsonProperty("expires_at")]
        public string? ExpiresAt { get; set; }

        [JsonProperty("account_base_url")]
        public string? AccountBaseUrl { get; set; }

        [JsonProperty("kommo_user_id")]
        public long? KommoUserId { get; set; }

        [JsonProperty("kommo_user_name")]
        public string? KommoUserName { get; set; }

        [JsonProperty("kommo_user_id_source")]
        public string? KommoUserIdSource { get; set; }
    }

    public class KommoProcessCallRequest
    {
        [JsonProperty("phone")]
        public string Phone { get; set; } = "";

        [JsonProperty("call_time")]
        public string CallTime { get; set; } = "";

        [JsonProperty("session_id")]
        public string? SessionId { get; set; }

        [JsonProperty("is_incoming")]
        public bool IsIncoming { get; set; }

        [JsonProperty("duration_seconds")]
        public int DurationSeconds { get; set; }

        [JsonProperty("was_answered")]
        public bool WasAnswered { get; set; }

        [JsonProperty("lead_id")]
        public long? LeadId { get; set; }

        [JsonProperty("client_recording_enabled")]
        public bool ClientRecordingEnabled { get; set; }

        [JsonProperty("connection_slot")]
        public string ConnectionSlot { get; set; } = "main";

        [JsonProperty("call_from_label")]
        public string? CallFromLabel { get; set; }

        [JsonProperty("call_log")]
        public string? CallLog { get; set; }

        [JsonProperty("enable_recording_upload")]
        public bool EnableRecordingUpload { get; set; } = true;

        [JsonProperty("answer_time")]
        public string? AnswerTime { get; set; }

        [JsonProperty("call_end_time")]
        public string? CallEndTime { get; set; }
    }

    public class KommoProcessCallRetryRequest : KommoProcessCallRequest
    {
        [JsonProperty("job_id")]
        public string? JobId { get; set; }
    }

    public class KommoProcessCallJobStatus
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("status")]
        public string Status { get; set; } = "";

        [JsonProperty("lead_id")]
        public long? LeadId { get; set; }

        [JsonProperty("upload_source")]
        public string? UploadSource { get; set; }

        [JsonProperty("reason")]
        public string? Reason { get; set; }

        [JsonProperty("created_at")]
        public string? CreatedAt { get; set; }

        [JsonProperty("updated_at")]
        public string? UpdatedAt { get; set; }

        /// <summary>Optional PBX CDR truth returned after gateway CDR match (desktop status sync).</summary>
        [JsonProperty("pbx_was_answered")]
        public bool? PbxWasAnswered { get; set; }

        [JsonProperty("pbx_duration_seconds")]
        public int? PbxDurationSeconds { get; set; }
    }

    public class CdrRecord
    {
        [JsonProperty("src_num")]
        public string SrcNum { get; set; } = "";

        [JsonProperty("dst_num")]
        public string DstNum { get; set; } = "";

        [JsonProperty("caller_id")]
        public string CallerId { get; set; } = "";

        [JsonProperty("start")]
        public string Start { get; set; } = "";

        [JsonProperty("duration")]
        public int Duration { get; set; }

        [JsonProperty("disposition")]
        public string Disposition { get; set; } = "";

        [JsonProperty("recording")]
        public string Recording { get; set; } = "";

        [JsonProperty("linkedid")]
        public string LinkedId { get; set; } = "";
    }
}
