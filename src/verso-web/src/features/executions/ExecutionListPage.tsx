import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router'

import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Field, Input, Select } from '@/components/ui/form'
import { executionsApi, type ExecutionFilter } from '@/lib/api/executions'
import type { ExecutionSummaryDto, WorkflowSummaryDto } from '@/lib/api/generated'
import { workflowsApi } from '@/lib/api/workflows'
import { watchExecutions } from '@/lib/realtime'

import { dayEndIso, dayStartIso, EXECUTION_STATUSES, FINISHED_STATUSES, formatDateTime, formatDuration, runDuration, shortId, statusTone } from './format'

const PAGE_SIZE = 25
/** Only used while something on the page is unfinished, in case notifications don't arrive. */
const POLL_MS = 5000

type ListState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; items: ExecutionSummaryDto[]; total: number }

interface Filters {
  workflow: string
  status: string
  from: string
  to: string
  correlationId: string
}

const NO_FILTERS: Filters = { workflow: '', status: '', from: '', to: '', correlationId: '' }

function toApiFilter(f: Filters): ExecutionFilter {
  return {
    workflow: f.workflow || undefined,
    status: f.status || undefined,
    from: dayStartIso(f.from),
    to: dayEndIso(f.to),
    correlationId: f.correlationId.trim() || undefined,
  }
}

