using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Threading;
using Softphone.Audio;

namespace Softphone.Avalonia
{
    public partial class CallWindow : Window
    {
        // ── Events (same contract as WPF CallWindow) ──────────────────────────
        public event Action<string, DateTime, CallStatus, TimeSpan?>? OnIncomingCallStatusChanged;
        public event Action<string, DateTime, DateTime?, DateTime?, DateTime?, bool, CallEndedBy, List<string>?, TimeSpan?, string?, CallTransport?, string?, string?>? OnCallDetailsChanged;

        // ── State ─────────────────────────────────────────────────────────────
        private SipService?    _sipService;
        private WebRtcService? _webRtcService;
        private string         _phoneNumber = string.Empty;
        private bool           _isMuted         = false;
        private bool           _isOnHold        = false;
        private bool           _isKeypadVisible  = false;
        private bool           _isIncomingCall   = false;
        private readonly bool  _openedAsOutgoingCall;
        private bool           _useWebRtc        = false;
        private DateTime       _callStartTime;
        private DateTime       _originalCallStartTime;
        private DateTime       _callWindowStartTime;
        private bool           _wasAnswered      = false;
        private CallEndedBy    _endedBy          = CallEndedBy.Unknown;
        private DateTime?      _ringbackStartTime;
        private DateTime?      _ringbackEndTime;
        private DateTime?      _answerTime;
        private DispatcherTimer? _callTimer;
        private CancellationTokenSource? _connectingTimeoutCts;

        private const int StandardConnectingTimeoutMs  = 25000;
        private const int OriginateConnectingTimeoutMs = 90000;

        // ── Constructor — SIP ─────────────────────────────────────────────────
        public CallWindow(
            SipService sipService,
            string     phoneNumber,
            bool       isIncomingCall = false,
            DateTime?  callStartTime  = null,
            long?      amoCrmLeadId   = null)
        {
            InitializeComponent();
            _sipService           = sipService;
            _phoneNumber          = phoneNumber;
            _isIncomingCall       = isIncomingCall;
            _openedAsOutgoingCall = !isIncomingCall;
            _callStartTime        = callStartTime ?? DateTime.Now;
            _originalCallStartTime = _callStartTime;
            _callWindowStartTime   = DateTime.Now;

            AvaloniaWindowChromeHelper.Apply(this, preferAccent: true);
            InitializeUiState();
            WireUpSipEvents();
        }

        // ── Constructor — WebRTC ──────────────────────────────────────────────
        public CallWindow(
            WebRtcService webRtcService,
            string        phoneNumber,
            bool          isIncomingCall  = false,
            DateTime?     callStartTime   = null,
            long?         amoCrmLeadId    = null,
            bool          isOriginateCall = false)
        {
            InitializeComponent();
            _webRtcService        = webRtcService;
            _useWebRtc            = true;
            _phoneNumber          = phoneNumber;
            _isIncomingCall       = isIncomingCall;
            _openedAsOutgoingCall = !isIncomingCall;
            _callStartTime        = callStartTime ?? DateTime.Now;
            _originalCallStartTime = _callStartTime;
            _callWindowStartTime   = DateTime.Now;

            AvaloniaWindowChromeHelper.Apply(this, preferAccent: true);
            InitializeUiState();
            WireUpWebRtcEvents();
        }

        // ── Init ──────────────────────────────────────────────────────────────
        private void InitializeUiState()
        {
            var tb = this.FindControl<TextBlock>("CallerNameTextBlock");
            if (tb != null) tb.Text = _phoneNumber;

            if (_isIncomingCall) ShowIncomingCallState();
            else                 ShowOutgoingCallState();
        }

        // ── SIP event wiring ──────────────────────────────────────────────────
        private void WireUpSipEvents()
        {
            if (_sipService == null) return;
            _sipService.OnCallEnded    += OnSipCallEnded;
            _sipService.OnStatusChanged += OnSipStatusChanged;
        }

        private void UnwireSipEvents()
        {
            if (_sipService == null) return;
            _sipService.OnCallEnded    -= OnSipCallEnded;
            _sipService.OnStatusChanged -= OnSipStatusChanged;
        }

