/** Pure helpers behind the Overview dashboard: they turn a list of execution summaries into the numbers and chart buckets shown. */
import type { ExecutionSummaryDto } from '@/lib/api/generated'

const FAILED = new Set(['Failed', 'Rejected'])

export interface HourBucket {
  hour: number
  succeeded: number
  /** Failed (technical) and Rejected (data failed validation). */
  failed: number
  /** Queued, Running or AwaitingApproval. */
  open: number
}

export interface DashboardStats {
  today: number
  succeeded: number
  failed: number
  open: number
  /** Share of finished runs today that succeeded on the first attempt (0..1), or null when nothing finished. */
  firstTimeRate: number | null
  /** Median submit-to-complete time of today's succeeded runs, in ms, or null. */
  medianMs: number | null
  hours: HourBucket[]
}

export const startOfDay = (date: Date, daysBack = 0) => {
  const d = new Date(date)
  d.setHours(0, 0, 0, 0)
  d.setDate(d.getDate() - daysBack)
  return d
}

export function median(values: number[]): number | null {
  if (values.length === 0) return null
  const sorted = [...values].sort((a, b) => a - b)
  const mid = Math.floor(sorted.length / 2)
  return sorted.length % 2 ? sorted[mid]! : Math.round((sorted[mid - 1]! + sorted[mid]!) / 2)
}

export function computeStats(executions: ExecutionSummaryDto[], now: Date): DashboardStats {
  const dayStart = startOfDay(now).getTime()
  const todays = executions.filter((e) => new Date(e.submittedAt).getTime() >= dayStart)
  const hours: HourBucket[] = Array.from({ length: 24 }, (_, hour) => ({ hour, succeeded: 0, failed: 0, open: 0 }))

  let succeeded = 0
  let failed = 0
  let firstTime = 0
  const durations: number[] = []
  for (const e of todays) {
    const bucket = hours[new Date(e.submittedAt).getHours()]!
    if (e.status === 'Succeeded') {
      succeeded++
      bucket.succeeded++
      if (e.attempt <= 1) firstTime++
      if (e.completedAt) durations.push(Math.max(0, new Date(e.completedAt).getTime() - new Date(e.submittedAt).getTime()))
    } else if (FAILED.has(e.status)) {
      failed++
      bucket.failed++
    } else {
      bucket.open++
    }
  }

  const finished = succeeded + failed
  return {
    today: todays.length,
    succeeded,
    failed,
    open: todays.length - finished,
    firstTimeRate: finished === 0 ? null : firstTime / finished,
    medianMs: median(durations),
    hours,
  }
}

/** Runs per workflow key, most runs first. */
export function countByWorkflow(executions: ExecutionSummaryDto[]): { key: string; count: number }[] {
  const counts = new Map<string, number>()
  for (const e of executions) counts.set(e.workflowKey, (counts.get(e.workflowKey) ?? 0) + 1)
  return [...counts.entries()].map(([key, count]) => ({ key, count })).sort((a, b) => b.count - a.count || a.key.localeCompare(b.key))
}

export function greeting(now: Date): string {
  const h = now.getHours()
  return h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening'
}

export const formatPercent = (share: number) => `${(share * 100).toFixed(share === 1 || share === 0 ? 0 : 1)}%`
