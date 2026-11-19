import { useEffect, useRef, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Field, Input, Select, Switch } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import type { DatabaseConnectionDto } from '@/lib/api/generated'

/** Database connection form shared by "New connection" (Authentication: Database login) and "Configure" on a database card (ADR 0016 and its addendum 1). */

export const SQL_AUTH = 'Database login'

export type DbProvider = 'SqlServer' | 'PostgreSql' | 'MySql' | 'Oracle'

export const DB_PROVIDERS: { id: DbProvider; name: string; port: number }[] = [
  { id: 'SqlServer', name: 'SQL Server', port: 1433 },
  { id: 'PostgreSql', name: 'PostgreSQL', port: 5432 },
  { id: 'MySql', name: 'MySQL / MariaDB', port: 3306 },
  { id: 'Oracle', name: 'Oracle', port: 1521 },
]

export const providerName = (id: string) => DB_PROVIDERS.find((p) => p.id === id)?.name ?? id

const CONNECTION_KEY = /^[a-z0-9][a-z0-9-]{1,63}$/
const SQL_SERVER_HOST = /^[A-Za-z0-9]([A-Za-z0-9.\-_]*[A-Za-z0-9])?(\\[A-Za-z0-9_$#@-]{1,64})?(,[0-9]{1,5})?$/
const PLAIN_HOST = /^[A-Za-z0-9]([A-Za-z0-9.\-_]*[A-Za-z0-9])?$/
const DATABASE_NAME = /^[A-Za-z0-9_.$#@-]{1,128}$/

export interface DbForm {
  provider: DbProvider
  key: string
  server: string
  port: string
  database: string
  userName: string
  password: string
  clearPassword: boolean
  encrypt: boolean
  trustServerCertificate: boolean
  timeoutSeconds: string
  /** Workflows may run INSERT, UPDATE and DELETE on this connection (Write to database step). */
  allowWrites: boolean
  /** The switch was changed in this dialog. Only then is allowWrites sent when saving an existing connection, so an unrelated edit never overwrites a change made by someone else. */
  writesTouched: boolean
}

export const blankDb: DbForm = { provider: 'SqlServer', key: '', server: '', port: '', database: '', userName: '', password: '', clearPassword: false, encrypt: true, trustServerCertificate: false, timeoutSeconds: '15', allowWrites: false, writesTouched: true }

const asProvider = (p: string): DbProvider => (DB_PROVIDERS.some((x) => x.id === p) ? (p as DbProvider) : 'SqlServer')

export const toDbForm = (c: DatabaseConnectionDto): DbForm => ({
  provider: asProvider(c.provider),
  key: c.key,
  server: c.server,
  port: c.port > 0 ? String(c.port) : '',
  database: c.database,
  userName: c.userName,
  password: '', // never filled in: the saved password is not sent to the browser
  clearPassword: false,
  encrypt: c.encrypt,
  trustServerCertificate: c.trustServerCertificate,
  timeoutSeconds: String(c.timeoutSeconds),
  allowWrites: c.allowWrites === true,
  writesTouched: false,
})

export type DbErrors = Record<string, string>

export function validateDb(form: DbForm, name: string, existing: boolean): DbErrors {
  const errors: DbErrors = {}
  const server = form.server.trim()
  if (!name.trim()) errors.name = 'Give the connection a name.'
  if (!existing && !CONNECTION_KEY.test(form.key)) errors.key = 'Lower-case letters, digits and hyphens, 2–64 characters.'
  if (form.provider === 'SqlServer' ? !SQL_SERVER_HOST.test(server) : !PLAIN_HOST.test(server)) {
    errors.server = form.provider === 'SqlServer' ? 'Enter a server name such as sql.example.com, optionally with \\INSTANCE or ,port.' : 'Enter a host name or address such as db.example.com (the port is its own field).'
  }
  if (form.port !== '' && (Number(form.port) < 1 || Number(form.port) > 65535)) errors.port = 'The port must be 1–65535.'
  if (form.provider === 'Oracle' && !DATABASE_NAME.test(form.database.trim())) errors.database = 'The service name is required.'
  if (!form.userName.trim()) errors.userName = 'Enter the login.'
  return errors
}

/** The request body. The password is null when left empty (the server keeps the saved one). Port 0 means the type's default. */
export function dbBody(form: DbForm, name: string) {
  return {
    key: form.key,
    provider: form.provider,
    name: name.trim(),
    server: form.server.trim(),
    port: form.port === '' ? 0 : Number(form.port),
    database: form.database.trim(),
    userName: form.userName.trim(),
    encrypt: form.encrypt,
    trustServerCertificate: form.provider === 'Oracle' ? false : form.trustServerCertificate,
    timeoutSeconds: Number(form.timeoutSeconds),
    password: form.password === '' ? null : form.password,
    clearPassword: form.clearPassword,
    allowWrites: form.writesTouched ? form.allowWrites : null, // null: the server keeps the saved value
  }
}

export function dbErrorsOf(error: unknown, fallback: string): DbErrors {
  if (error instanceof ApiError) {
    if (error.code === 'database_connection.key_taken') return { key: 'A connection with this key already exists.' }
    const out: DbErrors = {}
    for (const [field, messages] of Object.entries(error.problem.errors ?? {})) {
      if (messages[0]) out[field.charAt(0).toLowerCase() + field.slice(1)] = messages[0]
    }
    return Object.keys(out).length > 0 ? out : { form: error.message }
  }
  return { form: fallback }
}

/** The fields (no name: the dialog above owns it). `existing` is the connection being edited, or null for a new one. */
export function DbFields({
  form,
  errors,
  existing,
  onChange,
}: {
  form: DbForm
  errors: DbErrors
  existing: DatabaseConnectionDto | null
  onChange: (patch: Partial<DbForm>) => void
}) {
  const defaultPort = DB_PROVIDERS.find((p) => p.id === form.provider)?.port ?? 0
  const oracle = form.provider === 'Oracle'
  const [confirmingWrites, setConfirmingWrites] = useState(false)
  const keepOff = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    if (!confirmingWrites) return
    keepOff.current?.focus() // the safe choice has focus
    // Escape cancels the question only; the window listener runs before the dialog's own Escape handling.
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return
      e.stopPropagation()
      setConfirmingWrites(false)
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [confirmingWrites])
  return (
    <>
      <Field label="Database type" htmlFor="db-provider" hint={existing ? 'The type cannot change after creation.' : (form.allowWrites ? 'Choose the engine. Use a non-production server and a login limited to what your workflows need.' : 'Choose the engine. Use a read-only account on a non-production server.')}>
        <Select id="db-provider" value={form.provider} disabled={!!existing} onChange={(e) => onChange({ provider: e.target.value as DbProvider, trustServerCertificate: e.target.value === 'Oracle' ? false : form.trustServerCertificate })}>
          {DB_PROVIDERS.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </Select>
      </Field>
      <div className="grid grid-cols-[1fr_6.5rem] gap-3">
        <Field
          label="Server name"
          htmlFor="db-server"
          error={errors.server}
          hint={form.provider === 'SqlServer' ? 'Host name or address, optionally with \\INSTANCE or ,port.' : 'Host name or address.'}
        >
          <Input id="db-server" className="font-mono" value={form.server} onChange={(e) => onChange({ server: e.target.value })} aria-invalid={!!errors.server} placeholder="db.example.com" autoComplete="off" />
        </Field>
        <Field label="Port" htmlFor="db-port" error={errors.port}>
          <Input id="db-port" inputMode="numeric" className="font-mono" value={form.port} onChange={(e) => onChange({ port: e.target.value.replace(/\D/g, '') })} placeholder={String(defaultPort)} aria-invalid={!!errors.port} />
        </Field>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Login" htmlFor="db-user" error={errors.userName}>
          <Input id="db-user" value={form.userName} onChange={(e) => onChange({ userName: e.target.value })} aria-invalid={!!errors.userName} autoComplete="off" />
        </Field>
        <Field
          label="Password"
          htmlFor="db-password"
          error={errors.password}
          hint={existing?.hasPassword ? 'A password is saved. Leave empty to keep it; typing replaces it.' : 'Stored encrypted on the server. Never shown again.'}
        >
          <Input
            id="db-password"
            type="password"
            autoComplete="new-password"
            value={form.password}
            onChange={(e) => onChange({ password: e.target.value, clearPassword: false })}
            placeholder={existing?.hasPassword ? '••••••••' : ''}
          />
        </Field>
      </div>
      {existing?.hasPassword && (
        <label className="inline-flex items-center gap-2 text-[13px] text-ink-2">
          <input type="checkbox" className="size-4 accent-cyan" checked={form.clearPassword} onChange={(e) => onChange({ clearPassword: e.target.checked, password: '' })} />
          Remove the saved password
        </label>
      )}
      <div className="grid grid-cols-[1fr_8rem] gap-3">
        <Field
          label={oracle ? 'Service name' : 'Database (optional)'}
          htmlFor="db-database"
          error={errors.database}
          hint={oracle ? 'The Oracle service name, for example ORCLPDB1.' : 'Empty uses the login’s default database.'}
        >
          <Input id="db-database" className="font-mono" value={form.database} onChange={(e) => onChange({ database: e.target.value })} aria-invalid={!!errors.database} autoComplete="off" />
        </Field>
        <Field label="Timeout (seconds)" htmlFor="db-timeout" error={errors.timeoutSeconds}>
          <Input id="db-timeout" inputMode="numeric" value={form.timeoutSeconds} onChange={(e) => onChange({ timeoutSeconds: e.target.value.replace(/\D/g, '') })} />
        </Field>
      </div>
      <div className="flex flex-col gap-1.5 text-[13px] text-ink-2">
        <label className="inline-flex items-center gap-2">
          <input type="checkbox" className="size-4 accent-cyan" checked={form.encrypt} onChange={(e) => onChange({ encrypt: e.target.checked })} />
          {oracle ? 'Encrypt the connection (TLS / TCPS, recommended)' : 'Encrypt the connection (recommended)'}
        </label>
        {oracle ? (
          <p className="m-0 text-xs text-ink-3">Oracle checks the server certificate with the Worker’s certificate store or wallet; install the certificate authority there.</p>
        ) : (
          <label className="inline-flex items-center gap-2">
            <input type="checkbox" className="size-4 accent-cyan" checked={form.trustServerCertificate} onChange={(e) => onChange({ trustServerCertificate: e.target.checked })} />
            Trust the server certificate (non-production only)
          </label>
        )}
      </div>
      <Field label="Connection key" htmlFor="db-key" error={errors.key} hint="A short name for this connection in workflows later. It cannot change.">
        <Input id="db-key" className="font-mono" value={form.key} disabled={!!existing} onChange={(e) => onChange({ key: e.target.value.toLowerCase() })} aria-invalid={!!errors.key} placeholder="lasernet" />
      </Field>
      <div className="flex flex-col gap-1.5">
        <Switch
          checked={form.allowWrites}
          onCheckedChange={(on) => {
            if (on) {
              setConfirmingWrites(true) // turning writes on asks first
            } else {
              setConfirmingWrites(false)
              onChange({ allowWrites: false, writesTouched: true })
            }
          }}
          label="Allow workflows to change data"
        />
        <p className="m-0 text-xs text-ink-3">Only switch this on for a login that can write just the tables your workflows need.</p>
        {confirmingWrites && (
          <div role="group" aria-label="Confirm writes" className="flex flex-col gap-2 rounded-md border border-warn/50 bg-warn-soft p-3 text-[13px] text-ink">
            <p className="m-0">Workflows will be able to run INSERT, UPDATE and DELETE statements on this connection. Verso runs only the statement an author typed, with this login's permissions.</p>
            <div className="flex gap-2">
              <Button type="button" size="sm" variant="primary" onClick={() => { setConfirmingWrites(false); onChange({ allowWrites: true, writesTouched: true }) }}>
                Allow changes
              </Button>
              <Button ref={keepOff} type="button" size="sm" onClick={() => setConfirmingWrites(false)}>
                Keep writes off
              </Button>
            </div>
          </div>
        )}
      </div>
      <p className="m-0 text-xs text-ink-3">
        {form.allowWrites
          ? 'This login can change data. Give it permission to write only the tables your workflows need; the database login is the real protection, not Verso’s checks.'
          : 'Use a read-only login: Verso checks that queries are a single SELECT, but the database login is the real protection.'}
      </p>
      {errors.form && <p role="alert" className="text-[13px] text-err">{errors.form}</p>}
    </>
  )
}
