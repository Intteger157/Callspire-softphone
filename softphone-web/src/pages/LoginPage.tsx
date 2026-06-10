import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useAuth } from '@/app/auth/useAuth'

export function LoginPage() {
  const nav = useNavigate()
  const { login } = useAuth()

  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [loading, setLoading] = useState(false)

  const canSubmit = useMemo(() => email.trim().length > 3 && password.length > 0, [email, password])

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!canSubmit) return
    setLoading(true)
    try {
      await login(email.trim(), password)
      nav('/', { replace: true })
    } catch (err: any) {
      toast.error(err?.message || 'Login failed')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="min-h-full grid place-items-center p-4">
      <Card className="w-full max-w-md">
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <img src={`${import.meta.env.BASE_URL}favicon.ico`} alt="" className="h-6 w-6" />
            <span>Callspire Softphone</span>
          </CardTitle>
          <CardDescription>Sign in with the email and password provided by your administrator.</CardDescription>
        </CardHeader>
        <CardContent>
          <form className="grid gap-4" onSubmit={onSubmit}>
            <div className="grid gap-2">
              <Label htmlFor="email">Email</Label>
              <Input
                id="email"
                inputMode="email"
                autoComplete="username"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="user@company.com"
              />
            </div>
            <div className="grid gap-2">
              <Label htmlFor="password">Password</Label>
              <Input
                id="password"
                type="password"
                autoComplete="current-password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
            </div>
            <Button type="submit" disabled={!canSubmit || loading}>
              {loading ? 'Signing in…' : 'Sign in'}
            </Button>
          </form>
        </CardContent>
      </Card>
    </div>
  )
}

