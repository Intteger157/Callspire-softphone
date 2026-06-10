using System;
using System.IO;
using Newtonsoft.Json;

namespace Softphone
{
    /// <summary>
    /// Provider for SIP (PBX) password stored in settings.json.
    /// Supports encrypted storage (SipPasswordEncrypted) and legacy plaintext (SipPassword).
    /// </summary>
    public static class SipPasswordProvider
    {
        public static string? GetPassword(AppSettings? settings)
        {
            if (settings == null) return null;

            if (!string.IsNullOrWhiteSpace(settings.SipPasswordEncrypted))
            {
                var decrypted = TokenEncryption.Decrypt(settings.SipPasswordEncrypted);
                return string.IsNullOrWhiteSpace(decrypted) ? null : decrypted;
            }

            // Legacy plaintext
            return string.IsNullOrWhiteSpace(settings.SipPassword) ? null : settings.SipPassword;
        }

        /// <summary>
        /// WebRTC digest password for the main line: <see cref="AppSettings.WebRtcPasswordEncrypted"/> if set, else SIP password (legacy).
        /// </summary>
        public static string? GetMainWebRtcPassword(AppSettings? settings)
        {
            if (settings == null) return null;

            string? sipFallback = GetPassword(settings);
            if (string.IsNullOrWhiteSpace(settings.WebRtcPasswordEncrypted))
                return NormalizeSecret(sipFallback);

            string? webRtc = null;
            try
            {
                webRtc = NormalizeSecret(TokenEncryption.Decrypt(settings.WebRtcPasswordEncrypted));
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipPasswordProvider] WebRtcPasswordEncrypted decrypt failed ({DescribeEnc(settings.WebRtcPasswordEncrypted)}): {ex.Message}");
            }

            if (!string.IsNullOrEmpty(webRtc))
            {
                if (!string.IsNullOrEmpty(sipFallback) && webRtc != sipFallback)
                {
                    MainWindow.Log("[SipPasswordProvider] WARNING: WebRTC and SIP passwords differ in settings.json — WebRTC field is used. Re-save connection settings to sync.");
                }
                return webRtc;
            }

            if (!string.IsNullOrEmpty(sipFallback))
            {
                MainWindow.Log("[SipPasswordProvider] WebRTC password unavailable; using SIP password fallback.");
            }

            return sipFallback;
        }

        private static string? NormalizeSecret(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string DescribeEnc(string? encrypted)
        {
            if (string.IsNullOrEmpty(encrypted)) return "empty";
            if (encrypted.StartsWith("dpapi:", StringComparison.Ordinal)) return "dpapi";
            return "legacy-aes";
        }

        /// <summary>
        /// WebRTC digest password for line 2: dedicated field if set, else second SIP password (legacy).
        /// </summary>
        public static string? GetSecondaryWebRtcPassword(AppSettings? settings)
        {
            if (settings == null) return null;
            if (!string.IsNullOrWhiteSpace(settings.WebRtcPasswordEncrypted2))
            {
                try
                {
                    var d = TokenEncryption.Decrypt(settings.WebRtcPasswordEncrypted2);
                    if (!string.IsNullOrWhiteSpace(d)) return d;
                }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(settings.SipPasswordEncrypted2))
            {
                try
                {
                    var d = TokenEncryption.Decrypt(settings.SipPasswordEncrypted2);
                    return string.IsNullOrWhiteSpace(d) ? null : d;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Migrates legacy AES-encrypted SipPasswordEncrypted -> DPAPI format (best-effort).
        /// </summary>
        public static void MigrateEncryptedToDpapiIfNeeded(string settingsFilePath, AppSettings settings)
        {
            try
            {
                if (settings == null) return;
                if (string.IsNullOrWhiteSpace(settings.SipPasswordEncrypted)) return;
                if (settings.SipPasswordEncrypted.StartsWith("dpapi:", StringComparison.Ordinal)) return;

                var decrypted = TokenEncryption.Decrypt(settings.SipPasswordEncrypted);
                if (string.IsNullOrWhiteSpace(decrypted)) return;

                settings.SipPasswordEncrypted = TokenEncryption.Encrypt(decrypted);
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsFilePath, json);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// Migrates legacy plaintext SipPassword -> SipPasswordEncrypted (best-effort).
        /// </summary>
        public static void MigratePlaintextToEncryptedIfNeeded(string settingsFilePath, AppSettings settings)
        {
            try
            {
                if (settings == null) return;
                if (!string.IsNullOrWhiteSpace(settings.SipPasswordEncrypted)) return;
                if (string.IsNullOrWhiteSpace(settings.SipPassword)) return;

                settings.SipPasswordEncrypted = TokenEncryption.Encrypt(settings.SipPassword);
                settings.SipPassword = null; // don't keep plaintext on disk

                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsFilePath, json);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipPasswordProvider] Error migrating SIP password to encrypted: {ex.Message}");
            }
        }
    }
}