export function ExecutionListPage() {
  const [filters, setFilters] = useState<Filters>(NO_FILTERS)
  const [applied, setApplied] = useState<Filters>(NO_FILTERS) // text input is applied after a short pause
  const [page, setPage] = useState(1)
  const [state, setState] = useState<ListState>({ kind: 'loading' })
  const [workflows, setWorkflows] = useState<WorkflowSummaryDto[]>([])

  useEffect(() => {
    const controller = new AbortController()
    workflowsApi
      .list(1, 100, controller.signal)
      .then((p) => setWorkflows(p.items))
      .catch(() => undefined) // the filter just has no options; the list itself still works
    return () => controller.abort()
  }, [])

  useEffect(() => {
    const timer = setTimeout(() => setApplied(filters), 300)
    return () => clearTimeout(timer)
  }, [filters])

  useEffect(() => {
    const controller = new AbortController()
    let unfinished = false
    const load = () =>
      executionsApi
        .list(toApiFilter(applied), page, PAGE_SIZE, controller.signal)
        .then((result) => {
          unfinished = result.items.some((e) => !FINISHED_STATUSES.has(e.status))
          setState({ kind: 'ready', items: result.items, total: result.total })
        })
        .catch((e: unknown) => {
          if (e instanceof DOMException && e.name === 'AbortError') return
          setState((s) => (s.kind === 'ready' ? s : { kind: 'error', message: e instanceof Error ? e.message : 'Could not load executions.' }))
        })
    void load()

    const timer = setInterval(() => {
      if (unfinished) void load()
    }, POLL_MS)
    let unwatch: (() => void) | undefined
    let debounce: ReturnType<typeof setTimeout> | undefined
    void watchExecutions({ kind: 'all' }, () => {
      clearTimeout(debounce)
      debounce = setTimeout(() => void load(), 300)
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
  }, [applied, page])

  const change = useCallback((patch: Partial<Filters>) => {
    setFilters((f) => ({ ...f, ...patch }))
    setPage(1)
  }, [])

  const total = state.kind === 'ready' ? state.total : 0
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  const filtered = JSON.stringify(filters) !== JSON.stringify(NO_FILTERS)

  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      <p className="mb-4 max-w-prose text-ink-2">Every submission, newest first. The list updates as runs progress.</p>

      <form
        aria-label="Filter executions"
        className="mb-4 grid grid-cols-[repeat(auto-fill,minmax(170px,1fr))] items-end gap-3 rounded-[10px] border border-line bg-surface p-3.5"
        onSubmit={(e) => e.preventDefault()}
      >
        <Field label="Workflow" htmlFor="f-workflow">
          <Select id="f-workflow" value={filters.workflow} onChange={(e) => change({ workflow: e.target.value })}>
            <option value="">All workflows</option>
            {workflows.map((w) => (
              <option key={w.id} value={w.key}>
                {w.name} ({w.key})
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Status" htmlFor="f-status">
          <Select id="f-status" value={filters.status} onChange={(e) => change({ status: e.target.value })}>
            <option value="">Any status</option>
            {EXECUTION_STATUSES.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Submitted from" htmlFor="f-from">
          <Input id="f-from" type="date" value={filters.from} max={filters.to || undefined} onChange={(e) => change({ from: e.target.value })} />
        </Field>
        <Field label="Submitted to" htmlFor="f-to">
          <Input id="f-to" type="date" value={filters.to} min={filters.from || undefined} onChange={(e) => change({ to: e.target.value })} />
        </Field>
        <Field label="Correlation id" htmlFor="f-correlation">
          <Input id="f-correlation" value={filters.correlationId} onChange={(e) => change({ correlationId: e.target.value })} className="font-mono" />
        </Field>
        <Button type="button" disabled={!filtered} onClick={() => change(NO_FILTERS)}>
          Clear filters
        </Button>
      </form>

      {state.kind === 'loading' && <p className="text-ink-3">Loading executions…</p>}
      {state.kind === 'error' && (
        <p role="alert" className="rounded-lg border border-err/30 bg-err-soft px-4 py-3 text-err">
          {state.message}
        </p>
      )}
      {state.kind === 'ready' && state.items.length === 0 && (
        <section className="max-w-xl rounded-lg border border-dashed border-line-strong bg-surface p-6">
          <h2 className="mb-1 text-[14.5px] font-semibold">{filtered ? 'No executions match these filters' : 'No executions yet'}</h2>
          <p className="text-ink-2">
            {filtered ? 'Change or clear the filters.' : 'Submit invoice data to a published workflow: POST /api/v1/workflows/{key}/executions.'}
          </p>
        </section>
      )}
      {state.kind === 'ready' && state.items.length > 0 && (
        <>
          <div className="overflow-x-auto rounded-[10px] border border-line bg-surface">
            <table className="w-full border-collapse text-left text-[13px]">
              <caption className="sr-only">Executions</caption>
              <thead>
                <tr className="border-b border-line text-xs text-ink-3">
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Status</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Run</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Workflow</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Submitted</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Duration</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Correlation id</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Result</th>
                </tr>
              </thead>
              <tbody>
                {state.items.map((e) => (
                  <tr key={e.id} className="border-b border-line last:border-b-0 hover:bg-surface-2">
                    <td className="px-3.5 py-2.5">
                      <StatusPill tone={statusTone(e.status)}>{e.status}</StatusPill>
                    </td>
                    <td className="px-3.5 py-2.5">
                      <Link to={`/executions/${e.id}`} className="font-mono text-cyan-strong" aria-label={`Open run ${shortId(e.id)}`}>
                        {shortId(e.id)}
                      </Link>
                      {e.attempt > 1 && <span className="ml-1.5 text-xs text-ink-3">attempt {e.attempt}</span>}
                    </td>
                    <td className="px-3.5 py-2.5 whitespace-nowrap">
                      <span className="font-mono text-[12px]">{e.workflowKey}</span> <span className="text-ink-3">v{e.workflowVersion}</span>
                    </td>
                    <td className="px-3.5 py-2.5 whitespace-nowrap text-ink-2">{formatDateTime(e.submittedAt)}</td>
                    <td className="px-3.5 py-2.5 whitespace-nowrap text-ink-2">{formatDuration(runDuration(e.submittedAt, e.completedAt))}</td>
                    <td className="px-3.5 py-2.5 font-mono text-[12px] text-ink-3">{e.correlationId}</td>
                    <td className="max-w-[360px] truncate px-3.5 py-2.5 text-ink-2" title={e.failureMessage ?? undefined}>
                      {e.failureMessage ?? (e.status === 'Succeeded' ? 'Archived' : '')}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <nav aria-label="Pages" className="mt-3 flex items-center gap-3 text-[13px] text-ink-2">
            <span>
              {(page - 1) * PAGE_SIZE + 1}–{(page - 1) * PAGE_SIZE + state.items.length} of {total}
            </span>
            <Button className="ml-auto" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
              Previous
            </Button>
            <Button disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>
              Next
            </Button>
          </nav>
        </>
      )}
    </div>
  )
}
