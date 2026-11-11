import { CircleCheck, CircleX, FolderCheck, LoaderCircle, RefreshCw, TriangleAlert } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'

import { Button } from '@/components/ui/button'
import { ApiError } from '@/lib/api/client'
import { folderInputApi } from '@/lib/api/folderInput'
import type { FolderAccessTargetRequest, FolderAccessTestDto, FolderInputStatusDto, FolderLocationDto } from '@/lib/api/generated'
import { cn } from '@/lib/utils'

import { SchemaForm, type FieldSchema } from './SchemaForm'

type Config = Record<string, unknown>

const ROLE_LABELS: Record<string, string> = { source: 'Source folder', processed: 'Processed files', failed: 'Failed files' }

/** How often the designer asks for the result of a queued access test, and for how long. */
export const TEST_POLL_MS = 1000
const TEST_POLL_LIMIT = 30

/** The folders an access test should check, from the step's settings (only those with a location chosen). */
export function accessTargets(config: Config): FolderAccessTargetRequest[] {
  const text = (k: string) => (typeof config[k] === 'string' ? (config[k] as string) : '')
  return (['source', 'processed', 'failed'] as const)
    .map((role) => ({ role, location: text(`${role}Location`), subfolder: text(`${role}Subfolder`) }))
    .filter((t) => t.location !== '')
}

/**
 * Settings of the Folder input step: approved locations (chosen from what administrators configured on the Worker,
 * never typed paths), "Test access" (run by the Worker under its service identity), and the folder's activity.
 */
export function FolderInputSettings({
  schema,
  value,
  onChange,
  errorsFor,
  readOnly,
  canTest,
  locations,
  workflowId,
  published,
}: {
  schema: FieldSchema
  value: Config
  onChange: (next: Config, key: string) => void
  errorsFor: (field: string) => string[]
  readOnly: boolean
  /** Workflow authors may run access tests, also on a published version. */
  canTest: boolean
  locations: FolderLocationDto[] | undefined
  workflowId: string
  /** The workflow has a published version (only then does the Worker scan). */
  published: boolean
}) {
  return (
    <div className="flex flex-col gap-5">
      {locations !== undefined && locations.length === 0 && (
        <p className="rounded-md bg-warn-soft px-2.5 py-1.5 text-[12.5px] text-warn">
          No folders are approved yet. An administrator adds them to the Worker's configuration (FolderInput:Locations).
        </p>
      )}
      <SchemaForm schema={schema} value={value} onChange={onChange} ctx={{ templates: [], dataPaths: [], errorsFor, readOnly, folderLocations: locations }} />
      {canTest && <AccessTest targets={accessTargets(value)} />}
      {published && <FolderActivity workflowId={workflowId} />}
    </div>
  )
}

function AccessTest({ targets }: { targets: FolderAccessTargetRequest[] }) {
  const [test, setTest] = useState<FolderAccessTestDto | null>(null)
  const [running, setRunning] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const alive = useRef(true)
  useEffect(() => () => void (alive.current = false), [])

  const run = async () => {
    setRunning(true)
    setError(null)
    setTest(null)
    try {
      let current = await folderInputApi.requestTest(targets)
      for (let i = 0; i < TEST_POLL_LIMIT && alive.current && (current.status === 'Queued' || current.status === 'Running'); i++) {
        await new Promise((r) => setTimeout(r, TEST_POLL_MS))
        current = await folderInputApi.test(current.id)
      }
      if (!alive.current) return
      setTest(current)
      if (current.status === 'Queued' || current.status === 'Running') setError('The Worker hasn\'t run the test yet. Is the Worker running?')
    } catch (e) {
      if (alive.current) setError(e instanceof ApiError ? (Object.values(e.problem.errors ?? {})[0]?.[0] ?? e.message) : 'The test could not be requested.')
    } finally {
      if (alive.current) setRunning(false)
    }
  }

  return (
    <section aria-label="Test access" className="flex flex-col gap-2">
      <div className="flex items-center gap-2">
        <Button size="sm" onClick={run} disabled={running || targets.length === 0}>
          {running ? <LoaderCircle className="animate-spin" /> : <FolderCheck />} Test access
        </Button>
        <span className="text-xs text-ink-3">{targets.length === 0 ? 'Choose the locations first.' : 'Checked by the Worker, as its service account.'}</span>
      </div>
      {error && <p className="text-[12.5px] text-err">{error}</p>}
      {test?.status === 'Failed' && <p className="text-[12.5px] text-err">{test.error}</p>}
      {test?.status === 'Completed' && (
        <div className="flex flex-col gap-2">
          {test.results.map((r) => (
            <div key={r.role} className={cn('rounded-md px-2.5 py-2 text-[12.5px]', r.ok ? 'bg-ok-soft' : 'bg-err-soft')}>
              <p className={cn('flex items-center gap-1.5 font-medium', r.ok ? 'text-ok' : 'text-err')}>
                {r.ok ? <CircleCheck className="size-3.5" aria-hidden="true" /> : <CircleX className="size-3.5" aria-hidden="true" />}
                {ROLE_LABELS[r.role] ?? r.role}: {r.ok ? 'access OK' : 'no access'}
              </p>
              <ul className="mt-1 flex flex-col gap-0.5 text-ink-2">
                {r.checks.map((c) => (
                  <li key={c.name}>{c.ok ? '✓' : '✗'} {c.message}</li>
                ))}
              </ul>
            </div>
          ))}
          <p className="text-xs text-ink-3">
            Checked as <span className="font-mono">{test.identity}</span> on <span className="font-mono">{test.machine}</span>.
          </p>
        </div>
      )}
    </section>
  )
}

