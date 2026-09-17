using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Softphone.Audio;

namespace Softphone.AppHost.ViewModels
{
    /// <summary>
    /// UI-agnostic state machine for one call (SIP or WebRTC), ported from the WPF <c>CallWindow</c>.
    /// The view binds to the observable properties and calls the public verbs
    /// (<see cref="AnswerAsync"/>, <see cref="HangupAsync"/>, <see cref="ToggleMuteAsync"/>, …).
    /// The view model reports completion through <see cref="Completed"/> and asks the host to close
    /// through <see cref="CloseRequested"/>. All property changes are marshalled to the UI thread.
    /// </summary>
    public sealed class CallViewModel : ObservableObject, IDisposable
    {
        private const int StandardConnectingTimeoutMs = 25000;
        private const int OriginateConnectingTimeoutMs = 90000;
        private const int WebRtcMediaConnectWatchdogMs = 8000;
        private const int CloseDelayMs = 800;

        private readonly DesktopAppController _controller;
        private readonly CallWindowRequest _request;
        private readonly SipService? _sip;
        private readonly WebRtcService? _webRtc;
        private readonly bool _useWebRtc;
        private readonly string _logPrefix;
        private readonly List<string> _technicalDetails = new();
        private readonly object _stateLock = new();

        private ITonePlayer? _ringback;
        private WebRtcRecordingSession? _recording;
        private Timer? _tickTimer;
        private CancellationTokenSource? _connectingTimeoutCts;
        private CancellationTokenSource? _mediaWatchdogCts;

        // Call facts
        private DateTime _connectedAt;
        private DateTime? _ringbackStart;
        private DateTime? _ringbackEnd;
        private DateTime? _answerTime;
        private bool _wasAnswered;
        private bool _isIncoming;
        private CallEndedBy _endedBy = CallEndedBy.Unknown;
        private string? _webRtcSessionId;
        private string? _sipCallId;
        private string? _outboundCallerId;
        private string? _recordingPath;
        private bool _completed;
        private bool _closing;
        private bool _webRtcAcceptedPending;
        private bool _webRtcIceConnected;
        private bool _webRtcRemoteAudioStarted;
        private bool _webRtcTerminationHandled;
        private bool _isOriginate;

        // Bindable UI state
        private string _callerDisplay = "";
        private string _statusText = "";
        private string _timerText = "";
        private CallUiTone _statusTone = CallUiTone.Neutral;
        private bool _showIncomingButtons;
        private bool _showHangupButton = true;
        private bool _showControls;
        private bool _isMuted;
        private bool _isOnHold;
        private bool _isKeypadVisible;
        private bool _isRecordingIndicatorVisible;

        public event Action<CallEndedReport>? Completed;
        public event Action? CloseRequested;

        public CallViewModel(DesktopAppController controller, CallWindowRequest request)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _sip = request.Sip;
            _webRtc = request.WebRtc;
            _useWebRtc = request.Transport == CallTransport.WebRtc;
            _isIncoming = request.IsIncoming;
            _isOriginate = request.IsOriginateCall;
            _webRtcSessionId = request.IncomingSessionId;
            _logPrefix = _useWebRtc ? "[WebRTC Call]" : "[SIP Call]";

            PhoneNumber = request.PhoneNumber;
            CallStartTime = request.CallStartTime;
            _callerDisplay = request.PhoneNumber;
            _outboundCallerId = request.Slot == ConnectionSlot.Main ? controller.ViewModel.SelectedCallerId?.Number : null;

            if (_isIncoming)
            {
                _statusText = "Incoming call";
                _statusTone = CallUiTone.Good;
                _showIncomingButtons = true;
                _showHangupButton = false;
                _showControls = false;
            }
            else
            {
                _statusText = _isOriginate ? "Connecting via PBX..." : "Calling...";
                _statusTone = CallUiTone.Good;
                _showIncomingButtons = false;
                _showHangupButton = true;
                _showControls = false;
            }

            AddDetail(_isIncoming
                ? $"Incoming {(_useWebRtc ? "WebRTC" : "SIP")} call from {request.PhoneNumber}"
                : $"Initiating {(_useWebRtc ? "WebRTC" : "SIP")} call to {request.PhoneNumber}");
        }

        // ───────────────────────── bindable ─────────────────────────

