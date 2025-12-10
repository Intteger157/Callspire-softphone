using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Newtonsoft.Json;

namespace Softphone
{
    public partial class SettingsWindow : Window
    {
        private const string SettingsFileName = "settings.json";

        public SettingsWindow()
        {
            InitializeComponent();
            ShowView(ConnectionSettingsView);
            LoadSettings();
        }

        private void ShowView(Grid view)
        {
            ConnectionSettingsView.Visibility = Visibility.Collapsed;
            AudioSettingsView.Visibility = Visibility.Collapsed;
            GeneralSettingsView.Visibility = Visibility.Collapsed;

            view.Visibility = Visibility.Visible;
        }

        private void ConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(ConnectionSettingsView);
        }

        private void AudioButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(AudioSettingsView);
        }

        private void GeneralButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(GeneralSettingsView);
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFileName))
                {
                    string json = File.ReadAllText(SettingsFileName);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null)
                    {
                        SipServerTextBox.Text = settings.SipServer ?? "";
                        SipUsernameTextBox.Text = settings.SipUsername ?? "";
                        SipPasswordBox.Password = settings.SipPassword ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveConnectionSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settings = new AppSettings
                {
                    SipServer = SipServerTextBox.Text.Trim(),
                    SipUsername = SipUsernameTextBox.Text.Trim(),
                    SipPassword = SipPasswordBox.Password
                };

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsFileName, json);

                MessageBox.Show("Settings saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

