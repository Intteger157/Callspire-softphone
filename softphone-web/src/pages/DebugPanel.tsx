/**
 * Live WebRTC / SIP / audio diagnostic panel.
 *
 * Subscribes to the in-memory ring buffer in `src/webrtc/diag.ts` and renders
 * recent events with category/level/search filters. Use the "Download" button
 * to export a `diagnostics.json` dump (buffer + snapshot) that can be attached
 * to a bug report. No network IO — everything is local to the browser tab.
 */

import { useEffect, useMemo, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import {
  diag,
  filterEvents,
  formatDiagTime,
  DIAG_CATEGORIES,
  DIAG_LEVELS,
  type DiagCategory,
  type DiagEvent,
  type DiagLevel,
} from '@/webrtc/diag'
import { setJsSipVerbose } from '@/webrtc/sipWebRtc'

const LEVEL_COLOR: Record<DiagLevel, string> = {
  trace: 'text-muted-foreground/70',
  debug: 'text-muted-foreground',
  info: 'text-foreground',
  warn: 'text-amber-500',
  error: 'text-red-500',
}

const CATEGORY_COLOR: Record<DiagCategory, string> = {
  SIP: 'text-sky-500',
  WSS: 'text-cyan-500',
  ICE: 'text-purple-500',
  PC: 'text-indigo-500',
  STATS: 'text-slate-500',
  AUDIO: 'text-emerald-500',
  DEVICE: 'text-teal-500',
  UI: 'text-muted-foreground',
  ERROR: 'text-red-500',
}

const MAX_RENDER = 600

export function DebugPanel(props: { open: boolean; onClose: () => void }) {
  const { open, onClose } = props
  const [events, setEvents] = useState<DiagEvent[]>(() => diag.getAll())
  const [minLevel, setMinLevel] = useState<DiagLevel>('debug')
  const [cats, setCats] = useState<Set<DiagCategory>>(new Set(DIAG_CATEGORIES))
  const [search, setSearch] = useState('')
  const [paused, setPaused] = useState(false)
  const [verbose, setVerbose] = useState(false)
  const [autoscroll, setAutoscroll] = useState(true)
  const listRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    if (!open) return
    // Refresh once on open so the panel picks up events that arrived before
    // this subscription was installed. We do it via `queueMicrotask` so the
    // lint rule about "setState synchronously in an effect" is satisfied while
    // still catching those events on the very next tick.
    queueMicrotask(() => {
      setEvents(diag.getAll())
    })
    const unsub = diag.subscribe(() => {
      if (paused) return
      setEvents(diag.getAll())
    })
    return () => {
      unsub()
    }
  }, [open, paused])

  useEffect(() => {
    if (!open || !autoscroll) return
    const el = listRef.current
    if (!el) return
    el.scrollTop = el.scrollHeight
  }, [events, open, autoscroll])

  const filtered = useMemo(() => {
    const list = filterEvents(events, {
      minLevel,
      categories: Array.from(cats),
      search,
    })
    // Render only the tail for responsiveness; the full buffer is still in memory.
    return list.length > MAX_RENDER ? list.slice(list.length - MAX_RENDER) : list
  }, [events, minLevel, cats, search])

  if (!open) return null

  const toggleCat = (c: DiagCategory) => {
    setCats((prev) => {
      const next = new Set(prev)
      if (next.has(c)) next.delete(c)
      else next.add(c)
      return next
    })
  }

  const copyVisible = async () => {
    const text = filtered
      .map(
        (e) =>
          `${formatDiagTime(e.ts)} ${e.level.toUpperCase().padEnd(5)} ${e.category.padEnd(6)} ${e.message}${
            e.data !== undefined ? ' ' + JSON.stringify(e.data) : ''
          }`,
      )
      .join('\n')
    try {
      await navigator.clipboard.writeText(text)
    } catch {
      // Fall back to a hidden textarea when clipboard-API is blocked.
      const ta = document.createElement('textarea')
      ta.value = text
      document.body.appendChild(ta)
      ta.select()
      try {
        document.execCommand('copy')
      } catch {
        /* ignore */
      }
      ta.remove()
    }
  }

  const onDownload = () => {
    diag.downloadDump()
  }

  const onClear = () => {
    diag.clear()
    setEvents([])
  }

  const onToggleVerbose = (v: boolean) => {
    setVerbose(v)
    setJsSipVerbose(v)
  }

  const bufferCount = events.length

  return (
    <div
      role="dialog"
      aria-label="Debug panel"
      aria-modal="true"
      className="fixed inset-0 z-[120] flex bg-background/80 p-4 backdrop-blur-sm"
    >
      <div className="m-auto flex h-[86vh] w-[min(1100px,95vw)] flex-col overflow-hidden rounded-lg border bg-card text-card-foreground shadow-2xl">
        <div className="flex items-center justify-between border-b border-border p-3">
          <div className="flex items-center gap-3">
            <div className="text-sm font-semibold">WebRTC Debug</div>
            <span className="text-xs text-muted-foreground">
              {bufferCount} events {paused ? '· paused' : ''}
            </span>
          </div>
          <Button variant="ghost" size="sm" onClick={onClose} aria-label="Close debug panel">
            Close
          </Button>
        </div>

        <div className="flex flex-wrap items-end gap-2 border-b border-border p-3">
          <div className="flex flex-col gap-1">
            <Label className="text-[11px] text-muted-foreground">Min level</Label>
            <Select value={minLevel} onValueChange={(v) => setMinLevel(v as DiagLevel)}>
              <SelectTrigger className="h-8 w-[110px] text-xs">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {DIAG_LEVELS.map((l) => (
                  <SelectItem key={l} value={l}>
                    {l}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <div className="flex flex-1 flex-col gap-1">
            <Label className="text-[11px] text-muted-foreground">Search</Label>
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Filter by substring (message or JSON data)"
              className="h-8 text-xs"
            />
          </div>

          <label className="flex items-center gap-2 text-xs">
            <input
              type="checkbox"
              checked={autoscroll}
              onChange={(e) => setAutoscroll(e.target.checked)}
            />
            Auto-scroll
          </label>
          <label className="flex items-center gap-2 text-xs">
            <input
              type="checkbox"
              checked={verbose}
              onChange={(e) => onToggleVerbose(e.target.checked)}
            />
            Verbose (JsSIP)
          </label>
        </div>

        <div className="flex flex-wrap gap-1.5 border-b border-border p-2">
          {DIAG_CATEGORIES.map((c) => {
            const on = cats.has(c)
            return (
              <button
                key={c}
                type="button"
                onClick={() => toggleCat(c)}
                aria-pressed={on}
                className={[
                  'rounded-full border px-2.5 py-0.5 text-[11px] transition',
                  on
                    ? 'border-foreground/30 bg-muted text-foreground'
                    : 'border-transparent bg-muted/30 text-muted-foreground opacity-60 hover:opacity-100',
                  CATEGORY_COLOR[c],
                ].join(' ')}
              >
                {c}
              </button>
            )
          })}
          <div className="flex-1" />
          <Button size="sm" variant="ghost" onClick={() => setCats(new Set(DIAG_CATEGORIES))}>
            All
          </Button>
          <Button size="sm" variant="ghost" onClick={() => setCats(new Set())}>
            None
          </Button>
        </div>

        <div
          ref={listRef}
          className="flex-1 overflow-auto bg-black/85 p-2 font-mono text-[12px] leading-[1.35] text-zinc-100 dark:bg-black/60"
          onScroll={(e) => {
            const el = e.currentTarget
            const atBottom = el.scrollHeight - el.clientHeight - el.scrollTop < 24
            if (!atBottom && autoscroll) setAutoscroll(false)
          }}
        >
          {filtered.length === 0 ? (
            <div className="p-2 text-xs text-muted-foreground">No events match the current filter.</div>
          ) : (
            filtered.map((e) => (
              <div key={e.id} className="flex gap-2 whitespace-pre-wrap break-words">
                <span className="shrink-0 text-zinc-400">{formatDiagTime(e.ts)}</span>
                <span className={`shrink-0 w-12 uppercase ${LEVEL_COLOR[e.level]}`}>{e.level}</span>
                <span className={`shrink-0 w-14 ${CATEGORY_COLOR[e.category]}`}>{e.category}</span>
                <span className="flex-1">
                  <span>{e.message}</span>
                  {e.data !== undefined ? (
                    <span className="text-zinc-400"> {safeJson(e.data)}</span>
                  ) : null}
                </span>
              </div>
            ))
          )}
        </div>

        <div className="flex flex-wrap items-center gap-2 border-t border-border p-3">
          <Button size="sm" variant="outline" onClick={() => setPaused((p) => !p)}>
            {paused ? 'Resume' : 'Pause'}
          </Button>
          <Button size="sm" variant="outline" onClick={onClear}>
            Clear
          </Button>
          <Button size="sm" variant="outline" onClick={() => void copyVisible()}>
            Copy visible
          </Button>
          <Button size="sm" onClick={onDownload}>
            Download diagnostics.json
          </Button>
          <div className="flex-1" />
          <span className="text-[11px] text-muted-foreground">
            Ring-buffer size: {diag.getCapacity()} · In buffer: {bufferCount}
          </span>
        </div>
      </div>
    </div>
  )
}

function safeJson(v: unknown): string {
  try {
    const s = JSON.stringify(v)
    return s.length > 400 ? s.slice(0, 400) + '…' : s
  } catch {
    return String(v)
  }
}
