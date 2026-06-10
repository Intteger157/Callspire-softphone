import JsSIP from 'jssip'
import { diag } from './diag'

/**
 * Toggle JsSIP's internal `debug`-library logging. When enabled, JsSIP prints
 * namespaced debug output to the browser console (matching `localStorage.debug`
 * = `JsSIP:*`). This is wired to the "Verbose" switch in the Debug panel.
 */
export function setJsSipVerbose(on: boolean): void {
  try {
    const d = (JsSIP as unknown as { debug?: { enable?: (p: string) => void; disable?: () => void } }).debug
    if (!d) return
    if (on) d.enable?.('JsSIP:*')
    else d.disable?.()
    diag.info('SIP', `JsSIP verbose=${on ? 'on' : 'off'}`)
  } catch (err) {
    diag.warn('SIP', `setJsSipVerbose failed: ${(err as Error)?.message || err}`)
  }
}

export type WebRtcIceServer = { urls: string; username?: string; credential?: string }

export type SipWebRtcStatus = 'idle' | 'connecting' | 'registered' | 'in_call' | 'error'

export type AudioProcessingOptions = {
  echoCancellation?: boolean
  noiseSuppression?: boolean
  autoGainControl?: boolean
}

export type CallAudioOptions = {
  /** Prefer this microphone for `getUserMedia` (applies to this call only). */
  inputDeviceId?: string
  /** Play remote audio to this output device (`HTMLAudioElement.setSinkId`, Chromium). */
  outputDeviceId?: string
  /** Audio processing toggles applied as `getUserMedia` constraints. */
  processing?: AudioProcessingOptions
}

function sipHostFromUri(sipUri: string): string {
  const m = sipUri.trim().match(/^sip:[^@]+@([^;>\s]+)/i)
  return m ? m[1] : ''
}

/** MikoPBX (and similar): digest may apply to `ext` or `ext-WS` while REGISTER AoR is `ext-WS@...`. */
function mikoDigestAuthUserCandidates(authorizationUser: string | undefined, sipUri: string): string[] {
  const m = sipUri.trim().match(/^sip:([^@;>]+)@/i) || sipUri.trim().match(/^sip:([^@;>]+)/i)
  const sipUser = (m?.[1] || '').trim()
  const base = sipUser.replace(/-WS$/i, '')
  const seen = new Set<string>()
  const out: string[] = []
  const add = (s: string) => {
    const t = s.trim()
    if (!t || seen.has(t)) return
    seen.add(t)
    out.push(t)
  }
  const p = (authorizationUser || '').trim()
  // When the proxy sends an explicit digest username (sipAuthUser), it matches Asterisk auth
  // sections — try it before the URI user-part (often `<ext>-WS` while auth is `<ext>`).
  if (p) add(p)
  if (sipUser) add(sipUser)
  if (base) {
    add(base)
    add(`${base}-WS`)
  }
  if (out.length === 0) {
    if (p) out.push(p)
    else if (base) out.push(base)
  }
  return out
}

type SipRegisterDigestRetry = Error & { readonly _tag: 'SipRegisterDigestRetry'; responseCode?: number }

function createSipRegisterDigestRetry(message: string, responseCode?: number): SipRegisterDigestRetry {
  const e = new Error(message) as Error & { _tag?: 'SipRegisterDigestRetry'; responseCode?: number }
  e.name = 'SipRegisterDigestRetry'
  e._tag = 'SipRegisterDigestRetry'
  e.responseCode = responseCode
  return e as SipRegisterDigestRetry
}

function isSipRegisterDigestRetry(e: unknown): e is SipRegisterDigestRetry {
  return e instanceof Error && (e as SipRegisterDigestRetry)._tag === 'SipRegisterDigestRetry'
}

/** JsSIP UA in this project is typed loosely; we only need a small surface area. */
type AnyUa = {
  isRegistered(): boolean
  call: (target: string, options: Record<string, unknown>) => unknown
  stop: () => void
  configuration?: { uri?: { host?: string }; pcConfig?: RTCConfiguration }
  on: (ev: string, fn: (...args: unknown[]) => void) => void
  start: () => void
}

type AnySession = {
  terminate: (opts?: { status_code?: number; reason_phrase?: string }) => void
  answer?: (opts: Record<string, unknown>) => void
  on: (ev: string, fn: (...args: unknown[]) => void) => void
  mute?: (opts: { audio?: boolean; video?: boolean }) => void
  unmute?: (opts: { audio?: boolean; video?: boolean }) => void
  hold?: () => void
  unhold?: () => void
  sendDTMF?: (tone: string, opts?: Record<string, unknown>) => void
  remote_identity?: { uri?: { user?: string; toString?: () => string }; display_name?: string }
  /** JsSIP `RTCSession.connection` is the underlying `RTCPeerConnection`. */
  connection?: RTCPeerConnection
  id?: string
  direction?: 'incoming' | 'outgoing'
}

export type IncomingCall = {
  /** Caller display number (best effort, falls back to URI). */
  from: string
  /** Caller display name when set by remote. */
  displayName?: string
  /** Accept the call with optional audio I/O preferences. */
  accept: (audio?: CallAudioOptions) => Promise<void>
  /** Reject with 486 Busy Here (or terminate before answer). */
  reject: () => void
}

const DEFAULT_PROCESSING: Required<AudioProcessingOptions> = {
  echoCancellation: true,
  noiseSuppression: true,
  autoGainControl: true,
}

/** Preferred codec order on the audio transceiver. */
const CODEC_PREFERENCE = ['audio/opus', 'audio/PCMU', 'audio/PCMA', 'audio/telephone-event']

const STATS_INTERVAL_MS = 2000

/**
 * Truncate SDP in WSS dumps — full SDPs are captured separately as snapshots.
 * Keeps WSS category readable when scrolling.
 */
const WSS_FRAME_MAX = 2000

function redactSipFrameForDiag(frame: string): string {
  // Avoid leaking digest Authorization / Proxy-Authorization into diagnostics dumps.
  // Keep request/response lines and non-sensitive headers intact for debugging.
  try {
    const lines = frame.split(/\r?\n/)
    const out: string[] = []
    for (const line of lines) {
      if (/^\s*Authorization\s*:/i.test(line)) {
        out.push('Authorization: ***REDACTED***')
        continue
      }
      if (/^\s*Proxy-Authorization\s*:/i.test(line)) {
        out.push('Proxy-Authorization: ***REDACTED***')
        continue
      }
      // Not a secret, but very noisy; trim it.
      if (/^\s*WWW-Authenticate\s*:/i.test(line)) {
        out.push('WWW-Authenticate: ***REDACTED***')
        continue
      }
      out.push(line)
    }
    return out.join('\r\n')
  } catch {
    return frame
  }
}

