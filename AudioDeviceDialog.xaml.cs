using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using System.IO;

namespace Softphone
{
    public partial class AudioDeviceDialog : Window
    {
        public int? SelectedMicrophoneDeviceNumber { get; private set; }
        public int? SelectedSpeakerDeviceNumber { get; private set; }

        public AudioDeviceDialog()
        {
            InitializeComponent();
            // Загружаем устройства асинхронно, чтобы не блокировать UI
            _ = LoadAudioDevicesAsync();
        }

        private async System.Threading.Tasks.Task LoadAudioDevicesAsync()
        {
            try
            {
                // Показываем индикатор загрузки
                Dispatcher.Invoke(() =>
                {
                    MicrophoneComboBox.IsEnabled = false;
                    SpeakerComboBox.IsEnabled = false;
                    ApplyButton.IsEnabled = false;
                    // Показываем placeholder во время загрузки
                    var loadingPlaceholder = new List<AudioDeviceInfo> { new AudioDeviceInfo { Name = "Loading...", DeviceNumber = -1 } };
                    MicrophoneComboBox.ItemsSource = loadingPlaceholder;
                    SpeakerComboBox.ItemsSource = loadingPlaceholder;
                });
                
                // Загружаем устройства в фоновом потоке параллельно
                var microphonesTask = System.Threading.Tasks.Task.Run(() => AudioDeviceHelper.GetMicrophones());
                var speakersTask = System.Threading.Tasks.Task.Run(() => AudioDeviceHelper.GetSpeakers());
                
                // Ждем завершения обеих задач
                await System.Threading.Tasks.Task.WhenAll(microphonesTask, speakersTask);
                
                var microphones = await microphonesTask;
                var speakers = await speakersTask;
                
                // Обновляем UI в UI потоке
                Dispatcher.Invoke(() =>
                {
                    MicrophoneComboBox.ItemsSource = microphones;
                    SpeakerComboBox.ItemsSource = speakers;
                    MicrophoneComboBox.IsEnabled = true;
                    SpeakerComboBox.IsEnabled = true;
                    ApplyButton.IsEnabled = true;
                    
                    // Выбираем устройство по умолчанию, если ничего не выбрано
                    if (MicrophoneComboBox.Items.Count > 0 && MicrophoneComboBox.SelectedItem == null)
                    {
                        MicrophoneComboBox.SelectedIndex = 0;
                    }
                    
                    if (SpeakerComboBox.Items.Count > 0 && SpeakerComboBox.SelectedItem == null)
                    {
                        SpeakerComboBox.SelectedIndex = 0;
                    }
                    
                    // Загружаем сохраненные настройки после загрузки устройств
                    LoadAudioSettings();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    MicrophoneComboBox.IsEnabled = true;
                    SpeakerComboBox.IsEnabled = true;
                    ApplyButton.IsEnabled = true;
                    CustomMessageBox.Show($"Error loading audio devices: {ex.Message}", "Error", 
                        MessageBoxButton.OK, MessageBoxImage.Warning, this);
                });
            }
        }

        private void LoadAudioSettings()
        {
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null)
                    {
                        // Выбираем микрофон по сохраненному GUID
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
                        
                        // Выбираем динамик по сохраненному GUID
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
                    }
                }
            }
            catch (Exception ex)
            {
                // Игнорируем ошибки при загрузке настроек
                System.Diagnostics.Debug.WriteLine($"Error loading audio settings: {ex.Message}");
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            // Сохраняем выбранные устройства
            if (MicrophoneComboBox.SelectedItem is AudioDeviceInfo micDevice)
            {
                SelectedMicrophoneDeviceNumber = micDevice.DeviceNumber;
            }
            
            if (SpeakerComboBox.SelectedItem is AudioDeviceInfo speakerDevice)
            {
                SelectedSpeakerDeviceNumber = speakerDevice.DeviceNumber;
            }

            // Сохраняем настройки в файл
            try
            {
                AppSettings settings;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    settings = new AppSettings();
                }

                if (MicrophoneComboBox.SelectedItem is AudioDeviceInfo selectedMic)
                {
                    settings.MicrophoneDeviceGuid = selectedMic.Guid;
                }
                
                if (SpeakerComboBox.SelectedItem is AudioDeviceInfo selectedSpeaker)
                {
                    settings.SpeakerDeviceGuid = selectedSpeaker.Guid;
                }

                string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), settingsJson);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error saving settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning, this);
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
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
            DialogResult = false;
            Close();
        }
    }
}