        public string PhoneNumber { get; }
        public DateTime CallStartTime { get; }
        public bool IsWebRtc => _useWebRtc;
        public bool IsIncoming => _isIncoming;
        public string TransportLabel => _useWebRtc ? "WebRTC" : "SIP";
        public string ConnectionLabel => _request.Slot == ConnectionSlot.Main
            ? _controller.ViewModel.Main.DisplayName
            : _controller.ViewModel.Secondary.DisplayName;
        public string WindowTitle => _isIncoming ? "Incoming call" : "Active call";

        public string CallerDisplay { get => _callerDisplay; private set => Set(ref _callerDisplay, value); }
        public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
        public string TimerText { get => _timerText; private set => Set(ref _timerText, value); }
        public CallUiTone StatusTone
        {
            get => _statusTone;
            private set { if (Set(ref _statusTone, value)) { OnPropertyChanged(nameof(IsStatusGood)); OnPropertyChanged(nameof(IsStatusBad)); } }
        }
        public bool IsStatusGood => _statusTone == CallUiTone.Good;
        public bool IsStatusBad => _statusTone == CallUiTone.Bad;

        public bool ShowIncomingButtons { get => _showIncomingButtons; private set => Set(ref _showIncomingButtons, value); }
        public bool ShowHangupButton { get => _showHangupButton; private set => Set(ref _showHangupButton, value); }
        public bool ShowControls { get => _showControls; private set => Set(ref _showControls, value); }
        public bool IsMuted { get => _isMuted; private set { if (Set(ref _isMuted, value)) OnPropertyChanged(nameof(IsNotMuted)); } }
        public bool IsNotMuted => !_isMuted;
        public bool IsOnHold { get => _isOnHold; private set { if (Set(ref _isOnHold, value)) OnPropertyChanged(nameof(IsNotOnHold)); } }
        public bool IsNotOnHold => !_isOnHold;
        public bool IsKeypadVisible { get => _isKeypadVisible; set => Set(ref _isKeypadVisible, value); }
        public bool IsRecordingIndicatorVisible { get => _isRecordingIndicatorVisible; private set => Set(ref _isRecordingIndicatorVisible, value); }
        public bool WasAnswered => _wasAnswered;

        // ───────────────────────── lifecycle ─────────────────────────

