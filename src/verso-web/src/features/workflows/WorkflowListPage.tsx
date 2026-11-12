import { MoreHorizontal, Plus, Workflow } from 'lucide-react'
import { DropdownMenu } from 'radix-ui'
import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'

import { usePermissions } from '@/app/AuthGate'
import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input, Textarea } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import type { WorkflowSummaryDto } from '@/lib/api/generated'
import { workflowsApi } from '@/lib/api/workflows'

import { DeactivateWorkflowDialog } from './DeactivateWorkflowDialog'

type ListState = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; items: WorkflowSummaryDto[] }

const dateFormat = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })

export function WorkflowListPage() {
  const [state, setState] = useState<ListState>({ kind: 'loading' })
  const [creating, setCreating] = useState(false)
  const [showInactive, setShowInactive] = useState(false)
  const [reloads, setReloads] = useState(0)
  const [notice, setNotice] = useState<{ tone: 'ok' | 'err'; message: string } | null>(null)
  const [deactivating, setDeactivating] = useState<WorkflowSummaryDto | null>(null)
  const canCreate = usePermissions().workflowAuthoring

  useEffect(() => {
    const controller = new AbortController()
    workflowsApi
      .list(1, 100, controller.signal, showInactive)
      .then((page) => setState({ kind: 'ready', items: page.items }))
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load workflows.' })
      })
    return () => controller.abort()
  }, [showInactive, reloads])

  /** Runs one state change, then shows its result and reloads the list. */
  const change = useCallback(async (run: () => Promise<unknown>, message: string) => {
    try {
      await run()
      setNotice({ tone: 'ok', message })
      setReloads((n) => n + 1)
    } catch (e) {
      setNotice({ tone: 'err', message: e instanceof Error ? e.message : 'The change failed.' })
    }
  }, [])

  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      <div className="mb-5 flex flex-wrap items-center gap-3">
        <p className="max-w-prose text-ink-2">
          {showInactive
            ? 'Deactivated workflows don’t accept runs and are kept out of the main list. Activate one to bring it back.'
            : 'Workflows turn submitted invoice data into archived documents. Open one to edit its draft, or create a new one.'}
        </p>
        <div className="ml-auto flex items-center gap-2">
          <Button aria-pressed={showInactive} onClick={() => setShowInactive((v) => !v)}>
            {showInactive ? 'Show active list' : 'Show deactivated'}
          </Button>
          {canCreate && !showInactive && (
            <Button variant="primary" onClick={() => setCreating(true)}>
              <Plus /> New workflow
            </Button>
          )}
        </div>
      </div>

      {notice && (
        <p
          role={notice.tone === 'err' ? 'alert' : 'status'}
          className={`mb-4 rounded-lg border px-4 py-2.5 text-[13px] ${notice.tone === 'err' ? 'border-err/30 bg-err-soft text-err' : 'border-ok/30 bg-ok-soft text-ok'}`}
        >
          {notice.message}
        </p>
      )}

      {state.kind === 'loading' && <p className="text-ink-3">Loading workflows…</p>}
      {state.kind === 'error' && (
        <p role="alert" className="rounded-lg border border-err/30 bg-err-soft px-4 py-3 text-err">
          {state.message}
        </p>
      )}
      {state.kind === 'ready' && state.items.length === 0 && (
        <section className="max-w-xl rounded-lg border border-dashed border-line-strong bg-surface p-6">
          <h2 className="mb-1 text-[14.5px] font-semibold">{showInactive ? 'No deactivated workflows' : 'No workflows yet'}</h2>
          <p className="text-ink-2">{showInactive ? 'Workflows you deactivate appear here, so they can be activated again.' : 'Create the first workflow, for example "Invoice standard".'}</p>
        </section>
      )}
      {state.kind === 'ready' && state.items.length > 0 && (
        <ul aria-label="Workflows" className="grid grid-cols-[repeat(auto-fill,minmax(240px,1fr))] gap-3.5">
          {state.items.map((w) => (
            <li key={w.id} className="relative">
              <Link
                to={`/workflows/${w.id}`}
                className={`flex gap-3 rounded-[10px] border border-line bg-surface p-3.5 text-ink no-underline hover:border-line-strong ${w.isActive ? '' : 'opacity-75'}`}
              >
                <span className="grid size-9 flex-none place-items-center rounded-lg bg-cyan-soft text-cyan" aria-hidden="true">
                  <Workflow className="size-4" />
                </span>
                <span className="min-w-0 flex-1 pr-7">
                  <span className="block truncate text-[13.5px] font-semibold">{w.name}</span>
                  <span className="block font-mono text-[11.5px] text-ink-3">{w.key}</span>
                  <span className="mt-1.5 flex flex-wrap items-center gap-1.5">
                    {w.latestPublishedVersion != null && <StatusPill tone="ok">v{w.latestPublishedVersion}</StatusPill>}
                    {w.hasDraft && <StatusPill tone="warn">Draft</StatusPill>}
                    {!w.isActive && <StatusPill tone="neutral">Inactive</StatusPill>}
                  </span>
                  <span className="mt-1 block text-[12px] text-ink-3">Edited {dateFormat.format(new Date(w.updatedAt))}</span>
                </span>
              </Link>
              {canCreate && (
                <WorkflowMenu
                  workflow={w}
                  onDeactivate={() => setDeactivating(w)}
                  onActivate={() => void change(() => workflowsApi.activate(w.id), `“${w.name}” is active again and is back in the list.`)}
                />
              )}
            </li>
          ))}
        </ul>
      )}

      <NewWorkflowDialog open={creating} onOpenChange={setCreating} />
      {deactivating && (
        <DeactivateWorkflowDialog
          workflow={deactivating}
          open
          onOpenChange={(open) => !open && setDeactivating(null)}
          onDone={() => {
            setNotice({ tone: 'ok', message: `“${deactivating.name}” is deactivated. Find it under “Show deactivated” to activate it again.` })
            setReloads((n) => n + 1)
          }}
        />
      )}
    </div>
  )
}

