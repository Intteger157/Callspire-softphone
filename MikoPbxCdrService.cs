using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
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
            // На некоторых сетях запросы к CDR proxy могут выполняться дольше 30 секунд
            // (очереди/задержки на стороне PBX/Nginx/DB lock),
            // особенно когда мы делаем сразу несколько попыток подряд.
            // Увеличиваем таймаут, чтобы CallerID и скачивание записей не срывались.
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", jwtToken);
        }

        /// <summary>
        /// Queries the CDR proxy for records matching the given criteria.
        /// </summary>
        public async Task<List<CdrRecord>> GetCdrAsync(
            DateTime from,
            DateTime to,
            string? dst = null,
            int limit = 50)
        {
            string url = $"{_baseUrl}/api/cdr?ext={Uri.EscapeDataString(_extension)}" +
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
                MainWindow.Log($"[MikoPBX CDR] GetCdrAsync error: {ex}");
                return new List<CdrRecord>();
            }
        }

        /// <summary>
        /// Convenience wrapper: looks up the outbound CallerID for a specific call.
        /// Searches CDR ±2 minutes around the call start time for the matching dst number.
        /// </summary>
        public async Task<string?> GetCallCallerIdAsync(string calledNumber, DateTime callTime)
        {
            try
            {
                var from = callTime.AddMinutes(-5);
                var to = callTime.AddMinutes(5);

                MainWindow.Log($"[MikoPBX CDR] Querying CDR: url={_baseUrl}, ext={_extension}, dst={calledNumber}, from={from:yyyy-MM-ddTHH:mm:ss}, to={to:yyyy-MM-ddTHH:mm:ss}");

                var records = await GetCdrAsync(from, to, dst: calledNumber, limit: 10);

                MainWindow.Log($"[MikoPBX CDR] Got {records.Count} CDR record(s)");

                CdrRecord? best = null;
                double bestDiff = double.MaxValue;
                foreach (var r in records)
                {
                    MainWindow.Log($"[MikoPBX CDR]   record: src={r.SrcNum}, dst={r.DstNum}, callerId={r.CallerId}, start={r.Start}");
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
                MainWindow.Log($"[MikoPBX CDR] GetCallCallerIdAsync error: {ex}");
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
                MainWindow.Log($"[MikoPBX CDR] Downloading recording: linkedId={linkedId}");

                var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    MainWindow.Log($"[MikoPBX CDR] No recording found on server for linkedId={linkedId}");
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
                MainWindow.Log($"[MikoPBX CDR] Recording downloaded: {localPath} ({fileSize} bytes)");
                return localPath;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[MikoPBX CDR] DownloadRecordingAsync error: {ex}");
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
                MainWindow.Log($"[MikoPBX CDR] Fetching CallerIDs for extension {_extension}");
                var resp = await _http.GetAsync(url).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var envelope = JsonConvert.DeserializeObject<JObject>(json);
                var data = envelope?["callerids"];

                if (data == null || data.Type != JTokenType.Array)
                    return new List<string>();

                var list = data.ToObject<List<string>>() ?? new List<string>();
                MainWindow.Log($"[MikoPBX CDR] Got {list.Count} CallerID(s): {string.Join(", ", list)}");
                return list;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[MikoPBX CDR] GetMyCallerIdsAsync error: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Fetches CallerIDs with display names assigned to this user.
        /// </summary>
        public async Task<List<CallerIdItem>> GetMyCallerIdItemsAsync()
        {
            try
            {
                string url = $"{_baseUrl}/api/my-callerids";
                MainWindow.Log($"[MikoPBX CDR] Fetching CallerIDs for extension {_extension}");
                var resp = await _http.GetAsync(url).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

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

                MainWindow.Log($"[MikoPBX CDR] Got {items.Count} CallerID(s): {string.Join(", ", items.Select(i => i.DisplayText))}");
                return items;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[MikoPBX CDR] GetMyCallerIdItemsAsync error: {ex.Message}");
                return new List<CallerIdItem>();
            }
        }

        /// <summary>
        /// Initiates an outbound call via PBX AMI Originate with the specified CallerID.
        /// The PBX will ring the user's extension first, then bridge to the destination.
        /// </summary>
        public async Task<OriginateResult> OriginateCallAsync(string destination, string callerId)
        {
            try
            {
                string url = $"{_baseUrl}/api/originate";
                MainWindow.Log($"[MikoPBX CDR] Originate: dst={destination}, callerId={callerId}");

                var body = new { destination, callerid = callerId };
                var content = new StringContent(
                    JsonConvert.SerializeObject(body),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    MainWindow.Log($"[MikoPBX CDR] Originate failed ({resp.StatusCode}): {json}");
                    return new OriginateResult { Success = false, Error = json };
                }

                var result = JsonConvert.DeserializeObject<OriginateResult>(json) ?? new OriginateResult();
                MainWindow.Log($"[MikoPBX CDR] Originate result: success={result.Success}, originateId={result.OriginateId}");
                return result;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[MikoPBX CDR] OriginateCallAsync error: {ex.Message}");
                return new OriginateResult { Success = false, Error = ex.Message };
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
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