        /// <summary>Attach to service events and, for outgoing calls, dial.</summary>
        public async Task StartAsync()
        {
            if (_useWebRtc) HookWebRtc(); else HookSip();

            if (_isIncoming)
            {
                PlatformRingtone.Configure(_controller.Settings);
                PlatformRingtone.Start();
                return;
            }

            StartConnectingTimeout(_isOriginate ? OriginateConnectingTimeoutMs : StandardConnectingTimeoutMs);

            try
            {
                if (_useWebRtc)
                {
                    if (_isOriginate) await StartOriginateAsync().ConfigureAwait(false);
                    else await StartWebRtcCallAsync().ConfigureAwait(false);
                }
                else
                {
                    if (_sip == null) throw new InvalidOperationException("SIP service is not available");
                    await _sip.CallAsync(PhoneNumber).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log($"dial failed: {ex.Message}");
                AddDetail($"ERROR: {ex.Message}");
                FailAndClose("Call failed", CallEndedBy.Unknown);
            }
        }

        private async Task StartWebRtcCallAsync()
        {
            if (_webRtc == null) throw new InvalidOperationException("WebRTC service is not available");
            if (!_webRtc.IsReadyForCalls) throw new InvalidOperationException("WebRTC service not ready");

            // MikoPBX WebRTC endpoints are addressed as <number>-WS.
            string target = PhoneNumber.EndsWith("-WS", StringComparison.OrdinalIgnoreCase) ? PhoneNumber : $"{PhoneNumber}-WS";
            AddDetail($"Calling target {target} (MikoPBX WebRTC endpoint)");
            await _webRtc.MakeCallAsync(target).ConfigureAwait(false);
        }

        private async Task StartOriginateAsync()
        {
            if (_webRtc == null) throw new InvalidOperationException("WebRTC service is not available");
            AddDetail("PBX originate requested");
            _ = _webRtc.NotifyOriginatePendingAsync(true);

            var result = await _controller.BeginOriginateAsync(PhoneNumber, sessionId =>
            {
                _webRtcSessionId = sessionId;
                AddDetail($"Originate callback attached (SessionId: {sessionId})");
                Ui(() => SetStatus("Calling...", CallUiTone.Good));
            }).ConfigureAwait(false);

            if (result == null || !result.Success)
            {
                _ = _webRtc.NotifyOriginatePendingAsync(false);
                throw new InvalidOperationException(result?.Error ?? "PBX originate failed");
            }
            AddDetail("Originate accepted by PBX, waiting for callback");
        }

        // ───────────────────────── verbs ─────────────────────────

        public async Task AnswerAsync()
        {
            if (!_isIncoming || _wasAnswered) return;
            PlatformRingtone.Stop();
            AddDetail("Answering");
            try
            {
                if (_useWebRtc)
                {
                    if (_webRtc == null) return;
                    await _webRtc.AnswerAsync(_webRtcSessionId).ConfigureAwait(false);
                }
                else
                {
                    if (_sip == null) return;
                    bool ok = await _sip.AnswerIncomingCallAsync().ConfigureAwait(false);
                    if (!ok) { FailAndClose("Answer failed", CallEndedBy.Unknown); return; }
                    // SIP: "Call connected"/"Call answered" status confirms; be optimistic for the UI.
                    Ui(() => MarkConnected());
                }
            }
            catch (Exception ex)
            {
                Log($"answer failed: {ex.Message}");
                FailAndClose("Answer failed", CallEndedBy.Unknown);
            }
        }

        public async Task RejectAsync()
        {
            if (!_isIncoming || _wasAnswered) { await HangupAsync().ConfigureAwait(false); return; }
            _endedBy = CallEndedBy.LocalUser;
            PlatformRingtone.Stop();
            AddDetail("Rejected by user");
            try
            {
                if (_useWebRtc) { if (_webRtc != null) await _webRtc.HangupAsync(_webRtcSessionId).ConfigureAwait(false); }
                else if (_sip != null) await _sip.RejectIncomingCallAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { Log($"reject failed: {ex.Message}"); }
            FinishAndClose("Call rejected", CallUiTone.Neutral, immediate: true);
        }

        public async Task HangupAsync()
        {
            if (_endedBy == CallEndedBy.Unknown) _endedBy = CallEndedBy.LocalUser;
            AddDetail("Hangup by user");
            StopRingback();
            PlatformRingtone.Stop();
            try
            {
                if (_useWebRtc)
                {
                    if (_isOriginate && string.IsNullOrEmpty(_webRtcSessionId))
                    {
                        // Callback never arrived — nothing to hang up on the WebRTC side.
                        _controller.ClearOriginate();
                        if (_webRtc != null) _ = _webRtc.NotifyOriginatePendingAsync(false);
                    }
                    else if (_webRtc != null)
                    {
                        await _webRtc.HangupAsync(_webRtcSessionId).ConfigureAwait(false);
                    }
                }
                else
                {
                    _sip?.Hangup();
                }
            }
            catch (Exception ex) { Log($"hangup failed: {ex.Message}"); }

            // SIP raises OnCallEnded; WebRTC raises call_ended. Both close the window.
            // Fallback: if the service never confirms, close anyway.
            _ = Task.Delay(3000).ContinueWith(_ => { if (!_completed) FinishAndClose("Call ended", CallUiTone.Neutral, immediate: true); }, TaskScheduler.Default);
        }

        public async Task ToggleMuteAsync()
        {
            bool mute = !_isMuted;
            try
            {
                if (_useWebRtc) { if (_webRtc != null) await _webRtc.SetMuteAsync(mute).ConfigureAwait(false); }
                else _sip?.SetMute(mute);
                Ui(() => IsMuted = mute);
                AddDetail(mute ? "Microphone muted" : "Microphone unmuted");
            }
            catch (Exception ex) { Log($"mute failed: {ex.Message}"); }
        }

        public async Task ToggleHoldAsync()
        {
            if (!_wasAnswered) return;
            bool hold = !_isOnHold;
            try
            {
                if (_useWebRtc) { if (_webRtc != null) await _webRtc.SetHoldAsync(hold, _webRtcSessionId).ConfigureAwait(false); }
                else if (_sip != null) await _sip.HoldCallAsync(hold).ConfigureAwait(false);
                Ui(() =>
                {
                    IsOnHold = hold;
                    SetStatus(hold ? "On hold" : "Connected", CallUiTone.Good);
                });
                AddDetail(hold ? "Call on hold" : "Call resumed");
            }
            catch (Exception ex) { Log($"hold failed: {ex.Message}"); }
        }

        public async Task SendDtmfAsync(char digit)
        {
            try
            {
                if (_useWebRtc) { if (_webRtc != null) await _webRtc.SendDTMFAsync(digit).ConfigureAwait(false); }
                else _sip?.SendDTMF(digit);
                AddDetail($"DTMF {digit}");
            }
            catch (Exception ex) { Log($"DTMF failed: {ex.Message}"); }
        }

        public void ToggleKeypad() => IsKeypadVisible = !IsKeypadVisible;

        /// <summary>Called by the view when the window is closed by the OS / user without pressing hang up.</summary>
        public void OnViewClosed()
        {
            if (!_completed)
            {
                if (_endedBy == CallEndedBy.Unknown) _endedBy = CallEndedBy.LocalUser;
                _ = HangupAsync();
                Complete();
            }
            Dispose();
        }

        // ───────────────────────── SIP events ─────────────────────────

        private void HookSip()
        {
            if (_sip == null) return;
            _sip.OnStatusChanged += OnSipStatus;
            _sip.OnCallEnded += OnSipCallEnded;
            _sip.OnRecordingFinalized += OnSipRecordingFinalized;
            _sip.OnOutboundCallerIdReceived += OnSipOutboundCallerId;
        }

        private void UnhookSip()
        {
            if (_sip == null) return;
            _sip.OnStatusChanged -= OnSipStatus;
            _sip.OnCallEnded -= OnSipCallEnded;
            _sip.OnRecordingFinalized -= OnSipRecordingFinalized;
            _sip.OnOutboundCallerIdReceived -= OnSipOutboundCallerId;
        }

        private void OnSipOutboundCallerId(string callerId)
        {
            if (!string.IsNullOrWhiteSpace(callerId)) _outboundCallerId = callerId;
        }

        private void OnSipRecordingFinalized(string path)
        {
            if (!string.IsNullOrEmpty(path)) _recordingPath = path;
        }

        private void OnSipStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return;
            if (IsCallRelated(status)) AddDetail(status);

            Ui(() =>
            {
                if (_closing) return;

                if (status.Contains("100 Trying"))
                {
                    if (!_wasAnswered) SetStatus("Connecting...", CallUiTone.Good);
                }
                else if (status.Contains("180 Ringing") || status.Contains("183 Session Progress"))
                {
                    _ringbackStart ??= DateTime.Now;
                    if (!_wasAnswered) SetStatus("Ringing...", CallUiTone.Good);
                }
                else if (status.Contains("Call connected") || status.Contains("Call answered") || status.Contains("Call progress: 200 OK"))
                {
                    MarkConnected();
                }
                else if (status.Contains("Call ended by remote party"))
                {
                    _endedBy = CallEndedBy.RemoteParty;
                }
                else if (IsSipRemoteFailure(status))
                {
                    _endedBy = CallEndedBy.RemoteParty;
                    if (!_wasAnswered) SetStatus(FriendlySipFailure(status), CallUiTone.Bad);
                }
                else if (status.Contains("Call failed") || status.Contains("No answer (timeout)", StringComparison.OrdinalIgnoreCase)
                         || status.Contains("Call cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_wasAnswered) SetStatus(FriendlySipFailure(status), CallUiTone.Bad);
                }
                else if (status.Contains("Call on hold")) { IsOnHold = true; }
                else if (status.Contains("Call resumed")) { IsOnHold = false; }
            });
        }

        private void OnSipCallEnded()
        {
            Ui(() =>
            {
                _recordingPath ??= _sip?.CurrentRecordingFilePath;
                string text = _wasAnswered ? "Call ended"
                    : _statusTone == CallUiTone.Bad ? _statusText
                    : _isIncoming ? "Missed call"
                    : "Call ended";
                FinishAndClose(text, _statusTone == CallUiTone.Bad ? CallUiTone.Bad : CallUiTone.Neutral);
            });
        }

        private static bool IsSipRemoteFailure(string s) =>
            s.Contains("Busy Here", StringComparison.OrdinalIgnoreCase) || s.Contains("Line busy", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Decline", StringComparison.OrdinalIgnoreCase) || s.Contains("Call declined", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Temporarily Unavailable", StringComparison.OrdinalIgnoreCase) || s.Contains("Subscriber unavailable", StringComparison.OrdinalIgnoreCase)
            || s.Contains("cause=480", StringComparison.OrdinalIgnoreCase);

        private static string FriendlySipFailure(string s)
        {
            if (s.Contains("Line busy", StringComparison.OrdinalIgnoreCase) || s.Contains("Busy Here", StringComparison.OrdinalIgnoreCase)) return "Line busy";
            if (s.Contains("declined", StringComparison.OrdinalIgnoreCase) || s.Contains("Decline", StringComparison.OrdinalIgnoreCase)) return "Call declined";
            if (s.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || s.Contains("cause=480", StringComparison.OrdinalIgnoreCase)) return "Subscriber unavailable";
            if (s.Contains("Call cancelled", StringComparison.OrdinalIgnoreCase)) return "Call cancelled";
            if (s.Contains("Number not found", StringComparison.OrdinalIgnoreCase) || s.Contains("404")) return "Number not found";
            if (s.Contains("No answer", StringComparison.OrdinalIgnoreCase)) return "No answer";
            if (s.Contains("Cannot reach SIP server", StringComparison.OrdinalIgnoreCase) || s.Contains("unreachable", StringComparison.OrdinalIgnoreCase)) return "Call failed: server unreachable";
            if (s.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return "Call failed: timeout";
            return "Call failed";
        }

        private static bool IsCallRelated(string s) =>
            s.Contains("Call") || s.Contains("INVITE") || s.Contains("SIP Response") || s.Contains("Ringing") || s.Contains("Trying")
            || s.Contains("Session Progress") || s.Contains("200 OK") || s.Contains("BYE") || s.Contains("CANCEL") || s.Contains("Busy")
            || s.Contains("Failed") || s.Contains("Hanging up");

        // ───────────────────────── WebRTC events ─────────────────────────

        private void HookWebRtc()
        {
            if (_webRtc == null) return;
            _webRtc.Event += OnWebRtcEvent;
        }

        private void UnhookWebRtc()
        {
            if (_webRtc == null) return;
            _webRtc.Event -= OnWebRtcEvent;
        }

        private void OnWebRtcEvent(WebRtcEventDto dto)
        {
            if (dto == null) return;

            if (WebRtcRecordingSession.IsRecordingEvent(dto.Type))
            {
                _recording?.HandleEvent(dto);
                return;
            }

            // Ignore events for other sessions once we are bound to one.
            if (!string.IsNullOrEmpty(_webRtcSessionId) && !string.IsNullOrEmpty(dto.SessionId)
                && dto.SessionId != _webRtcSessionId
                && dto.Type is not ("error" or "ua_disconnected" or "ua_registration_failed"))
                return;

            Ui(() => HandleWebRtcUiEvent(dto));
        }

        private void HandleWebRtcUiEvent(WebRtcEventDto dto)
        {
            if (_closing && dto.Type is not ("call_ended" or "call_failed")) return;

            switch (dto.Type)
            {
                case "makeCall_started":
                    CancelConnectingTimeout();
                    AddDetail($"Making call (WebRTC){(dto.SessionId != null ? $" (SessionId: {dto.SessionId})" : "")}");
                    if (!_wasAnswered) SetStatus("Calling...", CallUiTone.Good);
                    StartConnectingTimeout(30000);
                    break;

                case "new_session":
                    CancelConnectingTimeout();
                    if (!string.IsNullOrEmpty(dto.SessionId)) _webRtcSessionId = dto.SessionId;
                    AddDetail($"New WebRTC session{(dto.SessionId != null ? $" (SessionId: {dto.SessionId})" : "")}");
                    break;

                case "call_progress":
                    AddDetail("Call in progress (WebRTC)");
                    if (!_wasAnswered) SetStatus("Calling...", CallUiTone.Good);
                    if (_isOriginate && !_wasAnswered)
                    {
                        _ringbackStart ??= DateTime.Now;
                        StartRingback();
                    }
                    break;

                case "ringing":
                    if (_wasAnswered) { StopRingback(); break; }
                    _ringbackStart ??= DateTime.Now;
                    StartRingback();
                    AddDetail("Remote party ringing (WebRTC)");
                    SetStatus("Ringing...", CallUiTone.Good);
                    break;

                case "call_accepted":
                case "call_confirmed":
                    CancelConnectingTimeout();
                    AddDetail($"Call {dto.Type.Replace("call_", "")} (WebRTC)");
                    _webRtcAcceptedPending = true;
                    if (_webRtcRemoteAudioStarted || _webRtcIceConnected || _isIncoming)
                    {
                        FinalizeWebRtcConnected();
                    }
                    else
                    {
                        // Keep ringback until media is actually flowing (ICE connected / remote audio).
                        SetStatus("Connecting media...", CallUiTone.Good);
                        StartMediaWatchdog();
                    }
                    break;

                case "ice_connection_state_change":
                    {
                        string? state = dto.Message ?? TryGetString(dto.Data, "state");
                        if (state is "connected" or "completed")
                        {
                            _webRtcIceConnected = true;
                            AddDetail($"ICE {state}");
                            if (_webRtcAcceptedPending && !_wasAnswered) FinalizeWebRtcConnected();
                        }
                        else if (state is "failed")
                        {
                            AddDetail("ICE failed");
                        }
                        break;
                    }

                case "remote_audio_started":
                case "audio_connected":
                case "audio_playing":
                    _webRtcRemoteAudioStarted = true;
                    if (_webRtcAcceptedPending && !_wasAnswered) FinalizeWebRtcConnected();
                    break;

                case "remote_party_answered":
                    AddDetail("Remote party answered");
                    if (_webRtcAcceptedPending && !_wasAnswered) FinalizeWebRtcConnected();
                    break;

                case "call_hold":
                    IsOnHold = true;
                    SetStatus("On hold", CallUiTone.Good);
                    break;

                case "call_unhold":
                    IsOnHold = false;
                    SetStatus("Connected", CallUiTone.Good);
                    break;

                case "recording_started":
                    IsRecordingIndicatorVisible = true;
                    break;

                case "recording_stopped":
                    IsRecordingIndicatorVisible = false;
                    break;

                case "call_failed":
                    if (_webRtcTerminationHandled) break;
                    _webRtcTerminationHandled = true;
                    CancelConnectingTimeout();
                    StopMediaWatchdog();
                    StopRingback();
                    _recording?.Stop();
                    if (_ringbackStart.HasValue && !_ringbackEnd.HasValue) _ringbackEnd = DateTime.Now;
                    AddDetail($"Call failed (WebRTC){(dto.Cause != null ? $" (Cause: {dto.Cause})" : "")}{(dto.Message != null ? $" ({dto.Message})" : "")}");
                    _endedBy = DetectEndedBy(dto) ?? (_wasAnswered ? CallEndedBy.RemoteParty : _endedBy);
                    if (_isOriginate) { _controller.ClearOriginate(); if (_webRtc != null) _ = _webRtc.NotifyOriginatePendingAsync(false); }
                    FinishAndClose(_wasAnswered ? "Call ended" : FriendlyWebRtcFailure(dto), _wasAnswered ? CallUiTone.Neutral : CallUiTone.Bad);
                    break;

                case "call_ended":
                    if (_webRtcTerminationHandled) break;
                    _webRtcTerminationHandled = true;
                    CancelConnectingTimeout();
                    StopMediaWatchdog();
                    StopRingback();
                    IsOnHold = false;
                    _recording?.Stop();
                    if (_ringbackStart.HasValue && !_ringbackEnd.HasValue) _ringbackEnd = DateTime.Now;
                    if (_endedBy == CallEndedBy.Unknown)
                        _endedBy = DetectEndedBy(dto) ?? (_wasAnswered ? CallEndedBy.RemoteParty : CallEndedBy.LocalUser);
                    AddDetail($"Call ended (WebRTC){(dto.Cause != null ? $" (Cause: {dto.Cause})" : "")}");
                    if (_isOriginate) { _controller.ClearOriginate(); if (_webRtc != null) _ = _webRtc.NotifyOriginatePendingAsync(false); }
                    FinishAndClose(_wasAnswered ? "Call ended" : (_isIncoming ? "Missed call" : "Call ended"), CallUiTone.Neutral);
                    break;

                case "error":
                    AddDetail($"WebRTC error: {dto.Name ?? ""} {dto.Message ?? ""} (phase={dto.Phase ?? "?"})");
                    if (!_wasAnswered && dto.Phase is "getUserMedia" or "ua.call")
                    {
                        _endedBy = CallEndedBy.Unknown;
                        FinishAndClose(dto.Name == "NotAllowedError" ? "Microphone access denied" : "Call failed", CallUiTone.Bad);
                    }
                    break;
            }
        }

        private void FinalizeWebRtcConnected()
        {
            _webRtcAcceptedPending = false;
            StopMediaWatchdog();
            StopRingback();
            MarkConnected();
            if (_isOriginate) _controller.ClearOriginate();

            if (IsCallRecordingEnabled() && _webRtc != null)
            {
                _recording ??= new WebRtcRecordingSession(_webRtc, _logPrefix + " recording");
                _recording.RecordingFinalized += p => _recordingPath = p;
                _recording.Start(PhoneNumber, CallStartTime);
                _recordingPath = _recording.RecordingFilePath;
            }
        }

        private static CallEndedBy? DetectEndedBy(WebRtcEventDto dto)
        {
            string? originator = TryGetString(dto.Data, "originator");
            if (string.IsNullOrEmpty(originator)) return null;
            return originator is "local" or "LocalUser" ? CallEndedBy.LocalUser : CallEndedBy.RemoteParty;
        }

        private static string FriendlyWebRtcFailure(WebRtcEventDto dto)
        {
            string cause = dto.Cause ?? "";
            if (cause.Contains("Busy", StringComparison.OrdinalIgnoreCase)) return "Line busy";
            if (cause.Contains("Rejected", StringComparison.OrdinalIgnoreCase)) return "Call declined";
            if (cause.Contains("Unavailable", StringComparison.OrdinalIgnoreCase)) return "Subscriber unavailable";
            if (cause.Contains("Not Found", StringComparison.OrdinalIgnoreCase)) return "Number not found";
            if (cause.Contains("Canceled", StringComparison.OrdinalIgnoreCase)) return "Call cancelled";
            if (cause.Contains("Timeout", StringComparison.OrdinalIgnoreCase) || cause.Contains("No Answer", StringComparison.OrdinalIgnoreCase)) return "No answer";
            if (cause.Contains("User Denied Media", StringComparison.OrdinalIgnoreCase)) return "Microphone access denied";
            return string.IsNullOrEmpty(cause) ? "Call failed" : $"Call failed: {cause}";
        }

        private static string? TryGetString(JsonElement? data, string name)
        {
            try
            {
                if (data is { ValueKind: JsonValueKind.Object } el && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            catch { }
            return null;
        }

        // ───────────────────────── state transitions ─────────────────────────

        private void MarkConnected()
        {
            if (_wasAnswered) return;
            CancelConnectingTimeout();
            StopRingback();
            PlatformRingtone.Stop();
            if (_ringbackStart.HasValue && !_ringbackEnd.HasValue) _ringbackEnd = DateTime.Now;
            _answerTime = DateTime.Now;
            _wasAnswered = true;
            _connectedAt = DateTime.Now;
            if (_isIncoming) { _isIncoming = false; OnPropertyChanged(nameof(IsIncoming)); }

            ShowIncomingButtons = false;
            ShowHangupButton = true;
            ShowControls = true;
            SetStatus("Connected", CallUiTone.Good);
            TimerText = "00:00:00";
            StartTickTimer();
            AddDetail("Connected");

            _controller.OnCallStatusChanged(PhoneNumber, CallStartTime, CallStatus.Connected);
        }

        private void FailAndClose(string text, CallEndedBy endedBy)
        {
            if (_endedBy == CallEndedBy.Unknown) _endedBy = endedBy;
            Ui(() => FinishAndClose(text, CallUiTone.Bad));
        }

        private void FinishAndClose(string text, CallUiTone tone, bool immediate = false)
        {
            if (_closing) return;
            _closing = true;
            StopTickTimer();
            CancelConnectingTimeout();
            StopMediaWatchdog();
            StopRingback();
            PlatformRingtone.Stop();
            SetStatus(text, tone);
            ShowIncomingButtons = false;
            ShowControls = false;

            Complete();

            if (immediate) { try { CloseRequested?.Invoke(); } catch { } }
            else _ = Task.Delay(CloseDelayMs).ContinueWith(_ => Ui(() => { try { CloseRequested?.Invoke(); } catch { } }), TaskScheduler.Default);
        }

        private void Complete()
        {
            lock (_stateLock)
            {
                if (_completed) return;
                _completed = true;
            }

            if (_ringbackStart.HasValue && !_ringbackEnd.HasValue) _ringbackEnd = DateTime.Now;
            TimeSpan? duration = _wasAnswered && _answerTime.HasValue ? DateTime.Now - _answerTime.Value : null;

            // SipService does not expose the SIP Call-ID; _sipCallId stays null (matches WPF behaviour).
            _recordingPath ??= _useWebRtc ? _recording?.RecordingFilePath : _sip?.CurrentRecordingFilePath;

            var report = new CallEndedReport
            {
                PhoneNumber = PhoneNumber,
                CallTime = CallStartTime,
                RingbackStart = _ringbackStart,
                RingbackEnd = _ringbackEnd,
                AnswerTime = _answerTime,
                WasAnswered = _wasAnswered,
                EndedBy = _endedBy == CallEndedBy.Unknown && _wasAnswered ? CallEndedBy.RemoteParty : _endedBy,
                TechnicalDetails = new List<string>(_technicalDetails),
                Duration = duration,
                RecordingFilePath = _recordingPath,
                Transport = _useWebRtc ? CallTransport.WebRtc : CallTransport.Sip,
                SipCallId = _sipCallId,
                WebRtcSessionId = _webRtcSessionId,
                OutboundCallerId = _outboundCallerId,
                Slot = _request.Slot,
                IsIncoming = _request.IsIncoming,
                LeadId = _request.AmoCrmLeadId,
                BrowserLeadId = _request.AmoCrmBrowserLeadId,
            };

            try { Completed?.Invoke(report); } catch (Exception ex) { Log($"Completed handler error: {ex.Message}"); }

            // If a late recording_complete lands after the report, patch the history entry.
            if (_recording != null)
            {
                _recording.RecordingFinalized += path =>
                {
                    try
                    {
                        _controller.History.UpdateCallDetails(
                            report.PhoneNumber, report.CallTime, report.RingbackStart, report.RingbackEnd, report.AnswerTime,
                            report.WasAnswered, report.EndedBy, report.TechnicalDetails, report.Duration, path,
                            report.Transport, report.SipCallId, report.WebRtcSessionId, report.OutboundCallerId,
                            report.Slot == ConnectionSlot.Main ? CallConnectionSlot.Main : CallConnectionSlot.Secondary, null);
                        _controller.RefreshHistory();
                    }
                    catch (Exception ex) { Log($"late recording update failed: {ex.Message}"); }
                };
            }
        }

        private void SetStatus(string text, CallUiTone tone)
        {
            StatusText = text;
            StatusTone = tone;
        }

        // ───────────────────────── timers & tones ─────────────────────────

        private void StartTickTimer()
        {
            StopTickTimer();
            _tickTimer = new Timer(_ =>
            {
                var elapsed = DateTime.Now - _connectedAt;
                Ui(() => TimerText = elapsed.ToString(@"hh\:mm\:ss"));
            }, null, 1000, 1000);
        }

        private void StopTickTimer()
        {
            try { _tickTimer?.Dispose(); } catch { }
            _tickTimer = null;
        }

        private void StartConnectingTimeout(int ms)
        {
            CancelConnectingTimeout();
            var cts = new CancellationTokenSource();
            _connectingTimeoutCts = cts;
            _ = Task.Delay(ms, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled || _wasAnswered || _closing) return;
                Log($"connecting timeout after {ms} ms");
                AddDetail("Timeout: no answer");
                _ = HangupAsync();
                Ui(() => FinishAndClose("No answer", CallUiTone.Bad));
            }, TaskScheduler.Default);
        }

        private void CancelConnectingTimeout()
        {
            try { _connectingTimeoutCts?.Cancel(); } catch { }
            _connectingTimeoutCts = null;
        }

        private void StartMediaWatchdog()
        {
            StopMediaWatchdog();
            var cts = new CancellationTokenSource();
            _mediaWatchdogCts = cts;
            _ = Task.Delay(WebRtcMediaConnectWatchdogMs, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled || _wasAnswered || _closing) return;
                Log("media watchdog: finalizing connected state without ICE confirmation");
                Ui(FinalizeWebRtcConnected);
            }, TaskScheduler.Default);
        }

        private void StopMediaWatchdog()
        {
            try { _mediaWatchdogCts?.Cancel(); } catch { }
            _mediaWatchdogCts = null;
        }

        private void StartRingback()
        {
            try
            {
                _ringback ??= TonePlayerFactory.Create?.Invoke();
                _ringback?.PlayRingbackTone();
            }
            catch (Exception ex) { Log($"ringback start failed: {ex.Message}"); }
        }

        private void StopRingback()
        {
            try { _ringback?.Stop(); } catch { }
        }

        private bool IsCallRecordingEnabled()
        {
            try { return _controller.Settings.EnableCallRecording; } catch { return false; }
        }

        // ───────────────────────── util ─────────────────────────

        private void AddDetail(string text)
        {
            lock (_technicalDetails) _technicalDetails.Add($"[{DateTime.Now:HH:mm:ss.fff}] {text}");
        }

        private void Log(string text) => AppLog.Log($"{_logPrefix} {text}");

        private static void Ui(Action action) => UiThread.BeginInvoke(action);

        public void Dispose()
        {
            StopTickTimer();
            CancelConnectingTimeout();
            StopMediaWatchdog();
            StopRingback();
            PlatformRingtone.Stop();
            try { _ringback?.Dispose(); } catch { }
            _ringback = null;
            UnhookSip();

            if (_recording != null)
            {
                // Keep the WebRTC subscription + recorder alive briefly: phone.js sends
                // recording_complete after hangup (mirrors the 10 s grace in the WPF window).
                var rec = _recording;
                _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
                {
                    UnhookWebRtc();
                    rec.Dispose();
                }, TaskScheduler.Default);
            }
            else
            {
                UnhookWebRtc();
            }
        }
    }

    public enum CallUiTone { Neutral, Good, Bad }
}
