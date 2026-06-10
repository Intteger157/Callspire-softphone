using System;
using System.IO;
using Newtonsoft.Json;

namespace Softphone
{
    /// <summary>
    /// TURN server passwords in settings.json (encrypted at rest via <see cref="TokenEncryption"/> / DPAPI).
    /// </summary>
    public static class TurnPasswordProvider
    {
        public static string? GetMainTurnPassword(AppSettings? settings)
        {
            if (settings == null) return null;

            if (!string.IsNullOrWhiteSpace(settings.MainWebRtcTurnPasswordEncrypted))
            {
                var decrypted = TokenEncryption.Decrypt(settings.MainWebRtcTurnPasswordEncrypted);
                if (!string.IsNullOrWhiteSpace(decrypted)) return decrypted;
            }

            // Legacy plaintext (pre-encryption migration)
            if (!string.IsNullOrWhiteSpace(settings.MainWebRtcTurnPassword))
                return settings.MainWebRtcTurnPassword;
            if (!string.IsNullOrWhiteSpace(settings.WebRtcTurnPassword))
                return settings.WebRtcTurnPassword;

            return null;
        }

        public static string? GetSecondaryTurnPassword(AppSettings? settings)
        {
            if (settings == null) return null;

            if (!string.IsNullOrWhiteSpace(settings.SecondaryWebRtcTurnPasswordEncrypted))
            {
                var decrypted = TokenEncryption.Decrypt(settings.SecondaryWebRtcTurnPasswordEncrypted);
                if (!string.IsNullOrWhiteSpace(decrypted)) return decrypted;
            }

            return string.IsNullOrWhiteSpace(settings.SecondaryWebRtcTurnPassword)
                ? null
                : settings.SecondaryWebRtcTurnPassword;
        }

        public static void SetMainTurnPassword(AppSettings settings, string? plainPassword)
        {
            if (settings == null) return;

            if (string.IsNullOrWhiteSpace(plainPassword))
            {
                settings.MainWebRtcTurnPasswordEncrypted = null;
            }
            else
            {
                settings.MainWebRtcTurnPasswordEncrypted = TokenEncryption.Encrypt(plainPassword);
            }

            settings.MainWebRtcTurnPassword = null;
            settings.WebRtcTurnPassword = null;
        }

        public static void ClearSecondaryTurnPassword(AppSettings settings)
        {
            if (settings == null) return;
            settings.SecondaryWebRtcTurnPasswordEncrypted = null;
            settings.SecondaryWebRtcTurnPassword = null;
        }

        /// <summary>Migrates legacy plaintext TURN passwords to encrypted fields.</summary>
        public static void MigratePlaintextToEncryptedIfNeeded(string settingsFilePath, AppSettings settings)
        {
            try
            {
                if (settings == null) return;

                bool changed = false;

                if (string.IsNullOrWhiteSpace(settings.MainWebRtcTurnPasswordEncrypted))
                {
                    string? legacy = !string.IsNullOrWhiteSpace(settings.MainWebRtcTurnPassword)
                        ? settings.MainWebRtcTurnPassword
                        : settings.WebRtcTurnPassword;
                    if (!string.IsNullOrWhiteSpace(legacy))
                    {
                        settings.MainWebRtcTurnPasswordEncrypted = TokenEncryption.Encrypt(legacy);
                        settings.MainWebRtcTurnPassword = null;
                        settings.WebRtcTurnPassword = null;
                        changed = true;
                    }
                }

                if (string.IsNullOrWhiteSpace(settings.SecondaryWebRtcTurnPasswordEncrypted)
                    && !string.IsNullOrWhiteSpace(settings.SecondaryWebRtcTurnPassword))
                {
                    settings.SecondaryWebRtcTurnPasswordEncrypted =
                        TokenEncryption.Encrypt(settings.SecondaryWebRtcTurnPassword);
                    settings.SecondaryWebRtcTurnPassword = null;
                    changed = true;
                }

                if (!changed) return;

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsFilePath, json);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[TurnPasswordProvider] Error migrating TURN password to encrypted: {ex.Message}");
            }
        }

        /// <summary>Migrates AES-encrypted TURN passwords to DPAPI format.</summary>
        public static void MigrateEncryptedToDpapiIfNeeded(string settingsFilePath, AppSettings settings)
        {
            try
            {
                if (settings == null) return;

                bool changed = false;
                changed |= MigrateFieldToDpapi(settings.MainWebRtcTurnPasswordEncrypted, v => settings.MainWebRtcTurnPasswordEncrypted = v);
                changed |= MigrateFieldToDpapi(settings.SecondaryWebRtcTurnPasswordEncrypted, v => settings.SecondaryWebRtcTurnPasswordEncrypted = v);

                if (!changed) return;

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsFilePath, json);
            }
            catch
            {
                // ignore
            }
        }

        private static bool MigrateFieldToDpapi(string? encrypted, Action<string> setter)
        {
            if (string.IsNullOrWhiteSpace(encrypted)) return false;
            if (encrypted.StartsWith("dpapi:", StringComparison.Ordinal)) return false;

            var decrypted = TokenEncryption.Decrypt(encrypted);
            if (string.IsNullOrWhiteSpace(decrypted)) return false;

            setter(TokenEncryption.Encrypt(decrypted));
            return true;
        }
    }
}
