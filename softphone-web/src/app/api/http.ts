export type ApiError = {
  status: number
  message: string
  details?: unknown
}

async function readErrorBody(res: Response): Promise<unknown> {
  const ct = res.headers.get('content-type') || ''
  try {
    if (ct.includes('application/json')) return await res.json()
    return await res.text()
  } catch {
    return undefined
  }
}

export async function apiFetch<T>(
  path: string,
  init: RequestInit = {},
): Promise<T> {
  const res = await fetch(path, {
    credentials: 'include',
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init.headers || {}),
    },
  })

  if (!res.ok) {
    const body = await readErrorBody(res)
    const msg =
      typeof body === 'string'
        ? body
        : (body as any)?.message || res.statusText || 'Request failed'
    const err: ApiError = { status: res.status, message: msg, details: body }
    throw err
  }

  if (res.status === 204) return undefined as T
  const ct = res.headers.get('content-type') || ''
  if (ct.includes('application/json')) return (await res.json()) as T
  // fallback (shouldn't happen for our API)
  return (await res.text()) as unknown as T
}

