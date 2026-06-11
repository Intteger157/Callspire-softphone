using System;
using System.Diagnostics;
using System.IO;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Platform.Storage;

namespace Softphone.Avalonia
{
    public partial class SettingsWindow : Window
    {
        // ── Active panel tracking ─────────────────────────────────────────────
        private string _currentPanel = "Connection";

        // ── Constructor ───────────────────────────────────────────────────────
        public SettingsWindow()
        {
            InitializeComponent();
            AvaloniaWindowChromeHelper.Apply(this, preferAccent: false);
            ShowPanel("ConnectionPanel");
            LoadSettings();
        }

        // ── Panel navigation ──────────────────────────────────────────────────
        private void ShowPanel(string panelName)
        {
            _currentPanel = panelName;
            foreach (var name in new[]
            {
                "ConnectionPanel", "AudioPanel", "NotificationsPanel",
                "AppearancePanel", "AdvancedPanel", "AboutPanel"
            })
            {
                var ctrl = this.FindControl<Control>(name);
                if (ctrl != null) ctrl.IsVisible = ctrl.Name == panelName;
            }
        }

        // ── Sidebar click handlers ────────────────────────────────────────────
        private void ConnectionButton_Click(object? sender, RoutedEventArgs e)
            => ShowPanel("ConnectionPanel");

        private void AudioButton_Click(object? sender, RoutedEventArgs e)
        {
            ShowPanel("AudioPanel");
            PopulateAudioDevices();
        }

        private void NotificationsButton_Click(object? sender, RoutedEventArgs e)
            => ShowPanel("NotificationsPanel");

        private void AppearanceButton_Click(object? sender, RoutedEventArgs e)
        {
            ShowPanel("AppearancePanel");
            PopulateThemes();
        }

        private void AdvancedButton_Click(object? sender, RoutedEventArgs e)
            => ShowPanel("AdvancedPanel");

        private void AboutButton_Click(object? sender, RoutedEventArgs e)
        {
            ShowPanel("AboutPanel");
            PopulateAbout();
        }

        // ── Settings load / save ──────────────────────────────────────────────
        private void LoadSettings()
        {
            var cfg = AppDataHelper.LoadSettingsOrNew();

            // Primary connection
            SetText("SipServerTextBox",   cfg.SipServer   ?? string.Empty);
            SetText("SipUsernameTextBox", cfg.SipUsername  ?? string.Empty);

            // Secondary connection
            SetText("SipServer2TextBox",   cfg.SipServer2  ?? string.Empty);
            SetText("SipUsername2TextBox", cfg.SipUsername2 ?? string.Empty);

            // Audio
            SetCheck("EchoCancellationCheckBox", cfg.EnableEchoCancellation);

            // Advanced
            SetCheck("CallRecordingCheckBox", cfg.EnableCallRecording);
        }

        private void SaveSettings()
        {
            var cfg = AppDataHelper.LoadSettingsOrNew();

            cfg.SipServer   = GetText("SipServerTextBox").NullIfEmpty();
            cfg.SipUsername = GetText("SipUsernameTextBox").NullIfEmpty();
            cfg.SipServer2  = GetText("SipServer2TextBox").NullIfEmpty();
            cfg.SipUsername2 = GetText("SipUsername2TextBox").NullIfEmpty();
            cfg.EnableEchoCancellation = GetCheck("EchoCancellationCheckBox");
            cfg.EnableCallRecording    = GetCheck("CallRecordingCheckBox");

            AppDataHelper.SaveSettings(cfg);
            AppLog.Log("[SettingsWindow Avalonia] Settings saved");
        }

        // ── Connection panel ──────────────────────────────────────────────────
        private void MainConnectionTestButton_Click(object? sender, RoutedEventArgs e)
        {
            AppLog.Log("[SettingsWindow] Test connection requested (TODO Phase 6)");
        }

        private void SaveConnectionButton_Click(object? sender, RoutedEventArgs e)
            => SaveSettings();

