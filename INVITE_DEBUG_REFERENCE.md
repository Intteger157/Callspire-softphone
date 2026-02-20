# Диагностика INVITE: логи и полный код

## Что добавлено в код (для доказательства «INVITE сформировался и ушёл по WSS»)

### 1) Логи JsSIP — «сырьё» SIP
- Все вызовы логгера JsSIP (log, warn, error, debug) дублируются в C# через `js_log` (вкладка Webrtc log).
- В консоли: `[WebRTC] [JsSIP LOG]`, `[JsSIP WARN]`, `[JsSIP ERROR]`, `[JsSIP DEBUG]`.
- Вокруг звонка смотрите все строки с префиксом `[JsSIP *]` и `[WebRTC] >>> ABOUT TO ua.call` / `<<< ua.call() returned`.

### 2) Перехват WSS (что реально ушло/пришло)
- После `new JsSIP.WebSocketInterface(cfg.wsUri)`:
  - **Исходящие**: перехвачен `socket.send` — в консоль и в C# пишется `[WSS OUT]` + первые 500 символов; при наличии `INVITE` или `SIP/2.0` — в js_log.
  - **Входящие**: в обёртке `socket.connect` после `origConnect()` к `socket._ws` вешается `message` — в консоль `[WSS IN ]`, ответы `SIP/2.0` в js_log.
- Интерпретация:
  - **Нет `[WSS OUT] INVITE`** — проблема на уровне JsSIP (создание offer/сессии).
  - **INVITE ушёл, но АТС не видит** — смотреть прокси/маршрут/WebSocket endpoint АТС.

### 3) Явные метки до/после ua.call()
- Перед вызовом: `[WebRTC] >>> ABOUT TO ua.call(target=...)` и событие js_log.
- После: `[WebRTC] <<< ua.call() returned sessionId=... HasRequest=...` и событие js_log.

---

## Полный код функций (без сокращений)

### initUA(cfg)

