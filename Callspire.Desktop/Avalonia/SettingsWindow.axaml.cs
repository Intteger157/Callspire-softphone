using System;
using System.Threading.Tasks;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using Softphone.AppHost;
using Softphone.AppHost.ViewModels;
using Softphone.Audio;

namespace Softphone.Avalonia
{
    /// <summary>
    /// Avalonia settings window: a thin view over <see cref="SettingsViewModel"/>.
    /// All state lives in the view model; this class only handles navigation and button clicks.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private static readonly string[] PanelNames =
        {
            "ConnectionPanel", "SecondaryPanel", "AudioPanel", "KommoPanel", "AppearancePanel", "AdvancedPanel", "AboutPanel",
        };

        private readonly SettingsViewModel _vm;

        public SettingsViewModel ViewModel => _vm;

        /// <summary>XAML-compiler / previewer constructor only.</summary>
        [Obsolete("Design-time only", error: true)]
        public SettingsWindow()
        {
            _vm = null!;
            InitializeComponent();
        }

        public SettingsWindow(DesktopAppController controller)
        {
            _vm = new SettingsViewModel(controller);
            DataContext = _vm;
            InitializeComponent();
            PlatformWindowChrome.Apply(this, preferAccent: false);
            ShowPanel("Connection");
        }

        // ── Navigation ────────────────────────────────────────────────────────

        private void Nav_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tag }) ShowPanel(tag);
        }

        private void ShowPanel(string key)
        {
            string panelName = key + "Panel";
            foreach (var name in PanelNames)
            {
                if (this.FindControl<Control>(name) is { } ctrl) ctrl.IsVisible = name == panelName;
            }

            foreach (var navName in new[] { "NavConnection", "NavSecondary", "NavAudio", "NavKommo", "NavAppearance", "NavAdvanced", "NavAbout" })
            {
                if (this.FindControl<Button>(navName) is { } btn)
                {
                    bool active = string.Equals(btn.Tag as string, key, StringComparison.Ordinal);
                    if (active) btn.Classes.Add("active"); else btn.Classes.Remove("active");
                }
            }

            if (key == "Audio") _vm.RefreshAudioDevices();
        }

        // ── Actions ───────────────────────────────────────────────────────────

        private async void Save_Click(object? sender, RoutedEventArgs e)
        {
            var error = await _vm.SaveAsync();
            if (error != null)
                await Dialogs.ShowMessageAsync(this, "Settings", error);
        }

        private void TestMain_Click(object? sender, RoutedEventArgs e) => _ = _vm.TestConnectionAsync(secondary: false);
        private void TestSecondary_Click(object? sender, RoutedEventArgs e) => _ = _vm.TestConnectionAsync(secondary: true);

        private void RefreshDevices_Click(object? sender, RoutedEventArgs e) => _vm.RefreshAudioDevices();

        private void PreviewRingtone_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
#if WINDOWS
                RingtoneService.Instance.Configure(null, (float)_vm.RingtoneVolume, _vm.RingtoneWav ? "wav" : "tone");
                RingtoneService.Instance.Start();
#else
                PortAudioTonePlayer.Shared.Configure(_vm.SelectedSpeaker?.Index ?? -1, (float)_vm.RingtoneVolume);
                PortAudioTonePlayer.Shared.PlayRingtone();
#endif
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SettingsWindow] ringtone preview failed: {ex.Message}");
            }
        }

        private void StopRingtone_Click(object? sender, RoutedEventArgs e) => PlatformRingtone.Stop();

        private void OpenRecordings_Click(object? sender, RoutedEventArgs e) => _vm.OpenRecordingsFolder();
        private void OpenLogs_Click(object? sender, RoutedEventArgs e) => _vm.OpenLogsFolder();

        private void CheckUpdates_Click(object? sender, RoutedEventArgs e) => _ = _vm.CheckForUpdatesAsync();
        private void DownloadUpdate_Click(object? sender, RoutedEventArgs e) => _vm.OpenUpdateUrl();

        // ── Title bar & close ─────────────────────────────────────────────────

        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            PlatformRingtone.Stop();
            base.OnClosed(e);
        }
    }
}
