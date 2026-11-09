import type { PillTone } from '@/components/StatusPill'
import type { HealthResult, HealthStatus } from '@/lib/api/health'

export interface StatusDisplay {
  tone: PillTone
  label: string
}

/** Pill tone and label for one health status reported by the API. */
export function describeStatus(status: HealthStatus): StatusDisplay {
  switch (status) {
    case 'Healthy':
      return { tone: 'ok', label: 'Healthy' }
    case 'Degraded':
      return { tone: 'warn', label: 'Degraded' }
    default:
      return { tone: 'err', label: 'Unhealthy' }
  }
}

/** Pill tone and label for the overall result, including "still checking" and "API unreachable". */
export function describeHealth(result: HealthResult | null): StatusDisplay {
  if (!result) return { tone: 'run', label: 'Checking' }
  if (result.kind === 'unreachable') return { tone: 'err', label: 'Unreachable' }
  return describeStatus(result.health.status)
}
