using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;

namespace Softphone
{
    public partial class CallWindow : Window
    {
        // Событие для обновления статуса входящего звонка в истории
        public event Action<string, DateTime, CallStatus, TimeSpan?>? OnIncomingCallStatusChanged;
        
        // Событие для обновления детальной информации о звонке
        public event Action<string, DateTime, DateTime?, DateTime?, DateTime?, bool, CallEndedBy, List<string>?, TimeSpan?, string?, CallTransport?, string?, string?>? OnCallDetailsChanged;
        private SipService? _sipService;
        private string _phoneNumber;
        private bool _isMuted = false;
        private bool _isOnHold = false;
        private DateTime _callStartTime;
        private DateTime _originalCallStartTime; // Исходное время начала звонка (не меняется при подключении)
        private System.Windows.Threading.DispatcherTimer? _callTimer;
        private bool _isKeypadVisible = false;
        private bool _isIncomingCall = false; // Флаг для входящего звонка
        private DateTime _incomingCallStartTime; // Время начала входящего звонка для истории
        private bool _wasAnswered = false; // Флаг, был ли звонок принят
        
        // Детальная информация о звонке
        private DateTime? _ringbackStartTime = null; // Время начала гудков
        private DateTime? _ringbackEndTime = null; // Время окончания гудков
        private DateTime? _answerTime = null; // Время ответа
        private CallEndedBy _endedBy = CallEndedBy.Unknown; // Кто завершил звонок
        private DateTime _callWindowStartTime; // Время создания окна звонка для фильтрации логов
        private List<string> _technicalDetails = new List<string>(); // Технические детали из логов
        private const double DefaultWidth = 380;
        private const double ExpandedWidth = 800; // Ширина с открытым нумпадом
        private const double DefaultHeight = 750;
        private const double ExpandedHeight = 750; // Высота с открытым нумпадом

        // WebRTC fields
        private bool _useWebRtc = false;
        private WebRtcConfig? _webRtcConfig;
        
        // Tone generator for ringback tone (WebRTC calls)
        private ToneGenerator? _toneGenerator;
        
        // WebRTC call recorder
        private WebRtcCallRecorder? _webRtcRecorder;
        private string? _webRtcRecordingFilePath; // Сохраняем путь к файлу записи для Call Details
        private bool _isStoppingRecording = false; // Флаг для предотвращения повторных вызовов StopWebRtcRecording
        
        // SIP call recording file path
        private string? _sipRecordingFilePath; // Сохраняем путь к файлу записи SIP звонка для Call Details
        
        // Call context with transport information
        private CallContext _callContext = null!; // Инициализируется в конструкторах

        public CallWindow(SipService sipService, string phoneNumber, bool isIncomingCall = false, DateTime? callStartTime = null)
        {
            InitializeComponent();
            _sipService = sipService;
            _phoneNumber = phoneNumber;
            _isIncomingCall = isIncomingCall;
            _callStartTime = callStartTime ?? DateTime.Now; // Используем переданное время или текущее
            _originalCallStartTime = _callStartTime; // Сохраняем исходное время начала звонка
            _incomingCallStartTime = isIncomingCall ? DateTime.Now : _callStartTime; // Для входящих - текущее время, для исходящих - переданное
            _callWindowStartTime = DateTime.Now; // Время создания окна для фильтрации логов
            
            // Создаем контекст звонка с транспортом SIP
            _callContext = new CallContext(CallTransport.Sip, phoneNumber, _callStartTime);
            
            // Логируем хеш-код для диагностики
            if (_sipService != null)
            {
                int serviceHash = _sipService.GetHashCode();
                MainWindow.Log($"CallWindow constructor: SipService hash: {serviceHash}, PhoneNumber: {phoneNumber}, IsIncomingCall: {isIncomingCall}");
            }
            
            UpdateTransportBadge();
            CallerNameTextBlock.Text = phoneNumber;
            
            if (_isIncomingCall)
            {
                // Для входящего звонка показываем кнопки "Ответить" и "Отклонить"
                CallStatusTextBlock.Text = $"Incoming call from {phoneNumber}";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                ShowIncomingCallButtons();
            }
            else
            {
                // Для исходящего звонка обычный интерфейс
                CallStatusTextBlock.Text = "Connecting...";
                ShowCallControls();
            }

            // Подписываемся на изменения статуса
            if (_sipService != null)
            {
                _sipService.OnStatusChanged += UpdateCallStatus;
                _sipService.OnCallEnded += OnCallEnded;
            }

            // НЕ запускаем таймер сразу - он запустится только когда звонок будет принят
            // До этого будем показывать статус (Calling..., Ringing... и т.д.)
            
            // Включаем обработку нажатий клавиш для DTMF
            KeyDown += CallWindow_KeyDown;
            Focusable = true;
            
            // Для входящих звонков настраиваем окно для немедленного отображения
            if (_isIncomingCall)
            {
                ShowActivated = true;
                ShowInTaskbar = true;
                Topmost = true; // Делаем окно поверх всех для входящих звонков
            }
            
            Focus();
        }
        
        private void ShowIncomingCallButtons()
        {
            // Скрываем обычные кнопки управления
            ControlButtonsPanel.Visibility = Visibility.Collapsed;
            HangupButton.Visibility = Visibility.Collapsed;
            
            // Показываем кнопки для входящего звонка
            IncomingCallButtonsPanel.Visibility = Visibility.Visible;
        }
        
        private void ShowCallControls()
        {
            // Показываем обычные кнопки управления
            ControlButtonsPanel.Visibility = Visibility.Visible;
            HangupButton.Visibility = Visibility.Visible;
            
            // Скрываем кнопки для входящего звонка
            IncomingCallButtonsPanel.Visibility = Visibility.Collapsed;
        }
        
        private async void AnswerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_useWebRtc)
            {
                AnswerButton.IsEnabled = false;
                RejectButton.IsEnabled = false;
                await WebRtcAnswerAsync();
                return;
            }
            
            if (_sipService == null)
            {
                CustomMessageBox.Show($"Error: SipService is null in CallWindow. Service hash: N/A", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
                return;
            }

            try
            {
                // Логируем хеш-код для диагностики
                int serviceHash = _sipService.GetHashCode();
                MainWindow.Log($"AnswerButton_Click: SipService hash: {serviceHash}");
                
                AnswerButton.IsEnabled = false;
                RejectButton.IsEnabled = false;
                
                // Принимаем звонок
                bool answered = await _sipService.AnswerIncomingCallAsync();
                
                if (answered)
                {
                    // Переключаемся на обычный интерфейс звонка
                    _isIncomingCall = false;
                    _wasAnswered = true;
                    _callStartTime = DateTime.Now; // Время начала разговора (не меняем _originalCallStartTime)
                    
                    // Показываем стандартные кнопки управления звонком
                    ShowCallControls();
                    
                    // Запускаем таймер для отсчета времени разговора
                    StartCallTimer();
                    
                    // Обновляем статус
                    CallStatusTextBlock.Text = "00:00:00";
                    CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                    
                    // Записываем время ответа
                    CallWindowHelpers.UpdateAnswerTime(ref _answerTime, ref _wasAnswered);
                    
                    // Если гудки еще играли, записываем время окончания
                    CallWindowHelpers.UpdateRingbackEndTime(_ringbackStartTime, ref _ringbackEndTime);
                    
                    // Уведомляем MainWindow об обновлении статуса входящего звонка
                    OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Connected, null);
                    
                    // Отправляем детальную информацию
                    SendCallDetails();
                }
                else
                {
                    CustomMessageBox.Show("Failed to answer the call.", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
                    AnswerButton.IsEnabled = true;
                    RejectButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error answering call: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
                AnswerButton.IsEnabled = true;
                RejectButton.IsEnabled = true;
            }
        }
        
        private async void RejectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AnswerButton.IsEnabled = false;
                RejectButton.IsEnabled = false;
                
                if (_useWebRtc)
                {
                    // WebRTC звонок - отклоняем через сервис
                    await WebRtcService.Instance.HangupAsync();
                    
                    // Уведомляем MainWindow об отклонении входящего звонка
                    OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Cancelled, null);
                    
                    // Закрываем окно
                    Close();
                }
                else if (_sipService != null)
                {
                    // SIPSorcery звонок
                    await _sipService.RejectIncomingCallAsync();
                    
                    // Уведомляем MainWindow об отклонении входящего звонка
                    OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Cancelled, null);
                    
