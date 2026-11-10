import { useCallback, useEffect, useRef, useState } from 'react'

import { executionsApi } from '@/lib/api/executions'
import type { ExecutionDto, ExecutionEventDto } from '@/lib/api/generated'
import { watchExecutions } from '@/lib/realtime'

import { FINISHED_STATUSES } from './format'

/** Safety net while a run is unfinished, in case notifications don't arrive (blocked WebSockets, a missed message). */
export const LIVE_POLL_MS = 3000

/** Adds events by id: no duplicates, oldest first. Re-reading overlapping ranges (after a reconnect) is therefore harmless. */
export function mergeEvents(existing: readonly ExecutionEventDto[], incoming: readonly ExecutionEventDto[]): ExecutionEventDto[] {
  const byId = new Map(existing.map((e) => [e.id, e]))
  for (const event of incoming) byId.set(event.id, event)
  return [...byId.values()].sort((a, b) => a.id - b.id)
}

export type LiveState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string; notFound: boolean }
  | { kind: 'ready'; execution: ExecutionDto; events: ExecutionEventDto[] }

/**
 * One execution kept current. The REST API is the source of truth: a notification (or the slow poll) only makes this
 * read again, and events are fetched "after the last one seen", so a reconnect or a missed message can't lose steps.
 */
export function useExecutionLive(id: string) {
  const [state, setState] = useState<LiveState>({ kind: 'loading' })
  const reload = useRef<() => void>(() => undefined)

  useEffect(() => {
    const controller = new AbortController()
    let lastEventId = 0
    let finished = false
    let inFlight = false
    let again = false
    let events: ExecutionEventDto[] = []

    const load = async () => {
      if (inFlight) {
        again = true // a read is running; read once more when it ends, so nothing that happened meanwhile is missed
        return
      }
      inFlight = true
      try {
        do {
          again = false
          const [execution, incoming] = await Promise.all([
            executionsApi.get(id, controller.signal),
            executionsApi.events(id, lastEventId, controller.signal),
          ])
          events = mergeEvents(events, incoming)
          lastEventId = events.at(-1)?.id ?? lastEventId
          finished = FINISHED_STATUSES.has(execution.status)
          setState({ kind: 'ready', execution, events })
        } while (again && !controller.signal.aborted)
      } catch (e) {
        if (e instanceof DOMException && e.name === 'AbortError') return
        const status = (e as { status?: number }).status
        if (status === 404) finished = true // nothing to wait for
        // Keep showing what we have when a refresh fails; only a first load can fail the page.
        setState((current) =>
          current.kind === 'ready' ? current : { kind: 'error', message: e instanceof Error ? e.message : 'Could not load the execution.', notFound: status === 404 },
        )
      } finally {
        inFlight = false
      }
    }
    reload.current = () => void load()

    void load()
    const timer = setInterval(() => {
      if (!finished) void load()
    }, LIVE_POLL_MS)

    let unwatch: (() => void) | undefined
    let debounce: ReturnType<typeof setTimeout> | undefined
    void watchExecutions({ kind: 'execution', id }, () => {
      clearTimeout(debounce)
      debounce = setTimeout(() => void load(), 100)
    }).then((stop) => {
      if (controller.signal.aborted) stop()
      else unwatch = stop
    })

    return () => {
      controller.abort()
      clearInterval(timer)
      clearTimeout(debounce)
      unwatch?.()
    }
  }, [id])

  const refresh = useCallback(() => reload.current(), [])
  return { state, refresh }
}
