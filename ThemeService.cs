using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace Softphone
{
    public static class ThemeService
    {
        private static readonly object _lock = new object();
        private static bool _initialized;
        private static bool _systemEventHooked;

        private const string DarkThemeUri = "Themes/DarkTheme.xaml";
        private const string LightThemeUri = "Themes/LightTheme.xaml";

        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;
            }

            // Apply once on startup.
            try { ApplyFromSettings(); } catch { }

            // React to system theme changes when ThemeMode == System.
            try
            {
                if (!_systemEventHooked)
                {
                    SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
                    _systemEventHooked = true;
                }
            }
            catch { }
        }

        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            try
            {
                if (GetConfiguredMode() == ThemeMode.System)
                {
                    ApplyTheme(ThemeMode.System);
                }
            }
            catch { }
        }

        public static void ApplyFromSettings()
        {
            ApplyTheme(GetConfiguredMode());
        }

        public static ThemeMode GetEffectiveModeFromSettings()
        {
            var configured = GetConfiguredMode();
            return configured == ThemeMode.System ? GetSystemEffectiveMode() : configured;
        }

        public static ThemeMode GetConfiguredMode()
        {
            try
            {
                var path = AppDataHelper.GetSettingsFilePath();
                if (!File.Exists(path)) return ThemeMode.System;
                var json = File.ReadAllText(path);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
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
                AppSettings settings;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                settings.ThemeMode = mode.ToString().ToLowerInvariant();
                File.WriteAllText(path, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch
            {
                // ignore
            }

            ApplyTheme(mode);
        }

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

        public static void ApplyTheme(ThemeMode mode)
        {
            if (Application.Current == null) return;

            // Must run on UI thread.
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => ApplyTheme(mode)));
                return;
            }

            var effective = mode == ThemeMode.System ? GetSystemEffectiveMode() : mode;
            var desiredUri = effective == ThemeMode.Light ? LightThemeUri : DarkThemeUri;

            try
            {
                var merged = Application.Current.Resources.MergedDictionaries;
                if (merged == null) return;

                // Platform-specific theme overrides (Win11 Mica-friendly brushes etc.)
                NativeWindowAppearanceManager.TryApplyWin11OverridesToAppResources(effective);

                // Find current theme dictionary (dark/light).
                int idx = -1;
                for (int i = 0; i < merged.Count; i++)
                {
                    var src = merged[i].Source?.ToString() ?? "";
                    if (src.EndsWith(DarkThemeUri, StringComparison.OrdinalIgnoreCase) ||
                        src.EndsWith(LightThemeUri, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }

                var rd = new ResourceDictionary { Source = new Uri(desiredUri, UriKind.Relative) };

                if (idx >= 0)
                {
                    merged[idx] = rd;
                }
                else
                {
                    merged.Add(rd);
                }

                // Force refresh all open windows
                foreach (Window window in Application.Current.Windows)
                {
                    try
                    {
                        window.InvalidateVisual();
                        window.UpdateLayout();
                    }
                    catch { }
                }

                // Let the platform manager sync DWM settings etc.
                NativeWindowAppearanceManager.OnThemeApplied(effective);
            }
            catch
            {
                // ignore
            }
        }

        private static ThemeMode GetSystemEffectiveMode()
        {
            try
            {
                // 1 = Light, 0 = Dark
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                object? v = key?.GetValue("AppsUseLightTheme");
                if (v is int i)
                {
                    return i == 1 ? ThemeMode.Light : ThemeMode.Dark;
                }
                if (v is byte b)
                {
                    return b == 1 ? ThemeMode.Light : ThemeMode.Dark;
                }
            }
            catch { }

            return ThemeMode.Dark;
        }
    }
}


