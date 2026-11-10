import { Plus } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router'

import { usePermissions } from '@/app/AuthGate'
import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input, Textarea } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import type { TemplateSummaryDto } from '@/lib/api/generated'
import { templatesApi } from '@/lib/api/templates'

type ListState = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; items: TemplateSummaryDto[] }

const dateFormat = new Intl.DateTimeFormat('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })

/** Mini page thumbnail from the design reference (.tpl-thumb / .mini). Decorative. */
function Thumb() {
  return (
    <div aria-hidden="true" className="grid h-[120px] place-items-end justify-center bg-[var(--stage)] pt-3.5">
      <div className="h-[106px] w-[120px] bg-white p-2.5 shadow-[var(--sheet-shadow)]">
        <i className="mb-[5px] block h-[7px] w-[45%] rounded-sm bg-[#1A232C]" />
        <i className="mb-[5px] block h-1 w-[30%] rounded-sm bg-cyan" />
        <i className="mb-[5px] block h-1 rounded-sm bg-[#DCE2E8]" />
        <i className="mb-[5px] block h-1 w-[80%] rounded-sm bg-[#DCE2E8]" />
        <i className="mb-[5px] block h-1 rounded-sm bg-[#DCE2E8]" />
        <i className="block h-1 w-[60%] rounded-sm bg-[#DCE2E8]" />
      </div>
    </div>
  )
}

export function TemplateListPage() {
  const [state, setState] = useState<ListState>({ kind: 'loading' })
  const [creating, setCreating] = useState(false)
  const canCreate = usePermissions().templateAuthoring

  useEffect(() => {
    const controller = new AbortController()
    templatesApi
      .list(1, 100, controller.signal)
      .then((page) => setState({ kind: 'ready', items: page.items }))
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load templates.' })
      })
    return () => controller.abort()
  }, [])

  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      <div className="mb-5 flex flex-wrap items-center gap-3">
        <p className="max-w-prose text-ink-2">Invoice templates. Open one to edit its draft, or create a new one.</p>
        {canCreate && (
          <Button variant="primary" className="ml-auto" onClick={() => setCreating(true)}>
            <Plus /> New template
          </Button>
        )}
      </div>

      {state.kind === 'loading' && <p className="text-ink-3">Loading templates…</p>}
      {state.kind === 'error' && (
        <p role="alert" className="rounded-lg border border-err/30 bg-err-soft px-4 py-3 text-err">
          {state.message}
        </p>
      )}
      {state.kind === 'ready' && state.items.length === 0 && (
        <section className="max-w-xl rounded-lg border border-dashed border-line-strong bg-surface p-6">
          <h2 className="mb-1 text-[14.5px] font-semibold">No templates yet</h2>
          <p className="text-ink-2">Create the first template to start designing.</p>
        </section>
      )}
      {state.kind === 'ready' && state.items.length > 0 && (
        <ul aria-label="Templates" className="grid grid-cols-[repeat(auto-fill,minmax(200px,1fr))] gap-3.5">
          {state.items.map((t) => (
            <li key={t.id}>
              <Link
                to={`/templates/${t.id}`}
                className="block overflow-hidden rounded-[10px] border border-line bg-surface text-ink no-underline hover:border-line-strong"
              >
                <Thumb />
                <div className="px-3 py-2.5">
                  <div className="truncate text-[13.5px] font-semibold">{t.name}</div>
                  <div className="font-mono text-[11.5px] text-ink-3">{t.key}</div>
                  <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
                    {t.latestPublishedVersion != null && <StatusPill tone="ok">v{t.latestPublishedVersion}</StatusPill>}
                    {t.hasDraft && <StatusPill tone="warn">Draft</StatusPill>}
                  </div>
                  <div className="mt-1 text-[12px] text-ink-3">Edited {dateFormat.format(new Date(t.updatedAt))}</div>
                </div>
              </Link>
            </li>
          ))}
        </ul>
      )}

      <NewTemplateDialog open={creating} onOpenChange={setCreating} />
    </div>
  )
}

const KEY_PATTERN = /^[A-Z][A-Z0-9-]{1,31}$/

function NewTemplateDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const navigate = useNavigate()
  const [key, setKey] = useState('')
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    const local: Record<string, string> = {}
    if (!KEY_PATTERN.test(key)) local.key = '2–32 characters: A–Z, 0–9 and -, starting with a letter.'
    if (!name.trim()) local.name = 'Enter a name.'
    setErrors(local)
    if (Object.keys(local).length) return

    setBusy(true)
    try {
      const created = await templatesApi.create({ key, name: name.trim(), description: description.trim() || null, documentType: 'Invoice' })
      navigate(`/templates/${created.id}`)
    } catch (error) {
      if (error instanceof ApiError) {
        if (error.code === 'template.key_taken') setErrors({ key: 'A template with this key already exists.' })
        else if (error.problem.errors) {
          const mapped: Record<string, string> = {}
          for (const [k, v] of Object.entries(error.problem.errors)) mapped[k.toLowerCase()] = v[0] ?? 'Invalid.'
          setErrors(mapped)
        } else setErrors({ form: error.message })
      } else setErrors({ form: 'Could not create the template.' })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title="New template"
      description="It starts as an empty draft. The key can't be changed later."
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button variant="primary" type="submit" form="new-template" disabled={busy}>
            Create
          </Button>
        </>
      }
    >
      <form id="new-template" onSubmit={submit} className="flex flex-col gap-3.5" noValidate>
        <Field label="Key" htmlFor="tpl-key" hint="For example INV-STD" error={errors.key}>
          <Input id="tpl-key" value={key} onChange={(e) => setKey(e.target.value.toUpperCase())} aria-invalid={!!errors.key} className="font-mono" autoFocus />
        </Field>
        <Field label="Name" htmlFor="tpl-name" error={errors.name}>
          <Input id="tpl-name" value={name} onChange={(e) => setName(e.target.value)} aria-invalid={!!errors.name} />
        </Field>
        <Field label="Description (optional)" htmlFor="tpl-desc" error={errors.description}>
          <Textarea id="tpl-desc" value={description} onChange={(e) => setDescription(e.target.value)} />
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
