import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
  type PointerEvent,
} from 'react'
import { toast } from 'sonner'
import { useNavigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Separator } from '@/components/ui/separator'
import * as api from '@/app/api/client'
import type { ApiError } from '@/app/api/http'
import type { CallerIdItem, CdrRecord, WebRtcConfig } from '@/app/api/types'
import { useAuth } from '@/app/auth/useAuth'
import { getThemeMode, setThemeMode, type ThemeMode } from '@/app/theme/theme'
import {
  Phone,
  PhoneOff,
  PhoneIncoming,
  PhoneOutgoing,
  PhoneMissed,
  Headphones,
  Mic,
  MicOff,
  Pause,
  Play,
  Volume2,
  Grid3x3,
} from 'lucide-react'
import { SipWebRtcClient, type IncomingCall, type AudioProcessingOptions } from '@/webrtc/sipWebRtc'
import {
  ensureAudioContextRunning,
  hotSwapMicrophone,
  playTestTone,
  probeMicrophone,
  startRingtone,
  type MicProbeHandle,
  type RingtoneHandle,
} from '@/webrtc/audioEngine'
import { DebugPanel } from './DebugPanel'
import { diag } from '@/webrtc/diag'

type WebRtcStatusKind = 'offline' | 'connecting' | 'registered' | 'error'

type AudioProcessingPrefs = Required<AudioProcessingOptions>

/**
 * PJSIP digest `username` for REGISTER. Prefer `sipAuthUser` from the proxy; otherwise the base
 * extension (strip trailing `-WS`). `SipWebRtcClient` adds `${base}-WS` as a fallback candidate.
 */
function webrtcDigestUsername(sipAuthUser: string | undefined, extension: string): string {
  const e = extension.trim()
  const base = e.toUpperCase().endsWith('-WS') ? e.slice(0, -3) : e
  const s = sipAuthUser?.trim()
  if (s) return s
  return base
}

/**
 * User-part for `sip:USER@host` (JsSIP `uri`). Must match the identity Asterisk
 * ties to the auth object when possible. If the proxy only sends `sipAuthUser`
 * (e.g. base ext) and omits `sipContactUserPart`, use that — do not default to
 * `ext-WS` or REGISTER From/To stay `…-WS@…` while digest uses the base ext → 401.
 */
function webrtcSipUserPart(cfg: WebRtcConfig): string {
  const hint = cfg.sipContactUserPart?.trim()
  if (hint) return hint
  const au = cfg.sipAuthUser?.trim()
  if (au) return au
  const ext = cfg.extension.trim()
  return ext.toUpperCase().endsWith('-WS') ? ext : `${ext}-WS`
}