const SCAN_LABELS: Record<string, string> = {
  Ok: 'OK',
  Unavailable: 'Folder unavailable',
  AccessDenied: 'Access denied',
  Misconfigured: 'Misconfigured',
  Error: 'Error',
}

function FolderActivity({ workflowId }: { workflowId: string }) {
  const [status, setStatus] = useState<FolderInputStatusDto | null>(null)
  const [failed, setFailed] = useState(false)

  const refresh = useCallback((signal?: AbortSignal) => {
    folderInputApi
      .status(workflowId, signal)
      .then((s) => {
        setStatus(s)
        setFailed(false)
      })
      .catch(() => {
        if (!signal?.aborted) setFailed(true)
      })
  }, [workflowId])

  useEffect(() => {
    const controller = new AbortController()
    refresh(controller.signal)
    return () => controller.abort()
  }, [refresh])

  const watch = status?.watch
  return (
    <section aria-label="Folder activity" className="flex flex-col gap-2">
      <div className="flex items-center gap-2">
        <h5 className="text-[13px] font-semibold">Folder activity</h5>
        <Button size="icon" variant="ghost" className="ml-auto size-7" aria-label="Refresh folder activity" onClick={() => refresh()}>
          <RefreshCw />
        </Button>
      </div>
      {failed && <p className="text-[12.5px] text-err">Couldn't load the folder activity.</p>}
      {status && !watch && <p className="text-[12.5px] text-ink-3">Not scanned yet. The Worker scans published workflows at their interval.</p>}
      {watch && (
        <p className={cn('flex items-start gap-1.5 rounded-md px-2.5 py-1.5 text-[12.5px]', watch.lastStatus === 'Ok' ? 'bg-ok-soft text-ok' : 'bg-err-soft text-err')}>
          {watch.lastStatus === 'Ok' ? <CircleCheck className="mt-0.5 size-3.5 flex-none" aria-hidden="true" /> : <TriangleAlert className="mt-0.5 size-3.5 flex-none" aria-hidden="true" />}
          <span>
            Last scan {watch.lastScanAt ? new Date(watch.lastScanAt).toLocaleString() : '—'}: {SCAN_LABELS[watch.lastStatus ?? ''] ?? watch.lastStatus}
            {watch.lastStatus === 'Ok' ? `, ${watch.lastFilesFound} file(s) found.` : `. ${watch.lastError ?? ''}`}
          </span>
        </p>
      )}
      {status && status.files.length > 0 && (
        <ul aria-label="Recent files" className="flex flex-col divide-y divide-line text-[12.5px]">
          {status.files.map((f) => (
            <li key={f.id} className="flex flex-col gap-0.5 py-1.5">
              <div className="flex items-center gap-2">
                <span className="min-w-0 truncate font-mono">{f.relativePath}</span>
                <span className={cn('ml-auto flex-none', f.outcome === 'Failed' ? 'text-err' : f.outcome === 'Processed' ? 'text-ok' : 'text-ink-3')}>
                  {f.outcome ?? (f.status === 'Submitted' ? 'Running' : f.status)}
                </span>
              </div>
              {f.errorMessage && <span className="text-ink-3">{f.errorMessage}</span>}
              {f.deliveryError && <span className="text-err">Not moved yet: {f.deliveryError}</span>}
              {f.executionId && (
                <Link to={`/executions/${f.executionId}`} className="text-cyan hover:underline">
                  View run
                </Link>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
