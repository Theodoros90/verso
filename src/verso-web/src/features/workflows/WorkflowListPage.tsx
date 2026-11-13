import { LayoutTemplate, MoreHorizontal, Plus, Workflow } from 'lucide-react'
import { DropdownMenu } from 'radix-ui'
import { Fragment, useCallback, useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'

import { usePermissions } from '@/app/AuthGate'
import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input, Textarea } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import type { NodeTypeDto, WorkflowSummaryDto } from '@/lib/api/generated'
import { workflowsApi } from '@/lib/api/workflows'

import { DeactivateWorkflowDialog } from './DeactivateWorkflowDialog'
import { DuplicateWorkflowDialog } from './DuplicateWorkflowDialog'
import { buildStarterDefinition, STARTER_CATEGORIES, stepLabels, starters, type Starter } from './starters'

type ListState = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; items: WorkflowSummaryDto[] }

const dateFormat = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })

export function WorkflowListPage() {
  const [state, setState] = useState<ListState>({ kind: 'loading' })
  const [step, setStep] = useState<'closed' | 'choose' | 'gallery' | 'form'>('closed')
  const [starter, setStarter] = useState<Starter | null>(null)
  const [filter, setFilter] = useState<'all' | 'published' | 'drafts'>('all')
  const [query, setQuery] = useState('')
  const [showInactive, setShowInactive] = useState(false)
  const [reloads, setReloads] = useState(0)
  const [notice, setNotice] = useState<{ tone: 'ok' | 'err'; message: string } | null>(null)
  const [deactivating, setDeactivating] = useState<WorkflowSummaryDto | null>(null)
  const [duplicating, setDuplicating] = useState<WorkflowSummaryDto | null>(null)
  const navigate = useNavigate()
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

  const items = state.kind === 'ready' ? state.items : []
  const needle = query.trim().toLowerCase()
  const visible = items.filter(
    (w) =>
      (filter === 'all' || (filter === 'published' ? w.latestPublishedVersion != null : w.hasDraft)) &&
      (!needle || `${w.name} ${w.key}`.toLowerCase().includes(needle)),
  )
  const filtered = filter !== 'all' || needle !== ''
  const startFlow = (next: 'choose' | 'gallery' | 'form', chosen: Starter | null = null) => {
    setStarter(chosen)
    setStep(next)
  }

  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      <div className="mb-6 flex flex-wrap items-center gap-3">
        <div>
          <h1 className="text-[26px] leading-tight font-bold tracking-tight">Document workflows</h1>
          <p className="mt-1 max-w-prose text-ink-2">
            {showInactive
              ? 'Deactivated workflows don’t accept runs and are kept out of the main list. Activate one to bring it back.'
              : 'Start with a ready-made workflow, or build your own.'}
          </p>
        </div>
        {canCreate && !showInactive && (
          <Button variant="primary" className="ml-auto" onClick={() => startFlow('choose')}>
            <Plus /> New workflow
          </Button>
        )}
      </div>

      {canCreate && !showInactive && (
        <section
          aria-label="Ways to start"
          className="mb-6 flex flex-wrap items-center justify-between gap-4 rounded-[14px] border border-cyan/25 bg-gradient-to-br from-cyan-soft to-surface p-6"
        >
          <div className="max-w-xl">
            <h2 className="text-[18px] font-semibold tracking-tight">A faster way to your first workflow</h2>
            <p className="mt-1 text-[13px] text-ink-2">
              Use an editable starter with steps already connected, or start with a blank canvas. Both paths create a draft you can customize.
            </p>
          </div>
          <div className="flex flex-none flex-wrap gap-2">
            <Button onClick={() => startFlow('form')}>Build custom</Button>
            <Button variant="primary" onClick={() => startFlow('gallery')}>
              Browse ready-made
            </Button>
          </div>
        </section>
      )}

      <div className="mb-3 flex flex-wrap items-center gap-3">
        <div role="group" aria-label="Filter workflows" className="flex gap-1">
          {(
            [
              ['all', 'All workflows'],
              ['published', 'Published'],
              ['drafts', 'With a draft'],
            ] as const
          ).map(([value, label]) => (
            <Button
              key={value}
              variant="ghost"
              size="sm"
              aria-pressed={filter === value}
              onClick={() => setFilter(value)}
              className={filter === value ? 'bg-cyan-soft text-cyan' : ''}
            >
              {label}
            </Button>
          ))}
        </div>
        <div className="ml-auto flex items-center gap-2">
          <Input type="search" aria-label="Search workflows" placeholder="Search workflows…" value={query} onChange={(e) => setQuery(e.target.value)} className="w-64" />
          <Button aria-pressed={showInactive} onClick={() => setShowInactive((v) => !v)}>
            {showInactive ? 'Show active list' : 'Show deactivated'}
          </Button>
        </div>
      </div>
      {state.kind === 'ready' && (
        <p aria-live="polite" className="mb-3 text-[12px] text-ink-3">
          {visible.length} workflow{visible.length === 1 ? '' : 's'}
        </p>
      )}

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
      {state.kind === 'ready' && visible.length === 0 && (
        <section className="rounded-xl border border-dashed border-line-strong bg-surface p-10 text-center">
          <h2 className="mb-1 text-[16px] font-semibold">
            {showInactive ? 'No deactivated workflows' : filtered ? 'No matching workflows' : canCreate ? 'Your next workflow starts here' : 'No workflows yet'}
          </h2>
          <p className="text-ink-2">
            {showInactive
              ? 'Workflows you deactivate appear here, so they can be activated again.'
              : filtered
                ? 'Change your search or filter, or create a new workflow.'
                : canCreate
                  ? 'Choose a ready-made starting point or create a custom workflow.'
                  : 'Workflows appear here once someone creates them.'}
          </p>
          {canCreate && !showInactive && !filtered && (
            <Button variant="primary" className="mt-4" onClick={() => startFlow('choose')}>
              New workflow
            </Button>
          )}
        </section>
      )}
      {state.kind === 'ready' && visible.length > 0 && (
        <ul aria-label="Workflows" className="grid grid-cols-[repeat(auto-fill,minmax(280px,1fr))] gap-4">
          {visible.map((w) => (
            <li key={w.id} className="relative">
              <Link
                to={`/workflows/${w.id}`}
                className={`flex h-full flex-col gap-2.5 rounded-xl border border-line bg-surface p-5 text-ink no-underline hover:border-line-strong ${w.isActive ? '' : 'opacity-75'}`}
              >
                <span className="flex flex-wrap items-center gap-1.5 pr-8">
                  {w.hasDraft && <StatusPill tone="warn">Draft</StatusPill>}
                  {w.latestPublishedVersion != null && <StatusPill tone="ok">Published v{w.latestPublishedVersion}</StatusPill>}
                  {!w.isActive && <StatusPill tone="neutral">Inactive</StatusPill>}
                </span>
                <span className="flex items-center gap-2">
                  <Workflow className="size-4 flex-none text-cyan" aria-hidden="true" />
                  <span className="min-w-0 truncate text-[15px] font-semibold">{w.name}</span>
                </span>
                <span className="font-mono text-[11.5px] text-ink-3">{w.key}</span>
                <span className="mt-auto flex items-center justify-between gap-2 border-t border-line pt-3 text-[11.5px] text-ink-3">
                  <span>Edited {dateFormat.format(new Date(w.updatedAt))}</span>
                  <span className="rounded-lg border border-line px-3 py-1 text-[12px] text-ink">{w.hasDraft ? 'View draft' : 'Open'}</span>
                </span>
              </Link>
              {canCreate && (
                <WorkflowMenu
                  workflow={w}
                  onDuplicate={() => setDuplicating(w)}
                  onDeactivate={() => setDeactivating(w)}
                  onActivate={() => void change(() => workflowsApi.activate(w.id), `“${w.name}” is active again and is back in the list.`)}
                />
              )}
            </li>
          ))}
        </ul>
      )}

      <p className="mt-6 text-[11.5px] text-ink-3">
        Ready-made workflows are starter drafts with the steps already connected, not activated automations. Connections, templates and rules still need setting up, and nothing runs until you publish.
      </p>

      <StartDialog open={step === 'choose'} onBrowse={() => startFlow('gallery')} onCustom={() => startFlow('form')} onOpenChange={(open) => !open && setStep('closed')} />
      <StarterGallery
        open={step === 'gallery'}
        onBack={() => startFlow('choose')}
        onPick={(s) => startFlow('form', s)}
        onOpenChange={(open) => !open && setStep('closed')}
      />
      <NewWorkflowDialog
        open={step === 'form'}
        starter={starter}
        onOpenChange={(open) => !open && setStep('closed')}
        onSeedFailed={() => setReloads((n) => n + 1)}
      />
      {duplicating && (
        <DuplicateWorkflowDialog
          workflow={duplicating}
          open
          onOpenChange={(open) => !open && setDuplicating(null)}
          onDone={(copy) => navigate(`/workflows/${copy.id}`)}
        />
      )}
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

/** The “⋯” menu of a card: Duplicate, then Deactivate (asks first) for an active workflow, Activate for a deactivated one. */
function WorkflowMenu({
  workflow,
  onDuplicate,
  onDeactivate,
  onActivate,
}: {
  workflow: WorkflowSummaryDto
  onDuplicate: () => void
  onDeactivate: () => void
  onActivate: () => void
}) {
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
          <DropdownMenu.Item className={item} onSelect={onDuplicate}>
            Duplicate…
          </DropdownMenu.Item>
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

/** “How would you like to start?”: ready-made starter or a blank workflow. */
function StartDialog({ open, onOpenChange, onBrowse, onCustom }: { open: boolean; onOpenChange: (open: boolean) => void; onBrowse: () => void; onCustom: () => void }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="How would you like to start?" className="max-w-3xl">
      <div className="grid gap-4 sm:grid-cols-2">
        <section className="flex flex-col items-start gap-3 rounded-xl border border-cyan/30 bg-cyan-soft/40 p-6">
          <span className="grid size-10 place-items-center rounded-[10px] bg-cyan-soft text-cyan" aria-hidden="true">
            <LayoutTemplate className="size-5" />
          </span>
          <h3 className="text-[16px] font-semibold">Start ready-made</h3>
          <p className="min-h-16 text-[13px] text-ink-2">
            Choose a common document process with connected steps. Review the configuration, add your connections and adapt it to your needs.
          </p>
          <Button variant="primary" onClick={onBrowse}>
            Browse ready-made workflows
          </Button>
        </section>
        <section className="flex flex-col items-start gap-3 rounded-xl border border-line p-6">
          <span className="grid size-10 place-items-center rounded-[10px] bg-cyan-soft text-cyan" aria-hidden="true">
            <Plus className="size-5" />
          </span>
          <h3 className="text-[16px] font-semibold">Build custom</h3>
          <p className="min-h-16 text-[13px] text-ink-2">Start with a workflow that only has its API trigger. Design your own inputs, processing, document generation and delivery.</p>
          <Button onClick={onCustom}>Start from scratch</Button>
        </section>
      </div>
    </Dialog>
  )
}

/** The ready-made starters, filtered by category and search; picking one opens the new-workflow form with it. */
function StarterGallery({ open, onOpenChange, onBack, onPick }: { open: boolean; onOpenChange: (open: boolean) => void; onBack: () => void; onPick: (s: Starter) => void }) {
  const [category, setCategory] = useState<(typeof STARTER_CATEGORIES)[number]>('All')
  const [query, setQuery] = useState('')
  const [chosen, setChosen] = useState<Starter | null>(null)
  const [nodeTypes, setNodeTypes] = useState<NodeTypeDto[]>([])

  useEffect(() => {
    if (!open) return
    const controller = new AbortController()
    workflowsApi
      .nodeTypes(controller.signal)
      .then(setNodeTypes)
      .catch(() => undefined) // labels fall back to the type names
    return () => controller.abort()
  }, [open])

  const needle = query.trim().toLowerCase()
  const list = starters.filter((s) => (category === 'All' || s.category === category) && (!needle || `${s.name} ${s.description}`.toLowerCase().includes(needle)))

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title="Choose a ready-made workflow"
      className="max-w-4xl"
      footer={
        <>
          <Button onClick={onBack}>Back</Button>
          <Button variant="primary" disabled={!chosen} onClick={() => chosen && onPick(chosen)}>
            Continue
          </Button>
        </>
      }
    >
      <div className="mb-4 flex flex-wrap gap-2">
        <Input type="search" aria-label="Search ready-made workflows" placeholder="Search ready-made workflows…" value={query} onChange={(e) => setQuery(e.target.value)} className="max-w-xs" />
        <select
          aria-label="Starter category"
          value={category}
          onChange={(e) => setCategory(e.target.value as (typeof STARTER_CATEGORIES)[number])}
          className="h-9 rounded-md border border-line-strong bg-surface px-2 text-[13px]"
        >
          {STARTER_CATEGORIES.map((c) => (
            <option key={c}>{c}</option>
          ))}
        </select>
      </div>
      <ul aria-label="Ready-made workflows" className="grid gap-3.5 sm:grid-cols-2">
        {list.map((s) => {
          const selected = chosen?.id === s.id
          return (
            <li key={s.id} className={`flex flex-col gap-2 rounded-[11px] border p-4 ${selected ? 'border-2 border-cyan bg-cyan-soft/30' : 'border-line bg-surface'}`}>
              <div className="flex items-center gap-2 text-[11px] text-ink-3">
                <StatusPill tone="neutral">{s.category}</StatusPill>
                {s.steps.length} connected steps
              </div>
              <h3 className="text-[15px] font-semibold">{s.name}</h3>
              <p className="text-[12.5px] text-ink-2">{s.description}</p>
              <p className="text-[11.5px] text-ink-3">Trigger: {s.trigger}</p>
              <div className="flex flex-wrap items-center gap-1 rounded-lg bg-surface-2 p-2 text-[11px]" aria-label="Steps">
                {stepLabels(s, nodeTypes).map((label, i) => (
                  <Fragment key={i}>
                    {i > 0 && <span aria-hidden="true">→</span>}
                    <span className="rounded border border-line bg-surface px-1.5 py-0.5">{label}</span>
                  </Fragment>
                ))}
              </div>
              <Button size="sm" aria-pressed={selected} className="mt-auto self-start" onClick={() => setChosen(s)}>
                {selected ? 'Selected' : 'Preview & select'}
              </Button>
            </li>
          )
        })}
      </ul>
      {list.length === 0 && <p className="rounded-lg border border-line bg-surface-2 p-3 text-[12.5px] text-ink-2">No matching starters. Try another category or search.</p>}
      <p className="mt-4 rounded-lg border border-line bg-surface-2 p-3 text-[12px] text-ink-2">
        Ready-made means the workflow structure is prepared, not that connections, document templates or business rules are configured. Publishing requires review.
      </p>
    </Dialog>
  )
}

