using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
#if WINDOWS
using Microsoft.Win32;
using System.Windows;
#endif
using Avalonia.Styling;

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
            // WPF path: Win32 UserPreferenceChanged (Windows only).
#if WINDOWS
            try
            {
                if (!_systemEventHooked)
                {
                    SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
                    _systemEventHooked = true;
                }
            }
            catch { }
#endif

            // Avalonia path: subscribe to platform color values changed (cross-platform).
            HookAvaloniaPlatformSettings();
        }

        private static bool _avaloniaHooked;

        /// <summary>
        /// Must be called once the Avalonia <c>Application</c> exists (e.g. from
        /// <c>OnFrameworkInitializationCompleted</c>) and <b>before</b> the first window is shown.
        /// <see cref="Initialize"/> may run earlier from the bootstrap, when
        /// <c>Application.Current</c> is still null — this call completes the Avalonia side.
        /// </summary>
        public static void EnsureAvaloniaInitialized()
        {
            Initialize();
            try { ApplyFromSettings(); } catch { }
            HookAvaloniaPlatformSettings();
        }

        private static void HookAvaloniaPlatformSettings()
        {
            try
            {
                lock (_lock)
                {
                    if (_avaloniaHooked) return;
                    var platformSettings = global::Avalonia.Application.Current?.PlatformSettings;
                    if (platformSettings == null) return;
                    platformSettings.ColorValuesChanged += OnAvaloniaPlatformColorValuesChanged;
                    _avaloniaHooked = true;
                }
            }
            catch { }
        }

        private static void OnAvaloniaPlatformColorValuesChanged(
            object? sender,
            global::Avalonia.Platform.PlatformColorValues e)
        {
            try
            {
                if (GetConfiguredMode() == ThemeMode.System)
                    ApplyTheme(ThemeMode.System);
            }
            catch { }
        }

#if WINDOWS
        private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            try
            {
                if (GetConfiguredMode() == ThemeMode.System)
                    ApplyTheme(ThemeMode.System);
            }
            catch { }
        }
#endif

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
            var effective = mode == ThemeMode.System ? GetSystemEffectiveMode() : mode;

            // --- Avalonia path ---
            // When Avalonia windows are active, drive their theme via RequestedThemeVariant.
            ApplyAvaloniaTheme(effective);

            // --- WPF path (kept for existing WPF windows during Phase 5 incremental port) ---
            ApplyWpfTheme(effective);
        }

        /// <summary>
        /// Applies the theme to Avalonia windows via <see cref="Avalonia.Application.RequestedThemeVariant"/>.
        /// No-op when the Avalonia application hasn't been initialized yet.
        /// </summary>
        private static void ApplyAvaloniaTheme(ThemeMode effective)
        {
            try
            {
                var app = global::Avalonia.Application.Current;
                if (app == null) return;

                app.RequestedThemeVariant = effective == ThemeMode.Light
                    ? ThemeVariant.Light
                    : ThemeVariant.Dark;
            }
            catch
            {
                // Avalonia may not be initialized on this code path yet.
            }
        }

        /// <summary>
        /// Applies the theme to WPF windows via ResourceDictionary swap (Windows only).
        /// Kept intact for the Phase 5 incremental port; removed when all WPF windows are gone.
        /// </summary>
        private static void ApplyWpfTheme(ThemeMode effective)
        {
#if WINDOWS
            if (Application.Current == null) return;

            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => ApplyWpfTheme(effective)));
                return;
            }

            var desiredUri = effective == ThemeMode.Light ? LightThemeUri : DarkThemeUri;

            try
            {
                var merged = Application.Current.Resources.MergedDictionaries;
                if (merged == null) return;

                NativeWindowAppearanceManager.TryApplyWin11OverridesToAppResources(effective);

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
                if (idx >= 0) merged[idx] = rd;
                else          merged.Add(rd);

                foreach (Window window in Application.Current.Windows)
                {
                    try { window.InvalidateVisual(); window.UpdateLayout(); } catch { }
                }

                NativeWindowAppearanceManager.OnThemeApplied(effective);
            }
            catch { }
#endif
        }

        private static ThemeMode GetSystemEffectiveMode()
        {
#if WINDOWS
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                object? v = key?.GetValue("AppsUseLightTheme");
                if (v is int i) return i == 1 ? ThemeMode.Light : ThemeMode.Dark;
                if (v is byte b) return b == 1 ? ThemeMode.Light : ThemeMode.Dark;
            }
            catch { }
#endif
            // Non-Windows: query Avalonia platform for system preference
            try
            {
                var platformSettings = global::Avalonia.Application.Current?.PlatformSettings;
                if (platformSettings != null)
                {
                    var colors = platformSettings.GetColorValues();
                    return colors.ThemeVariant == global::Avalonia.Platform.PlatformThemeVariant.Light
                        ? ThemeMode.Light
                        : ThemeMode.Dark;
                }
            }
            catch { }

            return ThemeMode.Dark;
        }
    }
}


