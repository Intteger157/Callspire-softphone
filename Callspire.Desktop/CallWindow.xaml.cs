#if WINDOWS
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;

namespace Softphone
{
    public partial class CallWindow : Window
    {
        // Событие для обновления статуса входящего звонка в истории
        public event Action<string, DateTime, CallStatus, TimeSpan?>? OnIncomingCallStatusChanged;
        
        // Событие для обновления детальной информации о звонке
        public event Action<string, DateTime, DateTime?, DateTime?, DateTime?, bool, CallEndedBy, List<string>?, TimeSpan?, string?, CallTransport?, string?, string?, int?>? OnCallDetailsChanged;
        private SipService? _sipService;
        private string _phoneNumber;
        private bool _isMuted = false;
        private bool _isOnHold = false;
        private DateTime _callStartTime;
        private DateTime _originalCallStartTime; // Исходное время начала звонка (не меняется при подключении)
        private System.Windows.Threading.DispatcherTimer? _callTimer;
        private System.Windows.Threading.DispatcherTimer? _ringbackUiTimer; // UI timer while ringing (pre-connect)
        private bool _isKeypadVisible = false;
        private bool _isIncomingCall = false; // Флаг для входящего звонка
        /// <summary>Set at construction: outbound vs inbound UI. Not cleared when remote answers (unlike <see cref="_isIncomingCall"/>).</summary>
        private readonly bool _openedAsOutgoingCall;
        private DateTime _incomingCallStartTime; // Время начала входящего звонка для истории
        private bool _wasAnswered = false; // Флаг, был ли звонок принят

