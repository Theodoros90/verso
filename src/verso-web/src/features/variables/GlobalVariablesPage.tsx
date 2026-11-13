import { Plus } from 'lucide-react'
import { useState } from 'react'

import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'

import { VariableDialog } from './VariableDialog'
import { MASK, placeholder, seedVariables, type GlobalVariable, type VariableDraft } from './variables'

let nextId = 1

/** Global variables (design reference): named values every workflow can reference as {{name}}. Simulated and frontend-only. */
export function GlobalVariablesPage() {
  const [variables, setVariables] = useState<GlobalVariable[]>(seedVariables)
  const [editing, setEditing] = useState<GlobalVariable | 'new' | null>(null)
  const [deleting, setDeleting] = useState<GlobalVariable | null>(null)
  const [notice, setNotice] = useState('')

  const save = (draft: VariableDraft) => {
    if (editing === 'new') {
      setVariables((list) => [...list, { ...draft, id: `local-${nextId++}`, updated: 'Just now' }])
      setNotice(`Variable ${placeholder(draft.name)} created.`)
    } else if (editing) {
      const id = editing.id
      setVariables((list) => list.map((v) => (v.id === id ? { ...v, ...draft, updated: 'Just now' } : v)))
      setNotice(`Variable ${placeholder(draft.name)} saved.`)
    }
    setEditing(null)
  }

  const remove = (v: GlobalVariable) => {
    setVariables((list) => list.filter((x) => x.id !== v.id))
    setNotice(`Variable ${placeholder(v.name)} deleted. Steps using it will flag it as unresolved.`)
    setDeleting(null)
  }

  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      <p className="mb-4 max-w-3xl text-ink-2">
        <b className="text-[15.5px] font-semibold text-ink">Global variables</b>{' '}
        <span className="text-ink-3">
          Named values every workflow can reference with {'{{double braces}}'}, for example {'{{companyName}}'} or {'{{vatRate}}'}. Sensitive values are referenced but never printed into documents.
        </span>
      </p>

      <section aria-label="Variables" className="rounded-[10px] border border-line bg-surface">
        <div className="flex items-center gap-3 px-4 py-3.5">
          <h2 className="m-0 text-[14.5px] font-semibold">
            {variables.length} {variables.length === 1 ? 'variable' : 'variables'}
          </h2>
          <Button type="button" variant="primary" size="sm" className="ml-auto" onClick={() => setEditing('new')}>
            <Plus />
            New variable
          </Button>
        </div>
        <div role="status" className="px-4 text-[13px] text-ink-2 empty:hidden">
          {notice}
        </div>

        {variables.length === 0 ? (
          <p className="border-t border-line px-4 py-6 text-ink-2">No variables yet. Create one to reuse a value in every workflow.</p>
        ) : (
          <div className="mt-2 overflow-x-auto border-t border-line">
            <table className="w-full border-collapse text-left text-[13px]">
              <caption className="sr-only">Global variables</caption>
              <thead>
                <tr className="border-b border-line text-xs text-ink-3">
                  {['Name', 'Value', 'Type', 'Scope', 'Environment', 'Sensitive', 'Updated'].map((h) => (
                    <th key={h} scope="col" className="px-4 py-2.5 font-medium">{h}</th>
                  ))}
                  <th scope="col" className="px-4 py-2.5"><span className="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                {variables.map((v) => (
                  <tr key={v.id} className="border-b border-line align-top last:border-b-0">
                    <td className="px-4 py-2.5">
                      <b className="font-mono">{placeholder(v.name)}</b>
                      {v.description && <div className="text-xs text-ink-3">{v.description}</div>}
                    </td>
                    <td className="px-4 py-2.5 font-mono">{v.sensitive ? MASK : v.value}</td>
                    <td className="px-4 py-2.5">{v.type}</td>
                    <td className="px-4 py-2.5">{v.scope}</td>
                    <td className="px-4 py-2.5">{v.env}</td>
                    <td className="px-4 py-2.5">{v.sensitive ? <StatusPill tone="warn">Secret</StatusPill> : <span className="text-ink-3">No</span>}</td>
                    <td className="px-4 py-2.5 whitespace-nowrap text-ink-2">{v.updated}</td>
                    <td className="px-4 py-2.5">
                      <div className="flex justify-end gap-1.5">
                        <Button type="button" variant="ghost" size="sm" onClick={() => setEditing(v)} aria-label={`Edit ${placeholder(v.name)}`}>
                          Edit
                        </Button>
                        <Button type="button" variant="ghost" size="sm" className="text-err" onClick={() => setDeleting(v)} aria-label={`Delete ${placeholder(v.name)}`}>
                          Delete
                        </Button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <p className="border-t border-line px-4 py-3 text-ink-3">
          Use them anywhere steps accept template text: email subjects and bodies, file name and folder patterns, metadata values. They resolve before the sample data fields.
        </p>
      </section>

      {editing && (
        <VariableDialog
          key={editing === 'new' ? 'new' : editing.id}
          variable={editing === 'new' ? null : editing}
          otherNames={variables.filter((v) => editing === 'new' || v.id !== editing.id).map((v) => v.name)}
          onSave={save}
          onClose={() => setEditing(null)}
        />
      )}

      {deleting && (
        <Dialog
          open
          onOpenChange={(open) => !open && setDeleting(null)}
          title={`Delete ${placeholder(deleting.name)}?`}
          description="Steps that use it will flag it as unresolved."
          footer={
            <>
              <Button type="button" onClick={() => setDeleting(null)}>
                Cancel
              </Button>
              <Button type="button" variant="primary" className="border-err bg-err" onClick={() => remove(deleting)}>
                Delete variable
              </Button>
            </>
          }
        >
          <p className="text-[13px] text-ink-2">This removes the variable from this page. Prototype only: nothing else changes.</p>
        </Dialog>
      )}

      <p className="mt-6 text-xs text-ink-3">Prototype: these variables are synthetic, kept in this page only and reset on reload.</p>
    </div>
  )
}
