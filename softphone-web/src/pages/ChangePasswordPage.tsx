import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { changePassword } from '@/app/api/client'
import { useAuth } from '@/app/auth/useAuth'

export function ChangePasswordPage() {
  const nav = useNavigate()
  const { state, refresh } = useAuth()
  const me = state.status === 'authenticated' ? state.me : null

  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [loading, setLoading] = useState(false)

  const canSubmit = useMemo(() => current.length > 0 && next.length >= 8, [current, next])

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!canSubmit) return
    setLoading(true)
    try {
      await changePassword(current, next)
      toast.success('Password updated')
      await refresh()
      nav('/', { replace: true })
    } catch (err: any) {
      toast.error(err?.message || 'Failed to change password')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="min-h-full grid place-items-center p-4">
      <Card className="w-full max-w-md">
        <CardHeader>
          <CardTitle>Change password</CardTitle>
          <CardDescription>
            {me?.email ? `Account: ${me.email}` : 'You must change your password to continue.'}
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form className="grid gap-4" onSubmit={onSubmit}>
            <div className="grid gap-2">
              <Label htmlFor="current">Current password</Label>
              <Input
                id="current"
                type="password"
                autoComplete="current-password"
                value={current}
                onChange={(e) => setCurrent(e.target.value)}
              />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="next">New password</Label>
              <Input
                id="next"
                type="password"
                autoComplete="new-password"
                value={next}
                onChange={(e) => setNext(e.target.value)}
              />
              <div className="text-xs text-muted-foreground">Minimum 8 characters.</div>
            </div>
            <Button type="submit" disabled={!canSubmit || loading}>
              {loading ? 'Saving…' : 'Update password'}
            </Button>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}

