import { ArrowLeft, Check, CircleDashed, Loader2, Minus, RotateCw, X } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router'

import { usePermissions } from '@/app/AuthGate'
import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { ApiError } from '@/lib/api/client'
import { executionsApi } from '@/lib/api/executions'
import type { ExecutionDocumentDto, ExecutionDto, ExecutionEventDto, ExecutionInputDto, ExecutionStepDto } from '@/lib/api/generated'
import { cn } from '@/lib/utils'

import { DownloadButton } from './DownloadButton'
import { formatBytes, formatDate, formatDateTime, formatDuration, runDuration, shortId, statusTone } from './format'
import { useExecutionLive } from './useExecutionLive'

export function ExecutionDetailPage() {
  const { id = '' } = useParams()
  const { state, refresh } = useExecutionLive(id)

  return (
    <div className="max-w-[1100px] px-4 pt-6 pb-10 md:px-7">
      <Link to="/executions" className="mb-4 inline-flex items-center gap-1.5 text-[13px] text-ink-2 hover:text-ink">
        <ArrowLeft className="size-4" aria-hidden="true" /> All executions
      </Link>

      {state.kind === 'loading' && <p className="text-ink-3">Loading execution…</p>}
      {state.kind === 'error' && (
        <p role="alert" className="rounded-lg border border-err/30 bg-err-soft px-4 py-3 text-err">
          {state.notFound ? 'This execution was not found.' : state.message}
        </p>
      )}
      {state.kind === 'ready' && <Detail execution={state.execution} events={state.events} onChanged={refresh} />}
    </div>
  )
}

function Detail({ execution: e, events, onChanged }: { execution: ExecutionDto; events: ExecutionEventDto[]; onChanged: () => void }) {
  const permissions = usePermissions()
  const live = e.status === 'Queued' || e.status === 'Running'
  const titles = new Map(e.steps.map((s) => [s.nodeId, s.title]))

  return (
    <>
      <header className="mb-5 flex flex-wrap items-center gap-x-3 gap-y-1.5">
        <h1 className="text-[17px] font-semibold">
          Run <span className="font-mono">{shortId(e.id)}</span>
        </h1>
        <StatusPill tone={statusTone(e.status)}>{e.status}</StatusPill>
        {live && <span className="text-xs text-ink-3">Updating live</span>}
        <span className="text-ink-2">
          <span className="font-mono text-[12.5px]">{e.workflow.key}</span> v{e.workflow.version}
        </span>
        {e.status === 'Failed' && permissions.operate && <RetryButton id={e.id} onChanged={onChanged} />}
      </header>

      {(e.status === 'Rejected' || e.status === 'Failed') && (
        <p role="status" className={cn('mb-5 rounded-lg border px-4 py-3', e.status === 'Rejected' ? 'border-warn/30 bg-warn-soft text-warn' : 'border-err/30 bg-err-soft text-err')}>
          <strong className="font-semibold">{e.status === 'Rejected' ? 'Rejected: the data needs correcting.' : 'Failed: a technical problem; it can be retried.'}</strong>{' '}
          {e.failureMessage} <span className="font-mono text-xs">{e.failureCode}</span>
        </p>
      )}

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_320px]">
        <div className="flex min-w-0 flex-col gap-5">
          <section aria-labelledby="steps-h" className="rounded-[10px] border border-line bg-surface p-4">
            <h2 id="steps-h" className="mb-2 text-[14px] font-semibold">Steps</h2>
            <ol aria-label="Steps">
              {e.steps.map((s) => (
                <Step key={s.nodeId} step={s} />
              ))}
            </ol>
          </section>

          <section aria-labelledby="docs-h" className="rounded-[10px] border border-line bg-surface p-4">
            <h2 id="docs-h" className="mb-2 text-[14px] font-semibold">Documents</h2>
            {e.documents.length === 0 ? (
              <p className="text-ink-3">{live ? 'No document yet.' : 'This run produced no document.'}</p>
            ) : (
              <ul className="flex flex-col gap-3">
                {e.documents.map((d) => (
                  <DocumentRow key={d.id} document={d} />
                ))}
              </ul>
            )}
          </section>

          <PayloadPanel id={e.id} allowed={permissions.readPayload} />
        </div>

        <aside className="flex min-w-0 flex-col gap-5">
          <section aria-labelledby="details-h" className="rounded-[10px] border border-line bg-surface p-4">
            <h2 id="details-h" className="mb-2 text-[14px] font-semibold">Details</h2>
            <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1.5 text-[13px]">
              <Row label="Submitted">{formatDateTime(e.submittedAt)}</Row>
              <Row label="By">{e.submittedBy}</Row>
              <Row label="Duration">{formatDuration(runDuration(e.submittedAt, e.completedAt))}</Row>
              <Row label="Attempt">{e.attempt}</Row>
              <Row label="Correlation" mono>{e.correlationId}</Row>
              <Row label="Idempotency key" mono>{e.idempotencyKey}</Row>
              <Row label="Input hash" mono>{e.inputHash.slice(0, 16)}…</Row>
              {Object.entries(e.templateVersions).map(([node, versionId]) => (
                <Row key={node} label={`Template (${titles.get(node) ?? node})`} mono>
                  {shortId(versionId)}
                </Row>
              ))}
            </dl>
          </section>

          <section aria-labelledby="activity-h" className="rounded-[10px] border border-line bg-surface p-4">
            <h2 id="activity-h" className="mb-2 text-[14px] font-semibold">Activity</h2>
            <ol aria-label="Activity" className="flex flex-col gap-1.5 text-[12.5px]">
              {events.map((ev) => (
                <li key={ev.id} className="flex gap-2">
                  <time dateTime={ev.occurredAt} className="flex-none font-mono text-ink-3">
                    {new Date(ev.occurredAt).toLocaleTimeString('en-GB')}
                  </time>
                  <span className="min-w-0 text-ink-2">
                    {ev.type}
                    {ev.nodeId && <span className="text-ink-3"> · {titles.get(ev.nodeId) ?? ev.nodeId}</span>}
                  </span>
                </li>
              ))}
            </ol>
          </section>
        </aside>
      </div>
    </>
  )
}

