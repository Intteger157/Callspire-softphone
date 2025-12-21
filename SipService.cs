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
        
        // Cooldown для игнорирования SIP входящих после завершения WebRTC звонка
        private DateTime? _ignoreSipUntil = null;
        private readonly object _ignoreSipLock = new object();

        private SIPTransport? _sipTransport;
        private SIPUserAgent? _userAgent;
        private SIPRegistrationUserAgent? _regUserAgent;

        private WindowsAudioEndPoint? _audioEndPoint;
        private VoIPMediaSession? _voipMediaSession;
        private bool _skipAudioInitialization = false; // Флаг для пропуска инициализации аудио (для WebRTC режима)
        private ToneGenerator? _toneGenerator; // Генератор гудков (ленивая инициализация)
        private Task<bool>? _activeCallTask; // Задача активного звонка для возможности отмены
        private System.Threading.CancellationTokenSource? _callCancellationTokenSource; // Для отмены звонка
        private bool _isCallCancelled = false; // Флаг отмены звонка пользователем (чтобы не воспроизводить гудки для последующих ответов)
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
        public string? CurrentRecordingFilePath => null; // SIP recording disabled (WebRTC-only)
        
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
            string audioCodec = "PCMU", int audioSampleRate = 8000, int audioBitrate = 64000)
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
            _instanceHash = this.GetHashCode();
            SetStatus($"SipService created, hash: {_instanceHash}");
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
        /// Включает/отключает микрофон (mute) на уровне системы Windows.
        /// Микрофон будет перечеркнут в трее Windows, как в Teams/Zoom/Discord.
        /// Также останавливает передачу аудио в RTP поток через SIPSorcery.
        /// </summary>
        public void SetMute(bool mute)
        {
            try
            {
                if (_voipMediaSession == null || _audioEndPoint == null)
                {
                    SetStatus("Audio not initialized");
                    return;
                }

                IsMuted = mute;
                MainWindow.Log($"[SipService] Microphone muted: {mute}");

                // 1. Системный mute через Windows Core Audio API
                // Это покажет перечеркнутый микрофон в трее Windows
                bool systemMuteSuccess = MicrophoneMuteHelper.SetMicrophoneMute(mute);
                
                if (!systemMuteSuccess)
                {
                    SetStatus($"Warning: Failed to set system microphone mute");
                }

                // 2. Останавливаем передачу аудио в RTP поток через SIPSorcery
                // Пробуем использовать SetPaused если доступно
                try
                {
                    var mediaEndPoints = _audioEndPoint.ToMediaEndPoints();
                    if (mediaEndPoints != null && mediaEndPoints.AudioSource != null)
                    {
                        var audioSource = mediaEndPoints.AudioSource;
                        
                        // Пробуем вызвать SetPaused через reflection
                        var setPausedMethod = audioSource.GetType().GetMethod("SetPaused",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (setPausedMethod != null)
                        {
                            setPausedMethod.Invoke(audioSource, new object[] { mute });
                        }
                        else
                        {
                            // Пробуем через Pause/Resume методы
                            if (mute)
                            {
                                var pauseMethod = audioSource.GetType().GetMethod("Pause",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (pauseMethod != null)
                                {
                                    pauseMethod.Invoke(audioSource, null);
                                }
                            }
                            else
                            {
                                var resumeMethod = audioSource.GetType().GetMethod("Resume",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (resumeMethod != null)
                                {
                                    resumeMethod.Invoke(audioSource, null);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Если не удалось управлять AudioSource, это не критично
                    // Системный mute все равно работает
                    System.Diagnostics.Debug.WriteLine($"Failed to pause/resume AudioSource: {ex.Message}");
                }

                SetStatus(mute ? "Microphone muted" : "Microphone unmuted");
            }
            catch (Exception ex)
            {
                SetStatus($"Mute error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"SetMute error: {ex}");
            }
        }
        

        /// <summary>
        /// Ставит звонок на удержание или возобновляет его.
        /// </summary>
        public Task HoldCallAsync(bool hold)
        {
            if (_userAgent == null || !IsInCall)
            {
                SetStatus("No active call to hold.");
                return Task.CompletedTask;
            }

            try
            {
                IsOnHold = hold;
                
                if (hold)
                {
                    // Для удержания вызова нужно отправить Re-INVITE с SDP, где a=sendonly или a=inactive
                    // Это означает, что мы не отправляем аудио, но можем получать
                    // В SIPSorcery это можно сделать через Reinvite или изменение SDP
                    if (_voipMediaSession != null)
                    {
                        // Временно останавливаем отправку аудио
                        // Примечание: Полная реализация требует изменения SDP в Re-INVITE
                        SetStatus("Call on hold");
                    }
                }
                else
                {
                    // Возобновляем звонок - отправляем Re-INVITE с нормальным SDP (a=sendrecv)
                    if (_voipMediaSession != null)
                    {
                        // Возобновляем отправку аудио
                        SetStatus("Call resumed");
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Hold error: {ex.Message}");
            }
            
            return Task.CompletedTask;
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
        /// Получает локальный IP адрес для использования в Contact URI
        /// </summary>
        private string GetLocalIPAddress()
        {
            try
            {
                // Пробуем получить IP адрес, который используется для подключения к серверу
                // Сначала пробуем подключиться к серверу, чтобы определить правильный интерфейс
                using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, 
                    System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp))
                {
                    socket.Connect(_server, _port);
                    var localEndPoint = socket.LocalEndPoint as IPEndPoint;
                    if (localEndPoint != null)
                    {
                        return localEndPoint.Address.ToString();
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
                        return ip.ToString();
                    }
                }
            }
            catch
            {
                // Если ничего не получилось, используем localhost
            }
            
            return "127.0.0.1";
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
            
            // Если аудио уже инициализировано, проверяем состояние медиа-сессии
            if (_audioEndPoint != null)
            {
                // Если медиа-сессия существует и валидна, не переинициализируем
                if (_voipMediaSession != null)
                {
                    return;
                }
                
                // Если медиа-сессия отсутствует, но эндпоинт есть, создаем только медиа-сессию
                // Это оптимизация для повторного использования аудио эндпоинта
                try
                {
                    var mediaEndPoints = _audioEndPoint.ToMediaEndPoints();
                    if (mediaEndPoints != null)
                    {
                        _voipMediaSession = new VoIPMediaSession(mediaEndPoints)
                        {
                            AcceptRtpFromAny = true
                        };
                        MainWindow.Log("[SipService] Media session recreated from existing audio endpoint");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[SipService] Cannot recreate media session from existing endpoint: {ex.Message}, will reinitialize fully");
                    // Продолжаем полную переинициализацию
                }
            }

            // Закрываем предыдущие экземпляры, если они есть (только если нужна полная переинициализация)
            try
            {
                _voipMediaSession?.Close("reinitialize");
            }
            catch
            {
                // Игнорируем ошибки при закрытии
            }
            
            // ВАЖНО: НЕ закрываем _audioEndPoint здесь, если он уже существует и работает
            // Закрываем только если force=true или если эндпоинт не может быть переиспользован
            if (force || _audioEndPoint == null)
            {
                try
                {
                    _audioEndPoint?.CloseAudio();
                }
                catch
                {
                    // Игнорируем ошибки при закрытии
                }
                _audioEndPoint = null;
            }

            _voipMediaSession = null;

            try
            {
                SetStatus("Initializing audio...");
                
                // Используем только аудио кодировщик, без видео
                var audioEncoder = new AudioEncoder();
                
                // КЛАССИЧЕСКИЙ endpoint SIPSorcery, без PortAudio и без смены sample rate
                try
                {
                    _audioEndPoint = new WindowsAudioEndPoint(audioEncoder);
                }
                catch (MissingMethodException ex)
                {
                    string errorMsg = $"Library version incompatibility detected. Please ensure SIPSorcery and SIPSorceryMedia.Windows versions match. Error: {ex.Message}";
                    if (ex.Message.Contains("VideoTestPatternSource") || ex.Message.Contains("OnVideoSourceRawSampleFaster"))
                    {
                        errorMsg += "\n\nSolution: Update both SIPSorcery and SIPSorceryMedia.Windows to the same version (recommended: 6.1.0 or later).";
                    }
                    throw new InvalidOperationException(errorMsg, ex);
                }
                catch (TypeLoadException ex)
                {
                    string errorMsg = $"Failed to load audio endpoint type. This is likely due to library version incompatibility. Error: {ex.Message}";
                    if (ex.Message.Contains("VideoTestPatternSource"))
                    {
                        errorMsg += "\n\nSolution: Update both SIPSorcery and SIPSorceryMedia.Windows to matching versions.";
                    }
                    throw new InvalidOperationException(errorMsg, ex);
                }
                catch (Exception ex)
                {
                    string errorMsg = $"Failed to create WindowsAudioEndPoint: {ex.Message}";
                    if (ex.InnerException != null)
                    {
                        errorMsg += $" ({ex.InnerException.Message})";
                    }
                    throw new InvalidOperationException(errorMsg, ex);
                }
                
                if (_audioEndPoint == null)
                {
                    throw new InvalidOperationException("Failed to create WindowsAudioEndPoint");
                }
                
                var mediaEndPoints = _audioEndPoint.ToMediaEndPoints();
                if (mediaEndPoints == null)
                {
                    throw new InvalidOperationException("No media endpoints available");
                }
                
                // Усиление микрофона через AmplifiedAudioSource
                // Цепочка: WindowsAudioEndPoint → AmplifiedAudioSource → TapAudioSource → VoIPMediaSession
                IAudioSource? originalAudioSource = null;
                TapAudioSource? tapAudioSource = null;
                if (mediaEndPoints.AudioSource is IAudioSource src)
                {
                    originalAudioSource = src;
                    // Усиление 6x, чтобы было заметно громче, но без жесткого клиппинга
                    double gain = 6.0; // можно 8.0, если не будет хрипеть
                    var amplifiedSource = new AmplifiedAudioSource(src, (float)gain);
                    
                    // TapAudioSource - последний в цепочке, перехватывает все samples перед VoIPMediaSession
                    tapAudioSource = new TapAudioSource(amplifiedSource);
                    
                    // Подписываемся на tap для записи outbound PCM
                    int _tapCount = 0;
                    tapAudioSource.OnTapRawSample = (rate, durationMs, samples) =>
                    {
                        var c = System.Threading.Interlocked.Increment(ref _tapCount);
                        if (c == 1)
                        {
                            MainWindow.Log($"[TapAudioSource] FIRST tap sample: rate={rate}, dur={durationMs}, len={samples?.Length ?? 0}, peak={(samples != null && samples.Length > 0 ? samples.Max(s => Math.Abs(s)) : 0)}");
                        }
                        
                        // SIP recording disabled (WebRTC-only)
                    };
                    
                    mediaEndPoints.AudioSource = tapAudioSource;
                    MainWindow.Log("[SipService] AudioSource chain: WindowsAudioEndPoint → AmplifiedAudioSource → TapAudioSource → VoIPMediaSession");
                }
                
                // SIP recording disabled (WebRTC-only): do not wrap AudioSink.
                
                _voipMediaSession = new VoIPMediaSession(mediaEndPoints)
                {
                    AcceptRtpFromAny = true
                };
                
                // TapAudioSource уже настроен выше, дополнительная подписка не нужна
                
                if (_voipMediaSession == null)
                {
                    throw new InvalidOperationException("Failed to create VoIPMediaSession");
                }
                
                // ДИАГНОСТИКА: логируем тип и структуру VoIPMediaSession для поиска RtpSession
                try
                {
                    MainWindow.Log($"[SipService] VoIPMediaSession type: {_voipMediaSession.GetType().FullName}");
                    
                    var props = _voipMediaSession.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    MainWindow.Log($"[SipService] VoIPMediaSession properties ({props.Length}): {string.Join(", ", props.Select(p => $"{p.Name}:{p.PropertyType.Name}"))}");
                    
                    var fields = _voipMediaSession.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    MainWindow.Log($"[SipService] VoIPMediaSession fields ({fields.Length}): {string.Join(", ", fields.Select(f => $"{f.Name}:{f.FieldType.Name}"))}");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[SipService] ERROR logging VoIPMediaSession structure: {ex.Message}");
                }
                
                // Подписываемся на RTP события для записи
                // Примечание: Inbound RTP перехватывается через GotAudioRtp в RecordingAudioSink
                // Outbound RTP пытаемся перехватить через события RTPSession или через AudioSource
                // Попытка подписки будет сделана после начала звонка, когда RTP session уже создан

                // Проверяем, что AudioSink настроен правильно
                try
                {
                    if (mediaEndPoints.AudioSink != null)
                    {
                        var audioSinkType = mediaEndPoints.AudioSink.GetType().Name;
                        SetStatus($"AudioSink configured: {audioSinkType}");
                    }
                    else
                    {
                        SetStatus("WARNING: AudioSink is null - audio output may not work!");
                    }
                }
                catch (Exception ex)
                {
                    SetStatus($"Error checking AudioSink: {ex.Message}");
                }
                
                // Настройка кодеков: G.722 первым, потом G.711
                try
                {
                    var codecs = new List<object>();

                    // 1. Пытаемся найти G.722 (широкополосный кодек 16 kHz)
                    var g722Type =
                        Type.GetType("SIPSorceryMedia.Abstractions.G722Codec, SIPSorceryMedia.Abstractions") ??
                        Type.GetType("SIPSorceryMedia.Windows.Codecs.G722Codec, SIPSorceryMedia.Windows");

                    if (g722Type != null)
                    {
                        var g722 = Activator.CreateInstance(g722Type);
                        if (g722 != null) codecs.Add(g722);
                    }

                    // 2. G.711 μ-law (PCMU)
                    var pcmuType =
                        Type.GetType("SIPSorceryMedia.Abstractions.PCMUCodec, SIPSorceryMedia.Abstractions") ??
                        Type.GetType("SIPSorceryMedia.Windows.Codecs.PCMUCodec, SIPSorceryMedia.Windows") ??
                        Type.GetType("SIPSorcery.Media.PCMUCodec, SIPSorcery");
                        
                    if (pcmuType != null)
                        {
                        var pcmu = Activator.CreateInstance(pcmuType);
                        if (pcmu != null) codecs.Add(pcmu);
                    }

                    // 3. G.711 A-law (PCMA)
                    var pcmaType =
                        Type.GetType("SIPSorceryMedia.Abstractions.PCMACodec, SIPSorceryMedia.Abstractions") ??
                        Type.GetType("SIPSorceryMedia.Windows.Codecs.PCMACodec, SIPSorceryMedia.Windows") ??
                        Type.GetType("SIPSorcery.Media.PCMACodec, SIPSorcery");

                    if (pcmaType != null)
                        {
                        var pcma = Activator.CreateInstance(pcmaType);
                        if (pcma != null) codecs.Add(pcma);
                    }

                    if (codecs.Count > 0)
                    {
                        var audioCodecsProperty = _voipMediaSession.GetType().GetProperty("AudioCodecs");
                        if (audioCodecsProperty != null && audioCodecsProperty.CanWrite)
                        {
                            audioCodecsProperty.SetValue(_voipMediaSession, codecs);
                            
                            // Логируем имена кодеков через рефлексию
                            var codecNames = new List<string>();
                            foreach (var codec in codecs)
                            {
                                var nameProperty = codec?.GetType().GetProperty("Name");
                                if (nameProperty != null)
                                {
                                    codecNames.Add(nameProperty.GetValue(codec)?.ToString() ?? "unknown");
                                }
                            }
                            SetStatus($"Media codecs order: {string.Join(", ", codecNames)}");
                        }
                    }
                    else
                    {
                        SetStatus("Warning: failed to configure audio codecs explicitly, using defaults.");
                    }
                }
                catch (Exception ex)
                {
                    SetStatus($"Error configuring audio codecs: {ex.Message}");
                }
                
                // Логируем информацию об усилении
                SetStatus($"Audio initialized with AmplifiedAudioSource, gain 6.0x, source: {mediaEndPoints.AudioSource?.GetType().Name}");

                // ПРИМЕЧАНИЕ: Тест TapAudioSource при старте убран, так как он вызывает ошибку
                // (WindowsAudioEndPoint пытается отправить encoded samples через RTP, но нет активного звонка)
                // Проверка будет происходить во время реального SIP звонка через логи [TapAudioSource] RAW

                // Сбрасываем состояние мута при инициализации
                IsMuted = false;
                
                // Оптимизируем настройки микрофона для максимальной слышимости
                // Это включает: максимальную громкость, усиление и проверку mute
                try
                {
                    bool optimized = MicrophoneMuteHelper.OptimizeMicrophoneForVoIP();
                    if (optimized)
                    {
                        SetStatus("Microphone optimized for VoIP: volume set to maximum, boost enabled");
                    }
                    else
                    {
                        // Fallback: просто устанавливаем максимальную громкость
                        MicrophoneMuteHelper.SetMicrophoneVolume(1.0f);
                        SetStatus("Microphone volume set to maximum");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error optimizing microphone: {ex.Message}");
                    // Пробуем хотя бы установить громкость
                    try
                    {
                        MicrophoneMuteHelper.SetMicrophoneVolume(1.0f);
                    }
                    catch
                    {
                        // Игнорируем ошибки
                    }
                }
                
                string deviceInfo = "";
                if (_microphoneDeviceNumber.HasValue || _speakerDeviceNumber.HasValue)
                {
                    deviceInfo = " (selected devices will be used on next restart)";
                }
                SetStatus($"Audio initialized successfully (G.722/G.711, amplified mic){deviceInfo}");
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
                _voipMediaSession = null;
                
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

            // UDP-канал на любой IP, порт будет назначен автоматически.
            var listenEndPoint = new IPEndPoint(IPAddress.Any, 0);
            var udpChannel = new SIPUDPChannel(listenEndPoint);
            _sipTransport.AddSIPChannel(udpChannel);
            
            // Получаем реальный порт, который был назначен каналу
            // Это важно для правильного Contact URI в регистрации
            var actualPort = udpChannel.ListeningEndPoint.Port;
            var localIP = GetLocalIPAddress();
            SetStatus($"SIP transport listening on {localIP}:{actualPort}");
            
            // Добавляем обработчик исходящих SIP запросов для диагностики
            _sipTransport.SIPRequestOutTraceEvent += (localEndPoint, remoteEndPoint, request) =>
            {
                try
                {
                    if (request != null && request.Method == SIPMethodsEnum.INVITE)
                    {
                        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                        SetStatus($"[{timestamp}] SIP Request OUT: INVITE to {remoteEndPoint}");
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
                            // Останавливаем busy tone, если он играл (на случай, если был 401 или другая ошибка)
                            _toneGenerator?.Stop();
                            // Воспроизводим ringback tone
                            EnsureToneGenerator();
                            _toneGenerator?.PlayRingbackTone();
                        }
                        else if (statusCode == 183)
                        {
                            SetStatus($"[{timestamp}] Call progress: 183 Session Progress (early media)");
                            MainWindow.Log($"[SipService] 183 Session Progress received, starting ringback tone");
                            // Воспроизводим ringback tone
                            EnsureToneGenerator();
                            _toneGenerator?.PlayRingbackTone();
                        }
                    else if (statusCode == 200)
                    {
                        // 200 OK может быть как для INVITE, так и для REGISTER
                        // Проверяем, есть ли активный звонок
                        // Не обрабатываем 200 OK, если звонок был отменен пользователем
                        if (!_isCallCancelled && (_userAgent?.IsCallActive == true || _toneGenerator != null))
                        {
                            SetStatus($"[{timestamp}] Call progress: 200 OK (call answered, media negotiation starting)");
                            MainWindow.Log($"[SipService] 200 OK received, stopping ringback tone");
                            // Останавливаем ringback tone только если был активный звонок
                            _toneGenerator?.Stop();
                        }
                        else if (_isCallCancelled)
                        {
                            // 200 OK пришел после отмены - просто останавливаем гудки
                            MainWindow.Log($"[SipService] 200 OK received but call was cancelled, stopping tones");
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
                            else if (statusCode != 401 && !_isCallCancelled && 
                                     (_userAgent?.IsCallActive == true || _toneGenerator != null))
                            {
                                SetStatus($"[{timestamp}] Call failed: {statusCode} {statusReason}");
                                MainWindow.Log($"[SipService] Call failed with {statusCode}, stopping ringback tone and playing busy tone");
                                // Останавливаем ringback tone, если он играл
                                _toneGenerator?.Stop();
                                // Воспроизводим busy tone при ошибке (только если еще не играет)
                                EnsureToneGenerator();
                                if (_toneGenerator != null)
                                {
                                    _toneGenerator.PlayBusyTone();
                                    // Останавливаем busy tone через 2 секунды
                                    _ = Task.Delay(2000).ContinueWith(_ => _toneGenerator?.Stop());
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
                        
                        // SIP recording disabled (WebRTC-only).
                        
                        // Уведомляем UI о завершении звонка через Dispatcher
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnCallEnded?.Invoke()));
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Error handling BYE: {ex.Message}");
                        MainWindow.Log($"[SipService] Error handling BYE: {ex.Message}");
                        // SIP recording disabled (WebRTC-only).
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
                    bool useWebRtc = ShouldUseWebRtc();
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
            bool useWebRtc = ShouldUseWebRtc();
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
                SetStatus($"Registration failed: {errorMessage}");
            };

            _regUserAgent.RegistrationTemporaryFailure += (uri, response, errorMessage) =>
            {
                IsRegistered = false;
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
            if (ShouldUseWebRtc())
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
                        // Закрываем старую медиа-сессию
                        try
                        {
                            _voipMediaSession?.Close("new call");
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"[SipService] Warning: Error closing previous media session: {ex.Message}");
                            // Игнорируем ошибки при закрытии, продолжаем
                        }
                        _voipMediaSession = null;
                        
                        // ВАЖНО: НЕ закрываем _audioEndPoint полностью, так как он может быть переиспользован
                        // Закрываем только медиа-сессию, а аудио эндпоинт остается для повторного использования
                        // Это критично для стабильности при множественных звонках (100+ в день)
                        
                        // Небольшая задержка для завершения закрытия медиа-сессии
                        await System.Threading.Tasks.Task.Delay(200);
                        
                        // Переинициализируем медиа-сессию (аудио эндпоинт остается)
                        // Если медиа-сессия была закрыта, создаем новую на основе существующего эндпоинта
                        if (_audioEndPoint != null)
                        {
                            var mediaEndPoints = _audioEndPoint.ToMediaEndPoints();
                            if (mediaEndPoints != null)
                            {
                                _voipMediaSession = new VoIPMediaSession(mediaEndPoints)
                                {
                                    AcceptRtpFromAny = true
                                };
                                MainWindow.Log("[SipService] Media session reinitialized for new call (audio endpoint reused)");
                                SetStatus("Media session reinitialized and ready for new call");
                            }
                            else
                            {
                                // Если эндпоинт не может создать медиа-эндпоинты, переинициализируем полностью
                                MainWindow.Log("[SipService] Audio endpoint cannot create media endpoints, reinitializing fully");
                                InitializeAudio(throwOnError: true);
                            }
                        }
                        else
                        {
                            // Если эндпоинт был закрыт, переинициализируем полностью
                            MainWindow.Log("[SipService] Audio endpoint is null, reinitializing fully");
                            InitializeAudio(throwOnError: true);
                        }
                        
                        if (_voipMediaSession == null)
                        {
                            throw new InvalidOperationException("Failed to reinitialize media session for new call");
                        }
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[SipService] Error reinitializing media session: {ex.Message}, StackTrace: {ex.StackTrace}");
                        SetStatus($"Error reinitializing audio: {ex.Message}");
                        throw new InvalidOperationException($"Failed to reinitialize audio for new call: {ex.Message}", ex);
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
                
                // Сбрасываем флаг отмены перед новым звонком
                _isCallCancelled = false;
                
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
                        var mediaEndPoints = _audioEndPoint?.ToMediaEndPoints();
                        
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
                    // Останавливаем ringback tone при неудаче
                    _toneGenerator?.Stop();
                    // Воспроизводим busy tone (только если еще не играет)
                    EnsureToneGenerator();
                    if (_toneGenerator != null)
                    {
                        _toneGenerator.PlayBusyTone();
                        _ = Task.Delay(2000).ContinueWith(_ => _toneGenerator?.Stop());
                    }
                    
                    SetStatus("Call failed (timeout/rejected).");
                    _wasCallActive = false; // Сбрасываем флаг при неудаче
                }
            }
            catch (System.Threading.Tasks.TaskCanceledException)
            {
                // Звонок был отменен пользователем
                _toneGenerator?.Stop();
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
                _callCancellationTokenSource?.Dispose();
                _callCancellationTokenSource = null;
                _activeCallTask = null;
                SetStatus($"Call error: {ex.Message}");
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
                        var mediaEndPoints = _audioEndPoint?.ToMediaEndPoints();
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
                    var mediaEndPoints = _audioEndPoint?.ToMediaEndPoints();
                    
                    // Запускаем AudioSource (микрофон)
                    // TapAudioSource уже настроен в InitializeAudio, дополнительная подписка не нужна
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
                    
                    // ВАЖНО: НЕ закрываем _audioEndPoint полностью при Hangup, так как он может использоваться для следующего звонка
                    // Закрываем только медиа-сессию, а аудио эндпоинт остается для повторного использования
                    // Это критично для стабильности при множественных звонках (100+ в день)
                    // _audioEndPoint будет закрыт только при полном Dispose() сервиса
                    
                    // SIP recording disabled (WebRTC-only).
                    
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

                // Небольшая асинхронная задержка для завершения закрытия
                await System.Threading.Tasks.Task.Delay(200);

                // Если аудио эндпоинт существует, создаем новую медиа-сессию
                if (_audioEndPoint != null)
                {
                    var mediaEndPoints = _audioEndPoint.ToMediaEndPoints();
                    if (mediaEndPoints != null)
                    {
                        _voipMediaSession = new VoIPMediaSession(mediaEndPoints)
                        {
                            AcceptRtpFromAny = true
                        };
                        SetStatus("Media session reinitialized");
                    }
                    else
                    {
                        SetStatus("Warning: No media endpoints available for reinitialization");
                    }
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
        /// Обработчик входящих RTP пакетов (для диагностики)
        /// </summary>
        private void OnRtpPacketReceived(object? sender, SIPSorcery.Net.RTPPacket rtpPacket)
        {
            // Inbound RTP уже обрабатывается через GotAudioRtp в RecordingAudioSink
            // Этот метод для диагностики
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
                        TrySubscribeToRtpSession(s);
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
                            else
                            {
                                TrySubscribeToMediaStream(value);
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
            // SIP recording disabled (WebRTC-only).
        }

        /// <summary>
        /// Пытается найти RtpSession внутри MediaStream
        /// </summary>
        private void TrySubscribeToMediaStream(object mediaStream)
        {
            try
            {
                var streamType = mediaStream.GetType();
                MainWindow.Log($"[SipService] MediaStream type: {streamType.FullName}");
                
                // Ищем методы отправки в самом MediaStream
                var methods = streamType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                var sendMethods = methods.Where(m => 
                    (m.Name.Contains("Send") || m.Name.Contains("Write") || m.Name.Contains("SendRtp")) &&
                    m.GetParameters().Length > 0 &&
                    !m.Name.Contains("Received") &&
                    !m.IsSpecialName).ToList();
                
                if (sendMethods.Count > 0)
                {
                    MainWindow.Log($"[SipService] MediaStream send methods ({sendMethods.Count}): {string.Join(", ", sendMethods.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Take(3).Select(p => p.ParameterType.Name))})"))}");
                }
                
                // Используем рекурсивный поиск для поиска RTPChannel
                var rtpChannelType = typeof(SIPSorcery.Net.RTPChannel);
                string path;
                var rtpChannel = FindByType(mediaStream, rtpChannelType, 6, out path);
                if (rtpChannel != null)
                {
                    MainWindow.Log($"[SipService] ✓ FOUND RTPChannel in MediaStream: {path}");
                    TrySubscribeToRtpChannel(rtpChannel);
                    return;
                }
                
                // Используем рекурсивный поиск для поиска RtpSession
                var rtpSessionType = Type.GetType("SIPSorcery.Net.RtpSession, SIPSorcery") 
                                     ?? Type.GetType("SIPSorcery.Net.RTPSession, SIPSorcery");
                if (rtpSessionType != null)
                {
                    var rtpSession = FindByType(mediaStream, rtpSessionType, 6, out path);
                    if (rtpSession != null)
                    {
                        MainWindow.Log($"[SipService] ✓ FOUND RTPSession in MediaStream: {path}");
                        TrySubscribeToRtpSession(rtpSession);
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] Error in TrySubscribeToMediaStream: {ex.Message}");
            }
        }

        /// <summary>
        /// Пытается подписаться на события отправки RTP через RtpSession
        /// </summary>
        private void TrySubscribeToRtpSession(object rtpSession)
        {
            try
            {
                var sessionType = rtpSession.GetType();
                MainWindow.Log($"[SipService] RtpSession type: {sessionType.FullName}");
                
                var events = sessionType.GetEvents(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                MainWindow.Log($"[SipService] RtpSession events: {string.Join(", ", events.Select(e => e.Name))}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[SipService] Error in TrySubscribeToRtpSession: {ex.Message}");
            }
        }

        /// <summary>
        /// Обработчик исходящих RTP пакетов
        /// </summary>

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

            try
            {
                _audioEndPoint?.CloseAudio();
            }
            catch
            {
                // ignore
            }
            // SIP recording disabled (WebRTC-only).

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
        private bool IsCallRecordingEnabled() => false; // SIP recording disabled (WebRTC-only)
        
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
        // SIP recording disabled (WebRTC-only).
        
        public void RecordInboundRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[]? payload)
        {
            // SIP recording disabled (WebRTC-only).
        }
        

        /// <summary>
        /// Записывает outbound RTP payload (локальная сторона - то, что отправляется в сеть)
        /// </summary>
        public void RecordOutboundRtpPayload(byte payloadType, byte[] payload, int sourceRateHz)
        {
            // SIP recording disabled (WebRTC-only).
        }

        /// <summary>
        /// Записывает outbound RTP пакет (локальная сторона)
        /// </summary>
        /// <summary>
        /// Записывает outbound PCM аудио (локальная сторона) - устаревший метод
        /// Теперь используется TapAudioSource напрямую
        /// </summary>
        [Obsolete("Use TapAudioSource.OnTapRawSample instead")]
        public void RecordOutboundPcm(byte[]? pcmData, int sampleRate)
        {
            // SIP recording disabled (WebRTC-only).
        }

        /// <summary>
        /// Запускает запись звонка
        /// </summary>
        private void StartCallRecording(string phoneNumber, DateTime callStartTime)
        {
            // SIP recording disabled (WebRTC-only).
        }
        
        /// <summary>
        /// Останавливает запись звонка
        /// </summary>
        private void StopCallRecording()
        {
            // SIP recording disabled (WebRTC-only).
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
