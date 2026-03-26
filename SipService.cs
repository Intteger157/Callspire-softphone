using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.IO;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using SIPSorceryMedia.Windows;
using SIPSorceryMedia.Abstractions;
using Newtonsoft.Json;
using NAudio.CoreAudioApi;

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
        
        // Кэшированный локальный IP адрес (выбирается один раз при инициализации)
        private string? _cachedLocalIPAddress;
        
        // Cooldown для игнорирования SIP входящих после завершения WebRTC звонка
        private DateTime? _ignoreSipUntil = null;
        private readonly object _ignoreSipLock = new object();

        private SIPTransport? _sipTransport;
        private SIPUserAgent? _userAgent;
        private SIPRegistrationUserAgent? _regUserAgent;

        private WindowsAudioEndPoint? _audioEndPoint;           // Fallback (WinMM)
        private WasapiAudioEndPoint? _wasapiAudioEndPoint;     // Primary (WASAPI Communications mode)
        private VoIPMediaSession? _voipMediaSession;
        private AudioEncoder? _audioEncoder; // Сохраняем ссылку на AudioEncoder для получения кодеков
        // Active audio source actually wired into the current VoIPMediaSession (not a new one from ToMediaEndPoints()).
        // We keep it so we can reliably pause/resume outgoing RTP on mute/hold.
        private object? _activeAudioSource;
        private bool _skipAudioInitialization = false; // Флаг для пропуска инициализации аудио (для WebRTC режима)
        private ToneGenerator? _toneGenerator; // Генератор гудков (ленивая инициализация)
        private Task<bool>? _activeCallTask; // Задача активного звонка для возможности отмены
        private System.Threading.CancellationTokenSource? _callCancellationTokenSource; // Для отмены звонка
        private bool _isCallCancelled = false; // Флаг отмены звонка пользователем (чтобы не воспроизводить гудки для последующих ответов)
        private bool _isCallRejected = false; // Флаг отклонения входящего звонка (чтобы не начинать запись)
        // NOTE: SIP call recording is disabled. Recording is supported for WebRTC calls only.
        
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
                MainWindow.Log($"[SipService] SetIgnoreSipCooldown: SIP incoming calls will be ignored for {durationSeconds} seconds");
            }
        }

        public bool IsRegistered { get; private set; }
        public bool IsInCall => _userAgent?.IsCallActive == true;
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
        
        // RTP call recorder для записи SIP звонков
        // Используем lock для защиты от race conditions при множественных звонках
        private RtpCallRecorder? _rtpCallRecorder;
        private readonly object _recorderLock = new object();
        private bool _isStoppingRecording = false; // Флаг для предотвращения множественных вызовов StopCallRecording
        
        // Filtering audio sink - prevents DTMF/CN packets from being decoded as audio
        private FilteringAudioSink? _filteringAudioSink;
        
        // ========== RTP DIAGNOSTICS ==========
        private int _rtpPacketsReceived;
        private int _rtpSequenceGaps;
        private int _rtpLastSequenceNumber = -1;
        private Dictionary<int, int> _rtpPayloadTypeCounts = new Dictionary<int, int>();
        private int _rtpNegotiatedPayloadType = -1; // Payload type из согласованного кодека
        private System.Threading.Timer? _rtpStatsTimer;
        private DateTime _rtpStatsStartTime;
        private long _rtpTotalBytesReceived;
        private NAudio.Wave.WaveFileWriter? _diagnosticWavWriter;
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

        public SipService(string username, string password, string server, int port = 5060, 
            int? microphoneDeviceNumber = null, int? speakerDeviceNumber = null,
            string audioCodec = "PCMU", int audioSampleRate = 8000, int audioBitrate = 64000,
            bool isSecondaryConnection = false)
        {
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
            _instanceHash = this.GetHashCode();
            SetStatus($"SipService created, hash: {_instanceHash}, secondary: {_isSecondaryConnection}");
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
                if (_voipMediaSession == null || (_audioEndPoint == null && _wasapiAudioEndPoint == null))
                {
                    SetStatus("Audio not initialized");
                    return;
                }

                IsMuted = mute;
                MainWindow.Log($"[SipService] Microphone muted (app-level): {mute}");

                bool applied = TryApplyAudioSourceMute(_activeAudioSource, mute);
                if (!applied)
                    MainWindow.Log("[SipService] ⚠ Mute: could not pause/resume AudioSource (no SetPaused/PauseAudio/Pause). Remote party may still hear you.");

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
                    MainWindow.Log($"[SipService] Mute: {t.Name}.SetPaused({mute})");
                    return true;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] Mute: SetPaused failed on {t.Name}: {ex.Message}");
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
                        MainWindow.Log($"[SipService] Mute: {t.Name}.PauseAudio()");
                        return true;
                    }

                    var pause = t.GetMethod("Pause", flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (pause != null)
                    {
                        pause.Invoke(audioSource, null);
                        MainWindow.Log($"[SipService] Mute: {t.Name}.Pause()");
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
                        MainWindow.Log($"[SipService] Mute: {t.Name}.ResumeAudio()");
                        return true;
                    }

                    var resume = t.GetMethod("Resume", flags, binder: null, types: Type.EmptyTypes, modifiers: null);
                    if (resume != null)
                    {
                        resume.Invoke(audioSource, null);
                        MainWindow.Log($"[SipService] Mute: {t.Name}.Resume()");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] Mute: Pause/Resume failed on {t.Name}: {ex.Message}");
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

                // 2) Always pause/resume local outgoing audio when holding/unholding.
                // This guarantees the microphone isn't heard even if PBX hold behavior is odd.
                try
                {
                    var audioSource = _activeAudioSource;
                    if (audioSource != null)
                    {
                        var setPausedMethod = audioSource.GetType().GetMethod("SetPaused", BindingFlags.Public | BindingFlags.Instance);
                        if (setPausedMethod != null)
                        {
                            setPausedMethod.Invoke(audioSource, new object[] { hold });
                        }
                        else if (hold)
                        {
                            audioSource.GetType().GetMethod("Pause", BindingFlags.Public | BindingFlags.Instance)?.Invoke(audioSource, null);
                        }
                        else
                        {
                            audioSource.GetType().GetMethod("Resume", BindingFlags.Public | BindingFlags.Instance)?.Invoke(audioSource, null);
                        }
                    }
                }
                catch (Exception ex2)
                {
                    System.Diagnostics.Debug.WriteLine($"Hold local pause/resume error: {ex2.Message}");
                }

                // 3) If the library didn't provide a hold helper, perform a real RFC hold using re-INVITE with SDP direction.
                // On hold we set audio to recvonly (we receive MOH, we don't send mic). On resume we restore sendrecv.
                if (!invoked)
                {
                    var currentLocalSdp = TryGetActiveLocalSdp();
                    if (!string.IsNullOrWhiteSpace(currentLocalSdp))
                    {
                        var updatedSdp = ApplyAudioDirectionToSdp(currentLocalSdp!, hold ? "recvonly" : "sendrecv");
                        var reinviteOk = await TrySendReinviteAsync(updatedSdp);
                        MainWindow.Log($"[SipService] HoldCallAsync: re-INVITE {(reinviteOk ? "sent" : "failed")} (direction={(hold ? "recvonly" : "sendrecv")})");
                    }
                    else
                    {
                        MainWindow.Log("[SipService] HoldCallAsync: Cannot send re-INVITE hold: local SDP not available.");
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
                MainWindow.Log($"[SipService] TryGetActiveLocalSdp error: {ex.Message}");
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
                        catch (Exception ex) { MainWindow.Log($"[SipService] re-INVITE invoke failed ({name}): {ex.Message}"); }
                    }
                    else if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && dialogue != null && ps[1].ParameterType.IsInstanceOfType(dialogue))
                    {
                        try
                        {
                            var result = mi.Invoke(target, new[] { (object)sdp, dialogue });
                            return await NormalizeInvokeResult(result).ConfigureAwait(false);
                        }
                        catch (Exception ex) { MainWindow.Log($"[SipService] re-INVITE invoke failed ({name}): {ex.Message}"); }
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
                    MainWindow.Log($"[SipService] {connLabel}P-Asserted-Identity detected: {callerId} (raw: {pai})");
                    OnOutboundCallerIdReceived?.Invoke(callerId);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] Error parsing P-Asserted-Identity: {ex.Message}");
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
                
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Querying STUN server {stunServer}:{stunPort} for public IP...");
                
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
                                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}✅ STUN public IP: {publicIP}");
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
                                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}✅ STUN public IP (XOR): {publicIP}");
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
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ STUN query failed: {ex.Message}");
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
                
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Detecting NAT type using STUN server {stunServer}:{stunPort}...");
                
                // Делаем первый STUN запрос
                (string? ip1, ushort? port1) = await GetStunMappedAddressAsync(stunServer, stunPort);
                if (ip1 == null || port1 == null)
                {
                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ Failed to get first STUN response");
                    return "Unknown (STUN failed)";
                }
                
                // Ждем немного, чтобы порт мог измениться
                await Task.Delay(100);
                
                // Делаем второй STUN запрос с другого локального порта
                (string? ip2, ushort? port2) = await GetStunMappedAddressAsync(stunServer, stunPort);
                if (ip2 == null || port2 == null)
                {
                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ Failed to get second STUN response");
                    return "Unknown (STUN failed)";
                }
                
                // Сравниваем результаты
                if (ip1 != ip2)
                {
                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ Different public IPs detected: {ip1} vs {ip2} - unusual NAT behavior");
                    return "Unknown (Multiple IPs)";
                }
                
                if (port1 == port2)
                {
                    // Одинаковые порты - это Cone NAT (Full/Restricted/Port Restricted)
                    // Для точного определения нужны дополнительные тесты, но для SIP это достаточно
                    _detectedNatType = "Cone NAT";
                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}✅ NAT Type: {_detectedNatType} (IP: {ip1}, Port: {port1} - same for both requests)");
                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}ℹ️ {_detectedNatType} should work with local IP in SDP");
                    return "Cone NAT (should work with local IP)";
                }
                else
                {
                    // Разные порты - это Symmetric NAT
                    _detectedNatType = "Symmetric NAT";
                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ NAT Type: {_detectedNatType} (IP: {ip1}, Ports: {port1} vs {port2} - different!)");
                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ {_detectedNatType} requires TURN server or port forwarding for RTP");
                    return "Symmetric NAT (needs TURN or port forwarding)";
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ NAT type detection failed: {ex.Message}");
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
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}⚠️ STUN query failed: {ex.Message}");
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
                            MainWindow.Log($"[SipService] {connectionLabel} Selected IP from socket connection: {selectedIP}");
                            _cachedLocalIPAddress = selectedIP.ToString();
                            return _cachedLocalIPAddress;
                        }
                        else
                        {
                            MainWindow.Log($"[SipService] {connectionLabel} Socket connection returned VPN/virtual IP {selectedIP}, trying to find real interface...");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] {connectionLabel} Error getting IP from socket connection: {ex.Message}");
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
                            MainWindow.Log($"[SipService] {connectionLabel} Found candidate IP: {ip} (interface: {ni.Name}, type: {ni.NetworkInterfaceType})");
                        }
                    }
                }
                
                // Выбираем первый подходящий IP
                if (candidateIPs.Count > 0)
                {
                    var selectedIP = candidateIPs[0];
                    MainWindow.Log($"[SipService] {connectionLabel} Selected IP from network interfaces: {selectedIP}");
                    _cachedLocalIPAddress = selectedIP.ToString();
                    return _cachedLocalIPAddress;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] {connectionLabel} Error getting IP from network interfaces: {ex.Message}");
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
                            MainWindow.Log($"[SipService] {connectionLabel} Selected IP from DNS: {ip}");
                            _cachedLocalIPAddress = ip.ToString();
                            return _cachedLocalIPAddress;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] {connectionLabel} Error getting IP from DNS: {ex.Message}");
            }
            
            MainWindow.Log($"[SipService] {connectionLabel} WARNING: Could not find suitable IP (VPN filtered), using 127.0.0.1");
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
            
            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4)
                return false;
            
            // VPN диапазоны:
            // 198.18.0.0/15 (198.18.0.0 - 198.19.255.255) - Benchmarking/Interconnect (часто используется VPN)
            // 169.254.0.0/16 (169.254.0.0 - 169.254.255.255) - Link-local (APIPA)
            // 100.64.0.0/10 (100.64.0.0 - 100.127.255.255) - Carrier-grade NAT
            // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255) - но это может быть и реальная сеть, так что не блокируем
            
            // 198.18.0.0/15
            if (bytes[0] == 198 && bytes[1] >= 18 && bytes[1] <= 19)
            {
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} IP {ip} detected as VPN (198.18.0.0/15 range)");
                return true;
            }
            
            // 169.254.0.0/16 (Link-local)
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} IP {ip} detected as link-local (169.254.0.0/16)");
                return true;
            }
            
            // 100.64.0.0/10 (Carrier-grade NAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            {
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} IP {ip} detected as carrier-grade NAT (100.64.0.0/10)");
                return true;
            }
            
            return false;
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
            if ((_audioEndPoint != null || _wasapiAudioEndPoint != null) && _voipMediaSession != null && !force)
            {
                return;
            }
            
            // НЕ пытаемся переиспользовать _audioEndPoint!
            // VoIPMediaSession.Close() вызывает CloseAudio()/CloseAudioSink(), 
            // что навсегда отключает обработчик DataAvailable и ставит _isAudioSourceClosed=true.
            // Всегда создаем новый endpoint.

            // Закрываем предыдущие экземпляры
            try { _voipMediaSession?.Close("reinitialize"); } catch { }
            _voipMediaSession = null;
            _filteringAudioSink = null;
            
            // Всегда закрываем и пересоздаём audio endpoint
            try { _wasapiAudioEndPoint?.Dispose(); } catch { }
            _wasapiAudioEndPoint = null;
            try { _audioEndPoint?.CloseAudio(); } catch { }
            _audioEndPoint = null;

            try
            {
                SetStatus("Initializing audio...");
                
                var audioEncoder = new AudioEncoder();
                _audioEncoder = audioEncoder;

                int micDevice = _microphoneDeviceNumber ?? -1;
                int spkDevice = _speakerDeviceNumber ?? -1;
                string connLabel = _isSecondaryConnection ? "[Connection2] " : "";
                
                MediaEndPoints? mediaEndPoints = null;
                
                // =========================================================
                // TRY 1: WASAPI Communications mode (provides AEC/NS/AGC)
                // This is the same audio pipeline that browsers use for WebRTC
                // =========================================================
                try
                {
                    _wasapiAudioEndPoint = new WasapiAudioEndPoint(audioEncoder,
                        audioOutDeviceIndex: spkDevice,
                        audioInDeviceIndex: micDevice);

                    _wasapiAudioEndPoint.AecEnabled = EnableAec;

                    // Prioritize PCMA (A-law, PT=8) for all SIP connections.
                    // VoIPMediaSession picks the FIRST codec from our list, so order matters.
                    // PCMA is the international standard (Europe, Russia, most of the world).
                    // PCMU (μ-law) stays in the offer as fallback for North American providers.
                    // Both are G.711, identical quality (64 kbps, 8000 Hz).
                    _wasapiAudioEndPoint.PrioritizeFormat(8);

                    // Tap raw 8 kHz PCM BEFORE codec encoding for call recording.
                    // This gives full 16-bit quality without A-law quantisation loss.
                    _wasapiAudioEndPoint.OnRawPcmFrameTap = (pcm8k) =>
                    {
                        try
                        {
                            RecordOutboundRawPcm(pcm8k);
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"[SipService] {connLabel}Error in OnRawPcmFrameTap: {ex.Message}");
                        }
                    };

                    mediaEndPoints = _wasapiAudioEndPoint.ToMediaEndPoints();

                    // Wrap AudioSink with RecordingAudioSink for inbound RTP recording (WASAPI path)
                    if (mediaEndPoints?.AudioSink != null)
                    {
                        mediaEndPoints.AudioSink = new RecordingAudioSink(mediaEndPoints.AudioSink, this);
                    }

                    MainWindow.Log($"[SipService] {connLabel}✓ Using WASAPI Communications mode (AEC/NS/AGC enabled)");
                }
                catch (Exception wasapiEx)
                {
                    MainWindow.Log($"[SipService] {connLabel}WASAPI failed ({wasapiEx.Message}), falling back to WinMM");
                    try { _wasapiAudioEndPoint?.Dispose(); } catch { }
                    _wasapiAudioEndPoint = null;
                }
                
                // =========================================================
                // TRY 2: Fallback to WindowsAudioEndPoint (WinMM) + FilteringAudioSink + RecordingAudioSink
                // =========================================================
                if (mediaEndPoints == null)
                {
                    try
                    {
                        _audioEndPoint = new WindowsAudioEndPoint(audioEncoder, 
                            audioOutDeviceIndex: spkDevice,
                            audioInDeviceIndex: micDevice);
                        mediaEndPoints = _audioEndPoint.ToMediaEndPoints();
                        
                        // Wrap AudioSink with FilteringAudioSink for WinMM path (filters DTMF/CN)
                        if (mediaEndPoints?.AudioSink != null)
                        {
                            _filteringAudioSink = new FilteringAudioSink(mediaEndPoints.AudioSink, connLabel);
                            // Wrap with RecordingAudioSink for inbound RTP recording
                            mediaEndPoints.AudioSink = new RecordingAudioSink(_filteringAudioSink, this);
                        }
                        MainWindow.Log($"[SipService] {connLabel}Using WinMM fallback with FilteringAudioSink + RecordingAudioSink");
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Failed to create audio endpoint: {ex.Message}", ex);
                    }
                }
                
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
                    MainWindow.Log($"[SipService] {connLabel}Local IP: {localIpStr}, RTP bind: {rtpBindAddress} (Any - for NAT traversal)");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[SipService] {connLabel}Error getting local IP: {ex.Message}");
                }
                
                _voipMediaSession = new VoIPMediaSession(mediaEndPoints, bindAddress: rtpBindAddress)
                {
                    AcceptRtpFromAny = true
                };
                
                string mode = _wasapiAudioEndPoint != null ? "WASAPI Communications" : "WinMM + FilteringAudioSink";
                MainWindow.Log($"[SipService] {connLabel}Audio chain: {mode} → VoIPMediaSession (native codecs, RTP bind={rtpBindAddress})");
                
                // Информация о NAT traversal
                string? publicIP = GetCachedPublicIP();
                if (!string.IsNullOrEmpty(publicIP))
                {
                    MainWindow.Log($"[SipService] {connLabel}✅ NAT traversal: RTP listening on all interfaces (0.0.0.0), public IP {publicIP} (STUN) will be used in SDP");
                }
                else
                {
                    MainWindow.Log($"[SipService] {connLabel}⚠️ NAT traversal: RTP listening on all interfaces, but public IP not yet determined (STUN in progress)");
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
                
                SetStatus(errorMessage);
                
                // Продолжаем без аудио для тестирования регистрации
                _audioEndPoint = null;
                _wasapiAudioEndPoint = null;
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
            MainWindow.Log($"[SipService] {connectionLabel} Binding UDP channel to all interfaces (IPAddress.Any)");

            // UDP-канал на конкретном IP (если useDirectInternet) или на всех интерфейсах
            var listenEndPoint = new IPEndPoint(localIPAddr, 0);
            var udpChannel = new SIPUDPChannel(listenEndPoint);
            _sipTransport.AddSIPChannel(udpChannel);
            
            // Получаем реальный порт, который был назначен каналу
            // Это важно для правильного Contact URI в регистрации
            var actualPort = udpChannel.ListeningEndPoint.Port;
            SetStatus($"SIP transport listening on {localIPStr}:{actualPort}");
            
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
                        MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}✅ Public IP cached: {publicIP}");
                    }
                    
                    // Определяем тип NAT после получения публичного IP
                    string natType = await DetectNatTypeAsync();
                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}📊 NAT Type Detection Result: {natType}");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}⚠️ Failed to get public IP via STUN: {ex.Message}");
                }
            });
            
            // Добавляем обработчик исходящих SIP запросов для диагностики и модификации SDP
            _sipTransport.SIPRequestOutTraceEvent += (localEndPoint, remoteEndPoint, request) =>
            {
                try
                {
                    if (request != null && request.Method == SIPMethodsEnum.INVITE)
                    {
                        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                        SetStatus($"[{timestamp}] SIP Request OUT: INVITE to {remoteEndPoint}");
                        
                        // Модифицируем SDP: заменяем локальный IP на внешний (для NAT traversal)
                        try
                        {
                            string? sdpBody = request.Body?.ToString();
                            
                            if (!string.IsNullOrEmpty(sdpBody) && sdpBody.Contains("v=0") && sdpBody.Contains("m=audio"))
                            {
                                // Заменяем 0.0.0.0 на локальный IP в SDP для NAT traversal
                                // Это работает, если NAT не симметричный (Cone NAT) - NAT автоматически пробросит порт
                                // Если RTP пакеты не приходят, возможные причины:
                                // 1. Симметричный NAT - нужен TURN сервер или port forwarding
                                // 2. Firewall блокирует входящие UDP пакеты
                                // 3. Провайдер не может достучаться до локального IP
                                string localIP = GetLocalIPAddress();
                                
                                // Заменяем 0.0.0.0 на локальный IP в SDP
                                if (sdpBody.Contains("c=IN IP4 0.0.0.0"))
                                {
                                    string modifiedSdp = sdpBody.Replace("c=IN IP4 0.0.0.0", $"c=IN IP4 {localIP}");
                                    request.Body = modifiedSdp;
                                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}✅ Replaced 0.0.0.0 with local IP {localIP} in SDP");
                                }
                                else
                                {
                                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}ℹ️ Using local IP {localIP} in SDP for NAT traversal");
                                }
                                
                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}ℹ️ If RTP packets don't arrive, possible solutions:");
                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}   1. Use WebRTC instead of SIP (has TURN support)");
                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}   2. Configure port forwarding on router (UDP ports 10000-20000)");
                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}   3. Deploy TURN server (coturn/eturnal) and configure RTP proxy");
                            }
                        }
                        catch (Exception natEx)
                        {
                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}⚠️ Error modifying SDP for NAT traversal: {natEx.Message}");
                        }
                        
                        // Логируем исходящий SDP для диагностики кодеков
                        try
                        {
                            // В SIPSorcery Body обычно string для SDP
                            string? sdpBody = request.Body?.ToString();
                            
                            if (!string.IsNullOrEmpty(sdpBody) && sdpBody.Contains("v=0") && sdpBody.Contains("m=audio"))
                            {
                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} OUTGOING INVITE SDP body:");
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
                                        MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} OUT SDP: {trimmedLine}");
                                    }
                                }
                                
                                // Извлекаем кодеки из m=audio строки
                                var mAudioLine = sdpLines.FirstOrDefault(l => l.Trim().StartsWith("m=audio"));
                                if (mAudioLine != null)
                                {
                                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} OUT m=audio line: {mAudioLine.Trim()}");
                                    // Парсим кодеки (формат: m=audio PORT RTP/AVP CODEC1 CODEC2 ...)
                                    var parts = mAudioLine.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                    if (parts.Length > 3)
                                    {
                                        var codecs = string.Join(", ", parts.Skip(3));
                                        MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} OUT Codecs in m=audio: {codecs}");
                                        
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
                                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} OUT Codecs decoded: {string.Join(", ", codecNames)}");
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception sdpEx)
                        {
                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error parsing outgoing SDP: {sdpEx.Message}");
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
                    if (response != null)
                    {
                        int statusCode = response.StatusCode;
                        var statusReason = response.ReasonPhrase ?? "";
                        SIPMethodsEnum cseq = SIPMethodsEnum.UNKNOWN;
                        if (response.Header?.CSeq != null)
                        {
                            // CSeq может быть объектом с свойством Method или просто int
                            // Используем рефлексию для безопасного доступа
                            try
                            {
                                var cseqObj = response.Header.CSeq;
                                var cseqType = cseqObj.GetType();
                                
                                // Проверяем, является ли это объектом с свойством Method
                                if (cseqType != typeof(int))
                                {
                                    var methodProperty = cseqType.GetProperty("Method");
                                    if (methodProperty != null)
                                    {
                                        var methodValue = methodProperty.GetValue(cseqObj);
                                        if (methodValue != null)
                                        {
                                            if (methodValue is SIPMethodsEnum methodEnum)
                                            {
                                                cseq = methodEnum;
                                            }
                                            else if (methodValue is int methodInt && Enum.IsDefined(typeof(SIPMethodsEnum), methodInt))
                                            {
                                                cseq = (SIPMethodsEnum)methodInt;
                                            }
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Если не получилось, оставляем UNKNOWN
                            }
                        }
                        var callId = response.Header?.CallId ?? "unknown";
                        
                        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                        SetStatus($"[{timestamp}] SIP Response: {statusCode} {statusReason} (Method: {cseq}, Call-ID: {callId})");
                        
                        // Детальное логирование и воспроизведение гудков только для ответов на INVITE
                        // Игнорируем ответы на REGISTER, OPTIONS и другие запросы
                        // Проверяем, что это ответ на INVITE по наличию Call-ID в активных звонках
                        // или по статус-кодам, характерным для INVITE (100, 180, 183)
                        
                        // Пропускаем ответы на REGISTER (401, 200) - они не должны воспроизводить гудки
                        bool isRegisterResponse = false;
                        if (response.Header?.CSeq != null)
                        {
                            try
                            {
                                var cseqObj = response.Header.CSeq;
                                var cseqType = cseqObj.GetType();
                                if (cseqType != typeof(int))
                                {
                                    var methodProperty = cseqType.GetProperty("Method");
                                    if (methodProperty != null)
                                    {
                                        var methodValue = methodProperty.GetValue(cseqObj);
                                        if (methodValue != null)
                                        {
                                            if (methodValue is SIPMethodsEnum methodEnum && methodEnum == SIPMethodsEnum.REGISTER)
                                            {
                                                isRegisterResponse = true;
                                            }
                                            else if (methodValue is int methodInt && methodInt == (int)SIPMethodsEnum.REGISTER)
                                            {
                                                isRegisterResponse = true;
                                            }
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Если не удалось определить метод, проверяем по другим признакам
                            }
                        }
                        
                        // Если это ответ на REGISTER, пропускаем обработку гудков
                        if (isRegisterResponse)
                        {
                            return; // Не обрабатываем ответы на REGISTER
                        }
                        
                        // Обрабатываем только ответы на INVITE
                        // 100, 180, 183, 200, 3xx, 4xx, 5xx, 6xx - это ответы на INVITE
                        if (statusCode == 100)
                        {
                            SetStatus($"[{timestamp}] Call progress: 100 Trying (server received INVITE)");
                        }
                        else if (statusCode == 180)
                        {
                            SetStatus($"[{timestamp}] Call progress: 180 Ringing (phone is ringing)");
                            MainWindow.Log($"[SipService] 180 Ringing received, starting ringback tone");
                            // Log unknown headers for diagnostics (to discover non-standard headers from carriers)
                            try
                            {
                                var unk = response.Header?.UnknownHeaders;
                                if (unk != null && unk.Count > 0)
                                {
                                    string connLabel = _isSecondaryConnection ? "[Connection2] " : "";
                                    MainWindow.Log($"[SipService] {connLabel}180 UnknownHeaders ({unk.Count}): {string.Join(" | ", unk)}");
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
                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 183 Session Progress received (early media detected)");
                            
                            // Логируем SDP из ответа 183 для диагностики RTP endpoint
                            try
                            {
                                if (response?.Body != null)
                                {
                                    string? sdpBody = response.Body.ToString();
                                    if (!string.IsNullOrEmpty(sdpBody))
                                    {
                                        MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 183 SDP body:\n{sdpBody}");
                                        
                                        // Ищем c= строку (connection information) с IP адресом
                                        var sdpLines = sdpBody.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                                        foreach (var line in sdpLines)
                                        {
                                            string trimmedLine = line.Trim();
                                            if (trimmedLine.StartsWith("c=IN IP4", StringComparison.OrdinalIgnoreCase))
                                            {
                                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 183 SDP connection line: {trimmedLine}");
                                                // Извлекаем IP адрес
                                                var parts = trimmedLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                                if (parts.Length >= 3)
                                                {
                                                    string rtpIp = parts[2];
                                                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 183 SDP RTP IP: {rtpIp}");
                                                    
                                                    // Для Beeline: если в SDP указан IP SIP сервера, но должен быть другой RTP IP
                                                    if (_isSecondaryConnection && rtpIp != "62.105.133.230")
                                                    {
                                                        MainWindow.Log($"[SipService] [Connection2] WARNING: SDP contains RTP IP {rtpIp}, but Beeline requires 62.105.133.230 for RTP!");
                                                    }
                                                }
                                            }
                                            if (trimmedLine.StartsWith("m=audio", StringComparison.OrdinalIgnoreCase))
                                            {
                                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 183 SDP m=audio: {trimmedLine}");
                                                // Extract the first codec payload type (this is the provider's preferred codec)
                                                var mParts = trimmedLine.Split(' ');
                                                if (mParts.Length >= 4 && int.TryParse(mParts[3], out int negotiatedPT))
                                                {
                                                    _rtpNegotiatedPayloadType = negotiatedPT;
                                                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Negotiated codec PT={negotiatedPT}");
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
                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error parsing 183 SDP: {sdpEx.Message}");
                            }
                            
                            // ВАЖНО: При получении 183 Session Progress провайдер уже отправляет гудок в RTP потоке (early media)
                            // НЕ воспроизводим ringback tone, чтобы избежать наложения гудков
                            // Это особенно важно для провайдеров типа Beeline, которые отправляют гудок в early media
                            if (_isSecondaryConnection)
                            {
                                MainWindow.Log($"[SipService] [Connection2] 183 received - NOT playing ringback tone (early media from provider)");
                            }
                            else
                            {
                                // Для основного подключения тоже не воспроизводим, если есть early media
                                MainWindow.Log($"[SipService] 183 received - NOT playing ringback tone (early media from provider)");
                            }
                            // Останавливаем ringback tone, если он уже играл
                            _toneGenerator?.Stop();
                        }
                    else if (statusCode == 200)
                    {
                        // 200 OK может быть как для INVITE, так и для REGISTER
                        // Проверяем, есть ли активный звонок
                        // Не обрабатываем 200 OK, если звонок был отменен пользователем
                        // Для INVITE: проверяем наличие медиа-сессии, активного звонка, или сохраненного номера
                        bool hasActiveCall = _userAgent?.IsCallActive == true || _toneGenerator != null || _voipMediaSession != null || !string.IsNullOrEmpty(_lastCalledNumber);
                        bool isInviteResponse = !isRegisterResponse && hasActiveCall;
                        MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK received: isInviteResponse={isInviteResponse}, isRegisterResponse={isRegisterResponse}, _isCallCancelled={_isCallCancelled}, IsCallActive={_userAgent?.IsCallActive}, _toneGenerator={(_toneGenerator != null ? "exists" : "null")}, _voipMediaSession={(_voipMediaSession != null ? "exists" : "null")}, _lastCalledNumber={_lastCalledNumber ?? "null"}");
                        
                        if (!_isCallCancelled && isInviteResponse)
                        {
                            SetStatus($"[{timestamp}] Call progress: 200 OK (call answered, media negotiation starting)");
                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK received, stopping ringback tone");
                            
                            // Логируем SDP из ответа 200 OK для диагностики RTP endpoint
                            try
                            {
                                if (response?.Body != null)
                                {
                                    string? sdpBody = response.Body.ToString();
                                    if (!string.IsNullOrEmpty(sdpBody))
                                    {
                                        MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK SDP body:\n{sdpBody}");
                                        
                                        // Ищем c= строку (connection information) с IP адресом
                                        var sdpLines = sdpBody.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                                        foreach (var line in sdpLines)
                                        {
                                            string trimmedLine = line.Trim();
                                            if (trimmedLine.StartsWith("c=IN IP4", StringComparison.OrdinalIgnoreCase))
                                            {
                                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK SDP connection line: {trimmedLine}");
                                                // Извлекаем IP адрес
                                                var parts = trimmedLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                                if (parts.Length >= 3)
                                                {
                                                    string rtpIp = parts[2];
                                                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK SDP RTP IP: {rtpIp}");
                                                    
                                                    // Для Beeline: если в SDP указан IP SIP сервера, но должен быть другой RTP IP
                                                    if (_isSecondaryConnection && rtpIp != "62.105.133.230")
                                                    {
                                                        MainWindow.Log($"[SipService] [Connection2] WARNING: SDP contains RTP IP {rtpIp}, but Beeline requires 62.105.133.230 for RTP!");
                                                    }
                                                }
                                            }
                                            if (trimmedLine.StartsWith("m=audio", StringComparison.OrdinalIgnoreCase))
                                            {
                                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK SDP m=audio: {trimmedLine}");
                                                // Extract negotiated codec PT from 200 OK (may differ from 183)
                                                var mParts = trimmedLine.Split(' ');
                                                if (mParts.Length >= 4 && int.TryParse(mParts[3], out int negotiatedPT))
                                                {
                                                    _rtpNegotiatedPayloadType = negotiatedPT;
                                                    _filteringAudioSink?.SetNegotiatedPayloadType(negotiatedPT);
                                                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK negotiated codec PT={negotiatedPT}");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception sdpEx)
                            {
                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error parsing 200 OK SDP: {sdpEx.Message}");
                            }
                            
                            // Останавливаем ringback tone только если был активный звонок
                            _toneGenerator?.Stop();
                            
                            // Начинаем запись звонка только после получения 200 OK (когда абонент ответил)
                            // Используем lock для защиты от race conditions при множественных звонках.
                            // ВАЖНО: Не создаём рекордер, если звонок уже завершён (IsCallActive==false)
                            // — это защита от дублирующих 200 OK, которые приходят после BYE.
                            lock (_recorderLock)
                            {
                                if (_rtpCallRecorder == null && _isStoppingRecording)
                                {
                                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Previous call recording stop still in progress, allowing new recorder for next call.");
                                    _isStoppingRecording = false;
                                }

                                bool isRecordingEnabled = IsCallRecordingEnabled();
                                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}200 OK received: IsCallRecordingEnabled={isRecordingEnabled}, _rtpCallRecorder={(_rtpCallRecorder == null ? "null" : "exists")}, _lastCalledNumber={_lastCalledNumber ?? "null"}, _isStoppingRecording={_isStoppingRecording}, _isCallCancelled={_isCallCancelled}, _isCallRejected={_isCallRejected}");

                                if (isRecordingEnabled && _rtpCallRecorder == null
                                    && !_isCallCancelled && !_isCallRejected
                                    && _voipMediaSession != null)
                                {
                                    try
                                    {
                                        string? phoneNumber = _lastCalledNumber ?? "unknown";
                                            
                                        if (!string.IsNullOrEmpty(phoneNumber) && phoneNumber != "unknown")
                                        {
                                            var callStartTime = DateTime.Now;
                                            _rtpCallRecorder = new RtpCallRecorder();
                                            _rtpCallRecorder.StartRecording(phoneNumber, callStartTime, GetRecordingsDirectory());
                                            _lastRecordingFilePath = null;
                                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Call recording started for {phoneNumber} (after 200 OK)");
                                        }
                                        else
                                        {
                                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")}⚠️ Cannot start recording: phoneNumber={phoneNumber ?? "null"}, _lastCalledNumber={_lastCalledNumber ?? "null"}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error starting call recording after 200 OK: {ex.Message}");
                                        _rtpCallRecorder = null;
                                    }
                                }
                            }
                        }
                        else if (_isCallCancelled)
                        {
                            // 200 OK пришел после отмены - просто останавливаем гудки
                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} 200 OK received but call was cancelled, stopping tones");
                            _toneGenerator?.Stop();
                        }
                    }
                        else if (statusCode >= 300 && statusCode < 700)
                        {
                            // Ошибки звонка - воспроизводим busy tone только если был активный звонок
                            // Исключаем 401 Unauthorized - это нормальный ответ для запроса авторизации
                            // Исключаем 487 Request Terminated - это ответ на CANCEL (звонок был отменен пользователем)
                            // Не воспроизводим гудки, если звонок был отменен пользователем
                            if (statusCode == 487)
                            {
                                // 487 Request Terminated - звонок был отменен пользователем (CANCEL был отправлен)
                                MainWindow.Log($"[SipService] Call terminated (487) - call was cancelled by local user (CANCEL confirmed), stopping tones");
                                _toneGenerator?.Stop();
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
                                    SetStatus($"[{timestamp}] Call failed: {statusCode} {statusReason}");
                                    MainWindow.Log($"[SipService] Call failed with {statusCode}, stopping ringback tone and playing busy tone");
                                    _toneGenerator?.Stop();
                                    EnsureToneGenerator();
                                    if (_toneGenerator != null)
                                    {
                                        _toneGenerator.PlayBusyTone();
                                        _ = Task.Delay(2000).ContinueWith(_ => _toneGenerator?.Stop());
                                    }
                                }
                                else
                                {
                                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Ignoring {statusCode} {statusReason} — no active call (likely REGISTER failure)");
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
                SetStatus($"Trace: Received {request.Method} from {remoteEndPoint}");

                // Handle CANCEL: caller cancelled the INVITE before we answered.
                if (request.Method == SIPMethodsEnum.CANCEL)
                {
                    try
                    {
                        var callId = request.Header?.CallId;
                        SetStatus($"Incoming call cancelled by remote party (CANCEL). Call-ID: {callId ?? "unknown"}");
                        MainWindow.Log($"[SipService] Incoming call cancelled by remote party (CANCEL). Call-ID: {callId ?? "unknown"}");

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
                        _toneGenerator?.Stop();
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnCallEnded?.Invoke()));

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
                    MainWindow.Log("[SipService] Call ended by remote party (BYE request received)");
                    // Отвечаем OK на BYE
                    try
                    {
                        var okResponse = SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null);
                        await _sipTransport.SendResponseAsync(okResponse);
                        
                        // Закрываем медиа-сессию
                        try
                        {
                            _voipMediaSession?.Close("remote bye");
                        }
                        catch
                        {
                            // Игнорируем ошибки при закрытии медиа-сессии
                        }
                        
                        // Останавливаем запись звонка и диагностику
                        StopCallRecording();
                        StopRtpDiagnostics();
                        
                        // Уведомляем UI о завершении звонка через Dispatcher
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnCallEnded?.Invoke()));
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Error handling BYE: {ex.Message}");
                        MainWindow.Log($"[SipService] Error handling BYE: {ex.Message}");
                        StopRtpDiagnostics();
                        // Все равно вызываем OnCallEnded через Dispatcher
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnCallEnded?.Invoke()));
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
                        MainWindow.Log($"[SipService] Warning: failed to send provisional INVITE response: {ex.Message}");
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
            _userAgent = new SIPUserAgent(_sipTransport, outboundProxy: null);

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

                    // ГЕЙТ: Основная проверка - если WebRTC режим включен, отклоняем все SIP звонки
                    // ВАЖНО: Вторичное подключение всегда работает через SIP, не проверяет WebRTC
                    bool useWebRtc = _isSecondaryConnection ? false : ShouldUseWebRtc();
                    if (useWebRtc)
                    {
                        SetStatus($"SIP incoming call from {callerNumber} rejected: WebRTC mode is enabled");
                        MainWindow.Log($"[SipService] SIP incoming call from {callerNumber} rejected: WebRTC mode is enabled");
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
                            MainWindow.Log($"[SipService] Error rejecting SIP call: {rejectEx.Message}");
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
                        
                        // Проверка 2: Активный WebRTC звонок
                        if (!shouldIgnoreSip)
                        {
                            // Безопасная проверка WebRtcService.Instance
                            var webRtcService = WebRtcService.Instance;
                            if (webRtcService != null)
                            {
                                var webRtcState = webRtcService.CurrentCallState;
                                
                                // Проверяем, что WebRTC готов и есть активный звонок
                                if (webRtcService.IsReadyForCalls && 
                                    (webRtcState == WebRtcCallState.Ringing || 
                                     webRtcState == WebRtcCallState.Connected || 
                                     webRtcState == WebRtcCallState.Calling))
                                {
                                    shouldIgnoreSip = true;
                                    ignoreReason = $"WebRTC call active (state: {webRtcState})";
                                }
                            }
                        }
                    }
                    catch (Exception webRtcCheckEx)
                    {
                        // Если проверка WebRTC не удалась, логируем, но продолжаем обработку SIP звонка
                        SetStatus($"Warning: Failed to check WebRTC state: {webRtcCheckEx.Message}");
                        MainWindow.Log($"[SipService] Warning: Failed to check WebRTC state: {webRtcCheckEx.Message}");
                    }
                    
                    if (shouldIgnoreSip)
                    {
                        SetStatus($"SIP incoming call from {callerNumber} ignored: {ignoreReason}");
                        MainWindow.Log($"[SipService] SIP incoming call from {callerNumber} ignored: {ignoreReason}");
                        return; // Не обрабатываем SIP входящий
                    }

                    SetStatus($"Incoming call from {callerNumber}");
                    
                    // Логируем SDP из INVITE для проверки codec (критично для диагностики)
                    try
                    {
                        string? sdpBody = req.Body; // В SIPSorcery Body это string
                        if (!string.IsNullOrEmpty(sdpBody))
                        {
                            int previewLength = Math.Min(500, sdpBody.Length);
                            MainWindow.Log($"[SipService] INVITE SDP body (first {previewLength} chars):\n{sdpBody.Substring(0, previewLength)}");
                            
                            // Ищем m=audio и a=rtpmap строки
                            var sdpLines = sdpBody.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var line in sdpLines)
                            {
                                string trimmedLine = line.Trim();
                                if (trimmedLine.StartsWith("m=audio", StringComparison.OrdinalIgnoreCase))
                                {
                                    MainWindow.Log($"[SipService] SDP m=audio: {trimmedLine}");
                                }
                                if (trimmedLine.StartsWith("a=rtpmap", StringComparison.OrdinalIgnoreCase))
                                {
                                    MainWindow.Log($"[SipService] SDP a=rtpmap: {trimmedLine}");
                                }
                            }
                        }
                    }
                    catch (Exception sdpEx)
                    {
                        MainWindow.Log($"[SipService] Error parsing SDP: {sdpEx.Message}");
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
                            MainWindow.Log($"[SipService] New incoming call (CallID: {callId}) while previous call (CallID: {_incomingCallId}) still pending - processing new call");
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
                    
                    // Уведомляем UI о входящем звонке (не принимаем автоматически)
                    // Вызываем событие в UI потоке через Dispatcher, так как мы в async обработчике (фоновый поток)
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null)
                    {
                        // Используем BeginInvoke для асинхронного выполнения без блокировки
                        _ = dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                // Убираем избыточное логирование - событие вызывается один раз
                    OnIncomingCall?.Invoke(callerNumber);
                            }
                            catch (Exception ex)
                            {
                                SetStatus($"UI thread: Error invoking OnIncomingCall event: {ex.Message}");
                                MainWindow.Log($"[SipService] Error invoking OnIncomingCall: {ex.Message}");
                            }
                        }), System.Windows.Threading.DispatcherPriority.Send);
                    }
                    else
                    {
                        // Fallback: вызываем напрямую, если Dispatcher недоступен
                        try
                        {
                            OnIncomingCall?.Invoke(callerNumber);
                            SetStatus($"OnIncomingCall event invoked (no dispatcher)");
                        }
                        catch (Exception ex)
                        {
                            SetStatus($"Error invoking OnIncomingCall event (no dispatcher): {ex.Message}");
                        }
                    }
                    
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

            // 4. Регистрация на SIP-сервере (MikoPBX).
            // ПРОВЕРКА: Если включен WebRTC для звонков, не регистрируемся на SIP сервере
            // ВАЖНО: Вторичное подключение всегда работает через SIP, не проверяет WebRTC
            bool useWebRtc = _isSecondaryConnection ? false : ShouldUseWebRtc();
            if (useWebRtc)
            {
                // ВАЖНО: Если была активная регистрация, отменяем её перед переключением на WebRTC
                if (_regUserAgent != null)
                {
                    try
                    {
                        MainWindow.Log("[SipService] Cancelling existing SIP registration before switching to WebRTC mode");
                        _regUserAgent.Stop();
                        _regUserAgent = null;
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[SipService] Error stopping SIP registration: {ex.Message}");
                    }
                }
                
                SetStatus("SIP registration skipped: WebRTC mode is enabled");
                MainWindow.Log("[SipService] SIP registration skipped: WebRTC mode is enabled");
                IsRegistered = false;
                // Не регистрируемся и не обрабатываем входящие SIP звонки
                await Task.Delay(500);
                return;
            }
            
            // Формируем правильный адрес сервера с портом
            var serverAddress = _server.Contains(":") 
                ? _server 
                : $"{_server}:{_port}";
            
            // Используем упрощенный конструктор SIPRegistrationUserAgent
            // который принимает: transport, username, password, server, expiry
            // Важно: для правильной регистрации нужно указать полный SIP URI пользователя
            // Формируем SIP URI для регистрации: sip:username@server:port
            var sipUri = $"sip:{_username}@{serverAddress}";
            
            // Логируем информацию о регистрации для диагностики
            if (_isSecondaryConnection)
            {
                MainWindow.Log($"[SipService] [Connection2] Registering with username='{_username}', server='{serverAddress}', sipUri='{sipUri}'");
            }
            else
            {
                MainWindow.Log($"[SipService] Registering with username='{_username}', server='{serverAddress}', sipUri='{sipUri}'");
            }
            
            _regUserAgent = new SIPRegistrationUserAgent(
                _sipTransport,
                _username,
                _password,
                serverAddress,
                expiry: 300);
            
            // Устанавливаем Contact URI для регистрации, чтобы сервер знал, куда отправлять входящие звонки
            // Это важно для приема входящих звонков

            _regUserAgent.RegistrationFailed += (uri, response, errorMessage) =>
            {
                IsRegistered = false;
                string connectionLabel = _isSecondaryConnection ? "[Connection2] " : "";
                MainWindow.Log($"[SipService] {connectionLabel}Registration failed: {errorMessage}, URI: {uri}");
                if (response != null)
                {
                    MainWindow.Log($"[SipService] {connectionLabel}Response status: {response.StatusCode} {response.ReasonPhrase}");
                    
                    // Логируем детали ответа для диагностики
                    if (response.Header != null)
                    {
                        // Логируем Warning заголовок (особенно важен для IMS сетей)
                        try
                        {
                            if (response.Header.Warning != null)
                            {
                                MainWindow.Log($"[SipService] {connectionLabel}Warning header found: {response.Header.Warning}");
                            }
                        }
                        catch { }
                        
                        // Логируем тело ответа, если есть
                        if (!string.IsNullOrEmpty(response.Body))
                        {
                            MainWindow.Log($"[SipService] {connectionLabel}Response body: {response.Body}");
                        }
                        
                        // Логируем полный ответ для диагностики (первые 1000 символов для IMS)
                        try
                        {
                            string responseStr = response.ToString();
                            if (!string.IsNullOrEmpty(responseStr))
                            {
                                string responsePreview = responseStr.Length > 1000 ? responseStr.Substring(0, 1000) + "..." : responseStr;
                                MainWindow.Log($"[SipService] {connectionLabel}Response preview: {responsePreview}");
                            }
                        }
                        catch { }
                    }
                    
                    // Специальная обработка для 403 Forbidden
                    // StatusCode это int, сравниваем с числовым значением
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
                        MainWindow.Log($"[SipService] {connectionLabel}{detailedMessage}");
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
            };

            _regUserAgent.RegistrationTemporaryFailure += (uri, response, errorMessage) =>
            {
                IsRegistered = false;
                string connectionLabel = _isSecondaryConnection ? "[Connection2] " : "";
                MainWindow.Log($"[SipService] {connectionLabel}Registration temporary failure: {errorMessage}, URI: {uri}");
                if (response != null)
                {
                    MainWindow.Log($"[SipService] {connectionLabel}Temporary failure response status: {response.StatusCode} {response.ReasonPhrase}");
                }
                SetStatus($"Registration temporary failure: {errorMessage}");
            };

            _regUserAgent.RegistrationRemoved += (uri, response) =>
            {
                IsRegistered = false;
                SetStatus("Registration removed.");
            };

            _regUserAgent.RegistrationSuccessful += (uri, response) =>
            {
                IsRegistered = true;
                // Логируем информацию о регистрации для диагностики
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
            };

            SetStatus("Registering on SIP server...");
            _regUserAgent.Start();

            // Small delay to allow registration to begin; keep it short to improve UX.
            await Task.Delay(150);
        }

        /// <summary>
        /// ��������� ������ �� ��������� �����.
        /// </summary>
        public async Task CallAsync(string number)
        {
            // IMPORTANT: When WebRTC mode is enabled, SIP outgoing calls must be blocked.
            // ВАЖНО: Вторичное подключение всегда работает через SIP, не проверяет WebRTC
            if (!_isSecondaryConnection && ShouldUseWebRtc())
            {
                MainWindow.Log($"[SipService] SIP outgoing call to {number} blocked: WebRTC mode is enabled");
                SetStatus("SIP calls are disabled (WebRTC mode enabled)");
                return;
            }

            if (_userAgent == null)
            {
                SetStatus("SIP not initialized. Click Connect.");
                return;
            }
            
            // Инициализируем аудио, если оно не было инициализировано
            // Но только если не включен WebRTC режим (для WebRTC аудио не используется)
            if (_voipMediaSession == null)
            {
                if (_skipAudioInitialization)
                {
                    // WebRTC режим включен - пропускаем инициализацию аудио
                    MainWindow.Log("[SipService] Skipping audio initialization for outgoing call (WebRTC mode)");
                    SetStatus("Audio initialization skipped (WebRTC mode)");
                    return; // Выходим из метода, так как для WebRTC звонки обрабатываются через WebRtcService
                }
                
                SetStatus("Audio not initialized. Attempting to initialize...");
                MainWindow.Log($"[SipService] Initializing audio for outgoing call. SkipAudioInit={_skipAudioInitialization}");
                
                // Для исходящих звонков тоже используем force=true, если WebRTC не должен использоваться
                // (WebRTC должен обрабатываться отдельно через WebView2)
                InitializeAudio(throwOnError: true, force: true);
                
                // Проверяем еще раз после попытки инициализации
                if (_voipMediaSession == null)
                {
                    string errorMsg = "Audio initialization failed. Please check:\n1. Microphone and speaker are connected\n2. Audio devices are not being used by another application\n3. Audio drivers are installed correctly\n4. WebRTC mode is disabled if you want to use SIPSorcery audio";
                    SetStatus(errorMsg);
                    MainWindow.Log("[SipService] Audio initialization failed for outgoing call");
                    throw new InvalidOperationException("Audio service not initialized. Please check your audio devices and try again.");
                }
                
                MainWindow.Log("[SipService] Audio initialized successfully for outgoing call");
            }
            else if (_wasCallActive)
            {
                // Если был предыдущий звонок, переинициализируем медиа-сессию перед новым звонком
                // Но только если не включен WebRTC режим (для WebRTC аудио не используется)
                if (_skipAudioInitialization)
                {
                    // WebRTC режим включен - пропускаем переинициализацию аудио
                    MainWindow.Log("[SipService] Skipping audio reinitialization (WebRTC mode)");
                    _wasCallActive = false;
                }
                else
                {
                    SetStatus("Reinitializing media session for new call...");
                    try
                    {
                        // Останавливаем запись предыдущего звонка перед началом нового
                        StopCallRecording();
                        
                        // Закрываем старую медиа-сессию
                        try
                        {
                            _voipMediaSession?.Close("new call");
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"[SipService] Warning: Error closing previous media session: {ex.Message}");
                        }
                        _voipMediaSession = null;
                        
                        // ВАЖНО: VoIPMediaSession.Close() вызывает CloseAudio()/CloseAudioSink() на endpoint,
                        // что делает его непригодным для повторного использования!
                        // Поэтому ВСЕГДА создаем новый endpoint для нового звонка.
                        try { _wasapiAudioEndPoint?.Dispose(); } catch { }
                        _wasapiAudioEndPoint = null;
                        try { _audioEndPoint?.CloseAudio(); } catch { }
                        _audioEndPoint = null;
                        
                        await System.Threading.Tasks.Task.Delay(200);
                        
                        // Полная переинициализация с новым endpoint
                        InitializeAudio(throwOnError: true, force: true);
                        
                        if (_voipMediaSession == null) throw new InvalidOperationException("Failed to reinitialize media session");
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[SipService] Error reinitializing media session: {ex.Message}");
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

            // Формируем адрес назначения для звонка через MikoPBX
            string destination;
            
            // Очищаем номер от лишних символов (пробелы, дефисы, скобки и т.д.)
            string cleanNumber = number.Trim().Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
            
            if (cleanNumber.Contains("@"))
            {
                // Если номер уже содержит @, используем как есть (полный SIP URI)
                destination = cleanNumber.StartsWith("sip:") ? cleanNumber : $"sip:{cleanNumber}";
            }
            else
            {
                // Для MikoPBX используем формат: sip:number@server:port
                // Обрабатываем случай, когда сервер уже содержит порт
                string serverPart;
                if (_server.Contains(":"))
                {
                    // Если сервер уже содержит порт (например, "192.168.1.1:5060"), используем как есть
                    serverPart = _server;
                }
                else
                {
                    // Если порта нет, добавляем его
                    serverPart = $"{_server}:{_port}";
                }
                
                destination = $"sip:{cleanNumber}@{serverPart}";
            }

            var callStartTime = DateTime.Now;
            SetStatus($"Calling {destination}...");
            SetStatus($"[{callStartTime:HH:mm:ss.fff}] Call initiated, sending INVITE...");

            // Новый исходящий звонок: сбрасываем предыдущий путь записи,
            // чтобы CallDetails/AmoCRM не подхватывали файл от прошлого звонка,
            // если текущий так и не будет записан (no answer/ошибка).
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
                _isCallRejected = false;
                
                // Создаем CancellationTokenSource для возможности отмены звонка
                _callCancellationTokenSource = new System.Threading.CancellationTokenSource();
                
                // Сохраняем задачу звонка для возможности отмены
                _activeCallTask = _userAgent.Call(
                    destination,
                    _username,
                    _password,
                    _voipMediaSession);

                bool callResult = await _activeCallTask;

                var callEndTime = DateTime.Now;
                var callDuration = (callEndTime - callStartTime).TotalSeconds;
                
                // Очищаем CancellationTokenSource после завершения звонка
                _callCancellationTokenSource?.Dispose();
                _callCancellationTokenSource = null;
                _activeCallTask = null;

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
                        var mediaEndPoints = _wasapiAudioEndPoint?.ToMediaEndPoints() ?? _audioEndPoint?.ToMediaEndPoints();
                        
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
                                
                                var startSinkMethod = mediaEndPoints.AudioSink.GetType().GetMethod("StartAudio", BindingFlags.Public | BindingFlags.Instance);
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
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Warning: Could not start media session: {ex.Message}");
                    }
                }
                else
                {
                    // Останавливаем ringback tone и RTP-диагностику при неудаче
                    _toneGenerator?.Stop();
                    StopRtpDiagnostics();
                    // Воспроизводим busy tone (только если еще не играет)
                    EnsureToneGenerator();
                    if (_toneGenerator != null)
                    {
                        _toneGenerator.PlayBusyTone();
                        _ = Task.Delay(2000).ContinueWith(_ => _toneGenerator?.Stop());
                    }
                    
                    SetStatus("Call failed (timeout/rejected).");
                    _wasCallActive = false; // Сбрасываем флаг при неудаче
                    MainWindow.Log("[SipService] Call failed (rejected/timeout), invoking OnCallEnded to close call window");
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnCallEnded?.Invoke()));
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
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnCallEnded?.Invoke()));
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
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnCallEnded?.Invoke()));
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
                MainWindow.Log("[SipService] Closing previous media session before answering new incoming call");
                try
                {
                    // Закрываем старую медиа-сессию
                    _voipMediaSession.Close("new incoming call");
                    _voipMediaSession = null;
                    MainWindow.Log("[SipService] Previous media session closed");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[SipService] Error closing previous media session: {ex.Message}");
                }
                
                // Закрываем аудио эндпоинт
                try
                {
                    _wasapiAudioEndPoint?.Dispose();
                    _wasapiAudioEndPoint = null;
                    _audioEndPoint?.CloseAudio();
                    _audioEndPoint = null;
                    MainWindow.Log("[SipService] Previous audio endpoint closed");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[SipService] Error closing previous audio endpoint: {ex.Message}");
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
                    MainWindow.Log("[SipService] Initializing audio for incoming call");
                    InitializeAudio(throwOnError: true, force: true);
                }
                catch (Exception ex)
                {
                    SetStatus($"Cannot answer call - audio init failed: {ex.Message}");
                    MainWindow.Log($"[SipService] Audio initialization failed: {ex.Message}");
                    return false;
                }
                
                // Оптимизируем микрофон перед ответом на входящий звонок
                try
                {
                    MicrophoneMuteHelper.OptimizeMicrophoneForVoIP();
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
                
                // Начинаем запись звонка только после AcceptCall (когда абонент ответил)
                // Это предотвращает запись гудков
                // Используем lock для защиты от race conditions при множественных звонках
                lock (_recorderLock)
                {
                    // Для последовательных входящих звонков (особенно на второй линии) важно
                    // разрешить старт новой записи, даже если остановка предыдущей ещё не завершилась,
                    // но сам рекордер уже обнулён.
                    if (_rtpCallRecorder == null && _isStoppingRecording)
                    {
                        MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Previous call recording stop still in progress before AcceptCall, allowing new recorder.");
                        _isStoppingRecording = false;
                    }

                    if (IsCallRecordingEnabled() && _rtpCallRecorder == null && !_isCallCancelled && !_isCallRejected && !string.IsNullOrEmpty(_lastCalledNumber))
                    {
                        try
                        {
                            var callStartTime = DateTime.Now; // Используем текущее время как время начала разговора
                            _rtpCallRecorder = new RtpCallRecorder();
                            _rtpCallRecorder.StartRecording(_lastCalledNumber, callStartTime, GetRecordingsDirectory());
                            _lastRecordingFilePath = null;
                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Call recording started for incoming call from {_lastCalledNumber} (after AcceptCall)");
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error starting call recording after AcceptCall: {ex.Message}");
                            _rtpCallRecorder = null;
                        }
                    }
                }
                
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

                if (!ok)
                {
                    SetStatus($"Failed to answer incoming call from {savedCaller} - Answer() returned false");
                    // Пробуем получить больше информации об ошибке
                    try
                    {
                        var mediaEndPoints = _wasapiAudioEndPoint?.ToMediaEndPoints() ?? _audioEndPoint?.ToMediaEndPoints();
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
                    var mediaEndPoints = _wasapiAudioEndPoint?.ToMediaEndPoints() ?? _audioEndPoint?.ToMediaEndPoints();
                    
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
                            
                            var startSinkMethod = mediaEndPoints.AudioSink.GetType().GetMethod("StartAudio", BindingFlags.Public | BindingFlags.Instance);
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
            _toneGenerator?.Stop();
            MainWindow.Log("[SipService] Stopping tones on hangup");
            
            // Отменяем задачу звонка, если она еще выполняется (звонок еще не принят)
            if (_callCancellationTokenSource != null && !_callCancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    _callCancellationTokenSource.Cancel();
                    MainWindow.Log("[SipService] Call task cancelled");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[SipService] Error cancelling call task: {ex.Message}");
                }
            }
            
            if (_userAgent == null)
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnCallEnded?.Invoke()));
                return;
            }

            if (_userAgent.IsCallActive)
            {
                SetStatus("Hanging up call...");
                try
                {
                    // Сбрасываем состояние мута при завершении звонка
                    IsMuted = false;
                    
                    // Размутим микрофон при завершении звонка
                    try
                    {
                        MicrophoneMuteHelper.SetMicrophoneMute(false);
                    }
                    catch
                    {
                        // Игнорируем ошибки
                    }
                    
                    // Сначала завершаем SIP-звонок
                    _userAgent.Hangup();
                    
                    // Останавливаем запись звонка и диагностику
                    StopCallRecording();
                    StopRtpDiagnostics();

                    // Сбрасываем флаг активного звонка ПЕРЕД закрытием медиа-сессии
                    // Это важно, чтобы следующий звонок мог правильно определить, что нужно закрыть старую сессию
                    _wasCallActive = false;

                    // Закрываем медиа-сессию синхронно, чтобы гарантировать полное закрытие перед следующим звонком
                    // Это критично для второго и последующих входящих звонков
                    try
                    {
                        MainWindow.Log("[SipService] Closing media session on hangup");
                        _voipMediaSession?.Close("hangup");
                        _voipMediaSession = null; // Обнуляем ссылку после закрытия
                        MainWindow.Log("[SipService] Media session closed and nulled");
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[SipService] Error closing media session: {ex.Message}");
                        _voipMediaSession = null; // Все равно обнуляем ссылку
                    }
                    
                    SetStatus("Call ended");
                    // Вызываем OnCallEnded через Dispatcher, чтобы не блокировать UI поток
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnCallEnded?.Invoke()));
                }
                catch (Exception ex)
                {
                    SetStatus($"Hangup error: {ex.Message}");
                    // Все равно вызываем OnCallEnded через Dispatcher, чтобы уведомить UI
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnCallEnded?.Invoke()));
                    throw;
                }
            }
            else
            {
                // Если звонок не активен, но может быть в процессе установления (ожидание ответа)
                // Пытаемся отправить CANCEL или просто закрываем медиа-сессию
                SetStatus("Cancelling call...");
                MainWindow.Log("[SipService] Call cancelled by local user (CANCEL sent) - call was not yet answered");
                
                // Устанавливаем флаг отмены, чтобы не воспроизводить гудки для последующих SIP ответов
                _isCallCancelled = true;
                
                try
                {
                    // Пытаемся отменить звонок через UserAgent, если есть такой метод
                    // В SIPSorcery для отмены звонка можно использовать Cancel() или просто Hangup()
                    try
                    {
                        // Проверяем, есть ли метод Cancel через рефлексию
                        var cancelMethod = _userAgent.GetType().GetMethod("Cancel", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (cancelMethod != null)
                        {
                            cancelMethod.Invoke(_userAgent, null);
                            MainWindow.Log("[SipService] Call cancelled via Cancel() method");
                        }
                        else
                        {
                            // Если метода Cancel нет, пытаемся вызвать Hangup (может работать для отмены)
                            // Это отправит CANCEL запрос, если звонок еще не принят
                            _userAgent.Hangup();
                            MainWindow.Log("[SipService] Call cancelled via Hangup() method (sending CANCEL)");
                        }
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[SipService] Error cancelling call: {ex.Message}");
                    }
                    
                    // Закрываем медиа-сессию, если она была открыта
                try
                {
                    _voipMediaSession?.Close("hangup");
                }
                catch
                {
                    // Игнорируем ошибки
                }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[SipService] Error during call cancellation: {ex.Message}");
                }
                
                // Очищаем ссылку на задачу звонка
                _activeCallTask = null;
                _callCancellationTokenSource?.Dispose();
                _callCancellationTokenSource = null;
                
                // SIP recording disabled (WebRTC-only).
                
                SetStatus("Call cancelled");
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnCallEnded?.Invoke()));
            }
        }

        private async System.Threading.Tasks.Task ReinitializeMediaSessionAsync()
        {
            try
            {
                SetStatus("Reinitializing media session...");
                
                // Закрываем текущую медиа-сессию
                try
                {
                    _voipMediaSession?.Close("reinitialize");
                }
                catch
                {
                    // Игнорируем ошибки при закрытии
                }
                _voipMediaSession = null;

                // VoIPMediaSession.Close() permanently closes the audio endpoint
                // Must create a new one
                try { _wasapiAudioEndPoint?.Dispose(); } catch { }
                _wasapiAudioEndPoint = null;
                try { _audioEndPoint?.CloseAudio(); } catch { }
                _audioEndPoint = null;

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
            _rtpSequenceGaps = 0;
            _rtpLastSequenceNumber = -1;
            _rtpPayloadTypeCounts.Clear();
            _rtpTotalBytesReceived = 0;
            _rtpStatsStartTime = DateTime.UtcNow;
            
            // Subscribe to RTP packets
            if (_voipMediaSession != null)
            {
                _voipMediaSession.OnRtpPacketReceived += DiagnosticRtpPacketReceived;
            }
            
            // Log stats every 5 seconds
            _rtpStatsTimer = new System.Threading.Timer(LogRtpStats, null, 5000, 5000);
            
            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}RTP diagnostics started");
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
            MainWindow.Log($"[SipService] {connLabel}=== RTP DIAGNOSTICS SUMMARY ===");
            MainWindow.Log($"[SipService] {connLabel}  Duration: {duration:F1}s");
            MainWindow.Log($"[SipService] {connLabel}  Total packets received: {_rtpPacketsReceived}");
            MainWindow.Log($"[SipService] {connLabel}  Total bytes received: {_rtpTotalBytesReceived}");
            MainWindow.Log($"[SipService] {connLabel}  Sequence gaps: {_rtpSequenceGaps}");
            MainWindow.Log($"[SipService] {connLabel}  Expected packets (50/sec): {(int)(duration * 50)}");
            MainWindow.Log($"[SipService] {connLabel}  Packet loss: {Math.Max(0, (int)(duration * 50) - _rtpPacketsReceived)} packets");
            
            if (_rtpPayloadTypeCounts.Count > 0)
            {
                var ptCounts = string.Join(", ", _rtpPayloadTypeCounts.Select(kv => $"PT{kv.Key}={kv.Value}"));
                MainWindow.Log($"[SipService] {connLabel}  Payload types: {ptCounts}");
                
                // Check if non-negotiated payload types were received
                foreach (var pt in _rtpPayloadTypeCounts)
                {
                    if (pt.Key != _rtpNegotiatedPayloadType && pt.Key != 101) // 101 = telephone-event
                    {
                        MainWindow.Log($"[SipService] {connLabel}  ⚠ WARNING: Received PT{pt.Key} ({pt.Value} packets) but negotiated codec is PT{_rtpNegotiatedPayloadType}!");
                    }
                }
            }
            MainWindow.Log($"[SipService] {connLabel}=== END RTP DIAGNOSTICS ===");
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
                MainWindow.Log($"[SipService] {connLabel}✓ FIRST RTP packet: PT={pt}, seq={seq}, len={payloadLen}, from={remoteEndPoint}");
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
                            _diagnosticWavWriter = new NAudio.Wave.WaveFileWriter(wavPath, new NAudio.Wave.WaveFormat(8000, 16, 1));
                            MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}Diagnostic WAV: {wavPath}");
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
                                        MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2] " : "")}RTP audio level: max={maxAmplitude}, rms={rms:F0}, samples={pcm.Length}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_rtpPacketsReceived <= 5)
                        MainWindow.Log($"[SipService] Diagnostic WAV error: {ex.Message}");
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
            
            MainWindow.Log($"[SipService] {connLabel}RTP stats: {_rtpPacketsReceived} pkts ({pps:F1}/s), gaps={_rtpSequenceGaps}, bytes={_rtpTotalBytesReceived}, PT=[{ptInfo}]");
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
            MainWindow.Log("[SipService] TrySubscribeToOutboundRtp called");

            if (_voipMediaSession == null)
            {
                MainWindow.Log("[SipService] voipMediaSession is null");
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
                            MainWindow.Log($"[SipService] ✓ FOUND RTPChannel in AudioStream: {path}");
                            TrySubscribeToRtpChannel(ch);
                            return;
                        }
                        else
                        {
                            MainWindow.Log("[SipService] RTPChannel not found in AudioStream graph");
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
                            MainWindow.Log($"[SipService] ✓ FOUND RTPChannel in MediaEndPoints: {path}");
                            TrySubscribeToRtpChannel(ch2);
                            return;
                        }
                        else
                        {
                            MainWindow.Log("[SipService] RTPChannel not found in MediaEndPoints graph");
                        }
                    }
                }

                // 3) если удалось достать RTPSession — тоже логируем путь
                if (rtpSessionType != null)
                {
                    var s = FindByType(_voipMediaSession, rtpSessionType, 6, out path);
                    if (s != null)
                    {
                        MainWindow.Log($"[SipService] ✓ FOUND RTPSession: {path}");
                        // Раньше здесь была попытка подписаться на RtpSession событиями,
                        // но текущее решение использует TapAudioSource + RtpCallRecorder,
                        // поэтому дополнительная подписка не требуется.
                        return;
                    }
                    else
                    {
                        MainWindow.Log("[SipService] RTPSession not found in VoIPMediaSession graph");
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] Error subscribing to outbound RTP: {ex.Message}, stack: {ex.StackTrace}");
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
                MainWindow.Log($"[SipService] MediaStreamTrack type: {trackType.FullName}");
                
                // Ищем поля и свойства, которые могут содержать RTP канал или session
                var fields = trackType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                var properties = trackType.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                
                MainWindow.Log($"[SipService] MediaStreamTrack fields: {string.Join(", ", fields.Select(f => $"{f.Name}:{f.FieldType.Name}"))}");
                MainWindow.Log($"[SipService] MediaStreamTrack properties: {string.Join(", ", properties.Select(p => $"{p.Name}:{p.PropertyType.Name}"))}");
                
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
                            MainWindow.Log($"[SipService] Found potential RTP channel in MediaStreamTrack: {field.Name}, type: {value.GetType().FullName}");
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
                MainWindow.Log($"[SipService] Error in TrySubscribeToMediaStreamTrack: {ex.Message}");
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
                MainWindow.Log($"[SipService] RTPChannel type: {channelType.FullName}");
                
                // Ищем события отправки RTP
                var events = channelType.GetEvents(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                MainWindow.Log($"[SipService] RTPChannel events ({events.Length}): {string.Join(", ", events.Select(e => e.Name))}");
                
                // Ищем ВСЕ методы (не только с "Send" в имени)
                var methods = channelType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                
                // Ищем методы, которые могут отправлять RTP (Send, SendRtp, SendPacket, Write, SendTo и т.д.)
                var sendMethods = methods.Where(m => 
                    (m.Name.Contains("Send") || m.Name.Contains("Write") || m.Name.Contains("SendTo")) &&
                    m.GetParameters().Length > 0 &&
                    !m.Name.Contains("Received") &&
                    !m.IsSpecialName).ToList();
                
                MainWindow.Log($"[SipService] RTPChannel all methods ({methods.Length}): {string.Join(", ", methods.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Take(3).Select(p => p.ParameterType.Name))})"))}");
                MainWindow.Log($"[SipService] RTPChannel potential send methods ({sendMethods.Count}): {string.Join(", ", sendMethods.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})"))}");
                
                // Пробуем найти событие OnRtpPacketSent или аналогичное
                var onRtpPacketSentEvent = events.FirstOrDefault(e => 
                    e.Name.Contains("RtpPacketSent") || 
                    e.Name.Contains("RtpSent") || 
                    e.Name.Contains("OnSend") ||
                    e.Name.Contains("PacketSent") ||
                    e.Name.Contains("RtpSend"));
                
                if (onRtpPacketSentEvent != null)
                {
                    MainWindow.Log($"[SipService] Found RTP send event: {onRtpPacketSentEvent.Name}, type: {onRtpPacketSentEvent.EventHandlerType?.FullName}");
                    TrySubscribeToRtpEvent(rtpChannel, onRtpPacketSentEvent);
                }
                
                // Пробуем подписаться на OnRTPDataReceived и фильтровать по направлению
                var onRtpDataReceivedEvent = events.FirstOrDefault(e => e.Name == "OnRTPDataReceived" || e.Name.Contains("RTPDataReceived"));
                if (onRtpDataReceivedEvent != null)
                {
                    MainWindow.Log($"[SipService] Found OnRTPDataReceived event, but this is for INBOUND data. Need to find OUTBOUND path.");
                }
                
                // Если событие не найдено, пробуем перехватить через методы Send
                if (onRtpPacketSentEvent == null && sendMethods.Count > 0)
                {
                    MainWindow.Log("[SipService] No RTP send event found, trying to intercept Send method...");
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
                    MainWindow.Log($"[SipService] RTPChannel socket/transport fields ({socketFields.Count}): {string.Join(", ", socketFields.Select(f => $"{f.Name}:{f.FieldType.Name}"))}");
                    
                    // Пробуем перехватить отправку через socket
                    foreach (var socketField in socketFields)
                    {
                        var socket = socketField.GetValue(rtpChannel);
                        if (socket != null)
                        {
                            MainWindow.Log($"[SipService] Found socket/transport: {socketField.Name}, type: {socket.GetType().FullName}");
                            TryInterceptSocketSend(socket);
                        }
                    }
                }
                else
                {
                    MainWindow.Log("[SipService] No socket/transport fields found in RTPChannel");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] Error in TrySubscribeToRtpChannel: {ex.Message}, stack: {ex.StackTrace}");
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
                    MainWindow.Log("[SipService] Event handler type is null");
                    return;
                }

                MainWindow.Log($"[SipService] Event handler type: {handlerType.FullName}");
                
                // Пробуем создать обработчик события
                // Обычно это Action<RtpPacket> или EventHandler<RtpPacketEventArgs>
                if (handlerType == typeof(Action<SIPSorcery.Net.RTPPacket>))
                {
                    Action<SIPSorcery.Net.RTPPacket> handler = (packet) =>
                    {
                        OnOutboundRtpPacket(packet);
                    };
                    eventInfo.AddEventHandler(rtpChannel, handler);
                    MainWindow.Log("[SipService] ✓ Subscribed to RTP send event (Action<RTPPacket>) - outbound RTP interception active!");
                }
                else if (handlerType.IsGenericType && handlerType.GetGenericTypeDefinition() == typeof(Action<>))
                {
                    var genericArg = handlerType.GetGenericArguments()[0];
                    MainWindow.Log($"[SipService] Event is Action<{genericArg.Name}>");
                    // TODO: Попробовать создать обработчик для других типов
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] Error subscribing to RTP event: {ex.Message}");
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
                MainWindow.Log($"[SipService] Socket type: {socketType.FullName}");
                
                // Ищем методы SendTo, Send, SendAsync в socket
                var methods = socketType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var sendMethods = methods.Where(m => 
                    (m.Name.Contains("Send") || m.Name.Contains("SendTo")) &&
                    m.GetParameters().Length > 0).ToList();
                
                MainWindow.Log($"[SipService] Socket send methods ({sendMethods.Count}): {string.Join(", ", sendMethods.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Take(3).Select(p => p.ParameterType.Name))})"))}");
                
                // Пробуем найти событие отправки
                var events = socketType.GetEvents(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                MainWindow.Log($"[SipService] Socket events ({events.Length}): {string.Join(", ", events.Select(e => e.Name))}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] Error in TryInterceptSocketSend: {ex.Message}");
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
                    MainWindow.Log($"[SipService] Found Send method: {sendMethod.Name}, parameters: {string.Join(", ", sendMethod.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))}");
                    
                    // Сохраняем ссылку на канал и метод
                    _rtpChannelInstance = rtpChannel;
                    _originalSendMethod = sendMethod;
                    
                    MainWindow.Log("[SipService] Send method found - will try to intercept via wrapper");
                    // Примечание: Полный перехват через reflection требует более сложной логики
                    // Пока просто логируем, что метод найден
                }
                else
                {
                    MainWindow.Log("[SipService] Suitable Send method not found for interception");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] Error intercepting Send method: {ex.Message}");
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
            RtpCallRecorder? recorder;
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
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error processing outbound RTP packet: {ex.Message}");
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

            try { _wasapiAudioEndPoint?.Dispose(); } catch { }
            _wasapiAudioEndPoint = null;
            try { _audioEndPoint?.CloseAudio(); } catch { }
            _audioEndPoint = null;
            
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
            RtpCallRecorder? recorder;
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
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error recording inbound RTP: {ex.Message}");
            }
        }
        

        /// <summary>
        /// Records raw 48 kHz mono PCM samples from the microphone (full bandwidth, before codec encoding).
        /// Called from WasapiAudioEndPoint.OnRawPcmFrameTap on the capture thread.
        /// </summary>
        private void RecordOutboundRawPcm(short[] pcm48k)
        {
            RtpCallRecorder? recorder;
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
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error recording outbound raw PCM: {ex.Message}");
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
        /// Останавливает запись звонка и сохраняет файл
        /// Использует lock для защиты от race conditions при множественных звонках
        /// Защищен от множественных вызовов через флаг _isStoppingRecording
        /// </summary>
        private async void StopCallRecording()
        {
            RtpCallRecorder? recorderToStop = null;
            
            // Извлекаем ссылку на рекордер под lock, чтобы избежать race condition
            lock (_recorderLock)
            {
                // Защита от множественных вызовов
                if (_isStoppingRecording || _rtpCallRecorder == null)
                    return;
                    
                _isStoppingRecording = true; // Устанавливаем флаг перед остановкой
                recorderToStop = _rtpCallRecorder;

                // Уже на этом этапе кешируем путь к файлу, чтобы он был доступен CallWindow/AmoCRM
                // даже пока асинхронная остановка записи еще выполняется.
                if (recorderToStop?.RecordingFilePath != null)
                {
                    _lastRecordingFilePath = recorderToStop.RecordingFilePath;
                }

                // Очищаем ссылку, чтобы следующий звонок мог создать новый рекордер.
                _rtpCallRecorder = null;
            }
            
            // Останавливаем запись вне lock, чтобы не блокировать другие операции
            try
            {
                if (recorderToStop != null && recorderToStop.IsRecording)
                {
                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Stopping call recording...");
                    await recorderToStop.StopRecordingAsync(DateTime.Now);
                    
                    // Сохраняем финальный путь к файлу, чтобы его могли увидеть CallWindow/AmoCRM
                    _lastRecordingFilePath = recorderToStop.RecordingFilePath;
                    MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Call recording stopped. File: {_lastRecordingFilePath ?? "null"}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] {(_isSecondaryConnection ? "[Connection2]" : "")} Error stopping call recording: {ex.Message}");
            }
            finally
            {
                // Сбрасываем флаг после завершения остановки
                lock (_recorderLock)
                {
                    _isStoppingRecording = false;
                }
            }
        }
        
        /// <summary>
        /// Проверяет, включен ли WebRTC режим для звонков
        /// </summary>
        private bool ShouldUseWebRtc()
        {
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(json);
                    return settings?.UseWebRtcAudio ?? false;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] Error checking WebRTC setting: {ex.Message}");
            }
            return false;
        }
    }
}
