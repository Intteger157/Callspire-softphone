using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Softphone.Audio;

namespace Softphone
{
    /// <summary>
    /// DTO для событий WebRTC
    /// </summary>
    public class WebRtcEventDto
    {
        public string Type { get; set; } = "";
        public string? SessionId { get; set; }
        public string? CallerNumber { get; set; }
        public string? Cause { get; set; }
        public string? Message { get; set; }
        public string? Name { get; set; } // Имя ошибки (NotAllowedError, NotFoundError, etc.)
        public string? Phase { get; set; } // Фаза ошибки (getUserMedia, ua.call, ice, ws)
        public string? Stack { get; set; } // Stack trace ошибки
        public object? Stats { get; set; }
        public JsonElement? Data { get; set; }
        /// <summary>Слот подключения, для которого пришло событие ("main" | "secondary").</summary>
        public string Slot { get; set; } = "main";
    }

    /// <summary>
    /// Информация об аудиоустройстве WebRTC
    /// </summary>
    public class WebRtcAudioDevice
    {
        public string DeviceId { get; set; } = "";
        public string Label { get; set; } = "";
        public string Kind { get; set; } = ""; // "audioinput" или "audiooutput"
    }

    /// <summary>
    /// Интерфейс WebRTC сервиса
    /// </summary>
    public interface IWebRtcService
    {
        bool IsReadyForCalls { get; }
        string? ActiveSessionId { get; }
        WebRtcCallState CurrentCallState { get; }

        event Action<WebRtcEventDto>? Event;

        Task MakeCallAsync(string number);
        Task AnswerAsync(string? sessionId = null);
        Task HangupAsync(string? sessionId = null);
        Task SetHoldAsync(bool hold, string? sessionId = null);
        Task ResetEngineAsync();
    }

    /// <summary>
    /// WebRTC сервис. Существует в двух экземплярах — по одному на слот подключения
    /// ("main" / "secondary"). Оба используют один общий WebRtcEngineHost (один WebView2),
    /// но команды и события маркируются полем slot, чтобы изолировать сессии.
    /// </summary>
    public sealed class WebRtcService : IWebRtcService
    {
        /// <summary>Слот основного подключения.</summary>
        public static WebRtcService Main { get; } = new WebRtcService("main");

        /// <summary>Слот второго подключения.</summary>
        public static WebRtcService Secondary { get; } = new WebRtcService("secondary");

        /// <summary>
        /// Получить сервис по имени слота ("main" | "secondary"). Для неизвестного слота возвращает Main.
        /// </summary>
        public static WebRtcService GetSlot(string slot)
        {
            return string.Equals(slot, "secondary", StringComparison.OrdinalIgnoreCase) ? Secondary : Main;
        }

        /// <summary>
        /// Перебор обоих слотов (для broadcast-операций вроде shutdown).
        /// </summary>
        public static IEnumerable<WebRtcService> AllSlots
        {
            get { yield return Main; yield return Secondary; }
        }

        /// <summary>
        /// Legacy-алиас для обратной совместимости. Возвращает сервис основного слота.
        /// </summary>
        [Obsolete("Используйте WebRtcService.Main или WebRtcService.GetSlot(...).")]
        public static WebRtcService Instance => Main;

        /// <summary>Идентификатор слота этого экземпляра ("main" | "secondary").</summary>
        public string Slot => _slot;

        private readonly string _slot;
        private WebRtcService(string slot) { _slot = slot; }

        private IWebRtcEngineHost? _engine;
        private readonly object _lock = new object();
        private string? _activeSessionId;
        private string? _ringingSessionId;
        private WebRtcCallState _state = WebRtcCallState.Idle;
        private bool _registered = false;
        private DateTime _lastRegisteredUtc = DateTime.MinValue; // Время последней успешной регистрации
        private DateTime _lastPongTimeUtc = DateTime.MinValue;
        private DateTime _lastEngineEventUtc = DateTime.MinValue; // any event from JS (fallback liveness)
        private bool _isResetting = false;
        private DateTime _lastResetUtc = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, DateTime> _incomingCallDedup = new();
        private System.Threading.Timer? _watchdogTimer;
        private int _watchdogInFlight = 0;
        private int _reconnectInFlight = 0;

        // Auto-reconnect (keeps WebRTC "ironclad" connected after network blips).
        private int _reinitInFlight = 0; // prevents duplicate initUA loops (e.g., Save&Connect + timer)
        private DateTime _suppressAutoReconnectUntilUtc = DateTime.MinValue; // grace window after initUA
        private DateTime _lastInitUaSentUtc = DateTime.MinValue;
        private bool _initialConnectionSafetyNetScheduled = false; // prevents multiple safety-net timers for initial connection
        private bool _wsConnectedAfterInitUa = false; // tracks if ws_connected arrived after last initUA
        private bool _autoReconnectPaused = false;
        private string? _autoReconnectPausedReason = null;

        private sealed class UaConfigSnapshot
        {
            public string WsUri { get; init; } = "";
            public string SipUri { get; init; } = "";
            public string Username { get; init; } = "";
            public string Password { get; init; } = ""; // in-memory only
        }

        private UaConfigSnapshot? _lastUaConfig;
        private System.Threading.Timer? _reconnectTimer;
        private int _reconnectAttempt = 0;
        private DateTime _nextReconnectUtc = DateTime.MinValue;
        private readonly Random _rng = new Random();

        public bool AutoReconnectEnabled { get; set; } = true;
        /// <summary>
        /// Управляет объемом логирования WebRTC.
        /// При false подавляются подробные js_log и часть детализированных сообщений.
        /// По умолчанию выключено, чтобы логи не захламляли файл — для отладки можно включить вручную.
        /// </summary>
        public bool DebugEnabled { get; set; } = false;

        private void CancelScheduledReconnectLocked(string reason)
        {
            try
            {
                _nextReconnectUtc = DateTime.MinValue;
                // Stop any pending timer firing; keep the timer instance for reuse.
                _reconnectTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            catch { }
        }

        private void TriggerReconnectNow(string reason)
        {
            // Fire-and-forget reconnect attempt with reentrancy guard.
            UaConfigSnapshot? cfg;
            IWebRtcEngineHost? engine;
            WebRtcCallState state;
            bool canAttempt;
            var nowUtc = DateTime.UtcNow;

            lock (_lock)
            {
                cfg = _lastUaConfig;
                engine = _engine;
                state = _state;
                canAttempt =
                    AutoReconnectEnabled &&
                    !_autoReconnectPaused &&
                    cfg != null &&
                    engine != null &&
                    !_isResetting &&
                    state != WebRtcCallState.Connected &&
                    !_registered &&
                    nowUtc >= _suppressAutoReconnectUntilUtc;
            }

            if (!canAttempt) return;

            if (Interlocked.Exchange(ref _reconnectInFlight, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    AppLog.Log($"[WebRtcService] AutoReconnect: starting immediate attempt (reason={reason})");
                    await TryReconnectAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[WebRtcService] AutoReconnect: immediate attempt failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _reconnectInFlight, 0);
                }
            });
        }

        private void ScheduleReconnect(string reason, bool immediate = false)
        {
            UaConfigSnapshot? cfg;
            WebRtcCallState state;
            bool enginePresent;
            string? activeSessionId;
            IWebRtcEngineHost? engine;

            lock (_lock)
            {
                if (_autoReconnectPaused)
                {
                    return;
                }

                if (DateTime.UtcNow < _suppressAutoReconnectUntilUtc)
                {
                    // We are in a grace window right after initUA; ignore transient unregistered/disconnect events.
                    return;
                }

                cfg = _lastUaConfig;
                state = _state;
                activeSessionId = _activeSessionId;
                // After sleep/resume, IsInitialized can transiently report false; don't block reconnect on that.
                enginePresent = _engine != null;
                engine = _engine;

                // Don't spam if disabled or we have no credentials yet.
                if (!AutoReconnectEnabled || cfg == null || string.IsNullOrEmpty(cfg.WsUri) || string.IsNullOrEmpty(cfg.SipUri) || string.IsNullOrEmpty(cfg.Username) || string.IsNullOrEmpty(cfg.Password))
                {
                    return;
                }

                // Don't kill an active connected call: wait for call to end.
                // Проверяем реальную активность звонка асинхронно вне lock блока
                if (state == WebRtcCallState.Connected || state == WebRtcCallState.Calling || state == WebRtcCallState.Ringing)
                {
                    // Проверяем реальную активность звонка асинхронно (вне lock)
                    _ = CheckCallActivityAndScheduleReconnect(reason, immediate, state, activeSessionId, engine);
                    return; // Временно откладываем переподключение до проверки активности
                }

                // If we are already registered/ready, do not schedule reconnect loops.
                if (_registered)
                {
                    return;
                }

                // If engine isn't initialized, we can't do anything here; MainWindow initialization will handle it.
                if (!enginePresent)
                {
                    return;
                }

                // "Immediate" still gets a tiny delay to prevent tight reconnect loops on flapping networks/servers.
                var delay = immediate ? TimeSpan.FromMilliseconds(250) : ComputeReconnectDelayLocked();
                var dueUtc = DateTime.UtcNow + delay;

                // If we already scheduled a sooner reconnect, keep it.
                if (_nextReconnectUtc != DateTime.MinValue && _nextReconnectUtc <= dueUtc)
                {
                    return;
                }

                _nextReconnectUtc = dueUtc;

                _reconnectTimer ??= new System.Threading.Timer(_ =>
                {
                    // Never overlap reconnect attempts (timer can re-enter if init is slow).
                    TriggerReconnectNow("timer");
                });

                _reconnectTimer.Change(delay, System.Threading.Timeout.InfiniteTimeSpan);

                AppLog.Log($"[WebRtcService] AutoReconnect: scheduled in {delay.TotalSeconds:F1}s (attempt={_reconnectAttempt}, state={state}, reason={reason})");
            }
        }

        /// <summary>
        /// Temporarily pauses auto-reconnect attempts (both scheduling and immediate tries).
        /// Useful when the app is using SIP calls and WebRTC reconnect loops would just spam logs.
        /// </summary>
        public void PauseAutoReconnect(string reason)
        {
            lock (_lock)
            {
                if (_autoReconnectPaused) return;
                _autoReconnectPaused = true;
                _autoReconnectPausedReason = reason;
                CancelScheduledReconnectLocked("pause");
            }
            AppLog.Log($"[WebRtcService] AutoReconnect: paused (reason={reason})");
        }