function Row({ label, mono, children }: { label: string; mono?: boolean; children: React.ReactNode }) {
  return (
    <>
      <dt className="text-ink-3">{label}</dt>
      <dd className={cn('min-w-0 break-words text-ink', mono && 'font-mono text-[12px]')}>{children}</dd>
    </>
  )
}

const STEP_ICON: Record<string, { icon: typeof Check; className: string }> = {
  Succeeded: { icon: Check, className: 'text-ok' },
  Failed: { icon: X, className: 'text-err' },
  Running: { icon: Loader2, className: 'animate-spin text-cyan' },
  Pending: { icon: CircleDashed, className: 'text-ink-3' },
  Skipped: { icon: Minus, className: 'text-ink-3' },
}

interface ResultIssue {
  path?: string | null
  rule?: string | null
  code?: string | null
  message?: string | null
}

function Step({ step: s }: { step: ExecutionStepDto }) {
  const { icon: Icon, className } = STEP_ICON[s.status] ?? { icon: CircleDashed, className: 'text-ink-3' }
  const result = s.result as { issues?: ResultIssue[]; warnings?: string[]; fileName?: string; folderPath?: string } | null
  const issues = result?.issues ?? []
  const warnings = result?.warnings ?? []

  return (
    <li className="grid grid-cols-[22px_minmax(0,1fr)_auto] items-start gap-2.5 border-b border-line py-2 text-[13px] last:border-b-0" data-status={s.status}>
      <span aria-hidden="true" className={cn('pt-0.5', className)}>
        <Icon className="size-4" />
      </span>
      <div className="min-w-0">
        <div className="font-medium">
          {s.title} <span className="sr-only">: {s.status}</span>
        </div>
        <small className="block text-xs text-ink-3">
          {s.type}
          {s.outputPort && <> · left by “{s.outputPort}”</>}
          {s.attempt > 1 && <> · attempt {s.attempt}</>}
          <span aria-hidden="true"> · {s.status}</span>
        </small>
        {s.error && <p className="mt-1 text-xs text-err">{s.error}</p>}
        {issues.length > 0 && (
          <ul aria-label={`Problems found in ${s.title}`} className="mt-1.5 flex flex-col gap-0.5 rounded-md bg-warn-soft px-2.5 py-1.5 text-xs text-ink">
            {issues.map((i, n) => (
              <li key={n}>
                <span className="font-mono">{i.path ?? i.code}</span>
                {i.rule && <span className="text-ink-3"> [{i.rule}]</span>}: {i.message}
              </li>
            ))}
          </ul>
        )}
        {warnings.length > 0 && <p className="mt-1 text-xs text-warn">{warnings.join('; ')}</p>}
      </div>
      <span className="pt-0.5 font-mono text-xs text-ink-3">{formatDuration(s.durationMs)}</span>
    </li>
  )
}

