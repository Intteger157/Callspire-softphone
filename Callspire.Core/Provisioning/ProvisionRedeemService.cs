using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Softphone.Provisioning
{
    /// <summary>
    /// Redeems a one-time <c>callspire://provision?token=…&amp;proxy=…</c> link from the PBX Gateway admin panel.
    /// Shared by WPF and the macOS sidecar (via <see cref="Softphone.AppHost.DesktopAppController"/>).
    /// </summary>
    public static class ProvisionRedeemService
    {
        public sealed class Result
        {
            public bool Success { get; init; }
            public AppSettings? Settings { get; init; }
            public string? UserMessage { get; init; }
            public string? LogLine { get; init; }
        }

        public static async Task<Result> RedeemAsync(string tokenId, string proxyUrl)
        {
            string baseUrl = (proxyUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(tokenId))
                return Fail("Missing setup link parameters.");

            try
            {
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

                string redeemUrl = $"{baseUrl}/api/provision/redeem?token={Uri.EscapeDataString(tokenId)}";
                var resp = await http.GetAsync(redeemUrl).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string msg = resp.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? "This setup link is invalid, already used, or expired. Ask your administrator for a new one."
                        : $"Failed to fetch SIP credentials from the PBX (HTTP {(int)resp.StatusCode}).";
                    AppLog.Log($"[Provision] Redeem failed HTTP {(int)resp.StatusCode}: {json}");
                    return Fail(msg);
                }

                var payload = JObject.Parse(json);
                string extension = (string?)payload["extension"] ?? "";
                string sipUser = (string?)payload["sip_username"] ?? extension;
                string sipPassword = (string?)payload["sip_password"] ?? "";
                string sipServer = (string?)payload["sip_server"] ?? "";
                string wsUrl = (string?)payload["ws_url"] ?? "";
                string returnedProxy = (string?)payload["proxy_url"] ?? "";

                if (string.IsNullOrEmpty(sipUser) || string.IsNullOrEmpty(sipPassword))
                {
                    AppLog.Log("[Provision] Payload missing sip_username / sip_password");
                    return Fail("The PBX returned incomplete credentials.");
                }

                var settings = AppDataHelper.LoadSettingsOrNew();
                settings.SipUsername = sipUser;
                settings.SipPasswordEncrypted = TokenEncryption.Encrypt(sipPassword);
                settings.SipPassword = null;
                if (!string.IsNullOrEmpty(sipServer))
                    settings.SipServer = sipServer;
                if (!string.IsNullOrEmpty(wsUrl))
                {
                    settings.WebRtcWsUri = wsUrl;
                    settings.UseWebRtcAudio = true;
                    settings.MainConnectionTransport = "WebRtc";
                }

                settings.MikoPbxExtension = extension;
                string cdrUrl = string.IsNullOrEmpty(returnedProxy) ? baseUrl : returnedProxy.TrimEnd('/');
                if (!string.IsNullOrEmpty(cdrUrl))
                {
                    settings.MikoPbxCdrServiceUrl = cdrUrl;
                    settings.EnableMikoPbxCdr = true;
                }

                var kommoHints = payload["kommo"] as JObject;
                bool configureGatewayKommo = ShouldConfigureGatewayKommoFromHints(kommoHints);
                string? kommoUserName = (string?)kommoHints?["kommo_user_name"];
                string? kommoSubdomain = (string?)kommoHints?["subdomain"];
                if (configureGatewayKommo)
                {
                    AppDataHelper.ApplyProvisionGatewayKommo(settings, kommoSubdomain);
                    AppLog.Log($"[Provision] Gateway Kommo auto-config from redeem (user={kommoUserName ?? "?"})");
                }

                string? gatewayJwt = await TryWinappGatewayAuthAsync(http, cdrUrl, sipUser, sipPassword).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(gatewayJwt))
                {
                    settings.MikoPbxCdrTokenEncrypted = TokenEncryption.Encrypt(gatewayJwt);
                    AppLog.Log("[Provision] PBX Gateway JWT obtained via winapp-auth");
                }

                string kommoLine = configureGatewayKommo
                    ? $"\nKommo: shared gateway ({kommoUserName ?? "mapped user"})."
                    : "";
                string gatewayAuthLine = string.IsNullOrEmpty(gatewayJwt)
                    ? "\n\nOpen Settings → Integrations → PBX Gateway → Authorize to finish gateway login."
                    : "\n\nGateway login completed automatically.";
                string userMessage =
                    $"Callspire was configured automatically for extension {extension}.\n\n" +
                    $"SIP server: {(string.IsNullOrEmpty(sipServer) ? "(from PBX)" : sipServer)}\n" +
                    $"User: {sipUser}{kommoLine}{gatewayAuthLine}";

                AppLog.Log($"[Provision] settings updated for ext={extension}, server={sipServer}");
                return new Result
                {
                    Success = true,
                    Settings = settings,
                    UserMessage = userMessage,
                    LogLine = "Provision redeem OK",
                };
            }
            catch (Exception ex)
            {
                AppLog.Log($"[Provision] Redeem error: {ex.Message}");
                return Fail($"Setup failed: {ex.Message}");
            }
        }

        private static Result Fail(string message) => new() { Success = false, UserMessage = message };

        private static async Task<string?> TryWinappGatewayAuthAsync(HttpClient http, string baseUrl, string username, string password)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                return null;
            try
            {
                var body = new { username = username.Trim(), password };
                var content = new StringContent(
                    Newtonsoft.Json.JsonConvert.SerializeObject(body),
                    Encoding.UTF8,
                    "application/json");
                var resp = await http.PostAsync($"{baseUrl.TrimEnd('/')}/winapp-auth", content).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    AppLog.Log($"[Provision] winapp-auth HTTP {(int)resp.StatusCode}: {json}");
                    return null;
                }
                var tokenNode = JObject.Parse(json)["token"];
                var token = tokenNode?.Type == JTokenType.String ? tokenNode.ToString() : null;
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[Provision] winapp-auth error: {ex.Message}");
                return null;
            }
        }

        private static bool ShouldConfigureGatewayKommoFromHints(JObject? kommoHints)
        {
            if (kommoHints == null) return false;
            if (ProvisionJsonBool(kommoHints["configure_gateway"])) return true;
            if (ProvisionJsonBool(kommoHints["upload_enabled"])) return true;
            return kommoHints["kommo_user_id"]?.Type == JTokenType.Integer && (int)kommoHints["kommo_user_id"]! > 0;
        }

        private static bool ProvisionJsonBool(JToken? token) => token?.Type switch
        {
            JTokenType.Boolean => (bool)token,
            JTokenType.Integer => (int)token != 0,
            JTokenType.String => bool.TryParse((string)token!, out bool parsed) && parsed,
            _ => false,
        };
    }
}
