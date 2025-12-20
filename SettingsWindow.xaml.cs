using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using Newtonsoft.Json;

namespace Softphone
{
    public partial class SettingsWindow : Window
    {
        private WebRtcStatusService? _webRtcStatusService;
        private static WebRtcStatusService? _sharedWebRtcStatusService; // Общий экземпляр для всех окон
        
        /// <summary>
        /// Получает или создает общий экземпляр WebRTC сервиса
        /// </summary>
        private WebRtcStatusService GetOrCreateSharedWebRtcService()
        {
            // Сначала пытаемся получить из MainWindow
            if (_sharedWebRtcStatusService == null)
            {
                _sharedWebRtcStatusService = MainWindow.GetSharedWebRtcStatusService();
            }
            
            // Если все еще null, создаем новый
            if (_sharedWebRtcStatusService == null)
            {
                _sharedWebRtcStatusService = new WebRtcStatusService();
                _sharedWebRtcStatusService.AttachWebView(WebRtcStatusWebView);
                _sharedWebRtcStatusService.SetParentWindow(this);
                MainWindow.SetSharedWebRtcStatusService(_sharedWebRtcStatusService);
            }
            else
            {
                // Если сервис уже существует, прикрепляем WebView2 из SettingsWindow
                _sharedWebRtcStatusService.AttachWebView(WebRtcStatusWebView);
                _sharedWebRtcStatusService.SetParentWindow(this);
            }
            
            return _sharedWebRtcStatusService;
        }

        public SettingsWindow()
        {
            InitializeComponent();
            
            // Убеждаемся, что поля доступны для редактирования
            if (SipServerTextBox != null)
            {
                SipServerTextBox.IsReadOnly = false;
                SipServerTextBox.IsEnabled = true;
                SipServerTextBox.Focusable = true;
            }
            if (SipPortTextBox != null)
            {
                SipPortTextBox.IsReadOnly = false;
                SipPortTextBox.IsEnabled = true;
                SipPortTextBox.Focusable = true;
            }
            if (SipUsernameTextBox != null)
            {
                SipUsernameTextBox.IsReadOnly = false;
                SipUsernameTextBox.IsEnabled = true;
                SipUsernameTextBox.Focusable = true;
            }
            if (SipPasswordBox != null)
            {
                SipPasswordBox.IsEnabled = true;
                SipPasswordBox.Focusable = true;
            }
            
            ShowView(ConnectionSettingsView);
            
            // Откладываем загрузку аудиоустройств до перехода на вкладку Audio
            // LoadAudioDevices() вызывается только при клике на AudioButton
            
            // Откладываем инициализацию WebRTC до перехода на вкладку Advanced
            // WebRTC инициализируется только при клике на AdvancedButton
            
            // Подписываемся на события статуса из MainWindow после загрузки окна
            Loaded += SettingsWindow_Loaded;
            
            // Выделяем кнопку Connection при загрузке окна
            Loaded += (s, e) =>
            {
                UpdateButtonSelection(ConnectionButton);
            };
            
            // Обновляем статус WebRTC при изменении чекбокса (только если WebRTC уже инициализирован)
            UseWebRtcCheckBox.Checked += (s, e) => 
            {
                if (_sharedWebRtcStatusService != null)
                {
                    UpdateWebRtcStatus();
                }
                TestWebRtcConnectionButton.IsEnabled = true;
            };
            UseWebRtcCheckBox.Unchecked += (s, e) => 
            {
                if (_sharedWebRtcStatusService != null)
                {
                    UpdateWebRtcStatus();
                }
                TestWebRtcConnectionButton.IsEnabled = false;
            };
            WebRtcWsUriTextBox.TextChanged += (s, e) => 
            {
                // Если поле пустое или не начинается с "wss://" или "ws://", предзаполняем "wss://"
                if (WebRtcWsUriTextBox != null)
                {
                    string currentText = WebRtcWsUriTextBox.Text ?? "";
                    if (string.IsNullOrWhiteSpace(currentText))
                    {
                        WebRtcWsUriTextBox.Text = "wss://";
                        WebRtcWsUriTextBox.CaretIndex = WebRtcWsUriTextBox.Text.Length; // Устанавливаем курсор в конец
                    }
                    else if (!currentText.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) && 
                             !currentText.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
                    {
                        // Если текст не начинается с протокола, добавляем "wss://"
                        int caretPos = WebRtcWsUriTextBox.CaretIndex;
                        WebRtcWsUriTextBox.Text = "wss://" + currentText;
                        WebRtcWsUriTextBox.CaretIndex = Math.Min(caretPos + 6, WebRtcWsUriTextBox.Text.Length); // Сохраняем позицию курсора
                    }
                }
                
                if (_sharedWebRtcStatusService != null)
                {
                    UpdateWebRtcStatus();
                }
            };
            
            // Обработка потери фокуса - если поле пустое, предзаполняем "wss://"
            WebRtcWsUriTextBox.LostFocus += (s, e) =>
            {
                if (WebRtcWsUriTextBox != null)
                {
                    string currentText = WebRtcWsUriTextBox.Text?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(currentText))
                    {
                        WebRtcWsUriTextBox.Text = "wss://";
                    }
                }
            };
        }

        private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateConnectionStatus();
            
            // Подписываемся на события статуса из MainWindow после установки Owner
            if (Owner is MainWindow mainWindow)
            {
                mainWindow.OnConnectionStatusChanged += UpdateConnectionStatusFromMainWindow;
                
                // Подписываемся на события WebRTC для динамического обновления статуса
                if (WebRtcService.Instance != null)
                {
                    WebRtcService.Instance.Event += OnWebRtcEvent;
                }
            }
            
