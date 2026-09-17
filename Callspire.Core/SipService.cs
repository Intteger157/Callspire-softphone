using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.IO;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Newtonsoft.Json;
using Softphone.Audio;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Net;

namespace Softphone
{
    public class SipService : IDisposable
    {
        private readonly string _username;
        private readonly string _password;
        private readonly string _server;
        private readonly int _port;
        
        // Для отладки: хеш-код экземпляра
        private readonly int _instanceHash;
        
        // Флаг для вторичного подключения (не проверяет WebRTC, всегда работает через SIP)
        private readonly bool _isSecondaryConnection;

        // SIP signalling transport: UDP vs TLS
        private readonly bool _useTls;

        // SIP media security: RTP vs SRTP (SDES)
        private readonly bool _useSrtp;

        // SRTP (SDES) is handled natively by SIPSorcery when enabled (see EnableNativeSrtp).
        
        // Кэшированный локальный IP адрес (выбирается один раз при инициализации)
        private string? _cachedLocalIPAddress;
        
        // Cooldown для игнорирования SIP входящих после завершения WebRTC звонка
        private DateTime? _ignoreSipUntil = null;
        private readonly object _ignoreSipLock = new object();

        private SIPTransport? _sipTransport;
        private SIPUserAgent? _userAgent;
        private SIPRegistrationUserAgent? _regUserAgent;

        // Audio devices are created through the injected IAudioDeviceFactory (Callspire.Core
        // abstraction) — SipService no longer references NAudio/WASAPI/WinMM types directly.
        private readonly IAudioDeviceFactory? _audioDeviceFactory;
        private AudioDeviceSession? _audioDevices;
        private VoIPMediaSession? _voipMediaSession;
        private AudioEncoder? _audioEncoder; // Сохраняем ссылку на AudioEncoder для получения кодеков
        // Active audio source actually wired into the current VoIPMediaSession (not a new one from ToMediaEndPoints()).
        // We keep it so we can reliably pause/resume outgoing RTP on mute/hold.
        private object? _activeAudioSource;
        private bool _skipAudioInitialization = false; // Флаг для пропуска инициализации аудио (для WebRTC режима)
        private ITonePlayer? _toneGenerator; // Генератор гудков (ленивая инициализация)
        private Task<bool>? _activeCallTask; // Задача активного звонка для возможности отмены
        private System.Threading.CancellationTokenSource? _callCancellationTokenSource; // Для отмены звонка
        private bool _isCallCancelled = false; // Флаг отмены звонка пользователем (чтобы не воспроизводить гудки для последующих ответов)
        private volatile bool _outgoingInviteTerminated; // 487 / CANCEL confirmed — block late 200 OK from starting a new recorder
        private bool _isCallRejected = false; // Флаг отклонения входящего звонка (чтобы не начинать запись)
        private System.Threading.CancellationTokenSource? _earlyMediaRingbackFallbackCts; // 183 w/ SDP but no RTP yet → start local ringback
        private System.Threading.CancellationTokenSource? _deadMediaWatchdogCts; // answered (200 OK) but 0 RTP → re-apply answer SDP to recover audio
        // Outgoing INVITE tracking: we must bind "final response" to a specific Call-ID,
        // otherwise parallel INVITEs (401 auth retry, number-format retry) can stomp each other
        // and prematurely close the call UI while signalling continues.
        private readonly object _outgoingInviteLock = new();
        private TaskCompletionSource<int>? _pendingOutgoingInviteFinalResponseTcs;
        private TaskCompletionSource<string>? _pendingOutgoingInviteCallIdTcs;
        private readonly Dictionary<string, TaskCompletionSource<int>> _outgoingInviteFinalByCallId = new(StringComparer.OrdinalIgnoreCase);
        private string? _currentOutgoingAttemptId;
        private string? _currentOutgoingDialNumber;
        private string? _currentOutgoingCallId;

        private void TrySignalOutgoingInviteFinal(string? callId, int statusCode)
        {
            if (string.IsNullOrWhiteSpace(callId)) return;
            TaskCompletionSource<int>? tcs = null;
            lock (_outgoingInviteLock)
            {
                _outgoingInviteFinalByCallId.TryGetValue(callId, out tcs);
            }
            try { tcs?.TrySetResult(statusCode); } catch { }
        }

        /// <summary>
        /// Marks that the SIP server replied to our outgoing INVITE (any 1xx–6xx except REGISTER).
        /// Used to avoid false "VPN/network" errors when carriers only send 183 Session Progress.
        /// </summary>
        private void MarkOutgoingInviteSipResponseSeen(string? callId, int statusCode, SIPMethodsEnum cseqMethod)
        {
            if (cseqMethod == SIPMethodsEnum.REGISTER)
                return;

            if (statusCode == 401 || statusCode == 407)
            {
                _outgoingInviteReceivedSipResponse = true;
                _lastFailureWasConnectTimeout = false;
                return;
            }

            if (cseqMethod == SIPMethodsEnum.INVITE || (statusCode >= 100 && statusCode < 200))
            {
                _outgoingInviteReceivedSipResponse = true;
                _lastFailureWasConnectTimeout = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(callId))
                return;

            lock (_outgoingInviteLock)
            {
                if (_outgoingInviteFinalByCallId.ContainsKey(callId)
                    || (!string.IsNullOrEmpty(_currentOutgoingCallId)
                        && string.Equals(callId, _currentOutgoingCallId, StringComparison.OrdinalIgnoreCase)))
                {
                    _outgoingInviteReceivedSipResponse = true;
                    _lastFailureWasConnectTimeout = false;
                }
            }
        }

        /// <summary>
        /// True when the carrier/PBX already responded to our INVITE or media is flowing —
        /// rules out TLS/DNS/VPN routing failures.
        /// </summary>
        private bool WasOutgoingInviteServerReachable()
        {
            return _outgoingInviteReceivedSipResponse
                || System.Threading.Volatile.Read(ref _rtpPacketsReceived) > 0
                || _earlyMediaAudioDetected;
        }

        private string? BuildOutboundFailureStatusMessage()
        {
            bool serverReachable = WasOutgoingInviteServerReachable();

            // Connect-timeout flag can be stale if CANCEL/487 arrives after teardown starts — never
            // show VPN/network when we already had SIP responses or RTP (early media / ringback).
            if (_lastFailureWasConnectTimeout && !serverReachable)
                return "Cannot reach SIP server (VPN/network)";

            switch (_lastInviteFailureStatusCode)
            {
                case 486:
                    return "Line busy";
                case 603:
                    return "Call declined";
                case 480:
                    return "Subscriber unavailable";
                case 487:
                    // Capture at 487 receipt — ApplyOutboundFailureUiTeardown sets _isCallCancelled before this runs.
                    return _last487FromLocalCancel ? "Call cancelled" : "Call declined";
                case 404:
                    return "Number not found";
                case 408:
                    return serverReachable ? "No answer (timeout)" : "Cannot reach SIP server (VPN/network)";
            }

            if (serverReachable)
                return "No answer (timeout)";

            return "Call failed (timeout/rejected).";
        }

        /// <summary>
        /// Beeline/Asterisk IVR: 183 early media + RTP, then 480 without 200 OK — still a successful reach.
        /// </summary>
        private bool ShouldTreatSipFailureAsEarlyMediaAnswered(int statusCode)
        {
            if (!_earlyMediaAudioDetected)
                return false;
            if (System.Threading.Volatile.Read(ref _rtpPacketsReceived) < 50)
                return false;
            return statusCode == 480
                || statusCode == 408
                || (statusCode == 487 && !_last487FromLocalCancel);
        }

        private bool IsEarlyMediaAnsweredCall() => _earlyMediaAnsweredCall;

        private void TryMarkEarlyMediaAnsweredIfReady()
        {
            if (_earlyMediaAnsweredCall || !_earlyMediaAudioDetected)
                return;
            if (System.Threading.Volatile.Read(ref _rtpPacketsReceived) < 50)
                return;
            MarkEarlyMediaAnswered("early media RTP (IVR/auto-attendant)");
        }

        private void MarkEarlyMediaAnswered(string context)
        {
            if (_earlyMediaAnsweredCall)
                return;

            _earlyMediaAnsweredCall = true;
            _earlyMediaAnswerTime = DateTime.Now;
            _wasCallActive = true;
            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Early media treated as answered ({context}), RTP={System.Threading.Volatile.Read(ref _rtpPacketsReceived)} pkts");
            TryStartCallRecordingAfterAnswer(context);
            SetStatus($"[{DateTime.Now:HH:mm:ss.fff}] Call connected (early media/IVR)");
        }

        private void FinalizeEarlyMediaAnsweredCallEnd()
        {
            _toneGenerator?.Stop();
            ForceCancelOutgoingCall("early media call ended");
            SetStatus($"[{DateTime.Now:HH:mm:ss.fff}] Call ended by remote party");
            _wasCallActive = false;
            StopCallRecording();
            StopRtpDiagnostics();
            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Early media/IVR call ended normally — invoking OnCallEnded");
            UiThread.BeginInvoke(() => OnCallEnded?.Invoke());
        }
        // NOTE: SIP call recording is disabled. Recording is supported for WebRTC calls only.

        // Registration/call recovery helpers (primarily for secondary SIP trunks).
        private readonly object _registrationLock = new object();
        private TaskCompletionSource<bool>? _registrationTcs;
        private int? _lastInviteFailureStatusCode;
        private bool _last487FromLocalCancel;
        private DateTime _lastInvite403AtUtc = DateTime.MinValue;
        private IPAddress? _resolvedServerIp;
        /// <summary>Real routable IP learned from successful REGISTER/INVITE TLS (e.g. 81.26.x.x when VPN DNS returns 198.18.x.x).</summary>
        private IPAddress? _lastKnownGoodServerIp;
        private volatile bool _outgoingInviteReceivedSipResponse;
        private bool _lastFailureWasConnectTimeout;

        /// <summary>True when DNS returned only VPN/virtual IPs and no cached good IP is available.</summary>
        public bool ServerDnsUnusable { get; private set; }

        /// <summary>Last registration failure message (for UI diagnostics).</summary>
        public string? LastRegistrationError { get; private set; }

        /// <summary>True when the last outgoing call failed before any SIP response (TLS/DNS/VPN routing).</summary>
        public bool LastFailureWasConnectTimeout => _lastFailureWasConnectTimeout;
        
        /// <summary>
        /// Создает тон-плеер при первом использовании (ленивая инициализация).
        /// Платформенная реализация подключается через TonePlayerFactory (Desktop: ToneGenerator/NAudio).
        /// </summary>
        private void EnsureToneGenerator()
        {
            if (_toneGenerator == null)
            {
                _toneGenerator = TonePlayerFactory.Create?.Invoke();
            }
        }