function WebRtcStatusPopover(props: {
  status: WebRtcStatusKind
  hasServerPassword: boolean
  passwordInput: string
  onPasswordInputChange: (v: string) => void
  canConnect: boolean
  onConnect: () => void
  onDisconnect: () => void
  audioInputs: MediaDeviceInfo[]
  audioOutputs: MediaDeviceInfo[]
  micId: string
  speakerId: string
  ringtoneId: string
  processing: AudioProcessingPrefs
  onMicChange: (id: string) => void
  onSpeakerChange: (id: string) => void
  onRingtoneChange: (id: string) => void
  onProcessingChange: (p: Partial<AudioProcessingPrefs>) => void
  onRefreshDevices: () => void
  onOpenDebugPanel: () => void
  inCall: boolean
  loading: boolean
  extension?: string
}) {
  const {
    status, hasServerPassword, passwordInput, onPasswordInputChange,
    canConnect, onConnect, onDisconnect,
    audioInputs, audioOutputs, micId, speakerId, ringtoneId, processing,
    onMicChange, onSpeakerChange, onRingtoneChange, onProcessingChange,
    onRefreshDevices, onOpenDebugPanel,
    inCall, loading, extension,
  } = props

  const [open, setOpen] = useState(false)
  const wrapperRef = useRef<HTMLDivElement | null>(null)
  const [vuLevel, setVuLevel] = useState(0)
  const micProbeRef = useRef<MicProbeHandle | null>(null)
  const [micTesting, setMicTesting] = useState(false)
  const [micLoopbackStop, setMicLoopbackStop] = useState<(() => void) | null>(null)
  const [toneBusy, setToneBusy] = useState(false)
  const ringtoneRef = useRef<RingtoneHandle | null>(null)
  const [ringtoneTesting, setRingtoneTesting] = useState(false)

  const stopMicProbe = useCallback(() => {
    micProbeRef.current?.stop()
    micProbeRef.current = null
    setVuLevel(0)
    setMicTesting(false)
    if (micLoopbackStop) {
      try {
        micLoopbackStop()
      } catch {
        /* ignore */
      }
      setMicLoopbackStop(null)
    }
  }, [micLoopbackStop])

  const startMicProbe = useCallback(async () => {
    stopMicProbe()
    try {
      await ensureAudioContextRunning()
      const probe = await probeMicrophone(micId || undefined, processing)
      micProbeRef.current = probe
      probe.subscribe(setVuLevel)
      setMicTesting(true)
    } catch (err) {
      toast.error((err as Error)?.message || 'Failed to open microphone')
      stopMicProbe()
    }
  }, [micId, processing, stopMicProbe])

  useEffect(() => {
    // Auto-start VU while the panel is open & not in a call; restart on device change.
    if (!open || inCall) {
      stopMicProbe()
      return
    }
    void startMicProbe()
    return () => stopMicProbe()
  }, [open, inCall, micId, processing.echoCancellation, processing.noiseSuppression, processing.autoGainControl, startMicProbe, stopMicProbe])

  useEffect(() => {
    return () => {
      ringtoneRef.current?.stop()
      ringtoneRef.current = null
    }
  }, [])

  const onTestMicLoopback = useCallback(() => {
    if (!micProbeRef.current) return
    if (micLoopbackStop) {
      micLoopbackStop()
      setMicLoopbackStop(null)
      return
    }
    const stop = micProbeRef.current.playLoopback(speakerId || undefined)
    setMicLoopbackStop(() => stop)
    setTimeout(() => {
      setMicLoopbackStop((prev) => {
        if (!prev) return null
        try {
          prev()
        } catch {
          /* ignore */
        }
        return null
      })
    }, 3000)
  }, [micLoopbackStop, speakerId])

  const onTestSpeaker = useCallback(async () => {
    if (toneBusy) return
    setToneBusy(true)
    try {
      await playTestTone({ outputDeviceId: speakerId || undefined })
    } catch (err) {
      toast.error((err as Error)?.message || 'Playback failed')
    } finally {
      setToneBusy(false)
    }
  }, [speakerId, toneBusy])

  const onTestRingtone = useCallback(async () => {
    if (ringtoneRef.current) {
      ringtoneRef.current.stop()
      ringtoneRef.current = null
      setRingtoneTesting(false)
      return
    }
    try {
      await ensureAudioContextRunning()
      ringtoneRef.current = startRingtone({
        outputDeviceId: (ringtoneId || speakerId) || undefined,
      })
      setRingtoneTesting(true)
      window.setTimeout(() => {
        ringtoneRef.current?.stop()
        ringtoneRef.current = null
        setRingtoneTesting(false)
      }, 4000)
    } catch (err) {
      toast.error((err as Error)?.message || 'Ringtone failed')
    }
  }, [ringtoneId, speakerId])

  useEffect(() => {
    if (!open) return
    function onDoc(e: MouseEvent) {
      if (!wrapperRef.current) return
      if (!wrapperRef.current.contains(e.target as Node)) setOpen(false)
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', onDoc)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDoc)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  const dotClass =
    status === 'registered'
      ? 'bg-emerald-500'
      : status === 'connecting'
        ? 'bg-amber-500'
        : status === 'error'
          ? 'bg-red-500'
          : 'bg-muted-foreground/40'

  const label =
    status === 'registered'
      ? 'Registered'
      : status === 'connecting'
        ? 'Connecting…'
        : status === 'error'
          ? 'Error'
          : 'Offline'

  return (
    <div ref={wrapperRef} className="relative">
      <Button
        type="button"
        variant="outline"
        size="sm"
        onClick={() => setOpen((v) => !v)}
        className="h-9 gap-2 px-3"
        aria-haspopup="dialog"
        aria-expanded={open}
        title="Browser calling (WebRTC)"
      >
        <Headphones className="h-4 w-4 shrink-0 opacity-70" aria-hidden />
        <span className="relative flex h-2 w-2">
          {status === 'connecting' ? (
            <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-amber-400 opacity-75" />
          ) : null}
          <span className={`relative inline-flex h-2 w-2 rounded-full ${dotClass}`} />
        </span>
        <span className="hidden text-xs sm:inline">{label}</span>
      </Button>

      {open ? (
        <div
          role="dialog"
          className="absolute right-0 z-50 mt-2 max-h-[80vh] w-[330px] overflow-y-auto rounded-lg border bg-popover p-3 text-popover-foreground shadow-lg"
        >
          <div className="mb-2 flex items-center justify-between">
            <div className="text-sm font-semibold">Browser call (WebRTC)</div>
            <span className="text-xs text-muted-foreground">{label}</span>
          </div>
          {extension ? (
            <div className="mb-2 text-xs text-muted-foreground">Extension {extension}</div>
          ) : null}

          {!hasServerPassword ? (
            <div className="mb-3">
              <Label htmlFor="popover-sip-pwd" className="text-xs text-foreground/80">
                SIP password
              </Label>
              <Input
                id="popover-sip-pwd"
                type="password"
                autoComplete="off"
                value={passwordInput}
                onChange={(e) => onPasswordInputChange(e.target.value)}
                placeholder="••••••••"
                disabled={loading || status === 'registered' || status === 'connecting'}
                className="mt-1 h-9"
              />
              <p className="mt-1 text-[11px] text-muted-foreground">
                Server could not read it from MikoPBX.
              </p>
            </div>
          ) : null}

          <div className="mb-3 flex flex-wrap gap-2">
            <Button
              type="button"
              size="sm"
              onClick={onConnect}
              disabled={!canConnect || status === 'registered' || status === 'connecting'}
            >
              {status === 'connecting' ? 'Connecting…' : 'Connect'}
            </Button>
            <Button
              type="button"
              size="sm"
              variant="secondary"
              onClick={onDisconnect}
              disabled={status !== 'registered' && status !== 'connecting'}
            >
              Disconnect
            </Button>
          </div>

          <div className="space-y-2 border-t border-border pt-2">
            <div className="text-xs font-medium text-foreground/90">Audio devices</div>
            <Label htmlFor="popover-mic" className="text-xs text-muted-foreground">
              Microphone {inCall ? <span className="text-[10px] opacity-60">(hot-swap)</span> : null}
            </Label>
            <Select
              value={micId || 'default'}
              onValueChange={(v) => onMicChange(v == null || v === 'default' ? '' : v)}
              disabled={loading}
            >
              <SelectTrigger id="popover-mic" className="h-9 text-sm">
                <SelectValue placeholder="Default" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="default">System default</SelectItem>
                {audioInputs
                  .filter((d) => d.deviceId)
                  .map((d) => (
                    <SelectItem key={d.deviceId} value={d.deviceId}>
                      {d.label || `Microphone ${d.deviceId.slice(0, 8)}…`}
                    </SelectItem>
                  ))}
              </SelectContent>
            </Select>

            {/* VU meter */}
            <div className="flex items-center gap-2">
              <div
                className="h-2 flex-1 overflow-hidden rounded-full bg-muted"
                aria-label="Microphone level"
              >
                <div
                  className="h-full bg-emerald-500 transition-[width] duration-75"
                  style={{ width: `${Math.min(100, Math.round(vuLevel * 200))}%` }}
                />
              </div>
              <span className="min-w-[2ch] text-[10px] tabular-nums text-muted-foreground">
                {Math.round(vuLevel * 100)}
              </span>
            </div>
            <div className="flex gap-2">
              <Button
                type="button"
                variant={micLoopbackStop ? 'secondary' : 'outline'}
                size="sm"
                className="h-7 flex-1 text-[11px]"
                onClick={onTestMicLoopback}
                disabled={!micTesting || inCall}
              >
                {micLoopbackStop ? 'Stop playback' : 'Test microphone'}
              </Button>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="h-7 flex-1 text-[11px]"
                onClick={() => void onTestSpeaker()}
                disabled={toneBusy}
              >
                {toneBusy ? 'Playing…' : 'Test speaker'}
              </Button>
            </div>

            <Label htmlFor="popover-out" className="text-xs text-muted-foreground">
              Speaker / headset
            </Label>
            <Select
              value={speakerId || 'default'}
              onValueChange={(v) => onSpeakerChange(v == null || v === 'default' ? '' : v)}
              disabled={loading}
            >
              <SelectTrigger id="popover-out" className="h-9 text-sm">
                <SelectValue placeholder="Default" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="default">System default</SelectItem>
                {audioOutputs
                  .filter((d) => d.deviceId)
                  .map((d) => (
                    <SelectItem key={d.deviceId} value={d.deviceId}>
                      {d.label || `Output ${d.deviceId.slice(0, 8)}…`}
                    </SelectItem>
                  ))}
              </SelectContent>
            </Select>

            <Label htmlFor="popover-ring" className="text-xs text-muted-foreground">
              Ringtone device
            </Label>
            <Select
              value={ringtoneId || 'default'}
              onValueChange={(v) => onRingtoneChange(v == null || v === 'default' ? '' : v)}
              disabled={loading}
            >
              <SelectTrigger id="popover-ring" className="h-9 text-sm">
                <SelectValue placeholder="Same as speaker" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="default">Same as speaker</SelectItem>
                {audioOutputs
                  .filter((d) => d.deviceId)
                  .map((d) => (
                    <SelectItem key={d.deviceId} value={d.deviceId}>
                      {d.label || `Output ${d.deviceId.slice(0, 8)}…`}
                    </SelectItem>
                  ))}
              </SelectContent>
            </Select>
            <Button
              type="button"
              variant={ringtoneTesting ? 'secondary' : 'outline'}
              size="sm"
              className="h-7 w-full text-[11px]"
              onClick={() => void onTestRingtone()}
            >
              {ringtoneTesting ? 'Stop ringtone' : 'Test ringtone'}
            </Button>
          </div>

          <div className="mt-3 space-y-1 border-t border-border pt-2">
            <div className="text-xs font-medium text-foreground/90">Audio processing</div>
            <label className="flex items-center gap-2 text-[11px] text-foreground/80">
              <input
                type="checkbox"
                checked={processing.echoCancellation}
                onChange={(e) => onProcessingChange({ echoCancellation: e.target.checked })}
              />
              Echo cancellation (AEC)
            </label>
            <label className="flex items-center gap-2 text-[11px] text-foreground/80">
              <input
                type="checkbox"
                checked={processing.noiseSuppression}
                onChange={(e) => onProcessingChange({ noiseSuppression: e.target.checked })}
              />
              Noise suppression (NS)
            </label>
            <label className="flex items-center gap-2 text-[11px] text-foreground/80">
              <input
                type="checkbox"
                checked={processing.autoGainControl}
                onChange={(e) => onProcessingChange({ autoGainControl: e.target.checked })}
              />
              Automatic gain control (AGC)
            </label>
            <p className="text-[10px] text-muted-foreground">
              Applied to the next call. During an active call, change affects re-taken microphone on device switch.
            </p>
          </div>

          <div className="mt-3 flex gap-2 border-t border-border pt-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="h-8 flex-1 text-[11px]"
              onClick={onRefreshDevices}
            >
              Refresh devices
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="h-8 flex-1 text-[11px]"
              onClick={() => {
                setOpen(false)
                onOpenDebugPanel()
              }}
            >
              Debug panel
            </Button>
          </div>

          <p className="mt-3 text-[11px] text-muted-foreground">
            TLS WSS to the PBX must be reachable from this browser.
          </p>
        </div>
      ) : null}
    </div>
  )
}

type CallDirection = 'outgoing' | 'incoming'
type CallOutcome = 'answered' | 'missed' | 'busy' | 'failed' | 'unknown'

function parseCdrDate(s: string): Date | null {
  if (!s) return null
  const d = new Date(s.replace(' ', 'T'))
  return Number.isNaN(+d) ? null : d
}

function classifyCdr(r: CdrRecord, myExt: string): {
  direction: CallDirection
  counterpart: string
  outcome: CallOutcome
} {
  const me = (myExt || '').trim()
  const direction: CallDirection =
    me && r.src_num && r.src_num === me ? 'outgoing' : 'incoming'
  const counterpart = direction === 'outgoing' ? r.dst_num : r.src_num
  const disp = (r.disposition || '').toUpperCase()
  let outcome: CallOutcome = 'unknown'
  if (disp === 'ANSWERED' || (r.billsec || 0) > 0) outcome = 'answered'
  else if (disp === 'NO ANSWER' || disp === 'NOANSWER') outcome = 'missed'
  else if (disp === 'BUSY') outcome = 'busy'
  else if (disp === 'FAILED' || disp === 'CONGESTION') outcome = 'failed'
  return { direction, counterpart: counterpart || '—', outcome }
}

function formatRelativeDay(d: Date, now: Date): string {
  const startOf = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate())
  const diffDays = Math.round(
    (startOf(now).getTime() - startOf(d).getTime()) / 86_400_000,
  )
  if (diffDays === 0) return 'Today'
  if (diffDays === 1) return 'Yesterday'
  if (diffDays < 7) {
    return d.toLocaleDateString(undefined, { weekday: 'short' })
  }
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
}

function formatTimeOfDay(d: Date): string {
  return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
}

function formatBillsec(sec: number): string {
  if (!sec || sec <= 0) return ''
  const m = Math.floor(sec / 60)
  const s = sec % 60
  if (m === 0) return `${s}s`
  if (s === 0) return `${m}m`
  return `${m}m ${s.toString().padStart(2, '0')}s`
}

function formatDuration(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000))
  const m = Math.floor(total / 60)
  const s = total % 60
  return `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`
}

