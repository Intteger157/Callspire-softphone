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
            // FluentTheme — standard Avalonia 12 Fluent v2 style set with the brand accent, so
            // checkbox/slider/selection/focus colours derived from SystemAccentColor match the palette.
            // FluentIcons.Avalonia SymbolIcon controls are self-contained and need no extra theme.
            var fluent = new FluentTheme();
            var accent = global::Avalonia.Media.Color.Parse("#5865F2");
            fluent.Palettes[ThemeVariant.Dark]  = new ColorPaletteResources { Accent = accent, RegionColor = global::Avalonia.Media.Color.Parse("#1E1E1E") };
            fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = accent, RegionColor = global::Avalonia.Media.Color.Parse("#F5F5F5") };
            Styles.Add(fluent);

            // Brand palette: Dark + Light dictionaries registered as ThemeDictionaries so every
            // {DynamicResource ...Brush} follows Application.RequestedThemeVariant, which is what
            // ThemeService.ApplyAvaloniaTheme toggles. (Previously only DarkTheme was merged and the
            // Light theme never took effect.)
            LoadThemeDictionaries(
                darkUri:  "avares://Callspire/Avalonia/Themes/DarkTheme.axaml",
                lightUri: "avares://Callspire/Avalonia/Themes/LightTheme.axaml");

            LoadAvaloniaResource("avares://Callspire/Avalonia/Styles/CommonStyles.axaml");
            LoadAvaloniaStyles("avares://Callspire/Avalonia/Styles/FormControls.axaml");
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Theme must be resolved before the first window is shown, otherwise the
            // window is created with the default variant and re-styled a frame later.
            try { ThemeService.EnsureAvaloniaInitialized(); }
            catch (Exception ex) { AppLog.Log($"[AvaloniaApp] Theme init: {ex.Message}"); }

            // One-line diagnostics for the Mac smoke test: theme/style dictionaries + icon font.
            AppLog.Log($"[AvaloniaApp] themes loaded: dark={_darkThemeLoaded}, light={_lightThemeLoaded}, common={_commonStylesLoaded}, " +
                       $"fluentIcons={Softphone.Avalonia.PlatformUiHints.IconFontAvailable}");

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = global::Avalonia.Controls.ShutdownMode.OnMainWindowClose;

                var controller = Softphone.AppHost.DesktopAppController.CreateFromSettings();
                var main = new Softphone.Avalonia.MainWindow(controller);
                desktop.MainWindow = main;

                desktop.ShutdownRequested += (_, _) =>
                {
                    try { controller.ShutdownTelephonyBestEffort(); } catch { }
                };
                desktop.Exit += (_, _) =>
                {
                    try { controller.Dispose(); } catch { }
                };

                main.Show();
                _ = controller.StartAsync();

                // ── click-to-call plumbing ───────────────────────────────────
                _controller = controller;
                _mainWindow = main;
                Softphone.Platform.ProtocolActivation.AttachHandler(url =>
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() => controller.HandleProtocolUrl(url)));

                // Linux/xdg-open or manual launch: URL as a command-line argument.
                var fromArgs = Softphone.Platform.ProtocolActivation.FindInArgs(desktop.Args);
                if (fromArgs != null) Softphone.Platform.ProtocolActivation.Enqueue(fromArgs);

                // macOS: LaunchServices delivers callspire:// via application:openURLs: → IActivatableLifetime.
                if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                {
                    activatable.Activated += (_, e) =>
                    {
                        switch (e)
                        {
                            case ProtocolActivatedEventArgs p when e.Kind == ActivationKind.OpenUri:
                                Softphone.Platform.ProtocolActivation.Enqueue(p.Uri.ToString());
                                break;
                            case { Kind: ActivationKind.Reopen }:
                                RequestActivate();
                                break;
                        }
                    };
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static Softphone.AppHost.DesktopAppController? _controller;
        private static Softphone.Avalonia.MainWindow? _mainWindow;

        /// <summary>Bring the main window forward (second instance launched, dock icon click).</summary>
        public static void RequestActivate()
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var w = _mainWindow;
                    if (w == null) return;
                    if (!w.IsVisible) w.Show();
                    if (w.WindowState == global::Avalonia.Controls.WindowState.Minimized)
                        w.WindowState = global::Avalonia.Controls.WindowState.Normal;
                    w.Activate();
                }
                catch (Exception ex) { AppLog.Log($"[AvaloniaApp] activate: {ex.Message}"); }
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static bool _darkThemeLoaded, _lightThemeLoaded, _commonStylesLoaded;

        private void LoadThemeDictionaries(string darkUri, string lightUri)
        {
            var dark  = TryLoadDictionary(darkUri);
            var light = TryLoadDictionary(lightUri);
            _darkThemeLoaded  = dark  != null;
            _lightThemeLoaded = light != null;
            if (dark == null && light == null) return;

            var themed = new global::Avalonia.Controls.ResourceDictionary();
            if (dark != null)  themed.ThemeDictionaries[ThemeVariant.Dark]  = dark;
            if (light != null) themed.ThemeDictionaries[ThemeVariant.Light] = light;
            // Default must be a separate instance — Avalonia allows only one parent per ResourceDictionary.
            var darkDefault = TryLoadDictionary(darkUri);
            if (darkDefault != null) themed.ThemeDictionaries[ThemeVariant.Default] = darkDefault;
            Resources.MergedDictionaries.Add(themed);
        }

        private static global::Avalonia.Controls.ResourceDictionary? TryLoadDictionary(string uri)
        {
            try
            {
                return (global::Avalonia.Controls.ResourceDictionary)AvaloniaXamlLoader.Load(new Uri(uri));
            }
            catch (Exception ex)
            {
                AppLog.Log($"[AvaloniaApp] Warning: could not load resource '{uri}': {ex.Message}");
                return null;
            }
        }

        private void LoadAvaloniaResource(string uri)
        {
            var dict = TryLoadDictionary(uri);
            _commonStylesLoaded = dict != null;
            if (dict != null)
            {
                Resources.MergedDictionaries.Add(dict);
                return;
            }

            // Without CommonStyles every Theme="{StaticResource ...}" button renders empty — fail loud.
            var msg = $"[AvaloniaApp] FATAL: ControlThemes not loaded from '{uri}'. Buttons will render without content.";
            AppLog.Log(msg);
            System.Diagnostics.Debug.WriteLine(msg);
        }

        /// <summary>Selector-based styles (forms, nav, icon fallback) must live in Application.Styles.</summary>
        private void LoadAvaloniaStyles(string uri)
        {
            try
            {
                Styles.Add((Styles)AvaloniaXamlLoader.Load(new Uri(uri)));
            }
            catch (Exception ex)
            {
                var msg = $"[AvaloniaApp] FATAL: styles not loaded from '{uri}': {ex.Message}";
                AppLog.Log(msg);
                System.Diagnostics.Debug.WriteLine(msg);
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
