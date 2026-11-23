import { useEffect, useRef, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Field, Input, Select, Switch, Textarea } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import type { SmsConnectionDto } from '@/lib/api/generated'

/** SMS connection form shared by "New connection" (Authentication: SMS (Brevo)) and "Configure" on an SMS card (ADR 0018). */

export const SMS_AUTH = 'SMS (Brevo)'
export const BREVO_BASE_URL = 'https://api.brevo.com/v3'

export type SmsMessageType = 'transactional' | 'marketing'

export const SMS_MESSAGE_TYPES: { id: SmsMessageType; name: string }[] = [
  { id: 'transactional', name: 'Transactional (service messages)' },
  { id: 'marketing', name: 'Marketing (promotions)' },
]

/** The server refuses every workflow send through a marketing connection (sms.marketing_not_allowed), whatever the switch says. */
export const MARKETING_NOT_IN_WORKFLOWS = 'Marketing connections can’t be used in workflows.'

const CONNECTION_KEY =/^[a-z0-9][a-z0-9-]{1,63}$/
const SENDER = /^([A-Za-z0-9]{1,11}|[0-9]{1,15})$/

export interface SmsForm {
  key: string
  baseUrl: string
  sender: string
  messageType: SmsMessageType
  unicodeEnabled: boolean
  organisationPrefix: string
  timeoutSeconds: string
  description: string
  secret: string
  /** Workflows may send real SMS through this connection (Send SMS step). */
  allowSending: boolean
  /** The switch was changed in this dialog. Only then is allowSending sent when saving an existing connection, so an unrelated edit never overwrites a change made by someone else. */
  sendingTouched: boolean
  /** Country prefixes the Send SMS step may send to: digits, comma-separated. Empty allows no country. */
  allowedCountryPrefixes: string
  /** The prefixes were edited in this dialog. Only then are they sent when saving an existing connection, so a stale dialog never overwrites (and so widens) a list changed by someone else. */
  prefixesTouched: boolean
}

export const blankSms: SmsForm = { key: '', baseUrl: BREVO_BASE_URL, sender: '', messageType: 'transactional', unicodeEnabled: true, organisationPrefix: '', timeoutSeconds: '15', description: '', secret: '', allowSending: false, sendingTouched: true, allowedCountryPrefixes: '', prefixesTouched: true }

export const toSmsForm = (c: SmsConnectionDto): SmsForm => ({
  key: c.key,
  baseUrl: c.baseUrl,
  sender: c.sender,
  messageType: c.messageType === 'marketing' ? 'marketing' : 'transactional',
  unicodeEnabled: c.unicodeEnabled,
  organisationPrefix: c.organisationPrefix,
  timeoutSeconds: String(c.timeoutSeconds),
  description: c.description,
  secret: '', // never filled in: the saved API key is not sent to the browser
  allowSending: c.allowSending === true,
  sendingTouched: false,
  allowedCountryPrefixes: c.allowedCountryPrefixes ?? '',
  prefixesTouched: false,
})

const isHttps = (u: string) => {
  try {
    const url = new URL(u.trim())
    return url.protocol === 'https:' && url.username === '' && url.password === '' && url.hash === '' && url.search === ''
  } catch {
    return false
  }
}

/** The number in international format without "+" (what the server sends on), or null. Mirrors the server's rule. */
export function normaliseRecipient(input: string): string | null {
  const text = input.trim()
  if (!text || text.length > 40) return null
  let value = ''
  for (let i = 0; i < text.length; i++) {
    const c = text.charAt(i)
    if ((c === '+' && i === 0) || ' .-()'.includes(c)) continue
    if (c < '0' || c > '9') return null
    value += c
  }
  if (!text.startsWith('+') && value.startsWith('00')) value = value.slice(2)
  return /^[1-9][0-9]{7,14}$/.test(value) ? value : null
}

export type SmsErrors = Record<string, string>