function redactSdpForDiag(sdp: string): string {
  // SDP dumps are super helpful, but must not contain ICE passwords or private IPs.
  try {
    let out = sdp
    out = out.replace(/^a=ice-ufrag:.*$/gim, 'a=ice-ufrag:***REDACTED***')
    out = out.replace(/^a=ice-pwd:.*$/gim, 'a=ice-pwd:***REDACTED***')
    // Best-effort: mask IPv4 addresses in candidate lines.
    out = out.replace(/^(a=candidate:[^\r\n]*\s)(\d{1,3}(?:\.\d{1,3}){3})(\s\d+\s typ\s\w+[^\r\n]*)$/gim, '$1***.***.***.***$3')
    return out
  } catch {
    return sdp
  }
}

/** Which SIP methods to surface in the WSS dump (case-insensitive). */
const WSS_METHODS_OF_INTEREST = [
  'INVITE',
  'BYE',
  'CANCEL',
  'UPDATE',
  'ACK',
  'PRACK',
  'REFER',
  'NOTIFY',
  'INFO',
  'MESSAGE',
  'SUBSCRIBE',
  'OPTIONS',
]

export class SipWebRtcClient {
  private ua: AnyUa | null = null
  private session: AnySession | null = null
  private _sipUri = ''
  private _status: SipWebRtcStatus = 'idle'
  private _lastError = ''
  private _remoteAudioEl: HTMLAudioElement | null = null
  private _outputDeviceId: string | undefined
  private _processing: Required<AudioProcessingOptions> = { ...DEFAULT_PROCESSING }
  private _activePc: RTCPeerConnection | null = null
  private _statsTimer: number | null = null
  private _localStream: MediaStream | null = null

  /** Fired when an RTC session ends (remote hangup or failure). */
  onSessionEnd: (() => void) | null = null
  /** INVITE or media setup failed (includes SIP cause when available). */
  onInviteFailed: ((message: string) => void) | null = null
  /** Transport drop or re-registration failure after a successful connect. */
  onConnectionProblem: ((message: string) => void) | null = null
  /** Remote side sent 1xx progress (ringing). */
  onCallRinging: (() => void) | null = null
  /** Dialog confirmed; media may start (use with onCallRinging for UI). */
  onCallConfirmed: (() => void) | null = null
  /** Incoming INVITE received. The handler must call `accept`/`reject` (default: auto-reject after 30 s). */
  onIncomingCall: ((info: IncomingCall) => void) | null = null
  /** Fired when the local `MediaStream` is (re)attached to the active peer connection. */
  onLocalStream: ((stream: MediaStream | null) => void) | null = null
  /** Fired when the active `RTCPeerConnection` is (re)created or torn down. */
  onPeerConnection: ((pc: RTCPeerConnection | null) => void) | null = null

  private _intentionalDisconnect = false
  /** Set while `connect()` promise is pending; lets `disconnect()` cancel it instantly. */
  private _cancelPendingConnect: ((reason: string) => void) | null = null

  get status() {
    return this._status
  }

  get lastError() {
    return this._lastError
  }

  private setStatus(s: SipWebRtcStatus) {
    if (this._status === s) return
    this._status = s
    diag.info('SIP', `status → ${s}`)
  }

  isRegistered(): boolean {
    return this.ua?.isRegistered() === true
  }

  getPeerConnection(): RTCPeerConnection | null {
    return this._activePc
  }

  getLocalStream(): MediaStream | null {
    return this._localStream
  }

  /** Get or update the processing defaults that will be applied to the next call. */
  setAudioProcessing(p: AudioProcessingOptions): void {
    this._processing = {
      echoCancellation: p.echoCancellation ?? this._processing.echoCancellation,
      noiseSuppression: p.noiseSuppression ?? this._processing.noiseSuppression,
      autoGainControl: p.autoGainControl ?? this._processing.autoGainControl,
    }
    diag.info('AUDIO', 'audio processing defaults updated', this._processing)
  }

  getAudioProcessing(): Required<AudioProcessingOptions> {
    return { ...this._processing }
  }

  /** Change speaker for the current or next remote audio (Chromium `setSinkId`). */
  setOutputDevice(deviceId: string | undefined): void {
    const clean = deviceId?.trim() || undefined
    if (clean === this._outputDeviceId) return
    this._outputDeviceId = clean
    diag.info('AUDIO', `output device → ${clean ?? '(default)'}`)
    this.applyOutputDevice()
  }

