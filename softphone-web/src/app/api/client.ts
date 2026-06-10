import { apiFetch } from './http'
import type {
  CdrResponse,
  LoginResponse,
  Me,
  MyCallerIds,
  OriginateResponse,
  WebRtcConfig,
} from './types'

// These endpoints are expected to be provided by your BFF under the same origin
// (e.g. https://host/softphone -> browser, https://host/api/... -> BFF routes).

export function login(email: string, password: string) {
  return apiFetch<LoginResponse>('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({ email, password }),
  })
}

export function logout() {
  return apiFetch<{ ok: true }>('/api/auth/logout', { method: 'POST' })
}

export function me() {
  return apiFetch<Me>('/api/me', { method: 'GET' })
}

export function changePassword(current_password: string, new_password: string) {
  return apiFetch<{ success: true }>('/api/me/change-password', {
    method: 'POST',
    body: JSON.stringify({ current_password, new_password }),
  })
}

export function myCallerIds() {
  return apiFetch<MyCallerIds>('/api/my-callerids', { method: 'GET' })
}

export function originate(destination: string, callerid: string) {
  return apiFetch<OriginateResponse>('/api/originate', {
    method: 'POST',
    body: JSON.stringify({ destination, callerid }),
  })
}

export function cdr(params: { limit?: number; offset?: number } = {}) {
  const usp = new URLSearchParams()
  if (params.limit != null) usp.set('limit', String(params.limit))
  if (params.offset != null) usp.set('offset', String(params.offset))
  const q = usp.toString()
  return apiFetch<CdrResponse>(`/api/cdr${q ? `?${q}` : ''}`, { method: 'GET' })
}

export function getWebRtcConfig() {
  return apiFetch<WebRtcConfig>('/api/webrtc/config', { method: 'GET' })
}

export type UserPreferences = {
  micId?: string
  speakerId?: string
  ringtoneDeviceId?: string
  aec?: boolean
  ns?: boolean
  agc?: boolean
  ringtoneEnabled?: boolean
}

export function getPreferences() {
  return apiFetch<{ preferences: UserPreferences }>('/api/me/preferences', { method: 'GET' })
}

export function putPreferences(patch: Partial<UserPreferences>) {
  return apiFetch<{ preferences: UserPreferences }>('/api/me/preferences', {
    method: 'PUT',
    body: JSON.stringify(patch),
  })
}

