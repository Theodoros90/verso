import { PlugZap } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'

import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input, Select, Textarea } from '@/components/ui/form'
import { cn } from '@/lib/utils'

import { AUTH_METHODS, CONNECTION_GROUPS, simulateTest, type Connection, type ConnectionGroup, type SimulatedTest } from './connections'

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

/** Create or configure a connection. There is deliberately no secret field: secrets live in the bank's secret store. */
export function ConnectionDialog({
  connection,
  existingNames,
  onSave,
  onClose,
}: {
  /** The connection to configure, or null to create a new one. */
  connection: Connection | null
  existingNames: string[]
  onSave: (draft: Draft) => void
  onClose: () => void
}) {
  const isNew = connection === null
  const [draft, setDraft] = useState<Draft>(connection ? toDraft(connection) : blank)
  const [submitted, setSubmitted] = useState(false)
  const [testing, setTesting] = useState(false)
  const [result, setResult] = useState<SimulatedTest | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined)
  useEffect(() => () => clearTimeout(timer.current), [])

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

  const save = () => {
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
          <Button type="button" onClick={test} disabled={testing} className="mr-auto">
            <PlugZap />
            {testing ? 'Testing…' : 'Test connection'}
          </Button>
          <Button type="button" onClick={onClose}>
            Cancel
          </Button>
          <Button type="button" variant="primary" onClick={save}>
            {isNew ? 'Create connection' : 'Save settings'}
          </Button>
        </>
      }
    >
      <form className="flex flex-col gap-3.5" onSubmit={(e) => { e.preventDefault(); save() }} noValidate>
        <Field label="Name" htmlFor="cn-name" error={submitted ? errors.name : undefined}>
          <Input id="cn-name" value={draft.name} onChange={(e) => set({ name: e.target.value })} aria-invalid={submitted && !!errors.name} placeholder="e.g. Finance API" autoFocus />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Group" htmlFor="cn-group">
            <Select id="cn-group" value={draft.group} onChange={(e) => set({ group: e.target.value as ConnectionGroup })}>
              {CONNECTION_GROUPS.map((g) => (
                <option key={g.name}>{g.name}</option>
              ))}
            </Select>
          </Field>
          <Field label="Authentication" htmlFor="cn-auth">
            <Select id="cn-auth" value={draft.auth} onChange={(e) => set({ auth: e.target.value })}>
              {[...new Set([...AUTH_METHODS, draft.auth])].map((a) => (
                <option key={a}>{a}</option>
              ))}
            </Select>
          </Field>
        </div>
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
      </form>
    </Dialog>
  )
}

export type { Draft as ConnectionDraft }