  /**
   * Replace the microphone track on an active call without a re-INVITE.
   * Returns the resulting `MediaStreamTrack`, or `null` if there is no active sender.
   */
  async replaceInputDevice(
    deviceId: string | undefined,
    processing?: AudioProcessingOptions,
  ): Promise<MediaStreamTrack | null> {
    const pc = this._activePc
    if (!pc) {
      diag.warn('AUDIO', 'replaceInputDevice: no active PC')
      return null
    }
    const sender = pc.getSenders().find((s) => s.track?.kind === 'audio')
    if (!sender) {
      diag.warn('AUDIO', 'replaceInputDevice: no audio sender on PC')
      return null
    }
    const constraints = this.buildAudioConstraints(deviceId, processing)
    diag.info('AUDIO', 'replaceInputDevice: requesting new track', { deviceId, constraints })
    let stream: MediaStream
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio: constraints, video: false })
    } catch (err) {
      const msg = (err as Error)?.message || 'getUserMedia failed'
      diag.error('AUDIO', `replaceInputDevice: getUserMedia failed: ${msg}`)
      throw err
    }
    const newTrack = stream.getAudioTracks()[0]
    if (!newTrack) {
      stream.getTracks().forEach((t) => t.stop())
      throw new Error('replaceInputDevice: no audio track in stream')
    }
    try {
      await sender.replaceTrack(newTrack)
    } catch (err) {
      newTrack.stop()
      const msg = (err as Error)?.message || 'replaceTrack failed'
      diag.error('AUDIO', `replaceInputDevice: replaceTrack failed: ${msg}`)
      throw err
    }
    // Stop old stream tracks (we now own a fresh MediaStream).
    const prev = this._localStream
    this._localStream = stream
    if (prev) {
      prev.getTracks().forEach((t) => {
        if (t !== newTrack) {
          try {
            t.stop()
          } catch {
            /* ignore */
          }
        }
      })
    }
    this.onLocalStream?.(stream)
    diag.info('AUDIO', 'replaceInputDevice: ok', {
      deviceId: newTrack.getSettings().deviceId,
      label: newTrack.label,
    })
    return newTrack
  }

  private getOrCreateRemoteAudio(): HTMLAudioElement {
    if (!this._remoteAudioEl) {
      const a = document.createElement('audio')
      a.autoplay = true
      a.setAttribute('playsinline', 'true')
      a.setAttribute('aria-hidden', 'true')
      Object.assign(a.style, { position: 'absolute', width: '0', height: '0', opacity: '0', pointerEvents: 'none' })
      document.body.appendChild(a)
      this._remoteAudioEl = a
    }
    return this._remoteAudioEl
  }

  private applyOutputDevice(): void {
    const el = this._remoteAudioEl
    const id = this._outputDeviceId
    if (!el || !id) return
    const fn = (el as HTMLAudioElement & { setSinkId?: (id: string) => Promise<void> }).setSinkId
    if (typeof fn !== 'function') {
      diag.warn('AUDIO', 'setSinkId not supported by this browser')
      return
    }
    fn.call(el, id).catch((err: Error) => {
      diag.warn('AUDIO', `setSinkId failed: ${err?.message || err}`)
    })
  }

  private clearRemoteAudio(): void {
    if (this._remoteAudioEl) {
      try {
        this._remoteAudioEl.srcObject = null
      } catch {
        /* ignore */
      }
    }
  }

  private buildAudioConstraints(
    deviceId: string | undefined,
    processing?: AudioProcessingOptions,
  ): MediaTrackConstraints {
    const p = {
      echoCancellation: processing?.echoCancellation ?? this._processing.echoCancellation,
      noiseSuppression: processing?.noiseSuppression ?? this._processing.noiseSuppression,
      autoGainControl: processing?.autoGainControl ?? this._processing.autoGainControl,
    }
    const constraints: MediaTrackConstraints & { deviceId?: ConstrainDOMString } = {
      echoCancellation: { ideal: p.echoCancellation } as ConstrainBoolean,
      noiseSuppression: { ideal: p.noiseSuppression } as ConstrainBoolean,
      autoGainControl: { ideal: p.autoGainControl } as ConstrainBoolean,
    }
    if (deviceId) {
      constraints.deviceId = { exact: deviceId }
    }
    return constraints
  }

  /**
   * Force audio transceivers to `sendrecv` — without this JsSIP sometimes
   * leaves them as the default `sendonly`/`recvonly` when processing
   * re-INVITEs, which leads to SDP mismatches like 488 Bad Media Description.
   */
  private enforceSendrecv(pc: RTCPeerConnection): void {
    try {
      for (const t of pc.getTransceivers()) {
        const kind = t.sender?.track?.kind || t.receiver?.track?.kind
        if (kind && kind !== 'audio') continue
        if (t.direction !== 'sendrecv') {
          try {
            t.direction = 'sendrecv'
            diag.debug('PC', 'transceiver.direction ← sendrecv', { mid: t.mid })
          } catch (err) {
            diag.warn('PC', `transceiver.direction failed: ${(err as Error)?.message || err}`)
          }
        }
      }
    } catch (err) {
      diag.warn('PC', `enforceSendrecv error: ${(err as Error)?.message || err}`)
    }
  }

  /** Re-order codecs on the audio transceiver so the preferred list comes first. */
  private applyCodecPreferences(pc: RTCPeerConnection): void {
    try {
      const caps =
        typeof RTCRtpSender !== 'undefined' && typeof RTCRtpSender.getCapabilities === 'function'
          ? RTCRtpSender.getCapabilities('audio')
          : null
      const codecs = caps?.codecs
      if (!codecs?.length) {
        diag.debug('PC', 'applyCodecPreferences: no capabilities available')
        return
      }
      type Cap = (typeof codecs)[number]
      const byName = (name: string): Cap[] =>
        codecs.filter((c: Cap) => (c.mimeType || '').toLowerCase() === name.toLowerCase())
      const preferred: Cap[] = []
      for (const want of CODEC_PREFERENCE) {
        preferred.push(...byName(want))
      }
      const rest = codecs.filter((c: Cap) => !preferred.includes(c))
      const ordered: Cap[] = preferred.concat(rest)
      const audioTransceivers = pc.getTransceivers().filter((t) => {
        const sender = t.sender
        const track = sender?.track
        if (track) return track.kind === 'audio'
        // before any track is attached, inspect mid/direction if possible
        return true
      })
      for (const t of audioTransceivers) {
        if (typeof t.setCodecPreferences === 'function') {
          try {
            t.setCodecPreferences(ordered)
          } catch (err) {
            diag.warn('PC', `setCodecPreferences failed: ${(err as Error)?.message || err}`)
          }
        }
      }
      diag.info(
        'PC',
        `codec preferences applied (${preferred.length}/${codecs.length})`,
        preferred.map((c) => c.mimeType),
      )
    } catch (err) {
      diag.warn('PC', `applyCodecPreferences error: ${(err as Error)?.message || err}`)
    }
  }

  /**
   * Wait until the peer connection reaches a stable signaling state, up to
   * `timeoutMs`. Used to avoid BYE while a re-INVITE is in progress (which
   * is a frequent source of spurious 488 Bad Media Description).
   */
  private async waitSignalingStable(pc: RTCPeerConnection | null, timeoutMs = 2000): Promise<void> {
    if (!pc || pc.signalingState === 'stable') return
    diag.debug('PC', `waitSignalingStable: current=${pc.signalingState}, timeout=${timeoutMs}ms`)
    await new Promise<void>((resolve) => {
      let done = false
      const finish = () => {
        if (done) return
        done = true
        pc.removeEventListener('signalingstatechange', onChange)
        clearTimeout(timer)
        resolve()
      }
      const onChange = () => {
        if (pc.signalingState === 'stable') finish()
      }
      pc.addEventListener('signalingstatechange', onChange)
      const timer = window.setTimeout(() => {
        diag.warn('PC', `waitSignalingStable: timed out, state=${pc.signalingState}`)
        finish()
      }, timeoutMs)
    })
  }

  async connect(params: {
    wsUrl: string
    sipUri: string
    password: string
    iceServers?: WebRtcIceServer[]
    /**
     * SIP digest auth username. Must be set when `sipUri` uses a different AoR than
     * the actual SIP account (e.g. MikoPBX WebRTC registers as `sip:<ext>-WS@...`
     * while credentials belong to `<ext>`). Without this JsSIP derives the auth
     * username from the URI user part and registration fails with 401.
     */
    authorizationUser?: string
  }): Promise<void> {
    this.disconnect()
    this._lastError = ''
    this._sipUri = params.sipUri
    if (!params.wsUrl || !params.sipUri || !params.password) {
      throw new Error('WebRTC: wsUrl, sipUri and password are required')
    }

    this.setStatus('connecting')

    const iceServers: WebRtcIceServer[] =
      params.iceServers?.length ? params.iceServers : [{ urls: 'stun:stun.l.google.com:19302' }]

    const authUsers = mikoDigestAuthUserCandidates(params.authorizationUser, params.sipUri)
    if (authUsers.length === 0) {
      throw new Error('WebRTC: no digest username candidates (check sipUri / authorizationUser)')
    }

    diag.info('SIP', `connect() wsUrl=${params.wsUrl} sipUri=${params.sipUri}`, {
      authorizationUser: params.authorizationUser || null,
      digestUserCandidates: authUsers,
      iceServers: iceServers.map((s) => ({
        urls: s.urls,
        hasCredentials: !!(s.username && s.credential),
      })),
    })

    const pcConfig: RTCConfiguration = {
      iceServers: iceServers.map((s) => ({
        urls: s.urls,
        username: s.username,
        credential: s.credential,
      })),
      bundlePolicy: 'max-bundle',
      rtcpMuxPolicy: 'require',
    }

    for (let attemptIdx = 0; attemptIdx < authUsers.length; attemptIdx++) {
      const authUser = authUsers[attemptIdx]
      if (attemptIdx > 0) {
        diag.info('SIP', `REGISTER attempt ${attemptIdx + 1}/${authUsers.length} authorization_user=${authUser}`)
        this.setStatus('connecting')
      }

      const socket = new JsSIP.WebSocketInterface(params.wsUrl)
      this.wireWssHooks(socket as unknown as { send?: unknown; _ws?: WebSocket })

      const uaOptions: Record<string, unknown> = {
        sockets: [socket],
        uri: params.sipUri,
        password: params.password,
        session_timers: false,
        register: true,
        register_expires: 300,
        connection_recovery_min_interval: 2,
        connection_recovery_max_interval: 30,
        pcConfig,
      }
      const authTrim = authUser.trim()
      if (authTrim) uaOptions.authorization_user = authTrim

      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const ua = new JsSIP.UA(uaOptions as any) as unknown as AnyUa

      this.ua = ua
      this._intentionalDisconnect = false

      diag.updateSnapshot({
        uaConfig: {
          uri: params.sipUri,
          authorizationUser: authTrim || null,
          wsUrl: params.wsUrl,
          register_expires: 300,
          session_timers: false,
        },
        iceServers,
      })

      this.wireUaEvents(ua)

      try {
        // eslint-disable-next-line no-await-in-loop
        await new Promise<void>((resolve, reject) => {
          let settled = false
          const timer = setTimeout(() => {
            if (!settled) fail('WebRTC: timed out (check WSS URL, TLS cert, network)')
          }, 15000)

          const fail = (msg: string) => {
            if (settled) return
            settled = true
            clearTimeout(timer)
            this._cancelPendingConnect = null
            this._lastError = msg
            this.setStatus('error')
            diag.error('SIP', `connect() failed: ${msg}`)
            try {
              ua.stop()
            } catch {
              /* ignore */
            }
            this.ua = null
            reject(new Error(msg))
          }

          this._cancelPendingConnect = (reason: string) => fail(reason)

          ua.on('registered', () => {
            if (settled) {
              diag.info('SIP', 'registered (re-registration)')
              this.setStatus('registered')
              return
            }
            settled = true
            clearTimeout(timer)
            this._cancelPendingConnect = null
            this.setStatus('registered')
            diag.info('SIP', 'registered (initial)')
            resolve()
          })

          ua.on('registrationFailed', (e: unknown) => {
            const ex = e as { cause?: string; message?: string; response?: { status_code?: number } }
            const code = ex?.response?.status_code
            const msg = ex?.message || ex?.cause || 'SIP registration failed'
            const full = code ? `${msg} (${code})` : msg
            diag.error('SIP', `registrationFailed: ${full}`, ex)
            if (!settled) {
              const canRetry =
                (code === 401 || code === 407) && attemptIdx < authUsers.length - 1
              if (canRetry) {
                settled = true
                clearTimeout(timer)
                this._cancelPendingConnect = null
                try {
                  ua.stop()
                } catch {
                  /* ignore */
                }
                this.ua = null
                reject(createSipRegisterDigestRetry(full, code))
                return
              }
              fail(full)
            } else {
              this._lastError = full
              this.setStatus('error')
              this.onConnectionProblem?.(full)
            }
          })

          ua.on('disconnected', (e: unknown) => {
            const ex = e as { cause?: string; code?: number; reason?: string }
            const wmsg = ex?.cause || ex?.reason || 'SIP transport disconnected'
            diag.warn('WSS', `ws disconnected: ${wmsg}`, {
              code: ex?.code,
              reason: ex?.reason,
              cause: ex?.cause,
            })
            if (this._intentionalDisconnect) return
            if (!settled) {
              fail(`WebRTC transport: ${wmsg}`)
            } else {
              this.setStatus('error')
              this.onConnectionProblem?.(String(wmsg))
            }
          })

          diag.info('SIP', 'ua.start()')
          ua.start()
        })
        return
      } catch (err) {
        if (isSipRegisterDigestRetry(err) && attemptIdx < authUsers.length - 1) {
          continue
        }
        throw err
      }
    }
  }

  disconnect(): void {
    if (this.ua || this.session) {
      diag.info('SIP', 'disconnect()')
    }
    this._intentionalDisconnect = true
    const cancel = this._cancelPendingConnect
    this._cancelPendingConnect = null
    try {
      this.session?.terminate()
    } catch {
      /* ignore */
    }
    this.stopStatsTimer()
    this.stopLocalTracks()
    this.detachPeerConnection()
    this.session = null
    this.clearRemoteAudio()
    try {
      this.ua?.stop()
    } catch {
      /* ignore */
    }
    this.ua = null
    this._sipUri = ''
    if (this._remoteAudioEl?.parentNode) {
      this._remoteAudioEl.remove()
    }
    this._remoteAudioEl = null
    this._outputDeviceId = undefined
    this.setStatus('idle')
    if (cancel) cancel('Disconnected by user')
    queueMicrotask(() => {
      this._intentionalDisconnect = false
    })
  }

  private handleIncomingSession(
    sess: AnySession,
    req: { from?: { uri?: { user?: string }; display_name?: string } } | undefined,
  ): void {
    const fromUser =
      req?.from?.uri?.user ||
      sess.remote_identity?.uri?.user ||
      ''
    const displayName = req?.from?.display_name || sess.remote_identity?.display_name || undefined

    diag.info('SIP', `incoming INVITE from ${fromUser || 'unknown'}${displayName ? ` (${displayName})` : ''}`, {
      sessionId: sess.id,
    })

    let answered = false

    // If a previous session is still around, reject the new one — UI handles only one at a time.
    if (this.session) {
      diag.warn('SIP', 'Another session is active, rejecting new INVITE (486)')
      try {
        sess.terminate({ status_code: 486, reason_phrase: 'Busy Here' })
      } catch {
        /* ignore */
      }
      return
    }

    const accept = async (audio?: CallAudioOptions): Promise<void> => {
      if (answered) return
      answered = true
      this._outputDeviceId = audio?.outputDeviceId?.trim() || this._outputDeviceId
      if (audio?.processing) this.setAudioProcessing(audio.processing)

      const audioConstr = this.buildAudioConstraints(audio?.inputDeviceId, audio?.processing)

      this.session = sess
      this.wireSession(sess)
      this.setStatus('in_call')

      diag.info('SIP', 'accepting incoming INVITE', {
        inputDeviceId: audio?.inputDeviceId || null,
        outputDeviceId: this._outputDeviceId || null,
      })

      try {
        sess.answer?.({
          mediaConstraints: { audio: audioConstr, video: false },
          rtcOfferConstraints: { offerToReceiveAudio: true, offerToReceiveVideo: false },
          pcConfig: this.ua?.configuration?.pcConfig,
        })
      } catch (err) {
        const msg = (err as Error)?.message || 'Failed to answer call'
        diag.error('SIP', `answer() threw: ${msg}`, err)
        this.session = null
        this.clearRemoteAudio()
        if (this.ua?.isRegistered()) this.setStatus('registered')
        else this.setStatus('idle')
        this.onInviteFailed?.(msg)
        throw err
      }
    }

    const reject = (): void => {
      if (answered) return
      answered = true
      diag.info('SIP', 'rejecting incoming INVITE with 486')
      try {
        sess.terminate({ status_code: 486, reason_phrase: 'Busy Here' })
      } catch {
        /* ignore */
      }
    }

    // Wire pre-answer cancellation: if remote sends CANCEL/BYE before user picks up,
    // mark "answered" so the auto-reject is a no-op and notify the UI to dismiss the popup.
    const handlePreAnswerEnd = (ev?: unknown) => {
      const wasUnanswered = !answered
      answered = true
      if (wasUnanswered) {
        const cause = (ev as { cause?: string })?.cause
        diag.info('SIP', `incoming ended before answer${cause ? ` (${cause})` : ''}`)
      }
      if (wasUnanswered && !this.session) {
        this.onSessionEnd?.()
      }
    }
    sess.on('failed', handlePreAnswerEnd)
    sess.on('ended', handlePreAnswerEnd)

    if (!this.onIncomingCall) {
      diag.warn('SIP', 'no onIncomingCall handler registered — auto-rejecting')
      reject()
      return
    }

    this.onIncomingCall({
      from: fromUser || 'unknown',
      displayName,
      accept,
      reject,
    })
  }

  private wireUaEvents(ua: AnyUa): void {
    ua.on('connecting', () => diag.info('WSS', 'ua: connecting'))
    ua.on('connected', () => diag.info('WSS', 'ua: connected'))
    ua.on('unregistered', (e: unknown) => {
      const ex = e as { cause?: string }
      diag.info('SIP', `ua: unregistered${ex?.cause ? ` (${ex.cause})` : ''}`)
    })
    ua.on('newRTCSession', (data: unknown) => {
      const evt = data as {
        originator?: 'local' | 'remote'
        session?: AnySession
        request?: { from?: { uri?: { user?: string }; display_name?: string } }
      }
      if (!evt?.session) return
      diag.info('SIP', `newRTCSession origin=${evt.originator} id=${evt.session.id || '?'}`)
      if (evt.originator === 'remote') {
        this.handleIncomingSession(evt.session, evt.request)
      }
      // Outgoing sessions are wired by our own call() method.
    })
  }

  private wireWssHooks(socket: { send?: unknown; _ws?: WebSocket }): void {
    // Patch outgoing send()
    try {
      // JsSIP calls socket.send(data) with either a string or ArrayBuffer.
      const origSend = (socket as { send?: (data: unknown) => void }).send
      if (typeof origSend === 'function') {
        ;(socket as { send: (data: unknown) => void }).send = (data: unknown) => {
          try {
            const s = typeof data === 'string' ? data : ''
            if (s) this.dumpWssFrame('out', s)
          } catch {
            /* ignore */
          }
          return (origSend as (d: unknown) => void).call(socket, data)
        }
        diag.debug('WSS', 'hook installed on socket.send')
      }
    } catch (err) {
      diag.warn('WSS', `hook send failed: ${(err as Error)?.message || err}`)
    }

    // Patch _ws.onmessage when the raw WebSocket becomes available. JsSIP
    // creates it lazily, so retry a few times.
    const attach = () => {
      const ws = (socket as { _ws?: WebSocket })._ws
      if (!ws) return false
      try {
        ws.addEventListener('message', (e: MessageEvent) => {
          try {
            const data = e?.data
            const s = typeof data === 'string' ? data : ''
            if (s) this.dumpWssFrame('in', s)
          } catch {
            /* ignore */
          }
        })
        diag.debug('WSS', 'hook installed on _ws.message')
        return true
      } catch (err) {
        diag.warn('WSS', `hook _ws failed: ${(err as Error)?.message || err}`)
        return false
      }
    }
    if (!attach()) {
      setTimeout(() => attach() || setTimeout(() => attach(), 500), 100)
    }
  }

  private dumpWssFrame(dir: 'in' | 'out', frame: string): void {
    const safeFrame = redactSipFrameForDiag(frame)
    const firstLine = frame.split(/\r?\n/, 1)[0] || ''
    const upper = firstLine.toUpperCase()
    const method = WSS_METHODS_OF_INTEREST.find((m) => upper.startsWith(m + ' '))
    const isResponse = /^SIP\/2\.0\s+(\d+)/.test(firstLine)
    const isRegisterKeepalive = upper.startsWith('REGISTER ') || /\r\nCSeq:\s*\d+\s+REGISTER/i.test(frame)

    // Treat REGISTERs as trace so keep-alive doesn't flood the WSS category,
    // but keep non-2xx responses as info.
    let level: 'trace' | 'info' | 'warn' = 'info'
    if (isRegisterKeepalive) level = 'trace'
    if (isResponse) {
      const code = Number(RegExp.$1 || '0')
      if (code >= 400) level = 'warn'
    }

    const label = isResponse
      ? firstLine
      : method
        ? `${method} ${firstLine.split(/\s+/).slice(1).join(' ')}`
        : firstLine

    // Capture SDPs into the snapshot (outgoing offer and answer, incoming offer/answer).
    const sdpMatch = safeFrame.match(/\r\n\r\n([\s\S]+)$/)
    if (sdpMatch && sdpMatch[1] && sdpMatch[1].startsWith('v=0')) {
      const redacted = redactSdpForDiag(sdpMatch[1])
      if (dir === 'out') diag.updateSnapshot({ lastLocalSdp: redacted })
      else diag.updateSnapshot({ lastRemoteSdp: redacted })
    }

    const truncated =
      safeFrame.length > WSS_FRAME_MAX ? safeFrame.slice(0, WSS_FRAME_MAX) + '…' : safeFrame
    diag.push(level, 'WSS', `${dir.toUpperCase()} ${label}`, { frame: truncated, bytes: safeFrame.length })
  }

  private attachPeerConnection(pc: RTCPeerConnection): void {
    this.detachPeerConnection()
    this._activePc = pc
    this.onPeerConnection?.(pc)

    this.applyCodecPreferences(pc)
    this.enforceSendrecv(pc)

    pc.addEventListener('track', (ev: RTCTrackEvent) => {
      diag.info('PC', `ontrack kind=${ev.track?.kind} streams=${ev.streams.length}`)
      const el = this.getOrCreateRemoteAudio()
      if (ev.streams[0]) {
        el.srcObject = ev.streams[0]
        void el.play().catch((err: Error) => {
          diag.warn('AUDIO', `remote audio play() blocked: ${err?.message || err}`)
        })
        this.applyOutputDevice()
      }
    })
    pc.addEventListener('connectionstatechange', () => {
      diag.info('PC', `connectionState=${pc.connectionState}`)
    })
    pc.addEventListener('iceconnectionstatechange', () => {
      diag.info('ICE', `iceConnectionState=${pc.iceConnectionState}`)
    })
    pc.addEventListener('icegatheringstatechange', () => {
      diag.debug('ICE', `iceGatheringState=${pc.iceGatheringState}`)
    })
    pc.addEventListener('signalingstatechange', () => {
      diag.debug('PC', `signalingState=${pc.signalingState}`)
    })
    pc.addEventListener('negotiationneeded', () => {
      diag.info('PC', 'negotiationneeded')
    })
    pc.addEventListener('icecandidate', (ev: RTCPeerConnectionIceEvent) => {
      if (!ev.candidate) {
        diag.debug('ICE', 'local candidates gathering complete')
        return
      }
      const c = ev.candidate
      diag.trace('ICE', `local candidate: ${c.candidate}`, {
        type: c.type,
        protocol: c.protocol,
        address: c.address,
        port: c.port,
        foundation: c.foundation,
      })
    })
    pc.addEventListener('icecandidateerror', (ev: Event) => {
      const e = ev as Event & {
        errorCode?: number
        errorText?: string
        url?: string
        address?: string
        port?: number
      }
      diag.warn('ICE', `candidate error ${e.errorCode || ''}: ${e.errorText || ''} (${e.url || ''})`)
    })

    // Track senders: capture local stream so the hot-swap can stop old tracks.
    const streams = new Set<MediaStream>()
    for (const s of pc.getSenders()) {
      if (s.track && 'stream' in (s as unknown as Record<string, unknown>)) {
        // no direct accessor; skip
      }
    }
    if (streams.size > 0) {
      // unused branch (kept for symmetry with ontrack)
    }

    this.startStatsTimer()
  }

  private detachPeerConnection(): void {
    this.stopStatsTimer()
    if (this._activePc) {
      diag.debug('PC', 'detachPeerConnection')
      this._activePc = null
      this.onPeerConnection?.(null)
    }
  }

  private stopLocalTracks(): void {
    const s = this._localStream
    this._localStream = null
    if (s) {
      let n = 0
      s.getTracks().forEach((t) => {
        try {
          t.stop()
          n++
        } catch {
          /* ignore */
        }
      })
      diag.info('AUDIO', `stopped ${n} local track(s)`)
      this.onLocalStream?.(null)
    }
  }

  private startStatsTimer(): void {
    this.stopStatsTimer()
    if (typeof window === 'undefined') return
    this._statsTimer = window.setInterval(() => {
      void this.captureStatsOnce()
    }, STATS_INTERVAL_MS)
  }

  private stopStatsTimer(): void {
    if (this._statsTimer != null) {
      clearInterval(this._statsTimer)
      this._statsTimer = null
    }
  }

  private async captureStatsOnce(): Promise<void> {
    const pc = this._activePc
    if (!pc) return
    try {
      const report = await pc.getStats()
      const sum = summarizeStats(report)
      diag.updateSnapshot({ lastStats: sum })
      diag.push('trace', 'STATS', 'getStats snapshot', sum)
    } catch (err) {
      diag.warn('STATS', `getStats failed: ${(err as Error)?.message || err}`)
    }
  }

  private wireSession(sess: AnySession): void {
    // Capture the PC as soon as JsSIP creates it, and again on any re-creation.
    const maybeAttachPc = () => {
      const pc = sess.connection
      if (pc && pc !== this._activePc) {
        diag.info('PC', 'attaching peer connection')
        this.attachPeerConnection(pc)
      }
    }

    sess.on('peerconnection', (data: unknown) => {
      const d = data as { peerconnection?: RTCPeerConnection }
      if (d?.peerconnection) {
        diag.info('PC', 'session event: peerconnection')
        this.attachPeerConnection(d.peerconnection)
      }
      this.captureLocalStreamFromPc()
    })
    sess.on('sending', (data: unknown) => {
      const d = data as { request?: { method?: string } }
      diag.debug('SIP', `session: sending ${d?.request?.method || '?'}`)
    })
    sess.on('progress', (ev: unknown) => {
      const d = ev as { response?: { status_code?: number; reason_phrase?: string } }
      const code = d?.response?.status_code
      const reason = d?.response?.reason_phrase
      diag.info('SIP', `session: progress${code ? ` ${code}` : ''}${reason ? ` ${reason}` : ''}`)
      this.onCallRinging?.()
    })
    sess.on('accepted', () => {
      diag.info('SIP', 'session: accepted (2xx)')
      maybeAttachPc()
      this.captureLocalStreamFromPc()
    })
    sess.on('confirmed', () => {
      diag.info('SIP', 'session: confirmed (ACK)')
      maybeAttachPc()
      this.captureLocalStreamFromPc()
      this.onCallConfirmed?.()
      this.applyOutputDevice()
    })
    sess.on('hold', () => diag.info('SIP', 'session: hold'))
    sess.on('unhold', () => diag.info('SIP', 'session: unhold'))
    sess.on('muted', (ev: unknown) => diag.info('AUDIO', 'session: muted', ev))
    sess.on('unmuted', (ev: unknown) => diag.info('AUDIO', 'session: unmuted', ev))
    sess.on('reinvite', (ev: unknown) => {
      diag.info('SIP', 'session: reinvite received', ev)
      // Re-attach in case JsSIP replaced the PC; re-apply sinkId.
      setTimeout(() => {
        maybeAttachPc()
        this.applyOutputDevice()
      }, 0)
    })
    sess.on('update', (ev: unknown) => {
      diag.info('SIP', 'session: update', ev)
      setTimeout(() => {
        maybeAttachPc()
        this.applyOutputDevice()
      }, 0)
    })
    sess.on('newDTMF', (ev: unknown) => {
      const d = ev as { originator?: string; dtmf?: { tone?: string } }
      diag.info('SIP', `session: DTMF ${d?.originator || '?'} ${d?.dtmf?.tone || '?'}`)
    })
    sess.on('ended', (ev: unknown) => {
      const d = ev as { cause?: string; originator?: string }
      diag.info('SIP', `session: ended cause=${d?.cause || '?'} by=${d?.originator || '?'}`)
      this.handleSessionTermination(sess)
      this.onSessionEnd?.()
    })
    sess.on('failed', (ev: unknown) => {
      const data = ev as { cause?: string; message?: string; originator?: string }
      const msg = data?.cause || data?.message || 'Call failed'
      diag.error('SIP', `session: failed cause=${msg} by=${data?.originator || '?'}`, ev)
      this.handleSessionTermination(sess)
      this.onInviteFailed?.(String(msg))
      this.onSessionEnd?.()
    })
  }

  private captureLocalStreamFromPc(): void {
    const pc = this._activePc
    if (!pc) return
    const audioSender = pc.getSenders().find((s) => s.track?.kind === 'audio')
    const track = audioSender?.track
    if (!track) return
    // Reconstruct a MediaStream if we don't have one yet (JsSIP owns the real one internally).
    if (!this._localStream || !this._localStream.getAudioTracks().includes(track)) {
      const stream = new MediaStream([track])
      this._localStream = stream
      this.onLocalStream?.(stream)
      diag.debug('AUDIO', 'captured local audio track from PC', {
        deviceId: track.getSettings().deviceId,
        label: track.label,
      })
    }
  }

  private handleSessionTermination(sess: AnySession): void {
    if (this.session === sess) this.session = null
    this.stopLocalTracks()
    this.detachPeerConnection()
    this.clearRemoteAudio()
    if (this.ua?.isRegistered()) this.setStatus('registered')
    else this.setStatus('idle')
  }

  async call(destination: string, audio?: CallAudioOptions): Promise<void> {
    if (!this.ua || !this.ua.isRegistered()) {
      throw new Error('WebRTC: not registered')
    }
    const clean = destination.trim()
    if (clean.length < 1) throw new Error('Enter a number')

    this._outputDeviceId = audio?.outputDeviceId?.trim() || this._outputDeviceId
    if (audio?.processing) this.setAudioProcessing(audio.processing)

    // End any lingering session first.
    try {
      this.session?.terminate()
    } catch {
      /* ignore */
    }
    this.session = null

    const domain = sipHostFromUri(this._sipUri)
    const target = clean.includes('@')
      ? clean.startsWith('sip:')
        ? clean
        : `sip:${clean}`
      : `sip:${clean}@${domain}`

    const audioConstr = this.buildAudioConstraints(audio?.inputDeviceId, audio?.processing)
    const mediaConstraints = {
      audio: audioConstr,
      video: false as const,
    }

    const options: Record<string, unknown> = {
      mediaConstraints,
      rtcOfferConstraints: { offerToReceiveAudio: true, offerToReceiveVideo: false },
      pcConfig: this.ua.configuration?.pcConfig,
    }

    diag.info('SIP', `call() → ${target}`, {
      inputDeviceId: audio?.inputDeviceId || null,
      outputDeviceId: this._outputDeviceId || null,
      constraints: audioConstr,
    })

    const sess = this.ua.call(target, options) as AnySession | null
    if (!sess) throw new Error('WebRTC: failed to start call')
    this.session = sess
    this.wireSession(sess)
    this.setStatus('in_call')
  }

  async hangup(): Promise<void> {
    const sess = this.session
    if (!sess) {
      diag.debug('SIP', 'hangup(): no active session')
      this.clearRemoteAudio()
      if (this.ua?.isRegistered()) this.setStatus('registered')
      else this.setStatus('idle')
      return
    }
    // Avoid sending BYE while a re-INVITE is in progress — that's a common
    // source of 488 Bad Media Description. Wait up to 2s for stable signaling.
    const pc = this._activePc
    if (pc && pc.signalingState !== 'stable') {
      diag.warn('SIP', `hangup() deferred: signalingState=${pc.signalingState}`)
      try {
        await this.waitSignalingStable(pc, 2000)
      } catch {
        /* fall through and terminate anyway */
      }
    }
    diag.info('SIP', 'hangup(): terminating session')
    try {
      sess.terminate()
    } catch (err) {
      diag.warn('SIP', `terminate() threw: ${(err as Error)?.message || err}`)
    }
    // handleSessionTermination will run from the `ended` event, but fall back
    // in case the event never arrives.
    setTimeout(() => {
      if (this.session === sess) {
        this.handleSessionTermination(sess)
      }
    }, 500)
  }

  /** Mute/unmute the local microphone for the current call (no-op outside a call). */
  setMicMuted(muted: boolean): void {
    const sess = this.session
    if (!sess) return
    try {
      if (muted) sess.mute?.({ audio: true })
      else sess.unmute?.({ audio: true })
      diag.info('AUDIO', `mic ${muted ? 'muted' : 'unmuted'}`)
    } catch (err) {
      diag.warn('AUDIO', `setMicMuted failed: ${(err as Error)?.message || err}`)
    }
  }

  /** Put the current call on hold or take it back. */
  setHold(hold: boolean): void {
    const sess = this.session
    if (!sess) return
    try {
      if (hold) sess.hold?.()
      else sess.unhold?.()
      diag.info('SIP', `hold=${hold}`)
    } catch (err) {
      diag.warn('SIP', `setHold failed: ${(err as Error)?.message || err}`)
    }
  }

  /** Send a single DTMF tone (RFC 2833 by default in JsSIP) on the current call. */
  sendDtmf(tone: string): void {
    const sess = this.session
    if (!sess) return
    const t = (tone || '').trim()
    if (!t) return
    try {
      sess.sendDTMF?.(t)
      diag.info('SIP', `DTMF sent: ${t}`)
    } catch (err) {
      diag.warn('SIP', `sendDtmf failed: ${(err as Error)?.message || err}`)
    }
  }

  hasActiveSession(): boolean {
    return this.session !== null
  }
}

