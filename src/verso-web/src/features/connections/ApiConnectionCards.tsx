import { PlugZap } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'

import { usePermissions } from '@/app/AuthGate'
import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input, Switch } from '@/components/ui/form'
import { apiConnectionsApi } from '@/lib/api/apiConnections'
import { ApiError } from '@/lib/api/client'
import type { ApiConnectionDto, ApiConnectionTestDto } from '@/lib/api/generated'

import { apiBody, apiErrorsOf, ApiFields, authTypeName, toApiForm, validateApi, type ApiErrors } from './ApiFields'

/** The real API connections (ADR 0017), loaded for administrators only. `reload` after any change. */
export function useApiConnections() {
  const administer = usePermissions().administer
  const [connections, setConnections] = useState<ApiConnectionDto[]>([])
  const [loadError, setLoadError] = useState(false)

  const reload = useCallback(
    async (signal?: AbortSignal) => {
      if (!administer) return
      try {
        const items = await apiConnectionsApi.list(true, signal)
        setConnections(Array.isArray(items) ? items : [])
        setLoadError(false)
      } catch (e) {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setLoadError(true)
      }
    },
    [administer],
  )

  useEffect(() => {
    const controller = new AbortController()
    void reload(controller.signal)
    return () => controller.abort()
  }, [reload])

  return { connections, loadError, reload }
}

const hostOf = (url: string) => {
  try {
    return new URL(url).host
  } catch {
    return url
  }
}

/** One real API connection, in the same card layout as the others. The switch activates or deactivates it. */
export function ApiCard({ connection, onToggle, onConfigure }: { connection: ApiConnectionDto; onToggle: (active: boolean) => void; onConfigure: () => void }) {
  const missingSecret = connection.isActive && connection.authType !== 'None' && !connection.hasSecret
  return (
    <li className={`flex flex-col gap-3 rounded-[10px] border border-line bg-surface p-4 ${connection.isActive ? '' : 'opacity-70'}`}>
      <div className="flex items-center gap-3">
        <div aria-hidden="true" className="grid size-[38px] flex-none place-items-center rounded-[9px] bg-[#0A6E8A] text-[13px] font-bold text-white">API</div>
        <div className="min-w-0 flex-1">
          <h3 className="m-0 text-[14.5px] font-semibold">{connection.name}</h3>
          <div className="text-xs text-ink-3">HTTP API · key <code className="font-mono">{connection.key}</code></div>
        </div>
        <Switch checked={connection.isActive} onCheckedChange={onToggle} label={`Enable ${connection.name}`} hideLabel />
      </div>
      <p className="m-0 text-[13px] text-ink-2">Real: stored on the server. Test connection sends one GET; the response body is never kept.</p>
      <dl className="m-0 mt-auto flex items-baseline justify-between gap-3 text-[12.5px]">
        <dt className="text-ink-3">Credential</dt>
        <dd className="m-0 min-w-0 text-right font-mono text-[11.5px] break-words text-ink-2">
          {authTypeName(connection.authType)} · {hostOf(connection.baseUrl)}
        </dd>
      </dl>
      <div className="flex flex-wrap items-center gap-2 text-[12.5px] text-ink-3">
        <StatusPill tone={!connection.isActive ? 'neutral' : missingSecret ? 'warn' : 'ok'}>
          {!connection.isActive ? 'Off' : missingSecret ? 'No secret' : 'Ready'}
        </StatusPill>
        {connection.hasSecret && <span>Secret saved</span>}
        <Button type="button" size="sm" className="ml-auto" onClick={onConfigure} aria-label={`Configure ${connection.name}`}>
          Configure
        </Button>
      </div>
    </li>
  )
}

export const API_TEST_POLL_MS = 1000
export const API_TEST_POLL_LIMIT = 30

type TestState = { kind: 'idle' } | { kind: 'running' } | { kind: 'done'; test: ApiConnectionTestDto } | { kind: 'error'; message: string }

