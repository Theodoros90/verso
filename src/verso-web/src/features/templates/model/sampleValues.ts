/**
 * Resolves sample values for the canvas's "Sample data" view. This is an approximation for editing;
 * the HTML preview from the server renderer is the faithful output (plan D2).
 */
import type { Filter } from './layout'

export function resolvePath(data: unknown, path: string): unknown {
  let current: unknown = data
  for (const part of path.split('.')) {
    if (current === null || typeof current !== 'object' || Array.isArray(current)) return undefined
    current = (current as Record<string, unknown>)[part]
  }
  return current ?? undefined
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** Formats a scalar like the server's filters (English, EUR). Returns undefined when there is no usable value. */
export function formatValue(value: unknown, filter?: Filter): string | undefined {
  if (value === undefined || value === null || typeof value === 'object') return undefined
  const raw = String(value)
  switch (filter) {
    case 'money': {
      const n = Number(value)
      if (!Number.isFinite(n)) return raw
      const abs = Math.abs(n).toLocaleString('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
      return `${n < 0 ? '-' : ''}€${abs}`
    }
    case 'number': {
      const n = Number(value)
      return Number.isFinite(n) ? n.toLocaleString('en-GB', { maximumFractionDigits: 10 }) : raw
    }
    case 'date':
    case 'year':
    case 'month':
    case 'day': {
      const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(raw)
      if (!m) return raw
      if (filter === 'year') return m[1]
      if (filter === 'month') return m[2]
      if (filter === 'day') return m[3]
      return `${m[3]} ${MONTHS[Number(m[2]) - 1] ?? m[2]} ${m[1]}`
    }
    case 'upper':
      return raw.toUpperCase()
    case 'lower':
      return raw.toLowerCase()
    default:
      return raw
  }
}