/** The “⋯” menu of a card: Deactivate (asks first) for an active workflow, Activate for a deactivated one. */
function WorkflowMenu({ workflow, onDeactivate, onActivate }: { workflow: WorkflowSummaryDto; onDeactivate: () => void; onActivate: () => void }) {
  const item = 'cursor-pointer rounded px-2.5 py-1.5 text-[13px] text-ink outline-none data-[highlighted]:bg-surface-2'
  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>
        <Button variant="ghost" size="icon" aria-label={`Actions for ${workflow.name}`} className="absolute top-2 right-2 size-7">
          <MoreHorizontal />
        </Button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content align="end" sideOffset={4} className="z-50 min-w-44 rounded-lg border border-line bg-surface p-1 shadow-lg">
          {workflow.isActive ? (
            <DropdownMenu.Item className={item} onSelect={onDeactivate}>
              Deactivate…
            </DropdownMenu.Item>
          ) : (
            <DropdownMenu.Item className={item} onSelect={onActivate}>
              Activate
            </DropdownMenu.Item>
          )}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  )
}

const KEY_PATTERN = /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/

function NewWorkflowDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const navigate = useNavigate()
  const [key, setKey] = useState('')
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    const local: Record<string, string> = {}
    if (key.length < 2 || key.length > 48 || !KEY_PATTERN.test(key)) local.key = '2–48 characters: lower-case letters, digits and single hyphens (e.g. invoice-standard).'
    if (!name.trim()) local.name = 'Enter a name.'
    setErrors(local)
    if (Object.keys(local).length) return

    setBusy(true)
    try {
      const created = await workflowsApi.create({ key, name: name.trim(), description: description.trim() || null })
      navigate(`/workflows/${created.id}`)
    } catch (error) {
      if (error instanceof ApiError && error.code === 'workflow.key_taken') setErrors({ key: 'A workflow with this key already exists.' })
      else if (error instanceof ApiError && error.problem.errors) {
        const mapped: Record<string, string> = {}
        for (const [k, v] of Object.entries(error.problem.errors)) mapped[k.toLowerCase()] = v[0] ?? 'Invalid.'
        setErrors(mapped)
      } else setErrors({ form: error instanceof Error ? error.message : 'Could not create the workflow.' })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title="New workflow"
      description="It starts as a draft with an API trigger. The key is part of the submission URL and can't be changed later."
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button variant="primary" type="submit" form="new-workflow" disabled={busy}>
            Create
          </Button>
        </>
      }
    >
      <form id="new-workflow" onSubmit={submit} className="flex flex-col gap-3.5" noValidate>
        <Field label="Key" htmlFor="wf-key" hint={key ? `POST /api/v1/workflows/${key}/executions` : 'For example invoice-standard'} error={errors.key}>
          <Input id="wf-key" value={key} onChange={(e) => setKey(e.target.value.toLowerCase())} aria-invalid={!!errors.key} className="font-mono" autoFocus />
        </Field>
        <Field label="Name" htmlFor="wf-name" error={errors.name}>
          <Input id="wf-name" value={name} onChange={(e) => setName(e.target.value)} aria-invalid={!!errors.name} />
        </Field>
        <Field label="Description (optional)" htmlFor="wf-desc" error={errors.description}>
          <Textarea id="wf-desc" value={description} onChange={(e) => setDescription(e.target.value)} />
        </Field>
        {errors.form && (
          <p role="alert" className="text-[13px] text-err">
            {errors.form}
          </p>
        )}
      </form>
    </Dialog>
  )
}
