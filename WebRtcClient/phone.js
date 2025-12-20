// WebRTC Softphone модуль для JsSIP
window.SoftphoneWebRtc = (function () {
    'use strict';

    let ua = null;
    let session = null;
    let mediaRecorder = null;
    let recordingStream = null;

    // Настройки медиа для WebRTC
    const mediaConstraints = {
        audio: {
            echoCancellation: true,
            noiseSuppression: true,
            autoGainControl: true
        }
    };

    // Безопасная конвертация значения в строку для отправки в C#
    function safeString(v) {
        if (v == null || v === undefined) return null;
        if (typeof v === "string") return v;
        if (typeof v === "number" || typeof v === "boolean") return String(v);
        // JsSIP.URI / Error / etc. - используем toString если доступен
        if (typeof v.toString === "function") {
            try {
                return v.toString();
            } catch {
                // Если toString падает, пробуем JSON
            }
        }
        // Для объектов пробуем JSON.stringify
        try {
            return JSON.stringify(v);
        } catch {
            return "[object]";
        }
    }

    // Отправка события в C#
    function sendEvent(obj) {
        try {
            // Безопасная рекурсивная сериализация с сохранением массивов и объектов
            function toSafeJson(value) {
                if (value === null || value === undefined) return null;
                if (Array.isArray(value)) {
                    // Массивы сериализуем рекурсивно, сохраняя структуру
                    return value.map(toSafeJson);
                }
                if (typeof value === 'object') {
                    // Объекты сериализуем рекурсивно
                    const safeObj = {};
                    for (const [k, v] of Object.entries(value)) {
                        safeObj[k] = toSafeJson(v);
                    }
                    return safeObj;
                }
                // Примитивы (string/number/bool) возвращаем как есть
                return value;
            }
            
            const safeObj = toSafeJson(obj);
            
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify(safeObj));
            } else {
                console.log('WebRTC Event:', safeObj);
            }
        } catch (e) {
            console.error('Error sending event:', e);
        }
    }

    // Глобальные переменные для сессий
    window._incomingSession = null;
    window._activeSession = null;

    // Фаза 2: Инициализация JsSIP UA без медиа (prewarm)
    async function initUA(cfg) {
        try {
            console.log('[WebRTC] Initializing JsSIP UA (without media)...');
            console.log('[WebRTC] Config:', { wsUri: cfg.wsUri, sipUri: cfg.sipUri, user: cfg.user, pass: cfg.pass ? '***' : 'NOT SET' });
            console.log('[WebRTC] Config details:', {
                wsUri: cfg.wsUri,
                sipUri: cfg.sipUri,
                user: cfg.user,
                passLength: cfg.pass ? cfg.pass.length : 0,
                passSet: !!cfg.pass
            });
            
            // Если UA уже существует, останавливаем его перед созданием нового
            if (ua) {
                console.log('[WebRTC] Stopping existing UA before reinitialization...');
                try {
                    ua.stop();
                    ua = null;
                } catch (e) {
                    console.warn('[WebRTC] Error stopping existing UA:', e);
                }
            }
            
            if (!cfg || !cfg.wsUri || !cfg.sipUri || !cfg.pass) {
                const errorMsg = 'Invalid configuration: missing wsUri, sipUri, or pass';
                console.error('[WebRTC]', errorMsg);
                console.error('[WebRTC] Config check:', {
                    cfg: !!cfg,
                    wsUri: !!cfg?.wsUri,
                    sipUri: !!cfg?.sipUri,
                    pass: !!cfg?.pass
                });
                sendEvent({ type: 'error', data: { message: errorMsg, phase: 'initUA', config: { wsUri: !!cfg?.wsUri, sipUri: !!cfg?.sipUri, pass: !!cfg?.pass } } });
                return;
            }

            console.log('[WebRTC] Creating WebSocket interface to:', cfg.wsUri);
            const socket = new JsSIP.WebSocketInterface(cfg.wsUri);
            
            console.log('[WebRTC] Creating JsSIP UA with SIP URI:', cfg.sipUri);
            ua = new JsSIP.UA({
                sockets: [socket],
                uri: cfg.sipUri,
                password: cfg.pass,
                session_timers: false,
                register: true,
                register_expires: 300,
                connection_recovery_min_interval: 2,
                connection_recovery_max_interval: 30
            });
            console.log('[WebRTC] JsSIP UA created successfully');

            // Обработчики событий UA
            ua.on('connected', () => {
                console.log('[WebRTC] ✓ WebSocket connected to ATS server');
                sendEvent({ type: 'ws_connected' });
            });

            ua.on('disconnected', (e) => {
                console.log('[WebRTC] ✗ WebSocket disconnected from ATS server', e);
                sendEvent({
                    type: 'ws_disconnected',
                    data: {
                        code: e?.code,
                        reason: e?.reason,
                        message: e?.message,
                        cause: e?.cause
                    }
                });
            });

            ua.on('registered', () => {
                console.log('[WebRTC] ✓ Successfully registered on ATS server as', cfg.sipUri);
                sendEvent({ type: 'registered' });
            });

            ua.on('unregistered', () => {
                console.log('[WebRTC] Unregistered from ATS server');
                sendEvent({ type: 'ua_unregistered' });
            });

            ua.on('registrationFailed', (e) => {
                console.error('[WebRTC] ✗ Registration failed:', e.cause, e.message);
                sendEvent({ 
                    type: 'reg_failed', 
                    data: e.cause + ': ' + (e.message || 'Registration failed')
                });
            });

            // Входящие звонки - сохраняем сессию, но НЕ вызываем answer (без медиа)
            ua.on('newRTCSession', (e) => {
                console.log('[WebRTC] New RTC session (incoming):', e.originator);
                if (e.originator === 'remote') {
                    const sessionId = e.session.id || e.session.request?.call_id || `session_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
                    const callerNumber = e.session.remote_identity ? e.session.remote_identity.uri.user : 'Unknown';
                    
                    window._incomingSession = e.session;
                    e.session._softphoneSessionId = sessionId;
                    wireSessionEvents(e.session, e.originator);
                    
                    sendEvent({ 
                        type: 'incoming',
                        data: {
                            sessionId: sessionId,
                            callerNumber: callerNumber
                        }
                    });
                }
            });

            console.log('[WebRTC] Starting JsSIP UA...');
            ua.start();
            console.log('[WebRTC] JsSIP UA started (without media)');
            sendEvent({ type: 'ua_started' });
        } catch (error) {
            console.error('[WebRTC] ✗ ERROR during UA initialization:', error.message, error.stack);
            sendEvent({ 
                type: 'error', 
                data: { 
                    message: error.message,
                    stack: error.stack 
                } 
            });
        }
    }

    // Старая функция init (для обратной совместимости с SettingsWindow)
    function init(config) {
        try {
            console.log('[WebRTC] Initializing JsSIP UA...');
            console.log('[WebRTC] Config:', { wsUri: config.wsUri, sipUri: config.sipUri, password: config.password ? '***' : 'NOT SET' });
            
            if (!config || !config.wsUri || !config.sipUri || !config.password) {
                const errorMsg = 'Invalid configuration: missing wsUri, sipUri, or password';
                console.error('[WebRTC]', errorMsg);
                sendEvent({ type: 'error', data: errorMsg });
                return;
            }

            // Тест WebSocket до JsSIP (диагностика доступности WSS)
            try {
                console.log('[WebRTC] Testing WebSocket connection to:', config.wsUri);
                const t = new WebSocket(config.wsUri);
                t.onopen = () => { 
                    console.log('[WebRTC] ✓ WebSocket test: connection opened');
                    sendEvent({ type: 'ws_test_open' }); 
                    t.close(); 
                };
                t.onerror = (err) => {
                    console.error('[WebRTC] ✗ WebSocket test error:', err);
                    // Извлекаем полезную информацию из события ошибки
                    let errorMsg = 'WebSocket connection failed';
                    if (err && err.target) {
                        const ws = err.target;
                        if (ws.readyState === WebSocket.CLOSED) {
                            errorMsg = `WebSocket closed (code: ${ws.extensions || 'unknown'})`;
                        } else if (ws.url) {
                            errorMsg = `Failed to connect to ${ws.url}`;
                        }
                    }
                    sendEvent({ type: 'ws_test_error', data: errorMsg });
                };
                t.onclose = (ev) => {
                    console.log('[WebRTC] WebSocket test closed:', ev.code, ev.reason);
                    sendEvent({ 
                        type: 'ws_test_close', 
                        data: { 
                            code: ev.code, 
                            reason: ev.reason, 
                            wasClean: ev.wasClean 
                        } 
                    });
                };
            } catch (e) {
                console.error('[WebRTC] ✗ WebSocket test exception:', e);
                sendEvent({ type: 'ws_test_exception', data: e.message });
            }

            console.log('[WebRTC] Creating WebSocket interface to:', config.wsUri);
            // Создаем WebSocket интерфейс
            let socket;
            try {
                socket = new JsSIP.WebSocketInterface(config.wsUri);
                console.log('[WebRTC] WebSocketInterface created successfully');
            } catch (e) {
                console.error('[WebRTC] ERROR creating WebSocketInterface:', e);
                sendEvent({ type: 'error', data: { message: 'Failed to create WebSocketInterface: ' + e.message, stack: e.stack } });
                return;
            }

            console.log('[WebRTC] Creating JsSIP UA with SIP URI:', config.sipUri);
            // Создаем UA
            try {
                ua = new JsSIP.UA({
                    sockets: [socket],
                    uri: config.sipUri,
                    password: config.password,
                    session_timers: false,
                    register: true,
                    register_expires: 300,
                    connection_recovery_min_interval: 2,
                    connection_recovery_max_interval: 30
                });
                console.log('[WebRTC] JsSIP UA created successfully');
            } catch (e) {
                console.error('[WebRTC] ERROR creating JsSIP UA:', e);
                sendEvent({ type: 'error', data: { message: 'Failed to create JsSIP UA: ' + e.message, stack: e.stack } });
                return;
            }

            // Обработчики событий UA
            ua.on('connected', () => {
                console.log('[WebRTC] ✓ WebSocket connected to ATS server');
                sendEvent({ type: 'ua_connected' });
            });

            ua.on('disconnected', (e) => {
                console.log('[WebRTC] ✗ WebSocket disconnected from ATS server', e);

                // JsSIP иногда кладёт полезное в e, иногда нет — отправим всё безопасно
                sendEvent({
                    type: 'ua_disconnected',
                    data: {
                        code: e?.code,
                        reason: e?.reason,
                        message: e?.message,
                        cause: e?.cause,
                        // на всякий — чтобы хоть что-то было видно
                        raw: (() => { try { return JSON.stringify(e); } catch { return String(e); } })()
                    }
                });
            });

            ua.on('registered', () => {
                console.log('[WebRTC] ✓ Successfully registered on ATS server as', config.sipUri);
                sendEvent({ type: 'ua_registered' });
            });

            ua.on('unregistered', () => {
                console.log('[WebRTC] Unregistered from ATS server');
                sendEvent({ type: 'ua_unregistered' });
            });

            ua.on('registrationFailed', (e) => {
                console.error('[WebRTC] ✗ Registration failed:', e.cause, e.message);
                console.error('[WebRTC] Registration failed details:', {
                    cause: e.cause,
                    message: e.message,
                    response: e.response,
                    status_code: e.response?.status_code,
                    reason_phrase: e.response?.reason_phrase
                });
                sendEvent({ 
                    type: 'ua_registration_failed', 
                    data: { 
                        cause: e.cause,
                        message: e.message || 'Registration failed',
                        statusCode: e.response?.status_code,
                        reasonPhrase: e.response?.reason_phrase
                    } 
                });
            });

            ua.on('newRTCSession', (e) => {
                console.log('[WebRTC] New RTC session:', e.originator);
                session = e.session;
                wireSessionEvents(session, e.originator);
            });

            console.log('[WebRTC] Starting JsSIP UA...');
            // Запускаем UA
            try {
                ua.start();
                console.log('[WebRTC] ua.start() called successfully');
            } catch (e) {
                console.error('[WebRTC] ERROR calling ua.start():', e);
                sendEvent({ type: 'error', data: { message: 'Failed to start UA: ' + e.message, stack: e.stack } });
                return;
            }

            console.log('[WebRTC] JsSIP UA started, connecting to ATS server...');
            sendEvent({ type: 'ua_started' });
            
            // Добавляем таймаут для диагностики - если через 5 секунд нет событий, отправляем предупреждение
            setTimeout(() => {
                if (ua && ua.isConnected() === false && ua.isRegistered() === false) {
                    console.warn('[WebRTC] WARNING: No connection events after 5 seconds. UA state:', {
                        isConnected: ua.isConnected(),
                        isRegistered: ua.isRegistered(),
                        status: ua.status()
                    });
                    sendEvent({ 
                        type: 'error', 
                        data: { 
                            message: 'No connection events after 5 seconds',
                            state: {
                                isConnected: ua.isConnected(),
                                isRegistered: ua.isRegistered(),
                                status: ua.status()
                            }
                        } 
                    });
                }
            }, 5000);
            
            // Добавляем таймаут для диагностики - если через 5 секунд нет событий, отправляем предупреждение
            setTimeout(() => {
                if (ua && ua.isConnected() === false && ua.isRegistered() === false) {
                    console.warn('[WebRTC] WARNING: No connection events after 5 seconds. UA state:', {
                        isConnected: ua.isConnected(),
                        isRegistered: ua.isRegistered(),
                        status: ua.status()
                    });
                    sendEvent({ 
                        type: 'error', 
                        data: { 
                            message: 'No connection events after 5 seconds',
                            state: {
                                isConnected: ua.isConnected(),
                                isRegistered: ua.isRegistered(),
                                status: ua.status()
                            }
                        } 
                    });
                }
            }, 5000);
        } catch (error) {
            console.error('[WebRTC] ✗ ERROR during initialization:', error.message, error.stack);
            sendEvent({ 
                type: 'error', 
                data: { 
                    message: error.message,
                    stack: error.stack 
                } 
            });
        }
    }

    // Подключение обработчиков событий сессии
    function wireSessionEvents(s, originator) {
        const sessionId = s.id;
        console.log('[WebRTC] wireSessionEvents: Wiring events for session:', sessionId, 'originator:', originator);
        
        sendEvent({ 
            type: 'new_session', 
            data: { 
                sessionId: sessionId,
                originator: originator,
                direction: originator === 'local' ? 'outgoing' : 'incoming'
            } 
        });

        s.on('progress', () => {
            console.log('[WebRTC] Session progress:', sessionId, 'originator:', originator);
            // Для исходящих звонков отправляем событие "ringing" для воспроизведения ringback tone
            if (originator === 'local') {
                sendEvent({ 
                    type: 'ringing',
                    data: { sessionId: sessionId }
                });
            }
            // Также отправляем call_progress для совместимости
            sendEvent({ 
                type: 'call_progress',
                data: { sessionId: sessionId }
            });
        });

        s.on('accepted', () => {
            console.log('[WebRTC] Session accepted:', sessionId);
            sendEvent({ 
                type: 'call_accepted',
                data: { sessionId: sessionId }
            });
            
            // Пытаемся подключить аудио треки сразу после accepted
            // PeerConnection должен быть готов к этому моменту
            try {
                const pc = s.connection;
                if (pc) {
                    console.log('[WebRTC] accepted: Checking for audio tracks in PeerConnection');
                    setTimeout(() => {
                        try {
                            const transceivers = pc.getTransceivers();
                            console.log('[WebRTC] accepted: Found transceivers:', transceivers.length);
                            transceivers.forEach((transceiver, index) => {
                                if (transceiver.receiver && transceiver.receiver.track && transceiver.receiver.track.kind === 'audio') {
                                    console.log(`[WebRTC] accepted: Found audio track in transceiver ${index}`);
                                    const remoteAudio = document.getElementById('remoteAudio');
                                    if (remoteAudio) {
                                        const stream = new MediaStream([transceiver.receiver.track]);
                                        remoteAudio.srcObject = stream;
                                        remoteAudio.muted = false;
                                        remoteAudio.volume = 1.0;
                                        remoteAudio.play().then(() => {
                                            console.log('[WebRTC] accepted: Audio track connected and playing');
                                            sendEvent({ 
                                                type: 'audio_connected', 
                                                data: { 
                                                    sessionId: sessionId,
                                                    trackId: transceiver.receiver.track.id,
                                                    source: 'accepted_event'
                                                } 
                                            });
                                        }).catch(err => {
                                            console.error('[WebRTC] accepted: Error playing audio:', err);
                                            sendEvent({ 
                                                type: 'error', 
                                                data: { 
                                                    name: 'AudioPlayError',
                                                    message: 'Failed to play audio in accepted: ' + err.message,
                                                    phase: 'accepted',
                                                    stack: err.stack
                                                } 
                                            });
                                        });
                                    }
                                }
                            });
                        } catch (err) {
                            console.error('[WebRTC] accepted: Error checking transceivers:', err);
                        }
                    }, 500); // Небольшая задержка для инициализации
                }
            } catch (err) {
                console.error('[WebRTC] accepted: Error accessing PeerConnection:', err);
            }
        });

        s.on('confirmed', () => {
            console.log('[WebRTC] Session confirmed:', sessionId);
            sendEvent({ 
                type: 'call_confirmed',
                data: { sessionId: sessionId }
            });
            
            // Пытаемся подключить аудио треки после confirmed (когда соединение точно установлено)
            try {
                const pc = s.connection;
                if (pc) {
                    console.log('[WebRTC] confirmed: Checking for audio tracks in PeerConnection');
                    setTimeout(() => {
                        try {
                            const transceivers = pc.getTransceivers();
                            console.log('[WebRTC] confirmed: Found transceivers:', transceivers.length);
                            transceivers.forEach((transceiver, index) => {
                                if (transceiver.receiver && transceiver.receiver.track && transceiver.receiver.track.kind === 'audio') {
                                    console.log(`[WebRTC] confirmed: Found audio track in transceiver ${index}`);
                                    const remoteAudio = document.getElementById('remoteAudio');
                                    if (remoteAudio) {
                                        const stream = new MediaStream([transceiver.receiver.track]);
                                        remoteAudio.srcObject = stream;
                                        remoteAudio.muted = false;
                                        remoteAudio.volume = 1.0;
                                        remoteAudio.play().then(() => {
                                            console.log('[WebRTC] confirmed: Audio track connected and playing');
                                            sendEvent({ 
                                                type: 'audio_connected', 
                                                data: { 
                                                    sessionId: sessionId,
                                                    trackId: transceiver.receiver.track.id,
                                                    source: 'confirmed_event'
                                                } 
                                            });
                                        }).catch(err => {
                                            console.error('[WebRTC] confirmed: Error playing audio:', err);
                                            sendEvent({ 
                                                type: 'error', 
                                                data: { 
                                                    name: 'AudioPlayError',
                                                    message: 'Failed to play audio in confirmed: ' + err.message,
                                                    phase: 'confirmed',
                                                    stack: err.stack
                                                } 
                                            });
                                        });
                                    }
                                }
                            });
                            
                            // Также проверяем через ontrack
                            if (!pc.ontrack) {
                                pc.ontrack = async (event) => {
                                    console.log('[WebRTC] confirmed: ontrack event:', event.track.kind);
                                    if (event.track && event.track.kind === 'audio') {
                                        const remoteAudio = document.getElementById('remoteAudio');
                                        if (remoteAudio) {
                                            const stream = new MediaStream([event.track]);
                                            remoteAudio.srcObject = stream;
                                            remoteAudio.muted = false;
                                            remoteAudio.volume = 1.0;
                                            await remoteAudio.play();
                                            console.log('[WebRTC] confirmed: Audio track from ontrack connected');
                                            sendEvent({ 
                                                type: 'audio_connected', 
                                                data: { 
                                                    sessionId: sessionId,
                                                    trackId: event.track.id,
                                                    source: 'confirmed_ontrack'
                                                } 
                                            });
                                        }
                                    }
                                };
                            }
                        } catch (err) {
                            console.error('[WebRTC] confirmed: Error checking transceivers:', err);
                        }
                    }, 500); // Небольшая задержка для инициализации
                }
            } catch (err) {
                console.error('[WebRTC] confirmed: Error accessing PeerConnection:', err);
            }
        });

        s.on('failed', (e) => {
            console.error('[WebRTC] Session failed:', sessionId, e);
            // Извлекаем безопасные строковые значения из события failed
            const causeStr = e?.cause ? (typeof e.cause === 'string' ? e.cause : (e.cause.toString ? e.cause.toString() : String(e.cause))) : null;
            const originatorStr = e?.originator ? (typeof e.originator === 'string' ? e.originator : String(e.originator)) : null;
            const statusCode = e?.response?.status_code ? (typeof e.response.status_code === 'number' ? e.response.status_code : parseInt(e.response.status_code) || null) : null;
            const reasonPhrase = e?.response?.reason_phrase ? (typeof e.response.reason_phrase === 'string' ? e.response.reason_phrase : String(e.response.reason_phrase)) : null;
            const messageStr = e?.message ? (typeof e.message === 'string' ? e.message : String(e.message)) : 'Call failed';
            
            sendEvent({ 
                type: 'call_failed', 
                data: { 
                    sessionId: sessionId,
                    cause: causeStr,
                    originator: originatorStr,
                    status_code: statusCode,
                    reason_phrase: reasonPhrase,
                    message: messageStr
                } 
            });
            session = null;
            window._activeSession = null;
        });

        s.on('ended', (e) => {
            console.log('[WebRTC] Session ended:', sessionId, e);
            // Извлекаем безопасные строковые значения из события ended
            const causeStr = e?.cause ? (typeof e.cause === 'string' ? e.cause : (e.cause.toString ? e.cause.toString() : String(e.cause))) : null;
            const originatorStr = e?.originator ? (typeof e.originator === 'string' ? e.originator : String(e.originator)) : null;
            const messageStr = e?.message ? (typeof e.message === 'string' ? e.message : String(e.message)) : null;
            
            sendEvent({ 
                type: 'call_ended',
                data: { 
                    sessionId: sessionId,
                    cause: causeStr,
                    originator: originatorStr,
                    message: messageStr
                }
            });
            session = null;
            window._activeSession = null;
        });

        s.on('peerconnection', (e) => {
            // Подключаем входящий аудио трек к аудио элементу для воспроизведения
            console.log('[WebRTC] peerconnection event received for session:', sessionId);
            try {
                const pc = e.peerconnection;
                console.log('[WebRTC] PeerConnection state:', pc.connectionState, 'ICE state:', pc.iceConnectionState);
                
                // Функция для подключения аудио трека к элементу и запуска воспроизведения
                const connectAudioTrack = async (track) => {
                    if (track.kind !== 'audio') {
                        console.log('[WebRTC] Skipping non-audio track:', track.kind);
                        return;
                    }
                    
                    console.log('[WebRTC] Connecting audio track:', track.id, 'enabled:', track.enabled, 'muted:', track.muted, 'readyState:', track.readyState);
                    
                    const remoteAudio = document.getElementById('remoteAudio');
                    if (!remoteAudio) {
                        console.warn('[WebRTC] Remote audio element not found');
                        sendEvent({ 
                            type: 'error', 
                            data: { 
                                name: 'AudioElementNotFound',
                                message: 'Remote audio element not found in DOM',
                                phase: 'peerconnection'
                            } 
                        });
                        return;
                    }
                    
                    try {
                        const stream = new MediaStream([track]);
                        remoteAudio.srcObject = stream;
                        console.log('[WebRTC] Audio stream set to element, tracks:', stream.getAudioTracks().length);
                        
                        // Проверяем состояние элемента
                        console.log('[WebRTC] Audio element state - paused:', remoteAudio.paused, 'muted:', remoteAudio.muted, 'volume:', remoteAudio.volume);
                        
                        // Убеждаемся, что элемент не muted
                        remoteAudio.muted = false;
                        remoteAudio.volume = 1.0;
                        
                        // Явно запускаем воспроизведение
                        const playPromise = remoteAudio.play();
                        if (playPromise !== undefined) {
                            await playPromise;
                            console.log('[WebRTC] ✓ Remote audio track connected and playing');
                            sendEvent({ 
                                type: 'audio_connected', 
                                data: { 
                                    sessionId: sessionId,
                                    trackId: track.id,
                                    enabled: track.enabled,
                                    readyState: track.readyState
                                } 
                            });
                        } else {
                            console.log('[WebRTC] Remote audio track connected (play() not available)');
                        }
                        
                        // Подписываемся на изменения состояния трека
                        track.onended = () => {
                            console.log('[WebRTC] Remote audio track ended');
                        };
                        track.onmute = () => {
                            console.warn('[WebRTC] Remote audio track muted');
                        };
                        track.onunmute = () => {
                            console.log('[WebRTC] Remote audio track unmuted');
                        };
                        
                        // Логируем информацию о кодеках
                        const settings = track.getSettings();
                        console.log('[WebRTC] Audio track settings:', settings);
                        sendEvent({ 
                            type: 'audio_track_info', 
                            data: {
                                codec: settings.codec || 'unknown',
                                sampleRate: settings.sampleRate || 'unknown',
                                channels: settings.channelCount || 'unknown'
                            }
                        });
                    } catch (playError) {
                        console.error('[WebRTC] Error playing remote audio:', playError);
                        sendEvent({ 
                            type: 'error', 
                            data: { 
                                name: 'AudioPlayError',
                                message: 'Failed to play remote audio: ' + playError.message,
                                phase: 'peerconnection',
                                stack: playError.stack
                            } 
                        });
                    }
                };
                
                // Обрабатываем существующие треки
                const transceivers = pc.getTransceivers();
                console.log('[WebRTC] Found transceivers:', transceivers.length);
                transceivers.forEach((transceiver, index) => {
                    console.log(`[WebRTC] Transceiver ${index}: direction=${transceiver.direction}, receiver=${!!transceiver.receiver}, sender=${!!transceiver.sender}`);
                    if (transceiver.receiver && transceiver.receiver.track) {
                        const track = transceiver.receiver.track;
                        console.log(`[WebRTC] Transceiver ${index} receiver track: kind=${track.kind}, id=${track.id}, enabled=${track.enabled}`);
                        connectAudioTrack(track);
                    }
                });
                
                // Подписываемся на новые треки (это важно, так как треки могут появиться позже)
                pc.ontrack = async (event) => {
                    console.log('[WebRTC] ontrack event:', event.track.kind, event.track.id, 'streams:', event.streams.length);
                    if (event.track && event.track.kind === 'audio') {
                        await connectAudioTrack(event.track);
                    }
                };
                
                // Также подписываемся на изменения состояния соединения
                pc.onconnectionstatechange = () => {
                    console.log('[WebRTC] PeerConnection connectionState changed to:', pc.connectionState);
                };
                pc.oniceconnectionstatechange = () => {
                    console.log('[WebRTC] PeerConnection iceConnectionState changed to:', pc.iceConnectionState);
                };
            } catch (err) {
                console.error('[WebRTC] Error handling peerconnection:', err);
                sendEvent({ 
                    type: 'error', 
                    data: { 
                        name: 'PeerConnectionError',
                        message: 'Error handling peerconnection: ' + err.message,
                        phase: 'peerconnection',
                        stack: err.stack
                    } 
                });
            }
        });
    }

    // Исходящий звонок
    async function makeCall(number) {
        try {
            console.log('[WebRTC] makeCall called with number:', number);
            sendEvent({ type: 'makeCall_started', data: { number: number } });
            
            if (!ua) {
                const errorMsg = 'UA not initialized';
                console.error('[WebRTC] makeCall:', errorMsg);
                sendEvent({ 
                    type: 'error', 
                    data: { 
                        name: 'UANotInitialized',
                        message: errorMsg,
                        phase: 'makeCall',
                        stack: new Error().stack
                    } 
                });
                return;
            }

            if (session || window._activeSession) {
                const errorMsg = 'Call already in progress';
                console.warn('[WebRTC] makeCall:', errorMsg);
                sendEvent({ 
                    type: 'error', 
                    data: { 
                        name: 'CallInProgress',
                        message: errorMsg,
                        phase: 'makeCall',
                        stack: new Error().stack
                    } 
                });
                return;
            }

            // Формируем SIP URI
            // ВАЖНО: ua.configuration.uri - это JsSIP.URI объект, а не строка
            let targetNumber = number;
            if (!targetNumber.includes('@')) {
                // Если номер не содержит @, формируем полный SIP URI
                // Получаем domain из JsSIP.URI объекта
                const domain = ua.configuration.uri.host || ua.configuration.uri.toString().split('@')[1];
                targetNumber = `sip:${targetNumber}@${domain}`;
            } else {
                // Если номер уже содержит @, проверяем формат
                const parts = targetNumber.split('@');
                if (parts.length === 2) {
                    let username = parts[0].replace('sip:', '');
                    targetNumber = `sip:${username}@${parts[1]}`;
                }
            }
            const targetUri = targetNumber;
            console.log('[WebRTC] makeCall: Target URI:', targetUri);

            // Явно запрашиваем медиа перед созданием звонка
            // Это критично для скрытого WebView2, чтобы получить разрешение на микрофон
            console.log('[WebRTC] makeCall: Requesting getUserMedia...');
            let mediaStream;
            try {
                mediaStream = await navigator.mediaDevices.getUserMedia(mediaConstraints);
                console.log('[WebRTC] makeCall: getUserMedia successful, stream:', mediaStream);
            } catch (getUserMediaError) {
                console.error('[WebRTC] makeCall: getUserMedia failed:', getUserMediaError);
                sendEvent({ 
                    type: 'error', 
                    data: { 
                        name: getUserMediaError.name || 'GetUserMediaError',
                        message: getUserMediaError.message || 'Failed to get user media',
                        phase: 'getUserMedia',
                        stack: getUserMediaError.stack || new Error().stack
                    } 
                });
                return;
            }

            // Создаем звонок с уже полученным медиа-стримом
            console.log('[WebRTC] makeCall: Calling ua.call with URI:', targetUri);
            session = ua.call(targetUri, {
                mediaStream: mediaStream, // Используем уже полученный стрим
                pcConfig: {
                    iceServers: [] // Можно добавить STUN/TURN серверы позже
                },
                rtcOfferConstraints: {
                    offerToReceiveAudio: true,
                    offerToReceiveVideo: false
                }
            });

            // Устанавливаем window._activeSession для совместимости
            window._activeSession = session;
            // Сохраняем ссылку на локальный поток для управления mute
            session.localStream = mediaStream;
            console.log('[WebRTC] makeCall: Call session created, sessionId:', session.id);

            wireSessionEvents(session, 'local');
            sendEvent({ type: 'makeCall_initiated', data: { sessionId: session.id, targetUri: targetUri } });
        } catch (error) {
            console.error('[WebRTC] makeCall error:', error);
            sendEvent({ 
                type: 'error', 
                data: { 
                    name: error.name || 'UnknownError',
                    message: error.message || 'Unknown error',
                    phase: 'makeCall',
                    stack: error.stack || new Error().stack
                } 
            });
        }
    }

    // Ответ на входящий звонок (с захватом медиа)
    async function answer() {
        try {
            const sess = window._incomingSession;
            if (!sess) {
                sendEvent({ type: 'error', data: 'No incoming session to answer' });
                return;
            }

            // Захватываем медиа ТОЛЬКО при ответе
            console.log('[WebRTC] Requesting user media for answer...');
            const stream = await navigator.mediaDevices.getUserMedia({
                audio: {
                    echoCancellation: true,
                    noiseSuppression: true,
                    autoGainControl: true
                },
                video: false
            });
            console.log('[WebRTC] ✓ User media acquired for answer');

            sess.answer({
                mediaStream: stream,
                rtcOfferConstraints: {
                    offerToReceiveAudio: true,
                    offerToReceiveVideo: false
                }
            });

            // Сохраняем ссылку на локальный поток для управления mute и записи
            sess.localStream = stream;
            console.log('[WebRTC] answer: Local stream saved to session.localStream');

            window._activeSession = sess;
            window._incomingSession = null;
            session = sess;
        } catch (error) {
            console.error('[WebRTC] ERROR in answer:', error);
            sendEvent({ 
                type: 'error', 
                data: { 
                    message: error.message,
                    stack: error.stack 
                } 
            });
        }
    }

    // Завершение звонка
    function hangup() {
        try {
            if (session) {
                session.terminate();
                session = null;
            }
        } catch (error) {
            sendEvent({ 
                type: 'error', 
                data: { 
                    message: error.message,
                    stack: error.stack 
                } 
            });
        }
    }

    // Получение статуса UA
    function getStatus() {
        if (!ua) {
            return { status: 'not_initialized' };
        }

        return {
            status: ua.isRegistered() ? 'registered' : 'unregistered',
            isConnected: ua.isConnected(),
            hasActiveSession: session !== null
        };
    }

    // Остановка UA
    function stop() {
        try {
            if (session) {
                session.terminate();
                session = null;
            }

            if (ua) {
                ua.stop();
                ua = null;
            }

            sendEvent({ type: 'ua_stopped' });
        } catch (error) {
            sendEvent({ 
                type: 'error', 
                data: { 
                    message: error.message,
                    stack: error.stack 
                } 
            });
        }
    }

    // Ping/Pong для watchdog
    // Когда C# вызывает ping(), мы отвечаем событием pong
    // Защита 3: JS ping() обязан постить pong даже если UA не зарегистрирован
    // ping/pong - это здоровье WebView2/JS runtime, а не SIP регистрации
    function ping() {
        try {
            // Всегда отвечаем pong, независимо от состояния UA
            if (window.chrome?.webview) {
                window.chrome.webview.postMessage(JSON.stringify({ type: "pong", ts: Date.now() }));
            } else {
                // Fallback через sendEvent если chrome.webview недоступен
                sendEvent({ type: 'pong', data: { timestamp: Date.now() } });
            }
        } catch (e) {
            console.error('[WebRTC] Error sending pong:', e);
        }
        return true; // чтобы ExecuteScriptAsync возвращал "true" (удобно для логов)
    }
    
    // Функция pong() не используется напрямую из C#
    // Она может быть использована для ответа на ping от других источников
    function pong(timestamp) {
        sendEvent({ type: 'pong', data: { timestamp: timestamp, receivedAt: Date.now() } });
    }
    
    // Reset engine (для watchdog)
    async function resetEngine() {
        try {
            console.log('[WebRTC] Resetting engine...');
            
            // Останавливаем активную сессию
            if (window._activeSession) {
                try {
                    window._activeSession.terminate();
                } catch (e) {
                    console.error('[WebRTC] Error terminating active session:', e);
                }
                window._activeSession = null;
            }
            
            // Очищаем входящую сессию
            window._incomingSession = null;
            session = null;
            
            // Останавливаем UA
            if (ua) {
                try {
                    ua.stop();
                } catch (e) {
                    console.error('[WebRTC] Error stopping UA:', e);
                }
                ua = null;
            }
            
            sendEvent({ type: 'engine_reset' });
            console.log('[WebRTC] Engine reset completed');
        } catch (error) {
            console.error('[WebRTC] ERROR in resetEngine:', error);
            sendEvent({ type: 'error', data: { message: 'Failed to reset engine: ' + error.message } });
        }
    }
    
    // Получение статистики для диагностики
    function getStats() {
        try {
            if (!window._activeSession || !window._activeSession.connection) {
                return null;
            }
            
            const pc = window._activeSession.connection;
            const stats = {
                iceConnectionState: pc.iceConnectionState,
                connectionState: pc.connectionState,
                signalingState: pc.signalingState
            };
            
            // Получаем статистику через getStats (асинхронно)
            pc.getStats().then((report) => {
                const statsData = {};
                report.forEach((stat) => {
                    if (stat.type === 'inbound-rtp' || stat.type === 'outbound-rtp') {
                        statsData[stat.type] = {
                            packetsLost: stat.packetsLost,
                            jitter: stat.jitter,
                            bytesReceived: stat.bytesReceived,
                            bytesSent: stat.bytesSent
                        };
                    }
                });
                sendEvent({ 
                    type: 'stats', 
                    data: { 
                        ...stats,
                        rtpStats: statsData
                    } 
                });
            }).catch((e) => {
                console.error('[WebRTC] Error getting stats:', e);
            });
            
            return stats;
        } catch (error) {
            console.error('[WebRTC] ERROR in getStats:', error);
            return null;
        }
    }
    
    // Флаг для отслеживания разрешения на микрофон
    let _micPermissionGranted = false;
    
    // Обеспечиваем разрешение на микрофон перед enumerateDevices
    async function ensureMicPermission() {
        if (_micPermissionGranted) {
            console.log('[WebRTC] ensureMicPermission: Permission already granted');
            return true;
        }
        
        try {
            console.log('[WebRTC] ensureMicPermission: Requesting microphone permission...');
            const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
            // Сразу останавливаем треки, нам нужно только разрешение
            stream.getTracks().forEach(track => {
                track.stop();
                console.log('[WebRTC] ensureMicPermission: Stopped temporary track:', track.id);
            });
            _micPermissionGranted = true;
            console.log('[WebRTC] ensureMicPermission: ✓ Permission granted');
            return true;
        } catch (error) {
            console.error('[WebRTC] ensureMicPermission: Failed to get permission:', error);
            sendEvent({
                type: 'error',
                data: {
                    name: 'MicPermissionError',
                    message: 'Failed to get microphone permission: ' + error.message,
                    phase: 'ensureMicPermission',
                    stack: error.stack
                }
            });
            return false;
        }
    }
    
    // Получение списка аудиоустройств
    async function enumerateAudioDevices() {
        try {
            // ВАЖНО: сначала получаем разрешение на микрофон, иначе enumerateDevices вернет пустые labels
            const permissionGranted = await ensureMicPermission();
            if (!permissionGranted) {
                console.warn('[WebRTC] enumerateAudioDevices: Microphone permission not granted');
                sendEvent({
                    type: 'audio_devices_list',
                    data: {
                        inputs: [],
                        outputs: []
                    }
                });
                return { inputs: [], outputs: [] };
            }
            
            const devices = await navigator.mediaDevices.enumerateDevices();
            
            const audioInputs = [];
            const audioOutputs = [];
            
            devices.forEach(device => {
                if (device.kind === 'audioinput') {
                    audioInputs.push({
                        deviceId: device.deviceId,
                        label: device.label || `Microphone ${audioInputs.length + 1}`,
                        groupId: device.groupId
                    });
                } else if (device.kind === 'audiooutput') {
                    audioOutputs.push({
                        deviceId: device.deviceId,
                        label: device.label || `Speaker ${audioOutputs.length + 1}`,
                        groupId: device.groupId
                    });
                }
            });
            
            console.log(`[WebRTC] enumerateAudioDevices: Found ${audioInputs.length} input(s) and ${audioOutputs.length} output(s)`);
            
            sendEvent({
                type: 'audio_devices_list',
                data: {
                    inputs: audioInputs,
                    outputs: audioOutputs
                }
            });
            
            return {
                inputs: audioInputs,
                outputs: audioOutputs
            };
        } catch (error) {
            console.error('[WebRTC] ERROR in enumerateAudioDevices:', error);
            sendEvent({
                type: 'error',
                data: {
                    name: 'EnumerateDevicesError',
                    message: 'Failed to enumerate devices: ' + error.message,
                    phase: 'enumerateAudioDevices',
                    stack: error.stack
                }
            });
            return { inputs: [], outputs: [] };
        }
    }
    
    // Переключение аудиоустройства
    async function switchAudioDevice(inputDeviceId, outputDeviceId) {
        try {
            if (!window._activeSession) {
                console.warn('[WebRTC] switchAudioDevice: No active session');
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'NoActiveSession',
                        message: 'Cannot switch device: no active session',
                        phase: 'switchAudioDevice'
                    }
                });
                return false;
            }
            
            const session = window._activeSession;
            const pc = session.connection;
            
            if (!pc) {
                console.warn('[WebRTC] switchAudioDevice: No PeerConnection');
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'NoPeerConnection',
                        message: 'Cannot switch device: no PeerConnection',
                        phase: 'switchAudioDevice'
                    }
                });
                return false;
            }
            
            // Переключение входного устройства (микрофона)
            if (inputDeviceId) {
                try {
                    const constraints = {
                        audio: {
                            deviceId: { exact: inputDeviceId },
                            echoCancellation: true,
                            noiseSuppression: true,
                            autoGainControl: true
                        }
                    };
                    
                    const newStream = await navigator.mediaDevices.getUserMedia(constraints);
                    console.log('[WebRTC] switchAudioDevice: Got new input stream');
                    
                    // Заменяем аудио треки в локальном потоке
                    const oldTracks = session.localStream.getAudioTracks();
                    oldTracks.forEach(track => {
                        track.stop();
                        session.localStream.removeTrack(track);
                    });
                    
                    const newTracks = newStream.getAudioTracks();
                    newTracks.forEach(track => {
                        session.localStream.addTrack(track);
                    });
                    
                    // Обновляем PeerConnection
                    const senders = pc.getSenders();
                    for (const sender of senders) {
                        if (sender.track && sender.track.kind === 'audio') {
                            await sender.replaceTrack(newTracks[0]);
                            console.log('[WebRTC] switchAudioDevice: Replaced audio track in PeerConnection');
                        }
                    }
                    
                    console.log(`[WebRTC] switchAudioDevice: Input device switched to ${inputDeviceId}`);
                } catch (err) {
                    console.error('[WebRTC] switchAudioDevice: Error switching input device:', err);
                    sendEvent({
                        type: 'error',
                        data: {
                            name: 'SwitchInputDeviceError',
                            message: 'Failed to switch input device: ' + err.message,
                            phase: 'switchAudioDevice'
                        }
                    });
                }
            }
            
            // Переключение выходного устройства (динамика)
            // В WebRTC выходное устройство управляется через HTMLAudioElement.setSinkId()
            if (outputDeviceId) {
                try {
                    const remoteAudio = document.getElementById('remoteAudio');
                    if (remoteAudio && 'setSinkId' in remoteAudio) {
                        await remoteAudio.setSinkId(outputDeviceId);
                        console.log(`[WebRTC] switchAudioDevice: Output device switched to ${outputDeviceId}`);
                        sendEvent({
                            type: 'audio_device_switched',
                            data: {
                                outputDeviceId: outputDeviceId,
                                success: true
                            }
                        });
                    } else {
                        console.warn('[WebRTC] switchAudioDevice: setSinkId not supported');
                        sendEvent({
                            type: 'error',
                            data: {
                                name: 'SetSinkIdNotSupported',
                                message: 'setSinkId is not supported in this browser',
                                phase: 'switchAudioDevice'
                            }
                        });
                    }
                } catch (err) {
                    console.error('[WebRTC] switchAudioDevice: Error switching output device:', err);
                    sendEvent({
                        type: 'error',
                        data: {
                            name: 'SwitchOutputDeviceError',
                            message: 'Failed to switch output device: ' + err.message,
                            phase: 'switchAudioDevice'
                        }
                    });
                }
            }
            
            return true;
        } catch (error) {
            console.error('[WebRTC] ERROR in switchAudioDevice:', error);
            sendEvent({
                type: 'error',
                data: {
                    name: 'SwitchAudioDeviceError',
                    message: 'Failed to switch audio device: ' + error.message,
                    phase: 'switchAudioDevice',
                    stack: error.stack
                }
            });
            return false;
        }
    }
    
    // Управление mute микрофона
    function setMute(mute) {
        try {
            console.log(`[WebRTC] setMute called with mute=${mute}`);

            if (!window._activeSession) {
                console.warn('[WebRTC] setMute: No active session');
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'NoActiveSession',
                        message: 'Cannot set mute: no active session',
                        phase: 'setMute'
                    }
                });
                return false;
            }

            const session = window._activeSession;
            const pc = session.connection;

            if (!pc) {
                console.warn('[WebRTC] setMute: No PeerConnection');
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'NoPeerConnection',
                        message: 'Cannot set mute: no PeerConnection',
                        phase: 'setMute'
                    }
                });
                return false;
            }

            // Получаем все senders и логируем их состояние
            const senders = pc.getSenders();
            console.log(`[WebRTC] setMute: Found ${senders.length} sender(s)`);

            const audioSenders = senders.filter(s => s.track && s.track.kind === 'audio');
            console.log(`[WebRTC] setMute: Found ${audioSenders.length} audio sender(s)`);

            if (audioSenders.length === 0) {
                console.warn('[WebRTC] setMute: No audio senders found');

                // Пробуем через transceivers как fallback
                const transceivers = pc.getTransceivers();
                const audioTransceivers = transceivers.filter(t => t.sender && t.sender.track && t.sender.track.kind === 'audio');
                console.log(`[WebRTC] setMute: Found ${audioTransceivers.length} audio transceiver(s)`);

                if (audioTransceivers.length === 0) {
                    sendEvent({
                        type: 'error',
                        data: {
                            name: 'NoAudioTracks',
                            message: 'Cannot set mute: no audio tracks found',
                            phase: 'setMute'
                        }
                    });
                    return false;
                }

                // Мьютим через transceivers
                audioTransceivers.forEach((transceiver, index) => {
                    const track = transceiver.sender.track;
                    track.enabled = !mute;
                    console.log(`[WebRTC] setMute: Transceiver ${index} track ${track.id} enabled=${track.enabled} (mute=${mute})`);
                });

                sendEvent({
                    type: 'mute_applied',
                    data: {
                        muted: mute,
                        tracksCount: audioTransceivers.length,
                        method: 'transceivers'
                    }
                });
                return true;
            }

            // Мьютим все аудио senders (самый надежный способ)
            audioSenders.forEach((sender, index) => {
                const track = sender.track;
                track.enabled = !mute;
                console.log(`[WebRTC] setMute: Sender ${index} track ${track.id} enabled=${track.enabled} (mute=${mute})`);
            });

            console.log(`[WebRTC] setMute: ✓ Mute set to ${mute} for ${audioSenders.length} audio track(s)`);
            sendEvent({
                type: 'mute_applied',
                data: {
                    muted: mute,
                    tracksCount: audioSenders.length,
                    method: 'senders'
                }
            });

            return true;
        } catch (error) {
            console.error('[WebRTC] ERROR in setMute:', error);
            sendEvent({
                type: 'error',
                data: {
                    name: 'SetMuteError',
                    message: 'Failed to set mute: ' + error.message,
                    phase: 'setMute',
                    stack: error.stack
                }
            });
            return false;
        }
    }

    // Отправка DTMF тона
    function sendDtmf(digit) {
        try {
            console.log(`[WebRTC] sendDtmf called with digit='${digit}'`);

            if (!window._activeSession) {
                console.warn('[WebRTC] sendDtmf: No active session');
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'NoActiveSession',
                        message: 'Cannot send DTMF: no active session',
                        phase: 'sendDtmf'
                    }
                });
                return false;
            }

            const session = window._activeSession;

            // Проверяем, что сессия существует и имеет connection (PeerConnection)
            // В JsSIP можно отправлять DTMF только когда сессия установлена и есть PeerConnection
            if (!session.connection) {
                console.warn(`[WebRTC] sendDtmf: Session has no PeerConnection`);
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'NoPeerConnection',
                        message: 'Cannot send DTMF: session has no PeerConnection',
                        phase: 'sendDtmf'
                    }
                });
                return false;
            }

            // Проверяем, что PeerConnection в правильном состоянии
            const pc = session.connection;
            if (pc.signalingState !== 'stable' || (pc.connectionState !== 'connected' && pc.iceConnectionState !== 'connected')) {
                console.warn(`[WebRTC] sendDtmf: PeerConnection not ready (signalingState=${pc.signalingState}, connectionState=${pc.connectionState}, iceConnectionState=${pc.iceConnectionState})`);
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'PeerConnectionNotReady',
                        message: `Cannot send DTMF: PeerConnection not ready (signalingState=${pc.signalingState}, connectionState=${pc.connectionState})`,
                        phase: 'sendDtmf'
                    }
                });
                return false;
            }

            // Валидация цифры
            if (typeof digit !== 'string' || digit.length !== 1) {
                console.warn(`[WebRTC] sendDtmf: Invalid digit format: '${digit}'`);
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'InvalidDigit',
                        message: `Invalid DTMF digit: '${digit}' (must be single character)`,
                        phase: 'sendDtmf'
                    }
                });
                return false;
            }

            // Отправляем DTMF через JsSIP session
            try {
                session.sendDTMF(digit);
                console.log(`[WebRTC] sendDtmf: ✓ DTMF digit '${digit}' sent successfully`);
                sendEvent({
                    type: 'dtmf_sent',
                    data: {
                        digit: digit,
                        success: true
                    }
                });
                return true;
            } catch (dtmfError) {
                console.error('[WebRTC] sendDtmf: Error sending DTMF:', dtmfError);
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'SendDtmfError',
                        message: 'Failed to send DTMF: ' + dtmfError.message,
                        phase: 'sendDtmf',
                        stack: dtmfError.stack
                    }
                });
                return false;
            }
        } catch (error) {
            console.error('[WebRTC] ERROR in sendDtmf:', error);
            sendEvent({
                type: 'error',
                data: {
                    name: 'SendDtmfError',
                    message: 'Failed to send DTMF: ' + error.message,
                    phase: 'sendDtmf',
                    stack: error.stack
                }
            });
            return false;
        }
    }
    
    // Массив для хранения чанков записи
    let recordingChunks = [];
    
    // Запуск записи звонка
    async function startRecording() {
        try {
            if (mediaRecorder && mediaRecorder.state !== 'inactive') {
                console.log('[WebRTC] Recording already started');
                return;
            }
            
            if (!session || !session.connection) {
                console.error('[WebRTC] Cannot start recording: no active session');
                sendEvent({ 
                    type: 'error', 
                    data: { 
                        name: 'RecordingError',
                        message: 'Cannot start recording: no active session',
                        phase: 'startRecording'
                    } 
                });
                return;
            }
            
            const pc = session.connection;
            
            // Получаем все аудио треки для записи
            const audioTracks = [];
            
            // 1. Получаем локальные треки (микрофон) из session.localStream
            if (session.localStream) {
                const localTracks = session.localStream.getAudioTracks();
                localTracks.forEach(track => {
                    if (track && track.readyState === 'live') {
                        audioTracks.push(track);
                        console.log('[WebRTC] startRecording: Added local audio track:', track.label, 'enabled:', track.enabled);
                    }
                });
            }
            
            // 2. Получаем удаленные треки (голос абонента) из PeerConnection receivers
            const transceivers = pc.getTransceivers();
            transceivers.forEach(transceiver => {
                if (transceiver.receiver && transceiver.receiver.track && transceiver.receiver.track.kind === 'audio') {
                    const remoteTrack = transceiver.receiver.track;
                    // Проверяем, что трек не дублируется (если он уже был добавлен как локальный)
                    if (!audioTracks.includes(remoteTrack) && remoteTrack.readyState === 'live') {
                        audioTracks.push(remoteTrack);
                        console.log('[WebRTC] startRecording: Added remote audio track:', remoteTrack.label);
                    }
                }
            });
            
            console.log('[WebRTC] startRecording: Total audio tracks collected:', audioTracks.length);
            
            if (audioTracks.length === 0) {
                console.error('[WebRTC] Cannot start recording: no audio tracks found');
                sendEvent({ 
                    type: 'error', 
                    data: { 
                        name: 'RecordingError',
                        message: 'Cannot start recording: no audio tracks found',
                        phase: 'startRecording'
                    } 
                });
                return;
            }
            
            // Микшируем все треки в один поток через AudioContext для гарантированной записи обоих потоков
            const audioContext = new (window.AudioContext || window.webkitAudioContext)();
            const destination = audioContext.createMediaStreamDestination();
            
            // Сохраняем источники для последующего отключения
            const audioSources = [];
            
            // Подключаем все треки к destination для микширования
            audioTracks.forEach((track, index) => {
                try {
                    console.log(`[WebRTC] startRecording: Track ${index}: label=${track.label}, id=${track.id}, enabled=${track.enabled}, muted=${track.muted}, readyState=${track.readyState}`);
                    
                    // Создаем MediaStream из одного трека
                    const trackStream = new MediaStream([track]);
                    const source = audioContext.createMediaStreamSource(trackStream);
                    
                    // Подключаем к destination
                    source.connect(destination);
                    audioSources.push(source);
                    
                    console.log(`[WebRTC] startRecording: Connected track ${index} to mixer: ${track.label}`);
                } catch (err) {
                    console.error(`[WebRTC] startRecording: Error connecting track ${index} (${track.label}):`, err);
                }
            });
            
            // Используем микшированный поток для записи
            recordingStream = destination.stream;
            console.log('[WebRTC] startRecording: Created mixed recording stream with', audioTracks.length, 'audio tracks,', recordingStream.getTracks().length, 'tracks in destination stream');
            
            // Проверяем, что destination stream содержит треки
            const destinationTracks = recordingStream.getAudioTracks();
            if (destinationTracks.length === 0) {
                console.error('[WebRTC] startRecording: WARNING - destination stream has no audio tracks!');
            } else {
                destinationTracks.forEach((track, index) => {
                    console.log(`[WebRTC] startRecording: Destination track ${index}: id=${track.id}, enabled=${track.enabled}, readyState=${track.readyState}`);
                });
            }
            
            // Сохраняем ссылки для очистки при остановке
            recordingStream._audioContext = audioContext;
            recordingStream._audioSources = audioSources;
            
            // Создаем MediaRecorder с WebM кодеком
            const options = {
                mimeType: 'audio/webm;codecs=opus',
                audioBitsPerSecond: 128000
            };
            
            // Пробуем создать MediaRecorder
            if (!MediaRecorder.isTypeSupported(options.mimeType)) {
                options.mimeType = 'audio/webm';
                if (!MediaRecorder.isTypeSupported(options.mimeType)) {
                    options.mimeType = '';
                }
            }
            
            mediaRecorder = new MediaRecorder(recordingStream, options);
            
            // Очищаем массив чанков перед началом записи
            recordingChunks = [];
            
            // Собираем все чанки в массив (не отправляем по мере поступления)
            mediaRecorder.ondataavailable = (event) => {
                console.log(`[WebRTC] ondataavailable fired: data.size=${event.data?.size || 0}, state=${mediaRecorder?.state}`);
                if (event.data && event.data.size > 0) {
                    recordingChunks.push(event.data);
                    console.log(`[WebRTC] Recording chunk received: ${event.data.size} bytes (total chunks: ${recordingChunks.length})`);
                } else {
                    console.warn(`[WebRTC] ondataavailable fired but data is empty or null`);
                }
            };
            
            mediaRecorder.onerror = (event) => {
                console.error('[WebRTC] MediaRecorder error:', event.error);
                recordingChunks = []; // Очищаем при ошибке
                
                // Закрываем AudioContext при ошибке
                if (recordingStream && recordingStream._audioContext) {
                    const audioContext = recordingStream._audioContext;
                    if (audioContext.state !== 'closed') {
                        audioContext.close().catch(err => {
                            console.error('[WebRTC] Error closing AudioContext on error:', err);
                        });
                    }
                    delete recordingStream._audioContext;
                }
                
                sendEvent({ 
                    type: 'error', 
                    data: { 
                        name: 'RecordingError',
                        message: 'MediaRecorder error: ' + (event.error?.message || 'unknown'),
                        phase: 'recording'
                    } 
                });
            };
            
            mediaRecorder.onstop = async () => {
                console.log('[WebRTC] MediaRecorder.onstop event fired, chunks count:', recordingChunks.length, 'state:', mediaRecorder?.state);
                try {
                    await processRecordingChunks();
                } catch (error) {
                    console.error('[WebRTC] Error in onstop handler:', error);
                    console.error('[WebRTC] Error stack:', error.stack);
                    sendEvent({ 
                        type: 'error', 
                        data: { 
                            name: 'RecordingError',
                            message: 'Error in onstop handler: ' + error.message,
                            phase: 'onstop',
                            stack: error.stack
                        } 
                    });
                }
            };
            
            // Запускаем запись БЕЗ интервала (собираем все чанки до stop)
            // Без timeslice - все данные будут в одном chunk при stop()
            mediaRecorder.start();
            console.log('[WebRTC] Recording started (MediaRecorder.start() - all data will be in one chunk on stop)');
            sendEvent({ type: 'recording_started' });
        } catch (error) {
            console.error('[WebRTC] Error starting recording:', error);
            recordingChunks = [];
            
            // Закрываем AudioContext при ошибке запуска
            if (recordingStream && recordingStream._audioContext) {
                const audioContext = recordingStream._audioContext;
                if (audioContext.state !== 'closed') {
                    audioContext.close().catch(err => {
                        console.error('[WebRTC] Error closing AudioContext on start error:', err);
                    });
                }
                delete recordingStream._audioContext;
            }
            recordingStream = null;
            
            sendEvent({ 
                type: 'error', 
                data: { 
                    name: 'RecordingError',
                    message: 'Failed to start recording: ' + error.message,
                    phase: 'startRecording',
                    stack: error.stack
                } 
            });
        }
    }
    
    // Остановка записи звонка
    function stopRecording() {
        try {
            console.log('[WebRTC] stopRecording called, mediaRecorder state:', mediaRecorder?.state, 'chunks count:', recordingChunks.length);
            
            // Сохраняем ссылку на mediaRecorder и обработчик перед остановкой
            const recorder = mediaRecorder;
            const onstopHandler = recorder?.onstop;
            
            if (recorder && recorder.state !== 'inactive') {
                console.log('[WebRTC] Stopping MediaRecorder, current state:', recorder.state);
                
                // Сохраняем обработчик onstop перед остановкой (на случай если он будет потерян)
                if (onstopHandler && typeof onstopHandler === 'function') {
                    console.log('[WebRTC] onstop handler exists, will be called after stop()');
                } else {
                    console.warn('[WebRTC] WARNING: onstop handler is missing or not a function!');
                }
                
                // Останавливаем MediaRecorder - это вызовет onstop
                try {
                    // Важно: requestData() перед stop() гарантирует получение последнего чанка
                    if (recorder.state === 'recording') {
                        recorder.requestData();
                        console.log('[WebRTC] MediaRecorder.requestData() called before stop()');
                    }
                    recorder.stop();
                    console.log('[WebRTC] MediaRecorder.stop() called successfully, waiting for onstop event...');
                    
                    // Устанавливаем таймаут на случай, если onstop не вызовется
                    setTimeout(() => {
                        if (recordingChunks.length > 0 && recorder.state === 'inactive') {
                            console.warn('[WebRTC] Timeout: onstop did not fire, processing chunks manually...');
                            // Вызываем обработку вручную
                            if (onstopHandler && typeof onstopHandler === 'function') {
                                try {
                                    onstopHandler();
                                } catch (err) {
                                    console.error('[WebRTC] Error calling onstop handler manually:', err);
                                }
                            } else {
                                // Если обработчика нет, обрабатываем чанки напрямую
                                console.log('[WebRTC] Processing chunks without onstop handler...');
                                processRecordingChunks();
                            }
                        }
                    }, 1000); // Таймаут 1 секунда
                } catch (stopError) {
                    console.error('[WebRTC] Error calling recorder.stop():', stopError);
                    throw stopError;
                }
                
                // НЕ обнуляем mediaRecorder сразу - ждем onstop, который обнулит его сам
            } else {
                console.log('[WebRTC] MediaRecorder is already inactive or null, state:', recorder?.state);
                // Если recorder уже остановлен, но есть чанки - обрабатываем их
                if (recordingChunks.length > 0) {
                    console.log('[WebRTC] MediaRecorder already stopped but chunks exist, processing...');
                    // Вызываем обработку вручную
                    if (onstopHandler && typeof onstopHandler === 'function') {
                        try {
                            onstopHandler();
                        } catch (err) {
                            console.error('[WebRTC] Error calling onstop handler:', err);
                            processRecordingChunks();
                        }
                    } else {
                        processRecordingChunks();
                    }
                } else {
                    console.warn('[WebRTC] MediaRecorder stopped but no chunks collected');
                    sendEvent({ 
                        type: 'recording_stopped',
                        data: {
                            reason: 'no_chunks',
                            message: 'MediaRecorder stopped but no chunks were collected'
                        }
                    });
                }
            }
            
            // Останавливаем треки и закрываем AudioContext после остановки MediaRecorder
            if (recordingStream) {
                // Отключаем все источники от destination
                if (recordingStream._audioSources) {
                    recordingStream._audioSources.forEach((source, index) => {
                        try {
                            source.disconnect();
                            console.log(`[WebRTC] Disconnected audio source ${index}`);
                        } catch (err) {
                            console.error(`[WebRTC] Error disconnecting audio source ${index}:`, err);
                        }
                    });
                    delete recordingStream._audioSources;
                }
                
                recordingStream.getTracks().forEach(track => {
                    console.log('[WebRTC] Stopping track:', track.id, track.kind);
                    track.stop();
                });
                
                // Закрываем AudioContext если он был создан для микширования
                if (recordingStream._audioContext) {
                    const audioContext = recordingStream._audioContext;
                    if (audioContext.state !== 'closed') {
                        audioContext.close().then(() => {
                            console.log('[WebRTC] AudioContext closed successfully');
                        }).catch(err => {
                            console.error('[WebRTC] Error closing AudioContext:', err);
                        });
                    }
                    delete recordingStream._audioContext;
                }
                
                recordingStream = null;
            }
            
            console.log('[WebRTC] Recording stop requested, chunks collected:', recordingChunks.length);
        } catch (error) {
            console.error('[WebRTC] Error stopping recording:', error);
            console.error('[WebRTC] Error stack:', error.stack);
            sendEvent({ 
                type: 'error', 
                data: { 
                    name: 'RecordingError',
                    message: 'Failed to stop recording: ' + error.message,
                    phase: 'stopRecording',
                    stack: error.stack
                } 
            });
        }
    }
    
    // Вспомогательная функция для обработки чанков записи
    async function processRecordingChunks() {
        try {
            console.log('[WebRTC] processRecordingChunks called, chunks:', recordingChunks.length);
            
            if (recordingChunks.length === 0) {
                console.warn('[WebRTC] No recording chunks to process');
                sendEvent({ 
                    type: 'recording_stopped',
                    data: {
                        reason: 'no_chunks',
                        message: 'No chunks to process'
                    }
                });
                return;
            }
            
            // Создаем финальный Blob из всех чанков
            const finalBlob = new Blob(recordingChunks, { type: 'audio/webm;codecs=opus' });
            console.log(`[WebRTC] Final blob created: ${finalBlob.size} bytes from ${recordingChunks.length} chunks`);
            
            // Конвертируем Blob в ArrayBuffer
            const arrayBuffer = await finalBlob.arrayBuffer();
            const uint8Array = new Uint8Array(arrayBuffer);
            
            // Вычисляем SHA256 хеш для контроля целостности
            const hashBuffer = await crypto.subtle.digest('SHA-256', arrayBuffer);
            const hashArray = Array.from(new Uint8Array(hashBuffer));
            const hashHex = hashArray.map(b => b.toString(16).padStart(2, '0')).join('');
            
            console.log(`[WebRTC] File hash (SHA256): ${hashHex}`);
            
            // Для файлов больше 1MB разбиваем на куски по 512KB
            const CHUNK_SIZE = 512 * 1024; // 512KB
            const totalSize = uint8Array.length;
            
            if (totalSize > 1024 * 1024) {
                // Большой файл - отправляем по частям
                console.log(`[WebRTC] Large file (${totalSize} bytes), sending in chunks...`);
                
                const totalChunks = Math.ceil(totalSize / CHUNK_SIZE);
                let chunkIndex = 0;
                
                // Отправляем метаданные сначала
                sendEvent({
                    type: 'recording_start',
                    data: {
                        totalSize: totalSize,
                        totalChunks: totalChunks,
                        hash: hashHex,
                        mimeType: 'audio/webm;codecs=opus'
                    }
                });
                
                // Отправляем чанки
                for (let offset = 0; offset < totalSize; offset += CHUNK_SIZE) {
                    const chunk = uint8Array.subarray(offset, Math.min(offset + CHUNK_SIZE, totalSize));
                    // Конвертируем Uint8Array в обычный массив чисел для JSON
                    const chunkArray = Array.from(chunk);
                    
                    sendEvent({
                        type: 'recording_chunk',
                        data: {
                            chunkIndex: chunkIndex,
                            totalChunks: totalChunks,
                            data: chunkArray,
                            offset: offset
                        }
                    });
                    
                    chunkIndex++;
                }
                
                // Отправляем финальное событие
                sendEvent({
                    type: 'recording_complete',
                    data: {
                        hash: hashHex,
                        size: totalSize
                    }
                });
            } else {
                // Маленький файл - отправляем целиком как массив чисел
                console.log(`[WebRTC] Small file (${totalSize} bytes), sending as single array...`);
                const dataArray = Array.from(uint8Array);
                
                console.log(`[WebRTC] Array created: ${dataArray.length} elements, sending recording_complete...`);
                const recordingCompleteEvent = { 
                    type: 'recording_complete', 
                    data: { 
                        audioData: dataArray,
                        size: totalSize,
                        hash: hashHex,
                        mimeType: 'audio/webm;codecs=opus'
                    } 
                };
                console.log(`[WebRTC] Sending recording_complete event: size=${totalSize}, hash=${hashHex}, arrayLength=${dataArray.length}`);
                sendEvent(recordingCompleteEvent);
                console.log('[WebRTC] recording_complete event sent (small file)');
            }
            
            // Очищаем массив чанков и обнуляем mediaRecorder после успешной обработки
            recordingChunks = [];
            mediaRecorder = null;
            
            // Закрываем AudioContext если он еще не был закрыт
            if (recordingStream && recordingStream._audioContext) {
                const audioContext = recordingStream._audioContext;
                if (audioContext.state !== 'closed') {
                    audioContext.close().catch(err => {
                        console.error('[WebRTC] Error closing AudioContext in processRecordingChunks:', err);
                    });
                }
                delete recordingStream._audioContext;
            }
            recordingStream = null;
            
            console.log('[WebRTC] Recording processing completed successfully');
        } catch (error) {
            console.error('[WebRTC] Error processing recording chunks:', error);
            console.error('[WebRTC] Error stack:', error.stack);
            recordingChunks = [];
            mediaRecorder = null;
            
            // Закрываем AudioContext при ошибке
            if (recordingStream && recordingStream._audioContext) {
                const audioContext = recordingStream._audioContext;
                if (audioContext.state !== 'closed') {
                    audioContext.close().catch(err => {
                        console.error('[WebRTC] Error closing AudioContext on process error:', err);
                    });
                }
                delete recordingStream._audioContext;
            }
            recordingStream = null;
            sendEvent({ 
                type: 'error', 
                data: { 
                    name: 'RecordingError',
                    message: 'Failed to process recording chunks: ' + error.message,
                    phase: 'processChunks',
                    stack: error.stack
                } 
            });
        }
    }
    
    return {
        init,
        initUA,
        makeCall,
        answer,
        hangup,
        getStatus,
        stop,
        ping,
        pong,
        resetEngine,
        getStats,
        setMute,
        sendDtmf,
        enumerateAudioDevices,
        switchAudioDevice,
        startRecording,
        stopRecording
    };
})();

