using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Softphone
{
    /// <summary>
    /// Avalonia application entry point.
    /// Initialized from WPF <see cref="App.OnStartup"/> via
    /// <see cref="AvaloniaAppBuilder.BuildAvaloniaApp"/> during Phase 5 (WPF/Avalonia coexistence).
    ///
    /// Phase 6: WPF App is removed and this becomes the sole entry point.
    /// </summary>
    public class AvaloniaApp : global::Avalonia.Application
    {
        public override void Initialize()
        {
            // FluentTheme — standard Avalonia 12 Fluent v2 style set.
            // FluentIcons.Avalonia SymbolIcon controls are self-contained and need no extra theme.
            Styles.Add(new FluentTheme());

            // Load Phase 5 theme + shared control styles embedded as avares:// resources.
            // The active theme (Dark/Light) is swapped at runtime by ThemeService.ApplyAvaloniaTheme.
            LoadAvaloniaResource("avares://Callspire/Avalonia/Themes/DarkTheme.axaml");
            LoadAvaloniaResource("avares://Callspire/Avalonia/Styles/CommonStyles.axaml");
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Phase 5: still co-existing with WPF — no desktop lifetime wiring here.
            // Phase 6 will add:
            //   if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            //       desktop.MainWindow = new Softphone.Avalonia.MainWindow(...);
            base.OnFrameworkInitializationCompleted();
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private void LoadAvaloniaResource(string uri)
        {
            try
            {
                var dict = (global::Avalonia.Controls.ResourceDictionary)
                    AvaloniaXamlLoader.Load(new Uri(uri));
                Resources.MergedDictionaries.Add(dict);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[AvaloniaApp] Warning: could not load resource '{uri}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Configures and returns the Avalonia <see cref="AppBuilder"/> for the application.
    /// When WPF is fully removed:
    ///   <c>BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)</c>
    /// </summary>
    public static class AvaloniaAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<AvaloniaApp>()
                         .UsePlatformDetect()
                         .LogToTrace();
    }
}
