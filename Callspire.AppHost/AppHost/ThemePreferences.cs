using System;
using System.IO;
using Newtonsoft.Json;

namespace Softphone.AppHost
{
    /// <summary>
    /// UI-free theme preference storage. Persists <see cref="AppSettings.ThemeMode"/> and notifies
    /// the hosting shell (WPF <c>ThemeService</c>, SwiftUI via IPC) so it can apply the visual change.
    /// </summary>
    public static class ThemePreferences
    {
        /// <summary>Raised after the configured mode was persisted. May fire on any thread.</summary>
        public static event Action<ThemeMode>? ConfiguredModeChanged;

        public static ThemeMode ParseMode(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return ThemeMode.System;
            return raw.Trim().ToLowerInvariant() switch
            {
                "dark" => ThemeMode.Dark,
                "light" => ThemeMode.Light,
                _ => ThemeMode.System
            };
        }

        public static ThemeMode GetConfiguredMode()
        {
            try
            {
                var path = AppDataHelper.GetSettingsFilePath();
                if (!File.Exists(path)) return ThemeMode.System;
                var settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path));
                return ParseMode(settings?.ThemeMode);
            }
            catch
            {
                return ThemeMode.System;
            }
        }

        public static void SetConfiguredMode(ThemeMode mode)
        {
            try
            {
                var path = AppDataHelper.GetSettingsFilePath();
                var settings = File.Exists(path)
                    ? JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(path)) ?? new AppSettings()
                    : new AppSettings();
                settings.ThemeMode = mode.ToString().ToLowerInvariant();
                File.WriteAllText(path, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                AppLog.Log($"[ThemePreferences] persist failed: {ex.Message}");
            }

            ConfiguredModeChanged?.Invoke(mode);
        }
    }
}
