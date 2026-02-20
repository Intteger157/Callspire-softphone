using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using System.Globalization;
using System.Windows.Data;
using System.Linq;
using System.Threading.Tasks;
using FluentIcons.Common;

namespace Softphone
{
    public partial class SettingsWindow : Window
    {
        private bool _suppressRecordingToggleEvent = false;
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

            // Centralized Win10/Wpf vs Win11/DWM native appearance.
            NativeWindowAppearanceManager.Attach(this);
            
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

                // IMPORTANT: Call recording is available only in WebRTC mode.
                // If WebRTC is turned off, force-disable call recording immediately and persist it.
                DisableCallRecordingBecauseWebRtcIsOff();
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
            AppearanceSettingsView.Visibility = Visibility.Collapsed;
            AdvancedSettingsView.Visibility = Visibility.Collapsed;
            IntegrationsSettingsView.Visibility = Visibility.Collapsed;
            AboutSettingsView.Visibility = Visibility.Collapsed;

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
            AppearanceButton.Background = System.Windows.Media.Brushes.Transparent;
            AppearanceButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            AdvancedButton.Background = System.Windows.Media.Brushes.Transparent;
            AdvancedButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            IntegrationsButton.Background = System.Windows.Media.Brushes.Transparent;
            IntegrationsButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
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

        private void AppearanceButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(AppearanceButton);
            ShowView(AppearanceSettingsView);
            LoadAppearanceSettings();
        }
        
