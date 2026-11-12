import { CircleAlert, CircleCheck } from 'lucide-react'

import { Field, Input, Select } from '@/components/ui/form'

import { FieldPicker } from './FieldPicker'
import type { FieldSchema, FormContext } from './SchemaForm'

type Config = Record<string, unknown>

const text = (v: unknown) => (typeof v === 'string' ? v : '')

const MODES = [
  { id: 'each', label: 'Each: run the next steps once per item' },
  { id: 'split', label: 'Split: each item becomes its own record' },
] as const

/** How many items a sample value holds: a list's length, 1 for a single value, 0 for nothing. */
export function itemCount(list: unknown): number {
  if (Array.isArray(list)) return list.length
  return list === undefined || list === null || list === '' ? 0 : 1
}

/**
 * Settings of the For each step: the list to go through, whether each item is added to the record or becomes the record, what the
 * item is called, and the names of the two paths (the loop and "no items"). The preview counts the items in the trigger's sample.
 * The list is picked from the lists of the trigger's sample payload.
 */
export function ForEachSettings({
  schema,
  value,
  onChange,
  ctx,
  sampleList,
}: {
  schema: FieldSchema
  value: Config
  onChange: (next: Config, key: string) => void
  ctx: FormContext
  /** The sample's value at the chosen list field; undefined when there is no sample or the field isn't in it. */
  sampleList?: unknown
}) {
  const field = text(value.field)
  const mode = value.mode === 'split' ? 'split' : 'each'
  const item = text(value.item)
  const itemName = item.trim() || 'item'
  const fieldErrors = ctx.errorsFor('field')
  const itemErrors = ctx.errorsFor('item')
  const count = itemCount(sampleList)
  const emptyLabel = text(value.emptyLabel).trim() || 'No items'
  const set = (key: string, v: string) => onChange({ ...value, [key]: v }, key)

  return (
    <div className="flex flex-col gap-3">
      <fieldset className="flex flex-col gap-3">
        <legend className="mb-1 text-[12.5px] font-medium text-ink-2">Loop</legend>
        <Field label="List field *" htmlFor="foreach-field" hint={schema.properties?.field?.description ?? 'A field that holds a list, such as the invoice line items.'} error={fieldErrors[0]}>
          <FieldPicker
            id="foreach-field"
            ariaLabel="List field *"
            invalid={fieldErrors.length > 0}
            paths={ctx.dataPaths}
            value={field}
            disabled={ctx.readOnly}
            onChange={(v) => set('field', v)}
          />
        </Field>
        <Field
          label="Loop behaviour"
          htmlFor="foreach-mode"
          hint={
            mode === 'split'
              ? 'Split: turn each item into its own record that runs the rest of the workflow.'
              : 'Each: run the next steps once per item, keeping the whole record.'
          }
        >
          <Select id="foreach-mode" value={mode} disabled={ctx.readOnly} onChange={(e) => set('mode', e.target.value)}>
            {MODES.map((m) => (
              <option key={m.id} value={m.id}>
                {m.label}
              </option>
            ))}
          </Select>
        </Field>
        <Field
          label="Item name"
          htmlFor="foreach-item"
          error={itemErrors[0]}
          hint={
            mode === 'split'
              ? `A plain value (not an object) becomes the record { ${itemName}: value }.`
              : `Later steps refer to each item as ${itemName}.description, ${itemName}.amount and so on.`
          }
        >
          <Input
            id="foreach-item"
            className="font-mono"
            aria-invalid={itemErrors.length > 0}
            maxLength={40}
            placeholder="item"
            spellCheck={false}
            value={item}
            disabled={ctx.readOnly}
            onChange={(e) => set('item', e.target.value)}
          />
        </Field>
      </fieldset>

      <fieldset className="flex flex-col gap-3">
        <legend className="mb-1 text-[12.5px] font-medium text-ink-2">Path names</legend>
        <Field label="When there are items" htmlFor="foreach-each-label">
          <Input
            id="foreach-each-label"
            aria-label="Name for the loop path"
            maxLength={40}
            value={text(value.eachLabel)}
            disabled={ctx.readOnly}
            onChange={(e) => set('eachLabel', e.target.value)}
          />
        </Field>
        <Field label="When the list is empty" htmlFor="foreach-empty-label">
          <Input
            id="foreach-empty-label"
            aria-label="Name for the empty path"
            maxLength={40}
            value={text(value.emptyLabel)}
            disabled={ctx.readOnly}
            onChange={(e) => set('emptyLabel', e.target.value)}
          />
        </Field>
      </fieldset>

      <div className="flex flex-col gap-1.5" aria-label="Preview">
        <span className="text-[12.5px] font-medium text-ink-2">Preview</span>
        {field.trim() === '' ? (
          <p className="text-xs text-ink-3">Choose a list field to see how many times the loop runs.</p>
        ) : sampleList === undefined ? (
          <p className="text-xs text-ink-3">The trigger’s sample payload has no “{field}”, so the number of items isn’t known here.</p>
        ) : count > 0 ? (
          <p className="flex items-center gap-1.5 text-xs text-ok">
            <CircleCheck className="size-3.5 flex-none" aria-hidden="true" />
            <span>
              The next steps run {count} time{count === 1 ? '' : 's'} — once per item.
            </span>
          </p>
        ) : (
          <p className="flex items-center gap-1.5 text-xs text-warn">
            <CircleAlert className="size-3.5 flex-none" aria-hidden="true" />
            <span>The list is empty, so the “{emptyLabel}” path is used.</span>
          </p>
        )}
        <p className="text-xs text-ink-3">
          To end an iteration early or at a chosen step, connect that step back into this one. Without it, an iteration ends where its path ends.
        </p>
      </div>
    </div>
  )
}
