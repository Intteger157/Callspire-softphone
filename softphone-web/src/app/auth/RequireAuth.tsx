import React, { useEffect } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useAuth } from './useAuth'

export function RequireAuth({ children }: { children: React.ReactNode }) {
  const { state } = useAuth()
  const nav = useNavigate()
  const loc = useLocation()

  useEffect(() => {
    if (state.status === 'anonymous') {
      nav('/login', { replace: true, state: { from: loc.pathname } })
    }
  }, [state.status, nav, loc.pathname])

  if (state.status === 'loading') {
    return (
      <div className="min-h-full grid place-items-center">
        <div className="text-sm text-muted-foreground">Loading…</div>
      </div>
    )
  }

  if (state.status !== 'authenticated') return null
  return <>{children}</>
}