        private void LoadGeneralSettingsForGeneralTab()
        {
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    if (settings != null)
                    {
                        // Загружаем настройки записи звонков
                        if (EnableCallRecordingCheckBox != null)
                        {
                            _suppressRecordingToggleEvent = true;
                            EnableCallRecordingCheckBox.IsChecked = settings.EnableCallRecording;
                            UpdateCallRecordingToggleColor();
                            _suppressRecordingToggleEvent = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadGeneralSettingsForGeneralTab: Error - {ex.Message}");
            }
        }
        
        private void LoadIntegrationsSettings()
        {
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    if (settings != null)
                    {
                        // Загружаем настройки Kommo
                        if (EnableAmoCrmIntegrationCheckBox != null)
                        {
                            EnableAmoCrmIntegrationCheckBox.IsChecked = settings.EnableAmoCrmIntegration;
                            UpdateAmoCrmSettingsVisibility(settings.EnableAmoCrmIntegration);
                        }
                        if (EnableAmoCrmLeadSelectionCheckBox != null)
                        {
                            EnableAmoCrmLeadSelectionCheckBox.IsChecked = settings.EnableAmoCrmLeadSelection;
                        }
                        
                        if (AmoCrmSubdomainTextBox != null && !string.IsNullOrEmpty(settings.AmoCrmSubdomain))
                        {
                            AmoCrmSubdomainTextBox.Text = settings.AmoCrmSubdomain;
                        }
                        
                        // Загружаем режим аутентификации
                        string authMode = settings.AmoCrmAuthMode ?? "manual";
                        bool isOAuth = authMode == "oauth";
                        
                        // Обновляем визуальное состояние кнопок сегментированного контрола
                        var accentBlueBrush = (Brush)FindResource("AccentBlueBrush");
                        var textPrimaryBrush = (Brush)FindResource("TextPrimaryBrush");
                        
                        if (AmoCrmAuthModeManualButton != null)
                        {
                            AmoCrmAuthModeManualButton.Tag = isOAuth ? "manual" : "manual_selected";
                            AmoCrmAuthModeManualButton.Background = isOAuth ? Brushes.Transparent : accentBlueBrush;
                            AmoCrmAuthModeManualButton.Foreground = isOAuth ? textPrimaryBrush : Brushes.White;
                        }
                        
                        if (AmoCrmAuthModeOAuthButton != null)
                        {
                            AmoCrmAuthModeOAuthButton.Tag = isOAuth ? "oauth_selected" : "oauth";
                            AmoCrmAuthModeOAuthButton.Background = isOAuth ? accentBlueBrush : Brushes.Transparent;
                            AmoCrmAuthModeOAuthButton.Foreground = isOAuth ? Brushes.White : textPrimaryBrush;
                        }
                        
                        UpdateAmoCrmAuthModeVisibility(isOAuth);
                        
                        // Загружаем Manual Token настройки
                        if (AmoCrmAccessTokenPasswordBox != null && !string.IsNullOrEmpty(settings.AmoCrmAccessTokenEncrypted))
                        {
                            // Расшифровываем токен для отображения (только если он есть)
                            string decryptedToken = TokenEncryption.Decrypt(settings.AmoCrmAccessTokenEncrypted);
                            if (!string.IsNullOrEmpty(decryptedToken))
                            {
                                AmoCrmAccessTokenPasswordBox.Password = decryptedToken;
                            }
                        }
                        
                        // Загружаем OAuth настройки
                        if (AmoCrmClientIdTextBox != null && !string.IsNullOrEmpty(settings.AmoCrmClientId))
                        {
                            AmoCrmClientIdTextBox.Text = settings.AmoCrmClientId;
                        }
                        if (AmoCrmClientSecretPasswordBox != null && !string.IsNullOrEmpty(settings.AmoCrmClientSecretEncrypted))
                        {
                            string decryptedSecret = TokenEncryption.Decrypt(settings.AmoCrmClientSecretEncrypted);
                            if (!string.IsNullOrEmpty(decryptedSecret))
                            {
                                AmoCrmClientSecretPasswordBox.Password = decryptedSecret;
                            }
                        }
                        if (AmoCrmRedirectUriTextBox != null)
                        {
                            AmoCrmRedirectUriTextBox.Text = settings.AmoCrmRedirectUri ?? "http://localhost:8080/callback";
                        }
                        
                        // Обновляем статусы в зависимости от режима
                        // Проверяем реальное состояние сервиса, а не только наличие токенов в настройках
                        // Используем небольшую задержку, чтобы дать время сервису инициализироваться
                        _ = Task.Delay(500).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                CheckAmoCrmConnectionStatus();
                            });
                        }, TaskContinuationOptions.OnlyOnRanToCompletion);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadIntegrationsSettings: Error - {ex.Message}");
            }
        }
        
        /// <summary>
        /// Проверяет и обновляет статус подключения Kommo
        /// </summary>
        private void CheckAmoCrmConnectionStatus()
        {
            try
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                string authMode = "manual";
                bool isAmoCrmInitialized = false;
                
                try
                {
                    string settingsPath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsPath))
                    {
                        string json = File.ReadAllText(settingsPath);
                        var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                        authMode = settings?.AmoCrmAuthMode ?? "manual";
                        MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: authMode={authMode}, EnableIntegration={settings?.EnableAmoCrmIntegration ?? false}");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: Error reading settings: {ex.Message}");
                }
                
                // КРИТИЧНО: Проверяем реальный статус AmoCRM сервиса, а не WebRTC/SIP подключение
                if (mainWindow != null)
                {
                    isAmoCrmInitialized = mainWindow.IsAmoCrmServiceInitialized();
                    MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: IsAmoCrmServiceInitialized={isAmoCrmInitialized}");
                    
                    // Для OAuth показываем "Authorized/Not authorized", для Manual Token - "Connected/Not connected"
                    if (isAmoCrmInitialized)
                    {
                        string statusText = authMode == "oauth" ? "Authorized" : "Connected";
                        MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: Setting status to '{statusText}' (AmoCRM initialized)");
                        UpdateAmoCrmStatus(statusText, true);
                    }
                    else
                    {
                        string statusText = authMode == "oauth" ? "Not authorized" : "Not connected";
                        MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: Setting status to '{statusText}' (AmoCRM not initialized)");
                        UpdateAmoCrmStatus(statusText, false);
                    }
                }
                else
                {
                    MainWindow.Log("[SettingsWindow] CheckAmoCrmConnectionStatus: MainWindow is null");
                    try
                    {
                        string settingsPath = AppDataHelper.GetSettingsFilePath();
                        if (File.Exists(settingsPath))
                        {
                            string json = File.ReadAllText(settingsPath);
                            var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                            authMode = settings?.AmoCrmAuthMode ?? "manual";
                        }
                    }
                    catch { }
                    string statusText = authMode == "oauth" ? "Not authorized" : "Not connected";
                    UpdateAmoCrmStatus(statusText, false);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] CheckAmoCrmConnectionStatus: Exception: {ex.Message}");
                string authMode = "manual";
                try
                {
                    string settingsPath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsPath))
                    {
                        string json = File.ReadAllText(settingsPath);
                        var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                        authMode = settings?.AmoCrmAuthMode ?? "manual";
                    }
                }
                catch { }
                string statusText = authMode == "oauth" ? "Not authorized" : "Not connected";
                UpdateAmoCrmStatus(statusText, false);
            }
        }
        
        /// <summary>
        /// Обновляет отображение статуса подключения Kommo (публичный метод для вызова из MainWindow)
        /// </summary>
        public void UpdateAmoCrmStatus(string statusText, bool isConnected)
        {
            // Определяем текущий режим аутентификации из настроек (более надежно, чем из Tag кнопок)
            bool isOAuth = false;
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    isOAuth = settings?.AmoCrmAuthMode == "oauth";
                    MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Determined authMode from settings: {(isOAuth ? "oauth" : "manual")}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Error reading settings, using button tags: {ex.Message}");
                
                // Fallback: проверяем Tag кнопок если настройки не удалось прочитать
                if (AmoCrmAuthModeOAuthButton != null && AmoCrmAuthModeOAuthButton.Tag is string oauthTag)
                {
                    isOAuth = oauthTag == "oauth_selected";
                }
                else if (AmoCrmAuthModeManualButton != null && AmoCrmAuthModeManualButton.Tag is string manualTag)
                {
                    isOAuth = manualTag != "manual_selected";
                }
            }
            
            MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: statusText='{statusText}', isConnected={isConnected}, isOAuth={isOAuth}");
            
            if (isOAuth)
            {
                // Для OAuth режима показываем статус "Authorized/Not authorized"
                // Убеждаемся, что панель OAuth статуса видима
                if (AmoCrmOAuthStatusPanel != null)
                {
                    AmoCrmOAuthStatusPanel.Visibility = Visibility.Visible;
                }
                if (AmoCrmManualTokenStatusPanel != null)
                {
                    AmoCrmManualTokenStatusPanel.Visibility = Visibility.Collapsed;
                }
                
                if (AmoCrmOAuthStatusTextBlock != null)
                {
                    // Обрабатываем как "Authorized", так и "Connected" (для совместимости)
                    if (statusText == "Authorized" || statusText == "Connected")
                    {
                        AmoCrmOAuthStatusTextBlock.Text = "Authorized";
                        AmoCrmOAuthStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                        MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Set OAuth status to 'Authorized' (green)");
                    }
                    else if (statusText == "Connecting..." || statusText == "Authorizing...")
                    {
                        AmoCrmOAuthStatusTextBlock.Text = "Authorizing...";
                        AmoCrmOAuthStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow
                        MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Set OAuth status to 'Authorizing...' (yellow)");
                    }
                    else if (statusText.StartsWith("Error"))
                    {
                        AmoCrmOAuthStatusTextBlock.Text = "Authorization failed";
                        AmoCrmOAuthStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
                        MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Set OAuth status to 'Authorization failed' (red)");
                    }
                    else
                    {
                        AmoCrmOAuthStatusTextBlock.Text = "Not authorized";
                        AmoCrmOAuthStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(106, 106, 106)); // Gray
                        MainWindow.Log($"[SettingsWindow] UpdateAmoCrmStatus: Set OAuth status to 'Not authorized' (gray), statusText was: '{statusText}'");
                    }
                }
                else
                {
                    MainWindow.Log("[SettingsWindow] UpdateAmoCrmStatus: AmoCrmOAuthStatusTextBlock is null!");
                }
            }
            else
            {
                // Для Manual Token режима показываем статус "Connected/Not connected"
                // Убеждаемся, что панель Manual Token статуса видима
                if (AmoCrmManualTokenStatusPanel != null)
                {
                    AmoCrmManualTokenStatusPanel.Visibility = Visibility.Visible;
                }
                if (AmoCrmOAuthStatusPanel != null)
                {
                    AmoCrmOAuthStatusPanel.Visibility = Visibility.Collapsed;
                }
                
                if (AmoCrmStatusTextBlock != null)
                {
                    AmoCrmStatusTextBlock.Text = statusText;
                }
                
                if (AmoCrmStatusIndicator != null)
                {
                    // Green - connected, gray - not connected, yellow - connecting...
                    if (statusText == "Connected")
                    {
                        AmoCrmStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                    }
                    else if (statusText == "Connecting...")
                    {
                        AmoCrmStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Yellow
                    }
                    else if (statusText.StartsWith("Error"))
                    {
                        AmoCrmStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
                    }
                    else
                    {
                        AmoCrmStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(106, 106, 106)); // Gray
                    }
                }
            }
        }
        
        private void UpdateAmoCrmSettingsVisibility(bool isEnabled)
        {
            if (AmoCrmSettingsPanel != null)
            {
                AmoCrmSettingsPanel.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
            }
            if (AmoCrmLeadSelectionGrid != null)
            {
                AmoCrmLeadSelectionGrid.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
            }
            // ...удалено: AmoCrmShowFirstLeadGrid...
        }
        
        private void EnableAmoCrmIntegrationCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            // Показываем настройки при включении
            UpdateAmoCrmSettingsVisibility(true);
            // Сохраняем состояние, но НЕ стираем токен и домен
            SaveAmoCrmIntegrationToggle();
        }
        
        private void EnableAmoCrmIntegrationCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            // Скрываем настройки при выключении
            UpdateAmoCrmSettingsVisibility(false);
            // Сохраняем состояние и отключаем сервис, но НЕ стираем токен и домен
            SaveAmoCrmIntegrationToggle();
        }
        
        /// <summary>
        /// Сохраняет состояние переключателя Kommo интеграции
        /// </summary>
        private void SaveAmoCrmIntegrationToggle()
        {
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                
                if (settings == null)
                {
                    settings = new AppSettings();
                }
                
                // Сохраняем состояние интеграции
                settings.EnableAmoCrmIntegration = EnableAmoCrmIntegrationCheckBox?.IsChecked ?? false;
                // ВАЖНО: не сбрасываем настройку выбора лида, если чекбокс ещё не создан (другая вкладка / ранний вызов)
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                }
                // ...удалено: ShowFirstLeadAfterCallCheckBox/ShowFirstLeadAfterCall...
                
                // Если интеграция выключена, только отключаем сервис (НЕ стираем токен и домен)
                if (!settings.EnableAmoCrmIntegration)
                {
                    // Отключаем сервис в MainWindow
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    if (mainWindow != null)
                    {
                        mainWindow.InitializeAmoCrmService(settings);
                    }
                    
                    // Обновляем статус
                    UpdateAmoCrmStatus("Not connected", false);
                }
                else
                {
                    // Если интеграция включена, проверяем наличие необходимых данных для подключения
                    // Проверяем оба режима аутентификации: manual token и OAuth
                    string authMode = settings.AmoCrmAuthMode ?? "manual";
                    bool hasManualToken = !string.IsNullOrEmpty(settings.AmoCrmSubdomain) && 
                                         !string.IsNullOrEmpty(settings.AmoCrmAccessTokenEncrypted);
                    bool hasOAuth = authMode == "oauth" && 
                                   !string.IsNullOrEmpty(settings.AmoCrmSubdomain) &&
                                   !string.IsNullOrEmpty(settings.AmoCrmClientId) &&
                                   !string.IsNullOrEmpty(settings.AmoCrmClientSecretEncrypted) &&
                                   (!string.IsNullOrEmpty(settings.AmoCrmOAuthAccessTokenEncrypted) || 
                                    !string.IsNullOrEmpty(settings.AmoCrmOAuthRefreshTokenEncrypted));
                    
                    // Если есть домен и хотя бы один из токенов (manual или OAuth), пытаемся подключиться
                    if (!string.IsNullOrEmpty(settings.AmoCrmSubdomain) && (hasManualToken || hasOAuth))
                    {
                        var mainWindow = Application.Current.MainWindow as MainWindow;
                        if (mainWindow != null)
                        {
                            MainWindow.Log($"[SettingsWindow] Initializing AmoCRM service (authMode={authMode}, hasManualToken={hasManualToken}, hasOAuth={hasOAuth})");
                            mainWindow.InitializeAmoCrmService(settings);
                        }
                    }
                    else
                    {
                        MainWindow.Log($"[SettingsWindow] Cannot initialize AmoCRM: missing required credentials (subdomain={!string.IsNullOrEmpty(settings.AmoCrmSubdomain)}, manualToken={hasManualToken}, oauth={hasOAuth})");
                        UpdateAmoCrmStatus("Not configured", false);
                    }
                }
                
                // Сохраняем настройки (токен и домен остаются в файле)
                string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsPath, updatedJson);
                
                MainWindow.Log($"[SettingsWindow] Kommo integration toggle saved: {settings.EnableAmoCrmIntegration}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error saving Kommo integration toggle: {ex.Message}");
            }
        }
        
        private void AmoCrmAccessTokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            // Можно добавить валидацию токена здесь, если нужно
        }
        
        /// <summary>
        /// Обработчик переключения режима аутентификации (современный сегментированный контрол)
        /// </summary>
        private void AmoCrmAuthModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string mode)
            {
                // Определяем выбранный режим: если нажата OAuth кнопка, то OAuth, иначе Manual Token
                bool isOAuth = (button == AmoCrmAuthModeOAuthButton);
                
                // Обновляем визуальное состояние кнопок
                var accentBlueBrush = (Brush)FindResource("AccentBlueBrush");
                var textPrimaryBrush = (Brush)FindResource("TextPrimaryBrush");
                
                if (AmoCrmAuthModeManualButton != null)
                {
                    AmoCrmAuthModeManualButton.Tag = isOAuth ? "manual" : "manual_selected";
                    AmoCrmAuthModeManualButton.Background = isOAuth ? Brushes.Transparent : accentBlueBrush;
                    AmoCrmAuthModeManualButton.Foreground = isOAuth ? textPrimaryBrush : Brushes.White;
                }
                
                if (AmoCrmAuthModeOAuthButton != null)
                {
                    AmoCrmAuthModeOAuthButton.Tag = isOAuth ? "oauth_selected" : "oauth";
                    AmoCrmAuthModeOAuthButton.Background = isOAuth ? accentBlueBrush : Brushes.Transparent;
                    AmoCrmAuthModeOAuthButton.Foreground = isOAuth ? Brushes.White : textPrimaryBrush;
                }
                
                // Обновляем видимость панелей и статусов
                UpdateAmoCrmAuthModeVisibility(isOAuth);
                
                // Отключаем сервис для неактивного режима и подключаем для активного
                DisconnectInactiveAuthMode(isOAuth);
            }
        }
        
        /// <summary>
        /// Отключает сервис для неактивного режима аутентификации
        /// </summary>
        private void DisconnectInactiveAuthMode(bool isOAuth)
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow == null) return;
            
            // Отключаем текущий сервис
            mainWindow.DisconnectAmoCrmService();
            
            // Загружаем настройки и подключаем только активный режим
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null && settings.EnableAmoCrmIntegration)
                    {
                        // Обновляем режим в настройках
                        settings.AmoCrmAuthMode = isOAuth ? "oauth" : "manual";
                        
                        // Сохраняем изменения
                        string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                        File.WriteAllText(settingsPath, updatedJson);
                        
                        // Подключаем только активный режим
                        mainWindow.InitializeAmoCrmService(settings);
                        
                        // Обновляем статус через небольшую задержку
                        _ = Task.Delay(2000).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                CheckAmoCrmConnectionStatus();
                            });
                        }, TaskContinuationOptions.OnlyOnRanToCompletion);
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error switching auth mode: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Обновляет видимость панелей в зависимости от режима аутентификации
        /// </summary>
        private void UpdateAmoCrmAuthModeVisibility(bool isOAuth)
        {
            // Subdomain поле показывается ВСЕГДА (нужен для обоих режимов)
            if (AmoCrmSubdomainPanel != null)
            {
                AmoCrmSubdomainPanel.Visibility = Visibility.Visible;
            }
            
            if (AmoCrmManualTokenPanel != null)
            {
                AmoCrmManualTokenPanel.Visibility = isOAuth ? Visibility.Collapsed : Visibility.Visible;
            }
            if (AmoCrmOAuthPanel != null)
            {
                AmoCrmOAuthPanel.Visibility = isOAuth ? Visibility.Visible : Visibility.Collapsed;
            }
            
            // Кнопки Save + Clear показываются только для Manual Token режима
            if (AmoCrmManualTokenButtonsPanel != null)
            {
                AmoCrmManualTokenButtonsPanel.Visibility = isOAuth ? Visibility.Collapsed : Visibility.Visible;
            }
            
            // Authorize + Clear кнопки управляются видимостью AmoCrmOAuthPanel (внутри него)
            
            // Статусы: Manual Token показывает "Connected/Not connected", OAuth показывает "Authorized/Not authorized"
            if (AmoCrmManualTokenStatusPanel != null)
            {
                AmoCrmManualTokenStatusPanel.Visibility = isOAuth ? Visibility.Collapsed : Visibility.Visible;
            }
            if (AmoCrmOAuthStatusPanel != null)
            {
                AmoCrmOAuthStatusPanel.Visibility = isOAuth ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        
        /// <summary>
        /// Обработчик кнопки "Authorize with Kommo"
        /// </summary>
        private async void AmoCrmAuthorizeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AmoCrmSubdomainTextBox == null || string.IsNullOrWhiteSpace(AmoCrmSubdomainTextBox.Text))
                {
                    CustomMessageBox.Show("Please enter Kommo subdomain first.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (AmoCrmClientIdTextBox == null || string.IsNullOrWhiteSpace(AmoCrmClientIdTextBox.Text))
                {
                    CustomMessageBox.Show("Please enter Client ID.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                if (AmoCrmClientSecretPasswordBox == null || string.IsNullOrWhiteSpace(AmoCrmClientSecretPasswordBox.Password))
                {
                    CustomMessageBox.Show("Please enter Client Secret.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                
                string redirectUri = AmoCrmRedirectUriTextBox?.Text?.Trim() ?? "http://localhost:8080/callback";
                if (string.IsNullOrWhiteSpace(redirectUri))
                {
                    redirectUri = "http://localhost:8080/callback";
                }
                
                // Нормализуем Redirect URI - должен быть полный путь с портом
                if (redirectUri == "http://localhost" || redirectUri == "http://localhost/")
                {
                    redirectUri = "http://localhost:8080/callback";
                    if (AmoCrmRedirectUriTextBox != null)
                    {
                        AmoCrmRedirectUriTextBox.Text = redirectUri;
                    }
                }
                
                // Убеждаемся, что есть путь (если только домен и порт)
                if (!redirectUri.Contains("/", StringComparison.Ordinal) || redirectUri.EndsWith(":8080", StringComparison.OrdinalIgnoreCase))
                {
                    redirectUri = redirectUri.TrimEnd('/') + "/callback";
                    if (AmoCrmRedirectUriTextBox != null)
                    {
                        AmoCrmRedirectUriTextBox.Text = redirectUri;
                    }
                }
                
                MainWindow.Log("[SettingsWindow] Starting OAuth authorization...");
                
                // КРИТИЧНО: Захватываем ВСЕ значения из UI-элементов ДО ConfigureAwait(false),
                // потому что после ConfigureAwait(false) мы можем оказаться на фоновом потоке
                // и доступ к UI-элементам вызовет InvalidOperationException.
                string clientIdValue = AmoCrmClientIdTextBox.Text.Trim();
                string clientSecretValue = AmoCrmClientSecretPasswordBox.Password;
                
                // Отключаем кнопку на время авторизации
                if (AmoCrmAuthorizeButton != null)
                {
                    AmoCrmAuthorizeButton.IsEnabled = false;
                    AmoCrmAuthorizeButton.Content = "Authorizing...";
                }
                
                // Запускаем OAuth flow (асинхронно, не блокируя UI)
                var oauthService = new AmoCrmOAuthService();
                // Для OAuth subdomain не нужен для авторизации (используется единый www.amocrm.ru/oauth)
                // Subdomain будет извлечен из referer после авторизации
                var (code, referer) = await oauthService.AuthorizeAsync(
                    string.Empty, // Subdomain не нужен для OAuth авторизации
                    clientIdValue,
                    redirectUri
                ).ConfigureAwait(false);
                
                if (string.IsNullOrEmpty(code))
                {
                    MainWindow.Log("[SettingsWindow] OAuth authorization failed or was cancelled");
                    
                    // Обновляем UI асинхронно, не блокируя
                    await Dispatcher.InvokeAsync(() =>
                    {
                        CustomMessageBox.Show("Authorization failed or was cancelled. Please try again.", "Authorization Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                        
                        if (AmoCrmAuthorizeButton != null)
                        {
                            AmoCrmAuthorizeButton.IsEnabled = true;
                            AmoCrmAuthorizeButton.Content = "Authorize with Kommo";
                        }
                    });
                    return;
                }
                
                MainWindow.Log($"[SettingsWindow] Authorization code received, exchanging for tokens...");
                
                // Обмениваем code на токены
                // Для OAuth subdomain извлекается из referer (обязательный параметр после авторизации)
                if (string.IsNullOrEmpty(referer))
                {
                    MainWindow.Log("[SettingsWindow] Referer not found in OAuth response - cannot determine subdomain");
                    
                    // Обновляем UI асинхронно, не блокируя
                    await Dispatcher.InvokeAsync(() =>
                    {
                        CustomMessageBox.Show("Failed to determine your Kommo subdomain from authorization response. Please try again.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        
                        if (AmoCrmAuthorizeButton != null)
                        {
                            AmoCrmAuthorizeButton.IsEnabled = true;
                            AmoCrmAuthorizeButton.Content = "Authorize with Kommo";
                        }
                    });
                    return;
                }
                
                // Извлекаем subdomain из referer
                // referer может быть в формате "https://mdkb.amocrm.ru" или "mdkb.amocrm.ru"
                string refererDomain = referer.Replace("https://", "").Replace("http://", "").Trim();
                string subdomainForTokenExchange;
                
                if (refererDomain.Contains("."))
                {
                    subdomainForTokenExchange = refererDomain.Split('.')[0];
                }
                else
                {
                    subdomainForTokenExchange = refererDomain;
                }
                
                MainWindow.Log($"[SettingsWindow] Extracted subdomain from referer: {subdomainForTokenExchange}");
                
                var (accessToken, refreshToken, expiresIn) = await oauthService.ExchangeCodeForTokensAsync(
                    subdomainForTokenExchange,
                    clientIdValue,
                    clientSecretValue,
                    code,
                    redirectUri
                ).ConfigureAwait(false);
                
                if (string.IsNullOrEmpty(accessToken))
                {
                    MainWindow.Log("[SettingsWindow] Failed to exchange authorization code for tokens");
                    
                    // Обновляем UI асинхронно, не блокируя
                    await Dispatcher.InvokeAsync(() =>
                    {
                        CustomMessageBox.Show("Failed to get access token. Please check your Client ID and Client Secret.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        
                        if (AmoCrmAuthorizeButton != null)
                        {
                            AmoCrmAuthorizeButton.IsEnabled = true;
                            AmoCrmAuthorizeButton.Content = "Authorize with Kommo";
                        }
                    });
                    return;
                }
                
                // Используем значения, захваченные из UI до ConfigureAwait(false)
                string subdomainToSave = subdomainForTokenExchange;
                string clientIdToSave = clientIdValue;
                string clientSecretToSave = clientSecretValue;
                
                // Сохраняем OAuth токены в настройки (в фоне, чтобы не блокировать UI)
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                
                // Читаем настройки асинхронно (не блокируя UI)
                await Task.Run(() =>
                {
                    if (File.Exists(settingsPath))
                    {
                        string json = File.ReadAllText(settingsPath);
                        settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    }
                    
                    if (settings == null)
                    {
                        settings = new AppSettings();
                    }
                }).ConfigureAwait(false);
                
                // Обновляем настройки OAuth
                // Subdomain сохраняется из referer (извлечен выше)
                if (settings == null)
                {
                    settings = new AppSettings();
                }
                
                // КРИТИЧНО: Убеждаемся, что интеграция включена
                settings.EnableAmoCrmIntegration = true;
                settings.AmoCrmSubdomain = subdomainToSave;
                settings.AmoCrmAuthMode = "oauth";
                settings.AmoCrmClientId = clientIdToSave;
                settings.AmoCrmClientSecretEncrypted = TokenEncryption.Encrypt(clientSecretToSave);
                settings.AmoCrmRedirectUri = redirectUri;
                settings.AmoCrmOAuthAccessTokenEncrypted = TokenEncryption.Encrypt(accessToken);
                settings.AmoCrmOAuthRefreshTokenEncrypted = refreshToken != null ? TokenEncryption.Encrypt(refreshToken) : null;
                if (expiresIn.HasValue)
                {
                    settings.AmoCrmOAuthTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn.Value);
                }
                
                // Сохраняем файл асинхронно, чтобы не блокировать UI
                await Task.Run(() =>
                {
                    try
                    {
                        string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                        File.WriteAllText(settingsPath, updatedJson);
                        MainWindow.Log($"[SettingsWindow] OAuth tokens saved to file: {settingsPath}");
                        MainWindow.Log($"[SettingsWindow] Access token encrypted: {!string.IsNullOrEmpty(settings.AmoCrmOAuthAccessTokenEncrypted)}");
                        MainWindow.Log($"[SettingsWindow] Refresh token encrypted: {!string.IsNullOrEmpty(settings.AmoCrmOAuthRefreshTokenEncrypted)}");
                        MainWindow.Log($"[SettingsWindow] Token expires at: {settings.AmoCrmOAuthTokenExpiresAt}");
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[SettingsWindow] Error saving OAuth tokens to file: {ex.Message}");
                        throw;
                    }
                }).ConfigureAwait(false);
                
                MainWindow.Log("[SettingsWindow] OAuth tokens saved successfully");
                
                // Обновляем UI в UI потоке асинхронно, не блокируя
                await Dispatcher.InvokeAsync(() =>
                {
                    // Обновляем статус OAuth (через UpdateAmoCrmStatus для правильного отображения)
                    UpdateAmoCrmStatus("Connected", true);
                    
                    if (AmoCrmAuthorizeButton != null)
                    {
                        AmoCrmAuthorizeButton.IsEnabled = true;
                        AmoCrmAuthorizeButton.Content = "Authorize with Kommo";
                    }
                });
                
                // Инициализируем Kommo сервис с OAuth токенами (в UI потоке, т.к. может обращаться к UI)
                await Dispatcher.InvokeAsync(() =>
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    if (mainWindow != null && settings != null)
                    {
                        mainWindow.InitializeAmoCrmService(settings);
                    }
                });
                
                // Проверяем статус через небольшую задержку (InvokeAsync — не блокирует фоновый поток)
                _ = Task.Delay(2000).ContinueWith(t =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        CheckAmoCrmConnectionStatus();
                    });
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
                
                // Выводим SettingsWindow на передний план, чтобы пользователь увидел обновлённый статус.
                // Модальный диалог (CustomMessageBox) НЕ используем — он появляется ЗА окном браузера
                // и блокирует интерфейс, пока пользователь не закроет браузер и не нажмёт OK.
                await Dispatcher.InvokeAsync(() =>
                {
                    this.Activate();
                    this.Topmost = true;
                    this.Topmost = false;
                    this.Focus();
                    MainWindow.Log("[SettingsWindow] OAuth authorization completed, window activated");
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error during OAuth authorization: {ex.Message}");
                MainWindow.Log($"[SettingsWindow] Stack trace: {ex.StackTrace}");
                
                // Обновляем UI асинхронно, не блокируя
                await Dispatcher.InvokeAsync(() =>
                {
                    CustomMessageBox.Show($"Error during authorization: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    
                    if (AmoCrmAuthorizeButton != null)
                    {
                        AmoCrmAuthorizeButton.IsEnabled = true;
                        AmoCrmAuthorizeButton.Content = "Authorize with Kommo";
                    }
                });
            }
        }
        
        private void SaveAmoCrmSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                
                if (settings == null)
                {
                    settings = new AppSettings();
                }
                
                // Убеждаемся, что интеграция включена при сохранении настроек
                settings.EnableAmoCrmIntegration = true;
                if (EnableAmoCrmIntegrationCheckBox != null)
                {
                    EnableAmoCrmIntegrationCheckBox.IsChecked = true;
                }
                // ВАЖНО: не сбрасываем настройку выбора лида, если чекбокс ещё не создан
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                }
                
                // Сохраняем настройки Kommo
                settings.AmoCrmSubdomain = AmoCrmSubdomainTextBox?.Text?.Trim();
                
                // Определяем режим аутентификации из состояния кнопок
                bool isOAuth = false;
                if (AmoCrmAuthModeOAuthButton != null && AmoCrmAuthModeOAuthButton.Tag is string oauthTag)
                {
                    isOAuth = oauthTag == "oauth_selected";
                }
                else if (AmoCrmAuthModeManualButton != null && AmoCrmAuthModeManualButton.Tag is string manualTag)
                {
                    isOAuth = manualTag != "manual_selected";
                }
                else
                {
                    // Fallback: проверяем текущее состояние кнопок по Background
                    if (AmoCrmAuthModeOAuthButton != null && AmoCrmAuthModeOAuthButton.Background != Brushes.Transparent)
                    {
                        isOAuth = true;
                    }
                }
                settings.AmoCrmAuthMode = isOAuth ? "oauth" : "manual";
                
                if (isOAuth)
                {
                    // Сохраняем OAuth настройки
                    settings.AmoCrmClientId = AmoCrmClientIdTextBox?.Text?.Trim();
                    if (AmoCrmClientSecretPasswordBox != null && !string.IsNullOrEmpty(AmoCrmClientSecretPasswordBox.Password))
                    {
                        settings.AmoCrmClientSecretEncrypted = TokenEncryption.Encrypt(AmoCrmClientSecretPasswordBox.Password);
                    }
                    settings.AmoCrmRedirectUri = AmoCrmRedirectUriTextBox?.Text?.Trim() ?? "http://localhost:8080/callback";
                    
                    // OAuth токены сохраняются через кнопку Authorize, здесь не трогаем их
                }
                else
                {
                    // Сохраняем Manual Token
                    if (AmoCrmAccessTokenPasswordBox != null && !string.IsNullOrEmpty(AmoCrmAccessTokenPasswordBox.Password))
                    {
                        settings.AmoCrmAccessTokenEncrypted = TokenEncryption.Encrypt(AmoCrmAccessTokenPasswordBox.Password);
                    }
                    else
                    {
                        // Если токен не указан, очищаем зашифрованный токен
                        settings.AmoCrmAccessTokenEncrypted = null;
                    }
                }
                
                // Сохраняем настройки
                string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsPath, updatedJson);
                
                MainWindow.Log("[SettingsWindow] Kommo settings saved successfully");
                
                // Обновляем статус на "Connecting..."
                UpdateAmoCrmStatus("Connecting...", false);
                
                // Переинициализируем Kommo сервис в MainWindow (асинхронно, не блокирует UI)
                // Получаем ссылку на MainWindow через Application
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    // Инициализация выполняется асинхронно в фоне, не блокирует UI
                    mainWindow.InitializeAmoCrmService(settings);
                    
                    // Проверяем статус через небольшую задержку (после начала инициализации)
                    _ = Task.Delay(2000).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            CheckAmoCrmConnectionStatus();
                        });
                    }, TaskContinuationOptions.OnlyOnRanToCompletion);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error saving Kommo settings: {ex.Message}");
                CustomMessageBox.Show($"Error saving Kommo settings:\n\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }
        
        /// <summary>
        /// Очищает настройки Kommo (токен и домен) из UI и файла settings.json
        /// </summary>
        private void ClearAmoCrmSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Подтверждение удаления
                var result = CustomMessageBox.Show(
                    "Are you sure you want to clear all Kommo settings?\n\nThis will remove the domain and access token from both the UI and settings file.",
                    "Clear Kommo Settings",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    this);
                
                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
                
                // Очищаем UI поля
                if (AmoCrmSubdomainTextBox != null)
                {
                    AmoCrmSubdomainTextBox.Text = string.Empty;
                }
                
                if (AmoCrmAccessTokenPasswordBox != null)
                {
                    AmoCrmAccessTokenPasswordBox.Password = string.Empty;
                }
                
                // Очищаем OAuth поля
                if (AmoCrmClientIdTextBox != null)
                {
                    AmoCrmClientIdTextBox.Text = string.Empty;
                }
                if (AmoCrmClientSecretPasswordBox != null)
                {
                    AmoCrmClientSecretPasswordBox.Password = string.Empty;
                }
                if (AmoCrmRedirectUriTextBox != null)
                {
                    AmoCrmRedirectUriTextBox.Text = "http://localhost:8080/callback";
                }
                if (AmoCrmOAuthStatusTextBlock != null)
                {
                    AmoCrmOAuthStatusTextBlock.Visibility = Visibility.Collapsed;
                }
                
                // Переключаем на Manual Token режим
                var accentBlueBrush = (Brush)FindResource("AccentBlueBrush");
                var textPrimaryBrush = (Brush)FindResource("TextPrimaryBrush");
                
                if (AmoCrmAuthModeManualButton != null)
                {
                    AmoCrmAuthModeManualButton.Tag = "manual_selected";
                    AmoCrmAuthModeManualButton.Background = accentBlueBrush;
                    AmoCrmAuthModeManualButton.Foreground = Brushes.White;
                }
                if (AmoCrmAuthModeOAuthButton != null)
                {
                    AmoCrmAuthModeOAuthButton.Tag = "oauth";
                    AmoCrmAuthModeOAuthButton.Background = Brushes.Transparent;
                    AmoCrmAuthModeOAuthButton.Foreground = textPrimaryBrush;
                }
                UpdateAmoCrmAuthModeVisibility(false);
                
                // Отключаем OAuth сервис при переключении на Manual Token
                DisconnectInactiveAuthMode(false);
                
                // Очищаем настройки в файле
                string settingsPath = AppDataHelper.GetSettingsFilePath();
                AppSettings? settings = null;
                
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                
                if (settings == null)
                {
                    settings = new AppSettings();
                }
                
                // Очищаем все AmoCRM настройки
                settings.AmoCrmSubdomain = null;
                settings.AmoCrmAccessTokenEncrypted = null;
                settings.AmoCrmAuthMode = "manual";
                settings.AmoCrmClientId = null;
                settings.AmoCrmClientSecretEncrypted = null;
                settings.AmoCrmRedirectUri = null;
                settings.AmoCrmOAuthAccessTokenEncrypted = null;
                settings.AmoCrmOAuthRefreshTokenEncrypted = null;
                settings.AmoCrmOAuthTokenExpiresAt = null;
                settings.EnableAmoCrmIntegration = false;
                
                if (EnableAmoCrmIntegrationCheckBox != null)
                {
                    EnableAmoCrmIntegrationCheckBox.IsChecked = false;
                    UpdateAmoCrmSettingsVisibility(false);
                }
                
                // Сохраняем изменения
                string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsPath, updatedJson);
                
                MainWindow.Log("[SettingsWindow] Kommo settings cleared");
                
                // Отключаем Kommo сервис
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.DisconnectAmoCrmService();
                }
                
                UpdateAmoCrmStatus("Not connected", false);
                
                if (File.Exists(settingsPath))
                {
                    string json = File.ReadAllText(settingsPath);
                    settings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                
                if (settings == null)
                {
                    settings = new AppSettings();
                }
                
                // Очищаем токен и домен
                settings.AmoCrmSubdomain = null;
                settings.AmoCrmAccessTokenEncrypted = null;
                // Выключаем интеграцию
                settings.EnableAmoCrmIntegration = false;
                
                // Обновляем UI
                if (EnableAmoCrmIntegrationCheckBox != null)
                {
                    EnableAmoCrmIntegrationCheckBox.IsChecked = false;
                }
                UpdateAmoCrmSettingsVisibility(false);
                
                // Отключаем сервис в MainWindow
                var mainWindowDisconnect = Application.Current.MainWindow as MainWindow;
                if (mainWindowDisconnect != null)
                {
                    mainWindowDisconnect.DisconnectAmoCrmService();
                }
                
                // Обновляем статус
                UpdateAmoCrmStatus("Not connected", false);
                
                // Сохраняем настройку выбора лида Kommo (если была установлена ранее)
                // При очистке настроек мы не сбрасываем эту настройку, так как она не связана с токеном/доменом
                // Но если чекбокс доступен, сохраняем его текущее состояние
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                }
                
                // Сохраняем настройки
                string clearedSettingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(settingsPath, clearedSettingsJson);
                
                MainWindow.Log("[SettingsWindow] AmoCRM settings cleared successfully");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error clearing Kommo settings: {ex.Message}");
                CustomMessageBox.Show($"Error clearing Kommo settings:\n\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }

        private void LoadAppearanceSettings()
        {
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);

                    if (ThemeModeComboBox != null)
                    {
                        var mode = ThemeService.ParseMode(settings?.ThemeMode);
                        ThemeModeComboBox.SelectedIndex = mode switch
                        {
                            ThemeMode.Dark => 1,
                            ThemeMode.Light => 2,
                            _ => 0
                        };
                    }
                }
                else
                {
                    if (ThemeModeComboBox != null)
                    {
                        ThemeModeComboBox.SelectedIndex = 0; // system
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadAppearanceSettings: Error - {ex.Message}");
            }
        }

        private void ApplyThemeButton_Click(object sender, RoutedEventArgs e)
        {
            if (ThemeModeComboBox == null) return;

            try
            {
                var selected = (ThemeModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
                var mode = ThemeService.ParseMode(selected);
                ThemeService.SetConfiguredMode(mode);
                
                CustomMessageBox.Show("Theme applied successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information, this);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error applying theme: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }
        
        private void EnableCallRecordingCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressRecordingToggleEvent) return;

            // Recording is supported only in WebRTC mode
            try
            {
                if (File.Exists(AppDataHelper.GetSettingsFilePath()))
                {
                    string json = File.ReadAllText(AppDataHelper.GetSettingsFilePath());
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    bool useWebRtc = settings?.UseWebRtcAudio ?? false;

                    if (!useWebRtc)
                    {
                        CustomMessageBox.Show(
                            "Call recording is available in WebRTC mode.",
                            "Recording Not Available",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            this);

                        _suppressRecordingToggleEvent = true;
                        EnableCallRecordingCheckBox.IsChecked = false;
                        UpdateCallRecordingToggleColor();
                        _suppressRecordingToggleEvent = false;
                        return;
                    }
                }
            }
            catch
            {
                // If we can't read settings, fail safe: don't enable.
                _suppressRecordingToggleEvent = true;
                EnableCallRecordingCheckBox.IsChecked = false;
                UpdateCallRecordingToggleColor();
                _suppressRecordingToggleEvent = false;
                return;
            }

            UpdateCallRecordingToggleColor();
            SaveCallRecordingSetting();
        }
        
        private void EnableCallRecordingCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressRecordingToggleEvent) return;
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
                
                // Сохраняем настройку выбора лида AmoCRM
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
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

        private void IntegrationsButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(IntegrationsButton);
            ShowView(IntegrationsSettingsView);
            LoadIntegrationsSettings();
            
            // Проверяем статус с задержками, чтобы дать время сервису инициализироваться
            // (если окно открывается сразу после запуска приложения)
            // Первая проверка через 500мс
            _ = Task.Delay(500).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    MainWindow.Log("[SettingsWindow] IntegrationsButton_Click: First status check (500ms delay)");
                    CheckAmoCrmConnectionStatus();
                });
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
            
            // Вторая проверка через 2 секунды (на случай если сервис инициализируется дольше)
            _ = Task.Delay(2000).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    MainWindow.Log("[SettingsWindow] IntegrationsButton_Click: Second status check (2000ms delay)");
                    CheckAmoCrmConnectionStatus();
                });
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateButtonSelection(AboutButton);
            ShowView(AboutSettingsView);
            
            // Загружаем текущую версию приложения
            string currentVersion = UpdateService.GetCurrentVersion();
            VersionTextBlock.Text = $"Version: {currentVersion}";
        }
        
        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CheckForUpdatesButton.IsEnabled = false;
                CheckForUpdatesButton.Content = "Checking...";
                
                MainWindow.Log("[SettingsWindow] Manual update check initiated");
                
                // Используем новый сервис обновлений через собственный сервер
                // forceCheck = true, чтобы проверить независимо от времени последней проверки
                var updateInfo = await UpdateService.CheckForUpdateAsync(forceCheck: true);
                
                if (updateInfo != null)
                {
                    // Новая версия доступна
                    string currentVersion = UpdateService.GetCurrentVersion();
                    var updateWindow = new UpdateAvailableWindow(updateInfo, currentVersion)
                    {
                        Owner = this
                    };
                    updateWindow.ShowDialog();
                }
                else
                {
                    string currentVersion = UpdateService.GetCurrentVersion();
                    CustomMessageBox.Show($"You are using the latest version ({currentVersion}).", 
                        "No Updates Available", MessageBoxButton.OK, MessageBoxImage.Information, this);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SettingsWindow] Error checking for updates: {ex.Message}");
                CustomMessageBox.Show(
                    $"Could not check for updates:\n\n{ex.Message}\n\nPlease check your internet connection and try again.",
                    "Update Check Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    this);
            }
            finally
            {
                CheckForUpdatesButton.IsEnabled = true;
                CheckForUpdatesButton.Content = "Check for Updates";
            }
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
                    string? sipPassword = settings != null ? SipPasswordProvider.GetPassword(settings) : null;
                    bool canTest = !string.IsNullOrWhiteSpace(wsUri) && 
                                   wsUri != "wss://pbx.example.com:8089/ws" &&
                                   settings != null && 
                                   !string.IsNullOrWhiteSpace(settings.SipUsername) && 
                                   !string.IsNullOrWhiteSpace(sipPassword);
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
                
                string? sipPasswordForConfig = settings != null ? SipPasswordProvider.GetPassword(settings) : null;
                if (settings == null || string.IsNullOrWhiteSpace(settings.SipUsername) || string.IsNullOrWhiteSpace(sipPasswordForConfig))
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
                    SipPassword = sipPasswordForConfig,
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
                    Password = SipPasswordProvider.GetPassword(settings) ?? "",
                    // Используем тот же флаг, что и основное приложение
                    EnableDebug = settings?.EnableWebRtcDebug ?? true
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

                // IMPORTANT: Recording is available only in WebRTC mode.
                // When WebRTC is disabled, ensure recording is disabled and the UI toggle is reset.
                if (!settings.UseWebRtcAudio)
                {
                    settings.EnableCallRecording = false;
                    _suppressRecordingToggleEvent = true;
                    if (EnableCallRecordingCheckBox != null)
                    {
                        EnableCallRecordingCheckBox.IsChecked = false;
                        UpdateCallRecordingToggleColor();
                    }
                    _suppressRecordingToggleEvent = false;
                }
                
                // ВАЖНО: Сохраняем настройку выбора лида AmoCRM, чтобы она не терялась при сохранении общих настроек
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                }
                
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

        private void DisableCallRecordingBecauseWebRtcIsOff()
        {
            try
            {
                // Update UI toggle (if available)
                if (EnableCallRecordingCheckBox != null && EnableCallRecordingCheckBox.IsChecked == true)
                {
                    _suppressRecordingToggleEvent = true;
                    EnableCallRecordingCheckBox.IsChecked = false;
                    UpdateCallRecordingToggleColor();
                    _suppressRecordingToggleEvent = false;
                }

                // Persist setting off
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

                if (settings.EnableCallRecording)
                {
                    settings.EnableCallRecording = false;
                    // Сохраняем настройку выбора лида AmoCRM
                    if (EnableAmoCrmLeadSelectionCheckBox != null)
                    {
                        settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                    }
                    string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                    File.WriteAllText(AppDataHelper.GetSettingsFilePath(), settingsJson);
                }
            }
            catch
            {
                // best-effort; ignore
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
                
                string? sipPassword = SipPasswordProvider.GetPassword(settings);
                if (settings == null || string.IsNullOrWhiteSpace(settings.SipUsername) || string.IsNullOrWhiteSpace(sipPassword))
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
                    Password = sipPassword ?? ""
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
                    
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null)
                    {
                        // Migrate legacy plaintext password to encrypted (best-effort)
                        SipPasswordProvider.MigratePlaintextToEncryptedIfNeeded(settingsPath, settings);
                        
                        MainWindow.Log($"[SettingsWindow] LoadSettings: Settings loaded - SipServer='{settings.SipServer}', SipUsername='{settings.SipUsername}', SipPasswordEncrypted={(string.IsNullOrEmpty(settings.SipPasswordEncrypted) ? "empty" : "set")}");
                        
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
                        string finalPassword = SipPasswordProvider.GetPassword(settings) ?? "";
                        
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
                settings.SipPasswordEncrypted = TokenEncryption.Encrypt(password);
                settings.SipPassword = null; // do not persist plaintext
                
                // Сохраняем настройку выбора лида AmoCRM
                if (EnableAmoCrmLeadSelectionCheckBox != null)
                {
                    settings.EnableAmoCrmLeadSelection = EnableAmoCrmLeadSelectionCheckBox.IsChecked ?? false;
                }

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
                    if (RingtoneDeviceComboBox != null) RingtoneDeviceComboBox.IsEnabled = false;
                    // Показываем placeholder во время загрузки
                    var loadingPlaceholder = new List<AudioDeviceInfo> { new AudioDeviceInfo { Name = "Loading...", DeviceNumber = -1 } };
                    MicrophoneComboBox.ItemsSource = loadingPlaceholder;
                    SpeakerComboBox.ItemsSource = loadingPlaceholder;
                });
                
                // Загружаем устройства в фоновом потоке параллельно
                var microphonesTask = System.Threading.Tasks.Task.Run(() => AudioDeviceHelper.GetMicrophones());
                var speakersTask = System.Threading.Tasks.Task.Run(() => AudioDeviceHelper.GetSpeakers());
                var ringtoneDevicesTask = System.Threading.Tasks.Task.Run(() => RingtoneDeviceHelper.GetOutputDevices());
                
                // Ждем завершения обеих задач
                await System.Threading.Tasks.Task.WhenAll(microphonesTask, speakersTask, ringtoneDevicesTask);
                
                var microphones = await microphonesTask;
                var speakers = await speakersTask;
                var ringtoneDevices = await ringtoneDevicesTask;
                
                // Обновляем UI в UI потоке
                Dispatcher.Invoke(() =>
                {
                    MicrophoneComboBox.ItemsSource = microphones;
                    SpeakerComboBox.ItemsSource = speakers;
                    MicrophoneComboBox.IsEnabled = true;
                    SpeakerComboBox.IsEnabled = true;

                    if (RingtoneDeviceComboBox != null)
                    {
                        RingtoneDeviceComboBox.ItemsSource = ringtoneDevices;
                        RingtoneDeviceComboBox.IsEnabled = true;
                    }
                    
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
                    if (RingtoneDeviceComboBox != null) RingtoneDeviceComboBox.IsEnabled = true;
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

                        // Ringtone settings
                        if (RingtoneSoundComboBox != null)
                        {
                            string mode = (settings.RingtoneSoundMode ?? "wav").Trim().ToLowerInvariant();
                            foreach (ComboBoxItem item in RingtoneSoundComboBox.Items)
                            {
                                if ((item.Tag?.ToString() ?? "") == mode)
                                {
                                    RingtoneSoundComboBox.SelectedItem = item;
                                    break;
                                }
                            }
                        }

                        if (RingtoneDeviceComboBox?.ItemsSource != null)
                        {
                            string selectedId = settings.RingtoneOutputDeviceId ?? "";
                            foreach (RingtoneOutputDeviceInfo device in RingtoneDeviceComboBox.ItemsSource)
                            {
                                if ((device.Id ?? "") == selectedId)
                                {
                                    RingtoneDeviceComboBox.SelectedItem = device;
                                    break;
                                }
                            }
                            if (RingtoneDeviceComboBox.SelectedItem == null && RingtoneDeviceComboBox.Items.Count > 0)
                            {
                                RingtoneDeviceComboBox.SelectedIndex = 0; // default
                            }
                        }

                        if (RingtoneVolumeSlider != null)
                        {
                            double v = settings.RingtoneVolume;
                            if (v < 0) v = 0;
                            if (v > 1) v = 1;
                            RingtoneVolumeSlider.Value = v * 100.0;
                        }
                        if (RingtoneVolumeValueTextBlock != null && RingtoneVolumeSlider != null)
                        {
                            RingtoneVolumeValueTextBlock.Text = $"{(int)Math.Round(RingtoneVolumeSlider.Value)}%";
                        }

                        ApplyRingtoneSettingsLive(settings);
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

                    if (RingtoneDeviceComboBox?.ItemsSource != null && RingtoneDeviceComboBox.Items.Count > 0)
                    {
                        RingtoneDeviceComboBox.SelectedIndex = 0;
                    }
                    if (RingtoneVolumeSlider != null)
                    {
                        RingtoneVolumeSlider.Value = 70;
                    }
                    if (RingtoneVolumeValueTextBlock != null)
                    {
                        RingtoneVolumeValueTextBlock.Text = "70%";
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

                // Ringtone settings
                if (RingtoneDeviceComboBox?.SelectedItem is RingtoneOutputDeviceInfo ringDev)
                {
                    settings.RingtoneOutputDeviceId = string.IsNullOrWhiteSpace(ringDev.Id) ? null : ringDev.Id;
                }
                if (RingtoneVolumeSlider != null)
                {
                    settings.RingtoneVolume = Math.Max(0.0, Math.Min(1.0, RingtoneVolumeSlider.Value / 100.0));
                }
                if (RingtoneSoundComboBox?.SelectedItem is ComboBoxItem soundItem && soundItem.Tag != null)
                {
                    settings.RingtoneSoundMode = soundItem.Tag.ToString() ?? "wav";
                }

                // Сохраняем в файл
                string settingsJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(AppDataHelper.GetSettingsFilePath(), settingsJson);

                ApplyRingtoneSettingsLive(settings);

                CustomMessageBox.Show("Audio settings saved!", "Success", 
                    MessageBoxButton.OK, MessageBoxImage.Information, this);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error saving audio settings: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }

        private void ApplyRingtoneSettingsLive(AppSettings settings)
        {
            try
            {
                float volume = (float)Math.Max(0.0, Math.Min(1.0, settings.RingtoneVolume));
                RingtoneService.Instance.Configure(settings.RingtoneOutputDeviceId, volume, settings.RingtoneSoundMode);
            }
            catch
            {
                // best-effort
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

        private void RingtoneDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string? id = (RingtoneDeviceComboBox?.SelectedItem as RingtoneOutputDeviceInfo)?.Id;
                float volume = (float)Math.Max(0.0, Math.Min(1.0, (RingtoneVolumeSlider?.Value ?? 70) / 100.0));
                string mode = ((RingtoneSoundComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "wav");
                RingtoneService.Instance.Configure(id, volume, mode);
            }
            catch
            {
                // ignore
            }
        }

        private void RingtoneVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                if (RingtoneVolumeValueTextBlock != null)
                {
                    RingtoneVolumeValueTextBlock.Text = $"{(int)Math.Round(e.NewValue)}%";
                }

                string? id = (RingtoneDeviceComboBox?.SelectedItem as RingtoneOutputDeviceInfo)?.Id;
                float volume = (float)Math.Max(0.0, Math.Min(1.0, e.NewValue / 100.0));
                string mode = ((RingtoneSoundComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "wav");
                RingtoneService.Instance.Configure(id, volume, mode);
            }
            catch
            {
                // ignore
            }
        }

        private void RingtoneSoundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                string mode = ((RingtoneSoundComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "wav");
                string? id = (RingtoneDeviceComboBox?.SelectedItem as RingtoneOutputDeviceInfo)?.Id;
                float volume = (float)Math.Max(0.0, Math.Min(1.0, (RingtoneVolumeSlider?.Value ?? 70) / 100.0));
                RingtoneService.Instance.Configure(id, volume, mode);
            }
            catch
            {
                // ignore
            }
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

    // Converter для иконок темы
    public class ThemeIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var tag = value?.ToString() ?? "system";
            return tag switch
            {
                "system" => Symbol.Desktop,
                "dark" => Symbol.WeatherMoon,
                "light" => Symbol.WeatherSunny,
                _ => Symbol.Desktop
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // Converter для описаний темы
    public class ThemeDescriptionConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var tag = value?.ToString() ?? "system";
            return tag switch
            {
                "system" => "Follows your Windows theme",
                "dark" => "Dark colors everywhere",
                "light" => "Light and bright",
                _ => ""
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

