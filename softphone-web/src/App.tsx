import { Navigate, Route, Routes } from 'react-router-dom'
import { Toaster } from '@/components/ui/sonner'
import { AuthProvider } from '@/app/auth/AuthProvider'
import { RequireAuth } from '@/app/auth/RequireAuth'
import { LoginPage } from '@/pages/LoginPage'
import { ChangePasswordPage } from '@/pages/ChangePasswordPage'
import { SoftphonePage } from '@/pages/SoftphonePage'

export default function App() {
  return (
    <AuthProvider>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route
          path="/change-password"
          element={
            <RequireAuth>
              <ChangePasswordPage />
            </RequireAuth>
          }
        />
        <Route
          path="/"
          element={
            <RequireAuth>
              <SoftphonePage />
            </RequireAuth>
          }
        />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
      <Toaster />
    </AuthProvider>
  )
}