/** Summary of RTCStatsReport that we keep in the diag ring buffer. */
function summarizeStats(report: RTCStatsReport): {
  inboundAudio?: {
    packetsReceived?: number
    packetsLost?: number
    jitter?: number
    bytesReceived?: number
    audioLevel?: number
    codec?: string
  }
  outboundAudio?: {
    packetsSent?: number
    bytesSent?: number
    targetBitrate?: number
    codec?: string
  }
  candidatePair?: {
    rtt?: number
    state?: string
    availableOutgoingBitrate?: number
    nominated?: boolean
  }
  selectedLocalCandidate?: { type?: string; protocol?: string; address?: string; port?: number }
  selectedRemoteCandidate?: { type?: string; protocol?: string; address?: string; port?: number }
} {
  const out: ReturnType<typeof summarizeStats> = {}
  const byId = new Map<string, RTCStats>()
  report.forEach((s) => byId.set(s.id, s))
  const codecs: Record<string, string> = {}
  report.forEach((s) => {
    if (s.type === 'codec') {
      const c = s as RTCStats & { mimeType?: string }
      if (c.mimeType) codecs[s.id] = c.mimeType
    }
  })
  report.forEach((s) => {
    if (s.type === 'inbound-rtp') {
      const r = s as RTCStats & {
        kind?: string
        packetsReceived?: number
        packetsLost?: number
        jitter?: number
        bytesReceived?: number
        audioLevel?: number
        codecId?: string
      }
      if (r.kind === 'audio') {
        out.inboundAudio = {
          packetsReceived: r.packetsReceived,
          packetsLost: r.packetsLost,
          jitter: r.jitter,
          bytesReceived: r.bytesReceived,
          audioLevel: r.audioLevel,
          codec: r.codecId ? codecs[r.codecId] : undefined,
        }
      }
    } else if (s.type === 'outbound-rtp') {
      const r = s as RTCStats & {
        kind?: string
        packetsSent?: number
        bytesSent?: number
        targetBitrate?: number
        codecId?: string
      }
      if (r.kind === 'audio') {
        out.outboundAudio = {
          packetsSent: r.packetsSent,
          bytesSent: r.bytesSent,
          targetBitrate: r.targetBitrate,
          codec: r.codecId ? codecs[r.codecId] : undefined,
        }
      }
    } else if (s.type === 'candidate-pair') {
      const p = s as RTCStats & {
        state?: string
        nominated?: boolean
        selected?: boolean
        currentRoundTripTime?: number
        availableOutgoingBitrate?: number
        localCandidateId?: string
        remoteCandidateId?: string
      }
      if (p.nominated && (p.state === 'succeeded' || p.selected)) {
        out.candidatePair = {
          rtt: p.currentRoundTripTime,
          state: p.state,
          availableOutgoingBitrate: p.availableOutgoingBitrate,
          nominated: p.nominated,
        }
        if (p.localCandidateId) {
          const loc = byId.get(p.localCandidateId) as RTCStats & {
            candidateType?: string
            protocol?: string
            address?: string
            port?: number
          }
          if (loc) {
            out.selectedLocalCandidate = {
              type: loc.candidateType,
              protocol: loc.protocol,
              address: loc.address,
              port: loc.port,
            }
          }
        }
        if (p.remoteCandidateId) {
          const rem = byId.get(p.remoteCandidateId) as RTCStats & {
            candidateType?: string
            protocol?: string
            address?: string
            port?: number
          }
          if (rem) {
            out.selectedRemoteCandidate = {
              type: rem.candidateType,
              protocol: rem.protocol,
              address: rem.address,
              port: rem.port,
            }
          }
        }
      }
    }
  })
  return out
}