```javascript
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
            sendEvent({ type: 'error', data: { message: errorMsg, phase: 'initUA', config: { wsUri: !!cfg?.wsUri, sipUri: !!cfg?.sipUri, pass: !!cfg?.pass } } });
            return;
        }

        console.log('[WebRTC] Creating WebSocket interface to:', cfg.wsUri);
        try {
            const url = new URL(cfg.wsUri);
            const port = url.port || (url.protocol === 'wss:' ? '443' : (url.protocol === 'ws:' ? '80' : 'unknown'));
            console.log(`[WebRTC] WebSocket URI parsed: Host=${url.hostname}, Port=${port}, Path=${url.pathname}, Protocol=${url.protocol}`);
        } catch (e) {
            console.warn('[WebRTC] Could not parse WebSocket URI for port logging:', e.message);
        }
        const socket = new JsSIP.WebSocketInterface(cfg.wsUri);

        // ----- Временный перехват WSS -----
        try {
            if (typeof socket.send === 'function') {
                const origSend = socket.send.bind(socket);
                socket.send = function(data) {
                    const str = (typeof data === 'string') ? data : String(data);
                    console.log('[WSS OUT]', str.substring(0, 500) + (str.length > 500 ? '...' : ''));
                    if (str.includes('INVITE') || str.includes('SIP/2.0')) {
                        sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WSS OUT] ' + str.substring(0, 300) } });
                    }
                    return origSend(data);
                };
                console.log('[WebRTC] WSS hook: socket.send() patched for [WSS OUT]');
            }
            const origConnect = socket.connect.bind(socket);
            socket.connect = function() {
                origConnect();
                function attachIncoming() {
                    const ws = socket._ws;
                    if (ws && typeof ws.addEventListener === 'function') {
                        ws.addEventListener('message', function(e) {
                            const str = (e && e.data != null) ? String(e.data) : '';
                            console.log('[WSS IN ]', str.substring(0, 500) + (str.length > 500 ? '...' : ''));
                            if (str.indexOf('SIP/2.0') !== -1) {
                                sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WSS IN ] ' + str.substring(0, 300) } });
                            }
                        });
                        console.log('[WebRTC] WSS hook: [WSS IN] listener attached to _ws');
                        return true;
                    }
                    return false;
                }
                if (!attachIncoming()) {
                    setTimeout(function() { attachIncoming(); }, 100);
                    setTimeout(function() { attachIncoming(); }, 500);
                }
            };
            console.log('[WebRTC] WSS hook: socket.connect() patched for [WSS IN]');
        } catch (e) {
            console.warn('[WebRTC] WSS hook failed:', e);
            sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] WSS hook failed: ' + (e.message || e) } });
        }
        // ----- конец перехвата WSS -----

        console.log('[WebRTC] Creating JsSIP UA with SIP URI:', cfg.sipUri);
        ua = new JsSIP.UA({
            sockets: [socket],
            uri: cfg.sipUri,
            password: cfg.pass,
            session_timers: false,
            register: true,
            register_expires: 300,
            connection_recovery_min_interval: 2,
            connection_recovery_max_interval: 30,
            ice_servers: [{ urls: 'stun:stun.l.google.com:19302' }],
            log: {
                level: 'debug',
                logger: {
                    log: (...args) => {
                        const logMsg = args.join(' ');
                        console.log('[WebRTC] [JsSIP LOG]:', logMsg);
                        sendEvent({ type: 'js_log', data: { level: 'critical', message: '[JsSIP LOG] ' + logMsg } });
                    },
                    error: (...args) => {
                        const logMsg = args.join(' ');
                        console.error('[WebRTC] [JsSIP ERROR]:', logMsg);
                        sendEvent({ type: 'js_log', data: { level: 'critical', message: '[JsSIP ERROR] ' + logMsg } });
                    },
                    warn: (...args) => {
                        const logMsg = args.join(' ');
                        console.warn('[WebRTC] [JsSIP WARN]:', logMsg);
                        sendEvent({ type: 'js_log', data: { level: 'critical', message: '[JsSIP WARN] ' + logMsg } });
                    },
                    debug: (...args) => {
                        const logMsg = args.join(' ');
                        console.log('[WebRTC] [JsSIP DEBUG]:', logMsg);
                        sendEvent({ type: 'js_log', data: { level: 'critical', message: '[JsSIP DEBUG] ' + logMsg } });
                    }
                }
            }
        });
        console.log('[WebRTC] JsSIP UA created successfully');

        ua.on('connected', () => {
            console.log('[WebRTC] ✓ WebSocket connected to ATS server');
            sendEvent({ type: 'ws_connected' });
        });
        ua.on('disconnected', (e) => {
            console.log('[WebRTC] ✗ WebSocket disconnected from ATS server', e);
            sendEvent({ type: 'ws_disconnected', data: { code: e?.code, reason: e?.reason, message: e?.message, cause: e?.cause } });
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
            sendEvent({ type: 'reg_failed', data: e.cause + ': ' + (e.message || 'Registration failed') });
        });

        ua.on('newRTCSession', (e) => {
            console.log('[WebRTC] ⚠️⚠️⚠️ NEW RTC SESSION EVENT ⚠️⚠️⚠️');
            console.log('[WebRTC] Originator:', e.originator);
            console.log('[WebRTC] Session ID:', e.session?.id || 'N/A');
            console.log('[WebRTC] Session request:', e.session?.request ? {
                method: e.session.request.method,
                ruri: e.session.request.ruri?.toString(),
                callId: e.session.request.call_id
            } : 'N/A');
            const sessionId = e.session.id || e.session.request?.call_id || `session_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
            e.session._softphoneSessionId = sessionId;
            wireSessionEvents(e.session, e.originator);
            if (e.originator === 'remote') {
                const callerNumber = e.session.remote_identity ? e.session.remote_identity.uri.user : 'Unknown';
                window._incomingSession = e.session;
                sendEvent({ type: 'incoming', data: { sessionId: sessionId, callerNumber: callerNumber } });
            } else {
                console.log('[WebRTC] ⚠️⚠️⚠️ OUTGOING SESSION DETECTED IN newRTCSession ⚠️⚠️⚠️');
                sendEvent({
                    type: 'js_log',
                    data: { level: 'critical', message: `[WebRTC] OUTGOING SESSION IN newRTCSession SessionID=${sessionId}, HasRequest=${!!(e.session?.request)}` }
                });
            }
        });

        console.log('[WebRTC] Starting JsSIP UA...');
        ua.start();
        console.log('[WebRTC] JsSIP UA started (without media)');
        sendEvent({ type: 'ua_started' });
    } catch (error) {
        console.error('[WebRTC] ✗ ERROR during UA initialization:', error.message, error.stack);
        sendEvent({ type: 'error', data: { message: error.message, stack: error.stack } });
    }
}
```

### makeCall(number)

```javascript
async function makeCall(number) {
    try {
        console.log('[WebRTC] makeCall called with number:', number);
        sendEvent({ type: 'makeCall_started', data: { number: number } });

        if (!ua) {
            const errorMsg = 'UA not initialized';
            console.error('[WebRTC] makeCall:', errorMsg);
            sendEvent({ type: 'error', data: { name: 'UANotInitialized', message: errorMsg, phase: 'makeCall', stack: new Error().stack } });
            return;
        }

        console.log('[WebRTC] makeCall: Cleaning up any existing sessions before new call');
        const sessionsToCleanup = [];
        if (session) sessionsToCleanup.push(session);
        if (window._activeSession) sessionsToCleanup.push(window._activeSession);
        if (window._incomingSession) sessionsToCleanup.push(window._incomingSession);
        if (ua && ua.sessions) {
            ua.sessions.forEach((s) => {
                if (s && !sessionsToCleanup.includes(s)) sessionsToCleanup.push(s);
            });
        }
        sessionsToCleanup.forEach((s) => {
            try {
                if (s && typeof s.terminate === 'function') {
                    console.log('[WebRTC] makeCall: Terminating old session:', s.id);
                    s.terminate();
                }
            } catch (e) {
                console.warn('[WebRTC] makeCall: Error terminating old session:', e);
            }
        });
        session = null;
        window._activeSession = null;
        window._incomingSession = null;
        console.log('[WebRTC] makeCall: Old sessions cleaned up, proceeding with new call');

        const cleanNumber = String(number).trim();
        const domain = ua.configuration.uri.host || ua.configuration.uri.toString().split('@')[1];
        const target = cleanNumber.includes('@') ? (cleanNumber.startsWith('sip:') ? cleanNumber : `sip:${cleanNumber}`) : `sip:${cleanNumber}@${domain}`;
        console.log('[WebRTC] makeCall: Calling target:', target);

        const iceServers = [
            { urls: 'stun:stun.l.google.com:19302' },
            { urls: 'stun:stun1.l.google.com:19302' }
        ];
        const options = {
            mediaConstraints: { ...mediaConstraints, video: false },
            pcConfig: { iceServers: iceServers },
            rtcOfferConstraints: { offerToReceiveAudio: true, offerToReceiveVideo: false },
            eventHandlers: {
                sending: (e) => {
                    console.log('[WebRTC] ✅ SIP INVITE SENT! Call-ID:', e.request?.call_id);
                    sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] ✅ SIP INVITE SENT! Request sent to network.' } });
                },
                progress: () => { console.log('[WebRTC] Call is in progress (ringing)'); },
                failed: (e) => { console.error('[WebRTC] Call failed (eventHandlers):', e?.cause); },
                confirmed: () => { console.log('[WebRTC] Call confirmed (answered)'); }
            }
        };

        console.log('[WebRTC] >>> ABOUT TO ua.call(target=', target, ', options keys:', Object.keys(options), ')');
        sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] >>> ABOUT TO ua.call(target=' + target + ')' } });
        session = ua.call(target, options);
        console.log('[WebRTC] <<< ua.call() returned sessionId=', session ? session.id : 'null', ' HasRequest=', session ? !!(session.request) : 'N/A');
        sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] <<< ua.call() returned sessionId=' + (session ? session.id : 'null') + ' HasRequest=' + (session ? !!session.request : 'N/A') } });

        if (!session) {
            console.error('[WebRTC] ua.call() returned null session');
            sendEvent({ type: 'error', data: { name: 'SessionNull', message: 'ua.call() failed to create session', phase: 'makeCall' } });
            return;
        }

        window._activeSession = session;
        wireSessionEvents(session, 'local');
        sendEvent({ type: 'makeCall_initiated', data: { sessionId: session.id, targetUri: target } });
    } catch (error) {
        console.error('[WebRTC] makeCall error:', error);
        sendEvent({ type: 'error', data: { name: error.name || 'UnknownError', message: error.message, phase: 'makeCall', stack: error.stack } });
    }
}
```

### cleanupSessions()

```javascript
function cleanupSessions() {
    try {
        console.log('[WebRTC] cleanupSessions: Cleaning up all sessions');
        const sessionsToCleanup = [];
        if (session) sessionsToCleanup.push(session);
        if (window._activeSession) sessionsToCleanup.push(window._activeSession);
        if (window._incomingSession) sessionsToCleanup.push(window._incomingSession);
        if (ua && ua.sessions) {
            ua.sessions.forEach((s) => {
                if (s && !sessionsToCleanup.includes(s)) sessionsToCleanup.push(s);
            });
        }
        sessionsToCleanup.forEach((s) => {
            try {
                if (s && typeof s.terminate === 'function') {
                    console.log('[WebRTC] cleanupSessions: Terminating session:', s.id);
                    s.terminate();
                }
            } catch (e) {
                console.warn('[WebRTC] cleanupSessions: Error terminating session:', e);
            }
        });
        session = null;
        window._activeSession = null;
        window._incomingSession = null;
        console.log('[WebRTC] cleanupSessions: All sessions cleaned up');
        return true;
    } catch (error) {
        console.error('[WebRTC] cleanupSessions error:', error);
        return false;
    }
}
```

---

## Вызов makeCall из C#

1. **WebRtcService.MakeCallAsync** (WebRtcService.cs) формирует команду и отправляет её в движок:

```csharp
MainWindow.Log($"[WebRtcService] MakeCallAsync: Sending makeCall command for number {number}");
if (_engine != null)
{
    try
    {
        await _engine.SendAsync(new { cmd = "makeCall", number });
        MainWindow.Log($"[WebRtcService] MakeCallAsync: Command sent successfully");
    }
    catch (Exception ex) { ... }
}
```

2. **WebRtcEngineHost.SendAsync** (WebRtcService.cs, switch по командам) собирает скрипт и выполняет его в WebView2:

```csharp
case "makeCall":
    string? number = ...; // из command
    MainWindow.Log($"[WebRtcEngineHost] SendAsync: Sending makeCall command for number '{number}'");
    script = $"window.SoftphoneWebRtc.cleanupSessions(); window.SoftphoneWebRtc.makeCall('{number}');";
    MainWindow.Log($"[WebRtcEngineHost] SendAsync: Script prepared: {script}");
    break;
```

3. **Выполнение скрипта** — через `_webView.CoreWebView2.ExecuteScriptAsync(script)` (в том же файле, метод SendAsync). То есть в одном вызове выполняются подряд:
   - `window.SoftphoneWebRtc.cleanupSessions();`
   - `window.SoftphoneWebRtc.makeCall('номер');`

Итог: сначала очищаются сессии, затем делается один вызов `makeCall` с переданным номером. В логах смотрите `[WSS OUT]`, `[JsSIP *]`, `>>> ABOUT TO ua.call` и `<<< ua.call() returned` — по ним видно, сформировался ли INVITE и ушёл ли он по WSS.