                    // Закрываем окно
                    Close();
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error rejecting call: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
                AnswerButton.IsEnabled = true;
                RejectButton.IsEnabled = true;
            }
        }
        
        private bool _isClosing = false;
        
        private void OnCallEnded()
        {
            // Используем BeginInvoke вместо Invoke, чтобы не блокировать поток
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Сохраняем путь к записи SIP звонка перед остановкой записи
                if (_sipService != null && string.IsNullOrEmpty(_sipRecordingFilePath))
                {
                    _sipRecordingFilePath = _sipService.CurrentRecordingFilePath;
                    if (!string.IsNullOrEmpty(_sipRecordingFilePath))
                    {
                        MainWindow.Log($"[CallWindow] SIP recording file path saved: {_sipRecordingFilePath}");
                    }
                }
                
                // Останавливаем ringback tone при завершении звонка
                _toneGenerator?.Stop();

                // Останавливаем запись звонка (если еще не остановлена)
                StopWebRtcRecording();
                
                // Записываем время окончания гудков, если еще не записано
                CallWindowHelpers.UpdateRingbackEndTime(_ringbackStartTime, ref _ringbackEndTime);
                
                // Если звонок завершился не по нашей инициативе, значит удаленная сторона
                if (_endedBy == CallEndedBy.Unknown)
                {
                    _endedBy = CallEndedBy.RemoteParty;
                }
                
                // Всегда закрываем окно при завершении вызова
                if (!_isClosing)
                {
                    _isClosing = true;
                    
                    // Останавливаем таймер
                    if (_callTimer != null)
                    {
                        _callTimer.Stop();
                    }
                    
                    // Вычисляем длительность звонка
                    // Для входящих звонков используем _answerTime (время ответа), для исходящих - _callStartTime (время подключения)
                    TimeSpan? duration = null;
                    if (_wasAnswered)
                    {
                        var startTime = _isIncomingCall ? (_answerTime ?? _incomingCallStartTime) : _callStartTime;
                        if (startTime != default)
                        {
                            duration = DateTime.Now - startTime;
                        }
                    }
                    
                    // Отправляем детальную информацию перед завершением (с длительностью)
                    SendCallDetails();
                    
                    // Уведомляем MainWindow о завершении входящего звонка
                    if (_wasAnswered)
                    {
                        OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Ended, duration);
                    }
                    // Для исходящих WebRTC звонков также отправляем обновление статуса через SendCallDetails
                    // которое уже вызвано выше и обновит статус через OnCallDetailsChanged
                    
                    // Обновляем статус перед закрытием
                    CallStatusTextBlock.Text = "Call ended";
                    
                    // Закрываем окно с небольшой задержкой, чтобы пользователь увидел статус
                    _ = System.Threading.Tasks.Task.Delay(800).ContinueWith(_ =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (!_isClosing) return; // Дополнительная проверка
                                Close();
                            }
                            catch (Exception ex)
                            {
                                // Окно уже закрыто или произошла ошибка
                                MainWindow.Log($"Error closing window: {ex.Message}");
                            }
                        }));
                    });
                }
            }));
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
                // Добавляем только те статусы, которые относятся к текущему звонку
                // Фильтруем по ключевым словам, связанным со звонками
                if (!string.IsNullOrEmpty(status))
                {
                    // Проверяем, относится ли статус к текущему звонку
                    bool isCallRelated = status.Contains("Call") || 
                                       status.Contains("INVITE") || 
                                       status.Contains("SIP Request") || 
                                       status.Contains("SIP Response") ||
                                       status.Contains("Ringing") ||
                                       status.Contains("Trying") ||
                                       status.Contains("Session Progress") ||
                                       status.Contains("200 OK") ||
                                       status.Contains("180") ||
                                       status.Contains("183") ||
                                       status.Contains("100") ||
                                       status.Contains("BYE") ||
                                       status.Contains("CANCEL") ||
                                       status.Contains("Busy") ||
                                       status.Contains("Failed") ||
                                       status.Contains("Hanging up") ||
                                       status.Contains("Call ended");
                    
                    if (isCallRelated)
                    {
                        // Проверяем, содержит ли статус уже временной штамп
                        // Если статус начинается с "[", значит временной штамп уже есть
                        string trimmedStatus = status.TrimStart();
                        if (trimmedStatus.StartsWith("[") && trimmedStatus.Length > 14 && trimmedStatus[14] == ']')
                        {
                            // Временной штамп уже есть в формате [HH:mm:ss.fff], добавляем как есть
                            _technicalDetails.Add(status);
                        }
                        else
                        {
                            // Добавляем временной штамп
                            _technicalDetails.Add($"[{DateTime.Now:HH:mm:ss.fff}] {status}");
                        }
                    }
                }
                
                // Обрабатываем статусы до начала разговора (показываем статус вместо таймера)
                if (status.Contains("Call progress: 100 Trying") || status.Contains("100 Trying"))
                {
                    // Показываем статус только если таймер еще не запущен (звонок не принят)
                    if (_callTimer == null || !_callTimer.IsEnabled)
                    {
                        CallStatusTextBlock.Text = "Connecting...";
                        CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                    }
                }
                else if (status.Contains("Call progress: 180 Ringing") || status.Contains("180 Ringing"))
                {
                    // Записываем время начала гудков
                    if (!_ringbackStartTime.HasValue)
                    {
                        _ringbackStartTime = DateTime.Now;
                    }
                    
                    // Показываем статус только если таймер еще не запущен (звонок не принят)
                    if (_callTimer == null || !_callTimer.IsEnabled)
                    {
                        CallStatusTextBlock.Text = "Ringing...";
                        CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                    }
                }
                else if (status.Contains("Call progress: 183 Session Progress") || status.Contains("183 Session Progress"))
                {
                    // Записываем время начала гудков (183 тоже означает начало гудков)
                    if (!_ringbackStartTime.HasValue)
                    {
                        _ringbackStartTime = DateTime.Now;
                    }
                    
                    // Показываем статус только если таймер еще не запущен (звонок не принят)
                    if (_callTimer == null || !_callTimer.IsEnabled)
                    {
                        CallStatusTextBlock.Text = "Connecting...";
                        CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                    }
                }
                else if (status.Contains("Call connected") || status.Contains("Call answered") || 
                         status.Contains("Call progress: 200 OK") || status.Contains("200 OK"))
                {
                    // Записываем время окончания гудков и время ответа
                    if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
                    {
                        _ringbackEndTime = DateTime.Now;
                    }
                    CallWindowHelpers.UpdateAnswerTime(ref _answerTime, ref _wasAnswered);
                    
                    // Когда звонок подключен, начинаем отсчет времени разговора
                    // НЕ меняем _originalCallStartTime - он используется для поиска звонка в истории
                    if (!_wasAnswered)
                    {
                        _callStartTime = DateTime.Now; // Время начала разговора
                    }
                    
                    // Останавливаем таймер, если он был запущен ранее (на всякий случай)
                    if (_callTimer != null)
                    {
                        _callTimer.Stop();
                    }
                    
                    // Если это был входящий звонок, переключаем интерфейс на обычный режим
                    if (_isIncomingCall)
                    {
                        _isIncomingCall = false;
                        ShowCallControls();
                    }
                    
                    // Запускаем таймер для отсчета времени разговора
                    StartCallTimer();
                    
                    CallStatusTextBlock.Text = "00:00:00";
                    CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                    
                    // Отправляем детальную информацию
                    SendCallDetails();
                }
                else if (status.Contains("Call ended by remote party"))
                {
                    // Удаленная сторона завершила звонок
                    _endedBy = CallEndedBy.RemoteParty;
                }
                else if (status.Contains("Call ended") || status.Contains("Hanging up") || status.Contains("Call failed"))
                {
                    // Сохраняем путь к записи SIP звонка перед завершением
                    if (_sipService != null && string.IsNullOrEmpty(_sipRecordingFilePath))
                    {
                        _sipRecordingFilePath = _sipService.CurrentRecordingFilePath;
                        if (!string.IsNullOrEmpty(_sipRecordingFilePath))
                        {
                            MainWindow.Log($"[CallWindow] SIP recording file path saved (from status): {_sipRecordingFilePath}");
                        }
                    }
                    
                    // При завершении вызова закрываем окно
                    if (!_isClosing)
                    {
                        _isClosing = true;
                        
                        // Останавливаем таймер
                        if (_callTimer != null)
                        {
                            _callTimer.Stop();
                        }
                        
                        // Записываем время окончания гудков, если еще не записано
                        if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
                        {
                            _ringbackEndTime = DateTime.Now;
                        }
                        
                        // Вычисляем длительность звонка
                        // Для входящих звонков используем _answerTime (время ответа), для исходящих - _callStartTime (время подключения)
                        TimeSpan? duration = null;
                        if (_wasAnswered)
                        {
                            var startTime = _isIncomingCall ? (_answerTime ?? _incomingCallStartTime) : _callStartTime;
                            if (startTime != default)
                        {
                                duration = DateTime.Now - startTime;
                        }
                        }
                        
                        // Отправляем детальную информацию перед завершением
                        SendCallDetails();
                        
                        // Уведомляем MainWindow о завершении входящего звонка
                        if (_wasAnswered)
                        {
                            OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Ended, duration);
                        }
                        else if (_isIncomingCall)
                        {
                            // Если звонок не был принят, но завершился (например, абонент сбросил)
                            // Это пропущенный звонок
                            OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Missed, null);
                        }
                        
                        // Обновляем статус
                        CallStatusTextBlock.Text = "Call ended";
                        
                        // Закрываем окно с небольшой задержкой
                        _ = System.Threading.Tasks.Task.Delay(800).ContinueWith(_ =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    if (!_isClosing) return; // Дополнительная проверка
                                    Close();
                                }
                                catch (Exception ex)
                                {
                                    // Окно уже закрыто или произошла ошибка
                                    MainWindow.Log($"Error closing window: {ex.Message}");
                                }
                            });
                        });
                    }
                }
                else if (status.Contains("Incoming call from"))
                {
                    // Извлекаем номер из статуса и показываем "Incoming call" с номером
                    var match = System.Text.RegularExpressions.Regex.Match(status, @"from (\d+)");
                    if (match.Success)
                    {
                        CallStatusTextBlock.Text = $"Incoming call from {match.Groups[1].Value}";
                    }
                    else
                    {
                        CallStatusTextBlock.Text = "Incoming call";
                    }
                    CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                }
                else if (status.Contains("Incoming call") && !status.Contains("from"))
                {
                    // Если статус содержит "Incoming call" но без номера, показываем номер из CallerNameTextBlock
                    var callerNumber = CallerNameTextBlock.Text;
                    if (!string.IsNullOrEmpty(callerNumber) && callerNumber != "Unknown (555-0199)")
                    {
                        CallStatusTextBlock.Text = $"Incoming call from {callerNumber}";
                    }
                    else
                    {
                        CallStatusTextBlock.Text = "Incoming call";
                    }
                    CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                }
                else if (status.Contains("Calling") || status.Contains("Call initiated"))
                {
                    // Показываем статус только если таймер еще не запущен (звонок не принят)
                    if (_callTimer == null || !_callTimer.IsEnabled)
                {
                    CallStatusTextBlock.Text = "Calling...";
                        CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                    }
                }
                else if (!status.Contains("Call connected") && !status.Contains("Call answered") 
                         && !status.Contains("Trace:") && !status.Contains("OnIncomingCall")
                         && !status.Contains("Saved incoming call") && !status.Contains("UserAgent:")
                         && !status.Contains("Request:") && !status.Contains("CallID:")
                         && !status.Contains("Dict Count:") && !status.Contains("Responded to OPTIONS")
                         && !status.Contains("Registration") && !status.Contains("SIP transport")
                         && !status.Contains("Initializing") && !status.Contains("Audio initialized")
                         && !status.Contains("Microphone") && !status.Contains("Call on hold")
                         && !status.Contains("Call resumed"))
                {
                    // Обновляем статус только для важных сообщений, фильтруем технические
                    // Не показываем технические сообщения в статусе
                }
            });
        }

        private async void HangupButton_Click(object sender, RoutedEventArgs e)
        {
            // Останавливаем ringback tone при нажатии Hangup
            _toneGenerator?.Stop();
            
            // Локальный пользователь завершил звонок
            _endedBy = CallEndedBy.LocalUser;
            
            if (_useWebRtc)
            {
                HangupButton.IsEnabled = false;
                _isClosing = true;
                _endedBy = CallEndedBy.LocalUser; // Пользователь сам завершил звонок
                
                // Останавливаем запись звонка
                StopWebRtcRecording();
                
                // Записываем время окончания гудков, если еще не записано
                CallWindowHelpers.UpdateRingbackEndTime(_ringbackStartTime, ref _ringbackEndTime);
                
                await WebRtcHangupAsync();
                CallStatusTextBlock.Text = "Hanging up...";
                
                // Добавляем техническую деталь о завершении звонка
                _technicalDetails.Add($"[{DateTime.Now:HH:mm:ss.fff}] Call terminated by local user (WebRTC)");
                
                // Отправляем детальную информацию перед закрытием
                SendCallDetails();
                
                _ = System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ =>
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
                return;
            }
            
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
                
                // Отправляем детальную информацию перед закрытием
                SendCallDetails();
                
                // Закрываем окно через небольшую задержку после завершения звонка
                // OnCallEnded закроет окно автоматически
                _ = System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ =>
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
                CustomMessageBox.Show($"Hangup error: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error, this);
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

        private async void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Если звонок еще активен, значит локальный пользователь закрыл окно
            if (_sipService != null && _sipService.IsInCall)
            {
                _endedBy = CallEndedBy.LocalUser;
            }
            
            // Для WebRTC звонков вызываем HangupAsync
            if (_useWebRtc)
            {
                try
                {
                    _isClosing = true;
                    HangupButton.IsEnabled = false;
                    AnswerButton.IsEnabled = false;
                    RejectButton.IsEnabled = false;
                    await WebRtcHangupAsync();
                    CallStatusTextBlock.Text = "Hanging up...";
                    
                    // Отправляем детальную информацию перед закрытием
                    SendCallDetails();
                    
                    // Ждем немного перед закрытием, чтобы звонок успел завершиться
                    _ = System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
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
            
            // При закрытии окна всегда завершаем звонок и останавливаем гудки
            if (_sipService != null)
            {
                try
                {
                    _isClosing = true;
                    // Всегда вызываем Hangup, даже если звонок не активен (чтобы остановить гудки)
                    _sipService.Hangup();
                    // Ждем немного перед закрытием, чтобы звонок успел завершиться
                    _ = System.Threading.Tasks.Task.Delay(300).ContinueWith(_ =>
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

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            // Определяем транспорт для логирования
            string transport = _useWebRtc ? "WebRTC" : "SIP";
            MainWindow.Log($"[CallWindow] Mute clicked: transport={transport}");
            
            // Для WebRTC звонков используем JavaScript функцию
            if (_useWebRtc)
            {
                try
                {
                    _isMuted = !_isMuted;
                    await WebRtcService.Instance.SetMuteAsync(_isMuted);
                    MainWindow.Log($"[CallWindow] Mute button clicked: muted={_isMuted} (WebRTC)");
                    
                    // Обновляем UI
                    UpdateMuteButtonUI();
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallWindow] ERROR: Failed to set mute for WebRTC call: {ex.Message}");
                    // Откатываем состояние при ошибке
                    _isMuted = !_isMuted;
                }
                return;
            }
            
            // Для SIP звонков используем SipService
            if (_sipService == null)
                return;

            _isMuted = !_isMuted;
            _sipService.SetMute(_isMuted);
            
            // Обновляем UI
            UpdateMuteButtonUI();
        }
        
        private void UpdateMuteButtonUI()
        {
            // Получаем TextBlock из Content или создаем новый
            System.Windows.Controls.TextBlock? iconTextBlock = MuteButton.Content as System.Windows.Controls.TextBlock;
            
            if (iconTextBlock == null)
            {
                // Если Content не TextBlock, создаем новый
                iconTextBlock = new System.Windows.Controls.TextBlock
                {
                    FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 24,
                    Foreground = System.Windows.Media.Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                MuteButton.Content = iconTextBlock;
            }
            
            // Убеждаемся, что FontFamily установлен правильно
            if (iconTextBlock.FontFamily == null || iconTextBlock.FontFamily.Source != "Segoe Fluent Icons")
            {
                iconTextBlock.FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons");
            }
            
            if (_isMuted)
            {
                // Иконка Muted - используем символ микрофона (E720)
                // Красный фон будет индикатором muted состояния
                iconTextBlock.Text = "\uE720";
                iconTextBlock.FontSize = 24;
                MuteButton.Background = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
            }
            else
            {
                // Иконка Mic
                iconTextBlock.Text = "\uE720";
                iconTextBlock.FontSize = 24;
                MuteButton.Background = (System.Windows.Media.Brush)FindResource("BackgroundMediumBrush");
            }
        }

        private async void HoldButton_Click(object sender, RoutedEventArgs e)
        {
            if (_sipService == null)
                return;

            _isOnHold = !_isOnHold;
            await _sipService.HoldCallAsync(_isOnHold);
            
            if (_isOnHold)
            {
                // Иконка Play
                var playIcon = new System.Windows.Controls.TextBlock 
                { 
                    Text = "\uE768", 
                    FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 24
                };
                HoldButton.Content = playIcon;
                HoldButton.Background = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                CallStatusTextBlock.Text = "On Hold";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                // Останавливаем таймер
                _callTimer?.Stop();
            }
            else
            {
                // Иконка Pause
                var pauseIcon = new System.Windows.Controls.TextBlock 
                { 
                    Text = "\uE769", 
                    FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
                    FontSize = 24
                };
                HoldButton.Content = pauseIcon;
                HoldButton.Background = (System.Windows.Media.Brush)FindResource("BackgroundMediumBrush");
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                // Возобновляем таймер
                if (_callTimer != null && !_callTimer.IsEnabled)
                {
                    _callTimer.Start();
                }
            }
        }

        private async void SpeakerButton_Click(object sender, RoutedEventArgs e)
        {
            // Для WebRTC звонков используем JavaScript API для управления устройствами
            if (_useWebRtc)
            {
                try
                {
                    MainWindow.Log("[CallWindow] SpeakerButton_Click: Opening WebRTC audio device dialog...");
                    
                    // Получаем список устройств
                    var devices = await WebRtcService.Instance.EnumerateAudioDevicesAsync();
                    
                    if (devices.Count == 0)
                    {
                        CustomMessageBox.Show(
                            "No audio devices found. Please check your system audio settings.",
                            "WebRTC Audio Devices",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            this);
                        return;
                    }
                    
                    // Разделяем устройства на входы и выходы
                    var inputs = devices.Where(d => d.Kind == "audioinput").ToList();
                    var outputs = devices.Where(d => d.Kind == "audiooutput").ToList();
                    
                    // Создаем простой диалог выбора устройств
                    var dialog = new WebRtcAudioDeviceDialog(inputs, outputs)
                    {
                        Owner = this
                    };
                    
                    if (dialog.ShowDialog() == true)
                    {
                        // Применяем выбранные устройства
                        await WebRtcService.Instance.SwitchAudioDeviceAsync(
                            dialog.SelectedInputDeviceId,
                            dialog.SelectedOutputDeviceId);
                        
                        MainWindow.Log($"[CallWindow] SpeakerButton_Click: Audio devices switched (input={dialog.SelectedInputDeviceId ?? "null"}, output={dialog.SelectedOutputDeviceId ?? "null"})");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallWindow] Error in SpeakerButton_Click (WebRTC): {ex.Message}");
                    CustomMessageBox.Show(
                        $"Error managing audio devices: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        this);
                }
                return;
            }
            
            // Для SIP звонков используем стандартный диалог
            if (_sipService == null)
                return;

            try
            {
                // Открываем диалог выбора устройств
                var dialog = new AudioDeviceDialog
                {
                    Owner = this
                };

                if (dialog.ShowDialog() == true)
                {
                    // Применяем выбранные устройства
                    _sipService.ChangeAudioDevices(
                        dialog.SelectedMicrophoneDeviceNumber,
                        dialog.SelectedSpeakerDeviceNumber);
                }
            }
            catch (Exception ex)
            {
                // Логируем ошибку, но не закрываем окно звонка
                MainWindow.Log($"[CallWindow] Error in SpeakerButton_Click: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[CallWindow] Error in SpeakerButton_Click: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        private void DtmfButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button)
            {
                char digit = '\0';
                
                // Пробуем получить символ из Content
                if (button.Content is string contentStr && contentStr.Length > 0)
                {
                    digit = contentStr[0];
                }
                else if (button.Content is char contentChar)
                {
                    digit = contentChar;
                }
                else
                {
                    // Пробуем преобразовать Content в строку
                    string? contentAsString = button.Content?.ToString();
                    if (!string.IsNullOrEmpty(contentAsString))
                    {
                        digit = contentAsString[0];
                    }
                }
                
                if (digit == '\0')
                    return;

                // Отправляем DTMF в зависимости от типа звонка
                if (_useWebRtc)
                {
                    // WebRTC звонок
                    if (WebRtcService.Instance != null && WebRtcService.Instance.CurrentCallState == WebRtcCallState.Connected)
                    {
                        _ = WebRtcService.Instance.SendDTMFAsync(digit);
                        MainWindow.Log($"[Call][WebRTC] DTMF sent: {digit}");
                    }
                    else
                    {
                        MainWindow.Log($"[Call][WebRTC] Cannot send DTMF: no active WebRTC call");
                    }
                }
                else
                {
                    // SIP звонок
                    if (_sipService != null && _sipService.IsInCall)
                    {
                        _sipService.SendDTMF(digit);
                    }
                    else
                    {
                        MainWindow.Log($"[Call][SIP] Cannot send DTMF: no active SIP call");
                    }
                }
            }
        }
        
        private void KeypadToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isKeypadVisible)
            {
                // Показать нумпад
                KeypadGrid.Visibility = Visibility.Visible;
                
                // Анимация увеличения ширины окна
                var widthAnimation = new DoubleAnimation(
                    ExpandedWidth,
                    TimeSpan.FromMilliseconds(250));
                widthAnimation.EasingFunction = new QuadraticEase 
                { 
                    EasingMode = EasingMode.EaseOut 
                };
                BeginAnimation(WidthProperty, widthAnimation);
                
                // Анимация увеличения высоты окна
                var heightAnimation = new DoubleAnimation(
                    ExpandedHeight,
                    TimeSpan.FromMilliseconds(250));
                heightAnimation.EasingFunction = new QuadraticEase 
                { 
                    EasingMode = EasingMode.EaseOut 
                };
                BeginAnimation(HeightProperty, heightAnimation);
                
                // Анимация показа нумпада
                var sb = (Storyboard)FindResource("ShowKeypadStoryboard");
                sb.Begin();
            }
            else
            {
                // Скрыть нумпад с анимацией
                var sb = (Storyboard)FindResource("HideKeypadStoryboard");
                sb.Completed += (s, _) =>
                {
                    KeypadGrid.Visibility = Visibility.Collapsed;
                };
                sb.Begin();
                
                // Анимация уменьшения ширины окна
                var widthAnimation = new DoubleAnimation(
                    DefaultWidth,
                    TimeSpan.FromMilliseconds(250));
                widthAnimation.EasingFunction = new QuadraticEase 
                { 
                    EasingMode = EasingMode.EaseOut 
                };
                BeginAnimation(WidthProperty, widthAnimation);
                
                // Анимация уменьшения высоты окна
                var heightAnimation = new DoubleAnimation(
                    DefaultHeight,
                    TimeSpan.FromMilliseconds(250));
                heightAnimation.EasingFunction = new QuadraticEase 
                { 
                    EasingMode = EasingMode.EaseOut 
                };
                BeginAnimation(HeightProperty, heightAnimation);
            }
            
            _isKeypadVisible = !_isKeypadVisible;
        }

        /// <summary>
        /// Создает ToneGenerator при первом использовании (ленивая инициализация)
        /// </summary>
        private void EnsureToneGenerator()
        {
            if (_toneGenerator == null)
            {
                _toneGenerator = new ToneGenerator();
            }
        }
        
        /// <summary>
        /// Проверяет, включена ли запись звонков в настройках
        /// </summary>
        private bool IsCallRecordingEnabled()
        {
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(json);
                    return settings?.EnableCallRecording ?? false;
                }
            }
            catch
            {
                // Игнорируем ошибки
            }
            return false;
        }
        
        /// <summary>
        /// Запускает запись WebRTC звонка
        /// </summary>
        private void StartWebRtcRecording()
        {
            if (!IsCallRecordingEnabled() || !_useWebRtc)
            {
                return;
            }
            
            // Проверяем, не запущена ли уже запись
            if (_webRtcRecorder != null && _webRtcRecorder.IsRecording)
            {
                var logPrefix = GetTransportLogPrefix();
                MainWindow.Log($"{logPrefix} WebRTC recording already started, skipping");
                return;
            }
            
            try
            {
                _webRtcRecorder?.Dispose();
                _webRtcRecorder = new WebRtcCallRecorder();
                _isStoppingRecording = false; // Сбрасываем флаг при создании нового рекордера
                var callStartTime = _isIncomingCall ? _incomingCallStartTime : _originalCallStartTime;
                _webRtcRecorder.StartRecording(_phoneNumber, callStartTime, AppDataHelper.GetRecordingsDirectory());
                
                // Сохраняем путь к файлу записи для Call Details
                _webRtcRecordingFilePath = _webRtcRecorder.RecordingFilePath;
                
                var logPrefix = GetTransportLogPrefix();
                MainWindow.Log($"{logPrefix} WebRTC call recording started for {_phoneNumber}");
                
                // Отправляем команду в JavaScript для начала записи
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await WebRtcService.Instance.SendCommandAsync(new { cmd = "startRecording" });
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[CallWindow] Error sending startRecording command: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallWindow] Error starting WebRTC recording: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Останавливает запись WebRTC звонка
        /// </summary>
        private void StopWebRtcRecording()
        {
            // Защита от повторных вызовов
            if (_isStoppingRecording)
            {
                return; // Уже останавливается, пропускаем
            }
            
            if (_webRtcRecorder == null)
            {
                return; // Рекордер уже остановлен или не был создан, не логируем
            }
            
            _isStoppingRecording = true; // Устанавливаем флаг
            
            try
            {
                // Сохраняем ссылку на рекордер и путь к файлу
                var recorder = _webRtcRecorder;
                var logPrefix = GetTransportLogPrefix();
                
                // Сохраняем путь к файлу перед остановкой (для Call Details)
                if (recorder != null && string.IsNullOrEmpty(_webRtcRecordingFilePath))
                {
                    _webRtcRecordingFilePath = recorder.RecordingFilePath;
                }
                
                if (recorder != null && recorder.IsRecording)
                {
                    MainWindow.Log($"{logPrefix} StopWebRtcRecording: stopping recording, recorder state: IsRecording={recorder.IsRecording}");
                    
                    // ВАЖНО: НЕ обнуляем _webRtcRecorder здесь - он нужен для обработки recording_complete
                    // Обнулим его только после успешного сохранения файла в recording_complete
                    
                    // Отправляем команду в JavaScript для остановки записи
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await WebRtcService.Instance.SendCommandAsync(new { cmd = "stopRecording" });
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"{logPrefix} Error sending stopRecording command: {ex.Message}");
                        }
                    });
                    
                    // Помечаем рекордер как "останавливается", но не Dispose пока не получим recording_complete
                    recorder.StopRecording();
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"{GetTransportLogPrefix()} Error stopping WebRTC recording: {ex.Message}");
                _isStoppingRecording = false; // Сбрасываем флаг при ошибке
            }
        }
        
        protected override void OnClosed(EventArgs e)
        {
            // Останавливаем ringback tone при закрытии окна
            _toneGenerator?.Stop();
            _toneGenerator?.Dispose();
            _toneGenerator = null;
            
            // Останавливаем запись WebRTC звонка (StopWebRtcRecording уже делает Dispose и устанавливает _webRtcRecorder = null)
            StopWebRtcRecording();
            
            // Отписываемся от событий WebRTC сервиса
            WebRtcService.Instance.Event -= OnWebRtcServiceEvent;
            
            // Останавливаем таймер
            _callTimer?.Stop();
            
            // Отписываемся от событий SipService
            if (_sipService != null)
            {
                // Сохраняем путь к записи SIP звонка перед отправкой деталей
                if (string.IsNullOrEmpty(_sipRecordingFilePath))
                {
                    _sipRecordingFilePath = _sipService.CurrentRecordingFilePath;
                    if (!string.IsNullOrEmpty(_sipRecordingFilePath))
                    {
                        MainWindow.Log($"[CallWindow] SIP recording file path saved (OnClosed): {_sipRecordingFilePath}");
                    }
                }
                
                _sipService.OnStatusChanged -= UpdateCallStatus;
                _sipService.OnCallEnded -= OnCallEnded;
                
                // Записываем время окончания гудков, если еще не записано
                CallWindowHelpers.UpdateRingbackEndTime(_ringbackStartTime, ref _ringbackEndTime);
                
                // Отправляем детальную информацию перед закрытием
                SendCallDetails();
                
                // Если это входящий звонок, который не был принят, помечаем как пропущенный
                if (_isIncomingCall && !_wasAnswered && !_isClosing)
                {
                    OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Missed, null);
                }
                
                // Запись звонка останавливается автоматически при Hangup() в SipService
                
                // Всегда вызываем Hangup при закрытии окна (чтобы остановить гудки)
                // даже если звонок не активен
                if (!_isClosing)
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
            
            // Для WebRTC также вызываем Hangup при закрытии
            if (_useWebRtc && !_isClosing)
            {
                try
                {
                    _ = WebRtcService.Instance.HangupAsync();
                }
                catch
                {
                    // Игнорируем ошибки
                }
            }
            
            base.OnClosed(e);
        }
        
        // Сохраняем последние отправленные детали для предотвращения избыточного логирования
        private string? _lastSentCallDetails = null;
        
        private void SendCallDetails()
        {
            // Отправляем детальную информацию о звонке
            // Используем исходное время начала звонка, которое не меняется
            var callTime = _isIncomingCall ? _incomingCallStartTime : _originalCallStartTime;
            
            // Вычисляем длительность звонка
            TimeSpan? duration = CallWindowHelpers.CalculateCallDuration(
                _isIncomingCall ? _incomingCallStartTime : _callStartTime,
                _answerTime,
                _wasAnswered,
                _isIncomingCall);
            
            var logPrefix = GetTransportLogPrefix();
            var detailsString = CallWindowHelpers.FormatCallDetailsString(
                _phoneNumber,
                callTime,
                _ringbackStartTime,
                _ringbackEndTime,
                _answerTime,
                _wasAnswered,
                duration,
                _endedBy,
                _technicalDetails.Count);
            
            // Логируем только при изменении
            if (_lastSentCallDetails != detailsString)
            {
                MainWindow.Log($"{logPrefix} SendCallDetails: {detailsString}");
                _lastSentCallDetails = detailsString;
            }
            
            // Получаем путь к записи
            string? recordingFilePath = CallWindowHelpers.GetRecordingFilePath(
                _sipRecordingFilePath,
                _sipService?.CurrentRecordingFilePath,
                _webRtcRecorder?.RecordingFilePath,
                _webRtcRecordingFilePath);
            
            // Логируем путь к записи для диагностики
            if (!string.IsNullOrEmpty(recordingFilePath))
            {
                bool fileExists = System.IO.File.Exists(recordingFilePath);
                MainWindow.Log($"{logPrefix} SendCallDetails: Recording file path: {recordingFilePath}, FileExists={fileExists}");
            }
            else
            {
                MainWindow.Log($"{logPrefix} SendCallDetails: WARNING - Recording file path is null or empty " +
                    $"(sipRecordingFilePath={_sipRecordingFilePath ?? "null"}, " +
                    $"sipService.CurrentRecordingFilePath={_sipService?.CurrentRecordingFilePath ?? "null"}, " +
                    $"webRtcRecorder.RecordingFilePath={_webRtcRecorder?.RecordingFilePath ?? "null"}, " +
                    $"webRtcRecordingFilePath={_webRtcRecordingFilePath ?? "null"})");
            }
            
            // Передаем транспорт и идентификаторы из контекста
            OnCallDetailsChanged?.Invoke(
                _phoneNumber,
                callTime,
                _ringbackStartTime,
                _ringbackEndTime,
                _answerTime,
                _wasAnswered,
                _endedBy,
                _technicalDetails.Count > 0 ? _technicalDetails : null,
                duration,
                recordingFilePath,
                _callContext.Transport,
                _callContext.SipCallId,
                _callContext.WebRtcSessionId
            );
        }
        
        // WebRTC Methods - использует WebRtcService
        public CallWindow(WebRtcConfig webRtcConfig, string phoneNumber, bool isIncomingCall = false, DateTime? callStartTime = null, string? webRtcSessionId = null)
        {
            InitializeComponent();
            _webRtcConfig = webRtcConfig;
            _phoneNumber = phoneNumber;
            _isIncomingCall = isIncomingCall;
            _useWebRtc = true;
            _callStartTime = callStartTime ?? DateTime.Now;
            _originalCallStartTime = _callStartTime;
            _incomingCallStartTime = isIncomingCall ? DateTime.Now : _callStartTime;
            _callWindowStartTime = DateTime.Now;
            
            // Создаем контекст звонка с транспортом WebRTC
            _callContext = new CallContext(CallTransport.WebRtc, phoneNumber, _callStartTime, webRtcSessionId: webRtcSessionId);
            
            MainWindow.Log($"[Call][WebRTC] CallWindow constructor: PhoneNumber: {phoneNumber}, IsIncomingCall: {isIncomingCall}, SessionId: {webRtcSessionId ?? "none"}");
            
            UpdateTransportBadge();
            CallerNameTextBlock.Text = phoneNumber;
            
            // НЕ показываем WebRTC контейнер - используем WebRtcService
            // WebRtcContainer остается скрытым, UI управляется через CallWindow
            
            if (_isIncomingCall)
            {
                CallStatusTextBlock.Text = $"Incoming call from {phoneNumber}";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                ShowIncomingCallButtons();
            }
            else
            {
                CallStatusTextBlock.Text = "Connecting...";
                ShowCallControls();
            }
            
            // Подписываемся на события WebRTC сервиса
            WebRtcService.Instance.Event += OnWebRtcServiceEvent;
            
            KeyDown += CallWindow_KeyDown;
            Focusable = true;
            Focus();
        }
        
        /// <summary>
        /// Обработчик событий WebRTC сервиса
        /// </summary>
        private void OnWebRtcServiceEvent(WebRtcEventDto dto)
        {
            try
            {
                // Обновляем sessionId в контексте, если он был передан
                if (!string.IsNullOrEmpty(dto.SessionId) && _callContext.Transport == CallTransport.WebRtc)
                {
                    _callContext = new CallContext(
                        CallTransport.WebRtc,
                        _callContext.RemoteNumber,
                        _callContext.StartedAt,
                        webRtcSessionId: dto.SessionId
                    );
                }
                
                Dispatcher.Invoke(() =>
                {
                    var logPrefix = GetTransportLogPrefix();
                    var timestamp = $"[{DateTime.Now:HH:mm:ss.fff}]";
                    
                    switch (dto.Type)
                    {
                        case "incoming":
                            var callerNumber = dto.CallerNumber ?? "Unknown";
                            var incomingDetail = $"{timestamp} Incoming call from {callerNumber}";
                            _technicalDetails.Add(incomingDetail);
                            MainWindow.Log($"{logPrefix} Incoming call from {callerNumber}");
                            break;
                            
                        case "new_session":
                            var sessionDetail = $"{timestamp} New WebRTC session created";
                            if (!string.IsNullOrEmpty(dto.SessionId))
                            {
                                sessionDetail += $" (SessionId: {dto.SessionId})";
                            }
                            _technicalDetails.Add(sessionDetail);
                            MainWindow.Log($"{logPrefix} New session: {dto.SessionId ?? "none"}");
                            break;
                            
                        case "call_progress":
                            var progressDetail = $"{timestamp} Call in progress (WebRTC)";
                            _technicalDetails.Add(progressDetail);
                            MainWindow.Log($"{logPrefix} Call in progress...");
                            CallStatusTextBlock.Text = "Calling...";
                            break;
                            
                        case "ringing":
                            if (!_ringbackStartTime.HasValue)
                            {
                                _ringbackStartTime = DateTime.Now;
                                // Отправляем обновление деталей при начале гудков
                                SendCallDetails();
                            }
                            
                            // Воспроизводим ringback tone для WebRTC звонков
                            EnsureToneGenerator();
                            _toneGenerator?.PlayRingbackTone();
                            
                            var ringingDetail = $"{timestamp} Remote party ringing (WebRTC)";
                            _technicalDetails.Add(ringingDetail);
                            MainWindow.Log($"{logPrefix} Remote party ringing");
                            CallStatusTextBlock.Text = "Ringing...";
                            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                            break;
                            
                        case "call_accepted":
                        case "call_confirmed":
                            // Останавливаем ringback tone при принятии звонка
                            _toneGenerator?.Stop();
                            
                            CallWindowHelpers.UpdateAnswerTime(ref _answerTime, ref _wasAnswered);
                            if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
                            {
                                _ringbackEndTime = _answerTime;
                            }
                            _callStartTime = DateTime.Now;
                            
                            // Запускаем запись звонка при принятии (только если еще не запущена)
                            if (_webRtcRecorder == null || !_webRtcRecorder.IsRecording)
                            {
                                StartWebRtcRecording();
                            }
                            
                            var acceptedDetail = $"{timestamp} Call accepted/confirmed (WebRTC)";
                            if (!string.IsNullOrEmpty(dto.SessionId))
                            {
                                acceptedDetail += $" (SessionId: {dto.SessionId})";
                            }
                            _technicalDetails.Add(acceptedDetail);
                            
                            MainWindow.Log($"{logPrefix} ✓ Call accepted/confirmed");
                            if (_isIncomingCall)
                            {
                                _isIncomingCall = false;
                                ShowCallControls();
                            }
                            StartCallTimer();
                            CallStatusTextBlock.Text = "00:00:00";
                            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                            break;
                            
                        case "call_failed":
                            // Останавливаем ringback tone при неудаче звонка
                            _toneGenerator?.Stop();
                            
                            // Останавливаем запись звонка
                            StopWebRtcRecording();
                            
                            // Записываем время окончания гудков, если еще не записано
                            if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
                            {
                                _ringbackEndTime = DateTime.Now;
                            }
                            
                            // Извлекаем сообщение об ошибке безопасно, без логирования всего Data (может содержать большие массивы)
                            string? failMsg = dto.Message ?? dto.Cause;
                            if (string.IsNullOrEmpty(failMsg) && dto.Data != null && dto.Data.HasValue)
                            {
                                // Пробуем извлечь только текстовые поля, не вызывая ToString() на всем объекте
                                if (dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (dto.Data.Value.TryGetProperty("message", out var msgEl))
                                    {
                                        failMsg = msgEl.GetString();
                                    }
                                    else if (dto.Data.Value.TryGetProperty("cause", out var causeEl))
                                    {
                                        failMsg = causeEl.GetString();
                                    }
                                }
                                else if (dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    failMsg = dto.Data.Value.GetString();
                                }
                            }
                            failMsg ??= "Unknown";
                            var failDetail = $"{timestamp} Call failed: {failMsg}";
                            if (!string.IsNullOrEmpty(dto.Cause))
                            {
                                failDetail += $" (Cause: {dto.Cause})";
                            }
                            _technicalDetails.Add(failDetail);
                            
                            MainWindow.Log($"{logPrefix} ✗ Call failed: {failMsg}");
                            CallStatusTextBlock.Text = $"Call Failed: {failMsg}";
                            _endedBy = CallEndedBy.RemoteParty; // Обычно удаленная сторона завершает при ошибке
                            SendCallDetails(); // Отправляем детали перед закрытием
                            _ = Task.Delay(1000).ContinueWith(_ =>
                            {
                                Dispatcher.Invoke(() => Close());
                            });
                            break;
                            
                        case "call_ended":
                            // Останавливаем ringback tone при завершении звонка
                            _toneGenerator?.Stop();
                            
                            // Останавливаем запись звонка
                            StopWebRtcRecording();
                            
                            // Записываем время окончания гудков, если еще не записано
                            if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
                            {
                                _ringbackEndTime = DateTime.Now;
                            }
                            
                            var endedDetail = $"{timestamp} Call ended (WebRTC)";
                            if (!string.IsNullOrEmpty(dto.Cause))
                            {
                                endedDetail += $" (Cause: {dto.Cause})";
                            }
                            if (!string.IsNullOrEmpty(dto.Message))
                            {
                                endedDetail += $" (Message: {dto.Message})";
                            }
                            _technicalDetails.Add(endedDetail);
                            
                            // Определяем, кто завершил звонок (если не определено ранее)
                            if (_endedBy == CallEndedBy.Unknown)
                            {
                                // Если звонок был принят и завершен, скорее всего удаленная сторона
                                _endedBy = _wasAnswered ? CallEndedBy.RemoteParty : CallEndedBy.LocalUser;
                            }
                            
                            MainWindow.Log($"{logPrefix} Call ended");
                            SendCallDetails(); // Отправляем детали перед завершением
                            OnCallEnded();
                            break;
                            
                        case "error":
                            var errorName = dto.Name ?? "Unknown";
                            var errorPhase = dto.Phase ?? "unknown";
                            var errorMsg = dto.Message ?? "No message";
                            var errorDetail = $"{timestamp} WebRTC Error: {errorName} (Phase: {errorPhase}, Message: {errorMsg})";
                            if (!string.IsNullOrEmpty(dto.Stack))
                            {
                                errorDetail += $"\n  Stack: {dto.Stack}";
                            }
                            _technicalDetails.Add(errorDetail);
                            MainWindow.Log($"{logPrefix} ✗ ERROR: {errorName} - {errorMsg} (phase: {errorPhase})");
                            break;
                            
                        case "ice_connection_state_change":
                            // Извлекаем состояние ICE безопасно, без логирования всего Data
                            string iceState = "unknown";
                            if (dto.Data != null && dto.Data.HasValue)
                            {
                                if (dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    iceState = dto.Data.Value.GetString() ?? "unknown";
                                }
                                else if (dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (dto.Data.Value.TryGetProperty("state", out var stateEl))
                                    {
                                        iceState = stateEl.GetString() ?? "unknown";
                                    }
                                }
                            }
                            var iceDetail = $"{timestamp} ICE connection state: {iceState}";
                            _technicalDetails.Add(iceDetail);
                            MainWindow.Log($"{logPrefix} ICE state: {iceState}");
                            break;
                            
                        case "audio_connected":
                            // Останавливаем ringback tone при подключении аудио (звонок принят)
                            _toneGenerator?.Stop();
                            
                            // Записываем время ответа, если еще не записано
                            if (!_answerTime.HasValue)
                            {
                                _answerTime = DateTime.Now;
                                if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
                                {
                                    _ringbackEndTime = _answerTime;
                                }
                            }
                            
                            // Если звонок еще не был помечен как принятый, помечаем его
                            if (!_wasAnswered)
                            {
                                _wasAnswered = true;
                                _callStartTime = DateTime.Now;
                                
                                // Запускаем запись звонка при подключении аудио (только если еще не запущена)
                                if (_webRtcRecorder == null || !_webRtcRecorder.IsRecording)
                                {
                                    StartWebRtcRecording();
                                }
                            }
                            
                            var audioDetail = $"{timestamp} Audio track connected (WebRTC)";
                            _technicalDetails.Add(audioDetail);
                            MainWindow.Log($"{logPrefix} Audio connected");
                            
                            // Если таймер еще не запущен, запускаем его
                            if (_callTimer == null || !_callTimer.IsEnabled)
                            {
                                StartCallTimer();
                                CallStatusTextBlock.Text = "00:00:00";
                                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                            }
                            break;
                            
                        case "recording_start":
                            // Обрабатываем начало передачи большого файла (метаданные)
                            if (_webRtcRecorder != null && _webRtcRecorder.IsRecording)
                            {
                                try
                                {
                                    if (dto.Data != null && dto.Data.HasValue)
                                    {
                                        // Работаем напрямую с JsonElement, не вызывая ToString()
                                        var dataJson = dto.Data.Value;
                                        if (dataJson.TryGetProperty("totalSize", out var totalSizeEl) &&
                                            dataJson.TryGetProperty("totalChunks", out var totalChunksEl) &&
                                            dataJson.TryGetProperty("hash", out var hashEl))
                                        {
                                            int totalSize = totalSizeEl.GetInt32();
                                            int totalChunks = totalChunksEl.GetInt32();
                                            string hash = hashEl.GetString() ?? "";
                                            
                                            _webRtcRecorder.HandleRecordingStart(totalSize, totalChunks, hash);
                                            MainWindow.Log($"{logPrefix} Recording start: {totalSize} bytes, {totalChunks} chunks");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    MainWindow.Log($"{logPrefix} Error processing recording_start: {ex.Message}");
                                }
                            }
                            break;
                            
                        case "recording_chunk":
                            // Обрабатываем чанк большого файла
                            if (_webRtcRecorder != null && _webRtcRecorder.IsRecording)
                            {
                                try
                                {
                                    if (dto.Data != null && dto.Data.HasValue)
                                    {
                                        // Работаем напрямую с JsonElement, не вызывая ToString() чтобы не логировать данные чанка
                                        var dataJson = dto.Data.Value;
                                        if (dataJson.TryGetProperty("chunkIndex", out var chunkIndexEl) &&
                                            dataJson.TryGetProperty("data", out var dataEl) &&
                                            dataJson.TryGetProperty("offset", out var offsetEl))
                                        {
                                            int chunkIndex = chunkIndexEl.GetInt32();
                                            int offset = offsetEl.GetInt32();
                                            
                                            // Конвертируем массив чисел в byte[]
                                            if (dataEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                            {
                                                var byteList = new List<byte>();
                                                foreach (var item in dataEl.EnumerateArray())
                                                {
                                                    if (item.ValueKind == System.Text.Json.JsonValueKind.Number)
                                                    {
                                                        byteList.Add((byte)item.GetInt32());
                                                    }
                                                }
                                                
                                                if (byteList.Count > 0)
                                                {
                                                    _webRtcRecorder.HandleRecordingChunk(chunkIndex, byteList.ToArray(), offset);
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    MainWindow.Log($"{logPrefix} Error processing recording_chunk: {ex.Message}");
                                }
                            }
                            break;
                            
                        case "recording_complete":
                            // Обрабатываем полный файл записи из JavaScript MediaRecorder
                            if (_webRtcRecorder != null)
                            {
                                try
                                {
                                    if (dto.Data != null && dto.Data.HasValue)
                                    {
                                        // Работаем напрямую с JsonElement, не вызывая ToString() чтобы не логировать весь массив audioData
                                        var dataJson = dto.Data.Value;
                                        string? hash = null;
                                        
                                        if (dataJson.TryGetProperty("hash", out var hashEl))
                                        {
                                            hash = hashEl.GetString();
                                        }
                                        
                                        byte[]? audioBytes = null;
                                        
                                        // Проверяем, есть ли audioData (массив чисел для маленьких файлов)
                                        if (dataJson.TryGetProperty("audioData", out var audioDataEl))
                                        {
                                            if (audioDataEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                            {
                                                // Массив чисел - конвертируем в byte[]
                                                var byteList = new List<byte>();
                                                foreach (var item in audioDataEl.EnumerateArray())
                                                {
                                                    if (item.ValueKind == System.Text.Json.JsonValueKind.Number)
                                                    {
                                                        byteList.Add((byte)item.GetInt32());
                                                    }
                                                }
                                                audioBytes = byteList.ToArray();
                                            }
                                            else if (audioDataEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                            {
                                                // Base64 строка (для обратной совместимости)
                                                var base64Audio = audioDataEl.GetString();
                                                if (!string.IsNullOrEmpty(base64Audio))
                                                {
                                                    audioBytes = Convert.FromBase64String(base64Audio);
                                                }
                                            }
                                        }
                                        
                                        if (audioBytes != null && audioBytes.Length > 0)
                                            {
                                                MainWindow.Log($"{logPrefix} recording_complete: {audioBytes.Length / 1024} KB received, converting...");
                                                // Сохраняем файл и конвертируем в WAV асинхронно в фоне
                                                var recorder = _webRtcRecorder;
#pragma warning disable CS4014 // Намеренно не ожидаем Task.Run - выполнение в фоне, не блокируя UI
                                                _ = Task.Run(async () =>
                                                {
                                                    try
                                                    {
                                                        if (recorder != null)
                                                        {
                                                            await recorder.SaveCompleteRecordingAsync(audioBytes, hash);
                                                            recorder.StopRecording();
                                                            
                                                            // Обновляем сохраненный путь к файлу
                                                            _webRtcRecordingFilePath = recorder.RecordingFilePath;
                                                            
                                                            // Обновляем CallHistoryItem с путем к записи через событие (в UI потоке)
                                                            if (!string.IsNullOrEmpty(_webRtcRecordingFilePath))
                                                            {
                                                                Dispatcher.BeginInvoke(new Action(() =>
                                                                {
                                                                    try
                                                                    {
                                                                        SendCallDetails();
                                                                    }
                                                                    catch (Exception ex2)
                                                                    {
                                                                        MainWindow.Log($"{logPrefix} Error updating call details: {ex2.Message}");
                                                                    }
                                                                }), System.Windows.Threading.DispatcherPriority.Background);
                                                            }
                                                            
                                                            // Только теперь Dispose рекордера и обнуляем ссылку
                                                            recorder.Dispose();
                                                            if (_webRtcRecorder == recorder)
                                                            {
                                                                _webRtcRecorder = null;
                                                                _isStoppingRecording = false; // Сбрасываем флаг после завершения
                                                            }
                                                        }
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        MainWindow.Log($"{logPrefix} Error saving complete recording: {ex.Message}");
                                                        MainWindow.Log($"{logPrefix} Error stack: {ex.StackTrace}");
                                                    }
                                                });
#pragma warning restore CS4014
                                        }
                                        else if (!string.IsNullOrEmpty(hash))
                                        {
                                            MainWindow.Log($"{logPrefix} recording_complete: no audioData, but hash present - file was sent in chunks, finalizing...");
                                            // Файл был передан чанками, финализируем
                                            var recorder = _webRtcRecorder;
#pragma warning disable CS4014 // Намеренно не ожидаем Task.Run - выполнение в фоне, не блокируя UI
                                            _ = Task.Run(async () =>
                                            {
                                                try
                                                {
                                                    if (recorder != null)
                                                    {
                                                        // Собираем файл из чанков (данные уже собраны в HandleRecordingChunk)
                                                        await recorder.SaveCompleteRecordingAsync(Array.Empty<byte>(), hash);
                                                        recorder.StopRecording();
                                                        
                                                        // Обновляем сохраненный путь к файлу
                                                        _webRtcRecordingFilePath = recorder.RecordingFilePath;
                                                        
                                                        MainWindow.Log($"{logPrefix} Recording complete (chunks) and saved: {_webRtcRecordingFilePath}");
                                                        
                                                        // Обновляем CallHistoryItem с путем к записи через событие (в UI потоке)
                                                        if (!string.IsNullOrEmpty(_webRtcRecordingFilePath))
                                                        {
                                                            Dispatcher.BeginInvoke(new Action(() =>
                                                            {
                                                                try
                                                                {
                                                                    SendCallDetails();
                                                                }
                                                                catch (Exception ex2)
                                                                {
                                                                    MainWindow.Log($"{logPrefix} Error updating call details: {ex2.Message}");
                                                                }
                                                            }), System.Windows.Threading.DispatcherPriority.Background);
                                                        }
                                                        
                                                        // Только теперь Dispose рекордера и обнуляем ссылку
                                                        recorder.Dispose();
                                                        if (_webRtcRecorder == recorder)
                                                        {
                                                            _webRtcRecorder = null;
                                                            MainWindow.Log($"{logPrefix} Recording recorder disposed and cleared (chunks)");
                                                        }
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    MainWindow.Log($"{logPrefix} Error finalizing recording: {ex.Message}");
                                                }
                                            });
#pragma warning restore CS4014
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    MainWindow.Log($"{logPrefix} Error processing recording_complete: {ex.Message}");
                                }
                            }
                            break;
                            
                        case "recording_started":
                            MainWindow.Log($"{logPrefix} Recording started (JavaScript)");
                            break;
                            
                        case "recording_stopped":
                            MainWindow.Log($"{logPrefix} Recording stopped (JavaScript) - waiting for recording_complete event");
                            break;
                            
                        default:
                            // Добавляем другие события как технические детали
                            var eventDetail = $"{timestamp} WebRTC Event: {dto.Type}";
                            if (!string.IsNullOrEmpty(dto.SessionId))
                            {
                                eventDetail += $" (SessionId: {dto.SessionId})";
                            }
                            if (!string.IsNullOrEmpty(dto.Message))
                            {
                                eventDetail += $" - {dto.Message}";
                            }
                            _technicalDetails.Add(eventDetail);
                            MainWindow.Log($"{logPrefix} Event: {dto.Type}");
                            break;
                    }
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallWindow] ERROR in OnWebRtcServiceEvent: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    _technicalDetails.Add($"[{DateTime.Now:HH:mm:ss.fff}] ERROR processing WebRTC event: {ex.Message}");
                });
            }
        }
        
        public async Task WebRtcMakeCallAsync(string number)
        {
            try
            {
                MainWindow.Log($"[WebRTC CallWindow] Making call to: {number}");
                
                // Добавляем техническую деталь о начале звонка
                _technicalDetails.Add($"[{DateTime.Now:HH:mm:ss.fff}] Initiating WebRTC call to {number}");
                
                // Используем WebRTC сервис
                if (!WebRtcService.Instance.IsReadyForCalls)
                {
                    var errorDetail = $"[{DateTime.Now:HH:mm:ss.fff}] ERROR: WebRTC service not ready";
                    _technicalDetails.Add(errorDetail);
                    MainWindow.Log("[WebRTC CallWindow] ERROR: WebRTC service not ready");
                    throw new InvalidOperationException("WebRTC service not ready");
                }
                
                // Для MikoPBX добавляем -WS к номеру
                string targetNumber = number;
                if (!targetNumber.EndsWith("-WS", StringComparison.OrdinalIgnoreCase))
                {
                    targetNumber = $"{number}-WS";
                }
                _technicalDetails.Add($"[{DateTime.Now:HH:mm:ss.fff}] Calling target: {targetNumber} (MikoPBX WebRTC endpoint)");
                MainWindow.Log($"[WebRTC CallWindow] Calling target: {targetNumber} (MikoPBX WebRTC endpoint)");
                
                await WebRtcService.Instance.MakeCallAsync(targetNumber);
                _technicalDetails.Add($"[{DateTime.Now:HH:mm:ss.fff}] Call initiated via WebRTC service");
                MainWindow.Log("[WebRTC CallWindow] Call initiated via WebRTC service");
            }
            catch (Exception ex)
            {
                var errorDetail = $"[{DateTime.Now:HH:mm:ss.fff}] ERROR: Failed to make WebRTC call: {ex.Message}";
                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    errorDetail += $"\n  Stack: {ex.StackTrace}";
                }
                _technicalDetails.Add(errorDetail);
                MainWindow.Log($"[WebRTC CallWindow] ERROR: Failed to make WebRTC call: {ex.Message}");
                MainWindow.Log($"[WebRTC CallWindow] Stack trace: {ex.StackTrace}");
                throw;
            }
        }
        
        private async Task WebRtcAnswerAsync()
        {
            try
            {
                MainWindow.Log("[WebRTC CallWindow] Answering incoming call...");
                
                // Используем WebRTC сервис
                if (!WebRtcService.Instance.IsReadyForCalls)
                {
                    MainWindow.Log("[WebRTC CallWindow] ERROR: WebRTC service not ready");
                    throw new InvalidOperationException("WebRTC service not ready");
                }
                
                await WebRtcService.Instance.AnswerAsync();
                MainWindow.Log("[WebRTC CallWindow] Answer command sent via WebRTC service");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC CallWindow] ERROR: Failed to answer WebRTC call: {ex.Message}");
                MainWindow.Log($"[WebRTC CallWindow] Stack trace: {ex.StackTrace}");
                throw;
            }
        }
        
        private async Task WebRtcHangupAsync()
        {
            try
            {
                MainWindow.Log("[WebRTC CallWindow] Hanging up call...");
                
                // Получаем sessionId из контекста звонка
                string? sessionId = null;
                if (_callContext != null && _callContext.Transport == CallTransport.WebRtc)
                {
                    sessionId = _callContext.WebRtcSessionId;
                }
                
                await WebRtcService.Instance.HangupAsync(sessionId);
                MainWindow.Log($"[WebRTC CallWindow] Hangup command sent via WebRTC service (sessionId: {sessionId ?? "null"})");
                
                // Даем время на обработку завершения звонка
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC CallWindow] ERROR: Failed to hangup WebRTC call: {ex.Message}");
                MainWindow.Log($"[WebRTC CallWindow] Stack trace: {ex.StackTrace}");
            }
        }
        
        // Этот обработчик больше не используется, т.к. события приходят в MainWindow
        // Оставлен для обратной совместимости, если нужно будет обрабатывать события локально
        private void WebRtcWebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(json))
                {
                    MainWindow.Log("[WebRTC CallWindow] Received empty message from WebView2");
                    return;
                }
                
                MainWindow.Log($"[WebRTC CallWindow] Received event from WebView2: {json}");
                
                var evt = JsonSerializer.Deserialize<WebRtcEvent>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (evt == null)
                {
                    MainWindow.Log("[WebRTC CallWindow] Failed to deserialize event");
                    return;
                }
                
                Dispatcher.Invoke(() =>
                {
                    switch (evt.Type)
                    {
                        case "ua_started":
                            MainWindow.Log("[WebRTC CallWindow] JsSIP UA started, connecting to ATS server...");
                            break;
                        case "ua_connected":
                            MainWindow.Log("[WebRTC CallWindow] ✓ WebSocket connected to ATS server");
                            CallStatusTextBlock.Text = "WebRTC Connected";
                            break;
                        case "ua_registered":
                            MainWindow.Log($"[WebRTC CallWindow] ✓ Successfully registered on ATS server as {_webRtcConfig?.SipUri}");
                            CallStatusTextBlock.Text = "WebRTC Registered";
                            break;
                        case "ua_registration_failed":
                            // Извлекаем сообщение об ошибке безопасно, без логирования всего Data
                            string errorData = "Unknown error";
                            if (evt.Data != null && evt.Data.HasValue)
                            {
                                if (evt.Data.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    errorData = evt.Data.Value.GetString() ?? "Unknown error";
                                }
                                else if (evt.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (evt.Data.Value.TryGetProperty("message", out var msgEl))
                                    {
                                        errorData = msgEl.GetString() ?? "Unknown error";
                                    }
                                    else if (evt.Data.Value.TryGetProperty("cause", out var causeEl))
                                    {
                                        errorData = causeEl.GetString() ?? "Unknown error";
                                    }
                                }
                            }
                            MainWindow.Log($"[WebRTC CallWindow] ✗ Registration failed on ATS server: {errorData}");
                            CallStatusTextBlock.Text = "Registration Failed";
                            break;
                        case "ua_unregistered":
                            MainWindow.Log("[WebRTC CallWindow] Unregistered from ATS server");
                            break;
                        case "ua_disconnected":
                            MainWindow.Log("[WebRTC CallWindow] ✗ WebSocket disconnected from ATS server");
                            break;
                        case "new_session":
                            MainWindow.Log("[WebRTC CallWindow] New RTC session created");
                            if (evt.Data.HasValue)
                            {
                                var direction = evt.Data.Value.GetProperty("direction").GetString();
                                if (direction == "incoming")
                                {
                                    MainWindow.Log($"[WebRTC CallWindow] Incoming call from {_phoneNumber}");
                                    CallStatusTextBlock.Text = $"Incoming call from {_phoneNumber}";
                                }
                            }
                            break;
                        case "call_progress":
                            MainWindow.Log($"{GetTransportLogPrefix()} Call in progress...");
                            CallStatusTextBlock.Text = "Calling...";
                            break;
                        case "call_accepted":
                        case "call_confirmed":
                            MainWindow.Log($"{GetTransportLogPrefix()} ✓ Call accepted/confirmed");
                            _wasAnswered = true;
                            _callStartTime = DateTime.Now;
                            if (_isIncomingCall)
                            {
                                _isIncomingCall = false;
                                ShowCallControls();
                            }
                            StartCallTimer();
                            CallStatusTextBlock.Text = "00:00:00";
                            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                            break;
                        case "call_failed":
                            // Извлекаем сообщение об ошибке безопасно, без логирования всего Data
                            string failData = "Unknown error";
                            if (evt.Data != null && evt.Data.HasValue)
                            {
                                if (evt.Data.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    failData = evt.Data.Value.GetString() ?? "Unknown error";
                                }
                                else if (evt.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (evt.Data.Value.TryGetProperty("message", out var msgEl))
                                    {
                                        failData = msgEl.GetString() ?? "Unknown error";
                                    }
                                    else if (evt.Data.Value.TryGetProperty("cause", out var causeEl))
                                    {
                                        failData = causeEl.GetString() ?? "Unknown error";
                                    }
                                }
                            }
                            MainWindow.Log($"{GetTransportLogPrefix()} ✗ Call failed: {failData}");
                            CallStatusTextBlock.Text = "Call Failed";
                            _ = System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ =>
                            {
                                Dispatcher.Invoke(() => Close());
                            });
                            break;
                        case "call_ended":
                            MainWindow.Log($"{GetTransportLogPrefix()} Call ended");
                            OnCallEnded();
                            break;
                        case "error":
                            // Извлекаем сообщение об ошибке безопасно, без логирования всего Data
                            string errorMsg = "Unknown error";
                            if (evt.Data != null && evt.Data.HasValue)
                            {
                                if (evt.Data.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    errorMsg = evt.Data.Value.GetString() ?? "Unknown error";
                                }
                                else if (evt.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (evt.Data.Value.TryGetProperty("message", out var msgEl))
                                    {
                                        errorMsg = msgEl.GetString() ?? "Unknown error";
                                    }
                                    else if (evt.Data.Value.TryGetProperty("name", out var nameEl))
                                    {
                                        errorMsg = nameEl.GetString() ?? "Unknown error";
                                    }
                                }
                            }
                            MainWindow.Log($"{GetTransportLogPrefix()} ✗ ERROR: {errorMsg}");
                            CallStatusTextBlock.Text = $"Error: {errorMsg}";
                            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                            break;
                        default:
                            MainWindow.Log($"{GetTransportLogPrefix()} Unknown event type: {evt.Type}");
                            break;
                    }
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC CallWindow] ERROR: Failed to process WebRTC message: {ex.Message}");
                MainWindow.Log($"[WebRTC CallWindow] Stack trace: {ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Обновляет бейдж транспорта в UI
        /// </summary>
        private void UpdateTransportBadge()
        {
            try
            {
                // Ищем TextBlock для отображения транспорта (если он есть в XAML)
                // Если нет, можно добавить в XAML или использовать существующий элемент
                var transportText = _callContext.Transport == CallTransport.WebRtc ? "WebRTC" : "SIP";
                var transportId = _callContext.Transport == CallTransport.WebRtc ? _callContext.WebRtcSessionId : _callContext.SipCallId;
                
                // Логируем с информацией о транспорте
                MainWindow.Log($"[Call][{transportText}] Transport badge updated: {transportText}, ID: {transportId ?? "none"}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallWindow] ERROR updating transport badge: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Получает строку для логирования с информацией о транспорте
        /// </summary>
        private string GetTransportLogPrefix()
        {
            var transport = _callContext.Transport == CallTransport.WebRtc ? "WebRTC" : "SIP";
            var id = _callContext.Transport == CallTransport.WebRtc ? _callContext.WebRtcSessionId : _callContext.SipCallId;
            return $"[Call][{transport}]{(id != null ? $"[id={id}]" : "")}";
        }
    }
    
    // WebRTC Configuration
    public class WebRtcConfig
    {
        public string WsUri { get; set; } = "";      // "wss://pbx.example.com:8089/ws"
        public string SipUri { get; set; } = "";     // "sip:1001@pbx.example.com"
        public string Password { get; set; } = "";   // пароль расширения
    }
    
    // WebRTC Event
    public class WebRtcEvent
    {
        public string Type { get; set; } = "";
        public JsonElement? Data { get; set; }
    }
    
    // WebRTC Call State
    public enum WebRtcCallState
    {
        Idle,           // Нет активного звонка
        Ringing,        // Входящий звонок (звонят нам)
        Calling,        // Исходящий звонок (мы звоним)
        Connected,      // Звонок установлен
        Ending,         // Звонок завершается
        Ended           // Звонок завершен
    }
}

