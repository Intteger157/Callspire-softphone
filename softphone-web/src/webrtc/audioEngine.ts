/**
 * Shared audio utilities for the browser softphone: VU metering, test tone,
 * ringtone playback on an independent output device, hot-swap helpers, and
 * microphone probing.
 *
 * This module deliberately avoids any SIP/WebRTC state — it only operates on
 * `MediaStream`s and `HTMLAudioElement`s. The SIP client owns the call and
 * passes the active stream/peer-connection here when a hot-swap is needed.
 */

import { diag } from './diag'
import type { SipWebRtcClient, AudioProcessingOptions } from './sipWebRtc'

export type VuHandle = {
  /** 0..1 instantaneous RMS level (fast attack / slow release). */
  readonly level: () => number
  /** Subscribe to level updates (~20 Hz). Returns an unsubscribe fn. */
  subscribe: (cb: (level: number) => void) => () => void
  stop: () => void
}

export type TestToneOptions = {
  outputDeviceId?: string
  frequency?: number
  durationMs?: number
  /** 0..1 */
  gain?: number
}

export type RingtoneHandle = {
  stop: () => void
  /** Hot-swap the output device during playback. */
  setOutputDevice: (deviceId: string | undefined) => Promise<void>
}

let _sharedAudioCtx: AudioContext | null = null

