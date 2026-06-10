import React, { createContext, useCallback, useEffect, useMemo, useState } from 'react'
import * as api from '@/app/api/client'
import type { Me } from '@/app/api/types'

type AuthState =
  | { status: 'loading'; me: null }
  | { status: 'anonymous'; me: null }
  | { status: 'authenticated'; me: Me }

type AuthContextValue = {
  state: AuthState
  refresh: () => Promise<void>
  login: (email: string, password: string) => Promise<void>
  logout: () => Promise<void>
  setMe: (me: Me) => void
}

export const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [state, setState] = useState<AuthState>({ status: 'loading', me: null })

  const setMe = useCallback((me: Me) => {
    setState({ status: 'authenticated', me })
  }, [])

  const refresh = useCallback(async () => {
    try {
      const me = await api.me()
      setState({ status: 'authenticated', me })
    } catch {
      setState({ status: 'anonymous', me: null })
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const login = useCallback(async (email: string, password: string) => {
    const r = await api.login(email, password)
    setState({ status: 'authenticated', me: r.me })
  }, [])

  const logout = useCallback(async () => {
    try {
      await api.logout()
    } finally {
      setState({ status: 'anonymous', me: null })
    }
  }, [])

  const value = useMemo<AuthContextValue>(
    () => ({ state, refresh, login, logout, setMe }),
    [state, refresh, login, logout, setMe],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

