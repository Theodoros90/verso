/**
 * Client for GET /api/health. Types mirror Verso.Api.Health.HealthResponse; see docs/api.md.
 * (Generated OpenAPI types replace these hand-written ones once the API has real resources.)
 */

export type HealthStatus = 'Healthy' | 'Degraded' | 'Unhealthy'

export interface HealthCheckEntry {
  name: string
  status: HealthStatus
  description: string | null
  durationMs: number
  data: Record<string, unknown> | null
}

export interface HealthResponse {
  status: HealthStatus
  totalDurationMs: number
  checks: HealthCheckEntry[]
}

/** What the UI shows: the API answered (any status), or it couldn't be reached at all. */
export type HealthResult =
  | { kind: 'reported'; health: HealthResponse }
  | { kind: 'unreachable'; reason: string }

const NOT_RESPONDING = 'The API did not respond. Is Verso.Api running?'

export async function fetchHealth(signal?: AbortSignal): Promise<HealthResult> {
  let response: Response
  try {
    response = await fetch('/api/health', { signal, headers: { Accept: 'application/json' } })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    return { kind: 'unreachable', reason: NOT_RESPONDING }
  }

  // 502/504 come from a proxy in front of the API (Vite in development, IIS later) when the API is down.
  if (response.status === 502 || response.status === 504) {
    return { kind: 'unreachable', reason: NOT_RESPONDING }
  }

  // 200 = Healthy/Degraded, 503 = Unhealthy; both carry a HealthResponse body.
  if (response.status === 200 || response.status === 503) {
    try {
      return { kind: 'reported', health: (await response.json()) as HealthResponse }
    } catch {
      return { kind: 'unreachable', reason: `The API returned an unreadable response (HTTP ${response.status}).` }
    }
  }

  return { kind: 'unreachable', reason: `Unexpected response from the API (HTTP ${response.status}).` }
}
