using System;
using global::Avalonia.Media;

namespace Softphone.Avalonia
{
    /// <summary>
    /// Runtime UI capability hints for the Avalonia shell. Bound from AXAML via <c>{x:Static}</c>.
    ///
    /// <see cref="IconFontAvailable"/> probes the FluentIcons "Seagull Fluent Icons" embedded font
    /// that <c>ic:SymbolIcon</c> draws with. If the font cannot be resolved (seen on macOS with a
    /// self-contained .app), the navigation rail and call buttons fall back to plain Unicode glyphs
    /// instead of rendering as empty squares.
    /// </summary>
    public static class PlatformUiHints
    {
        private const string SeagullTypeface = "avares://FluentIcons.Resources.Avalonia/Assets#Seagull Fluent Icons";

        private static bool? _iconFontAvailable;

        /// <summary>True when FluentIcons glyphs can be rendered on this platform.</summary>
        public static bool IconFontAvailable => _iconFontAvailable ??= ProbeIconFont();

        /// <summary>True when text-based icon fallbacks must be shown instead of <c>SymbolIcon</c>.</summary>
        public static bool UseTextNavFallback => !IconFontAvailable;

        private static bool ProbeIconFont()
        {
            try
            {
                var ok = FontManager.Current.TryGetGlyphTypeface(new Typeface(SeagullTypeface), out var glyphTypeface)
                         && glyphTypeface.FamilyName.Contains("Seagull", StringComparison.OrdinalIgnoreCase);
                AppLog.Log($"[AvaloniaApp] FluentIcons font available: {ok} (family='{glyphTypeface?.FamilyName}')");
                return ok;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[AvaloniaApp] FluentIcons font probe failed: {ex.Message}");
                return false;
            }
        }
    }
}
