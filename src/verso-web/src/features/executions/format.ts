import type { PillTone } from '@/components/StatusPill'

export const FINISHED_STATUSES = new Set(['Succeeded', 'Rejected', 'Failed'])

export const EXECUTION_STATUSES = ['Queued', 'Running', 'Succeeded', 'Rejected', 'Failed'] as const

export function statusTone(status: string): PillTone {
  switch (status) {
    case 'Succeeded':
      return 'ok'
    case 'Rejected':
      return 'warn'
    case 'Failed':
      return 'err'
    case 'Running':
      return 'run'
    default:
      return 'neutral'
  }
}

export const shortId = (id: string) => id.replace(/-/g, '').slice(-8)

const dateTime = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' })
const dateOnly = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric' })

export const formatDateTime = (iso: string) => dateTime.format(new Date(iso))
export const formatDate = (iso: string) => dateOnly.format(new Date(iso))

export function formatDuration(ms: number | null | undefined): string {
  if (ms == null) return '–'
  if (ms < 1000) return `${ms} ms`
  const seconds = ms / 1000
  return seconds < 60 ? `${seconds.toFixed(1)} s` : `${Math.floor(seconds / 60)} min ${Math.round(seconds % 60)} s`
}

export function runDuration(startedAt: string | null | undefined, completedAt: string | null | undefined): number | null {
  if (!startedAt || !completedAt) return null
  return Math.max(0, new Date(completedAt).getTime() - new Date(startedAt).getTime())
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  return bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

/** A local calendar day (from a date input, `YYYY-MM-DD`) as an ISO instant: its start, or the start of the next day. */
export function dayStartIso(day: string): string | undefined {
  return day ? new Date(`${day}T00:00:00`).toISOString() : undefined
}

export function dayEndIso(day: string): string | undefined {
  if (!day) return undefined
  const next = new Date(`${day}T00:00:00`)
  next.setDate(next.getDate() + 1)
  return next.toISOString()
}
