import { useState } from 'react'

import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { Field, Input, Select, Switch } from '@/components/ui/form'

import {
  MASK,
  placeholder,
  validateVariable,
  VARIABLE_ENVIRONMENTS,
  VARIABLE_SCOPES,
  VARIABLE_TYPES,
  type GlobalVariable,
  type VariableDraft,
  type VariableEnvironment,
  type VariableScope,
  type VariableType,
} from './variables'

const blank: VariableDraft = { name: '', type: 'Text', scope: 'Tenant', env: 'All', value: '', sensitive: false, description: '' }

const toDraft = (v: GlobalVariable): VariableDraft => ({ name: v.name, type: v.type, scope: v.scope, env: v.env, value: v.value, sensitive: v.sensitive, description: v.description })

/** Create or edit a variable. A sensitive variable has no value field: its value is managed in the bank's secret store. */
export function VariableDialog({
  variable,
  otherNames,
  onSave,
  onClose,
}: {
  /** The variable to edit, or null to create one. */
  variable: GlobalVariable | null
  otherNames: string[]
  onSave: (draft: VariableDraft) => void
  onClose: () => void
}) {
  const isNew = variable === null
  const [draft, setDraft] = useState<VariableDraft>(variable ? toDraft(variable) : blank)
  const [submitted, setSubmitted] = useState(false)
  const errors = validateVariable(draft, otherNames)
  const set = (patch: Partial<VariableDraft>) => setDraft((d) => ({ ...d, ...patch }))

  const save = () => {
    setSubmitted(true)
    if (errors.name || errors.value) return
    onSave({ ...draft, name: draft.name.trim(), value: draft.sensitive ? '' : draft.value.trim(), description: draft.description.trim() })
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => !open && onClose()}
      title={isNew ? 'New variable' : placeholder(variable.name)}
      description={isNew ? 'Reusable in every workflow.' : `${variable.type} · ${variable.scope}`}
      footer={
        <>
          <Button type="button" onClick={onClose}>
            Cancel
          </Button>
          <Button type="button" variant="primary" onClick={save}>
            {isNew ? 'Create variable' : 'Save changes'}
          </Button>
        </>
      }
    >
      <form className="flex flex-col gap-3.5" noValidate onSubmit={(e) => { e.preventDefault(); save() }}>
        <Field label="Name" htmlFor="gv-name" error={submitted ? errors.name : undefined} hint="Used as {{name}} in step text.">
          <Input id="gv-name" className="font-mono" value={draft.name} onChange={(e) => set({ name: e.target.value })} aria-invalid={submitted && !!errors.name} placeholder="e.g. companyName" autoFocus />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Type" htmlFor="gv-type">
            <Select
              id="gv-type"
              value={draft.type}
              onChange={(e) => {
                const type = e.target.value as VariableType
                set({ type, sensitive: type === 'Secret' })
              }}
            >
              {VARIABLE_TYPES.map((t) => (
                <option key={t}>{t}</option>
              ))}
            </Select>
          </Field>
          <Field label="Scope" htmlFor="gv-scope">
            <Select id="gv-scope" value={draft.scope} onChange={(e) => set({ scope: e.target.value as VariableScope })}>
              {VARIABLE_SCOPES.map((s) => (
                <option key={s}>{s}</option>
              ))}
            </Select>
          </Field>
        </div>
        <Field label="Environment" htmlFor="gv-env">
          <Select id="gv-env" value={draft.env} onChange={(e) => set({ env: e.target.value as VariableEnvironment })}>
            {VARIABLE_ENVIRONMENTS.map((s) => (
              <option key={s}>{s}</option>
            ))}
          </Select>
        </Field>
        {draft.sensitive ? (
          <Field label="Value" hint="Stored in the bank’s secret store. Verso never asks for it here and never displays it.">
            <Input value={MASK} disabled readOnly aria-label="Value (managed outside Verso)" className="font-mono" />
          </Field>
        ) : (
          <Field label="Value" htmlFor="gv-value" error={submitted ? errors.value : undefined}>
            <Input id="gv-value" className="font-mono" value={draft.value} onChange={(e) => set({ value: e.target.value })} aria-invalid={submitted && !!errors.value} placeholder="e.g. Halcyon Energy" />
          </Field>
        )}
        <Field label="Description" htmlFor="gv-desc">
          <Input id="gv-desc" value={draft.description} onChange={(e) => set({ description: e.target.value })} placeholder="What is this used for?" />
        </Field>
        <Switch
          checked={draft.sensitive}
          onCheckedChange={(sensitive) => set({ sensitive, type: sensitive ? 'Secret' : draft.type === 'Secret' ? 'Text' : draft.type })}
          label="Sensitive (secret): referenced by workflows, never printed into documents"
        />
        <p className="text-xs text-ink-3">Prototype: variables are kept in this page only and reset on reload.</p>
      </form>
    </Dialog>
  )
}
