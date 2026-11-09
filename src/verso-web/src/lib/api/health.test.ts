import { describe, expect, it, vi } from 'vitest'

import { fetchHealth, type HealthResponse } from './health'

const healthy: HealthResponse = {
  status: 'Healthy',
  totalDurationMs: 12.3,
  checks: [{ name: 'database', status: 'Healthy', description: 'Connected; schema is up to date.', durationMs: 11, data: { pendingMigrations: 0 } }],
}

const json = (body: unknown, status: number) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('fetchHealth', () => {
  it('calls the relative /api/health URL so the dev proxy and production host both work', async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(healthy, 200))
    vi.stubGlobal('fetch', fetchMock)

    await fetchHealth()

    expect(fetchMock).toHaveBeenCalledWith('/api/health', expect.anything())
  })

  it('returns the report when the API answers 200', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(healthy, 200)))

    expect(await fetchHealth()).toEqual({ kind: 'reported', health: healthy })
  })

  it('returns the report when the API answers 503 (Unhealthy still has a body)', async () => {
    const unhealthy = { ...healthy, status: 'Unhealthy' }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(unhealthy, 503)))

    const result = await fetchHealth()

    expect(result).toEqual({ kind: 'reported', health: unhealthy })
  })

  it('reports unreachable when the request fails', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    const result = await fetchHealth()

    expect(result.kind).toBe('unreachable')
  })

  it.each([502, 504])('treats a proxy %i (API process down) as not responding', async (status) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('Bad gateway', { status })))

    const result = await fetchHealth()

    expect(result).toEqual({ kind: 'unreachable', reason: 'The API did not respond. Is Verso.Api running?' })
  })

  it('reports other unexpected HTTP statuses with the status code', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('Not found', { status: 404 })))

    const result = await fetchHealth()

    expect(result).toEqual({ kind: 'unreachable', reason: 'Unexpected response from the API (HTTP 404).' })
  })

  it('rethrows aborts so a cancelled check does not overwrite newer state', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new DOMException('Aborted', 'AbortError')))

    await expect(fetchHealth()).rejects.toThrow('Aborted')
  })
})
