import { Plus, Trash2 } from 'lucide-react'
import type { ReactNode } from 'react'

import { Button } from '@/components/ui/button'
import { Field, Input, Select } from '@/components/ui/form'

import { FieldPicker } from './FieldPicker'
import type { FieldSchema, FormContext } from './SchemaForm'

type Config = Record<string, unknown>
type Condition = { join?: string; field?: string; op?: string; compare?: string; value?: string }

const NO_VALUE_OPS = ['is-empty', 'is-not-empty']
const text = (v: unknown) => (typeof v === 'string' ? v : '')

/**
 * Settings of the If / else step: rows of "field, operator, value-or-field", each row after the first joined to the one above
 * by AND or OR (AND is applied first). The field and a compared field are suggested from the form's data fields, like Map fields' sources.
 */
export function IfElseSettings({
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
  const conditions = Array.isArray(value.conditions) ? (value.conditions as Condition[]) : []
  const item = schema.properties?.conditions?.items?.properties ?? {}
  const max = schema.properties?.conditions?.maxItems
  const set = (next: Condition[], key: string) => onChange({ ...value, conditions: next }, key)
  const setRow = (i: number, patch: Condition, key: string) => set(conditions.map((c, n) => (n === i ? { ...c, ...patch } : c)), key)
  const options = (name: string) => {
    const f = item[name]
    return (f?.enum ?? []).map((v, n) => ({ value: v, label: f?.['x-enumLabels']?.[n] ?? v }))
  }
  const listErrors = ctx.errorsFor('conditions')

  return (
    <div className="flex flex-col gap-3">
      <fieldset className="flex flex-col gap-2">
        <legend className="mb-1 text-[12.5px] font-medium text-ink-2">Conditions</legend>
        <p className="text-xs text-ink-3">{schema.properties?.conditions?.description}</p>
        {conditions.map((c, i) => {
          const n = i + 1
          const fieldErrors = ctx.errorsFor(`conditions[${i}].field`)
          const valueErrors = ctx.errorsFor(`conditions[${i}].value`)
          const noValue = NO_VALUE_OPS.includes(text(c.op))
          const byField = c.compare === 'field'
          return (
            <div key={i} className="flex flex-col gap-2">
              {i > 0 && (
                <div className="flex items-center gap-2">
                  <span className="h-px flex-1 bg-line" aria-hidden="true" />
                  <Select
                    aria-label={`Condition ${n}: joined to the row above by`}
                    className="!w-auto !py-[3px] !text-[12px] font-semibold"
                    value={c.join === 'or' ? 'or' : 'and'}
                    disabled={ctx.readOnly}
                    onChange={(e) => setRow(i, { join: e.target.value }, `conditions[${i}].join`)}
                  >
                    {options('join').map((o) => (
                      <option key={o.value} value={o.value}>
                        {o.label}
                      </option>
                    ))}
                  </Select>
                  <span className="h-px flex-1 bg-line" aria-hidden="true" />
                </div>
              )}
              <div className="flex flex-col gap-2.5 rounded-lg border border-line p-3">
                <div className="flex items-center justify-between">
                  <span className="text-[12px] font-semibold text-ink-2">Condition {n}</span>
                  {!ctx.readOnly && conditions.length > 1 && (
                    <Button
                      variant="ghost"
                      size="icon"
                      className="-my-1 -mr-1.5 size-7 flex-none text-err"
                      aria-label={`Remove condition ${n}`}
                      onClick={() => set(conditions.filter((_, k) => k !== i), 'conditions')}
                    >
                      <Trash2 />
                    </Button>
                  )}
                </div>
                <Row label="Field" error={fieldErrors[0]}>
                  <FieldPicker
                    ariaLabel={`Condition ${n}: field`}
                    invalid={fieldErrors.length > 0}
                    paths={ctx.dataPaths}
                    value={text(c.field)}
                    disabled={ctx.readOnly}
                    onChange={(v) => setRow(i, { field: v }, `conditions[${i}].field`)}
                  />
                </Row>
                <Row label="Operator">
                  <Select
                    aria-label={`Condition ${n}: operator`}
                    value={text(c.op)}
                    disabled={ctx.readOnly}
                    onChange={(e) => setRow(i, { op: e.target.value }, `conditions[${i}].op`)}
                  >
                    {options('op').map((o) => (
                      <option key={o.value} value={o.value}>
                        {o.label}
                      </option>
                    ))}
                  </Select>
                </Row>
                {!noValue && (
                  <>
                    <Row label="Compare with">
                      <Select
                        aria-label={`Condition ${n}: compare with`}
                        value={byField ? 'field' : 'value'}
                        disabled={ctx.readOnly}
                        onChange={(e) => setRow(i, { compare: e.target.value, value: '' }, `conditions[${i}].compare`)}
                      >
                        {options('compare').map((o) => (
                          <option key={o.value} value={o.value}>
                            {o.label}
                          </option>
                        ))}
                      </Select>
                    </Row>
                    <Row label={byField ? 'Field to compare with' : 'Value'} error={valueErrors[0]}>
                      {byField ? (
                        <FieldPicker
                          ariaLabel={`Condition ${n}: field to compare with`}
                          invalid={valueErrors.length > 0}
                          paths={ctx.dataPaths}
                          value={text(c.value)}
                          disabled={ctx.readOnly}
                          onChange={(v) => setRow(i, { value: v }, `conditions[${i}].value`)}
                        />
                      ) : (
                        <Input
                          aria-label={`Condition ${n}: value`}
                          aria-invalid={valueErrors.length > 0}
                          placeholder="10000"
                          spellCheck={false}
                          value={text(c.value)}
                          disabled={ctx.readOnly}
                          onChange={(e) => setRow(i, { value: e.target.value }, `conditions[${i}].value`)}
                        />
                      )}
                    </Row>
                  </>
                )}
              </div>
            </div>
          )
        })}
        {listErrors[0] && <p className="text-xs text-err">{listErrors[0]}</p>}
        {!ctx.readOnly && (
          <Button
            size="sm"
            className="self-start"
            disabled={max !== undefined && conditions.length >= max}
            onClick={() => set([...conditions, { join: 'and', field: '', op: options('op')[0]?.value ?? 'equals', compare: 'value', value: '' }], 'conditions')}
          >
            <Plus /> Add condition
          </Button>
        )}
      </fieldset>
      <div className="grid grid-cols-2 gap-2">
        {(['yesLabel', 'noLabel'] as const).map((name) => (
          <Field key={name} label={schema.properties?.[name]?.title ?? name} htmlFor={`if-${name}`}>
            <Input
              id={`if-${name}`}
              maxLength={40}
              value={text(value[name])}
              disabled={ctx.readOnly}
              onChange={(e) => onChange({ ...value, [name]: e.target.value }, name)}
            />
          </Field>
        ))}
      </div>
    </div>
  )
}

/** The server prefixes its messages with the row ("Condition 2: enter…"); inside the row that is noise. */
const tidy = (message: string) => {
  const m = message.replace(/^Condition \d+: /, '')
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
