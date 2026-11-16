import { PlugZap } from 'lucide-react'
import { useCallback, useEffect, useRef, useState } from 'react'

import { usePermissions } from '@/app/AuthGate'
import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input, Switch } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import { emailRelaysApi } from '@/lib/api/emailRelays'
import type { EmailRelayDto, EmailRelayTestDto } from '@/lib/api/generated'

import { relayBody, relayErrorsOf, RelayFields, toRelayForm, validateRelay, type RelayErrors } from './RelayFields'

/** The real email relays (ADR 0015), loaded for administrators only. `reload` after any change. */
export function useEmailRelays() {
  const administer = usePermissions().administer
  const [relays, setRelays] = useState<EmailRelayDto[]>([])
  const [loadError, setLoadError] = useState(false)

  const reload = useCallback(
    async (signal?: AbortSignal) => {
      if (!administer) return
      try {
        const items = await emailRelaysApi.list(true, signal)
        setRelays(Array.isArray(items) ? items : [])
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

  return { relays, loadError, reload }
}

/** One real email connection, in the same card layout as the others. The switch activates or deactivates it. */
export function RelayCard({ relay, onToggle, onConfigure }: { relay: EmailRelayDto; onToggle: (active: boolean) => void; onConfigure: () => void }) {
  return (
    <li className={`flex flex-col gap-3 rounded-[10px] border border-line bg-surface p-4 ${relay.isActive ? '' : 'opacity-70'}`}>
      <div className="flex items-center gap-3">
        <div aria-hidden="true" className="grid size-[38px] flex-none place-items-center rounded-[9px] bg-[#BE3D37] text-[13px] font-bold text-white">@</div>
        <div className="min-w-0 flex-1">
          <h3 className="m-0 text-[14.5px] font-semibold">{relay.name}</h3>
          <div className="text-xs text-ink-3">Email · SMTP · key <code className="font-mono">{relay.key}</code></div>
        </div>
        <Switch checked={relay.isActive} onCheckedChange={onToggle} label={`Enable ${relay.name}`} hideLabel />
      </div>
      <p className="m-0 text-[13px] text-ink-2">Sends from {relay.fromAddress}. Real: Send email steps use it by its key.</p>
      <dl className="m-0 mt-auto flex items-baseline justify-between gap-3 text-[12.5px]">
        <dt className="text-ink-3">Credential</dt>
        <dd className="m-0 min-w-0 text-right font-mono text-[11.5px] break-words text-ink-2">
          {relay.userName ? 'SMTP credentials' : 'No login'} · {relay.host}:{relay.port}
        </dd>
      </dl>
      <div className="flex flex-wrap items-center gap-2 text-[12.5px] text-ink-3">
        <StatusPill tone={!relay.isActive ? 'neutral' : relay.userName && !relay.hasPassword ? 'warn' : 'ok'}>
          {!relay.isActive ? 'Off' : relay.userName && !relay.hasPassword ? 'No password' : 'Ready'}
        </StatusPill>
        {relay.hasPassword && <span>Password saved</span>}
        <Button type="button" size="sm" className="ml-auto" onClick={onConfigure} aria-label={`Configure ${relay.name}`}>
          Configure
        </Button>
      </div>
    </li>
  )
}

export const TEST_POLL_MS = 1000
export const TEST_POLL_LIMIT = 30

type TestState = { kind: 'idle' } | { kind: 'running' } | { kind: 'done'; test: EmailRelayTestDto } | { kind: 'error'; message: string }

/** Queues a test (the Worker sends one message through the saved relay) and polls until it finishes. */
function useRelayTest(relayId: string) {
  const [state, setState] = useState<TestState>({ kind: 'idle' })
  const alive = useRef(true)
  useEffect(() => {
    alive.current = true
    return () => {
      alive.current = false
    }
  }, [])

  const run = async (toAddress: string) => {
    setState({ kind: 'running' })
    try {
      let test = await emailRelaysApi.requestTest(relayId, toAddress)
      for (let i = 0; i < TEST_POLL_LIMIT && (test.status === 'Queued' || test.status === 'Running'); i++) {
        await new Promise((resolve) => setTimeout(resolve, TEST_POLL_MS))
        if (!alive.current) return
        test = await emailRelaysApi.getTest(test.id)
      }
      if (!alive.current) return
      setState(test.status === 'Queued' || test.status === 'Running'
        ? { kind: 'error', message: 'The Worker has not run the test yet. Is the Worker running?' }
        : { kind: 'done', test })
    } catch (e) {
      if (!alive.current) return
      const fields = e instanceof ApiError ? e.problem.errors : undefined
      setState({ kind: 'error', message: fields?.toAddress?.[0] ?? fields?.ToAddress?.[0] ?? (e instanceof Error ? e.message : 'Could not run the test.') })
    }
  }
  return { state, run }
}

/** Edit an existing email connection. The key cannot change; the password is write-only (empty keeps it). */
export function RelayEditDialog({ relay, onClose, onSaved }: { relay: EmailRelayDto; onClose: () => void; onSaved: (name: string) => Promise<void> }) {
  const [name, setName] = useState(relay.name)
  const [form, setForm] = useState(toRelayForm(relay))
  const [errors, setErrors] = useState<RelayErrors>({})
  const [busy, setBusy] = useState(false)
  const [to, setTo] = useState('')
  const test = useRelayTest(relay.id)
  const testing = test.state.kind === 'running'

  const save = async () => {
    const local = validateRelay(form, name, true)
    setErrors(local)
    if (Object.keys(local).length > 0) return
    setBusy(true)
    try {
      await emailRelaysApi.update(relay.id, relayBody(form, name))
      setForm((f) => ({ ...f, password: '' }))
      await onSaved(name.trim())
    } catch (e) {
      setErrors(relayErrorsOf(e, 'Could not save the email connection.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={relay.name}
      description="Email · SMTP. Real: Send email steps use it by its key."
      footer={
        <>
          <Button type="button" onClick={() => void test.run(to.trim())} disabled={testing || !relay.isActive} className="mr-auto">
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
        <RelayFields form={form} errors={errors} existing={relay} onChange={(patch) => { setForm((f) => ({ ...f, ...patch })); setErrors({}) }} />
        <Field
          label="Send the test to"
          htmlFor="er-test-to"
          hint="Test connection sends one short message through the saved settings (save your changes first). The address is not kept."
        >
          <Input id="er-test-to" type="email" autoComplete="off" value={to} onChange={(e) => setTo(e.target.value)} placeholder="you@example.com" />
        </Field>
        <div aria-live="polite" className="min-h-5 text-[13px]">
          {testing && <span className="text-ink-3">Waiting for the Worker to send the message…</span>}
          {test.state.kind === 'error' && <span role="alert" className="text-err">{test.state.message}</span>}
          {test.state.kind === 'done' && test.state.test.status === 'Succeeded' && <span className="text-ok">Test message sent. Check the inbox.</span>}
          {test.state.kind === 'done' && test.state.test.status === 'Failed' && (
            <span role="alert" className="text-err">
              The test failed: {test.state.test.message} <code className="font-mono text-xs">{test.state.test.code}</code>
            </span>
          )}
        </div>
      </form>
    </Dialog>
  )
}