        private void OnSipCallEnded()
        {
            UiThread.BeginInvoke(() =>
            {
                StopCallTimer();
                SetCallStatus("Call Ended");
                RaiseCallEnded();
                Close();
            });
        }

        private void OnSipStatusChanged(string status)
        {
            UiThread.BeginInvoke(() =>
            {
                if (status.Contains("Connected", StringComparison.OrdinalIgnoreCase) ||
                    status.Contains("Answered",  StringComparison.OrdinalIgnoreCase))
                {
                    _answerTime  = DateTime.Now;
                    _wasAnswered = true;
                    SetCallStatus("Connected");
                    StartCallTimer();
                    RingbackToneControl.Stop();
                }
                else if (status.Contains("Ringing", StringComparison.OrdinalIgnoreCase))
                {
                    _ringbackStartTime = DateTime.Now;
                    SetCallStatus("Ringing…");
                }
                else
                {
                    SetCallStatus(status);
                }
            });
        }

        // ── WebRTC event wiring ───────────────────────────────────────────────
        private void WireUpWebRtcEvents()
        {
            if (_webRtcService == null) return;
            _webRtcService.Event += OnWebRtcEvent;
        }

        private void UnwireWebRtcEvents()
        {
            if (_webRtcService == null) return;
            _webRtcService.Event -= OnWebRtcEvent;
        }

        private void OnWebRtcEvent(WebRtcEventDto dto)
        {
            UiThread.BeginInvoke(() =>
            {
                switch (dto.Type)
                {
                    case "call_connected":
                    case "ice_connected":
                        if (!_wasAnswered)
                        {
                            _answerTime  = DateTime.Now;
                            _wasAnswered = true;
                            SetCallStatus("Connected");
                            StartCallTimer();
                            RingbackToneControl.Stop();
                        }
                        break;
                    case "call_ended":
                    case "hangup":
                        StopCallTimer();
                        SetCallStatus("Call Ended");
                        RaiseCallEnded();
                        Close();
                        break;
                    case "ringing":
                        _ringbackStartTime = DateTime.Now;
                        SetCallStatus("Ringing…");
                        break;
                }
            });
        }

        // ── UI state methods ──────────────────────────────────────────────────
        private void ShowOutgoingCallState()
        {
            SetCallStatus("Calling…");
            SetControlVisible("HangupButton",            true);
            SetControlVisible("IncomingCallButtonsPanel", false);
            SetControlVisible("ControlButtonsPanel",      false);
            StartConnectingTimeout();
        }

        private void ShowIncomingCallState()
        {
            SetCallStatus("Incoming Call");
            SetControlVisible("HangupButton",            false);
            SetControlVisible("IncomingCallButtonsPanel", true);
            SetControlVisible("ControlButtonsPanel",      false);
        }

        private void ShowActiveCallState()
        {
            SetCallStatus("Connected");
            SetControlVisible("HangupButton",            true);
            SetControlVisible("IncomingCallButtonsPanel", false);
            SetControlVisible("ControlButtonsPanel",      true);
            StartCallTimer();
        }

        private void SetCallStatus(string status)
        {
            var tb = this.FindControl<TextBlock>("CallStatusTextBlock");
            if (tb != null) tb.Text = status;
        }

        private void SetControlVisible(string name, bool visible)
        {
            var ctrl = this.FindControl<Control>(name);
            if (ctrl != null) ctrl.IsVisible = visible;
        }

        // ── Call timer ────────────────────────────────────────────────────────
        private void StartCallTimer()
        {
            _callTimer?.Stop();
            _callTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _callTimer.Tick += (_, _) =>
            {
                var elapsed = DateTime.Now - (_answerTime ?? _callStartTime);
                var tb = this.FindControl<TextBlock>("CallTimerTextBlock");
                if (tb != null) tb.Text = elapsed.ToString(@"hh\:mm\:ss");
            };
            _callTimer.Start();
        }

        private void StopCallTimer() { _callTimer?.Stop(); _callTimer = null; }

