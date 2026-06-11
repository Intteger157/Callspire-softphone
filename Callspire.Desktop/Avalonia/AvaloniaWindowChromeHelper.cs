using System;
using System.Runtime.InteropServices;
using global::Avalonia.Controls;

namespace Softphone.Avalonia
{
    /// <summary>
    /// Cross-platform replacement for <c>NativeWindowAppearanceManager</c>,
    /// <c>Windows11BackdropService</c>, and <c>WindowCornerService</c>.
    ///
    /// On Windows 11 the window gets Mica (or Acrylic as fallback).
    /// On Windows 10 and all other platforms it gracefully falls back to
    /// standard (solid background) decorations — no P/Invoke, no platform guards.
    ///
    /// Usage: call <see cref="Apply"/> right after <c>InitializeComponent()</c>.
    /// </summary>
    public static class AvaloniaWindowChromeHelper
    {
        private static readonly bool _isWindows11 = CheckIsWindows11();

        /// <summary>
        /// Applies the best available backdrop for the current platform.
        /// </summary>
        /// <param name="window">The Avalonia window to configure.</param>
        /// <param name="preferAccent">
        ///   When <c>true</c> the window tries Acrylic instead of Mica
        ///   (better for small/popup windows like <see cref="CallWindow"/>).
        /// </param>
        public static void Apply(Window window, bool preferAccent = false)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));

            if (_isWindows11)
            {
                // Avalonia 12 exposes TransparencyLevelHint as a Window property.
                // On Windows 11 Mica/AcrylicBlur are supported natively.
                window.TransparencyLevelHint = preferAccent
                    ? new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None }
                    : new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None };

                window.Background = new global::Avalonia.Media.SolidColorBrush(
                    global::Avalonia.Media.Color.Parse("#CC1E1E1E")); // semi-transparent to let Mica tint show through
            }
            else
            {
                // Windows 10 / macOS / Linux: solid background, no transparency tricks.
                window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            }

            // Frameless chrome — same on all platforms.
            // The window XAML already sets SystemDecorations="None" + ExtendClientAreaToDecorationsHint="True".
            // Nothing more to do here for non-Windows platforms; they get a clean solid window.
        }

        // ─────────────────────────────────────────────────────────────────────
        private static bool CheckIsWindows11()
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
                var v = Environment.OSVersion.Version;
                // Windows 11 starts at build 22000
                return v.Build >= 22000;
            }
            catch
            {
                return false;
            }
        }
    }
}
