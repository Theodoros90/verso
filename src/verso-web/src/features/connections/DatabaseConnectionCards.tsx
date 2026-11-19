import { PlugZap } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'

import { usePermissions } from '@/app/AuthGate'
import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input, Switch } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import { databaseConnectionsApi } from '@/lib/api/databaseConnections'
import type { DatabaseConnectionDto, DatabaseConnectionTestDto } from '@/lib/api/generated'

import { dbBody, dbErrorsOf, DbFields, providerName, toDbForm, validateDb, type DbErrors } from './DbFields'

/** The real database connections (ADR 0016), loaded for administrators only. `reload` after any change. */
export function useDatabaseConnections() {
  const administer = usePermissions().administer
  const [connections, setConnections] = useState<DatabaseConnectionDto[]>([])
  const [loadError, setLoadError] = useState(false)

  const reload = useCallback(
    async (signal?: AbortSignal) => {
      if (!administer) return
      try {
        const items = await databaseConnectionsApi.list(true, signal)
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

/** One real database connection, in the same card layout as the others. The switch activates or deactivates it. */
export function DatabaseCard({ connection, onToggle, onConfigure }: { connection: DatabaseConnectionDto; onToggle: (active: boolean) => void; onConfigure: () => void }) {
  const missingPassword = connection.isActive && !connection.hasPassword
  return (
    <li className={`flex flex-col gap-3 rounded-[10px] border border-line bg-surface p-4 ${connection.isActive ? '' : 'opacity-70'}`}>
      <div className="flex items-center gap-3">
        <div aria-hidden="true" className="grid size-[38px] flex-none place-items-center rounded-[9px] bg-[#1A3442] text-[13px] font-bold text-white">DB</div>
        <div className="min-w-0 flex-1">
          <h3 className="m-0 text-[14.5px] font-semibold">{connection.name}</h3>
          <div className="text-xs text-ink-3">{providerName(connection.provider)} · key <code className="font-mono">{connection.key}</code></div>
        </div>
        <Switch checked={connection.isActive} onCheckedChange={onToggle} label={`Enable ${connection.name}`} hideLabel />
      </div>
      <p className="m-0 text-[13px] text-ink-2">Real: stored on the server. Test connection connects and runs SELECT 1 only; no query is run.</p>
      <dl className="m-0 mt-auto flex items-baseline justify-between gap-3 text-[12.5px]">
        <dt className="text-ink-3">Credential</dt>
        <dd className="m-0 min-w-0 text-right font-mono text-[11.5px] break-words text-ink-2">
          Database login · {connection.server}
          {connection.port > 0 ? `:${connection.port}` : ''}
          {connection.database ? ` / ${connection.database}` : ''}
        </dd>
      </dl>
      <div className="flex flex-wrap items-center gap-2 text-[12.5px] text-ink-3">
        <StatusPill tone={!connection.isActive ? 'neutral' : missingPassword ? 'warn' : 'ok'}>
          {!connection.isActive ? 'Off' : missingPassword ? 'No password' : 'Ready'}
        </StatusPill>
        {connection.hasPassword && <span>Password saved</span>}
        {connection.allowWrites && <StatusPill tone="warn">Writes on</StatusPill>}
        <Button type="button" size="sm" className="ml-auto" onClick={onConfigure} aria-label={`Configure ${connection.name}`}>
          Configure
        </Button>
      </div>
    </li>
  )
}

export const DB_TEST_POLL_MS = 1000
export const DB_TEST_POLL_LIMIT = 30

type TestState = { kind: 'idle' } | { kind: 'running' } | { kind: 'done'; test: DatabaseConnectionTestDto } | { kind: 'error'; message: string }

/** Queues a test (the Worker connects and runs SELECT 1) and polls until it finishes. */
function useConnectionTest(connectionId: string) {
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
      let test = await databaseConnectionsApi.requestTest(connectionId)
      for (let i = 0; i < DB_TEST_POLL_LIMIT && (test.status === 'Queued' || test.status === 'Running'); i++) {
        await new Promise((resolve) => setTimeout(resolve, DB_TEST_POLL_MS))
        if (!alive.current) return
        test = await databaseConnectionsApi.getTest(test.id)
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

/** Edit an existing database connection. The key cannot change; the password is write-only (empty keeps it). */
export function DatabaseEditDialog({ connection, onClose, onSaved }: { connection: DatabaseConnectionDto; onClose: () => void; onSaved: (name: string) => Promise<void> }) {
  const [name, setName] = useState(connection.name)
  const [form, setForm] = useState(toDbForm(connection))
  const [errors, setErrors] = useState<DbErrors>({})
  const [busy, setBusy] = useState(false)
  const test = useConnectionTest(connection.id)
  const testing = test.state.kind === 'running'

  const save = async () => {
    const local = validateDb(form, name, true)
    setErrors(local)
    if (Object.keys(local).length > 0) return
    setBusy(true)
    try {
      await databaseConnectionsApi.update(connection.id, dbBody(form, name))
      setForm((f) => ({ ...f, password: '' }))
      await onSaved(name.trim())
    } catch (e) {
      setErrors(dbErrorsOf(e, 'Could not save the database connection.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={connection.name}
      description={`${providerName(connection.provider)} · real. Stored on the server; Test connection runs SELECT 1 only.`}
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
        <DbFields form={form} errors={errors} existing={connection} onChange={(patch) => { setForm((f) => ({ ...f, ...patch })); setErrors({}) }} />
        <div aria-live="polite" className="min-h-5 text-[13px]">
          {testing && <span className="text-ink-3">Waiting for the Worker to connect…</span>}
          {test.state.kind === 'error' && <span role="alert" className="text-err">{test.state.message}</span>}
          {test.state.kind === 'done' && test.state.test.status === 'Succeeded' && (
            <span className="text-ok">Connected{test.state.test.serverVersion ? ` · ${test.state.test.serverVersion}` : ''}.</span>
          )}
          {test.state.kind === 'done' && test.state.test.status === 'Failed' && (
            <span role="alert" className="text-err">
              The test failed: {test.state.test.message} <code className="font-mono text-xs">{test.state.test.code}</code>
            </span>
          )}
        </div>
        <p className="text-xs text-ink-3">Test connection uses the saved settings (save your changes first). It connects and runs SELECT 1; nothing else is run.</p>
      </form>
    </Dialog>
  )
}