const LS_AUDIO_MIC = 'callspire_audio_input'
const LS_AUDIO_OUT = 'callspire_audio_output'
const LS_AUDIO_RING = 'callspire_audio_ringtone'
const LS_AUDIO_AEC = 'callspire_audio_aec'
const LS_AUDIO_NS = 'callspire_audio_ns'
const LS_AUDIO_AGC = 'callspire_audio_agc'

function loadBoolPref(key: string, fallback: boolean): boolean {
  try {
    const v = localStorage.getItem(key)
    if (v == null) return fallback
    return v === '1' || v === 'true'
  } catch {
    return fallback
  }
}

const DIGIT_LETTERS: Record<string, string> = {
  '1': '',
  '2': 'ABC',
  '3': 'DEF',
  '4': 'GHI',
  '5': 'JKL',
  '6': 'MNO',
  '7': 'PQRS',
  '8': 'TUV',
  '9': 'WXYZ',
  '*': '',
  '0': '+',
  '#': '',
}

export function SoftphonePage() {
  const nav = useNavigate()
  const { state, logout, refresh } = useAuth()
  const me = state.status === 'authenticated' ? state.me : null
  const sipRef = useRef(new SipWebRtcClient())

  const [callerIds, setCallerIds] = useState<CallerIdItem[]>([])
  const [callerId, setCallerId] = useState<string>('')
  const [destination, setDestination] = useState('')
  const [cdr, setCdr] = useState<CdrRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [calling, setCalling] = useState(false)
  const [theme, setTheme] = useState<ThemeMode>(() => getThemeMode())

  const [webRtcConfig, setWebRtcConfig] = useState<WebRtcConfig | null>(null)
  const [sipPassword, setSipPassword] = useState('')
  /** After manual Disconnect, do not auto-register until user clicks Connect again. */
  const [deferAutoSip, setDeferAutoSip] = useState(false)
  const [webrtcConnecting, setWebrtcConnecting] = useState(false)
  const [sipConnected, setSipConnected] = useState(false)
  const [inWebRtcCall, setInWebRtcCall] = useState(false)
  const [callPhase, setCallPhase] = useState<'idle' | 'ringing' | 'active'>('idle')
  const [callMuted, setCallMuted] = useState(false)
  const [callOnHold, setCallOnHold] = useState(false)
  const [dtmfPanelOpen, setDtmfPanelOpen] = useState(false)
  const [speakerPanelOpen, setSpeakerPanelOpen] = useState(false)
  const [callStartedAt, setCallStartedAt] = useState<number | null>(null)
  const [callElapsedMs, setCallElapsedMs] = useState(0)
  const [callWindowPos, setCallWindowPos] = useState<{ x: number; y: number } | null>(null)
  const callWindowRef = useRef<HTMLDivElement | null>(null)
  const dragStateRef = useRef<{ offsetX: number; offsetY: number } | null>(null)
  const [incomingCall, setIncomingCall] = useState<{
    from: string
    displayName?: string
    accept: () => void
    reject: () => void
  } | null>(null)
  const [callRemoteNumber, setCallRemoteNumber] = useState<string>('')

  const [audioInputs, setAudioInputs] = useState<MediaDeviceInfo[]>([])
  const [audioOutputs, setAudioOutputs] = useState<MediaDeviceInfo[]>([])
  const [micId, setMicId] = useState(() =>
    typeof localStorage !== 'undefined' ? localStorage.getItem(LS_AUDIO_MIC) || '' : '',
  )
  const [speakerId, setSpeakerId] = useState(() =>
    typeof localStorage !== 'undefined' ? localStorage.getItem(LS_AUDIO_OUT) || '' : '',
  )
  const [ringtoneId, setRingtoneId] = useState(() =>
    typeof localStorage !== 'undefined' ? localStorage.getItem(LS_AUDIO_RING) || '' : '',
  )
  const [processing, setProcessing] = useState<AudioProcessingPrefs>(() => ({
    echoCancellation: loadBoolPref(LS_AUDIO_AEC, true),
    noiseSuppression: loadBoolPref(LS_AUDIO_NS, true),
    autoGainControl: loadBoolPref(LS_AUDIO_AGC, true),
  }))
  const [debugPanelOpen, setDebugPanelOpen] = useState(false)
  const ringtoneRef = useRef<RingtoneHandle | null>(null)
  /** Guards the server-side save effect so the initial hydration doesn't echo back. */
  const prefsHydratedRef = useRef(false)
  const prefsSaveTimerRef = useRef<number | null>(null)

  const mustChange = Boolean(me?.must_change_password)

  useEffect(() => {
    if (mustChange) nav('/change-password', { replace: true })
  }, [mustChange, nav])

  async function load() {
    setDeferAutoSip(false)
    setLoading(true)
    try {
      let wc: WebRtcConfig | null = null
      try {
        wc = await api.getWebRtcConfig()
      } catch (err: unknown) {
        const ae = err as ApiError
        if (ae?.status === 401) {
          toast.error('Session expired. Please sign in again.')
          void refresh()
        } else {
          const msg = ae?.message || (err instanceof Error ? err.message : 'Failed to load WebRTC settings')
          toast.error(`${msg}. Check that you are logged in and the server is reachable.`)
        }
        wc = null
      }
      setWebRtcConfig(wc)
      if (
        wc &&
        (!String(wc.wsUrl || '').trim() ||
          !String(wc.sipHost || '').trim() ||
          !String(wc.extension || '').trim())
      ) {
        toast.warning(
          'Browser calling is not fully configured: WSS URL, SIP host, or extension is missing. Ask an admin to set Web Admin → Browser calling.',
        )
      }

      const [my, cdrR] = await Promise.all([api.myCallerIds(), api.cdr({ limit: 20 })])
      const items = my.callerid_items || []
      setCallerIds(items)
      if (items.length === 0) {
        setCallerId('')
      } else {
        setCallerId((prev) => {
          if (prev && items.some((c) => c.number === prev)) return prev
          return items[0]?.number || ''
        })
      }
      setCdr(cdrR.data || [])
    } catch (err: any) {
      toast.error(err?.message || 'Failed to load data')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Load server-side preferences once per session and merge into state.
  // localStorage is left in place as a cache for offline reloads; the server
  // value wins when both are present so settings follow the user across
  // browsers.
  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const { preferences } = await api.getPreferences()
        if (cancelled || !preferences) return
        if (preferences.micId != null) setMicId(preferences.micId)
        if (preferences.speakerId != null) {
          setSpeakerId(preferences.speakerId)
          sipRef.current.setOutputDevice(preferences.speakerId || undefined)
        }
        if (preferences.ringtoneDeviceId != null) setRingtoneId(preferences.ringtoneDeviceId)
        if (
          typeof preferences.aec === 'boolean' ||
          typeof preferences.ns === 'boolean' ||
          typeof preferences.agc === 'boolean'
        ) {
          setProcessing((prev) => ({
            echoCancellation:
              typeof preferences.aec === 'boolean' ? preferences.aec : prev.echoCancellation,
            noiseSuppression:
              typeof preferences.ns === 'boolean' ? preferences.ns : prev.noiseSuppression,
            autoGainControl:
              typeof preferences.agc === 'boolean' ? preferences.agc : prev.autoGainControl,
          }))
        }
      } catch {
        // 401 on first render, or BFF not ready — fall back to localStorage.
      } finally {
        prefsHydratedRef.current = true
      }
    })()
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Hash-route entry for the debug panel: typing `#/debug` opens it without a
  // click through the popover. `#/` closes it again.
  useEffect(() => {
    const check = () => {
      if (window.location.hash === '#/debug') setDebugPanelOpen(true)
    }
    check()
    window.addEventListener('hashchange', check)
    return () => window.removeEventListener('hashchange', check)
  }, [])

  // Keep the diag snapshot in sync with the UI selections so `diagnostics.json`
  // always includes the user's actual device / processing picks at the moment
  // of download.
  useEffect(() => {
    diag.updateSnapshot({
      selectedDevices: {
        micId: micId || undefined,
        speakerId: speakerId || undefined,
        ringtoneDeviceId: ringtoneId || undefined,
        aec: processing.echoCancellation,
        ns: processing.noiseSuppression,
        agc: processing.autoGainControl,
      },
      devices: {
        inputs: audioInputs.map((d) => ({ deviceId: d.deviceId, label: d.label, groupId: d.groupId })),
        outputs: audioOutputs.map((d) => ({ deviceId: d.deviceId, label: d.label, groupId: d.groupId })),
      },
    })
  }, [micId, speakerId, ringtoneId, processing, audioInputs, audioOutputs])

  // Debounced save of current prefs to the server.
  useEffect(() => {
    if (!prefsHydratedRef.current) return
    if (prefsSaveTimerRef.current != null) {
      window.clearTimeout(prefsSaveTimerRef.current)
    }
    prefsSaveTimerRef.current = window.setTimeout(() => {
      prefsSaveTimerRef.current = null
      const patch = {
        micId: micId || '',
        speakerId: speakerId || '',
        ringtoneDeviceId: ringtoneId || '',
        aec: processing.echoCancellation,
        ns: processing.noiseSuppression,
        agc: processing.autoGainControl,
      }
      api.putPreferences(patch).catch((err: { message?: string }) => {
        // Non-fatal: localStorage still works as a cache.
        console.warn('[softphone] putPreferences failed:', err?.message || err)
      })
    }, 800)
    return () => {
      if (prefsSaveTimerRef.current != null) {
        window.clearTimeout(prefsSaveTimerRef.current)
        prefsSaveTimerRef.current = null
      }
    }
  }, [micId, speakerId, ringtoneId, processing])

  const refreshAudioDevices = useCallback(async () => {
    if (!navigator.mediaDevices?.enumerateDevices) return
    try {
      await navigator.mediaDevices.getUserMedia({ audio: true })
    } catch {
      /* labels may stay empty until permission */
    }
    try {
      const all = await navigator.mediaDevices.enumerateDevices()
      setAudioInputs(all.filter((d) => d.kind === 'audioinput'))
      setAudioOutputs(all.filter((d) => d.kind === 'audiooutput'))
    } catch {
      /* ignore */
    }
  }, [])

  useEffect(() => {
    void refreshAudioDevices()
  }, [])

  useEffect(() => {
    const nav = navigator.mediaDevices
    if (!nav?.addEventListener) return
    const onDev = () => void refreshAudioDevices()
    nav.addEventListener('devicechange', onDev)
    return () => nav.removeEventListener('devicechange', onDev)
  }, [])

  useEffect(() => {
    if (micId) localStorage.setItem(LS_AUDIO_MIC, micId)
    else localStorage.removeItem(LS_AUDIO_MIC)
  }, [micId])

  useEffect(() => {
    if (speakerId) localStorage.setItem(LS_AUDIO_OUT, speakerId)
    else localStorage.removeItem(LS_AUDIO_OUT)
  }, [speakerId])

  useEffect(() => {
    if (ringtoneId) localStorage.setItem(LS_AUDIO_RING, ringtoneId)
    else localStorage.removeItem(LS_AUDIO_RING)
  }, [ringtoneId])

  useEffect(() => {
    localStorage.setItem(LS_AUDIO_AEC, processing.echoCancellation ? '1' : '0')
    localStorage.setItem(LS_AUDIO_NS, processing.noiseSuppression ? '1' : '0')
    localStorage.setItem(LS_AUDIO_AGC, processing.autoGainControl ? '1' : '0')
    sipRef.current.setAudioProcessing(processing)
  }, [processing])

  const stopIncomingRingtone = useCallback(() => {
    if (ringtoneRef.current) {
      try {
        ringtoneRef.current.stop()
      } catch {
        /* ignore */
      }
      ringtoneRef.current = null
    }
  }, [])

  const resetCallControls = useCallback(() => {
    setCallMuted(false)
    setCallOnHold(false)
    setDtmfPanelOpen(false)
    setSpeakerPanelOpen(false)
    setCallStartedAt(null)
    setCallElapsedMs(0)
  }, [])

  useEffect(() => {
    const sip = sipRef.current
    sip.onSessionEnd = () => {
      stopIncomingRingtone()
      setInWebRtcCall(false)
      setCallPhase('idle')
      setCallRemoteNumber('')
      setIncomingCall(null)
      resetCallControls()
    }
    sip.onInviteFailed = (msg) => toast.error(msg || 'Call failed')
    sip.onConnectionProblem = (msg) => {
      stopIncomingRingtone()
      toast.error(msg || 'SIP connection lost')
      setSipConnected(false)
      setInWebRtcCall(false)
      setCallPhase('idle')
      setIncomingCall(null)
      resetCallControls()
    }
    sip.onCallRinging = () => setCallPhase('ringing')
    sip.onCallConfirmed = () => {
      setCallPhase('active')
      setCallStartedAt(Date.now())
    }
    sip.onIncomingCall = (info: IncomingCall) => {
      // Play ringtone on the chosen ringtone device (falls back to speaker).
      void ensureAudioContextRunning()
        .catch(() => { /* may require user gesture */ })
        .finally(() => {
          try {
            ringtoneRef.current = startRingtone({
              outputDeviceId: (ringtoneId || speakerId) || undefined,
            })
          } catch {
            /* ignore */
          }
        })
      setIncomingCall({
        from: info.from,
        displayName: info.displayName,
        accept: () => {
          stopIncomingRingtone()
          setIncomingCall(null)
          setCallRemoteNumber(info.from)
          setInWebRtcCall(true)
          setCallPhase('active')
          info
            .accept({
              inputDeviceId: micId.trim() || undefined,
              outputDeviceId: speakerId.trim() || undefined,
              processing,
            })
            .catch((err: Error) => {
              toast.error(err?.message || 'Failed to answer call')
              setInWebRtcCall(false)
              setCallPhase('idle')
              setCallRemoteNumber('')
            })
        },
        reject: () => {
          stopIncomingRingtone()
          info.reject()
          setIncomingCall(null)
        },
      })
    }
    return () => {
      sip.onSessionEnd = null
      sip.onInviteFailed = null
      sip.onConnectionProblem = null
      sip.onCallRinging = null
      sip.onCallConfirmed = null
      sip.onIncomingCall = null
      stopIncomingRingtone()
      sip.disconnect()
      setSipConnected(false)
      setInWebRtcCall(false)
      setCallPhase('idle')
      setIncomingCall(null)
      setCallRemoteNumber('')
      resetCallControls()
    }
  }, [resetCallControls, micId, speakerId, ringtoneId, processing, stopIncomingRingtone])

  useEffect(() => {
    if (!callStartedAt) {
      setCallElapsedMs(0)
      return
    }
    const tick = () => setCallElapsedMs(Date.now() - callStartedAt)
    tick()
    const id = window.setInterval(tick, 1000)
    return () => window.clearInterval(id)
  }, [callStartedAt])

  /** Place the call window near the top-right when it opens. Re-clamp on viewport resize so the
   *  window never falls off-screen. */
  useEffect(() => {
    if (!inWebRtcCall) {
      setCallWindowPos(null)
      return
    }
    const place = () => {
      const el = callWindowRef.current
      const w = el?.offsetWidth || 360
      const h = el?.offsetHeight || 540
      setCallWindowPos((prev) => {
        const margin = 16
        const maxX = Math.max(margin, window.innerWidth - w - margin)
        const maxY = Math.max(margin, window.innerHeight - h - margin)
        if (prev == null) {
          return { x: maxX, y: margin + 24 }
        }
        return {
          x: Math.min(Math.max(margin, prev.x), maxX),
          y: Math.min(Math.max(margin, prev.y), maxY),
        }
      })
    }
    place()
    const onResize = () => place()
    window.addEventListener('resize', onResize)
    return () => window.removeEventListener('resize', onResize)
  }, [inWebRtcCall])

  function onCallWindowPointerDown(e: PointerEvent<HTMLDivElement>) {
    // Allow ignoring drag when grabbing inside an interactive control (e.g. a Select).
    const target = e.target as HTMLElement
    if (target.closest('button, input, select, [role="combobox"], a')) return
    const el = callWindowRef.current
    if (!el) return
    const rect = el.getBoundingClientRect()
    dragStateRef.current = {
      offsetX: e.clientX - rect.left,
      offsetY: e.clientY - rect.top,
    }
    el.setPointerCapture(e.pointerId)
    e.preventDefault()
  }

  function onCallWindowPointerMove(e: PointerEvent<HTMLDivElement>) {
    const drag = dragStateRef.current
    const el = callWindowRef.current
    if (!drag || !el) return
    const margin = 8
    const w = el.offsetWidth
    const h = el.offsetHeight
    const maxX = Math.max(margin, window.innerWidth - w - margin)
    const maxY = Math.max(margin, window.innerHeight - h - margin)
    const x = Math.min(Math.max(margin, e.clientX - drag.offsetX), maxX)
    const y = Math.min(Math.max(margin, e.clientY - drag.offsetY), maxY)
    setCallWindowPos({ x, y })
  }

  function onCallWindowPointerUp(e: PointerEvent<HTMLDivElement>) {
    if (!dragStateRef.current) return
    dragStateRef.current = null
    try {
      callWindowRef.current?.releasePointerCapture(e.pointerId)
    } catch {
      /* ignore */
    }
  }

  const hasCallerIdOptions = callerIds.length > 0

  const webRtcUsable = Boolean(
    webRtcConfig?.wsUrl?.trim() &&
      webRtcConfig?.sipHost?.trim() &&
      webRtcConfig?.extension?.trim(),
  )

  const serverSipPwd = useMemo(() => (webRtcConfig?.sipPassword || '').trim(), [webRtcConfig?.sipPassword])

  /** Guards re-entry of the auto-connect effect across renders.
   *  We must not put ``webrtcConnecting``/``sipConnected`` into the dep list — flipping them inside
   *  the effect would re-run cleanup and silently strand the in-flight promise (UI would stay on
   *  "Connecting..." forever). A ref is the right tool for that. */
  const autoConnectingRef = useRef(false)

  useEffect(() => {
    if (!webRtcUsable || !webRtcConfig || mustChange) return
    if (!serverSipPwd) return
    if (deferAutoSip) return
    if (autoConnectingRef.current) return
    if (sipRef.current.isRegistered()) return

    autoConnectingRef.current = true
    let cancelled = false
    void (async () => {
      setWebrtcConnecting(true)
      try {
        // REGISTER AoR user-part: proxy may send `sipContactUserPart` when `-WS` vs base is PBX-specific.
        const ext = webRtcConfig.extension.trim()
        const wsAor = webrtcSipUserPart(webRtcConfig)
        const authUser = webrtcDigestUsername(webRtcConfig.sipAuthUser, ext)
        const sipUri = `sip:${wsAor}@${webRtcConfig.sipHost.trim()}`
        await sipRef.current.connect({
          wsUrl: webRtcConfig.wsUrl.trim(),
          sipUri,
          authorizationUser: authUser,
          password: serverSipPwd,
          iceServers: webRtcConfig.iceServers,
        })
        if (cancelled) return
        setSipConnected(true)
        await refreshAudioDevices()
        toast.success('Browser calling connected.')
      } catch (err: unknown) {
        if (!cancelled) {
          const msg = err instanceof Error ? err.message : 'WebRTC connect failed'
          toast.error(msg)
          setSipConnected(false)
        }
      } finally {
        autoConnectingRef.current = false
        setWebrtcConnecting(false)
      }
    })()
    return () => {
      cancelled = true
    }
  }, [
    webRtcUsable,
    webRtcConfig,
    serverSipPwd,
    mustChange,
    deferAutoSip,
    refreshAudioDevices,
  ])

  const canHangup = inWebRtcCall
  const destOk = destination.trim().length >= 1
  const canPlaceWebRtc = sipConnected && !inWebRtcCall && destOk
  const canPlaceOriginate =
    !sipConnected && hasCallerIdOptions && callerId.trim().length > 0 && destOk

  /** Dialer is usable: real call, hangup, or guided hint (WebRTC not connected yet / nothing configured).
   *  Do not gate on `webrtcConnecting`: that state only applies to the Connect button; blocking here left the
   *  green Call button disabled (e.g. after Connect or with no Caller ID), even though click only shows toasts or originate. */
  const canCall = useMemo(() => {
    if (!destOk || calling) return false
    if (canHangup) return true
    if (canPlaceWebRtc || canPlaceOriginate) return true
    if (webRtcUsable && !sipConnected) return true
    if (!webRtcUsable && !hasCallerIdOptions) return true
    return false
  }, [
    destOk,
    calling,
    canHangup,
    canPlaceWebRtc,
    canPlaceOriginate,
    webRtcUsable,
    sipConnected,
    hasCallerIdOptions,
  ])

  const callerIdLabel = useMemo(() => {
    const item = callerIds.find((c) => c.number === callerId)
    if (!item) return ''
    return item.name ? `${item.number} — ${item.name}` : item.number
  }, [callerIds, callerId])

  function appendDigit(d: string) {
    setDestination((prev) => `${prev}${d}`)
  }

  function backspace() {
    setDestination((prev) => prev.slice(0, -1))
  }

  async function onOriginate() {
    if (!canPlaceOriginate || calling || webrtcConnecting) return
    setCalling(true)
    try {
      const r = await api.originate(destination.trim(), callerId.trim())
      toast.success(`Originate sent (id: ${r.originate_id})`)
      setDestination('')
      await load()
    } catch (err: any) {
      toast.error(err?.message || 'Originate failed')
    } finally {
      setCalling(false)
    }
  }

  async function onWebRtcConnect() {
    if (!webRtcUsable || !webRtcConfig) {
      toast.error('WebRTC is not configured on the server.')
      return
    }
    const pwd = serverSipPwd || sipPassword.trim()
    if (!pwd) {
      toast.error(
        'No SIP secret for this extension in MikoPBX (or proxy cannot read config DB). Enter SIP password below, or fix extension / PBX access.',
      )
      return
    }
    setDeferAutoSip(false)
    setWebrtcConnecting(true)
    try {
      const ext = webRtcConfig.extension.trim()
      const wsAor = webrtcSipUserPart(webRtcConfig)
      const authUser = webrtcDigestUsername(webRtcConfig.sipAuthUser, ext)
      const sipUri = `sip:${wsAor}@${webRtcConfig.sipHost.trim()}`
      await sipRef.current.connect({
        wsUrl: webRtcConfig.wsUrl.trim(),
        sipUri,
        authorizationUser: authUser,
        password: pwd,
        iceServers: webRtcConfig.iceServers,
      })
      setSipConnected(true)
      await refreshAudioDevices()
      toast.success('Browser calling connected.')
    } catch (err: any) {
      toast.error(err?.message || 'WebRTC connect failed')
      setSipConnected(false)
    } finally {
      setWebrtcConnecting(false)
    }
  }

  function onWebRtcDisconnect() {
    setDeferAutoSip(true)
    sipRef.current.disconnect()
    setSipConnected(false)
    setWebrtcConnecting(false)
    setInWebRtcCall(false)
    setCallPhase('idle')
    resetCallControls()
  }

  function onToggleMute() {
    setCallMuted((prev) => {
      const next = !prev
      sipRef.current.setMicMuted(next)
      return next
    })
  }

  function onToggleHold() {
    setCallOnHold((prev) => {
      const next = !prev
      sipRef.current.setHold(next)
      return next
    })
  }

  function onSendDtmf(tone: string) {
    sipRef.current.sendDtmf(tone)
  }

  async function onCallAction() {
    const dest = destination.trim()
    if (!dest) {
      toast.error('Enter a number.')
      return
    }
    if (inWebRtcCall) {
      sipRef.current.hangup()
      setInWebRtcCall(false)
      setCallPhase('idle')
      return
    }
    if (sipConnected) {
      setCalling(true)
      setInWebRtcCall(true)
      setCallPhase('ringing')
      setCallRemoteNumber(dest)
      try {
        await sipRef.current.call(dest, {
          inputDeviceId: micId.trim() || undefined,
          outputDeviceId: speakerId.trim() || undefined,
          processing,
        })
      } catch (err: any) {
        toast.error(err?.message || 'Call failed')
        setInWebRtcCall(false)
        setCallPhase('idle')
        setCallRemoteNumber('')
      } finally {
        setCalling(false)
      }
      return
    }
    if (hasCallerIdOptions && !callerId.trim()) {
      toast.error('Select “My caller ID”.')
      return
    }
    if (hasCallerIdOptions && callerId.trim()) {
      await onOriginate()
      return
    }
    if (webRtcUsable && !sipConnected) {
      toast.info(
        serverSipPwd
          ? 'Browser calling is connecting or was disconnected — use Connect below, then Call again.'
          : 'Use “Browser call (WebRTC)” below: enter SIP password and press Connect, then Call again.',
      )
      return
    }
    toast.error(
      'WebRTC is not configured on the server and no Caller ID is assigned. Set WSS + SIP host in Web Admin, or ask an admin to assign a Caller ID.',
    )
  }

  async function onSignOut() {
    sipRef.current.disconnect()
    setSipConnected(false)
    setDeferAutoSip(false)
    setInWebRtcCall(false)
    setCallPhase('idle')
    await logout()
    nav('/login', { replace: true })
  }

  return (
    <div className="min-h-full bg-background">
      <div className="mx-auto max-w-5xl p-4 md:p-6">
        <div className="flex items-center justify-between gap-3">
          <div>
            <div className="flex items-center gap-2 text-xl font-semibold tracking-tight">
              <img src={`${import.meta.env.BASE_URL}favicon.ico`} alt="" className="h-6 w-6" />
              <span>Callspire Softphone</span>
            </div>
            <div className="text-sm text-muted-foreground">
              {me?.email ? `${me.email} • ext ${me.extension}` : '—'}
            </div>
          </div>
          <div className="flex items-center gap-2">
            {webRtcUsable ? (
              <WebRtcStatusPopover
                status={
                  sipConnected
                    ? 'registered'
                    : webrtcConnecting
                      ? 'connecting'
                      : 'offline'
                }
                hasServerPassword={Boolean(serverSipPwd)}
                passwordInput={sipPassword}
                onPasswordInputChange={setSipPassword}
                canConnect={Boolean(serverSipPwd) || sipPassword.trim().length > 0}
                onConnect={() => void onWebRtcConnect()}
                onDisconnect={onWebRtcDisconnect}
                audioInputs={audioInputs}
                audioOutputs={audioOutputs}
                micId={micId}
                speakerId={speakerId}
                ringtoneId={ringtoneId}
                processing={processing}
                onMicChange={(id) => {
                  setMicId(id)
                  if (inWebRtcCall) {
                    void hotSwapMicrophone(sipRef.current, id || undefined, processing).catch(
                      (err: Error) => toast.error(err?.message || 'Mic switch failed'),
                    )
                  }
                }}
                onSpeakerChange={(id) => {
                  setSpeakerId(id)
                  sipRef.current.setOutputDevice(id || undefined)
                }}
                onRingtoneChange={setRingtoneId}
                onProcessingChange={(p) =>
                  setProcessing((prev) => ({
                    echoCancellation: p.echoCancellation ?? prev.echoCancellation,
                    noiseSuppression: p.noiseSuppression ?? prev.noiseSuppression,
                    autoGainControl: p.autoGainControl ?? prev.autoGainControl,
                  }))
                }
                onRefreshDevices={() => void refreshAudioDevices()}
                onOpenDebugPanel={() => setDebugPanelOpen(true)}
                inCall={inWebRtcCall}
                loading={loading}
                extension={webRtcConfig?.extension}
              />
            ) : null}
            <Select
              value={theme}
              onValueChange={(v) => {
                const m = (v ?? 'system') as ThemeMode
                setTheme(m)
                setThemeMode(m)
              }}
              disabled={loading}
            >
              <SelectTrigger className="w-[150px]">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="system">System</SelectItem>
                <SelectItem value="light">Light</SelectItem>
                <SelectItem value="dark">Dark</SelectItem>
              </SelectContent>
            </Select>
            <Button variant="secondary" onClick={() => void load()} disabled={loading}>
              Refresh
            </Button>
            <Button variant="outline" onClick={() => void onSignOut()}>
              Sign out
            </Button>
          </div>
        </div>

        <Separator className="my-6" />

        <div className="grid gap-6 md:grid-cols-2">
          <Card>
            <CardHeader>
              <CardTitle>Dial</CardTitle>
              <CardDescription>
                {inWebRtcCall
                  ? 'Call in progress — audio via WebRTC. Use Hang up when finished.'
                  : sipConnected
                    ? 'Browser audio is active — call goes through WebRTC.'
                    : hasCallerIdOptions
                      ? 'Choose Caller ID and enter a number, or connect WebRTC below.'
                      : 'Enter a number. Use WebRTC below or ask an admin to assign a Caller ID.'}
              </CardDescription>
            </CardHeader>
            <CardContent>
              <div className="mx-auto w-full max-w-[320px]">
                <div className="mb-4 grid gap-1.5">
                  <Label htmlFor="dst" className="text-foreground/80">
                    Enter a number
                  </Label>
                  <div className="relative">
                    <Input
                      id="dst"
                      value={destination}
                      onChange={(e) => setDestination(e.target.value)}
                      placeholder="Enter a number"
                      inputMode="tel"
                      disabled={inWebRtcCall}
                      className={[
                        'h-9 pr-10 text-center text-sm',
                        !inWebRtcCall && destination.trim().length === 0
                          ? 'ring-2 ring-emerald-500/25 focus-visible:ring-emerald-500/35'
                          : '',
                      ].join(' ')}
                    />
                    <button
                      type="button"
                      aria-label="Backspace"
                      onClick={backspace}
                      disabled={loading || inWebRtcCall || destination.length === 0}
                      className="absolute right-2 top-1/2 -translate-y-1/2 rounded-md px-2 py-1 text-sm text-muted-foreground hover:bg-muted disabled:opacity-50"
                    >
                      ⌫
                    </button>
                  </div>
                </div>

                {hasCallerIdOptions ? (
                  <div className="mb-4 flex items-center gap-3">
                    <Label className="shrink-0">My caller ID</Label>
                    <Select value={callerId} onValueChange={(v) => setCallerId(v ?? '')} disabled={loading}>
                      <SelectTrigger className="h-9 flex-1 text-sm">
                        <span className="truncate text-left">
                          {callerIdLabel || 'Select caller ID'}
                        </span>
                      </SelectTrigger>
                      <SelectContent>
                        {callerIds.map((c) => (
                          <SelectItem key={c.number} value={c.number}>
                            {c.name ? `${c.number} — ${c.name}` : c.number}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>
                ) : !loading && !sipConnected ? (
                  <p className="mb-4 text-center text-xs text-muted-foreground">
                    No Caller ID assigned to your extension. Ask an administrator to assign numbers in Web Admin, or
                    connect browser calling below.
                  </p>
                ) : null}

                <div className="mx-auto grid w-fit grid-cols-3 gap-2.5">
                  {(['1', '2', '3', '4', '5', '6', '7', '8', '9', '*', '0', '#'] as const).map((d) => (
                    <button
                      key={d}
                      type="button"
                      onClick={() => appendDigit(d)}
                      disabled={loading || inWebRtcCall}
                      className="group grid h-16 w-16 place-items-center rounded-full border bg-background text-foreground shadow-sm transition hover:bg-muted disabled:opacity-50"
                    >
                      <div className="grid place-items-center leading-none">
                        <div className="text-[22px] font-medium">{d}</div>
                        <div className="mt-1 text-[9px] tracking-widest text-muted-foreground">
                          {DIGIT_LETTERS[d]}
                        </div>
                      </div>
                    </button>
                  ))}
                </div>

                {!inWebRtcCall ? (
                  <div className="mt-5 flex flex-col items-center justify-center gap-2">
                    <button
                      type="button"
                      onClick={() => void onCallAction()}
                      disabled={!canCall}
                      className="group grid h-14 w-14 place-items-center rounded-full bg-emerald-600 text-white shadow-md shadow-emerald-600/20 transition hover:bg-emerald-700 active:scale-95 disabled:opacity-50 disabled:shadow-none"
                      aria-label="Call"
                      title={sipConnected ? 'Call (WebRTC)' : 'Call'}
                    >
                      <Phone className="h-7 w-7" strokeWidth={2} aria-hidden />
                    </button>
                  </div>
                ) : null}
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Recent calls</CardTitle>
              <CardDescription>Latest CDR entries.</CardDescription>
            </CardHeader>
            <CardContent>
              {loading ? (
                <div className="text-sm text-muted-foreground">Loading…</div>
              ) : cdr.length === 0 ? (
                <div className="text-sm text-muted-foreground">No data</div>
              ) : (
                <ul className="divide-y divide-border -mx-2">
                  {cdr.map((r, i) => (
                    <CallLogItem
                      key={`${r.linkedid || 'row'}-${r.start}-${r.dst_num}-${i}`}
                      record={r}
                      myExtension={me?.extension || ''}
                      onRedial={(num) => {
                        if (!num) return
                        setDestination(num)
                      }}
                    />
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>
        </div>
      </div>

      {incomingCall ? (
        <div
          role="dialog"
          aria-label="Incoming call"
          aria-modal="true"
          className="fixed inset-x-0 top-4 z-[110] mx-auto flex w-[min(360px,calc(100vw-2rem))] flex-col rounded-2xl border bg-card p-5 shadow-2xl"
        >
          <div className="mb-1 flex items-center gap-2 text-xs uppercase tracking-wider text-muted-foreground">
            <PhoneIncoming className="h-3.5 w-3.5 text-sky-500" aria-hidden />
            Incoming call
          </div>
          <div className="text-center">
            <div className="break-all text-2xl font-semibold tabular-nums leading-tight text-foreground">
              {incomingCall.from}
            </div>
            {incomingCall.displayName ? (
              <div className="mt-1 truncate text-sm text-muted-foreground">
                {incomingCall.displayName}
              </div>
            ) : null}
            <div className="mt-2 inline-flex items-center gap-2 text-sm font-medium text-sky-500 dark:text-sky-400">
              <span className="relative flex h-2 w-2">
                <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-sky-400 opacity-75" />
                <span className="relative inline-flex h-2 w-2 rounded-full bg-sky-500" />
              </span>
              Ringing…
            </div>
          </div>
          <div className="mt-5 grid grid-cols-2 gap-3">
            <button
              type="button"
              onClick={() => incomingCall.reject()}
              className="grid h-12 place-items-center rounded-full bg-red-600 text-white shadow-md shadow-red-600/25 transition hover:bg-red-700 active:scale-95"
              aria-label="Reject call"
              title="Reject"
            >
              <PhoneOff className="h-5 w-5" aria-hidden />
            </button>
            <button
              type="button"
              onClick={() => incomingCall.accept()}
              className="grid h-12 place-items-center rounded-full bg-emerald-600 text-white shadow-md shadow-emerald-600/25 transition hover:bg-emerald-700 active:scale-95"
              aria-label="Answer call"
              title="Answer"
            >
              <Phone className="h-5 w-5" aria-hidden />
            </button>
          </div>
        </div>
      ) : null}

      {inWebRtcCall ? (
        <div
          ref={callWindowRef}
          role="dialog"
          aria-label="Active call"
          onPointerDown={onCallWindowPointerDown}
          onPointerMove={onCallWindowPointerMove}
          onPointerUp={onCallWindowPointerUp}
          onPointerCancel={onCallWindowPointerUp}
          style={{
            position: 'fixed',
            left: callWindowPos ? `${callWindowPos.x}px` : undefined,
            top: callWindowPos ? `${callWindowPos.y}px` : undefined,
            visibility: callWindowPos ? 'visible' : 'hidden',
            touchAction: 'none',
            cursor: dragStateRef.current ? 'grabbing' : 'grab',
          }}
          className="z-[100] flex w-[340px] flex-col rounded-2xl border bg-card p-5 shadow-2xl select-none"
        >
          <div className="mb-3 flex items-center justify-between gap-2">
            <div className="flex items-center gap-1.5 text-xs uppercase tracking-wider text-muted-foreground">
              <span className="inline-flex h-1 w-3 rounded-full bg-muted-foreground/40" />
              <span className="inline-flex h-1 w-3 rounded-full bg-muted-foreground/40" />
              <span className="font-medium tracking-normal normal-case">Active Call</span>
            </div>
          </div>

          <div className="text-center">
            <div className="break-all text-3xl font-semibold tabular-nums leading-tight text-foreground">
              {(callRemoteNumber || destination).trim()}
            </div>
            <div className="mt-2 text-sm font-medium">
              {callPhase === 'ringing' ? (
                <span className="inline-flex items-center gap-2 text-blue-500 dark:text-blue-400">
                  <span className="relative flex h-2 w-2">
                    <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-blue-400 opacity-75" />
                    <span className="relative inline-flex h-2 w-2 rounded-full bg-blue-500" />
                  </span>
                  Calling…
                </span>
              ) : (
                <span className="font-mono tabular-nums text-emerald-600 dark:text-emerald-400">
                  {formatDuration(callElapsedMs)}
                </span>
              )}
            </div>
          </div>

            {dtmfPanelOpen ? (
              <div className="mt-6 grid grid-cols-3 gap-2.5">
                {(['1', '2', '3', '4', '5', '6', '7', '8', '9', '*', '0', '#'] as const).map((d) => (
                  <button
                    key={d}
                    type="button"
                    onClick={() => onSendDtmf(d)}
                    className="grid h-12 place-items-center rounded-full border bg-muted text-foreground transition hover:bg-muted/70 active:scale-95"
                    aria-label={`Send DTMF ${d}`}
                  >
                    <span className="text-lg font-medium">{d}</span>
                  </button>
                ))}
              </div>
            ) : speakerPanelOpen ? (
              <div className="mt-6">
                <Label htmlFor="overlay-speaker" className="text-xs text-muted-foreground">
                  Speaker
                </Label>
                <Select
                  value={speakerId || 'default'}
                  onValueChange={(v) => {
                    const id = v == null || v === 'default' ? '' : v
                    setSpeakerId(id)
                    sipRef.current.setOutputDevice(id || undefined)
                  }}
                >
                  <SelectTrigger id="overlay-speaker" className="mt-1 h-10 w-full">
                    <SelectValue placeholder="Default output" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="default">System default</SelectItem>
                    {audioOutputs
                      .filter((d) => d.deviceId)
                      .map((d) => (
                        <SelectItem key={d.deviceId} value={d.deviceId}>
                          {d.label || `Output ${d.deviceId.slice(0, 8)}…`}
                        </SelectItem>
                      ))}
                  </SelectContent>
                </Select>
                <p className="mt-2 text-[11px] text-muted-foreground">
                  Output device change takes effect immediately (Chromium).
                </p>
              </div>
            ) : (
              <div className="flex-1" />
            )}

            <div className="mt-auto pt-6">
              <div className="mb-5 grid grid-cols-4 gap-3">
                <CallControlButton
                  active={callMuted}
                  disabled={callPhase !== 'active'}
                  onClick={onToggleMute}
                  ariaLabel={callMuted ? 'Unmute microphone' : 'Mute microphone'}
                  title={callMuted ? 'Unmute' : 'Mute'}
                  icon={callMuted ? <MicOff className="h-5 w-5" /> : <Mic className="h-5 w-5" />}
                />
                <CallControlButton
                  active={callOnHold}
                  disabled={callPhase !== 'active'}
                  onClick={onToggleHold}
                  ariaLabel={callOnHold ? 'Resume call' : 'Hold call'}
                  title={callOnHold ? 'Resume' : 'Hold'}
                  icon={callOnHold ? <Play className="h-5 w-5" /> : <Pause className="h-5 w-5" />}
                />
                <CallControlButton
                  active={speakerPanelOpen}
                  onClick={() => {
                    setSpeakerPanelOpen((v) => !v)
                    setDtmfPanelOpen(false)
                  }}
                  ariaLabel="Speaker"
                  title="Speaker"
                  icon={<Volume2 className="h-5 w-5" />}
                />
                <CallControlButton
                  active={dtmfPanelOpen}
                  disabled={callPhase !== 'active'}
                  onClick={() => {
                    setDtmfPanelOpen((v) => !v)
                    setSpeakerPanelOpen(false)
                  }}
                  ariaLabel="Dialpad (DTMF)"
                  title="Dialpad"
                  icon={<Grid3x3 className="h-5 w-5" />}
                />
              </div>

              <div className="flex items-center justify-center">
                <button
                  type="button"
                  onClick={() => void onCallAction()}
                  className="grid h-16 w-16 place-items-center rounded-full bg-red-600 text-white shadow-lg shadow-red-600/25 transition hover:bg-red-700 active:scale-95"
                  aria-label="Hang up"
                  title="Hang up"
                >
                  <PhoneOff className="h-7 w-7" aria-hidden />
                </button>
              </div>
            </div>
          </div>
      ) : null}

      <DebugPanel open={debugPanelOpen} onClose={() => setDebugPanelOpen(false)} />
    </div>
  )
}

function CallLogItem(props: {
  record: CdrRecord
  myExtension: string
  onRedial: (number: string) => void
}) {
  const { record, myExtension, onRedial } = props
  const { direction, counterpart, outcome } = classifyCdr(record, myExtension)
  const when = parseCdrDate(record.start)
  const now = new Date()
  const dateLabel = when ? formatRelativeDay(when, now) : ''
  const timeLabel = when ? formatTimeOfDay(when) : ''
  const duration = formatBillsec(record.billsec || 0)

  const isMissedIncoming = direction === 'incoming' && outcome !== 'answered'
  const Icon =
    isMissedIncoming
      ? PhoneMissed
      : direction === 'outgoing'
      ? PhoneOutgoing
      : PhoneIncoming

  const iconWrap = isMissedIncoming
    ? 'bg-red-500/10 text-red-500 dark:text-red-400'
    : direction === 'outgoing'
    ? 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400'
    : 'bg-sky-500/10 text-sky-600 dark:text-sky-400'

  const nameClass = isMissedIncoming
    ? 'text-red-600 dark:text-red-400'
    : 'text-foreground'

  const statusLabel =
    outcome === 'missed'
      ? 'Missed'
      : outcome === 'busy'
      ? 'Busy'
      : outcome === 'failed'
      ? 'Failed'
      : duration || (direction === 'outgoing' ? 'Outgoing' : 'Incoming')

  return (
    <li className="group relative flex items-center gap-3 px-2 py-2.5 transition-colors hover:bg-muted/50">
      <div className={`grid h-9 w-9 shrink-0 place-items-center rounded-full ${iconWrap}`}>
        <Icon className="h-4 w-4" aria-hidden />
      </div>

      <div className="min-w-0 flex-1">
        <div className={`truncate text-sm font-medium tabular-nums ${nameClass}`}>
          {counterpart}
        </div>
        <div className="mt-0.5 flex items-center gap-1.5 text-[11px] text-muted-foreground">
          <span className="capitalize">{direction}</span>
          {record.caller_id && record.caller_id !== counterpart ? (
            <>
              <span aria-hidden>•</span>
              <span className="truncate">via {record.caller_id}</span>
            </>
          ) : null}
        </div>
      </div>

      <div className="flex shrink-0 flex-col items-end gap-0.5 text-right">
        <div className="text-[11px] text-muted-foreground">
          {dateLabel}
          {timeLabel ? <span className="ml-1.5 tabular-nums">{timeLabel}</span> : null}
        </div>
        <div
          className={`text-[11px] tabular-nums ${
            isMissedIncoming ? 'text-red-500 dark:text-red-400' : 'text-muted-foreground'
          }`}
        >
          {statusLabel}
        </div>
      </div>

      <button
        type="button"
        onClick={() => onRedial(counterpart)}
        disabled={!counterpart || counterpart === '—'}
        className="ml-1 grid h-8 w-8 shrink-0 place-items-center rounded-full text-muted-foreground opacity-0 transition hover:bg-emerald-500/10 hover:text-emerald-600 group-hover:opacity-100 focus:opacity-100 disabled:hidden dark:hover:text-emerald-400"
        aria-label={`Call ${counterpart}`}
        title={`Call ${counterpart}`}
      >
        <Phone className="h-4 w-4" aria-hidden />
      </button>
    </li>
  )
}

function CallControlButton(props: {
  active?: boolean
  disabled?: boolean
  onClick: () => void
  ariaLabel: string
  title?: string
  icon: ReactNode
}) {
  const { active, disabled, onClick, ariaLabel, title, icon } = props
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={ariaLabel}
      title={title}
      aria-pressed={active}
      className={[
        'grid h-12 w-12 place-items-center justify-self-center rounded-full border transition active:scale-95 disabled:opacity-40 disabled:cursor-not-allowed',
        active
          ? 'border-emerald-500/40 bg-emerald-500/15 text-emerald-600 dark:text-emerald-400'
          : 'border-border bg-muted text-foreground hover:bg-muted/70',
      ].join(' ')}
    >
      {icon}
    </button>
  )
}

