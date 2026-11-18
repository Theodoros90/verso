import { PlugZap } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'

import { usePermissions } from '@/app/AuthGate'
import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input, Switch } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import type { SmsConnectionDto, SmsConnectionTestDto } from '@/lib/api/generated'
import { smsConnectionsApi } from '@/lib/api/smsConnections'

import { normaliseRecipient, SmsFields, smsBody, smsErrorsOf, toSmsForm, validateSms, type SmsErrors } from './SmsFields'

/** The real SMS connections (ADR 0018), loaded for administrators only. `reload` after any change. */
export function useSmsConnections() {
  const administer = usePermissions().administer
  const [connections, setConnections] = useState<SmsConnectionDto[]>([])
  const [loadError, setLoadError] = useState(false)

  const reload = useCallback(
    async (signal?: AbortSignal) => {
      if (!administer) return
      try {
        const items = await smsConnectionsApi.list(true, signal)
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

/** One real SMS connection, in the same card layout as the others. The switch activates or deactivates it. */
export function SmsCard({ connection, onToggle, onConfigure }: { connection: SmsConnectionDto; onToggle: (active: boolean) => void; onConfigure: () => void }) {
  const missingSecret = connection.isActive && !connection.hasSecret
  return (
    <li className={`flex flex-col gap-3 rounded-[10px] border border-line bg-surface p-4 ${connection.isActive ? '' : 'opacity-70'}`}>
      <div className="flex items-center gap-3">
        <div aria-hidden="true" className="grid size-[38px] flex-none place-items-center rounded-[9px] bg-[#0B7A5A] text-[13px] font-bold text-white">SMS</div>
        <div className="min-w-0 flex-1">
          <h3 className="m-0 text-[14.5px] font-semibold">{connection.name}</h3>
          <div className="text-xs text-ink-3">SMS · Brevo · key <code className="font-mono">{connection.key}</code></div>
        </div>
        <Switch checked={connection.isActive} onCheckedChange={onToggle} label={`Enable ${connection.name}`} hideLabel />
      </div>
      <p className="m-0 text-[13px] text-ink-2">Real: stored on the server. Test connection sends one real SMS to the number you type.</p>
      <dl className="m-0 mt-auto flex items-baseline justify-between gap-3 text-[12.5px]">
        <dt className="text-ink-3">Sender</dt>
        <dd className="m-0 min-w-0 text-right font-mono text-[11.5px] break-words text-ink-2">
          {connection.sender} · {connection.messageType}
        </dd>
      </dl>
      <div className="flex flex-wrap items-center gap-2 text-[12.5px] text-ink-3">
        <StatusPill tone={!connection.isActive ? 'neutral' : missingSecret ? 'warn' : 'ok'}>
          {!connection.isActive ? 'Off' : missingSecret ? 'No key' : 'Ready'}
        </StatusPill>
        {connection.hasSecret && <span>Key saved</span>}
        <Button type="button" size="sm" className="ml-auto" onClick={onConfigure} aria-label={`Configure ${connection.name}`}>
          Configure
        </Button>
      </div>
    </li>
  )
}

export const SMS_TEST_POLL_MS = 1000
export const SMS_TEST_POLL_LIMIT = 30

type TestState = { kind: 'idle' } | { kind: 'running' } | { kind: 'done'; test: SmsConnectionTestDto } | { kind: 'error'; message: string }

/** Queues a test (the Worker sends one real SMS) and polls until it finishes. */
function useSmsTest(connectionId: string) {
  const [state, setState] = useState<TestState>({ kind: 'idle' })
  const alive = useRef(true)
  useEffect(() => {
    alive.current = true
    return () => {
      alive.current = false
    }
  }, [])

  const run = async (recipient: string) => {
    setState({ kind: 'running' })
    try {
      let test = await smsConnectionsApi.requestTest(connectionId, recipient)
      for (let i = 0; i < SMS_TEST_POLL_LIMIT && (test.status === 'Queued' || test.status === 'Running'); i++) {
        await new Promise((resolve) => setTimeout(resolve, SMS_TEST_POLL_MS))
        if (!alive.current) return
        test = await smsConnectionsApi.getTest(test.id)
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

const summary = (t: SmsConnectionTestDto) =>
  [
    t.httpStatus != null ? `HTTP ${t.httpStatus}` : null,
    t.elapsedMs != null ? `${t.elapsedMs} ms` : null,
    t.smsCount != null ? `${t.smsCount} SMS ${t.smsCount === 1 ? 'part' : 'parts'}` : null,
    t.remainingCredits != null ? `${t.remainingCredits} credits left` : null,
    t.messageId ? `message ${t.messageId}` : null,
  ]
    .filter(Boolean)
    .join(' · ')

/** Edit an existing SMS connection. The key cannot change; the API key is write-only (empty keeps it). Test connection sends one real SMS. */
export function SmsEditDialog({ connection, onClose, onSaved }: { connection: SmsConnectionDto; onClose: () => void; onSaved: (name: string) => Promise<void> }) {
  const [name, setName] = useState(connection.name)
  const [form, setForm] = useState(toSmsForm(connection))
  const [errors, setErrors] = useState<SmsErrors>({})
  const [busy, setBusy] = useState(false)
  const [asking, setAsking] = useState(false)
  const [recipient, setRecipient] = useState('')
  const [recipientError, setRecipientError] = useState<string>()
  const test = useSmsTest(connection.id)
  const testing = test.state.kind === 'running'

  const save = async () => {
    const local = validateSms(form, name, true)
    setErrors(local)
    if (Object.keys(local).length > 0) return
    setBusy(true)
    try {
      await smsConnectionsApi.update(connection.id, smsBody(form, name))
      setForm((f) => ({ ...f, secret: '' }))
      await onSaved(name.trim())
    } catch (e) {
      setErrors(smsErrorsOf(e, 'Could not save the SMS connection.'))
    } finally {
      setBusy(false)
    }
  }

  const send = () => {
    if (normaliseRecipient(recipient) === null) {
      setRecipientError('Enter the phone number in international format, such as +357 99 000000.')
      return
    }
    setRecipientError(undefined)
    void test.run(recipient)
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={connection.name}
      description="SMS (Brevo) · real. Stored on the server; Test connection sends one real SMS."
      footer={
        <>
          <Button type="button" onClick={() => setAsking(true)} disabled={testing || !connection.isActive} className="mr-auto">
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
        <SmsFields form={form} errors={errors} existing={connection} onChange={(patch) => { setForm((f) => ({ ...f, ...patch })); setErrors({}) }} />
        {asking && (
          <div className="flex flex-col gap-2 rounded-lg border border-line bg-surface-2 p-3">
            <Field
              label="Send the test SMS to"
              htmlFor="sms-test-recipient"
              error={recipientError}
              hint="International format, for example +357 99 000000. This sends one real SMS and uses one credit."
            >
              <Input id="sms-test-recipient" type="tel" inputMode="tel" value={recipient} onChange={(e) => setRecipient(e.target.value)} aria-invalid={!!recipientError} placeholder="+357 99 000000" autoComplete="off" />
            </Field>
            <div>
              <Button type="button" variant="primary" size="sm" onClick={send} disabled={testing}>
                {testing ? 'Sending…' : 'Send test SMS'}
              </Button>
            </div>
          </div>
        )}
        <div aria-live="polite" className="min-h-5 text-[13px]">
          {testing && <span className="text-ink-3">Waiting for the Worker to send the SMS…</span>}
          {test.state.kind === 'error' && <span role="alert" className="text-err">{test.state.message}</span>}
          {test.state.kind === 'done' && test.state.test.status === 'Succeeded' && <span className="text-ok">SMS accepted by the provider · {summary(test.state.test)}.</span>}
          {test.state.kind === 'done' && test.state.test.status === 'Failed' && (
            <span role="alert" className="text-err">
              The test failed: {test.state.test.message} <code className="font-mono text-xs">{test.state.test.code}</code>
              {test.state.test.httpStatus != null ? ` · HTTP ${test.state.test.httpStatus}` : ''}
            </span>
          )}
        </div>
        <p className="text-xs text-ink-3">Test connection uses the saved settings (save your changes first). It sends one real SMS to the number you type. The number and the text are never kept; only the status, the time, the message id, the SMS parts and the credits left are shown.</p>
      </form>
    </Dialog>
  )
}