function NewWorkflowDialog({
  open,
  onOpenChange,
  starter,
  onSeedFailed,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  starter: Starter | null
  onSeedFailed: () => void
}) {
  const navigate = useNavigate()
  const [key, setKey] = useState('')
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)

  // Each time the form opens it starts from the chosen starter (or blank).
  useEffect(() => {
    if (!open) return
    setKey(starter ? starter.id : '')
    setName(starter?.name ?? '')
    setDescription(starter?.description ?? '')
    setErrors({})
  }, [open, starter])

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
      if (starter) {
        try {
          const definition = buildStarterDefinition(starter, await workflowsApi.nodeTypes())
          if (!definition) throw new Error('missing node type')
          const draft = await workflowsApi.draft(created.id)
          if (!draft.etag) throw new Error('no etag')
          await workflowsApi.saveDraft(created.id, definition, draft.etag)
        } catch {
          setErrors({ form: 'The workflow was created, but the starter steps could not be added. Open it from the list to build it.' })
          onSeedFailed()
          return
        }
      }
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
      title={starter ? `New workflow from “${starter.name}”` : 'New workflow'}
      description={
        starter
          ? 'It starts as a draft with the starter’s steps already connected. The key is part of the submission URL and can’t be changed later.'
          : 'It starts as a draft with an API trigger. The key is part of the submission URL and can’t be changed later.'
      }
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
