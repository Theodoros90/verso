import { PlugZap } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'

import { usePermissions } from '@/app/AuthGate'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input, Select, Textarea } from '@/components/ui/form'
import { apiConnectionsApi } from '@/lib/api/apiConnections'
import { databaseConnectionsApi } from '@/lib/api/databaseConnections'
import { emailRelaysApi } from '@/lib/api/emailRelays'
import { cn } from '@/lib/utils'

import { AUTH_METHODS, CONNECTION_GROUPS, simulateTest, type Connection, type ConnectionGroup, type SimulatedTest } from './connections'
import { API_AUTH, apiBody, apiErrorsOf, ApiFields, blankApi, validateApi, type ApiErrors } from './ApiFields'
import { blankDb, dbBody, dbErrorsOf, DbFields, SQL_AUTH, validateDb, type DbErrors } from './DbFields'
import { blankRelay, relayBody, relayErrorsOf, RelayFields, SMTP_AUTH, validateRelay, type RelayErrors } from './RelayFields'

interface Draft {
  name: string
  group: ConnectionGroup
  category: string
  auth: string
  endpoint: string
  description: string
}

const blank: Draft = { name: '', group: 'Data sources', category: '', auth: 'API key', endpoint: '', description: '' }

const toDraft = (c: Connection): Draft => ({ name: c.name, group: c.group, category: c.category, auth: c.auth, endpoint: c.endpoint, description: c.description })

/**
 * Create or configure a connection. Most connections are simulated (no secret field: secrets live in the bank's secret store).
 * Choosing the authentication **SMTP credentials** on a new connection makes it a real email relay (ADR 0015): the SMTP form,
 * with a write-only password, creates it on the server, and Send email steps can use it by its key.
 */
