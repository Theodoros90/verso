import { Link } from 'react-router'

import { StatusPill } from '@/components/StatusPill'

import { describeHealth } from './healthDisplay'
import { useHealth } from './useHealth'

/** Compact API status for the top bar; links to the Overview page for details. */
export function HealthIndicator() {
  const { result } = useHealth()
  const { tone, label } = describeHealth(result)
  return (
    <Link to="/" aria-label={`API status: ${label}. Open overview.`} className="rounded-full">
      <StatusPill tone={tone}>API {label.toLowerCase()}</StatusPill>
    </Link>
  )
}
