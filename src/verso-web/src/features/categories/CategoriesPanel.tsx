import { Plus } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'

import { usePermissions } from '@/app/AuthGate'
import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import { documentCategoriesApi } from '@/lib/api/documentCategories'
import type { DocumentCategoryDto } from '@/lib/api/generated'

type ListState = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; items: DocumentCategoryDto[] }

/** Turns a failed category call into text for the name field (or `form` for anything else). */
function errorsOf(error: unknown, fallback: string): Record<string, string> {
  if (error instanceof ApiError) {
    if (error.code === 'category.name_taken') return { name: 'A category with this name already exists.' }
    const first = error.problem.errors?.name?.[0] ?? error.problem.errors?.Name?.[0]
    if (first) return { name: first }
    return { form: error.message }
  }
  return { form: fallback }
}

const count = (n: number) => `${n} ${n === 1 ? 'template' : 'templates'}`

/**
 * Document categories tab of the Configuration drawer: the one real (server-backed) tab. Categories group
 * templates; they are deactivated, never deleted. Write controls need template authoring.
 */
export function CategoriesPanel() {
  const canWrite = usePermissions().templateAuthoring
  const [showInactive, setShowInactive] = useState(false)
  const [state, setState] = useState<ListState>({ kind: 'loading' })
  const [newName, setNewName] = useState('')
  const [addErrors, setAddErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)
  const [renaming, setRenaming] = useState<DocumentCategoryDto | null>(null)
  const [deactivating, setDeactivating] = useState<DocumentCategoryDto | null>(null)
  const [rowError, setRowError] = useState<string | null>(null)

  const load = useCallback(
    async (signal?: AbortSignal) => {
      try {
        const items = await documentCategoriesApi.list(showInactive, signal)
        setState({ kind: 'ready', items })
      } catch (e) {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load the categories.' })
      }
    },
    [showInactive],
  )

  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => controller.abort()
  }, [load])

  const add = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!newName.trim()) {
      setAddErrors({ name: 'Enter a name.' })
      return
    }
    setBusy(true)
    try {
      await documentCategoriesApi.create(newName.trim())
      setNewName('')
      setAddErrors({})
      await load()
    } catch (error) {
      setAddErrors(errorsOf(error, 'Could not add the category.'))
    } finally {
      setBusy(false)
    }
  }

  const activate = async (c: DocumentCategoryDto) => {
    setRowError(null)
    try {
      await documentCategoriesApi.activate(c.id)
      await load()
    } catch (error) {
      setRowError(errorsOf(error, 'Could not activate the category.').form ?? 'Could not activate the category.')
    }
  }

  return (
    <div className="px-5 py-4">
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <p className="max-w-prose text-[13px] text-ink-2">Group templates by kind of document. A category in use is deactivated, not deleted.</p>
        <label className="ml-auto inline-flex cursor-pointer items-center gap-2 text-[13px] text-ink-2">
          <input type="checkbox" checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} className="size-4 accent-cyan" />
          Show inactive
        </label>
      </div>

      {canWrite && (
        <form onSubmit={add} noValidate className="mb-4 flex items-start gap-2">
          <Field label="New category name" htmlFor="cat-new" error={addErrors.name} className="min-w-0 flex-1">
            <Input
              id="cat-new"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              aria-invalid={!!addErrors.name}
              maxLength={100}
              placeholder="For example Credit notes"
            />
            {addErrors.form && (
              <p role="alert" className="text-xs text-err">
                {addErrors.form}
              </p>
            )}
          </Field>
          <Button variant="primary" type="submit" disabled={busy} className="mt-[22px]">
            <Plus /> Add category
          </Button>
        </form>
      )}

      {rowError && (
        <p role="alert" className="mb-3 rounded-lg border border-err/30 bg-err-soft px-3 py-2 text-[13px] text-err">
          {rowError}
        </p>
      )}
      {state.kind === 'loading' && <p className="text-ink-3">Loading categories…</p>}
      {state.kind === 'error' && (
        <p role="alert" className="rounded-lg border border-err/30 bg-err-soft px-4 py-3 text-err">
          {state.message}
        </p>
      )}
      {state.kind === 'ready' && state.items.length === 0 && (
        <section className="rounded-lg border border-dashed border-line-strong bg-surface p-5">
          <h2 className="mb-1 text-[14.5px] font-semibold">No categories yet</h2>
          <p className="text-ink-2">Add one above to start grouping templates.</p>
        </section>
      )}
      {state.kind === 'ready' && state.items.length > 0 && (
        <ul aria-label="Document categories" className="divide-y divide-line overflow-hidden rounded-lg border border-line bg-surface">
          {state.items.map((c) => (
            <li key={c.id} className="flex flex-wrap items-center gap-2 px-3.5 py-2.5">
              <span className="min-w-0 flex-1 truncate text-[13.5px] font-medium">{c.name}</span>
              {!c.isActive && <StatusPill tone="neutral">Inactive</StatusPill>}
              <span className="text-[12.5px] text-ink-3">{count(c.templateCount)}</span>
              {canWrite && (
                <span className="flex gap-1">
                  <Button size="sm" aria-label={`Rename ${c.name}`} onClick={() => setRenaming(c)}>
                    Rename
                  </Button>
                  {c.isActive ? (
                    <Button size="sm" aria-label={`Deactivate ${c.name}`} onClick={() => setDeactivating(c)}>
                      Deactivate
                    </Button>
                  ) : (
                    <Button size="sm" aria-label={`Activate ${c.name}`} onClick={() => void activate(c)}>
                      Activate
                    </Button>
                  )}
                </span>
              )}
            </li>
          ))}
        </ul>
      )}

      <RenameDialog category={renaming} onClose={() => setRenaming(null)} onDone={() => void load()} />
      <DeactivateDialog category={deactivating} onClose={() => setDeactivating(null)} onDone={() => void load()} />
    </div>
  )
}

