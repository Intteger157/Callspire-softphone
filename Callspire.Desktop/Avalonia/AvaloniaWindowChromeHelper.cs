using System;
using System.Runtime.InteropServices;
using global::Avalonia.Controls;

namespace Softphone.Avalonia
{
    /// <summary>
    /// Cross-platform replacement for <c>NativeWindowAppearanceManager</c>,
    /// <c>Windows11BackdropService</c>, and <c>WindowCornerService</c>.
    ///
    /// <see cref="Apply"/> is kept for backwards compatibility and now delegates to
    /// <see cref="PlatformWindowChrome.Apply"/>, which picks native decorations on
    /// macOS / Linux and the frameless Windows chrome (Mica / Acrylic) on Windows.
    /// </summary>
    public static class AvaloniaWindowChromeHelper
    {
        private static readonly bool _isWindows11 = CheckIsWindows11();

        /// <summary>
        /// Applies the best available chrome for the current platform.
        /// </summary>
        public static void Apply(Window window, bool preferAccent = false)
            => PlatformWindowChrome.Apply(window, preferAccent);

        /// <summary>
        /// Windows-only backdrop tuning for the frameless chrome. On Windows 11 the window gets
        /// Mica (or Acrylic as fallback); on Windows 10 a solid background.
        /// </summary>
        internal static void ApplyWindowsBackdrop(Window window, bool preferAccent)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));

            if (_isWindows11)
            {
                window.TransparencyLevelHint = preferAccent
                    ? new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None }
                    : new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None };

                window.Background = new global::Avalonia.Media.SolidColorBrush(
                    global::Avalonia.Media.Color.Parse("#CC1E1E1E")); // semi-transparent to let Mica tint show through
            }
            else
            {
                window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            }
        }

        private static bool CheckIsWindows11()
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
                var v = Environment.OSVersion.Version;
                return v.Build >= 22000;
            }
            catch
            {
                return false;
            }
        }
    }
}
