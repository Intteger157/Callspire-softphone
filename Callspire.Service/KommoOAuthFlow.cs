using System;
using System.Threading;
using System.Threading.Tasks;
using Softphone.AppHost;
using Softphone.Service.Contracts;

namespace Softphone.Service
{
    /// <summary>
    /// "Authorize with Kommo" (WPF <c>SettingsWindow.AmoCrmAuthorizeButton_Click</c>) without the UI:
    /// <see cref="AmoCrmOAuthService"/> opens the system browser and listens on the localhost redirect,
    /// the code is exchanged for tokens, tokens are persisted encrypted and the controller's local Kommo
    /// client is re-initialised. Progress is reported through <see cref="StatusChanged"/> so the Swift
    /// settings panel can show Authorizing… / Authorized / failed exactly like WPF.
    /// </summary>
    public sealed class KommoOAuthFlow
    {
        private readonly DesktopAppController _controller;
        private int _running;
        private string? _lastError;

        public event Action? StatusChanged;

        public KommoOAuthFlow(DesktopAppController controller) { _controller = controller; }

        public bool IsBusy => _running == 1;

        public KommoOAuthStatusDto Snapshot()
        {
            var s = _controller.Settings;
            bool oauthMode = string.Equals(s.AmoCrmAuthMode, "oauth", StringComparison.OrdinalIgnoreCase);
            bool hasToken = !string.IsNullOrEmpty(s.AmoCrmOAuthAccessTokenEncrypted);
            bool authorized = oauthMode && hasToken && _controller.IsLocalKommoInitialized;
            string text = IsBusy ? "Authorizing…"
                : _lastError != null ? "Authorization failed"
                : authorized ? "Authorized"
                : hasToken ? "Token stored (not verified)"
                : "Not authorized";
            return new KommoOAuthStatusDto
            {
                IsAuthorized = authorized, StatusText = text, ExpiresAt = s.AmoCrmOAuthTokenExpiresAt,
                IsBusy = IsBusy, Error = _lastError,
            };
        }

        /// <summary>Runs the whole flow; returns an error message or null on success.</summary>
        public async Task<string?> AuthorizeAsync(string clientId, string clientSecret, string? redirectUri)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) return "Authorization is already in progress.";
            _lastError = null;
            Notify();
            try
            {
                clientId = (clientId ?? "").Trim();
                if (clientId.Length == 0) return Fail("Please enter Client ID.");
                if (string.IsNullOrWhiteSpace(clientSecret)) return Fail("Please enter Client Secret.");
                redirectUri = NormalizeRedirectUri(redirectUri);

                AppLog.Log("[KommoOAuth] starting authorization…");
                var oauth = new AmoCrmOAuthService();
                var (code, referer) = await oauth.AuthorizeAsync(string.Empty, clientId, redirectUri).ConfigureAwait(false);
                if (string.IsNullOrEmpty(code)) return Fail("Authorization failed or was cancelled. Please try again.");
                if (string.IsNullOrEmpty(referer)) return Fail("Failed to determine your Kommo subdomain from the authorization response. Please try again.");

                string refererDomain = referer.Replace("https://", "").Replace("http://", "").Trim();
                string subdomain = refererDomain.Contains('.') ? refererDomain.Split('.')[0] : refererDomain;
                AppLog.Log($"[KommoOAuth] subdomain from referer: {subdomain}");

                var (accessToken, refreshToken, expiresIn) = await oauth.ExchangeCodeForTokensAsync(subdomain, clientId, clientSecret, code, redirectUri).ConfigureAwait(false);
                if (string.IsNullOrEmpty(accessToken)) return Fail("Failed to get access token. Please check your Client ID and Client Secret.");

                var settings = DesktopAppController.LoadSettingsWithMigrations();
                settings.EnableAmoCrmIntegration = true;
                settings.AmoCrmConnectionSource = "local";
                settings.AmoCrmRecordingUploadSource = "local";
                settings.AmoCrmSubdomain = subdomain;
                settings.AmoCrmAuthMode = "oauth";
                settings.AmoCrmClientId = clientId;
                settings.AmoCrmClientSecretEncrypted = TokenEncryption.Encrypt(clientSecret);
                settings.AmoCrmRedirectUri = redirectUri;
                settings.AmoCrmOAuthAccessTokenEncrypted = TokenEncryption.Encrypt(accessToken);
                settings.AmoCrmOAuthRefreshTokenEncrypted = refreshToken != null ? TokenEncryption.Encrypt(refreshToken) : null;
                settings.AmoCrmOAuthTokenExpiresAt = expiresIn.HasValue ? DateTime.UtcNow.AddSeconds(expiresIn.Value) : null;
                _controller.SaveSettings(settings);
                AppLog.Log("[KommoOAuth] tokens saved");

                await _controller.ReloadLocalKommoAsync().ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[KommoOAuth] error: {ex}");
                return Fail(ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                Notify();
            }
        }

        private string Fail(string message)
        {
            _lastError = message;
            AppLog.Log($"[KommoOAuth] {message}");
            return message;
        }

        private void Notify() { try { StatusChanged?.Invoke(); } catch { } }

        /// <summary>Same normalisation the WPF settings window applies to the redirect URI field.</summary>
        internal static string NormalizeRedirectUri(string? raw)
        {
            var uri = (raw ?? "").Trim();
            if (uri.Length == 0 || uri == "http://localhost" || uri == "http://localhost/") uri = "http://localhost:8080/callback";
            if (!uri.Contains('/') || uri.EndsWith(":8080", StringComparison.OrdinalIgnoreCase)) uri = uri.TrimEnd('/') + "/callback";
            return uri;
        }
    }
}
