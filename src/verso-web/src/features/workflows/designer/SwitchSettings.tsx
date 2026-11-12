import { Plus, Trash2 } from 'lucide-react'
import type { ReactNode } from 'react'

import { Button } from '@/components/ui/button'
import { Field, Input, Select, Switch } from '@/components/ui/form'

import { FieldPicker } from './FieldPicker'
import type { FieldSchema, FormContext } from './SchemaForm'

type Config = Record<string, unknown>
type Branch = { id?: string; label?: string; compare?: string; value?: string }

const text = (v: unknown) => (typeof v === 'string' ? v : '')

/** The server prefixes its messages with the row ("Branch 2: enter…"); inside the row that is noise. */
const tidy = (message: string) => {
  const m = message.replace(/^Branch \d+: /, '')
  return m.charAt(0).toUpperCase() + m.slice(1)
}

function Row({ label, error, children }: { label: string; error?: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-[11.5px] font-medium text-ink-3">{label}</span>
      {children}
      {error && <p className="text-xs text-err">{tidy(error)}</p>}
    </div>
  )
}

/** An output id no branch uses yet. It stays with the branch for good, so renaming a branch keeps its connection. */
function newBranchId(branches: Branch[]): string {
  const taken = new Set(branches.map((b) => b.id))
  for (let n = branches.length + 1; ; n++) if (!taken.has(`branch-${n}`)) return `branch-${n}`
}

/**
 * Settings of the Switch step: the field and operator, then one card per branch (its name, and the value or field the data's
 * field is compared with). Each branch is an output of the step; the first branch that matches wins. "Other" catches the rest.
 * Field suggestions come from the form's data fields, as in If / else and Map fields.
 */
export function SwitchSettings({
  schema,
  value,
  onChange,
  ctx,
}: {
  schema: FieldSchema
  value: Config
  onChange: (next: Config, key: string) => void
  ctx: FormContext
}) {
  const branches = Array.isArray(value.branches) ? (value.branches as Branch[]) : []
  const max = schema.properties?.branches?.maxItems
  const ops = schema.properties?.op
  const compares = schema.properties?.branches?.items?.properties?.compare
  const set = (next: Branch[], key: string) => onChange({ ...value, branches: next }, key)
  const setRow = (i: number, patch: Branch, key: string) => set(branches.map((b, n) => (n === i ? { ...b, ...patch } : b)), key)
  const fieldErrors = ctx.errorsFor('field')
  const listErrors = ctx.errorsFor('branches')

  return (
    <div className="flex flex-col gap-3">
      <Field label="Field *" htmlFor="switch-field" hint={schema.properties?.field?.description} error={fieldErrors[0]}>
        <FieldPicker
          id="switch-field"
          ariaLabel="Field *"
          invalid={fieldErrors.length > 0}
          paths={ctx.dataPaths}
          value={text(value.field)}
          disabled={ctx.readOnly}
          onChange={(v) => onChange({ ...value, field: v }, 'field')}
        />
      </Field>
      <Field label="Operator" htmlFor="switch-op" hint={ops?.description}>
        <Select id="switch-op" value={text(value.op)} disabled={ctx.readOnly} onChange={(e) => onChange({ ...value, op: e.target.value }, 'op')}>
          {(ops?.enum ?? []).map((v, n) => (
            <option key={v} value={v}>
              {ops?.['x-enumLabels']?.[n] ?? v}
            </option>
          ))}
        </Select>
      </Field>

      <fieldset className="flex flex-col gap-2">
        <legend className="mb-1 text-[12.5px] font-medium text-ink-2">Branches</legend>
        <p className="text-xs text-ink-3">Each branch is an output of the step. The first one whose value matches is taken.</p>
        {branches.map((b, i) => {
          const n = i + 1
          const labelErrors = ctx.errorsFor(`branches[${i}].label`)
          const idErrors = ctx.errorsFor(`branches[${i}].id`)
          const valueErrors = ctx.errorsFor(`branches[${i}].value`)
          const byField = b.compare === 'field'
          return (
            <div key={text(b.id) || i} className="flex flex-col gap-2.5 rounded-lg border border-line p-3">
              <div className="flex items-center justify-between">
                <span className="text-[12px] font-semibold text-ink-2">Branch {n}</span>
                {!ctx.readOnly && (
                  <Button
                    variant="ghost"
                    size="icon"
                    className="-my-1 -mr-1.5 size-7 flex-none text-err"
                    aria-label={`Remove branch ${n}`}
                    onClick={() => set(branches.filter((_, k) => k !== i), 'branches')}
                  >
                    <Trash2 />
                  </Button>
                )}
              </div>
              <Row label="Branch name" error={labelErrors[0] ?? idErrors[0]}>
                <Input
                  aria-label={`Branch ${n}: name`}
                  aria-invalid={labelErrors.length + idErrors.length > 0}
                  maxLength={40}
                  value={text(b.label)}
                  disabled={ctx.readOnly}
                  onChange={(e) => setRow(i, { label: e.target.value }, `branches[${i}].label`)}
                />
              </Row>
              <Row label="Compare with">
                <Select
                  aria-label={`Branch ${n}: compare with`}
                  value={byField ? 'field' : 'value'}
                  disabled={ctx.readOnly}
                  onChange={(e) => setRow(i, { compare: e.target.value, value: '' }, `branches[${i}].compare`)}
                >
                  {(compares?.enum ?? []).map((v, k) => (
                    <option key={v} value={v}>
                      {compares?.['x-enumLabels']?.[k] ?? v}
                    </option>
                  ))}
                </Select>
              </Row>
              <Row label={byField ? 'Field to compare with' : 'Value'} error={valueErrors[0]}>
                {byField ? (
                  <FieldPicker
                    ariaLabel={`Branch ${n}: field to compare with`}
                    invalid={valueErrors.length > 0}
                    paths={ctx.dataPaths}
                    value={text(b.value)}
                    disabled={ctx.readOnly}
                    onChange={(v) => setRow(i, { value: v }, `branches[${i}].value`)}
                  />
                ) : (
                  <Input
                    aria-label={`Branch ${n}: value`}
                    aria-invalid={valueErrors.length > 0}
                    placeholder="email"
                    spellCheck={false}
                    value={text(b.value)}
                    disabled={ctx.readOnly}
                    onChange={(e) => setRow(i, { value: e.target.value }, `branches[${i}].value`)}
                  />
                )}
              </Row>
            </div>
          )
        })}
        {listErrors[0] && <p className="text-xs text-err">{listErrors[0]}</p>}
        {!ctx.readOnly && (
          <Button
            size="sm"
            className="self-start"
            disabled={max !== undefined && branches.length >= max}
            onClick={() => set([...branches, { id: newBranchId(branches), label: 'New branch', compare: 'value', value: '' }], 'branches')}
          >
            <Plus /> Add branch
          </Button>
        )}
      </fieldset>

      <div className="flex flex-col gap-1">
        <Switch
          checked={value.other === true}
          onCheckedChange={(v) => onChange({ ...value, other: v }, 'other')}
          label={schema.properties?.other?.title ?? 'Add an “Other” path'}
          disabled={ctx.readOnly}
        />
        <p className="text-xs text-ink-3">Turned off, a record that matches no branch ends the run at this step.</p>
      </div>
    </div>
  )
}