function getAudioContext(): AudioContext {
  if (_sharedAudioCtx && _sharedAudioCtx.state !== 'closed') return _sharedAudioCtx
  const Ctor: typeof AudioContext =
    (window as unknown as { AudioContext?: typeof AudioContext }).AudioContext ||
    (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext!
  _sharedAudioCtx = new Ctor()
  return _sharedAudioCtx
}

/**
 * Resume the shared AudioContext on a user gesture. Browsers block auto-start.
 * Call this from a button/click handler at least once per session.
 */
export async function ensureAudioContextRunning(): Promise<void> {
  const ctx = getAudioContext()
  if (ctx.state === 'suspended') {
    try {
      await ctx.resume()
      diag.info('AUDIO', 'AudioContext resumed')
    } catch (err) {
      diag.warn('AUDIO', `AudioContext resume failed: ${(err as Error)?.message || err}`)
    }
  }
}

/**
 * Attach a VU meter to an existing MediaStream (local mic or remote audio).
 * The handle exposes an `unsubscribe`-style lifecycle — callers MUST call
 * `stop()` to release the AnalyserNode and the RAF timer.
 */
export function createVuMeter(stream: MediaStream): VuHandle {
  const ctx = getAudioContext()
  const source = ctx.createMediaStreamSource(stream)
  const analyser = ctx.createAnalyser()
  analyser.fftSize = 1024
  analyser.smoothingTimeConstant = 0.2
  source.connect(analyser)
  const buf = new Uint8Array(analyser.fftSize)
  let lastLevel = 0
  const listeners = new Set<(level: number) => void>()
  let raf = 0
  let stopped = false
  // Slow release to emulate a VU bar.
  const RELEASE = 0.85

  const tick = () => {
    if (stopped) return
    analyser.getByteTimeDomainData(buf)
    // RMS around 128 centerline.
    let sum = 0
    for (let i = 0; i < buf.length; i++) {
      const v = (buf[i] - 128) / 128
      sum += v * v
    }
    const rms = Math.sqrt(sum / buf.length)
    // Hold-with-release for a smoother UI.
    lastLevel = rms > lastLevel ? rms : lastLevel * RELEASE + rms * (1 - RELEASE)
    for (const cb of listeners) {
      try {
        cb(lastLevel)
      } catch {
        /* ignore */
      }
    }
    // ~20 Hz is plenty for a VU bar.
    raf = window.setTimeout(tick, 50) as unknown as number
  }
  tick()

  return {
    level: () => lastLevel,
    subscribe(cb) {
      listeners.add(cb)
      return () => listeners.delete(cb)
    },
    stop() {
      if (stopped) return
      stopped = true
      listeners.clear()
      clearTimeout(raf)
      try {
        source.disconnect()
      } catch {
        /* ignore */
      }
      try {
        analyser.disconnect()
      } catch {
        /* ignore */
      }
    },
  }
}

export type MicProbeHandle = VuHandle & {
  readonly stream: MediaStream
  /** The resolved settings from the chosen track. */
  readonly settings: MediaTrackSettings
  /** Play captured mic through the given speaker (loopback). Stop via returned fn. */
  playLoopback: (outputDeviceId?: string) => () => void
}

/**
 * Open a short-lived mic stream for testing. Remember to call `.stop()`.
 */
export async function probeMicrophone(
  deviceId: string | undefined,
  processing?: AudioProcessingOptions,
): Promise<MicProbeHandle> {
  await ensureAudioContextRunning()
  const constraints: MediaTrackConstraints & { deviceId?: ConstrainDOMString } = {
    echoCancellation: { ideal: processing?.echoCancellation ?? true } as ConstrainBoolean,
    noiseSuppression: { ideal: processing?.noiseSuppression ?? true } as ConstrainBoolean,
    autoGainControl: { ideal: processing?.autoGainControl ?? true } as ConstrainBoolean,
  }
  if (deviceId) constraints.deviceId = { exact: deviceId }
  const stream = await navigator.mediaDevices.getUserMedia({ audio: constraints, video: false })
  const track = stream.getAudioTracks()[0]
  const settings = track?.getSettings() || {}
  const vu = createVuMeter(stream)

  let loopbackEl: HTMLAudioElement | null = null

  const stop = () => {
    vu.stop()
    try {
      stream.getTracks().forEach((t) => t.stop())
    } catch {
      /* ignore */
    }
    if (loopbackEl) {
      try {
        loopbackEl.pause()
        loopbackEl.srcObject = null
        loopbackEl.remove()
      } catch {
        /* ignore */
      }
      loopbackEl = null
    }
  }

  diag.info('DEVICE', 'probeMicrophone: stream opened', {
    deviceId: settings.deviceId,
    label: track?.label,
  })

  return {
    stream,
    settings,
    level: vu.level,
    subscribe: vu.subscribe,
    stop,
    playLoopback(outputDeviceId?: string) {
      if (loopbackEl) return () => { /* already playing */ }
      const el = document.createElement('audio')
      el.autoplay = true
      el.muted = false
      el.setAttribute('playsinline', 'true')
      Object.assign(el.style, { position: 'absolute', width: '0', height: '0' })
      document.body.appendChild(el)
      el.srcObject = stream
      loopbackEl = el
      if (outputDeviceId) void applySinkId(el, outputDeviceId)
      void el.play().catch(() => {
        /* autoplay may block without gesture */
      })
      return () => {
        if (!loopbackEl) return
        try {
          loopbackEl.pause()
          loopbackEl.srcObject = null
          loopbackEl.remove()
        } catch {
          /* ignore */
        }
        loopbackEl = null
      }
    },
  }
}

async function applySinkId(el: HTMLMediaElement, deviceId: string): Promise<void> {
  const fn = (el as HTMLMediaElement & { setSinkId?: (id: string) => Promise<void> }).setSinkId
  if (typeof fn !== 'function') {
    diag.warn('AUDIO', 'setSinkId not supported by browser')
    return
  }
  try {
    await fn.call(el, deviceId)
  } catch (err) {
    diag.warn('AUDIO', `setSinkId failed: ${(err as Error)?.message || err}`)
  }
}

/**
 * Play a short sine tone through the given speaker. Returns a Promise that
 * resolves when the tone has finished playing (or is stopped).
 */
export async function playTestTone(opts: TestToneOptions = {}): Promise<void> {
  await ensureAudioContextRunning()
  const ctx = getAudioContext()
  const freq = opts.frequency ?? 440
  const dur = opts.durationMs ?? 700
  const gain = opts.gain ?? 0.2

  // Render tone into a MediaStream so we can pipe it through an <audio>
  // element that supports setSinkId (plain AudioContext.destination cannot
  // target a specific output device).
  const osc = ctx.createOscillator()
  osc.type = 'sine'
  osc.frequency.value = freq
  const g = ctx.createGain()
  g.gain.value = gain
  const dest = ctx.createMediaStreamDestination()
  osc.connect(g)
  g.connect(dest)

  const el = document.createElement('audio')
  el.autoplay = true
  el.setAttribute('playsinline', 'true')
  Object.assign(el.style, { position: 'absolute', width: '0', height: '0' })
  document.body.appendChild(el)
  el.srcObject = dest.stream

  if (opts.outputDeviceId) await applySinkId(el, opts.outputDeviceId)

  diag.info('AUDIO', `playTestTone: ${freq} Hz ${dur} ms → ${opts.outputDeviceId || 'default'}`)

  osc.start()
  await el.play().catch(() => { /* autoplay */ })

  await new Promise<void>((resolve) => setTimeout(resolve, dur))
  try {
    osc.stop()
  } catch {
    /* ignore */
  }
  try {
    el.pause()
    el.srcObject = null
    el.remove()
  } catch {
    /* ignore */
  }
}

/**
 * Start a looping ringtone on an independent output device (so the ringtone
 * can go to e.g. laptop speakers while the call audio stays on a headset).
 */
export function startRingtone(opts: {
  outputDeviceId?: string
  /** Volume 0..1; default 0.35. */
  gain?: number
  /** Ringing cadence: tone+silence pattern in ms. Default classic ITU cadence. */
  pattern?: number[]
  /** Base frequency (Hz) — default 440/480 dual (ITU pattern). */
  frequencies?: [number, number]
}): RingtoneHandle {
  const ctx = getAudioContext()
  const g = ctx.createGain()
  g.gain.value = opts.gain ?? 0.35

  const [f1, f2] = opts.frequencies ?? [440, 480]
  const osc1 = ctx.createOscillator()
  const osc2 = ctx.createOscillator()
  osc1.type = 'sine'
  osc2.type = 'sine'
  osc1.frequency.value = f1
  osc2.frequency.value = f2

  const toneGain = ctx.createGain()
  toneGain.gain.value = 0
  osc1.connect(toneGain)
  osc2.connect(toneGain)
  toneGain.connect(g)

  const dest = ctx.createMediaStreamDestination()
  g.connect(dest)

  const el = document.createElement('audio')
  el.autoplay = true
  el.loop = false
  el.setAttribute('playsinline', 'true')
  Object.assign(el.style, { position: 'absolute', width: '0', height: '0' })
  document.body.appendChild(el)
  el.srcObject = dest.stream
  if (opts.outputDeviceId) void applySinkId(el, opts.outputDeviceId)

  const pattern = opts.pattern ?? [400, 200, 400, 2000] // on-off-on-off
  let idx = 0
  let timer: number | null = null
  let stopped = false
  const step = () => {
    if (stopped) return
    const d = pattern[idx % pattern.length]
    const on = idx % 2 === 0
    const now = ctx.currentTime
    toneGain.gain.cancelScheduledValues(now)
    toneGain.gain.setValueAtTime(toneGain.gain.value, now)
    toneGain.gain.linearRampToValueAtTime(on ? 1 : 0, now + 0.03)
    idx++
    timer = window.setTimeout(step, d)
  }
  osc1.start()
  osc2.start()
  void el.play().catch(() => { /* autoplay */ })
  step()

  diag.info('AUDIO', `startRingtone → ${opts.outputDeviceId || 'default'}`)

  return {
    stop() {
      if (stopped) return
      stopped = true
      if (timer != null) clearTimeout(timer)
      try {
        osc1.stop()
      } catch {
        /* ignore */
      }
      try {
        osc2.stop()
      } catch {
        /* ignore */
      }
      try {
        el.pause()
        el.srcObject = null
        el.remove()
      } catch {
        /* ignore */
      }
      diag.debug('AUDIO', 'ringtone stopped')
    },
    async setOutputDevice(deviceId) {
      if (!deviceId) return
      await applySinkId(el, deviceId)
    },
  }
}

/**
 * Convenience wrapper that delegates to `SipWebRtcClient.replaceInputDevice`
 * so UI code has a single surface for hot-swapping the mic during a call.
 */
export async function hotSwapMicrophone(
  sip: SipWebRtcClient,
  deviceId: string | undefined,
  processing?: AudioProcessingOptions,
): Promise<MediaStreamTrack | null> {
  return sip.replaceInputDevice(deviceId, processing)
}

/** Fetch current list of audio input/output devices. Empty labels mean the user hasn't granted permission yet. */
export async function listAudioDevices(): Promise<{
  inputs: MediaDeviceInfo[]
  outputs: MediaDeviceInfo[]
}> {
  if (!navigator.mediaDevices?.enumerateDevices) return { inputs: [], outputs: [] }
  const all = await navigator.mediaDevices.enumerateDevices()
  return {
    inputs: all.filter((d) => d.kind === 'audioinput'),
    outputs: all.filter((d) => d.kind === 'audiooutput'),
  }
}