export function validateSms(form: SmsForm, name: string, existing: boolean): SmsErrors {
  const errors: SmsErrors = {}
  if (!name.trim()) errors.name = 'Give the connection a name.'
  if (!existing && !CONNECTION_KEY.test(form.key)) errors.key = 'Lower-case letters, digits and hyphens, 2–64 characters.'
  if (!isHttps(form.baseUrl)) errors.baseUrl = 'Enter the base address starting with https:// (no login and no ? query), such as https://api.brevo.com/v3.'
  if (!SENDER.test(form.sender.trim())) errors.sender = 'Enter a sender of up to 11 letters and digits (no spaces), or a number of up to 15 digits.'
  if (!existing && form.secret === '') errors.secret = 'Enter the Brevo API key.'
  if (/\s/.test(form.secret)) errors.secret = 'The API key has no spaces.'
  return errors
}

/** The request body. The API key is null when left empty (the server keeps the saved one). */
export function smsBody(form: SmsForm, name: string) {
  return {
    key: form.key,
    name: name.trim(),
    provider: 'brevo',
    baseUrl: form.baseUrl.trim(),
    sender: form.sender.trim(),
    messageType: form.messageType,
    unicodeEnabled: form.unicodeEnabled,
    organisationPrefix: form.organisationPrefix.trim(),
    timeoutSeconds: Number(form.timeoutSeconds),
    description: form.description.trim(),
    secret: form.secret === '' ? null : form.secret,
    allowSending: form.sendingTouched ? form.allowSending : null, // null: the server keeps the saved value
    allowedCountryPrefixes: form.prefixesTouched ? form.allowedCountryPrefixes.trim() : null, // checked and normalised by the server; null keeps the saved list
  }
}

export function smsErrorsOf(error: unknown, fallback: string): SmsErrors {
  if (error instanceof ApiError) {
    if (error.code === 'sms_connection.key_taken') return { key: 'A connection with this key already exists.' }
    const out: SmsErrors = {}
    for (const [field, messages] of Object.entries(error.problem.errors ?? {})) {
      if (messages[0]) out[field.charAt(0).toLowerCase() + field.slice(1)] = messages[0]
    }
    return Object.keys(out).length > 0 ? out : { form: error.message }
  }
  return { form: fallback }
}

