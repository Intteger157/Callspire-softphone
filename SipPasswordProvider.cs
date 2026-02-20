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


