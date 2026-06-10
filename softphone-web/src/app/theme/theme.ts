export type ThemeMode = 'system' | 'light' | 'dark'

const KEY = 'callspire_theme'

function prefersDark() {
  return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches
}

export function getThemeMode(): ThemeMode {
  const v = localStorage.getItem(KEY)
  if (v === 'light' || v === 'dark' || v === 'system') return v
  return 'system'
}

export function setThemeMode(mode: ThemeMode) {
  localStorage.setItem(KEY, mode)
  applyThemeMode(mode)
}

export function applyThemeMode(mode: ThemeMode) {
  const root = document.documentElement
  const isDark = mode === 'dark' || (mode === 'system' && prefersDark())
  root.classList.toggle('dark', isDark)
}

export function initTheme() {
  const mode = getThemeMode()
  applyThemeMode(mode)

  // Keep in sync when system theme changes (only in system mode).
  if (window.matchMedia) {
    const mq = window.matchMedia('(prefers-color-scheme: dark)')
    mq.addEventListener?.('change', () => {
      if (getThemeMode() === 'system') applyThemeMode('system')
    })
  }
}

