// WebRTC Softphone модуль для JsSIP
window.SoftphoneWebRtc = (function () {
    'use strict';

    let ua = null;
    let session = null;
    let mediaRecorder = null;
    let recordingStream = null;
    let _jssipLoadPromise = null;
    let _wssKeepaliveTimer = null;
    let _wssKeepaliveSocket = null;
    let _wssKeepaliveTargetUri = null;
    // nginx/proxy idle timeouts on WSS are often 60–90s; keep traffic below that interval.
    const WSS_KEEPALIVE_INTERVAL_MS = 30000;

    function stopWssKeepalive() {
        if (_wssKeepaliveTimer) {
            clearInterval(_wssKeepaliveTimer);
            _wssKeepaliveTimer = null;
        }
        _wssKeepaliveSocket = null;
        _wssKeepaliveTargetUri = null;
    }

    function sendWssKeepaliveTick() {
        try {
            if (!ua || typeof ua.isConnected !== 'function' || !ua.isConnected()) {
                return;
            }

            // Asterisk/Miko WSS transport accepts CRLF keepalive frames (WebSocket text).
            // Browser WebSocket API cannot send RFC6455 ping frames from JS.
            if (_wssKeepaliveSocket && typeof _wssKeepaliveSocket.send === 'function') {
                _wssKeepaliveSocket.send('\r\n\r\n');
            }

            // SIP OPTIONS keeps the WSS path and NAT bindings alive end-to-end.
            if (_wssKeepaliveTargetUri && ua && typeof ua.sendOptions === 'function') {
                ua.sendOptions(_wssKeepaliveTargetUri);
            }
        } catch (e) {
            console.warn('[WebRTC] WSS keepalive failed:', e?.message || e);
        }
    }

    function startWssKeepalive(socket, sipUri) {
        stopWssKeepalive();
        if (!socket || !sipUri) return;

        _wssKeepaliveSocket = socket;
        _wssKeepaliveTargetUri = sipUri;

        // First tick soon after connect so idle proxies never reach their 90s cutoff.
        setTimeout(sendWssKeepaliveTick, 5000);
        _wssKeepaliveTimer = setInterval(sendWssKeepaliveTick, WSS_KEEPALIVE_INTERVAL_MS);
        console.log(`[WebRTC] WSS keepalive started (interval=${WSS_KEEPALIVE_INTERVAL_MS}ms, target=${sipUri})`);
    }

    // Connection slot identifier ("main" | "secondary"). Read from URL query string
    // so that each iframe (one per slot) carries its own slot label.
    // Default to "main" for backward compatibility with single-slot pages.
    const _slot = (function () {
        try {
            return new URLSearchParams(window.location.search).get('slot') || 'main';
        } catch (e) {
            return 'main';
        }
    })();

    // When running inside an iframe, WebView2's CoreWebView2.WebMessageReceived event
    // ONLY fires for messages posted from the top-level frame. Messages from iframes
    // go to a separate WebMessageReceivedFromFrame event that the host doesn't subscribe to.
    //
    // To keep a single subscription point on the host side, we relay every message to the
    // parent window (index.html), which forwards it via its own chrome.webview.postMessage.
    // For the standalone test page (slot=test, no parent), we fall back to direct posting.
    const _inIframe = (function () { try { return window.parent && window.parent !== window; } catch (e) { return false; } })();
    // Host bridges:
    //   • WebView2 (Windows WPF):        window.chrome.webview.postMessage(str)
    //   • Avalonia NativeWebView (macOS WKWebView / Linux WebKit / Windows WebView2 via Avalonia):
    //                                     invokeCSharpAction(str)  (injected by Avalonia.Controls.WebView)
    function hasDirectHostBridge() {
        try {
            return !!((window.chrome && window.chrome.webview) || typeof window.invokeCSharpAction === 'function');
        } catch (e) { return false; }
    }
    function postToHostDirect(jsonStr) {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(jsonStr);
            return true;
        }
        if (typeof window.invokeCSharpAction === 'function') {
            window.invokeCSharpAction(jsonStr);
            return true;
        }
        return false;
    }
    function postToHost(jsonStr) {
        try {
            if (_inIframe) {
                // Relay through the container; same-origin so '*' is fine.
                window.parent.postMessage({ __toHost: true, payload: jsonStr }, '*');
                return true;
            }
            return postToHostDirect(jsonStr);
        } catch (e) { /* swallow — never break SIP flow because of logging */ }
        return false;
    }

    function loadJsSIPOnce() {
        if (window.JsSIP) return Promise.resolve(true);
        if (_jssipLoadPromise) return _jssipLoadPromise;

        const tryLoad = (src) => new Promise((resolve, reject) => {
            const s = document.createElement('script');
            s.src = src;
            s.async = true;
            s.onload = () => resolve(true);
            s.onerror = () => reject(new Error(`Failed to load ${src}`));
            document.head.appendChild(s);
        });

        _jssipLoadPromise = (async () => {
            const sources = [
                // Bundled with WebRtcClient (served from softphone.local); works offline / no CDN.
                'jssip.min.js',
                'https://cdn.jsdelivr.net/npm/jssip@3.10.0/dist/jssip.min.js',
                'https://unpkg.com/jssip@3.10.0/dist/jssip.min.js'
            ];
            for (const src of sources) {
                try {
                    console.log('[WebRTC] Loading JsSIP from:', src);
                    await tryLoad(src);
                    if (window.JsSIP) return true;
                } catch (e) {
                    console.warn('[WebRTC] JsSIP load failed from', src, e);
                }
            }
            return false;
        })();

        return _jssipLoadPromise;
    }

    // Настройки медиа для WebRTC (best practices для эхоподавления)
    // Используем ideal вместо exact для лучшей совместимости с разными браузерами
    const mediaConstraints = {
        audio: {
            echoCancellation: { ideal: true },      // Акустическое эхоподавление (критично для VoIP)
            noiseSuppression: { ideal: true },      // Подавление шумов
            autoGainControl: { ideal: true },       // Автоматическая регулировка усиления
            channelCount: { ideal: 1 },            // Моно канал (стандарт для VoIP)
            sampleRate: { ideal: 48000 },          // Высокая частота дискретизации для лучшего качества
            latency: { ideal: 0.01, max: 0.05 }    // Низкая задержка для реального времени
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

    // Redact secrets from SIP messages before logging/sending to host logs.
    // INVITE/REGISTER may contain Authorization / Proxy-Authorization headers.
    //
    // Only the `response="..."` Digest param is sensitive (it's the MD5/SHA hash of the
    // password mixed with nonce). Everything else (username, realm, nonce, uri, qop, etc.)
    // is publicly observable on the wire and is critical for diagnosing auth failures
    // (e.g. "is JsSIP sending the right username?", "does realm match PBX config?").
    // WWW-Authenticate carries the challenge — no secret there at all.
    function redactSipForLog(raw) {
        try {
            if (!raw || typeof raw !== 'string') return raw;
            const lines = raw.split(/\r?\n/);
            const out = [];
            for (const line of lines) {
                const l = String(line);
                if (/^\s*Authorization\s*:/i.test(l) || /^\s*Proxy-Authorization\s*:/i.test(l)) {
                    // Mask only the response= digest hash, keep everything else visible.
                    out.push(l.replace(/response\s*=\s*"[^"]*"/i, 'response="***REDACTED***"'));
                    continue;
                }
                // WWW-Authenticate carries the server challenge (realm, nonce, qop, ...) — no secret.
                out.push(l);
            }
            return out.join('\r\n');
        } catch {
            return raw;
        }
    }

    // Redact sensitive SDP before sending it to C# logs.
    // SDP may contain ICE ufrag/pwd and private IP candidates.
    function redactSdpForLog(rawSdp) {
        try {
            if (!rawSdp || typeof rawSdp !== 'string') return rawSdp;
            let s = rawSdp.replace(/^a=ice-ufrag:.*$/gmi, 'a=ice-ufrag:***REDACTED***');
            s = s.replace(/^a=ice-pwd:.*$/gmi, 'a=ice-pwd:***REDACTED***');
            // Mask IP address in "a=candidate:" lines (best-effort).
            s = s.replace(/^(a=candidate:[^\r\n]*\s)(\d{1,3}(?:\.\d{1,3}){3})(\s\d+\s typ\s\w+[^\r\n]*)$/gmi, '$1***.***.***.***$3');
            return s;
        } catch {
            return rawSdp;
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

            // Tag every outgoing event with the connection slot so the C# router
            // can demultiplex between WebRtcService.Main and WebRtcService.Secondary.
            // Inner code can override by setting safeObj.slot explicitly (rare).
            if (safeObj && typeof safeObj === 'object' && safeObj.slot == null) {
                safeObj.slot = _slot;
            }

            // ДЕТАЛЬНОЕ ЛОГИРОВАНИЕ для критичных событий
            if (safeObj.type === 'call_accepted' || safeObj.type === 'audio_connected' || safeObj.type === 'audio_playing') {
                console.log(`[WebRTC] ⚠️⚠️⚠️ SENDING CRITICAL EVENT: ${safeObj.type} ⚠️⚠️⚠️`);
                console.log(`[WebRTC] Event data:`, JSON.stringify(safeObj, null, 2));
                
                // КРИТИЧНО: Отправляем также как отдельное событие лога для гарантированного попадания в C#
                try {
                    const logEvent = {
                        type: 'js_log',
                        slot: _slot,
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ⚠️⚠️⚠️ CRITICAL EVENT: ${safeObj.type} ⚠️⚠️⚠️`,
                            eventType: safeObj.type,
                            sessionId: safeObj.data?.sessionId || 'none',
                            timestamp: Date.now()
                        }
                    };
                    postToHost(JSON.stringify(logEvent));
                } catch (logErr) {
                    console.error('[WebRTC] Error sending log event:', logErr);
                }
            }

            const jsonStr = JSON.stringify(safeObj);
            const delivered = postToHost(jsonStr);
            if (delivered) {
                // Подтверждение отправки для критичных событий
                if (safeObj.type === 'call_accepted' || safeObj.type === 'audio_connected' || safeObj.type === 'audio_playing') {
                    console.log(`[WebRTC] ✅ Event ${safeObj.type} sent via postMessage (${jsonStr.length} bytes)`);
                }
            } else {
                console.warn('[WebRTC] ⚠️ chrome.webview.postMessage not available!');
                console.warn('[WebRTC] ⚠️ Events will NOT reach C#!');
                console.log('WebRTC Event:', safeObj);
            }
        } catch (e) {
            console.error('[WebRTC] ❌ ERROR sending event:', e);
            console.error('[WebRTC] ❌ Event that failed:', obj);
        }
    }

    // Глобальные переменные для сессий
    window._incomingSession = null;
    window._activeSession = null;
    window._callspireOriginatePending = false;
    window._callspireOriginateAcceptedSessionId = null;
    window._callspireAllowedCallerIds = [];

    function setOriginatePending(pending) {
        window._callspireOriginatePending = !!pending;
        if (!pending) {
            window._callspireOriginateAcceptedSessionId = null;
        }
        if (pending) {
            prewarmTurn('originate');
        }
    }

    // Relay-only режим включается, только если пользователь явно настроил TURN.
    function isRelayOnlyMode(cfgOverride) {
        const cfg = cfgOverride || window._softphoneWebRtcCfg || {};
        return !!(cfg.turnServer && typeof cfg.turnServer === 'string' && cfg.turnServer.trim() !== '');
    }

    // Единый источник ICE-конфигурации для initUA / makeCall / answer.
    // В relay-only режиме публичные STUN'ы не могут дать пригодных кандидатов,
    // но добавляют DNS-резолвы и ошибки 701 в фазу сбора кандидатов.
    function buildIceConfig(cfgOverride) {
        const cfg = cfgOverride || window._softphoneWebRtcCfg || {};
        const iceServers = [];
        let turnUrl = null;

        if (isRelayOnlyMode(cfg)) {
            turnUrl = cfg.turnServer.trim();
            if (!/^turns?:/i.test(turnUrl)) {
                turnUrl = 'turn:' + turnUrl;
            }
            const turnEntry = { urls: turnUrl };
            if (cfg.turnUsername && typeof cfg.turnUsername === 'string' && cfg.turnUsername.trim() !== '') {
                turnEntry.username = cfg.turnUsername.trim();
            }
            if (cfg.turnPassword && typeof cfg.turnPassword === 'string' && cfg.turnPassword.trim() !== '') {
                turnEntry.credential = cfg.turnPassword.trim();
            }
            iceServers.push(turnEntry);
        } else {
            iceServers.push(
                { urls: 'stun:stun.l.google.com:19302' },
                { urls: 'stun:stun1.l.google.com:19302' }
            );
        }

        const forceRelay = !!turnUrl;
        const pcConfig = forceRelay
            ? { iceServers: iceServers, iceTransportPolicy: 'relay', iceCandidatePoolSize: 1 }
            : { iceServers: iceServers };

        return { iceServers: iceServers, pcConfig: pcConfig, forceRelay: forceRelay, turnUrl: turnUrl };
    }

    let _turnPrewarmInFlight = false;
    let _turnPrewarmTimer = null;
    // coturn сбрасывает простаивающие allocation'ы примерно через 10 минут.
    const TURN_PREWARM_INTERVAL_MS = 240000;

    // Первый звонок после холодного старта падал: TURN allocation (DNS + Allocate) не успевал
    // за окно сбора ICE, и answer уходил вообще без кандидатов. Прогрев делает первый
    // click2call таким же быстрым, как повторный.
    function prewarmTurn(reason) {
        if (!isRelayOnlyMode()) return;
        if (_turnPrewarmInFlight) return;

        const iceConfig = buildIceConfig();
        let pc = null;
        let done = false;
        _turnPrewarmInFlight = true;

        const finish = (ok, detail) => {
            if (done) return;
            done = true;
            _turnPrewarmInFlight = false;
            try { if (pc) pc.close(); } catch (e) { /* ignore */ }
            pc = null;
            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] TURN prewarm (${reason}): ${ok ? 'relay OK' : 'no relay'} — ${detail}, url=${iceConfig.turnUrl}`
                }
            });
        };

        try {
            pc = new RTCPeerConnection(iceConfig.pcConfig);
            pc.createDataChannel('callspire-turn-prewarm');
            pc.onicecandidate = (e) => {
                const candidate = e.candidate && e.candidate.candidate;
                if (candidate && candidate.indexOf(' typ relay') !== -1) {
                    finish(true, 'relay candidate gathered');
                }
            };
            pc.createOffer()
                .then((offer) => pc.setLocalDescription(offer))
                .catch((err) => finish(false, 'offer failed: ' + (err && err.message ? err.message : err)));
            setTimeout(() => finish(false, 'timeout'), 8000);
        } catch (err) {
            finish(false, 'exception: ' + (err && err.message ? err.message : err));
        }
    }

    function startTurnPrewarmLoop() {
        if (_turnPrewarmTimer) return;
        if (!isRelayOnlyMode()) return;

        prewarmTurn('startup');
        _turnPrewarmTimer = setInterval(() => {
            if (window._activeSession || window._incomingSession) return;
            prewarmTurn('keepalive');
        }, TURN_PREWARM_INTERVAL_MS);
    }

    function stopTurnPrewarmLoop() {
        if (_turnPrewarmTimer) {
            clearInterval(_turnPrewarmTimer);
            _turnPrewarmTimer = null;
        }
    }

    function setOriginateAcceptedSessionId(sessionId) {
        window._callspireOriginateAcceptedSessionId = sessionId || null;
        window._callspireOriginatePending = false;
    }

    function setAllowedCallerIds(ids) {
        window._callspireAllowedCallerIds = Array.isArray(ids)
            ? ids.map((id) => String(id || '').trim()).filter(Boolean)
            : [];
    }

    function normalizePhoneDigits(value) {
        return String(value || '').replace(/\D/g, '');
    }

    function phoneNumbersLooselyMatch(a, b) {
        const da = normalizePhoneDigits(a);
        const db = normalizePhoneDigits(b);
        if (!da || !db) return false;
        if (da.length >= 7 && db.length >= 7 && da === db) return true;
        return String(a || '').trim().toLowerCase() === String(b || '').trim().toLowerCase();
    }

    function isOwnOutboundCallerId(number) {
        const ids = window._callspireAllowedCallerIds || [];
        if (!number || !ids.length) return false;
        return ids.some((id) => phoneNumbersLooselyMatch(number, id));
    }

    function extractOriginateId(session) {
        try {
            const hdr = session?.request?.getHeader?.('X-Callspire-Originate');
            return hdr ? String(hdr).trim() : null;
        } catch {
            return null;
        }
    }

    function isDesktopWebViewHost() {
        try {
            // Inside the desktop container (index.html) every slot iframe is hosted by C#;
            // the container itself exposes either bridge. Standalone web SPA has neither.
            if (hasDirectHostBridge()) return true;
            if (_inIframe) {
                try { return !!(window.parent && window.parent.__softphoneDesktopHost === true); } catch (e) { /* cross-origin */ }
            }
            return false;
        } catch {
            return false;
        }
    }

    /** PBX forks originate callback INVITE to every -WS binding; reject when another client originated. */
    function isForeignOriginateCallback(originateId, callerNumber) {
        if (window._callspireOriginatePending) return false;
        if (originateId) return true;
        if (isOwnOutboundCallerId(callerNumber)) return true;
        return false;
    }

    async function loadAllowedCallerIds(cfg) {
        if ((window._callspireAllowedCallerIds || []).length > 0) return;
        if (Array.isArray(cfg?.allowedCallerIds) && cfg.allowedCallerIds.length > 0) {
            setAllowedCallerIds(cfg.allowedCallerIds);
            return;
        }
        const urls = [];
        if (cfg?.callerIdsUrl) urls.push(cfg.callerIdsUrl);
        if (!isDesktopWebViewHost()) urls.push('/softphone/api/my-callerids');
        for (const url of urls) {
            try {
                const response = await fetch(url, { credentials: 'include' });
                if (!response.ok) continue;
                const data = await response.json();
                const ids = data.callerids
                    || (Array.isArray(data.callerid_items)
                        ? data.callerid_items.map((item) => item?.number).filter(Boolean)
                        : []);
                if (ids.length > 0) {
                    setAllowedCallerIds(ids);
                    console.log('[WebRTC] Loaded allowed caller IDs:', ids.length);
                    return;
                }
            } catch (ex) {
                console.warn('[WebRTC] loadAllowedCallerIds failed:', url, ex);
            }
        }
    }

    function rejectIncomingSession(session, sessionId, reason) {
        console.log('[WebRTC]', reason, sessionId);
        sendEvent({
            type: 'js_log',
            data: { level: 'warn', message: `[WebRTC] ${reason} session=${sessionId}` }
        });
        try {
            session.terminate({ status_code: 486, reason_phrase: 'Busy Here' });
        } catch (ex) {
            console.warn('[WebRTC] rejectIncomingSession terminate failed:', ex);
        }
    }

    function normalizeExtUser(user) {
        if (!user) return '';
        return String(user).replace(/-WS$/i, '');
    }

    /** Extract sip:user from a JsSIP URI / NameAddr object, string, or nested _uri. */
    function sipUserFromUri(uriLike) {
        if (!uriLike) return '';
        if (typeof uriLike === 'string') {
            const m = uriLike.match(/sip:([^@;>]+)/i);
            return m ? m[1] : '';
        }
        const nested = uriLike._uri || uriLike.uri;
        if (nested && nested !== uriLike) {
            const fromNested = sipUserFromUri(nested);
            if (fromNested) return fromNested;
        }
        const direct = uriLike.user || uriLike._user;
        if (direct) return String(direct);
        const raw = typeof uriLike.toString === 'function' ? String(uriLike.toString()) : '';
        const m = raw.match(/sip:([^@;>]+)/i);
        return m ? m[1] : '';
    }

    function sipUserFromRawRequest(req) {
        try {
            const raw = typeof req?.toString === 'function' ? String(req.toString()) : '';
            if (!raw) return '';
            const inviteLine = raw.match(/^INVITE\s+sip:([^@\s;>]+)/im);
            if (inviteLine) return inviteLine[1];
            const toHdr = raw.match(/^To:\s*(?:[^<]*<)?sip:([^@;>]+)/im);
            if (toHdr) return toHdr[1];
        } catch {
            /* ignore */
        }
        return '';
    }

    function getRegisteredContactUser() {
        try {
            const uri = ua?.contact?.uri;
            const user = sipUserFromUri(uri);
            if (user) return user;
            const raw = typeof ua?.contact?.toString === 'function' ? ua.contact.toString() : '';
            return sipUserFromUri(raw) || null;
        } catch {
            return null;
        }
    }

    function getRegisteredAorUser() {
        try {
            return sipUserFromUri(ua?.configuration?.uri) || null;
        } catch {
            return null;
        }
    }

    /** Request-URI / To user the PBX is trying to reach (JsSIP ruri.user is often undefined). */
    function getInviteTargetUser(session) {
        const req = session?.request;
        if (!req) return '';
        return (
            sipUserFromUri(req.ruri) ||
            sipUserFromUri(req.uri) ||
            sipUserFromUri(req.to) ||
            sipUserFromUri(req.to?.uri) ||
            sipUserFromRawRequest(req) ||
            ''
        );
    }

    function isInviteForCurrentContact(session) {
        try {
            const originatePending = !!window._callspireOriginatePending;
            const inviteRaw = getInviteTargetUser(session);
            const inviteUser = normalizeExtUser(inviteRaw);
            const contactUser = getRegisteredContactUser() || '';
            const aorUser = normalizeExtUser(getRegisteredAorUser());

            const matchesUs = () => {
                if (inviteRaw && contactUser && inviteRaw === contactUser) return true;
                if (inviteUser && aorUser && inviteUser === aorUser) return true;
                return false;
            };

            // Originate callback: accept when unsure (C# OriginateCoordinator dedupes stale legs).
            if (originatePending) {
                if (!inviteRaw) return true;
                return matchesUs() || !contactUser;
            }

            if (matchesUs()) return true;

            // JsSIP may fire newRTCSession before RURI/To are parsed — accept rather than auto-reject.
            if (!inviteRaw) return true;

            // Explicit target for another binding (old WebRTC contact after re-register).
            return false;
        } catch (ex) {
            console.warn('[WebRTC] isInviteForCurrentContact error:', ex);
            return true;
        }
    }

    // Фаза 2: Инициализация JsSIP UA без медиа (prewarm)
    async function initUA(cfg) {
        try {
            void loadAllowedCallerIds(cfg);
            // Сохраняем текущую конфигурацию, чтобы исходящий makeCall мог также использовать
            // пользовательский TURN (если задан) из C# настроек.
            window._softphoneWebRtcCfg = cfg;
            if (!window.JsSIP) {
                console.warn('[WebRTC] JsSIP is not defined, attempting to load dynamically...');
                const loaded = await loadJsSIPOnce();
                if (!loaded || !window.JsSIP) {
                    const errMsg = 'JsSIP is not defined (failed to load library)';
                    console.error('[WebRTC] ' + errMsg);
                    sendEvent({ type: 'error', data: { message: errMsg, phase: 'initUA' } });
                    return;
                }
                console.log('[WebRTC] JsSIP loaded dynamically, retrying initUA...');
            }
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
            stopWssKeepalive();
            stopTurnPrewarmLoop();
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
            // Логируем порт для диагностики
            try {
                const url = new URL(cfg.wsUri);
                const port = url.port || (url.protocol === 'wss:' ? '443' : (url.protocol === 'ws:' ? '80' : 'unknown'));
                console.log(`[WebRTC] WebSocket URI parsed: Host=${url.hostname}, Port=${port}, Path=${url.pathname}, Protocol=${url.protocol}`);
            } catch (e) {
                console.warn('[WebRTC] Could not parse WebSocket URI for port logging:', e.message);
            }
            const socket = new JsSIP.WebSocketInterface(cfg.wsUri);

            // ----- Временный перехват WSS: что реально уходит/приходит по WebSocket (для диагностики INVITE) -----
            try {
                if (typeof socket.send === 'function') {
                    const origSend = socket.send.bind(socket);
                    socket.send = function(data) {
                        const str = (typeof data === 'string') ? data : String(data);
                        // Логируем только важные SIP сообщения (не REGISTER - это периодические сообщения регистрации)
                        const isImportantMessage = str.includes('INVITE') || str.includes('BYE') || str.includes('CANCEL') || 
                                                   str.includes('ACK') || str.includes('UPDATE') || str.includes('REFER') ||
                                                   (str.includes('SIP/2.0') && !str.includes('REGISTER'));
                        if (isImportantMessage) {
                            const safe = redactSipForLog(str);
                            console.log('[WSS OUT]', safe.substring(0, 1500) + (safe.length > 1500 ? '...' : ''));
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WSS OUT] ' + safe.substring(0, 1500) } });
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
                                // Логируем только важные SIP сообщения (не REGISTER - это периодические сообщения регистрации)
                                const isImportantMessage = str.includes('INVITE') || str.includes('BYE') || str.includes('CANCEL') || 
                                                           str.includes('ACK') || str.includes('UPDATE') || str.includes('REFER') ||
                                                           (str.indexOf('SIP/2.0') !== -1 && str.indexOf('REGISTER') === -1);
                                if (isImportantMessage) {
                                    const safe = redactSipForLog(str);
                                    console.log('[WSS IN ]', safe.substring(0, 1500) + (safe.length > 1500 ? '...' : ''));
                                    sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WSS IN ] ' + safe.substring(0, 1500) } });
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

            // Формируем список ICE‑серверов.
            // ВАЖНО: если пользовательский TURN не задан, НЕ подставляем публичные TURN'ы по умолчанию.
            // В некоторых сетях они не резолвятся/блокируются и могут ломать ICE (0 кандидатов).
            // Relay-only включаем ТОЛЬКО если пользователь явно настроил TURN. Иначе остаёмся
            // в STUN-only режиме, чтобы звонки не "падали" при недоступном публичном TURN.
            const initIceConfig = buildIceConfig(cfg);
            const iceServers = initIceConfig.iceServers;
            const forceRelay = initIceConfig.forceRelay;
            const pcConfig = initIceConfig.pcConfig;

            if (initIceConfig.turnUrl) {
                console.log('[WebRTC] Using custom TURN server from config:', initIceConfig.turnUrl);
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] TURN config in initUA: url=${initIceConfig.turnUrl}, hasUser=${!!iceServers[0].username}, hasPass=${!!iceServers[0].credential}`
                    }
                });
            }

            console.log('[WebRTC] ICE servers:', iceServers);

            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] pcConfig: forceRelay=${forceRelay}, iceServers=${iceServers.map(s => s.urls).join(', ')}`
                }
            });

            console.log('[WebRTC] Creating JsSIP UA with SIP URI:', cfg.sipUri);
            // IMPORTANT (MikoPBX WebRTC):
            // We register as "<EXT>-WS" AoR (cfg.sipUri = sip:52678419-WS@...) so that the
            // contact is bound to the WSS endpoint (otherwise PBX picks the UDP endpoint
            // and originate callback fails with PJSIP_ETPNOTSUITABLE).
            // BUT the actual auth account on PBX is still "<EXT>" (cfg.user = "52678419").
            // Without authorization_user, JsSIP derives auth username from uri ("52678419-WS"),
            // which causes "Authentication Error: Registration failed".
            ua = new JsSIP.UA({
                sockets: [socket],
                uri: cfg.sipUri,
                authorization_user: cfg.user,
                password: cfg.pass,
                session_timers: false,
                register: true,
                register_expires: 300,
                connection_recovery_min_interval: 2,
                connection_recovery_max_interval: 30,
                // STUN нужен для NAT traversal: MikoPBX выполняет ICE negotiation ДО отправки 200 OK.
                // Без публичных (srflx) кандидатов PBX не может завершить ICE → 200 OK не отправляется.
                // Для symmetric NAT также потребуется TURN сервер (настраивается на PBX или в настройках клиента).
                // IMPORTANT: JsSIP uses RTCPeerConnection config under "pcConfig".
                // Without this, incoming sessions answered via sess.answer() can run with default ICE (host-only),
                // causing long silence before media starts and users miss the beginning of IVR prompts.
                pcConfig: pcConfig,
                // Back-compat: keep old key (may be ignored by JsSIP).
                ice_servers: iceServers,
                // Минимальное логирование JsSIP - только ошибки и предупреждения
                log: {
                    level: 'warn', // Только warn и error
                    logger: {
                        log: (...args) => {
                            // Не логируем обычные log сообщения
                        },
                        error: (...args) => {
                            const logMsg = args.join(' ');
                            console.error('[WebRTC] [JsSIP ERROR]:', logMsg);
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: '[JsSIP ERROR] ' + logMsg } });
                        },
                        warn: (...args) => {
                            const logMsg = args.join(' ');
                            // Логируем только важные предупреждения
                            if (logMsg.includes('timeout') || logMsg.includes('failed') || logMsg.includes('error')) {
                            console.warn('[WebRTC] [JsSIP WARN]:', logMsg);
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: '[JsSIP WARN] ' + logMsg } });
                            }
                        },
                        debug: (...args) => {
                            // Не логируем debug сообщения
                        }
                    }
                }
            });
            console.log('[WebRTC] JsSIP UA created successfully');

            // Обработчики событий UA
            ua.on('connected', () => {
                console.log('[WebRTC] ✓ WebSocket connected to ATS server');
                startWssKeepalive(socket, cfg.sipUri);
                sendEvent({ type: 'ws_connected' });
            });

            ua.on('disconnected', (e) => {
                stopWssKeepalive();
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
                startTurnPrewarmLoop();
                // Прогреваем разрешение микрофона сразу после регистрации.
                // Это устраняет задержку разрешения во время ua.call() и помогает формировать INVITE.
                ensureMicPermission().then((granted) => {
                    console.log('[WebRTC] ensureMicPermission after registered:', granted);
                }).catch((err) => {
                    console.warn('[WebRTC] ensureMicPermission after registered failed:', err);
                });
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

            // КРИТИЧНО: Обрабатываем ВСЕ сессии (входящие и исходящие) через newRTCSession
            // Это гарантирует, что события сессии будут подписаны ДО того, как JsSIP начнет отправлять SIP сообщения
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

                if (e.originator === 'remote') {
                    if (window._callspireOriginateAcceptedSessionId
                        && sessionId !== window._callspireOriginateAcceptedSessionId) {
                        rejectIncomingSession(e.session, sessionId, 'Originate: rejecting duplicate INVITE');
                        return;
                    }

                    const callerNumber = e.session.remote_identity
                        ? e.session.remote_identity.uri.user
                        : 'Unknown';
                    const originateId = extractOriginateId(e.session);
                    if (isForeignOriginateCallback(originateId, callerNumber)) {
                        rejectIncomingSession(
                            e.session,
                            sessionId,
                            `Silently rejecting foreign originate callback (caller=${callerNumber}, originateId=${originateId || 'n/a'})`,
                        );
                        return;
                    }

                    // PBX Originate callback must never be dropped here — C# OriginateCoordinator dedupes stale legs.
                    if (!window._callspireOriginatePending && !isInviteForCurrentContact(e.session)) {
                        rejectIncomingSession(
                            e.session,
                            sessionId,
                            `Rejecting INVITE for stale WebRTC contact (invite=${getInviteTargetUser(e.session)}, contact=${getRegisteredContactUser()}, aor=${getRegisteredAorUser()})`,
                        );
                        return;
                    }
                }
                
                // КРИТИЧНО: Подписываемся на события СРАЗУ, чтобы не пропустить sending/sdp события
                wireSessionEvents(e.session, e.originator);
                
                if (e.originator === 'remote') {
                    // Входящий звонок
                    const callerNumber = e.session.remote_identity
                        ? e.session.remote_identity.uri.user
                        : 'Unknown';
                    window._incomingSession = e.session;

                    // Extract PBX Originate correlation header (set by AMI Originate via SIPADDHEADER)
                    let originateId = extractOriginateId(e.session);
                    if (originateId) {
                        console.log('[WebRTC] Detected X-Callspire-Originate header:', originateId);
                    }

                    sendEvent({
                        type: 'incoming',
                        data: {
                            sessionId: sessionId,
                            callerNumber: callerNumber,
                            originateId: originateId
                        }
                    });
                } else {
                    // Исходящий звонок - сессия уже создана через ua.call()
                    console.log('[WebRTC] ⚠️⚠️⚠️ OUTGOING SESSION DETECTED IN newRTCSession ⚠️⚠️⚠️');
                    console.log('[WebRTC] Session ID:', sessionId);
                    
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ⚠️⚠️⚠️ OUTGOING SESSION IN newRTCSession ⚠️⚠️⚠️ SessionID=${sessionId}, HasRequest=${!!(e.session?.request)}`
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
                // See comment in initUA(): authorization_user must be the actual PBX extension,
                // while uri/AoR is "<EXT>-WS" for WebRTC contact binding.
                ua = new JsSIP.UA({
                    sockets: [socket],
                    uri: config.sipUri,
                    authorization_user: config.user || config.username,
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

    // Wake default audio output (WASAPI) early — cold output devices can add seconds before first samples are audible.
    function prewarmAudioOutputBestEffort() {
        try {
            const AC = window.AudioContext || window.webkitAudioContext;
            if (!AC) return;
            if (!window._softphoneAudioPrewarmCtx) {
                window._softphoneAudioPrewarmCtx = new AC({ latencyHint: 'interactive' });
            }
            const ctx = window._softphoneAudioPrewarmCtx;
            const resume = ctx.resume();
            if (resume && typeof resume.then === 'function') {
                resume.then(() => {
                    try {
                        const frames = Math.max(2, Math.floor(ctx.sampleRate * 0.02));
                        const buf = ctx.createBuffer(1, frames, ctx.sampleRate);
                        const src = ctx.createBufferSource();
                        src.buffer = buf;
                        src.connect(ctx.destination);
                        src.start(0);
                    } catch (e) { /* ignore */ }
                }).catch(() => {});
            }
        } catch (e) { /* ignore */ }
    }

    /**
     * Где слышен IVR: по умолчанию удалённый звук идёт через Web Audio (элемент remoteAudio с volume=0).
     * Если обрезается начало — проверьте обычный <audio>:
     *   В консоли DevTools на странице софтфона: localStorage.setItem('callspire.remotePlayback','audio'); затем перезагрузка WebView.
     * Вернуть Web Audio: localStorage.setItem('callspire.remotePlayback','webaudio'); или removeItem.
     */
    function isRemotePlaybackViaWebAudio() {
        try {
            const v = (localStorage.getItem('callspire.remotePlayback') || 'webaudio').toLowerCase();
            return v !== 'audio' && v !== 'html';
        } catch {
            return true;
        }
    }

    // Tear down Web Audio graph used for remote playback (call on hangup / new session).
    function teardownRemoteWebAudioPlayback() {
        try {
            const g = window._softphoneRemotePlayback;
            if (!g) return;
            try { if (g.source) g.source.disconnect(); } catch { }
            try { if (g.gain) g.gain.disconnect(); } catch { }
            try { if (g.ctx && g.ctx.state !== 'closed') g.ctx.close(); } catch { }
            window._softphoneRemotePlayback = null;
        } catch { /* ignore */ }
    }

    // Fall back to direct <audio> output when Web Audio graph cannot run (common in hidden WebView2 hosts).
    function fallbackRemotePlaybackToHtmlAudio(remoteAudioEl, logSessionId, reason) {
        try {
            teardownRemoteWebAudioPlayback();
            if (remoteAudioEl) {
                remoteAudioEl.volume = 1.0;
                remoteAudioEl.muted = false;
                void remoteAudioEl.play().catch(() => {});
            }
            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] Remote playback fallback to html_audio (${reason}) session=${logSessionId || 'n/a'}`
                }
            });
        } catch { /* ignore */ }
    }

    // Play remote MediaStream to speakers via Web Audio API — often lower latency than <audio> alone (important after PBX re-INVITE).
    function attachRemoteWebAudioPlayback(mediaStream, logSessionId, remoteAudioEl) {
        if (!mediaStream || typeof mediaStream.getAudioTracks !== 'function') return;
        if (!isRemotePlaybackViaWebAudio()) return;
        teardownRemoteWebAudioPlayback();
        try {
            const AC = window.AudioContext || window.webkitAudioContext;
            if (!AC) {
                fallbackRemotePlaybackToHtmlAudio(remoteAudioEl, logSessionId, 'AudioContext unavailable');
                return;
            }
            const ctx = new AC({ latencyHint: 'interactive' });
            const source = ctx.createMediaStreamSource(mediaStream);
            const gain = ctx.createGain();
            gain.gain.value = 1.0;
            source.connect(gain);
            gain.connect(ctx.destination);
            window._softphoneRemotePlayback = { ctx, source, gain, stream: mediaStream, remoteAudioEl: remoteAudioEl || null };

            const verifyRunning = (phase) => {
                try {
                    if (ctx.state === 'running') {
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] Remote playback: WebAudio graph active (session=${logSessionId || 'n/a'}, phase=${phase})`
                            }
                        });
                        return;
                    }
                    fallbackRemotePlaybackToHtmlAudio(remoteAudioEl, logSessionId, `AudioContext state=${ctx.state} (${phase})`);
                } catch { /* ignore */ }
            };

            const resumePromise = ctx.resume();
            if (resumePromise && typeof resumePromise.then === 'function') {
                resumePromise.then(() => verifyRunning('resume')).catch(() => {
                    fallbackRemotePlaybackToHtmlAudio(remoteAudioEl, logSessionId, 'AudioContext resume rejected');
                });
            } else {
                setTimeout(() => verifyRunning('immediate'), 50);
            }
            setTimeout(() => {
                try {
                    if (ctx.state !== 'running') {
                        fallbackRemotePlaybackToHtmlAudio(remoteAudioEl, logSessionId, `AudioContext still ${ctx.state} after 500ms`);
                    }
                } catch { /* ignore */ }
            }, 500);
        } catch (e) {
            console.warn('[WebRTC] attachRemoteWebAudioPlayback failed:', e);
            fallbackRemotePlaybackToHtmlAudio(remoteAudioEl, logSessionId, `attach failed: ${e?.message || e}`);
        }
    }

    function resumeRemoteWebAudioPlaybackBestEffort() {
        try {
            const g = window._softphoneRemotePlayback;
            if (g && g.ctx && g.ctx.state === 'suspended') {
                g.ctx.resume().catch(() => {});
            }
        } catch { /* ignore */ }
    }

    // Larger jitter target helps survive PBX re-INVITE bursts so the first IVR syllables are not dropped.
    const INBOUND_AUDIO_JITTER_BUFFER_TARGET_MS = 220;

    function applyInboundAudioReceiverBufferHints(pc, targetMs) {
        if (!pc || typeof pc.getReceivers !== 'function') return;
        try {
            const receivers = pc.getReceivers();
            for (let i = 0; i < receivers.length; i++) {
                const r = receivers[i];
                if (!r.track || r.track.kind !== 'audio' || r.track.readyState === 'ended') continue;
                if ('jitterBufferTarget' in r) {
                    try {
                        r.jitterBufferTarget = targetMs;
                    } catch { /* ignore */ }
                }
            }
        } catch { /* ignore */ }
    }

    /** After SDP renegotiation (re-INVITE), rebuild WebAudio graph — resume() alone is not enough. */
    function reattachRemoteWebAudioFromElement(logSessionId) {
        try {
            if (!isRemotePlaybackViaWebAudio()) return;
            const remoteAudio = document.getElementById('remoteAudio');
            if (!remoteAudio || !remoteAudio.srcObject) return;
            const stream = remoteAudio.srcObject;
            const tracks = stream.getAudioTracks();
            if (!tracks.length || tracks[0].readyState === 'ended') return;
            if (remoteAudio.volume !== 0) return;
            attachRemoteWebAudioPlayback(stream, logSessionId, remoteAudio);
            remoteAudio.volume = 0;
        } catch { /* ignore */ }
    }

    // Подключение обработчиков событий сессии
    function wireSessionEvents(s, originator) {
        // Предотвращаем двойную подписку
        if (s._eventsWired) {
            console.log('[WebRTC] ⚠️ Events already wired for session:', s.id);
            return;
        }
        
        const sessionId = s.id || s.request?.call_id || `session_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
        // PBX Originate callback carries X-Callspire-Originate — remote answer != ICE to PBX.
        // Many PBX setups omit the header; fall back to global originate state set by C#.
        let originateId = s._softphoneOriginateId || null;
        try {
            const hdr = s.request?.getHeader?.('X-Callspire-Originate');
            if (hdr) {
                originateId = String(hdr).trim() || originateId;
            }
        } catch { /* ignore */ }
        if (!originateId && originator === 'remote') {
            if (window._callspireOriginateAcceptedSessionId === sessionId) {
                originateId = window._callspireOriginateAcceptedSessionId;
            } else if (window._callspireOriginatePending) {
                originateId = 'originate-pending';
            }
        }
        if (originateId) {
            s._softphoneOriginateId = originateId;
        }
        let sdpStableCount = 0;
        let remotePartyAnsweredSent = false;

        console.log('[WebRTC] ⚠️⚠️⚠️ WIRING SESSION EVENTS ⚠️⚠️⚠️');
        console.log('[WebRTC] Session ID:', sessionId);
        console.log('[WebRTC] Originator:', originator);
        if (originateId) {
            console.log('[WebRTC] Originate session detected, id=', originateId);
        }
        console.log('[WebRTC] Session object:', s);
        console.log('[WebRTC] Session has request:', !!(s.request));
        
        // Помечаем, что события уже подписаны
        s._eventsWired = true;
        console.log('[WebRTC] Session status:', s.status);
        console.log('[WebRTC] Session direction:', s.direction);

        // IMPORTANT: We reuse a single <audio id="remoteAudio"> element across calls (index.html).
        // After a call ends, the element can keep an old srcObject/readyState/paused state and delay
        // playback for the next call (users then miss the beginning of IVR prompts).
        // Reset the element to a clean state at the start of every session wiring.
        try {
            const existing = document.getElementById('remoteAudio');
            if (existing && existing.parentNode) {
                try { existing.pause(); } catch { }
                try { existing.srcObject = null; } catch { }
                try { existing.removeAttribute('src'); } catch { }
                try { existing.load(); } catch { }

                const fresh = existing.cloneNode(false);
                fresh.id = 'remoteAudio';
                fresh.autoplay = true;
                fresh.playsInline = true;
                fresh.muted = false;
                fresh.volume = 1.0;
                existing.parentNode.replaceChild(fresh, existing);

                teardownRemoteWebAudioPlayback();

                sendEvent({
                    type: 'js_log',
                    data: { level: 'critical', message: `[WebRTC] remoteAudio reset for new session ${sessionId}` }
                });
            }
        } catch (e) {
            sendEvent({
                type: 'js_log',
                data: { level: 'critical', message: `[WebRTC] WARNING: remoteAudio reset failed: ${e?.message || e}` }
            });
        }

        prewarmAudioOutputBestEffort();
        
        sendEvent({
            type: 'js_log',
            data: {
                level: 'critical',
                message: `[WebRTC] ⚠️⚠️⚠️ WIRING SESSION EVENTS ⚠️⚠️⚠️ SessionID=${sessionId}, Originator=${originator}, Status=${s.status || 'N/A'}, Direction=${s.direction || 'N/A'}`
            }
        });
        
        // Флаг для отслеживания, было ли уже отправлено событие call_accepted
        // Это предотвращает дублирование событий от разных источников (accepted, confirmed, peerconnection)
        let callAcceptedSent = false;
        const sendCallAcceptedOnce = (source) => {
            if (!callAcceptedSent) {
                callAcceptedSent = true;
                // Used to avoid synthetic call_ended on early ICE/PC teardown before the call is actually up.
                try { s._softphoneHadMediaNegotiationProgress = true; } catch { }
                console.log(`[WebRTC] ⚠️⚠️⚠️ SENDING call_accepted EVENT ⚠️⚠️⚠️`);
                console.log(`[WebRTC] Source: ${source}, SessionId: ${sessionId}`);
                console.log(`[WebRTC] ⚠️ Ringback tone MUST be stopped now!`);
                
                // КРИТИЧНО: Отправляем js_log ПЕРЕД call_accepted для гарантированного попадания в C# логи
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ⚠️⚠️⚠️ SENDING call_accepted EVENT - Source=${source}, SessionId=${sessionId} ⚠️⚠️⚠️`
                    }
                });
                
                try {
                    sendEvent({ 
                        type: 'call_accepted',
                        data: { 
                            sessionId: sessionId,
                            source: source
                        }
                    });
                    console.log(`[WebRTC] ✅ call_accepted event sent successfully`);
                    
                    // КРИТИЧНО: Подтверждение отправки через js_log
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ✅ call_accepted event sent successfully - Source=${source}, SessionId=${sessionId}`
                        }
                    });
                } catch (err) {
                    console.error(`[WebRTC] ❌ ERROR sending call_accepted event:`, err);
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ❌ ERROR sending call_accepted event: ${err.message || err}`
                        }
                    });
                }
            } else {
                console.log(`[WebRTC] call_accepted already sent, skipping (source: ${source})`);
            }
        };
        
        // Сохраняем функцию sendCallAcceptedOnce в сессии для доступа из других мест
        s._sendCallAcceptedOnce = sendCallAcceptedOnce;
        
        // Prevent repeated connectAudioTrack() calls from resetting srcObject/playback for the same track.
        // (We wire audio via transceivers + ontrack + fallback timers, so duplicates are common.)
        const connectedRemoteAudioTrackIds = new Set();
        // Detect when remote audio actually has non-silent samples (IVR voice started).
        // This allows the UI to stop local ringback as soon as the user can hear remote audio,
        // instead of relying on ICE state which can be "connected" before audio begins.
        const remoteAudioStartedSentForSession = new Set();

        const forceReconnectInboundFromReceivers = (reason) => {
            const pc = s.connection;
            const remoteAudio = document.getElementById('remoteAudio');
            if (!pc || !remoteAudio) return;

            connectedRemoteAudioTrackIds.clear();
            teardownRemoteWebAudioPlayback(sessionId);

            const audioTracks = [];
            try {
                const receivers = pc.getReceivers();
                for (let ri = 0; ri < receivers.length; ri++) {
                    const t = receivers[ri].track;
                    if (t && t.kind === 'audio' && t.readyState !== 'ended') {
                        audioTracks.push(t);
                        connectedRemoteAudioTrackIds.add(t.id);
                    }
                }
            } catch { /* ignore */ }

            if (audioTracks.length === 0) return;

            const ms = new MediaStream();
            for (let i = 0; i < audioTracks.length; i++) {
                ms.addTrack(audioTracks[i]);
            }

            remoteAudio.srcObject = ms;
            remoteAudio.muted = false;
            if (isRemotePlaybackViaWebAudio()) {
                remoteAudio.volume = 0;
                attachRemoteWebAudioPlayback(ms, sessionId, remoteAudio);
            } else {
                remoteAudio.volume = 1;
            }
            resumeRemoteWebAudioPlaybackBestEffort();
            void remoteAudio.play().catch(() => {});

            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] forceReconnectInboundFromReceivers(${reason}) tracks=${audioTracks.length}, session=${sessionId}`
                }
            });
        };

        const scheduleInboundMediaRecovery = (reason) => {
            if (s._softphoneInboundRecoveryScheduled) return;
            s._softphoneInboundRecoveryScheduled = true;

            const runRecovery = (phase) => {
                try {
                    forceReconnectInboundFromReceivers(`${reason}:${phase}`);
                } catch { /* ignore */ }

                if (!s._softphoneInboundRenegotiateDone && typeof s.renegotiate === 'function') {
                    s._softphoneInboundRenegotiateDone = true;
                    try {
                        s.renegotiate({
                            useUpdate: false,
                            rtcOfferConstraints: { offerToReceiveAudio: true, offerToReceiveVideo: false }
                        }, function () {
                            sendEvent({
                                type: 'js_log',
                                data: {
                                    level: 'critical',
                                    message: `[WebRTC] inbound media renegotiate OK (${reason}:${phase}) session=${sessionId}`
                                }
                            });
                            setTimeout(() => forceReconnectInboundFromReceivers(`${reason}:post-renegotiate`), 250);
                        }, function (err) {
                            sendEvent({
                                type: 'js_log',
                                data: {
                                    level: 'critical',
                                    message: `[WebRTC] inbound media renegotiate FAILED (${reason}:${phase}) session=${sessionId}: ${err}`
                                }
                            });
                        });
                    } catch (e) {
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] inbound media renegotiate error (${reason}): ${e}`
                            }
                        });
                    }
                }
            };

            setTimeout(() => runRecovery('initial'), 350);

            // If PBX bridged B-leg but still sends no inbound RTP, retry once.
            setTimeout(() => {
                try {
                    if (s.isEnded && s.isEnded()) return;
                    const pc = s.connection;
                    if (!pc || typeof pc.getStats !== 'function') return;
                    pc.getStats().then((stats) => {
                        let inboundBytes = 0;
                        stats.forEach((r) => {
                            if (r.type === 'inbound-rtp' && r.kind === 'audio') {
                                inboundBytes += r.bytesReceived || 0;
                            }
                        });
                        if (inboundBytes > 0) return;
                        if (s._softphoneInboundRenegotiateRetry) return;
                        s._softphoneInboundRenegotiateRetry = true;
                        s._softphoneInboundRenegotiateDone = false;
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] inbound RTP still zero after ${reason}; retrying media recovery session=${sessionId}`
                            }
                        });
                        runRecovery('rtp-watchdog');
                    }).catch(() => {});
                } catch { /* ignore */ }
            }, 2200);
        };

        const emitRemoteAudioStarted = (payload) => {
            sendEvent({ type: 'remote_audio_started', data: payload });
            if (!originateId || remotePartyAnsweredSent) return;

            const afterReneg = !!payload.afterRenegotiation;
            const postBridge = sdpStableCount > 1;
            const waitedMs = payload.waitedMs ?? 0;
            // B-leg may bridge without a dedicated re-INVITE probe hit; after SDP is stable,
            // sustained remote audio usually means callee media (not just local ringback UI).
            // postBridge: B-leg often bridges via re-INVITE; sustained remote audio after that is callee media.
            if (afterReneg || (postBridge && waitedMs >= 1000)) {
                remotePartyAnsweredSent = true;
                sendEvent({
                    type: 'remote_party_answered',
                    data: {
                        sessionId: payload.sessionId,
                        originateId: originateId,
                        source: payload.source || 'remote_audio_analyser',
                        waitedMs: payload.waitedMs ?? null,
                        trackId: payload.trackId || null,
                        sdpStableCount: sdpStableCount
                    }
                });
                // Miko/Asterisk often bridges trunk without refreshing WS RTP; client re-INVITE + receiver rebuild.
                scheduleInboundMediaRecovery('remote_party_answered');
            }
        };

        const startRemoteAudioSignalProbe = (sessionId, mediaStream, trackId, options = {}) => {
            try {
                const afterRenegotiation = !!options.afterRenegotiation;
                const probeKey = afterRenegotiation ? `${sessionId}:reneg` : sessionId;
                if (!sessionId || remoteAudioStartedSentForSession.has(probeKey)) return;
                if (!mediaStream || typeof mediaStream.getAudioTracks !== 'function') return;
                const audioTracks = mediaStream.getAudioTracks();
                if (!audioTracks || audioTracks.length === 0) return;

                const AudioCtx = window.AudioContext || window.webkitAudioContext;
                if (!AudioCtx) return;

                const ctx = new AudioCtx({ latencyHint: 'interactive' });
                const source = ctx.createMediaStreamSource(mediaStream);
                const analyser = ctx.createAnalyser();
                analyser.fftSize = 2048;
                analyser.smoothingTimeConstant = 0.3;
                source.connect(analyser);

                let consecutive = 0;
                const requiredConsecutive = 3; // ~300ms (checkInterval=100ms)
                const checkInterval = 100;
                const maxWaitMs = originateId ? 120000 : 6000;
                let waited = 0;

                const closeCtx = () => {
                    try { source.disconnect(); } catch {}
                    try { analyser.disconnect(); } catch {}
                    try { if (ctx.state !== 'closed') ctx.close(); } catch {}
                };

                const tick = () => {
                    // If track already ended, stop probing.
                    try {
                        const t = audioTracks[0];
                        if (!t || t.readyState === 'ended') {
                            closeCtx();
                            return;
                        }
                    } catch {}

                    waited += checkInterval;
                    try {
                        const data = new Uint8Array(analyser.frequencyBinCount);
                        analyser.getByteTimeDomainData(data);
                        let maxDeviation = 0;
                        let strong = 0;
                        for (let i = 0; i < data.length; i++) {
                            const d = Math.abs(data[i] - 128);
                            if (d > maxDeviation) maxDeviation = d;
                            if (d > 10) strong++;
                        }
                        const hasRealSignal = maxDeviation > 10 && strong > 20;
                        if (hasRealSignal) {
                            consecutive++;
                            if (consecutive >= requiredConsecutive) {
                                remoteAudioStartedSentForSession.add(probeKey);
                                if (!afterRenegotiation) {
                                    remoteAudioStartedSentForSession.add(sessionId);
                                }
                                emitRemoteAudioStarted({
                                    sessionId: sessionId,
                                    trackId: trackId || null,
                                    source: 'remote_audio_analyser',
                                    waitedMs: waited,
                                    maxDeviation: maxDeviation,
                                    strongSamples: strong,
                                    afterRenegotiation: afterRenegotiation,
                                    originateId: originateId || null
                                });
                                closeCtx();
                                return;
                            }
                        } else {
                            consecutive = 0;
                        }
                    } catch (e) {
                        // Ignore transient analyser errors; keep trying until timeout.
                        consecutive = 0;
                    }

                    if (waited >= maxWaitMs) {
                        closeCtx();
                        return;
                    }
                    setTimeout(tick, checkInterval);
                };

                const resume = ctx.resume();
                const startAfterResume = () => {
                    setTimeout(tick, 150);
                };
                if (resume && typeof resume.then === 'function') {
                    resume.then(startAfterResume).catch(startAfterResume);
                } else {
                    startAfterResume();
                }
            } catch (e) {
                // Best effort only; never break calls if AudioContext fails.
            }
        };
        
        // КРИТИЧНО: Определяем connectAudioTrack на уровне wireSessionEvents, чтобы она была доступна везде
        // (включая таймаут-проверку в s.on('progress'))
        const connectAudioTrack = async (track) => {
            // КРИТИЧНО: Логируем ВХОД в функцию для диагностики
            console.log('[WebRTC] ⚠️⚠️⚠️ connectAudioTrack CALLED ⚠️⚠️⚠️');
            console.log('[WebRTC] Track:', track ? `kind=${track.kind}, id=${track.id}, readyState=${track.readyState}` : 'NULL');
            
            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] ⚠️⚠️⚠️ connectAudioTrack CALLED ⚠️⚠️⚠️ TrackID=${track?.id || 'NULL'}, kind=${track?.kind || 'NULL'}, readyState=${track?.readyState || 'NULL'}`
                }
            });
            
            if (track.kind !== 'audio') {
                console.log('[WebRTC] Skipping non-audio track:', track.kind);
                return;
            }

            // If this exact track was already connected, do not replace remoteAudio.srcObject again.
            // Replacing srcObject can cause short silences and users miss the start of IVR prompts.
            if (track && connectedRemoteAudioTrackIds.has(track.id)) {
                try {
                    resumeRemoteWebAudioPlaybackBestEffort();
                    reattachRemoteWebAudioFromElement(sessionId);
                    const remoteAudioExisting = document.getElementById('remoteAudio');
                    if (remoteAudioExisting && remoteAudioExisting.paused) {
                        console.log('[WebRTC] Track already connected; remoteAudio is paused — retrying play()');
                        await remoteAudioExisting.play();
                    } else {
                        console.log('[WebRTC] Track already connected; skipping reconnect:', track.id);
                    }
                } catch (err) {
                    console.warn('[WebRTC] Track already connected but play() failed:', err);
                }
                return;
            }
            
            // ДЕТАЛЬНОЕ ЛОГИРОВАНИЕ для диагностики проблемы с аудио
            console.log('[WebRTC] ===== CONNECTING REMOTE AUDIO TRACK =====');
            console.log('[WebRTC] Track ID:', track.id);
            console.log('[WebRTC] Track enabled:', track.enabled);
            console.log('[WebRTC] Track muted:', track.muted);
            console.log('[WebRTC] Track readyState:', track.readyState);
            console.log('[WebRTC] Originator:', originator);
            console.log('[WebRTC] Session ID:', sessionId);
            
            // КРИТИЧНО: Отправляем js_log для гарантированного попадания в C# логи
            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] ===== CONNECTING REMOTE AUDIO TRACK ===== TrackID=${track.id}, readyState=${track.readyState}, enabled=${track.enabled}, originator=${originator}`
                }
            });
            
            // КРИТИЧНО: Для исходящих звонков, когда удаленный аудио трек появляется и становится live,
            // это означает, что звонок принят - отправляем call_accepted СРАЗУ, до подключения к элементу
            if (originator === 'local' && track.readyState === 'live' && track.enabled) {
                console.log('[WebRTC] ⚠️ CRITICAL: Remote audio track is LIVE for outgoing call!');
                console.log('[WebRTC] ⚠️ This means call is answered - sending call_accepted IMMEDIATELY');
                console.log('[WebRTC] ⚠️ Ringback tone should STOP now!');
                sendCallAcceptedOnce('connectAudioTrack_remote_live_early');
            } else {
                console.log('[WebRTC] ⚠️ WARNING: Remote audio track is NOT ready yet!');
                console.log('[WebRTC] ⚠️ readyState:', track.readyState, '(should be "live")');
                console.log('[WebRTC] ⚠️ enabled:', track.enabled, '(should be true)');
            }
            
            let remoteAudio = document.getElementById('remoteAudio');
            if (!remoteAudio) {
                console.warn('[WebRTC] Remote audio element not found, creating it...');
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ⚠️ Remote audio element not found, creating it...`
                    }
                });
                
                // Создаем элемент если его нет
                const audioElement = document.createElement('audio');
                audioElement.id = 'remoteAudio';
                audioElement.autoplay = true;
                audioElement.playsInline = true;
                audioElement.muted = false;
                audioElement.volume = 1.0;
                document.body.appendChild(audioElement);
                remoteAudio = audioElement;
                
                console.log('[WebRTC] ✅ Remote audio element created');
                console.log('[WebRTC] Element properties:');
                console.log('[WebRTC]   - id:', remoteAudio.id);
                console.log('[WebRTC]   - autoplay:', remoteAudio.autoplay);
                console.log('[WebRTC]   - playsInline:', remoteAudio.playsInline);
                console.log('[WebRTC]   - muted:', remoteAudio.muted);
                console.log('[WebRTC]   - volume:', remoteAudio.volume);
                console.log('[WebRTC]   - paused:', remoteAudio.paused);
                console.log('[WebRTC]   - srcObject:', remoteAudio.srcObject ? 'SET' : 'NULL');
                console.log('[WebRTC]   - sinkId:', remoteAudio.sinkId || 'default (not set)');
                
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ✅ Remote audio element CREATED - id=${remoteAudio.id}, autoplay=${remoteAudio.autoplay}, muted=${remoteAudio.muted}, volume=${remoteAudio.volume}, paused=${remoteAudio.paused}`
                    }
                });
            } else {
                console.log('[WebRTC] ✅ Remote audio element found in DOM');
                console.log('[WebRTC] Existing element properties:');
                console.log('[WebRTC]   - id:', remoteAudio.id);
                console.log('[WebRTC]   - muted:', remoteAudio.muted);
                console.log('[WebRTC]   - volume:', remoteAudio.volume);
                console.log('[WebRTC]   - paused:', remoteAudio.paused);
                console.log('[WebRTC]   - srcObject:', remoteAudio.srcObject ? 'SET' : 'NULL');
                console.log('[WebRTC]   - sinkId:', remoteAudio.sinkId || 'default (not set)');
                
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ✅ Remote audio element FOUND - muted=${remoteAudio.muted}, volume=${remoteAudio.volume}, paused=${remoteAudio.paused}, srcObject=${remoteAudio.srcObject ? 'SET' : 'NULL'}, sinkId=${remoteAudio.sinkId || 'default'}`
                    }
                });
            }
            
            // КРИТИЧНО: Проверяем доступность setSinkId для выбора устройства вывода
            const hasSetSinkId = 'setSinkId' in remoteAudio;
            if (hasSetSinkId) {
                console.log('[WebRTC] ✅ setSinkId is supported - can select audio output device');
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ✅ setSinkId is SUPPORTED - can select audio output device`
                    }
                });
            } else {
                console.warn('[WebRTC] ⚠️ setSinkId is NOT supported - will use default audio output device');
                console.warn('[WebRTC] ⚠️ This may cause audio to play on wrong device!');
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ⚠️ setSinkId is NOT SUPPORTED - will use default audio output device (may cause audio to play on wrong device!)`
                    }
                });
            }
            
            // Проверяем доступные аудио устройства вывода
            try {
                navigator.mediaDevices.enumerateDevices().then(devices => {
                    const audioOutputs = devices.filter(d => d.kind === 'audiooutput');
                    console.log('[WebRTC] Available audio output devices:', audioOutputs.length);
                    
                    let deviceList = '';
                    audioOutputs.forEach((device, index) => {
                        const deviceInfo = `Device ${index}: ${device.label || 'Unknown'} (ID: ${device.deviceId.substring(0, 20)}...)`;
                        console.log(`[WebRTC]   ${deviceInfo}`);
                        deviceList += deviceInfo + '; ';
                    });
                    
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] Available audio output devices: ${audioOutputs.length} - ${deviceList}`
                        }
                    });
                    
                    // Если есть сохраненное устройство вывода, пытаемся его использовать
                    // Но пока используем устройство по умолчанию
                    console.log('[WebRTC] Using default audio output device (setSinkId will be called if device is selected)');
                }).catch(err => {
                    console.error('[WebRTC] Error enumerating audio devices:', err);
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ❌ Error enumerating audio devices: ${err.message || err}`
                        }
                    });
                });
            } catch (err) {
                console.error('[WebRTC] Error accessing mediaDevices:', err);
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ❌ Error accessing mediaDevices: ${err.message || err}`
                    }
                });
            }
            
            try {
                const stream = new MediaStream([track]);
                
                // Проверяем состояние трека перед подключением
                console.log('[WebRTC] Remote audio track state:', {
                    id: track.id,
                    kind: track.kind,
                    enabled: track.enabled,
                    muted: track.muted,
                    readyState: track.readyState
                });
                
                // КРИТИЧНО: Проверяем, что трек действительно активен и передает данные
                const trackReadyState = track.readyState;
                const trackEnabled = track.enabled;
                const trackMuted = track.muted;
                
                console.log('[WebRTC] ===== TRACK STATE ANALYSIS =====');
                console.log('[WebRTC] Track ID:', track.id);
                console.log('[WebRTC] Track kind:', track.kind);
                console.log('[WebRTC] Track readyState:', trackReadyState, trackReadyState === 'live' ? '✅ LIVE' : '❌ NOT LIVE');
                console.log('[WebRTC] Track enabled:', trackEnabled, trackEnabled ? '✅ ENABLED' : '❌ DISABLED');
                console.log('[WebRTC] Track muted:', trackMuted, trackMuted ? '❌ MUTED' : '✅ UNMUTED');
                console.log('[WebRTC] Track settings:', track.getSettings ? JSON.stringify(track.getSettings()) : 'N/A');
                console.log('[WebRTC] Track constraints:', track.getConstraints ? JSON.stringify(track.getConstraints()) : 'N/A');
                console.log('[WebRTC] Track capabilities:', track.getCapabilities ? JSON.stringify(track.getCapabilities()) : 'N/A');
                
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ===== TRACK STATE ANALYSIS ===== TrackID=${track.id}, readyState=${trackReadyState}${trackReadyState === 'live' ? ' ✅ LIVE' : ' ❌ NOT LIVE'}, enabled=${trackEnabled}${trackEnabled ? ' ✅' : ' ❌'}, muted=${trackMuted}${trackMuted ? ' ❌' : ' ✅'}`
                    }
                });
                
                if (trackReadyState !== 'live') {
                    console.error('[WebRTC] ❌❌❌ TRACK IS NOT LIVE! ❌❌❌');
                    console.error('[WebRTC] ❌ Track readyState:', trackReadyState, '(should be "live")');
                    console.error('[WebRTC] ❌ This means track exists but is NOT transmitting audio data!');
                    
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ❌❌❌ TRACK IS NOT LIVE! readyState=${trackReadyState} (should be "live") - Track exists but NOT transmitting audio! ❌❌❌`
                        }
                    });
                } else {
                    console.log('[WebRTC] ✅✅✅ TRACK IS LIVE - ready to transmit audio ✅✅✅');
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ✅✅✅ TRACK IS LIVE - ready to transmit audio ✅✅✅`
                        }
                    });
                }
                
                // КРИТИЧНО: Убеждаемся, что трек включен И НЕ ЗАГЛУШЕН
                if (!track.enabled) {
                    console.warn('[WebRTC] Remote audio track is disabled, enabling it...');
                    track.enabled = true;
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ⚠️ Track was DISABLED, enabled it - TrackID=${track.id}`
                        }
                    });
                }
                
                // КРИТИЧНО: Логируем состояние muted для диагностики, но НЕ блокируем подключение
                // В WebRTC track.muted может быть true временно (пока не прилетел первый пакет данных)
                // Это нормальное поведение, поэтому мы всегда подключаем трек независимо от muted
                if (track.muted) {
                    console.log('[WebRTC] ⚠️ Track is muted (may be temporary until first data packet arrives)');
                    console.log('[WebRTC] ⚠️ This is normal in WebRTC - track.muted will become false when data starts flowing');
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ⚠️ Track is muted (may be temporary) - TrackID=${track.id} - This is normal, will unmute when data arrives`
                        }
                    });
                } else {
                    console.log('[WebRTC] ✅ Track is NOT muted');
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ✅ Track is NOT muted - TrackID=${track.id}`
                        }
                    });
                }
                
                // КРИТИЧНО: Добавляем обработчики событий трека для диагностики
                track.addEventListener('ended', () => {
                    console.error('[WebRTC] ❌❌❌ REMOTE AUDIO TRACK ENDED! ❌❌❌');
                    console.error('[WebRTC] ❌ This means track stopped transmitting!');
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ❌❌❌ REMOTE AUDIO TRACK ENDED! Track stopped transmitting! ❌❌❌`
                        }
                    });
                });
                
                track.addEventListener('mute', () => {
                    console.warn('[WebRTC] ⚠️ Remote audio track was MUTED');
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ⚠️ Remote audio track was MUTED`
                        }
                    });
                });
                
                track.addEventListener('unmute', () => {
                    console.log('[WebRTC] ✅ Remote audio track was UNMUTED');
                    resumeRemoteWebAudioPlaybackBestEffort();
                    reattachRemoteWebAudioFromElement(sessionId);
                });
                
                // Заменяем предыдущий поток если есть
                // КРИТИЧНО: НЕ вызываем track.stop() на предыдущем потоке!
                // Трек может быть тот же самый объект, что и новый трек (повторное подключение
                // из accepted → confirmed). Вызов stop() навсегда остановит трек
                // (readyState='ended'), и запись будет записывать тишину.
                if (remoteAudio.srcObject) {
                    console.log('[WebRTC] Replacing previous srcObject (NOT stopping tracks to preserve them for recording)');
                    remoteAudio.srcObject = null;
                }
                
                // КРИТИЧНО: Перед установкой srcObject проверяем состояние трека для диагностики
                // ВАЖНО: track.muted может быть true временно (пока не прилетел первый пакет данных)
                // Это нормальное поведение WebRTC, поэтому мы ВСЕГДА устанавливаем srcObject
                console.log('[WebRTC] ===== FINAL TRACK CHECK BEFORE SETTING srcObject =====');
                console.log('[WebRTC] Track state:');
                console.log('[WebRTC]   - id:', track.id);
                console.log('[WebRTC]   - enabled:', track.enabled, track.enabled ? '✅' : '❌');
                console.log('[WebRTC]   - muted:', track.muted, track.muted ? '⚠️ MUTED (may be temporary)' : '✅ NOT MUTED');
                console.log('[WebRTC]   - readyState:', track.readyState, track.readyState === 'live' ? '✅ LIVE' : '❌ NOT LIVE');
                console.log('[WebRTC] ⚠️ NOTE: track.muted may be true temporarily until first data packet arrives - this is NORMAL');
                
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ===== FINAL TRACK CHECK BEFORE SETTING srcObject ===== TrackID=${track.id}, enabled=${track.enabled}${track.enabled ? ' ✅' : ' ❌'}, muted=${track.muted}${track.muted ? ' ⚠️ MUTED (may be temporary)' : ' ✅ NOT MUTED'}, readyState=${track.readyState}${track.readyState === 'live' ? ' ✅ LIVE' : ' ❌ NOT LIVE'}`
                    }
                });
                
                // КРИТИЧНО: ВСЕГДА устанавливаем srcObject, независимо от track.muted
                // track.muted может быть true временно (пока не прилетел первый пакет) - это нормально
                // Если трек действительно muted удаленной стороной, мы все равно должны подключить его,
                // чтобы браузер мог обработать событие unmute когда данные начнут приходить
                remoteAudio.srcObject = stream;
                console.log('[WebRTC] ✓ Audio stream set to element, tracks:', stream.getAudioTracks().length);
                
                // КРИТИЧНО: Проверяем, что MediaStream содержит активные треки
                const audioTracks = stream.getAudioTracks();
                const videoTracks = stream.getVideoTracks();
                const allTracks = stream.getTracks();
                
                console.log('[WebRTC] ===== MEDIASTREAM ANALYSIS =====');
                console.log('[WebRTC] Total tracks:', allTracks.length);
                console.log('[WebRTC] Audio tracks:', audioTracks.length);
                console.log('[WebRTC] Video tracks:', videoTracks.length);
                console.log('[WebRTC] Stream ID:', stream.id);
                console.log('[WebRTC] Stream active:', stream.active);
                
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ===== MEDIASTREAM ANALYSIS ===== TotalTracks=${allTracks.length}, AudioTracks=${audioTracks.length}, VideoTracks=${videoTracks.length}, StreamActive=${stream.active}`
                    }
                });
                
                audioTracks.forEach((t, idx) => {
                    const trackInfo = `Track ${idx}: id=${t.id}, enabled=${t.enabled}, muted=${t.muted}, readyState=${t.readyState}`;
                    console.log(`[WebRTC]   ${trackInfo}`);
                    
                    if (t.readyState !== 'live') {
                        console.error(`[WebRTC] ❌ Track ${idx} is NOT live! readyState=${t.readyState}`);
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] ❌ MediaStream track ${idx} is NOT live! readyState=${t.readyState} - ${trackInfo}`
                            }
                        });
                    } else {
                        console.log(`[WebRTC] ✅ Track ${idx} is LIVE`);
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] ✅ MediaStream track ${idx} is LIVE - ${trackInfo}`
                            }
                        });
                    }
                });
                
                // Проверяем состояние элемента ДО изменений
                console.log('[WebRTC] Audio element state BEFORE changes:');
                console.log('[WebRTC]   - paused:', remoteAudio.paused);
                console.log('[WebRTC]   - muted:', remoteAudio.muted);
                console.log('[WebRTC]   - volume:', remoteAudio.volume);
                console.log('[WebRTC]   - srcObject:', remoteAudio.srcObject ? 'SET' : 'NULL');
                
                // Убеждаемся, что элемент не muted и volume установлен
                remoteAudio.muted = false;
                remoteAudio.volume = 1.0;
                
                console.log('[WebRTC] ===== AFTER SETTING muted=false, volume=1.0 =====');
                console.log('[WebRTC]   - muted:', remoteAudio.muted, '(should be false)');
                console.log('[WebRTC]   - volume:', remoteAudio.volume, '(should be 1.0)');
                console.log('[WebRTC]   - sinkId:', remoteAudio.sinkId || 'default (not set)');
                
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ===== AFTER SETTING muted=false, volume=1.0 ===== muted=${remoteAudio.muted}, volume=${remoteAudio.volume}, sinkId=${remoteAudio.sinkId || 'default'}`
                    }
                });
                
                // КРИТИЧНО: Явно устанавливаем sinkId на default, если не установлен
                // Это гарантирует, что аудио будет воспроизводиться на устройстве по умолчанию
                if ('setSinkId' in remoteAudio) {
                    const currentSinkId = remoteAudio.sinkId || '';
                    console.log('[WebRTC] ===== SINKID CONFIGURATION =====');
                    console.log('[WebRTC] Current sinkId:', currentSinkId || 'default (empty)');
                    console.log('[WebRTC] setSinkId supported:', true);
                    
                    try {
                        // Устанавливаем на default (пустую строку) для гарантии
                        console.log('[WebRTC] Setting sinkId to default (empty string) to ensure audio plays on default device...');
                        await remoteAudio.setSinkId('');
                        
                        const newSinkId = remoteAudio.sinkId || '';
                        console.log('[WebRTC] ✅ sinkId set to default');
                        console.log('[WebRTC] New sinkId:', newSinkId || 'default (empty)');
                        
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] ✅ sinkId set to default (empty string) - old=${currentSinkId || 'default'}, new=${newSinkId || 'default'}`
                            }
                        });
                    } catch (sinkErr) {
                        console.warn('[WebRTC] ⚠️ Failed to set sinkId to default:', sinkErr);
                        console.warn('[WebRTC] Error details:', sinkErr.message, sinkErr.stack);
                        
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] ⚠️ Failed to set sinkId to default: ${sinkErr.message || sinkErr} - Current sinkId=${remoteAudio.sinkId || 'default'}`
                            }
                        });
                    }
                } else {
                    console.warn('[WebRTC] ⚠️ setSinkId is NOT supported - cannot set sinkId');
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ⚠️ setSinkId is NOT supported - cannot set sinkId, will use default device`
                        }
                    });
                }
                
                // Финальное состояние после всех изменений
                const finalState = {
                    paused: remoteAudio.paused,
                    muted: remoteAudio.muted,
                    volume: remoteAudio.volume,
                    srcObject: remoteAudio.srcObject ? 'SET' : 'NULL',
                    sinkId: remoteAudio.sinkId || 'default',
                    readyState: remoteAudio.readyState,
                    currentTime: remoteAudio.currentTime
                };
                
                console.log('[WebRTC] ===== FINAL STATE AFTER ALL CHANGES =====');
                console.log('[WebRTC] Audio element state AFTER all changes:');
                console.log('[WebRTC]   - paused:', finalState.paused, '(should be true before play())');
                console.log('[WebRTC]   - muted:', finalState.muted, '(should be false)');
                console.log('[WebRTC]   - volume:', finalState.volume, '(should be 1.0)');
                console.log('[WebRTC]   - srcObject:', finalState.srcObject, '(should be SET)');
                console.log('[WebRTC]   - sinkId:', finalState.sinkId);
                console.log('[WebRTC]   - readyState:', finalState.readyState);
                console.log('[WebRTC]   - currentTime:', finalState.currentTime);
                
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ===== FINAL STATE AFTER ALL CHANGES ===== paused=${finalState.paused}, muted=${finalState.muted}, volume=${finalState.volume}, srcObject=${finalState.srcObject}, sinkId=${finalState.sinkId}, readyState=${finalState.readyState}, currentTime=${finalState.currentTime}`
                    }
                });
                
                // Добавляем обработчики событий для отладки с детальным логированием
                remoteAudio.addEventListener('play', () => {
                    console.log('[WebRTC] ✅✅✅ REMOTE AUDIO STARTED PLAYING ✅✅✅');
                    console.log('[WebRTC] ✅ This means you should hear the remote party now!');
                    console.log('[WebRTC] ✅ If you still hear ringback tone, it means tone was not stopped!');
                    console.log('[WebRTC] ✅ Audio element state at play event:');
                    console.log('[WebRTC]   - paused:', remoteAudio.paused);
                    console.log('[WebRTC]   - muted:', remoteAudio.muted);
                    console.log('[WebRTC]   - volume:', remoteAudio.volume);
                    console.log('[WebRTC]   - sinkId:', remoteAudio.sinkId || 'default (not set)');
                    console.log('[WebRTC]   - currentTime:', remoteAudio.currentTime);
                    sendEvent({ 
                        type: 'audio_playing', 
                        data: { 
                            sessionId: sessionId,
                            trackId: track.id,
                            timestamp: Date.now(),
                            sinkId: remoteAudio.sinkId || 'default',
                            volume: remoteAudio.volume,
                            muted: remoteAudio.muted
                        } 
                    });
                });
                remoteAudio.addEventListener('pause', () => {
                    console.warn('[WebRTC] ⚠️⚠️⚠️ REMOTE AUDIO PAUSED ⚠️⚠️⚠️');
                    console.warn('[WebRTC] ⚠️ This should NOT happen during active call!');
                    sendEvent({ 
                        type: 'audio_paused', 
                        data: { 
                            sessionId: sessionId,
                            trackId: track.id,
                            timestamp: Date.now()
                        } 
                    });
                });
                remoteAudio.addEventListener('error', (e) => {
                    console.error('[WebRTC] ❌❌❌ REMOTE AUDIO ERROR ❌❌❌');
                    console.error('[WebRTC] ❌ Error details:', e);
                    console.error('[WebRTC] ❌ Error code:', remoteAudio.error?.code);
                    console.error('[WebRTC] ❌ Error message:', remoteAudio.error?.message);
                    console.error('[WebRTC] ❌ This will prevent you from hearing the remote party!');
                    console.error('[WebRTC] ❌ Audio element state at error:');
                    console.error('[WebRTC]   - paused:', remoteAudio.paused);
                    console.error('[WebRTC]   - muted:', remoteAudio.muted);
                    console.error('[WebRTC]   - volume:', remoteAudio.volume);
                    console.error('[WebRTC]   - sinkId:', remoteAudio.sinkId || 'default (not set)');
                    sendEvent({ 
                        type: 'audio_error', 
                        data: { 
                            sessionId: sessionId,
                            trackId: track.id,
                            error: e.toString(),
                            errorCode: remoteAudio.error?.code,
                            errorMessage: remoteAudio.error?.message,
                            timestamp: Date.now(),
                            sinkId: remoteAudio.sinkId || 'default'
                        } 
                    });
                });
                remoteAudio.addEventListener('loadedmetadata', () => {
                    console.log('[WebRTC] ✓ Remote audio metadata loaded');
                    console.log('[WebRTC]   - duration:', remoteAudio.duration);
                    console.log('[WebRTC]   - readyState:', remoteAudio.readyState);
                });
                remoteAudio.addEventListener('timeupdate', () => {
                    // Логируем периодически, что аудио действительно воспроизводится
                    if (remoteAudio.currentTime > 0 && remoteAudio.currentTime % 5 < 0.1) {
                        console.log('[WebRTC] ✓ Remote audio is playing, currentTime:', remoteAudio.currentTime.toFixed(2));
                    }
                });
                
                // Явно запускаем воспроизведение с обработкой ошибок
                console.log('[WebRTC] ===== ATTEMPTING TO PLAY REMOTE AUDIO =====');
                console.log('[WebRTC] Track ID:', track.id);
                console.log('[WebRTC] Element exists:', !!remoteAudio);
                console.log('[WebRTC] Element state before play():');
                console.log('[WebRTC]   - paused:', remoteAudio.paused);
                console.log('[WebRTC]   - muted:', remoteAudio.muted);
                console.log('[WebRTC]   - volume:', remoteAudio.volume);
                console.log('[WebRTC]   - srcObject:', remoteAudio.srcObject ? 'SET' : 'NULL');
                console.log('[WebRTC]   - sinkId:', remoteAudio.sinkId || 'default');
                console.log('[WebRTC]   - readyState:', remoteAudio.readyState);
                console.log('[WebRTC]   - currentTime:', remoteAudio.currentTime);
                
                // Проверяем состояние трека перед play()
                const streamBeforePlay = remoteAudio.srcObject;
                if (streamBeforePlay) {
                    const tracksBeforePlay = streamBeforePlay.getAudioTracks();
                    console.log('[WebRTC] Stream tracks before play():', tracksBeforePlay.length);
                    tracksBeforePlay.forEach((t, idx) => {
                        console.log(`[WebRTC]   Track ${idx}: id=${t.id}, enabled=${t.enabled}, muted=${t.muted}, readyState=${t.readyState}`);
                    });
                }
                
                // КРИТИЧНО: Отправляем js_log перед попыткой play()
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ===== ATTEMPTING TO PLAY REMOTE AUDIO ===== TrackID=${track.id}, element exists=${!!remoteAudio}, paused=${remoteAudio.paused}, muted=${remoteAudio.muted}, volume=${remoteAudio.volume}, sinkId=${remoteAudio.sinkId || 'default'}, readyState=${remoteAudio.readyState}`
                    }
                });
                
                // КРИТИЧНО: Сохраняем время начала play() для последующих проверок
                const playStartTime = Date.now();
                
                try {
                    console.log('[WebRTC] Calling remoteAudio.play() at', playStartTime);
                    
                    const playPromise = remoteAudio.play();
                    if (playPromise !== undefined) {
                        console.log('[WebRTC] play() returned Promise, awaiting...');
                        await playPromise;
                        
                        const playEndTime = Date.now();
                        const playDuration = playEndTime - playStartTime;
                        console.log('[WebRTC] ✅✅✅ REMOTE AUDIO PLAY() SUCCESS ✅✅✅');
                        console.log('[WebRTC] Play() completed in', playDuration, 'ms');
                        console.log('[WebRTC] ✅ Remote audio track connected and playing');
                        
                        // Mark this track as connected only after successful play().
                        connectedRemoteAudioTrackIds.add(track.id);
                        // Speakers: Web Audio (default) или напрямую <audio> — см. callspire.remotePlayback
                        try {
                            if (isRemotePlaybackViaWebAudio()) {
                                attachRemoteWebAudioPlayback(remoteAudio.srcObject, sessionId, remoteAudio);
                                remoteAudio.volume = 0;
                                sendEvent({
                                    type: 'js_log',
                                    data: {
                                        level: 'critical',
                                        message: `[WebRTC] Remote playback mode: webaudio (session=${sessionId})`
                                    }
                                });
                            } else {
                                teardownRemoteWebAudioPlayback();
                                remoteAudio.volume = 1.0;
                                sendEvent({
                                    type: 'js_log',
                                    data: {
                                        level: 'critical',
                                        message: `[WebRTC] Remote playback mode: html_audio (localStorage callspire.remotePlayback=audio) session=${sessionId}`
                                    }
                                });
                            }
                            try {
                                const pc = s.connection;
                                if (pc) applyInboundAudioReceiverBufferHints(pc, INBOUND_AUDIO_JITTER_BUFFER_TARGET_MS);
                            } catch { /* ignore */ }
                        } catch (webAudErr) {
                            console.warn('[WebRTC] WebAudio attach after play:', webAudErr);
                            remoteAudio.volume = 1.0;
                        }
                        console.log('[WebRTC] ===== FINAL STATE AFTER play() SUCCESS =====');
                        console.log('[WebRTC] ✅ Final state check:');
                        console.log('[WebRTC]   - paused:', remoteAudio.paused, '(should be false)');
                        console.log('[WebRTC]   - muted:', remoteAudio.muted, '(should be false)');
                        console.log('[WebRTC]   - volume:', remoteAudio.volume, '(should be 1.0)');
                        console.log('[WebRTC]   - readyState:', remoteAudio.readyState);
                        console.log('[WebRTC]   - sinkId:', remoteAudio.sinkId || 'default (not set)');
                        console.log('[WebRTC]   - currentTime:', remoteAudio.currentTime);
                        console.log('[WebRTC]   - duration:', remoteAudio.duration || 'N/A');
                        console.log('[WebRTC]   - error:', remoteAudio.error || 'NONE');
                        
                        // Проверяем состояние трека после play()
                        const streamAfterPlay = remoteAudio.srcObject;
                        if (streamAfterPlay) {
                            const tracksAfterPlay = streamAfterPlay.getAudioTracks();
                            console.log('[WebRTC] Stream tracks after play():', tracksAfterPlay.length);
                            tracksAfterPlay.forEach((t, idx) => {
                                console.log(`[WebRTC]   Track ${idx}: id=${t.id}, enabled=${t.enabled}, muted=${t.muted}, readyState=${t.readyState}`);
                            });
                        }
                        
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] ✅✅✅ REMOTE AUDIO PLAY() SUCCESS ✅✅✅ Completed in ${playDuration}ms - paused=${remoteAudio.paused}, muted=${remoteAudio.muted}, volume=${remoteAudio.volume}, sinkId=${remoteAudio.sinkId || 'default'}, readyState=${remoteAudio.readyState}, currentTime=${remoteAudio.currentTime}`
                            }
                        });
                        
                        // КРИТИЧНО: Отправляем js_log о успешном play()
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] ✅✅✅ REMOTE AUDIO PLAY() SUCCESS ✅✅✅ paused=${remoteAudio.paused}, muted=${remoteAudio.muted}, volume=${remoteAudio.volume}, currentTime=${remoteAudio.currentTime}`
                            }
                        });

                        // Start probe: detect when real remote audio begins (non-silent samples).
                        // If the local ringback tone is still playing, it can mask the first words of IVR.
                        // We send remote_audio_started only once per session.
                        try {
                            startRemoteAudioSignalProbe(sessionId, remoteAudio.srcObject, track.id);
                        } catch {}
                        
                        // КРИТИЧНО: Отправляем событие audio_connected после успешного play()
                        console.log('[WebRTC] ⚠️⚠️⚠️ SENDING audio_connected EVENT ⚠️⚠️⚠️');
                        console.log('[WebRTC] SessionId:', sessionId);
                        console.log('[WebRTC] TrackId:', track.id);
                        console.log('[WebRTC] Audio element state: paused=', remoteAudio.paused, ', muted=', remoteAudio.muted, ', volume=', remoteAudio.volume);
                        
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] ⚠️⚠️⚠️ SENDING audio_connected EVENT ⚠️⚠️⚠️ SessionId=${sessionId}, TrackId=${track.id}, paused=${remoteAudio.paused}, muted=${remoteAudio.muted}`
                            }
                        });
                        
                        sendEvent({ 
                            type: 'audio_connected', 
                            data: { 
                                sessionId: sessionId,
                                trackId: track.id,
                                source: 'connectAudioTrack_play_success',
                                paused: remoteAudio.paused,
                                muted: remoteAudio.muted,
                                volume: remoteAudio.volume,
                                currentTime: remoteAudio.currentTime
                            } 
                        });
                        
                        console.log('[WebRTC] ✅ audio_connected event sent successfully');
                        
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] ✅ audio_connected event sent successfully - SessionId=${sessionId}, TrackId=${track.id}`
                            }
                        });
                        
                        // КРИТИЧНО: Проверяем, что аудио действительно играет
                        if (remoteAudio.paused) {
                            console.error('[WebRTC] ❌❌❌ AUDIO IS PAUSED AFTER PLAY()! ❌❌❌');
                            console.error('[WebRTC] ❌ This means audio is NOT playing!');
                            
                            sendEvent({
                                type: 'js_log',
                                data: {
                                    level: 'critical',
                                    message: `[WebRTC] ❌❌❌ AUDIO IS PAUSED AFTER PLAY()! This means audio is NOT playing! ❌❌❌`
                                }
                            });
                            
                            // Пробуем еще раз через небольшую задержку
                            setTimeout(async () => {
                                try {
                                    console.log('[WebRTC] Retrying play() after pause detected...');
                                    await remoteAudio.play();
                                    console.log('[WebRTC] ✅ Retry play() succeeded');
                                } catch (retryErr) {
                                    console.error('[WebRTC] ❌ Retry play() failed:', retryErr);
                                }
                            }, 500);
                        } else {
                            console.log('[WebRTC] ✅ Audio is NOT paused - should be playing');
                            
                            // Дополнительная проверка через 500ms - если currentTime не изменился, аудио не играет
                            setTimeout(() => {
                                const checkTime500ms = Date.now();
                                const initialTime = remoteAudio.currentTime;
                                const isPaused = remoteAudio.paused;
                                const isMuted = remoteAudio.muted;
                                const volume = remoteAudio.volume;
                                const sinkId = remoteAudio.sinkId || 'default';
                                const readyState = remoteAudio.readyState;
                                const duration = remoteAudio.duration;
                                const error = remoteAudio.error;
                                
                                console.log('[WebRTC] ===== AUDIO PLAYBACK VERIFICATION AFTER 500ms =====');
                                console.log('[WebRTC] Check time:', checkTime500ms);
                                console.log('[WebRTC] Audio element state:');
                                console.log('[WebRTC]   - currentTime:', initialTime);
                                console.log('[WebRTC]   - paused:', isPaused, isPaused ? '❌ PAUSED' : '✅ PLAYING');
                                console.log('[WebRTC]   - muted:', isMuted, isMuted ? '❌ MUTED' : '✅ UNMUTED');
                                console.log('[WebRTC]   - volume:', volume);
                                console.log('[WebRTC]   - sinkId:', sinkId);
                                console.log('[WebRTC]   - readyState:', readyState);
                                console.log('[WebRTC]   - duration:', duration || 'N/A');
                                console.log('[WebRTC]   - error:', error || 'NONE');
                                
                                // Проверяем состояние трека
                                const stream = remoteAudio.srcObject;
                                if (stream) {
                                    const tracks = stream.getAudioTracks();
                                    console.log('[WebRTC] Stream state:');
                                    console.log('[WebRTC]   - stream active:', stream.active);
                                    console.log('[WebRTC]   - stream tracks:', tracks.length);
                                    tracks.forEach((t, idx) => {
                                        console.log(`[WebRTC]     Track ${idx}: id=${t.id}, enabled=${t.enabled}, muted=${t.muted}, readyState=${t.readyState}`);
                                    });
                                } else {
                                    console.error('[WebRTC] ❌ Stream is NULL!');
                                }
                                
                                const verificationMessage = `[WebRTC] ===== AUDIO PLAYBACK VERIFICATION AFTER 500ms ===== currentTime=${initialTime}, paused=${isPaused}, muted=${isMuted}, volume=${volume}, sinkId=${sinkId}, readyState=${readyState}, streamActive=${stream?.active || false}, tracks=${stream?.getAudioTracks().length || 0}`;
                                
                                if (initialTime === 0 && isPaused) {
                                    console.error('[WebRTC] ❌❌❌ AUDIO NOT PLAYING: currentTime=0 and paused=true after 500ms! ❌❌❌');
                                    sendEvent({
                                        type: 'js_log',
                                        data: {
                                            level: 'critical',
                                            message: `${verificationMessage} - ❌❌❌ AUDIO NOT PLAYING: currentTime=0 and paused=true! ❌❌❌`
                                        }
                                    });
                                } else if (isPaused) {
                                    console.error('[WebRTC] ❌❌❌ AUDIO IS PAUSED after 500ms! ❌❌❌');
                                    sendEvent({
                                        type: 'js_log',
                                        data: {
                                            level: 'critical',
                                            message: `${verificationMessage} - ❌❌❌ AUDIO IS PAUSED! ❌❌❌`
                                        }
                                    });
                                } else {
                                    console.log('[WebRTC] ✅ Audio playback verified: currentTime=', initialTime, ', paused=', isPaused);
                                    sendEvent({
                                        type: 'js_log',
                                        data: {
                                            level: 'critical',
                                            message: `${verificationMessage} - ✅ Audio playback verified`
                                        }
                                    });
                                    
                                    // Еще одна проверка через 2 секунды - если currentTime все еще 0, трек не передает данные
                                    setTimeout(() => {
                                        const checkTime2s = Date.now();
                                        const laterTime = remoteAudio.currentTime;
                                        const laterPaused = remoteAudio.paused;
                                        const laterMuted = remoteAudio.muted;
                                        const laterVolume = remoteAudio.volume;
                                        const laterSinkId = remoteAudio.sinkId || 'default';
                                        const laterReadyState = remoteAudio.readyState;
                                        
                                        console.log('[WebRTC] ===== AUDIO PLAYBACK VERIFICATION AFTER 2 SECONDS =====');
                                        console.log('[WebRTC] Check time:', checkTime2s);
                                        console.log('[WebRTC] Time elapsed since play():', checkTime2s - playStartTime, 'ms');
                                        console.log('[WebRTC] Audio element state:');
                                        console.log('[WebRTC]   - currentTime:', laterTime, '(was', initialTime, 'at 500ms)');
                                        console.log('[WebRTC]   - paused:', laterPaused);
                                        console.log('[WebRTC]   - muted:', laterMuted);
                                        console.log('[WebRTC]   - volume:', laterVolume);
                                        console.log('[WebRTC]   - sinkId:', laterSinkId);
                                        console.log('[WebRTC]   - readyState:', laterReadyState);
                                        
                                        const stream2s = remoteAudio.srcObject;
                                        if (stream2s) {
                                            const tracks2s = stream2s.getAudioTracks();
                                            console.log('[WebRTC] Stream state after 2s:');
                                            console.log('[WebRTC]   - stream active:', stream2s.active);
                                            console.log('[WebRTC]   - stream tracks:', tracks2s.length);
                                            tracks2s.forEach((t, idx) => {
                                                console.log(`[WebRTC]     Track ${idx}: id=${t.id}, enabled=${t.enabled}, muted=${t.muted}, readyState=${t.readyState}`);
                                            });
                                        }
                                        
                                        const timeDelta = laterTime - initialTime;
                                        const verification2sMessage = `[WebRTC] ===== AUDIO PLAYBACK VERIFICATION AFTER 2 SECONDS ===== currentTime=${laterTime} (delta=${timeDelta.toFixed(3)}s), paused=${laterPaused}, muted=${laterMuted}, volume=${laterVolume}, sinkId=${laterSinkId}, readyState=${laterReadyState}, streamActive=${stream2s?.active || false}, tracks=${stream2s?.getAudioTracks().length || 0}`;
                                        
                                        if (laterTime === 0 && !laterPaused) {
                                            console.error('[WebRTC] ❌❌❌ AUDIO ELEMENT IS PLAYING BUT NO DATA RECEIVED! ❌❌❌');
                                            console.error('[WebRTC] ❌ currentTime is still 0 after 2 seconds - track is NOT transmitting audio data!');
                                            sendEvent({
                                                type: 'js_log',
                                                data: {
                                                    level: 'critical',
                                                    message: `${verification2sMessage} - ❌❌❌ AUDIO ELEMENT IS PLAYING BUT NO DATA RECEIVED! currentTime is still 0 after 2 seconds - track is NOT transmitting audio data! ❌❌❌`
                                                }
                                            });
                                        } else if (timeDelta === 0 && !laterPaused) {
                                            console.error('[WebRTC] ❌❌❌ AUDIO ELEMENT IS PLAYING BUT currentTime NOT INCREASING! ❌❌❌');
                                            console.error('[WebRTC] ❌ currentTime did not change after 2 seconds - track may not be transmitting audio data!');
                                            sendEvent({
                                                type: 'js_log',
                                                data: {
                                                    level: 'critical',
                                                    message: `${verification2sMessage} - ❌❌❌ AUDIO ELEMENT IS PLAYING BUT currentTime NOT INCREASING! currentTime did not change after 2 seconds! ❌❌❌`
                                                }
                                            });
                                        } else {
                                            console.log('[WebRTC] ✅ Audio data confirmed: currentTime=', laterTime, 'after 2 seconds (delta:', timeDelta.toFixed(3), 's)');
                                            sendEvent({
                                                type: 'js_log',
                                                data: {
                                                    level: 'critical',
                                                    message: `${verification2sMessage} - ✅ Audio data confirmed: currentTime increased by ${timeDelta.toFixed(3)}s`
                                                }
                                            });
                                        }
                                    }, 2000);
                                }
                            }, 500);
                        }
                    } else {
                        console.warn('[WebRTC] ⚠️ play() returned undefined (may not be supported)');
                    }
                } catch (playErr) {
                    console.error('[WebRTC] ❌❌❌ REMOTE AUDIO PLAY() FAILED ❌❌❌');
                    console.error('[WebRTC] ❌ Error:', playErr);
                    console.error('[WebRTC] ❌ This will prevent you from hearing the remote party!');
                    console.error('[WebRTC] ❌ Audio element state at error:');
                    console.error('[WebRTC]   - paused:', remoteAudio.paused);
                    console.error('[WebRTC]   - muted:', remoteAudio.muted);
                    console.error('[WebRTC]   - volume:', remoteAudio.volume);
                    console.error('[WebRTC]   - sinkId:', remoteAudio.sinkId || 'default (not set)');
                    console.error('[WebRTC]   - error:', remoteAudio.error);
                    
                    // КРИТИЧНО: Отправляем js_log об ошибке play()
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ❌❌❌ REMOTE AUDIO PLAY() FAILED ❌❌❌ Error: ${playErr.message || playErr}, paused=${remoteAudio.paused}, muted=${remoteAudio.muted}`
                        }
                    });
                    
                    // Пробуем еще раз через небольшую задержку
                    setTimeout(async () => {
                        try {
                            console.log('[WebRTC] Retrying play() after error...');
                            sendEvent({
                                type: 'js_log',
                                data: {
                                    level: 'critical',
                                    message: `[WebRTC] Retrying play() after error...`
                                }
                            });
                            await remoteAudio.play();
                            console.log('[WebRTC] ✅ Retry play() succeeded');
                            
                            sendEvent({
                                type: 'js_log',
                                data: {
                                    level: 'critical',
                                    message: `[WebRTC] ✅ Retry play() succeeded`
                                }
                            });
                            
                            // Отправляем audio_connected после успешного retry
                            sendEvent({ 
                                type: 'audio_connected', 
                                data: { 
                                    sessionId: sessionId,
                                    trackId: track.id,
                                    source: 'connectAudioTrack_play_retry_success'
                                } 
                            });
                        } catch (retryErr) {
                            console.error('[WebRTC] ❌ Retry play() failed:', retryErr);
                            sendEvent({
                                type: 'js_log',
                                data: {
                                    level: 'critical',
                                    message: `[WebRTC] ❌ Retry play() failed: ${retryErr.message || retryErr}`
                                }
                            });
                        }
                    }, 1000);
                    
                    sendEvent({ 
                        type: 'error', 
                        data: { 
                            name: 'AudioPlayError',
                            message: 'Failed to play remote audio: ' + playErr.message,
                            phase: 'connectAudioTrack',
                            stack: playErr.stack,
                            trackId: track.id,
                            sessionId: sessionId
                        } 
                    });
                }
                
                // КРИТИЧНО: Для исходящих звонков, когда удаленный аудио трек подключен и играет,
                // это означает, что звонок принят - отправляем call_accepted
                if (originator === 'local' && track.readyState === 'live' && track.enabled) {
                    console.log('[WebRTC] ⚠️⚠️⚠️ CALL ACCEPTED - REMOTE AUDIO IS LIVE ⚠️⚠️⚠️');
                    console.log('[WebRTC] ⚠️ Ringback tone MUST be stopped now!');
                    console.log('[WebRTC] ⚠️ Sending call_accepted event...');
                    
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ⚠️⚠️⚠️ CALL ACCEPTED - REMOTE AUDIO IS LIVE (from connectAudioTrack) ⚠️⚠️⚠️`
                        }
                    });
                    
                    sendCallAcceptedOnce('audio_connected_live');
                } else {
                    console.log('[WebRTC] ⚠️ Call not yet accepted (track not ready):');
                    console.log('[WebRTC]   - originator:', originator, '(should be "local" for outgoing)');
                    console.log('[WebRTC]   - readyState:', track.readyState, '(should be "live")');
                    console.log('[WebRTC]   - enabled:', track.enabled, '(should be true)');
                    
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ⚠️ Call not yet accepted: originator=${originator}, readyState=${track.readyState}, enabled=${track.enabled}`
                        }
                    });
                }
                
                console.log('[WebRTC] ===== REMOTE AUDIO TRACK CONNECTION COMPLETE =====');
                
                // КРИТИЧНО: Отправляем js_log о завершении подключения
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ===== REMOTE AUDIO TRACK CONNECTION COMPLETE ===== TrackID=${track.id}, playPromise resolved`
                    }
                });
            } catch (err) {
                console.error('[WebRTC] ❌❌❌ ERROR in connectAudioTrack ❌❌❌');
                console.error('[WebRTC] ❌ Error:', err);
                console.error('[WebRTC] ❌ This will prevent you from hearing the remote party!');
                
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ❌❌❌ ERROR in connectAudioTrack ❌❌❌ Error: ${err.message || err}`
                    }
                });
                
                sendEvent({ 
                    type: 'error', 
                    data: { 
                        name: 'ConnectAudioTrackError',
                        message: 'Error connecting audio track: ' + err.message,
                        phase: 'connectAudioTrack',
                        stack: err.stack,
                        trackId: track.id,
                        sessionId: sessionId
                    } 
                });
            }
        };
        
        // Сохраняем функцию connectAudioTrack в сессии для доступа из других мест
        s._connectAudioTrack = connectAudioTrack;
        
        const newSessionPayload = {
            sessionId: sessionId,
            originator: originator,
            direction: originator === 'local' ? 'outgoing' : 'incoming'
        };
        if (originator === 'remote') {
            try {
                const caller = s.remote_identity?.uri?.user;
                if (caller) newSessionPayload.callerNumber = caller;
            } catch { }
        }

        sendEvent({ 
            type: 'new_session', 
            data: newSessionPayload
        });
        
        // КРИТИЧНО: Логируем ВСЕ события JsSIP для диагностики проблем со звонками
        const logJSSIPEvent = (eventName, eventData) => {
            console.log(`[WebRTC] ⚠️⚠️⚠️ JSSIP EVENT: ${eventName} ⚠️⚠️⚠️`);
            console.log(`[WebRTC] Session ID: ${sessionId}`);
            console.log(`[WebRTC] Originator: ${originator}`);
            console.log(`[WebRTC] Event data:`, eventData);
            
            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] ⚠️⚠️⚠️ JSSIP EVENT: ${eventName} ⚠️⚠️⚠️ SessionID=${sessionId}, Originator=${originator}`
                }
            });
        };

        /** Send long SDP to C# host in chunks (WebRTC answers often exceed 3000 chars). */
        const emitSdpChunksToHost = (label, sdp, maxChunks) => {
            if (!sdp || typeof sdp !== 'string') return;
            sdp = redactSdpForLog(sdp);
            const chunkSize = 1800;
            const limit = maxChunks != null ? maxChunks : 24;
            let offset = 0;
            let part = 0;
            const totalParts = Math.min(limit, Math.ceil(sdp.length / chunkSize));
            while (offset < sdp.length && part < limit) {
                const slice = sdp.substring(offset, offset + chunkSize);
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ${label} chunk ${part + 1}/${totalParts}: ${slice}`
                    }
                });
                offset += chunkSize;
                part++;
            }
            if (offset < sdp.length) {
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ${label}: ... truncated (sent ${offset} of ${sdp.length} bytes). Increase maxChunks if needed.`
                    }
                });
            }
        };
        
        // КРИТИЧНО: Логируем отправку SIP запросов
        s.on('sending', (request) => {
            console.log('[WebRTC] ⚠️⚠️⚠️ JSSIP SENDING REQUEST ⚠️⚠️⚠️');
            console.log('[WebRTC] Session ID:', sessionId);
            console.log('[WebRTC] Request method:', request?.method || 'N/A');
            console.log('[WebRTC] Request URI:', request?.ruri?.toString() || 'N/A');
            console.log('[WebRTC] Request headers:', request?.getHeaders ? Object.keys(request.getHeaders()) : 'N/A');
            try {
                const raw = request?.toString ? String(request.toString()) : '';
                const safe = redactSipForLog(raw);
                console.log('[WebRTC] Full request (redacted):', safe.substring(0, 500));
            } catch {
                console.log('[WebRTC] Full request (redacted):', 'N/A');
            }
            
            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] ⚠️⚠️⚠️ JSSIP SENDING REQUEST ⚠️⚠️⚠️ Method=${request?.method || 'N/A'}, URI=${request?.ruri?.toString() || 'N/A'}, SessionID=${sessionId}`
                }
            });
        });
        
        // КРИТИЧНО: Логируем получение SIP ответов
        s.on('newInfo', (info) => {
            console.log('[WebRTC] ⚠️⚠️⚠️ JSSIP NEW INFO (SIP response) ⚠️⚠️⚠️');
            console.log('[WebRTC] Session ID:', sessionId);
            console.log('[WebRTC] Info:', info);
            
            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] ⚠️⚠️⚠️ JSSIP NEW INFO ⚠️⚠️⚠️ SessionID=${sessionId}, Info=${JSON.stringify(info)}`
                }
            });
        });
        
        // КРИТИЧНО: Логируем получение SIP ответов через request/response
        if (s.request) {
            s.request.on('onRequestTimeout', () => {
                console.error('[WebRTC] ❌❌❌ SIP REQUEST TIMEOUT ❌❌❌');
                console.error('[WebRTC] Session ID:', sessionId);
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ❌❌❌ SIP REQUEST TIMEOUT ❌❌❌ SessionID=${sessionId} - No response from PBX!`
                    }
                });
            });
        }
        
        // Подписываемся на все возможные события JsSIP для диагностики
        // ВАЖНО: Некоторые события могут уже быть подписаны ниже, но мы добавляем логирование здесь для полноты
        s.on('sdpCreated', (e) => {
            logJSSIPEvent('sdpCreated', e);
        });
        
        s.on('getDescription', (e) => {
            logJSSIPEvent('getDescription', e);
        });
        
        s.on('setDescription', (e) => {
            logJSSIPEvent('setDescription', e);
        });
        
        s.on('peerconnection', (e) => {
            logJSSIPEvent('peerconnection', e);
            
            // КРИТИЧНО: Добавляем обработчики для отслеживания отключения локального микрофона
            // Локальный поток создается автоматически JsSIP при ua.call с mediaConstraints
            try {
                if (s.connection && s.connection.getLocalStreams) {
                    const localStreams = s.connection.getLocalStreams();
                    localStreams.forEach(stream => {
                        const localAudioTracks = stream.getAudioTracks();
                        localAudioTracks.forEach(track => {
                            // Проверяем, не добавлен ли уже обработчик (избегаем дублирования)
                            if (!track._localMicHandlerAdded) {
                                track._localMicHandlerAdded = true;
                                
                                track.addEventListener('ended', () => {
                                    console.error('[WebRTC] ❌❌❌ LOCAL AUDIO TRACK ENDED! Microphone disconnected! ❌❌❌');
                                    sendEvent({
                                        type: 'error',
                                        data: {
                                            name: 'LocalMicDisconnected',
                                            message: 'Local microphone track ended (microphone may have been disconnected)',
                                            phase: 'peerconnection',
                                            trackId: track.id,
                                            sessionId: sessionId
                                        }
                                    });
                                });
                                
                                track.addEventListener('mute', () => {
                                    console.warn('[WebRTC] ⚠️ Local audio track was MUTED');
                                    sendEvent({
                                        type: 'js_log',
                                        data: {
                                            level: 'warning',
                                            message: `[WebRTC] Local microphone track muted - SessionId=${sessionId}, TrackId=${track.id}`
                                        }
                                    });
                                });
                                
                                track.addEventListener('unmute', () => {
                                    console.log('[WebRTC] ✅ Local audio track was UNMUTED');
                                });
                                
                                console.log(`[WebRTC] ✅ Local microphone track monitoring added for track ${track.id}`);
                            }
                        });
                    });
                }
            } catch (err) {
                console.warn('[WebRTC] Error adding local track handlers:', err);
            }

            // Helpful diagnostics: TURN failures / STUN errors can explain delayed media start.
            try {
                const pc = s.connection;
                if (pc && !pc._iceCandidateErrorWired) {
                    pc._iceCandidateErrorWired = true;
                    pc.addEventListener('icecandidateerror', (ev) => {
                        const msg = `[WebRTC] icecandidateerror: url=${ev?.url || 'n/a'} code=${ev?.errorCode || 'n/a'} text=${ev?.errorText || 'n/a'} address=${ev?.address || 'n/a'}:${ev?.port || 'n/a'}`;
                        console.warn(msg);
                        sendEvent({ type: 'js_log', data: { level: 'critical', message: msg } });
                    });
                }
            } catch (err) {
                // ignore
            }
        });
        
        // Подписываемся на s.on('icecandidate') с УМНЫМ таймаутом:
        // - Без этого обработчика JsSIP ждёт iceGatheringState='complete' перед отправкой INVITE.
        //   Если STUN/TURN-сервер недоступен, это может занять 30+ секунд.
        // - С обработчиком мы контролируем момент отправки INVITE/200 OK:
        //   1) Первый пригодный кандидат (relay в relay-only режиме, иначе srflx) — отправляем сразу
        //   2) Если gathering завершился раньше — отправляем сразу
        //   3) По таймауту — отправляем с тем, что есть; в relay-only режиме сначала
        //      дожидаемся TURN allocation, потому что SDP без кандидатов гарантирует ICE timeout
        {
            let iceReadyCalled = false;
            let lastReadyFn = null;
            let hasSrflx = false;
            let hasRelay = false;
            let candidateCount = 0;
            let gatherTimer = null;
            const relayOnly = isRelayOnlyMode();
            const gatherStartedAt = Date.now();
            // Incoming / PBX originate legs should answer quickly (PBX waits for 200 OK). Outbound INVITE can wait a bit longer for srflx.
            const baseGatherTimeoutMs = originator === 'remote' ? 1600 : 3000;
            // В relay-only режиме srflx-кандидатов не бывает, а холодный TURN allocation
            // (DNS + Allocate) может не уложиться в базовое окно. Ответ без кандидатов
            // гарантированно заканчивается ICE timeout, поэтому ждём дольше — таймер
            // всё равно обрывается сразу, как только придёт relay-кандидат.
            const ICE_GATHER_TIMEOUT_MS = relayOnly ? Math.max(baseGatherTimeoutMs, 4000) : baseGatherTimeoutMs;
            const ICE_GATHER_MAX_WAIT_MS = relayOnly ? 9000 : ICE_GATHER_TIMEOUT_MS;

            const callReady = (reason) => {
                if (iceReadyCalled) return;
                iceReadyCalled = true;
                if (gatherTimer) {
                    clearTimeout(gatherTimer);
                    gatherTimer = null;
                }
                const msg = `[WebRTC] icecandidate: Calling e.ready() — reason: ${reason}, candidates: ${candidateCount}, hasSrflx: ${hasSrflx}, hasRelay: ${hasRelay}, relayOnly: ${relayOnly}`;
                console.log(msg);
                sendEvent({ type: 'js_log', data: { level: 'critical', message: msg } });
                if (lastReadyFn) lastReadyFn();
            };

            const onGatherTimeout = () => {
                const waitedMs = Date.now() - gatherStartedAt;
                if (relayOnly && candidateCount === 0 && waitedMs < ICE_GATHER_MAX_WAIT_MS) {
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] icecandidate: still 0 candidates after ${waitedMs}ms, waiting for TURN allocation (max ${ICE_GATHER_MAX_WAIT_MS}ms)`
                        }
                    });
                    gatherTimer = setTimeout(onGatherTimeout, Math.min(1000, ICE_GATHER_MAX_WAIT_MS - waitedMs));
                    return;
                }
                callReady(`${waitedMs}ms timeout (TURN/STUN may be unreachable)`);
            };

            gatherTimer = setTimeout(onGatherTimeout, ICE_GATHER_TIMEOUT_MS);

            s.on('icecandidate', (evt) => {
                lastReadyFn = evt.ready; // всегда обновляем, чтобы callReady() мог вызвать последний ready()

                if (!evt.candidate) {
                    // null candidate = gathering complete
                    callReady('ICE gathering complete (null candidate)');
                    return;
                }

                candidateCount++;
                const c = evt.candidate.candidate || '';
                const originStr = typeof originator === 'string' ? originator : String(originator || '');
                const dirStr = s && s.direction != null ? String(s.direction) : '';

                if (c.includes('srflx')) {
                    hasSrflx = true;
                    console.log(`[WebRTC] icecandidate: srflx found! Will send INVITE shortly.`);
                    // Logs showed ~3s gap is mostly media-path buffering, not SIP 200 OK timing — keep srflx path snappy.
                    if (!relayOnly) {
                        callReady(`srflx candidate found (origin=${originStr} dir=${dirStr})`);
                    }
                    return;
                }

                if (c.includes('typ relay')) {
                    hasRelay = true;
                    console.log(`[WebRTC] icecandidate: relay found! Will send INVITE/answer shortly.`);
                    // В relay-only режиме единственный пригодный кандидат — relay, ждать остальные незачем.
                    callReady(`relay candidate found (origin=${originStr} dir=${dirStr})`);
                }
            });
        }

        s.on('progress', (e) => {
            console.log('[WebRTC] ⚠️⚠️⚠️ JSSIP PROGRESS EVENT (180 Ringing) ⚠️⚠️⚠️');
            console.log('[WebRTC] Session ID:', sessionId);
            console.log('[WebRTC] Originator:', originator);
            console.log('[WebRTC] Event:', e);
            console.log('[WebRTC] ⚠️ This means PBX is ringing the remote party!');
            
            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] ⚠️⚠️⚠️ JSSIP PROGRESS EVENT (180 Ringing) ⚠️⚠️⚠️ SessionID=${sessionId}, Originator=${originator} - PBX is ringing remote party!`
                }
            });
            
            if (progressTimeout) {
                clearTimeout(progressTimeout);
                progressTimeout = null;
            }
            // Для исходящих звонков отправляем событие "ringing" для воспроизведения ringback tone.
            // PBX Originate uses an incoming INVITE callback (originator=remote) — treat as outbound ringing too.
            const isOriginateCallback =
                originator === 'remote' && (
                    !!window._callspireOriginatePending ||
                    window._callspireOriginateAcceptedSessionId === sessionId ||
                    !!s._softphoneOriginateId
                );
            if (originator === 'local' || isOriginateCallback) {
                console.log('[WebRTC] Outgoing/Originate call - sending ringing event to start ringback tone');
                sendEvent({ 
                    type: 'ringing',
                    data: { sessionId: sessionId, originate: isOriginateCallback }
                });
                
                // Таймаут для диагностики состояния звонка через 5 секунд
                // НЕ вызываем sendCallAcceptedOnce — ждём реального 200 OK (accepted/confirmed от JsSIP)
                setTimeout(() => {
                    try {
                        if (!callAcceptedSent && s.connection) {
                            const pc = s.connection;
                            const iceState = pc.iceConnectionState;
                            const connState = pc.connectionState;
                            console.log(`[WebRTC] TIMEOUT CHECK: call_accepted not sent after 5s. ICE=${iceState}, Connection=${connState}`);
                            sendEvent({
                                type: 'js_log',
                                data: {
                                    level: 'critical',
                                    message: `[WebRTC] TIMEOUT CHECK (5s): call_accepted NOT sent. ICE=${iceState}, Connection=${connState}. Waiting for 200 OK from PBX...`
                                }
                            });
                            
                            // Только если ICE реально подключён (200 OK пришёл, SDP answer установлен)
                            // тогда можно считать звонок принятым
                            if (iceState === 'connected' || iceState === 'completed') {
                                console.log('[WebRTC] TIMEOUT CHECK: ICE is connected, sending call_accepted');
                                sendCallAcceptedOnce('timeout_check_ice_connected');
                                
                                // Подключаем треки через connectAudioTrack
                                const transceivers = pc.getTransceivers();
                                transceivers.forEach((transceiver, idx) => {
                                    if (transceiver.receiver && transceiver.receiver.track && 
                                        transceiver.receiver.track.kind === 'audio') {
                                        const track = transceiver.receiver.track;
                                        console.log(`[WebRTC] TIMEOUT CHECK: Connecting track ${idx}: ${track.id} (readyState=${track.readyState})`);
                                        connectAudioTrack(track).then(() => {
                                            console.log(`[WebRTC] TIMEOUT CHECK: Track ${track.id} connected`);
                                        }).catch((err) => {
                                            console.error(`[WebRTC] TIMEOUT CHECK: Error connecting track:`, err);
                                        });
                                    }
                                });
                            } else {
                                // ICE НЕ подключён — 200 OK от PBX ещё не пришёл
                                console.log(`[WebRTC] TIMEOUT CHECK: ICE not connected (${iceState}), call still ringing. Waiting for 200 OK...`);
                                sendEvent({
                                    type: 'js_log',
                                    data: {
                                        level: 'critical',
                                        message: `[WebRTC] TIMEOUT CHECK: ICE=${iceState}, no 200 OK yet. NOT sending false call_accepted.`
                                    }
                                });
                            }
                        }
                    } catch (timeoutErr) {
                        console.error('[WebRTC] Error in timeout check:', timeoutErr);
                    }
                }, 5000); // 5 секунд после progress

                // 15-секундная расширенная диагностика
                setTimeout(() => {
                    try {
                        const pc = s.connection;
                        if (!pc) return;
                        const iceState = pc.iceConnectionState;
                        const connState = pc.connectionState;
                        const gatherState = pc.iceGatheringState;
                        const sigState = pc.signalingState;
                        const localSDP = pc.localDescription ? pc.localDescription.type : 'none';
                        const remoteSDP = pc.remoteDescription ? pc.remoteDescription.type : 'none';
                        
                        const diag = `[WebRTC] ⏱️ 15s DIAGNOSTIC: ICE=${iceState}, Conn=${connState}, Gather=${gatherState}, Signaling=${sigState}, LocalSDP=${localSDP}, RemoteSDP=${remoteSDP}`;
                        console.log(diag);
                        sendEvent({ type: 'js_log', data: { level: 'critical', message: diag } });

                        if (remoteSDP === 'none') {
                            const noAnswer = `[WebRTC] ❌❌❌ NO SDP ANSWER AFTER 15s! PBX did NOT send 200 OK. Possible causes: 1) ICE negotiation failed on PBX (client needs STUN/TURN), 2) PBX WebRTC/DTLS misconfigured, 3) Firewall blocking UDP`;
                            console.error(noAnswer);
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: noAnswer } });
                        }

                        // Логируем локальный SDP для проверки кандидатов
                        if (pc.localDescription && pc.localDescription.sdp) {
                            const sdp = pc.localDescription.sdp;
                            const sdpLines = sdp.split('\n');
                            const candidates = sdpLines.filter(l => l.startsWith('a=candidate:'));
                            const hostCount = candidates.filter(l => l.includes('typ host')).length;
                            const srflxCount = candidates.filter(l => l.includes('typ srflx')).length;
                            const relayCount = candidates.filter(l => l.includes('typ relay')).length;
                            const candidateSummary = `[WebRTC] ⏱️ 15s: Local SDP candidates: total=${candidates.length}, host=${hostCount}, srflx=${srflxCount}, relay=${relayCount}`;
                            console.log(candidateSummary);
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: candidateSummary } });
                            
                            if (srflxCount === 0 && relayCount === 0) {
                                const noPublic = `[WebRTC] ❌ NO PUBLIC CANDIDATES! Only host (private IP) candidates found. PBX cannot reach client. Need STUN or TURN server.`;
                                console.error(noPublic);
                                sendEvent({ type: 'js_log', data: { level: 'critical', message: noPublic } });
                            }
                        }
                    } catch (err) {
                        console.error('[WebRTC] Error in 15s diagnostic:', err);
                    }
                }, 15000);
            }
            // Также отправляем call_progress для совместимости
            sendEvent({ 
                type: 'call_progress',
                data: { sessionId: sessionId }
            });
        });
        
        // Добавляем обработку события 'sdp' для отладки SDP negotiation
        s.on('sdp', (e) => {
            const sdpType = e?.type || 'N/A';
            const sdpContent = e?.sdp || 'no sdp';
            console.log(`[WebRTC] JSSIP SDP EVENT: type=${sdpType}, SessionID=${sessionId}`);
            console.log('[WebRTC] Full SDP:\n' + sdpContent);

            // Подсчитываем типы ICE кандидатов в SDP
            if (sdpContent && sdpContent !== 'no sdp') {
                const lines = sdpContent.split('\n');
                const hostCandidates = lines.filter(l => l.includes('typ host')).length;
                const srflxCandidates = lines.filter(l => l.includes('typ srflx')).length;
                const relayCandidates = lines.filter(l => l.includes('typ relay')).length;
                const candidateSummary = `host=${hostCandidates}, srflx=${srflxCandidates}, relay=${relayCandidates}`;

                if (sdpType === 'answer') {
                    console.log(`[WebRTC] 🎉🎉🎉 SDP ANSWER RECEIVED (200 OK from PBX!) 🎉🎉🎉`);
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] 🎉 SDP ANSWER RECEIVED (200 OK!) SessionID=${sessionId}, Candidates: ${candidateSummary}`
                        }
                    });
                } else {
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] SDP EVENT: type=${sdpType}, SessionID=${sessionId}, Candidates: ${candidateSummary}`
                        }
                    });
                }

                const sdpLabel = `SDP type=${sdpType} SessionID=${sessionId}`;
                emitSdpChunksToHost(sdpLabel, sdpContent, 24);
            } else {
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] SDP EVENT: type=${sdpType}, SessionID=${sessionId} (no SDP content)`
                    }
                });
            }
        });
        
        // КРИТИЧНО: Логирование ICE кандидатов для диагностики NAT/Firewall проблем
        if (s.connection) {
            s.connection.onicecandidate = (event) => {
                if (event.candidate) {
                    console.log('[WebRTC] ICE candidate found:', {
                        candidate: event.candidate.candidate,
                        sdpMLineIndex: event.candidate.sdpMLineIndex,
                        sdpMid: event.candidate.sdpMid
                    });
                    
                    // Логируем тип кандидата для диагностики
                    const candidateStr = event.candidate.candidate;
                    if (candidateStr.includes('typ host')) {
                        console.log('[WebRTC] ⚠️ Host candidate (local IP) - may not work through NAT');
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] ⚠️ ICE Host candidate (local IP): ${candidateStr.substring(0, 100)} - may not work through NAT`
                            }
                        });
                    } else if (candidateStr.includes('typ srflx')) {
                        console.log('[WebRTC] ✅ Server reflexive candidate (STUN) - should work through NAT');
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] ✅ ICE Server reflexive candidate (STUN): ${candidateStr.substring(0, 100)} - should work through NAT`
                            }
                        });
                    } else if (candidateStr.includes('typ relay')) {
                        console.log('[WebRTC] ✅ Relay candidate (TURN) - should work through strict NAT');
                        sendEvent({
                            type: 'js_log',
                            data: {
                                level: 'critical',
                                message: `[WebRTC] ✅ ICE Relay candidate (TURN): ${candidateStr.substring(0, 100)} - should work through strict NAT`
                            }
                        });
                    }
                } else {
                    console.log('[WebRTC] ✅ All ICE candidates gathered');
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ✅ All ICE candidates gathered for session ${sessionId}`
                        }
                    });
                }
            };

            // Отслеживаем состояние ICE gathering (new → gathering → complete)
            s.connection.onicegatheringstatechange = () => {
                const gatherState = s.connection.iceGatheringState;
                console.log(`[WebRTC] ICE Gathering state: ${gatherState}`);
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ICE Gathering state: ${gatherState}, SessionID=${sessionId}`
                    }
                });
            };
            
            s.connection.oniceconnectionstatechange = () => {
                const iceState = s.connection.iceConnectionState;
                console.log('[WebRTC] ⚠️⚠️⚠️ ICE CONNECTION STATE CHANGED ⚠️⚠️⚠️');
                console.log('[WebRTC] Session ID:', sessionId);
                console.log('[WebRTC] ICE Connection State:', iceState);
                console.log('[WebRTC] Connection State:', s.connection.connectionState);
                
                // Notify host app (C#) so UI can synchronize ringback/connected state with real media readiness.
                sendEvent({
                    type: 'ice_connection_state_change',
                    data: {
                        sessionId: sessionId,
                        state: iceState,
                        connectionState: s.connection.connectionState
                    }
                });

                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ⚠️⚠️⚠️ ICE CONNECTION STATE CHANGED ⚠️⚠️⚠️ SessionID=${sessionId}, ICEState=${iceState}, ConnectionState=${s.connection.connectionState}`
                    }
                });
                
                if (iceState === 'failed') {
                    console.error('[WebRTC] ❌❌❌ ICE CONNECTION FAILED ❌❌❌');
                    console.error('[WebRTC] ❌ This usually means NAT traversal failed - check STUN servers!');
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ❌❌❌ ICE CONNECTION FAILED ❌❌❌ This usually means NAT traversal failed - check STUN servers!`
                        }
                    });
                } else if (iceState === 'connected' || iceState === 'completed') {
                    console.log('[WebRTC] ✅✅✅ ICE CONNECTION ESTABLISHED ✅✅✅');
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ✅✅✅ ICE CONNECTION ESTABLISHED ✅✅✅ ICEState=${iceState}`
                        }
                    });
                }
            };
            
            s.connection.onconnectionstatechange = () => {
                const connState = s.connection.connectionState;
                const iceState = s.connection.iceConnectionState;
                console.log('[WebRTC] ⚠️⚠️⚠️ CONNECTION STATE CHANGED ⚠️⚠️⚠️');
                console.log('[WebRTC] Session ID:', sessionId);
                console.log('[WebRTC] Connection State:', connState);
                console.log('[WebRTC] ICE Connection State:', iceState);
                
                sendEvent({
                    type: 'js_log',
                    data: {
                        level: 'critical',
                        message: `[WebRTC] ⚠️⚠️⚠️ CONNECTION STATE CHANGED ⚠️⚠️⚠️ SessionID=${sessionId}, ConnectionState=${connState}, ICEState=${iceState}`
                    }
                });
                
                if (connState === 'failed') {
                    console.error('[WebRTC] ❌❌❌ CONNECTION FAILED ❌❌❌');
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ❌❌❌ CONNECTION FAILED ❌❌❌ ConnectionState=${connState}, ICEState=${iceState}`
                        }
                    });
                } else if (connState === 'connected') {
                    console.log('[WebRTC] ✅✅✅ CONNECTION ESTABLISHED ✅✅✅');
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ✅✅✅ CONNECTION ESTABLISHED ✅✅✅ ConnectionState=${connState}, ICEState=${iceState}`
                        }
                    });
                }
            };
        }

        s.on('accepted', () => {
            console.log('[WebRTC] JSSIP ACCEPTED EVENT - call answered by remote party, SessionId:', sessionId);
            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] JSSIP ACCEPTED EVENT - SessionId=${sessionId} - 200 OK received from PBX!`
                }
            });
            
            sendCallAcceptedOnce('accepted_event');
        });

        s.on('confirmed', () => {
            console.log('[WebRTC] JSSIP CONFIRMED EVENT - call fully established, SessionId:', sessionId);
            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] JSSIP CONFIRMED EVENT - SessionId=${sessionId} - Call fully established (ACK exchanged)`
                }
            });
            
            sendCallAcceptedOnce('confirmed_event');
            // Также отправляем call_confirmed для совместимости
            sendEvent({ 
                type: 'call_confirmed',
                data: { sessionId: sessionId }
            });
        });

        // Добавляем таймаут для отслеживания отсутствия события progress
        let progressTimeout = null;
        if (originator === 'local') {
            progressTimeout = setTimeout(() => {
                console.warn('[WebRTC] WARNING: No progress event received for session:', sessionId, 'after 5 seconds');
                console.warn('[WebRTC] Session state:', {
                    status: s.status,
                    direction: s.direction,
                    localIdentity: s.local_identity?.uri,
                    remoteIdentity: s.remote_identity?.uri
                });
                // Проверяем состояние PeerConnection
                if (s.connection) {
                    console.warn('[WebRTC] PeerConnection state:', {
                        iceConnectionState: s.connection.iceConnectionState,
                        connectionState: s.connection.connectionState,
                        signalingState: s.connection.signalingState,
                        localDescription: s.connection.localDescription ? s.connection.localDescription.type : null,
                        remoteDescription: s.connection.remoteDescription ? s.connection.remoteDescription.type : null
                    });
                }
            }, 5000);
        }
        
        s.on('failed', (e) => {
            console.error('[WebRTC] ⚠️⚠️⚠️ JSSIP FAILED EVENT ⚠️⚠️⚠️');
            console.error('[WebRTC] Session ID:', sessionId);
            console.error('[WebRTC] Event:', e);
            console.error('[WebRTC] ⚠️ This means the call failed!');
            
            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] ⚠️⚠️⚠️ JSSIP FAILED EVENT ⚠️⚠️⚠️ SessionID=${sessionId} - Call failed!`
                }
            });
            
            if (progressTimeout) {
                clearTimeout(progressTimeout);
                progressTimeout = null;
            }
            // Prevent synthetic call_ended from PeerConnection 'closed' after a failed dialog.
            s._softphoneHostCallEndedSent = true;
            // Извлекаем безопасные строковые значения из события failed
            const causeStr = e?.cause ? (typeof e.cause === 'string' ? e.cause : (e.cause.toString ? e.cause.toString() : String(e.cause))) : null;
            const originatorStr = e?.originator ? (typeof e.originator === 'string' ? e.originator : String(e.originator)) : null;
            const statusCode = e?.response?.status_code ? (typeof e.response.status_code === 'number' ? e.response.status_code : parseInt(e.response.status_code) || null) : null;
            const reasonPhrase = e?.response?.reason_phrase ? (typeof e.response.reason_phrase === 'string' ? e.response.reason_phrase : String(e.response.reason_phrase)) : null;
            
            console.error('[WebRTC] Failed details:');
            console.error('[WebRTC]   - cause:', causeStr);
            console.error('[WebRTC]   - originator:', originatorStr);
            console.error('[WebRTC]   - status_code:', statusCode);
            console.error('[WebRTC]   - reason_phrase:', reasonPhrase);
            
            const messageStr = e?.message ? (typeof e.message === 'string' ? e.message : String(e.message)) : 'Call failed';

            sendEvent({
                type: 'js_log',
                data: {
                    level: 'critical',
                    message: `[WebRTC] Failed details: cause=${causeStr || 'N/A'}, originator=${originatorStr || 'N/A'}, status_code=${statusCode || 'N/A'}, reason_phrase=${reasonPhrase || 'N/A'}, message=${messageStr}`
                }
            });

            try {
                const pc = s.connection;
                if (pc) {
                    const summary = `[WebRTC] FAILED PC: ice=${pc.iceConnectionState}, conn=${pc.connectionState}, sig=${pc.signalingState}, localType=${pc.localDescription ? pc.localDescription.type : 'none'}, remoteType=${pc.remoteDescription ? pc.remoteDescription.type : 'none'}`;
                    sendEvent({ type: 'js_log', data: { level: 'critical', message: summary } });
                    const sdpMediaHint = (sdp) => {
                        if (!sdp) return '(no sdp)';
                        const audioLine = sdp.match(/^m=audio .*$/m);
                        const hasCrypto = /^a=crypto:/m.test(sdp);
                        const hasFingerprint = /^a=fingerprint:/m.test(sdp);
                        return `${audioLine ? audioLine[0] : 'no m=audio'} | SDES(a=crypto)=${hasCrypto} | DTLS(a=fingerprint)=${hasFingerprint}`;
                    };
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] FAILED SDP check — remote: ${sdpMediaHint(pc.remoteDescription && pc.remoteDescription.sdp)} || local: ${sdpMediaHint(pc.localDescription && pc.localDescription.sdp)}`
                        }
                    });
                    emitSdpChunksToHost('FAILED remote SDP', pc.remoteDescription && pc.remoteDescription.sdp, 24);
                    emitSdpChunksToHost('FAILED local SDP', pc.localDescription && pc.localDescription.sdp, 24);
                }
            } catch (diagErr) {
                console.warn('[WebRTC] failed-handler SDP diagnostics:', diagErr);
            }
            
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
            
            // КРИТИЧНО: Очищаем все сессии при неудаче звонка
            if (session && (session.id === sessionId || session === s)) {
                session = null;
            }
            if (window._activeSession && (window._activeSession.id === sessionId || window._activeSession === s)) {
                window._activeSession = null;
            }
            if (window._incomingSession && (window._incomingSession.id === sessionId || window._incomingSession === s)) {
                window._incomingSession = null;
            }
        });

        s.on('ended', (e) => {
            console.log('[WebRTC] Session ended:', sessionId, e);
            try {
                console.log('[WebRTC] Session ended - full event object:', JSON.stringify(e, null, 2));
            } catch (stringifyErr) {
                console.warn('[WebRTC] Session ended - event not JSON-serializable (non-fatal):', stringifyErr);
            }
            
            // Извлекаем безопасные строковые значения из события ended
            const causeStr = e?.cause ? (typeof e.cause === 'string' ? e.cause : (e.cause.toString ? e.cause.toString() : String(e.cause))) : null;
            const originatorStr = e?.originator ? (typeof e.originator === 'string' ? e.originator : String(e.originator)) : null;
            const messageStr = e?.message ? (typeof e.message === 'string' ? e.message : String(e.message)) : null;
            
            console.log('[WebRTC] Session ended - extracted values:', {
                cause: causeStr,
                originator: originatorStr,
                message: messageStr,
                sessionId: sessionId
            });
            
            // КРИТИЧНО: Останавливаем локальные медиа-стримы ПЕРЕД отправкой события
            try {
                if (s.localStream) {
                    s.localStream.getTracks().forEach(track => {
                        try {
                            track.stop();
                            console.log('[WebRTC] Stopped local media track:', track.kind);
                        } catch (trackError) {
                            console.warn('[WebRTC] Error stopping track:', trackError);
                        }
                    });
                }
            } catch (streamError) {
                console.warn('[WebRTC] Error stopping local stream:', streamError);
            }
            
            // КРИТИЧНО: Отправляем событие call_ended ПЕРЕД очисткой сессий
            // Это гарантирует, что событие будет отправлено на обе стороны
            const endedEventData = {
                sessionId: sessionId,
                cause: causeStr || 'Unknown',
                originator: originatorStr || 'local', // По умолчанию 'local', если не указан
                message: messageStr
            };
            
            console.log('[WebRTC] wireSessionEvents: Sending call_ended event with data:', endedEventData);
            if (!s._softphoneHostCallEndedSent) {
                s._softphoneHostCallEndedSent = true;
                sendEvent({ 
                    type: 'call_ended',
                    data: endedEventData
                });
                console.log('[WebRTC] wireSessionEvents: call_ended event sent for session:', sessionId, 'originator:', originatorStr || 'local');
            } else {
                console.log('[WebRTC] wireSessionEvents: call_ended already sent for session (e.g. PeerConnection closed); skipping duplicate');
            }
            
            // КРИТИЧНО: Очищаем все сессии при завершении звонка
            if (session && (session.id === sessionId || session === s)) {
                session = null;
                console.log('[WebRTC] wireSessionEvents: Cleared session');
            }
            if (window._activeSession && (window._activeSession.id === sessionId || window._activeSession === s)) {
                window._activeSession = null;
                console.log('[WebRTC] wireSessionEvents: Cleared window._activeSession');
            }
            if (window._incomingSession && (window._incomingSession.id === sessionId || window._incomingSession === s)) {
                window._incomingSession = null;
                console.log('[WebRTC] wireSessionEvents: Cleared window._incomingSession');
            }
        });

        // Hold/unhold notifications (local or remote). JsSIP emits these events when call hold state changes.
        // КРИТИЧНО: Обрабатываем события hold/unhold с учетом originator (local/remote)
        // Это важно для правильной обработки удаленных hold/unhold
        try {
            s.on('hold', (e) => {
                // Извлекаем originator из события (если доступен)
                const originator = e?.originator ? (typeof e.originator === 'string' ? e.originator : String(e.originator)) : 'local';
                console.log('[WebRTC] wireSessionEvents: JsSIP hold event received, originator:', originator, 'sending call_hold');
                console.log('[WebRTC] wireSessionEvents: JsSIP hold event - full event object:', JSON.stringify(e, null, 2));
                
                // КРИТИЧНО: Устанавливаем флаг, что событие было отправлено через обработчик JsSIP
                if (s) {
                    s._holdEventSent = true;
                    s._unholdEventSent = false; // Сбрасываем флаг unhold при hold
                }
                
                sendEvent({ 
                    type: 'call_hold', 
                    data: { 
                        sessionId: sessionId,
                        originator: originator
                    } 
                });
                console.log('[WebRTC] wireSessionEvents: call_hold event sent for session:', sessionId, 'originator:', originator);
            });
            s.on('unhold', (e) => {
                // Извлекаем originator из события (если доступен)
                const originator = e?.originator ? (typeof e.originator === 'string' ? e.originator : String(e.originator)) : 'local';
                console.log('[WebRTC] wireSessionEvents: JsSIP unhold event received, originator:', originator, 'sending call_unhold');
                console.log('[WebRTC] wireSessionEvents: JsSIP unhold event - full event object:', JSON.stringify(e, null, 2));
                
                // КРИТИЧНО: Устанавливаем флаг, что событие было отправлено через обработчик JsSIP
                if (s) {
                    s._unholdEventSent = true;
                }
                
                sendEvent({ 
                    type: 'call_unhold', 
                    data: { 
                        sessionId: sessionId,
                        originator: originator
                    } 
                });
                console.log('[WebRTC] wireSessionEvents: call_unhold event sent for session:', sessionId, 'originator:', originator);
            });
        } catch (e) {
            console.warn('[WebRTC] wireSessionEvents: hold/unhold handlers not supported:', e);
        }

        s.on('peerconnection', (e) => {
            // Подключаем входящий аудио трек к аудио элементу для воспроизведения
            console.log('[WebRTC] peerconnection event received for session:', sessionId);
            try {
                const pc = e.peerconnection;
                console.log('[WebRTC] PeerConnection state:', pc.connectionState, 'ICE state:', pc.iceConnectionState);

                const trySyntheticCallEndedFromPc = (reason) => {
                    if (s._softphoneHostCallEndedSent) return;
                    if (!s._softphoneHadMediaNegotiationProgress) return;
                    try {
                        s._softphoneHostCallEndedSent = true;
                        console.warn('[WebRTC]', reason, '- synthesizing call_ended for host', sessionId);
                        sendEvent({
                            type: 'call_ended',
                            data: {
                                sessionId: sessionId,
                                cause: reason,
                                originator: 'remote',
                                message: reason
                            }
                        });
                    } catch (synErr) {
                        try { s._softphoneHostCallEndedSent = false; } catch { }
                        console.warn('[WebRTC] Error sending synthetic call_ended:', synErr);
                    }
                };

                try {
                    applyInboundAudioReceiverBufferHints(pc, INBOUND_AUDIO_JITTER_BUFFER_TARGET_MS);
                } catch { /* ignore */ }

                // Логируем ICE-кандидаты через PeerConnection напрямую (не через JsSIP s.on('icecandidate'))
                pc.addEventListener('icecandidate', (evt) => {
                    if (evt.candidate) {
                        const c = evt.candidate.candidate;
                        const isSrflx = c.includes('srflx');
                        const isRelay = c.includes('relay');
                        const label = isSrflx ? '✅ ICE Server reflexive candidate (STUN)' :
                                      isRelay ? '✅ ICE Relay candidate (TURN)' :
                                      '⚠️ ICE Host candidate (local IP)';
                        const suffix = (isSrflx || isRelay) ? ' - should work through NAT' : ' - may not work through NAT';
                        console.log(`[WebRTC] ${label}: ${c.substring(0, 120)} ${suffix}`);
                        sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] ${label}: ${c.substring(0, 120)} ${suffix}` } });
                    } else {
                        console.log('[WebRTC] ✅ All ICE candidates gathered for session', sessionId);
                        sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] ✅ All ICE candidates gathered for session ' + sessionId } });
                    }
                });
                
                // Для исходящих звонков: отслеживаем установку соединения для остановки ringback tone
                // Когда ICE соединение установлено и есть аудио треки, это означает, что звонок принят
                const checkConnectionEstablished = () => {
                    const connectionState = pc.connectionState;
                    const iceState = pc.iceConnectionState;
                    const hasAudioTracks = pc.getReceivers().some(r => r.track && r.track.kind === 'audio');
                    
                    console.log('[WebRTC] Connection check - state:', connectionState, 'ICE:', iceState, 'hasAudio:', hasAudioTracks);
                    
                    // Если соединение установлено и есть аудио треки, отправляем событие call_accepted
                    // Это важно для исходящих звонков, где события accepted/confirmed могут не сработать
                    if ((connectionState === 'connected' || connectionState === 'completed') && 
                        (iceState === 'connected' || iceState === 'completed') && 
                        hasAudioTracks) {
                        console.log('[WebRTC] Connection established, sending call_accepted event for outgoing call');
                        sendCallAcceptedOnce('peerconnection_established');
                    }
                };
                
                // Проверяем сразу
                checkConnectionEstablished();
                
                // Подписываемся на изменения состояния соединения
                pc.addEventListener('connectionstatechange', () => {
                    const state = pc.connectionState;
                    console.log('[WebRTC] PeerConnection connectionState changed:', state);
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] PeerConnection connectionState: ${state}, ICE: ${pc.iceConnectionState}, SessionID=${sessionId}`
                        }
                    });
                    // If the media layer tears down without a JsSIP 'ended' callback (some PBX / race cases),
                    // the host CallWindow would stay open. Mirror call_ended once per session.
                    if (state === 'closed') {
                        trySyntheticCallEndedFromPc('PeerConnection closed');
                    } else if (state === 'failed') {
                        trySyntheticCallEndedFromPc('PeerConnection failed');
                    }
                    checkConnectionEstablished();
                });
                
                pc.addEventListener('iceconnectionstatechange', () => {
                    const iceState = pc.iceConnectionState;
                    console.log('[WebRTC] PeerConnection ICE connectionState changed:', iceState);
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] PeerConnection ICE state: ${iceState}, Connection: ${pc.connectionState}, SessionID=${sessionId}`
                        }
                    });
                    // ICE can reach 'closed' before connectionState on some stacks; cover that path too.
                    if (iceState === 'closed') {
                        trySyntheticCallEndedFromPc('ICE connection closed');
                    } else if (iceState === 'failed') {
                        trySyntheticCallEndedFromPc('ICE connection failed');
                    }
                    checkConnectionEstablished();
                });

                // PBX often sends re-INVITE when the B-leg bridges. On stable, rebuild WebAudio + jitter hints — not just resume().
                if (!pc._softphoneSignalingStableWired) {
                    pc._softphoneSignalingStableWired = true;
                    pc.addEventListener('signalingstatechange', () => {
                        if (pc.signalingState !== 'stable') return;
                        sdpStableCount++;
                        try {
                            applyInboundAudioReceiverBufferHints(pc, INBOUND_AUDIO_JITTER_BUFFER_TARGET_MS);
                        } catch { /* ignore */ }

                        if (sdpStableCount > 1) {
                            // re-INVITE completed — rebuild inbound path from receivers (RTP path may have changed).
                            forceReconnectInboundFromReceivers('sdp_renegotiated');

                            sendEvent({
                                type: 'sdp_renegotiated',
                                data: {
                                    sessionId: sessionId,
                                    stableCount: sdpStableCount,
                                    originateId: originateId || null
                                }
                            });

                            setTimeout(() => {
                                try {
                                    const remoteAudioEl = document.getElementById('remoteAudio');
                                    if (remoteAudioEl && remoteAudioEl.srcObject) {
                                        startRemoteAudioSignalProbe(sessionId, remoteAudioEl.srcObject, null, { afterRenegotiation: true });
                                    }
                                } catch { /* ignore */ }
                            }, 80);
                        } else {
                            setTimeout(() => {
                                try {
                                    reattachRemoteWebAudioFromElement(sessionId);
                                    resumeRemoteWebAudioPlaybackBestEffort();
                                } catch { /* ignore */ }
                            }, 50);
                        }
                    });
                }
                
                // КРИТИЧНО: Используем connectAudioTrack из wireSessionEvents (определена выше)
                // Не создаем локальную версию, чтобы избежать дублирования кода
                // const connectAudioTrack уже определена на уровне wireSessionEvents
                
                // Обрабатываем существующие треки
                const transceivers = pc.getTransceivers();
                console.log('[WebRTC] Found transceivers:', transceivers.length, 'originator:', originator);
                transceivers.forEach(async (transceiver, index) => {
                    console.log(`[WebRTC] Transceiver ${index}: direction=${transceiver.direction}, receiver=${!!transceiver.receiver}, sender=${!!transceiver.sender}`);
                    if (transceiver.receiver && transceiver.receiver.track) {
                        const track = transceiver.receiver.track;
                        console.log(`[WebRTC] Transceiver ${index} receiver track: kind=${track.kind}, id=${track.id}, enabled=${track.enabled}, readyState=${track.readyState}`);
                        
                        // НЕ отправляем call_accepted здесь — трек в трансивере создаётся при offer
                        // (offerToReceiveAudio:true) и всегда live, даже без remote SDP.
                        // call_accepted должен приходить ТОЛЬКО от JsSIP accepted/confirmed или ICE connected.
                        if (originator === 'local' && track.kind === 'audio') {
                            console.log(`[WebRTC] Transceiver ${index}: audio track exists (readyState=${track.readyState}, enabled=${track.enabled}) — NOT sending call_accepted (waiting for 200 OK)`);
                        }
                        
                        // КРИТИЧНО: Вызываем connectAudioTrack с await для гарантированного подключения
                        try {
                            await connectAudioTrack(track);
                            console.log(`[WebRTC] ✅ Transceiver ${index} track ${track.id} connected successfully`);
                        } catch (err) {
                            console.error(`[WebRTC] ❌ Error connecting transceiver ${index} track:`, err);
                            sendEvent({
                                type: 'js_log',
                                data: {
                                    level: 'critical',
                                    message: `[WebRTC] ❌ Error connecting transceiver ${index} track: ${err.message || err}`
                                }
                            });
                        }
                    }
                });
                
                // Подписываемся на новые треки (это важно, так как треки могут появиться позже)
                pc.ontrack = async (event) => {
                    console.log('[WebRTC] ⚠️⚠️⚠️ ONTRACK EVENT RECEIVED ⚠️⚠️⚠️');
                    console.log('[WebRTC] Track kind:', event.track.kind);
                    console.log('[WebRTC] Track ID:', event.track.id);
                    console.log('[WebRTC] Track readyState:', event.track.readyState);
                    console.log('[WebRTC] Track enabled:', event.track.enabled);
                    console.log('[WebRTC] Streams:', event.streams.length);
                    console.log('[WebRTC] Originator:', originator);
                    console.log('[WebRTC] Session ID:', sessionId);
                    
                    // КРИТИЧНО: Отправляем js_log для гарантированного попадания в C# логи
                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] ONTRACK EVENT: kind=${event.track.kind}, readyState=${event.track.readyState}, enabled=${event.track.enabled}, originator=${originator}`
                        }
                    });
                    
                    if (event.track && event.track.kind === 'audio') {
                        // КРИТИЧНО: Когда появляется удаленный аудио трек, это означает, что звонок принят
                        // Отправляем call_accepted немедленно для исходящих звонков ДО подключения трека
                        if (originator === 'local') {
                            console.log('[WebRTC] ⚠️⚠️⚠️ ONTRACK: REMOTE AUDIO TRACK RECEIVED FOR OUTGOING CALL ⚠️⚠️⚠️');
                            console.log('[WebRTC] ⚠️ This means call is ANSWERED - sending call_accepted IMMEDIATELY');
                            console.log('[WebRTC] ⚠️ Ringback tone MUST be stopped NOW!');
                            
                            // КРИТИЧНО: Отправляем js_log перед call_accepted для диагностики
                            sendEvent({
                                type: 'js_log',
                                data: {
                                    level: 'critical',
                                    message: `[WebRTC] ⚠️⚠️⚠️ SENDING call_accepted FROM ONTRACK (remote audio track detected) ⚠️⚠️⚠️`
                                }
                            });
                            
                            sendCallAcceptedOnce('ontrack_remote_audio');
                        }
                        
                        // Подключаем трек после отправки события
                        await connectAudioTrack(event.track);
                    }
                };
                
                // Дополнительные обработчики состояния (дублируют addEventListener выше,
                // но onX-обработчик гарантирует, что мы не пропустим событие)
                pc.oniceconnectionstatechange = () => {
                    const iceState = pc.iceConnectionState;
                    console.log('[WebRTC] pc.oniceconnectionstatechange:', iceState);

                    // Notify host app (C#). This handler can overwrite session.connection.oniceconnectionstatechange,
                    // so we also emit the structured event here.
                    sendEvent({
                        type: 'ice_connection_state_change',
                        data: {
                            sessionId: sessionId,
                            state: iceState,
                            connectionState: pc.connectionState
                        }
                    });

                    sendEvent({
                        type: 'js_log',
                        data: {
                            level: 'critical',
                            message: `[WebRTC] pc.oniceconnectionstatechange: ${iceState}, connection: ${pc.connectionState}`
                        }
                    });
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

            // КРИТИЧНО: Принудительно очищаем все старые сессии перед новым звонком
            // Это предотвращает блокировку нового звонка из-за "зависших" сессий
            // cleanupSessions также очищает remoteAudio.srcObject, чтобы startRecording
            // не использовал старый ended трек от предыдущего звонка
            console.log('[WebRTC] makeCall: Cleaning up any existing sessions before new call');
            cleanupSessions();
            console.log('[WebRTC] makeCall: Old sessions cleaned up, proceeding with new call');

            // Формируем чистый SIP URI (без пробелов и лишних символов)
            const cleanNumber = String(number).trim();
            const domain = ua.configuration.uri.host || ua.configuration.uri.toString().split('@')[1];
            const target = cleanNumber.includes('@') ? (cleanNumber.startsWith('sip:') ? cleanNumber : `sip:${cleanNumber}`) : `sip:${cleanNumber}@${domain}`;
            console.log('[WebRTC] makeCall: Calling target:', target);

            // STUN нужен для NAT traversal: MikoPBX выполняет ICE negotiation ДО 200 OK.
            // Без srflx-кандидатов PBX не может достучаться до клиента за NAT.
            // TURN сервер нужен для обхода строгого NAT (symmetric NAT) и firewall, которые блокируют входящие UDP соединения.
            // UDP 443 используется для обхода firewall, которые блокируют другие UDP порты.
            const pbxHost = (ua.configuration.uri.host || '').replace(/:\d+$/, '');
            // Используем те же ICE серверы, что и в initUA.
            // Пользовательский TURN важен для symmetric NAT / корпоративных VPN.
            const callIceConfig = buildIceConfig();
            const iceServers = callIceConfig.iceServers;
            if (callIceConfig.turnUrl) {
                console.log('[WebRTC] makeCall: Using custom TURN server from initUA config:', callIceConfig.turnUrl);
            } else {
                console.log('[WebRTC] makeCall: No custom TURN server found in initUA config (STUN-only)');
            }
            // НЕ добавляем stun:pbx:3478 по умолчанию — если порт закрыт, это задерживает ICE gathering на 30+ секунд.
            console.log('[WebRTC] makeCall: ICE servers:', JSON.stringify(iceServers));
            sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] makeCall: ICE servers: ' + JSON.stringify(iceServers) } });

            // Media constraints for outgoing calls.
            // Previously this was `audio: true`, which lets the browser pick defaults.
            // In practice that can disable/weakly apply AEC/NS/AGC, causing echo for the remote side.
            //
            // Keep it simple (only AEC/NS/AGC with ideal) to avoid breaking getUserMedia/INVITE in WebView2.
            const mediaOpts = {
                audio: {
                    echoCancellation: { ideal: true },   // Reduce acoustic echo (critical for VoIP)
                    noiseSuppression: { ideal: true }, // Reduce surrounding room pickup
                    autoGainControl: { ideal: true }    // Keep operator voice at a stable level
                },
                video: false
            };

            // Гарантируем, что разрешение на микрофон получено ДО ua.call.
            // Это убирает задержку разрешения в WebView2, из-за которой INVITE не формируется.
            const permissionGranted = await ensureMicPermission();
            if (!permissionGranted) {
                console.warn('[WebRTC] makeCall: Microphone permission NOT granted (continuing anyway)');
                sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] makeCall: Mic permission not granted before ua.call (continuing)' } });
            }
            await new Promise(resolve => setTimeout(resolve, 100));

            const pcConfig = callIceConfig.pcConfig;

            const options = {
                mediaConstraints: mediaOpts,
                pcConfig: pcConfig,
                rtcOfferConstraints: { offerToReceiveAudio: true, offerToReceiveVideo: false },
                eventHandlers: {
                    sending: (e) => {
                        console.log('[WebRTC] ✅ SIP INVITE SENT! Call-ID:', e.request?.call_id);
                        sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] ✅ SIP INVITE SENT! Request sent to network. Call-ID: ' + (e.request?.call_id || 'N/A') } });
                    },
                    progress: (e) => { 
                        console.log('[WebRTC] ⚠️⚠️⚠️ PROGRESS EVENT (180 Ringing) ⚠️⚠️⚠️');
                        console.log('[WebRTC] Progress event:', e);
                        sendEvent({ 
                            type: 'js_log', 
                            data: { 
                                level: 'critical', 
                                message: '[WebRTC] ⚠️⚠️⚠️ PROGRESS EVENT (180 Ringing) ⚠️⚠️⚠️ - PBX is ringing remote party!' 
                            } 
                        });
                    },
                    failed: (e) => { 
                        console.error('[WebRTC] ❌❌❌ CALL FAILED (eventHandlers) ❌❌❌');
                        console.error('[WebRTC] Failed event:', e);
                        console.error('[WebRTC] Cause:', e?.cause);
                        sendEvent({ 
                            type: 'js_log', 
                            data: { 
                                level: 'critical', 
                                message: '[WebRTC] ❌❌❌ CALL FAILED (eventHandlers) ❌❌❌ Cause: ' + (e?.cause || 'Unknown') 
                            } 
                        });
                    },
                    confirmed: (e) => { 
                        console.log('[WebRTC] ✅✅✅ CALL CONFIRMED (eventHandlers) ✅✅✅');
                        console.log('[WebRTC] Confirmed event:', e);
                        sendEvent({ 
                            type: 'js_log', 
                            data: { 
                                level: 'critical', 
                                message: '[WebRTC] ✅✅✅ CALL CONFIRMED (eventHandlers) ✅✅✅ - Call fully established!' 
                            } 
                        });
                    }
                }
            };

            console.log('[WebRTC] >>> ua.call with mediaConstraints, target=', target);
            sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] >>> ua.call(target=' + target + ') with mediaConstraints' } });
            session = ua.call(target, options);
            if (!session) {
                console.error('[WebRTC] ua.call() returned null');
                sendEvent({ type: 'error', data: { name: 'SessionNull', message: 'ua.call() failed to create session', phase: 'makeCall' } });
                return;
            }
            console.log('[WebRTC] <<< ua.call() sessionId=', session.id, ' HasRequest=', !!session.request);
            sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] <<< ua.call() sessionId=' + session.id + ' HasRequest=' + !!session.request } });

            window._activeSession = session;
            wireSessionEvents(session, 'local');
            sendEvent({ type: 'makeCall_initiated', data: { sessionId: session.id, targetUri: target } });
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

            // Use the same ICE servers as initUA/makeCall to ensure TURN/STUN applies to answered sessions too.
            const answerIceConfig = buildIceConfig();
            const pcConfig = answerIceConfig.pcConfig;
            sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] answer: Using pcConfig iceServers count=' + answerIceConfig.iceServers.length + ', relayOnly=' + answerIceConfig.forceRelay } });

            // Захватываем медиа ТОЛЬКО при ответе
            console.log('[WebRTC] Requesting user media for answer...');
            const stream = await navigator.mediaDevices.getUserMedia({
                audio: {
                    echoCancellation: { ideal: true },      // Акустическое эхоподавление
                    noiseSuppression: { ideal: true },      // Подавление шумов
                    autoGainControl: { ideal: true },       // Автоматическая регулировка усиления
                    channelCount: { ideal: 1 },            // Моно канал
                    sampleRate: { ideal: 48000 },          // Высокая частота дискретизации
                    latency: { ideal: 0.01, max: 0.05 }    // Низкая задержка
                },
                video: false
            });
            console.log('[WebRTC] ✓ User media acquired for answer');
            
            // Проверяем применённые настройки эхоподавления
            const audioTrack = stream.getAudioTracks()[0];
            if (audioTrack) {
                const settings = audioTrack.getSettings();
                console.log('[WebRTC] Applied audio settings for answer:', {
                    echoCancellation: settings.echoCancellation,
                    noiseSuppression: settings.noiseSuppression,
                    autoGainControl: settings.autoGainControl
                });
            }

            sess.answer({
                mediaStream: stream,
                pcConfig: pcConfig,
                rtcOfferConstraints: {
                    offerToReceiveAudio: true,
                    offerToReceiveVideo: false
                }
            });

            // Сохраняем ссылку на локальный поток для управления mute и записи
            sess.localStream = stream;
            console.log('[WebRTC] answer: Local stream saved to session.localStream');
            
            // КРИТИЧНО: Добавляем обработчики для отслеживания отключения локального микрофона
            const localAudioTracks = stream.getAudioTracks();
            localAudioTracks.forEach(track => {
                track.addEventListener('ended', () => {
                    console.error('[WebRTC] ❌❌❌ LOCAL AUDIO TRACK ENDED! Microphone disconnected! ❌❌❌');
                    sendEvent({
                        type: 'error',
                        data: {
                            name: 'LocalMicDisconnected',
                            message: 'Local microphone track ended (microphone may have been disconnected)',
                            phase: 'answer',
                            trackId: track.id
                        }
                    });
                });
                
                track.addEventListener('mute', () => {
                    console.warn('[WebRTC] ⚠️ Local audio track was MUTED');
                });
                
                track.addEventListener('unmute', () => {
                    console.log('[WebRTC] ✅ Local audio track was UNMUTED');
                });
            });

            window._activeSession = sess;
            window._incomingSession = null;
            session = sess;
        } catch (error) {
            console.error('[WebRTC] ERROR in answer:', error);
            
            // КРИТИЧНО: Детальная обработка ошибок getUserMedia
            let errorName = 'AnswerError';
            let errorMessage = error.message || 'Unknown error';
            let userFriendlyMessage = 'Failed to answer call';
            
            if (error.name === 'NotAllowedError' || error.name === 'PermissionDeniedError') {
                errorName = 'MicPermissionDenied';
                userFriendlyMessage = 'Microphone permission denied. Please allow microphone access in browser settings.';
            } else if (error.name === 'NotReadableError' || error.name === 'TrackStartError') {
                errorName = 'MicNotReadable';
                userFriendlyMessage = 'Microphone is not accessible. It may be in use by another application.';
            } else if (error.name === 'NotFoundError' || error.name === 'DevicesNotFoundError') {
                errorName = 'MicNotFound';
                userFriendlyMessage = 'No microphone found. Please connect a microphone and try again.';
            } else if (error.name === 'OverconstrainedError' || error.name === 'ConstraintNotSatisfiedError') {
                errorName = 'MicConstraintsError';
                userFriendlyMessage = 'Microphone does not support required settings.';
            }
            
            sendEvent({ 
                type: 'error', 
                data: { 
                    name: errorName,
                    message: errorMessage,
                    userFriendlyMessage: userFriendlyMessage,
                    phase: 'answer',
                    stack: error.stack 
                } 
            });
        }
    }

    // Завершение/отклонение звонка (работает и для входящего до ответа)
    function hangup(sessionId) {
        try {
            teardownRemoteWebAudioPlayback();
            console.log('[WebRTC] ⚠️⚠️⚠️ HANGUP CALLED ⚠️⚠️⚠️');
            console.log('[WebRTC] SessionId parameter:', sessionId);
            console.log('[WebRTC] window._incomingSession:', window._incomingSession?.id);
            console.log('[WebRTC] window._activeSession:', window._activeSession?.id);
            console.log('[WebRTC] global session:', session?.id);
            console.log('[WebRTC] ua.sessions count:', ua?.sessions?.length || 0);
            
            // Prefer terminating the specific sessionId if provided, otherwise terminate everything we know about.
            const matches = (s) => {
                if (!s) return false;
                try {
                    if (sessionId && (s.id === sessionId || s._softphoneSessionId === sessionId || s.request?.call_id === sessionId)) {
                        return true;
                    }
                } catch {}
                return !sessionId; // no sessionId => match all
            };

            const toTerminate = [];
            if (matches(window._incomingSession)) {
                console.log('[WebRTC] hangup: Found matching _incomingSession:', window._incomingSession.id);
                toTerminate.push(window._incomingSession);
            }
            if (matches(window._activeSession)) {
                console.log('[WebRTC] hangup: Found matching _activeSession:', window._activeSession.id);
                toTerminate.push(window._activeSession);
            }
            if (matches(session)) {
                console.log('[WebRTC] hangup: Found matching global session:', session.id);
                toTerminate.push(session);
            }
            
            // КРИТИЧНО: Также проверяем все сессии в ua.sessions для надежности
            if (ua && ua.sessions) {
                console.log('[WebRTC] hangup: Checking ua.sessions (count:', ua.sessions.length, ')');
                ua.sessions.forEach((s, index) => {
                    if (s) {
                        console.log(`[WebRTC] hangup: ua.sessions[${index}]:`, s.id, 'matches:', matches(s));
                        if (matches(s) && !toTerminate.includes(s)) {
                            console.log('[WebRTC] hangup: Adding session from ua.sessions:', s.id);
                            toTerminate.push(s);
                        }
                    }
                });
            }
            
            // КРИТИЧНО: Если sessionId указан, но сессия не найдена, пробуем найти по всем возможным способам
            if (sessionId && toTerminate.length === 0) {
                console.warn('[WebRTC] ⚠️⚠️⚠️ WARNING: SessionId provided but no matching session found!');
                console.warn('[WebRTC] ⚠️ Trying to find session by all possible methods...');
                
                // Пробуем найти сессию по всем возможным идентификаторам
                if (ua && ua.sessions) {
                    ua.sessions.forEach((s) => {
                        if (s) {
                            console.log('[WebRTC] hangup: Checking session:', s.id);
                            console.log('[WebRTC] hangup:   - s.id === sessionId:', s.id === sessionId);
                            console.log('[WebRTC] hangup:   - s._softphoneSessionId === sessionId:', s._softphoneSessionId === sessionId);
                            console.log('[WebRTC] hangup:   - s.request?.call_id === sessionId:', s.request?.call_id === sessionId);
                            
                            // Более мягкое сравнение - пробуем частичное совпадение
                            if (s.id && s.id.includes(sessionId) || 
                                s._softphoneSessionId && s._softphoneSessionId.includes(sessionId) ||
                                s.request?.call_id && s.request.call_id.includes(sessionId)) {
                                console.log('[WebRTC] hangup: ✅ Found session by partial match:', s.id);
                                toTerminate.push(s);
                            }
                        }
                    });
                }
                
                // Если все еще не найдено, завершаем ВСЕ активные сессии
                if (toTerminate.length === 0) {
                    console.warn('[WebRTC] ⚠️⚠️⚠️ CRITICAL: Still no session found, terminating ALL sessions!');
                    if (ua && ua.sessions) {
                        ua.sessions.forEach((s) => {
                            if (s) {
                                console.log('[WebRTC] hangup: Terminating all session:', s.id);
                                toTerminate.push(s);
                            }
                        });
                    }
                    // Также добавляем глобальные сессии
                    if (window._activeSession && !toTerminate.includes(window._activeSession)) {
                        toTerminate.push(window._activeSession);
                    }
                    if (session && !toTerminate.includes(session)) {
                        toTerminate.push(session);
                    }
                }
            }

            // De-dup by object reference
            const unique = Array.from(new Set(toTerminate));
            
            console.log('[WebRTC] ⚠️⚠️⚠️ HANGUP: Found', unique.length, 'session(s) to terminate ⚠️⚠️⚠️');
            unique.forEach((s, index) => {
                console.log(`[WebRTC] hangup: Session ${index + 1} to terminate:`, s.id);
            });

            let terminatedCount = 0;
            unique.forEach((s) => {
                try {
                    console.log('[WebRTC] ⚠️⚠️⚠️ TERMINATING SESSION:', s.id, '⚠️⚠️⚠️');
                    
                    // Проверяем состояние сессии перед завершением
                    console.log('[WebRTC] hangup: Session state before terminate:', s.status);
                    console.log('[WebRTC] hangup: Session direction:', s.direction);
                    
                    // For incoming sessions not answered yet, terminate() will send a reject (486/603 depending on JsSIP).
                    // Для активных сессий terminate() отправит BYE
                    s.terminate({
                        status_code: 487, // Request Terminated
                        reason_phrase: 'Call terminated by user'
                    });
                    
                    terminatedCount++;
                    console.log('[WebRTC] ✅ Session terminated successfully:', s.id);
                } catch (e) {
                    console.error('[WebRTC] ❌❌❌ ERROR terminating session:', s.id, '❌❌❌');
                    console.error('[WebRTC] Error details:', e);
                    console.error('[WebRTC] Error stack:', e.stack);
                    
                    // Пробуем альтернативный способ завершения
                    try {
                        console.log('[WebRTC] Trying alternative termination method...');
                        if (s.connection) {
                            s.connection.close();
                            console.log('[WebRTC] Closed PeerConnection for session:', s.id);
                        }
                    } catch (altErr) {
                        console.error('[WebRTC] Alternative termination also failed:', altErr);
                    }
                }
            });
            
            console.log('[WebRTC] ⚠️⚠️⚠️ HANGUP COMPLETE: Terminated', terminatedCount, 'session(s) ⚠️⚠️⚠️');

            // Clear globals
            if (!sessionId || matches(window._incomingSession)) {
                window._incomingSession = null;
                console.log('[WebRTC] hangup: Cleared window._incomingSession');
            }
            if (!sessionId || matches(window._activeSession)) {
                window._activeSession = null;
                console.log('[WebRTC] hangup: Cleared window._activeSession');
            }
            if (!sessionId || matches(session)) {
                session = null;
                console.log('[WebRTC] hangup: Cleared session');
            }
            
            // КРИТИЧНО: Отправляем событие call_ended для всех завершенных сессий ПЕРЕД очисткой
            // Это гарантирует, что событие будет отправлено на обе стороны
            if (terminatedCount > 0) {
                unique.forEach((s) => {
                    try {
                        // Останавливаем локальные медиа-стримы перед отправкой события
                        if (s.localStream) {
                            s.localStream.getTracks().forEach(track => {
                                try {
                                    track.stop();
                                    console.log('[WebRTC] hangup: Stopped local media track:', track.kind);
                                } catch (trackError) {
                                    console.warn('[WebRTC] hangup: Error stopping track:', trackError);
                                }
                            });
                        }
                        
                        // Отправляем событие call_ended
                        try { s._softphoneHostCallEndedSent = true; } catch { }
                        sendEvent({
                            type: 'call_ended',
                            data: {
                                sessionId: s.id,
                                cause: 'Terminated',
                                originator: 'local'
                            }
                        });
                        console.log('[WebRTC] hangup: call_ended event sent for session:', s.id);
                    } catch (e) {
                        console.warn('[WebRTC] hangup: Error sending call_ended event:', e);
                    }
                });
            } else {
                // Если не было сессий для завершения, все равно отправляем событие call_ended
                // Это важно для случаев, когда сессия уже была завершена, но окно еще открыто
                console.log('[WebRTC] hangup: No sessions to terminate, sending call_ended anyway');
                sendEvent({
                    type: 'call_ended',
                    data: {
                        sessionId: sessionId || null,
                        cause: 'Terminated',
                        originator: 'local'
                    }
                });
            }
        } catch (error) {
            console.error('[WebRTC] hangup error:', error);
            sendEvent({ 
                type: 'error', 
                data: { 
                    message: error.message,
                    stack: error.stack 
                } 
            });
        }
    }

    // Hold/unhold active call (SIP re-INVITE via JsSIP).
    // Works for both outgoing and already-answered incoming calls.
    function setHold(hold, sessionId) {
        try {
            const matches = (s) => {
                if (!s) return false;
                if (!sessionId) return true;
                try {
                    return (s.id === sessionId || s._softphoneSessionId === sessionId || s.request?.call_id === sessionId);
                } catch {
                    return false;
                }
            };

            // Ищем активную сессию среди всех возможных кандидатов
            // Проверяем все возможные места, где может быть сохранена сессия
            const candidates = [
                window._activeSession, 
                session, 
                window._incomingSession,
                // Также проверяем все сессии в ua.sessions, если они доступны
                ...(ua && ua.sessions ? Array.from(ua.sessions.values()) : [])
            ].filter(Boolean);
            
            const s = candidates.find(matches);
            if (!s) {
                console.warn('[WebRTC] setHold: No matching active session', sessionId, 'candidates:', candidates.length);
                console.warn('[WebRTC] setHold: Available sessions:', {
                    _activeSession: !!window._activeSession,
                    session: !!session,
                    _incomingSession: !!window._incomingSession,
                    uaSessions: ua && ua.sessions ? ua.sessions.size : 0
                });
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'HoldError',
                        message: 'Cannot set hold: no active session',
                        phase: 'setHold',
                        sessionId: sessionId || null
                    }
                });
                return false;
            }

            if (hold) {
                if (typeof s.hold === 'function') {
                    try {
                        // КРИТИЧНО: Сбрасываем флаг перед вызовом hold()
                        if (s) {
                            s._holdEventSent = false;
                        }
                        
                        console.log('[WebRTC] setHold: Calling s.hold() for session:', s.id);
                        console.log('[WebRTC] setHold: Session state before hold:', {
                            id: s.id,
                            status: s.status,
                            isOnHold: s.isOnHold ? s.isOnHold() : 'method not available'
                        });
                        
                        // Вызываем hold() - JsSIP отправит re-INVITE на удаленную сторону
                        // Если JsSIP не сгенерирует событие 'hold' в течение 500ms, отправляем событие явно
                        s.hold();
                        console.log('[WebRTC] setHold: hold() called successfully, waiting for JsSIP hold event');
                        
                        // КРИТИЧНО: Устанавливаем таймаут для отправки события hold, если JsSIP не сгенерирует его
                        // Это гарантирует, что событие будет отправлено даже если JsSIP не сгенерирует событие 'hold'
                        setTimeout(() => {
                            // Проверяем, было ли уже отправлено событие через обработчик JsSIP
                            // Если нет, отправляем явно
                            if (s && !s._holdEventSent) {
                                console.log('[WebRTC] setHold: JsSIP hold event not received after 500ms, sending call_hold explicitly');
                                sendEvent({ 
                                    type: 'call_hold', 
                                    data: { 
                                        sessionId: s.id,
                                        originator: 'local'
                                    } 
                                });
                                s._holdEventSent = true;
                            } else {
                                console.log('[WebRTC] setHold: JsSIP hold event was received (or already sent)');
                            }
                        }, 500); // Уменьшил таймаут до 500ms для более быстрой реакции
                        
                        return true;
                    } catch (holdError) {
                        console.error('[WebRTC] setHold: Error calling hold():', holdError);
                        console.error('[WebRTC] setHold: Error stack:', holdError.stack);
                        // При ошибке отправляем событие явно
                        sendEvent({ 
                            type: 'call_hold', 
                            data: { 
                                sessionId: s.id,
                                originator: 'local'
                            } 
                        });
                        sendEvent({
                            type: 'error',
                            data: {
                                name: 'HoldError',
                                message: 'Failed to call hold(): ' + holdError.message,
                                phase: 'setHold',
                                sessionId: s.id,
                                stack: holdError.stack
                            }
                        });
                        return false;
                    }
                }
                console.warn('[WebRTC] setHold: session.hold() not available');
            } else {
                if (typeof s.unhold === 'function') {
                    try {
                        // КРИТИЧНО: Сбрасываем флаг перед вызовом unhold()
                        if (s) {
                            s._unholdEventSent = false;
                        }
                        
                        console.log('[WebRTC] setHold: Calling s.unhold() for session:', s.id);
                        console.log('[WebRTC] setHold: Session state before unhold:', {
                            id: s.id,
                            status: s.status,
                            isOnHold: s.isOnHold ? s.isOnHold() : 'method not available'
                        });
                        
                        // Вызываем unhold() - JsSIP отправит re-INVITE на удаленную сторону
                        // Если JsSIP не сгенерирует событие 'unhold' в течение 500ms, отправляем событие явно
                        s.unhold();
                        console.log('[WebRTC] setHold: unhold() called successfully, waiting for JsSIP unhold event');
                        
                        // КРИТИЧНО: Устанавливаем таймаут для отправки события unhold, если JsSIP не сгенерирует его
                        // Это гарантирует, что событие будет отправлено даже если JsSIP не сгенерирует событие 'unhold'
                        setTimeout(() => {
                            // Проверяем, было ли уже отправлено событие через обработчик JsSIP
                            // Если нет, отправляем явно
                            if (s && !s._unholdEventSent) {
                                console.log('[WebRTC] setHold: JsSIP unhold event not received after 500ms, sending call_unhold explicitly');
                                sendEvent({ 
                                    type: 'call_unhold', 
                                    data: { 
                                        sessionId: s.id,
                                        originator: 'local'
                                    } 
                                });
                                s._unholdEventSent = true;
                            } else {
                                console.log('[WebRTC] setHold: JsSIP unhold event was received (or already sent)');
                            }
                        }, 500); // Уменьшил таймаут до 500ms для более быстрой реакции
                        
                        return true;
                    } catch (unholdError) {
                        console.error('[WebRTC] setHold: Error calling unhold():', unholdError);
                        console.error('[WebRTC] setHold: Error stack:', unholdError.stack);
                        // При ошибке отправляем событие явно
                        sendEvent({ 
                            type: 'call_unhold', 
                            data: { 
                                sessionId: s.id,
                                originator: 'local'
                            } 
                        });
                        sendEvent({
                            type: 'error',
                            data: {
                                name: 'UnholdError',
                                message: 'Failed to call unhold(): ' + unholdError.message,
                                phase: 'setHold',
                                sessionId: s.id,
                                stack: unholdError.stack
                            }
                        });
                        return false;
                    }
                }
                console.warn('[WebRTC] setHold: session.unhold() not available');
            }

            sendEvent({
                type: 'error',
                data: {
                    name: 'HoldNotSupported',
                    message: 'Hold/unhold not supported by JsSIP session',
                    phase: 'setHold',
                    sessionId: s.id
                }
            });
            return false;
        } catch (error) {
            console.error('[WebRTC] ERROR in setHold:', error);
            sendEvent({
                type: 'error',
                data: {
                    name: error.name || 'HoldError',
                    message: error.message,
                    phase: 'setHold',
                    stack: error.stack
                }
            });
            return false;
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

    // Очистка всех сессий (для использования перед новым звонком)
    function cleanupSessions() {
        try {
            console.log('[WebRTC] cleanupSessions: Cleaning up all sessions');
            const sessionsToCleanup = [];
            if (session) sessionsToCleanup.push(session);
            if (window._activeSession) sessionsToCleanup.push(window._activeSession);
            if (window._incomingSession) sessionsToCleanup.push(window._incomingSession);
            
            // Также проверяем все сессии в ua.sessions
            if (ua && ua.sessions) {
                ua.sessions.forEach((s) => {
                    if (s && !sessionsToCleanup.includes(s)) {
                        sessionsToCleanup.push(s);
                    }
                });
            }
            
            // Завершаем все найденные сессии
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
            
            // Очищаем глобальные переменные
            session = null;
            window._activeSession = null;
            window._incomingSession = null;
            
            // КРИТИЧНО: Очищаем remoteAudio.srcObject, чтобы при следующем звонке
            // startRecording не использовал старый ended трек от предыдущего звонка
            const remoteAudioEl = document.getElementById('remoteAudio');
            if (remoteAudioEl && remoteAudioEl.srcObject) {
                console.log('[WebRTC] cleanupSessions: Clearing remoteAudio.srcObject to prevent using old ended tracks');
                remoteAudioEl.srcObject = null;
            }
            
            // КРИТИЧНО: Очищаем MediaRecorder и recordingChunks при очистке сессий
            // Это предотвращает использование остатков от предыдущего звонка при быстрых последовательных звонках
            if (mediaRecorder) {
                console.log('[WebRTC] cleanupSessions: Cleaning up old MediaRecorder (state:', mediaRecorder.state, ')');
                try {
                    if (mediaRecorder.state === 'recording') {
                        console.log('[WebRTC] cleanupSessions: Stopping MediaRecorder that is still recording');
                        mediaRecorder.stop();
                    }
                } catch (e) {
                    console.warn('[WebRTC] cleanupSessions: Error stopping MediaRecorder:', e);
                }
                mediaRecorder = null;
            }
            
            if (recordingChunks && recordingChunks.length > 0) {
                console.log('[WebRTC] cleanupSessions: Clearing', recordingChunks.length, 'old recording chunks');
                recordingChunks = [];
            }
            
            // Сбрасываем флаги записи
            _isRecordingStarting = false;
            _recordingAborted = false;
            
            // Очищаем recordingStream и его AudioContext
            if (recordingStream && recordingStream._audioContext) {
                const oldAudioContext = recordingStream._audioContext;
                console.log('[WebRTC] cleanupSessions: Closing old AudioContext (state:', oldAudioContext.state, ')');
                try {
                    if (oldAudioContext.state !== 'closed') {
                        oldAudioContext.close().catch(err => {
                            console.warn('[WebRTC] cleanupSessions: Error closing AudioContext:', err);
                        });
                    }
                } catch (e) {
                    console.warn('[WebRTC] cleanupSessions: Error closing AudioContext:', e);
                }
                delete recordingStream._audioContext;
            }
            recordingStream = null;
            
            console.log('[WebRTC] cleanupSessions: All sessions cleaned up');
            return true;
        } catch (error) {
            console.error('[WebRTC] cleanupSessions error:', error);
            return false;
        }
    }

    // Остановка UA
    function stop() {
        try {
            // Очищаем все сессии перед остановкой UA
            cleanupSessions();
            stopWssKeepalive();
            stopTurnPrewarmLoop();

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
            if (isDesktopWebViewHost()) {
                postToHost(JSON.stringify({ type: "pong", slot: _slot, ts: Date.now() }));
            } else {
                // Fallback через sendEvent если host bridge недоступен
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
            
            stopWssKeepalive();
            stopTurnPrewarmLoop();

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
    
    // Проверка реальной активности звонка (есть ли активные аудио потоки и передача данных)
    function isCallActuallyActive(sessionId) {
        try {
            // Находим активную сессию
            const matches = (s) => {
                if (!s) return false;
                if (!sessionId) return true;
                try {
                    return (s.id === sessionId || s._softphoneSessionId === sessionId || s.request?.call_id === sessionId);
                } catch {
                    return false;
                }
            };
            
            const candidates = [
                window._activeSession, 
                session, 
                window._incomingSession,
                ...(ua && ua.sessions ? Array.from(ua.sessions.values()) : [])
            ].filter(Boolean);
            
            const s = candidates.find(matches);
            if (!s || !s.connection) {
                console.log('[WebRTC] isCallActuallyActive: No active session or PeerConnection found');
                return false;
            }
            
            const pc = s.connection;
            
            // Проверка 1: Состояние PeerConnection должно быть connected или completed
            const connectionState = pc.connectionState;
            const iceState = pc.iceConnectionState;
            
            if (connectionState !== 'connected' && connectionState !== 'completed') {
                console.log(`[WebRTC] isCallActuallyActive: Connection state is ${connectionState}, not active`);
                return false;
            }
            
            if (iceState !== 'connected' && iceState !== 'completed') {
                console.log(`[WebRTC] isCallActuallyActive: ICE state is ${iceState}, not active`);
                return false;
            }
            
            // Проверка 2: Наличие активных аудио треков
            const receivers = pc.getReceivers();
            const senders = pc.getSenders();
            
            const hasActiveReceiver = receivers.some(r => {
                const track = r.track;
                return track && 
                       track.kind === 'audio' && 
                       track.readyState === 'live' && 
                       track.enabled && 
                       !track.muted;
            });
            
            const hasActiveSender = senders.some(s => {
                const track = s.track;
                return track && 
                       track.kind === 'audio' && 
                       track.readyState === 'live' && 
                       track.enabled && 
                       !track.muted;
            });
            
            if (!hasActiveReceiver && !hasActiveSender) {
                console.log('[WebRTC] isCallActuallyActive: No active audio tracks found');
                return false;
            }
            
            // Проверка 3: Статистика RTP (передача данных) - асинхронная проверка
            // Эта проверка выполняется через getStats, но мы можем проверить базовые условия синхронно
            // Для более точной проверки нужно использовать асинхронную функцию checkCallActivityWithStats
            
            console.log(`[WebRTC] isCallActuallyActive: Call is ACTIVE - connection=${connectionState}, ICE=${iceState}, receivers=${receivers.length}, senders=${senders.length}, hasActiveReceiver=${hasActiveReceiver}, hasActiveSender=${hasActiveSender}`);
            return true;
        } catch (error) {
            console.error('[WebRTC] ERROR in isCallActuallyActive:', error);
            return false;
        }
    }
    
    // Асинхронная проверка активности звонка с использованием статистики RTP
    async function checkCallActivityWithStats(sessionId) {
        try {
            const matches = (s) => {
                if (!s) return false;
                if (!sessionId) return true;
                try {
                    return (s.id === sessionId || s._softphoneSessionId === sessionId || s.request?.call_id === sessionId);
                } catch {
                    return false;
                }
            };
            
            const candidates = [
                window._activeSession, 
                session, 
                window._incomingSession,
                ...(ua && ua.sessions ? Array.from(ua.sessions.values()) : [])
            ].filter(Boolean);
            
            const s = candidates.find(matches);
            if (!s || !s.connection) {
                return { active: false, reason: 'No session or PeerConnection' };
            }
            
            const pc = s.connection;
            
            // Базовая проверка состояния
            if (pc.connectionState !== 'connected' && pc.connectionState !== 'completed') {
                return { active: false, reason: `Connection state: ${pc.connectionState}` };
            }
            
            if (pc.iceConnectionState !== 'connected' && pc.iceConnectionState !== 'completed') {
                return { active: false, reason: `ICE state: ${pc.iceConnectionState}` };
            }
            
            // Проверка треков
            const receivers = pc.getReceivers();
            const senders = pc.getSenders();
            
            const hasActiveReceiver = receivers.some(r => {
                const track = r.track;
                return track && track.kind === 'audio' && track.readyState === 'live' && track.enabled;
            });
            
            const hasActiveSender = senders.some(s => {
                const track = s.track;
                return track && track.kind === 'audio' && track.readyState === 'live' && track.enabled;
            });
            
            if (!hasActiveReceiver && !hasActiveSender) {
                return { active: false, reason: 'No active audio tracks' };
            }
            
            // Проверка статистики RTP для подтверждения передачи данных
            try {
                const report = await pc.getStats();
                let hasDataFlow = false;
                let bytesReceived = 0;
                let bytesSent = 0;
                
                report.forEach((stat) => {
                    if (stat.type === 'inbound-rtp' && stat.mediaType === 'audio') {
                        bytesReceived = stat.bytesReceived || 0;
                        if (bytesReceived > 0) {
                            hasDataFlow = true;
                        }
                    }
                    if (stat.type === 'outbound-rtp' && stat.mediaType === 'audio') {
                        bytesSent = stat.bytesSent || 0;
                        if (bytesSent > 0) {
                            hasDataFlow = true;
                        }
                    }
                });
                
                if (!hasDataFlow && (bytesReceived === 0 && bytesSent === 0)) {
                    // Если нет передачи данных, но треки активны, возможно звонок только что установился
                    // В этом случае считаем звонок активным, если треки есть
                    return { 
                        active: hasActiveReceiver || hasActiveSender, 
                        reason: hasDataFlow ? 'Active' : 'No RTP data yet (call may be establishing)',
                        bytesReceived,
                        bytesSent
                    };
                }
                
                return { 
                    active: true, 
                    reason: 'Active with RTP data flow',
                    bytesReceived,
                    bytesSent
                };
            } catch (statsError) {
                console.warn('[WebRTC] Error getting stats for activity check:', statsError);
                // Если не удалось получить статистику, полагаемся на базовые проверки
                return { 
                    active: hasActiveReceiver || hasActiveSender, 
                    reason: 'Active (stats unavailable)'
                };
            }
        } catch (error) {
            console.error('[WebRTC] ERROR in checkCallActivityWithStats:', error);
            return { active: false, reason: `Error: ${error.message}` };
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
            // Используем best practices constraints для эхоподавления
            const stream = await navigator.mediaDevices.getUserMedia({ 
                audio: {
                    echoCancellation: { ideal: true },
                    noiseSuppression: { ideal: true },
                    autoGainControl: { ideal: true },
                    channelCount: { ideal: 1 },
                    sampleRate: { ideal: 48000 },
                    latency: { ideal: 0.01, max: 0.05 }
                }
            });
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
            
            // КРИТИЧНО: Детальная обработка ошибок разрешений
            let errorName = 'MicPermissionError';
            let userFriendlyMessage = 'Failed to get microphone permission';
            
            if (error.name === 'NotAllowedError' || error.name === 'PermissionDeniedError') {
                errorName = 'MicPermissionDenied';
                userFriendlyMessage = 'Microphone permission denied. Please allow microphone access.';
            } else if (error.name === 'NotReadableError') {
                errorName = 'MicNotReadable';
                userFriendlyMessage = 'Microphone is not accessible. It may be in use by another application.';
            } else if (error.name === 'NotFoundError') {
                errorName = 'MicNotFound';
                userFriendlyMessage = 'No microphone found. Please connect a microphone.';
            }
            
            sendEvent({
                type: 'error',
                data: {
                    name: errorName,
                    message: 'Failed to get microphone permission: ' + error.message,
                    userFriendlyMessage: userFriendlyMessage,
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
                            echoCancellation: { ideal: true },      // Акустическое эхоподавление
                            noiseSuppression: { ideal: true },      // Подавление шумов
                            autoGainControl: { ideal: true },       // Автоматическая регулировка усиления
                            channelCount: { ideal: 1 },            // Моно канал
                            sampleRate: { ideal: 48000 },          // Высокая частота дискретизации
                            latency: { ideal: 0.01, max: 0.05 }    // Низкая задержка
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

            // Ищем активную сессию: сначала window._activeSession, затем глобальная session
            let activeSession = window._activeSession || session;
            
            if (!activeSession) {
                console.warn('[WebRTC] setMute: No active session (checked window._activeSession and session)');
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

            const pc = activeSession.connection;

            if (!pc) {
                console.warn(`[WebRTC] setMute: No PeerConnection (sessionId=${activeSession.id})`);
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'NoPeerConnection',
                        message: 'Cannot set mute: no PeerConnection',
                        phase: 'setMute',
                        sessionId: activeSession.id
                    }
                });
                return false;
            }

            console.log(`[WebRTC] setMute: Using session ${activeSession.id}, PeerConnection state: signaling=${pc.signalingState}, connection=${pc.connectionState}, ice=${pc.iceConnectionState}`);

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

            // Ищем активную сессию: сначала window._activeSession, затем глобальная session
            let activeSession = window._activeSession || session;
            
            if (!activeSession) {
                console.warn('[WebRTC] sendDtmf: No active session (checked window._activeSession and session)');
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

            // Проверяем, что сессия существует и имеет connection (PeerConnection)
            // В JsSIP можно отправлять DTMF только когда сессия установлена и есть PeerConnection
            if (!activeSession.connection) {
                console.warn(`[WebRTC] sendDtmf: Session ${activeSession.id} has no PeerConnection`);
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'NoPeerConnection',
                        message: 'Cannot send DTMF: session has no PeerConnection',
                        phase: 'sendDtmf',
                        sessionId: activeSession.id
                    }
                });
                return false;
            }

            // Проверяем, что PeerConnection в правильном состоянии
            // Ослабляем проверку: разрешаем отправку DTMF если signalingState='stable' И
            // (connectionState='connected' ИЛИ iceConnectionState='connected')
            // Это позволяет отправлять DTMF даже если connectionState ещё 'connecting', но ICE уже connected
            const pc = activeSession.connection;
            const isSignalingStable = pc.signalingState === 'stable';
            const isConnectionReady = pc.connectionState === 'connected' || pc.iceConnectionState === 'connected';
            
            if (!isSignalingStable || !isConnectionReady) {
                console.warn(`[WebRTC] sendDtmf: PeerConnection not ready (signalingState=${pc.signalingState}, connectionState=${pc.connectionState}, iceConnectionState=${pc.iceConnectionState})`);
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'PeerConnectionNotReady',
                        message: `Cannot send DTMF: PeerConnection not ready (signalingState=${pc.signalingState}, connectionState=${pc.connectionState}, iceConnectionState=${pc.iceConnectionState})`,
                        phase: 'sendDtmf',
                        sessionId: activeSession.id
                    }
                });
                return false;
            }
            
            console.log(`[WebRTC] sendDtmf: PeerConnection ready, sending DTMF digit '${digit}'`);

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
                activeSession.sendDTMF(digit);
                console.log(`[WebRTC] sendDtmf: ✓ DTMF digit '${digit}' sent successfully (sessionId=${activeSession.id})`);
                sendEvent({
                    type: 'dtmf_sent',
                    data: {
                        digit: digit,
                        success: true,
                        sessionId: activeSession.id
                    }
                });
                return true;
            } catch (dtmfError) {
                console.error(`[WebRTC] sendDtmf: Error sending DTMF (sessionId=${activeSession.id}):`, dtmfError);
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'SendDtmfError',
                        message: 'Failed to send DTMF: ' + dtmfError.message,
                        phase: 'sendDtmf',
                        sessionId: activeSession.id,
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
    
    // Флаг для предотвращения повторного вызова startRecording
    let _isRecordingStarting = false;
    // Флаг для отмены записи если stopRecording вызван во время startRecording (стабилизации)
    let _recordingAborted = false;
    
    // Запуск записи звонка
    async function startRecording() {
        // КРИТИЧНО: Защита от повторного вызова startRecording
        // Если запись уже запускается или запущена, игнорируем повторный вызов
        if (_isRecordingStarting) {
            console.warn('[WebRTC] startRecording: Already starting recording, ignoring duplicate call');
            return;
        }
        
        if (mediaRecorder && mediaRecorder.state === 'recording') {
            console.warn('[WebRTC] startRecording: MediaRecorder is already recording, ignoring duplicate call');
            return;
        }
        
        _isRecordingStarting = true;
        _recordingAborted = false;
        
        try {
            // КРИТИЧНО: Принудительно очищаем старый MediaRecorder перед началом новой записи
            // Это предотвращает использование остатков от предыдущего звонка, которые могут привести к пустым записям
            if (mediaRecorder) {
                console.log('[WebRTC] startRecording: Cleaning up old MediaRecorder (state:', mediaRecorder.state, ')');
                try {
                    if (mediaRecorder.state === 'recording') {
                        console.log('[WebRTC] startRecording: Stopping old MediaRecorder that is still recording');
                        mediaRecorder.stop();
                    }
                } catch (e) {
                    console.warn('[WebRTC] startRecording: Error stopping old MediaRecorder:', e);
                }
                // Обнуляем ссылку на старый MediaRecorder
                mediaRecorder = null;
            }
            
            // КРИТИЧНО: Очищаем старые чанки перед началом новой записи
            // Если чанки от предыдущего звонка остались, они могут помешать новой записи
            if (recordingChunks.length > 0) {
                console.log('[WebRTC] startRecording: Clearing', recordingChunks.length, 'old chunks from previous recording');
                recordingChunks = [];
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
            
            // ===== СБОР АУДИО ТРЕКОВ ДЛЯ ЗАПИСИ =====
            // Стратегия: записываем ОБА направления (микрофон + голос абонента)
            // через AudioContext mixer → единый MediaRecorder
            
            // КРИТИЧНО: Закрываем старый recordingStream и его AudioContext перед созданием нового
            // Это предотвращает использование остатков от предыдущего звонка
            if (recordingStream && recordingStream._audioContext) {
                const oldAudioContext = recordingStream._audioContext;
                console.log('[WebRTC] startRecording: Closing old AudioContext (state:', oldAudioContext.state, ')');
                try {
                    if (oldAudioContext.state !== 'closed') {
                        await oldAudioContext.close();
                        console.log('[WebRTC] startRecording: Old AudioContext closed');
                    }
                } catch (e) {
                    console.warn('[WebRTC] startRecording: Error closing old AudioContext:', e);
                }
                delete recordingStream._audioContext;
            }
            recordingStream = null; // Очищаем ссылку на старый поток
            
            // Микшируем все треки в один поток через AudioContext
            const audioContext = new (window.AudioContext || window.webkitAudioContext)();
            
            // КРИТИЧНО: В WebView2 (Chromium) AudioContext стартует в состоянии "suspended".
            // Без resume() никакая обработка аудио не происходит → MediaRecorder записывает тишину.
            if (audioContext.state === 'suspended') {
                console.log('[WebRTC] startRecording: AudioContext is suspended, resuming...');
                await audioContext.resume();
                console.log('[WebRTC] startRecording: AudioContext resumed, state:', audioContext.state);
            }
            
            const destination = audioContext.createMediaStreamDestination();
            const audioSources = [];
            let localConnected = false;
            let remoteConnected = false;
            
            // ===== 1. ЛОКАЛЬНЫЙ АУДИО (МИКРОФОН) =====
            // Приоритет: session.localStream > pc.getSenders()
            // session.localStream — это оригинальный MediaStream от getUserMedia,
            // createMediaStreamSource с ним работает надёжно.
            if (session.localStream) {
                try {
                    const source = audioContext.createMediaStreamSource(session.localStream);
                    source.connect(destination);
                    audioSources.push(source);
                    localConnected = true;
                    const tracks = session.localStream.getAudioTracks();
                    console.log('[WebRTC] startRecording: LOCAL audio connected via session.localStream, tracks:', tracks.length,
                        tracks.map(t => `${t.label}:${t.readyState}:enabled=${t.enabled}`).join(', '));
                    sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] startRecording: LOCAL audio via session.localStream OK (${tracks.length} tracks)` } });
                } catch (err) {
                    console.error('[WebRTC] startRecording: Error connecting session.localStream:', err);
                }
            }
            
            // Fallback: трек из PeerConnection sender (работает для исходящих звонков)
            if (!localConnected && pc.getSenders) {
                for (const sender of pc.getSenders()) {
                    if (sender.track && sender.track.kind === 'audio') {
                        try {
                            // ВАЖНО: используем оригинальный трек, НЕ фильтруем по readyState
                            const localStream = new MediaStream([sender.track]);
                            const source = audioContext.createMediaStreamSource(localStream);
                            source.connect(destination);
                            audioSources.push(source);
                            localConnected = true;
                            console.log('[WebRTC] startRecording: LOCAL audio connected via sender track:', sender.track.label,
                                'readyState:', sender.track.readyState, 'enabled:', sender.track.enabled);
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] startRecording: LOCAL audio via sender OK (${sender.track.label}, ${sender.track.readyState})` } });
                            break; // Нужен только один локальный трек
                        } catch (err) {
                            console.error('[WebRTC] startRecording: Error connecting sender track:', err);
                        }
                    }
                }
            }
            
            // ===== 2. УДАЛЁННЫЙ АУДИО (ГОЛОС АБОНЕНТА) =====
            // Приоритет: pc.getReceivers() > remoteAudio.srcObject (если LIVE) > pc.getRemoteStreams()
            // pc.getReceivers() — самый надёжный источник, всегда содержит актуальные треки текущей сессии
            // remoteAudio.srcObject может содержать старый трек от предыдущего звонка (ended)
            
            // Основной источник: receiver tracks напрямую из PeerConnection
            if (pc.getReceivers) {
                for (const receiver of pc.getReceivers()) {
                    // ВАЖНО: НЕ фильтруем по readyState — трек может быть временно "ended"
                    // из-за SDP renegotiation, но createMediaStreamSource может восстановиться
                    if (receiver.track && receiver.track.kind === 'audio') {
                        try {
                            const receiverStream = new MediaStream([receiver.track]);
                            const source = audioContext.createMediaStreamSource(receiverStream);
                            source.connect(destination);
                            audioSources.push(source);
                            remoteConnected = true;
                            console.log('[WebRTC] startRecording: REMOTE audio connected via receiver track:',
                                'readyState:', receiver.track.readyState, 'enabled:', receiver.track.enabled,
                                'muted:', receiver.track.muted);
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] startRecording: REMOTE audio via receiver OK (${receiver.track.readyState})` } });
                            break;
                        } catch (err) {
                            console.error('[WebRTC] startRecording: Error connecting receiver track:', err);
                        }
                    }
                }
            }
            
            // Fallback 1: remoteAudio.srcObject (только если содержит LIVE треки)
            // Это может быть полезно, если getReceivers() не работает, но нужно проверить readyState
            if (!remoteConnected) {
                const remoteAudioEl = document.getElementById('remoteAudio');
                if (remoteAudioEl && remoteAudioEl.srcObject) {
                    try {
                        const remoteStream = remoteAudioEl.srcObject;
                        const remoteTracks = remoteStream.getAudioTracks();
                        console.log('[WebRTC] startRecording: remoteAudio.srcObject found, tracks:', remoteTracks.length,
                            remoteTracks.map(t => `${t.label||t.id}:${t.readyState}:enabled=${t.enabled}:muted=${t.muted}`).join(', '));
                        
                        // КРИТИЧНО: Используем только LIVE треки (ended треки от предыдущего звонка игнорируем)
                        const liveTracks = remoteTracks.filter(t => t.readyState === 'live');
                        if (liveTracks.length > 0) {
                            const liveStream = new MediaStream(liveTracks);
                            const source = audioContext.createMediaStreamSource(liveStream);
                            source.connect(destination);
                            audioSources.push(source);
                            remoteConnected = true;
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] startRecording: REMOTE audio via remoteAudio.srcObject OK (${liveTracks.length} live tracks)` } });
                        } else {
                            console.warn('[WebRTC] startRecording: remoteAudio.srcObject has no LIVE tracks (all ended), skipping');
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] startRecording: remoteAudio.srcObject tracks are ENDED (from previous call?), skipping` } });
                        }
                    } catch (err) {
                        console.error('[WebRTC] startRecording: Error connecting remoteAudio.srcObject:', err);
                        sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] startRecording: REMOTE audio via srcObject FAILED: ${err.message}` } });
                    }
                }
            }
            
            // Fallback 2: getRemoteStreams (deprecated но работает в Chromium)
            if (!remoteConnected && pc.getRemoteStreams) {
                try {
                    const remoteStreams = pc.getRemoteStreams();
                    for (const rs of remoteStreams) {
                        const audioTrks = rs.getAudioTracks();
                        if (audioTrks.length > 0) {
                            const source = audioContext.createMediaStreamSource(rs);
                            source.connect(destination);
                            audioSources.push(source);
                            remoteConnected = true;
                            console.log('[WebRTC] startRecording: REMOTE audio connected via getRemoteStreams():', audioTrks.length, 'tracks');
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] startRecording: REMOTE audio via getRemoteStreams OK` } });
                            break;
                        }
                    }
                } catch (err) {
                    console.error('[WebRTC] startRecording: Error with getRemoteStreams:', err);
                }
            }
            
            console.log('[WebRTC] startRecording: Summary - localConnected:', localConnected, ', remoteConnected:', remoteConnected);
            sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] startRecording: Summary - local=${localConnected}, remote=${remoteConnected}` } });
            
            if (!localConnected && !remoteConnected) {
                console.error('[WebRTC] Cannot start recording: no audio sources connected');
                sendEvent({ 
                    type: 'error', 
                    data: { 
                        name: 'RecordingError',
                        message: 'Cannot start recording: no audio sources connected (local=' + localConnected + ', remote=' + remoteConnected + ')',
                        phase: 'startRecording'
                    } 
                });
                audioContext.close().catch(() => {});
                return;
            }
            
            // Используем микшированный поток для записи
            recordingStream = destination.stream;
            
            // Проверяем destination stream
            const destinationTracks = recordingStream.getAudioTracks();
            console.log('[WebRTC] startRecording: Destination stream tracks:', destinationTracks.length,
                destinationTracks.map(t => `${t.label||t.id}:${t.readyState}:enabled=${t.enabled}`).join(', '));
            
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
            
            // ===== ОЖИДАНИЕ СТАБИЛИЗАЦИИ ПОТОКОВ ПЕРЕД НАЧАЛОМ ЗАПИСИ =====
            // КРИТИЧНО: Не начинаем запись сразу — даём время потокам стабилизироваться.
            // Это предотвращает:
            // 1. Шум/тишину в начале записи (потоки ещё не готовы)
            // 2. Запись только одного потока (второй подключается позже)
            // 3. Проблемы с синхронизацией локального и удалённого аудио
            
            console.log('[WebRTC] Waiting for audio streams to stabilize before starting recording...');
            sendEvent({ type: 'js_log', data: { level: 'critical', message: '[WebRTC] Waiting for streams stabilization (local=' + localConnected + ', remote=' + remoteConnected + ')' } });
            
            // Создаём AnalyserNode ДО начала записи для проверки активности
            let analyser = null;
            
            // Функция проверки активности потоков через AnalyserNode
            // КРИТИЧНО: Увеличена строгость проверки для предотвращения пустых записей
            // Проверяем наличие РЕАЛЬНОГО аудио сигнала (не тишины), чтобы не начинать запись до начала речи IVR
            let consecutiveActiveChecks = 0; // Счетчик последовательных проверок с активным сигналом
            const requiredConsecutiveChecks = 3; // Требуем 3 последовательные проверки с активным сигналом (300ms)
            
            const checkStreamsActive = () => {
                if (!analyser) {
                    consecutiveActiveChecks = 0;
                    return false;
                }
                try {
                    const dataArray = new Uint8Array(analyser.frequencyBinCount);
                    analyser.getByteTimeDomainData(dataArray);
                    // Проверяем, есть ли реальный сигнал (отклонение от тишины = 128)
                    let maxDeviation = 0;
                    let samplesWithSignal = 0;
                    let samplesWithStrongSignal = 0; // Сэмплы с сильным сигналом (> 10)
                    
                    for (let i = 0; i < dataArray.length; i++) {
                        const deviation = Math.abs(dataArray[i] - 128);
                        if (deviation > maxDeviation) maxDeviation = deviation;
                        // Считаем сэмплы с реальным сигналом (отклонение > 3)
                        if (deviation > 3) samplesWithSignal++;
                        // Считаем сэмплы с сильным сигналом (отклонение > 10) - это реальный голос, не шум
                        if (deviation > 10) samplesWithStrongSignal++;
                    }
                    
                    // Более строгая проверка: нужен не только максимальный сигнал, но и достаточное количество активных сэмплов
                    // И требуем наличие СИЛЬНОГО сигнала (отклонение > 10) - это реальный голос, не тишина и не шум
                    // Это предотвращает ложные срабатывания от шума/артефактов и начало записи до начала речи IVR
                    const hasRealSignal = maxDeviation > 10 && samplesWithStrongSignal > 20;
                    
                    if (hasRealSignal) {
                        consecutiveActiveChecks++;
                        console.log(`[WebRTC] checkStreamsActive: maxDeviation=${maxDeviation}, strongSignalSamples=${samplesWithStrongSignal}, consecutiveChecks=${consecutiveActiveChecks}/${requiredConsecutiveChecks} - REAL SIGNAL DETECTED`);
                        // Требуем несколько последовательных проверок с активным сигналом, чтобы убедиться, что это не случайный шум
                        return consecutiveActiveChecks >= requiredConsecutiveChecks;
                    } else {
                        consecutiveActiveChecks = 0; // Сбрасываем счетчик при отсутствии сигнала
                        return false;
                    }
                } catch (e) {
                    console.warn('[WebRTC] checkStreamsActive error:', e);
                    consecutiveActiveChecks = 0;
                    return false;
                }
            };
            try {
                analyser = audioContext.createAnalyser();
                analyser.fftSize = 2048;
                analyser.smoothingTimeConstant = 0.3;
                // Подключаем analyser ПАРАЛЛЕЛЬНО с destination
                audioSources.forEach(src => {
                    try { src.connect(analyser); } catch(e) { /* ignore */ }
                });
                recordingStream._analyser = analyser;
            } catch (e) {
                console.warn('[WebRTC] Could not create AnalyserNode:', e);
            }
            
            // Ожидаем стабилизации: максимум 5 секунд, проверяем каждые 100ms
            // Увеличено время ожидания для более надежной проверки готовности аудио потоков
            // Это дает время IVR начать говорить перед началом записи
            let stabilizationWaitTime = 0;
            const maxWaitTime = 1500; // 1.5 секунды максимум — достаточно для подключения потоков, не блокирует короткие звонки
            const checkInterval = 100; // проверяем каждые 100ms
            
            const waitForStabilization = () => {
                return new Promise((resolve) => {
                    const checkStabilization = () => {
                        if (_recordingAborted) {
                            console.warn(`[WebRTC] Recording aborted during stabilization (waited ${stabilizationWaitTime}ms)`);
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] Recording aborted during stabilization (${stabilizationWaitTime}ms)` } });
                            resolve();
                            return;
                        }

                        stabilizationWaitTime += checkInterval;
                        const streamsActive = checkStreamsActive();
                        
                        if (streamsActive || stabilizationWaitTime >= maxWaitTime) {
                            if (streamsActive) {
                                console.log(`[WebRTC] Streams stabilized after ${stabilizationWaitTime}ms - audio detected, starting recording`);
                                sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] Streams stabilized (${stabilizationWaitTime}ms) - starting recording` } });
                            } else {
                                console.warn(`[WebRTC] Streams not fully active after ${stabilizationWaitTime}ms, starting recording anyway`);
                                sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] Starting recording without full stabilization (${stabilizationWaitTime}ms)` } });
                            }
                            resolve();
                        } else {
                            setTimeout(checkStabilization, checkInterval);
                        }
                    };
                    // Первая проверка через 200ms (даём время потокам подключиться)
                    setTimeout(checkStabilization, 200);
                });
            };
            
            // Ждём стабилизации, затем начинаем запись
            await waitForStabilization();

            if (_recordingAborted) {
                console.warn('[WebRTC] Recording was aborted during stabilization, skipping MediaRecorder start');
                _isRecordingStarting = false;
                audioContext.close().catch(() => {});
                recordingStream = null;
                sendEvent({
                    type: 'recording_stopped',
                    data: {
                        reason: 'aborted_during_stabilization',
                        message: 'Recording aborted because call ended during stream stabilization'
                    }
                });
                return;
            }
            
            // КРИТИЧНО: Финальная проверка перед началом записи
            // Убеждаемся, что AudioContext в состоянии "running" и destination stream имеет активные треки
            if (audioContext.state !== 'running') {
                console.warn('[WebRTC] AudioContext is not running before recording start (state:', audioContext.state, '), attempting to resume...');
                try {
                    await audioContext.resume();
                    console.log('[WebRTC] AudioContext resumed, new state:', audioContext.state);
                } catch (e) {
                    console.error('[WebRTC] Failed to resume AudioContext:', e);
                }
            }
            
            // Финальная проверка: убеждаемся, что destination stream имеет активные треки
            const finalAudioTracks = destination.stream.getAudioTracks();
            const activeFinalTracks = finalAudioTracks.filter(t => t.readyState === 'live' && t.enabled);
            if (activeFinalTracks.length === 0) {
                console.error('[WebRTC] Cannot start recording: Destination stream has no active audio tracks after stabilization.');
                sendEvent({
                    type: 'error',
                    data: {
                        name: 'RecordingError',
                        message: 'Cannot start recording: Destination stream has no active audio tracks after stabilization',
                        phase: 'startRecording'
                    }
                });
                _isRecordingStarting = false; // Сбрасываем флаг при ошибке
                audioContext.close().catch(() => {});
                return;
            }
            
            console.log('[WebRTC] Final check before recording: AudioContext state=', audioContext.state, ', active tracks=', activeFinalTracks.length);
            
            // Запускаем запись с timeslice 1000ms — данные приходят каждую секунду,
            // а не одним гигантским blob при stop(). Это гарантирует, что ondataavailable
            // вызывается регулярно, даже если stop() потеряет данные.
            mediaRecorder.start(1000);
            console.log('[WebRTC] Recording started (MediaRecorder.start(1000) - data every 1s), AudioContext state:', audioContext.state);
            _isRecordingStarting = false; // Сбрасываем флаг после успешного запуска
            sendEvent({ type: 'recording_started' });
            
            // Через 2 секунды после начала записи проверяем здоровье записи
            // (AnalyserNode уже создан выше в waitForStabilization)
            setTimeout(() => {
                if (mediaRecorder && mediaRecorder.state === 'recording') {
                    const ctx = recordingStream?._audioContext;
                    const tracks = recordingStream?.getAudioTracks() || [];
                    const trackInfo = tracks.map(t => `${t.label||t.id}:${t.readyState}:enabled=${t.enabled}`).join(', ');
                    
                    // Используем уже созданный analyser из recordingStream
                    const analyser = recordingStream?._analyser;
                    let audioLevel = 'N/A';
                    let hasSilence = true;
                    if (analyser) {
                        const dataArray = new Uint8Array(analyser.frequencyBinCount);
                        analyser.getByteTimeDomainData(dataArray);
                        // Ищем отклонение от 128 (тишина = ровно 128 для всех сэмплов)
                        let maxDeviation = 0;
                        for (let i = 0; i < dataArray.length; i++) {
                            const deviation = Math.abs(dataArray[i] - 128);
                            if (deviation > maxDeviation) maxDeviation = deviation;
                        }
                        audioLevel = maxDeviation;
                        hasSilence = maxDeviation < 2; // <2 = полная тишина
                    }
                    
                    const healthMsg = `Recording health (2s): chunks=${recordingChunks.length}, AudioCtx=${ctx?.state}, audioLevel=${audioLevel}${hasSilence ? ' ❌ SILENCE!' : ' ✅ HAS AUDIO'}, local=${localConnected}, remote=${remoteConnected}, tracks=[${trackInfo}]`;
                    console.log(`[WebRTC] ${healthMsg}`);
                    sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] ${healthMsg}` } });
                    
                    if (hasSilence) {
                        console.warn('[WebRTC] ⚠️ Recording appears to be SILENT! Possible causes:');
                        console.warn('[WebRTC]   - createMediaStreamSource from WebRTC track not producing audio');
                        console.warn('[WebRTC]   - Track was stopped (readyState=ended) before recording started');
                        console.warn('[WebRTC]   - AudioContext pipeline issue');
                        
                        // Доп. диагностика: проверяем состояние треков PeerConnection
                        const pc2 = session?.connection;
                        if (pc2) {
                            const senders = pc2.getSenders();
                            const receivers = pc2.getReceivers();
                            const sInfo = senders.map(s => s.track ? `${s.track.kind}:${s.track.readyState}:${s.track.label}` : 'null').join(', ');
                            const rInfo = receivers.map(r => r.track ? `${r.track.kind}:${r.track.readyState}:${r.track.label}` : 'null').join(', ');
                            console.log(`[WebRTC] PC senders: [${sInfo}], receivers: [${rInfo}]`);
                            sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] SILENCE DEBUG: senders=[${sInfo}], receivers=[${rInfo}]` } });
                        }
                    }
                }
            }, 2000);
            
            // Повторная проверка через 5 секунд
            setTimeout(() => {
                if (mediaRecorder && mediaRecorder.state === 'recording' && analyser) {
                    const dataArray = new Uint8Array(analyser.frequencyBinCount);
                    analyser.getByteTimeDomainData(dataArray);
                    let maxDeviation = 0;
                    for (let i = 0; i < dataArray.length; i++) {
                        const deviation = Math.abs(dataArray[i] - 128);
                        if (deviation > maxDeviation) maxDeviation = deviation;
                    }
                    const hasSilence = maxDeviation < 2;
                    const msg = `Recording health (5s): chunks=${recordingChunks.length}, audioLevel=${maxDeviation}${hasSilence ? ' ❌ SILENCE!' : ' ✅ HAS AUDIO'}`;
                    console.log(`[WebRTC] ${msg}`);
                    sendEvent({ type: 'js_log', data: { level: 'critical', message: `[WebRTC] ${msg}` } });
                }
            }, 5000);
        } catch (error) {
            // Сбрасываем флаг при ошибке
            _isRecordingStarting = false;
            
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
            console.log('[WebRTC] stopRecording called, mediaRecorder state:', mediaRecorder?.state, 'chunks count:', recordingChunks.length, ', _isRecordingStarting:', _isRecordingStarting);

            if (_isRecordingStarting) {
                console.warn('[WebRTC] stopRecording: Recording is still starting (in stabilization phase), setting abort flag');
                _recordingAborted = true;
                _isRecordingStarting = false;
                return;
            }

            _isRecordingStarting = false;
            
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
                // Отключаем AnalyserNode
                if (recordingStream._analyser) {
                    try { recordingStream._analyser.disconnect(); } catch(e) {}
                    delete recordingStream._analyser;
                }
                
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
        setHold,
        getStatus,
        stop,
        cleanupSessions,
        ping,
        pong,
        resetEngine,
        getStats,
        isCallActuallyActive,
        checkCallActivityWithStats,
        checkCallActivity: function(sessionId) {
            try {
                const isActive = isCallActuallyActive(sessionId);
                sendEvent({
                    type: 'call_activity_check',
                    data: {
                        sessionId: sessionId,
                        active: isActive
                    }
                });
                
                // КРИТИЧНО: Если звонок активен, но call_accepted еще не был отправлен, отправляем его
                // Это важно для исходящих звонков, где события accepted/confirmed могут не сработать
                if (isActive) {
                    // Находим сессию и проверяем, была ли уже отправлена call_accepted
                    const matches = (s) => {
                        if (!s) return false;
                        if (!sessionId) return true;
                        try {
                            return (s.id === sessionId || s._softphoneSessionId === sessionId || s.request?.call_id === sessionId);
                        } catch {
                            return false;
                        }
                    };
                    
                    const candidates = [
                        window._activeSession, 
                        session, 
                        window._incomingSession,
                        ...(ua && ua.sessions ? Array.from(ua.sessions.values()) : [])
                    ].filter(Boolean);
                    
                    const s = candidates.find(matches);
                    if (s) {
                        // Проверяем, есть ли удаленные аудио треки - это означает, что звонок принят
                        const pc = s.connection;
                        if (pc) {
                            const receivers = pc.getReceivers();
                            const hasRemoteAudio = receivers.some(r => r.track && r.track.kind === 'audio' && r.track.readyState === 'live');
                            
                            if (hasRemoteAudio) {
                                console.log('[WebRTC] checkCallActivity: Remote audio detected, ensuring call_accepted is sent');
                                // Используем wireSessionEvents функцию sendCallAcceptedOnce через глобальный доступ
                                // Но проще - просто отправим событие напрямую
                                sendEvent({
                                    type: 'call_accepted',
                                    data: {
                                        sessionId: sessionId,
                                        source: 'checkCallActivity_remote_audio_detected'
                                    }
                                });
                            }
                        }
                    }
                }
                
                return isActive;
            } catch (error) {
                console.error('[WebRTC] ERROR in checkCallActivity:', error);
                sendEvent({
                    type: 'call_activity_check',
                    data: {
                        sessionId: sessionId,
                        active: false,
                        error: error.message
                    }
                });
                return false;
            }
        },
        setMute,
        sendDtmf,
        setOriginatePending,
        setOriginateAcceptedSessionId,
        setAllowedCallerIds,
        enumerateAudioDevices,
        switchAudioDevice,
        startRecording,
        stopRecording
    };
})();

