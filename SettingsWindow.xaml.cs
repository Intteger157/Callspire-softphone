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
            LoadAudioDevices(); // Загружаем устройства при открытии окна
            
            // Подписываемся на события статуса из MainWindow после загрузки окна
            Loaded += SettingsWindow_Loaded;
        }

        private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateConnectionStatus();
            
            // Подписываемся на события статуса из MainWindow после установки Owner
            if (Owner is MainWindow mainWindow)
            {
                mainWindow.OnConnectionStatusChanged += UpdateConnectionStatusFromMainWindow;
            }
        }

        private void UpdateConnectionStatus()
        {
            if (Owner is MainWindow mainWindow)
            {
                if (mainWindow.IsConnected)
                {
                    ConnectionStatusTextBlock.Text = "Status: Connected";
                }
                else
                {
                    ConnectionStatusTextBlock.Text = "Status: Not connected";
                }
            }
            else
            {
                ConnectionStatusTextBlock.Text = "Status: Not connected";
            }
        }

        private void UpdateConnectionStatusFromMainWindow(string status)
        {
            Dispatcher.Invoke(() =>
            {
                ConnectionStatusTextBlock.Text = $"Status: {status}";
            });
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
            LoadAudioDevices();
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
                        // Парсим server:port если есть
                        string server = settings.SipServer ?? "";
                        string port = "5060";
                        if (server.Contains(":"))
                        {
                            var parts = server.Split(':');
                            server = parts[0];
                            if (parts.Length > 1) port = parts[1];
                        }
                        
                        SipServerTextBox.Text = server;
                        if (SipPortTextBox != null) SipPortTextBox.Text = port;
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

        private async void SaveConnectionSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string server = SipServerTextBox.Text.Trim();
                string username = SipUsernameTextBox.Text.Trim();
                string password = SipPasswordBox.Password;
                string port = SipPortTextBox?.Text?.Trim() ?? "5060";

                if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    MessageBox.Show("Please fill in all SIP connection fields.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(port, out int portNum) || portNum < 1 || portNum > 65535)
                {
                    MessageBox.Show("Invalid port number. Using default port 5060.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    portNum = 5060;
                }

                string serverWithPort = $"{server}:{portNum}";
                
                // Загружаем существующие настройки, чтобы сохранить аудиоустройства
                AppSettings settings;
                if (File.Exists(SettingsFileName))
                {
                    string existingJson = File.ReadAllText(SettingsFileName);
                    settings = JsonConvert.DeserializeObject<AppSettings>(existingJson) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                // Обновляем настройки подключения
                settings.SipServer = serverWithPort;
                settings.SipUsername = username;
                settings.SipPassword = password;

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsFileName, json);

                // Отключаем кнопку во время подключения
                SaveAndConnectButton.IsEnabled = false;
                ConnectionStatusTextBlock.Text = "Status: Saving settings...";

                // Уведомляем главное окно о необходимости переподключения
                if (Owner is MainWindow mainWindow)
                {
                    ConnectionStatusTextBlock.Text = "Status: Connecting...";
                    
                    // Подписка уже есть в конструкторе, просто вызываем переподключение
                    await mainWindow.ReconnectFromSettingsAsync();
                }

                SaveAndConnectButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                ConnectionStatusTextBlock.Text = $"Status: Error - {ex.Message}";
                SaveAndConnectButton.IsEnabled = true;
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

        private void LoadAudioDevices()
        {
            try
            {
                // Загружаем микрофоны
                var microphones = AudioDeviceHelper.GetMicrophones();
                MicrophoneComboBox.ItemsSource = microphones;
                
                // Загружаем динамики
                var speakers = AudioDeviceHelper.GetSpeakers();
                SpeakerComboBox.ItemsSource = speakers;

                // Выбираем сохраненные устройства
                LoadAudioSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading audio devices: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadAudioSettings()
        {
            try
            {
                if (File.Exists(SettingsFileName))
                {
                    string json = File.ReadAllText(SettingsFileName);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null)
                    {
                        // Выбираем микрофон
                        if (MicrophoneComboBox.ItemsSource != null && 
                            !string.IsNullOrEmpty(settings.MicrophoneDeviceGuid))
                        {
                            foreach (AudioDeviceInfo device in MicrophoneComboBox.ItemsSource)
                            {
                                if (device.Guid == settings.MicrophoneDeviceGuid)
                                {
                                    MicrophoneComboBox.SelectedItem = device;
                                    break;
                                }
                            }
                        }
                        else if (MicrophoneComboBox.ItemsSource != null && MicrophoneComboBox.Items.Count > 0)
                        {
                            // Выбираем устройство по умолчанию, если ничего не выбрано
                            MicrophoneComboBox.SelectedIndex = 0;
                        }

                        // Выбираем динамик
                        if (SpeakerComboBox.ItemsSource != null && 
                            !string.IsNullOrEmpty(settings.SpeakerDeviceGuid))
                        {
                            foreach (AudioDeviceInfo device in SpeakerComboBox.ItemsSource)
                            {
                                if (device.Guid == settings.SpeakerDeviceGuid)
                                {
                                    SpeakerComboBox.SelectedItem = device;
                                    break;
                                }
                            }
                        }
                        else if (SpeakerComboBox.ItemsSource != null && SpeakerComboBox.Items.Count > 0)
                        {
                            // Выбираем устройство по умолчанию, если ничего не выбрано
                            SpeakerComboBox.SelectedIndex = 0;
                        }
                    }
                }
                else
                {
                    // Если файла настроек нет, выбираем устройства по умолчанию
                    if (MicrophoneComboBox.ItemsSource != null && MicrophoneComboBox.Items.Count > 0)
                    {
                        MicrophoneComboBox.SelectedIndex = 0;
                    }
                    if (SpeakerComboBox.ItemsSource != null && SpeakerComboBox.Items.Count > 0)
                    {
                        SpeakerComboBox.SelectedIndex = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading audio settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveAudioSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Загружаем существующие настройки
                AppSettings settings;
                if (File.Exists(SettingsFileName))
                {
                    string json = File.ReadAllText(SettingsFileName);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                // Сохраняем выбранные аудиоустройства
                if (MicrophoneComboBox.SelectedItem is AudioDeviceInfo mic)
                {
                    settings.MicrophoneDeviceGuid = mic.Guid;
                    settings.MicrophoneDeviceNumber = mic.DeviceNumber;
                }

                if (SpeakerComboBox.SelectedItem is AudioDeviceInfo speaker)
                {
                    settings.SpeakerDeviceGuid = speaker.Guid;
                    settings.SpeakerDeviceNumber = speaker.DeviceNumber;
                }

                // Сохраняем в файл
                string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsFileName, settingsJson);

                MessageBox.Show("Audio settings saved!", "Success", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving audio settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MicrophoneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Можно добавить тестирование микрофона здесь
        }

        private void SpeakerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Можно добавить тестирование динамика здесь
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            // Отписываемся от событий при закрытии окна
            if (Owner is MainWindow mainWindow)
            {
                mainWindow.OnConnectionStatusChanged -= UpdateConnectionStatusFromMainWindow;
            }
            base.OnClosed(e);
        }
    }
}

