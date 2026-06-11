#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Softphone
{
    internal static class WindowCornerService
    {
        // Windows 11+ only: DWMWA_WINDOW_CORNER_PREFERENCE = 33
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        private enum DwmWindowCornerPreference
        {
            Default = 0,
            DoNotRound = 1,
            Round = 2,
            RoundSmall = 3
        }

        [DllImport("dwmapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void TryApplyRoundedCorners(Window window)
        {
            try
            {
                // Only meaningful on Windows 11 (build 22000+). On older OS it's a no-op.
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
                // DWM corner preference generally doesn't apply to layered (transparent) windows.
                if (window.AllowsTransparency) return;

                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                int value = (int)DwmWindowCornerPreference.Round;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
            }
            catch
            {
                // best-effort, ignore
            }
        }
    }
}



#endif // WINDOWS