/** Queues a test (the Worker sends one GET) and polls until it finishes. */
function useApiTest(connectionId: string) {
  const [state, setState] = useState<TestState>({ kind: 'idle' })
  const alive = useRef(true)
  useEffect(() => {
    alive.current = true
    return () => {
      alive.current = false
    }
  }, [])

  const run = async () => {
    setState({ kind: 'running' })
    try {
      let test = await apiConnectionsApi.requestTest(connectionId)
      for (let i = 0; i < API_TEST_POLL_LIMIT && (test.status === 'Queued' || test.status === 'Running'); i++) {
        await new Promise((resolve) => setTimeout(resolve, API_TEST_POLL_MS))
        if (!alive.current) return
        test = await apiConnectionsApi.getTest(test.id)
      }
      if (!alive.current) return
      setState(test.status === 'Queued' || test.status === 'Running'
        ? { kind: 'error', message: 'The Worker has not run the test yet. Is the Worker running?' }
        : { kind: 'done', test })
    } catch (e) {
      if (!alive.current) return
      setState({ kind: 'error', message: e instanceof ApiError || e instanceof Error ? e.message : 'Could not run the test.' })
    }
  }
  return { state, run }
}

const summary = (t: ApiConnectionTestDto) =>
  [t.httpStatus != null ? `HTTP ${t.httpStatus}` : null, t.elapsedMs != null ? `${t.elapsedMs} ms` : null, t.contentType, t.responseBytes != null ? `${t.responseBytes} bytes` : null]
    .filter(Boolean)
    .join(' · ')

/** Edit an existing API connection. The key cannot change; the secret is write-only (empty keeps it). */
export function ApiEditDialog({ connection, onClose, onSaved }: { connection: ApiConnectionDto; onClose: () => void; onSaved: (name: string) => Promise<void> }) {
  const [name, setName] = useState(connection.name)
  const [form, setForm] = useState(toApiForm(connection))
  const [errors, setErrors] = useState<ApiErrors>({})
  const [busy, setBusy] = useState(false)
  const test = useApiTest(connection.id)
  const testing = test.state.kind === 'running'

  const save = async () => {
    const local = validateApi(form, name, true)
    setErrors(local)
    if (Object.keys(local).length > 0) return
    setBusy(true)
    try {
      await apiConnectionsApi.update(connection.id, apiBody(form, name))
      setForm((f) => ({ ...f, secret: '' }))
      await onSaved(name.trim())
    } catch (e) {
      setErrors(apiErrorsOf(e, 'Could not save the API connection.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={connection.name}
      description="HTTP API · real. Stored on the server; Test connection sends one GET."
      footer={
        <>
          <Button type="button" onClick={() => void test.run()} disabled={testing || !connection.isActive} className="mr-auto">
            <PlugZap />
            {testing ? 'Testing…' : 'Test connection'}
          </Button>
          <Button type="button" onClick={onClose}>
            Cancel
          </Button>
          <Button type="button" variant="primary" onClick={() => void save()} disabled={busy}>
            Save settings
          </Button>
        </>
      }
    >
      <form className="flex flex-col gap-3.5" onSubmit={(e) => { e.preventDefault(); void save() }} noValidate>
        <Field label="Name" htmlFor="cn-name" error={errors.name}>
          <Input id="cn-name" value={name} onChange={(e) => setName(e.target.value)} aria-invalid={!!errors.name} />
        </Field>
        <ApiFields form={form} errors={errors} existing={connection} onChange={(patch) => { setForm((f) => ({ ...f, ...patch })); setErrors({}) }} />
        <div aria-live="polite" className="min-h-5 text-[13px]">
          {testing && <span className="text-ink-3">Waiting for the Worker to call the API…</span>}
          {test.state.kind === 'error' && <span role="alert" className="text-err">{test.state.message}</span>}
          {test.state.kind === 'done' && test.state.test.status === 'Succeeded' && <span className="text-ok">Connected · {summary(test.state.test)}.</span>}
          {test.state.kind === 'done' && test.state.test.status === 'Failed' && (
            <span role="alert" className="text-err">
              The test failed: {test.state.test.message} <code className="font-mono text-xs">{test.state.test.code}</code>
              {test.state.test.httpStatus != null ? ` · HTTP ${test.state.test.httpStatus}` : ''}
            </span>
          )}
        </div>
        <p className="text-xs text-ink-3">Test connection uses the saved settings (save your changes first). It sends one GET and shows only the status, the time, the content type and the size.</p>
      </form>
    </Dialog>
  )
}
