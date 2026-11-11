import { Plus, Trash2 } from 'lucide-react'
import { useId } from 'react'

import { Button } from '@/components/ui/button'
import { Field, Input, Select, Switch, Textarea } from '@/components/ui/form'
import type { FolderLocationDto } from '@/lib/api/generated'
import { cn } from '@/lib/utils'

import { jsonObjectProblem } from '../model/payloadSchema'

/** The JSON Schema subset the node catalog uses (see ConfigSchemaValidator on the server), plus designer hints. */
export interface FieldSchema {
  type: 'object' | 'array' | 'string' | 'boolean'
  title?: string
  description?: string
  properties?: Record<string, FieldSchema>
  required?: string[]
  items?: FieldSchema
  enum?: string[]
  'x-enumLabels'?: string[]
  'x-widget'?: 'template' | 'template-version' | 'data-path' | 'pattern' | 'json' | 'endpoint-path' | 'mono' | 'folder-location-input' | 'folder-location-output'
  maxItems?: number
}

export interface TemplateOption {
  id: string
  name: string
  key: string
  /** Published version numbers, newest first; undefined while loading. */
  publishedVersions?: number[]
}

export interface FormContext {
  templates: TemplateOption[]
  /** Suggestions for data-path fields (from the selected template's data model). */
  dataPaths: string[]
  /** Server issues for a settings path, e.g. `fileName` or `metadata[0].key`. */
  errorsFor: (field: string) => string[]
  readOnly: boolean
  /** Approved folder locations (folder-input steps). */
  folderLocations?: FolderLocationDto[]
}

type Value = Record<string, unknown>

/** Settings panel generated from a node's config schema. `onChange(next, key)`: key groups typing into one undo step. */
export function SchemaForm({
  schema,
  value,
  onChange,
  ctx,
}: {
  schema: FieldSchema
  value: Value
  onChange: (next: Value, key: string) => void
  ctx: FormContext
}) {
  const listId = useId()
  const props = Object.entries(schema.properties ?? {})
  if (props.length === 0) return <p className="text-[12.5px] text-ink-3">This step has no settings.</p>

  const set = (name: string, v: unknown, key = name) => onChange({ ...value, [name]: v }, key)

  return (
    <div className="flex flex-col gap-3">
      <datalist id={listId}>
        {ctx.dataPaths.map((p) => (
          <option key={p} value={p} />
        ))}
      </datalist>
      {props.map(([name, field]) => (
        <PropertyField
          key={name}
          name={name}
          path={name}
          field={field}
          value={value[name]}
          parent={value}
          required={schema.required?.includes(name) ?? false}
          onChange={(v, key) => set(name, v, key)}
          ctx={ctx}
          listId={listId}
        />
      ))}
    </div>
  )
}

function PropertyField({
  name,
  path,
  field,
  value,
  parent,
  required,
  onChange,
  ctx,
  listId,
}: {
  name: string
  path: string
  field: FieldSchema
  value: unknown
  parent: Value
  required: boolean
  onChange: (v: unknown, key: string) => void
  ctx: FormContext
  listId: string
}) {
  const id = useId()
  const errors = ctx.errorsFor(path)
  const label = required && field.type === 'string' ? `${field.title ?? name} *` : (field.title ?? name)

  if (field.type === 'array') {
    return <ArrayField path={path} field={field} value={Array.isArray(value) ? value : []} onChange={onChange} ctx={ctx} listId={listId} label={label} />
  }

  if (field.type === 'boolean') {
    return (
      <div className="flex flex-col gap-1">
        <Switch checked={value === true} onCheckedChange={(v) => onChange(v, path)} label={field.title ?? name} disabled={ctx.readOnly} />
        {errors[0] && <p className="text-xs text-err">{errors[0]}</p>}
      </div>
    )
  }

  return (
    <Field label={label} htmlFor={id} hint={field.description} error={errors[0]}>
      <StringInput id={id} path={path} field={field} value={typeof value === 'string' ? value : ''} parent={parent} onChange={onChange} ctx={ctx} listId={listId} invalid={errors.length > 0} />
    </Field>
  )
}