function RenameDialog({ category, onClose, onDone }: { category: DocumentCategoryDto | null; onClose: () => void; onDone: () => void }) {
  const [name, setName] = useState('')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [busy, setBusy] = useState(false)

  // Start from the category's current name each time the dialog opens.
  useEffect(() => {
    setName(category?.name ?? '')
    setErrors({})
  }, [category])

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!category) return
    if (!name.trim()) {
      setErrors({ name: 'Enter a name.' })
      return
    }
    setBusy(true)
    try {
      await documentCategoriesApi.rename(category.id, name.trim())
      onDone()
      onClose()
    } catch (error) {
      setErrors(errorsOf(error, 'Could not rename the category.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open={category !== null}
      onOpenChange={(open) => !open && onClose()}
      title="Rename category"
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" type="submit" form="rename-category" disabled={busy}>
            Save
          </Button>
        </>
      }
    >
      <form id="rename-category" onSubmit={submit} className="flex flex-col gap-3.5" noValidate>
        <Field label="Name" htmlFor="cat-rename" error={errors.name}>
          <Input id="cat-rename" value={name} onChange={(e) => setName(e.target.value)} aria-invalid={!!errors.name} maxLength={100} autoFocus />
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

function DeactivateDialog({ category, onClose, onDone }: { category: DocumentCategoryDto | null; onClose: () => void; onDone: () => void }) {
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => setError(null), [category])

  const confirm = async () => {
    if (!category) return
    setBusy(true)
    try {
      await documentCategoriesApi.deactivate(category.id)
      onDone()
      onClose()
    } catch (e) {
      setError(errorsOf(e, 'Could not deactivate the category.').form ?? 'Could not deactivate the category.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog
      open={category !== null}
      onOpenChange={(open) => !open && onClose()}
      title="Deactivate category"
      description={
        category && (
          <>
            Deactivate “{category.name}”? Its {count(category.templateCount)} keep the category, but it can't be chosen for other templates until it is activated again.
          </>
        )
      }
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" onClick={() => void confirm()} disabled={busy}>
            Deactivate
          </Button>
        </>
      }
    >
      {error ? (
        <p role="alert" className="text-[13px] text-err">
          {error}
        </p>
      ) : (
        <p className="text-[13px] text-ink-2">You can activate it again from the inactive list.</p>
      )}
    </Dialog>
  )
}
