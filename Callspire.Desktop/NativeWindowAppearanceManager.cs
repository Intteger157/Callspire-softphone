#if WINDOWS
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Softphone
{
    internal static class NativeWindowAppearanceManager
    {
        private const string Win11DarkOverridesUri = "Themes/Windows11Dark.xaml";
        private const string Win11LightOverridesUri = "Themes/Windows11Light.xaml";
        private const string Win11ControlsUri = "Themes/Windows11Controls.xaml";

        // Attached flag so we don't double-subscribe handlers.
        private static readonly DependencyProperty IsAttachedProperty =
            DependencyProperty.RegisterAttached(
                "IsAttached",
                typeof(bool),
                typeof(NativeWindowAppearanceManager),
                new PropertyMetadata(false));

        public static bool IsWindows11OrGreater()
        {
            // Windows 11 is 10.0.22000+
            return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        }

        /// <summary>
        /// Attach native appearance behavior to any window.
        /// Safe to call multiple times.
        /// </summary>
        public static void Attach(Window window)
        {
            if (window == null) return;
            if ((bool)window.GetValue(IsAttachedProperty)) return;
            window.SetValue(IsAttachedProperty, true);

            // Prepare window properties BEFORE HWND creation.
            // (The HWND is usually created on first Show, which happens after ctor.)
            PrepareWindowForPlatform(window);

            WindowWorkAreaHelper.Attach(window, blockMaximize: window is MainWindow);

            // Apply DWM attributes once HWND exists.
            window.SourceInitialized += (_, __) =>
            {
                ApplyDwmIfNeeded(window, ThemeService.GetEffectiveModeFromSettings());
            };
        }

        /// <summary>
        /// Called from ThemeService when theme changes to keep Win11 DWM (mica + dark titlebar) in sync.
        /// </summary>
        public static void OnThemeApplied(ThemeMode effectiveMode)
        {
            if (Application.Current == null) return;
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => OnThemeApplied(effectiveMode)));
                return;
            }

            // Update Win11-only brush overrides.
            TryApplyWin11OverridesToAppResources(effectiveMode);

            // Re-apply DWM attributes to all windows (best-effort).
            foreach (Window w in Application.Current.Windows)
            {
                try { ApplyDwmIfNeeded(w, effectiveMode); } catch { }
            }
        }

        public static void TryApplyWin11OverridesToAppResources(ThemeMode effectiveMode)
        {
            if (Application.Current == null) return;
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => TryApplyWin11OverridesToAppResources(effectiveMode)));
                return;
            }

            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged == null) return;

            int idx = -1;
            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.ToString() ?? "";
                if (src.EndsWith(Win11DarkOverridesUri, StringComparison.OrdinalIgnoreCase) ||
                    src.EndsWith(Win11LightOverridesUri, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }

            int controlsIdx = -1;
            for (int i = 0; i < merged.Count; i++)
            {
                var src = merged[i].Source?.ToString() ?? "";
                if (src.EndsWith(Win11ControlsUri, StringComparison.OrdinalIgnoreCase))
                {
                    controlsIdx = i;
                    break;
                }
            }

            if (!IsWindows11OrGreater())
            {
                if (idx >= 0) merged.RemoveAt(idx);
                if (controlsIdx >= 0) merged.RemoveAt(controlsIdx);
                return;
            }

            var desired = effectiveMode == ThemeMode.Light ? Win11LightOverridesUri : Win11DarkOverridesUri;
            var rd = new ResourceDictionary { Source = new Uri(desired, UriKind.Relative) };
            if (idx >= 0) merged[idx] = rd;
            else merged.Add(rd);

            // Win11 control sizing/visual tweaks (ComboBox etc.)
            var controlsRd = new ResourceDictionary { Source = new Uri(Win11ControlsUri, UriKind.Relative) };
            if (controlsIdx >= 0) merged[controlsIdx] = controlsRd;
            else merged.Add(controlsRd);
        }

        private static void PrepareWindowForPlatform(Window window)
        {
            try
            {
                // Win10 needs an explicit 1px stroke for window separation.
                // On Win11 we prefer native DWM chrome (mica + shadow) and keep the WPF stroke hidden
                // to avoid double borders.
                TrySetWindowStrokeVisibility(window, visible: !IsWindows11OrGreater());

                if (IsWindows11OrGreater())
                {
                    // Win11: все окна непрозрачные для единообразия
                    // Non-layered window so DWM can draw Mica + rounded corners.
                    window.AllowsTransparency = false;

                    // Ensure there is a non-black base surface behind any semi-transparent brushes.
                    // Для CustomMessageBox не устанавливаем фон программно - он уже установлен в XAML
                    if (!(window is CustomMessageBox))
                    {
                        try
                        {
                            if (Application.Current != null && Application.Current.TryFindResource("BackgroundDarkBrush") is Brush b)
                                window.Background = b;
                        }
                        catch { }
                    }

                    // Avoid client-side rounding/clip when using DWM corners (prevents black corner artifacts).
                    // CustomMessageBox использует WPF скругления, поэтому не отключаем их
                    TryDisableClientRounding(window);
                }
                else
                {
                    // Win10: все окна непрозрачные для единообразия
                    window.AllowsTransparency = false;
                    // Для CustomMessageBox не устанавливаем фон программно - он уже установлен в XAML
                    if (!(window is CustomMessageBox))
                    {
                        try
                        {
                            if (Application.Current != null && Application.Current.TryFindResource("BackgroundDarkBrush") is Brush b)
                                window.Background = b;
                        }
                        catch { }
                    }
                }
            }
            catch
            {
                // best-effort
            }
        }

        private static void TrySetWindowStrokeVisibility(Window window, bool visible)
        {
            try
            {
                if (window.FindName("WindowStroke") is Border stroke)
                {
                    stroke.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch
            {
                // best-effort
            }
        }

        private static void ApplyDwmIfNeeded(Window window, ThemeMode effectiveMode)
        {
            if (!IsWindows11OrGreater()) return;
            if (window.AllowsTransparency) return;
            
            // Не применяем DWM эффекты к CustomMessageBox - оно использует WPF скругления и тени
            if (window is CustomMessageBox) return;

            // Rounded corners + Mica + dark titlebar.
            WindowCornerService.TryApplyRoundedCorners(window);
            Windows11BackdropService.TryApply(window, isDark: effectiveMode != ThemeMode.Light);
        }

        private static void TryDisableClientRounding(Window window)
        {
            try
            {
                // Не применяем к CustomMessageBox - оно использует WPF скругления
                if (window is CustomMessageBox) return;
                
                // Теперь используем скругления, поэтому не отключаем их
                // Оставляем ClipToBounds для правильной отрисовки скруглений
                if (window.FindName("WindowSurface") is Border surface)
                {
                    surface.ClipToBounds = true;
                }
                if (window.FindName("WindowStroke") is Border stroke)
                {
                    // WindowStroke остается видимым для скругленных углов
                }
                if (window.FindName("TitleBarBorder") is Border titleBar)
                {
                    titleBar.ClipToBounds = true;
                }
                if (window.FindName("SidebarBorder") is Border sidebar)
                {
                    sidebar.ClipToBounds = true;
                }
            }
            catch { }
        }
    }
}



#endif // WINDOWS