            // Загружаем настройки после полной загрузки окна
            // Это гарантирует, что все UI элементы инициализированы
            LoadSettings();
        }
        
        private void OnWebRtcEvent(WebRtcEventDto dto)
        {
            // Обновляем статусы при событиях WebRTC (registered, ws_connected, etc.)
            // Синхронизируем все три места: MainWindow, Connection tab, Advanced tab
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Обновляем статус в Connection tab (синхронизируется с MainWindow)
                UpdateConnectionStatus();
                
                // Обновляем статус в Advanced tab (WebRTC Status) при важных событиях
                if (dto.Type == "registered" || dto.Type == "ua_registered" || 
                    dto.Type == "reg_failed" || dto.Type == "ua_registration_failed" ||
                    dto.Type == "ua_connected" || dto.Type == "ws_connected" ||
                    dto.Type == "unregistered" || dto.Type == "ua_unregistered")
                {
                    // Обновляем WebRTC статус в Advanced tab через WebRtcStatusService
                    if (_sharedWebRtcStatusService != null)
                    {
                        var currentStatus = _sharedWebRtcStatusService.CurrentStatus;
                        UpdateWebRtcStatusDisplay(currentStatus);
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private void UpdateConnectionStatus()
        {
            if (Owner is MainWindow mainWindow)
            {
                // ВАЖНО: Используем ту же логику, что и в MainWindow.UpdateConnectionStatus()
                // Это гарантирует синхронизацию статусов между главным окном и настройками
                bool useWebRtc = false;
                try
                {
                    string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsFilePath))
                    {
                        string json = File.ReadAllText(settingsFilePath);
                        var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                        useWebRtc = settings?.UseWebRtcAudio ?? false;
                    }
                }
                catch { }
                
                bool isConnected = mainWindow.IsConnected;
                
                // Получаем текущий статус из MainWindow для точной синхронизации
                string mainWindowStatus = mainWindow.ConnectionStatus;
                
                // Если MainWindow имеет конкретный статус, используем его для синхронизации
                if (!string.IsNullOrEmpty(mainWindowStatus))
                {
                    // Синхронизируем статус с главным окном
                    if (useWebRtc)
                    {
                        if (mainWindowStatus == "Connected with WebRTC")
                        {
                            ConnectionStatusTextBlock.Text = "Status: Connected with WebRTC";
                            ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                        }
                        else if (mainWindowStatus.Contains("Connecting") || mainWindowStatus.Contains("Initializing"))
                        {
                            // Показываем промежуточный статус
                            ConnectionStatusTextBlock.Text = $"Status: {mainWindowStatus}";
                            ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                        }
                        else
                        {
                            ConnectionStatusTextBlock.Text = "Status: Not connected";
                            ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
                        }
                    }
                    else
                    {
                        if (mainWindowStatus == "Connected with SIP")
                        {
                            ConnectionStatusTextBlock.Text = "Status: Connected with SIP";
                            ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                        }
                        else if (mainWindowStatus.Contains("Connecting") || mainWindowStatus.Contains("Initializing"))
                        {
                            // Показываем промежуточный статус
                            ConnectionStatusTextBlock.Text = $"Status: {mainWindowStatus}";
                            ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                        }
                        else
                        {
                            ConnectionStatusTextBlock.Text = "Status: Not connected";
                            ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
                        }
                    }
                }
                else
                {
                    // Fallback: используем старую логику, если статус из MainWindow недоступен
                    if (useWebRtc)
                    {
                        // WebRTC режим
                        if (isConnected)
                        {
                            ConnectionStatusTextBlock.Text = "Status: Connected with WebRTC";
                            ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                        }
                        else
                        {
                            ConnectionStatusTextBlock.Text = "Status: Not connected";
                            ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
                        }
                    }
                    else
                    {
                        // SIP режим
                        if (isConnected)
                        {
                            ConnectionStatusTextBlock.Text = "Status: Connected with SIP";
                            ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                        }
                        else
                        {
                            ConnectionStatusTextBlock.Text = "Status: Not connected";
                            ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
                        }
                    }
                }
            }
            else
            {
                ConnectionStatusTextBlock.Text = "Status: Not connected";
                ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
            }
        }

        private void UpdateConnectionStatusFromMainWindow(string status)
        {
            Dispatcher.Invoke(() =>
            {
                // Всегда обновляем статус подключения для синхронизации с MainWindow
                // Это гарантирует, что статус в настройках всегда соответствует статусу в главном окне
                UpdateConnectionStatus();
            });
        }
        
        private string? FormatStatusMessage(string technicalStatus)
        {
            if (string.IsNullOrEmpty(technicalStatus))
                return "Not connected";

            // Преобразуем технические сообщения в понятные для пользователя
            string status = technicalStatus.ToLower();

            // Статусы подключения к серверу
            if (status.Contains("registration successful"))
                return "Connected to server";
            
            if (status.Contains("registration failed") || status.Contains("registration temporary failure"))
            {
                // Извлекаем причину ошибки, если есть
                if (status.Contains("could not resolve"))
                    return "Connection failed: Cannot reach server";
                if (status.Contains("timeout"))
                    return "Connection failed: Timeout";
                if (status.Contains("unauthorized") || status.Contains("401"))
                    return "Connection failed: Invalid credentials";
                return "Connection failed";
            }
            
            if (status.Contains("registration removed"))
                return "Disconnected from server";
            
            if (status.Contains("registering on sip server") || status.Contains("initializing sip"))
                return "Connecting to server...";
            
            if (status.Contains("sip transport listening"))
                return "Starting connection...";
            
            // Фильтруем все технические SIP сообщения
            if (status.Contains("responded to options") ||
                status.Contains("sip response:") || 
                status.Contains("sip request:") ||
                status.Contains("invite") ||
                status.Contains("200 ok") ||
                status.Contains("call"))
            {
                // Не обновляем статус для технических сообщений
                return null;
            }
            
            return null;
        }

        private void ShowView(Grid view)
        {
            ConnectionSettingsView.Visibility = Visibility.Collapsed;
            AudioSettingsView.Visibility = Visibility.Collapsed;
            GeneralSettingsView.Visibility = Visibility.Collapsed;
            AdvancedSettingsView.Visibility = Visibility.Collapsed;

            view.Visibility = Visibility.Visible;
            
            // Загружаем настройки при переключении на Advanced
            if (view == AdvancedSettingsView)
            {
                LoadGeneralSettings();
                
                // Сначала пытаемся получить сервис из MainWindow, если он еще не инициализирован локально
                if (_sharedWebRtcStatusService == null)
                {
                    _sharedWebRtcStatusService = MainWindow.GetSharedWebRtcStatusService();
                }
                
                // Если WebRTC сервис уже инициализирован, восстанавливаем текущий статус сразу
                if (_sharedWebRtcStatusService != null)
                {
                    // Восстанавливаем текущий статус из сервиса синхронно (без задержки)
                    var currentStatus = _sharedWebRtcStatusService.CurrentStatus;
                    
                    // Обновляем статус сразу, до подписки на события
                    UpdateWebRtcStatusDisplay(currentStatus);
                    
                    // Подписываемся на изменения статуса, если еще не подписаны
                    if (_webRtcStatusService == null)
                    {
                        _webRtcStatusService = _sharedWebRtcStatusService;
                        _webRtcStatusService.OnStatusChanged += UpdateWebRtcStatusDisplay;
                    }
                    
                    // НЕ запускаем автоматическую проверку при открытии вкладки
                    // Проверка запускается только по кнопке "Test Connection"
                    MainWindow.Log($"[SettingsWindow] ShowView: Restored WebRTC status: {currentStatus}");
                }
                else
                {
                    // Если сервис не инициализирован, проверяем настройки и показываем соответствующий статус
                    bool useWebRtc = UseWebRtcCheckBox?.IsChecked ?? false;
                    string wsUri = WebRtcWsUriTextBox?.Text?.Trim() ?? "";
                    
                    if (!useWebRtc)
                    {
                        WebRtcStatusTextBlock.Text = "WebRTC Status: Disabled";
                        WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                    }
                    else if (string.IsNullOrWhiteSpace(wsUri) || wsUri == "wss://")
                    {
                        WebRtcStatusTextBlock.Text = "WebRTC Status: Not configured (WebSocket URI missing or invalid)";
                        WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    }
                    else
                    {
                        // Если WebRTC включен и URI указан, но сервис еще не создан,
                        // возможно подключение еще инициализируется при старте приложения
                        // Показываем "Connecting..." вместо "Not configured"
                        WebRtcStatusTextBlock.Text = "WebRTC Status: Connecting...";
                        WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    }
                }
            }
        }
        
        private void UpdateButtonSelection(Button selectedButton)
        {
            // Сбрасываем выделение всех кнопок
            ConnectionButton.Background = System.Windows.Media.Brushes.Transparent;
            ConnectionButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            AudioButton.Background = System.Windows.Media.Brushes.Transparent;
            AudioButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            GeneralButton.Background = System.Windows.Media.Brushes.Transparent;
            GeneralButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            AdvancedButton.Background = System.Windows.Media.Brushes.Transparent;
            AdvancedButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            AboutButton.Background = System.Windows.Media.Brushes.Transparent;
            AboutButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            
            // Выделяем выбранную кнопку
            if (selectedButton != null)
            {
                selectedButton.Background = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                selectedButton.Foreground = System.Windows.Media.Brushes.White;
            }
        }

        private void ConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(ConnectionButton);
            ShowView(ConnectionSettingsView);
        }

        private void AudioButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(AudioButton);
            ShowView(AudioSettingsView);
            // Загружаем аудиоустройства асинхронно только при первом переходе на вкладку Audio
            if (MicrophoneComboBox.ItemsSource == null || MicrophoneComboBox.Items.Count == 0)
            {
                _ = LoadAudioDevicesAsync();
            }
        }

        private void GeneralButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(GeneralButton);
            ShowView(GeneralSettingsView);
            LoadGeneralSettingsForGeneralTab();
        }
        
        private void LoadGeneralSettingsForGeneralTab()
        {
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null && EnableCallRecordingCheckBox != null)
                    {
                        EnableCallRecordingCheckBox.IsChecked = settings.EnableCallRecording;
                        UpdateCallRecordingToggleColor();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadGeneralSettingsForGeneralTab: Error - {ex.Message}");
            }
        }
        
        private void EnableCallRecordingCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            UpdateCallRecordingToggleColor();
            SaveCallRecordingSetting();
        }
        
        private void EnableCallRecordingCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateCallRecordingToggleColor();
            SaveCallRecordingSetting();
        }
        
        private void UpdateCallRecordingToggleColor()
        {
            if (EnableCallRecordingCheckBox == null) return;
            
            if (EnableCallRecordingCheckBox.IsChecked == true)
            {
                // Зеленый цвет когда включено
                EnableCallRecordingCheckBox.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
            }
            else
            {
                // Серый цвет когда выключено
                EnableCallRecordingCheckBox.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            }
        }
        
        private void SaveCallRecordingSetting()
        {
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

                if (EnableCallRecordingCheckBox != null)
                {
                    settings.EnableCallRecording = EnableCallRecordingCheckBox.IsChecked ?? false;
                }

                string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), settingsJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveCallRecordingSetting: Error - {ex.Message}");
            }
        }
        
        private void OpenRecordingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string recordingsPath = AppDataHelper.GetRecordingsDirectory();
                
                // Открываем папку в проводнике
                System.Diagnostics.Process.Start("explorer.exe", recordingsPath);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error opening recordings folder: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning, this);
            }
        }

        private void AdvancedButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(AdvancedButton);
            
            // Получаем или создаем общий WebRTC сервис ПЕРЕД вызовом ShowView,
            // чтобы статус мог быть восстановлен сразу при открытии вкладки
            GetOrCreateSharedWebRtcService();
            
            ShowView(AdvancedSettingsView);
            // LoadGeneralSettings уже вызывается в ShowView, не нужно вызывать дважды
        }
        
        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(AboutButton);
            ShowView(AboutSettingsView);
            
            // Загружаем текущую версию приложения
            string currentVersion = GitHubVersionService.GetCurrentVersion();
            VersionTextBlock.Text = $"Version: {currentVersion}";
            
            // Загружаем сохраненный GitHub токен (расшифровываем)
            LoadGitHubToken();
        }
        
        private void LoadGitHubToken()
        {
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null && !string.IsNullOrEmpty(settings.GitHubTokenEncrypted))
                    {
                        // Расшифровываем токен перед отображением
                        string decryptedToken = TokenEncryption.Decrypt(settings.GitHubTokenEncrypted);
                        GitHubTokenPasswordBox.Password = decryptedToken;
                        
                        // Если токен уже сохранен, показываем кнопку "Check for Updates"
                        UpdateTokenButtonState(true);
                    }
                    else
                    {
                        // Если токен не сохранен, показываем кнопку "Save Token"
                        UpdateTokenButtonState(false);
                    }
                }
                else
                {
                    UpdateTokenButtonState(false);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error loading GitHub token: {ex.Message}");
                UpdateTokenButtonState(false);
            }
        }
        
        private void UpdateTokenButtonState(bool tokenSaved)
        {
            if (SaveTokenButton == null || CheckForUpdatesButton == null)
                return;
                
            if (tokenSaved)
            {
                // Токен сохранен - показываем кнопку "Check for Updates"
                SaveTokenButton.Visibility = Visibility.Collapsed;
                CheckForUpdatesButton.Visibility = Visibility.Visible;
            }
            else
            {
                // Токен не сохранен - показываем кнопку "Save Token"
                SaveTokenButton.Visibility = Visibility.Visible;
                CheckForUpdatesButton.Visibility = Visibility.Collapsed;
            }
        }
        
        private void SaveTokenButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string tokenToSave = GitHubTokenPasswordBox.Password;
                
                if (string.IsNullOrWhiteSpace(tokenToSave))
                {
                    CustomMessageBox.Show("Please enter a GitHub token before saving.", 
                        "Token Required", MessageBoxButton.OK, MessageBoxImage.Information, this);
                    return;
                }
                
                // Сохраняем токен (шифруем перед сохранением)
                SaveGitHubToken();
                
                // Обновляем состояние кнопок
                UpdateTokenButtonState(true);
                
                CustomMessageBox.Show("GitHub token saved successfully!\n\nThe token has been encrypted and stored securely.", 
                    "Token Saved", MessageBoxButton.OK, MessageBoxImage.Information, this);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error saving token: {ex.Message}");
                CustomMessageBox.Show($"Error saving token:\n\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }
        
        private void SaveGitHubToken()
        {
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

                // Шифруем токен перед сохранением
                string tokenToSave = GitHubTokenPasswordBox.Password;
                if (!string.IsNullOrEmpty(tokenToSave))
                {
                    string encryptedToken = TokenEncryption.Encrypt(tokenToSave);
                    if (string.IsNullOrEmpty(encryptedToken))
                    {
                        MainWindow.Log("[SettingsWindow] ERROR: Failed to encrypt token");
                        throw new Exception("Failed to encrypt token");
                    }
                    
                    settings.GitHubTokenEncrypted = encryptedToken;
                    
                    // Проверяем, что токен правильно зашифровался (тест расшифровки)
                    string testDecrypt = TokenEncryption.Decrypt(encryptedToken);
                    if (testDecrypt != tokenToSave)
                    {
                        MainWindow.Log("[SettingsWindow] WARNING: Token encryption/decryption test failed!");
                    }
                    else
                    {
                        string tokenPreview = tokenToSave.Length > 10 
                            ? tokenToSave.Substring(0, 10) + "..." 
                            : tokenToSave.Substring(0, Math.Min(tokenToSave.Length, 10));
                        MainWindow.Log($"[SettingsWindow] GitHub token saved (encrypted, preview: {tokenPreview}, length: {tokenToSave.Length})");
                    }
                }
                else
                {
                    settings.GitHubTokenEncrypted = null;
                    MainWindow.Log("[SettingsWindow] GitHub token cleared");
                }

                string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), settingsJson);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error saving GitHub token: {ex.Message}");
            }
        }
        
        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CheckForUpdatesButton.IsEnabled = false;
                CheckForUpdatesButton.Content = "Checking...";
                
                // Получаем данные репозитория
                string repositoryOwner = GetRepositoryOwner();
                string repositoryName = GetRepositoryName();
                
                if (repositoryOwner == "YOUR_GITHUB_USERNAME" || repositoryName == "YOUR_REPOSITORY_NAME")
                {
                    CustomMessageBox.Show(
                        "GitHub repository is not configured.\n\n" +
                        "Please update the repository settings in the code:\n" +
                        "- SettingsWindow.xaml.cs: GetRepositoryOwner() and GetRepositoryName()\n" +
                        "- MainWindow.xaml.cs: CheckForUpdatesOnStartupAsync()",
                        "Repository Not Configured", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Information, 
                        this);
                    CheckForUpdatesButton.IsEnabled = true;
                    CheckForUpdatesButton.Content = "Check for Updates";
                    return;
                }
                
                MainWindow.Log("[SettingsWindow] Manual update check initiated");
                
                // Получаем GitHub токен из настроек (расшифрованный)
                string? githubToken = GitHubTokenProvider.GetToken();
                
                var latestRelease = await GitHubVersionService.CheckForUpdateAsync(repositoryOwner, repositoryName, githubToken);
                
                if (latestRelease != null)
                {
                    string currentVersion = GitHubVersionService.GetCurrentVersion();
                    string latestVersion = latestRelease.TagName.TrimStart('v', 'V');
                    
                    int comparison = GitHubVersionService.CompareVersions(currentVersion, latestVersion);
                    
                    if (comparison < 0)
                    {
                        // Новая версия доступна
                        string repositoryUrl = $"https://github.com/Miomi228/Softphone";
                        var updateWindow = new UpdateAvailableWindow(latestRelease, currentVersion, repositoryUrl)
                        {
                            Owner = this
                        };
                        updateWindow.ShowDialog();
                    }
                    else
                    {
                        CustomMessageBox.Show($"You are using the latest version ({currentVersion}).", 
                            "No Updates Available", MessageBoxButton.OK, MessageBoxImage.Information, this);
                    }
                }
                else
                {
                    // Проверяем, есть ли токен (для более точного сообщения об ошибке)
                    bool hasToken = !string.IsNullOrEmpty(githubToken);
                    
                    // Более информативное сообщение об ошибке
                    string errorMessage = "Could not check for updates.\n\n";
                    
                    if (hasToken)
                    {
                        errorMessage += "Possible reasons:\n";
                        errorMessage += "• GitHub token is invalid or expired (401 error)\n";
                        errorMessage += "• Token does not have required permissions\n";
                        errorMessage += "• Repository does not exist or is private\n";
                        errorMessage += "• No releases have been created yet\n";
                        errorMessage += "• Internet connection issue\n\n";
                        errorMessage += $"Repository: {repositoryOwner}/{repositoryName}\n";
                        errorMessage += "Token: configured (check if valid)";
                    }
                    else
                    {
                        errorMessage += "Possible reasons:\n";
                        errorMessage += "• Repository is private and token is not configured\n";
                        errorMessage += "• Repository does not exist\n";
                        errorMessage += "• No releases have been created yet\n";
                        errorMessage += "• Repository name or owner is incorrect\n";
                        errorMessage += "• Internet connection issue\n\n";
                        errorMessage += $"Repository: {repositoryOwner}/{repositoryName}\n";
                        errorMessage += "Token: not configured (required for private repositories)";
                    }
                    
                    CustomMessageBox.Show(errorMessage, 
                        "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error checking for updates: {ex.Message}");
                CustomMessageBox.Show($"Error checking for updates:\n\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
            finally
            {
                CheckForUpdatesButton.IsEnabled = true;
                CheckForUpdatesButton.Content = "Check for Updates";
            }
        }
        
        /// <summary>
        /// Получает владельца репозитория GitHub
        /// </summary>
        private string GetRepositoryOwner()
        {
            return "Miomi228";
        }
        
        /// <summary>
        /// Получает название репозитория GitHub
        /// </summary>
        private string GetRepositoryName()
        {
            return "Softphone";
        }
        
        private void LoadGeneralSettings()
        {
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null)
                    {
                        UseWebRtcCheckBox.IsChecked = settings.UseWebRtcAudio;
                        // Используем сохраненное значение или предзаполняем "wss://"
                        if (!string.IsNullOrWhiteSpace(settings.WebRtcWsUri))
                        {
                            // Если значение не начинается с "wss://", добавляем префикс
                            string wsUri = settings.WebRtcWsUri;
                            if (!wsUri.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) && 
                                !wsUri.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
                            {
                                wsUri = "wss://" + wsUri;
                            }
                            WebRtcWsUriTextBox.Text = wsUri;
                            System.Diagnostics.Debug.WriteLine($"LoadGeneralSettings: Loaded WebRtcWsUri from file: '{settings.WebRtcWsUri}' -> '{wsUri}'");
                        }
                        else
                        {
                            WebRtcWsUriTextBox.Text = "wss://";
                            System.Diagnostics.Debug.WriteLine("LoadGeneralSettings: WebRtcWsUri was empty in file, prefilled with 'wss://'");
                        }
                        System.Diagnostics.Debug.WriteLine($"LoadGeneralSettings: UseWebRtcAudio={settings.UseWebRtcAudio}, WebRtcWsUri='{settings.WebRtcWsUri}'");
                        
                        // Обновляем состояние кнопки тестирования
                        if (TestWebRtcConnectionButton != null)
                        {
                            TestWebRtcConnectionButton.IsEnabled = settings.UseWebRtcAudio;
                        }
                    }
                }
                else
                {
                    // Если файла нет, устанавливаем значения по умолчанию
                    UseWebRtcCheckBox.IsChecked = false;
                    WebRtcWsUriTextBox.Text = "wss://";
                    System.Diagnostics.Debug.WriteLine("LoadGeneralSettings: Settings file not found, prefilled with 'wss://'");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadGeneralSettings: Error - {ex.Message}");
                CustomMessageBox.Show($"Error loading general settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning, this);
            }
        }
        
        private bool _isUpdatingWebRtcStatus = false; // Флаг для предотвращения множественных вызовов
        private System.Threading.CancellationTokenSource? _updateWebRtcStatusCts; // Для отмены отложенных вызовов
        
        private void UpdateWebRtcStatus()
        {
            // Отменяем предыдущий отложенный вызов, если он есть
            _updateWebRtcStatusCts?.Cancel();
            _updateWebRtcStatusCts = new System.Threading.CancellationTokenSource();
            var token = _updateWebRtcStatusCts.Token;
            
            // Откладываем выполнение на 500мс (debounce)
            System.Threading.Tasks.Task.Delay(500, token).ContinueWith(async t =>
            {
                if (t.IsCanceled || token.IsCancellationRequested)
                {
                    return;
                }
                
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateWebRtcStatusInternal();
                });
            });
        }
        
        private void UpdateWebRtcStatusInternal()
        {
            // Предотвращаем множественные одновременные вызовы
            if (_isUpdatingWebRtcStatus)
            {
                MainWindow.Log("[WebRTC] UpdateWebRtcStatus already in progress, skipping...");
                return;
            }
            
            try
            {
                _isUpdatingWebRtcStatus = true;
                
                // Проверяем, что элементы UI инициализированы
                if (UseWebRtcCheckBox == null || WebRtcWsUriTextBox == null || WebRtcStatusTextBlock == null)
                {
                    System.Diagnostics.Debug.WriteLine("UpdateWebRtcStatus: UI elements not initialized yet");
                    return;
                }
                
                // Используем текущие значения из UI
                bool useWebRtc = UseWebRtcCheckBox.IsChecked ?? false;
                string wsUri = WebRtcWsUriTextBox.Text?.Trim() ?? "";
                
                System.Diagnostics.Debug.WriteLine($"UpdateWebRtcStatus: useWebRtc={useWebRtc}, wsUri='{wsUri}', wsUri.Length={wsUri.Length}, TextBox.IsLoaded={WebRtcWsUriTextBox.IsLoaded}");
                
                // Загружаем настройки из файла для получения SIP credentials
                AppSettings? settings = null;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    System.Diagnostics.Debug.WriteLine($"UpdateWebRtcStatus: Loaded settings from file, WebRtcWsUri='{settings?.WebRtcWsUri}'");
                }
                
                // Если значение в TextBox пустое, но есть в настройках, используем значение из настроек
                if (string.IsNullOrWhiteSpace(wsUri) && settings != null && !string.IsNullOrWhiteSpace(settings.WebRtcWsUri))
                {
                    wsUri = settings.WebRtcWsUri;
                    WebRtcWsUriTextBox.Text = wsUri;
                    System.Diagnostics.Debug.WriteLine($"UpdateWebRtcStatus: Using URI from settings file: '{wsUri}'");
                }
                
                if (!useWebRtc)
                {
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Disabled";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                    StopWebRtcStatusCheck();
                    // НЕ удаляем общий сервис здесь, только отписываемся
                    if (TestWebRtcConnectionButton != null)
                        TestWebRtcConnectionButton.IsEnabled = false;
                    return;
                }
                
                // Включаем кнопку тестирования если WebRTC включен и настроен
                if (TestWebRtcConnectionButton != null)
                {
                    bool canTest = !string.IsNullOrWhiteSpace(wsUri) && 
                                   wsUri != "wss://pbx.example.com:8089/ws" &&
                                   settings != null && 
                                   !string.IsNullOrWhiteSpace(settings.SipUsername) && 
                                   !string.IsNullOrWhiteSpace(settings.SipPassword);
                    TestWebRtcConnectionButton.IsEnabled = canTest;
                }
                
                // Проверяем, что URI указан и не является дефолтным примером
                // Проверяем только на точное совпадение с дефолтным примером
                bool isDefaultExample = wsUri == "wss://pbx.example.com:8089/ws";
                
                System.Diagnostics.Debug.WriteLine($"UpdateWebRtcStatus: Checking URI - wsUri='{wsUri}', IsNullOrWhiteSpace={string.IsNullOrWhiteSpace(wsUri)}, isDefaultExample={isDefaultExample}");
                
                if (string.IsNullOrWhiteSpace(wsUri) || isDefaultExample)
                {
                    System.Diagnostics.Debug.WriteLine($"UpdateWebRtcStatus: URI is empty or default example. wsUri='{wsUri}', isDefaultExample={isDefaultExample}");
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Not configured (WebSocket URI missing or invalid)";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    StopWebRtcStatusCheck();
                    // НЕ удаляем общий сервис здесь, только отписываемся
                    return;
                }
                
                if (settings == null || string.IsNullOrWhiteSpace(settings.SipUsername) || string.IsNullOrWhiteSpace(settings.SipPassword))
                {
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Not configured (SIP credentials missing)";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    StopWebRtcStatusCheck();
                    // НЕ удаляем общий сервис здесь, только отписываемся
                    return;
                }
                
                // Проверяем текущий статус подключения перед запуском новой проверки
                if (_sharedWebRtcStatusService != null)
                {
                    var currentStatus = _sharedWebRtcStatusService.CurrentStatus;
                    
                    // Если подключение уже активно и конфигурация не изменилась, не запускаем новую проверку
                    if (currentStatus == WebRtcConnectionStatus.Registered || 
                        currentStatus == WebRtcConnectionStatus.Connected)
                    {
                        // Проверяем, что конфигурация не изменилась
                        string currentWsUri = _sharedWebRtcStatusService.CurrentConfig?.WsUri ?? "";
                        string currentSipUri = _sharedWebRtcStatusService.CurrentConfig?.SipUri ?? "";
                        
                        // Формируем ожидаемый SIP URI для сравнения
                        string pbxAddress = "";
                        if (!string.IsNullOrEmpty(wsUri))
                        {
                            try
                            {
                                var uri = new Uri(wsUri);
                                pbxAddress = uri.Host;
                            }
                            catch { }
                        }
                        if (string.IsNullOrEmpty(pbxAddress))
                        {
                            pbxAddress = settings.SipServer?.Split(':')[0] ?? "";
                        }
                        string expectedSipUri = $"sip:{settings.SipUsername}@{pbxAddress}";
                        
                        // Если конфигурация не изменилась, просто восстанавливаем статус и подписываемся на события
                        if (currentWsUri == wsUri && currentSipUri == expectedSipUri)
                        {
                            MainWindow.Log($"[WebRTC] Connection already active ({currentStatus}), skipping new connection check");
                            UpdateWebRtcStatusDisplay(currentStatus);
                            
                            // Подписываемся на изменения статуса, если еще не подписаны
                            if (_webRtcStatusService == null)
                            {
                                _webRtcStatusService = _sharedWebRtcStatusService;
                                _webRtcStatusService.OnStatusChanged += UpdateWebRtcStatusDisplay;
                            }
                            
                            return;
                        }
                        else
                        {
                            MainWindow.Log($"[WebRTC] Configuration changed (URI or SIP credentials), reconnecting...");
                            MainWindow.Log($"[WebRTC]   Current: WsUri={currentWsUri}, SipUri={currentSipUri}");
                            MainWindow.Log($"[WebRTC]   New: WsUri={wsUri}, SipUri={expectedSipUri}");
                        }
                    }
                }
                
                // Обновляем настройки для проверки подключения
                var checkSettings = new AppSettings
                {
                    UseWebRtcAudio = true,
                    WebRtcWsUri = wsUri,
                    SipUsername = settings.SipUsername,
                    SipPassword = settings.SipPassword,
                    SipServer = settings.SipServer
                };
                
                // Запускаем проверку подключения
                StartWebRtcStatusCheck(checkSettings);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] Error updating WebRTC status: {ex.Message}");
                WebRtcStatusTextBlock.Text = "WebRTC Status: Unknown";
                WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                StopWebRtcStatusCheck();
            }
            finally
            {
                _isUpdatingWebRtcStatus = false;
            }
        }
        
        private async void StartWebRtcStatusCheck(AppSettings settings)
        {
            try
            {
                // Проверяем, не инициализирован ли уже WebRTC сервис в MainWindow
                var isReady = WebRtcService.Instance.IsReadyForCalls;
                MainWindow.Log($"[WebRTC] Checking WebRTC service status: IsReadyForCalls={isReady}");
                
                if (isReady)
                {
                    // WebRTC уже работает - просто показываем статус без повторной инициализации
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Connected and Ready";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                    MainWindow.Log("[WebRTC] Using existing WebRTC service status (already initialized)");
                    return;
                }
                
                MainWindow.Log("[WebRTC] WebRTC service not ready, initializing test connection in Settings");
                
                // Формируем конфиг
                // Извлекаем адрес PBX из WebSocket URI или используем SipServer
                string pbxAddress = "";
                if (!string.IsNullOrEmpty(settings.WebRtcWsUri))
                {
                    try
                    {
                        var uri = new Uri(settings.WebRtcWsUri);
                        pbxAddress = uri.Host; // Извлекаем домен/IP из WebSocket URI
                    }
                    catch
                    {
                        // Если не удалось распарсить WebSocket URI, используем SipServer
                    }
                }
                if (string.IsNullOrEmpty(pbxAddress))
                {
                    pbxAddress = settings.SipServer?.Split(':')[0] ?? "";
                }
                if (string.IsNullOrEmpty(pbxAddress))
                {
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Not configured (PBX address missing)";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    return;
                }
                string username = settings.SipUsername ?? "";
                // Формируем SIP URI: sip:username@pbx_address
                string sipUri = $"sip:{username}@{pbxAddress}";
                string wsUri = settings.WebRtcWsUri ?? "";
                
                // Используем общий сервис (он уже создан в конструкторе)
                if (_sharedWebRtcStatusService == null)
                {
                    MainWindow.Log("[WebRTC] ERROR: Shared WebRtcStatusService is null");
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Error (service not initialized)";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    return;
                }
                
                // Отписываемся от старого обработчика
                StopWebRtcStatusCheck();
                
                _webRtcStatusService = _sharedWebRtcStatusService;
                _webRtcStatusService.OnStatusChanged += (status) =>
                {
                    Dispatcher.Invoke(() => UpdateWebRtcStatusDisplay(status));
                };
                
                var config = new WebRtcConfig
                {
                    WsUri = wsUri,
                    SipUri = sipUri,
                    Password = settings.SipPassword ?? ""
                };
                
                // Логируем только кратко для тестирования в настройках
                MainWindow.Log($"[WebRTC] Testing connection in Settings (WsUri={wsUri}, SipUri={sipUri})");
                
                // Запускаем проверку подключения (InitializeAsync сам обработает множественные вызовы)
                await _webRtcStatusService.CheckConnectionAsync(config);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] Error starting WebRTC status check: {ex.Message}");
                WebRtcStatusTextBlock.Text = "WebRTC Status: Error checking connection";
                WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
            }
        }
        
        private void StopWebRtcStatusCheck()
        {
            try
            {
                if (_webRtcStatusService != null)
                {
                    // Не удаляем сервис, если он еще инициализируется
                    // Просто отписываемся от событий
                    _webRtcStatusService.OnStatusChanged -= UpdateWebRtcStatusDisplay;
                    _webRtcStatusService = null;
                }
                
                // НЕ останавливаем подключение в shared сервисе при закрытии окна настроек
                // Подключение должно оставаться активным, если WebRTC включен
                // Остановка подключения происходит только при выключении WebRTC или закрытии приложения
            }
            catch
            {
                // Игнорируем ошибки
            }
        }
        
        private void DisposeWebRtcStatusService()
        {
            try
            {
                if (_sharedWebRtcStatusService != null)
                {
                    MainWindow.Log("[WebRTC] Disposing shared WebRtcStatusService");
                    _sharedWebRtcStatusService.Dispose();
                    _sharedWebRtcStatusService = null;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] Error disposing shared service: {ex.Message}");
            }
        }
        
        private void UpdateWebRtcStatusDisplay(WebRtcConnectionStatus status)
        {
            switch (status)
            {
                case WebRtcConnectionStatus.NotConnected:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Not connected";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                    break;
                case WebRtcConnectionStatus.InitializingWebView2:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Initializing WebView2...";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    break;
                case WebRtcConnectionStatus.InitializingJsSIP:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Initializing JsSIP...";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    break;
                case WebRtcConnectionStatus.ConnectingToWebSocket:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Connecting to WebSocket...";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    break;
                case WebRtcConnectionStatus.Connecting:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Connecting...";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    break;
                case WebRtcConnectionStatus.Connected:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Connected";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    break;
                case WebRtcConnectionStatus.Registered:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Registered";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                    break;
                case WebRtcConnectionStatus.Disconnected:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Disconnected";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                    break;
                case WebRtcConnectionStatus.RegistrationFailed:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Registration failed";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    break;
                case WebRtcConnectionStatus.Error:
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Error";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    break;
            }
        }
        
        private void SaveGeneralSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Загружаем существующие настройки
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

                // Сохраняем WebRTC настройки
                settings.UseWebRtcAudio = UseWebRtcCheckBox.IsChecked ?? false;
                settings.WebRtcWsUri = WebRtcWsUriTextBox.Text.Trim();
                
                System.Diagnostics.Debug.WriteLine($"SaveGeneralSettings: Saving UseWebRtcAudio={settings.UseWebRtcAudio}, WebRtcWsUri='{settings.WebRtcWsUri}'");

                // Сохраняем в файл
                string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), settingsJson);
                
                System.Diagnostics.Debug.WriteLine($"SaveGeneralSettings: Settings saved to file");

                // Обновляем статус после сохранения с небольшой задержкой
                Dispatcher.BeginInvoke(new Action(async () => 
                {
                    System.Diagnostics.Debug.WriteLine($"SaveGeneralSettings: Calling UpdateWebRtcStatus after save, TextBox.Text='{WebRtcWsUriTextBox?.Text}'");
                    UpdateWebRtcStatus();
                    
                    // Обновляем индикатор WebRTC в главном окне
                    if (Owner is MainWindow mainWindow)
                    {
                        mainWindow.UpdateWebRtcIndicator();
                        
                        // Переподключаемся с учетом нового режима (SIP или WebRTC)
                        await mainWindow.ReconnectFromSettingsAsync();
                        
                        // Обновляем статус подключения
                        mainWindow.UpdateConnectionStatus();
                        UpdateConnectionStatus(); // Обновляем статус в окне настроек
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                CustomMessageBox.Show("General settings saved!", "Success", 
                    MessageBoxButton.OK, MessageBoxImage.Information, this);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error saving general settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }
        
        private async void TestWebRtcConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TestWebRtcConnectionButton.IsEnabled = false;
                TestWebRtcConnectionButton.Content = "Testing...";
                
                MainWindow.Log("[WebRTC] ===== Manual connection test initiated =====");
                
                // Останавливаем предыдущую проверку
                StopWebRtcStatusCheck();
                
                // Проверяем настройки перед тестированием
                bool useWebRtc = UseWebRtcCheckBox.IsChecked ?? false;
                string wsUri = WebRtcWsUriTextBox?.Text?.Trim() ?? "";
                
                if (!useWebRtc)
                {
                    CustomMessageBox.Show("Please enable 'Use WebRTC for audio' first.", "WebRTC Test", 
                        MessageBoxButton.OK, MessageBoxImage.Information, this);
                    TestWebRtcConnectionButton.IsEnabled = true;
                    TestWebRtcConnectionButton.Content = "Test Connection";
                    return;
                }
                
                if (string.IsNullOrWhiteSpace(wsUri) || wsUri == "wss://pbx.example.com:8089/ws")
                {
                    CustomMessageBox.Show("Please enter a valid WebSocket URI.", "WebRTC Test", 
                        MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    TestWebRtcConnectionButton.IsEnabled = true;
                    TestWebRtcConnectionButton.Content = "Test Connection";
                    return;
                }
                
                // Загружаем настройки для получения SIP credentials
                AppSettings? settings = null;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                
                if (settings == null || string.IsNullOrWhiteSpace(settings.SipUsername) || string.IsNullOrWhiteSpace(settings.SipPassword))
                {
                    CustomMessageBox.Show("Please configure SIP credentials in the 'Connection' tab first.", "WebRTC Test", 
                        MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    TestWebRtcConnectionButton.IsEnabled = true;
                    TestWebRtcConnectionButton.Content = "Test Connection";
                    return;
                }
                
                // Показываем начальный статус
                WebRtcStatusTextBlock.Text = "WebRTC Status: Testing connection...";
                WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                
                // Формируем конфиг
                // Извлекаем адрес PBX из WebSocket URI или используем SipServer
                string pbxAddress = "";
                if (!string.IsNullOrEmpty(wsUri))
                {
                    try
                    {
                        var uri = new Uri(wsUri);
                        pbxAddress = uri.Host; // Извлекаем домен/IP из WebSocket URI
                    }
                    catch
                    {
                        // Если не удалось распарсить WebSocket URI, используем SipServer
                    }
                }
                if (string.IsNullOrEmpty(pbxAddress))
                {
                    pbxAddress = settings.SipServer?.Split(':')[0] ?? "";
                }
                string username = settings.SipUsername ?? "";
                // Формируем SIP URI: sip:username@pbx_address
                string sipUri = $"sip:{username}@{pbxAddress}";
                
                MainWindow.Log($"[WebRTC] Test config:");
                MainWindow.Log($"[WebRTC]   WebSocket URI: {wsUri}");
                MainWindow.Log($"[WebRTC]   SIP URI: {sipUri}");
                MainWindow.Log($"[WebRTC]   PBX Address: {pbxAddress}");
                MainWindow.Log($"[WebRTC]   Username: {settings.SipUsername} -> {username}");
                
                var config = new WebRtcConfig
                {
                    WsUri = wsUri,
                    SipUri = sipUri,
                    Password = settings.SipPassword ?? ""
                };
                
                // Используем общий сервис для тестирования (он уже создан в конструкторе)
                if (_sharedWebRtcStatusService == null)
                {
                    MainWindow.Log("[WebRTC] ERROR: Shared WebRtcStatusService is null");
                    TestWebRtcConnectionButton.IsEnabled = true;
                    TestWebRtcConnectionButton.Content = "Test Connection";
                    return;
                }
                
                var testService = _sharedWebRtcStatusService;
                bool testCompleted = false;
                WebRtcConnectionStatus finalStatus = WebRtcConnectionStatus.NotConnected;
                
                // Отписываемся от предыдущих обработчиков
                StopWebRtcStatusCheck();
                
                testService.OnStatusChanged += (status) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        MainWindow.Log($"[WebRTC] Test status changed: {status}");
                        finalStatus = status;
                        
                        // Обновляем статус в UI
                        UpdateWebRtcStatusDisplay(status);
                        
                        // Если получили финальный статус (Registered или ошибка), завершаем тест
                        if (status == WebRtcConnectionStatus.Registered || 
                            status == WebRtcConnectionStatus.RegistrationFailed ||
                            status == WebRtcConnectionStatus.Error)
                        {
                            // Проверяем, что тест еще не завершен, чтобы не показывать сообщение дважды
                            if (testCompleted)
                            {
                                MainWindow.Log($"[WebRTC] Test already completed, ignoring status change: {status}");
                                return;
                            }
                            
                            testCompleted = true;
                            
                            // Показываем результат теста
                            string resultMessage = "";
                            MessageBoxImage icon = MessageBoxImage.Information;
                            
                            switch (status)
                            {
                                case WebRtcConnectionStatus.Registered:
                                    resultMessage = $"✓ WebRTC connection test successful!\n\n" +
                                                   $"Connected to: {wsUri}\n" +
                                                   $"Registered as: {sipUri}\n\n" +
                                                   $"WebRTC is ready to use.";
                                    icon = MessageBoxImage.Information;
                                    break;
                                case WebRtcConnectionStatus.RegistrationFailed:
                                    resultMessage = $"✗ WebRTC registration failed.\n\n" +
                                                   $"WebSocket URI: {wsUri}\n" +
                                                   $"SIP URI: {sipUri}\n\n" +
                                                   $"Please check:\n" +
                                                   $"• SIP username and password\n" +
                                                   $"• WebSocket URI is correct\n" +
                                                   $"• Server supports WebRTC endpoints";
                                    icon = MessageBoxImage.Warning;
                                    break;
                                case WebRtcConnectionStatus.Error:
                                    // Показываем ошибку только если не было успешной регистрации
                                    resultMessage = $"✗ WebRTC connection error.\n\n" +
                                                   $"Please check:\n" +
                                                   $"• WebSocket URI is accessible\n" +
                                                   $"• Server supports WebSocket connections\n" +
                                                   $"• Check Debug Output for details";
                                    icon = MessageBoxImage.Error;
                                    break;
                            }
                            
                            // Проверяем, что окно еще открыто перед показом MessageBox
                            if (IsLoaded && IsVisible)
                            {
                                try
                                {
                                    // Используем Dispatcher.BeginInvoke для асинхронного показа сообщения,
                                    // чтобы избежать конфликтов при быстрой смене статусов
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        try
                                        {
                                            CustomMessageBox.Show(resultMessage, "WebRTC Test Result", 
                                                MessageBoxButton.OK, icon, this);
                                        }
                                        catch (Exception msgEx)
                                        {
                                            MainWindow.Log($"[WebRTC] Error showing message box: {msgEx.Message}");
                                            // Показываем результат в статусе вместо MessageBox
                                            WebRtcStatusTextBlock.Text = status == WebRtcConnectionStatus.Registered 
                                                ? "WebRTC Status: Test successful ✓" 
                                                : "WebRTC Status: Test failed ✗";
                                        }
                                    }), System.Windows.Threading.DispatcherPriority.Normal);
                                }
                                catch (Exception msgEx)
                                {
                                    MainWindow.Log($"[WebRTC] Error scheduling message box: {msgEx.Message}");
                                    // Показываем результат в статусе вместо MessageBox
                                    WebRtcStatusTextBlock.Text = status == WebRtcConnectionStatus.Registered 
                                        ? "WebRTC Status: Test successful ✓" 
                                        : "WebRTC Status: Test failed ✗";
                                }
                            }
                            else
                            {
                                MainWindow.Log("[WebRTC] Settings window closed, skipping message box");
                            }
                            
                            // НЕ удаляем сервис - он общий и может использоваться дальше
                            TestWebRtcConnectionButton.IsEnabled = true;
                            TestWebRtcConnectionButton.Content = "Test Connection";
                        }
                    });
                };
                
                // Запускаем тест с таймаутом
                var testTask = testService.CheckConnectionAsync(config);
                var timeoutTask = System.Threading.Tasks.Task.Delay(30000); // 30 секунд таймаут (увеличено)
                
                var completedTask = await System.Threading.Tasks.Task.WhenAny(testTask, timeoutTask);
                
                if (completedTask == timeoutTask && !testCompleted)
                {
                    // Таймаут - НЕ удаляем сервис, он общий
                    MainWindow.Log("[WebRTC] Test timed out after 30 seconds.");
                    if (IsLoaded && IsVisible)
                    {
                        CustomMessageBox.Show("WebRTC test timed out after 30 seconds.\nThis might indicate:\n• WebSocket server is not accessible\n• Network connectivity issues\n• WebView2 initialization is still in progress",
                            "WebRTC Test Result", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    }
                    WebRtcStatusTextBlock.Text = "WebRTC Status: Test timeout (check connection)";
                    WebRtcStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                    
                    CustomMessageBox.Show("WebRTC test timed out after 15 seconds.\n\n" +
                                         "This might indicate:\n" +
                                         "• WebSocket server is not accessible\n" +
                                         "• Network connectivity issues\n" +
                                         "• Server is not responding\n\n" +
                                         "Check Debug Output for detailed logs.", 
                                         "WebRTC Test Timeout", 
                                         MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    
                    TestWebRtcConnectionButton.IsEnabled = true;
                    TestWebRtcConnectionButton.Content = "Test Connection";
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC] ERROR during manual test: {ex.Message}");
                MainWindow.Log($"[WebRTC] Stack trace: {ex.StackTrace}");
                
                CustomMessageBox.Show($"Error testing WebRTC connection:\n\n{ex.Message}\n\nCheck Debug Output for details.", 
                    "WebRTC Test Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error, this);
                
                TestWebRtcConnectionButton.IsEnabled = true;
                TestWebRtcConnectionButton.Content = "Test Connection";
            }
        }

        private void LoadSettings()
        {
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                MainWindow.Log($"[SettingsWindow] LoadSettings: Checking file at {settingsPath}");
                
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    MainWindow.Log($"[SettingsWindow] LoadSettings: File exists, JSON length={json.Length}");
                    MainWindow.Log($"[SettingsWindow] LoadSettings: JSON content={json}");
                    
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null)
                    {
                        MainWindow.Log($"[SettingsWindow] LoadSettings: Settings loaded - SipServer='{settings.SipServer}', SipUsername='{settings.SipUsername}', SipPassword length={settings.SipPassword?.Length ?? 0}");
                        
                        // Парсим server:port если есть
                        string server = settings.SipServer ?? "";
                        string port = "5060";
                        if (server.Contains(":"))
                        {
                            var parts = server.Split(':');
                            server = parts[0];
                            if (parts.Length > 1) port = parts[1];
                        }
                        
                        MainWindow.Log($"[SettingsWindow] LoadSettings: Parsed - server='{server}', port='{port}'");
                        
                        // Сохраняем значения для установки
                        string finalServer = server;
                        string finalPort = port;
                        string finalUsername = settings.SipUsername ?? "";
                        string finalPassword = settings.SipPassword ?? "";
                        
                        // Устанавливаем значения напрямую, так как мы уже в UI потоке (вызвано из Loaded)
                        if (SipServerTextBox != null)
                        {
                            SipServerTextBox.Text = finalServer;
                            MainWindow.Log($"[SettingsWindow] LoadSettings: Set SipServerTextBox.Text='{SipServerTextBox.Text}'");
                        }
                        else
                        {
                            MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - SipServerTextBox is null!");
                        }
                        
                        if (SipPortTextBox != null)
                        {
                            SipPortTextBox.Text = finalPort;
                            MainWindow.Log($"[SettingsWindow] LoadSettings: Set SipPortTextBox.Text='{SipPortTextBox.Text}'");
                        }
                        else
                        {
                            MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - SipPortTextBox is null!");
                        }
                        
                        if (SipUsernameTextBox != null)
                        {
                            SipUsernameTextBox.Text = finalUsername;
                            MainWindow.Log($"[SettingsWindow] LoadSettings: Set SipUsernameTextBox.Text='{SipUsernameTextBox.Text}'");
                        }
                        else
                        {
                            MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - SipUsernameTextBox is null!");
                        }
                        
                        if (SipPasswordBox != null)
                        {
                            SipPasswordBox.Password = finalPassword;
                            MainWindow.Log($"[SettingsWindow] LoadSettings: Set SipPasswordBox.Password (length={SipPasswordBox.Password.Length})");
                        }
                        else
                        {
                            MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - SipPasswordBox is null!");
                        }
                    }
                    else
                    {
                        MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - Settings deserialized as null!");
                    }
                }
                else
                {
                    MainWindow.Log($"[SettingsWindow] LoadSettings: File does not exist at {settingsPath}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] LoadSettings: ERROR - {ex.Message}");
                MainWindow.Log($"[SettingsWindow] LoadSettings: Stack trace - {ex.StackTrace}");
                CustomMessageBox.Show($"Error loading settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
            }
            finally
            {
                // Убеждаемся, что поля доступны для редактирования после загрузки
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (SipServerTextBox != null)
                    {
                        SipServerTextBox.IsReadOnly = false;
                        SipServerTextBox.IsEnabled = true;
                        SipServerTextBox.Focusable = true;
                    }
                    if (SipPortTextBox != null)
                    {
                        SipPortTextBox.IsReadOnly = false;
                        SipPortTextBox.IsEnabled = true;
                        SipPortTextBox.Focusable = true;
                    }
                    if (SipUsernameTextBox != null)
                    {
                        SipUsernameTextBox.IsReadOnly = false;
                        SipUsernameTextBox.IsEnabled = true;
                        SipUsernameTextBox.Focusable = true;
                    }
                    if (SipPasswordBox != null)
                    {
                        SipPasswordBox.IsEnabled = true;
                        SipPasswordBox.Focusable = true;
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
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
                    CustomMessageBox.Show("Please fill in all SIP connection fields.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    return;
                }

                if (!int.TryParse(port, out int portNum) || portNum < 1 || portNum > 65535)
                {
                    CustomMessageBox.Show("Invalid port number. Using default port 5060.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning, this);
                    portNum = 5060;
                }

                string serverWithPort = $"{server}:{portNum}";
                
                // Загружаем существующие настройки, чтобы сохранить аудиоустройства
                AppSettings settings;
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string existingJson = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
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
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), json);

                // Отключаем кнопку во время подключения
                SaveAndConnectButton.IsEnabled = false;
                ConnectionStatusTextBlock.Text = "Status: Saving settings...";
                ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");

                // Уведомляем главное окно о необходимости переподключения
                if (Owner is MainWindow mainWindow)
                {
                    ConnectionStatusTextBlock.Text = "Status: Connecting...";
                    ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                    
                    // Подписка уже есть в конструкторе, просто вызываем переподключение
                    await mainWindow.ReconnectFromSettingsAsync();
                    
                    // Обновляем статус после переподключения
                    UpdateConnectionStatus();
                }

                SaveAndConnectButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                ConnectionStatusTextBlock.Text = $"Status: Error - {ex.Message}";
                ConnectionStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                SaveAndConnectButton.IsEnabled = true;
                CustomMessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
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
                    
                    // Выбираем сохраненные устройства
                    LoadAudioSettings();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    MicrophoneComboBox.IsEnabled = true;
                    SpeakerComboBox.IsEnabled = true;
                    CustomMessageBox.Show($"Error loading audio devices: {ex.Message}", "Error", 
                        MessageBoxButton.OK, MessageBoxImage.Warning, this);
                });
            }
        }
        
        private void LoadAudioDevices()
        {
            // Синхронная версия для обратной совместимости (если где-то еще используется)
            _ = LoadAudioDevicesAsync();
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

                        // Загружаем настройки кодеков
                        if (AudioCodecComboBox != null)
                        {
                            string codec = settings.AudioCodec ?? "PCMU";
                            foreach (ComboBoxItem item in AudioCodecComboBox.Items)
                            {
                                if (item.Tag?.ToString() == codec)
                                {
                                    AudioCodecComboBox.SelectedItem = item;
                                    break;
                                }
                            }
                        }

                        if (SampleRateComboBox != null)
                        {
                            int sampleRate = settings.AudioSampleRate > 0 ? settings.AudioSampleRate : 16000;
                            foreach (ComboBoxItem item in SampleRateComboBox.Items)
                            {
                                if (item.Tag != null && int.TryParse(item.Tag.ToString(), out int rate) && rate == sampleRate)
                                {
                                    SampleRateComboBox.SelectedItem = item;
                                    break;
                                }
                            }
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
                CustomMessageBox.Show($"Error loading audio settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning, this);
            }
        }

        private void SaveAudioSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Загружаем существующие настройки
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

                // Сохраняем настройки кодеков
                if (AudioCodecComboBox?.SelectedItem is ComboBoxItem codecItem && codecItem.Tag != null)
                {
                    settings.AudioCodec = codecItem.Tag.ToString() ?? "PCMU";
                }

                if (SampleRateComboBox?.SelectedItem is ComboBoxItem sampleRateItem && sampleRateItem.Tag != null)
                {
                    if (int.TryParse(sampleRateItem.Tag.ToString(), out int sampleRate))
                    {
                        settings.AudioSampleRate = sampleRate;
                    }
                }

                // Сохраняем в файл
                string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), settingsJson);

                CustomMessageBox.Show("Audio settings saved!", "Success", 
                    MessageBoxButton.OK, MessageBoxImage.Information, this);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error saving audio settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error, this);
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

        private void AudioCodecComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Можно добавить информацию о выбранном кодеке
        }

        private void SampleRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Можно добавить информацию о выбранной частоте дискретизации
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
                
                // Активируем главное окно после закрытия настроек
                // Используем BeginInvoke для выполнения после полного закрытия окна
                mainWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        mainWindow.Activate();
                        mainWindow.Focus();
                        mainWindow.BringIntoView();
                        
                        // Если окно свернуто, восстанавливаем его
                        if (mainWindow.WindowState == WindowState.Minimized)
                        {
                            mainWindow.WindowState = WindowState.Normal;
                        }
                    }
                    catch
                    {
                        // Игнорируем ошибки активации
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            
            // Отписываемся от событий сервиса, но НЕ удаляем shared service
            // Shared service должен жить дольше SettingsWindow
            StopWebRtcStatusCheck();
            
            // НЕ вызываем DisposeWebRtcStatusService() здесь - shared service используется приложением
            // Dispose будет вызван только при закрытии приложения или отключении WebRTC
            
            base.OnClosed(e);
        }
    }
}