function DocumentRow({ document: d }: { document: ExecutionDocumentDto }) {
  return (
    <li className="rounded-lg border border-line bg-surface-2 p-3 text-[13px]">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{d.fileName}</span>
        <StatusPill tone={d.status === 'Archived' ? 'ok' : 'neutral'}>{d.status}</StatusPill>
        <span className="text-ink-3">{formatBytes(d.sizeBytes)}</span>
        {d.status === 'Archived' && <DownloadButton documentId={d.id} fileName={d.fileName} className="ml-auto" />}
      </div>
      <dl className="mt-2 grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-xs">
        <Row label="SHA-256" mono>{d.sha256}</Row>
        {d.archive && (
          <>
            <Row label="Folder" mono>{d.archive.folderPath}</Row>
            <Row label="Archived">{formatDateTime(d.archive.archivedAt)}</Row>
            <Row label="Retention">
              {d.archive.retentionUntil ? `Until ${formatDate(d.archive.retentionUntil)} (${d.archive.retention})` : 'Permanent'}
            </Row>
            {Object.entries(d.archive.metadata).map(([key, value]) => (
              <Row key={key} label={key}>{value}</Row>
            ))}
          </>
        )}
      </dl>
    </li>
  )
}

function RetryButton({ id, onChanged }: { id: string; onChanged: () => void }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const retry = async () => {
    setBusy(true)
    setError(null)
    try {
      await executionsApi.retry(id)
      onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not retry.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <Button variant="primary" onClick={retry} disabled={busy}>
        <RotateCw /> Retry from the failed step
      </Button>
      {error && (
        <span role="alert" className="text-[13px] text-err">
          {error}
        </span>
      )}
    </>
  )
}

type PayloadState =
  | { kind: 'hidden' }
  | { kind: 'loading' }
  | { kind: 'forbidden' }
  | { kind: 'error'; message: string }
  | { kind: 'shown'; input: ExecutionInputDto }

/** The submitted data may contain personal data: it is only fetched on request, restricted to Auditor/Admin, and each read is audited. */
function PayloadPanel({ id, allowed }: { id: string; allowed: boolean }) {
  const [state, setState] = useState<PayloadState>({ kind: 'hidden' })

  const show = async () => {
    setState({ kind: 'loading' })
    try {
      setState({ kind: 'shown', input: await executionsApi.input(id) })
    } catch (e) {
      if (e instanceof ApiError && e.status === 403) setState({ kind: 'forbidden' })
      else setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load the submitted data.' })
    }
  }

  return (
    <section aria-labelledby="payload-h" className="rounded-[10px] border border-line bg-surface p-4">
      <h2 id="payload-h" className="mb-2 text-[14px] font-semibold">Submitted data</h2>
      {!allowed && <p className="text-ink-2">Submitted data is restricted to the Auditor and Admin roles.</p>}
      {allowed && state.kind === 'hidden' && (
        <div className="flex flex-wrap items-center gap-3">
          <Button onClick={show}>Show submitted data</Button>
          <span className="text-xs text-ink-3">Restricted to auditors and admins. Viewing is recorded in the audit trail.</span>
        </div>
      )}
      {state.kind === 'loading' && <p className="text-ink-3">Loading…</p>}
      {state.kind === 'forbidden' && (
        <p role="alert" className="text-ink-2">
          You don’t have access to submitted data. It needs the Auditor or Admin role.
        </p>
      )}
      {state.kind === 'error' && (
        <p role="alert" className="text-err">
          {state.message}
        </p>
      )}
      {state.kind === 'shown' && (
        <>
          <pre aria-label="Submitted JSON" className="max-h-96 overflow-auto rounded-lg bg-surface-2 p-3 font-mono text-xs">
            {JSON.stringify(state.input.data, null, 2)}
          </pre>
          <Button className="mt-2" onClick={() => setState({ kind: 'hidden' })}>
            Hide
          </Button>
        </>
      )}
    </section>
  )
}
