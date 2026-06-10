import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { BrowserRouter } from 'react-router-dom'
import { initTheme } from '@/app/theme/theme'

initTheme()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter basename="/softphone">
      <App />
    </BrowserRouter>
  </StrictMode>,
)
