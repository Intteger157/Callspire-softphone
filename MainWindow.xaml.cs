using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;

namespace Softphone
{
    public partial class MainWindow : Window
    {
        private SipService? _sipService;
        private CallHistoryService _callHistoryService;

        private const string SettingsFileName = "settings.json";

        public bool IsConnected => _sipService?.IsRegistered ?? false;

        public MainWindow()
        {
            InitializeComponent();
            _callHistoryService = new CallHistoryService();
            ShowView(DialerView);
            _ = TryConnectFromSettings(); // Запускаем асинхронно без ожидания
        }

        private async System.Threading.Tasks.Task TryConnectFromSettings()
        {
            try
            {
                if (File.Exists(SettingsFileName))
                {
                    string json = File.ReadAllText(SettingsFileName);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    
                    if (settings != null && !string.IsNullOrEmpty(settings.SipServer) && 
                        !string.IsNullOrEmpty(settings.SipUsername) && !string.IsNullOrEmpty(settings.SipPassword))
                    {
                        // Автоматически подключаемся при запуске, если есть настройки
                        await ConnectWithSettings(settings);
                    }
                }
            }
            catch
            {
                // Игнорируем ошибки при автоподключении
            }
        }

        private async System.Threading.Tasks.Task ConnectWithSettings(AppSettings settings)
        {
            try
            {
                int port = 5060;
                if (!string.IsNullOrEmpty(settings.SipServer) && settings.SipServer.Contains(":"))
                {
                    var parts = settings.SipServer.Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int parsedPort))
                    {
                        port = parsedPort;
                    }
                }

                _sipService?.Dispose();