        // ── Connecting timeout ────────────────────────────────────────────────
        private void StartConnectingTimeout()
        {
            _connectingTimeoutCts?.Cancel();
            _connectingTimeoutCts = new CancellationTokenSource();
            var token = _connectingTimeoutCts.Token;
            var delay = _useWebRtc ? OriginateConnectingTimeoutMs : StandardConnectingTimeoutMs;
            Task.Delay(delay, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                UiThread.BeginInvoke(() =>
                {
                    if (!_wasAnswered) { SetCallStatus("No Answer"); RaiseCallEnded(); Close(); }
                });
            }, TaskScheduler.Default);
        }

        // ── Call-end event ────────────────────────────────────────────────────
        private void RaiseCallEnded()
        {
            _connectingTimeoutCts?.Cancel();
            var duration = _answerTime.HasValue ? (TimeSpan?)(DateTime.Now - _answerTime.Value) : null;
            OnCallDetailsChanged?.Invoke(
                _phoneNumber, _originalCallStartTime,
                _ringbackStartTime, _ringbackEndTime, _answerTime,
                _wasAnswered, _endedBy, null, duration, null,
                _useWebRtc ? CallTransport.WebRtc : CallTransport.Sip,
                null, null);
        }

        // ── Keypad ────────────────────────────────────────────────────────────
        private void ShowKeypad()
        {
            var grid = this.FindControl<Grid>("KeypadGrid");
            if (grid == null) return;
            grid.IsVisible   = true;
            grid.Opacity     = 1.0;
            _isKeypadVisible = true;
        }

        private void HideKeypad()
        {
            var grid = this.FindControl<Grid>("KeypadGrid");
            if (grid == null) return;
            grid.IsVisible   = false;
            grid.Opacity     = 0.0;
            _isKeypadVisible = false;
        }

        // ── Button handlers ───────────────────────────────────────────────────
        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void CloseButton_Click(object? sender, RoutedEventArgs e)
        {
            _endedBy = CallEndedBy.LocalUser;
            HangUp();
        }

        private void HangupButton_Click(object? sender, RoutedEventArgs e)
        {
            _endedBy = CallEndedBy.LocalUser;
            HangUp();
        }

        private void AnswerButton_Click(object? sender, RoutedEventArgs e)
        {
            RingtoneControl.Stop();
            _wasAnswered = true;
            _answerTime  = DateTime.Now;
            ShowActiveCallState();
            // TODO: SipService does not expose AnswerCall() in Core yet — Phase 6
        }

        private void RejectButton_Click(object? sender, RoutedEventArgs e)
        {
            _endedBy = CallEndedBy.LocalUser;
            RingtoneControl.Stop();
            HangUp();
        }

        private void MuteButton_Click(object? sender, RoutedEventArgs e)
        {
            _isMuted = !_isMuted;
            MicrophoneControl.SetMute(_isMuted);
            this.FindControl<Control>("MuteIconOn")!.IsVisible  = !_isMuted;
            this.FindControl<Control>("MuteIconOff")!.IsVisible = _isMuted;
        }

        private void HoldButton_Click(object? sender, RoutedEventArgs e)
        {
            _isOnHold = !_isOnHold;
            // TODO: hold/resume via SipService (Phase 6)
            this.FindControl<Control>("HoldIconPause")!.IsVisible = !_isOnHold;
            this.FindControl<Control>("HoldIconPlay")!.IsVisible  = _isOnHold;
        }

        private void SpeakerButton_Click(object? sender, RoutedEventArgs e)
            => AppLog.Log("Speaker toggle (not yet implemented for Avalonia)");

        private void KeypadToggleButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_isKeypadVisible) HideKeypad();
            else                  ShowKeypad();
        }

        private void DtmfButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string dtmfStr && dtmfStr.Length > 0)
                _sipService?.SendDTMF(dtmfStr[0]);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape) { e.Handled = true; _endedBy = CallEndedBy.LocalUser; HangUp(); }
        }

        // ── Hang-up ───────────────────────────────────────────────────────────
        private void HangUp()
        {
            StopCallTimer();
            UnwireSipEvents();
            UnwireWebRtcEvents();
            RingbackToneControl.Stop();
            RingtoneControl.Stop();
            _sipService?.Hangup();
            // WebRTC hangup via Event-driven flow — engine notifies on hangup
            RaiseCallEnded();
            Close();
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            StopCallTimer();
            _connectingTimeoutCts?.Cancel();
            UnwireSipEvents();
            UnwireWebRtcEvents();
        }
    }
}