        // ── Audio panel ───────────────────────────────────────────────────────
        private void PopulateAudioDevices()
        {
            // TODO: populate via IAudioDeviceFactory (Phase 6)
            var micCombo = this.FindControl<ComboBox>("MicrophoneComboBox");
            var spkCombo = this.FindControl<ComboBox>("SpeakerComboBox");
            if (micCombo != null) micCombo.ItemsSource = new[] { "Default", "System Microphone" };
            if (spkCombo != null) spkCombo.ItemsSource = new[] { "Default", "System Speaker" };
        }

        private void AudioDevice_SelectionChanged(object? sender, SelectionChangedEventArgs e)
            => AppLog.Log("[SettingsWindow] Audio device selection changed");

        private void AecEnabled_CheckedChanged(object? sender, RoutedEventArgs e)
            => AppLog.Log("[SettingsWindow] AEC enabled changed");

        // ── Appearance panel ──────────────────────────────────────────────────
        private void PopulateThemes()
        {
            var cfg   = AppDataHelper.LoadSettingsOrNew();
            var combo = this.FindControl<ComboBox>("ThemeComboBox");
            if (combo == null) return;
            combo.ItemsSource   = new[] { "Dark", "Light", "System" };
            combo.SelectedIndex = ThemeService.ParseMode(cfg.ThemeMode) switch
            {
                ThemeMode.Light  => 1,
                ThemeMode.System => 2,
                _                => 0,
            };
        }

        private void ThemeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox cb && cb.SelectedItem is string label)
            {
                var mode = label switch { "Light" => "light", "System" => "system", _ => "dark" };
                ThemeService.SetConfiguredMode(ThemeService.ParseMode(mode));
            }
        }

        // ── Advanced panel ────────────────────────────────────────────────────
        private async void BrowseRecordingFolder_Click(object? sender, RoutedEventArgs e)
        {
            var result = await this.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Select Recordings Folder", AllowMultiple = false });
            if (result.Count > 0)
            {
                var tb = this.FindControl<TextBox>("RecordingFolderTextBox");
                if (tb != null) tb.Text = result[0].Path.LocalPath;
            }
        }

        private void OpenLogsFolderButton_Click(object? sender, RoutedEventArgs e)
        {
            var logsPath = AppDataHelper.GetLogsDirectory();
            if (Directory.Exists(logsPath))
                OpenFolderCrossPlatform(logsPath);
        }

        // ── About panel ───────────────────────────────────────────────────────
        private void PopulateAbout()
        {
            var version = UpdateService.GetCurrentVersion();
            var tb = this.FindControl<TextBlock>("VersionTextBlock");
            if (tb != null) tb.Text = $"Version: {version}";
        }

        private void CheckUpdateButton_Click(object? sender, RoutedEventArgs e)
            => AppLog.Log("[SettingsWindow] Check for updates requested (TODO Phase 6)");

        // ── Title bar & close ─────────────────────────────────────────────────
        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void CloseButton_Click(object? sender, RoutedEventArgs e)
            => Close();

        // ── Helpers ───────────────────────────────────────────────────────────
        private void SetText(string controlName, string value)
        {
            if (this.FindControl<TextBox>(controlName) is TextBox tb) tb.Text = value;
        }

        private string GetText(string controlName)
            => this.FindControl<TextBox>(controlName)?.Text ?? string.Empty;

        private void SetCheck(string controlName, bool value)
        {
            if (this.FindControl<CheckBox>(controlName) is CheckBox cb) cb.IsChecked = value;
        }

        private bool GetCheck(string controlName)
            => this.FindControl<CheckBox>(controlName)?.IsChecked == true;

        private static void OpenFolderCrossPlatform(string path)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start("explorer.exe", path);
                else if (OperatingSystem.IsMacOS())
                    Process.Start("open", path);
                else
                    Process.Start("xdg-open", path);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SettingsWindow] Could not open folder '{path}': {ex.Message}");
            }
        }
    }

    internal static class StringExtensions
    {
        public static string? NullIfEmpty(this string? s)
            => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
