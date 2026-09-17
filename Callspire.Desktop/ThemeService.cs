using System;
using System.Linq;
using Microsoft.Win32;
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

            try { ApplyFromSettings(); } catch { }

            Softphone.AppHost.ThemePreferences.ConfiguredModeChanged += mode => { try { ApplyTheme(mode); } catch { } };

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
                    ApplyTheme(ThemeMode.System);
            }
            catch { }
        }

        public static void ApplyFromSettings() => ApplyTheme(GetConfiguredMode());

        public static ThemeMode GetEffectiveModeFromSettings()
        {
            var configured = GetConfiguredMode();
            return configured == ThemeMode.System ? GetSystemEffectiveMode() : configured;
        }

        public static ThemeMode GetConfiguredMode() => Softphone.AppHost.ThemePreferences.GetConfiguredMode();

        public static void SetConfiguredMode(ThemeMode mode) => Softphone.AppHost.ThemePreferences.SetConfiguredMode(mode);

        public static ThemeMode ParseMode(string? raw) => Softphone.AppHost.ThemePreferences.ParseMode(raw);

        public static void ApplyTheme(ThemeMode mode)
        {
            ApplyWpfTheme(mode == ThemeMode.System ? GetSystemEffectiveMode() : mode);
        }

        private static void ApplyWpfTheme(ThemeMode effective)
        {
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
                else merged.Add(rd);

                foreach (Window window in Application.Current.Windows)
                {
                    try { window.InvalidateVisual(); window.UpdateLayout(); } catch { }
                }

                NativeWindowAppearanceManager.OnThemeApplied(effective);
            }
            catch { }
        }

        private static ThemeMode GetSystemEffectiveMode()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                object? v = key?.GetValue("AppsUseLightTheme");
                if (v is int i) return i == 1 ? ThemeMode.Light : ThemeMode.Dark;
                if (v is byte b) return b == 1 ? ThemeMode.Light : ThemeMode.Dark;
            }
            catch { }
            return ThemeMode.Dark;
        }
    }
}
