/**
 * Diagnostic event bus + ring-buffer for the browser softphone.
 *
 * Everything the WebRTC/SIP layer wants to surface (WSS frames, JsSIP events,
 * PeerConnection state changes, getStats snapshots, UI actions, errors) goes
 * through {@link diag}. Consumers (Debug panel, download-diagnostics button)
 * read the buffer via `subscribe()` / `getAll()` and build a JSON dump via
 * `dumpJson()`.
 *
 * Storage is in-memory only (no network I/O). The buffer is capped and drops
 * oldest events first.
 */

export type DiagLevel = 'trace' | 'debug' | 'info' | 'warn' | 'error'

export type DiagCategory =
  | 'SIP'
  | 'WSS'
  | 'ICE'
  | 'PC'
  | 'STATS'
  | 'AUDIO'
  | 'DEVICE'
  | 'UI'
  | 'ERROR'

export const DIAG_LEVELS: DiagLevel[] = ['trace', 'debug', 'info', 'warn', 'error']
export const DIAG_CATEGORIES: DiagCategory[] = [
  'SIP',
  'WSS',
  'ICE',
  'PC',
  'STATS',
  'AUDIO',
  'DEVICE',
  'UI',
  'ERROR',
]

const LEVEL_ORDER: Record<DiagLevel, number> = {
  trace: 0,
  debug: 1,
  info: 2,
  warn: 3,
  error: 4,
}

export type DiagEvent = {
  id: number
  /** Epoch ms */
  ts: number
  level: DiagLevel
  category: DiagCategory
  message: string
  /** Arbitrary structured payload. Must be JSON-safe. */
  data?: unknown
}

export type DiagSnapshot = {
  /** ISO 8601 UTC */
  createdAt: string
  userAgent: string
  webClientVersion: string
  uaConfig?: Record<string, unknown>
  iceServers?: unknown
  devices?: {
    inputs?: Array<Pick<MediaDeviceInfo, 'deviceId' | 'label' | 'groupId'>>
    outputs?: Array<Pick<MediaDeviceInfo, 'deviceId' | 'label' | 'groupId'>>
  }
  selectedDevices?: {
    micId?: string
    speakerId?: string
    ringtoneDeviceId?: string
    aec?: boolean
    ns?: boolean
    agc?: boolean
  }
  lastLocalSdp?: string
  lastRemoteSdp?: string
  lastStats?: unknown
  session?: {
    status?: string
    lastError?: string
  }
}

export type DiagListener = (ev: DiagEvent) => void

const DEFAULT_CAPACITY = 5000

const WEB_CLIENT_VERSION =
  ((): string => {
    try {
      const v = (import.meta as unknown as { env?: { VITE_APP_VERSION?: string } })?.env
        ?.VITE_APP_VERSION
      return typeof v === 'string' && v ? v : 'dev'
    } catch {
      return 'dev'
    }
  })()

class Diag {
  private buffer: DiagEvent[] = []
  private capacity: number = DEFAULT_CAPACITY
  private nextId = 1
  private listeners = new Set<DiagListener>()
  private snapshot: DiagSnapshot = {
    createdAt: new Date().toISOString(),
    userAgent: typeof navigator !== 'undefined' ? navigator.userAgent : '',
    webClientVersion: WEB_CLIENT_VERSION,
  }
  private minLevel: DiagLevel = 'debug'
  /** When false, new events are ignored (used by pause in the UI). */
  private accepting = true
  /** Also mirror events to console (default: off to avoid noise). */
  private mirrorToConsole = false

  setCapacity(n: number): void {
    const cap = Math.max(100, Math.min(100000, Math.floor(n)))
    this.capacity = cap
    if (this.buffer.length > cap) {
      this.buffer.splice(0, this.buffer.length - cap)
    }
  }

  getCapacity(): number {
    return this.capacity
  }

  setMinLevel(level: DiagLevel): void {
    this.minLevel = level
  }

  getMinLevel(): DiagLevel {
    return this.minLevel
  }

  setAccepting(on: boolean): void {
    this.accepting = !!on
  }

  isAccepting(): boolean {
    return this.accepting
  }

  setMirrorToConsole(on: boolean): void {
    this.mirrorToConsole = !!on
  }

  clear(): void {
    this.buffer = []
  }

  push(
    level: DiagLevel,
    category: DiagCategory,
    message: string,
    data?: unknown,
  ): void {
    if (!this.accepting) return
    if (LEVEL_ORDER[level] < LEVEL_ORDER[this.minLevel]) return

    const ev: DiagEvent = {
      id: this.nextId++,
      ts: Date.now(),
      level,
      category,
      message: typeof message === 'string' ? message : String(message),
      data: safeData(data),
    }
    this.buffer.push(ev)
    if (this.buffer.length > this.capacity) {
      this.buffer.splice(0, this.buffer.length - this.capacity)
    }
    if (this.mirrorToConsole) {
      const tag = `[diag:${category}] ${ev.message}`
      const fn =
        level === 'error'
          ? console.error
          : level === 'warn'
            ? console.warn
            : level === 'info'
              ? console.info
              : console.debug
      if (ev.data !== undefined) fn.call(console, tag, ev.data)
      else fn.call(console, tag)
    }
    for (const cb of this.listeners) {
      try {
        cb(ev)
      } catch {
        /* listener error must not break the emitter */
      }
    }
  }