        private void ScheduleRingbackFallbackAfter183(string timestamp)
        {
            // If we got 183 with SDP, some carriers provide early media (RTP ringback).
            // But if RTP never arrives, user hears silence. In that case we start local ringback
            // after a short grace period, and stop it as soon as RTP arrives / call is answered / cancelled.
            try { _earlyMediaRingbackFallbackCts?.Cancel(); } catch { }
            try { _earlyMediaRingbackFallbackCts?.Dispose(); } catch { }
            _earlyMediaRingbackFallbackCts = new System.Threading.CancellationTokenSource();
            var token = _earlyMediaRingbackFallbackCts.Token;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(1800, token);
                    if (token.IsCancellationRequested) return;

                    // If we already have real early media audio, do nothing.
                    if (_earlyMediaAudioDetected) return;

                    // If RTP is already flowing (even comfort-noise / near-silent early media),
                    // do not start local ringback. Carriers often send quiet CN first, then real ringback;
                    // overlaying ToneGenerator here correlates with one-way-audio after answer (181 / multi-183).
                    if (System.Threading.Volatile.Read(ref _rtpPacketsReceived) > 0) return;

                    if (_isCallCancelled) return;

                    // Only start ringback if we're still in a "setting up call" state.
                    if (_userAgent?.IsCallActive == true) return; // already connected (or at least active)
                    if (_voipMediaSession == null) return;

                    EnsureToneGenerator();
                    _toneGenerator?.PlayRingbackTone();
                    _localRingbackFallbackActive = true;
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}{timestamp} 183 received but early media is silent — starting local ringback fallback.");
                }
                catch (OperationCanceledException) { }
                catch { }
            });
        }

        /// <summary>
        /// После ответа абонента (200 OK) проверяет, что медиа реально пошло.
        /// В звонках БЕЗ early media (нет 183 с SDP) SIPSorcery иногда не применяет SDP из 200 OK:
        /// форматы кодеков не согласуются (нет "Recv/Send format set"), RTP не отправляется и не принимается,
        /// разговор и запись остаются пустыми. Если через ~2.5с после ответа не принято ни одного RTP-пакета,
        /// повторно применяем SDP-ответ и перезапускаем медиасессию.
        /// </summary>
        private void ScheduleDeadMediaWatchdogAfterAnswer(string? answerSdpBody)
        {
            try { _deadMediaWatchdogCts?.Cancel(); } catch { }
            try { _deadMediaWatchdogCts?.Dispose(); } catch { }
            _deadMediaWatchdogCts = new System.Threading.CancellationTokenSource();
            var token = _deadMediaWatchdogCts.Token;
            string connLabel = _isSecondaryConnection ? "[Connection2] " : "";

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(2500, token);
                    if (token.IsCancellationRequested) return;
                    if (_isCallCancelled || _outgoingInviteTerminated) return;
                    if (_userAgent?.IsCallActive != true) return;
                    var ms = _voipMediaSession;
                    if (ms == null) return;

                    // Медиа живое — RTP уже приходит, вмешиваться не нужно.
                    if (System.Threading.Volatile.Read(ref _rtpPacketsReceived) > 0) return;

                    AppLog.Log($"[SipService] {connLabel}⚠️ DEAD MEDIA after answer: 0 RTP packets ~2.5s after 200 OK — re-applying answer SDP to recover audio.");

                    if (string.IsNullOrWhiteSpace(answerSdpBody))
                    {
                        AppLog.Log($"[SipService] {connLabel}Dead media recovery aborted: 200 OK SDP is not available.");
                        return;
                    }

                    try
                    {
                        var remoteSdp = SDP.ParseSDPDescription(answerSdpBody);
                        var setResult = ms.SetRemoteDescription(SdpType.answer, remoteSdp);
                        AppLog.Log($"[SipService] {connLabel}Dead media recovery: SetRemoteDescription(answer) => {setResult}");

                        await ms.Start().ConfigureAwait(false);

                        // Подстраховка: явно стартуем источник/приёмник звука (вызовы идемпотентны).
                        try
                        {
                            var src = ms.Media?.AudioSource;
                            if (src != null) await src.StartAudio().ConfigureAwait(false);
                        }
                        catch (Exception exSrc) { AppLog.Log($"[SipService] {connLabel}Dead media recovery: StartAudio failed: {exSrc.Message}"); }
                        try
                        {
                            var sink = ms.Media?.AudioSink;
                            if (sink != null) await sink.StartAudioSink().ConfigureAwait(false);
                        }
                        catch (Exception exSink) { AppLog.Log($"[SipService] {connLabel}Dead media recovery: StartAudioSink failed: {exSink.Message}"); }

                        AppLog.Log($"[SipService] {connLabel}Dead media recovery: media session restarted.");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log($"[SipService] {connLabel}Dead media recovery failed: {ex.Message}");
                    }

                    // Контрольная проверка результата для диагностики.
                    await System.Threading.Tasks.Task.Delay(2500, token);
                    if (token.IsCancellationRequested) return;
                    int pkts = System.Threading.Volatile.Read(ref _rtpPacketsReceived);
                    AppLog.Log($"[SipService] {connLabel}Dead media recovery result: rtpPacketsReceived={pkts} (~2.5s after recovery attempt).");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppLog.Log($"[SipService] Dead media watchdog error: {ex.Message}");
                }
            });
        }

        // Информация о входящем звонке
        private SIPUserAgent? _incomingCallUserAgent;
        private SIPRequest? _incomingCallRequest;
        private string? _incomingCallerNumber;
        private string? _incomingCallId; // Сохраняем Call-ID для проверки
        
        // Хранилище входящих запросов по Call-ID для надежного восстановления
        private readonly ConcurrentDictionary<string, (SIPUserAgent UserAgent, SIPRequest Request, string CallerNumber)> _incomingCalls = new();
        
        // Сохраняем ссылку на основной UserAgent для входящих звонков
        // В SIPSorcery входящий звонок обрабатывается через событие OnIncomingCall
        // где ua - это SIPUserAgent для этого конкретного звонка

        public event Action<string>? OnStatusChanged;
        public event Action? OnCallEnded;
        /// <summary>Raised on UI thread when SIP RTP recording WAV is ready (after ffmpeg).</summary>
        public event Action<string>? OnRecordingFinalized;
        public event Action<string>? OnIncomingCall; // Событие для входящего звонка (номер звонящего)
        public event Action<string>? OnOutboundCallerIdReceived; // P-Asserted-Identity from SIP responses

        /// <summary>
        /// The outbound CallerID (P-Asserted-Identity) received from the PBX/carrier in SIP responses.
        /// </summary>
        public string? OutboundCallerId { get; private set; }
        
        /// <summary>
        /// Устанавливает cooldown для игнорирования SIP входящих звонков (используется при активном WebRTC звонке)
        /// </summary>
        /// <param name="durationSeconds">Длительность cooldown в секундах (по умолчанию 3 секунды)</param>
        public void SetIgnoreSipCooldown(int durationSeconds = 3)
        {
            lock (_ignoreSipLock)
            {
                _ignoreSipUntil = DateTime.UtcNow.AddSeconds(durationSeconds);
                AppLog.Log($"[SipService] SetIgnoreSipCooldown: SIP incoming calls will be ignored for {durationSeconds} seconds");
            }
        }

        public bool IsRegistered { get; private set; }
        public bool IsInCall => _userAgent?.IsCallActive == true;

        /// <summary>Earliest UTC time a new outbound INVITE may start (carrier/BYE teardown grace).</summary>
        private DateTime _outboundQuietUntilUtc = DateTime.MinValue;

        /// <summary>
        /// True while an outbound call is active, tearing down, or in post-BYE quiet period.
        /// Prevents rapid re-dial (603 Decline) before the trunk finishes clearing the previous leg.
        /// </summary>
        public bool IsOutboundBusy =>
            IsInCall ||
            (_activeCallTask != null && !_activeCallTask.IsCompleted) ||
            DateTime.UtcNow < _outboundQuietUntilUtc;

        /// <summary>
        /// True while media/ringback is being set up or torn down — avoid disposing the whole stack (e.g. periodic trunk refresh).
        /// </summary>
        public bool IsOccupiedForStackRefresh =>
            (_userAgent?.IsCallActive == true) ||
            _voipMediaSession != null ||
            _toneGenerator != null;

        public bool IsMuted { get; private set; }
        public bool IsOnHold { get; private set; }

        /// <summary>
        /// Enable software AEC on the WASAPI audio endpoint for speakerphone use.
        /// Set before calling InitializeAudio / making a call; propagated to each new endpoint.
        /// </summary>
        public bool EnableAec { get; set; } = true;
        public string? CurrentRecordingFilePath
        {
            get
            {
                lock (_recorderLock)
                {
                    // Пока рекордер жив – берём путь из него.
                    // После остановки записи используем последний известный путь.
                    return _rtpCallRecorder?.RecordingFilePath ?? _lastRecordingFilePath;
                }
            }
        }

        /// <summary>
        /// Returns true once after a SIP recording stop that produced no WAV file, then clears the latch.
        /// CallWindow uses empty <see cref="CallHistoryService.UpdateCallDetails"/> path to drop stale placeholders.
        /// </summary>
        public bool TryConsumeSipRecordingWithNoOutputFile()
        {
            lock (_recorderLock)
            {
                if (!_sipRecordingStopHadNoOutputFile) return false;
                _sipRecordingStopHadNoOutputFile = false;
                return true;
            }
        }

        /// <summary>Stops active SIP RTP recording and finalizes WAV if a recorder is running.</summary>
        public void FinalizeCallRecordingIfActive()
        {
            StopCallRecording();
        }

        /// <summary>Waits for in-flight SIP recording finalize and attempts orphan PCM recovery.</summary>
        public async Task FinalizeCallRecordingIfActiveAsync(int waitMs = 90000)
        {
            StopCallRecording();
            Task? task;
            lock (_recorderLock)
            {
                task = _recordingFinalizeTask;
            }
            if (task != null)
            {
                try
                {
                    await Task.WhenAny(task, Task.Delay(waitMs)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}FinalizeCallRecordingIfActiveAsync wait: {ex.Message}");
                }
            }

            string? path;
            lock (_recorderLock)
            {
                path = _lastRecordingFilePath;
            }
            if (!string.IsNullOrEmpty(path) && (!File.Exists(path) || new FileInfo(path).Length == 0))
            {
                if ((RtpCallRecorderFactory.TryRecoverWavFromOrphanPcm?.Invoke(path) ?? false))
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Recovered WAV from orphan PCM: {path}");
            }
        }
        
        // RTP call recorder для записи SIP звонков
        // Используем lock для защиты от race conditions при множественных звонках
        private IRtpCallRecorder? _rtpCallRecorder;
        private readonly object _recorderLock = new object();
        private bool _isStoppingRecording = false; // Флаг для предотвращения множественных вызовов StopCallRecording
        private Task? _recordingFinalizeTask;
        
        // Filtering audio sink - prevents DTMF/CN packets from being decoded as audio
        private FilteringAudioSink? _filteringAudioSink;
        
        // ========== RTP DIAGNOSTICS ==========
        private int _rtpPacketsReceived;
        private int _rtpSequenceGaps;
        private int _rtpLastSequenceNumber = -1;
        private Dictionary<int, int> _rtpPayloadTypeCounts = new Dictionary<int, int>();
        private int _rtpNegotiatedPayloadType = -1; // Payload type из согласованного кодека
        private volatile bool _earlyMediaAudioDetected = false; // True once we see non-silent early media (ringback/IVR)
        private volatile bool _earlyMediaAnsweredCall = false; // IVR/auto-attendant: 183+RTP treated as answered even without 200 OK
        private DateTime? _earlyMediaAnswerTime;
        private volatile bool _localRingbackFallbackActive = false;
        private System.Threading.Timer? _rtpStatsTimer;
        private DateTime _rtpStatsStartTime;
        private long _rtpTotalBytesReceived;
        private WavFileWriter? _diagnosticWavWriter;
        private readonly object _diagnosticWavLock = new object();
        
        // ========== NAT TRAVERSAL (STUN) ==========
        private string? _publicIPAddress; // Кэшированный внешний IP адрес
        private DateTime _publicIPLastUpdate = DateTime.MinValue;
        private readonly TimeSpan _publicIPCacheTimeout = TimeSpan.FromMinutes(10); // Кэшируем на 10 минут
        private string? _detectedNatType; // Определенный тип NAT
        
        // ========== CALL RECORDING ==========
        private string? _lastCalledNumber; // Сохраняем номер последнего вызова для записи после 200 OK
        // Последний путь к файлу записи (сохраняем его даже после Dispose рекордера).
        private string? _lastRecordingFilePath;
        /// <summary>After the last StopCallRecording, SIP recorder had no output WAV on disk (consumed by CallWindow).</summary>
        private bool _sipRecordingStopHadNoOutputFile;
        /// <summary>Deduplicate INVITE 200 OK retransmissions (Call-ID|CSeq).</summary>
        private string? _lastHandledInvite200OkKey;

        /// <summary>Inbound RTP packets at end of last call (null until StopRtpDiagnostics).</summary>
        public int? LastCallInboundRtpPackets { get; private set; }

        /// <summary>Number of the active or most recent outbound/inbound dial on this SipService instance.</summary>
        public string? ActiveDialNumber => _currentOutgoingDialNumber ?? _lastCalledNumber;
        
        /// <summary>
        /// Отправляет DTMF-тон во время активного звонка.
        /// </summary>
        public void SendDTMF(char digit)
        {
            if (!IsInCall)
            {
                SetStatus("No active call to send DTMF.");
                return;
            }

            if (_voipMediaSession == null)
            {
                SetStatus("Media session not available.");
                return;
            }

            try
            {
                // Отправляем DTMF через RTP (RFC 4733)
                // Преобразуем символ в правильный DTMF код (0-9, * = 10, # = 11)
                byte dtmfByte;
                if (digit >= '0' && digit <= '9')
                {
                    dtmfByte = (byte)(digit - '0');
                }
                else if (digit == '*')
                {
                    dtmfByte = 10;
                }
                else if (digit == '#')
                {
                    dtmfByte = 11;
                }
                else
                {
                    SetStatus($"Invalid DTMF digit: {digit}");
                    return;
                }
                
                _voipMediaSession.SendDtmf(dtmfByte, System.Threading.CancellationToken.None);
                SetStatus($"DTMF sent: {digit}");
            }
            catch (Exception ex)
            {
                SetStatus($"DTMF error: {ex.Message}");
            }
        }
        private bool _wasCallActive = false; // Флаг для отслеживания завершения звонка

        private int? _microphoneDeviceNumber;
        private int? _speakerDeviceNumber;

        private string _audioCodec = "PCMU"; // По умолчанию G.711 μ-law
        private int _audioSampleRate = 8000;
        private int _audioBitrate = 64000;

        private static SIPRegistrationUserAgent CreateRegistrationUserAgent(
            SIPTransport sipTransport,
            string username,
            string password,
            string serverAddress,
            string aorUri,
            int expiry,
            bool useTls)
        {
            // IMPORTANT:
            // For UDP registrations the legacy ctor is the most reliable across SIPSorcery versions.
            // Our reflection-based ctor selection can mis-map string parameters on some versions,
            // resulting in an invalid Contact header (e.g., Contact becomes the server itself),
            // which some providers reject with 403 without a 401 challenge.
            if (!useTls)
            {
                var legacyUdp = new SIPRegistrationUserAgent(sipTransport, username, password, serverAddress, expiry: expiry);
                try
                {
                    AppLog.Log($"[SipService] CreateRegistrationUserAgent: using legacy ctor for UDP (useTls={useTls}, aor='{aorUri}', serverAddress='{serverAddress}')");
                }
                catch { }
                return legacyUdp;
            }

            try
            {
                var protocol = useTls ? SIPProtocolsEnum.tls : SIPProtocolsEnum.udp;

                SIPURI? parsedAor = null;
                try { parsedAor = SIPURI.ParseSIPURI(aorUri); } catch { }
                SIPURI? parsedRegistrar = null;
                try
                {
                    // Some providers reject REGISTER if the Request-URI becomes sip:registrar@host.
                    // Prefer a domain-style registrar URI (sip:host[:port]) and only fall back to a dummy user
                    // if the SIPURI parser is strict.
                    var scheme = useTls ? "sips" : "sip";
                    try
                    {
                        var registrarUriNoUser = $"{scheme}:{serverAddress}";
                        parsedRegistrar = SIPURI.ParseSIPURI(registrarUriNoUser);
                    }
                    catch
                    {
                        // Fallback: strict parsers may require a user part.
                        var registrarUriWithUser = $"{scheme}:registrar@{serverAddress}";
                        parsedRegistrar = SIPURI.ParseSIPURI(registrarUriWithUser);
                    }
                }
                catch { }

                // Try to find a ctor that allows explicitly passing protocol and/or SIPURI (sips:).
                // We avoid relying on parameter names; instead we map by parameter TYPES.
                var ctors = typeof(SIPRegistrationUserAgent).GetConstructors()
                    .OrderByDescending(c =>
                    {
                        var ps = c.GetParameters();
                        // Strongly prefer overloads that allow explicit protocol.
                        int score = 0;
                        if (ps.Any(p => p.ParameterType == typeof(SIPProtocolsEnum))) score += 100;
                        // Prefer overloads that accept SIPURI (so we can pass sips: AOR/registrar).
                        score += ps.Count(p => p.ParameterType == typeof(SIPURI)) * 10;
                        // Slightly prefer longer overloads (more control).
                        score += ps.Length;
                        return score;
                    })
                    .ToArray();

                foreach (var ctor in ctors)
                {
                    var ps = ctor.GetParameters();

                    // We only attempt constructors that at least start with SIPTransport.
                    if (ps.Length < 4 || ps[0].ParameterType != typeof(SIPTransport))
                        continue;

                    // Prefer constructors that can accept SIPProtocolsEnum when TLS is requested.
                    if (useTls && !ps.Any(p => p.ParameterType == typeof(SIPProtocolsEnum)) && !ps.Any(p => p.ParameterType == typeof(SIPURI)))
                        continue;

                    var args = new object?[ps.Length];
                    int sipUriIndex = 0;
                    int strIndex = 0;
                    int intIndex = 0;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var p = ps[i];

                        if (i == 0) { args[i] = sipTransport; continue; }

                        if (p.ParameterType == typeof(SIPProtocolsEnum)) { args[i] = protocol; continue; }

                        if (p.ParameterType == typeof(SIPURI))
                        {
                            // Many overloads accept both AOR and Registrar as SIPURI.
                            // Fill first SIPURI with AOR, second with Registrar (best-effort).
                            if (sipUriIndex == 0) args[i] = parsedAor;
                            else if (sipUriIndex == 1) args[i] = parsedRegistrar ?? parsedAor;
                            else args[i] = parsedRegistrar ?? parsedAor;
                            sipUriIndex++;
                            continue;
                        }

                        if (p.ParameterType == typeof(string))
                        {
                            // Deterministic positional mapping for strings:
                            // 0=username, 1=password, 2=serverAddress, 3=aorUri, others=serverAddress.
                            args[i] = strIndex switch
                            {
                                0 => username,
                                1 => password,
                                2 => serverAddress,
                                3 => aorUri,
                                _ => serverAddress
                            };
                            strIndex++;
                            continue;
                        }

                        if (p.ParameterType == typeof(int))
                        {
                            // Prefer mapping the first int to expiry (common for "expiry"/"expires").
                            args[i] = intIndex == 0 ? expiry : expiry;
                            intIndex++;
                            continue;
                        }

                        args[i] = p.HasDefaultValue ? p.DefaultValue : null;
                    }

                    // If ctor expects SIPURI but we couldn't parse, skip it.
                    if (ps.Any(p => p.ParameterType == typeof(SIPURI)) && parsedAor == null)
                        continue;
                    if (ps.Any(p => p.ParameterType == typeof(SIPURI)) && sipUriIndex >= 2 && parsedRegistrar == null)
                    {
                        // If it needs both AOR+Registrar as SIPURI, but registrar parse failed, skip.
                        continue;
                    }

                    var ua = ctor.Invoke(args) as SIPRegistrationUserAgent;
                    if (ua != null)
                    {
                        try
                        {
                            AppLog.Log($"[SipService] CreateRegistrationUserAgent: selected ctor '{ctor}' (useTls={useTls}, aor='{aorUri}', registrar='{(parsedRegistrar?.ToString() ?? "<null>")}', protocol={protocol})");
                        }
                        catch { }
                        // If we managed to select a rich ctor (with SIPEndPoint/SIPURI and/or protocol),
                        // avoid "best-effort" forcing — it can clobber internal fields and create false "Could not resolve" noise.
                        // Only force when we fall back to the legacy ctor.
                        return ua;
                    }
                }
            }
            catch { }

            // Fallback: legacy ctor without protocol.
            var legacy = new SIPRegistrationUserAgent(sipTransport, username, password, serverAddress, expiry: expiry);
            try
            {
                AppLog.Log($"[SipService] CreateRegistrationUserAgent: using legacy ctor (useTls={useTls}, aor='{aorUri}', serverAddress='{serverAddress}')");
                if (useTls)
                {
                    try
                    {
                        var all = typeof(SIPRegistrationUserAgent).GetConstructors();
                        foreach (var c in all)
                        {
                            var ps = c.GetParameters();
                            var sig = string.Join(", ", ps.Select(p => p.ParameterType.Name));
                            AppLog.Log($"[SipService] CreateRegistrationUserAgent: ctor candidate: ({sig})");
                        }
                    }
                    catch { }
                }
            }
            catch { }
            if (useTls)
            {
                TryForceRegistrationTransport(legacy, SIPProtocolsEnum.tls, aorUri, serverAddress);
            }
            return legacy;
        }

        private static void TryForceRegistrationTransport(
            SIPRegistrationUserAgent regUa,
            SIPProtocolsEnum protocol,
            string aorUri,
            string serverAddress)
        {
            try
            {
                int setCount = 0;

                // 1) Set any writable properties of type SIPProtocolsEnum.
                foreach (var prop in regUa.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (!prop.CanWrite) continue;
                        if (prop.PropertyType != typeof(SIPProtocolsEnum)) continue;
                        prop.SetValue(regUa, protocol);
                        setCount++;
                    }
                    catch { }
                }

                // 2) Set any fields of type SIPProtocolsEnum.
                foreach (var field in regUa.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (field.FieldType != typeof(SIPProtocolsEnum)) continue;
                        field.SetValue(regUa, protocol);
                        setCount++;
                    }
                    catch { }
                }

                // Keep forcing minimal: protocol enum only.
                // Overwriting SIPURI/string fields can break internal resolution logic and produce false "Could not resolve" errors.
                AppLog.Log($"[SipService] CreateRegistrationUserAgent: forced TLS best-effort (protocol only, setOps={setCount})");
            }
            catch (Exception ex)
            {
                try { AppLog.Log($"[SipService] CreateRegistrationUserAgent: force TLS failed: {ex.Message}"); } catch { }
            }
        }

        public SipService(string username, string password, string server, int port = 5060,
            int? microphoneDeviceNumber = null, int? speakerDeviceNumber = null,
            string audioCodec = "PCMU", int audioSampleRate = 8000, int audioBitrate = 64000,
            bool isSecondaryConnection = false,
            bool useTls = false,
            bool useSrtp = false,
            IAudioDeviceFactory? audioDeviceFactory = null)
        {
            _audioDeviceFactory = audioDeviceFactory;
            _username = username ?? throw new ArgumentNullException(nameof(username));
            _password = password ?? throw new ArgumentNullException(nameof(password));
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _port = port;
            _microphoneDeviceNumber = microphoneDeviceNumber;
            _speakerDeviceNumber = speakerDeviceNumber;
            _audioCodec = audioCodec ?? "PCMU";
            _audioSampleRate = audioSampleRate;
            _audioBitrate = audioBitrate;
            _isSecondaryConnection = isSecondaryConnection;
            _useTls = useTls;
            _useSrtp = useSrtp;
            _instanceHash = this.GetHashCode();
            EnsureSipsorceryLoggingWired();
            SetStatus($"SipService created, hash: {_instanceHash}, secondary: {_isSecondaryConnection}, tls: {_useTls}, srtp: {_useSrtp}");
        }

        private static int _sipsorceryLogWired;

        /// <summary>
        /// Прокидывает внутренние предупреждения/ошибки SIPSorcery в журнал приложения.
        /// Без этого сбои применения SDP (SetRemoteDescription) и старта медиа проходят молча:
        /// звонок выглядит установленным, но RTP не идёт и запись остаётся пустой.
        /// </summary>
        private static void EnsureSipsorceryLoggingWired()
        {
            if (System.Threading.Interlocked.Exchange(ref _sipsorceryLogWired, 1) == 1) return;
            try
            {
                SIPSorcery.LogFactory.Set(new AppLogLoggerFactory());
                AppLog.Log("[SipService] SIPSorcery internal logging wired to AppLog (Warning+).");
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Failed to wire SIPSorcery logging: {ex.Message}");
            }
        }

        private sealed class AppLogLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
        {
            public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new AppLogLogger(categoryName);
            public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
            public void Dispose() { }
        }

        private sealed class AppLogLogger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly string _category;
            public AppLogLogger(string category) { _category = category; }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
                => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                try
                {
                    string msg = formatter(state, exception);
                    AppLog.Log($"[SIPSorcery:{_category}] {logLevel}: {msg}{(exception != null ? $" | {exception.GetType().Name}: {exception.Message}" : "")}");
                }
                catch { }
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();
                public void Dispose() { }
            }
        }


        private static bool IsPrivateIp(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 100.64.0.0/10 (CGNAT)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            // 198.18.0.0/15 (benchmarking, non-routable on Internet)
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;
            return false;
        }

        /// <summary>
        /// VPN/split-DNS often resolves hostnames to 198.18.0.0/15 (Tailscale etc.).
        /// Those addresses are not reachable on the public Internet and must not be used for SIP TLS.
        /// </summary>
        private static bool IsNonRoutableVpnOrVirtualIp(IPAddress ip)
        {
            if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork)
                return false;

            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 198 && bytes[1] >= 18 && bytes[1] <= 19)
                return true;
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                return true;
            return false;
        }

        private static IPAddress? ResolveServerIp(string host, bool filterVpnAddresses)
        {
            if (string.IsNullOrWhiteSpace(host))
                return null;

            if (IPAddress.TryParse(host, out var parsed))
            {
                if (filterVpnAddresses && IsNonRoutableVpnOrVirtualIp(parsed))
                    return null;
                return parsed;
            }

            try
            {
                var addrs = Dns.GetHostAddresses(host);
                var ipv4 = addrs
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .ToList();

                if (filterVpnAddresses)
                {
                    var filtered = ipv4.Where(a => !IsNonRoutableVpnOrVirtualIp(a)).ToList();
                    if (filtered.Count > 0)
                        return filtered[0];

                    if (ipv4.Count > 0)
                    {
                        AppLog.Log($"[SipService] WARNING: DNS for '{host}' returned only VPN/virtual IPs ({string.Join(", ", ipv4.Select(a => a.ToString()))}) — ignoring for outbound SIP");
                        return null;
                    }
                }

                return ipv4.FirstOrDefault() ?? addrs.FirstOrDefault();
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] DNS resolve failed for '{host}': {ex.Message}");
                return null;
            }
        }

        private void RememberKnownGoodServerIp(object? remoteEndPoint)
        {
            if (!_isSecondaryConnection || remoteEndPoint == null)
                return;

            try
            {
                IPAddress? ip = remoteEndPoint switch
                {
                    IPEndPoint ep => ep.Address,
                    SIPEndPoint sep => sep.Address,
                    _ => null
                };

                if (ip == null || IsNonRoutableVpnOrVirtualIp(ip))
                    return;

                if (_lastKnownGoodServerIp != null && _lastKnownGoodServerIp.Equals(ip))
                    return;

                _lastKnownGoodServerIp = ip;
                AppLog.Log($"[SipService] [Connection2] Cached known-good server IP: {ip} (from live SIP TLS endpoint)");
            }
            catch { }
        }

        private void RefreshResolvedServerIp()
        {
            var host = _server;
            if (!string.IsNullOrWhiteSpace(host) && host.Contains(':', StringComparison.Ordinal))
            {
                var parts = host.Split(':', 2);
                host = parts[0];
            }

            var label = _isSecondaryConnection ? "[Connection2] " : "";
            var dnsIp = ResolveServerIp(host, filterVpnAddresses: _isSecondaryConnection);

            if (dnsIp != null && (!_isSecondaryConnection || !IsNonRoutableVpnOrVirtualIp(dnsIp)))
            {
                _resolvedServerIp = dnsIp;
                AppLog.Log($"[SipService] {label}Server '{host}' resolved to {_resolvedServerIp} (VPN-filtered DNS)");
            }
            else if (_lastKnownGoodServerIp != null)
            {
                _resolvedServerIp = _lastKnownGoodServerIp;
                AppLog.Log($"[SipService] {label}Server '{host}' DNS unusable — using last known good IP {_lastKnownGoodServerIp}");
            }
            else
            {
                _resolvedServerIp = dnsIp;
                ServerDnsUnusable = dnsIp == null && _lastKnownGoodServerIp == null;
                if (dnsIp == null)
                    AppLog.Log($"[SipService] {label}WARNING: Could not resolve server '{host}' to a usable IP");
            }

            if (_resolvedServerIp != null)
                ServerDnsUnusable = false;
        }

        private string GetServerPartForUri()
        {
            if (_resolvedServerIp != null)
                return $"{_resolvedServerIp}:{_port}";
            if (_server.Contains(':', StringComparison.Ordinal))
                return _server;
            return $"{_server}:{_port}";
        }

        private SIPEndPoint? BuildOutboundProxyEndpoint()
        {
            var host = _server;
            var port = _port;
            if (!string.IsNullOrWhiteSpace(_server) && _server.Contains(':', StringComparison.Ordinal))
            {
                var parts = _server.Split(':', 2);
                host = parts[0];
                if (parts.Length == 2 && int.TryParse(parts[1], out var parsedPort) && parsedPort is >= 1 and <= 65535)
                    port = parsedPort;
            }

            if (string.IsNullOrWhiteSpace(host))
                return null;

            var ip = _resolvedServerIp ?? ResolveServerIp(host, filterVpnAddresses: _isSecondaryConnection);
            if (ip == null)
                return null;

            return new SIPEndPoint(_useTls ? SIPProtocolsEnum.tls : SIPProtocolsEnum.udp, new IPEndPoint(ip, port));
        }

        private static IPAddress? TryGetPublicIpFromContactHeader(SIPResponse response)
        {
            try
            {
                var header = response.Header;
                if (header?.Contact == null || header.Contact.Count == 0) return null;
                var contact = header.Contact[0];
                var uri = contact?.ContactURI;
                var host = uri?.Host;
                if (string.IsNullOrWhiteSpace(host)) return null;
                if (IPAddress.TryParse(host, out var ip) && !IsPrivateIp(ip)) return ip;

                // Hostname: resolve and pick a public IPv4 address.
                try
                {
                    var addrs = Dns.GetHostAddresses(host);
                    var pub4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IsPrivateIp(a));
                    if (pub4 != null) return pub4;
                    var pubAny = addrs.FirstOrDefault(a => !IsPrivateIp(a));
                    if (pubAny != null) return pubAny;
                }
                catch { }
            }
            catch { }
            return null;
        }

        private static string? TryGetSdpConnectionIp(string sdp)
        {
            foreach (var raw in sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.StartsWith("c=IN IP4", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3) return parts[2];
                }
            }
            return null;
        }

        private static string ReplaceSdpConnectionIp(string sdp, string newIp)
        {
            // Replace first c=IN IP4 line only.
            var lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("c=IN IP4", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"c=IN IP4 {newIp}";
                    break;
                }
            }
            return string.Join("\r\n", lines);
        }

        /// <summary>
        /// Пропускает инициализацию аудио в SIPSorcery (для использования WebRTC)
        /// </summary>
        public void SkipAudioInitialization()
        {
            _skipAudioInitialization = true;
            SetStatus("Audio initialization will be skipped (WebRTC mode)");
        }

        /// <summary>
        /// Включает/отключает микрофон для SIP-звонка (без системного mute Windows).
        /// Системный mute микрофона через Core Audio на части ноутбуков/драйверов (Realtek)
        /// глушит и динамики — поэтому используем только паузу источника (перестаём слать голос в RTP).
        /// Иконка микрофона в трее Windows при этом не перечёркивается — это ожидаемо.
        /// </summary>
        public void SetMute(bool mute)
        {
            try
            {
                if (_voipMediaSession == null || _audioDevices == null)
                {
                    SetStatus("Audio not initialized");
                    return;
                }

                IsMuted = mute;
                AppLog.Log($"[SipService] Microphone muted (app-level): {mute}");

                bool applied = TryApplyAudioSourceMute(_activeAudioSource, mute);
                if (!applied)
                    AppLog.Log("[SipService] ⚠ Mute: could not pause/resume AudioSource (no SetPaused/PauseAudio/Pause). Remote party may still hear you.");

                SetStatus(mute ? "Microphone muted" : "Microphone unmuted");
            }
            catch (Exception ex)
            {
                SetStatus($"Mute error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"SetMute error: {ex}");
            }
        }

        /// <summary>
        /// Pauses capture on the concrete audio source used by VoIPMediaSession.
        /// WasapiAudioEndPoint exposes <see cref="WasapiAudioEndPoint.PauseAudio"/> / ResumeAudio;
        /// older reflection code only looked for Pause/Resume and never matched.
        /// </summary>
        private static bool TryApplyAudioSourceMute(object? audioSource, bool mute)
        {
            if (audioSource == null) return false;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var t = audioSource.GetType();

            try
            {
                var setPaused = t.GetMethod("SetPaused", flags, binder: null, types: new[] { typeof(bool) }, modifiers: null);
                if (setPaused != null)
                {
                    setPaused.Invoke(audioSource, new object[] { mute });
                    AppLog.Log($"[SipService] Mute: {t.Name}.SetPaused({mute})");
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Mute: SetPaused failed on {t.Name}: {ex.Message}");
            }

            try
            {
                if (mute)
                {
                    var pauseAudio = t.GetMethod("PauseAudio", flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (pauseAudio != null)
                    {
                        var r = pauseAudio.Invoke(audioSource, null);
                        if (r is Task task)
                            task.GetAwaiter().GetResult();
                        AppLog.Log($"[SipService] Mute: {t.Name}.PauseAudio()");
                        return true;
                    }

                    var pause = t.GetMethod("Pause", flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (pause != null)
                    {
                        pause.Invoke(audioSource, null);
                        AppLog.Log($"[SipService] Mute: {t.Name}.Pause()");
                        return true;
                    }
                }
                else
                {
                    var resumeAudio = t.GetMethod("ResumeAudio", flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (resumeAudio != null)
                    {
                        var r = resumeAudio.Invoke(audioSource, null);
                        if (r is Task task)
                            task.GetAwaiter().GetResult();
                        AppLog.Log($"[SipService] Mute: {t.Name}.ResumeAudio()");
                        return true;
                    }

                    var resume = t.GetMethod("Resume", flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (resume != null)
                    {
                        resume.Invoke(audioSource, null);
                        AppLog.Log($"[SipService] Mute: {t.Name}.Resume()");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Mute: Pause/Resume failed on {t.Name}: {ex.Message}");
            }

            return false;
        }

        private static bool TryApplyAudioSinkPause(object? audioSink, bool pause)
        {
            if (audioSink == null) return false;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var t = audioSink.GetType();

            try
            {
                if (pause)
                {
                    var pauseSink = t.GetMethod("PauseAudioSink", flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (pauseSink != null)
                    {
                        var r = pauseSink.Invoke(audioSink, null);
                        if (r is Task task)
                            task.GetAwaiter().GetResult();
                        AppLog.Log($"[SipService] Hold: {t.Name}.PauseAudioSink()");
                        return true;
                    }
                }
                else
                {
                    var resumeSink = t.GetMethod("ResumeAudioSink", flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (resumeSink != null)
                    {
                        var r = resumeSink.Invoke(audioSink, null);
                        if (r is Task task)
                            task.GetAwaiter().GetResult();
                        AppLog.Log($"[SipService] Hold: {t.Name}.ResumeAudioSink()");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Hold: sink pause/resume failed on {t.Name}: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Ставит звонок на удержание или возобновляет его.
        /// </summary>
        public async Task HoldCallAsync(bool hold)
        {
            if (_userAgent == null || !IsInCall)
            {
                SetStatus("No active call to hold.");
                return;
            }

            try
            {
                IsOnHold = hold;

                // 1) Prefer native SIP hold/unhold if available in this SIPSorcery version.
                // We use reflection to stay compatible across library versions.
                async Task<bool> TryInvokeAsync(object target, string methodName, params object?[] args)
                {
                    try
                    {
                        var mi = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                        if (mi == null) return false;

                        var result = mi.Invoke(target, args);
                        if (result is Task t)
                        {
                            await t.ConfigureAwait(false);
                            return true;
                        }
                        return true;
                    }
                    catch (TargetInvocationException tie)
                    {
                        throw tie.InnerException ?? tie;
                    }
                }

                bool invoked = false;
                var ua = _userAgent;
                var ms = _voipMediaSession;

                if (hold)
                {
                    // Common names in various versions.
                    invoked =
                        await TryInvokeAsync(ua, "PutOnHold") ||
                        await TryInvokeAsync(ua, "Hold") ||
                        await TryInvokeAsync(ua, "SetHold", true) ||
                        (ms != null && (await TryInvokeAsync(ms, "PutOnHold") || await TryInvokeAsync(ms, "Hold") || await TryInvokeAsync(ms, "SetHold", true)));
                }
                else
                {
                    invoked =
                        await TryInvokeAsync(ua, "TakeOffHold") ||
                        await TryInvokeAsync(ua, "Unhold") ||
                        await TryInvokeAsync(ua, "Resume") ||
                        await TryInvokeAsync(ua, "SetHold", false) ||
                        (ms != null && (await TryInvokeAsync(ms, "TakeOffHold") || await TryInvokeAsync(ms, "Unhold") || await TryInvokeAsync(ms, "Resume") || await TryInvokeAsync(ms, "SetHold", false)));
                }

                // 2) Pause/resume mic capture and speaker playback during hold.
                if (!TryApplyAudioSourceMute(_activeAudioSource, hold))
                    AppLog.Log($"[SipService] HoldCallAsync: ⚠ could not pause capture — remote party may still hear the mic");

                var playbackSink = _audioDevices?.Render?.Sink;
                if (!TryApplyAudioSinkPause(playbackSink, hold))
                    AppLog.Log($"[SipService] HoldCallAsync: ⚠ could not pause/resume playback sink");

                // 3) If the library didn't provide a hold helper, RFC 3264 hold via re-INVITE:
                // sendonly = we stop sending RTP (remote should play MOH toward us). Resume with sendrecv.
                if (!invoked)
                {
                    var currentLocalSdp = TryGetActiveLocalSdp();
                    if (!string.IsNullOrWhiteSpace(currentLocalSdp))
                    {
                        string direction = hold ? "sendonly" : "sendrecv";
                        var updatedSdp = ApplyAudioDirectionToSdp(currentLocalSdp!, direction);
                        var reinviteOk = await TrySendReinviteAsync(updatedSdp);
                        AppLog.Log($"[SipService] HoldCallAsync: re-INVITE {(reinviteOk ? "sent" : "failed")} (direction={direction})");
                    }
                    else
                    {
                        AppLog.Log("[SipService] HoldCallAsync: Cannot send re-INVITE hold: local SDP not available.");
                    }
                }

                SetStatus(hold ? "Call on hold" : "Call resumed");
            }
            catch (Exception ex)
            {
                SetStatus($"Hold error: {ex.Message}");
            }
        }

        private string? TryGetActiveLocalSdp()
        {
            try
            {
                if (_userAgent == null) return null;

                // 1) Check common properties directly on UA.
                foreach (var propName in new[] { "LocalSDP", "LocalSdp", "LocalSdpString", "LocalDescription", "Sdp", "SDP" })
                {
                    var p = _userAgent.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p?.PropertyType == typeof(string))
                    {
                        var s = p.GetValue(_userAgent) as string;
                        if (LooksLikeSdp(s)) return s;
                    }
                }

                // 2) Try to access active dialogue from UA and read SDP from it.
                var dialogue = TryGetActiveDialogue(_userAgent);
                if (dialogue != null)
                {
                    foreach (var propName in new[] { "LocalSDP", "LocalSdp", "LocalDescription", "LocalSdpString", "Sdp", "SDP" })
                    {
                        var p = dialogue.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (p?.PropertyType == typeof(string))
                        {
                            var s = p.GetValue(dialogue) as string;
                            if (LooksLikeSdp(s)) return s;
                        }
                    }
                }

                // 3) As a last resort, scan all string properties/fields for something that looks like SDP.
                foreach (var p in _userAgent.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (p.PropertyType == typeof(string))
                    {
                        var s = p.GetValue(_userAgent) as string;
                        if (LooksLikeSdp(s)) return s;
                    }
                }
                foreach (var f in _userAgent.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType == typeof(string))
                    {
                        var s = f.GetValue(_userAgent) as string;
                        if (LooksLikeSdp(s)) return s;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] TryGetActiveLocalSdp error: {ex.Message}");
            }

            return null;
        }

        private static bool LooksLikeSdp(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            return s.Contains("v=0", StringComparison.Ordinal) && s.Contains("m=audio", StringComparison.OrdinalIgnoreCase);
        }

        private static object? TryGetActiveDialogue(object ua)
        {
            try
            {
                var p = ua.GetType().GetProperty("Dialogue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? ua.GetType().GetProperty("Dialog", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var d = p?.GetValue(ua);
                if (d != null && d.GetType().FullName?.Contains("SIPDialogue", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return d;
                }

                // Scan fields for SIPDialogue
                foreach (var f in ua.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var v = f.GetValue(ua);
                    if (v != null && v.GetType().FullName?.Contains("SIPDialogue", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return v;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string ApplyAudioDirectionToSdp(string sdp, string direction) // direction: sendrecv/recvonly/sendonly/inactive
        {
            // Only modify the m=audio section. Ensure exactly one direction attribute in that section.
            var lines = sdp.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            bool inAudio = false;
            bool changed = false;
            int audioStartIndex = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i].TrimEnd();
                if (line.StartsWith("m=", StringComparison.OrdinalIgnoreCase))
                {
                    inAudio = line.StartsWith("m=audio", StringComparison.OrdinalIgnoreCase);
                    if (inAudio) audioStartIndex = i;
                }

                if (!inAudio) continue;

                if (line.StartsWith("a=sendrecv", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("a=recvonly", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("a=sendonly", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("a=inactive", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = "a=" + direction;
                    changed = true;
                    // Remove any subsequent direction lines in the same audio section.
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        var l2 = lines[j].TrimEnd();
                        if (l2.StartsWith("m=", StringComparison.OrdinalIgnoreCase)) break;
                        if (l2.StartsWith("a=sendrecv", StringComparison.OrdinalIgnoreCase) ||
                            l2.StartsWith("a=recvonly", StringComparison.OrdinalIgnoreCase) ||
                            l2.StartsWith("a=sendonly", StringComparison.OrdinalIgnoreCase) ||
                            l2.StartsWith("a=inactive", StringComparison.OrdinalIgnoreCase))
                        {
                            lines[j] = "";
                        }
                    }
                    break;
                }
            }

            if (!changed && audioStartIndex >= 0)
            {
                // Insert direction line right after m=audio line (safe & widely accepted).
                lines.Insert(audioStartIndex + 1, "a=" + direction);
            }

            var normalized = string.Join("\r\n", lines.Where(l => !string.IsNullOrWhiteSpace(l))) + "\r\n";
            return normalized;
        }

        private async Task<bool> TrySendReinviteAsync(string sdp)
        {
            if (_userAgent == null) return false;
            object ua = _userAgent;
            object? dialogue = TryGetActiveDialogue(ua);

            // Try candidates on UA first, then on dialogue.
            foreach (var targetObj in new object?[] { ua, dialogue })
            {
                if (targetObj == null) continue;
                var target = targetObj;
                var methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var mi in methods)
                {
                    var name = mi.Name ?? "";
                    if (!name.Contains("reinvite", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("reInvite", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var ps = mi.GetParameters();
                    // We only support methods that can take SDP as a string.
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    {
                        try
                        {
                            var result = mi.Invoke(target, new object[] { sdp });
                            return await NormalizeInvokeResult(result).ConfigureAwait(false);
                        }
                        catch (Exception ex) { AppLog.Log($"[SipService] re-INVITE invoke failed ({name}): {ex.Message}"); }
                    }
                    else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && dialogue != null && ps[1].ParameterType.IsInstanceOfType(dialogue))
                    {
                        try
                        {
                            var result = mi.Invoke(target, new[] { (object)sdp, dialogue });
                            return await NormalizeInvokeResult(result).ConfigureAwait(false);
                        }
                        catch (Exception ex) { AppLog.Log($"[SipService] re-INVITE invoke failed ({name}): {ex.Message}"); }
                    }
                }
            }

            return false;
        }

        private static async Task<bool> NormalizeInvokeResult(object? result)
        {
            if (result is bool b) return b;
            if (result is Task t)
            {
                await t.ConfigureAwait(false);
                var type = t.GetType();
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var resProp = type.GetProperty("Result");
                    var resVal = resProp?.GetValue(t);
                    if (resVal is bool tb) return tb;
                }
                return true;
            }
            return result != null;
        }

        /// <summary>
        /// Изменяет аудио устройства. Настройки будут применены при следующем звонке.
        /// </summary>
        public void ChangeAudioDevices(int? microphoneDeviceNumber, int? speakerDeviceNumber)
        {
            _microphoneDeviceNumber = microphoneDeviceNumber;
            _speakerDeviceNumber = speakerDeviceNumber;
            
            // Примечание: WindowsAudioEndPoint не поддерживает изменение устройств во время активного звонка
            // Настройки будут применены при следующем звонке или переподключении
            if (IsInCall)
            {
                SetStatus("Audio device settings saved. Changes will be applied on next call.");
            }
            else
            {
                SetStatus("Audio device settings saved. Will be applied on next call.");
            }
        }

        private void SetStatus(string message)
        {
            OnStatusChanged?.Invoke(message);
        }

        /// <summary>
        /// Extracts the user part from a P-Asserted-Identity SIP header value.
        /// Handles formats like: "name" &lt;sip:number@domain&gt; and &lt;sip:number@domain&gt;
        /// </summary>
        private static string? ParsePaiUser(string? paiValue)
        {
            if (string.IsNullOrWhiteSpace(paiValue)) return null;
            // Extract URI between angle brackets: <sip:user@domain>
            int lt = paiValue.IndexOf('<');
            int gt = paiValue.IndexOf('>');
            string uri = (lt >= 0 && gt > lt) ? paiValue.Substring(lt + 1, gt - lt - 1) : paiValue.Trim();
            // Strip "sip:" or "sips:" prefix
            if (uri.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
                uri = uri.Substring(4);
            else if (uri.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
                uri = uri.Substring(5);
            // Take user part before @
            int at = uri.IndexOf('@');
            return at > 0 ? uri.Substring(0, at) : uri;
        }

        /// <summary>
        /// Tries to extract P-Asserted-Identity from a SIP response and fire the event.
        /// </summary>
        private void TryExtractOutboundCallerId(SIPSorcery.SIP.SIPResponse response)
        {
            try
            {
                string? pai = response.Header?.GetUnknownHeaderValue("P-Asserted-Identity");
                if (string.IsNullOrEmpty(pai))
                {
                    // Fallback: scan UnknownHeaders list (some SIPSorcery versions store with prefix)
                    var unknownHeaders = response.Header?.UnknownHeaders;
                    if (unknownHeaders != null)
                    {
                        foreach (var h in unknownHeaders)
                        {
                            if (h != null && h.StartsWith("P-Asserted-Identity", StringComparison.OrdinalIgnoreCase))
                            {
                                int colon = h.IndexOf(':');
                                if (colon > 0) pai = h.Substring(colon + 1).Trim();
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(pai)) return;

                string? callerId = ParsePaiUser(pai);
                if (!string.IsNullOrEmpty(callerId) && callerId != OutboundCallerId)
                {
                    OutboundCallerId = callerId;
                    string connLabel = _isSecondaryConnection ? "[Connection2] " : "";
                    AppLog.Log($"[SipService] {connLabel}P-Asserted-Identity detected: {callerId} (raw: {pai})");
                    OnOutboundCallerIdReceived?.Invoke(callerId);
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Error parsing P-Asserted-Identity: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Получает внешний (публичный) IP адрес через STUN для использования в SDP
        /// </summary>
        private async Task<string?> GetPublicIPAddressViaStunAsync()
        {
            try
            {
                // Используем публичный STUN сервер Google
                string stunServer = "stun.l.google.com";
                int stunPort = 19302;
                
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Querying STUN server {stunServer}:{stunPort} for public IP...");
                
                // Простой STUN запрос для получения mapped address
                using (var udpClient = new System.Net.Sockets.UdpClient())
                {
                    udpClient.Client.ReceiveTimeout = 3000; // 3 секунды таймаут
                    
                    // STUN Binding Request (RFC 5389)
                    byte[] stunRequest = new byte[]
                    {
                        0x00, 0x01, // Message Type: Binding Request
                        0x00, 0x00, // Message Length
                        0x21, 0x12, 0xA4, 0x42, // Magic Cookie
                        0x00, 0x00, 0x00, 0x00, // Transaction ID (часть 1)
                        0x00, 0x00, 0x00, 0x00, // Transaction ID (часть 2)
                        0x00, 0x00, 0x00, 0x00  // Transaction ID (часть 3)
                    };
                    
                    // Генерируем случайный Transaction ID
                    var random = new Random();
                    byte[] transactionId = new byte[12];
                    random.NextBytes(transactionId);
                    Array.Copy(transactionId, 0, stunRequest, 8, 12);
                    
                    await udpClient.SendAsync(stunRequest, stunRequest.Length, stunServer, stunPort);
                    
                    var result = await udpClient.ReceiveAsync();
                    var response = result.Buffer;
                    
                    if (response.Length >= 20)
                    {
                        // Проверяем, что это STUN Binding Response
                        if (response[0] == 0x01 && response[1] == 0x01)
                        {
                            // Парсим MAPPED-ADDRESS или XOR-MAPPED-ADDRESS
                            int offset = 20; // После заголовка
                            while (offset < response.Length - 4)
                            {
                                ushort attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
                                ushort attrLength = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
                                
                                if (attrType == 0x0001) // MAPPED-ADDRESS
                                {
                                    if (offset + 4 + attrLength <= response.Length)
                                    {
                                        byte family = response[offset + 5];
                                        if (family == 0x01) // IPv4
                                        {
                                            ushort port = (ushort)((response[offset + 6] << 8) | response[offset + 7]);
                                            byte ip1 = response[offset + 8];
                                            byte ip2 = response[offset + 9];
                                            byte ip3 = response[offset + 10];
                                            byte ip4 = response[offset + 11];
                                            
                                            string publicIP = $"{ip1}.{ip2}.{ip3}.{ip4}";
                                            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}✅ STUN public IP: {publicIP}");
                                            return publicIP;
                                        }
                                    }
                                }
                                else if (attrType == 0x0020) // XOR-MAPPED-ADDRESS
                                {
                                    if (offset + 4 + attrLength <= response.Length)
                                    {
                                        byte family = response[offset + 5];
                                        if (family == 0x01) // IPv4
                                        {
                                            ushort port = (ushort)((response[offset + 6] << 8) | response[offset + 7]);
                                            // XOR с Magic Cookie
                                            byte ip1 = (byte)(response[offset + 8] ^ 0x21);
                                            byte ip2 = (byte)(response[offset + 9] ^ 0x12);
                                            byte ip3 = (byte)(response[offset + 10] ^ 0xA4);
                                            byte ip4 = (byte)(response[offset + 11] ^ 0x42);
                                            
                                            string publicIP = $"{ip1}.{ip2}.{ip3}.{ip4}";
                                            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}✅ STUN public IP (XOR): {publicIP}");
                                            return publicIP;
                                        }
                                    }
                                }
                                
                                offset += 4 + attrLength;
                                if (attrLength % 4 != 0)
                                    offset += 4 - (attrLength % 4); // Padding
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ STUN query failed: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Определяет тип NAT через STUN (RFC 5389)
        /// Типы NAT:
        /// - Full Cone NAT: один внешний IP:порт для всех запросов
        /// - Restricted Cone NAT: один внешний IP:порт, но только для известных IP
        /// - Port Restricted Cone NAT: один внешний IP:порт, проверяет IP и порт
        /// - Symmetric NAT: разные порты для разных запросов (самый сложный, нужен TURN)
        /// </summary>
        public async Task<string> DetectNatTypeAsync()
        {
            try
            {
                string stunServer = "stun.l.google.com";
                int stunPort = 19302;
                
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Detecting NAT type using STUN server {stunServer}:{stunPort}...");
                
                // Делаем первый STUN запрос
                (string? ip1, ushort? port1) = await GetStunMappedAddressAsync(stunServer, stunPort);
                if (ip1 == null || port1 == null)
                {
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ Failed to get first STUN response");
                    return "Unknown (STUN failed)";
                }
                
                // Ждем немного, чтобы порт мог измениться
                await Task.Delay(100);
                
                // Делаем второй STUN запрос с другого локального порта
                (string? ip2, ushort? port2) = await GetStunMappedAddressAsync(stunServer, stunPort);
                if (ip2 == null || port2 == null)
                {
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ Failed to get second STUN response");
                    return "Unknown (STUN failed)";
                }
                
                // Сравниваем результаты
                if (ip1 != ip2)
                {
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ Different public IPs detected: {ip1} vs {ip2} - unusual NAT behavior");
                    return "Unknown (Multiple IPs)";
                }
                
                if (port1 == port2)
                {
                    // Одинаковые порты - это Cone NAT (Full/Restricted/Port Restricted)
                    // Для точного определения нужны дополнительные тесты, но для SIP это достаточно
                    _detectedNatType = "Cone NAT";
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}✅ NAT Type: {_detectedNatType} (IP: {ip1}, Port: {port1} - same for both requests)");
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}ℹ️ {_detectedNatType} should work with local IP in SDP");
                    return "Cone NAT (should work with local IP)";
                }
                else
                {
                    // Разные порты - это Symmetric NAT
                    _detectedNatType = "Symmetric NAT";
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ NAT Type: {_detectedNatType} (IP: {ip1}, Ports: {port1} vs {port2} - different!)");
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ {_detectedNatType} requires TURN server or port forwarding for RTP");
                    return "Symmetric NAT (needs TURN or port forwarding)";
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ NAT type detection failed: {ex.Message}");
                return $"Unknown (Error: {ex.Message})";
            }
        }
        
        /// <summary>
        /// Выполняет STUN запрос и возвращает mapped address (IP и порт)
        /// </summary>
        private async Task<(string? ip, ushort? port)> GetStunMappedAddressAsync(string stunServer, int stunPort)
        {
            try
            {
                using (var udpClient = new System.Net.Sockets.UdpClient())
                {
                    udpClient.Client.ReceiveTimeout = 3000;
                    
                    // STUN Binding Request
                    byte[] stunRequest = new byte[]
                    {
                        0x00, 0x01, // Message Type: Binding Request
                        0x00, 0x00, // Message Length
                        0x21, 0x12, 0xA4, 0x42, // Magic Cookie
                        0x00, 0x00, 0x00, 0x00, // Transaction ID (часть 1)
                        0x00, 0x00, 0x00, 0x00, // Transaction ID (часть 2)
                        0x00, 0x00, 0x00, 0x00  // Transaction ID (часть 3)
                    };
                    
                    var random = new Random();
                    byte[] transactionId = new byte[12];
                    random.NextBytes(transactionId);
                    Array.Copy(transactionId, 0, stunRequest, 8, 12);
                    
                    await udpClient.SendAsync(stunRequest, stunRequest.Length, stunServer, stunPort);
                    var result = await udpClient.ReceiveAsync();
                    var response = result.Buffer;
                    
                    if (response.Length >= 20 && response[0] == 0x01 && response[1] == 0x01)
                    {
                        int offset = 20;
                        while (offset < response.Length - 4)
                        {
                            ushort attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
                            ushort attrLength = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
                            
                            if (attrType == 0x0020) // XOR-MAPPED-ADDRESS (предпочтительно)
                            {
                                if (offset + 4 + attrLength <= response.Length)
                                {
                                    byte family = response[offset + 5];
                                    if (family == 0x01) // IPv4
                                    {
                                        ushort port = (ushort)((response[offset + 6] << 8) | response[offset + 7]);
                                        port ^= 0x2112; // XOR с первыми 2 байтами Magic Cookie
                                        
                                        byte ip1 = (byte)(response[offset + 8] ^ 0x21);
                                        byte ip2 = (byte)(response[offset + 9] ^ 0x12);
                                        byte ip3 = (byte)(response[offset + 10] ^ 0xA4);
                                        byte ip4 = (byte)(response[offset + 11] ^ 0x42);
                                        
                                        string ip = $"{ip1}.{ip2}.{ip3}.{ip4}";
                                        return (ip, port);
                                    }
                                }
                            }
                            else if (attrType == 0x0001) // MAPPED-ADDRESS (fallback)
                            {
                                if (offset + 4 + attrLength <= response.Length)
                                {
                                    byte family = response[offset + 5];
                                    if (family == 0x01) // IPv4
                                    {
                                        ushort port = (ushort)((response[offset + 6] << 8) | response[offset + 7]);
                                        byte ip1 = response[offset + 8];
                                        byte ip2 = response[offset + 9];
                                        byte ip3 = response[offset + 10];
                                        byte ip4 = response[offset + 11];
                                        
                                        string ip = $"{ip1}.{ip2}.{ip3}.{ip4}";
                                        return (ip, port);
                                    }
                                }
                            }
                            
                            offset += 4 + attrLength;
                            if (attrLength % 4 != 0)
                                offset += 4 - (attrLength % 4);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ STUN query failed: {ex.Message}");
            }
            
            return (null, null);
        }
        
        /// <summary>
        /// Получает кэшированный внешний IP адрес или обновляет его, если кэш устарел
        /// </summary>
        private string? GetCachedPublicIP()
        {
            // Если кэш устарел или пуст, пытаемся обновить (синхронно, но быстро)
            if (_publicIPAddress == null || DateTime.UtcNow - _publicIPLastUpdate > _publicIPCacheTimeout)
            {
                // Пытаемся получить новый IP (не блокируем, используем кэш если есть)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string? publicIP = await GetPublicIPAddressViaStunAsync();
                        if (!string.IsNullOrEmpty(publicIP))
                        {
                            _publicIPAddress = publicIP;
                            _publicIPLastUpdate = DateTime.UtcNow;
                        }
                    }
                    catch { }
                });
                
                // Возвращаем старый кэш, если он есть (даже если устарел)
                return _publicIPAddress;
            }
            
            return _publicIPAddress;
        }
        
        /// <summary>
        /// Получает локальный IP адрес для использования в Contact URI
        /// Для второго подключения по умолчанию игнорирует VPN (обратная совместимость)
        /// Результат кэшируется после первого вызова
        /// </summary>
        private string GetLocalIPAddress()
        {
            // Используем кэшированное значение, если оно уже было вычислено
            if (!string.IsNullOrEmpty(_cachedLocalIPAddress))
            {
                return _cachedLocalIPAddress;
            }
            
            // Для второго подключения по умолчанию игнорируем VPN (обратная совместимость)
            bool shouldIgnoreVpn = _isSecondaryConnection;
            
            // Если не нужно игнорировать VPN, используем стандартную логику
            if (!shouldIgnoreVpn)
            {
                try
                {
                    // Пробуем получить IP адрес, который используется для подключения к серверу
                    using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, 
                        System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp))
                    {
                        socket.Connect(_server, _port);
                        var localEndPoint = socket.LocalEndPoint as IPEndPoint;
                    if (localEndPoint != null)
                    {
                        _cachedLocalIPAddress = localEndPoint.Address.ToString();
                        return _cachedLocalIPAddress;
                    }
                    }
                }
                catch
                {
                    // Если не удалось, используем первый доступный IPv4 адрес
                }
                
                // Fallback: используем первый IPv4 адрес
                try
                {
                    var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                    foreach (var ip in host.AddressList)
                    {
                        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            _cachedLocalIPAddress = ip.ToString();
                            return _cachedLocalIPAddress;
                        }
                    }
                }
                catch
                {
                    // Если ничего не получилось, используем localhost
                }
                
                _cachedLocalIPAddress = "127.0.0.1";
                return _cachedLocalIPAddress;
            }
            
            // Игнорируем VPN интерфейсы (если настройка включена или это второе подключение)
            string connectionLabel = _isSecondaryConnection ? "[Connection2]" : "";
            try
            {
                // Пробуем получить IP адрес, который используется для подключения к серверу
                using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, 
                    System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp))
                {
                    socket.Connect(_server, _port);
                    var localEndPoint = socket.LocalEndPoint as IPEndPoint;
                    if (localEndPoint != null)
                    {
                        var selectedIP = localEndPoint.Address;
                        // Проверяем, не является ли это VPN или виртуальным интерфейсом
                        if (!IsVpnOrVirtualInterface(selectedIP))
                        {
                            AppLog.Log($"[SipService] {connectionLabel} Selected IP from socket connection: {selectedIP}");
                            _cachedLocalIPAddress = selectedIP.ToString();
                            return _cachedLocalIPAddress;
                        }
                        else
                        {
                            AppLog.Log($"[SipService] {connectionLabel} Socket connection returned VPN/virtual IP {selectedIP}, trying to find real interface...");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] {connectionLabel} Error getting IP from socket connection: {ex.Message}");
            }
            
            // Fallback: используем первый реальный IPv4 адрес (не VPN, не виртуальный, не loopback)
            try
            {
                var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                var candidateIPs = new List<IPAddress>();
                
                foreach (var ni in networkInterfaces)
                {
                    // Пропускаем неактивные интерфейсы
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    
                    // Пропускаем loopback интерфейсы
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;
                    
                    // Пропускаем туннельные интерфейсы (VPN)
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                        continue;
                    
                    // Пропускаем PPP интерфейсы (могут быть VPN)
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ppp)
                        continue;
                    
                    var properties = ni.GetIPProperties();
                    foreach (var addr in properties.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            var ip = addr.Address;
                            
                            // Пропускаем loopback
                            if (IPAddress.IsLoopback(ip))
                                continue;
                            
                            // Пропускаем VPN диапазоны
                            if (IsVpnOrVirtualInterface(ip))
                                continue;
                            
                            candidateIPs.Add(ip);
                            AppLog.Log($"[SipService] {connectionLabel} Found candidate IP: {ip} (interface: {ni.Name}, type: {ni.NetworkInterfaceType})");
                        }
                    }
                }
                
                // Выбираем первый подходящий IP
                if (candidateIPs.Count > 0)
                {
                    var selectedIP = candidateIPs[0];
                    AppLog.Log($"[SipService] {connectionLabel} Selected IP from network interfaces: {selectedIP}");
                    _cachedLocalIPAddress = selectedIP.ToString();
                    return _cachedLocalIPAddress;
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] {connectionLabel} Error getting IP from network interfaces: {ex.Message}");
            }
            
            // Последний fallback: используем первый IPv4 адрес из DNS (без VPN)
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        // Пропускаем loopback и VPN
                        if (!IPAddress.IsLoopback(ip) && !IsVpnOrVirtualInterface(ip))
                        {
                            AppLog.Log($"[SipService] {connectionLabel} Selected IP from DNS: {ip}");
                            _cachedLocalIPAddress = ip.ToString();
                            return _cachedLocalIPAddress;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] {connectionLabel} Error getting IP from DNS: {ex.Message}");
            }
            
            AppLog.Log($"[SipService] {connectionLabel} WARNING: Could not find suitable IP (VPN filtered), using 127.0.0.1");
            _cachedLocalIPAddress = "127.0.0.1";
            return _cachedLocalIPAddress;
        }
        
        /// <summary>
        /// Проверяет, является ли IP адрес VPN или виртуальным интерфейсом
        /// </summary>
        private bool IsVpnOrVirtualInterface(IPAddress ip)
        {
            if (ip == null)
                return true;

            if (!IsNonRoutableVpnOrVirtualIp(ip))
                return false;

            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4 && bytes[0] == 198 && bytes[1] >= 18 && bytes[1] <= 19)
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} IP {ip} detected as VPN (198.18.0.0/15 range)");
            return true;
        }

        /// <summary>
        /// IP to advertise in SDP c= line. Prefer STUN public IP when the local address is VPN/virtual
        /// or symmetric NAT was detected — otherwise the PBX sends RTP to an unreachable address (e.g. 198.18.x.x).
        /// </summary>
        private string GetSdpAdvertiseAddress(string localIP)
        {
            string? publicIP = null;
            try { publicIP = GetCachedPublicIP(); } catch { }

            if (string.IsNullOrWhiteSpace(publicIP))
                return localIP;

            if (_isSecondaryConnection)
                return publicIP;

            if (IPAddress.TryParse(localIP, out var lip) && IsVpnOrVirtualInterface(lip))
                return publicIP;

            if (!string.IsNullOrEmpty(_detectedNatType)
                && _detectedNatType.Contains("Symmetric", StringComparison.OrdinalIgnoreCase))
                return publicIP;

            return localIP;
        }

        /// <summary>
        /// Closes VoIPMediaSession and disposes audio devices. Required after cancel/BYE paths that
        /// only called Close() without nulling — otherwise the next call reuses closed WASAPI endpoints.
        /// </summary>
        private void ReleaseMediaSessionAndAudio(string reason)
        {
            try
            {
                _voipMediaSession?.Close(reason);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Warning closing media session ({reason}): {ex.Message}");
            }

            _voipMediaSession = null;
            _filteringAudioSink = null;
            _activeAudioSource = null;

            try { _audioDevices?.Dispose(); } catch { }
            _audioDevices = null;
        }

        private void InitializeAudio(bool throwOnError = false, bool force = false)
        {
            // Если установлен флаг пропуска инициализации (для WebRTC режима), не инициализируем аудио
            // Но если force=true, игнорируем флаг (нужно для входящих звонков)
            if (_skipAudioInitialization && !force)
            {
                SetStatus("Audio initialization skipped (WebRTC mode)");
                return;
            }
            
            // Если аудио уже инициализировано и сессия существует - не переинициализируем
            if (_audioDevices != null && _voipMediaSession != null && !force)
            {
                return;
            }
            
            // НЕ пытаемся переиспользовать аудио-устройства!
            // VoIPMediaSession.Close() вызывает CloseAudio()/CloseAudioSink(), 
            // что навсегда отключает обработчик DataAvailable и ставит _isAudioSourceClosed=true.
            // Всегда создаем новую пару устройств.

            ReleaseMediaSessionAndAudio("reinitialize");

            string connLabel = _isSecondaryConnection ? "[Connection2] " : "";

            try
            {
                SetStatus("Initializing audio...");
                
                var audioEncoder = new AudioEncoder();
                _audioEncoder = audioEncoder;

                var deviceFactory = _audioDeviceFactory ?? AudioDeviceFactory.Default
                    ?? throw new InvalidOperationException("No IAudioDeviceFactory available (platform head must set AudioDeviceFactory.Default at startup)");

                // Платформенная фабрика выбирает бэкенд (Windows: WASAPI → PortAudio; macOS/Linux: PortAudio).
                // Prioritize PCMA (A-law, PT=8): VoIPMediaSession picks the FIRST codec from our
                // list, PCMA is the international standard, PCMU stays as fallback in the offer.
                _audioDevices = deviceFactory.Create(new AudioDeviceOptions
                {
                    CaptureDeviceIndex = _microphoneDeviceNumber ?? -1,
                    RenderDeviceIndex = _speakerDeviceNumber ?? -1,
                    EnableEchoCancellation = EnableAec,
                    PreferredPayloadFormatId = 8
                }, audioEncoder);

                // Tap raw 8 kHz PCM BEFORE codec encoding for call recording.
                // This gives full 16-bit quality without A-law quantisation loss.
                _audioDevices.Capture.OnRawPcmFrameTap = (pcm8k) =>
                {
                    try
                    {
                        RecordOutboundRawPcm(pcm8k);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log($"[SipService] {connLabel}Error in OnRawPcmFrameTap: {ex.Message}");
                    }
                };

                var mediaEndPoints = _audioDevices.ToMediaEndPoints();

                // Wrap AudioSink: FilteringAudioSink (только для бэкендов без собственной фильтрации,
                // например WinMM) + RecordingAudioSink for inbound RTP recording.
                if (mediaEndPoints?.AudioSink != null)
                {
                    IAudioSink sink = mediaEndPoints.AudioSink;
                    if (_audioDevices.RequiresInboundFiltering)
                    {
                        _filteringAudioSink = new FilteringAudioSink(sink, connLabel);
                        sink = _filteringAudioSink;
                    }
                    mediaEndPoints.AudioSink = new RecordingAudioSink(sink, this);
                }

                AppLog.Log($"[SipService] {connLabel}✓ Using audio backend: {_audioDevices.Description}");
                
                if (mediaEndPoints == null) throw new InvalidOperationException("No media endpoints available");

                _activeAudioSource = mediaEndPoints.AudioSource;
                
                // Определяем локальный IP для RTP
                // КРИТИЧНО: Для NAT traversal используем IPAddress.Any (0.0.0.0), чтобы RTP сокет
                // слушал на всех интерфейсах. Это позволяет принимать RTP пакеты, которые NAT
                // перенаправляет с внешнего IP на локальный интерфейс.
                // В SDP мы укажем внешний IP (полученный через STUN), но сокет слушает на всех интерфейсах.
                IPAddress? rtpBindAddress = IPAddress.Any; // 0.0.0.0 - слушать на всех интерфейсах
                
                try
                {
                    string localIpStr = GetLocalIPAddress();
                    AppLog.Log($"[SipService] {connLabel}Local IP: {localIpStr}, RTP bind: {rtpBindAddress} (Any - for NAT traversal)");
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[SipService] {connLabel}Error getting local IP: {ex.Message}");
                }
                
                if (_useSrtp)
                {
                    // Native SDES-SRTP in SIPSorcery 6.2.0 is enabled via RtpSecureMediaOptionEnum.SdpCryptoNegotiation.
                    // This ensures the offer SDP uses RTP/SAVP and contains a=crypto lines.
                    var cfg = new VoIPMediaSessionConfig
                    {
                        MediaEndPoint = mediaEndPoints,
                        BindAddress = rtpBindAddress,
                        BindPort = 0,
                        RtpSecureMediaOption = RtpSecureMediaOptionEnum.SdpCryptoNegotiation
                    };

                    _voipMediaSession = new VoIPMediaSession(cfg)
                    {
                        AcceptRtpFromAny = true
                    };

                    // Prefer the common SDES suite used by Asterisk when media_encryption=sdes.
                    // If multiple suites are supported, SIPSorcery will negotiate based on intersection.
                    try
                    {
                        _voipMediaSession.SrtpCryptoSuites = new List<SDPSecurityDescription.CryptoSuites>
                        {
                            SDPSecurityDescription.CryptoSuites.AES_CM_128_HMAC_SHA1_80
                        };
                    }
                    catch { }
                }
                else
                {
                    _voipMediaSession = new VoIPMediaSession(mediaEndPoints, bindAddress: rtpBindAddress)
                    {
                        AcceptRtpFromAny = true
                    };
                }
                
                string mode = _audioDevices?.Description ?? "unknown";
                var sipAsm = typeof(VoIPMediaSession).Assembly.GetName();
                AppLog.Log($"[SipService] {connLabel}Audio chain: {mode} → VoIPMediaSession (SIPSorcery {sipAsm.Version}, RTP bind={rtpBindAddress})");
                
                // Информация о NAT traversal
                string? publicIP = GetCachedPublicIP();
                if (!string.IsNullOrEmpty(publicIP))
                {
                    AppLog.Log($"[SipService] {connLabel}✅ NAT traversal: RTP listening on all interfaces (0.0.0.0), public IP {publicIP} (STUN) will be used in SDP");
                }
                else
                {
                    AppLog.Log($"[SipService] {connLabel}⚠️ NAT traversal: RTP listening on all interfaces, but public IP not yet determined (STUN in progress)");
                }
                
                IsMuted = false;

                string deviceInfo = (_microphoneDeviceNumber.HasValue || _speakerDeviceNumber.HasValue) ? " (custom devices)" : "";
                SetStatus($"Audio initialized successfully (Native SIPSorcery Mode){deviceInfo}");
            }
            catch (Exception ex)
            {
                string errorMessage = $"Audio initialization error: {ex.Message}";
                if (ex.InnerException != null)
                {
                    errorMessage += $" ({ex.InnerException.Message})";
                }
                errorMessage += ". Please check your audio devices are connected and not being used by another application.";

                AppLog.Log($"[SipService] {connLabel}Audio initialization failed: {ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException != null)
                    AppLog.Log($"[SipService] {connLabel}  Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                
                SetStatus(errorMessage);
                
                // Продолжаем без аудио для тестирования регистрации
                try { _audioDevices?.Dispose(); } catch { }
                _audioDevices = null;
                _voipMediaSession = null;
                _filteringAudioSink = null;
                
                // Если требуется пробросить исключение (при звонке), делаем это
                if (throwOnError)
                {
                    throw new InvalidOperationException($"Audio service not initialized. {ex.Message}. Please check your audio devices and try again.", ex);
                }
            }
        }

        /// <summary>
        /// ������������� SIP ����������, ����� � ����������� �� �������.
        /// </summary>
        public async Task StartAsync()
        {
            SetStatus($"StartAsync on service: {_instanceHash}");
            if (_sipTransport != null)
            {
                // Уже запущен.
                return;
            }

            SetStatus("Initializing SIP...");

            // 1. SIP ���������.
            _sipTransport = new SIPTransport();

            // Используем IPAddress.Any для работы в любой сети
            string localIPStr = GetLocalIPAddress();
            IPAddress localIPAddr = IPAddress.Any;
            
            var connectionLabel = _isSecondaryConnection ? "[Connection2]" : "";
            AppLog.Log($"[SipService] {connectionLabel} Binding {(_useTls ? "TLS" : "UDP")} channel to all interfaces (IPAddress.Any)");

            // Channel on all interfaces (ephemeral port). Actual transport depends on settings (UDP/TLS).
            var listenEndPoint = new IPEndPoint(localIPAddr, 0);
            int actualPort;
            if (_useTls)
            {
                // NOTE: SIPSorcery uses OS TLS stack. We rely on the library defaults for certificates/validation.
                // If your PBX requires client certs or custom validation, we can extend this later.
                var tlsChannel = new SIPTLSChannel(listenEndPoint);
                _sipTransport.AddSIPChannel(tlsChannel);
                actualPort = tlsChannel.ListeningEndPoint.Port;

                // SIPSorcery may create a default UDP channel inside SIPTransport().
                // If TLS is enabled, make sure UDP channels are removed so REGISTER can't silently fall back to UDP.
                RemoveUdpChannelsFromTransport(_sipTransport);

                try
                {
                    // Log active channels/protocols for debugging.
                    var chans = _sipTransport.GetSIPChannels();
                    foreach (var ch in chans)
                    {
                        try
                        {
                            AppLog.Log($"[SipService] {connectionLabel} Active SIP channel: {ch.GetType().Name} {ch.ListeningEndPoint}");
                        }
                        catch { }
                    }
                }
                catch { }
            }
            else
            {
                var udpChannel = new SIPUDPChannel(listenEndPoint);
                _sipTransport.AddSIPChannel(udpChannel);
                actualPort = udpChannel.ListeningEndPoint.Port;
            }
            
            SetStatus($"SIP transport listening on {localIPStr}:{actualPort}");

            RefreshResolvedServerIp();
            
            // Получаем внешний IP через STUN для NAT traversal (асинхронно, не блокируем инициализацию)
            _ = Task.Run(async () =>
            {
                try
                {
                    string? publicIP = await GetPublicIPAddressViaStunAsync();
                    if (!string.IsNullOrEmpty(publicIP))
                    {
                        _publicIPAddress = publicIP;
                        _publicIPLastUpdate = DateTime.UtcNow;
                        AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}✅ Public IP cached: {publicIP}");
                    }
                    
                    // Определяем тип NAT после получения публичного IP
                    string natType = await DetectNatTypeAsync();
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}📊 NAT Type Detection Result: {natType}");
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}⚠️ Failed to get public IP via STUN: {ex.Message}");
                }
            });
            
            // Добавляем обработчик исходящих SIP запросов для диагностики и модификации SDP
            _sipTransport.SIPRequestOutTraceEvent += (localEndPoint, remoteEndPoint, request) =>
            {
                try
                {
                    RememberKnownGoodServerIp(remoteEndPoint);

                    if (request != null && (request.Method == SIPMethodsEnum.INVITE || request.Method == SIPMethodsEnum.REGISTER))
                    {
                        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                        var connLabel = _isSecondaryConnection ? "[Connection2]" : "";
                        var callId = request.Header?.CallId ?? "unknown";
                        string cseqNo = "?";
                        try
                        {
                            // Be resilient across SIPSorcery versions: CSeq can be a struct/class or an int.
                            var cseqObj = request.Header?.CSeq;
                            if (cseqObj != null)
                            {
                                var cseqType = cseqObj.GetType();
                                if (cseqType == typeof(int))
                                {
                                    cseqNo = cseqObj.ToString() ?? "?";
                                }
                                else
                                {
                                    var noProp = cseqType.GetProperty("CSeqNumber");
                                    var noVal = noProp?.GetValue(cseqObj);
                                    if (noVal != null) cseqNo = noVal.ToString() ?? "?";
                                }
                            }
                        }
                        catch { }
                        AppLog.Log($"[SipService] {connLabel}[{timestamp}] SIP OUT {request.Method}: Call-ID={callId}, CSeq={cseqNo}, local={localEndPoint} remote={remoteEndPoint} useTls={_useTls} reqUri='{request.URI}'");
                        if (request.Method == SIPMethodsEnum.REGISTER)
                        {
                            try
                            {
                                var to = request.Header?.To?.ToString();
                                var from = request.Header?.From?.ToString();
                                var contact = request.Header?.Contact?.FirstOrDefault()?.ToString();
                                AppLog.Log($"[SipService] {connLabel}[{timestamp}] REGISTER hdr: To={to}, From={from}, Contact={contact}");
                            }
                            catch { }

                            try
                            {
                                // Helpful when debugging 403 without 401 challenge.
                                var txt = request.ToString();
                                if (!string.IsNullOrWhiteSpace(txt))
                                {
                                    var preview = txt.Length > 1200 ? txt.Substring(0, 1200) + "..." : txt;
                                    AppLog.Log($"[SipService] {connLabel}[{timestamp}] REGISTER preview: {preview}");
                                }
                            }
                            catch { }
                        }
                        if (request.Method == SIPMethodsEnum.INVITE)
                        {
                            SetStatus($"[{timestamp}] SIP Request OUT: INVITE to {remoteEndPoint}");
                            try
                            {
                                var inviteCallId = request.Header?.CallId;
                                if (!string.IsNullOrWhiteSpace(inviteCallId))
                                {
                                    lock (_outgoingInviteLock)
                                    {
                                        if (_pendingOutgoingInviteFinalResponseTcs != null)
                                        {
                                            if (!_outgoingInviteFinalByCallId.ContainsKey(inviteCallId))
                                                _outgoingInviteFinalByCallId[inviteCallId] = _pendingOutgoingInviteFinalResponseTcs;
                                        }

                                        // Complete "call-id observed" latch for the current attempt.
                                        _currentOutgoingCallId = inviteCallId;
                                        try { _pendingOutgoingInviteCallIdTcs?.TrySetResult(inviteCallId); } catch { }
                                    }
                                }
                            }
                            catch { }

                            // High-signal INVITE debugging (helps detect "mystery second calls").
                            try
                            {
                                string? attempt, dial;
                                lock (_outgoingInviteLock)
                                {
                                    attempt = _currentOutgoingAttemptId;
                                    dial = _currentOutgoingDialNumber;
                                }
                                var to = request.Header?.To?.ToString();
                                var from = request.Header?.From?.ToString();
                                AppLog.Log($"[SipService] {connLabel}[{timestamp}] INVITE meta: attempt={attempt ?? "<none>"}, dial='{dial ?? "<unknown>"}', From={from}, To={to}");
                            }
                            catch { }
                        }
                        
                        // Модифицируем SDP: заменяем локальный IP на внешний (для NAT traversal)
                        try
                        {
                            string? sdpBody = request.Body?.ToString();
                            
                            if (!string.IsNullOrEmpty(sdpBody) && sdpBody.Contains("v=0") && sdpBody.Contains("m=audio"))
                            {
                                // SRTP (SDES) is handled natively by SIPSorcery when VoIPMediaSession is
                                // created with RtpSecureMediaOptionEnum.SdpCryptoNegotiation.
                                // Do not patch SDP crypto lines here.

                                // Заменяем 0.0.0.0 на локальный IP в SDP для NAT traversal
                                // Это работает, если NAT не симметричный (Cone NAT) - NAT автоматически пробросит порт
                                // Если RTP пакеты не приходят, возможные причины:
                                // 1. Симметричный NAT - нужен TURN сервер или port forwarding
                                // 2. Firewall блокирует входящие UDP пакеты
                                // 3. Провайдер не может достучаться до локального IP
                                string localIP = GetLocalIPAddress();
                                string advertiseIP = GetSdpAdvertiseAddress(localIP);
                                
                                // Заменяем 0.0.0.0 на advertise IP в SDP
                                if (sdpBody.Contains("c=IN IP4 0.0.0.0"))
                                {
                                    string modifiedSdp = sdpBody.Replace("c=IN IP4 0.0.0.0", $"c=IN IP4 {advertiseIP}");

                                    request.Body = modifiedSdp;
                                    // IMPORTANT: We mutate the SIP request after it has been built.
                                    // Ensure Content-Length stays in sync, otherwise proxies like Kamailio reject it.
                                    try
                                    {
                                        if (request.Header != null)
                                        {
                                            request.Header.ContentType = "application/sdp";
                                            request.Header.ContentLength = Encoding.UTF8.GetByteCount(modifiedSdp);
                                        }
                                    }
                                    catch { }
                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}✅ SDP c= set to {advertiseIP} (local={localIP})");
                                }
                                else if (!string.Equals(advertiseIP, localIP, StringComparison.Ordinal)
                                         && sdpBody.Contains($"c=IN IP4 {localIP}"))
                                {
                                    string modifiedSdp = sdpBody.Replace($"c=IN IP4 {localIP}", $"c=IN IP4 {advertiseIP}");
                                    request.Body = modifiedSdp;
                                    try
                                    {
                                        if (request.Header != null)
                                        {
                                            request.Header.ContentType = "application/sdp";
                                            request.Header.ContentLength = Encoding.UTF8.GetByteCount(modifiedSdp);
                                        }
                                    }
                                    catch { }
                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}✅ SDP c= rewritten to {advertiseIP} (was {localIP})");
                                }
                                else
                                {
                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}ℹ️ Using {advertiseIP} in SDP for NAT traversal");
                                }
                                
                                // Keep NAT troubleshooting concise; TURN is for WebRTC, not SIP.
                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}ℹ️ If RTP packets don't arrive over SIP, possible solutions:");
                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}   1. Ensure the PBX/SBC relays media (direct_media=no) and advertises a reachable RTP IP.");
                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}   2. Configure port forwarding on router/firewall (UDP RTP ports).");
                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}   3. Use a proper SIP SBC/media relay close to the client network.");
                            }
                        }
                        catch (Exception natEx)
                        {
                            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}⚠️ Error modifying SDP for NAT traversal: {natEx.Message}");
                        }
                        
                        // Логируем исходящий SDP для диагностики кодеков
                        try
                        {
                            // В SIPSorcery Body обычно string для SDP
                            string? sdpBody = request.Body?.ToString();
                            
                            if (!string.IsNullOrEmpty(sdpBody) && sdpBody.Contains("v=0") && sdpBody.Contains("m=audio"))
                            {
                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} OUTGOING INVITE SDP body:");
                                var sdpLines = sdpBody.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var line in sdpLines)
                                {
                                    var trimmedLine = line.Trim();
                                    if (string.IsNullOrEmpty(trimmedLine)) continue;
                                    
                                    // Логируем важные строки SDP
                                    if (trimmedLine.StartsWith("v=") || 
                                        trimmedLine.StartsWith("o=") || 
                                        trimmedLine.StartsWith("s=") || 
                                        trimmedLine.StartsWith("c=IN IP4") ||
                                        trimmedLine.StartsWith("m=audio") ||
                                        trimmedLine.StartsWith("a=rtpmap:") ||
                                        trimmedLine.StartsWith("a=fmtp:"))
                                    {
                                        AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} OUT SDP: {trimmedLine}");
                                    }
                                }
                                
                                // Извлекаем кодеки из m=audio строки
                                var mAudioLine = sdpLines.FirstOrDefault(l => l.Trim().StartsWith("m=audio"));
                                if (mAudioLine != null)
                                {
                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} OUT m=audio line: {mAudioLine.Trim()}");
                                    // Парсим кодеки (формат: m=audio PORT RTP/AVP CODEC1 CODEC2 ...)
                                    var parts = mAudioLine.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                    if (parts.Length > 3)
                                    {
                                        var codecs = string.Join(", ", parts.Skip(3));
                                        AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} OUT Codecs in m=audio: {codecs}");
                                        
                                        // Расшифровываем кодеки
                                        var codecNames = new List<string>();
                                        foreach (var codecId in parts.Skip(3))
                                        {
                                            if (int.TryParse(codecId, out int id))
                                            {
                                                string name = id switch
                                                {
                                                    0 => "PCMU (G.711 μ-law)",
                                                    8 => "PCMA (G.711 A-law)",
                                                    9 => "G.722",
                                                    18 => "G.729",
                                                    _ => $"Unknown ({id})"
                                                };
                                                codecNames.Add($"{name} ({id})");
                                            }
                                        }
                                        if (codecNames.Any())
                                        {
                                            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} OUT Codecs decoded: {string.Join(", ", codecNames)}");
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception sdpEx)
                        {
                            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error parsing outgoing SDP: {sdpEx.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error logging outgoing SIP request: {ex.Message}");
                }
            };
            
            // Добавляем обработчик входящих SIP ответов для диагностики
            _sipTransport.SIPResponseInTraceEvent += (localEndPoint, remoteEndPoint, response) =>
            {
                try
                {
                    RememberKnownGoodServerIp(remoteEndPoint);

                    if (response != null)
                    {
                        int statusCode = response.StatusCode;
                        var statusReason = response.ReasonPhrase ?? "";
                        // Use SIPSorcery's CSeqMethod property directly (SIPHeader.CSeq is int, CSeqMethod is SIPMethodsEnum)
                        SIPMethodsEnum cseq = response.Header?.CSeqMethod ?? SIPMethodsEnum.UNKNOWN;
                        var callId = response.Header?.CallId ?? "unknown";
                        
                        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                        SetStatus($"[{timestamp}] SIP Response: {statusCode} {statusReason} (Method: {cseq}, Call-ID: {callId})");
                        
                        // Детальное логирование и воспроизведение гудков только для ответов на INVITE
                        // Игнорируем ответы на REGISTER, OPTIONS и другие запросы
                        // Проверяем, что это ответ на INVITE по наличию Call-ID в активных звонках
                        // или по статус-кодам, характерным для INVITE (100, 180, 183)
                        
                        // Determine if this is a REGISTER response.
                        // SIPSorcery SIPHeader.CSeq is int (sequence number) and CSeqMethod is SIPMethodsEnum.
                        // The previous reflection-based approach on CSeq (which is int) never ran,
                        // so isRegisterResponse was always false — causing REGISTER 200 OKs to be
                        // processed as INVITE responses and corrupting the media session.
                        bool isRegisterResponse = response.Header?.CSeqMethod == SIPMethodsEnum.REGISTER;

                        // Fallback: if CSeqMethod is UNKNOWN (shouldn't happen with SIPSorcery 6.x),
                        // also check the Call-ID. A REGISTER Call-ID never matches the current INVITE dialog.
                        if (!isRegisterResponse && response.Header?.CSeqMethod == SIPMethodsEnum.UNKNOWN)
                        {
                            var responseCallId = response.Header?.CallId;
                            if (!string.IsNullOrEmpty(responseCallId)
                                && !string.IsNullOrEmpty(_currentOutgoingCallId)
                                && !string.Equals(responseCallId, _currentOutgoingCallId, StringComparison.OrdinalIgnoreCase))
                            {
                                // Response Call-ID does not match the current outgoing INVITE — treat as non-INVITE
                                isRegisterResponse = true;
                            }
                        }

                        // Skip non-INVITE responses (REGISTER, OPTIONS, etc.)
                        if (isRegisterResponse)
                        {
                            return;
                        }

                        MarkOutgoingInviteSipResponseSeen(callId, statusCode, cseq);
                        
                        // Обрабатываем только ответы на INVITE
                        // 100, 180, 183, 200, 3xx, 4xx, 5xx, 6xx - это ответы на INVITE
                        if (statusCode == 100)
                        {
                            SetStatus($"[{timestamp}] Call progress: 100 Trying (server received INVITE)");
                        }
                        else if (statusCode == 180)
                        {
                            SetStatus($"[{timestamp}] Call progress: 180 Ringing (phone is ringing)");
                            AppLog.Log($"[SipService] 180 Ringing received, starting ringback tone");
                            // Log unknown headers for diagnostics (to discover non-standard headers from carriers)
                            try
                            {
                                var unk = response.Header?.UnknownHeaders;
                                if (unk != null && unk.Count > 0)
                                {
                                    string connLabel = _isSecondaryConnection ? "[Connection2] " : "";
                                    AppLog.Log($"[SipService] {connLabel}180 UnknownHeaders ({unk.Count}): {string.Join(" | ", unk)}");
                                }
                            }
                            catch { }
                            // Останавливаем busy tone, если он играл (на случай, если был 401 или другая ошибка)
                            _toneGenerator?.Stop();
                            // Воспроизводим ringback tone
                            EnsureToneGenerator();
                            _toneGenerator?.PlayRingbackTone();
                        }
                        else if (statusCode == 183)
                        {
                            SetStatus($"[{timestamp}] Call progress: 183 Session Progress (early media)");
                            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 183 Session Progress received (early media detected)");
                            
                            // Логируем SDP из ответа 183 для диагностики RTP endpoint
                            try
                            {
                                if (response?.Body != null)
                                {
                                    string? sdpBody = response.Body.ToString();
                                    if (!string.IsNullOrEmpty(sdpBody))
                                    {
                                        // Secondary: if SDP advertises a private RTP IP (common when Asterisk behind NAT),
                                        // rewrite it to the remote SIP endpoint address so RTP can actually reach us.
                                        try
                                        {
                                            if (_isSecondaryConnection)
                                            {
                                                string? contactHostDbg = null;
                                                try
                                                {
                                                    contactHostDbg = response?.Header?.Contact?.FirstOrDefault()?.ContactURI?.Host;
                                                }
                                                catch { }

                                                var publicFromContact = TryGetPublicIpFromContactHeader(response!);
                                                var publicFromRemote = (remoteEndPoint != null && remoteEndPoint.Address != null && !IsPrivateIp(remoteEndPoint.Address))
                                                    ? remoteEndPoint.Address
                                                    : null;
                                                var rewriteTarget = publicFromContact ?? publicFromRemote;

                                                var sdpIpStr = TryGetSdpConnectionIp(sdpBody);
                                                if (!string.IsNullOrWhiteSpace(sdpIpStr) &&
                                                    IPAddress.TryParse(sdpIpStr, out var sdpIp) &&
                                                    IsPrivateIp(sdpIp) &&
                                                    rewriteTarget != null)
                                                {
                                                    var rewritten = ReplaceSdpConnectionIp(sdpBody, rewriteTarget.ToString());
                                                    if (response?.Body != null) response.Body = rewritten;
                                                    sdpBody = rewritten;
                                                    var src = publicFromContact != null ? "Contact" : "SIP remote";
                                                    AppLog.Log($"[SipService] [Connection2] Rewrote SDP c= IP from private {sdpIp} to {rewriteTarget} (from {src}).");
                                                }
                                                else if (!string.IsNullOrWhiteSpace(sdpIpStr) &&
                                                         IPAddress.TryParse(sdpIpStr, out var sdpIp2) &&
                                                         IsPrivateIp(sdpIp2) &&
                                                         rewriteTarget == null)
                                                {
                                                    AppLog.Log($"[SipService] [Connection2] SDP c= is private ({sdpIp2}) but no public rewrite target found. contactHost='{contactHostDbg ?? "<null>"}', contactIp='{publicFromContact?.ToString() ?? "<null>"}', remoteIp='{(remoteEndPoint?.Address?.ToString() ?? "<null>")}'.");
                                                }
                                            }
                                        }
                                        catch { }

                                        // SRTP crypto negotiation is handled natively by SIPSorcery.

                                        AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 183 SDP body:\n{sdpBody}");
                                        
                                        // Ищем c= строку (connection information) с IP адресом
                                        var sdpLines = sdpBody.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                                        foreach (var line in sdpLines)
                                        {
                                            string trimmedLine = line.Trim();
                                            if (trimmedLine.StartsWith("c=IN IP4", StringComparison.OrdinalIgnoreCase))
                                            {
                                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 183 SDP connection line: {trimmedLine}");
                                                // Извлекаем IP адрес
                                                var parts = trimmedLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                                if (parts.Length >= 3)
                                                {
                                                    string rtpIp = parts[2];
                                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 183 SDP RTP IP: {rtpIp}");
                                                    
                                                    // NOTE: When we call through a B2BUA (Asterisk/SBC), the SDP RTP IP will be the B2BUA.
                                                    // Do not try to "guess" the downstream carrier RTP IP here.
                                                }
                                            }
                                            if (trimmedLine.StartsWith("m=audio", StringComparison.OrdinalIgnoreCase))
                                            {
                                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 183 SDP m=audio: {trimmedLine}");
                                                // Extract the first codec payload type (this is the provider's preferred codec)
                                                var mParts = trimmedLine.Split(' ');
                                                if (mParts.Length >= 4 && int.TryParse(mParts[3], out int negotiatedPT))
                                                {
                                                    _rtpNegotiatedPayloadType = negotiatedPT;
                                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Negotiated codec PT={negotiatedPT}");
                                                    // Inform FilteringAudioSink about the negotiated PT
                                                    _filteringAudioSink?.SetNegotiatedPayloadType(negotiatedPT);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception sdpEx)
                            {
                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error parsing 183 SDP: {sdpEx.Message}");
                            }
                            
                            // ВАЖНО: При получении 183 Session Progress провайдер уже отправляет гудок в RTP потоке (early media)
                            // НЕ воспроизводим ringback tone, чтобы избежать наложения гудков
                            // Это особенно важно для провайдеров типа Beeline, которые отправляют гудок в early media
                            if (_isSecondaryConnection)
                            {
                                AppLog.Log($"[SipService] [Connection2] 183 received - NOT playing ringback tone (early media from provider)");
                            }
                            else
                            {
                                // Для основного подключения тоже не воспроизводим, если есть early media
                                AppLog.Log($"[SipService] 183 received - NOT playing ringback tone (early media from provider)");
                            }
                            // If we started local ringback fallback because early media was silent,
                            // do NOT stop it on every repeated 183. We'll stop it once early media audio is detected
                            // (see RTP handler) or when the call is answered/cancelled.
                            if (!_localRingbackFallbackActive)
                            {
                                _toneGenerator?.Stop();
                            }

                            // Fallback: if early media RTP never arrives, start local ringback after a short grace period.
                            ScheduleRingbackFallbackAfter183(timestamp);
                        }
                    else if (statusCode == 200)
                    {
                        // 200 OK может быть как для INVITE, так и для REGISTER
                        // Проверяем, есть ли активный звонок
                        // Не обрабатываем 200 OK, если звонок был отменен пользователем
                        // For INVITE: only treat 200 OK as "call answered" if we still have an active call context.
                        // _lastCalledNumber can persist after hangup; do NOT use it as a signal for an active call.
                        bool hasActiveCall = _userAgent?.IsCallActive == true || _toneGenerator != null || _voipMediaSession != null;
                        var cseqMethod = response.Header?.CSeqMethod ?? SIPMethodsEnum.UNKNOWN;

                        if (cseqMethod == SIPMethodsEnum.CANCEL)
                        {
                            string? cancelCallId = response.Header?.CallId;
                            bool forCurrentInvite = string.IsNullOrEmpty(cancelCallId)
                                || string.IsNullOrEmpty(_currentOutgoingCallId)
                                || string.Equals(cancelCallId, _currentOutgoingCallId, StringComparison.OrdinalIgnoreCase);
                            if (forCurrentInvite)
                            {
                                _isCallCancelled = true;
                                _outgoingInviteTerminated = true;
                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}CANCEL confirmed (200 OK) — blocking late INVITE answer/recording");
                                StopCallRecording();
                                StopRtpDiagnostics();
                            }
                            else
                            {
                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Ignoring stale CANCEL 200 OK for Call-ID {cancelCallId} (current={_currentOutgoingCallId ?? "none"})");
                            }
                        }

                        // 200 OK to CANCEL/REGISTER/BYE must not be treated as INVITE answer (starts recording / marks answered).
                        bool isInviteResponse = !isRegisterResponse
                            && (cseqMethod == SIPMethodsEnum.INVITE
                                || (cseqMethod == SIPMethodsEnum.UNKNOWN && hasActiveCall))
                            && hasActiveCall
                            && !_outgoingInviteTerminated;
                        AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK received: isInviteResponse={isInviteResponse}, isRegisterResponse={isRegisterResponse}, _isCallCancelled={_isCallCancelled}, _outgoingInviteTerminated={_outgoingInviteTerminated}, IsCallActive={_userAgent?.IsCallActive}, _toneGenerator={(_toneGenerator != null ? "exists" : "null")}, _voipMediaSession={(_voipMediaSession != null ? "exists" : "null")}, _lastCalledNumber={_lastCalledNumber ?? "null"}");
                        
                        if (!_isCallCancelled && !_outgoingInviteTerminated && isInviteResponse)
                        {
                            int inviteCSeq = response.Header?.CSeq ?? 0;
                            string inviteAnswerKey = $"{response.Header?.CallId}|{inviteCSeq}";
                            bool isDuplicateInvite200 = !string.IsNullOrEmpty(_lastHandledInvite200OkKey)
                                && string.Equals(_lastHandledInvite200OkKey, inviteAnswerKey, StringComparison.OrdinalIgnoreCase);

                            if (isDuplicateInvite200)
                            {
                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Ignoring duplicate INVITE 200 OK (retransmit): {inviteAnswerKey}");
                                _toneGenerator?.Stop();
                                try { _earlyMediaRingbackFallbackCts?.Cancel(); } catch { }
                                _localRingbackFallbackActive = false;
                            }
                            else
                            {
                                _lastHandledInvite200OkKey = inviteAnswerKey;
                            }

                            bool isReinviteAnswer = _wasCallActive && _userAgent?.IsCallActive == true && inviteCSeq > 2;

                            if (!isDuplicateInvite200)
                            {
                                if (isReinviteAnswer)
                                {
                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} INVITE 200 OK for re-INVITE (CSeq={inviteCSeq}, onHold={IsOnHold}) — media update, not a new answer");
                                }
                                else
                                {
                                    TrySignalOutgoingInviteFinal(response.Header?.CallId, 200);
                                    SetStatus($"[{timestamp}] Call progress: 200 OK (call answered, media negotiation starting)");
                                }

                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK received, stopping ringback tone");
                                try { _earlyMediaRingbackFallbackCts?.Cancel(); } catch { }
                                _localRingbackFallbackActive = false;
                            }

                            string? answerSdpForWatchdog = null;

                            if (!isDuplicateInvite200)
                            {
                            try
                            {
                                if (response?.Body != null)
                                {
                                    string? sdpBody = response.Body.ToString();
                                    if (!string.IsNullOrEmpty(sdpBody))
                                    {
                                        // Same private-IP rewrite for final answer.
                                        try
                                        {
                                            if (_isSecondaryConnection)
                                            {
                                                string? contactHostDbg = null;
                                                try
                                                {
                                                    contactHostDbg = response?.Header?.Contact?.FirstOrDefault()?.ContactURI?.Host;
                                                }
                                                catch { }

                                                var publicFromContact = TryGetPublicIpFromContactHeader(response!);
                                                var publicFromRemote = (remoteEndPoint != null && remoteEndPoint.Address != null && !IsPrivateIp(remoteEndPoint.Address))
                                                    ? remoteEndPoint.Address
                                                    : null;
                                                var rewriteTarget = publicFromContact ?? publicFromRemote;

                                                var sdpIpStr = TryGetSdpConnectionIp(sdpBody);
                                                if (!string.IsNullOrWhiteSpace(sdpIpStr) &&
                                                    IPAddress.TryParse(sdpIpStr, out var sdpIp) &&
                                                    IsPrivateIp(sdpIp) &&
                                                    rewriteTarget != null)
                                                {
                                                    var rewritten = ReplaceSdpConnectionIp(sdpBody, rewriteTarget.ToString());
                                                    if (response?.Body != null) response.Body = rewritten;
                                                    sdpBody = rewritten;
                                                    var src = publicFromContact != null ? "Contact" : "SIP remote";
                                                    AppLog.Log($"[SipService] [Connection2] Rewrote SDP c= IP from private {sdpIp} to {rewriteTarget} (from {src}).");
                                                }
                                                else if (!string.IsNullOrWhiteSpace(sdpIpStr) &&
                                                         IPAddress.TryParse(sdpIpStr, out var sdpIp2) &&
                                                         IsPrivateIp(sdpIp2) &&
                                                         rewriteTarget == null)
                                                {
                                                    AppLog.Log($"[SipService] [Connection2] SDP c= is private ({sdpIp2}) but no public rewrite target found. contactHost='{contactHostDbg ?? "<null>"}', contactIp='{publicFromContact?.ToString() ?? "<null>"}', remoteIp='{(remoteEndPoint?.Address?.ToString() ?? "<null>")}'.");
                                                }
                                            }
                                        }
                                        catch { }

                                        // SRTP crypto negotiation is handled natively by SIPSorcery.

                                        answerSdpForWatchdog = sdpBody;

                                        AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK SDP body:\n{sdpBody}");
                                        
                                        // Ищем c= строку (connection information) с IP адресом
                                        var sdpLines = sdpBody.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                                        foreach (var line in sdpLines)
                                        {
                                            string trimmedLine = line.Trim();
                                            if (trimmedLine.StartsWith("c=IN IP4", StringComparison.OrdinalIgnoreCase))
                                            {
                                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK SDP connection line: {trimmedLine}");
                                                // Извлекаем IP адрес
                                                var parts = trimmedLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                                if (parts.Length >= 3)
                                                {
                                                    string rtpIp = parts[2];
                                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK SDP RTP IP: {rtpIp}");
                                                    
                                                    // NOTE: When we call through a B2BUA (Asterisk/SBC), the SDP RTP IP will be the B2BUA.
                                                    // Do not try to "guess" the downstream carrier RTP IP here.
                                                }
                                            }
                                            if (trimmedLine.StartsWith("m=audio", StringComparison.OrdinalIgnoreCase))
                                            {
                                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK SDP m=audio: {trimmedLine}");
                                                // Extract negotiated codec PT from 200 OK (may differ from 183)
                                                var mParts = trimmedLine.Split(' ');
                                                if (mParts.Length >= 4 && int.TryParse(mParts[3], out int negotiatedPT))
                                                {
                                                    _rtpNegotiatedPayloadType = negotiatedPT;
                                                    _filteringAudioSink?.SetNegotiatedPayloadType(negotiatedPT);
                                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK negotiated codec PT={negotiatedPT}");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception sdpEx)
                            {
                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error parsing 200 OK SDP: {sdpEx.Message}");
                            }

                            if (!isReinviteAnswer)
                                ScheduleDeadMediaWatchdogAfterAnswer(answerSdpForWatchdog);
                            }
                        }
                        else if (_isCallCancelled)
                        {
                            // 200 OK пришел после отмены - просто останавливаем гудки
                            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK received but call was cancelled, stopping tones");
                            _toneGenerator?.Stop();
                        }
                    }
                        else if (statusCode >= 300 && statusCode < 700)
                        {
                            // For outgoing INVITE tracking: any final (>=300) response is a terminal result.
                            // (401/407 are auth challenges and are not terminal.)
                            if (statusCode != 401 && statusCode != 407)
                            {
                                // Mark IVR/early-media answer before unblocking CallInternalAsync waiter.
                                if (ShouldTreatSipFailureAsEarlyMediaAnswered(statusCode) && !_earlyMediaAnsweredCall)
                                    MarkEarlyMediaAnswered($"SIP {statusCode} after early media");
                                TrySignalOutgoingInviteFinal(response.Header?.CallId, statusCode);
                            }
                            // Ошибки звонка - воспроизводим busy tone только если был активный звонок
                            // Исключаем 401 Unauthorized - это нормальный ответ для запроса авторизации
                            // Исключаем 487 Request Terminated - это ответ на CANCEL (звонок был отменен пользователем)
                            // Не воспроизводим гудки, если звонок был отменен пользователем
                            if (statusCode == 487)
                            {
                                string? terminatedCallId = response.Header?.CallId;
                                bool forCurrentInvite = string.IsNullOrEmpty(terminatedCallId)
                                    || string.IsNullOrEmpty(_currentOutgoingCallId)
                                    || string.Equals(terminatedCallId, _currentOutgoingCallId, StringComparison.OrdinalIgnoreCase);
                                if (forCurrentInvite)
                                {
                                    _outgoingInviteTerminated = true;
                                    _lastInviteFailureStatusCode = 487;
                                    _last487FromLocalCancel = _isCallCancelled;
                                    string who = _last487FromLocalCancel ? "local user (CANCEL confirmed)" : "remote party / network";
                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Call terminated (487) — ended by {who}, stopping tones");
                                    _toneGenerator?.Stop();
                                    StopCallRecording();
                                    StopRtpDiagnostics();
                                }
                                else
                                {
                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Ignoring stale 487 for Call-ID {terminatedCallId} (current={_currentOutgoingCallId ?? "none"})");
                                }
                            }
                            else if (statusCode != 401 && !_isCallCancelled)
                            {
                                // Play busy tone ONLY when there is a genuine active call
                                // (media session exists or UA reports active call).
                                // _lastCalledNumber alone is NOT sufficient — it persists
                                // after call ends and would cause spurious tones on
                                // unrelated SIP responses (e.g. REGISTER 403).
                                bool hasActiveCall = _userAgent?.IsCallActive == true ||
                                                     _voipMediaSession != null;

                                if (hasActiveCall)
                                {
                                    // Capture last INVITE failure code for recovery logic in CallAsync.
                                    _lastInviteFailureStatusCode = statusCode;
                                    // Also signal the specific Call-ID waiter (if any).
                                    TrySignalOutgoingInviteFinal(response.Header?.CallId, statusCode);

                                    if (ShouldTreatSipFailureAsEarlyMediaAnswered(statusCode))
                                    {
                                        if (!_earlyMediaAnsweredCall)
                                            MarkEarlyMediaAnswered($"SIP {statusCode} after early media");
                                        AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}SIP {statusCode} after early media/IVR — not playing busy tone");
                                        _toneGenerator?.Stop();
                                    }
                                    else
                                    {
                                    // RFC3326 Reason-заголовок (если оператор/Asterisk его прокидывает) несёт
                                    // настоящую причину — например cause=480/«Temporarily Unavailable» при отказе,
                                    // замапленном в 603. Прокидываем его в статус, чтобы окно звонка показало
                                    // понятное «Subscriber unavailable (network)» вместо общего «Call declined».
                                    string reasonHeader = "";
                                    try { reasonHeader = response.Header?.GetUnknownHeaderValue("Reason") ?? ""; } catch { }
                                    string failureStatus = string.IsNullOrWhiteSpace(reasonHeader)
                                        ? $"[{timestamp}] Call failed: {statusCode} {statusReason}"
                                        : $"[{timestamp}] Call failed: {statusCode} {statusReason} | Reason: {reasonHeader}";
                                    SetStatus(failureStatus);
                                    AppLog.Log($"[SipService] Call failed with {statusCode}{(string.IsNullOrWhiteSpace(reasonHeader) ? "" : $" (Reason: {reasonHeader})")}, stopping ringback tone and playing busy tone");
                                    _toneGenerator?.Stop();
                                    EnsureToneGenerator();
                                    if (_toneGenerator != null)
                                    {
                                        _toneGenerator.PlayBusyTone();
                                        _ = Task.Delay(2000).ContinueWith(_ => _toneGenerator?.Stop());
                                    }
                                    }
                                }
                                else
                                {
                                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Ignoring {statusCode} {statusReason} — no active call (likely REGISTER failure)");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error logging SIP response: {ex.Message}");
                }
            };

            // SRTP SDP offer/answer is handled natively by SIPSorcery.
            
            // Добавляем обработчик входящих SIP запросов на уровне транспорта для диагностики и обработки
            // Важно: обрабатываем OPTIONS запросы, чтобы сервер знал, что мы доступны
            _sipTransport.SIPRequestInTraceEvent += async (localEndPoint, remoteEndPoint, request) =>
            {
                // Обрабатываем OPTIONS запросы - отвечаем OK, чтобы сервер знал, что мы доступны
                if (request.Method == SIPMethodsEnum.OPTIONS)
                {
                    try
                    {
                        var okResponse = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
                        okResponse.Header.Supported = SIPExtensionHeaders.REPLACES;
                        await _sipTransport.SendResponseAsync(okResponse);
                        SetStatus($"Responded to OPTIONS from {remoteEndPoint}");
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Error responding to OPTIONS: {ex.Message}");
                    }
                    return; // OPTIONS обработан, не нужно дальше обрабатывать
                }
                
                // Логируем все входящие запросы для диагностики
                try
                {
                    var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    var connLabel = _isSecondaryConnection ? "[Connection2]" : "";
                    var callId = request.Header?.CallId ?? "unknown";
                    string cseqNo = "?";
                    try
                    {
                        var cseqObj = request.Header?.CSeq;
                        if (cseqObj != null)
                        {
                            var cseqType = cseqObj.GetType();
                            if (cseqType == typeof(int))
                            {
                                cseqNo = cseqObj.ToString() ?? "?";
                            }
                            else
                            {
                                var noProp = cseqType.GetProperty("CSeqNumber");
                                var noVal = noProp?.GetValue(cseqObj);
                                if (noVal != null) cseqNo = noVal.ToString() ?? "?";
                            }
                        }
                    }
                    catch { }
                    var from = request.Header?.From?.ToString();
                    var to = request.Header?.To?.ToString();
                    AppLog.Log($"[SipService] {connLabel}[{timestamp}] SIP IN {request.Method}: Call-ID={callId}, CSeq={cseqNo}, from={remoteEndPoint}, From={from}, To={to}");
                }
                catch { }

                SetStatus($"Trace: Received {request.Method} from {remoteEndPoint}");

                // Handle CANCEL: caller cancelled the INVITE before we answered.
                if (request.Method == SIPMethodsEnum.CANCEL)
                {
                    try
                    {
                        var callId = request.Header?.CallId;
                        SetStatus($"Incoming call cancelled by remote party (CANCEL). Call-ID: {callId ?? "unknown"}");
                        AppLog.Log($"[SipService] Incoming call cancelled by remote party (CANCEL). Call-ID: {callId ?? "unknown"}");

                        // Respond 200 OK to CANCEL.
                        var ok = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
                        await _sipTransport.SendResponseAsync(ok);

                        // Best-effort: respond 487 to the original INVITE if we still have it.
                        SIPRequest? savedInvite;
                        lock (this) { savedInvite = _incomingCallRequest; }
                        if (savedInvite != null && !string.IsNullOrEmpty(callId) && savedInvite.Header?.CallId == callId)
                        {
                            try
                            {
                                var terminated = SIPResponse.GetResponse(savedInvite, SIPResponseStatusCodesEnum.RequestTerminated, "Cancelled");
                                await _sipTransport.SendResponseAsync(terminated);
                            }
                            catch { }
                        }

                        // Stop any local tones and notify UI to close the call window.
                        EndCallAndNotifyUi();

                        // Clear incoming call state so retransmits won't keep UI alive.
                        lock (this)
                        {
                            _incomingCallUserAgent = null;
                            _incomingCallRequest = null;
                            _incomingCallerNumber = null;
                            _incomingCallId = null;
                        }
                        _incomingCalls.Clear();
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Error handling CANCEL: {ex.Message}");
                    }
                    return;
                }
                
                // Обрабатываем BYE запросы - другая сторона завершила звонок
                if (request.Method == SIPMethodsEnum.BYE)
                {
                    SetStatus("Call ended by remote party");
                    AppLog.Log("[SipService] Call ended by remote party (BYE request received)");
                    // Отвечаем OK на BYE
                    try
                    {
                        var okResponse = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
                        await _sipTransport.SendResponseAsync(okResponse);
                        
                        StopCallRecording();
                        StopRtpDiagnostics();
                        _wasCallActive = false;
                        ReleaseMediaSessionAndAudio("remote bye");
                        
                        // Уведомляем UI о завершении звонка через Dispatcher
                        UiThread.BeginInvoke(() => OnCallEnded?.Invoke());
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Error handling BYE: {ex.Message}");
                        AppLog.Log($"[SipService] Error handling BYE: {ex.Message}");
                        StopCallRecording();
                        StopRtpDiagnostics();
                        UiThread.BeginInvoke(() => OnCallEnded?.Invoke());
                    }
                    return;
                }
                
                // Логируем входящие INVITE запросы для диагностики (только первый раз для каждого Call-ID)
                if (request.Method == SIPMethodsEnum.INVITE)
                {
                    string? callId = request.Header?.CallId;
                    bool isKnownCall = !string.IsNullOrEmpty(callId) && _incomingCalls != null && _incomingCalls.ContainsKey(callId);

                    // Always send a provisional response for INVITE. This prevents PBX retransmits and allows proper CANCEL handling.
                    // Safe even if UA also sends 100/180; duplicates are tolerated by SIP.
                    try
                    {
                        var trying = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Trying, null);
                        await _sipTransport.SendResponseAsync(trying);

                        // For first INVITE, also send 180 Ringing (so caller knows we're ringing).
                        // For retransmits, re-send 180 so the transaction stays alive with a response.
                        var ringing = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ringing, null);
                        await _sipTransport.SendResponseAsync(ringing);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log($"[SipService] Warning: failed to send provisional INVITE response: {ex.Message}");
                    }
                    
                    // Логируем только первый INVITE для каждого Call-ID
                    if (!isKnownCall)
                    {
                        SetStatus($"Trace: Received INVITE from {request.RemoteSIPEndPoint}");
                    if (request.Header?.To != null && request.Header.To.ToURI != null)
                    {
                        string toUser = request.Header.To.ToURI.User ?? "";
                            string callerNumber = "Unknown";
                            if (request.Header.From != null && request.Header.From.FromURI != null)
                            {
                                callerNumber = request.Header.From.FromURI.User ?? request.Header.From.FromURI.ToString();
                            }
                            
                            SetStatus($"Trace: INVITE details - To: {toUser}, From: {callerNumber}, Expected username: {_username}");
                            
                            // Проверяем, является ли INVITE для нас
                            // Если To пустой, но From совпадает с нашим username, это может быть звонок для нас
                            bool isForUs = false;
                            string? requestUri = null;
                            if (!string.IsNullOrEmpty(toUser) && toUser == _username)
                            {
                                isForUs = true;
                            }
                            else if (string.IsNullOrEmpty(toUser))
                            {
                                // Если To пустой, проверяем Request-URI или другие заголовки
                                // В некоторых случаях PBX может отправлять INVITE с пустым To, но правильным Request-URI
                                requestUri = request.URI?.User;
                                if (!string.IsNullOrEmpty(requestUri) && requestUri == _username)
                                {
                                    isForUs = true;
                                    SetStatus($"Trace: INVITE has empty To but Request-URI matches: {requestUri}");
                                }
                                else
                                {
                                    SetStatus($"Trace: INVITE has empty To and Request-URI ({requestUri}) doesn't match expected ({_username})");
                                }
                            }
                            
                            if (isForUs)
                            {
                                SetStatus($"Trace: INVITE for {toUser ?? requestUri ?? "us"} from {callerNumber} - should be handled by UserAgent");
                                // UserAgent должен автоматически обработать этот запрос через OnIncomingCall
                            }
                            else
                            {
                                SetStatus($"Trace: INVITE not for us (to: {toUser ?? "(empty)"}, expected: {_username})");
                            }
                    }
                        else
                        {
                            SetStatus($"Trace: INVITE received but To header is null or ToURI is null");
                        }
                    }
                    // Для ретрансмиссий не логируем подробности, только в UserAgent обработчике
                }
            };

            // Инициализируем генератор гудков (ленивая инициализация - создаем только при необходимости)
            // _toneGenerator будет создан при первом использовании

            // 2. UserAgent для звонков.
            // В SIPSorcery SIPUserAgent автоматически обрабатывает входящие INVITE запросы
            // через событие OnIncomingCall, если транспорт правильно настроен
            SIPEndPoint? outboundProxy = null;
            try
            {
                // For the Secondary connection we typically talk to an SBC/proxy (Kamailio).
                // Setting outboundProxy helps SIPSorcery handle 407 Proxy Authentication challenges correctly.
                if (_isSecondaryConnection)
                    outboundProxy = BuildOutboundProxyEndpoint();
            }
            catch { }

            _userAgent = new SIPUserAgent(_sipTransport, outboundProxy: outboundProxy);

            // Обработка входящих звонков
            _userAgent.OnIncomingCall += async (ua, req) =>
            {
                try
                {
                    SetStatus($"OnIncomingCall event triggered on service: {_instanceHash}");
                    SetStatus($"OnIncomingCall: UserAgent: {ua != null}, Request: {req != null}");
                    
                    // Проверяем, что req не null
                    if (req == null)
                    {
                        SetStatus("OnIncomingCall: Request is null, cannot process incoming call");
                        return;
                    }
                    
                    // Извлекаем номер звонящего из From заголовка
                    string callerNumber = "Unknown";
                    if (req.Header?.From != null && req.Header.From.FromURI != null)
                    {
                        callerNumber = req.Header.From.FromURI.User ?? req.Header.From.FromURI.ToString();
                    }

                    // ГЕЙТ: Если для этого подключения выбран транспорт WebRTC — отклоняем все SIP звонки.
                    // Per-connection: Main и Secondary читают свой собственный транспорт из настроек.
                    bool useWebRtc = ShouldUseWebRtcFor(_isSecondaryConnection);
                    if (useWebRtc)
                    {
                        SetStatus($"SIP incoming call from {callerNumber} rejected: WebRTC mode is enabled");
                        AppLog.Log($"[SipService] SIP incoming call from {callerNumber} rejected: WebRTC mode is enabled");
                        // Отклоняем звонок с причиной "Busy Here"
                        try
                        {
                            var busyResponse = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, "WebRTC mode enabled");
                            if (_sipTransport != null)
                            {
                                await _sipTransport.SendResponseAsync(busyResponse);
                            }
                        }
                        catch (Exception rejectEx)
                        {
                            AppLog.Log($"[SipService] Error rejecting SIP call: {rejectEx.Message}");
                        }
                        return; // Выходим из обработчика, не обрабатываем SIP звонок
                    }
                    
                    // ГЕЙТ: Основная проверка - игнорируем SIP входящие, если WebRTC звонок активен или в cooldown
                    bool shouldIgnoreSip = false;
                    string ignoreReason = "";
                    
                    try
                    {
                        // Проверка 1: Cooldown после завершения WebRTC звонка
                        lock (_ignoreSipLock)
                        {
                            if (_ignoreSipUntil.HasValue && DateTime.UtcNow < _ignoreSipUntil.Value)
                            {
                                shouldIgnoreSip = true;
                                var remainingSeconds = (_ignoreSipUntil.Value - DateTime.UtcNow).TotalSeconds;
                                ignoreReason = $"WebRTC cooldown active (remaining: {remainingSeconds:F1}s)";
                            }
                        }
                        
                        // Проверка 2: Активный WebRTC звонок в любом слоте (main или secondary)
                        if (!shouldIgnoreSip)
                        {
                            foreach (var webRtcService in WebRtcService.AllSlots)
                            {
                                if (webRtcService == null) continue;
                                var webRtcState = webRtcService.CurrentCallState;

                                if (webRtcService.IsReadyForCalls &&
                                    (webRtcState == WebRtcCallState.Ringing ||
                                     webRtcState == WebRtcCallState.Connected ||
                                     webRtcState == WebRtcCallState.Calling))
                                {
                                    shouldIgnoreSip = true;
                                    ignoreReason = $"WebRTC call active in slot '{webRtcService.Slot}' (state: {webRtcState})";
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception webRtcCheckEx)
                    {
                        // Если проверка WebRTC не удалась, логируем, но продолжаем обработку SIP звонка
                        SetStatus($"Warning: Failed to check WebRTC state: {webRtcCheckEx.Message}");
                        AppLog.Log($"[SipService] Warning: Failed to check WebRTC state: {webRtcCheckEx.Message}");
                    }
                    
                    if (shouldIgnoreSip)
                    {
                        SetStatus($"SIP incoming call from {callerNumber} ignored: {ignoreReason}");
                        AppLog.Log($"[SipService] SIP incoming call from {callerNumber} ignored: {ignoreReason}");
                        return; // Не обрабатываем SIP входящий
                    }

                    SetStatus($"Incoming call from {callerNumber}");
                    
                    // Логируем SDP из INVITE для проверки codec (критично для диагностики)
                    try
                    {
                        string? sdpBody = req.Body; // В SIPSorcery Body это string
                        if (!string.IsNullOrEmpty(sdpBody))
                        {
                            // SRTP offer/answer is handled natively by SIPSorcery.

                            int previewLength = Math.Min(500, sdpBody.Length);
                            AppLog.Log($"[SipService] INVITE SDP body (first {previewLength} chars):\n{sdpBody.Substring(0, previewLength)}");
                            
                            // Ищем m=audio и a=rtpmap строки
                            var sdpLines = sdpBody.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in sdpLines)
                            {
                                string trimmedLine = line.Trim();
                                if (trimmedLine.StartsWith("m=audio", StringComparison.OrdinalIgnoreCase))
                                {
                                    AppLog.Log($"[SipService] SDP m=audio: {trimmedLine}");
                                }
                                if (trimmedLine.StartsWith("a=rtpmap", StringComparison.OrdinalIgnoreCase))
                                {
                                    AppLog.Log($"[SipService] SDP a=rtpmap: {trimmedLine}");
                                }
                            }
                        }
                    }
                    catch (Exception sdpEx)
                    {
                        AppLog.Log($"[SipService] Error parsing SDP: {sdpEx.Message}");
                    }
                    
                    // Извлекаем Call-ID из запроса для уникальной идентификации звонка
                    string? callId = req?.Header?.CallId;
                    if (string.IsNullOrEmpty(callId))
                    {
                        callId = Guid.NewGuid().ToString();
                    }
                    
                    // Проверяем, не является ли это повторным INVITE для того же звонка
                    bool isRetransmission = false;
                    lock (this)
                    {
                        if (!string.IsNullOrEmpty(_incomingCallId) && _incomingCallId == callId)
                        {
                            // Это повторный INVITE (retransmission) для того же звонка
                            // Не перезаписываем данные и не вызываем событие OnIncomingCall
                            isRetransmission = true;
                            SetStatus($"Trace: INVITE retransmission detected (CallID: {callId}), ignoring");
                        }
                    }
                    
                    // Если это ретрансмиссия, выходим из обработчика
                    if (isRetransmission)
                    {
                        return; // Выходим из обработчика, не вызывая OnIncomingCall
                    }
                    
                    // Проверяем, не является ли это новый звонок с другим Call-ID, но окно уже открыто
                    // В этом случае нужно либо закрыть старое окно, либо отклонить новый звонок
                    lock (this)
                    {
                        if (!string.IsNullOrEmpty(_incomingCallId) && _incomingCallId != callId)
                        {
                            // Новый звонок пришел, но старый еще не обработан
                            SetStatus($"Trace: New incoming call (CallID: {callId}) while previous call (CallID: {_incomingCallId}) still pending");
                            AppLog.Log($"[SipService] New incoming call (CallID: {callId}) while previous call (CallID: {_incomingCallId}) still pending - processing new call");
                            // Продолжаем обработку нового звонка - старое окно должно быть закрыто или обработано
                        }
                    }
                    
                    // Сохраняем информацию о входящем звонке в словарь по Call-ID
                    // Это более надежный способ хранения, так как запрос может быть временным объектом
                    if (ua != null && req != null)
                    {
                    _incomingCalls[callId] = (ua, req, callerNumber);
                    }
                    
                    // Также сохраняем в поля класса для обратной совместимости
                        lock (this)
                        {
                            _incomingCallUserAgent = ua;
                            _incomingCallRequest = req;
                            _incomingCallerNumber = callerNumber;
                            _incomingCallId = callId; // Сохраняем Call-ID
                    }
                    
                    // Логируем сохранение информации для диагностики (только для первого INVITE)
                    SetStatus($"Saved incoming call info. CallID: {callId}, UserAgent: {ua != null}, Request: {req != null}, Saved Request: {_incomingCallRequest != null}, Dict Count: {_incomingCalls.Count}, Caller: {callerNumber}");
                    
                    // Уведомляем UI о входящем звонке (не принимаем автоматически).
                    // UiThread маршалит в UI-поток (urgent = DispatcherPriority.Send в WPF);
                    // при отсутствии зарегистрированного диспетчера выполняет действие напрямую.
                    UiThread.BeginInvokeUrgent(() =>
                    {
                        try
                        {
                            // Убираем избыточное логирование - событие вызывается один раз
                            OnIncomingCall?.Invoke(callerNumber);
                        }
                        catch (Exception ex)
                        {
                            SetStatus($"UI thread: Error invoking OnIncomingCall event: {ex.Message}");
                        }
                    });
                    
                    // Проверяем еще раз после уведомления UI
                    if (_incomingCallRequest == null && _incomingCalls.ContainsKey(callId))
                    {
                        SetStatus($"Request became null after UI notification. Restoring from dictionary. Dict has call: {_incomingCalls.ContainsKey(callId)}");
                        // Восстанавливаем из словаря
                        if (_incomingCalls.TryGetValue(callId, out var callInfo))
                        {
                            lock (this)
                            {
                                _incomingCallUserAgent = callInfo.UserAgent;
                                _incomingCallRequest = callInfo.Request;
                                _incomingCallerNumber = callInfo.CallerNumber;
                            }
                            SetStatus($"Restored from dictionary. Request: {_incomingCallRequest != null}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    SetStatus($"Error handling incoming call: {ex.Message}");
                    // Отклоняем звонок при ошибке
                    try
                    {
                        var busyResponse = SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.BusyHere, "Error");
                        if (_sipTransport != null)
                        {
                            await _sipTransport.SendResponseAsync(busyResponse);
                        }
                    }
                    catch
                    {
                        // Игнорируем ошибки при отклонении
                    }
                }
            };

            // 3. Audio/media session initialization can be expensive (device enumeration, endpoint creation).
            // We defer it to the moment a SIP call is actually made/answered to speed up registration,
            // especially when switching from WebRTC -> SIP.

            // 4. Registration is required before most carriers will accept INVITE.
            // Start it here, but note we do not block UI waiting for success.
            await StartRegistrationAsync();
        }

        // NOTE: SRTP SDP offer/answer is handled natively by SIPSorcery via SDP crypto negotiation.

        private async Task StartRegistrationAsync()
        {
            if (_sipTransport == null)
            {
                SetStatus("Cannot register: SIP transport not initialized.");
                return;
            }

            bool useWebRtc = ShouldUseWebRtcFor(_isSecondaryConnection);
            if (useWebRtc)
            {
                // If switching to WebRTC mode, stop any existing SIP registration.
                if (_regUserAgent != null)
                {
                    try
                    {
                        AppLog.Log("[SipService] Cancelling existing SIP registration before switching to WebRTC mode");
                        _regUserAgent.Stop();
                        _regUserAgent = null;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log($"[SipService] Error stopping SIP registration: {ex.Message}");
                    }
                }

                SetStatus("SIP registration skipped: WebRTC mode is enabled");
                AppLog.Log("[SipService] SIP registration skipped: WebRTC mode is enabled");
                IsRegistered = false;
                await Task.Delay(150);
                return;
            }

            lock (_registrationLock)
            {
                _registrationTcs?.TrySetCanceled();
                _registrationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            // Best-effort: restart registration UA to recover from stale state.
            if (_regUserAgent != null)
            {
                try { _regUserAgent.Stop(); } catch { }
                _regUserAgent = null;
            }

            IsRegistered = false;

            RefreshResolvedServerIp();

            var serverAddress = _server.Contains(":")
                ? _server
                : $"{_server}:{_port}";

            var sipUri = $"{(_useTls ? "sips" : "sip")}:{_username}@{serverAddress}";

            if (_isSecondaryConnection)
            {
                AppLog.Log($"[SipService] [Connection2] Registering with username='{_username}', server='{serverAddress}', sipUri='{sipUri}'");
            }
            else
            {
                AppLog.Log($"[SipService] Registering with username='{_username}', server='{serverAddress}', sipUri='{sipUri}'");
            }

            // Some SIPSorcery versions support passing protocol explicitly for registration.
            // We use reflection so the project stays compatible across package updates.
            _regUserAgent = CreateRegistrationUserAgent(
                _sipTransport,
                _username,
                _password,
                serverAddress,
                aorUri: sipUri,
                expiry: 300,
                useTls: _useTls);

            _regUserAgent.RegistrationFailed += (uri, response, errorMessage) =>
            {
                IsRegistered = false;
                LastRegistrationError = errorMessage;
                string connectionLabel = _isSecondaryConnection ? "[Connection2] " : "";
                AppLog.Log($"[SipService] {connectionLabel}Registration failed: {errorMessage}, URI: {uri}");
                if (response != null)
                {
                    AppLog.Log($"[SipService] {connectionLabel}Response status: {response.StatusCode} {response.ReasonPhrase}");

                    if (response.Header != null)
                    {
                        try
                        {
                            if (response.Header.Warning != null)
                            {
                                AppLog.Log($"[SipService] {connectionLabel}Warning header found: {response.Header.Warning}");
                            }
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(response.Body))
                        {
                            AppLog.Log($"[SipService] {connectionLabel}Response body: {response.Body}");
                        }

                        try
                        {
                            string responseStr = response.ToString();
                            if (!string.IsNullOrEmpty(responseStr))
                            {
                                string responsePreview = responseStr.Length > 1000 ? responseStr.Substring(0, 1000) + "..." : responseStr;
                                AppLog.Log($"[SipService] {connectionLabel}Response preview: {responsePreview}");
                            }
                        }
                        catch { }
                    }

                    if (response.StatusCode == (int)SIPResponseStatusCodesEnum.Forbidden)
                    {
                        string detailedMessage = $"403 Forbidden - Registration denied by server.\n\n" +
                            $"Possible reasons:\n" +
                            $"• Incorrect username or password\n" +
                            $"• Account is disabled or locked\n" +
                            $"• IP address is not allowed\n" +
                            $"• Server does not allow registration from this location\n" +
                            $"• Account does not have permission to register\n\n" +
                            $"Please check your credentials and server settings.";
                        AppLog.Log($"[SipService] {connectionLabel}{detailedMessage}");
                        SetStatus($"Registration failed: 403 Forbidden - Check credentials");
                    }
                    else
                    {
                        SetStatus($"Registration failed: {errorMessage}");
                    }
                }
                else
                {
                    SetStatus($"Registration failed: {errorMessage}");
                }

                lock (_registrationLock)
                {
                    _registrationTcs?.TrySetResult(false);
                }
            };

            _regUserAgent.RegistrationTemporaryFailure += (uri, response, errorMessage) =>
            {
                IsRegistered = false;
                string connectionLabel = _isSecondaryConnection ? "[Connection2] " : "";
                AppLog.Log($"[SipService] {connectionLabel}Registration temporary failure: {errorMessage}, URI: {uri}");
                if (response != null)
                {
                    AppLog.Log($"[SipService] {connectionLabel}Temporary failure response status: {response.StatusCode} {response.ReasonPhrase}");
                }
                SetStatus($"Registration temporary failure: {errorMessage}");

                lock (_registrationLock)
                {
                    _registrationTcs?.TrySetResult(false);
                }
            };

            _regUserAgent.RegistrationRemoved += (uri, response) =>
            {
                IsRegistered = false;
                SetStatus("Registration removed.");

                lock (_registrationLock)
                {
                    _registrationTcs?.TrySetResult(false);
                }
            };

            _regUserAgent.RegistrationSuccessful += (uri, response) =>
            {
                IsRegistered = true;
                LastRegistrationError = null;
                ServerDnsUnusable = false;
                if (response != null && response.Header != null)
                {
                    var contactHeader = response.Header.Contact;
                    if (contactHeader != null && contactHeader.Count > 0)
                    {
                        var contact = contactHeader[0].ContactURI?.ToString() ?? "unknown";
                        SetStatus($"Registration successful! Contact: {contact}");
                    }
                    else
                    {
                        SetStatus("Registration successful!");
                    }
                }
                else
                {
                    SetStatus("Registration successful!");
                }

                lock (_registrationLock)
                {
                    _registrationTcs?.TrySetResult(true);
                }
            };

            SetStatus("Registering on SIP server...");
            _regUserAgent.Start();

            await Task.Delay(150);
        }

        private async Task<bool> EnsureRegisteredForOutgoingCallAsync(TimeSpan timeout)
        {
            // If already registered, nothing to do.
            if (IsRegistered) return true;

            // If registration UA is missing (or we just restarted transport), start it now.
            if (_regUserAgent == null)
            {
                await StartRegistrationAsync();
            }
            else
            {
                // Best-effort: ensure it is running.
                try { _regUserAgent.Start(); } catch { }
            }

            var start = DateTime.UtcNow;
            while (!IsRegistered && (DateTime.UtcNow - start) < timeout)
            {
                await Task.Delay(75);
            }

            if (IsRegistered) return true;

            // One more attempt: restart registration and wait briefly again.
            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}EnsureRegisteredForOutgoingCallAsync: registration still not ready, restarting registration...");
            await StartRegistrationAsync();

            start = DateTime.UtcNow;
            while (!IsRegistered && (DateTime.UtcNow - start) < TimeSpan.FromMilliseconds(Math.Min(1200, (int)timeout.TotalMilliseconds)))
            {
                await Task.Delay(75);
            }

            return IsRegistered;
        }

        private static void RemoveUdpChannelsFromTransport(SIPTransport transport)
        {
            try
            {
                // Prefer public API if present.
                var removeMethod = transport.GetType().GetMethod("RemoveSIPChannel", BindingFlags.Instance | BindingFlags.Public);
                var getChannelsMethod = transport.GetType().GetMethod("GetSIPChannels", BindingFlags.Instance | BindingFlags.Public);

                if (getChannelsMethod != null)
                {
                    var channelsObj = getChannelsMethod.Invoke(transport, Array.Empty<object>());
                    if (channelsObj is System.Collections.IEnumerable enumerable)
                    {
                        var toRemove = new List<object>();
                        foreach (var ch in enumerable)
                        {
                            try
                            {
                                var lepProp = ch?.GetType().GetProperty("ListeningEndPoint", BindingFlags.Instance | BindingFlags.Public);
                                var lep = lepProp?.GetValue(ch);
                                var protoProp = lep?.GetType().GetProperty("Protocol", BindingFlags.Instance | BindingFlags.Public);
                                var protoVal = protoProp?.GetValue(lep);
                                if (protoVal is SIPProtocolsEnum proto && proto == SIPProtocolsEnum.udp)
                                {
                                    toRemove.Add(ch!);
                                }
                            }
                            catch { }
                        }

                        if (removeMethod != null)
                        {
                            foreach (var ch in toRemove)
                            {
                                try { removeMethod.Invoke(transport, new[] { ch }); } catch { }
                            }
                        }
                        else
                        {
                            // No public remove API; fall back to direct list manipulation below.
                        }
                    }
                }

                // Reflection fallback: find a field that holds channels list.
                foreach (var field in transport.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    object? val = null;
                    try { val = field.GetValue(transport); } catch { }
                    if (val is System.Collections.IList list && list.Count > 0)
                    {
                        // Heuristic: list items look like SIPChannel and have ListeningEndPoint.Protocol
                        bool looksLikeChannels = false;
                        try
                        {
                            var first = list[0];
                            looksLikeChannels = first != null && first.GetType().GetProperty("ListeningEndPoint", BindingFlags.Instance | BindingFlags.Public) != null;
                        }
                        catch { }

                        if (!looksLikeChannels) continue;

                        for (int i = list.Count - 1; i >= 0; i--)
                        {
                            try
                            {
                                var ch = list[i];
                                var lepProp = ch?.GetType().GetProperty("ListeningEndPoint", BindingFlags.Instance | BindingFlags.Public);
                                var lep = lepProp?.GetValue(ch);
                                var protoProp = lep?.GetType().GetProperty("Protocol", BindingFlags.Instance | BindingFlags.Public);
                                var protoVal = protoProp?.GetValue(lep);
                                if (protoVal is SIPProtocolsEnum proto && proto == SIPProtocolsEnum.udp)
                                {
                                    list.RemoveAt(i);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// ��������� ������ �� ��������� �����.
        /// </summary>
        public async Task CallAsync(string number)
        {
            await CallInternalAsync(number, allowRecoveryRetry: true);
        }

        /// <summary>
        /// Stops local tones, sends CANCEL/BYE when possible, and closes RTP/media.
        /// Safe to call multiple times. Does not raise <see cref="OnCallEnded"/>.
        /// </summary>
        private void ForceCancelOutgoingCall(string reason)
        {
            _toneGenerator?.Stop();
            try { _earlyMediaRingbackFallbackCts?.Cancel(); } catch { }
            try { _deadMediaWatchdogCts?.Cancel(); } catch { }
            _localRingbackFallbackActive = false;

            _isCallCancelled = true;
            _outgoingInviteTerminated = true;

            if (_callCancellationTokenSource != null && !_callCancellationTokenSource.IsCancellationRequested)
            {
                try { _callCancellationTokenSource.Cancel(); } catch { }
            }

            try
            {
                lock (_outgoingInviteLock)
                {
                    _pendingOutgoingInviteFinalResponseTcs?.TrySetCanceled();
                    _pendingOutgoingInviteCallIdTcs?.TrySetCanceled();
                }
            }
            catch { }

            if (_userAgent != null)
            {
                try
                {
                    if (_userAgent.IsCallActive)
                        _userAgent.Hangup();
                    else
                    {
                        var cancelMethod = _userAgent.GetType().GetMethod(
                            "Cancel", BindingFlags.Public | BindingFlags.Instance);
                        if (cancelMethod != null)
                            cancelMethod.Invoke(_userAgent, null);
                        else
                            _userAgent.Hangup();
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}ForceCancelOutgoingCall({reason}): signaling error: {ex.Message}");
                }
            }

            StopCallRecording();
            StopRtpDiagnostics();
            ReleaseMediaSessionAndAudio(reason);
            _wasCallActive = false;
            _activeCallTask = null;
            try { _callCancellationTokenSource?.Dispose(); } catch { }
            _callCancellationTokenSource = null;
            _outboundQuietUntilUtc = DateTime.UtcNow.AddSeconds(2.5);

            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}ForceCancelOutgoingCall: {reason}");
        }

        /// <summary>
        /// Исходящий SIP сорван: RTP/тон/рекордер + <see cref="OnCallEnded"/> (закрыть CallWindow).
        /// Идемпотентно если диагностика RTP не была запущена.
        /// </summary>
        private void ApplyOutboundFailureUiTeardown(bool playBusyTone, string? statusMessage = null)
        {
            try
            {
                ForceCancelOutgoingCall("outbound failure");

                if (playBusyTone && !(_isSecondaryConnection && _lastInviteFailureStatusCode == 404))
                {
                    EnsureToneGenerator();
                    if (_toneGenerator != null)
                    {
                        _toneGenerator.PlayBusyTone();
                        _ = Task.Delay(2000).ContinueWith(_ => _toneGenerator?.Stop());
                    }
                }

                SetStatus(string.IsNullOrWhiteSpace(statusMessage)
                    ? (BuildOutboundFailureStatusMessage() ?? "Call failed (timeout/rejected).")
                    : statusMessage!);
                _wasCallActive = false;
                StopCallRecording();
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}ApplyOutboundFailureUiTeardown (playBusyTone={playBusyTone}) — invoking OnCallEnded");
                UiThread.BeginInvoke(() => OnCallEnded?.Invoke());
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] ApplyOutboundFailureUiTeardown: {ex.Message}");
            }
        }

        private async Task CallInternalAsync(string number, bool allowRecoveryRetry)
        {
            // IMPORTANT: When this connection is configured for WebRTC, SIP outgoing calls must be blocked.
            if (ShouldUseWebRtcFor(_isSecondaryConnection))
            {
                AppLog.Log($"[SipService] SIP outgoing call to {number} blocked: WebRTC mode is enabled for this connection ({(_isSecondaryConnection ? "secondary" : "main")})");
                SetStatus("SIP calls are disabled (WebRTC mode enabled)");
                return;
            }

            if (_userAgent == null)
            {
                SetStatus("SIP not initialized. Click Connect.");
                return;
            }

            // Carrier trunks typically require REGISTER before accepting INVITE.
            // This is critical for the secondary connection ("Connection2") where users expect one-click dialing.
            if (_isSecondaryConnection)
            {
                if (!await EnsureRegisteredForOutgoingCallAsync(TimeSpan.FromSeconds(2.5)))
                {
                    AppLog.Log("[SipService] [Connection2] Outgoing call blocked: not registered after wait/retry.");
                    // Иначе CallWindow висит «в звонке» (после 403→refetch регистрации второй Invite не вышел).
                    ApplyOutboundFailureUiTeardown(playBusyTone: false,
                        statusMessage: "SIP not registered yet — retrying connection. Please wait a moment and try again.");
                    return;
                }
            }

            if (DateTime.UtcNow < _outboundQuietUntilUtc)
            {
                var waitMs = (int)Math.Ceiling((_outboundQuietUntilUtc - DateTime.UtcNow).TotalMilliseconds);
                if (waitMs > 0)
                {
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Waiting {waitMs}ms for previous call teardown before new INVITE...");
                    await Task.Delay(waitMs).ConfigureAwait(false);
                }
            }

            if (_userAgent?.IsCallActive == true)
            {
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Outgoing call blocked: previous call still active.");
                SetStatus("Previous call still active — please wait.");
                return;
            }

            // Track this outgoing attempt so we can correlate multiple INVITEs / responses to a single UI action.
            try
            {
                var attemptId = Guid.NewGuid().ToString("N")[..8];
                lock (_outgoingInviteLock)
                {
                    _currentOutgoingAttemptId = attemptId;
                    _currentOutgoingDialNumber = number;
                    _currentOutgoingCallId = null;
                }
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Outgoing call attempt started: attempt={attemptId}, number='{number}', allowRecoveryRetry={allowRecoveryRetry}");
            }
            catch { }
            
            // Cancel/BYE may have closed the session without nulling references — never reuse a dead stack.
            if (_voipMediaSession != null && !_userAgent.IsCallActive)
            {
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Releasing stale media session before outgoing call");
                ReleaseMediaSessionAndAudio("stale before outgoing call");
            }

            // Инициализируем аудио, если оно не было инициализировано
            // Но только если не включен WebRTC режим (для WebRTC аудио не используется)
            if (_voipMediaSession == null)
            {
                if (_skipAudioInitialization)
                {
                    // WebRTC режим включен - пропускаем инициализацию аудио
                    AppLog.Log("[SipService] Skipping audio initialization for outgoing call (WebRTC mode)");
                    SetStatus("Audio initialization skipped (WebRTC mode)");
                    return; // Выходим из метода, так как для WebRTC звонки обрабатываются через WebRtcService
                }
                
                SetStatus("Audio not initialized. Attempting to initialize...");
                AppLog.Log($"[SipService] Initializing audio for outgoing call. SkipAudioInit={_skipAudioInitialization}");
                
                // Для исходящих звонков тоже используем force=true, если WebRTC не должен использоваться
                // (WebRTC должен обрабатываться отдельно через WebView2)
                InitializeAudio(throwOnError: true, force: true);
                
                // Проверяем еще раз после попытки инициализации
                if (_voipMediaSession == null)
                {
                    string errorMsg = "Audio initialization failed. Please check:\n1. Microphone and speaker are connected\n2. Audio devices are not being used by another application\n3. Audio drivers are installed correctly\n4. WebRTC mode is disabled if you want to use SIPSorcery audio";
                    SetStatus(errorMsg);
                    AppLog.Log("[SipService] Audio initialization failed for outgoing call");
                    throw new InvalidOperationException("Audio service not initialized. Please check your audio devices and try again.");
                }
                
                AppLog.Log("[SipService] Audio initialized successfully for outgoing call");
            }
            else if (_wasCallActive)
            {
                // Если был предыдущий звонок, переинициализируем медиа-сессию перед новым звонком
                // Но только если не включен WebRTC режим (для WebRTC аудио не используется)
                if (_skipAudioInitialization)
                {
                    // WebRTC режим включен - пропускаем переинициализацию аудио
                    AppLog.Log("[SipService] Skipping audio reinitialization (WebRTC mode)");
                    _wasCallActive = false;
                }
                else
                {
                    SetStatus("Reinitializing media session for new call...");
                    try
                    {
                        // Останавливаем запись предыдущего звонка перед началом нового
                        StopCallRecording();
                        
                        ReleaseMediaSessionAndAudio("new call");
                        await System.Threading.Tasks.Task.Delay(200);
                        
                        // Полная переинициализация с новым endpoint
                        InitializeAudio(throwOnError: true, force: true);
                        
                        if (_voipMediaSession == null) throw new InvalidOperationException("Failed to reinitialize media session");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log($"[SipService] Error reinitializing media session: {ex.Message}");
                        throw;
                    }
                    _wasCallActive = false;
                }
            }

            if (_userAgent.IsCallActive)
            {
                SetStatus("Call already active.");
                return;
            }

            if (string.IsNullOrWhiteSpace(number))
            {
                SetStatus("Enter number for call.");
                return;
            }

            // Re-resolve before INVITE: VPN split-DNS may return 198.18.x.x while REGISTER already reached real IP.
            RefreshResolvedServerIp();

            // Формируем адрес назначения для звонка через MikoPBX
            string destination;
            
            // Очищаем номер от лишних символов (пробелы, дефисы, скобки и т.д.)
            string cleanNumber = number.Trim().Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
            bool isLikelyPhone = cleanNumber.Length >= 3 &&
                                 cleanNumber.All(ch => char.IsDigit(ch) || ch == '+');
            
            if (cleanNumber.Contains("@"))
            {
                // Если номер уже содержит @, используем как есть (полный SIP URI)
                if (_useTls && cleanNumber.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
                {
                    destination = "sips:" + cleanNumber.Substring(4);
                }
                else if (!_useTls && cleanNumber.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
                {
                    destination = "sip:" + cleanNumber.Substring(5);
                }
                else
                {
                    destination = cleanNumber.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) || cleanNumber.StartsWith("sips:", StringComparison.OrdinalIgnoreCase)
                        ? cleanNumber
                        : $"{(_useTls ? "sips" : "sip")}:{cleanNumber}";
                }
            }
            else
            {
                // Для MikoPBX используем формат: sip:number@server:port
                // Используем IP-литерал (если резолвили), чтобы обойти VPN split-DNS (198.18.0.0/15).
                string serverPart = GetServerPartForUri();
                
                destination = $"{(_useTls ? "sips" : "sip")}:{cleanNumber}@{serverPart}";

                // Some trunks require explicit phone user parameter for E.164/number routing.
                // Example: sip:+123456789@trunk.example.com;user=phone
                if (isLikelyPhone && !destination.Contains(";user=phone", StringComparison.OrdinalIgnoreCase))
                {
                    destination += ";user=phone";
                }
            }

            var callStartTime = DateTime.Now;
            SetStatus($"Calling {destination}...");
            SetStatus($"[{callStartTime:HH:mm:ss.fff}] Call initiated, sending INVITE...");

            // Новый исходящий звонок: останавливаем осиротевший рекордер (если есть).
            // Это важно для сценария recovery-retry: когда первый INVITE получает 403,
            // callResult=false, _wasCallActive остаётся false — ветка с StopCallRecording
            // не вызывается. Тем не менее аутентифицированный re-INVITE (CSeq=2) может
            // успеть получить 200 OK и создать рекордер до того, как retry-запрос стартует.
            // Без явной остановки этот рекордер будет висеть до конца следующего звонка.
            StopCallRecording();
            _lastRecordingFilePath = null;
            OutboundCallerId = null;

            // Сохраняем номер для использования при старте записи после 200 OK
            _lastCalledNumber = cleanNumber;

            // НЕ начинаем запись здесь - будем начинать только после получения 200 OK (когда абонент ответил)
            // Это предотвращает запись гудков

            try
            {
                // �����: ����� ������������ ���������� Call(string dst, string username, string password, IMediaSession mediaSession, int ringTimeout = 0)
                // Логируем информацию о кодеках и аудио источнике перед звонком
                try
                {
                    var audioCodecsProperty = _voipMediaSession.GetType().GetProperty("AudioCodecs");
                    if (audioCodecsProperty != null)
                    {
                        var codecs = audioCodecsProperty.GetValue(_voipMediaSession) as System.Collections.IEnumerable;
                        if (codecs != null)
                        {
                            var codecNames = new List<string>();
                            foreach (var codec in codecs)
                            {
                                var nameProperty = codec?.GetType().GetProperty("Name");
                                if (nameProperty != null)
                                {
                                    codecNames.Add(nameProperty.GetValue(codec)?.ToString() ?? "unknown");
                                }
                            }
                            SetStatus($"Calling. Codecs: {string.Join(", ", codecNames)}");
                        }
                    }
                    
                    var audioLocalMediaProperty = _voipMediaSession.GetType().GetProperty("AudioLocalMedia");
                    if (audioLocalMediaProperty != null)
                    {
                        var audioLocalMedia = audioLocalMediaProperty.GetValue(_voipMediaSession);
                        if (audioLocalMedia != null)
                        {
                            var audioSourceProperty = audioLocalMedia.GetType().GetProperty("AudioSource");
                            if (audioSourceProperty != null)
                            {
                                var audioSource = audioSourceProperty.GetValue(audioLocalMedia);
                                var audioSourceType = audioSource?.GetType().Name ?? "null";
                                SetStatus($"Audio source type: {audioSourceType}");
                            }
                        }
                    }
                }
                catch (Exception ex2)
                {
                    System.Diagnostics.Debug.WriteLine($"Error logging media info: {ex2.Message}");
                }
                
                var callStartTime2 = DateTime.Now;
                SetStatus($"[{callStartTime2:HH:mm:ss.fff}] Starting Call() method...");
                
                // Start RTP diagnostics for this call
                StartRtpDiagnostics();
                
                // Сбрасываем флаги отмены и отклонения перед новым звонком
                _isCallCancelled = false;
                _outgoingInviteTerminated = false;
                _isCallRejected = false;
                _lastFailureWasConnectTimeout = false;
                _outgoingInviteReceivedSipResponse = false;
                _last487FromLocalCancel = false;
                _lastHandledInvite200OkKey = null;

                // Track final SIP response for this outgoing INVITE (by Call-ID).
                var finalRespTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                var callIdTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_outgoingInviteLock)
                {
                    _pendingOutgoingInviteFinalResponseTcs = finalRespTcs;
                    _pendingOutgoingInviteCallIdTcs = callIdTcs;
                }
                
                // Создаем CancellationTokenSource для возможности отмены звонка
                _callCancellationTokenSource = new System.Threading.CancellationTokenSource();
                
                // Сохраняем задачу звонка для возможности отмены
                var callTask = _userAgent.Call(
                    destination,
                    _username,
                    _password,
                    _voipMediaSession);

                if (callTask == null)
                {
                    throw new InvalidOperationException("SIPUserAgent.Call returned null Task — cannot place call.");
                }

                _activeCallTask = callTask;

                // Avoid hangs: enforce a maximum wait for final SIP response.
                // IMPORTANT: SIPUserAgent.Call() may complete early (false) even while SIP signalling continues
                // (e.g. after provisional responses). We therefore wait for the final SIP response via trace events.
                var maxWait = TimeSpan.FromSeconds(90);
                var connectTimeout = TimeSpan.FromSeconds(10);
                bool callResult = false;
                bool connectFailedEarly = false;
                try
                {
                    var gotCallId = await Task.WhenAny(callIdTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                    if (gotCallId != callIdTcs.Task)
                    {
                        AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}INVITE Call-ID was not observed in time — proceeding with best-effort wait.");
                    }

                    // Give the stack a moment to send INVITE before declaring the server unreachable.
                    if (!callIdTcs.Task.IsCompleted)
                        await Task.WhenAny(callIdTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));

                    if (!WasOutgoingInviteServerReachable())
                    {
                        // Poll until first SIP response (401/100/183…) or connectTimeout — do not use a
                        // single Task.Delay that can fire after provisional responses already arrived.
                        var connectDeadline = DateTime.UtcNow.Add(connectTimeout);
                        while (!WasOutgoingInviteServerReachable()
                               && DateTime.UtcNow < connectDeadline
                               && !finalRespTcs.Task.IsCompleted)
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                        }

                        if (!WasOutgoingInviteServerReachable() && !finalRespTcs.Task.IsCompleted)
                        {
                            _lastInviteFailureStatusCode = 408;
                            _lastFailureWasConnectTimeout = true;
                            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}No SIP response within {connectTimeout.TotalSeconds:F0}s — server unreachable (VPN/DNS?). Target: {GetServerPartForUri()}");
                            ForceCancelOutgoingCall("connect timeout");
                            try { await Task.WhenAny(callTask, Task.Delay(2000)); } catch { }

                            if (_isSecondaryConnection && allowRecoveryRetry)
                            {
                                var prevIp = _resolvedServerIp?.ToString();
                                RefreshResolvedServerIp();

                                IPAddress? retryIp = null;
                                if (_resolvedServerIp != null
                                    && !IsNonRoutableVpnOrVirtualIp(_resolvedServerIp)
                                    && !string.Equals(prevIp, _resolvedServerIp.ToString(), StringComparison.Ordinal))
                                {
                                    retryIp = _resolvedServerIp;
                                }
                                else if (_lastKnownGoodServerIp != null
                                    && !string.Equals(prevIp, _lastKnownGoodServerIp.ToString(), StringComparison.Ordinal))
                                {
                                    _resolvedServerIp = _lastKnownGoodServerIp;
                                    retryIp = _lastKnownGoodServerIp;
                                }

                                if (retryIp != null)
                                {
                                    AppLog.Log($"[SipService] [Connection2] Retrying call using server IP {retryIp}");
                                    _lastFailureWasConnectTimeout = false;
                                    _toneGenerator?.Stop();
                                    StopCallRecording();
                                    StopRtpDiagnostics();
                                    _isCallCancelled = false;
                                    _outgoingInviteTerminated = false;
                                    lock (_outgoingInviteLock)
                                    {
                                        _pendingOutgoingInviteFinalResponseTcs = null;
                                        _pendingOutgoingInviteCallIdTcs = null;
                                    }
                                    _callCancellationTokenSource?.Dispose();
                                    _callCancellationTokenSource = null;
                                    _activeCallTask = null;
                                    await CallInternalAsync(number, allowRecoveryRetry: false);
                                    return;
                                }
                            }

                            connectFailedEarly = true;
                            callResult = false;
                        }
                    }

                    if (!connectFailedEarly)
                    {
                        var completed = await Task.WhenAny(finalRespTcs.Task, Task.Delay(maxWait));
                        if (completed != finalRespTcs.Task)
                        {
                            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}INVITE timed out after {maxWait.TotalSeconds:F0}s without final response — hanging up.");
                            if (_outgoingInviteReceivedSipResponse)
                                _lastInviteFailureStatusCode = 408;
                            ForceCancelOutgoingCall("invite timeout");
                            callResult = false;
                        }
                        else
                        {
                            int finalCode = 0;
                            try { finalCode = await finalRespTcs.Task; } catch { }
                            callResult = finalCode >= 200 && finalCode < 300;
                        }
                    }
                }
                catch (Exception callEx)
                {
                    // Capture fault details (SIPSorcery can throw inside Call task).
                    try
                    {
                        var baseEx = callEx.GetBaseException();
                        AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Call() faulted: {baseEx.GetType().Name}: {baseEx.Message}");
                        if (callTask.Exception != null)
                        {
                            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Call() task exception: {callTask.Exception}");
                        }
                    }
                    catch { }

                    try { ForceCancelOutgoingCall("call task fault"); } catch { }
                    throw;
                }

                var callEndTime = DateTime.Now;
                var callDuration = (callEndTime - callStartTime).TotalSeconds;
                
                // Очищаем CancellationTokenSource после завершения звонка
                _callCancellationTokenSource?.Dispose();
                _callCancellationTokenSource = null;
                _activeCallTask = null;
                lock (_outgoingInviteLock)
                {
                    _pendingOutgoingInviteFinalResponseTcs = null;
                    _pendingOutgoingInviteCallIdTcs = null;
                }

                if (callResult)
                {
                    // Останавливаем ringback tone при успешном подключении
                    _toneGenerator?.Stop();
                    
                    SetStatus($"[{callEndTime:HH:mm:ss.fff}] Call connected! (took {callDuration:F2} seconds)");
                    _wasCallActive = true; // Устанавливаем флаг при успешном подключении
                    
                    // SIP recording disabled (WebRTC-only).
                    
                    // После успешного подключения проверяем, что медиа-сессия запущена
                    try
                    {
                        var startMethod = _voipMediaSession.GetType().GetMethod("Start", BindingFlags.Public | BindingFlags.Instance);
                        if (startMethod != null)
                        {
                            startMethod.Invoke(_voipMediaSession, null);
                            SetStatus("Media session started for outgoing call");
                        }
                        
                        // Получаем медиа-эндпоинты
                        var mediaEndPoints = _audioDevices?.ToMediaEndPoints();
                        
                        // Запускаем AudioSource (микрофон)
                        if (mediaEndPoints?.AudioSource != null)
                        {
                            try
                            {
                                var audioSourceType = mediaEndPoints.AudioSource.GetType().Name;
                                SetStatus($"AudioSource type: {audioSourceType}");
                                
                                // Пытаемся запустить AudioSource
                                var startSourceMethod = mediaEndPoints.AudioSource.GetType().GetMethod("StartAudio", BindingFlags.Public | BindingFlags.Instance);
                                if (startSourceMethod != null)
                                {
                                    var startTask = startSourceMethod.Invoke(mediaEndPoints.AudioSource, null);
                                    if (startTask is Task task)
                                    {
                                        await task;
                                    }
                                    SetStatus("AudioSource started for outgoing call");
                }
                else
                {
                                    // Пробуем Start без параметров
                                    var startMethod2 = mediaEndPoints.AudioSource.GetType().GetMethod("Start", BindingFlags.Public | BindingFlags.Instance);
                                    if (startMethod2 != null)
                                    {
                                        startMethod2.Invoke(mediaEndPoints.AudioSource, null);
                                        SetStatus("AudioSource started (via Start method)");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                SetStatus($"Warning: Could not start AudioSource: {ex.Message}");
                            }
                        }
                        else
                        {
                            SetStatus("WARNING: AudioSource is null - microphone may not work!");
                        }
                        
                        // Запускаем AudioSink (динамик)
                        if (mediaEndPoints?.AudioSink != null)
                        {
                            try
                            {
                                var audioSinkType = mediaEndPoints.AudioSink.GetType().Name;
                                SetStatus($"AudioSink type: {audioSinkType}");
                                
                                var startSinkMethod = mediaEndPoints.AudioSink.GetType().GetMethod("StartAudioSink", BindingFlags.Public | BindingFlags.Instance);
                                if (startSinkMethod != null)
                                {
                                    var startTask = startSinkMethod.Invoke(mediaEndPoints.AudioSink, null);
                                    if (startTask is Task task)
                                    {
                                        await task;
                                    }
                                    SetStatus("AudioSink started for outgoing call");
                                }
                                else
                                {
                                    // Пробуем Start без параметров
                                    var startMethod2 = mediaEndPoints.AudioSink.GetType().GetMethod("Start", BindingFlags.Public | BindingFlags.Instance);
                                    if (startMethod2 != null)
                                    {
                                        startMethod2.Invoke(mediaEndPoints.AudioSink, null);
                                        SetStatus("AudioSink started (via Start method)");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                SetStatus($"Warning: Could not start AudioSink: {ex.Message}");
                            }
                        }
                        else
                        {
                            SetStatus("WARNING: AudioSink is null - audio output may not work!");
                        }

                        TryStartCallRecordingAfterAnswer("outgoing media ready");
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Warning: Could not start media session: {ex.Message}");
                    }
                }
                else
                {
                    // If the carrier rejects INVITE with 403, it is often because registration isn't valid yet
                    // (race on startup) or the trunk needs a fresh REGISTER. Auto-recover once.
                    if (_isSecondaryConnection && allowRecoveryRetry && _lastInviteFailureStatusCode == 403)
                    {
                        var nowUtc = DateTime.UtcNow;
                        if ((nowUtc - _lastInvite403AtUtc) > TimeSpan.FromSeconds(10))
                        {
                            _lastInvite403AtUtc = nowUtc;
                            AppLog.Log("[SipService] [Connection2] INVITE rejected with 403 — restarting registration and retrying call once.");
                            // Останавливаем RTP/тон первой попытки — иначе таймер логирует после Stop из «внутреннего» ретрая.
                            _toneGenerator?.Stop();
                            StopCallRecording();
                            StopRtpDiagnostics();
                            _isCallCancelled = false;
                            _outgoingInviteTerminated = false;
                            try
                            {
                                await StartRegistrationAsync();
                                await Task.Delay(250);
                            }
                            catch (Exception ex)
                            {
                                AppLog.Log($"[SipService] [Connection2] Failed to restart registration after 403: {ex.Message}");
                            }

                            await CallInternalAsync(number, allowRecoveryRetry: false);
                            return;
                        }
                    }

                    // Secondary carrier sometimes returns 404 when number format isn't acceptable.
                    // Auto-retry once by stripping leading '+' (keep E.164 digits) as some trunks expect digits-only.
                    if (_isSecondaryConnection && allowRecoveryRetry && _lastInviteFailureStatusCode == 404)
                    {
                        var retryNumber = number?.Trim();
                        if (!string.IsNullOrWhiteSpace(retryNumber) && retryNumber.StartsWith("+", StringComparison.Ordinal))
                        {
                            var noPlus = retryNumber.TrimStart('+');
                            if (!string.IsNullOrWhiteSpace(noPlus))
                            {
                                AppLog.Log($"[SipService] [Connection2] INVITE rejected with 404 — retrying once without '+': {noPlus}");
                                _toneGenerator?.Stop();
                                StopCallRecording();
                                StopRtpDiagnostics();
                                _isCallCancelled = false;
                                _outgoingInviteTerminated = false;
                                await CallInternalAsync(noPlus, allowRecoveryRetry: false);
                                return;
                            }
                        }
                    }

                    // Финальный отказ: гудки/закрытие окна (Busy tone см. условие внутри Apply).
                    if (IsEarlyMediaAnsweredCall() || ShouldTreatSipFailureAsEarlyMediaAnswered(_lastInviteFailureStatusCode ?? 0))
                    {
                        if (!_earlyMediaAnsweredCall)
                            MarkEarlyMediaAnswered($"INVITE final {_lastInviteFailureStatusCode} after early media");
                        FinalizeEarlyMediaAnsweredCallEnd();
                        AppLog.Log("[SipService] Early media/IVR call ended via INVITE failure response — treated as answered");
                    }
                    else
                    {
                        ApplyOutboundFailureUiTeardown(playBusyTone: true);
                        AppLog.Log("[SipService] Call failed (rejected/timeout), OnCallEnded via ApplyOutboundFailureUiTeardown");
                    }
                }
            }
            catch (System.Threading.Tasks.TaskCanceledException)
            {
                // Звонок был отменен пользователем
                _toneGenerator?.Stop();
                StopRtpDiagnostics();
                _callCancellationTokenSource?.Dispose();
                _callCancellationTokenSource = null;
                _activeCallTask = null;
                SetStatus("Call cancelled by user");
                _wasCallActive = false;
                StopCallRecording();
                UiThread.BeginInvoke(() => OnCallEnded?.Invoke());
            }
            catch (Exception ex)
            {
                // Очищаем CancellationTokenSource при ошибке
                _toneGenerator?.Stop();
                StopRtpDiagnostics();
                _callCancellationTokenSource?.Dispose();
                _callCancellationTokenSource = null;
                _activeCallTask = null;
                SetStatus($"Call error: {ex.Message}");
                _wasCallActive = false;
                StopCallRecording();
                UiThread.BeginInvoke(() => OnCallEnded?.Invoke());
            }
        }

        /// <summary>
        /// Принимает входящий звонок.
        /// </summary>
        public async Task<bool> AnswerIncomingCallAsync()
        {
            SetStatus($"AnswerIncomingCallAsync on service: {_instanceHash}");
            SIPRequest? savedRequest;
            SIPUserAgent? savedUserAgent;
            string? savedCaller;

            lock (this)
            {
                savedRequest = _incomingCallRequest;
                savedUserAgent = _incomingCallUserAgent;
                savedCaller = _incomingCallerNumber;
            }

            SetStatus($"AnswerIncomingCallAsync called. Service hash: {_instanceHash}, UserAgent: {savedUserAgent != null}, Request: {savedRequest != null}, Caller: {savedCaller ?? "null"}");

            if (savedUserAgent == null || savedRequest == null)
            {
                SetStatus("No incoming call to answer (UserAgent or Request is null).");
                return false;
            }

            // КРИТИЧНО: Если была активная медиа-сессия (предыдущий звонок), закрываем её перед новым звонком
            // Это важно для второго и последующих входящих звонков
            if (_wasCallActive && _voipMediaSession != null)
            {
                AppLog.Log("[SipService] Closing previous media session before answering new incoming call");
                try
                {
                    // Закрываем старую медиа-сессию
                    _voipMediaSession.Close("new incoming call");
                    _voipMediaSession = null;
                    AppLog.Log("[SipService] Previous media session closed");
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[SipService] Error closing previous media session: {ex.Message}");
                }
                
                // Закрываем аудио эндпоинт
                try
                {
                    _audioDevices?.Dispose();
                    _audioDevices = null;
                    AppLog.Log("[SipService] Previous audio endpoint closed");
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[SipService] Error closing previous audio endpoint: {ex.Message}");
                }
                
                // Небольшая задержка для полного завершения закрытия
                await System.Threading.Tasks.Task.Delay(200);
                
                // Сбрасываем флаг активного звонка
                _wasCallActive = false;
            }

            // Инициализируем аудио, если оно не было инициализировано или было закрыто
            // force=true: для входящих звонков всегда нужен VoIPMediaSession, даже если WebRTC включен
            if (_voipMediaSession == null)
            {
                try
                {
                    AppLog.Log("[SipService] Initializing audio for incoming call");
                    InitializeAudio(throwOnError: true, force: true);
                }
                catch (Exception ex)
                {
                    SetStatus($"Cannot answer call - audio init failed: {ex.Message}");
                    AppLog.Log($"[SipService] Audio initialization failed: {ex.Message}");
                    return false;
                }
                
                // Оптимизируем микрофон перед ответом на входящий звонок
                try
                {
                    MicrophoneControl.OptimizeForVoIP();
                }
                catch
                {
                    // Игнорируем ошибки оптимизации
                }
            }
            
            // НЕ начинаем запись здесь для входящих звонков - будем начинать только после AcceptCall (когда абонент ответил)
            // Это предотвращает запись гудков
            // Сохраняем номер для использования при старте записи после AcceptCall
            _lastCalledNumber = savedCaller;

            if (_voipMediaSession == null)
            {
                SetStatus("Cannot answer call - media session is null.");
                return false;
            }

            try
            {
                // 1) Принимаем INVITE как UAS (User Agent Server)
                var uas = savedUserAgent.AcceptCall(savedRequest);
                if (uas == null)
                {
                    SetStatus($"Failed to accept call - UAS is null");
                    return false;
                }

                SetStatus($"UAS created, attempting to answer with media session...");
                
                // Recording starts after media session is ready (TryStartCallRecordingAfterAnswer).
                
                // Логируем информацию о кодеках и аудио источнике перед ответом
                try
                {
                    var audioCodecsProperty = _voipMediaSession.GetType().GetProperty("AudioCodecs");
                    if (audioCodecsProperty != null)
                    {
                        var codecs = audioCodecsProperty.GetValue(_voipMediaSession) as System.Collections.IEnumerable;
                        if (codecs != null)
                        {
                            var codecNames = new List<string>();
                            foreach (var codec in codecs)
                            {
                                var nameProperty = codec?.GetType().GetProperty("Name");
                                if (nameProperty != null)
                                {
                                    codecNames.Add(nameProperty.GetValue(codec)?.ToString() ?? "unknown");
                                }
                            }
                            SetStatus($"Answering call. Codecs: {string.Join(", ", codecNames)}");
                        }
                    }
                    
                    var audioLocalMediaProperty = _voipMediaSession.GetType().GetProperty("AudioLocalMedia");
                    if (audioLocalMediaProperty != null)
                    {
                        var audioLocalMedia = audioLocalMediaProperty.GetValue(_voipMediaSession);
                        if (audioLocalMedia != null)
                        {
                            var audioSourceProperty = audioLocalMedia.GetType().GetProperty("AudioSource");
                            if (audioSourceProperty != null)
                            {
                                var audioSource = audioSourceProperty.GetValue(audioLocalMedia);
                                var audioSourceType = audioSource?.GetType().Name ?? "null";
                                SetStatus($"Audio source type: {audioSourceType}");
                            }
                        }
                    }
                }
                catch (Exception ex2)
                {
                    System.Diagnostics.Debug.WriteLine($"Error logging media info: {ex2.Message}");
                }
                
                // 2) Отвечаем с медиа-сессией (SIPSorcery сам сделает SDP, 200 OK, диалог и т.д.)
                bool ok = await savedUserAgent.Answer(uas, _voipMediaSession);
                if (ok)
                {
                    // SRTP offer/answer is handled natively by SIPSorcery.
                }

                if (!ok)
                {
                    SetStatus($"Failed to answer incoming call from {savedCaller} - Answer() returned false");
                    // Пробуем получить больше информации об ошибке
                    try
                    {
                        var mediaEndPoints = _audioDevices?.ToMediaEndPoints();
                        if (mediaEndPoints?.AudioSource == null)
                        {
                            SetStatus($"Audio source is null in media endpoints");
                        }
                        else
                        {
                            SetStatus($"Audio source type: {mediaEndPoints.AudioSource.GetType().Name}");
                        }
                    }
                    catch (Exception ex2)
                    {
                        SetStatus($"Error checking audio source: {ex2.Message}");
                    }
                    return false;
                }

                SetStatus($"Incoming call answered: {savedCaller}");
                _wasCallActive = true;

                // После успешного ответа проверяем, что медиа-сессия запущена
                try
                {
                    var startMethod = _voipMediaSession.GetType().GetMethod("Start", BindingFlags.Public | BindingFlags.Instance);
                    if (startMethod != null)
                    {
                        startMethod.Invoke(_voipMediaSession, null);
                        SetStatus("Media session started for incoming call");
                    }
                    
                    // SIP recording disabled (WebRTC-only).
                    
                    // Получаем медиа-эндпоинты
                    var mediaEndPoints = _audioDevices?.ToMediaEndPoints();
                    
                    // Запускаем AudioSource (микрофон)
                    if (mediaEndPoints?.AudioSource != null)
                    {
                        try
                        {
                            var audioSourceType = mediaEndPoints.AudioSource.GetType().Name;
                            SetStatus($"AudioSource type: {audioSourceType}");
                            
                            // Пытаемся запустить AudioSource
                            var startSourceMethod = mediaEndPoints.AudioSource.GetType().GetMethod("StartAudio", BindingFlags.Public | BindingFlags.Instance);
                            if (startSourceMethod != null)
                            {
                                var startTask = startSourceMethod.Invoke(mediaEndPoints.AudioSource, null);
                                if (startTask is Task task)
                                {
                                    await task;
                                }
                                SetStatus("AudioSource started for incoming call");
                            }
                            else
                            {
                                // Пробуем Start без параметров
                                var startMethod2 = mediaEndPoints.AudioSource.GetType().GetMethod("Start", BindingFlags.Public | BindingFlags.Instance);
                                if (startMethod2 != null)
                                {
                                    startMethod2.Invoke(mediaEndPoints.AudioSource, null);
                                    SetStatus("AudioSource started (via Start method)");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            SetStatus($"Warning: Could not start AudioSource: {ex.Message}");
                        }
                    }
                    else
                    {
                        SetStatus("WARNING: AudioSource is null - microphone may not work!");
                    }
                    
                    // Запускаем AudioSink (динамик)
                    if (mediaEndPoints?.AudioSink != null)
                    {
                        try
                        {
                            var audioSinkType = mediaEndPoints.AudioSink.GetType().Name;
                            SetStatus($"AudioSink type: {audioSinkType}");
                            
                            var startSinkMethod = mediaEndPoints.AudioSink.GetType().GetMethod("StartAudioSink", BindingFlags.Public | BindingFlags.Instance);
                            if (startSinkMethod != null)
                            {
                                var startTask = startSinkMethod.Invoke(mediaEndPoints.AudioSink, null);
                                if (startTask is Task task)
                                {
                                    await task;
                                }
                                SetStatus("AudioSink started for incoming call");
                            }
                            else
                            {
                                // Пробуем Start без параметров
                                var startMethod2 = mediaEndPoints.AudioSink.GetType().GetMethod("Start", BindingFlags.Public | BindingFlags.Instance);
                                if (startMethod2 != null)
                                {
                                    startMethod2.Invoke(mediaEndPoints.AudioSink, null);
                                    SetStatus("AudioSink started (via Start method)");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            SetStatus($"Warning: Could not start AudioSink: {ex.Message}");
                        }
                    }
                    else
                    {
                        SetStatus("WARNING: AudioSink is null - audio output may not work!");
                    }

                    TryStartCallRecordingAfterAnswer("incoming media ready");
                }
                catch (Exception ex)
                {
                    SetStatus($"Warning: Could not start media session: {ex.Message}");
                }

                // Очищаем состояние входящего звонка
                lock (this)
                {
                    _incomingCallUserAgent = null;
                    _incomingCallRequest = null;
                    _incomingCallerNumber = null;
                }
                _incomingCalls.Clear();

                return true;
            }
            catch (Exception ex)
            {
                SetStatus($"Error answering call: {ex.Message}");
                if (ex.InnerException != null)
                {
                    SetStatus($"Inner exception: {ex.InnerException.Message}");
                }
                return false;
            }
        }

        /// <summary>
        /// Отклоняет входящий звонок.
        /// </summary>
        public async Task RejectIncomingCallAsync()
        {
            SIPRequest? savedRequest;
            SIPUserAgent? savedUserAgent;

            lock (this)
            {
                savedRequest = _incomingCallRequest;
                savedUserAgent = _incomingCallUserAgent;
            }

            if (savedUserAgent == null || savedRequest == null)
            {
                SetStatus("No incoming call to reject.");
                return;
            }

            try
            {
                // Always stop any local tones when rejecting an incoming call
                _toneGenerator?.Stop();

                // Устанавливаем флаг отклонения, чтобы запись не начиналась
                _isCallRejected = true;
                
                // Останавливаем запись перед отклонением звонка (на случай, если она уже началась)
                StopCallRecording();
                
                // В SIPSorcery для отклонения входящего звонка нужно использовать AcceptCall и затем Reject на UAS
                // или отправить ответ Busy Here вручную
                var uas = savedUserAgent.AcceptCall(savedRequest);
                if (uas != null)
                {
                    // Используем Reject на UAS для отклонения звонка
                    uas.Reject(SIPResponseStatusCodesEnum.BusyHere, "Rejected by user");
                    SetStatus("Incoming call rejected.");
                }
                else
                {
                    // Fallback: отправляем ответ Busy Here вручную
                    var busyResponse = SIPResponse.GetResponse(savedRequest, SIPResponseStatusCodesEnum.BusyHere, "Rejected by user");
                    if (_sipTransport != null)
                    {
                        await _sipTransport.SendResponseAsync(busyResponse);
                    }
                    SetStatus("Incoming call rejected (manual response).");
                }

                // Best-effort: ensure UA state is not left "active"
                try { _userAgent?.Hangup(); } catch { }
                
                // Останавливаем запись еще раз на всякий случай (если она началась между AcceptCall и Reject)
                StopCallRecording();
                
                // Сбрасываем флаг отклонения после завершения
                _isCallRejected = false;

                lock (this)
                {
                    _incomingCallUserAgent = null;
                    _incomingCallRequest = null;
                    _incomingCallerNumber = null;
                }
                _incomingCalls.Clear();
            }
            catch (Exception ex)
            {
                SetStatus($"Error rejecting call: {ex.Message}");
                // Пробуем отправить ответ вручную в случае ошибки
                try
                {
                    var busyResponse = SIPResponse.GetResponse(savedRequest, SIPResponseStatusCodesEnum.BusyHere, "Rejected by user");
                    if (_sipTransport != null)
                    {
                        await _sipTransport.SendResponseAsync(busyResponse);
                    }
                }
                catch
                {
                    // Игнорируем ошибки при отправке ответа
                }
            }
        }

        /// <summary>
        /// ���������� ��������� ������.
        /// </summary>
        public void Hangup()
        {
            // Всегда останавливаем гудки при Hangup, даже если звонок не активен
            try
            {
                string? attempt, dial, cid;
                lock (_outgoingInviteLock)
                {
                    attempt = _currentOutgoingAttemptId;
                    dial = _currentOutgoingDialNumber;
                    cid = _currentOutgoingCallId;
                }
                AppLog.Log($"[SipService] Hangup() debug: attempt={attempt ?? "<none>"}, dial='{dial ?? "<unknown>"}', lastCallId={cid ?? "<none>"}, IsCallActive={_userAgent?.IsCallActive}");
            }
            catch { }

            _lastCalledNumber = null;

            bool wasActive = _userAgent?.IsCallActive == true;
            if (wasActive)
            {
                try
                {
                    IsMuted = false;
                    MicrophoneControl.SetMute(false);
                }
                catch { }
            }

            ForceCancelOutgoingCall("hangup");

            if (_userAgent == null)
            {
                UiThread.BeginInvoke(() => OnCallEnded?.Invoke());
                return;
            }

            SetStatus(wasActive ? "Call ended" : "Call cancelled");
            UiThread.BeginInvoke(() => OnCallEnded?.Invoke());
        }

        private async System.Threading.Tasks.Task ReinitializeMediaSessionAsync()
        {
            try
            {
                SetStatus("Reinitializing media session...");
                
                ReleaseMediaSessionAndAudio("reinitialize async");

                // Небольшая асинхронная задержка для завершения закрытия
                await System.Threading.Tasks.Task.Delay(200);

                // Full reinitialization with new endpoint
                InitializeAudio(throwOnError: false, force: true);
                
                if (_voipMediaSession != null)
                {
                    SetStatus("Media session reinitialized");
                }
                else
                {
                    SetStatus("Warning: Audio endpoint not available for reinitialization");
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Warning: Failed to reinitialize media session: {ex.Message}");
                // Не пробрасываем исключение, чтобы не блокировать завершение звонка
            }
        }

        /// <summary>
        /// Starts RTP diagnostics collection for the current call
        /// </summary>
        private void StartRtpDiagnostics()
        {
            _rtpPacketsReceived = 0;
            LastCallInboundRtpPackets = null;
            _rtpSequenceGaps = 0;
            _rtpLastSequenceNumber = -1;
            _rtpPayloadTypeCounts.Clear();
            _rtpTotalBytesReceived = 0;
            _rtpStatsStartTime = DateTime.UtcNow;
            _earlyMediaAudioDetected = false;
            _earlyMediaAnsweredCall = false;
            _earlyMediaAnswerTime = null;
            _localRingbackFallbackActive = false;
            
            // Subscribe to RTP packets
            if (_voipMediaSession != null)
            {
                _voipMediaSession.OnRtpPacketReceived += DiagnosticRtpPacketReceived;
            }
            
            // Log stats every 5 seconds
            _rtpStatsTimer = new System.Threading.Timer(LogRtpStats, null, 5000, 5000);
            
            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}RTP diagnostics started");
        }
        
        /// <summary>
        /// Stops RTP diagnostics and logs final summary
        /// </summary>
        private void StopRtpDiagnostics()
        {
            _rtpStatsTimer?.Dispose();
            _rtpStatsTimer = null;
            
            if (_voipMediaSession != null)
            {
                try { _voipMediaSession.OnRtpPacketReceived -= DiagnosticRtpPacketReceived; } catch { }
            }
            
            // Close diagnostic WAV writer
            lock (_diagnosticWavLock)
            {
                try { _diagnosticWavWriter?.Dispose(); } catch { }
                _diagnosticWavWriter = null;
            }
            
            var duration = (DateTime.UtcNow - _rtpStatsStartTime).TotalSeconds;
            var connLabel = _isSecondaryConnection ? "[Connection2] " : "";
            AppLog.Log($"[SipService] {connLabel}=== RTP DIAGNOSTICS SUMMARY ===");
            AppLog.Log($"[SipService] {connLabel}  Duration: {duration:F1}s");
            AppLog.Log($"[SipService] {connLabel}  Total packets received: {_rtpPacketsReceived}");
            AppLog.Log($"[SipService] {connLabel}  Total bytes received: {_rtpTotalBytesReceived}");
            AppLog.Log($"[SipService] {connLabel}  Sequence gaps: {_rtpSequenceGaps}");
            AppLog.Log($"[SipService] {connLabel}  Expected packets (50/sec): {(int)(duration * 50)}");
            AppLog.Log($"[SipService] {connLabel}  Packet loss: {Math.Max(0, (int)(duration * 50) - _rtpPacketsReceived)} packets");
            
            LastCallInboundRtpPackets = _rtpPacketsReceived;
            
            if (_rtpPayloadTypeCounts.Count > 0)
            {
                var ptCounts = string.Join(", ", _rtpPayloadTypeCounts.Select(kv => $"PT{kv.Key}={kv.Value}"));
                AppLog.Log($"[SipService] {connLabel}  Payload types: {ptCounts}");
                
                // Check if non-negotiated payload types were received
                foreach (var pt in _rtpPayloadTypeCounts)
                {
                    if (pt.Key != _rtpNegotiatedPayloadType && pt.Key != 101) // 101 = telephone-event
                    {
                        AppLog.Log($"[SipService] {connLabel}  ⚠ WARNING: Received PT{pt.Key} ({pt.Value} packets) but negotiated codec is PT{_rtpNegotiatedPayloadType}!");
                    }
                }
            }
            AppLog.Log($"[SipService] {connLabel}=== END RTP DIAGNOSTICS ===");
        }
        
        /// <summary>
        /// Handles each received RTP packet for diagnostics
        /// </summary>
        private void DiagnosticRtpPacketReceived(IPEndPoint remoteEndPoint, SDPMediaTypesEnum mediaType, SIPSorcery.Net.RTPPacket rtpPacket)
        {
            if (mediaType != SDPMediaTypesEnum.audio) return;
            
            var header = rtpPacket.Header;
            int pt = header.PayloadType;
            int seq = (int)header.SequenceNumber;
            int payloadLen = rtpPacket.Payload?.Length ?? 0;
            
            System.Threading.Interlocked.Increment(ref _rtpPacketsReceived);
            System.Threading.Interlocked.Add(ref _rtpTotalBytesReceived, payloadLen);
            
            // Track payload types
            lock (_rtpPayloadTypeCounts)
            {
                if (_rtpPayloadTypeCounts.ContainsKey(pt))
                    _rtpPayloadTypeCounts[pt]++;
                else
                    _rtpPayloadTypeCounts[pt] = 1;
            }
            
            // Check sequence gaps
            if (_rtpLastSequenceNumber >= 0)
            {
                int expectedSeq = (_rtpLastSequenceNumber + 1) & 0xFFFF;
                if (seq != expectedSeq)
                {
                    int gap = (seq - _rtpLastSequenceNumber + 65536) % 65536;
                    if (gap > 1 && gap < 1000) // Ignore wrap-around
                    {
                        System.Threading.Interlocked.Add(ref _rtpSequenceGaps, gap - 1);
                    }
                }
            }
            _rtpLastSequenceNumber = seq;
            
            // Log first packet
            if (_rtpPacketsReceived == 1)
            {
                var connLabel = _isSecondaryConnection ? "[Connection2] " : "";
                AppLog.Log($"[SipService] {connLabel}✓ FIRST RTP packet: PT={pt}, seq={seq}, len={payloadLen}, from={remoteEndPoint}");
                // Do NOT stop ringback fallback just because RTP exists — some carriers send silent early media first.
            }

            // Detect if early media contains real audio (ringback/IVR). Once detected, stop any local ringback fallback.
            // Keep this lightweight: decode only occasionally until we detect audio.
            if (!_earlyMediaAudioDetected &&
                pt == _rtpNegotiatedPayloadType &&
                _audioEncoder != null &&
                _rtpPacketsReceived <= 250 && // focus on early call setup
                (_rtpPacketsReceived % 25 == 1)) // sample roughly twice a second at 50pps
            {
                try
                {
                    if (rtpPacket.Payload != null && rtpPacket.Payload.Length > 0)
                    {
                        var matchingFormats = _audioEncoder.SupportedFormats.Where(f => f.FormatID == pt).ToList();
                        if (matchingFormats.Count > 0)
                        {
                            var pcm = _audioEncoder.DecodeAudio(rtpPacket.Payload, matchingFormats[0]);
                            if (pcm != null && pcm.Length > 0)
                            {
                                int maxAmp = 0;
                                for (int i = 0; i < pcm.Length; i++)
                                {
                                    int a = pcm[i];
                                    if (a < 0) a = -a;
                                    if (a > maxAmp) maxAmp = a;
                                }

                                // Threshold tuned for 16-bit PCM: near-zero is silence/comfort noise.
                                if (maxAmp >= 250)
                                {
                                    _earlyMediaAudioDetected = true;
                                    try { _earlyMediaRingbackFallbackCts?.Cancel(); } catch { }
                                    try { _toneGenerator?.Stop(); } catch { }
                                    _localRingbackFallbackActive = false;
                                    var connLabel = _isSecondaryConnection ? "[Connection2] " : "";
                                    AppLog.Log($"[SipService] {connLabel}Early media audio detected (maxAmp={maxAmp}) — stopping local ringback fallback.");
                                    TryMarkEarlyMediaAnsweredIfReady();
                                }
                            }
                        }
                    }
                }
                catch { }
            }
            else if (_earlyMediaAudioDetected && !_earlyMediaAnsweredCall)
            {
                TryMarkEarlyMediaAnsweredIfReady();
            }
            
            // Write decoded audio to diagnostic WAV file (first 10 seconds only)
            if (_rtpPacketsReceived <= 500 && pt == _rtpNegotiatedPayloadType && _audioEncoder != null)
            {
                try
                {
                    lock (_diagnosticWavLock)
                    {
                        if (_diagnosticWavWriter == null)
                        {
                            var wavPath = Path.Combine(Path.GetTempPath(), $"callspire_diag_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                            _diagnosticWavWriter = new WavFileWriter(wavPath, sampleRate: 8000, channels: 1);
                            AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Diagnostic WAV: {wavPath}");
                        }
                        
                        if (rtpPacket.Payload != null && rtpPacket.Payload.Length > 0)
                        {
                            // Get the negotiated format from AudioEncoder
                            var matchingFormats = _audioEncoder.SupportedFormats.Where(f => f.FormatID == pt).ToList();
                            
                            if (matchingFormats.Count > 0)
                            {
                                var format = matchingFormats[0];
                                var pcm = _audioEncoder.DecodeAudio(rtpPacket.Payload, format);
                                if (pcm != null && pcm.Length > 0)
                                {
                                    // Convert short[] to byte[]
                                    var bytes = new byte[pcm.Length * 2];
                                    Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                                    _diagnosticWavWriter.Write(bytes, 0, bytes.Length);
                                    
                                    // Log audio level for first few packets
                                    if (_rtpPacketsReceived <= 5)
                                    {
                                        int maxAmplitude = pcm.Max(s => Math.Abs(s));
                                        double rms = Math.Sqrt(pcm.Average(s => (double)s * s));
                                        AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}RTP audio level: max={maxAmplitude}, rms={rms:F0}, samples={pcm.Length}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_rtpPacketsReceived <= 5)
                        AppLog.Log($"[SipService] Diagnostic WAV error: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Logs periodic RTP statistics
        /// </summary>
        private void LogRtpStats(object? state)
        {
            var connLabel = _isSecondaryConnection ? "[Connection2] " : "";
            var duration = (DateTime.UtcNow - _rtpStatsStartTime).TotalSeconds;
            var pps = duration > 0 ? _rtpPacketsReceived / duration : 0;
            
            string ptInfo;
            lock (_rtpPayloadTypeCounts)
            {
                ptInfo = string.Join(",", _rtpPayloadTypeCounts.Select(kv => $"PT{kv.Key}:{kv.Value}"));
            }
            
            AppLog.Log($"[SipService] {connLabel}RTP stats: {_rtpPacketsReceived} pkts ({pps:F1}/s), gaps={_rtpSequenceGaps}, bytes={_rtpTotalBytesReceived}, PT=[{ptInfo}]");
        }

        /// <summary>
        /// Reference equality comparer для избежания циклов при рекурсивном поиске
        /// </summary>
        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        /// <summary>
        /// Рекурсивный поиск объекта заданного типа в графе объектов
        /// </summary>
        private object? FindByType(object? root, Type target, int maxDepth, out string foundPath)
        {
            foundPath = "";
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            return FindByTypeInner(root, target, maxDepth, "root", visited, out foundPath);
        }

        /// <summary>
        /// Внутренний рекурсивный поиск
        /// </summary>
        private object? FindByTypeInner(object? obj, Type target, int depth, string path,
            HashSet<object> visited, out string foundPath)
        {
            foundPath = "";

            if (obj == null || depth < 0) return null;

            var t = obj.GetType();
            if (target.IsAssignableFrom(t))
            {
                foundPath = path + $" ({t.FullName})";
                return obj;
            }

            // avoid cycles
            if (!t.IsValueType)
            {
                if (visited.Contains(obj)) return null;
                visited.Add(obj);
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // fields
            foreach (var f in t.GetFields(flags))
            {
                object? v = null;
                try { v = f.GetValue(obj); } catch { }
                if (v == null) continue;

                var res = FindByTypeInner(v, target, depth - 1, $"{path}.{f.Name}", visited, out foundPath);
                if (res != null) return res;
            }

            // properties (safe subset)
            foreach (var p in t.GetProperties(flags))
            {
                if (!p.CanRead) continue;
                if (p.GetIndexParameters().Length > 0) continue;

                object? v = null;
                try { v = p.GetValue(obj); } catch { }
                if (v == null) continue;

                var res = FindByTypeInner(v, target, depth - 1, $"{path}.{p.Name}", visited, out foundPath);
                if (res != null) return res;
            }

            return null;
        }

        /// <summary>
        /// Пытается подписаться на отправку outbound RTP пакетов через reflection
        /// Вызывается после начала звонка, когда RTP session уже создан
        /// </summary>
        private void TrySubscribeToOutboundRtp()
        {
            AppLog.Log("[SipService] TrySubscribeToOutboundRtp called");

            if (_voipMediaSession == null)
            {
                AppLog.Log("[SipService] voipMediaSession is null");
                return;
            }

            try
            {
                var rtpChannelType = typeof(SIPSorcery.Net.RTPChannel);
                var rtpSessionType = Type.GetType("SIPSorcery.Net.RtpSession, SIPSorcery") 
                                     ?? Type.GetType("SIPSorcery.Net.RTPSession, SIPSorcery");

                string path;

                // 1) ищем RTPChannel, начиная с AudioStream
                var audioStreamProp = _voipMediaSession.GetType().GetProperty("AudioStream", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (audioStreamProp != null)
                {
                    var audioStream = audioStreamProp.GetValue(_voipMediaSession);
                    if (audioStream != null)
                    {
                        var ch = FindByType(audioStream, rtpChannelType, 6, out path);
                        if (ch != null)
                        {
                            AppLog.Log($"[SipService] ✓ FOUND RTPChannel in AudioStream: {path}");
                            TrySubscribeToRtpChannel(ch);
                            return;
                        }
                        else
                        {
                            AppLog.Log("[SipService] RTPChannel not found in AudioStream graph");
                        }
                    }
                }

                // 2) ищем RTPChannel, начиная с MediaEndPoints
                var mediaProp = _voipMediaSession.GetType().GetProperty("Media", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mediaProp != null)
                {
                    var media = mediaProp.GetValue(_voipMediaSession);
                    if (media != null)
                    {
                        var ch2 = FindByType(media, rtpChannelType, 6, out path);
                        if (ch2 != null)
                        {
                            AppLog.Log($"[SipService] ✓ FOUND RTPChannel in MediaEndPoints: {path}");
                            TrySubscribeToRtpChannel(ch2);
                            return;
                        }
                        else
                        {
                            AppLog.Log("[SipService] RTPChannel not found in MediaEndPoints graph");
                        }
                    }
                }

                // 3) если удалось достать RTPSession — тоже логируем путь
                if (rtpSessionType != null)
                {
                    var s = FindByType(_voipMediaSession, rtpSessionType, 6, out path);
                    if (s != null)
                    {
                        AppLog.Log($"[SipService] ✓ FOUND RTPSession: {path}");
                        // Раньше здесь была попытка подписаться на RtpSession событиями,
                        // но текущее решение использует TapAudioSource + RtpCallRecorder,
                        // поэтому дополнительная подписка не требуется.
                        return;
                    }
                    else
                    {
                        AppLog.Log("[SipService] RTPSession not found in VoIPMediaSession graph");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Error subscribing to outbound RTP: {ex.Message}, stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Пытается найти RTP канал внутри MediaStreamTrack
        /// </summary>
        private void TrySubscribeToMediaStreamTrack(object mediaStreamTrack)
        {
            try
            {
                var trackType = mediaStreamTrack.GetType();
                AppLog.Log($"[SipService] MediaStreamTrack type: {trackType.FullName}");
                
                // Ищем поля и свойства, которые могут содержать RTP канал или session
                var fields = trackType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                var properties = trackType.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                
                AppLog.Log($"[SipService] MediaStreamTrack fields: {string.Join(", ", fields.Select(f => $"{f.Name}:{f.FieldType.Name}"))}");
                AppLog.Log($"[SipService] MediaStreamTrack properties: {string.Join(", ", properties.Select(p => $"{p.Name}:{p.PropertyType.Name}"))}");
                
                // Ищем RTP канал или session
                foreach (var field in fields)
                {
                    if (field.FieldType.Name.Contains("Rtp") || field.FieldType.Name.Contains("RTP") || 
                        field.Name.Contains("Rtp") || field.Name.Contains("rtp") ||
                        field.Name.Contains("Channel") || field.Name.Contains("channel"))
                    {
                        var value = field.GetValue(mediaStreamTrack);
                        if (value != null)
                        {
                            AppLog.Log($"[SipService] Found potential RTP channel in MediaStreamTrack: {field.Name}, type: {value.GetType().FullName}");
                            if (value.GetType().Name.Contains("RTPChannel") || value.GetType().Name.Contains("RtpChannel"))
                            {
                                TrySubscribeToRtpChannel(value);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Error in TrySubscribeToMediaStreamTrack: {ex.Message}");
            }
        }

        // Храним оригинальные методы для перехвата
        private object? _rtpChannelInstance = null;
        private MethodInfo? _originalSendMethod = null;

        /// <summary>
        /// Пытается подписаться на события отправки RTP через RTPChannel
        /// </summary>
        private void TrySubscribeToRtpChannel(object rtpChannel)
        {
            try
            {
                var channelType = rtpChannel.GetType();
                AppLog.Log($"[SipService] RTPChannel type: {channelType.FullName}");
                
                // Ищем события отправки RTP
                var events = channelType.GetEvents(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                AppLog.Log($"[SipService] RTPChannel events ({events.Length}): {string.Join(", ", events.Select(e => e.Name))}");
                
                // Ищем ВСЕ методы (не только с "Send" в имени)
                var methods = channelType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                
                // Ищем методы, которые могут отправлять RTP (Send, SendRtp, SendPacket, Write, SendTo и т.д.)
                var sendMethods = methods.Where(m => 
                    (m.Name.Contains("Send") || m.Name.Contains("Write") || m.Name.Contains("SendTo")) &&
                    m.GetParameters().Length > 0 &&
                    !m.Name.Contains("Received") &&
                    !m.IsSpecialName).ToList();
                
                AppLog.Log($"[SipService] RTPChannel all methods ({methods.Length}): {string.Join(", ", methods.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Take(3).Select(p => p.ParameterType.Name))})"))}");
                AppLog.Log($"[SipService] RTPChannel potential send methods ({sendMethods.Count}): {string.Join(", ", sendMethods.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})"))}");
                
                // Пробуем найти событие OnRtpPacketSent или аналогичное
                var onRtpPacketSentEvent = events.FirstOrDefault(e => 
                    e.Name.Contains("RtpPacketSent") || 
                    e.Name.Contains("RtpSent") || 
                    e.Name.Contains("OnSend") ||
                    e.Name.Contains("PacketSent") ||
                    e.Name.Contains("RtpSend"));
                
                if (onRtpPacketSentEvent != null)
                {
                    AppLog.Log($"[SipService] Found RTP send event: {onRtpPacketSentEvent.Name}, type: {onRtpPacketSentEvent.EventHandlerType?.FullName}");
                    TrySubscribeToRtpEvent(rtpChannel, onRtpPacketSentEvent);
                }
                
                // Пробуем подписаться на OnRTPDataReceived и фильтровать по направлению
                var onRtpDataReceivedEvent = events.FirstOrDefault(e => e.Name == "OnRTPDataReceived" || e.Name.Contains("RTPDataReceived"));
                if (onRtpDataReceivedEvent != null)
                {
                    AppLog.Log($"[SipService] Found OnRTPDataReceived event, but this is for INBOUND data. Need to find OUTBOUND path.");
                }
                
                // Если событие не найдено, пробуем перехватить через методы Send
                if (onRtpPacketSentEvent == null && sendMethods.Count > 0)
                {
                    AppLog.Log("[SipService] No RTP send event found, trying to intercept Send method...");
                    TryInterceptSendMethod(rtpChannel, sendMethods);
                }
                
                // Пробуем найти поля, которые могут содержать socket или transport для отправки
                var fields = channelType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                var socketFields = fields.Where(f => 
                    f.FieldType.Name.Contains("Socket") || 
                    f.FieldType.Name.Contains("Udp") ||
                    f.Name.Contains("socket") ||
                    f.Name.Contains("Socket") ||
                    f.Name.Contains("transport") ||
                    f.Name.Contains("Udp")).ToList();
                
                if (socketFields.Count > 0)
                {
                    AppLog.Log($"[SipService] RTPChannel socket/transport fields ({socketFields.Count}): {string.Join(", ", socketFields.Select(f => $"{f.Name}:{f.FieldType.Name}"))}");
                    
                    // Пробуем перехватить отправку через socket
                    foreach (var socketField in socketFields)
                    {
                        var socket = socketField.GetValue(rtpChannel);
                        if (socket != null)
                        {
                            AppLog.Log($"[SipService] Found socket/transport: {socketField.Name}, type: {socket.GetType().FullName}");
                            TryInterceptSocketSend(socket);
                        }
                    }
                }
                else
                {
                    AppLog.Log("[SipService] No socket/transport fields found in RTPChannel");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Error in TrySubscribeToRtpChannel: {ex.Message}, stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Пытается подписаться на событие отправки RTP
        /// </summary>
        private void TrySubscribeToRtpEvent(object rtpChannel, EventInfo eventInfo)
        {
            try
            {
                var handlerType = eventInfo.EventHandlerType;
                if (handlerType == null)
                {
                    AppLog.Log("[SipService] Event handler type is null");
                    return;
                }

                AppLog.Log($"[SipService] Event handler type: {handlerType.FullName}");
                
                // Пробуем создать обработчик события
                // Обычно это Action<RtpPacket> или EventHandler<RtpPacketEventArgs>
                if (handlerType == typeof(Action<SIPSorcery.Net.RTPPacket>))
                {
                    Action<SIPSorcery.Net.RTPPacket> handler = (packet) =>
                    {
                        OnOutboundRtpPacket(packet);
                    };
                    eventInfo.AddEventHandler(rtpChannel, handler);
                    AppLog.Log("[SipService] ✓ Subscribed to RTP send event (Action<RTPPacket>) - outbound RTP interception active!");
                }
                else if (handlerType.IsGenericType && handlerType.GetGenericTypeDefinition() == typeof(Action<>))
                {
                    var genericArg = handlerType.GetGenericArguments()[0];
                    AppLog.Log($"[SipService] Event is Action<{genericArg.Name}>");
                    // TODO: Попробовать создать обработчик для других типов
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Error subscribing to RTP event: {ex.Message}");
            }
        }

        /// <summary>
        /// Пытается перехватить отправку через socket
        /// </summary>
        private void TryInterceptSocketSend(object socket)
        {
            try
            {
                var socketType = socket.GetType();
                AppLog.Log($"[SipService] Socket type: {socketType.FullName}");
                
                // Ищем методы SendTo, Send, SendAsync в socket
                var methods = socketType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var sendMethods = methods.Where(m => 
                    (m.Name.Contains("Send") || m.Name.Contains("SendTo")) &&
                    m.GetParameters().Length > 0).ToList();
                
                AppLog.Log($"[SipService] Socket send methods ({sendMethods.Count}): {string.Join(", ", sendMethods.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Take(3).Select(p => p.ParameterType.Name))})"))}");
                
                // Пробуем найти событие отправки
                var events = socketType.GetEvents(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                AppLog.Log($"[SipService] Socket events ({events.Length}): {string.Join(", ", events.Select(e => e.Name))}");
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Error in TryInterceptSocketSend: {ex.Message}");
            }
        }

        /// <summary>
        /// Пытается перехватить метод Send через reflection
        /// </summary>
        private void TryInterceptSendMethod(object rtpChannel, List<MethodInfo> sendMethods)
        {
            try
            {
                // Ищем метод Send, который принимает RTPPacket или byte[] и IPEndPoint
                var sendMethod = sendMethods.FirstOrDefault(m =>
                {
                    var parameters = m.GetParameters();
                    return parameters.Length >= 2 &&
                           (parameters[0].ParameterType == typeof(SIPSorcery.Net.RTPPacket) ||
                            parameters[0].ParameterType == typeof(byte[])) &&
                           parameters[1].ParameterType == typeof(IPEndPoint);
                });

                if (sendMethod != null)
                {
                    AppLog.Log($"[SipService] Found Send method: {sendMethod.Name}, parameters: {string.Join(", ", sendMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))}");
                    
                    // Сохраняем ссылку на канал и метод
                    _rtpChannelInstance = rtpChannel;
                    _originalSendMethod = sendMethod;
                    
                    AppLog.Log("[SipService] Send method found - will try to intercept via wrapper");
                    // Примечание: Полный перехват через reflection требует более сложной логики
                    // Пока просто логируем, что метод найден
                }
                else
                {
                    AppLog.Log("[SipService] Suitable Send method not found for interception");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Error intercepting Send method: {ex.Message}");
            }
        }

        /// <summary>
        /// Обработчик outbound RTP пакета
        /// </summary>
        private void OnOutboundRtpPacket(SIPSorcery.Net.RTPPacket packet)
        {
            if (!IsCallRecordingEnabled() || packet == null)
                return;
                
            // Получаем ссылку на рекордер под lock для thread-safety
            IRtpCallRecorder? recorder;
            lock (_recorderLock)
            {
                recorder = _rtpCallRecorder;
            }
            
            if (recorder == null)
                return;
                
            try
            {
                // Извлекаем payload из RTP пакета
                byte[] payload = packet.Payload;
                if (payload == null || payload.Length == 0)
                    return;
                    
                // Получаем payload type из заголовка RTP
                byte payloadType = (byte)(packet.Header.PayloadType & 0x7F);
                
                // Определяем sample rate на основе payload type
                int sourceRateHz = payloadType switch
                {
                    0 => 8000,  // PCMU (G.711 μ-law)
                    8 => 8000,  // PCMA (G.711 A-law)
                    9 => 16000, // G.722
                    111 => 48000, // Opus
                    _ => 8000   // По умолчанию
                };
                
                // Записываем outbound RTP payload
                recorder.ProcessOutboundRtpPayload(payloadType, payload, sourceRateHz);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error processing outbound RTP packet: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                _toneGenerator?.Dispose();
                _toneGenerator = null;
            }
            catch
            {
                // ignore
            }

            try
            {
                _regUserAgent?.Stop();
            }
            catch
            {
                // ignore
            }

            try
            {
                _userAgent?.Hangup();
                _userAgent?.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                _voipMediaSession?.Close("dispose");
            }
            catch
            {
                // ignore
            }

            try { _audioDevices?.Dispose(); } catch { }
            _audioDevices = null;
            
            // Останавливаем и освобождаем рекордер
            try
            {
                lock (_recorderLock)
                {
                    if (_rtpCallRecorder != null)
                    {
                        // Останавливаем запись синхронно (с таймаутом) при Dispose
                        var stopTask = _rtpCallRecorder.StopRecordingAsync(DateTime.Now);
                        if (!stopTask.IsCompleted)
                        {
                            // Ждем максимум 2 секунды, затем продолжаем в фоне
                            if (!stopTask.Wait(TimeSpan.FromSeconds(2)))
                            {
                                _ = Task.Run(async () =>
                                {
                                    try { await stopTask; } catch { }
                                });
                            }
                        }
                        _rtpCallRecorder = null;
                    }
                }
            }
            catch
            {
                // ignore - не бросаем исключения из Dispose
            }

            try
            {
                _sipTransport?.Shutdown();
            }
            catch
            {
                // ignore
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
                // Игнорируем ошибки чтения настроек, по умолчанию запись выключена
            }
            return false;
        }
        
        /// <summary>
        /// Получает путь к папке Recordings в %LOCALAPPDATA%\Callspire\Recordings
        /// </summary>
        private string GetRecordingsDirectory()
        {
            return AppDataHelper.GetRecordingsDirectory();
        }
        
        /// <summary>
        /// Записывает inbound RTP пакет (удаленная сторона)
        /// </summary>
        public void RecordInboundRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[]? payload)
        {
            if (!IsCallRecordingEnabled() || payload == null || payload.Length == 0)
                return;
                
            // Получаем ссылку на рекордер под lock для thread-safety
            IRtpCallRecorder? recorder;
            lock (_recorderLock)
            {
                recorder = _rtpCallRecorder;
            }
            
            if (recorder == null)
                return;
                
            try
            {
                recorder.ProcessInboundRtp(remoteEndPoint, ssrc, seqnum, timestamp, payloadID, marker, payload);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error recording inbound RTP: {ex.Message}");
            }
        }
        

        /// <summary>
        /// Records raw 48 kHz mono PCM samples from the microphone (full bandwidth, before codec encoding).
        /// Called from WasapiAudioEndPoint.OnRawPcmFrameTap on the capture thread.
        /// </summary>
        private void RecordOutboundRawPcm(short[] pcm48k)
        {
            IRtpCallRecorder? recorder;
            lock (_recorderLock)
            {
                recorder = _rtpCallRecorder;
            }
            
            if (recorder == null)
                return;
                
            try
            {
                recorder.ProcessOutboundRawPcm48k(pcm48k);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error recording outbound raw PCM: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Записывает outbound PCM данные (микрофон) для записи
        /// Устаревший метод - теперь используется ProcessOutboundSamples напрямую из TapAudioSource
        /// </summary>
        [Obsolete("Use ProcessOutboundSamples from TapAudioSource instead")]
        public void RecordOutboundPcm(byte[] pcmData, int sampleRate)
        {
            // Метод оставлен для обратной совместимости, но не используется
            // Запись происходит напрямую через TapAudioSource.OnTapRawSample -> ProcessOutboundSamples
        }

        /// <summary>
        /// Настраивает кодеки для медиа-сессии
        /// </summary>

        /// <summary>
        /// Записывает outbound RTP пакет (локальная сторона)
        /// </summary>

        /// <summary>
        /// Запускает запись звонка
        /// </summary>
        private void StartCallRecording(string phoneNumber, DateTime callStartTime)
        {
            // SIP recording disabled (WebRTC-only).
        }
        
        /// <summary>
        /// Starts call recording once media endpoints are running (after 200 OK / AcceptCall media start).
        /// Idempotent — safe to call from media-ready path and ignored if recorder already exists.
        /// </summary>
        private void TryStartCallRecordingAfterAnswer(string context)
        {
            if (_isCallCancelled || _isCallRejected || _outgoingInviteTerminated)
                return;

            lock (_recorderLock)
            {
                if (_rtpCallRecorder == null && _isStoppingRecording)
                {
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Previous recording stop in progress before {context}, allowing new recorder.");
                    _isStoppingRecording = false;
                }

                if (!IsCallRecordingEnabled() || _rtpCallRecorder != null || _voipMediaSession == null)
                    return;

                string? phoneNumber = _lastCalledNumber;
                if (string.IsNullOrEmpty(phoneNumber))
                    return;

                try
                {
                    _sipRecordingStopHadNoOutputFile = false;
                    _rtpCallRecorder = RtpCallRecorderFactory.Create?.Invoke();
                    _rtpCallRecorder?.StartRecording(phoneNumber, DateTime.Now, GetRecordingsDirectory());
                    _lastRecordingFilePath = null;
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Call recording started for {phoneNumber} ({context})");
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Error starting call recording ({context}): {ex.Message}");
                    _rtpCallRecorder = null;
                }
            }
        }

        /// <summary>
        /// Останавливает запись звонка и сохраняет файл
        /// Использует lock для защиты от race conditions при множественных звонках
        /// Защищен от множественных вызовов через флаг _isStoppingRecording
        /// </summary>
        private void StopCallRecording()
        {
            IRtpCallRecorder? recorderToStop;
            string? expectedRecordingPath;
            lock (_recorderLock)
            {
                if (_isStoppingRecording || _rtpCallRecorder == null)
                    return;

                _isStoppingRecording = true;
                recorderToStop = _rtpCallRecorder;
                expectedRecordingPath = recorderToStop?.RecordingFilePath;
                if (recorderToStop?.RecordingFilePath != null)
                    _lastRecordingFilePath = recorderToStop.RecordingFilePath;
                _rtpCallRecorder = null;
                _recordingFinalizeTask = StopCallRecordingCoreAsync(recorderToStop, expectedRecordingPath);
            }
        }

        private async Task StopCallRecordingCoreAsync(IRtpCallRecorder? recorderToStop, string? expectedRecordingPath)
        {
            bool finalizeAttempted = false;

            try
            {
                if (recorderToStop != null && recorderToStop.IsRecording)
                {
                    finalizeAttempted = true;
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Stopping call recording...");
                    await recorderToStop.StopRecordingAsync(DateTime.Now).ConfigureAwait(false);

                    string finalPath = recorderToStop.RecordingFilePath ?? expectedRecordingPath ?? "null";
                    AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Call recording stopped. File: {finalPath}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error stopping call recording: {ex.Message}");
            }
            finally
            {
                bool produced = false;
                lock (_recorderLock)
                {
                    _isStoppingRecording = false;

                    if (finalizeAttempted && !string.IsNullOrEmpty(expectedRecordingPath))
                    {
                        produced = File.Exists(expectedRecordingPath) && new FileInfo(expectedRecordingPath).Length > 0;
                        if (!produced)
                        {
                            produced = (RtpCallRecorderFactory.TryRecoverWavFromOrphanPcm?.Invoke(expectedRecordingPath) ?? false);
                            if (produced)
                                AppLog.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Recovered WAV after stop: {expectedRecordingPath}");
                        }

                        _sipRecordingStopHadNoOutputFile = !produced;
                        if (!produced && string.Equals(_lastRecordingFilePath, expectedRecordingPath, StringComparison.OrdinalIgnoreCase))
                            _lastRecordingFilePath = null;
                    }
                    else
                    {
                        _sipRecordingStopHadNoOutputFile = false;
                    }

                    _recordingFinalizeTask = null;
                }

                if (produced && !string.IsNullOrEmpty(expectedRecordingPath))
                {
                    string finalizedPath = expectedRecordingPath;
                    UiThread.BeginInvoke(() => OnRecordingFinalized?.Invoke(finalizedPath));
                }
            }
        }

        /// <summary>Stops recording/RTP and notifies UI — use instead of bare OnCallEnded when a call may have been answered.</summary>
        private void EndCallAndNotifyUi()
        {
            StopCallRecording();
            StopRtpDiagnostics();
            UiThread.BeginInvoke(() => OnCallEnded?.Invoke());
        }
        
        /// <summary>
        /// Проверяет, включён ли WebRTC режим для указанного подключения.
        /// secondary=false читает MainConnectionTransport (с fallback на UseWebRtcAudio).
        /// secondary=true читает SecondaryConnectionTransport.
        /// </summary>
        private bool ShouldUseWebRtcFor(bool secondary)
        {
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (!File.Exists(settingsFilePath)) return false;
                string json = File.ReadAllText(settingsFilePath);
                var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(json);
                if (settings == null) return false;

                if (secondary)
                {
                    return AppSettings.SecondaryLineUsesWebRtc(settings);
                }

                // Legacy fallback: если MainConnectionTransport не выставлен, смотрим UseWebRtcAudio.
                if (string.IsNullOrWhiteSpace(settings.MainConnectionTransport) ||
                    string.Equals(settings.MainConnectionTransport, "Sip", StringComparison.OrdinalIgnoreCase))
                {
                    return settings.UseWebRtcAudio;
                }
                return string.Equals(settings.MainConnectionTransport, "WebRtc", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SipService] Error checking WebRTC setting: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Backward-compatible wrapper. For new code prefer ShouldUseWebRtcFor(_isSecondaryConnection).
        /// </summary>
        private bool ShouldUseWebRtc()
        {
            return ShouldUseWebRtcFor(_isSecondaryConnection);
        }
    }
}
