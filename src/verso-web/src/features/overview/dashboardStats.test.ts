import { describe, expect, it } from 'vitest'

import type { ExecutionSummaryDto } from '@/lib/api/generated'

import { computeStats, countByWorkflow, formatPercent, greeting, median } from './dashboardStats'

const NOW = new Date(2026, 10, 16, 14, 30) // 16 Nov 2026, local time

const run = (status: string, hour: number, extra: Partial<ExecutionSummaryDto> = {}): ExecutionSummaryDto => {
  const submitted = new Date(2026, 10, 16, hour, 0, 0)
  return {
    id: `${status}-${hour}-${Math.random()}`,
    status,
    workflowKey: 'invoice-standard',
    workflowVersion: 1,
    correlationId: 'c',
    submittedAt: submitted.toISOString(),
    completedAt: status === 'Queued' || status === 'Running' ? null : new Date(submitted.getTime() + 4000).toISOString(),
    attempt: 1,
    failureCode: null,
    failureMessage: null,
    ...extra,
  }
}

describe('computeStats', () => {
  it('counts only today, buckets by hour and outcome, and reports first-time success and median time', () => {
    const yesterday = run('Succeeded', 10, { submittedAt: new Date(2026, 10, 15, 10).toISOString() })
    const stats = computeStats(
      [yesterday, run('Succeeded', 9), run('Succeeded', 9, { attempt: 2 }), run('Failed', 9), run('Rejected', 13), run('Running', 14), run('AwaitingApproval', 14)],
      NOW,
    )

    expect(stats.today).toBe(6)
    expect(stats.succeeded).toBe(2)
    expect(stats.failed).toBe(2)
    expect(stats.open).toBe(2)
    expect(stats.firstTimeRate).toBeCloseTo(1 / 4)
    expect(stats.medianMs).toBe(4000)
    expect(stats.hours[9]).toMatchObject({ succeeded: 2, failed: 1, open: 0 })
    expect(stats.hours[13]!.failed).toBe(1)
    expect(stats.hours[14]!.open).toBe(2)
    expect(stats.hours.reduce((n, h) => n + h.succeeded + h.failed + h.open, 0)).toBe(6)
  })

  it('has no rate or median when nothing finished', () => {
    const stats = computeStats([run('Running', 8)], NOW)
    expect(stats.firstTimeRate).toBeNull()
    expect(stats.medianMs).toBeNull()
    expect(computeStats([], NOW).today).toBe(0)
  })
})

describe('helpers', () => {
  it('median handles odd, even and empty lists', () => {
    expect(median([])).toBeNull()
    expect(median([5, 1, 3])).toBe(3)
    expect(median([1, 2, 3, 10])).toBe(3)
  })

  it('counts runs per workflow, most first', () => {
    const rows = countByWorkflow([run('Succeeded', 1, { workflowKey: 'b' }), run('Succeeded', 1, { workflowKey: 'a' }), run('Failed', 1, { workflowKey: 'a' })])
    expect(rows).toEqual([
      { key: 'a', count: 2 },
      { key: 'b', count: 1 },
    ])
  })

  it('greets by time of day and formats percentages', () => {
    expect(greeting(new Date(2026, 10, 16, 8))).toBe('Good morning')
    expect(greeting(new Date(2026, 10, 16, 14))).toBe('Good afternoon')
    expect(greeting(new Date(2026, 10, 16, 20))).toBe('Good evening')
    expect(formatPercent(1)).toBe('100%')
    expect(formatPercent(0.9982)).toBe('99.8%')
  })
})