/** The fields (no name: the dialog above owns it). `existing` is the connection being edited, or null for a new one. */
export function SmsFields({
  form,
  errors,
  existing,
  onChange,
}: {
  form: SmsForm
  errors: SmsErrors
  existing: SmsConnectionDto | null
  onChange: (patch: Partial<SmsForm>) => void
}) {
  const [confirmingSending, setConfirmingSending] = useState(false)
  const keepOff = useRef<HTMLButtonElement>(null)
  useEffect(() => {
    if (!confirmingSending) return
    keepOff.current?.focus() // the safe choice has focus
    // Escape cancels the question only; the window listener runs before the dialog's own Escape handling.
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return
      e.stopPropagation()
      setConfirmingSending(false)
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [confirmingSending])
  return (
    <>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Sender" htmlFor="sms-sender" error={errors.sender} hint="What the phone shows: up to 11 letters and digits, or a number.">
          <Input id="sms-sender" value={form.sender} onChange={(e) => onChange({ sender: e.target.value })} aria-invalid={!!errors.sender} placeholder="YourBank" autoComplete="off" maxLength={15} />
        </Field>
        <Field label="Type" htmlFor="sms-type" error={errors.messageType}>
          <Select id="sms-type" value={form.messageType} onChange={(e) => onChange({ messageType: e.target.value as SmsMessageType })}>
            {SMS_MESSAGE_TYPES.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          </Select>
        </Field>
      </div>
      {form.messageType === 'marketing' && (
        <p className="text-xs text-ink-3">Marketing SMS needs the recipient’s consent and opt-out wording in many countries. That is the sender’s responsibility.</p>
      )}
      <Switch checked={form.unicodeEnabled} onCheckedChange={(v) => onChange({ unicodeEnabled: v })} label="Allow Unicode (accents, Greek, emoji; shorter messages)" />
      <Field label="Organisation prefix (optional)" htmlFor="sms-prefix" error={errors.organisationPrefix} hint="Text the provider puts in front of the message.">
        <Input id="sms-prefix" value={form.organisationPrefix} onChange={(e) => onChange({ organisationPrefix: e.target.value })} aria-invalid={!!errors.organisationPrefix} autoComplete="off" />
      </Field>
      <Field label="Base address" htmlFor="sms-base" error={errors.baseUrl} hint="https only. Brevo’s is https://api.brevo.com/v3.">
        <Input id="sms-base" className="font-mono" value={form.baseUrl} onChange={(e) => onChange({ baseUrl: e.target.value })} aria-invalid={!!errors.baseUrl} autoComplete="off" />
      </Field>
      <div className="grid grid-cols-[1fr_8rem] gap-3">
        <Field
          label="API key"
          htmlFor="sms-secret"
          error={errors.secret}
          hint={existing?.hasSecret ? 'A key is saved. Leave empty to keep it; typing replaces it.' : 'Stored encrypted on the server. Never shown again.'}
        >
          <Input
            id="sms-secret"
            type="password"
            autoComplete="new-password"
            value={form.secret}
            onChange={(e) => onChange({ secret: e.target.value })}
            aria-invalid={!!errors.secret}
            placeholder={existing?.hasSecret ? '••••••••' : ''}
          />
        </Field>
        <Field label="Timeout (seconds)" htmlFor="sms-timeout" error={errors.timeoutSeconds}>
          <Input id="sms-timeout" inputMode="numeric" value={form.timeoutSeconds} onChange={(e) => onChange({ timeoutSeconds: e.target.value.replace(/\D/g, '') })} />
        </Field>
      </div>
      <Field label="Description" htmlFor="sms-desc" error={errors.description}>
        <Textarea id="sms-desc" rows={2} value={form.description} onChange={(e) => onChange({ description: e.target.value })} placeholder="What is this connection for?" />
      </Field>
      <div className="flex flex-col gap-1.5">
        <Switch
          checked={form.allowSending}
          onCheckedChange={(on) => {
            if (on) {
              setConfirmingSending(true) // turning sending on asks first
            } else {
              setConfirmingSending(false)
              onChange({ allowSending: false, sendingTouched: true })
            }
          }}
          label="Allow workflows to send SMS"
        />
        {confirmingSending && (
          <div role="group" aria-label="Confirm sending" className="flex flex-col gap-2 rounded-md border border-warn/50 bg-warn-soft p-3 text-[13px] text-ink">
            <p className="m-0">Workflows will be able to send real SMS to numbers taken from their data, within the allowed countries. Each message costs a credit and cannot be recalled.</p>
            <div className="flex gap-2">
              <Button type="button" size="sm" variant="primary" onClick={() => { setConfirmingSending(false); onChange({ allowSending: true, sendingTouched: true }) }}>
                Allow sending
              </Button>
              <Button ref={keepOff} type="button" size="sm" onClick={() => setConfirmingSending(false)}>
                Keep sending off
              </Button>
            </div>
          </div>
        )}
        <p className="m-0 text-xs text-ink-3">
          {form.allowSending
            ? form.messageType === 'marketing'
              ? 'Marketing connections can’t be used in workflows, even with sending on. Choose Transactional for service messages.'
              : 'Send SMS steps in workflows can send real messages through this connection, only to the countries listed below.'
            : 'Off: workflows cannot send SMS through this connection. Test connection still works.'}
        </p>
      </div>
      <Field
        label="Allowed country prefixes"
        htmlFor="sms-countries"
        error={errors.allowedCountryPrefixes}
        hint="Digits without +, separated by commas, for example 357, 30. Nothing can be sent to a country that is not listed."
      >
        <Input
          id="sms-countries"
          className="font-mono"
          inputMode="numeric"
          value={form.allowedCountryPrefixes}
          onChange={(e) => onChange({ allowedCountryPrefixes: e.target.value, prefixesTouched: true })}
          aria-invalid={!!errors.allowedCountryPrefixes}
          autoComplete="off"
          maxLength={200}
          placeholder="357, 30"
        />
      </Field>
      <Field label="Connection key" htmlFor="sms-key" error={errors.key} hint="A short name for this connection in workflows later. It cannot change.">
        <Input id="sms-key" className="font-mono" value={form.key} disabled={!!existing} onChange={(e) => onChange({ key: e.target.value.toLowerCase() })} aria-invalid={!!errors.key} placeholder="brevo-sms" />
      </Field>
      {errors.form && <p role="alert" className="text-[13px] text-err">{errors.form}</p>}
    </>
  )
}