export function ConnectionDialog({
  connection,
  existingNames,
  onSave,
  onRelayCreated,
  onDatabaseCreated,
  onApiCreated,
  onClose,
}: {
  /** The connection to configure, or null to create a new one. */
  connection: Connection | null
  existingNames: string[]
  onSave: (draft: Draft) => void
  /** A real email relay was created from this dialog. */
  onRelayCreated: (name: string) => void
  /** A real database connection was created from this dialog. */
  onDatabaseCreated: (name: string) => void
  /** A real API connection was created from this dialog. */
  onApiCreated: (name: string) => void
  onClose: () => void
}) {
  const isNew = connection === null
  const administer = usePermissions().administer
  const [draft, setDraft] = useState<Draft>(connection ? toDraft(connection) : blank)
  const [relay, setRelay] = useState(blankRelay)
  const [relayErrors, setRelayErrors] = useState<RelayErrors>({})
  const [db, setDb] = useState(blankDb)
  const [dbErrors, setDbErrors] = useState<DbErrors>({})
  const [api, setApi] = useState(blankApi)
  const [apiErrors, setApiErrors] = useState<ApiErrors>({})
  const [saving, setSaving] = useState(false)
  const [submitted, setSubmitted] = useState(false)
  const [testing, setTesting] = useState(false)
  const [result, setResult] = useState<SimulatedTest | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined)
  useEffect(() => () => clearTimeout(timer.current), [])

  const isRelay = isNew && draft.auth === SMTP_AUTH
  const isDb = isNew && draft.auth === SQL_AUTH
  const isApi = isNew && draft.auth === API_AUTH

  const set = (patch: Partial<Draft>) => {
    setDraft((d) => ({ ...d, ...patch }))
    setResult(null)
  }
  const name = draft.name.trim()
  const errors = {
    name: !name ? 'Give the connection a name.' : existingNames.some((n) => n.toLowerCase() === name.toLowerCase()) ? 'A connection with that name already exists.' : undefined,
    endpoint: !draft.endpoint.trim() ? 'Endpoint or host is required.' : undefined,
  }

  const test = () => {
    setTesting(true)
    setResult(null)
    timer.current = setTimeout(() => {
      setTesting(false)
      setResult(simulateTest({ name: name || 'connection', health: connection?.health ?? 'ok' }))
    }, 600)
  }

  const saveRelay = async () => {
    const local = validateRelay(relay, draft.name, false)
    if (errors.name && draft.name.trim()) local.name = errors.name
    setRelayErrors(local)
    if (Object.keys(local).length > 0) return
    setSaving(true)
    try {
      await emailRelaysApi.create(relayBody(relay, draft.name))
      setRelay((r) => ({ ...r, password: '' })) // the typed password does not stay in memory longer than needed
      onRelayCreated(draft.name.trim())
    } catch (e) {
      setRelayErrors(relayErrorsOf(e, 'Could not create the email connection.'))
    } finally {
      setSaving(false)
    }
  }

  const saveDb = async () => {
    const local = validateDb(db, draft.name, false)
    if (errors.name && draft.name.trim()) local.name = errors.name
    setDbErrors(local)
    if (Object.keys(local).length > 0) return
    setSaving(true)
    try {
      await databaseConnectionsApi.create(dbBody(db, draft.name))
      setDb((d) => ({ ...d, password: '' })) // the typed password does not stay in memory longer than needed
      onDatabaseCreated(draft.name.trim())
    } catch (e) {
      setDbErrors(dbErrorsOf(e, 'Could not create the database connection.'))
    } finally {
      setSaving(false)
    }
  }

  const saveApi = async () => {
    const local = validateApi(api, draft.name, false)
    if (errors.name && draft.name.trim()) local.name = errors.name
    setApiErrors(local)
    if (Object.keys(local).length > 0) return
    setSaving(true)
    try {
      await apiConnectionsApi.create(apiBody(api, draft.name))
      setApi((a) => ({ ...a, secret: '' })) // the typed secret does not stay in memory longer than needed
      onApiCreated(draft.name.trim())
    } catch (e) {
      setApiErrors(apiErrorsOf(e, 'Could not create the API connection.'))
    } finally {
      setSaving(false)
    }
  }

  const save = () => {
    if (isApi) {
      void saveApi()
      return
    }
    if (isRelay) {
      void saveRelay()
      return
    }
    if (isDb) {
      void saveDb()
      return
    }
    setSubmitted(true)
    if (errors.name || errors.endpoint) return
    onSave({ ...draft, name, endpoint: draft.endpoint.trim(), category: draft.category.trim(), description: draft.description.trim() })
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={isNew ? 'New connection' : connection.name}
      description={isNew ? 'Shared across every workflow.' : connection.category || connection.group}
      footer={
        <>
          {!isRelay && !isDb && !isApi && (
            <Button type="button" onClick={test} disabled={testing} className="mr-auto">
              <PlugZap />
              {testing ? 'Testing…' : 'Test connection'}
            </Button>
          )}
          <Button type="button" onClick={onClose} className={isRelay || isDb || isApi ? 'ml-auto' : undefined}>
            Cancel
          </Button>
          <Button type="button" variant="primary" onClick={save} disabled={saving || ((isRelay || isDb || isApi) && !administer)}>
            {isNew ? 'Create connection' : 'Save settings'}
          </Button>
        </>
      }
    >
      <form className="flex flex-col gap-3.5" onSubmit={(e) => { e.preventDefault(); save() }} noValidate>
        <Field label="Name" htmlFor="cn-name" error={isRelay ? relayErrors.name : isDb ? dbErrors.name : isApi ? apiErrors.name : submitted ? errors.name : undefined}>
          <Input id="cn-name" value={draft.name} onChange={(e) => set({ name: e.target.value })} aria-invalid={isRelay ? !!relayErrors.name : isDb ? !!dbErrors.name : isApi ? !!apiErrors.name : submitted && !!errors.name} placeholder="e.g. Finance API" autoFocus />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Group" htmlFor="cn-group">
            <Select id="cn-group" value={draft.group} onChange={(e) => set({ group: e.target.value as ConnectionGroup })}>
              {CONNECTION_GROUPS.map((g) => (
                <option key={g.name}>{g.name}</option>
              ))}
            </Select>
          </Field>
          <Field label="Authentication" htmlFor="cn-auth" hint={isNew && !isRelay && !isDb && !isApi ? 'Choose SMTP credentials for a real email connection, Database login for a real database (SQL Server, PostgreSQL, MySQL / MariaDB, Oracle), or HTTP API for a real web API.' : undefined}>
            <Select id="cn-auth" value={draft.auth} onChange={(e) => set({ auth: e.target.value, ...(e.target.value === SMTP_AUTH ? { group: 'Delivery channels' as const } : {}), ...(e.target.value === SQL_AUTH || e.target.value === API_AUTH ? { group: 'Data sources' as const } : {}) })}>
              {[...new Set([...AUTH_METHODS, draft.auth])].map((a) => (
                <option key={a}>{a}</option>
              ))}
            </Select>
          </Field>
        </div>
        {isApi ? (
          <>
            {!administer && <p role="alert" className="text-[13px] text-err">Only administrators can create API connections.</p>}
            <ApiFields form={api} errors={apiErrors} existing={null} onChange={(patch) => { setApi((a) => ({ ...a, ...patch })); setApiErrors({}) }} />
            <p className="text-xs text-ink-3">After you create it, open Configure and use Test connection. It sends one GET to the test path; the response body is never kept.</p>
          </>
        ) : isDb ? (
          <>
            {!administer && <p role="alert" className="text-[13px] text-err">Only administrators can create database connections.</p>}
            <DbFields form={db} errors={dbErrors} existing={null} onChange={(patch) => { setDb((d) => ({ ...d, ...patch })); setDbErrors({}) }} />
            <p className="text-xs text-ink-3">After you create it, open Configure and use Test connection. It connects and runs SELECT 1 only; no query is run.</p>
          </>
        ) : isRelay ? (
          <>
            {!administer && <p role="alert" className="text-[13px] text-err">Only administrators can create email connections.</p>}
            <RelayFields form={relay} errors={relayErrors} existing={null} onChange={(patch) => { setRelay((r) => ({ ...r, ...patch })); setRelayErrors({}) }} />
            <p className="text-xs text-ink-3">After you create it, open Configure and use Test connection to send a test message through it.</p>
          </>
        ) : (
          <>
            <Field label="Type" htmlFor="cn-category" hint="Protocol or format, e.g. OData v4 · API / HTTP endpoint">
              <Input id="cn-category" value={draft.category} onChange={(e) => set({ category: e.target.value })} />
            </Field>
            <Field label="Endpoint / host" htmlFor="cn-endpoint" error={submitted ? errors.endpoint : undefined}>
              <Input id="cn-endpoint" className="font-mono" value={draft.endpoint} onChange={(e) => set({ endpoint: e.target.value })} aria-invalid={submitted && !!errors.endpoint} placeholder="https://api.example.com or host:port" />
            </Field>
            <Field label="Secret" hint="Stored in the bank’s secret store. Verso never asks for it here and never displays it.">
              <Input value="••••••••••••••••" disabled aria-label="Secret (managed outside Verso)" className="font-mono" readOnly />
            </Field>
            <Field label="Description" htmlFor="cn-desc">
              <Textarea id="cn-desc" rows={2} value={draft.description} onChange={(e) => set({ description: e.target.value })} placeholder="What is this connection for?" />
            </Field>
            <div aria-live="polite" className={cn('min-h-5 text-[13px]', result && (result.ok ? 'text-ok' : 'text-err'))}>
              {testing && <span className="text-ink-3">Testing…</span>}
              {result?.message}
            </div>
            <p className="text-xs text-ink-3">Prototype: connections are not saved anywhere and the test is simulated. No external service is contacted.</p>
          </>
        )}
      </form>
    </Dialog>
  )
}

export type { Draft as ConnectionDraft }
