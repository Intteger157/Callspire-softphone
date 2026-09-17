using System;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Media;
using global::Avalonia.Platform;

namespace Softphone.Avalonia
{
    /// <summary>
    /// Platform-aware window chrome for the Avalonia shell.
    ///
    /// <list type="bullet">
    ///   <item><b>macOS / Linux</b> — native system decorations (traffic lights, title bar, resize
    ///   handles, safe-area behaviour). The XAML custom title bar (<c>Name="CustomTitleBar"</c>) is
    ///   hidden and the rounded <c>WindowSurface</c> border is squared so the window fills the
    ///   native frame. Brand colours stay inside the client area via Dark/Light theme brushes.</item>
    ///   <item><b>Windows</b> — the existing frameless chrome (custom title bar, Mica / Acrylic
    ///   where supported) is kept for visual parity with the WPF build.</item>
    /// </list>
    ///
    /// Call <see cref="Apply"/> right after <c>InitializeComponent()</c>.
    /// </summary>
    public static class PlatformWindowChrome
    {
        /// <summary>Name used in AXAML for the custom (frameless) title bar container.</summary>
        public const string CustomTitleBarName = "CustomTitleBar";

        /// <summary>Name used in AXAML for the rounded window surface border.</summary>
        public const string WindowSurfaceName = "WindowSurface";

        /// <summary>True when the OS should draw the window frame (macOS, Linux).</summary>
        public static bool UseNativeChrome => !OperatingSystem.IsWindows();

        /// <summary>Convenience for AXAML bindings: true when the custom title bar must be visible.</summary>
        public static bool IsCustomChrome => !UseNativeChrome;

        private static WindowIcon? _appIcon;

        public static void Apply(Window window, bool preferAccent = false)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));

            TryApplyAppIcon(window);

            if (UseNativeChrome)
            {
                ApplyNativeChrome(window);
                return;
            }

            // Windows: frameless chrome + backdrop handled by the legacy helper.
            AvaloniaWindowChromeHelper.ApplyWindowsBackdrop(window, preferAccent);
        }

        private static void ApplyNativeChrome(Window window)
        {
            window.WindowDecorations = WindowDecorations.Full;
            window.ExtendClientAreaToDecorationsHint = false;
            window.ExtendClientAreaTitleBarHeightHint = -1;
            window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };

            // Opaque themed background (the XAML may declare Background="Transparent" for the
            // rounded frameless look, which renders as a black/garbage frame on macOS).
            try
            {
                window.Bind(global::Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty,
                            window.GetResourceObservable("BackgroundDarkBrush"));
            }
            catch
            {
                window.Background = Brushes.Transparent;
            }

            // Hide the custom title bar: the system title bar takes over.
            if (window.FindControl<Control>(CustomTitleBarName) is Control titleBar)
                titleBar.IsVisible = false;

            // Square off the surface so it fills the native frame with no inset.
            if (window.FindControl<Border>(WindowSurfaceName) is Border surface)
            {
                surface.CornerRadius = new CornerRadius(0);
                surface.BorderThickness = new Thickness(0);
                surface.Padding = new Thickness(0);
                surface.ClipToBounds = false;
            }
        }

        private static void TryApplyAppIcon(Window window)
        {
            try
            {
                if (window.Icon != null) return;
                _appIcon ??= LoadAppIcon();
                if (_appIcon != null) window.Icon = _appIcon;
            }
            catch
            {
                // Icon is cosmetic; never fail window creation because of it.
            }
        }

        private static WindowIcon? LoadAppIcon()
        {
            try
            {
                var uri = new Uri("avares://Callspire/Assets/icon.png");
                if (!AssetLoader.Exists(uri)) return null;
                using var stream = AssetLoader.Open(uri);
                return new WindowIcon(stream);
            }
            catch
            {
                return null;
            }
        }
    }
}
