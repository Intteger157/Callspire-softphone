using System;
using System.Windows;
using System.Windows.Input;

namespace Softphone
{
    public partial class CallWindow : Window
    {
        private SipService? _sipService;
        private string _phoneNumber;
        private bool _isMuted = false;
        private bool _isOnHold = false;
        private DateTime _callStartTime;
        private System.Windows.Threading.DispatcherTimer? _callTimer;

        public CallWindow(SipService sipService, string phoneNumber)
        {
            InitializeComponent();
            _sipService = sipService;
            _phoneNumber = phoneNumber;
            _callStartTime = DateTime.Now;
            
            CallerNameTextBlock.Text = phoneNumber;
            CallStatusTextBlock.Text = "Calling...";

            // Подписываемся на изменения статуса
            if (_sipService != null)
            {
                _sipService.OnStatusChanged += UpdateCallStatus;
            }

            // Запускаем таймер для отображения длительности звонка
            StartCallTimer();
        }

        private void StartCallTimer()
        {
            _callTimer = new System.Windows.Threading.DispatcherTimer();
            _callTimer.Interval = TimeSpan.FromSeconds(1);
            _callTimer.Tick += (s, e) =>
            {
                if (!_isOnHold)
                {
                    var duration = DateTime.Now - _callStartTime;
                    CallStatusTextBlock.Text = duration.ToString(@"hh\:mm\:ss");
                }
            };
            _callTimer.Start();
        }

        private void UpdateCallStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                if (status.Contains("Call connected"))
                {
                    // Когда звонок подключен, начинаем отсчет времени
                    _callStartTime = DateTime.Now;
                    if (_callTimer != null && !_callTimer.IsEnabled)
                    {
                        _callTimer.Start();
                    }
                }
                else if (!status.Contains("Calling"))
                {
                    CallStatusTextBlock.Text = status;
                }
            });
        }

        private void HangupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_sipService == null)
            {
                Close();
                return;
            }

            try
            {
                // Отключаем кнопку, чтобы предотвратить повторные нажатия
                HangupButton.IsEnabled = false;
                
                // Завершаем звонок
                _sipService.Hangup();
                CallStatusTextBlock.Text = "Call ended";
                
                // Закрываем окно через небольшую задержку, чтобы пользователь увидел статус
                System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() => Close());
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Hangup error: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
                HangupButton.IsEnabled = true;
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
            // При закрытии окна завершаем звонок
            if (_sipService != null)
            {
                try
                {
                    _sipService.Hangup();
                }
                catch
                {
                    // Игнорируем ошибки при закрытии
                }
            }
            Close();
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            _isMuted = !_isMuted;
            if (_isMuted)
            {
                MuteButton.Content = "\uE72F"; // Иконка Muted
                MuteButton.Background = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                // TODO: Логика отключения микрофона в SIP-клиенте
            }
            else
            {
                MuteButton.Content = "\uE720"; // Иконка Mic
                MuteButton.Background = (System.Windows.Media.Brush)FindResource("BackgroundMediumBrush");
                // TODO: Логика включения микрофона
            }
        }

        private void HoldButton_Click(object sender, RoutedEventArgs e)
        {
            _isOnHold = !_isOnHold;
            if (_isOnHold)
            {
                HoldButton.Content = "\uE768"; // Иконка Play
                HoldButton.Background = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                CallStatusTextBlock.Text = "On Hold";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                // TODO: Логика удержания вызова
            }
            else
            {
                HoldButton.Content = "\uE769"; // Иконка Pause
                HoldButton.Background = (System.Windows.Media.Brush)FindResource("BackgroundMediumBrush");
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                // TODO: Логика возобновления вызова
            }
        }

        private void SpeakerButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Логика переключения динамика
        }

        protected override void OnClosed(EventArgs e)
        {
            // Останавливаем таймер
            _callTimer?.Stop();
            
            // Отписываемся от событий
            if (_sipService != null)
            {
                _sipService.OnStatusChanged -= UpdateCallStatus;
            }
            base.OnClosed(e);
        }
    }
}