function StringInput({
  id,
  path,
  field,
  value,
  parent,
  onChange,
  ctx,
  listId,
  invalid,
  ariaLabel,
}: {
  id?: string
  path: string
  field: FieldSchema
  value: string
  parent: Value
  onChange: (v: unknown, key: string) => void
  ctx: FormContext
  listId: string
  invalid: boolean
  ariaLabel?: string
}) {
  const common = { id, 'aria-label': ariaLabel, 'aria-invalid': invalid, disabled: ctx.readOnly }

  if (field.enum) {
    const labels = field['x-enumLabels'] ?? field.enum
    return (
      <Select {...common} value={value} onChange={(e) => onChange(e.target.value, path)}>
        {!field.enum.includes(value) && <option value={value}>{value || 'Choose…'}</option>}
        {field.enum.map((v, i) => (
          <option key={v} value={v}>
            {labels[i] ?? v}
          </option>
        ))}
      </Select>
    )
  }

  if (field['x-widget'] === 'template') {
    const known = ctx.templates.some((t) => t.id === value)
    return (
      <Select {...common} value={value} onChange={(e) => onChange(e.target.value, path)}>
        <option value="">{ctx.templates.length === 0 ? 'No templates yet' : 'Choose a template…'}</option>
        {!known && value && <option value={value}>Unknown template ({value.slice(0, 8)}…)</option>}
        {ctx.templates.map((t) => (
          <option key={t.id} value={t.id}>
            {t.name} ({t.key}){t.publishedVersions && t.publishedVersions.length === 0 ? ', not published' : ''}
          </option>
        ))}
      </Select>
    )
  }

  if (field['x-widget'] === 'folder-location-input' || field['x-widget'] === 'folder-location-output') {
    const input = field['x-widget'] === 'folder-location-input'
    const all = ctx.folderLocations ?? []
    const usable = all.filter((l) => (input ? l.input : l.output))
    const current = all.find((l) => l.key === value)
    return (
      <Select {...common} value={value} onChange={(e) => onChange(e.target.value, path)}>
        <option value="">{usable.length === 0 ? 'No approved locations yet' : 'Choose an approved location…'}</option>
        {value && !usable.some((l) => l.key === value) && (
          <option value={value}>{current ? `${current.name} (not approved for this)` : `Unknown location (${value})`}</option>
        )}
        {usable.map((l) => (
          <option key={l.key} value={l.key} disabled={!l.enabled}>
            {l.name} ({l.key}){l.enabled ? '' : ', unavailable'}
          </option>
        ))}
      </Select>
    )
  }

  if (field['x-widget'] === 'template-version') {
    const template = ctx.templates.find((t) => t.id === parent.templateId)
    const versions = template?.publishedVersions ?? []
    return (
      <Select {...common} value={value} onChange={(e) => onChange(e.target.value, path)}>
        <option value="latest-published">Latest published{versions[0] ? ` (currently v${versions[0]})` : ''}</option>
        {value !== 'latest-published' && !versions.map(String).includes(value) && <option value={value}>v{value} (not published)</option>}
        {versions.map((v) => (
          <option key={v} value={String(v)}>
            Version {v}
          </option>
        ))}
      </Select>
    )
  }

  if (field['x-widget'] === 'json') {
    return (
      <div className="flex flex-col gap-1">
        <Textarea
          {...common}
          value={value}
          rows={10}
          spellCheck={false}
          className="font-mono text-[12px]"
          onChange={(e) => onChange(e.target.value, path)}
        />
        {jsonObjectProblem(value) && <p className="text-[12px] text-err">{jsonObjectProblem(value)}</p>}
      </div>
    )
  }

  const mono = field['x-widget'] === 'data-path' || field['x-widget'] === 'pattern' || field['x-widget'] === 'endpoint-path' || field['x-widget'] === 'mono'
  return (
    <Input
      {...common}
      value={value}
      list={field['x-widget'] === 'data-path' ? listId : undefined}
      placeholder={field['x-widget'] === 'data-path' ? 'invoice.number' : undefined}
      className={cn(mono && 'font-mono text-[12.5px]')}
      spellCheck={!mono}
      onChange={(e) => onChange(e.target.value, path)}
    />
  )
}

