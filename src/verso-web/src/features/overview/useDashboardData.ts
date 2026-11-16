import { useCallback, useEffect, useState } from 'react'

import { executionsApi } from '@/lib/api/executions'
import type { ExecutionSummaryDto, TemplateSummaryDto } from '@/lib/api/generated'
import { templatesApi } from '@/lib/api/templates'
import { workflowsApi } from '@/lib/api/workflows'
import { watchExecutions } from '@/lib/realtime'

import { startOfDay } from './dashboardStats'

const PAGE = 100
/** At most this many pages of the last 7 days are read; the dashboard says so when there were more runs than that. */
const MAX_PAGES = 5
const REFRESH_MS = 30_000
const ATTENTION_ITEMS = 4

export interface AttentionList {
  total: number
  items: ExecutionSummaryDto[]
}

export interface DashboardData {
  /** Runs submitted in the last 7 days (newest first), possibly only the newest part of them. */
  week: ExecutionSummaryDto[]
  weekTotal: number
  failed: AttentionList
  awaiting: AttentionList
  templates: TemplateSummaryDto[]
  templateTotal: number
  workflowTotal: number
  loadedAt: Date
}

export type DashboardState = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; data: DashboardData }

async function readWeek(from: string, signal: AbortSignal) {
  const items: ExecutionSummaryDto[] = []
  let total = 0
  for (let page = 1; page <= MAX_PAGES; page++) {
    const result = await executionsApi.list({ from }, page, PAGE, signal)
    total = result.total
    items.push(...result.items)
    if (items.length >= total || result.items.length < PAGE) break
  }
  return { items, total }
}

/** Loads everything the Overview shows from the existing endpoints, and refreshes on notifications and slowly on a timer. */
export function useDashboardData() {
  const [state, setState] = useState<DashboardState>({ kind: 'loading' })
  const [tick, setTick] = useState(0)
  const refresh = useCallback(() => setTick((t) => t + 1), [])

  useEffect(() => {
    const controller = new AbortController()
    const { signal } = controller
    const load = async () => {
      try {
        const from = startOfDay(new Date(), 6).toISOString()
        const [week, failed, awaiting, templates, workflows] = await Promise.all([
          readWeek(from, signal),
          executionsApi.list({ status: 'Failed' }, 1, ATTENTION_ITEMS, signal),
          executionsApi.list({ status: 'AwaitingApproval' }, 1, ATTENTION_ITEMS, signal),
          templatesApi.list(1, PAGE, signal),
          workflowsApi.list(1, 1, signal),
        ])
        if (signal.aborted) return
        setState({
          kind: 'ready',
          data: {
            week: week.items,
            weekTotal: week.total,
            failed: { total: failed.total, items: failed.items },
            awaiting: { total: awaiting.total, items: awaiting.items },
            templates: [...templates.items].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)),
            templateTotal: templates.total,
            workflowTotal: workflows.total,
            loadedAt: new Date(),
          },
        })
      } catch (e) {
        if (e instanceof DOMException && e.name === 'AbortError') return
        // Keep showing the last good numbers when a refresh fails.
        setState((s) => (s.kind === 'ready' ? s : { kind: 'error', message: e instanceof Error ? e.message : 'Could not load the dashboard.' }))
      }
    }
    void load()

    const timer = setInterval(() => void load(), REFRESH_MS)
    let debounce: ReturnType<typeof setTimeout> | undefined
    let unwatch: (() => void) | undefined
    void watchExecutions({ kind: 'all' }, () => {
      clearTimeout(debounce)
      debounce = setTimeout(() => void load(), 500)
    })
      .then((stop) => {
        if (signal.aborted) stop()
        else unwatch = stop
      })
      .catch(() => undefined)

    return () => {
      controller.abort()
      clearInterval(timer)
      clearTimeout(debounce)
      unwatch?.()
    }
  }, [tick])

  return { state, refresh }
}