        private static void ObserveTaskFault(Task? task, string context)
        {
            if (task == null) return;
            task.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    MainWindow.Log($"[CallWindow] {context}: {t.Exception.GetBaseException().Message}");
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        private static void FireAndForget(Task task, string context) => ObserveTaskFault(task, context);
        
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

        /// <summary>
        /// Конкретный экземпляр WebRTC-сервиса (main/secondary), используемый этим окном.
        /// Если null, окно работает в SIP-режиме.
        /// </summary>
        private WebRtcService? _webRtcService;
        private bool _webRtcIceConnected = false;
        private bool _webRtcAcceptedPending = false;
        private bool _webRtcRemoteAudioStarted = false;
        private int _webRtcSdpStableCount = 0;
        /// <summary>Prevents duplicate FinalizeWebRtcConnected (ICE + remote_audio_started used to run twice).</summary>
        private bool _webRtcConnectedFinalized = false;
        
        // WebRTC media establishment watchdog: SIP signaling can confirm but ICE may never connect
        // behind strict NAT/firewall without TURN. Without a timeout, UI can remain in "Calling" indefinitely.
        private System.Windows.Threading.DispatcherTimer? _webRtcMediaConnectTimer;
        private DateTime? _webRtcAcceptedPendingSinceUtc;
        private static readonly TimeSpan WebRtcMediaConnectTimeout = TimeSpan.FromSeconds(12);

        private const int StandardConnectingTimeoutMs = 25000;
        private const int OriginateConnectingTimeoutMs = 90000;
        private CancellationTokenSource? _connectingTimeoutCts;
        
        // Ringback tone is managed globally via RingbackToneService (singleton)
        
        // WebRTC call recorder
        private WebRtcCallRecorder? _webRtcRecorder;
        private string? _webRtcRecordingFilePath; // Сохраняем путь к файлу записи для Call Details
        private bool _isStoppingRecording = false; // Флаг для предотвращения повторных вызовов StopWebRtcRecording

        // Recording events can be very frequent (chunks). Processing them on UI thread can freeze the app.
        // We offload them to a single background worker queue.
        private readonly ConcurrentQueue<WebRtcEventDto> _recordingEventQueue = new();
        private int _recordingWorkerRunning = 0;
        
        // SIP call recording removed (WebRTC-only)
        
        // Call context with transport information
        private CallContext _callContext = null!; // Инициализируется в конструкторах

        // Outbound CallerID from P-Asserted-Identity (the number PBX/carrier presents to the remote party)
        private string? _outboundCallerId;

        /// <summary>PBX Originate: WebRTC leg to PBX is not the same as remote party answering.</summary>
        private bool _isOriginateCall = false;
        private bool _sipOriginateAnswered;
        private bool _originateRemoteAnswered = false;

        // AmoCRM lead ID found during call (for optimization - search happens in parallel with conversation)
        private long? _foundAmoCrmLeadId = null;
        private bool _isSearchingAmoCrmLead = false; // Флаг для предотвращения повторного поиска

        /// <summary>SIP stack for this window; null for WebRTC calls.</summary>
        public SipService? SipCallService => _useWebRtc ? null : _sipService;

        public CallWindow(SipService sipService, string phoneNumber, bool isIncomingCall = false, DateTime? callStartTime = null, long? amoCrmLeadId = null)
        {
            InitializeComponent();
            NativeWindowAppearanceManager.Attach(this);
            _sipService = sipService;
            _phoneNumber = phoneNumber;
            _isIncomingCall = isIncomingCall;
            _openedAsOutgoingCall = !isIncomingCall;
            _callStartTime = callStartTime ?? DateTime.Now; // Используем переданное время или текущее
            _originalCallStartTime = _callStartTime; // Сохраняем исходное время начала звонка
            _incomingCallStartTime = isIncomingCall ? DateTime.Now : _callStartTime; // Для входящих - текущее время, для исходящих - переданное
            _callWindowStartTime = DateTime.Now; // Время создания окна для фильтрации логов
            
            // Создаем контекст звонка с транспортом SIP и leadId из браузера
            _callContext = new CallContext(CallTransport.Sip, phoneNumber, _callStartTime, amoCrmLeadId: amoCrmLeadId);
            
            // Логируем хеш-код для диагностики
            if (_sipService != null)
            {
                int serviceHash = _sipService.GetHashCode();
                MainWindow.Log($"[Call][SIP] CallWindow constructor: SipService hash: {serviceHash}, PhoneNumber: {phoneNumber}, IsIncomingCall: {isIncomingCall}, AmoCrmLeadId: {amoCrmLeadId?.ToString() ?? "null"}");
            }
            
            UpdateTransportBadge();
            CallerNameTextBlock.Text = phoneNumber;
            
            if (_isIncomingCall)
            {
                // Для входящего звонка показываем кнопки "Ответить" и "Отклонить"
                CallStatusTextBlock.Text = $"Incoming call from {phoneNumber}";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                CallTimerTextBlock.Text = "";
                ShowIncomingCallButtons();

                // Start ringtone for incoming SIP calls
                RingtoneService.Instance.Start();
            }
            else
            {
                // Для исходящего звонка обычный интерфейс
                CallStatusTextBlock.Text = "Connecting...";
                CallTimerTextBlock.Text = "";
                ShowCallControls();
                
                // Ищем контакт в AmoCRM для исходящего звонка
                LoadContactNameFromAmoCrm(phoneNumber);
            }

            // Подписываемся на изменения статуса
            if (_sipService != null)
            {
                _sipService.OnStatusChanged += UpdateCallStatus;
                _sipService.OnCallEnded += OnCallEnded;
                _sipService.OnRecordingFinalized += OnSipRecordingFinalized;
                _sipService.OnOutboundCallerIdReceived += OnOutboundCallerIdReceived;
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
                // Stop ringtone before answering
                RingtoneService.Instance.Stop();
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
                // Stop ringtone before answering
                RingtoneService.Instance.Stop();

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
                    CallStatusTextBlock.Text = "Connected";
                    CallTimerTextBlock.Text = "00:00:00";
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
                // Stop ringtone on reject
                RingtoneService.Instance.Stop();
                RingbackToneService.Instance.Stop(); // safety: stop any ringback tone

                AnswerButton.IsEnabled = false;
                RejectButton.IsEnabled = false;
                
                // КРИТИЧНО: Устанавливаем читаемый статус перед отклонением
                CallStatusTextBlock.Text = "Call Rejected";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                CallTimerTextBlock.Text = "";
                
                if (_useWebRtc)
                {
                    // WebRTC звонок - отклоняем через сервис (важно: с sessionId)
                    await WebRtcHangupAsync();
                    
                    // Уведомляем MainWindow об отклонении входящего звонка
                    OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Cancelled, null);
                    
                    // Закрываем окно с небольшой задержкой, чтобы пользователь увидел статус
                    _isClosing = true;
                    _ = Task.Delay(500).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                if (IsLoaded)
                                {
                                    Close();
                                }
                            }
                            catch { }
                        });
                    });
                }
                else if (_sipService != null)
                {
                    // SIPSorcery звонок
                    await _sipService.RejectIncomingCallAsync();
                    // Ensure all tones stop and call is fully torn down
                    _sipService.Hangup();
                    
                    // Уведомляем MainWindow об отклонении входящего звонка
                    OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Cancelled, null);
                    
                    // Закрываем окно с небольшой задержкой, чтобы пользователь увидел статус
                    _isClosing = true;
                    _ = Task.Delay(500).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                if (IsLoaded)
                                {
                                    Close();
                                }
                            }
                            catch { }
                        });
                    });
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
        private bool _closeScheduled = false;
        private bool _sipCallEndHandled = false;
        /// <summary>Текст уведомления об ошибке (например «Абонент временно недоступен»), который нужно
        /// оставить на экране активного звонка и не перетирать зелёным «Call ended».</summary>
        private string? _failureNoticeText;
        private bool _isFailureNotice = false;
        /// <summary>WebRTC: coalesce duplicate call_ended / call_failed from JS (e.g. PC closed + JsSIP ended).</summary>
        private bool _webRtcCallTerminationUiHandled;

        /// <summary>
        /// Проверяет, закрывается ли окно
        /// </summary>
        public bool IsClosing()
        {
            return _isClosing;
        }

        private bool CanTouchCallUi() =>
            IsLoaded && !_isClosing && CallStatusTextBlock != null && CallTimerTextBlock != null;

        private void SetCallStatusUi(string text, string brushResourceKey = "AccentGreenBrush")
        {
            if (!CanTouchCallUi()) return;
            try
            {
                CallStatusTextBlock.Text = text;
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource(brushResourceKey);
                CallTimerTextBlock.Text = "";
            }
            catch (Exception ex)
            {
                MainWindow.Log($"{GetTransportLogPrefix()} SetCallStatusUi skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Распознаёт случай «абонент/сеть временно недоступны». Оператор (Asterisk) часто отдаёт
        /// 480 Temporarily Unavailable, либо мапит его в 603 Decline с RFC3326 Reason-заголовком
        /// (cause=480 / "restoration" / "timeout"). Для менеджера это понятнее, чем общий «недозвон».
        /// </summary>
        private static bool IsServerUnreachableFailure(string status)
        {
            if (string.IsNullOrEmpty(status))
                return false;
            return status.Contains("Cannot reach SIP server", StringComparison.OrdinalIgnoreCase)
                || status.Contains("server unreachable", StringComparison.OrdinalIgnoreCase)
                || status.Contains("VPN/network", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Matches SIP status codes in structured log lines, not bare digits inside timestamps (e.g. 14:49:47.480).
        /// </summary>
        private static bool ContainsSipStatusCode(string status, int code)
        {
            if (string.IsNullOrEmpty(status))
                return false;
            string codeText = code.ToString(CultureInfo.InvariantCulture);
            return status.Contains($"SIP Response: {codeText} ", StringComparison.OrdinalIgnoreCase)
                || status.Contains($"Call failed: {codeText}", StringComparison.OrdinalIgnoreCase)
                || status.Contains($"Response: {codeText}", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSipDeclineFailure(string status) =>
            ContainsSipStatusCode(status, 603)
            || status.Contains("Decline", StringComparison.OrdinalIgnoreCase);

        private static bool IsSipBusyFailure(string status) =>
            ContainsSipStatusCode(status, 486)
            || status.Contains("Busy Here", StringComparison.OrdinalIgnoreCase);

        private static bool IsSubscriberUnavailableFailure(string status)
        {
            if (string.IsNullOrEmpty(status))
                return false;
            // Не используем голую "480" — её содержит и метка времени (например 13:33:24.480).
            // Сопоставляем только осмысленные позиции кода/причины.
            return ContainsSipStatusCode(status, 480)
                || status.Contains("Temporarily Unavailable", StringComparison.OrdinalIgnoreCase)
                || status.Contains("cause=480", StringComparison.OrdinalIgnoreCase)
                || status.Contains("restoration", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveSipFailureStatusText(string status)
        {
            // Canonical messages from SipService — pass through unchanged.
            if (status.Contains("Line busy", StringComparison.OrdinalIgnoreCase))
                return "Line busy";
            if (status.Contains("Call declined", StringComparison.OrdinalIgnoreCase))
                return "Call declined";
            if (status.Contains("Subscriber unavailable", StringComparison.OrdinalIgnoreCase))
                return "Subscriber unavailable";
            if (status.Contains("Call cancelled", StringComparison.OrdinalIgnoreCase))
                return "Call cancelled";
            if (status.Contains("Number not found", StringComparison.OrdinalIgnoreCase))
                return "Number not found";

            // Сеть/абонент временно недоступны — проверяем до 603, т.к. оператор может прислать
            // 603 Decline с Reason: cause=480, что фактически означает недоступность абонента.
            if (IsServerUnreachableFailure(status))
                return "Cannot reach SIP server (VPN/network)";
            if (status.Contains("No answer (timeout)", StringComparison.OrdinalIgnoreCase))
                return "No answer (timeout)";
            if (IsSubscriberUnavailableFailure(status))
                return "Subscriber unavailable";
            if (IsSipDeclineFailure(status))
                return "Call declined";
            if (IsSipBusyFailure(status))
                return "Line busy";
            if (status.Contains("timeout", StringComparison.OrdinalIgnoreCase) || status.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return "No answer (timeout)";
            return "Call failed";
        }

        private void ApplySipFailureStatusFromMessage(string status)
        {
            if (_endedBy == CallEndedBy.Unknown)
                _endedBy = CallEndedBy.RemoteParty;
            string text = ResolveSipFailureStatusText(status);
            // Запоминаем, чтобы OnCallEnded не перетёр уведомление зелёным «Call ended»
            // и окно подержалось дольше — менеджер успеет прочитать причину.
            _failureNoticeText = text;
            _isFailureNotice = true;
            SetCallStatusUi(text, "AccentRedBrush");
        }

        private void ScheduleCloseCallWindow(int delayMs = 300)
        {
            if (_closeScheduled) return;
            _closeScheduled = true;
            _ = Task.Delay(delayMs).ContinueWith(t =>
            {
                if (t.IsCanceled || t.IsFaulted)
                    return;

                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            if (IsLoaded)
                                Close();
                        }
                        catch (Exception closeEx)
                        {
                            MainWindow.Log($"{GetTransportLogPrefix()} ScheduleCloseCallWindow: {closeEx.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"{GetTransportLogPrefix()} ScheduleCloseCallWindow dispatch: {ex.Message}");
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        
        private void OnCallEnded()
        {
            // КРИТИЧНО: OnCallEnded теперь используется только для SIP звонков
            // Для WebRTC звонков окно закрывается напрямую в обработчике call_ended
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(OnCallEnded);
                return;
            }

            if (!IsLoaded || _sipCallEndHandled)
                return;

            // WebRTC termination is handled in OnWebRtcServiceEvent (call_ended), not here.
            if (_useWebRtc)
                return;

            _sipCallEndHandled = true;
            _isClosing = true;

            try
            {
                try { RingtoneService.Instance.Stop(); } catch { }
                StopRingbackUiTimer();
                RingbackToneService.Instance.Stop();

                if (_useWebRtc)
                    StopWebRtcRecording();

                CallWindowHelpers.UpdateRingbackEndTime(_ringbackStartTime, ref _ringbackEndTime);

                if (_endedBy == CallEndedBy.Unknown)
                    _endedBy = CallEndedBy.RemoteParty;

                _callTimer?.Stop();

                TimeSpan? duration = null;
                if (_wasAnswered)
                {
                    var startTime = _answerTime ?? (_isIncomingCall ? _incomingCallStartTime : _callStartTime);
                    if (startTime != default)
                        duration = DateTime.Now - startTime;
                }

                // При неуспешном звонке оставляем уведомление о причине (красный),
                // иначе показываем обычное «Call ended».
                bool showFailureNotice = !_wasAnswered && _isFailureNotice && !string.IsNullOrEmpty(_failureNoticeText);
                if (showFailureNotice)
                    SetCallStatusUi(_failureNoticeText!, "AccentRedBrush");
                else
                    SetCallStatusUi("Call ended", "AccentGreenBrush");

                if (_wasAnswered)
                    OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Ended, duration);
                else if (_isIncomingCall)
                    OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Cancelled, null);

                try { _sipService?.FinalizeCallRecordingIfActive(); }
                catch (Exception ex) { MainWindow.Log($"{GetTransportLogPrefix()} OnCallEnded: FinalizeCallRecordingIfActive: {ex.Message}"); }

                SendCallDetails();

                MainWindow.Log($"{GetTransportLogPrefix()} OnCallEnded: scheduling window close");
                // Даём менеджеру время прочитать причину отказа, прежде чем окно закроется.
                ScheduleCloseCallWindow(showFailureNotice ? 3500 : 300);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"{GetTransportLogPrefix()} OnCallEnded error: {ex.Message}");
                ScheduleCloseCallWindow(0);
            }
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
            // Ensure we don't run multiple timers
            try { _callTimer?.Stop(); } catch { }

            _callTimer = new System.Windows.Threading.DispatcherTimer();
            _callTimer.Interval = TimeSpan.FromSeconds(1);
            _callTimer.Tick += (s, e) =>
            {
                if (!_isOnHold)
                {
                    var duration = DateTime.Now - _callStartTime;
                    CallTimerTextBlock.Text = duration.ToString(@"hh\:mm\:ss");
                }
            };
            _callTimer.Start();
        }

        private void StartRingbackUiTimer()
        {
            // Idempotent: WebRTC can emit multiple "ringing"/"progress" events; don't restart the UI timer.
            if (_ringbackUiTimer != null && _ringbackUiTimer.IsEnabled)
            {
                return;
            }

            if (_ringbackStartTime == null)
            {
                _ringbackStartTime = DateTime.Now;
            }

            try { _ringbackUiTimer?.Stop(); } catch { }
            _ringbackUiTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _ringbackUiTimer.Tick += (s, e) =>
            {
                if (_ringbackStartTime.HasValue && !_wasAnswered)
                {
                    var elapsed = DateTime.Now - _ringbackStartTime.Value;
                    CallTimerTextBlock.Text = elapsed.ToString(@"hh\:mm\:ss");
                }
            };
            // Show starting value immediately
            CallTimerTextBlock.Text = "00:00:00";
            _ringbackUiTimer.Start();
        }

        private void StopRingbackUiTimer()
        {
            try { _ringbackUiTimer?.Stop(); } catch { }
        }

        private void FinalizeWebRtcConnected(string timestamp, string logPrefix)
        {
            if (_webRtcConnectedFinalized)
            {
                MainWindow.Log($"{logPrefix} FinalizeWebRtcConnected: skipped (already finalized)");
                return;
            }
            _webRtcConnectedFinalized = true;
            _webRtcAcceptedPending = false;
            StopWebRtcMediaConnectWatchdog();
            CancelOutboundConnectingTimeout();

            // Stop ringback now that ICE is connected (media path established).
            try
            {
                RingbackToneService.Instance.Stop();
                MainWindow.Log($"{logPrefix} ✅ Ringback tone STOPPED successfully (ICE connected)");
            }
            catch (Exception toneEx)
            {
                MainWindow.Log($"{logPrefix} ❌ ERROR stopping ringback tone: {toneEx.Message}");
            }

            // Originate: PBX callback is up, but remote PSTN party may still be ringing.
            if (_isOriginateCall && !_originateRemoteAnswered)
            {
                if (!_ringbackStartTime.HasValue)
                {
                    _ringbackStartTime = _originalCallStartTime;
                }
                if (_ringbackUiTimer == null || !_ringbackUiTimer.IsEnabled)
                {
                    StartRingbackUiTimer();
                }
                // Local ringback until remote answers or PBX early media arrives (WebRTC playback may still be establishing).
                RingbackToneService.Instance.Play();

                MainWindow.Log($"{logPrefix} Originate: PBX media connected; waiting for remote party answer (AnswerTime not set yet)");
                _technicalDetails.Add($"{timestamp} PBX Originate media connected (ICE); waiting for remote answer");

                // Start recording when media is actually flowing.
                if (_webRtcRecorder == null || !_webRtcRecorder.IsRecording)
                {
                    StartWebRtcRecording();
                }

                if (_isIncomingCall)
                {
                    _isIncomingCall = false;
                    ShowCallControls();
                }

                CallStatusTextBlock.Text = "Calling...";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                StartAmoCrmLeadSearchAsync();
                return;
            }

            StopRingbackUiTimer();
            MainWindow.Log($"{logPrefix} ✅ Ringback UI timer stopped");

            CallWindowHelpers.UpdateAnswerTime(ref _answerTime, ref _wasAnswered);
            if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
            {
                _ringbackEndTime = _answerTime;
            }
            MainWindow.Log($"{logPrefix} Answer time: {_answerTime}");
            MainWindow.Log($"{logPrefix} Ringback duration: {(_ringbackEndTime.HasValue && _ringbackStartTime.HasValue ? (_ringbackEndTime.Value - _ringbackStartTime.Value).TotalSeconds.ToString("F2") : "N/A")} seconds");

            _callStartTime = DateTime.Now;
            MainWindow.Log($"{logPrefix} Call start time set: {_callStartTime}");
            MainWindow.Log($"{logPrefix} ===== CALL CONNECTED (ICE) PROCESSING COMPLETE =====");

            // Start recording when media is actually flowing.
            if (_webRtcRecorder == null || !_webRtcRecorder.IsRecording)
            {
                StartWebRtcRecording();
            }
            else
            {
                MainWindow.Log($"{logPrefix} Recording already started, skipping duplicate startRecording call");
            }

            if (_isIncomingCall)
            {
                _isIncomingCall = false;
                ShowCallControls();
            }

            StartCallTimer();
            CallStatusTextBlock.Text = "Connected";
            CallTimerTextBlock.Text = "00:00:00";
            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");

            // Start AmoCRM lead search in parallel.
            StartAmoCrmLeadSearchAsync();
        }

        private void FinalizeOriginateRemoteAnswered(string timestamp, string logPrefix)
        {
            if (!_isOriginateCall || _originateRemoteAnswered)
            {
                return;
            }

            _originateRemoteAnswered = true;
            CallWindowHelpers.UpdateAnswerTime(ref _answerTime, ref _wasAnswered);
            if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
            {
                _ringbackEndTime = _answerTime;
            }

            try { RingbackToneService.Instance.Stop(); } catch { }
            StopRingbackUiTimer();
            _callStartTime = DateTime.Now;
            StartCallTimer();

            CallStatusTextBlock.Text = "Connected";
            CallTimerTextBlock.Text = "00:00:00";
            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");

            var detail = $"{timestamp} Remote party answered (Originate B-leg bridged)";
            _technicalDetails.Add(detail);
            MainWindow.Log($"{logPrefix} Originate: remote party answered at {_answerTime:HH:mm:ss.fff}");
            SendCallDetails();
        }

        private void TryInferOriginateAnswerFromRecording(string logPrefix)
        {
            if (!_isOriginateCall || _originateRemoteAnswered || _endedBy == CallEndedBy.Unknown)
                return;

            string? recordingPath = CallWindowHelpers.GetRecordingFilePath(
                null,
                _sipService?.CurrentRecordingFilePath,
                _webRtcRecorder?.RecordingFilePath,
                _webRtcRecordingFilePath);
            if (string.IsNullOrEmpty(recordingPath) || !File.Exists(recordingPath))
                return;

            TimeSpan? ringbackDuration = null;
            if (_ringbackStartTime.HasValue)
            {
                var ringbackEnd = _ringbackEndTime ?? DateTime.Now;
                ringbackDuration = ringbackEnd - _ringbackStartTime.Value;
                if (ringbackDuration.Value.TotalSeconds < 0)
                    ringbackDuration = null;
            }

            int recSeconds = CallWindowHelpers.EstimateWavDurationSeconds(recordingPath);
            bool inferred = false;

            if (_webRtcRemoteAudioStarted && recSeconds >= 2)
            {
                inferred = true;
            }
            else if (CallWindowHelpers.TryInferAnsweredFromRecording(
                    recordingPath,
                    _endedBy,
                    ringbackDuration,
                    out recSeconds,
                    remoteAudioDetected: _webRtcRemoteAudioStarted,
                    isOriginateCall: true))
            {
                inferred = true;
            }

            if (!inferred)
                return;

            _originateRemoteAnswered = true;
            _wasAnswered = true;
            if (!_answerTime.HasValue)
            {
                _answerTime = DateTime.Now.AddSeconds(-Math.Max(3, recSeconds / 2));
                if (_ringbackStartTime.HasValue && _answerTime < _ringbackStartTime.Value.AddSeconds(3))
                    _answerTime = _ringbackStartTime.Value.AddSeconds(3);
            }

            if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
                _ringbackEndTime = _answerTime;

            _callStartTime = _answerTime ?? DateTime.Now;
            CallStatusTextBlock.Text = "Connected";
            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");

            var inferDetail = _webRtcRemoteAudioStarted && recSeconds >= 2
                ? $"{DateTime.Now:HH:mm:ss.fff} Remote party answer inferred from remote audio + recording ({recSeconds}s WAV)"
                : $"{DateTime.Now:HH:mm:ss.fff} Remote party answer inferred from recording ({recSeconds}s WAV, EndedBy={_endedBy})";
            _technicalDetails.Add(inferDetail);
            MainWindow.Log($"{logPrefix} Originate: {inferDetail}");
        }

        private void StartWebRtcMediaConnectWatchdog(string timestamp, string logPrefix)
        {
            // Only relevant for WebRTC calls when we have signaling "accepted" but media isn't ready yet.
            if (!_useWebRtc) return;
            if (_webRtcConnectedFinalized) return;
            if (_webRtcIceConnected || _webRtcRemoteAudioStarted) return;
            if (!_webRtcAcceptedPending) return;

            _webRtcAcceptedPendingSinceUtc ??= DateTime.UtcNow;

            // Idempotent: don't start multiple timers.
            if (_webRtcMediaConnectTimer != null && _webRtcMediaConnectTimer.IsEnabled)
            {
                return;
            }

            _webRtcMediaConnectTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            _webRtcMediaConnectTimer.Tick += async (s, e) =>
            {
                try
                {
                    if (_isClosing || _webRtcConnectedFinalized)
                    {
                        StopWebRtcMediaConnectWatchdog();
                        return;
                    }

                    if (_webRtcIceConnected || _webRtcRemoteAudioStarted)
                    {
                        StopWebRtcMediaConnectWatchdog();
                        return;
                    }

                    var since = _webRtcAcceptedPendingSinceUtc ?? DateTime.UtcNow;
                    var elapsed = DateTime.UtcNow - since;
                    if (elapsed < WebRtcMediaConnectTimeout)
                    {
                        return;
                    }

                    StopWebRtcMediaConnectWatchdog();

                    var msg = "Media connection failed (ICE timeout). Check TURN / firewall / NAT.";
                    MainWindow.Log($"{logPrefix} ❌ WebRTC media watchdog timeout after {elapsed.TotalSeconds:F0}s: ICE is not connected");
                    _technicalDetails.Add($"{timestamp} ❌ {msg}");

                    // Stop local sounds; we are not going to establish media.
                    try { RingbackToneService.Instance.Stop(); } catch { }
                    StopRingbackUiTimer();

                    CallStatusTextBlock.Text = msg;
                    CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");

                    // Best-effort: terminate the call to avoid "stuck calling" state.
                    // We keep the window open to wait for call_ended, with the existing timeout close logic in Hangup button.
                    _endedBy = CallEndedBy.LocalUser;
                    await WebRtcHangupAsync();

                    // Fallback: close if call_ended never arrives.
                    ScheduleCloseCallWindow(3000);
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"{logPrefix} WebRTC media watchdog tick error: {ex.Message}");
                    // Keep timer running; next tick may still succeed or timeout.
                }
            };

            _webRtcMediaConnectTimer.Start();
        }

        private void StopWebRtcMediaConnectWatchdog()
        {
            try { _webRtcMediaConnectTimer?.Stop(); } catch { }
            _webRtcMediaConnectTimer = null;
            _webRtcAcceptedPendingSinceUtc = null;
        }

        private void OnOutboundCallerIdReceived(string callerId)
        {
            _outboundCallerId = callerId;
            MainWindow.Log($"{GetTransportLogPrefix()} Outbound CallerID received: {callerId}");
        }

        private void UpdateCallStatus(string status)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded)
                    return;

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
                        CallTimerTextBlock.Text = "";
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
                        CallTimerTextBlock.Text = "";
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
                        // 183 often means "ringing/early media". Even if we end up using local ringback fallback,
                        // "Ringing..." communicates the real user-facing state better than "Connecting...".
                        CallStatusTextBlock.Text = "Ringing...";
                        CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                        CallTimerTextBlock.Text = "";
                    }
                }
                else if (status.Contains("Call connected") || status.Contains("Call answered") ||
                         status.Contains("Call progress: 200 OK"))
                {
                    // Записываем время окончания гудков и время ответа
                    if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
                    {
                        _ringbackEndTime = DateTime.Now;
                    }
                    CallWindowHelpers.UpdateAnswerTime(ref _answerTime, ref _wasAnswered);
                    
                    // Когда звонок подключен, начинаем отсчет времени разговора
                    // НЕ меняем _originalCallStartTime - он используется для поиска звонка в истории
                    // Always reset the UI call timer baseline to "now" at the moment we consider the call connected.
                    // Otherwise, if the call stays in "connecting/ringing" for a long time, the timer jumps forward.
                    _callStartTime = DateTime.Now;
                    
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
                    
                    CallStatusTextBlock.Text = "Connected";
                    CallTimerTextBlock.Text = "00:00:00";
                    CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                    
                    // Отправляем детальную информацию
                    SendCallDetails();
                }
                else if (status.Contains("Call ended by remote party"))
                {
                    // Удаленная сторона завершила звонок
                    _endedBy = CallEndedBy.RemoteParty;
                }
                else if (IsSipBusyFailure(status) || IsSipDeclineFailure(status) || IsSubscriberUnavailableFailure(status))
                {
                    // Абонент отклонил / сбросил / недоступен
                    _endedBy = CallEndedBy.RemoteParty;
                    if (!_wasAnswered)
                        ApplySipFailureStatusFromMessage(status);
                }
                else if (status.Contains("Call failed") || IsServerUnreachableFailure(status)
                         || status.Contains("No answer (timeout)", StringComparison.OrdinalIgnoreCase)
                         || status.Contains("Line busy", StringComparison.OrdinalIgnoreCase)
                         || status.Contains("Call declined", StringComparison.OrdinalIgnoreCase)
                         || status.Contains("Subscriber unavailable", StringComparison.OrdinalIgnoreCase)
                         || status.Contains("Call cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    // SIP: ApplyOutboundFailureUiTeardown always invokes OnCallEnded — only refresh status here.
                    if (!_wasAnswered)
                        ApplySipFailureStatusFromMessage(status);
                }
                else if ((status.Contains("Call ended") || status.Contains("Hanging up")) && !_useWebRtc)
                {
                    // SIP teardown (SendCallDetails, close) is handled exclusively in OnCallEnded.
                }
                else if (status.Contains("Call ended") || status.Contains("Hanging up"))
                {
                    if (_isClosing)
                        return;

                    _isClosing = true;
                    _callTimer?.Stop();

                    if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
                        _ringbackEndTime = DateTime.Now;

                    TimeSpan? duration = null;
                    if (_wasAnswered)
                    {
                        var startTime = _answerTime ?? (_isIncomingCall ? _incomingCallStartTime : _callStartTime);
                        if (startTime != default)
                            duration = DateTime.Now - startTime;
                    }

                    SendCallDetails();

                    if (_wasAnswered)
                        OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Ended, duration);
                    else if (_isIncomingCall)
                        OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Missed, null);

                    SetCallStatusUi("Call ended", "AccentGreenBrush");
                    ScheduleCloseCallWindow(800);
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
            // Stop ringtone if it was still playing (safety)
            RingtoneService.Instance.Stop();

            // Останавливаем ringback tone при нажатии Hangup
            RingbackToneService.Instance.Stop();
            StopWebRtcMediaConnectWatchdog();
            
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
                
                // КРИТИЧНО: Вызываем hangup с правильным sessionId
                await WebRtcHangupAsync();
                CallStatusTextBlock.Text = "Hanging up...";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                
                // Добавляем техническую деталь о завершении звонка
                _technicalDetails.Add($"[{DateTime.Now:HH:mm:ss.fff}] Call terminated by local user (WebRTC)");
                
                // Отправляем детальную информацию перед закрытием
                SendCallDetails();
                
                // Ждём call_ended от JS; если не пришёл — закрываем принудительно.
                // (_isClosing уже true — нельзя проверять ! _isClosing, иначе окно зависает на "Hanging up...")
                ScheduleCloseCallWindow(3000);
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
                
                // Устанавливаем флаг закрытия (OnCallEnded не дублирует teardown)
                _isClosing = true;
                _sipCallEndHandled = true;
                
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
            // Используем SystemCommands для стандартных анимаций Windows 11
            SystemCommands.MinimizeWindow(this);
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
                    StopRingbackUiTimer();
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
                    await (_webRtcService ?? WebRtcService.Main).SetMuteAsync(_isMuted);
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
            if (_isMuted)
            {
                // Show MicOff icon + red background
                if (MuteIconOn != null) MuteIconOn.Visibility = Visibility.Collapsed;
                if (MuteIconOff != null) MuteIconOff.Visibility = Visibility.Visible;
                MuteButton.Background = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
            }
            else
            {
                // Show Mic icon + normal background
                if (MuteIconOn != null) MuteIconOn.Visibility = Visibility.Visible;
                if (MuteIconOff != null) MuteIconOff.Visibility = Visibility.Collapsed;
                MuteButton.Background = (System.Windows.Media.Brush)FindResource("BackgroundMediumBrush");
            }
        }

        private async void HoldButton_Click(object sender, RoutedEventArgs e)
        {
            var logPrefix = $"[Call][WebRTC][id={_callContext?.WebRtcSessionId ?? "none"}]";
            MainWindow.Log($"{logPrefix} HoldButton_Click: Current _isOnHold={_isOnHold}, toggling to {!_isOnHold}");
            
            _isOnHold = !_isOnHold;

            // WebRTC calls: delegate to WebRtcService/JS (JsSIP hold/unhold).
            if (_useWebRtc)
            {
                try
                {
                    string? sessionId = _callContext?.WebRtcSessionId;
                    MainWindow.Log($"{logPrefix} HoldButton_Click: Calling SetHoldAsync(hold={_isOnHold}, sessionId={sessionId ?? "null"})");
                    if (sessionId != null)
                    {
                        await (_webRtcService ?? WebRtcService.Main).SetHoldAsync(_isOnHold, sessionId);
                        MainWindow.Log($"{logPrefix} HoldButton_Click: SetHoldAsync completed successfully");
                    }
                    else
                    {
                        MainWindow.Log($"{logPrefix} HoldButton_Click: Cannot set hold - sessionId is null");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"{logPrefix} HoldButton_Click: ERROR: Failed to set hold for WebRTC call: {ex.Message}");
                    MainWindow.Log($"{logPrefix} HoldButton_Click: Stack trace: {ex.StackTrace}");
                    // rollback
                    _isOnHold = !_isOnHold;
                    return;
                }
            }
            else
            {
                if (_sipService == null)
                    return;

                try
                {
                    await _sipService.HoldCallAsync(_isOnHold);
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallWindow] ERROR: Failed to set hold for SIP call: {ex.Message}");
                    _isOnHold = !_isOnHold;
                    return;
                }
            }
            
            if (_isOnHold)
            {
                // Show "resume" (Play) icon
                if (HoldIconPause != null) HoldIconPause.Visibility = Visibility.Collapsed;
                if (HoldIconPlay != null) HoldIconPlay.Visibility = Visibility.Visible;
                HoldButton.Background = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                CallStatusTextBlock.Text = "On Hold";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                // Останавливаем таймер
                _callTimer?.Stop();

                // SIP local hold: re-INVITE sends a=sendonly → PBX plays MOH to client.
                // Operator hears nothing. No local audio needed.
            }
            else
            {
                // Show "hold" (Pause) icon
                if (HoldIconPause != null) HoldIconPause.Visibility = Visibility.Visible;
                if (HoldIconPlay != null) HoldIconPlay.Visibility = Visibility.Collapsed;
                HoldButton.Background = (System.Windows.Media.Brush)FindResource("BackgroundMediumBrush");
                CallStatusTextBlock.Text = "Connected";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                // Возобновляем таймер
                if (_callTimer != null && !_callTimer.IsEnabled)
                {
                    _callTimer.Start();
                }

                if (!_useWebRtc)
                {
                    try
                    {
                        HoldMusicService.Instance.Stop();
                        MainWindow.Log("[CallWindow] Hold music stopped (SIP unhold)");
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[CallWindow] Failed to stop hold music: {ex.Message}");
                    }
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
                    var devices = await (_webRtcService ?? WebRtcService.Main).EnumerateAudioDevicesAsync();
                    
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
                        await (_webRtcService ?? WebRtcService.Main).SwitchAudioDeviceAsync(
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
                    if ((_webRtcService ?? WebRtcService.Main) != null && (_webRtcService ?? WebRtcService.Main).CurrentCallState == WebRtcCallState.Connected)
                    {
                        _ = (_webRtcService ?? WebRtcService.Main).SendDTMFAsync(digit);
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
            // ToneGenerator moved to RingbackToneService (singleton)
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
                        await (_webRtcService ?? WebRtcService.Main).SendCommandAsync(new { cmd = "startRecording" });
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
                            await (_webRtcService ?? WebRtcService.Main).SendCommandAsync(new { cmd = "stopRecording" });
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"{logPrefix} Error sending stopRecording command: {ex.Message}");
                        }
                    });
                    
                    // НЕ вызываем recorder.StopRecording() здесь: раньше это сбрасывало приём чанков до прихода данных из JS → пустой WebM и срыв загрузки в AmoCRM.
                    // Флаги остановки на стороне рекордера выставляет WebRtcCallRecorder.StopRecordingAsync (из recording_complete после сборки чанков).
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
            var logPrefix = GetTransportLogPrefix();
            MainWindow.Log($"{logPrefix} OnClosed: Window closing, processing remaining recording events...");
            
            // Stop ringtone / hold music on window close
            RingtoneService.Instance.Stop();
            HoldMusicService.Instance.Stop();

            // Останавливаем ringback tone при закрытии окна (singleton)
            RingbackToneService.Instance.Stop();
            
            // КРИТИЧНО: НЕ отписываемся от событий сразу — нужен recording_complete.
            MainWindow.Log($"{logPrefix} OnClosed: Requesting recording stop (keeping subscription for recording_complete)...");
            if (_useWebRtc)
            {
                try { StopWebRtcRecording(); }
                catch (Exception ex) { MainWindow.Log($"{logPrefix} OnClosed: StopWebRtcRecording: {ex.Message}"); }
            }
            else if (_webRtcRecorder != null)
            {
                try
                {
                    _webRtcRecorder.StopRecording();
                    MainWindow.Log($"{logPrefix} OnClosed: Recording stopped (non-WebRTC)");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"{logPrefix} OnClosed: Error stopping recording: {ex.Message}");
                }
            }
            
            if (_useWebRtc)
            {
                // События нужны для recording_complete, который может прийти после закрытия окна.
                MainWindow.Log($"{logPrefix} OnClosed: Keeping WebRTC event subscription for recording_complete processing (queue size={_recordingEventQueue.Count})");
                _ = Task.Delay(10000).ContinueWith(_ =>
                {
                    MainWindow.Log($"{logPrefix} OnClosed: Unsubscribing from WebRTC events after delay, disposing recorder");
                    try
                    {
                        (_webRtcService ?? WebRtcService.Main).Event -= OnWebRtcServiceEvent;
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"{logPrefix} OnClosed: Error unsubscribing WebRTC events: {ex.Message}");
                    }

                    if (_webRtcRecorder != null)
                    {
                        try
                        {
                            _webRtcRecorder.Dispose();
                            MainWindow.Log($"{logPrefix} OnClosed: Recorder disposed");
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"{logPrefix} OnClosed: Error disposing recorder: {ex.Message}");
                        }
                        _webRtcRecorder = null;
                    }
                });
            }
            
            // Останавливаем таймер
            _callTimer?.Stop();
            
            // Отписываемся от событий SipService
            if (_sipService != null)
            {
                // SIP call recording removed (WebRTC-only)
                
                _sipService.OnStatusChanged -= UpdateCallStatus;
                _sipService.OnCallEnded -= OnCallEnded;
                _sipService.OnRecordingFinalized -= OnSipRecordingFinalized;
                _sipService.OnOutboundCallerIdReceived -= OnOutboundCallerIdReceived;
                
                // Записываем время окончания гудков, если еще не записано
                CallWindowHelpers.UpdateRingbackEndTime(_ringbackStartTime, ref _ringbackEndTime);

                if (_endedBy == CallEndedBy.Unknown)
                    _endedBy = CallEndedBy.LocalUser;
                
                // Finalize SIP recording before last SendCallDetails (Hangup cancel path may skip StopCallRecording).
                try { _sipService.FinalizeCallRecordingIfActive(); }
                catch (Exception ex) { MainWindow.Log($"{logPrefix} OnClosed: FinalizeCallRecordingIfActive: {ex.Message}"); }

                // Отправляем детальную информацию перед закрытием
                SendCallDetails();
                
                // Если это входящий звонок, который не был принят, помечаем как пропущенный
                if (_isIncomingCall && !_wasAnswered && !_isClosing)
                {
                    OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Missed, null);
                }
                
                // Всегда завершаем SIP/RTP при закрытии окна (early media продолжает играть иначе).
                try { _sipService.Hangup(); } catch { }
            }
            
            // WebRTC: если окно закрыли без call_ended (крестик, сбой), история не получала SendCallDetails — досылаем.
            if (_useWebRtc)
            {
                if (_endedBy == CallEndedBy.Unknown)
                    _endedBy = CallEndedBy.LocalUser;
                try
                {
                    SendCallDetails();
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallWindow] OnClosed: SendCallDetails error (WebRTC): {ex.Message}");
                }
            }

            // КРИТИЧНО: Для WebRTC также вызываем Hangup при закрытии с правильным sessionId
            if (_useWebRtc && !_isClosing)
            {
                try
                {
                    var sessionId = _callContext?.WebRtcSessionId;
                    if (string.IsNullOrEmpty(sessionId))
                    {
                        MainWindow.Log("[CallWindow] OnClosed: Hangup skipped — no WebRTC sessionId");
                    }
                    else
                    {
                        FireAndForget(
                            (_webRtcService ?? WebRtcService.Main).HangupAsync(sessionId),
                            "OnClosed HangupAsync");
                        MainWindow.Log($"[CallWindow] OnClosed: Hangup called for WebRTC call (sessionId: {sessionId})");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallWindow] OnClosed: Error calling HangupAsync: {ex.Message}");
                }
            }
            
            base.OnClosed(e);
        }
        
        // Сохраняем последние отправленные детали для предотвращения избыточного логирования
        private string? _lastSentCallDetails = null;
        private bool _hasBeenSentToAmoCrm = false; // Флаг для предотвращения повторной отправки одного звонка в AmoCRM
        
        private void SendCallDetails()
        {
            // Отправляем детальную информацию о звонке
            // Используем исходное время начала звонка, которое не меняется
            var callTime = _isIncomingCall ? _incomingCallStartTime : _originalCallStartTime;

            var logPrefix = GetTransportLogPrefix();

            // Получаем путь к записи (нужен для fallback-определения ответа Originate)
            string? recordingFilePath = CallWindowHelpers.GetRecordingFilePath(
                null,
                _sipService?.CurrentRecordingFilePath,
                _webRtcRecorder?.RecordingFilePath,
                _webRtcRecordingFilePath);
            if (_sipService?.TryConsumeSipRecordingWithNoOutputFile() == true)
            {
                recordingFilePath = string.Empty;
                MainWindow.Log($"{logPrefix} SendCallDetails: SIP recording produced no file — clearing history path.");
            }

            // IsInCall can stay true briefly after BYE while SIPSorcery tears down — treat ended calls as inactive.
            bool sipCallEnded = _endedBy != CallEndedBy.Unknown || _sipCallEndHandled;
            bool sipCallStillActive = !_useWebRtc && (_sipService?.IsInCall == true) && !sipCallEnded;

            // SIP: nudge finalize only after the call ended (WAV may still be encoding). Never while IsInCall —
            // recording starts on 200 OK while File.Exists is still false and would stop after ~1 RTP packet.
            if (!_useWebRtc && _endedBy != CallEndedBy.Unknown && !sipCallStillActive
                && !string.IsNullOrEmpty(recordingFilePath) && !File.Exists(recordingFilePath))
            {
                try { _sipService?.FinalizeCallRecordingIfActive(); }
                catch (Exception ex) { MainWindow.Log($"{logPrefix} SendCallDetails: SIP recording finalize nudge: {ex.Message}"); }
            }

            if (_isOriginateCall && _endedBy != CallEndedBy.Unknown)
            {
                TryInferOriginateAnswerFromRecording(logPrefix);
            }

            if (!_useWebRtc && !_isOriginateCall && _endedBy != CallEndedBy.Unknown)
            {
                TryInferSipAnswerFromEarlyMedia(logPrefix);
            }
            
            // Вычисляем длительность звонка
            // Для SIP звонков запись начинается после 200 OK (когда устанавливается _answerTime)
            // Поэтому используем _answerTime для вычисления Duration, если он установлен
            // Для WebRTC звонков запись начинается при makeCall_started (до call_accepted),
            // поэтому используем _originalCallStartTime (время начала звонка), а не _answerTime
            // Определяем, какой транспорт используется: если _useWebRtc=true, то это WebRTC
            var startTimeForDuration = _useWebRtc
                ? (_isOriginateCall && _answerTime.HasValue
                    ? _answerTime.Value
                    : (_isIncomingCall ? _incomingCallStartTime : _originalCallStartTime))
                : (_isIncomingCall ? _incomingCallStartTime : _callStartTime);
            bool finalWasAnswered = _isOriginateCall
                ? _originateRemoteAnswered
                : (_wasAnswered || _answerTime.HasValue);
            TimeSpan? duration = CallWindowHelpers.CalculateCallDuration(
                startTimeForDuration,
                _answerTime,
                finalWasAnswered,
                _isIncomingCall);
            
            var detailsString = CallWindowHelpers.FormatCallDetailsString(
                _phoneNumber,
                callTime,
                _ringbackStartTime,
                _ringbackEndTime,
                _answerTime,
                finalWasAnswered,
                duration,
                _endedBy,
                _technicalDetails.Count);
            
            // Логируем только при изменении
            if (_lastSentCallDetails != detailsString)
            {
                MainWindow.Log($"{logPrefix} SendCallDetails: {detailsString}");
                _lastSentCallDetails = detailsString;
            }
            
            // Логируем путь к записи для диагностики
            if (!string.IsNullOrEmpty(recordingFilePath))
            {
                bool fileExists = System.IO.File.Exists(recordingFilePath);
                MainWindow.Log($"{logPrefix} SendCallDetails: Recording file path: {recordingFilePath}, FileExists={fileExists}");
            }
            else
            {
                MainWindow.Log($"{logPrefix} SendCallDetails: Recording file path is null or empty " +
                    $"(webRtcRecorder.RecordingFilePath={_webRtcRecorder?.RecordingFilePath ?? "null"}, " +
                    $"webRtcRecordingFilePath={_webRtcRecordingFilePath ?? "null"})");
            }
            
            // Завершённый звонок: всегда обновляем историю через OnCallDetailsChanged (путь к записи может прийти позже).
            // В очередь AmoCRM — только один раз (дедуп в MainWindow.ProcessCallInAmoCrm).
            if (_endedBy != CallEndedBy.Unknown && !sipCallStillActive)
            {
                bool firstAmoDispatch = !_hasBeenSentToAmoCrm;
                if (firstAmoDispatch)
                {
                    _hasBeenSentToAmoCrm = true;
                }

                if (firstAmoDispatch)
                {
                    MainWindow.Log($"{logPrefix} SendCallDetails: Call ended (EndedBy={_endedBy}), sending to AmoCRM (wasAnswered={finalWasAnswered}, hasAnswerTime={_answerTime.HasValue}, sessionId={_callContext.WebRtcSessionId ?? _callContext.SipCallId ?? "none"})");
                }
                else
                {
                    MainWindow.Log($"{logPrefix} SendCallDetails: Call ended (EndedBy={_endedBy}), refreshing history/recording path (AmoCRM job already queued, sessionId={_callContext.WebRtcSessionId ?? _callContext.SipCallId ?? "none"})");
                }

                OnCallDetailsChanged?.Invoke(
                    _phoneNumber,
                    callTime,
                    _ringbackStartTime,
                    _ringbackEndTime,
                    _answerTime,
                    finalWasAnswered,
                    _endedBy,
                    _technicalDetails.Count > 0 ? _technicalDetails : null,
                    duration,
                    recordingFilePath,
                    _callContext.Transport,
                    _callContext.SipCallId,
                    _callContext.WebRtcSessionId,
                    _sipService?.LastCallInboundRtpPackets
                );
            }
            else if (_endedBy == CallEndedBy.Unknown)
            {
                MainWindow.Log($"{logPrefix} SendCallDetails: Call still in progress (EndedBy=Unknown), skipping AmoCRM/history update");
            }
            else
            {
                MainWindow.Log($"{logPrefix} SendCallDetails: Deferring AmoCRM/history update (EndedBy={_endedBy}, sipActive={_sipService?.IsInCall == true})");
            }
        }

        private void OnSipRecordingFinalized(string? recordingPath)
        {
            if (_useWebRtc || string.IsNullOrEmpty(recordingPath))
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => OnSipRecordingFinalized(recordingPath));
                return;
            }

            MainWindow.Log($"{GetTransportLogPrefix()} SIP recording finalized: {recordingPath}");
            SendCallDetails();
        }

        /// <summary>
        /// Beeline/Asterisk IVR: 183+RTP without 200 OK — infer answered from inbound RTP count.
        /// </summary>
        private void TryInferSipAnswerFromEarlyMedia(string logPrefix)
        {
            if (_wasAnswered || _answerTime.HasValue)
                return;
            if (_endedBy == CallEndedBy.Unknown)
                return;

            int? inboundRtp = _sipService?.LastCallInboundRtpPackets;
            if (inboundRtp is null or < 50)
                return;
            if (!_ringbackStartTime.HasValue)
                return;

            _wasAnswered = true;
            _answerTime = _ringbackStartTime;
            if (!_ringbackEndTime.HasValue)
                _ringbackEndTime = DateTime.Now;

            MainWindow.Log($"{logPrefix} Inferred answered from early media/IVR (inbound RTP={inboundRtp} pkts)");
        }
        
        /// <summary>
        /// Возвращает ID лида AmoCRM: сначала из браузера (если звонок инициирован из AmoCRM),
        /// затем найденный во время звонка (если был выполнен поиск)
        /// </summary>
        /// <summary>Returns the outbound CallerID (P-Asserted-Identity) if PBX sent one.</summary>
        public string? GetOutboundCallerId() => _outboundCallerId;

        public bool HasWebRtcSession() =>
            !string.IsNullOrEmpty(_callContext?.WebRtcSessionId);

        /// <summary>Context for a PBX Originate callback timeout (no inbound INVITE within the wait window).</summary>
        public readonly struct OriginateCallbackTimeoutContext
        {
            public bool IsWebRtc { get; init; }
            public bool WebRtcReadyWhenOriginateSent { get; init; }
            public bool WebRtcReadyAtTimeout { get; init; }
            public string? GatewayExtension { get; init; }
        }

        /// <summary>
        /// PBX Originate did not callback with an INVITE before the wait window expired.
        /// </summary>
        public void ShowOriginateCallbackTimeout(OriginateCallbackTimeoutContext context)
        {
            if (_wasAnswered || (_callTimer?.IsEnabled ?? false) || _isClosing)
                return;

            string message;
            if (context.IsWebRtc)
            {
                if (!context.WebRtcReadyWhenOriginateSent || !context.WebRtcReadyAtTimeout)
                {
                    message =
                        "Could not connect: WebRTC was still connecting when the call started. " +
                        "Wait until the main window shows Connected, then try again.";
                }
                else
                {
                    message =
                        "Could not connect: PBX did not reach this softphone. " +
                        "Wait a few seconds and try again. If it keeps failing, restart Callspire.";
                }
            }
            else
            {
                string extHint = string.IsNullOrWhiteSpace(context.GatewayExtension)
                    ? "your PBX extension"
                    : $"extension {context.GatewayExtension}";
                message =
                    $"Could not connect: PBX did not call back within 20 seconds. " +
                    $"Check that SIP is registered on {extHint}.";
            }

            CallStatusTextBlock.Text = message;
            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
            MainWindow.Log(
                $"[CallWindow][Originate] UI timeout: isWebRtc={context.IsWebRtc}, " +
                $"readyAtSend={context.WebRtcReadyWhenOriginateSent}, readyAtTimeout={context.WebRtcReadyAtTimeout}, " +
                $"gatewayExt={context.GatewayExtension ?? ""}, message={message}");
        }

        /// <summary>True if this window was opened for an outbound call (does not change when the callee answers).</summary>
        public bool OpenedAsOutgoingCall => _openedAsOutgoingCall;

        /// <summary>Dialed / remote party number shown in this window.</summary>
        public string CallRemoteNumber => _phoneNumber;

        /// <summary>Lead id passed from Kommo browser click-to-call (not from in-call search).</summary>
        public long? GetAmoCrmBrowserLeadId() => _callContext.AmoCrmLeadId;

        public long? GetAmoCrmLeadId()
        {
            // Приоритет: сначала лид из браузера, затем найденный во время звонка
            long? leadId = _callContext.AmoCrmLeadId ?? _foundAmoCrmLeadId;
            MainWindow.Log($"[CallWindow] GetAmoCrmLeadId: returning {leadId?.ToString() ?? "null"} (browser={_callContext.AmoCrmLeadId?.ToString() ?? "null"}, found={_foundAmoCrmLeadId?.ToString() ?? "null"})");
            return leadId;
        }

        /// <summary>
        /// Marks this window as a PBX Originate outbound call (AnswerTime = remote party, not PBX callback).
        /// </summary>
        public void MarkAsOriginateCall()
        {
            _isOriginateCall = true;
            MainWindow.Log($"[CallWindow][Originate] Marked as originate call to {_phoneNumber}");
            // PBX Originate API can take 30–60s; keep the window open until callback INVITE or this timeout.
            StartOutboundConnectingTimeout(OriginateConnectingTimeoutMs);
        }

        /// <summary>True after PBX Originate callback was auto-answered on the native SIP leg.</summary>
        public bool IsSipOriginateAnswered => _sipOriginateAnswered;

        /// <summary>
        /// PBX Originate callback on SIP: auto-answer succeeded — switch outgoing window to active call UI.
        /// </summary>
        public void NotifyOriginateSipAnswered()
        {
            if (_sipOriginateAnswered)
                return;

            _sipOriginateAnswered = true;
            _isIncomingCall = false;
            _wasAnswered = true;
            _callStartTime = DateTime.Now;

            ShowCallControls();
            StartCallTimer();

            CallStatusTextBlock.Text = "Connected";
            CallTimerTextBlock.Text = "00:00:00";
            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");

            CallWindowHelpers.UpdateAnswerTime(ref _answerTime, ref _wasAnswered);
            CallWindowHelpers.UpdateRingbackEndTime(_ringbackStartTime, ref _ringbackEndTime);

            MainWindow.Log($"[CallWindow][Originate] SIP callback answered for {_phoneNumber}");
            SendCallDetails();
        }

        private void StartOutboundConnectingTimeout(int delayMs)
        {
            if (_isIncomingCall)
                return;
            if (!_useWebRtc && !_isOriginateCall)
                return;

            _connectingTimeoutCts?.Cancel();
            _connectingTimeoutCts?.Dispose();
            _connectingTimeoutCts = new CancellationTokenSource();
            var token = _connectingTimeoutCts.Token;

            _ = Task.Delay(delayMs, token).ContinueWith(t =>
            {
                if (t.IsCanceled)
                    return;

                Dispatcher.Invoke(() =>
                {
                    if (CallStatusTextBlock.Text != "Connecting..." ||
                        _wasAnswered ||
                        (_callTimer != null && _callTimer.IsEnabled) ||
                        _isClosing)
                    {
                        return;
                    }

                    var seconds = delayMs / 1000;
                    MainWindow.Log($"{GetTransportLogPrefix()} Connection timeout - no call started after {seconds} seconds" +
                                   (_isOriginateCall ? " (originate)" : ""));
                    CallStatusTextBlock.Text = "Connection Failed: Timeout";
                    CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");

                    HandleWebRtcUiEvent(new WebRtcEventDto
                    {
                        Type = "call_failed",
                        Message = $"Connection timeout - no call started after {seconds} seconds",
                        Cause = "Timeout"
                    });
                });
            }, TaskScheduler.Default);
        }

        private void CancelOutboundConnectingTimeout()
        {
            try
            {
                _connectingTimeoutCts?.Cancel();
                _connectingTimeoutCts?.Dispose();
            }
            catch { }
            finally
            {
                _connectingTimeoutCts = null;
            }
        }

        /// <summary>
        /// Associates this CallWindow with the WebRTC session created by the PBX Originate callback.
        /// Called when the PBX rings our extension as part of an Originate flow and the softphone auto-answers.
        /// </summary>
        public void SetOriginateWebRtcSessionId(string sessionId)
        {
            MarkAsOriginateCall();
            MainWindow.Log($"[CallWindow][Originate] Associating session {sessionId} with outgoing call to {_phoneNumber}");
            _callContext.WebRtcSessionId = sessionId;
        }

        public void SetOutboundCallerId(string callerId)
        {
            _outboundCallerId = callerId;
            MainWindow.Log($"{GetTransportLogPrefix()} Outbound CallerID set (originate): {callerId}");
        }
        
        /// <summary>
        /// Запускает асинхронный поиск контакта и лида в AmoCRM параллельно с разговором
        /// (оптимизация: поиск происходит во время звонка, а не после его завершения)
        /// </summary>
        private async void StartAmoCrmLeadSearchAsync()
        {
            // Предотвращаем повторный поиск
            if (_isSearchingAmoCrmLead)
            {
                MainWindow.Log($"[CallWindow] StartAmoCrmLeadSearchAsync: Search already in progress, skipping");
                return;
            }
            
            // Если лид уже есть (из браузера), не ищем
            if (_callContext.AmoCrmLeadId.HasValue)
            {
                MainWindow.Log($"[CallWindow] StartAmoCrmLeadSearchAsync: LeadId already set from browser ({_callContext.AmoCrmLeadId.Value}), skipping search");
                return;
            }
            
            // Проверяем, включена ли интеграция AmoCRM
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow == null || !mainWindow.IsAmoCrmServiceInitialized())
            {
                MainWindow.Log($"[CallWindow] StartAmoCrmLeadSearchAsync: AmoCRM integration not initialized, skipping search");
                return;
            }
            
            _isSearchingAmoCrmLead = true;
            MainWindow.Log($"[CallWindow] StartAmoCrmLeadSearchAsync: Starting parallel search for contact and lead (phone={_phoneNumber})");
            
            try
            {
                var amoCrmService = mainWindow.GetAmoCrmService();
                if (amoCrmService == null)
                {
                    MainWindow.Log($"[CallWindow] StartAmoCrmLeadSearchAsync: AmoCrmService is null");
                    return;
                }
                
                // Шаг 1: Находим контакт по номеру телефона
                long? contactId = await amoCrmService.FindContactByPhoneAsync(_phoneNumber);
                if (!contactId.HasValue)
                {
                    MainWindow.Log($"[CallWindow] StartAmoCrmLeadSearchAsync: Contact not found for phone {_phoneNumber}");
                    return;
                }
                
                MainWindow.Log($"[CallWindow] StartAmoCrmLeadSearchAsync: Contact found: {contactId.Value}");
                
                // Шаг 2: Находим открытый лид для контакта
                // Используем приватный метод через рефлексию или создаем публичный метод
                // Для простоты создадим публичный метод в AmoCrmService
                long? leadId = await amoCrmService.FindLeadByContactIdForCallAsync(contactId.Value, _phoneNumber);
                
                if (leadId.HasValue)
                {
                    _foundAmoCrmLeadId = leadId.Value;
                    MainWindow.Log($"[CallWindow] StartAmoCrmLeadSearchAsync: ✅ Lead found during call: {leadId.Value} (will be used when call ends)");
                }
                else
                {
                    MainWindow.Log($"[CallWindow] StartAmoCrmLeadSearchAsync: No open lead found for contact {contactId.Value} (will attach to contact when call ends)");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallWindow] StartAmoCrmLeadSearchAsync: Error during search: {ex.Message}");
                MainWindow.Log($"[CallWindow] StartAmoCrmLeadSearchAsync: Stack trace: {ex.StackTrace}");
            }
            finally
            {
                _isSearchingAmoCrmLead = false;
            }
        }
        
        // WebRTC Methods - использует WebRtcService
        public CallWindow(WebRtcConfig webRtcConfig, string phoneNumber, bool isIncomingCall = false, DateTime? callStartTime = null, string? webRtcSessionId = null, long? amoCrmLeadId = null, WebRtcService? webRtcService = null)
        {
            InitializeComponent();
            NativeWindowAppearanceManager.Attach(this);
            _webRtcConfig = webRtcConfig;
            _phoneNumber = phoneNumber;
            _isIncomingCall = isIncomingCall;
            _openedAsOutgoingCall = !isIncomingCall;
            _useWebRtc = true;
            _webRtcService = webRtcService ?? WebRtcService.Main;
            _callStartTime = callStartTime ?? DateTime.Now;
            _originalCallStartTime = _callStartTime;
            _incomingCallStartTime = isIncomingCall ? DateTime.Now : _callStartTime;
            _callWindowStartTime = DateTime.Now;
            
            // Создаем контекст звонка с транспортом WebRTC и leadId из браузера
            _callContext = new CallContext(CallTransport.WebRtc, phoneNumber, _callStartTime, webRtcSessionId: webRtcSessionId, amoCrmLeadId: amoCrmLeadId);
            
            MainWindow.Log($"[Call][WebRTC] CallWindow constructor: PhoneNumber: {phoneNumber}, IsIncomingCall: {isIncomingCall}, SessionId: {webRtcSessionId ?? "none"}, AmoCrmLeadId: {amoCrmLeadId?.ToString() ?? "null"}");
            
            UpdateTransportBadge();
            CallerNameTextBlock.Text = phoneNumber;
            
            // НЕ показываем WebRTC контейнер - используем WebRtcService
            // WebRtcContainer остается скрытым, UI управляется через CallWindow
            
            if (_isIncomingCall)
            {
                CallStatusTextBlock.Text = $"Incoming call from {phoneNumber}";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                ShowIncomingCallButtons();
                CallTimerTextBlock.Text = "";

                // Start ringtone for incoming WebRTC calls
                RingtoneService.Instance.Start();
            }
            else
            {
                CallStatusTextBlock.Text = "Connecting...";
                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                CallTimerTextBlock.Text = "";
                ShowCallControls();
                
                // Ищем контакт в AmoCRM для исходящего звонка
                LoadContactNameFromAmoCrm(phoneNumber);
                
                StartOutboundConnectingTimeout(StandardConnectingTimeoutMs);
            }
            
            // Подписываемся на события WebRTC сервиса
            (_webRtcService ?? WebRtcService.Main).Event += OnWebRtcServiceEvent;
            
            KeyDown += CallWindow_KeyDown;
            Focusable = true;
            Focus();
        }
        
        /// <summary>
        /// Обработчик событий WebRTC сервиса
        /// </summary>
        private void OnWebRtcServiceEvent(WebRtcEventDto dto)
        {
            var logPrefix = GetTransportLogPrefix();
            try
            {
                // Логируем только важные события (не логируем js_log, pong, stats и т.д.)
                if (dto.Type != "js_log" && dto.Type != "pong" && dto.Type != "stats" && dto.Type != "ice_connection_state_change")
                {
                    MainWindow.Log($"{logPrefix} Event: {dto.Type}");
                }
                
                // Обновляем sessionId в контексте, если он был передан
                // КРИТИЧНО: Сохраняем amoCrmLeadId из существующего контекста при обновлении sessionId
                if (!string.IsNullOrEmpty(dto.SessionId) && _callContext.Transport == CallTransport.WebRtc)
                {
                    _callContext = new CallContext(
                        CallTransport.WebRtc,
                        _callContext.RemoteNumber,
                        _callContext.StartedAt,
                        webRtcSessionId: dto.SessionId,
                        amoCrmLeadId: _callContext.AmoCrmLeadId // Сохраняем leadId из браузера
                    );
                }
                
                // Recording events can be huge; never process them on UI thread.
                if (dto.Type == "recording_start" || dto.Type == "recording_chunk" || dto.Type == "recording_complete")
                {
                    MainWindow.Log($"{logPrefix} OnWebRtcServiceEvent: Recording event detected, enqueueing... (_webRtcRecorder={_webRtcRecorder != null})");
                    EnqueueRecordingEvent(dto);
                    return;
                }

                // UI updates must be on UI thread, but use BeginInvoke to avoid deadlocks.
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => HandleWebRtcUiEvent(dto)));
                }
                else
                {
                    HandleWebRtcUiEvent(dto);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallWindow] ERROR in OnWebRtcServiceEvent: {ex.Message}");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _technicalDetails.Add($"[{DateTime.Now:HH:mm:ss.fff}] ERROR processing WebRTC event: {ex.Message}");
                }));
            }
        }

        private void HandleWebRtcUiEvent(WebRtcEventDto dto)
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
                            
                        case "makeCall_started":
                            CancelOutboundConnectingTimeout();
                            // Звонок начат - обновляем статус с "Connecting..." на "Calling..."
                            var makeCallDetail = $"{timestamp} Making call (WebRTC)";
                            if (!string.IsNullOrEmpty(dto.SessionId))
                            {
                                makeCallDetail += $" (SessionId: {dto.SessionId})";
                            }
                            _technicalDetails.Add(makeCallDetail);
                            MainWindow.Log($"{logPrefix} Making call...");
                            CallStatusTextBlock.Text = "Calling...";
                            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");

                            // РАНЬШЕ: здесь запускали StartWebRtcRecording(), чтобы писать гудки с самого начала.
                            // Но на этом этапе еще нет активной WebRTC-сессии (session/PeerConnection),
                            // поэтому JS выдавал RecordingError "Cannot start recording: no active session"
                            // и запись вообще не создавалась.
                            // Теперь запись надёжно запускается на событиях call_accepted / audio_connected
                            // (когда sessionId уже установлен и медиа-сессия активна).

                            // КРИТИЧНО: Устанавливаем таймаут для статуса "Calling..."
                            // Если через 30 секунд нет события call_progress или call_accepted, считаем звонок неудачным
                            _ = Task.Delay(30000).ContinueWith(_ =>
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    // Проверяем, что статус все еще "Calling..." и звонок не был принят
                                        if (CallStatusTextBlock.Text == "Calling..." &&
                                            !_wasAnswered &&
                                            (_callTimer == null || !_callTimer.IsEnabled) &&
                                            !_isClosing)
                                    {
                                        MainWindow.Log($"{logPrefix} Call timeout - no progress after 30 seconds");
                                        CallStatusTextBlock.Text = "Call Failed: Timeout";
                                        CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                                        
                                        // Отправляем событие call_failed для обработки
                                        var timeoutDto = new WebRtcEventDto
                                        {
                                            Type = "call_failed",
                                            SessionId = dto.SessionId,
                                            Message = "Call timeout - no progress after 30 seconds",
                                            Cause = "Timeout"
                                        };
                                        HandleWebRtcUiEvent(timeoutDto);
                                    }
                                });
                            });
                            break;
                            
                        case "new_session":
                            CancelOutboundConnectingTimeout();
                            MainWindow.Log($"{logPrefix} ⚠️⚠️⚠️ NEW SESSION EVENT RECEIVED ⚠️⚠️⚠️");
                            MainWindow.Log($"{logPrefix} SessionId: {dto.SessionId ?? "NULL"}");
                            
                            // КРИТИЧНО: Сохраняем sessionId в контексте звонка для последующего использования в hangup
                            // КРИТИЧНО: Сохраняем amoCrmLeadId из существующего контекста при обновлении sessionId
                            if (!string.IsNullOrEmpty(dto.SessionId) && _callContext.Transport == CallTransport.WebRtc)
                            {
                                _callContext = new CallContext(
                                    CallTransport.WebRtc,
                                    _callContext.RemoteNumber,
                                    _callContext.StartedAt,
                                    webRtcSessionId: dto.SessionId,
                                    amoCrmLeadId: _callContext.AmoCrmLeadId // Сохраняем leadId из браузера
                                );
                                MainWindow.Log($"{logPrefix} ✅ SessionId saved to _callContext: {dto.SessionId}, AmoCrmLeadId preserved: {_callContext.AmoCrmLeadId?.ToString() ?? "null"}");
                            }
                            else
                            {
                                MainWindow.Log($"{logPrefix} ⚠️ WARNING: Cannot save sessionId - Transport={_callContext?.Transport}, SessionId={dto.SessionId ?? "NULL"}");
                            }
                            
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
                            CallStatusTextBlock.Text = "Calling";
                            // Originate callback (180 Ringing) may not emit a separate "ringing" JS event when originator=remote.
                            if (_isOriginateCall && !_wasAnswered && !(_callTimer?.IsEnabled ?? false))
                            {
                                if (!_ringbackStartTime.HasValue)
                                {
                                    _ringbackStartTime = DateTime.Now;
                                    SendCallDetails();
                                }
                                RingbackToneService.Instance.Play();
                                StartRingbackUiTimer();
                                MainWindow.Log($"{logPrefix} Originate: local ringback started on call_progress (180 Ringing)");
                            }
                            break;
                            
                        case "ringing":
                            // Guard: ignore late "ringing" events after call is connected/answered.
                            if (_wasAnswered || (_callTimer != null && _callTimer.IsEnabled))
                            {
                                // Ensure ringback is stopped if something tries to restart it.
                                RingbackToneService.Instance.Stop();
                                StopRingbackUiTimer();
                                break;
                            }
                            if (!_ringbackStartTime.HasValue)
                            {
                                _ringbackStartTime = DateTime.Now;
                                // Отправляем обновление деталей при начале гудков
                                SendCallDetails();
                            }
                            
                            // Воспроизводим ringback tone для WebRTC звонков
                            RingbackToneService.Instance.Play();
                            
                            var ringingDetail = $"{timestamp} Remote party ringing (WebRTC)";
                            _technicalDetails.Add(ringingDetail);
                            MainWindow.Log($"{logPrefix} Remote party ringing");
                            // UX: show Calling + timer while ringback is playing
                            CallStatusTextBlock.Text = "Calling";
                            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                            if (!_wasAnswered)
                            {
                                StartRingbackUiTimer();
                            }
                            break;
                            
                        case "call_accepted":
                        case "call_confirmed":
                            // КРИТИЧНО: Если окно уже закрывается, не обрабатываем события
                            if (_isClosing)
                            {
                                MainWindow.Log($"{logPrefix} call_accepted/call_confirmed ignored: window is closing");
                                break;
                            }
                            
                            MainWindow.Log($"{logPrefix} ===== CALL ACCEPTED EVENT RECEIVED =====");
                            MainWindow.Log($"{logPrefix} Event type: {dto.Type}");
                            MainWindow.Log($"{logPrefix} Session ID: {dto.SessionId}");
                            MainWindow.Log($"{logPrefix} Source: {dto.Data?.ToString() ?? "unknown"}");
                            
                            // ДЕТАЛЬНОЕ ЛОГИРОВАНИЕ состояния ringback tone перед остановкой
                            MainWindow.Log($"{logPrefix} Ringback tone state BEFORE stop:");
                            MainWindow.Log($"{logPrefix}   - Using RingbackToneService singleton, stopping...");
                            
                            // Do NOT stop ringback / mark connected until ICE is connected.
                            // Otherwise user hears: ringback -> silence -> remote audio, and IVR prompt start is lost.
                            _webRtcAcceptedPending = true;
                            // UX: signaling is confirmed, but media may still be establishing (ICE).
                            // Show a more accurate status than "Calling" while we keep ringback.
                            if (!_webRtcIceConnected && !_webRtcRemoteAudioStarted)
                            {
                                CallStatusTextBlock.Text = "Connecting media...";
                                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                            }
                            // If remote audio already started (detected by analyser), finalize immediately
                            // even if ICE event did not arrive yet.
                            if (_webRtcRemoteAudioStarted)
                            {
                                MainWindow.Log($"{logPrefix} Remote audio already started; finalizing WebRTC connected UI/state");
                                FinalizeWebRtcConnected(timestamp, logPrefix);
                                break;
                            }

                            if (!_webRtcIceConnected)
                            {
                                MainWindow.Log($"{logPrefix} call_accepted received but ICE not connected yet; keeping ringback until ICE connected");
                                StartWebRtcMediaConnectWatchdog(timestamp, logPrefix);
                                break;
                            }
                            
                            FinalizeWebRtcConnected(timestamp, logPrefix);
                            
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
                            CallStatusTextBlock.Text = "Connected";
                            CallTimerTextBlock.Text = "00:00:00";
                            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                            
                            // ОПТИМИЗАЦИЯ: Запускаем поиск лида в AmoCRM параллельно с разговором
                            // К моменту завершения звонка лид уже будет найден, останется только прикрепить запись
                            StartAmoCrmLeadSearchAsync();
                            break;
                            
                        case "call_failed":
                            if (_webRtcCallTerminationUiHandled)
                            {
                                MainWindow.Log($"{logPrefix} call_failed ignored (termination UI already handled)");
                                break;
                            }
                            _webRtcCallTerminationUiHandled = true;

                            StopWebRtcMediaConnectWatchdog();
                            // Останавливаем ringback tone при неудаче звонка
                            RingbackToneService.Instance.Stop();
                            StopRingbackUiTimer();
                            
                            // Останавливаем музыку удержания при неудаче звонка
                            try
                            {
                                HoldMusicService.Instance.Stop();
                            }
                            catch (Exception ex)
                            {
                                MainWindow.Log($"{logPrefix} Failed to stop hold music on call failure: {ex.Message}");
                            }
                            
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
                            
                            // КРИТИЧНО: Определяем читаемый статус на основе причины ошибки
                            string userFriendlyStatus = "Call Failed";
                            if (!string.IsNullOrEmpty(dto.Cause))
                            {
                                var causeLower = dto.Cause.ToLowerInvariant();
                                if (causeLower.Contains("cancel") || causeLower.Contains("reject") || causeLower.Contains("busy"))
                                {
                                    userFriendlyStatus = "Call Rejected";
                                }
                                else if (causeLower.Contains("timeout") || causeLower.Contains("not found"))
                                {
                                    userFriendlyStatus = "Call Failed: No Answer";
                                }
                                else if (causeLower.Contains("decline"))
                                {
                                    userFriendlyStatus = "Call Declined";
                                }
                                else
                                {
                                    // Для других ошибок показываем краткое сообщение без технических деталей
                                    userFriendlyStatus = "Call Failed";
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
                            
                            // КРИТИЧНО: Устанавливаем читаемый статус вместо технических деталей
                            // Проверяем, не был ли уже установлен статус "Call Rejected" при нажатии кнопки отклонения
                            if (CallStatusTextBlock.Text != "Call Rejected")
                            {
                                CallStatusTextBlock.Text = userFriendlyStatus;
                            }
                            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                            CallTimerTextBlock.Text = "";
                            
                            // КРИТИЧНО: Сбрасываем флаги состояния при неудаче звонка
                            // НО: если звонок уже был принят (есть AnswerTime), не сбрасываем _wasAnswered
                            // Это важно для случаев, когда робот ответил, но пользователь сбросил звонок
                            if (!_answerTime.HasValue)
                            {
                            _wasAnswered = false;
                            }
                            else
                            {
                                MainWindow.Log($"{logPrefix} ⚠️ call_failed received but AnswerTime exists ({_answerTime}), keeping _wasAnswered={_wasAnswered} (call was answered before failure)");
                            }
                            _isOnHold = false;
                            
                            // Who ended the call? Do not override if it was already determined (e.g., user pressed Hangup).
                            // Many WebRTC failures with "Canceled/Cancelled" are initiated locally.
                            if (_endedBy == CallEndedBy.Unknown)
                            {
                                var causeLower = (dto.Cause ?? failMsg ?? "").ToLowerInvariant();
                                _endedBy = (causeLower.Contains("cancel"))
                                    ? CallEndedBy.LocalUser
                                    : CallEndedBy.RemoteParty;
                            }
                            SendCallDetails(); // Отправляем детали перед закрытием
                            _isClosing = true;
                            _ = Task.Delay(2000).ContinueWith(_ =>
                            {
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    try { Close(); } catch (Exception closeEx) { MainWindow.Log($"{logPrefix} call_failed delayed Close: {closeEx.Message}"); }
                                }));
                            });
                            break;
                            
                        case "call_ended":
                            if (_webRtcCallTerminationUiHandled)
                            {
                                MainWindow.Log($"{logPrefix} call_ended ignored (termination UI already handled)");
                                break;
                            }
                            _webRtcCallTerminationUiHandled = true;

                            StopWebRtcMediaConnectWatchdog();
                            // КРИТИЧНО: Останавливаем все звуки и медиа при завершении звонка
                            RingbackToneService.Instance.Stop();
                            StopRingbackUiTimer();
                            
                            // КРИТИЧНО: Останавливаем музыку удержания при завершении звонка (независимо от того, кто завершил)
                            try
                            {
                                HoldMusicService.Instance.Stop();
                                MainWindow.Log($"{logPrefix} Hold music stopped on call end");
                            }
                            catch (Exception ex)
                            {
                                MainWindow.Log($"{logPrefix} Failed to stop hold music on call end: {ex.Message}");
                            }
                            
                            // Сбрасываем флаг удержания при завершении звонка
                            _isOnHold = false;
                            
                            // Останавливаем запись звонка
                            StopWebRtcRecording();
                            
                            // Записываем время окончания гудков, если еще не записано
                            if (_ringbackStartTime.HasValue && !_ringbackEndTime.HasValue)
                            {
                                _ringbackEndTime = DateTime.Now;
                            }
                            
                            // Определяем, кто завершил звонок (если не определено ранее)
                            if (_endedBy == CallEndedBy.Unknown)
                            {
                                // Проверяем originator из события
                                string? originatorStr = null;
                                if (dto.Data != null && dto.Data.HasValue && dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (dto.Data.Value.TryGetProperty("originator", out var originatorEl))
                                    {
                                        originatorStr = originatorEl.GetString();
                                    }
                                }
                                
                                if (!string.IsNullOrEmpty(originatorStr))
                                {
                                    _endedBy = (originatorStr == "local" || originatorStr == "LocalUser") 
                                        ? CallEndedBy.LocalUser 
                                        : CallEndedBy.RemoteParty;
                                    MainWindow.Log($"{logPrefix} Call ended by: {originatorStr} (EndedBy: {_endedBy})");
                                }
                                else
                                {
                                    // Если originator не определен, используем логику по умолчанию
                                    _endedBy = _wasAnswered ? CallEndedBy.RemoteParty : CallEndedBy.LocalUser;
                                    MainWindow.Log($"{logPrefix} Call ended by: Unknown originator, using default: {_endedBy}");
                                }
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
                            
                            MainWindow.Log($"{logPrefix} Call ended (EndedBy: {_endedBy})");
                            
                            // КРИТИЧНО: Обновляем UI перед отправкой деталей
                            CallStatusTextBlock.Text = "Call Ended";
                            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                            CallTimerTextBlock.Text = "";
                            
                            // Отправляем детали перед завершением
                            SendCallDetails();
                            
                            _isClosing = true;
                            
                            // Останавливаем таймер
                            if (_callTimer != null)
                            {
                                _callTimer.Stop();
                            }
                            
                            // Вычисляем длительность звонка для истории
                            TimeSpan? duration = null;
                            if (_wasAnswered)
                            {
                                var startTime = _isIncomingCall ? (_answerTime ?? _incomingCallStartTime) : _callStartTime;
                                if (startTime != default)
                                {
                                    duration = DateTime.Now - startTime;
                                }
                            }
                            
                            // Уведомляем MainWindow о завершении входящего звонка
                            if (_wasAnswered)
                            {
                                OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Ended, duration);
                            }
                            else if (_isIncomingCall)
                            {
                                OnIncomingCallStatusChanged?.Invoke(_phoneNumber, _incomingCallStartTime, CallStatus.Cancelled, null);
                            }
                            
                            // Close asynchronously: avoids rare re-entrancy / IsLoaded edge cases after SendCallDetails
                            // (MainWindow may Dispatcher.Invoke during OnCallDetailsChanged). Do not require IsLoaded.
                            MainWindow.Log($"{logPrefix} Scheduling Close after call_ended (_isClosing={_isClosing})");
                            try
                            {
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    try
                                    {
                                        MainWindow.Log($"{logPrefix} BeginInvoke: closing CallWindow after call_ended");
                                        Close();
                                    }
                                    catch (Exception exClose)
                                    {
                                        MainWindow.Log($"{logPrefix} BeginInvoke Close failed: {exClose.Message}");
                                    }
                                }), System.Windows.Threading.DispatcherPriority.Normal);
                            }
                            catch (Exception closeEx)
                            {
                                MainWindow.Log($"{logPrefix} Error scheduling Close after call_ended: {closeEx.Message}");
                                try { Close(); } catch (Exception ex2) { MainWindow.Log($"{logPrefix} Direct Close failed: {ex2.Message}"); }
                            }
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

                            if (!_webRtcIceConnected && (iceState == "connected" || iceState == "completed"))
                            {
                                _webRtcIceConnected = true;
                                MainWindow.Log($"{logPrefix} ICE connected; finalizing WebRTC connected UI/state");
                                if (_webRtcAcceptedPending)
                                {
                                    FinalizeWebRtcConnected(timestamp, logPrefix);
                                }
                            }
                            else if (iceState == "failed" || iceState == "disconnected")
                            {
                                // If signaling was already accepted but ICE failed, don't leave UI stuck in "Calling".
                                if (_webRtcAcceptedPending && !_webRtcConnectedFinalized)
                                {
                                    MainWindow.Log($"{logPrefix} ❌ ICE state '{iceState}' while accepted pending; hanging up to avoid stuck call");
                                    _technicalDetails.Add($"{timestamp} ❌ ICE state '{iceState}' (media failed)");
                                    CallStatusTextBlock.Text = "Media connection failed (ICE). Check TURN / firewall / NAT.";
                                    CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                                    try { RingbackToneService.Instance.Stop(); } catch { }
                                    StopRingbackUiTimer();
                                    _endedBy = CallEndedBy.LocalUser;
                                    FireAndForget(WebRtcHangupAsync(), "ICE failure hangup");
                                }
                            }
                            else if (_useWebRtc && iceState == "closed" &&
                                     (_webRtcConnectedFinalized || _wasAnswered) &&
                                     !_isClosing && !_webRtcCallTerminationUiHandled)
                            {
                                // Defense in depth: some stacks emit ICE closed before JsSIP 'ended' / engine call_ended.
                                MainWindow.Log($"{logPrefix} ICE closed after established call; host fallback → call_ended");
                                JsonElement? fallbackData = null;
                                try
                                {
                                    using var doc = JsonDocument.Parse("{\"originator\":\"remote\",\"cause\":\"ICE closed (host)\"}");
                                    fallbackData = doc.RootElement.Clone();
                                }
                                catch { /* ignore */ }

                                HandleWebRtcUiEvent(new WebRtcEventDto
                                {
                                    Type = "call_ended",
                                    SessionId = _callContext?.Transport == CallTransport.WebRtc ? _callContext.WebRtcSessionId : dto.SessionId,
                                    Cause = "ICE closed",
                                    Message = "ICE connection closed (host detected)",
                                    Data = fallbackData
                                });
                            }
                            break;

                        case "remote_audio_started":
                            // Sent from JS when analyser detects non-silent remote audio samples.
                            // This is the earliest reliable moment to stop local ringback without masking IVR start.
                            if (!_webRtcRemoteAudioStarted)
                            {
                                _webRtcRemoteAudioStarted = true;
                                if (dto.Data != null && dto.Data.HasValue && dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (dto.Data.Value.TryGetProperty("waitedMs", out var waitedEl))
                                    {
                                        MainWindow.Log($"{logPrefix} ✅ Remote audio started: analyser waited ~{waitedEl} ms after probe start (non-silent samples)");
                                    }
                                    else
                                    {
                                        MainWindow.Log($"{logPrefix} ✅ Remote audio started (non-silent samples detected); stopping ringback / finalizing if pending");
                                    }
                                }
                                else
                                {
                                    MainWindow.Log($"{logPrefix} ✅ Remote audio started (non-silent samples detected); stopping ringback / finalizing if pending");
                                }
                            }

                            // PBX early media / callee voice — stop local ringback so it is not masked.
                            try { RingbackToneService.Instance.Stop(); } catch { }

                            // If we did not get ICE connected yet, treat this as media-ready.
                            if (!_webRtcIceConnected)
                            {
                                _webRtcIceConnected = true;
                                MainWindow.Log($"{logPrefix} Remote audio started; treating ICE as connected for UI finalization");
                            }

                            if (_webRtcAcceptedPending)
                            {
                                FinalizeWebRtcConnected(timestamp, logPrefix);
                            }

                            // Originate: PBX callback omits X-Callspire-Originate; treat sustained remote
                            // audio after B-leg re-INVITE as callee answer (C# fallback if JS missed it).
                            if (_isOriginateCall && !_originateRemoteAnswered && _webRtcSdpStableCount >= 2)
                            {
                                int waitedMs = 0;
                                bool afterReneg = false;
                                if (dto.Data != null && dto.Data.HasValue && dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                                {
                                    if (dto.Data.Value.TryGetProperty("waitedMs", out var waitedEl) && waitedEl.TryGetInt32(out var wm))
                                        waitedMs = wm;
                                    if (dto.Data.Value.TryGetProperty("afterRenegotiation", out var renegEl))
                                        afterReneg = renegEl.GetBoolean();
                                }

                                if (afterReneg || waitedMs >= 1000)
                                {
                                    MainWindow.Log($"{logPrefix} Originate: remote audio after B-leg bridge (waitedMs={waitedMs}, afterReneg={afterReneg}) — marking answered");
                                    FinalizeOriginateRemoteAnswered(timestamp, logPrefix);
                                }
                            }
                            break;

                        case "remote_party_answered":
                            MainWindow.Log($"{logPrefix} Originate: remote party answered event received");
                            FinalizeOriginateRemoteAnswered(timestamp, logPrefix);
                            break;

                        case "sdp_renegotiated":
                            {
                                int stableCount = 0;
                                if (dto.Data != null && dto.Data.HasValue && dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object
                                    && dto.Data.Value.TryGetProperty("stableCount", out var scEl))
                                {
                                    stableCount = scEl.GetInt32();
                                }
                                if (stableCount > _webRtcSdpStableCount)
                                    _webRtcSdpStableCount = stableCount;
                                MainWindow.Log($"{logPrefix} SDP renegotiated (re-INVITE stable #{stableCount})");
                                _technicalDetails.Add($"{timestamp} SDP renegotiated (re-INVITE #{stableCount})");
                            }
                            break;
                            
                        case "audio_track_muted":
                            MainWindow.Log($"{logPrefix} ===== AUDIO TRACK MUTED EVENT (CRITICAL) =====");
                            MainWindow.Log($"{logPrefix} ⚠️⚠️⚠️ REMOTE AUDIO TRACK IS MUTED ⚠️⚠️⚠️");
                            MainWindow.Log($"{logPrefix} Session ID: {dto.SessionId ?? "none"}");
                            
                            // Извлекаем TrackId из dto.Data
                            string? trackId = null;
                            if (dto.Data != null && dto.Data.HasValue && dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (dto.Data.Value.TryGetProperty("trackId", out var trackIdEl))
                                {
                                    trackId = trackIdEl.GetString();
                                }
                            }
                            MainWindow.Log($"{logPrefix} Track ID: {trackId ?? "none"}");
                            MainWindow.Log($"{logPrefix} ⚠️ This means the remote peer has muted the audio track");
                            MainWindow.Log($"{logPrefix} ⚠️ Audio will NOT be transmitted even though track is 'live'");
                            MainWindow.Log($"{logPrefix} ⚠️ This is likely a WebRTC peer connection issue or remote peer configuration");
                            MainWindow.Log($"{logPrefix} ⚠️ You will NOT hear the remote party!");
                            
                            var mutedDetail = $"{timestamp} ⚠️ CRITICAL: Remote audio track is MUTED - audio will not be transmitted";
                            _technicalDetails.Add(mutedDetail);
                            break;
                            
                        case "audio_connected":
                            MainWindow.Log($"{logPrefix} ✅ Audio connected");
                            // Audio element is ready, but RTP may still be negotiating (ICE checking).
                            // Keep ringback / do not mark answered here.
                            var audioDetail = $"{timestamp} Audio track connected (WebRTC, waiting for ICE)";
                            _technicalDetails.Add(audioDetail);
                            break;
                            
                        case "audio_playing":
                            // Не логируем - это нормальное событие во время звонка
                            break;
                            
                        case "audio_paused":
                            MainWindow.Log($"{logPrefix} ⚠️⚠️⚠️ REMOTE AUDIO PAUSED ⚠️⚠️⚠️");
                            MainWindow.Log($"{logPrefix} This should NOT happen during active call!");
                            MainWindow.Log($"{logPrefix} Session ID: {dto.SessionId ?? "none"}");
                            break;
                            
                        case "audio_error":
                            MainWindow.Log($"{logPrefix} ❌❌❌ REMOTE AUDIO ERROR ❌❌❌");
                            MainWindow.Log($"{logPrefix} This will prevent you from hearing the remote party!");
                            MainWindow.Log($"{logPrefix} Session ID: {dto.SessionId ?? "none"}");
                            if (dto.Data != null)
                            {
                                MainWindow.Log($"{logPrefix} Error details: {dto.Data}");
                            }
                            break;
                            
                        case "recording_started":
                            MainWindow.Log($"{logPrefix} Recording started (JavaScript)");
                            break;
                            
                        case "recording_stopped":
                            MainWindow.Log($"{logPrefix} Recording stopped (JavaScript) - waiting for recording_complete event");
                            break;
                            
                        case "call_hold":
                            // КРИТИЧНО: Определяем, кто поставил звонок на удержание (локально или удаленно)
                            // Если originator = 'remote', значит удаленная сторона поставила нас на удержание
                            // Если originator = 'local', значит мы сами поставили удаленную сторону на удержание
                            string? holdOriginator = null;
                            if (dto.Data != null && dto.Data.HasValue && dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (dto.Data.Value.TryGetProperty("originator", out var originatorEl))
                                {
                                    holdOriginator = originatorEl.GetString();
                                }
                            }
                            
                            // КРИТИЧНО: Музыка удержания должна играть только когда УДАЛЕННАЯ сторона поставила НАС на удержание
                            // Если мы сами поставили удаленную сторону на удержание (originator = 'local'), музыку не запускаем
                            bool isRemoteHold = holdOriginator == "remote";
                            
                            if (!_isOnHold)
                            {
                                _isOnHold = true;
                                
                                // Remote hold: PBX streams MOH to us via RTP — no local audio needed.
                                MainWindow.Log($"{logPrefix} Hold detected (originator: {holdOriginator ?? "unknown"}), PBX handles MOH");
                                
                                // Обновляем UI
                                if (HoldIconPause != null) HoldIconPause.Visibility = Visibility.Collapsed;
                                if (HoldIconPlay != null) HoldIconPlay.Visibility = Visibility.Visible;
                                HoldButton.Background = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
                                CallStatusTextBlock.Text = "On Hold";
                                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                                
                                // Останавливаем таймер
                                _callTimer?.Stop();
                                
                                var holdDetail = $"{timestamp} Call put on hold ({(isRemoteHold ? "remote" : "local")})";
                                if (!string.IsNullOrEmpty(dto.SessionId))
                                {
                                    holdDetail += $" (SessionId: {dto.SessionId})";
                                }
                                _technicalDetails.Add(holdDetail);
                                MainWindow.Log($"{logPrefix} Event: call_hold (originator: {holdOriginator ?? "unknown"})");
                            }
                            break;
                            
                        case "call_unhold":
                            // КРИТИЧНО: Определяем, кто снял звонок с удержания (локально или удаленно)
                            // Если originator = 'remote', значит удаленная сторона сняла нас с удержания
                            // Если originator = 'local', значит мы сами сняли удаленную сторону с удержания
                            string? unholdOriginator = null;
                            if (dto.Data != null && dto.Data.HasValue && dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (dto.Data.Value.TryGetProperty("originator", out var originatorEl))
                                {
                                    unholdOriginator = originatorEl.GetString();
                                }
                            }
                            
                            bool isRemoteUnhold = unholdOriginator == "remote";
                            
                            if (_isOnHold)
                            {
                                _isOnHold = false;
                                
                                // КРИТИЧНО: ВСЕГДА останавливаем музыку удержания при unhold, независимо от originator
                                // Музыка может играть локально если:
                                // 1. Удаленная сторона поставила нас на удержание (originator='remote' в call_hold)
                                // 2. Или если была какая-то ошибка/задержка в обработке событий
                                // Поэтому всегда останавливаем музыку при unhold для надежности
                                try
                                {
                                    HoldMusicService.Instance.Stop();
                                    MainWindow.Log($"{logPrefix} Hold music stopped on unhold (originator: {unholdOriginator ?? "unknown"})");
                                }
                                catch (Exception ex)
                                {
                                    MainWindow.Log($"{logPrefix} Failed to stop hold music on unhold: {ex.Message}");
                                }
                                
                                // Обновляем UI
                                if (HoldIconPause != null) HoldIconPause.Visibility = Visibility.Visible;
                                if (HoldIconPlay != null) HoldIconPlay.Visibility = Visibility.Collapsed;
                                HoldButton.Background = (System.Windows.Media.Brush)FindResource("BackgroundMediumBrush");
                                CallStatusTextBlock.Text = "Connected";
                                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                                
                                // Возобновляем таймер
                                if (_callTimer != null && !_callTimer.IsEnabled)
                                {
                                    _callTimer.Start();
                                }
                                
                                var unholdDetail = $"{timestamp} Call resumed ({(isRemoteUnhold ? "remote" : "local")})";
                                if (!string.IsNullOrEmpty(dto.SessionId))
                                {
                                    unholdDetail += $" (SessionId: {dto.SessionId})";
                                }
                                _technicalDetails.Add(unholdDetail);
                                MainWindow.Log($"{logPrefix} Event: call_unhold (originator: {unholdOriginator ?? "unknown"})");
                            }
                            else
                            {
                                // Если мы не были на удержании, все равно останавливаем музыку на всякий случай
                                try
                                {
                                    HoldMusicService.Instance.Stop();
                                    MainWindow.Log($"{logPrefix} Hold music stopped on unhold (was not on hold, safety stop)");
                                }
                                catch (Exception ex)
                                {
                                    MainWindow.Log($"{logPrefix} Failed to stop hold music on unhold (safety): {ex.Message}");
                                }
                            }
                            break;
                            
                        default:
                            // Не логируем неважные события (js_log, pong, stats уже отфильтрованы выше)
                            if (dto.Type != "js_log" && dto.Type != "pong" && dto.Type != "stats")
                            {
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
                            }
                            break;
            }
        }

        private void EnqueueRecordingEvent(WebRtcEventDto dto)
        {
            var logPrefix = GetTransportLogPrefix();
            MainWindow.Log($"{logPrefix} EnqueueRecordingEvent: Type={dto.Type}, SessionId={dto.SessionId ?? "none"}, Queue size before={_recordingEventQueue.Count}");
            _recordingEventQueue.Enqueue(dto);
            MainWindow.Log($"{logPrefix} EnqueueRecordingEvent: Event enqueued, Queue size after={_recordingEventQueue.Count}, WorkerRunning={_recordingWorkerRunning}");
            if (Interlocked.CompareExchange(ref _recordingWorkerRunning, 1, 0) == 0)
            {
                MainWindow.Log($"{logPrefix} EnqueueRecordingEvent: Starting ProcessRecordingQueueAsync");
                _ = Task.Run(ProcessRecordingQueueAsync);
            }
            else
            {
                MainWindow.Log($"{logPrefix} EnqueueRecordingEvent: Worker already running, event queued");
            }
        }

        private async Task ProcessRecordingQueueAsync()
        {
            var logPrefix = GetTransportLogPrefix();
            MainWindow.Log($"{logPrefix} ProcessRecordingQueueAsync: Started, initial queue size={_recordingEventQueue.Count}");
            try
            {
                int processedCount = 0;
                while (_recordingEventQueue.TryDequeue(out var dto))
                {
                    processedCount++;
                    MainWindow.Log($"{logPrefix} ProcessRecordingQueueAsync: Processing event #{processedCount}, Type={dto.Type}, SessionId={dto.SessionId ?? "none"}");
                    try
                    {
                        await ProcessRecordingEventAsync(dto);
                        MainWindow.Log($"{logPrefix} ProcessRecordingQueueAsync: Event #{processedCount} processed successfully");
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"{logPrefix} ProcessRecordingQueueAsync: Error processing recording event '{dto.Type}': {ex.Message}");
                        MainWindow.Log($"{logPrefix} ProcessRecordingQueueAsync: Stack trace: {ex.StackTrace}");
                    }
                }
                MainWindow.Log($"{logPrefix} ProcessRecordingQueueAsync: Finished, processed {processedCount} events, remaining queue size={_recordingEventQueue.Count}");
            }
            finally
            {
                Interlocked.Exchange(ref _recordingWorkerRunning, 0);
                MainWindow.Log($"{logPrefix} ProcessRecordingQueueAsync: Worker stopped, remaining queue size={_recordingEventQueue.Count}");
                // If new items arrived after we stopped, restart.
                if (!_recordingEventQueue.IsEmpty)
                {
                    MainWindow.Log($"{logPrefix} ProcessRecordingQueueAsync: Restarting worker for remaining {_recordingEventQueue.Count} events");
                    EnqueueRecordingEvent(new WebRtcEventDto { Type = "recording_noop" });
                }
            }
        }

        private async Task ProcessRecordingEventAsync(WebRtcEventDto dto)
        {
            var logPrefix = GetTransportLogPrefix();
            
            // Логируем все события recording для диагностики
            MainWindow.Log($"{logPrefix} ProcessRecordingEventAsync: Type={dto.Type}, SessionId={dto.SessionId ?? "none"}");

            // Ignore placeholder/noop.
            if (dto.Type == "recording_noop")
            {
                return;
            }

            if (dto.Type == "recording_start")
            {
                var recorder = _webRtcRecorder;
                if (recorder == null || !recorder.IsRecording) return;

                if (dto.Data != null && dto.Data.HasValue)
                {
                    var dataJson = dto.Data.Value;
                    if (dataJson.TryGetProperty("totalSize", out var totalSizeEl) &&
                        dataJson.TryGetProperty("totalChunks", out var totalChunksEl) &&
                        dataJson.TryGetProperty("hash", out var hashEl))
                    {
                        int totalSize = totalSizeEl.GetInt32();
                        int totalChunks = totalChunksEl.GetInt32();
                        string hash = hashEl.GetString() ?? "";
                        recorder.HandleRecordingStart(totalSize, totalChunks, hash);
                        MainWindow.Log($"{logPrefix} Recording start: {totalSize} bytes, {totalChunks} chunks");
                    }
                }
                return;
            }

            if (dto.Type == "recording_chunk")
            {
                var recorder = _webRtcRecorder;
                if (recorder == null) return;
                if (recorder is WebRtcCallRecorder wrtc && !wrtc.IsAcceptingRecordingChunks) return;
                if (recorder is not WebRtcCallRecorder && !recorder.IsRecording) return;

                if (dto.Data != null && dto.Data.HasValue)
                {
                    var dataJson = dto.Data.Value;
                    if (dataJson.TryGetProperty("chunkIndex", out var chunkIndexEl) &&
                        dataJson.TryGetProperty("data", out var dataEl) &&
                        dataJson.TryGetProperty("offset", out var offsetEl))
                    {
                        int chunkIndex = chunkIndexEl.GetInt32();
                        int offset = offsetEl.GetInt32();

                        // Convert number[] -> byte[] (still expensive, but now off UI thread).
                        if (dataEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            // pre-size if possible
                            int len = dataEl.GetArrayLength();
                            var bytes = new byte[len];
                            int i = 0;
                            foreach (var item in dataEl.EnumerateArray())
                            {
                                if (item.ValueKind == System.Text.Json.JsonValueKind.Number)
                                {
                                    bytes[i++] = (byte)item.GetInt32();
                                }
                            }
                            if (i > 0)
                            {
                                if (i != bytes.Length) Array.Resize(ref bytes, i);
                                recorder.HandleRecordingChunk(chunkIndex, bytes, offset);
                            }
                        }
                    }
                }
                return;
            }

            if (dto.Type == "recording_complete")
            {
                // КРИТИЧНО: Сохраняем ссылку на recorder до проверки, чтобы не потерять его при закрытии окна
                var recorder = _webRtcRecorder;
                if (recorder == null)
                {
                    MainWindow.Log($"{logPrefix} recording_complete: ⚠️ recorder is null, cannot save recording (window may be closed)");
                    return;
                }

                MainWindow.Log($"{logPrefix} recording_complete event received, processing... (recorder exists, IsRecording={recorder.IsRecording})");

                if (dto.Data == null || !dto.Data.HasValue)
                {
                    MainWindow.Log($"{logPrefix} recording_complete: Data is null or has no value");
                    return;
                }

                var dataJson = dto.Data.Value;
                string? hash = null;
                if (dataJson.TryGetProperty("hash", out var hashEl))
                {
                    hash = hashEl.GetString();
                    MainWindow.Log($"{logPrefix} recording_complete: hash={hash}");
                }

                byte[]? audioBytes = null;
                if (dataJson.TryGetProperty("audioData", out var audioDataEl))
                {
                    MainWindow.Log($"{logPrefix} recording_complete: audioData found, ValueKind={audioDataEl.ValueKind}");
                    if (audioDataEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        int len = audioDataEl.GetArrayLength();
                        MainWindow.Log($"{logPrefix} recording_complete: audioData is array, length={len}");
                        var bytes = new byte[len];
                        int i = 0;
                        foreach (var item in audioDataEl.EnumerateArray())
                        {
                            if (item.ValueKind == System.Text.Json.JsonValueKind.Number)
                            {
                                bytes[i++] = (byte)item.GetInt32();
                            }
                        }
                        if (i > 0)
                        {
                            if (i != bytes.Length) Array.Resize(ref bytes, i);
                            audioBytes = bytes;
                            MainWindow.Log($"{logPrefix} recording_complete: extracted {audioBytes.Length} bytes from array");
                        }
                    }
                    else if (audioDataEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var base64Audio = audioDataEl.GetString();
                        if (!string.IsNullOrEmpty(base64Audio))
                        {
                            MainWindow.Log($"{logPrefix} recording_complete: audioData is base64 string, length={base64Audio.Length}");
                            audioBytes = Convert.FromBase64String(base64Audio);
                            MainWindow.Log($"{logPrefix} recording_complete: decoded {audioBytes.Length} bytes from base64");
                        }
                    }
                }
                else
                {
                    MainWindow.Log($"{logPrefix} recording_complete: audioData property not found in data");
                }

                // Save/convert in background (already async-safe).
                if (audioBytes != null && audioBytes.Length > 0)
                {
                    MainWindow.Log($"{logPrefix} recording_complete: {audioBytes.Length / 1024} KB received, saving and converting...");
                    await recorder.SaveCompleteRecordingAsync(audioBytes, hash);
                    recorder.StopRecording();
                    MainWindow.Log($"{logPrefix} recording_complete: SaveCompleteRecordingAsync completed");
                }
                else if (!string.IsNullOrEmpty(hash))
                {
                    MainWindow.Log($"{logPrefix} recording_complete: no audioData, but hash present - file was sent in chunks, checking chunks...");
                    // Проверяем, есть ли chunks в recorder
                    if (recorder is WebRtcCallRecorder webRtcRecorder)
                    {
                        MainWindow.Log($"{logPrefix} recording_complete: finalizing with chunks (hash={hash})");
                    }
                    await recorder.SaveCompleteRecordingAsync(Array.Empty<byte>(), hash);
                    recorder.StopRecording();
                    MainWindow.Log($"{logPrefix} recording_complete: SaveCompleteRecordingAsync completed (chunks)");
                }
                else
                {
                    MainWindow.Log($"{logPrefix} recording_complete: ⚠️ No audioData and no hash - cannot save recording");
                }

                // Update path + push call details on UI thread.
                _webRtcRecordingFilePath = recorder.RecordingFilePath;
                if (!string.IsNullOrEmpty(_webRtcRecordingFilePath))
                {
                    // Fire-and-forget UI update.
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { SendCallDetails(); } catch { }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }

                // КРИТИЧНО: НЕ обнуляем _webRtcRecorder и НЕ делаем Dispose сразу
                // Recorder нужен для обработки recording_complete, который может прийти позже
                // Dispose и обнуление произойдет в OnClosed после задержки
                MainWindow.Log($"{logPrefix} recording_complete: Keeping recorder reference for potential late events");
                _isStoppingRecording = false;
                return;
            }

            await Task.CompletedTask;
        }
        
        public async Task WebRtcMakeCallAsync(string number)
        {
            try
            {
                MainWindow.Log($"[WebRTC CallWindow] Making call to: {number}");
                
                // Добавляем техническую деталь о начале звонка
                _technicalDetails.Add($"[{DateTime.Now:HH:mm:ss.fff}] Initiating WebRTC call to {number}");
                
                // Используем WebRTC сервис
                if (!(_webRtcService ?? WebRtcService.Main).IsReadyForCalls)
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
                
                await (_webRtcService ?? WebRtcService.Main).MakeCallAsync(targetNumber);
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
                if (!(_webRtcService ?? WebRtcService.Main).IsReadyForCalls)
                {
                    MainWindow.Log("[WebRTC CallWindow] ERROR: WebRTC service not ready");
                    throw new InvalidOperationException("WebRTC service not ready");
                }
                
                await (_webRtcService ?? WebRtcService.Main).AnswerAsync();
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
                MainWindow.Log("[WebRTC CallWindow] ⚠️⚠️⚠️ HANGUP CALLED ⚠️⚠️⚠️");
                
                // Получаем sessionId из контекста звонка
                string? sessionId = null;
                if (_callContext != null && _callContext.Transport == CallTransport.WebRtc)
                {
                    sessionId = _callContext.WebRtcSessionId;
                    MainWindow.Log($"[WebRTC CallWindow] SessionId from _callContext: {sessionId ?? "NULL"}");
                }
                else
                {
                    MainWindow.Log("[WebRTC CallWindow] ⚠️ WARNING: _callContext is null or not WebRTC!");
                }
                
                if (string.IsNullOrEmpty(sessionId))
                {
                    MainWindow.Log("[WebRTC CallWindow] Hangup skipped: no sessionId (no active WebRTC session)");
                    return;
                }
                
                MainWindow.Log($"[WebRTC CallWindow] Calling (_webRtcService ?? WebRtcService.Main).HangupAsync with sessionId: {sessionId}");
                await (_webRtcService ?? WebRtcService.Main).HangupAsync(sessionId);
                MainWindow.Log($"[WebRTC CallWindow] ✅ Hangup command sent via WebRTC service (sessionId: {sessionId ?? "null"})");
                
                // Даем время на обработку завершения звонка
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRTC CallWindow] ❌❌❌ ERROR: Failed to hangup WebRTC call ❌❌❌");
                MainWindow.Log($"[WebRTC CallWindow] Error message: {ex.Message}");
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
                            MainWindow.Log($"{GetTransportLogPrefix()} ===== CALL ACCEPTED/CONFIRMED (SIP) =====");
                            MainWindow.Log($"{GetTransportLogPrefix()} Event type: {evt.Type}");

                            // WebRTC: gate "connected" UX until ICE is connected to avoid ringback->silence gaps.
                            if (_useWebRtc)
                            {
                                _webRtcAcceptedPending = true;
                                if (!_webRtcIceConnected)
                                {
                                    CallStatusTextBlock.Text = "Connecting media...";
                                    CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                                    MainWindow.Log($"{GetTransportLogPrefix()} call_accepted received but ICE not connected yet; keeping ringback until ICE connected");
                                    StartWebRtcMediaConnectWatchdog($"{DateTime.Now:HH:mm:ss.fff}", GetTransportLogPrefix());
                                    break;
                                }

                                FinalizeWebRtcConnected($"{DateTime.Now:HH:mm:ss.fff}", GetTransportLogPrefix());
                                break;
                            }

                            // SIP (non-WebRTC) legacy path.
                            MainWindow.Log($"{GetTransportLogPrefix()} Stopping ringback tone...");
                            try
                            {
                                RingbackToneService.Instance.Stop();
                                MainWindow.Log($"{GetTransportLogPrefix()} ✅ Ringback tone STOPPED");
                            }
                            catch (Exception toneEx)
                            {
                                MainWindow.Log($"{GetTransportLogPrefix()} ❌ ERROR stopping ringback tone: {toneEx.Message}");
                            }

                            StopRingbackUiTimer();
                            MainWindow.Log($"{GetTransportLogPrefix()} ✅ Ringback UI timer stopped");

                            _wasAnswered = true;
                            _callStartTime = DateTime.Now;
                            MainWindow.Log($"{GetTransportLogPrefix()} Call start time: {_callStartTime}");

                            if (_isIncomingCall)
                            {
                                _isIncomingCall = false;
                                ShowCallControls();
                            }
                            StartCallTimer();
                            CallStatusTextBlock.Text = "Connected";
                            CallTimerTextBlock.Text = "00:00:00";
                            CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentGreenBrush");
                            MainWindow.Log($"{GetTransportLogPrefix()} ✅ Call status updated to 'Connected'");
                            MainWindow.Log($"{GetTransportLogPrefix()} ===== CALL ACCEPTED PROCESSING COMPLETE =====");
                            break;
                        case "call_failed":
                            // Извлекаем сообщение об ошибке безопасно, без логирования всего Data
                            string failData = "Unknown error";
                            string? failCause = null;
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
                                        failCause = causeEl.GetString();
                                        failData = failCause ?? "Unknown error";
                                    }
                                }
                            }
                            
                            // КРИТИЧНО: Определяем читаемый статус на основе причины ошибки
                            string userFriendlyStatus = "Call Failed";
                            if (!string.IsNullOrEmpty(failCause))
                            {
                                var causeLower = failCause.ToLowerInvariant();
                                if (causeLower.Contains("cancel") || causeLower.Contains("reject") || causeLower.Contains("busy"))
                                {
                                    userFriendlyStatus = "Call Rejected";
                                }
                                else if (causeLower.Contains("timeout") || causeLower.Contains("not found"))
                                {
                                    userFriendlyStatus = "Call Failed: No Answer";
                                }
                                else if (causeLower.Contains("decline"))
                                {
                                    userFriendlyStatus = "Call Declined";
                                }
                            }
                            
                            MainWindow.Log($"{GetTransportLogPrefix()} ✗ Call failed: {failData}");
                            
                            // КРИТИЧНО: Устанавливаем читаемый статус вместо технических деталей
                            // Проверяем, не был ли уже установлен статус "Call Rejected" при нажатии кнопки отклонения
                            if (CallStatusTextBlock.Text != "Call Rejected")
                            {
                                CallStatusTextBlock.Text = userFriendlyStatus;
                                CallStatusTextBlock.Foreground = (System.Windows.Media.Brush)FindResource("AccentRedBrush");
                            }
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
        
        /// <summary>
        /// Загружает имя контакта из AmoCRM по номеру телефона (только для исходящих звонков)
        /// </summary>
        private async void LoadContactNameFromAmoCrm(string phoneNumber)
        {
            try
            {
                // Проверяем, включена ли интеграция AmoCRM
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow == null || !mainWindow.IsAmoCrmServiceInitialized())
                {
                    return; // Интеграция не включена
                }
                
                // Получаем имя контакта из AmoCRM
                string? contactName = await mainWindow.GetAmoCrmContactNameAsync(phoneNumber);
                
                if (!string.IsNullOrEmpty(contactName))
                {
                    // Обновляем UI в главном потоке
                    Dispatcher.Invoke(() =>
                    {
                        // Показываем имя и номер: "Имя Контакта (номер)"
                        CallerNameTextBlock.Text = $"{contactName} ({phoneNumber})";
                        MainWindow.Log($"[CallWindow] Updated contact name from AmoCRM: {contactName} for {phoneNumber}");
                    });
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallWindow] Error loading contact name from AmoCRM: {ex.Message}");
            }
        }
    }
    
    // WebRTC Configuration
    public class WebRtcConfig
    {
        public string WsUri { get; set; } = "";      // "wss://pbx.example.com:8089/ws"
        public string SipUri { get; set; } = "";     // "sip:1001@pbx.example.com"
        public string Password { get; set; } = "";   // пароль расширения
        /// <summary>
        /// Включить детализированное логирование WebRTC (JS + C#).
        /// Используется для управления объемом логов без пересборки.
        /// </summary>
        public bool EnableDebug { get; set; } = true;
        
        // Пользовательский TURN‑сервер и креды (опционально).
        public string? TurnServer { get; set; }
        public string? TurnUsername { get; set; }
        public string? TurnPassword { get; set; }
    }
    
    // WebRTC Event
    public class WebRtcEvent
    {
        public string Type { get; set; } = "";
        public JsonElement? Data { get; set; }
    }
    
    // WebRtcCallState enum moved to Callspire.Core/WebRtc/WebRtcCallState.cs
}


#endif // WINDOWS
