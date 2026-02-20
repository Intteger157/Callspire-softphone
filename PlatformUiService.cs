using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Softphone
{
    // Backward-compat wrapper. Keep for now to avoid touching too much code at once.
    internal static class PlatformUiService
    {
        public static bool IsWindows11OrGreater() => NativeWindowAppearanceManager.IsWindows11OrGreater();

        public static void ApplyPlatformWindowMode(Window window)
        {
            // Refactored: central logic lives in NativeWindowAppearanceManager.
            NativeWindowAppearanceManager.Attach(window);
        }
    }
}


