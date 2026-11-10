import { useEffect, useState } from 'react'
import { Link } from 'react-router'

import { usePermissions } from '@/app/AuthGate'
import { Button } from '@/components/ui/button'
import { Field, Input, Select } from '@/components/ui/form'
import { auditApi } from '@/lib/api/audit'
import type { AuditEntryDto } from '@/lib/api/generated'

import { dayEndIso, dayStartIso, formatDateTime, shortId } from '../executions/format'

const PAGE_SIZE = 50
const ENTITY_TYPES = ['Template', 'Workflow', 'Execution', 'Document'] as const

type ListState = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; items: AuditEntryDto[]; total: number }

interface Filters {
  action: string
  actorId: string
  entityType: string
  entityId: string
  from: string
  to: string
}

const NO_FILTERS: Filters = { action: '', actorId: '', entityType: '', entityId: '', from: '', to: '' }

/** Where an audited entity can be opened, when it has a screen. */
function entityLink(type: string, id: string): string | null {
  switch (type) {
    case 'Template':
      return `/templates/${id}`
    case 'Workflow':
      return `/workflows/${id}`
    case 'Execution':
      return `/executions/${id}`
    default:
      return null
  }
}

/** Read-only audit trail (plan §7, M9): who did what, newest first. Entries are append-only; nothing here changes them. */
export function AuditPage() {
  const { readAudit } = usePermissions()
  const [filters, setFilters] = useState<Filters>(NO_FILTERS)
  const [applied, setApplied] = useState<Filters>(NO_FILTERS)
  const [page, setPage] = useState(1)
  const [state, setState] = useState<ListState>({ kind: 'loading' })
  const [actions, setActions] = useState<string[]>([])

  useEffect(() => {
    if (!readAudit) return
    const controller = new AbortController()
    auditApi
      .actions(controller.signal)
      .then(setActions)
      .catch(() => undefined) // the filter just has no options
    return () => controller.abort()
  }, [readAudit])

  useEffect(() => {
    const timer = setTimeout(() => setApplied(filters), 300)
    return () => clearTimeout(timer)
  }, [filters])

  useEffect(() => {
    if (!readAudit) return
    const controller = new AbortController()
    auditApi
      .list(
        {
          action: applied.action || undefined,
          actorId: applied.actorId.trim() || undefined,
          entityType: applied.entityType || undefined,
          entityId: applied.entityId.trim() || undefined,
          from: dayStartIso(applied.from),
          to: dayEndIso(applied.to),
        },
        page,
        PAGE_SIZE,
        controller.signal,
      )
      .then((result) => setState({ kind: 'ready', items: result.items, total: result.total }))
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load the audit trail.' })
      })
    return () => controller.abort()
  }, [applied, page, readAudit])

  if (!readAudit) {
    return (
      <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
        <p role="alert" className="max-w-xl rounded-lg border border-line bg-surface px-4 py-3 text-ink-2">
          The audit trail is available to the Auditor and Admin roles.
        </p>
      </div>
    )
  }

  const change = (patch: Partial<Filters>) => {
    setFilters((f) => ({ ...f, ...patch }))
    setPage(1)
  }
  const total = state.kind === 'ready' ? state.total : 0
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  const filtered = JSON.stringify(filters) !== JSON.stringify(NO_FILTERS)

  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      <p className="mb-4 max-w-prose text-ink-2">
        Every change, publication, submission, retry, payload view and download, newest first. The trail is append-only; this view can’t change it.
      </p>

      <form
        aria-label="Filter the audit trail"
        className="mb-4 grid grid-cols-[repeat(auto-fill,minmax(160px,1fr))] items-end gap-3 rounded-[10px] border border-line bg-surface p-3.5"
        onSubmit={(e) => e.preventDefault()}
      >
        <Field label="Action" htmlFor="au-action">
          <Select id="au-action" value={filters.action} onChange={(e) => change({ action: e.target.value })}>
            <option value="">Any action</option>
            {actions.map((a) => (
              <option key={a} value={a}>
                {a}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Actor id" htmlFor="au-actor">
          <Input id="au-actor" value={filters.actorId} onChange={(e) => change({ actorId: e.target.value })} className="font-mono" />
        </Field>
        <Field label="Entity type" htmlFor="au-type">
          <Select id="au-type" value={filters.entityType} onChange={(e) => change({ entityType: e.target.value })}>
            <option value="">Any type</option>
            {ENTITY_TYPES.map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </Select>
        </Field>
        <Field label="Entity id" htmlFor="au-entity">
          <Input id="au-entity" value={filters.entityId} onChange={(e) => change({ entityId: e.target.value })} className="font-mono" />
        </Field>
        <Field label="From" htmlFor="au-from">
          <Input id="au-from" type="date" value={filters.from} max={filters.to || undefined} onChange={(e) => change({ from: e.target.value })} />
        </Field>
        <Field label="To" htmlFor="au-to">
          <Input id="au-to" type="date" value={filters.to} min={filters.from || undefined} onChange={(e) => change({ to: e.target.value })} />
        </Field>
        <Button type="button" disabled={!filtered} onClick={() => change(NO_FILTERS)}>
          Clear filters
        </Button>
      </form>

      {state.kind === 'loading' && <p className="text-ink-3">Loading the audit trail…</p>}
      {state.kind === 'error' && (
        <p role="alert" className="rounded-lg border border-err/30 bg-err-soft px-4 py-3 text-err">
          {state.message}
        </p>
      )}
      {state.kind === 'ready' && state.items.length === 0 && <p className="text-ink-2">{filtered ? 'No entries match these filters.' : 'No entries yet.'}</p>}
      {state.kind === 'ready' && state.items.length > 0 && (
        <>
          <div className="overflow-x-auto rounded-[10px] border border-line bg-surface">
            <table className="w-full border-collapse text-left text-[13px]">
              <caption className="sr-only">Audit trail</caption>
              <thead>
                <tr className="border-b border-line text-xs text-ink-3">
                  <th scope="col" className="px-3.5 py-2.5 font-medium">When</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Who</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Action</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Entity</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Details</th>
                  <th scope="col" className="px-3.5 py-2.5 font-medium">Trace</th>
                </tr>
              </thead>
              <tbody>
                {state.items.map((e) => {
                  const link = entityLink(e.entityType, e.entityId)
                  return (
                    <tr key={e.id} className="border-b border-line align-top last:border-b-0">
                      <td className="px-3.5 py-2.5 whitespace-nowrap text-ink-2">{formatDateTime(e.occurredAt)}</td>
                      <td className="px-3.5 py-2.5">
                        <div>{e.actorName}</div>
                        <div className="font-mono text-[11.5px] text-ink-3">{e.actorId}</div>
                      </td>
                      <td className="px-3.5 py-2.5 font-medium">{e.action}</td>
                      <td className="px-3.5 py-2.5 whitespace-nowrap">
                        <span className="text-ink-3">{e.entityType}</span>{' '}
                        {link ? (
                          <Link to={link} className="font-mono text-cyan-strong" aria-label={`Open ${e.entityType} ${shortId(e.entityId)}`}>
                            {shortId(e.entityId)}
                          </Link>
                        ) : (
                          <span className="font-mono">{shortId(e.entityId)}</span>
                        )}
                      </td>
                      <td className="max-w-[420px] px-3.5 py-2.5 font-mono text-[11.5px] break-words text-ink-2">
                        {e.details == null ? '' : JSON.stringify(e.details)}
                      </td>
                      <td className="px-3.5 py-2.5 font-mono text-[11.5px] text-ink-3" title={e.traceId ?? undefined}>
                        {e.traceId ? `${e.traceId.slice(0, 8)}…` : ''}
                      </td>
                    </tr>
                  )
                })}
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
