using System;
using System.Globalization;
using System.Threading.Tasks;

namespace Softphone
{
    /// <summary>
    /// Resolves a Kommo contact name for call details.
    /// Uses the already-initialized local client, and in PBX Gateway mode opens a short-lived
    /// client from <c>GET /api/kommo/session</c> when the desktop has no local Kommo token.
    /// </summary>
    public static class KommoContactDirectory
    {
        public static async Task<(string? Name, long? ContactId, string? Error)> LookupAsync(
            string? phone,
            AmoCrmService? readyClient,
            MikoPbxCdrService? gateway)
        {
            if (string.IsNullOrWhiteSpace(phone))
                return (null, null, "Phone number is missing");

            if (readyClient?.IsInitialized == true)
                return await FromClientAsync(readyClient, phone).ConfigureAwait(false);

            if (gateway == null)
                return (null, null, "AmoCRM service not available");

            var (session, sessionError) = await gateway.GetKommoSessionAsync().ConfigureAwait(false);
            if (session == null || string.IsNullOrWhiteSpace(session.AccessToken))
                return (null, null, sessionError ?? "PBX Gateway Kommo session is unavailable");

            var svc = new AmoCrmService();
            try
            {
                await svc.InitializeGatewayOAuthAsync(
                    session.Subdomain,
                    session.AccessToken,
                    ParseExpires(session.ExpiresAt),
                    async forceRefresh =>
                    {
                        var (refreshed, _) = await gateway.GetKommoSessionAsync(forceRefresh).ConfigureAwait(false);
                        if (refreshed == null || string.IsNullOrWhiteSpace(refreshed.AccessToken))
                            return (string.Empty, (DateTime?)null);
                        return (refreshed.AccessToken, ParseExpires(refreshed.ExpiresAt));
                    },
                    session.AccountBaseUrl,
                    session.KommoUserId,
                    session.KommoUserName).ConfigureAwait(false);

                return await FromClientAsync(svc, phone).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[KommoContact] gateway lookup failed: {ex.Message}");
                return (null, null, "Error loading contact name");
            }
            finally
            {
                try { svc.Dispose(); } catch { }
            }
        }

        private static async Task<(string? Name, long? ContactId, string? Error)> FromClientAsync(AmoCrmService client, string phone)
        {
            try
            {
                var (name, id) = await client.LookupContactByPhoneAsync(phone).ConfigureAwait(false);
                if (id == null)
                    return (null, null, "Contact not found in AmoCRM");
                return (string.IsNullOrWhiteSpace(name) ? $"Contact #{id}" : name, id, null);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[KommoContact] lookup failed: {ex.Message}");
                return (null, null, "Error loading contact name");
            }
        }

        private static DateTime? ParseExpires(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return null;
            return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
        }
    }
}
