using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Softphone
{
    /// <summary>
    /// Pull TURN settings from PBX Gateway when integration is enabled.
    /// Server empty → keep local. Server set + local match → skip. Otherwise apply from server.
    /// </summary>
    public static class GatewayTurnSync
    {
        public static async Task<bool> TrySyncAsync(MikoPbxCdrService? gateway, AppSettings settings, string settingsPath)
        {
            if (gateway == null || !settings.EnableMikoPbxCdr)
                return false;

            var (remote, error) = await gateway.GetSoftphoneAppSettingsAsync().ConfigureAwait(false);
            if (remote == null)
            {
                if (!string.IsNullOrWhiteSpace(error))
                    AppLog.Log($"[PBX Gateway] TURN sync: fetch failed ({error})");
                return false;
            }

            if (string.IsNullOrWhiteSpace(remote.TurnUri))
            {
                AppLog.Log("[PBX Gateway] TURN sync: server empty, kept local");
                return false;
            }

            string localUri = settings.MainWebRtcTurnUri ?? settings.WebRtcTurnUri ?? "";
            string? localUser = settings.MainWebRtcTurnUsername ?? settings.WebRtcTurnUsername;
            string? localPass = TurnPasswordProvider.GetMainTurnPassword(settings);

            if (!string.IsNullOrWhiteSpace(settings.MainWebRtcTurnGatewayRevision) &&
                string.Equals(settings.MainWebRtcTurnGatewayRevision, remote.ConfigRevision, StringComparison.Ordinal) &&
                TurnUriHelper.AreTurnSettingsEqual(
                    localUri, localUser, localPass,
                    remote.TurnUri, remote.TurnUsername, remote.TurnPassword))
            {
                AppLog.Log("[PBX Gateway] TURN sync: skipped (revision match)");
                return false;
            }

            if (TurnUriHelper.AreTurnSettingsEqual(
                    localUri, localUser, localPass,
                    remote.TurnUri, remote.TurnUsername, remote.TurnPassword))
            {
                if (!string.Equals(settings.MainWebRtcTurnGatewayRevision, remote.ConfigRevision, StringComparison.Ordinal))
                {
                    settings.MainWebRtcTurnGatewayRevision = remote.ConfigRevision;
                    SaveSettings(settings, settingsPath);
                    AppLog.Log("[PBX Gateway] TURN sync: skipped (match), revision updated");
                }
                else
                {
                    AppLog.Log("[PBX Gateway] TURN sync: skipped (match)");
                }
                return false;
            }

            settings.MainWebRtcTurnUri = remote.TurnUri.Trim();
            settings.MainWebRtcTurnUsername = string.IsNullOrWhiteSpace(remote.TurnUsername)
                ? null
                : remote.TurnUsername.Trim();
            TurnPasswordProvider.SetMainTurnPassword(settings, remote.TurnPassword);
            settings.WebRtcTurnUri = null;
            settings.WebRtcTurnUsername = null;
            settings.WebRtcTurnPassword = null;
            settings.MainWebRtcTurnGatewayRevision = remote.ConfigRevision;
            SaveSettings(settings, settingsPath);
            AppLog.Log("[PBX Gateway] TURN sync: applied from gateway");
            return true;
        }

        private static void SaveSettings(AppSettings settings, string settingsPath)
        {
            string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            File.WriteAllText(settingsPath, json);
        }
    }
}