        /// <summary>
        /// Resumes auto-reconnect attempts. If we're not registered, we schedule a reconnect attempt.
        /// </summary>
        public void ResumeAutoReconnect(string reason)
        {
            bool shouldKick = false;
            lock (_lock)
            {
                if (!_autoReconnectPaused) return;
                _autoReconnectPaused = false;
                _autoReconnectPausedReason = null;
                shouldKick = AutoReconnectEnabled && !_registered && _engine != null && _lastUaConfig != null;
            }
            AppLog.Log($"[WebRtcService] AutoReconnect: resumed (reason={reason})");

            if (shouldKick)
            {
                // Small delay to avoid fighting with post-call UI teardown.
                ScheduleReconnect("resume", immediate: false);
            }
        }

        // Асинхронная проверка активности звонка перед переподключением
        private async Task CheckCallActivityAndScheduleReconnect(string reason, bool immediate, WebRtcCallState state, string? activeSessionId, IWebRtcEngineHost? engine)
        {
            bool isActuallyActive = false;
            
            try
            {
                if (engine != null && engine.IsInitialized && !string.IsNullOrEmpty(activeSessionId))
                {
                    // Используем SendAsync для проверки активности через новую команду
                    var checkCommand = new { cmd = "checkCallActivity", sessionId = activeSessionId, slot = _slot };
                    
                    // Создаем TaskCompletionSource для получения результата
                    var tcs = new TaskCompletionSource<bool>();
                    
                    // Подписываемся на событие один раз для получения результата
                    Action<WebRtcEventDto>? handler = null;
                    handler = (dto) =>
                    {
                        if (dto.Type == "call_activity_check" && dto.SessionId == activeSessionId)
                        {
                            if (dto.Data.HasValue && dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                if (dto.Data.Value.TryGetProperty("active", out var activeEl))
                                {
                                    isActuallyActive = activeEl.GetBoolean();
                                }
                            }
                            Event -= handler;
                            tcs.TrySetResult(true);
                        }
                    };
                    
                    Event += handler;
                    
                    // Отправляем команду проверки
                    await engine.SendAsync(checkCommand);
                    
                    // Ждем результат с таймаутом
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
                    var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        Event -= handler;
                        AppLog.Log($"[WebRtcService] CheckCallActivity: Timeout waiting for activity check result");
                        // При таймауте считаем звонок активным для безопасности
                        isActuallyActive = true;
                    }
                    else
                    {
                        // Результат получен через событие, isActuallyActive уже установлен в handler
                        await tcs.Task; // Ждем завершения обработки
                    }
                }
            }
            catch (Exception checkEx)
            {
                AppLog.Log($"[WebRtcService] CheckCallActivity: Error checking call activity: {checkEx.Message}");
                // В случае ошибки проверки, полагаемся на состояние из кода
                isActuallyActive = true; // Безопаснее предположить, что звонок активен
            }
            