  trace(cat: DiagCategory, msg: string, data?: unknown): void {
    this.push('trace', cat, msg, data)
  }
  debug(cat: DiagCategory, msg: string, data?: unknown): void {
    this.push('debug', cat, msg, data)
  }
  info(cat: DiagCategory, msg: string, data?: unknown): void {
    this.push('info', cat, msg, data)
  }
  warn(cat: DiagCategory, msg: string, data?: unknown): void {
    this.push('warn', cat, msg, data)
  }
  error(cat: DiagCategory, msg: string, data?: unknown): void {
    this.push('error', cat, msg, data)
  }

  subscribe(cb: DiagListener): () => void {
    this.listeners.add(cb)
    return () => this.listeners.delete(cb)
  }

  getAll(): DiagEvent[] {
    return this.buffer.slice()
  }

  getCount(): number {
    return this.buffer.length
  }

  updateSnapshot(patch: Partial<DiagSnapshot>): void {
    this.snapshot = { ...this.snapshot, ...patch, createdAt: this.snapshot.createdAt }
  }

  getSnapshot(): DiagSnapshot {
    return this.snapshot
  }

  /** Build a JSON-serializable diagnostics dump. */
  dumpJson(): string {
    const dump = {
      snapshot: { ...this.snapshot, createdAt: new Date().toISOString() },
      events: this.buffer,
    }
    try {
      return JSON.stringify(dump, null, 2)
    } catch {
      return JSON.stringify({
        snapshot: this.snapshot,
        events: this.buffer.map((e) => ({ ...e, data: undefined })),
      })
    }
  }

  /** Download the current dump as a file via an anchor click. */
  downloadDump(filename?: string): void {
    const json = this.dumpJson()
    const blob = new Blob([json], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = filename || defaultDumpFilename()
    document.body.appendChild(a)
    a.click()
    setTimeout(() => {
      a.remove()
      URL.revokeObjectURL(url)
    }, 100)
  }
}

/**
 * Best-effort deep clone that drops non-serializable values (functions,
 * DOM nodes, cyclic graphs) so the ring buffer never holds references to
 * large objects or causes JSON.stringify to throw.
 */
function safeData(value: unknown): unknown {
  if (value == null) return value
  const t = typeof value
  if (t === 'string' || t === 'number' || t === 'boolean' || t === 'bigint') return value
  if (t === 'function') return `[function ${((value as { name?: string }).name) || 'anonymous'}]`
  try {
    const seen = new WeakSet<object>()
    return JSON.parse(
      JSON.stringify(value, (_key, v) => {
        if (v == null) return v
        const tt = typeof v
        if (tt === 'function') return undefined
        if (tt !== 'object') return v
        if (seen.has(v as object)) return '[cyclic]'
        seen.add(v as object)
        if (v instanceof Error) {
          return { name: v.name, message: v.message, stack: v.stack }
        }
        if (typeof Node !== 'undefined' && v instanceof Node) {
          return '[Node]'
        }
        if (typeof Element !== 'undefined' && v instanceof Element) {
          return `[Element <${v.tagName.toLowerCase()}>]`
        }
        return v
      }),
    )
  } catch {
    try {
      return String(value)
    } catch {
      return '[unserializable]'
    }
  }
}

function defaultDumpFilename(): string {
  const d = new Date()
  const pad = (n: number, w = 2) => String(n).padStart(w, '0')
  const stamp = `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}`
  return `softphone-diagnostics-${stamp}.json`
}

/** Lightweight formatter for the UI (HH:mm:ss.SSS local). */
export function formatDiagTime(ts: number): string {
  const d = new Date(ts)
  const pad = (n: number, w = 2) => String(n).padStart(w, '0')
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`
}

export const diag = new Diag()

/** Convenience filter helper for the UI. */
export function filterEvents(
  events: DiagEvent[],
  opts: {
    minLevel?: DiagLevel
    categories?: DiagCategory[]
    search?: string
  },
): DiagEvent[] {
  const minIdx = opts.minLevel ? LEVEL_ORDER[opts.minLevel] : 0
  const cats = opts.categories && opts.categories.length ? new Set(opts.categories) : null
  const needle = (opts.search || '').trim().toLowerCase()
  return events.filter((ev) => {
    if (LEVEL_ORDER[ev.level] < minIdx) return false
    if (cats && !cats.has(ev.category)) return false
    if (needle) {
      const hay = (ev.message + ' ' + ev.category + ' ' + (ev.data ? JSON.stringify(ev.data) : '')).toLowerCase()
      if (!hay.includes(needle)) return false
    }
    return true
  })
}

/** Expose the diag object on window for manual inspection in DevTools. */
if (typeof window !== 'undefined') {
  try {
    ;(window as unknown as { __softphoneDiag?: Diag }).__softphoneDiag = diag
  } catch {
    /* ignore */
  }
}