function emptyItem(items: FieldSchema): unknown {
  if (items.type === 'string') return ''
  const obj: Value = {}
  for (const [k, f] of Object.entries(items.properties ?? {})) obj[k] = f.enum ? f.enum[0] : ''
  return obj
}

function ArrayField({
  path,
  field,
  value,
  onChange,
  ctx,
  listId,
  label,
}: {
  path: string
  field: FieldSchema
  value: unknown[]
  onChange: (v: unknown, key: string) => void
  ctx: FormContext
  listId: string
  label: string
}) {
  const items = field.items ?? { type: 'string' }
  const errors = ctx.errorsFor(path)
  const setItem = (i: number, v: unknown, key: string) => onChange(value.map((x, n) => (n === i ? v : x)), key)
  const full = field.maxItems !== undefined && value.length >= field.maxItems

  return (
    <fieldset className="flex flex-col gap-1.5">
      <legend className="mb-1 text-[12.5px] font-medium text-ink-2">{label}</legend>
      {value.length === 0 && <p className="text-xs text-ink-3">None yet.</p>}
      <ol className="flex flex-col gap-1.5">
        {value.map((item, i) => {
          const itemPath = `${path}[${i}]`
          const itemErrors = ctx.errorsFor(itemPath)
          return (
            <li key={i} className="flex items-start gap-1.5">
              <div className={cn('grid min-w-0 flex-1 gap-1.5', items.type === 'object' && 'grid-cols-2')}>
                {items.type === 'object' ? (
                  Object.entries(items.properties ?? {}).map(([k, f]) => {
                    const sub = `${itemPath}.${k}`
                    const subErrors = ctx.errorsFor(sub)
                    return (
                      <div key={k} className="min-w-0">
                        <StringInput
                          path={sub}
                          field={f}
                          value={String((item as Value)[k] ?? '')}
                          parent={item as Value}
                          onChange={(v, key) => setItem(i, { ...(item as Value), [k]: v }, key)}
                          ctx={ctx}
                          listId={listId}
                          invalid={subErrors.length > 0}
                          ariaLabel={`${label} ${i + 1}: ${f.title ?? k}`}
                        />
                        {subErrors[0] && <p className="mt-0.5 text-xs text-err">{subErrors[0]}</p>}
                      </div>
                    )
                  })
                ) : (
                  <div>
                    <StringInput
                      path={itemPath}
                      field={items}
                      value={String(item ?? '')}
                      parent={{}}
                      onChange={(v, key) => setItem(i, v, key)}
                      ctx={ctx}
                      listId={listId}
                      invalid={itemErrors.length > 0}
                      ariaLabel={`${label} ${i + 1}`}
                    />
                    {itemErrors[0] && <p className="mt-0.5 text-xs text-err">{itemErrors[0]}</p>}
                  </div>
                )}
              </div>
              {!ctx.readOnly && (
                <Button variant="ghost" size="icon" className="size-[34px] flex-none text-err" aria-label={`Remove ${label} ${i + 1}`} onClick={() => onChange(value.filter((_, n) => n !== i), path)}>
                  <Trash2 />
                </Button>
              )}
            </li>
          )
        })}
      </ol>
      {errors[0] && <p className="text-xs text-err">{errors[0]}</p>}
      {!ctx.readOnly && (
        <Button size="sm" className="self-start" disabled={full} onClick={() => onChange([...value, emptyItem(items)], path)}>
          <Plus /> Add
        </Button>
      )}
    </fieldset>
  )
}
