#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Softphone
{
    internal static class Windows11BackdropService
    {
        // Windows 11+:
        // - DWMWA_SYSTEMBACKDROP_TYPE = 38
        // - DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (some builds used 19; 20 works on Win11)
        // - DWMWA_TRANSITIONS_FORCEDISABLED = 3 (для включения плавных анимаций)
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

        private enum DwmSystemBackdropType
        {
            Auto = 0,
            None = 1,
            Mica = 2,
            Acrylic = 3,
            Tabbed = 4
        }

        [DllImport("dwmapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>
        /// Явно включает плавные анимации переходов для окна (минимизация, максимизация и т.д.)
        /// </summary>
        public static void EnsureTransitionsEnabled(Window window)
        {
            try
            {
                if (!NativeWindowAppearanceManager.IsWindows11OrGreater()) return;
                if (window.AllowsTransparency) return;

                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                // Включаем плавные анимации переходов (0 = FALSE означает, что анимации НЕ отключены, т.е. включены)
                int transitionsEnabled = 0;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref transitionsEnabled, sizeof(int));
            }
            catch
            {
                // best-effort
            }
        }

        public static void TryApply(Window window, bool isDark)
        {
            try
            {
                if (!NativeWindowAppearanceManager.IsWindows11OrGreater()) return;
                if (window.AllowsTransparency) return; // layered windows don't get DWM backdrops reliably

                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                // Применяем DWM атрибуты для внешнего вида окна
                int dark = isDark ? 1 : 0;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                int backdrop = (int)DwmSystemBackdropType.Mica;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

                // НЕ применяем DWMWA_TRANSITIONS_FORCEDISABLED здесь - анимации должны работать по умолчанию
                // Явное включение анимаций происходит через EnsureTransitionsEnabled() перед минимизацией
            }
            catch
            {
                // best-effort
            }
        }
    }
}



#endif // WINDOWS
