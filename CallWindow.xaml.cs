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
        private const double DefaultWidth = 380;
        private const double ExpandedWidth = 800; // Ширина с открытым нумпадом
        private const double DefaultHeight = 450;
        private const double ExpandedHeight = 680; // Высота с открытым нумпадом

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
                _sipService.OnCallEnded += OnCallEnded;
            }

            // Запускаем таймер для отображения длительности звонка
            StartCallTimer();
            
            // Включаем обработку нажатий клавиш для DTMF
            KeyDown += CallWindow_KeyDown;
            Focusable = true;
            Focus();
        }
        
        private bool _isClosing = false;
        
        private void OnCallEnded()
        {
            Dispatcher.Invoke(() =>
            {
                if (_isClosing)
                {
                    // Если окно уже закрывается, просто закрываем его
                    Close();
                }
                else
                {
                    // Обновляем статус
                    CallStatusTextBlock.Text = "Call ended";
                }
            });
        }
        
        private void CallWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (_sipService == null || !_sipService.IsInCall)
                return;

            char? digit = null;
            
            // Обрабатываем цифры 0-9
            if (e.Key >= Key.D0 && e.Key <= Key.D9)
            {
                digit = (char)('0' + (e.Key - Key.D0));
            }
            // Обрабатываем цифры на цифровой клавиатуре
            else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
            {
                digit = (char)('0' + (e.Key - Key.NumPad0));
            }
            // Обрабатываем * и #
            else if (e.Key == Key.Multiply || e.Key == Key.OemQuestion)
            {
                digit = '*';
            }
            else if (e.Key == Key.Divide || e.Key == Key.OemTilde)
            {
                digit = '#';
            }

            if (digit.HasValue)
            {
                _sipService.SendDTMF(digit.Value);
                e.Handled = true;
            }
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
                
                // Устанавливаем флаг закрытия
                _isClosing = true;
                
                // Завершаем звонок
                _sipService.Hangup();
                CallStatusTextBlock.Text = "Hanging up...";
                
                // Закрываем окно через небольшую задержку после завершения звонка
                // OnCallEnded закроет окно автоматически
                System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            Close();
                        }
                        catch
                        {
                            // Окно уже закрыто
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                _isClosing = false;
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
            if (_sipService != null && _sipService.IsInCall)
            {
                try
                {
                    _isClosing = true;
                    _sipService.Hangup();
                    // Ждем немного перед закрытием, чтобы звонок успел завершиться
                    System.Threading.Tasks.Task.Delay(300).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                Close();
                            }
                            catch
                            {
                                // Окно уже закрыто
                            }
                        });
                    });
                    return; // Не закрываем сразу
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
        
        private void DtmfButton_Click(object sender, RoutedEventArgs e)
        {
            if (_sipService == null || !_sipService.IsInCall)
                return;

            if (sender is System.Windows.Controls.Button button && button.Content is string digit)
            {
                _sipService.SendDTMF(digit[0]);
            }
        }
        
        private void KeypadToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (KeypadGrid.Visibility == Visibility.Visible)
            {
                // Закрываем нумпад
                KeypadGrid.Visibility = Visibility.Collapsed;
                
                // Анимация уменьшения ширины окна
                var widthAnimation = new System.Windows.Media.Animation.DoubleAnimation(
                    DefaultWidth,
                    TimeSpan.FromMilliseconds(250));
                widthAnimation.EasingFunction = new System.Windows.Media.Animation.QuadraticEase 
                { 
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut 
                };
                BeginAnimation(WidthProperty, widthAnimation);
                
                // Анимация уменьшения высоты окна
                var heightAnimation = new System.Windows.Media.Animation.DoubleAnimation(
                    DefaultHeight,
                    TimeSpan.FromMilliseconds(250));
                heightAnimation.EasingFunction = new System.Windows.Media.Animation.QuadraticEase 
                { 
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut 
                };
                BeginAnimation(HeightProperty, heightAnimation);
            }
            else
            {
                // Открываем нумпад
                KeypadGrid.Visibility = Visibility.Visible;
                
                // Анимация увеличения ширины окна
                var widthAnimation = new System.Windows.Media.Animation.DoubleAnimation(
                    ExpandedWidth,
                    TimeSpan.FromMilliseconds(250));
                widthAnimation.EasingFunction = new System.Windows.Media.Animation.QuadraticEase 
                { 
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut 
                };
                BeginAnimation(WidthProperty, widthAnimation);
                
                // Анимация увеличения высоты окна
                var heightAnimation = new System.Windows.Media.Animation.DoubleAnimation(
                    ExpandedHeight,
                    TimeSpan.FromMilliseconds(250));
                heightAnimation.EasingFunction = new System.Windows.Media.Animation.QuadraticEase 
                { 
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut 
                };
                BeginAnimation(HeightProperty, heightAnimation);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Останавливаем таймер
            _callTimer?.Stop();
            
            // Отписываемся от событий
            if (_sipService != null)
            {
                _sipService.OnStatusChanged -= UpdateCallStatus;
                _sipService.OnCallEnded -= OnCallEnded;
                
                // Если звонок все еще активен при закрытии окна, завершаем его
                if (_sipService.IsInCall && !_isClosing)
                {
                    try
                    {
                        _sipService.Hangup();
                    }
                    catch
                    {
                        // Игнорируем ошибки
                    }
                }
            }
            base.OnClosed(e);
        }
    }
}