                _sipService = new SipService(
                    settings.SipUsername ?? "", 
                    settings.SipPassword ?? "", 
                    settings.SipServer?.Split(':')[0] ?? settings.SipServer ?? "", 
                    port,
                    settings.MicrophoneDeviceNumber, 
                    settings.SpeakerDeviceNumber);
                _sipService.OnStatusChanged += (status) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusTextBlock.Text = $"Status: {status}";
                        OnConnectionStatusChanged?.Invoke(status);
                    });
                };

                StatusTextBlock.Text = "Status: Connecting...";

                await _sipService.StartAsync();

                CallButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Status: Error - {ex.Message}";
            }
        }

        private void ShowView(Grid view)
        {
            DialerView.Visibility = Visibility.Collapsed;
            HistoryView.Visibility = Visibility.Collapsed;

            view.Visibility = Visibility.Visible;
        }

        private void DialerButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(DialerView);
        }

        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(HistoryView);
            LoadCallHistory();
        }

        private void LoadCallHistory()
        {
            var history = _callHistoryService.GetHistory();
            
            if (history.Count == 0)
            {
                HistoryItemsControl.ItemsSource = null;
                EmptyHistoryTextBlock.Visibility = Visibility.Visible;
                return;
            }

            EmptyHistoryTextBlock.Visibility = Visibility.Collapsed;
            
            // Преобразуем в формат для отображения
            var displayItems = history.Select(call => new
            {
                PhoneNumber = call.PhoneNumber,
                Status = GetStatusText(call.Status),
                StatusColor = GetStatusColor(call.Status),
                CallTimeText = call.CallTime.ToString("yyyy-MM-dd HH:mm:ss"),
                DurationText = call.Duration.HasValue 
                    ? $"{call.Duration.Value.Minutes:D2}:{call.Duration.Value.Seconds:D2}" 
                    : "-",
                CallDirectionIcon = call.IsIncoming ? "CallInbound" : "CallOutbound",
                CallDirectionColor = call.IsIncoming 
                    ? new SolidColorBrush(Color.FromRgb(59, 130, 246)) // Blue for incoming
                    : new SolidColorBrush(Color.FromRgb(34, 197, 94)) // Green for outgoing
            }).ToList();

            HistoryItemsControl.ItemsSource = displayItems;
        }

        private string GetStatusText(CallStatus status)
        {
            return status switch
            {
                CallStatus.Calling => "Calling...",
                CallStatus.Connected => "Connected",
                CallStatus.Ended => "Ended",
                CallStatus.Failed => "Failed",
                CallStatus.Cancelled => "Cancelled",
                _ => "Unknown"
            };
        }

        private Brush GetStatusColor(CallStatus status)
        {
            return status switch
            {
                CallStatus.Connected => new SolidColorBrush(Color.FromRgb(34, 197, 94)), // Green
                CallStatus.Ended => new SolidColorBrush(Color.FromRgb(156, 163, 175)), // Gray
                CallStatus.Failed => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                CallStatus.Cancelled => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                CallStatus.Calling => new SolidColorBrush(Color.FromRgb(59, 130, 246)), // Blue
                _ => new SolidColorBrush(Color.FromRgb(156, 163, 175)) // Gray
            };
        }

        private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to clear all call history?", 
                "Clear History", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                _callHistoryService.ClearHistory();
                LoadCallHistory();
            }
        }

        private void CallFromHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string phoneNumber)
            {
                // Устанавливаем номер в поле ввода
                PhoneNumberTextBox.Text = phoneNumber;
                PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
                
                // Переключаемся на экран набора номера
                ShowView(DialerView);
                
                // Если подключение активно, сразу инициируем звонок
                if (_sipService != null && IsConnected && CallButton.IsEnabled)
                {
                    // Небольшая задержка для обновления UI
                    System.Threading.Tasks.Task.Delay(100).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            CallButton_Click(sender, e);
                        });
                    });
                }
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = this
            };
            settingsWindow.ShowDialog();
        }

        private void PhoneNumberTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (PhoneNumberTextBox.Text == "Enter the number")
            {
                PhoneNumberTextBox.Text = "";
                PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
            }
        }

        private void PhoneNumberTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PhoneNumberTextBox.Text))
            {
                PhoneNumberTextBox.Text = "Enter the number";
                PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
            }
        }

        private void PhoneNumberTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Если пользователь начал вводить текст, меняем цвет на белый
            if (PhoneNumberTextBox.Text != "Enter the number" && PhoneNumberTextBox.Text.Length > 0)
            {
                PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
            }
        }

        private void PhoneNumberTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Если это placeholder, очищаем при первом нажатии
            if (PhoneNumberTextBox.Text == "Enter the number")
            {
                PhoneNumberTextBox.Text = "";
                PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
            }

            // Enter для звонка
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                if (CallButton.IsEnabled)
                {
                    CallButton_Click(sender, e);
                }
                e.Handled = true;
                return;
            }

            // Разрешаем все остальные клавиши для нормального ввода
            // TextBox сам обработает ввод цифр, букв и специальных символов
            e.Handled = false;
        }

        private void NumpadButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                // Если текст - это placeholder, очищаем его
                if (PhoneNumberTextBox.Text == "Enter the number")
                {
                    PhoneNumberTextBox.Text = "";
                    PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
                }

                string digit = button.Content.ToString() ?? "";
                PhoneNumberTextBox.Text += digit;
            }
        }

        private void BackspaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (PhoneNumberTextBox.Text == "Enter the number")
            {
                return;
            }

            if (!string.IsNullOrEmpty(PhoneNumberTextBox.Text))
            {
                PhoneNumberTextBox.Text = PhoneNumberTextBox.Text.Substring(0, PhoneNumberTextBox.Text.Length - 1);
                
                // Если текст пустой, возвращаем placeholder
                if (string.IsNullOrEmpty(PhoneNumberTextBox.Text))
                {
                    PhoneNumberTextBox.Text = "Enter the number";
                    PhoneNumberTextBox.Foreground = (SolidColorBrush)FindResource("TextSecondaryBrush");
                }
            }
        }

        public event Action<string>? OnConnectionStatusChanged;

        public async System.Threading.Tasks.Task ReconnectFromSettingsAsync()
        {
            await TryConnectFromSettings();
        }

        public async void ReconnectFromSettings()
        {
            await ReconnectFromSettingsAsync();
        }

        private async void CallButton_Click(object sender, RoutedEventArgs e)
        {
            await PerformCall();
        }

        private async System.Threading.Tasks.Task PerformCall()
        {
            if (_sipService == null)
            {
                MessageBox.Show("Please connect first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string number = PhoneNumberTextBox.Text.Trim();
            if (string.IsNullOrEmpty(number) || number == "Enter the number")
            {
                MessageBox.Show("Please enter a phone number.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DateTime callStartTime = DateTime.Now;
            CallHistoryItem? currentCallHistoryItem = null;

            try
            {
                CallButton.IsEnabled = false;

                // Создаем запись в истории звонков
                currentCallHistoryItem = new CallHistoryItem
                {
                    PhoneNumber = number,
                    CallTime = callStartTime,
                    Status = CallStatus.Calling
                };
                _callHistoryService.AddCall(currentCallHistoryItem);

                // Открываем окно звонка
                var callWindow = new CallWindow(_sipService, number)
                {
                    Owner = this
                };
                
                bool callConnected = false;
                DateTime? callConnectedTime = null;
                
                // Подписываемся на события статуса для отслеживания состояния звонка
                _sipService.OnStatusChanged += (status) =>
                {
                    if (status.Contains("Call connected") || status.Contains("Calling"))
                    {
                        callConnected = true;
                        callConnectedTime = DateTime.Now;
                        if (currentCallHistoryItem != null)
                        {
                            currentCallHistoryItem.Status = CallStatus.Connected;
                            _callHistoryService.UpdateCallStatus(number, callStartTime, CallStatus.Connected);
                        }
                    }
                };
                
                // Подписываемся на событие завершения звонка для обновления состояния кнопки
                _sipService.OnCallEnded += () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        CallButton.IsEnabled = true;
                        
                        // Обновляем историю звонка
                        if (currentCallHistoryItem != null)
                        {
                            TimeSpan? duration = null;
                            if (callConnected && callConnectedTime.HasValue)
                            {
                                duration = DateTime.Now - callConnectedTime.Value;
                            }
                            
                            currentCallHistoryItem.Status = callConnected ? CallStatus.Ended : CallStatus.Failed;
                            currentCallHistoryItem.Duration = duration;
                            _callHistoryService.UpdateCallStatus(number, callStartTime, 
                                callConnected ? CallStatus.Ended : CallStatus.Failed, duration);
                        }
                    });
                };
                
                // Обрабатываем закрытие окна звонка
                callWindow.Closed += (s, e) =>
                {
                    // Если окно закрылось, но звонок еще активен, завершаем его
                    if (_sipService != null && _sipService.IsInCall)
                    {
                        _sipService.Hangup();
                    }
                    
                    // Обновляем историю, если звонок был отменен
                    if (currentCallHistoryItem != null && currentCallHistoryItem.Status == CallStatus.Calling)
                    {
                        currentCallHistoryItem.Status = CallStatus.Cancelled;
                        _callHistoryService.UpdateCallStatus(number, callStartTime, CallStatus.Cancelled);
                    }
                    
                    CallButton.IsEnabled = true;
                };
                
                callWindow.Show();

                // Инициируем звонок
                await _sipService.CallAsync(number);
            }
            catch (Exception ex)
            {
                // Обновляем историю при ошибке
                if (currentCallHistoryItem != null)
                {
                    currentCallHistoryItem.Status = CallStatus.Failed;
                    currentCallHistoryItem.ErrorMessage = ex.Message;
                    _callHistoryService.UpdateCallStatus(number, callStartTime, CallStatus.Failed, null, ex.Message);
                }
                
                MessageBox.Show($"Call error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                CallButton.IsEnabled = true;
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _sipService?.Dispose();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _sipService?.Dispose();
            base.OnClosed(e);
        }
    }
}

