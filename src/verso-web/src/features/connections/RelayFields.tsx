import { Button } from '@/components/ui/button'
import { Field, Input, Select } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import type { EmailRelayDto } from '@/lib/api/generated'

/** Email (SMTP) relay form shared by "New connection" (Authentication: SMTP credentials) and "Configure" on a relay card (ADR 0015). */

export const SMTP_AUTH = 'SMTP credentials'

const PRESETS = [
  { name: 'Gmail', host: 'smtp.gmail.com', port: 587 },
  { name: 'Outlook 365', host: 'smtp.office365.com', port: 587 },
  { name: 'iCloud', host: 'smtp.mail.me.com', port: 587 },
]

const RELAY_KEY = /^[a-z0-9][a-z0-9-]{1,63}$/

export interface RelayForm {
  key: string
  host: string
  port: string
  security: 'tls' | 'none'
  timeoutSeconds: string
  userName: string
  password: string
  clearPassword: boolean
  fromAddress: string
  fromName: string
  domains: string
}

export const blankRelay: RelayForm = { key: '', host: '', port: '587', security: 'tls', timeoutSeconds: '30', userName: '', password: '', clearPassword: false, fromAddress: '', fromName: 'Verso', domains: '' }

export const toRelayForm = (r: EmailRelayDto): RelayForm => ({
  key: r.key,
  host: r.host,
  port: String(r.port),
  security: r.enableSsl ? 'tls' : 'none',
  timeoutSeconds: String(r.timeoutSeconds),
  userName: r.userName,
  password: '', // never filled in: the saved password is not sent to the browser
  clearPassword: false,
  fromAddress: r.fromAddress,
  fromName: r.fromName,
  domains: r.allowedRecipientDomains.join(', '),
})

export type RelayErrors = Record<string, string>

/** Checks what can be checked before asking the server (the server checks everything again). */
export function validateRelay(form: RelayForm, name: string, existing: boolean): RelayErrors {
  const errors: RelayErrors = {}
  if (!name.trim()) errors.name = 'Give the connection a name.'
  if (!existing && !RELAY_KEY.test(form.key)) errors.key = 'Lower-case letters, digits and hyphens, 2–64 characters.'
  if (!form.host.trim()) errors.host = 'Server address is required.'
  if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.fromAddress.trim())) errors.fromAddress = 'Enter the sender as an email address.'
  return errors
}

/** The request body. The password is null when left empty (the server keeps the saved one). */
export function relayBody(form: RelayForm, name: string) {
  return {
    key: form.key,
    name: name.trim(),
    host: form.host.trim(),
    port: Number(form.port),
    enableSsl: form.security === 'tls',
    userName: form.userName.trim(),
    fromAddress: form.fromAddress.trim(),
    fromName: form.fromName.trim() || 'Verso',
    timeoutSeconds: Number(form.timeoutSeconds),
    allowedRecipientDomains: form.domains.split(/[,\s;]+/).filter(Boolean),
    password: form.password === '' ? null : form.password,
    clearPassword: form.clearPassword,
  }
}

/** Turns a failed call into messages per field (or `form`). */
export function relayErrorsOf(error: unknown, fallback: string): RelayErrors {
  if (error instanceof ApiError) {
    if (error.code === 'email_relay.key_taken') return { key: 'A relay with this key already exists.' }
    const out: RelayErrors = {}
    for (const [field, messages] of Object.entries(error.problem.errors ?? {})) {
      if (messages[0]) out[field.charAt(0).toLowerCase() + field.slice(1)] = messages[0]
    }
    return Object.keys(out).length > 0 ? out : { form: error.message }
  }
  return { form: fallback }
}

/** The fields (no name: the dialog above owns it). `existing` is the relay being edited, or null for a new one. */
export function RelayFields({
  form,
  errors,
  existing,
  onChange,
}: {
  form: RelayForm
  errors: RelayErrors
  existing: EmailRelayDto | null
  onChange: (patch: Partial<RelayForm>) => void
}) {
  return (
    <>
      <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Presets">
        <span className="text-[13px] text-ink-3">Presets</span>
        {PRESETS.map((p) => (
          <Button key={p.name} type="button" size="sm" onClick={() => onChange({ host: p.host, port: String(p.port), security: 'tls' })}>
            {p.name}
          </Button>
        ))}
      </div>
      <div className="grid grid-cols-[1fr_6rem] gap-3">
        <Field label="Server address" htmlFor="er-host" error={errors.host}>
          <Input id="er-host" className="font-mono" value={form.host} onChange={(e) => onChange({ host: e.target.value })} aria-invalid={!!errors.host} placeholder="smtp.example.com" />
        </Field>
        <Field label="Port" htmlFor="er-port" error={errors.port}>
          <Input id="er-port" inputMode="numeric" className="font-mono" value={form.port} onChange={(e) => onChange({ port: e.target.value.replace(/\D/g, '') })} aria-invalid={!!errors.port} />
        </Field>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Security" htmlFor="er-security" hint="TLS (STARTTLS) is recommended. SSL 3.0 is not offered.">
          <Select id="er-security" value={form.security} onChange={(e) => onChange({ security: e.target.value as RelayForm['security'] })}>
            <option value="tls">TLS (STARTTLS)</option>
            <option value="none">None (closed networks only)</option>
          </Select>
        </Field>
        <Field label="Timeout (seconds)" htmlFor="er-timeout" error={errors.timeoutSeconds}>
          <Input id="er-timeout" inputMode="numeric" value={form.timeoutSeconds} onChange={(e) => onChange({ timeoutSeconds: e.target.value.replace(/\D/g, '') })} />
        </Field>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Username" htmlFor="er-user" error={errors.userName} hint="Leave empty for a relay that trusts the Worker’s IP.">
          <Input id="er-user" value={form.userName} onChange={(e) => onChange({ userName: e.target.value })} autoComplete="off" />
        </Field>
        <Field
          label="Password"
          htmlFor="er-password"
          error={errors.password}
          hint={existing?.hasPassword ? 'A password is saved. Leave empty to keep it; typing replaces it.' : 'Stored encrypted on the server. Never shown again.'}
        >
          <Input
            id="er-password"
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
      <div className="grid grid-cols-2 gap-3">
        <Field label="From address" htmlFor="er-from" error={errors.fromAddress}>
          <Input id="er-from" value={form.fromAddress} onChange={(e) => onChange({ fromAddress: e.target.value })} aria-invalid={!!errors.fromAddress} placeholder="billing@example.com" />
        </Field>
        <Field label="From name" htmlFor="er-fromname" error={errors.fromName}>
          <Input id="er-fromname" value={form.fromName} onChange={(e) => onChange({ fromName: e.target.value })} />
        </Field>
      </div>
      <Field label="Relay key" htmlFor="er-key" error={errors.key} hint="Chosen by name in the “Email connection” setting of a Send email step. It cannot change later.">
        <Input id="er-key" className="font-mono" value={form.key} disabled={!!existing} onChange={(e) => onChange({ key: e.target.value.toLowerCase() })} aria-invalid={!!errors.key} placeholder="statements" />
      </Field>
      <Field label="Allowed recipient domains" htmlFor="er-domains" error={errors.allowedRecipientDomains} hint="Optional, separated by commas. When set, a message to any other domain is refused.">
        <Input id="er-domains" className="font-mono" value={form.domains} onChange={(e) => onChange({ domains: e.target.value })} placeholder="customers.example, bank.example" />
      </Field>
      {errors.form && <p role="alert" className="text-[13px] text-err">{errors.form}</p>}
    </>
  )
}