            if (isActuallyActive)
            {
                AppLog.Log($"[WebRtcService] CheckCallActivity: Active call detected (state={state}, sessionId={activeSessionId}), deferring reconnect");
                return; // Не переподключаемся, если звонок активен
            }
            else
            {
                AppLog.Log($"[WebRtcService] CheckCallActivity: Call state is {state} but call is NOT actually active, proceeding with reconnect");
                // Звонок не активен реально, можно переподключаться
                ScheduleReconnect(reason, immediate);
            }
        }

        private TimeSpan ComputeReconnectDelayLocked()
        {
            // Exponential-ish backoff with cap + jitter.
            // 0, 1, 2, 4, 8, 15, 30, 30, ...
            int attempt = Math.Max(0, _reconnectAttempt);
            int baseSeconds = attempt switch
            {
                // Avoid 0s loops; even the first retry should yield a small pause to prevent reconnect storms.
                0 => 1,
                1 => 1,
                2 => 2,
                3 => 4,
                4 => 8,
                5 => 15,
                _ => 30
            };

            // ±20% jitter
            double jitter = 0.8 + (_rng.NextDouble() * 0.4);
            var seconds = Math.Min(30, Math.Max(0, (int)Math.Round(baseSeconds * jitter)));
            return TimeSpan.FromSeconds(seconds);
        }

        private async Task TryReconnectAsync()
        {
            UaConfigSnapshot? cfg;
            IWebRtcEngineHost? engine;
            WebRtcCallState state;
            bool shouldReconnect;
            int attemptSnapshot;

            lock (_lock)
            {
                cfg = _lastUaConfig;
                engine = _engine;
                state = _state;
                shouldReconnect =
                    AutoReconnectEnabled &&
                    cfg != null &&
                    engine != null &&
                    !_isResetting &&
                    state != WebRtcCallState.Connected &&
                    !_registered &&
                    DateTime.UtcNow >= _suppressAutoReconnectUntilUtc;

                // Clear schedule marker so subsequent failures can schedule again.
                _nextReconnectUtc = DateTime.MinValue;

                if (shouldReconnect)
                {
                    _reconnectAttempt = Math.Min(_reconnectAttempt + 1, 1000);
                }
                attemptSnapshot = _reconnectAttempt;
            }

            if (!shouldReconnect || cfg == null)
            {
                return;
            }

            try
            {
                AppLog.Log($"[WebRtcService] AutoReconnect: attempting initUA (attempt={_reconnectAttempt}, state={state}, wsUri={(string.IsNullOrEmpty(cfg.WsUri) ? "empty" : "set")}, sipUri={cfg.SipUri})");
                await ReinitializeUAAsync(cfg.WsUri, cfg.SipUri, cfg.Username, cfg.Password);

                // Re-capture attempt snapshot AFTER ReinitializeUAAsync because it resets _reconnectAttempt to 0.
                int postInitAttempt;
                lock (_lock)
                {
                    postInitAttempt = _reconnectAttempt;
                }

                // If no events arrive (e.g., WS down or JS stuck), schedule a follow-up attempt only if
                // we are still not registered after a short grace period.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(6));
                        lock (_lock)
                        {
                            if (!_registered && _engine != null && !_isResetting && _state != WebRtcCallState.Connected && _reconnectAttempt == postInitAttempt)
                            {
                                ScheduleReconnect("post_initUA_check", immediate: false);
                            }
                        }
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcService] AutoReconnect: attempt failed: {ex.Message}");
                ScheduleReconnect("exception", immediate: false);
            }
        }

        public bool IsReadyForCalls
        {
            get
            {
                lock (_lock)
                {
                    return _engine != null && _engine.IsInitialized && _registered;
                }
            }
        }

        /// <summary>
        /// When true, the in-memory UA config matches these values and the UA is registered.
        /// Settings "Test Connection" uses this to avoid a second JsSIP registration: many PBXs
        /// reject a parallel REGISTER for the same extension with 401 Unauthorized.
        /// </summary>
        public bool MatchesActiveRegistration(string wsUri, string sipUri, string authUsername, string password)
        {
            lock (_lock)
            {
                if (!_registered || _lastUaConfig == null || _engine == null || !_engine.IsInitialized)
                    return false;

                static string NormWs(string? s) => SipEndpointHelper.NormalizeWebRtcWsUri(s?.Trim() ?? "");
                static string NormSip(string? s) => (s ?? "").Trim();

                if (!string.Equals(NormWs(wsUri), NormWs(_lastUaConfig.WsUri), StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!string.Equals(NormSip(sipUri), NormSip(_lastUaConfig.SipUri), StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!string.Equals((authUsername ?? "").Trim(), (_lastUaConfig.Username ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    return false;
                return string.Equals(password ?? "", _lastUaConfig.Password ?? "", StringComparison.Ordinal);
            }
        }
        
        /// <summary>
        /// Отправляет команду в JavaScript engine, авто-инжектируя slot этого сервиса.
        /// </summary>
        public async Task SendCommandAsync(object command)
        {
            if (_engine != null)
            {
                await _engine.SendAsync(InjectSlot(command));
            }
        }

        /// <summary>
        /// Гарантирует, что в команду добавлен slot этого экземпляра.
        /// Поскольку анонимные типы immutable, объект конвертируется в Dictionary при необходимости.
        /// </summary>
        private object InjectSlot(object command)
        {
            try
            {
                if (command is Dictionary<string, object> dict)
                {
                    if (!dict.ContainsKey("slot"))
                    {
                        dict["slot"] = _slot;
                    }
                    return dict;
                }

                // Анонимный объект: сериализуем -> десериализуем в Dictionary и добавляем slot
                var json = System.Text.Json.JsonSerializer.Serialize(command);
                var bag = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
                bag["slot"] = _slot;
                return bag;
            }
            catch
            {
                // В worst case вернём оригинал; engine.SendAsync обрабатывает оба формата.
                return command;
            }
        }

        public string? ActiveSessionId
        {
            get
            {
                lock (_lock)
                {
                    return _activeSessionId;
                }
            }
        }

        public WebRtcCallState CurrentCallState
        {
            get
            {
                lock (_lock)
                {
                    return _state;
                }
            }
        }

        public event Action<WebRtcEventDto>? Event;

        public void AttachEngine(IWebRtcEngineHost engine)
        {
            lock (_lock)
            {
                // Idempotent: if we're already attached to this host, just refresh the subscription
                // to make sure no other code path accidentally double-registered OnEngineEvent.
                // Double-registration would cause recording_chunk events to be processed twice and
                // corrupt the recorded audio buffer.
                if (_engine != null)
                {
                    try { _engine.EngineEvent -= OnEngineEvent; } catch { /* ignore */ }
                }

                _engine = engine;
                _engine.EngineEvent -= OnEngineEvent; // defensive: no-op if not subscribed
                _engine.EngineEvent += OnEngineEvent;
                AppLog.Log($"[WebRtcService] AttachEngine: engine attached (slot={_slot}), IsInitialized={engine.IsInitialized}");
            }
        }

        /// <summary>
        /// Detaches the current engine and resets WebRTC runtime state (used when switching to SIP mode).
        /// </summary>
        public void DetachEngine(bool resetState = true)
        {
            System.Threading.Timer? watchdogToDispose = null;
            System.Threading.Timer? reconnectToDispose = null;

            lock (_lock)
            {
                try
                {
                    if (_engine != null)
                    {
                        _engine.EngineEvent -= OnEngineEvent;
                    }
                }
                catch
                {
                    // ignore
                }

                _engine = null;

                // Stop timers when detaching engine (prevents idle leaks and reconnect loops after switching to SIP).
                watchdogToDispose = _watchdogTimer;
                _watchdogTimer = null;
                reconnectToDispose = _reconnectTimer;
                _reconnectTimer = null;
                _nextReconnectUtc = DateTime.MinValue;
                _reconnectAttempt = 0;
                System.Threading.Interlocked.Exchange(ref _watchdogInFlight, 0);
                System.Threading.Interlocked.Exchange(ref _reconnectInFlight, 0);

                if (resetState)
                {
                    _registered = false;
                    _activeSessionId = null;
                    _ringingSessionId = null;
                    _state = WebRtcCallState.Idle;
                    _initialConnectionSafetyNetScheduled = false; // Reset flag on state reset
                    _wsConnectedAfterInitUa = false; // Reset flag on state reset
                }
            }

            try { watchdogToDispose?.Dispose(); } catch { }
            try { reconnectToDispose?.Dispose(); } catch { }
        }

        // Безопасное чтение строки из JsonElement
        private static string? GetAsString(JsonElement e)
        {
            return e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number => e.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                JsonValueKind.Object => e.GetRawText(),   // объект -> raw json
                JsonValueKind.Array => e.GetRawText(),
                _ => null
            };
        }

        private void OnEngineEvent(string json)
        {
            try
            {
                var evt = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(json);

                // Slot demultiplexing: phone.js tags every event with the iframe's slot.
                // Each WebRtcService instance only processes events for its own slot.
                // Events without slot are treated as "main" for backward compatibility.
                if (evt.TryGetProperty("slot", out var slotEl) && slotEl.ValueKind == JsonValueKind.String)
                {
                    var eventSlot = slotEl.GetString() ?? "main";
                    if (!string.Equals(eventSlot, _slot, StringComparison.OrdinalIgnoreCase))
                    {
                        return; // Not for this slot.
                    }
                }
                else if (!string.Equals(_slot, "main", StringComparison.OrdinalIgnoreCase))
                {
                    // Untagged event reaches both subscribers — only the "main" slot consumes it.
                    return;
                }

                // Any event from JS indicates the WebView2 runtime is alive (even if pong is missing).
                lock (_lock)
                {
                    _lastEngineEventUtc = DateTime.UtcNow;
                }
                
                // Поддержка обоих форматов: type и cmd (для совместимости)
                string? type = null;
                if (evt.TryGetProperty("type", out var typeEl))
                {
                    type = GetAsString(typeEl);
                }
                else if (evt.TryGetProperty("cmd", out var cmdEl))
                {
                    type = GetAsString(cmdEl);
                }
                
                if (string.IsNullOrEmpty(type))
                {
                    AppLog.Log($"[WebRtcService][{_slot}] OnEngineEvent: Event without type/cmd field: {json}");
                    return;
                }
                
                // Логируем только критичные события (без полного JSON)
                if (type == "reg_failed" || type == "ua_registration_failed" || type == "error" || type == "ws_disconnected")
                {
                    AppLog.Log($"[WebRtcService][{_slot}] Event: {type}");
                }
                
                // Обработка события js_log для логирования сообщений из JavaScript
                // КРИТИЧНО: Всегда логируем js_log с level='critical', даже если DebugEnabled=false
                if (type == "js_log")
                {
                    try
                    {
                        if (evt.TryGetProperty("data", out var logDataEl) && logDataEl.ValueKind == JsonValueKind.Object)
                        {
                            string? logLevel = null;
                            if (logDataEl.TryGetProperty("level", out var levelEl))
                            {
                                logLevel = GetAsString(levelEl);
                            }
                            
                            if (logDataEl.TryGetProperty("message", out var msgEl))
                            {
                                string? logMessage = GetAsString(msgEl);
                                if (!string.IsNullOrEmpty(logMessage))
                                {
                                    // КРИТИЧНО: Всегда логируем critical сообщения, остальные только при DebugEnabled
                                    if (logLevel == "critical" || DebugEnabled)
                                    {
                                        AppLog.Log($"[WebRTC JS] {logMessage}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception logEx)
                    {
                        AppLog.Log($"[WebRtcService] Error processing js_log event: {logEx.Message}");
                    }
                }
                
                // Логируем только важные события (регистрация, ошибки, аудио для диагностики)
                // ВАЖНО: Добавляем "ua_registered" и "ua_connected" для полного логирования.
                // При выключенном DebugEnabled оставляем только самые критичные события.
                if (type == "registered" || type == "ua_registered" || type == "reg_failed" || type == "unregistered" || 
                    type == "ua_connected" || type == "ws_disconnected" || type == "error" || 
                    type == "audio_connected" || type == "audio_playing" || type == "audio_track_info" ||
                    type == "call_accepted" || type == "call_confirmed")
                {
                    if (DebugEnabled ||
                        type == "error" ||
                        type == "reg_failed" ||
                        type == "ws_disconnected")
                    {
                        AppLog.Log($"[WebRtcService] OnEngineEvent: {type}");
                    }
                }
                
                // Логируем все события записи для отладки (без вывода всего JSON, чтобы не логировать массивы audioData)
                if (type.StartsWith("recording_"))
                {
                    // Извлекаем только метаданные, не логируя весь JSON (может содержать большие массивы)
                    string? logMessage = null;
                    if (evt.TryGetProperty("data", out var recordingDataEl) && recordingDataEl.ValueKind == JsonValueKind.Object)
                    {
                        var parts = new List<string>();
                        if (recordingDataEl.TryGetProperty("size", out var sizeEl))
                        {
                            parts.Add($"size={sizeEl.GetInt32()}");
                        }
                        if (recordingDataEl.TryGetProperty("totalSize", out var totalSizeEl))
                        {
                            parts.Add($"totalSize={totalSizeEl.GetInt32()}");
                        }
                        if (recordingDataEl.TryGetProperty("totalChunks", out var totalChunksEl))
                        {
                            parts.Add($"totalChunks={totalChunksEl.GetInt32()}");
                        }
                        if (recordingDataEl.TryGetProperty("chunkIndex", out var chunkIndexEl))
                        {
                            parts.Add($"chunkIndex={chunkIndexEl.GetInt32()}");
                        }
                        if (recordingDataEl.TryGetProperty("hash", out var hashEl))
                        {
                            var hash = hashEl.GetString();
                            if (!string.IsNullOrEmpty(hash))
                            {
                                parts.Add($"hash={hash.Substring(0, Math.Min(8, hash.Length))}...");
                            }
                        }
                        if (recordingDataEl.TryGetProperty("reason", out var reasonEl))
                        {
                            parts.Add($"reason={reasonEl.GetString()}");
                        }
                        if (recordingDataEl.TryGetProperty("message", out var msgEl))
                        {
                            parts.Add($"message={msgEl.GetString()}");
                        }
                        // НЕ логируем audioData, chunk data и другие большие массивы
                        if (parts.Count > 0)
                        {
                            logMessage = string.Join(", ", parts);
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(logMessage))
                    {
                        AppLog.Log($"[WebRtcService] OnEngineEvent: {type} - {logMessage}");
                    }
                    else
                    {
                        AppLog.Log($"[WebRtcService] OnEngineEvent: {type}");
                    }
                }

                // Извлекаем sessionId из разных мест
                string? sessionId = null;
                if (evt.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
                {
                    // Пробуем извлечь sessionId из data
                    if (dataEl.TryGetProperty("sessionId", out var sessionIdEl))
                    {
                        sessionId = GetAsString(sessionIdEl);
                    }
                }

                var dto = new WebRtcEventDto
                {
                    Type = type ?? "",
                    SessionId = sessionId,
                    Slot = _slot
                };

                // When HandleIncomingCall rejects (already in call), subscribers must NOT run — otherwise UI opens a duplicate CallWindow.
                bool suppressIncomingSubscriberDispatch = false;

                lock (_lock)
                {
                    switch (type)
                    {
                        case "ws_connected":
                        case "ua_connected":
                            _wsConnectedAfterInitUa = true; // Mark that WebSocket connected after initUA
                            AppLog.Log($"[WebRtcService] OnEngineEvent: WebSocket connected ({type})");
                            break;
                        case "registered":
                        case "ua_registered":
                            _registered = true;
                            _lastRegisteredUtc = DateTime.UtcNow;
                            _reconnectAttempt = 0;
                            _nextReconnectUtc = DateTime.MinValue;
                            _initialConnectionSafetyNetScheduled = false; // Reset flag on successful registration
                            CancelScheduledReconnectLocked("registered");
                            AppLog.Log($"[WebRtcService] OnEngineEvent: UA registered ({type}), setting _registered=true, _lastRegisteredUtc={_lastRegisteredUtc:HH:mm:ss.fff}, IsReadyForCalls={IsReadyForCalls}");
                            break;
                        case "reg_failed":
                        case "ua_registration_failed":
                            // Извлекаем детали ошибки регистрации
                            string? regErrorDetails = null;
                            if (evt.TryGetProperty("data", out var regErrorDataEl))
                            {
                                if (regErrorDataEl.ValueKind == JsonValueKind.String)
                                {
                                    regErrorDetails = GetAsString(regErrorDataEl);
                                }
                                else if (regErrorDataEl.ValueKind == JsonValueKind.Object)
                                {
                                    if (regErrorDataEl.TryGetProperty("message", out var regMsgEl))
                                    {
                                        regErrorDetails = GetAsString(regMsgEl);
                                    }
                                    if (regErrorDataEl.TryGetProperty("cause", out var regCauseEl))
                                    {
                                        var cause = GetAsString(regCauseEl);
                                        regErrorDetails = string.IsNullOrEmpty(regErrorDetails) ? cause : $"{cause}: {regErrorDetails}";
                                    }
                                }
                            }
                            
                            // ВАЖНО: Не сбрасываем _registered, если регистрация была недавно успешной
                            // Это защищает от ложных reg_failed событий, которые могут прийти из SettingsWindow
                            // или из-за временных проблем с сетью
                            if (_lastRegisteredUtc != DateTime.MinValue)
                            {
                                var secondsSinceRegistration = (DateTime.UtcNow - _lastRegisteredUtc).TotalSeconds;
                                if (secondsSinceRegistration < 5)
                                {
                                    // Регистрация была недавно успешной (менее 5 секунд назад)
                                    // Не сбрасываем статус, возможно это ложное событие или временная проблема
                                    AppLog.Log($"[WebRtcService] OnEngineEvent: UA registration failed ({type}) - but recent successful registration ({secondsSinceRegistration:F1}s ago), keeping registered state");
                                    // НЕ сбрасываем _registered и _lastRegisteredUtc
                                    dto.Message = regErrorDetails ?? "Registration failed (ignored - recent success)";
                                    break;
                                }
                            }
                            
                            // КРИТИЧНО: Если есть активный звонок, проверяем реальную активность перед отменой переподключения
                            // Активные звонки могут продолжаться даже при временных проблемах с регистрацией
                            if (_state == WebRtcCallState.Connected || _state == WebRtcCallState.Calling || _state == WebRtcCallState.Ringing)
                            {
                                // Проверяем реальную активность звонка асинхронно (вне обработчика события)
                                var currentState = _state;
                                var currentSessionId = _activeSessionId;
                                var currentEngine = _engine;
                                _ = Task.Run(async () =>
                                {
                                    bool isActuallyActive = false;
                                    try
                                    {
                                        if (currentEngine != null && currentEngine.IsInitialized && !string.IsNullOrEmpty(currentSessionId))
                                        {
                                            var checkCommand = new { cmd = "checkCallActivity", sessionId = currentSessionId, slot = _slot };
                                            await currentEngine.SendAsync(checkCommand);
                                            // Результат будет получен через событие call_activity_check
                                            // Пока что полагаемся на базовую проверку состояния
                                            isActuallyActive = true; // Безопаснее предположить активность
                                        }
                                    }
                                    catch (Exception checkEx)
                                    {
                                        AppLog.Log($"[WebRtcService] Error checking call activity: {checkEx.Message}");
                                        isActuallyActive = true; // Безопаснее предположить, что звонок активен
                                    }
                                    
                                    if (!isActuallyActive)
                                    {
                                        AppLog.Log($"[WebRtcService] OnEngineEvent: UA registration failed ({type}) during call (state={currentState}), but call is NOT actually active, proceeding with reconnect");
                                        ScheduleReconnect(type ?? "reg_failed", immediate: true);
                                    }
                                });
                                
                                AppLog.Log($"[WebRtcService] OnEngineEvent: UA registration failed ({type}) during active call (state={_state}), deferring reconnect to avoid call interruption");
                                // НЕ сбрасываем _registered, чтобы звонок мог продолжаться
                                // Запланируем переподключение после завершения звонка
                                dto.Message = regErrorDetails ?? "Registration failed (deferred - active call)";
                                // НЕ вызываем ScheduleReconnect здесь синхронно - это переинициализирует UA и прервет звонок
                                break;
                            }
                            
                            // Если регистрации не было недавно и нет активного звонка, сбрасываем статус
                            _registered = false;
                            _lastRegisteredUtc = DateTime.MinValue;
                            AppLog.Log($"[WebRtcService] OnEngineEvent: UA registration failed ({type})" + 
                                (string.IsNullOrEmpty(regErrorDetails) ? "" : $" - {regErrorDetails}"));
                            dto.Message = regErrorDetails ?? "Registration failed";
                            // Auto-reconnect quickly if credentials are known.
                            // Schedule a reconnect; ScheduleReconnect already dedupes earlier attempts.
                            // Avoid also triggering an immediate attempt here to prevent reconnect storms.
                            ScheduleReconnect(type ?? "reg_failed", immediate: true);
                            break;
                        case "unregistered":
                        case "ua_unregistered":
                            // Ignore transient unregistered events right after an intentional initUA.
                            if (DateTime.UtcNow < _suppressAutoReconnectUntilUtc)
                            {
                                AppLog.Log("[WebRtcService] AutoReconnect: suppressed (ua_unregistered during initUA grace window)");
                                break;
                            }
                            _registered = false;
                            _lastRegisteredUtc = DateTime.MinValue;
                            AppLog.Log($"[WebRtcService] OnEngineEvent: UA unregistered ({type})");
                            ScheduleReconnect(type ?? "ua_unregistered", immediate: true);
                            break;
                        case "ws_disconnected":
                            // Ignore transient WS disconnect events right after an intentional initUA.
                            if (DateTime.UtcNow < _suppressAutoReconnectUntilUtc)
                            {
                                AppLog.Log("[WebRtcService] AutoReconnect: suppressed (ws_disconnected during initUA grace window)");
                                break;
                            }
                            // WS disconnect means we are not healthy/ready for calls. Always drop registered state so
                            // auto-reconnect can proceed deterministically (prevents reconnect loops while "registered=true").
                            _registered = false;
                            _lastRegisteredUtc = DateTime.MinValue;
                            AppLog.Log($"[WebRtcService] OnEngineEvent: ws_disconnected - resetting registered state");
                            // If ws_connected never arrived after the last initUA, the post-initUA watchdog already
                            // schedules a reconnect — avoid duplicate immediate retries that flood the WebView/UI thread.
                            if (!_wsConnectedAfterInitUa &&
                                _lastInitUaSentUtc != DateTime.MinValue &&
                                DateTime.UtcNow < _lastInitUaSentUtc + TimeSpan.FromSeconds(8))
                            {
                                AppLog.Log("[WebRtcService] AutoReconnect: suppressed (ws_disconnected before first ws_connected; watchdog will reconnect)");
                                break;
                            }
                            ScheduleReconnect("ws_disconnected", immediate: true);
                            break;
                        case "incoming":
                            suppressIncomingSubscriberDispatch = !HandleIncomingCall(evt, sessionId, dto);
                            break;
                        case "makeCall_started":
                            AppLog.Log($"[WebRtcService] OnEngineEvent: makeCall started");
                            break;
                        case "makeCall_initiated":
                            // Извлекаем sessionId из события makeCall_initiated
                            if (evt.TryGetProperty("data", out var initDataEl) && initDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (initDataEl.TryGetProperty("sessionId", out var initSessionIdEl))
                                {
                                    var initSessionId = GetAsString(initSessionIdEl);
                                    if (!string.IsNullOrEmpty(initSessionId))
                                    {
                                        _activeSessionId = initSessionId;
                                        dto.SessionId = initSessionId;
                                        AppLog.Log($"[WebRtcService] OnEngineEvent: makeCall initiated with sessionId: {initSessionId}");
                                    }
                                }
                            }
                            break;
                        case "new_session":
                            // Извлекаем sessionId из события new_session
                            if (evt.TryGetProperty("data", out var newSessionDataEl) && newSessionDataEl.ValueKind == JsonValueKind.Object)
                            {
                                // Пробуем разные варианты получения sessionId
                                if (newSessionDataEl.TryGetProperty("sessionId", out var newSessionIdEl))
                                {
                                    sessionId = GetAsString(newSessionIdEl);
                                }
                                
                                // Проверяем направление звонка (incoming/outgoing)
                                string? direction = null;
                                if (newSessionDataEl.TryGetProperty("direction", out var directionEl))
                                {
                                    direction = GetAsString(directionEl);
                                }
                                string? originator = null;
                                if (newSessionDataEl.TryGetProperty("originator", out var originatorEl))
                                {
                                    originator = GetAsString(originatorEl);
                                }
                                
                                // Если sessionId не найден в data, пробуем получить из самого объекта сессии через JS
                                if (!string.IsNullOrEmpty(sessionId))
                                {
                                    _activeSessionId = sessionId;
                                    
                                    // Для входящих звонков устанавливаем Ringing, для исходящих - Calling
                                    if (direction == "incoming" || originator == "remote")
                                    {
                                        _ringingSessionId = sessionId;
                                        _state = WebRtcCallState.Ringing;
                                        AppLog.Log($"[WebRtcService] OnEngineEvent: new_session (incoming) with sessionId: {sessionId}");
                                    }
                                    else
                                    {
                                        _state = WebRtcCallState.Calling;
                                        AppLog.Log($"[WebRtcService] OnEngineEvent: new_session (outgoing) with sessionId: {sessionId}");
                                    }
                                    
                                    dto.SessionId = sessionId;
                                }
                                else
                                {
                                    AppLog.Log($"[WebRtcService] OnEngineEvent: new_session without sessionId");
                                }
                            }
                            break;
                        case "call_progress":
                            // Если sessionId не был извлечен из data, пробуем еще раз
                            if (string.IsNullOrEmpty(sessionId) && evt.TryGetProperty("data", out var progressDataEl) && progressDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (progressDataEl.TryGetProperty("sessionId", out var progressSessionIdEl))
                                {
                                    sessionId = GetAsString(progressSessionIdEl);
                                    dto.SessionId = sessionId;
                                }
                            }
                            if (sessionId == _activeSessionId || string.IsNullOrEmpty(_activeSessionId))
                            {
                                if (string.IsNullOrEmpty(_activeSessionId) && !string.IsNullOrEmpty(sessionId))
                                {
                                    _activeSessionId = sessionId;
                                }
                                _state = WebRtcCallState.Calling;
                                AppLog.Log($"[WebRtcService] OnEngineEvent: call_progress for sessionId: {sessionId}");
                            }
                            break;
                        case "call_accepted":
                        case "call_confirmed":
                            // Если sessionId не был извлечен из data, пробуем еще раз
                            JsonElement acceptedDataEl = default;
                            if (string.IsNullOrEmpty(sessionId) && evt.TryGetProperty("data", out acceptedDataEl) && acceptedDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (acceptedDataEl.TryGetProperty("sessionId", out var acceptedSessionIdEl))
                                {
                                    sessionId = GetAsString(acceptedSessionIdEl);
                                    dto.SessionId = sessionId;
                                }
                            }
                            if (sessionId == _activeSessionId || string.IsNullOrEmpty(_activeSessionId))
                            {
                                if (string.IsNullOrEmpty(_activeSessionId) && !string.IsNullOrEmpty(sessionId))
                                {
                                    _activeSessionId = sessionId;
                                }
                                _state = WebRtcCallState.Connected;
                                AppLog.Log($"[WebRtcService] OnEngineEvent: {type} for sessionId: {sessionId}");
                                
                                // Извлекаем source для логирования
                                string? source = null;
                                if (evt.TryGetProperty("data", out acceptedDataEl) && acceptedDataEl.ValueKind == JsonValueKind.Object && acceptedDataEl.TryGetProperty("source", out var sourceEl))
                                {
                                    source = GetAsString(sourceEl);
                                }
                                if (!string.IsNullOrEmpty(source))
                                {
                                    AppLog.Log($"[WebRtcService] OnEngineEvent: call_accepted source: {source}");
                                }
                            }
                            break;
                        case "audio_connected":
                            // КРИТИЧНО: Для исходящих звонков audio_connected означает, что удаленный аудио трек подключен
                            // Это первый признак принятия звонка - отправляем call_accepted напрямую
                            if (string.IsNullOrEmpty(sessionId) && evt.TryGetProperty("data", out var audioDataEl) && audioDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (audioDataEl.TryGetProperty("sessionId", out var audioSessionIdEl))
                                {
                                    sessionId = GetAsString(audioSessionIdEl);
                                    dto.SessionId = sessionId;
                                }
                            }
                            
                            // Если это активная сессия и звонок еще не принят, отправляем call_accepted напрямую
                            if ((sessionId == _activeSessionId || string.IsNullOrEmpty(_activeSessionId)) && 
                                (_state == WebRtcCallState.Calling || _state == WebRtcCallState.Ringing))
                            {
                                AppLog.Log($"[WebRtcService] OnEngineEvent: audio_connected for session {sessionId} during calling state, sending call_accepted directly");
                                
                                // Отправляем call_accepted напрямую как событие (JavaScript уже должен был отправить, но на всякий случай)
                                // Создаем событие call_accepted и отправляем его подписчикам
                                JsonElement? audioConnectedData = null;
                                if (evt.TryGetProperty("data", out var audioConnectedDataEl))
                                {
                                    audioConnectedData = audioConnectedDataEl;
                                }
                                
                                var acceptedDto = new WebRtcEventDto
                                {
                                    Type = "call_accepted",
                                    SessionId = sessionId ?? _activeSessionId,
                                    Data = audioConnectedData,
                                    Slot = _slot
                                };
                                
                                // Отправляем событие подписчикам
                                var acceptedHandlers = Event;
                                if (acceptedHandlers != null)
                                {
                                    foreach (var d in acceptedHandlers.GetInvocationList())
                                    {
                                        if (d is Action<WebRtcEventDto> h)
                                        {
                                            try
                                            {
                                                h(acceptedDto);
                                            }
                                            catch (Exception cbEx)
                                            {
                                                AppLog.Log($"[WebRtcService] ERROR: WebRTC event subscriber threw for call_accepted: {cbEx.Message}");
                                            }
                                        }
                                    }
                                }
                            }
                            break;
                        case "ice_connection_state_change":
                            // Pass through ICE state changes so UI can decide when media is actually ready.
                            if (string.IsNullOrEmpty(sessionId) && evt.TryGetProperty("data", out var iceDataEl) && iceDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (iceDataEl.TryGetProperty("sessionId", out var iceSessionIdEl))
                                {
                                    sessionId = GetAsString(iceSessionIdEl);
                                    dto.SessionId = sessionId;
                                }
                            }
                            break;
                        case "call_failed":
                        case "call_ended":
                            // КРИТИЧНО: Останавливаем все звуки при завершении/неудаче звонка
                            try { RingtoneControl.Stop(); } catch { }
                            try { RingbackToneControl.Stop(); } catch { }

                            // Если sessionId не был извлечен из data, пробуем еще раз
                            if (string.IsNullOrEmpty(sessionId) && evt.TryGetProperty("data", out var endedDataEl) && endedDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (endedDataEl.TryGetProperty("sessionId", out var endedSessionIdEl))
                                {
                                    sessionId = GetAsString(endedSessionIdEl);
                                    dto.SessionId = sessionId;
                                }
                            }
                            
                            // Извлекаем детали для логирования
                            string? causeStr = null;
                            string? originatorStr = null;
                            string? statusCodeStr = null;
                            string? reasonPhraseStr = null;
                            if (evt.TryGetProperty("data", out var failedDataEl) && failedDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (failedDataEl.TryGetProperty("cause", out var causeEl))
                                {
                                    causeStr = GetAsString(causeEl);
                                    dto.Cause = causeStr;
                                }
                                if (failedDataEl.TryGetProperty("originator", out var originatorEl))
                                {
                                    originatorStr = GetAsString(originatorEl);
                                }
                                if (failedDataEl.TryGetProperty("status_code", out var statusCodeEl))
                                {
                                    statusCodeStr = GetAsString(statusCodeEl);
                                }
                                if (failedDataEl.TryGetProperty("reason_phrase", out var reasonPhraseEl))
                                {
                                    reasonPhraseStr = GetAsString(reasonPhraseEl);
                                }
                                if (failedDataEl.TryGetProperty("message", out var msgEl))
                                {
                                    dto.Message = GetAsString(msgEl);
                                }
                            }
                            
                            // Детальное логирование причины завершения звонка
                            var reasonParts = new System.Collections.Generic.List<string>();
                            if (!string.IsNullOrEmpty(causeStr)) reasonParts.Add($"cause={causeStr}");
                            if (!string.IsNullOrEmpty(originatorStr)) reasonParts.Add($"originator={originatorStr}");
                            if (!string.IsNullOrEmpty(statusCodeStr)) reasonParts.Add($"status_code={statusCodeStr}");
                            if (!string.IsNullOrEmpty(reasonPhraseStr)) reasonParts.Add($"reason={reasonPhraseStr}");
                            var reasonDetails = reasonParts.Count > 0 ? " (" + string.Join(", ", reasonParts) + ")" : "";
                            
                            // КРИТИЧНО: Всегда сбрасываем состояние при call_ended или call_failed
                            // Это важно для возможности нового звонка
                            AppLog.Log($"[WebRtcService] Call {type} for session {sessionId ?? "null"}{reasonDetails}, resetting state to Idle");
                            lock (_lock)
                            {
                                // КРИТИЧНО: Принудительно очищаем все состояния при завершении звонка
                                _state = WebRtcCallState.Idle;
                                _activeSessionId = null;
                                _ringingSessionId = null;
                                
                                // Также очищаем дедупликацию входящих звонков для этого sessionId
                                if (!string.IsNullOrEmpty(sessionId))
                                {
                                    _incomingCallDedup.TryRemove(sessionId, out _);
                                }
                            }
                            
                            // После завершения звонка проверяем, нужно ли переподключение
                            // Если была проблема с регистрацией во время звонка, теперь можно переподключиться
                            if (!_registered && _lastRegisteredUtc == DateTime.MinValue)
                            {
                                AppLog.Log("[WebRtcService] Call ended, scheduling reconnect if needed");
                                ScheduleReconnect("post_call_reconnect", immediate: false);
                            }
                            break;
                        case "ping":
                            // Событие ping от JS не должно обрабатываться здесь
                            // Ping отправляется из C# в JS, а pong приходит обратно как событие
                            // Если это событие пришло, просто игнорируем его
                            AppLog.Log("[WebRtcService] OnEngineEvent: Received ping event from JS (unexpected, ignoring)");
                            break;
                        case "call_activity_check":
                            // Результат проверки активности звонка
                            // Это событие используется для асинхронной проверки реальной активности звонка
                            // перед переподключением при reg_failed
                            if (evt.TryGetProperty("data", out var activityDataEl) && activityDataEl.ValueKind == JsonValueKind.Object)
                            {
                                bool? isActive = null;
                                if (activityDataEl.TryGetProperty("active", out var activeEl))
                                {
                                    if (activeEl.ValueKind == JsonValueKind.True || activeEl.ValueKind == JsonValueKind.False)
                                    {
                                        isActive = activeEl.GetBoolean();
                                    }
                                }
                                AppLog.Log($"[WebRtcService] OnEngineEvent: call_activity_check - active={isActive}, sessionId={sessionId}");
                            }
                            // Это событие обрабатывается асинхронно в CheckCallActivityAndScheduleReconnect
                            break;
                        case "mute_applied":
                        case "mute_changed":
                            // Логируем детали mute события
                            if (evt.TryGetProperty("data", out var muteDataEl) && muteDataEl.ValueKind == JsonValueKind.Object)
                            {
                                string? mutedStr = null;
                                string? tracksCountStr = null;
                                string? methodStr = null;
                                if (muteDataEl.TryGetProperty("muted", out var mutedEl))
                                {
                                    mutedStr = GetAsString(mutedEl);
                                }
                                if (muteDataEl.TryGetProperty("tracksCount", out var tracksCountEl))
                                {
                                    tracksCountStr = GetAsString(tracksCountEl);
                                }
                                if (muteDataEl.TryGetProperty("method", out var methodEl))
                                {
                                    methodStr = GetAsString(methodEl);
                                }
                                AppLog.Log($"[WebRtcService] OnEngineEvent: {type} - muted={mutedStr ?? "unknown"}, tracksCount={tracksCountStr ?? "unknown"}, method={methodStr ?? "unknown"}");
                            }
                            else
                            {
                                AppLog.Log($"[WebRtcService] OnEngineEvent: {type} (no data)");
                            }
                            break;
                        case "pong":
                            // Защита 1: Pong должен приходить только от активного engine
                            lock (_lock)
                            {
                                if (_engine != null && _engine.IsInitialized)
                                {
                                    _lastPongTimeUtc = DateTime.UtcNow;
                                    // Don't log successful pongs: watchdog runs frequently and this is very noisy.
                                }
                                else
                                {
                                    // Don't log pongs at all; watchdog will handle missing pongs.
                                }
                            }
                            break;
                        case "error":
                            // Детальное логирование ошибок с name, message, phase, stack
                            string? errorName = null;
                            string? errorMessage = null;
                            string? errorPhase = null;
                            string? errorStack = null;
                            bool isNonFatal = false;
                            
                            // Извлекаем детали ошибки из data (вне lock для минимизации времени блокировки)
                            if (evt.TryGetProperty("data", out var errorDataEl) && errorDataEl.ValueKind == JsonValueKind.Object)
                            {
                                if (errorDataEl.TryGetProperty("name", out var nameEl))
                                {
                                    errorName = GetAsString(nameEl);
                                    dto.Name = errorName;
                                }
                                if (errorDataEl.TryGetProperty("message", out var errorMsgEl))
                                {
                                    errorMessage = GetAsString(errorMsgEl);
                                    dto.Message = errorMessage;
                                }
                                if (errorDataEl.TryGetProperty("phase", out var phaseEl))
                                {
                                    errorPhase = GetAsString(phaseEl);
                                    dto.Phase = errorPhase;
                                }
                                if (errorDataEl.TryGetProperty("stack", out var stackEl))
                                {
                                    errorStack = GetAsString(stackEl);
                                    dto.Stack = errorStack;
                                }
                            }
                            
                            // Логируем все детали ошибки
                            AppLog.Log($"[WebRtcService] OnEngineEvent: JS ERROR - Name={errorName ?? "unknown"}, Message={errorMessage ?? "no message"}, Phase={errorPhase ?? "unknown"}, Stack={errorStack ?? "no stack"}");
                            
                            // Фильтруем нефатальные ошибки (не сбрасываем состояние звонка)
                            // AudioPlayError - ошибка воспроизведения аудио (не критично для звонка)
                            // EnumerateDevicesError - ошибка перечисления устройств (не критично для звонка)
                            // ВАЖНО: PeerConnectionError, ICEFailed, WebSocketDisconnected и подобные - ФАТАЛЬНЫЕ, их фильтровать нельзя!
                            isNonFatal =
                                string.Equals(errorName, "AudioPlayError", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(errorName, "EnumerateDevicesError", StringComparison.OrdinalIgnoreCase);
                            
                            if (isNonFatal)
                            {
                                AppLog.Log($"[WebRtcService] Non-fatal error ignored: {errorName} (phase={errorPhase ?? "unknown"})");
                                break; // Выходим из case, не сбрасывая состояние звонка
                            }
                            
                            // Если ошибка произошла во время звонка, сбрасываем состояние (только для фатальных ошибок)
                            lock (_lock)
                            {
                                if (_state == WebRtcCallState.Calling || _state == WebRtcCallState.Connected)
                                {
                                    AppLog.Log($"[WebRtcService] Fatal error during call, resetting state from {_state} to Idle");
                                    try { RingbackToneControl.Stop(); } catch { }
                                    try { RingtoneControl.Stop(); } catch { }
                                    _state = WebRtcCallState.Idle;
                                    _activeSessionId = null;
                                    _ringingSessionId = null;
                                }
                            }
                            break;
                        case "engine_reset":
                            _registered = false;
                            _activeSessionId = null;
                            _ringingSessionId = null;
                            _state = WebRtcCallState.Idle;
                            break;
                    }

                    // Извлекаем дополнительные данные для DTO (для всех событий)
                    if (evt.TryGetProperty("data", out var dataEl2))
                    {
                        if (dataEl2.ValueKind == JsonValueKind.Object)
                        {
                            if (dataEl2.TryGetProperty("callerNumber", out var callerEl))
                            {
                                dto.CallerNumber = GetAsString(callerEl);
                            }
                            if (dataEl2.TryGetProperty("cause", out var causeEl))
                            {
                                dto.Cause = GetAsString(causeEl);
                            }
                            if (dataEl2.TryGetProperty("message", out var msgEl) && string.IsNullOrEmpty(dto.Message))
                            {
                                dto.Message = GetAsString(msgEl);
                            }
                            if (dataEl2.TryGetProperty("name", out var nameEl2) && string.IsNullOrEmpty(dto.Name))
                            {
                                dto.Name = GetAsString(nameEl2);
                            }
                            if (dataEl2.TryGetProperty("phase", out var phaseEl2) && string.IsNullOrEmpty(dto.Phase))
                            {
                                dto.Phase = GetAsString(phaseEl2);
                            }
                            if (dataEl2.TryGetProperty("stack", out var stackEl2) && string.IsNullOrEmpty(dto.Stack))
                            {
                                dto.Stack = GetAsString(stackEl2);
                            }
                        }
                        dto.Data = dataEl2;
                    }
                }

                if (suppressIncomingSubscriberDispatch)
                    return;

                // Event handlers are user-code; one buggy subscriber must not prevent others from receiving critical events
                // (e.g., CallWindow must always receive call_failed/call_ended to stop ringback and close).
                var handlers = Event;
                if (handlers != null)
                {
                    // DIAGNOSTICS: log only for a few important event types to avoid spam.
                    if (dto.Type == "registered" || dto.Type == "ua_registered" || dto.Type == "reg_failed" || dto.Type == "ua_registration_failed" ||
                        dto.Type == "ua_connected" || dto.Type == "ws_connected" || dto.Type == "call_failed" || dto.Type == "call_ended")
                    {
                        AppLog.Log($"[WebRtcService] OnEngineEvent: Dispatching {handlers.GetInvocationList().Length} subscriber(s) for type='{dto.Type}', registered={_registered}, IsReadyForCalls={IsReadyForCalls}");
                    }

                    foreach (var d in handlers.GetInvocationList())
                    {
                        if (d is Action<WebRtcEventDto> h)
                        {
                            try
                            {
                                h(dto);
                            }
                            catch (Exception cbEx)
                            {
                                AppLog.Log($"[WebRtcService] ERROR: WebRTC event subscriber threw for type='{dto.Type}': {cbEx.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcService] ERROR processing engine event: {ex.Message}");
                AppLog.Log($"[WebRtcService] ERROR stack trace: {ex.StackTrace}");
            }
        }

        /// <returns>true if incoming was accepted and UI/subscribers should see this event; false if suppressed.</returns>
        private bool HandleIncomingCall(JsonElement evt, string? sessionId, WebRtcEventDto dto)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AppLog.Log("[WebRtcService] WARNING: Incoming call without sessionId, ignoring");
                return false;
            }

            // Дедуп
            if (_incomingCallDedup.TryGetValue(sessionId, out var lastTime))
            {
                var timeSinceLastCall = DateTime.Now - lastTime;
                if (timeSinceLastCall.TotalMilliseconds < 1000)
                {
                    AppLog.Log($"[WebRtcService] Incoming call {sessionId} ignored (dedup)");
                    return false;
                }
            }

            // КРИТИЧНО: Если предыдущий звонок завершился некорректно, очищаем состояние перед новым входящим звонком
            lock (_lock)
            {
                if (_state == WebRtcCallState.Ending || _state == WebRtcCallState.Ended)
                {
                    AppLog.Log($"[WebRtcService] HandleIncomingCall: Resetting state from {_state} to Idle before processing incoming call");
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                    _ringingSessionId = null;
                }
            }

            // КРИТИЧНО: Проверяем активный звонок и принудительно очищаем состояние Ringing/Connected если оно "зависло"
            bool duplicatePhaseIncoming = false;
            lock (_lock)
            {
                // Если состояние Ringing или Connected, но нет активной сессии - это "зависшее" состояние
                // Очищаем его перед обработкой нового входящего звонка
                if ((_state == WebRtcCallState.Ringing || _state == WebRtcCallState.Connected || _state == WebRtcCallState.Calling)
                    && string.IsNullOrEmpty(_activeSessionId) && string.IsNullOrEmpty(_ringingSessionId))
                {
                    AppLog.Log($"[WebRtcService] HandleIncomingCall: Clearing stuck state {_state} (no active session)");
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                    _ringingSessionId = null;
                }

                if (_state != WebRtcCallState.Idle && _state != WebRtcCallState.Ended)
                {
                    // phone.js вызывает wireSessionEvents → шлёт new_session, затем отдельно шлёт incoming.
                    // new_session уже перевёл нас в Ringing и заполнил session ids. Старый guard здесь отбрасывал
                    // второе событие → подписчики не получали incoming → PBX Originate (автоответ) переставал работать.
                    if (_state == WebRtcCallState.Ringing
                        && !string.IsNullOrEmpty(_activeSessionId)
                        && !string.IsNullOrEmpty(_ringingSessionId)
                        && sessionId == _activeSessionId
                        && sessionId == _ringingSessionId)
                    {
                        duplicatePhaseIncoming = true;
                    }
                    else
                    {
                        AppLog.Log($"[WebRtcService] Incoming call {sessionId} ignored (active call in state {_state})");
                        return false;
                    }
                }
            }

            _incomingCallDedup[sessionId] = DateTime.Now;

            // Очищаем старые записи
            var keysToRemove = _incomingCallDedup.Where(kvp => (DateTime.Now - kvp.Value).TotalSeconds > 10).Select(kvp => kvp.Key).ToList();
            foreach (var key in keysToRemove)
            {
                _incomingCallDedup.TryRemove(key, out _);
            }

            if (!duplicatePhaseIncoming)
            {
                _ringingSessionId = sessionId;
                _activeSessionId = sessionId;
                _state = WebRtcCallState.Ringing;
            }

            // Извлекаем callerNumber
            if (evt.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
            {
                if (dataEl.TryGetProperty("callerNumber", out var callerEl))
                {
                    dto.CallerNumber = GetAsString(callerEl);
                }
            }

            return true;
        }

        public async Task MakeCallAsync(string number)
        {
            // Extra guard: MainWindow should already block calls when IsReadyForCalls=false, but keep this here
            // so calls from other places can't create a "ghost" outgoing call that never reaches PBX.
            // Also log enough state to diagnose "PBX sees no attempt".
            lock (_lock)
            {
                // КРИТИЧНО: Если состояние Ending, Ended, Calling или Ringing - принудительно очищаем его
                // Это важно для случаев, когда предыдущий звонок не завершился корректно
                if (_state == WebRtcCallState.Ending || _state == WebRtcCallState.Ended || 
                    _state == WebRtcCallState.Calling || _state == WebRtcCallState.Ringing)
                {
                    AppLog.Log($"[WebRtcService] MakeCallAsync: Resetting state from {_state} to Idle (previous call may not have completed properly)");
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                    _ringingSessionId = null;
                    
                    // КРИТИЧНО: Принудительно очищаем сессии в JavaScript перед новым звонком
                    // Это предотвращает блокировку нового звонка из-за "зависших" сессий
                    if (_engine != null && _engine.IsInitialized)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _engine.SendAsync(new { cmd = "cleanupSessions", slot = _slot });
                                AppLog.Log("[WebRtcService] MakeCallAsync: cleanupSessions command sent");
                            }
                            catch (Exception cleanupEx)
                            {
                                AppLog.Log($"[WebRtcService] MakeCallAsync: Error sending cleanupSessions: {cleanupEx.Message}");
                            }
                        });
                    }
                }
                
                if (_state != WebRtcCallState.Idle)
                {
                    AppLog.Log($"[WebRtcService] MakeCallAsync: Cannot make call in state {_state}");
                    throw new InvalidOperationException($"Cannot make call in state {_state}");
                }

                if (!IsReadyForCalls)
                {
                    var engineNull = _engine == null;
                    var initialized = _engine?.IsInitialized ?? false;
                    AppLog.Log($"[WebRtcService] MakeCallAsync: WebRTC not ready (engine={!engineNull}, initialized={initialized}, registered={_registered})");
                    AppLog.Log($"[WebRtcService] MakeCallAsync: Details - engine type: {_engine?.GetType().Name ?? "null"}, IsInitialized property: {initialized}");
                    throw new InvalidOperationException("WebRTC not ready");
                }

                _state = WebRtcCallState.Calling;
            }

            AppLog.Log($"[WebRtcService] MakeCallAsync: Sending makeCall command for number {number}");
            if (_engine != null)
            {
                try
                {
                    await _engine.SendAsync(new { cmd = "makeCall", number, slot = _slot });
                    AppLog.Log($"[WebRtcService] MakeCallAsync: Command sent successfully");
                }
                catch (Exception ex)
                {
                    // If we fail to execute the JS call, PBX will not see any INVITE attempt.
                    lock (_lock)
                    {
                        _state = WebRtcCallState.Idle;
                        _activeSessionId = null;
                    }
                    AppLog.Log($"[WebRtcService] MakeCallAsync: ERROR sending makeCall command: {ex.Message}");
                    throw;
                }
            }
            else
            {
                AppLog.Log($"[WebRtcService] MakeCallAsync: ERROR - engine is null");
                lock (_lock)
                {
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                }
                throw new InvalidOperationException("WebRTC engine is null");
            }
        }

        public async Task AnswerAsync(string? sessionId = null)
        {
            lock (_lock)
            {
                // Разрешаем ответить в состоянии Ringing или Calling (для входящих звонков)
                // Calling может быть установлен событием new_session для входящих звонков
                if (_state != WebRtcCallState.Ringing && _state != WebRtcCallState.Calling)
                {
                    AppLog.Log($"[WebRtcService] Answer ignored (current state: {_state})");
                    return;
                }

                // Для входящих звонков проверяем, что есть активная сессия
                if (_state == WebRtcCallState.Calling && string.IsNullOrEmpty(_activeSessionId) && string.IsNullOrEmpty(_ringingSessionId))
                {
                    AppLog.Log($"[WebRtcService] Answer ignored (Calling state but no active/ringing session)");
                    return;
                }

                if (!IsReadyForCalls)
                {
                    throw new InvalidOperationException("WebRTC not ready");
                }

                _state = WebRtcCallState.Connected;
            }

            if (_engine != null)
            {
                await _engine.SendAsync(new { cmd = "answer", slot = _slot });
                AppLog.Log($"[WebRtcService] AnswerAsync: Answer command sent");
            }
        }

        public async Task SetMuteAsync(bool mute)
        {
            if (_engine == null || !_engine.IsInitialized)
            {
                AppLog.Log("[WebRtcService] SetMuteAsync: Engine not initialized");
                return;
            }

            try
            {
                // ВАЖНО: отправляем команду как Dictionary, чтобы WebRtcEngineHost правильно распарсил поле mute
                var cmd = new Dictionary<string, object> 
                { 
                    { "cmd", "setMute" },
                    { "mute", mute },
                    { "slot", _slot }
                };
                
                // Логируем перед отправкой - ожидаем увидеть "Executing script" в WebRtcEngineHost
                AppLog.Log($"[WebRtcService] SetMuteAsync: Sending mute command (mute={mute}), expecting 'Executing script: window.SoftphoneWebRtc.setMute(...)' log");
                
                await _engine.SendAsync(cmd);
                AppLog.Log($"[WebRtcService] SetMuteAsync: Mute command sent (mute={mute})");
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcService] SetMuteAsync: ERROR - {ex.Message}");
            }
        }

        public async Task SendDTMFAsync(char digit)
        {
            if (_engine == null || !_engine.IsInitialized)
            {
                AppLog.Log("[WebRtcService] SendDTMFAsync: Engine not initialized");
                return;
            }

            try
            {
                var cmd = new Dictionary<string, object> 
                { 
                    { "cmd", "sendDtmf" },
                    { "digit", digit.ToString() },
                    { "slot", _slot }
                };
                AppLog.Log($"[WebRtcService] SendDTMFAsync: Sending DTMF command (digit='{digit}')");

                await _engine.SendAsync(cmd);
                AppLog.Log($"[WebRtcService] SendDTMFAsync: DTMF command sent (digit='{digit}')");
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcService] SendDTMFAsync: ERROR - {ex.Message}");
            }
        }

        public async Task<List<WebRtcAudioDevice>> EnumerateAudioDevicesAsync()
        {
            var devices = new List<WebRtcAudioDevice>();
            
            if (_engine == null || !_engine.IsInitialized)
            {
                AppLog.Log("[WebRtcService] EnumerateAudioDevicesAsync: Engine not initialized");
                return devices;
            }

            try
            {
                // Подписываемся на событие audio_devices_list
                var tcs = new TaskCompletionSource<List<WebRtcAudioDevice>>();
                Action<WebRtcEventDto>? handler = null;
                
                handler = (dto) =>
                {
                    if (dto.Type == "audio_devices_list")
                    {
                        if (handler != null) Event -= handler;
                        try
                        {
                            if (dto.Data.HasValue && dto.Data.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                            {
                                var data = dto.Data.Value;
                                
                                if (data.TryGetProperty("inputs", out var inputsEl) && inputsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var input in inputsEl.EnumerateArray())
                                    {
                                        if (input.TryGetProperty("deviceId", out var deviceIdEl) && input.TryGetProperty("label", out var labelEl))
                                        {
                                            devices.Add(new WebRtcAudioDevice
                                            {
                                                DeviceId = GetAsString(deviceIdEl) ?? "",
                                                Label = GetAsString(labelEl) ?? "Unknown",
                                                Kind = "audioinput"
                                            });
                                        }
                                    }
                                }
                                
                                if (data.TryGetProperty("outputs", out var outputsEl) && outputsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var output in outputsEl.EnumerateArray())
                                    {
                                        if (output.TryGetProperty("deviceId", out var deviceIdEl) && output.TryGetProperty("label", out var labelEl))
                                        {
                                            devices.Add(new WebRtcAudioDevice
                                            {
                                                DeviceId = GetAsString(deviceIdEl) ?? "",
                                                Label = GetAsString(labelEl) ?? "Unknown",
                                                Kind = "audiooutput"
                                            });
                                        }
                                    }
                                }
                            }
                            
                            tcs.SetResult(devices);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Log($"[WebRtcService] EnumerateAudioDevicesAsync: Error parsing devices: {ex.Message}");
                            tcs.SetResult(devices);
                        }
                    }
                    else if (dto.Type == "error")
                    {
                        if (handler != null) Event -= handler;
                        AppLog.Log($"[WebRtcService] EnumerateAudioDevicesAsync: Error from JS: {dto.Message ?? "unknown"}");
                        tcs.SetResult(devices);
                    }
                };
                
                Event += handler;
                
                // Отправляем команду
                await _engine.SendAsync(new { cmd = "enumerateAudioDevices", slot = _slot });
                
                // Ждем ответа с таймаутом 5 секунд
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    if (handler != null) Event -= handler;
                    AppLog.Log("[WebRtcService] EnumerateAudioDevicesAsync: Timeout waiting for devices list");
                    return devices;
                }
                
                return await tcs.Task;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcService] EnumerateAudioDevicesAsync: ERROR - {ex.Message}");
                return devices;
            }
        }

        public async Task SwitchAudioDeviceAsync(string? inputDeviceId, string? outputDeviceId)
        {
            if (_engine == null || !_engine.IsInitialized)
            {
                AppLog.Log("[WebRtcService] SwitchAudioDeviceAsync: Engine not initialized");
                return;
            }

            try
            {
                var cmd = new Dictionary<string, object>
                {
                    { "cmd", "switchAudioDevice" },
                    { "slot", _slot }
                };
                if (!string.IsNullOrEmpty(inputDeviceId))
                {
                    cmd["inputDeviceId"] = inputDeviceId;
                }
                if (!string.IsNullOrEmpty(outputDeviceId))
                {
                    cmd["outputDeviceId"] = outputDeviceId;
                }
                
                await _engine.SendAsync(cmd);
                AppLog.Log($"[WebRtcService] SwitchAudioDeviceAsync: Switch command sent (input={inputDeviceId ?? "null"}, output={outputDeviceId ?? "null"})");
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcService] SwitchAudioDeviceAsync: ERROR - {ex.Message}");
            }
        }

        public async Task HangupAsync(string? sessionId = null)
        {
            // Stop app-side ringtone immediately when we initiate hangup.
            try { RingtoneControl.Stop(); } catch { }
            try { RingbackToneControl.Stop(); } catch { }

            lock (_lock)
            {
                if (_state == WebRtcCallState.Ending || _state == WebRtcCallState.Ended)
                {
                    AppLog.Log($"[WebRtcService] HangupAsync: Already hanging up or ended (state: {_state}), resetting to Idle");
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                    return;
                }

                AppLog.Log($"[WebRtcService] HangupAsync: Setting state to Ending (current: {_state})");
                _state = WebRtcCallState.Ending;
            }

            if (_engine != null)
            {
                await _engine.SendAsync(new { cmd = "hangup", sessionId, slot = _slot });
                AppLog.Log($"[WebRtcService] HangupAsync: Hangup command sent for session {sessionId ?? "null"}");
                
                // Сбрасываем состояние через небольшую задержку, если событие call_ended не придет
                _ = System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
                {
                    lock (_lock)
                    {
                        if (_state == WebRtcCallState.Ending)
                        {
                            AppLog.Log("[WebRtcService] HangupAsync: Timeout - call_ended event not received, resetting state to Idle");
                            try { RingbackToneControl.Stop(); } catch { }
                            try { RingtoneControl.Stop(); } catch { }
                            _state = WebRtcCallState.Idle;
                            _activeSessionId = null;
                            _ringingSessionId = null;
                        }
                    }
                });
            }
            else
            {
                AppLog.Log("[WebRtcService] HangupAsync: Engine is null, resetting state to Idle");
                try { RingbackToneControl.Stop(); } catch { }
                try { RingtoneControl.Stop(); } catch { }
                lock (_lock)
                {
                    _state = WebRtcCallState.Idle;
                    _activeSessionId = null;
                    _ringingSessionId = null;
                }
            }
        }

        public Task NotifyOriginatePendingAsync(bool pending)
        {
            if (_engine == null || !_engine.IsInitialized)
                return Task.CompletedTask;

            var flag = pending ? "true" : "false";
            return _engine.SendAsync(new Dictionary<string, object>
            {
                { "cmd", "executeScript" },
                { "script", $"try{{window.SoftphoneWebRtc.setOriginatePending({flag});}}catch(e){{}}" }
            });
        }

        public Task NotifyOriginateAcceptedAsync(string sessionId)
        {
            if (_engine == null || !_engine.IsInitialized)
                return Task.CompletedTask;

            var escaped = System.Text.Json.JsonSerializer.Serialize(sessionId);
            return _engine.SendAsync(new Dictionary<string, object>
            {
                { "cmd", "executeScript" },
                { "script", $"try{{window.SoftphoneWebRtc.setOriginateAcceptedSessionId({escaped});}}catch(e){{}}" }
            });
        }

        public async Task SetHoldAsync(bool hold, string? sessionId = null)
        {
            if (_engine == null || !_engine.IsInitialized)
            {
                throw new InvalidOperationException("WebRTC engine not initialized");
            }

            // Prefer explicit session id; otherwise fallback to the service-tracked session.
            sessionId ??= _activeSessionId ?? _ringingSessionId;

            var cmd = new Dictionary<string, object>
            {
                { "cmd", "setHold" },
                { "hold", hold },
                { "slot", _slot }
            };
            if (!string.IsNullOrEmpty(sessionId))
            {
                cmd["sessionId"] = sessionId!;
            }

            await _engine.SendAsync(cmd);
            AppLog.Log($"[WebRtcService] SetHoldAsync: {(hold ? "hold" : "resume")} command sent (sessionId={sessionId ?? "null"})");
        }

        public async Task ResetEngineAsync()
        {
            // Защита 2: Не делай resetEngine() если уже в процессе Resetting
            lock (_lock)
            {
                if (_isResetting)
                {
                    AppLog.Log("[WebRtcService] ResetEngineAsync: Already resetting, skipping");
                    return;
                }
                
                // Проверяем cooldown после последнего reset
                if ((DateTime.UtcNow - _lastResetUtc).TotalSeconds < 20)
                {
                    AppLog.Log($"[WebRtcService] ResetEngineAsync: Cooldown active (last reset {(DateTime.UtcNow - _lastResetUtc).TotalSeconds:F1}s ago), skipping");
                    return;
                }
                
                _isResetting = true;
                _lastResetUtc = DateTime.UtcNow;
                _state = WebRtcCallState.Idle;
                _activeSessionId = null;
                _ringingSessionId = null;
            }

            try
            {
                if (_engine != null)
                {
                    await _engine.SendAsync(new { cmd = "resetEngine", slot = _slot });
                }
            }
            finally
            {
                lock (_lock)
                {
                    _isResetting = false;
                }
            }
        }

        /// <summary>
        /// Переинициализирует UA с новыми учетными данными (для изменения настроек подключения)
        /// </summary>
        public async Task ReinitializeUAAsync(string wsUri, string sipUri, string username, string password)
        {
            // After sleep/resume IsInitialized can transiently return false; try anyway.
            if (_engine == null)
            {
                AppLog.Log("[WebRtcService] ReinitializeUAAsync: Engine not initialized, skipping");
                return;
            }

            // Guard against duplicate initUA loops (e.g. Save&Connect triggers initUA while reconnect timer is firing).
            if (System.Threading.Interlocked.Exchange(ref _reinitInFlight, 1) == 1)
            {
                AppLog.Log("[WebRtcService] ReinitializeUAAsync: initUA already in-flight, skipping");
                return;
            }

            try
            {
                // Store last config for auto-reconnect.
                lock (_lock)
                {
                    _lastUaConfig = new UaConfigSnapshot
                    {
                        WsUri = wsUri ?? "",
                        SipUri = sipUri ?? "",
                        Username = username ?? "",
                        Password = password ?? ""
                    };

                    // Grace window: JsSIP often emits ua_unregistered/ws_disconnected during intentional re-init.
                    // During this window we must not schedule auto-reconnect attempts, otherwise we create loops.
                    _lastInitUaSentUtc = DateTime.UtcNow;
                    _suppressAutoReconnectUntilUtc = _lastInitUaSentUtc + TimeSpan.FromSeconds(4);
                    _wsConnectedAfterInitUa = false; // Reset flag - we expect ws_connected after initUA

                    // Cancel any scheduled reconnect timers from previous failures.
                    CancelScheduledReconnectLocked("manual_reinit");
                    _reconnectAttempt = 0;
                }

                // Capture reconnect attempt BEFORE sending initUA to detect if this is initial connection
                int reconnectAttemptBeforeInit;
                bool shouldScheduleInitialSafetyNet = false;
                lock (_lock)
                {
                    reconnectAttemptBeforeInit = _reconnectAttempt;
                    // Only schedule safety-net for initial connection AND if not already scheduled
                    if (reconnectAttemptBeforeInit == 0 && !_initialConnectionSafetyNetScheduled)
                    {
                        _initialConnectionSafetyNetScheduled = true;
                        shouldScheduleInitialSafetyNet = true;
                    }
                }

                AppLog.Log($"[WebRtcService] ReinitializeUAAsync: Reinitializing UA with new credentials (user={username}, sipUri={sipUri}, passLen={(string.IsNullOrEmpty(password) ? 0 : password.Length)}, debug={DebugEnabled})");

                // Загружаем TURN‑параметры из настроек (если заданы).
                // TURN теперь хранится per-connection (MainWebRtcTurn*); legacy WebRtcTurn* оставлен для миграции.
                string? turnServer = null;
                string? turnUsername = null;
                string? turnPassword = null;
                try
                {
                    var settingsPath = AppDataHelper.GetSettingsFilePath();
                    if (System.IO.File.Exists(settingsPath))
                    {
                        var json = System.IO.File.ReadAllText(settingsPath);
                        var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(json);
                        if (settings != null)
                        {
                            turnServer = settings.MainWebRtcTurnUri ?? settings.WebRtcTurnUri;
                            turnUsername = settings.MainWebRtcTurnUsername ?? settings.WebRtcTurnUsername;
                            turnPassword = TurnPasswordProvider.GetMainTurnPassword(settings);
                        }
                    }
                }
                catch
                {
                    // best‑effort: отсутствие TURN‑настроек не критично
                }

                AppLog.Log($"[WebRtcService] ReinitializeUAAsync: TURN to initUA - server={(string.IsNullOrWhiteSpace(turnServer) ? "<none>" : turnServer)}, userSet={!string.IsNullOrWhiteSpace(turnUsername)}, passSet={!string.IsNullOrWhiteSpace(turnPassword)}");

                await _engine.SendAsync(new
                {
                    cmd = "initUA",
                    wsUri = wsUri,
                    sipUri = sipUri,
                    user = username,
                    pass = password,
                    enableDebug = DebugEnabled,
                    turnServer,
                    turnUsername,
                    turnPassword,
                    slot = _slot
                });
                AppLog.Log("[WebRtcService] ReinitializeUAAsync: initUA command sent");

                // Check if ws_connected arrives after grace window (5 seconds = grace window 4s + 1s buffer)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        lock (_lock)
                        {
                            // If ws_connected didn't arrive after grace window, schedule reconnect
                            if (!_wsConnectedAfterInitUa && !_registered && _engine != null && !_isResetting && _state != WebRtcCallState.Connected)
                            {
                                if (!_autoReconnectPaused)
                                {
                                    AppLog.Log("[WebRtcService] ReinitializeUAAsync: ws_connected did not arrive after grace window, scheduling reconnect");
                                    ScheduleReconnect("no_ws_connected_after_initua", immediate: false);
                                }
                            }
                        }
                    }
                    catch { }
                });

                // Safety-net: Only for INITIAL connection (not auto-reconnect), and only once.
                // Auto-reconnect already has its own safety-net in TryReconnectAsync.
                if (shouldScheduleInitialSafetyNet)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(6));
                            lock (_lock)
                            {
                                // Only trigger if still not registered AND reconnect attempt is still 0 (no auto-reconnect happened)
                                if (!_registered && _engine != null && !_isResetting && _state != WebRtcCallState.Connected && _reconnectAttempt == 0)
                                {
                                    if (!_autoReconnectPaused)
                                    {
                                        AppLog.Log("[WebRtcService] ReinitializeUAAsync: Initial connection safety-net triggered - no registration after 6s, scheduling reconnect");
                                        ScheduleReconnect("initial_connection_safety_net", immediate: false);
                                    }
                                }
                            }
                        }
                        catch { }
                    });
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcService] ERROR in ReinitializeUAAsync: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _reinitInFlight, 0);
            }
        }

        public void StartWatchdog()
        {
            lock (_lock)
            {
                if (_watchdogTimer != null) return; // idempotent

                // Avoid false "no pong" resets after sleep/resume: treat watchdog start as a fresh baseline.
                _lastPongTimeUtc = DateTime.UtcNow;
                _lastEngineEventUtc = DateTime.UtcNow;

                _watchdogTimer = new System.Threading.Timer(_ =>
                {
                    // Timer callbacks must never be async-void; run async work on a Task and guard reentrancy.
                    if (System.Threading.Interlocked.Exchange(ref _watchdogInFlight, 1) == 1) return;

                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            IWebRtcEngineHost? engine;
                            WebRtcCallState state;
                            DateTime lastPong;
                            DateTime lastAnyEvent;
                            bool resetting;
                            DateTime lastReset;

                            lock (_lock)
                            {
                                engine = _engine;
                                state = _state;
                                lastPong = _lastPongTimeUtc;
                                lastAnyEvent = _lastEngineEventUtc;
                                resetting = _isResetting;
                                lastReset = _lastResetUtc;
                            }

                            if (engine == null || !engine.IsInitialized) return;

                            // Ping (SendAsync has its own timeout).
                            await engine.SendAsync(new { cmd = "ping", slot = _slot });

                            // Cooldown: don't spam reset if we're already resetting.
                            if (resetting || (DateTime.UtcNow - lastReset).TotalSeconds < 20) return;

                            // Consider both pong and any JS event as liveness: after resume ping may fail briefly
                            // while WS/registration events still flow, and we must not reset the engine in that case.
                            var lastAlive = lastPong > lastAnyEvent ? lastPong : lastAnyEvent;
                            if (lastAlive != DateTime.MinValue && (DateTime.UtcNow - lastAlive).TotalSeconds > 30)
                            {
                                AppLog.Log($"[WebRtcService] WARNING: No WebRTC liveness received for {(DateTime.UtcNow - lastAlive).TotalSeconds:F1} seconds, resetting engine...");
                                // Force registered=false so auto-reconnect can proceed deterministically after reset.
                                lock (_lock)
                                {
                                    _registered = false;
                                    _lastRegisteredUtc = DateTime.MinValue;
                                }
                                _ = ResetEngineAsync();
                                TriggerReconnectNow("watchdog_no_pong");
                            }

                            // Request stats only during active calls.
                            if (state == WebRtcCallState.Connected)
                            {
                                _ = engine.SendAsync(new { cmd = "getStats", slot = _slot });
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Log($"[WebRtcService] ERROR in watchdog: {ex.Message}");
                        }
                        finally
                        {
                            System.Threading.Interlocked.Exchange(ref _watchdogInFlight, 0);
                        }
                    });
                }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
            }
        }
        
        /// <summary>
        /// Останавливает watchdog timer и очищает ресурсы
        /// </summary>
        public void StopWatchdog()
        {
            lock (_lock)
            {
                if (_watchdogTimer != null)
                {
                    _watchdogTimer.Dispose();
                    _watchdogTimer = null;
                    System.Threading.Interlocked.Exchange(ref _watchdogInFlight, 0);
                    AppLog.Log("[WebRtcService] Watchdog timer stopped");
                }
            }
        }

        /// <summary>
        /// Stops auto-reconnect attempts and clears any scheduled reconnect.
        /// Useful when switching to SIP mode or shutting down.
        /// </summary>
        public void StopAutoReconnect()
        {
            System.Threading.Timer? toDispose = null;
            lock (_lock)
            {
                toDispose = _reconnectTimer;
                _reconnectTimer = null;
                _nextReconnectUtc = DateTime.MinValue;
                _reconnectAttempt = 0;
                System.Threading.Interlocked.Exchange(ref _reconnectInFlight, 0);
            }
            try { toDispose?.Dispose(); } catch { }
        }
    }
}


