import {
  AlignCenter,
  AlignLeft,
  AlignRight,
  ArrowDown,
  ArrowUp,
  Bold,
  Braces,
  Copy,
  Italic,
  Link2,
  Plus,
  Settings2,
  Trash2,
  X,
} from 'lucide-react'
import { useEffect, useId, useState, type ReactNode } from 'react'

import { Button } from '@/components/ui/button'
import { Field, Input, Kbd, Segmented, Select, Textarea } from '@/components/ui/form'
import { cn } from '@/lib/utils'

import {
  FILTERS,
  MAX_REGION_HEIGHT_MM,
  REGION_LABELS,
  TYPE_LABELS,
  type Align,
  type Filter,
  type Layout,
  type LayoutElement,
  type PageSettings,
  type PositionedElement,
  type Region,
  type TableColumn,
  type TextStyle,
} from '../model/layout'
import { findElement, isValidId, renameElement, setBandHeight, updatePlacement, updateProps, updateStyle } from '../model/ops'
import { arrayPaths, rowFields, scalarPaths, type SchemaField } from '../model/schema'
import type { DesignIssue } from '../model/validate'
import type { Draft, Selection } from './designerState'
import { getPayload, hasPayload } from './dnd'
import { Section, TYPE_ICONS } from './SidePanel'

type EditOpts = { key?: string; select?: Selection }

export interface PropertiesProps {
  draft: Draft
  selection: Selection
  issues: DesignIssue[]
  fields: SchemaField[]
  readOnly: boolean
  templateName: string
  templateKey: string
  edit: (fn: (d: Draft) => Draft, opts?: EditOpts) => void
  editLayout: (fn: (l: Layout) => Layout, opts?: EditOpts) => void
  onDuplicate: (id: string) => void
  onDelete: (id: string) => void
  onReorder: (id: string, delta: number) => void
  onAddBand: (region: 'header' | 'footer') => void
  onRemoveBand: (region: 'header' | 'footer') => void
}

const FILTER_LABELS: Record<Filter, string> = {
  money: 'Money (€1,234.50)',
  number: 'Number (18,450)',
  date: 'Date (02 Nov 2026)',
  year: 'Year',
  month: 'Month (2 digits)',
  day: 'Day (2 digits)',
  upper: 'UPPER CASE',
  lower: 'lower case',
}

export function PropertiesPanel(props: PropertiesProps) {
  const { selection, draft } = props
  if (selection?.kind === 'element') {
    const found = findElement(draft.layout, selection.id)
    if (found) return <ElementProperties {...props} element={found.element} region={found.region} index={found.index} />
  }
  if (selection?.kind === 'region') return <RegionProperties {...props} region={selection.region} />
  return <TemplateSettings {...props} />
}

/* ---------- small inputs ---------- */

/** Number input that commits on blur or Enter (one undo step), and reverts invalid input. */
function NumberInput({
  value,
  onCommit,
  min,
  max,
  step = 0.5,
  label,
  optional,
  disabled,
  id,
}: {
  value: number | undefined
  onCommit: (v: number | undefined) => void
  min: number
  max: number
  step?: number
  label: string
  optional?: boolean
  disabled?: boolean
  id?: string
}) {
  const [text, setText] = useState(value === undefined ? '' : String(value))
  useEffect(() => setText(value === undefined ? '' : String(value)), [value])
  const commit = () => {
    if (text.trim() === '') {
      if (optional) onCommit(undefined)
      else setText(value === undefined ? '' : String(value))
      return
    }
    const n = Number(text)
    if (!Number.isFinite(n)) return setText(value === undefined ? '' : String(value))
    const clamped = Math.min(max, Math.max(min, n))
    setText(String(clamped))
    if (clamped !== value) onCommit(clamped)
  }
  return (
    <Input
      id={id}
      type="number"
      inputMode="decimal"
      aria-label={id ? undefined : label}
      min={min}
      max={max}
      step={step}
      value={text}
      disabled={disabled}
      onChange={(e) => setText(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') commit()
      }}
    />
  )
}

const HEX = /^#[0-9A-Fa-f]{6}$/

function ColorInput({ value, onChange, label, placeholder, disabled }: { value: string | undefined; onChange: (v: string | undefined) => void; label: string; placeholder: string; disabled?: boolean }) {
  const [text, setText] = useState(value ?? '')
  useEffect(() => setText(value ?? ''), [value])
  const commit = (t: string) => {
    const v = t.trim()
    if (v === '') onChange(undefined)
    else if (HEX.test(v)) onChange(v.toUpperCase())
    else setText(value ?? '')
  }
  return (
    <div className="flex items-center gap-1.5">
      <input
        type="color"
        aria-label={`${label} picker`}
        value={HEX.test(text) ? text : placeholder}
        disabled={disabled}
        onChange={(e) => {
          setText(e.target.value.toUpperCase())
          onChange(e.target.value.toUpperCase())
        }}
        className="h-[34px] w-9 flex-none cursor-pointer rounded-[7px] border border-line-strong bg-surface p-0.5"
      />
      <Input
        aria-label={label}
        value={text}
        placeholder={placeholder}
        disabled={disabled}
        className="font-mono text-[12.5px]"
        onChange={(e) => setText(e.target.value)}
        onBlur={() => commit(text)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') commit(text)
        }}
      />
    </div>
  )
}

/** Binding box from the design reference (.bind-drop): shows the path, accepts a dragged field, and offers a list to choose from. */
export function BindingPicker({
  label,
  value,
  options,
  onChange,
  invalid,
  disabled,
  placeholder = 'Drop a field here',
  relativeTo,
}: {
  label: string
  value: string
  options: SchemaField[]
  onChange: (path: string) => void
  invalid?: boolean
  disabled?: boolean
  placeholder?: string
  /** For table columns: dragged absolute paths under this array become row-relative. */
  relativeTo?: string
}) {
  const id = useId()
  const [over, setOver] = useState(false)
  const known = options.some((o) => o.path === value)
  return (
    <div className="flex flex-col gap-1.5">
      <div
        className={cn(
          'flex min-h-[34px] items-center gap-1.5 rounded-[7px] border border-dashed border-line-strong px-2.5 py-1.5 font-mono text-xs text-ink-2',
          value && !invalid && 'border-solid border-transparent bg-cyan-soft text-cyan-strong',
          value && invalid && 'border-solid border-transparent bg-err-soft text-err',
          over && 'border-dashed border-magenta bg-magenta-soft',
        )}
        onDragOver={(e) => {
          if (disabled || !hasPayload(e)) return
          e.preventDefault()
          setOver(true)
        }}
        onDragLeave={() => setOver(false)}
        onDrop={(e) => {
          setOver(false)
          const p = getPayload(e)
          if (!p || p.kind !== 'field' || disabled) return
          e.preventDefault()
          let path = p.path
          if (relativeTo) {
            if (!path.startsWith(`${relativeTo}.`)) return
            path = path.slice(relativeTo.length + 1)
          }
          onChange(path)
        }}
      >
        {value ? (
          <>
            <Braces className="size-3.5 flex-none" aria-hidden="true" />
            <span className="min-w-0 flex-1 truncate">{value}</span>
            {invalid && <span className="font-sans text-[11px]">unmapped</span>}
            {!disabled && (
              <button type="button" aria-label={`Clear ${label}`} className="rounded p-0.5 hover:bg-black/5" onClick={() => onChange('')}>
                <X className="size-3.5" />
              </button>
            )}
          </>
        ) : (
          <span className="text-ink-3">{placeholder}</span>
        )}
      </div>
      <Select id={id} aria-label={label} value={known ? value : ''} disabled={disabled || options.length === 0} onChange={(e) => onChange(e.target.value)}>
        <option value="">{options.length === 0 ? 'No fields in the data model' : 'Choose a field…'}</option>
        {options.map((o) => (
          <option key={o.path} value={o.path}>
            {o.path} ({o.kind})
          </option>
        ))}
      </Select>
    </div>
  )
}

function IssueList({ issues }: { issues: DesignIssue[] }) {
  if (issues.length === 0) return null
  return (
    <ul className="mx-3.5 mt-3 flex flex-col gap-1" aria-label="Problems with this element">
      {issues.map((i, n) => (
        <li key={n} className={cn('rounded-md px-2.5 py-1.5 text-[12.5px]', i.severity === 'error' ? 'bg-err-soft text-err' : 'bg-warn-soft text-warn')}>
          {i.message}
        </li>
      ))}
    </ul>
  )
}

/* ---------- panels ---------- */

function TemplateSettings(props: PropertiesProps) {
  const { draft, readOnly, edit } = props
  const page = draft.pageSettings
  const setPage = (fn: (p: PageSettings) => PageSettings) => edit((d) => ({ ...d, pageSettings: fn(d.pageSettings) }))
  const margins = ['top', 'right', 'bottom', 'left'] as const
  return (
    <>
      <Section title="Template settings" icon={Settings2}>
        <div className="grid grid-cols-2 gap-2">
          <Field label="Name" className="col-span-2">
            <Input value={props.templateName} readOnly aria-readonly className="bg-surface-2" />
          </Field>
          <Field label="Key">
            <Input value={props.templateKey} readOnly aria-readonly className="bg-surface-2 font-mono" />
          </Field>
          <Field label="Page" htmlFor="page-orientation">
            <Select
              id="page-orientation"
              value={page.orientation}
              disabled={readOnly}
              onChange={(e) => setPage((p) => ({ ...p, orientation: e.target.value as PageSettings['orientation'] }))}
            >
              <option value="portrait">A4 portrait</option>
              <option value="landscape">A4 landscape</option>
            </Select>
          </Field>
          {margins.map((m) => (
            <Field key={m} label={`Margin ${m} (mm)`} htmlFor={`margin-${m}`}>
              <NumberInput
                id={`margin-${m}`}
                label={`Margin ${m}`}
                value={page.marginsMm[m]}
                min={0}
                max={50}
                disabled={readOnly}
                onCommit={(v) => setPage((p) => ({ ...p, marginsMm: { ...p.marginsMm, [m]: v ?? 0 } }))}
              />
            </Field>
          ))}
        </div>
      </Section>
      <BandSettings {...props} region="header" />
      <BandSettings {...props} region="footer" />
      <Section title="Keyboard">
        <dl className="grid grid-cols-[auto_1fr] items-center gap-x-3 gap-y-1.5 text-[12.5px] text-ink-2">
          <dt><Kbd>Tab</Kbd></dt><dd>Move between elements</dd>
          <dt><Kbd>↑</Kbd> <Kbd>↓</Kbd></dt><dd>Select in the element list</dd>
          <dt><Kbd>Arrows</Kbd></dt><dd>Move a header/footer element 1 mm (<Kbd>Shift</Kbd> 5 mm)</dd>
          <dt><Kbd>Alt</Kbd>+<Kbd>↑↓</Kbd></dt><dd>Move a body block up or down</dd>
          <dt><Kbd>Del</Kbd></dt><dd>Delete the element</dd>
          <dt><Kbd>Ctrl</Kbd>+<Kbd>D</Kbd></dt><dd>Duplicate</dd>
          <dt><Kbd>Ctrl</Kbd>+<Kbd>Z</Kbd></dt><dd>Undo (<Kbd>Ctrl</Kbd>+<Kbd>Y</Kbd> redo)</dd>
          <dt><Kbd>Ctrl</Kbd>+<Kbd>S</Kbd></dt><dd>Save draft</dd>
          <dt><Kbd>Esc</Kbd></dt><dd>Clear the selection</dd>
        </dl>
      </Section>
    </>
  )
}

function BandSettings(props: PropertiesProps & { region: 'header' | 'footer' }) {
  const { region, draft, readOnly, editLayout } = props
  const band = draft.layout[region]
  return (
    <Section title={`${REGION_LABELS[region]} band`}>
      {band ? (
        <div className="flex items-end gap-2">
          <Field label="Height (mm)" htmlFor={`${region}-height`} className="flex-1" hint="Repeats on every page.">
            <NumberInput
              id={`${region}-height`}
              label={`${REGION_LABELS[region]} height`}
              value={band.heightMm}
              min={0}
              max={MAX_REGION_HEIGHT_MM}
              disabled={readOnly}
              onCommit={(v) => editLayout((l) => setBandHeight(l, region, v ?? 0))}
            />
          </Field>
          {!readOnly && (
            <Button variant="ghost" size="sm" className="mb-6 text-err" onClick={() => props.onRemoveBand(region)}>
              Remove
            </Button>
          )}
        </div>
      ) : (
        <div className="flex items-center gap-2 text-[12.5px] text-ink-3">
          No {region}.
          {!readOnly && (
            <Button size="sm" className="ml-auto" onClick={() => props.onAddBand(region)}>
              <Plus /> Add {region}
            </Button>
          )}
        </div>
      )}
    </Section>
  )
}

function RegionProperties(props: PropertiesProps & { region: Region }) {
  if (props.region === 'body')
    return (
      <Section title="Body">
        <p className="text-[12.5px] text-ink-2">
          Body blocks stack top to bottom across the content width and flow onto further pages. A repeating table breaks between rows and repeats its headings.
        </p>
        <p className="mt-2 text-[12.5px] text-ink-3">Reorder blocks by dragging them, or with Alt+↑/↓.</p>
      </Section>
    )
  return <BandSettings {...props} region={props.region} />
}

function ElementProperties(props: PropertiesProps & { element: LayoutElement; region: Region; index: number }) {
  const { element: e, region, readOnly, editLayout, issues, fields, draft } = props
  const Icon = TYPE_ICONS[e.type]
  const mine = issues.filter((i) => i.elementId === e.id)
  const has = (field: string) => mine.some((i) => i.field === field && i.severity === 'error')
  const k = (name: string) => `${e.id}:${name}`
  const setProps = (patch: Record<string, unknown>, key?: string) => editLayout((l) => updateProps(l, e.id, patch), { key })
  const setStyle = (patch: Record<string, unknown>, key?: string) => editLayout((l) => updateStyle(l, e.id, patch), { key })
  const count = region === 'body' ? (draft.layout.body?.blocks.length ?? 0) : (draft.layout[region]?.elements.length ?? 0)

  return (
    <>
      <div className="flex items-center gap-2 border-b border-line px-3.5 py-3">
        <Icon className="size-4 text-ink-2" strokeWidth={1.75} aria-hidden="true" />
        <h3 className="text-[13px] font-semibold">{TYPE_LABELS[e.type]}</h3>
        <span className="ml-auto text-xs text-ink-3">in {REGION_LABELS[region].toLowerCase()}</span>
      </div>
      <IssueList issues={mine} />

      <Section title="Placement">
        <div className="grid grid-cols-2 gap-2">
          <IdField element={e} readOnly={readOnly} invalid={has('id')} layout={draft.layout} editLayout={editLayout} />
          {region !== 'body' ? (
            (['xMm', 'yMm', 'widthMm', 'heightMm'] as const).map((f) => (
              <Field key={f} label={{ xMm: 'X (mm)', yMm: 'Y (mm)', widthMm: 'Width (mm)', heightMm: 'Height (mm)' }[f]} htmlFor={k(f)}>
                <NumberInput
                  id={k(f)}
                  label={f}
                  value={(e as PositionedElement)[f]}
                  min={f === 'widthMm' || f === 'heightMm' ? 0.1 : 0}
                  max={420}
                  disabled={readOnly}
                  onCommit={(v) => editLayout((l) => updatePlacement(l, e.id, { [f]: v ?? 0 }))}
                />
              </Field>
            ))
          ) : (
            <Field label="Space before (mm)" htmlFor={k('space')} className="col-span-2">
              <NumberInput
                id={k('space')}
                label="Space before"
                value={(e as { spaceBeforeMm?: number }).spaceBeforeMm}
                min={0}
                max={100}
                optional
                disabled={readOnly}
                onCommit={(v) => editLayout((l) => updatePlacement(l, e.id, { spaceBeforeMm: v }))}
              />
            </Field>
          )}
        </div>
      </Section>

      {e.type === 'field' && (
        <Section title="Data binding" icon={Link2}>
          <div className="flex flex-col gap-2.5">
            <BindingPicker
              label="Data field"
              value={e.props.binding}
              options={scalarPaths(fields)}
              invalid={has('binding')}
              disabled={readOnly}
              onChange={(binding) => setProps({ binding })}
            />
            <Field label="Label" htmlFor={k('label')} hint="Shown before the value, e.g. Invoice no.">
              <Input id={k('label')} value={e.props.label ?? ''} disabled={readOnly} onChange={(ev) => setProps({ label: ev.target.value || undefined }, k('label'))} />
            </Field>
            <FilterSelect id={k('filter')} value={e.props.filter} disabled={readOnly} onChange={(filter) => setProps({ filter })} />
          </div>
        </Section>
      )}

      {e.type === 'text' && <TextContent element={e} fields={fields} readOnly={readOnly} invalid={has('text')} setProps={setProps} k={k} />}

      {e.type === 'table' && <TableProperties element={e} fields={fields} readOnly={readOnly} has={has} setProps={setProps} k={k} />}

      {(e.type === 'text' || e.type === 'field' || e.type === 'table') && (
        <TypographySection style={e.props.style} readOnly={readOnly} setStyle={setStyle} k={k} />
      )}

      {e.type === 'divider' && (
        <Section title="Line">
          <div className="grid grid-cols-2 gap-2">
            <Field label="Thickness (pt)" htmlFor={k('thick')}>
              <NumberInput id={k('thick')} label="Thickness" value={e.props.thicknessPt} min={0.25} max={6} step={0.25} optional disabled={readOnly} onCommit={(v) => setProps({ thicknessPt: v })} />
            </Field>
            <Field label="Color" className="col-span-2">
              <ColorInput label="Line color" value={e.props.color} placeholder="#1A232C" disabled={readOnly} onChange={(color) => setProps({ color })} />
            </Field>
          </div>
        </Section>
      )}

      {e.type === 'box' && (
        <Section title="Box">
          <div className="flex flex-col gap-2">
            <Field label="Border color">
              <ColorInput label="Border color" value={e.props.borderColor} placeholder="#C4CDD6" disabled={readOnly} onChange={(borderColor) => setProps({ borderColor })} />
            </Field>
            <Field label="Fill (empty = none)">
              <ColorInput label="Fill color" value={e.props.fill} placeholder="#F8FAFB" disabled={readOnly} onChange={(fill) => setProps({ fill })} />
            </Field>
            <Field label="Corner radius (mm)" htmlFor={k('radius')}>
              <NumberInput id={k('radius')} label="Corner radius" value={e.props.radiusMm} min={0} max={20} optional disabled={readOnly} onCommit={(v) => setProps({ radiusMm: v })} />
            </Field>
          </div>
        </Section>
      )}

      {e.type === 'image' && <ImageProperties element={e} readOnly={readOnly} invalid={has('src')} setProps={setProps} k={k} />}

      {e.type === 'signature' && (
        <Section title="Signature">
          <Field label="Caption" htmlFor={k('sig')}>
            <Input id={k('sig')} value={e.props.label ?? ''} placeholder="Authorised signatory" disabled={readOnly} onChange={(ev) => setProps({ label: ev.target.value || undefined }, k('sig'))} />
          </Field>
        </Section>
      )}

      {!readOnly && (
        <div className="flex flex-wrap gap-1.5 px-3.5 py-3.5">
          <Button size="sm" onClick={() => props.onReorder(e.id, -1)} disabled={props.index === 0} aria-label={region === 'body' ? 'Move up' : 'Send backward'}>
            <ArrowUp /> {region === 'body' ? 'Up' : 'Back'}
          </Button>
          <Button size="sm" onClick={() => props.onReorder(e.id, 1)} disabled={props.index === count - 1} aria-label={region === 'body' ? 'Move down' : 'Bring forward'}>
            <ArrowDown /> {region === 'body' ? 'Down' : 'Forward'}
          </Button>
          <Button size="sm" onClick={() => props.onDuplicate(e.id)}>
            <Copy /> Duplicate
          </Button>
          <Button size="sm" variant="ghost" className="text-err" onClick={() => props.onDelete(e.id)}>
            <Trash2 /> Delete
          </Button>
        </div>
      )}
    </>
  )
}

function IdField({ element, readOnly, invalid, layout, editLayout }: { element: LayoutElement; readOnly: boolean; invalid: boolean; layout: Layout; editLayout: PropertiesProps['editLayout'] }) {
  const [text, setText] = useState(element.id)
  const [error, setError] = useState<string>()
  useEffect(() => {
    setText(element.id)
    setError(undefined)
  }, [element.id])
  const commit = () => {
    if (text === element.id) return
    if (!isValidId(text)) return setError('Use 1–64 letters, digits, - or _.')
    if (findElement(layout, text)) return setError('Another element has this name.')
    setError(undefined)
    editLayout((l) => renameElement(l, element.id, text), { select: { kind: 'element', id: text } })
  }
  return (
    <Field label="Name" htmlFor="element-id" className="col-span-2" error={error}>
      <Input
        id="element-id"
        value={text}
        disabled={readOnly}
        aria-invalid={invalid || !!error}
        className="font-mono text-[12.5px]"
        onChange={(e) => setText(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') commit()
        }}
      />
    </Field>
  )
}

function FilterSelect({ id, value, onChange, disabled, label = 'Format' }: { id: string; value: Filter | undefined; onChange: (f: Filter | undefined) => void; disabled?: boolean; label?: string }) {
  return (
    <Field label={label} htmlFor={id}>
      <Select id={id} value={value ?? ''} disabled={disabled} onChange={(e) => onChange((e.target.value || undefined) as Filter | undefined)}>
        <option value="">As is</option>
        {FILTERS.map((f) => (
          <option key={f} value={f}>
            {FILTER_LABELS[f]}
          </option>
        ))}
      </Select>
    </Field>
  )
}

function TextContent({
  element,
  fields,
  readOnly,
  invalid,
  setProps,
  k,
}: {
  element: Extract<LayoutElement, { type: 'text' }>
  fields: SchemaField[]
  readOnly: boolean
  invalid: boolean
  setProps: (patch: Record<string, unknown>, key?: string) => void
  k: (n: string) => string
}) {
  const [cursor, setCursor] = useState<number | null>(null)
  const text = element.props.text ?? ''
  const insert = (path: string) => {
    if (!path) return
    const at = cursor ?? text.length
    setProps({ text: `${text.slice(0, at)}{{${path}}}${text.slice(at)}` })
  }
  return (
    <Section title="Content">
      <div className="flex flex-col gap-2">
        <Field label="Text" htmlFor={k('text')} hint="Line breaks are kept. Insert data as {{path}} or {{path|money}}.">
          <Textarea
            id={k('text')}
            value={text}
            disabled={readOnly}
            aria-invalid={invalid}
            className="min-h-24 font-mono text-[12.5px]"
            onChange={(e) => {
              setCursor(e.target.selectionStart)
              setProps({ text: e.target.value }, k('text'))
            }}
            onSelect={(e) => setCursor((e.target as HTMLTextAreaElement).selectionStart)}
          />
        </Field>
        {!readOnly && (
          <Select aria-label="Insert data field" value="" onChange={(e) => insert(e.target.value)} disabled={scalarPaths(fields).length === 0}>
            <option value="">Insert data field…</option>
            {scalarPaths(fields).map((f) => (
              <option key={f.path} value={f.path}>
                {f.path}
              </option>
            ))}
          </Select>
        )}
      </div>
    </Section>
  )
}

function TableProperties({
  element,
  fields,
  readOnly,
  has,
  setProps,
  k,
}: {
  element: Extract<LayoutElement, { type: 'table' }>
  fields: SchemaField[]
  readOnly: boolean
  has: (f: string) => boolean
  setProps: (patch: Record<string, unknown>, key?: string) => void
  k: (n: string) => string
}) {
  const { binding, columns, emptyText } = element.props
  const rows = binding ? rowFields(fields, binding) : []
  const setColumns = (next: TableColumn[], key?: string) => setProps({ columns: next }, key)
  const setColumn = (i: number, patch: Partial<TableColumn>, key?: string) =>
    setColumns(
      columns.map((c, n) => {
        if (n !== i) return c
        const next: Record<string, unknown> = { ...c }
        for (const [pk, v] of Object.entries(patch)) {
          if (v === undefined) delete next[pk]
          else next[pk] = v
        }
        return next as unknown as TableColumn
      }),
      key,
    )
  const move = (i: number, d: number) => {
    const next = [...columns]
    const [c] = next.splice(i, 1)
    next.splice(i + d, 0, c!)
    setColumns(next)
  }
  const total = columns.reduce((s, c) => s + (c.widthPct ?? 0), 0)

  return (
    <>
      <Section title="Data binding" icon={Link2}>
        <BindingPicker
          label="Repeat over list"
          value={binding}
          options={arrayPaths(fields)}
          invalid={has('binding')}
          disabled={readOnly}
          placeholder="Drop a list here"
          onChange={(b) => setProps({ binding: b })}
        />
        <Field label="Text when the list is empty" htmlFor={k('empty')} className="mt-2.5">
          <Input id={k('empty')} value={emptyText ?? ''} disabled={readOnly} onChange={(e) => setProps({ emptyText: e.target.value || undefined }, k('empty'))} />
        </Field>
      </Section>
      <Section title={`Columns (${columns.length})`} note={total > 0 ? `${total}% set` : undefined}>
        {has('columns') && <p className="mb-2 text-xs text-err">Fix the column problems listed above.</p>}
        <ol className="flex flex-col gap-2.5">
          {columns.map((c, i) => (
            <li key={i} className="rounded-lg border border-line bg-surface-2 p-2.5" aria-label={`Column ${i + 1}`}>
              <div className="mb-2 flex items-center gap-1">
                <span className="text-xs font-semibold text-ink-2">Column {i + 1}</span>
                {!readOnly && (
                  <span className="ml-auto flex">
                    <Button variant="ghost" size="sm" className="px-1.5" aria-label={`Move column ${i + 1} left`} disabled={i === 0} onClick={() => move(i, -1)}>
                      <ArrowUp />
                    </Button>
                    <Button variant="ghost" size="sm" className="px-1.5" aria-label={`Move column ${i + 1} right`} disabled={i === columns.length - 1} onClick={() => move(i, 1)}>
                      <ArrowDown />
                    </Button>
                    <Button variant="ghost" size="sm" className="px-1.5 text-err" aria-label={`Remove column ${i + 1}`} disabled={columns.length === 1} onClick={() => setColumns(columns.filter((_, n) => n !== i))}>
                      <Trash2 />
                    </Button>
                  </span>
                )}
              </div>
              <div className="grid grid-cols-2 gap-2">
                <Field label="Heading" htmlFor={k(`col${i}h`)} className="col-span-2">
                  <Input id={k(`col${i}h`)} value={c.header} disabled={readOnly} onChange={(e) => setColumn(i, { header: e.target.value }, k(`col${i}h`))} />
                </Field>
                <div className="col-span-2">
                  <BindingPicker
                    label={`Column ${i + 1} field`}
                    value={c.binding}
                    options={rows}
                    relativeTo={binding}
                    disabled={readOnly || !binding}
                    invalid={!!c.binding && !rows.some((r) => r.path === c.binding)}
                    placeholder={binding ? 'Drop a row field here' : 'Choose the list first'}
                    onChange={(b) => setColumn(i, { binding: b })}
                  />
                </div>
                <FilterSelect id={k(`col${i}f`)} value={c.filter} disabled={readOnly} onChange={(filter) => setColumn(i, { filter })} />
                <Field label="Width (%)" htmlFor={k(`col${i}w`)}>
                  <NumberInput id={k(`col${i}w`)} label="Width" value={c.widthPct} min={1} max={100} step={1} optional disabled={readOnly} onCommit={(v) => setColumn(i, { widthPct: v })} />
                </Field>
                <div className="col-span-2">
                  <Segmented<Align>
                    label={`Column ${i + 1} alignment`}
                    value={c.align ?? 'left'}
                    onChange={(a) => !readOnly && setColumn(i, { align: a === 'left' ? undefined : a })}
                    options={ALIGN_OPTIONS}
                  />
                </div>
              </div>
            </li>
          ))}
        </ol>
        {!readOnly && (
          <Button size="sm" className="mt-2.5 w-full" onClick={() => setColumns([...columns, { header: 'Column', binding: '' }])}>
            <Plus /> Add column
          </Button>
        )}
      </Section>
    </>
  )
}

const ALIGN_OPTIONS = [
  { value: 'left' as const, label: 'Align left', icon: <AlignLeft /> },
  { value: 'center' as const, label: 'Align center', icon: <AlignCenter /> },
  { value: 'right' as const, label: 'Align right', icon: <AlignRight /> },
]

function TypographySection({
  style,
  readOnly,
  setStyle,
  k,
}: {
  style: TextStyle | undefined
  readOnly: boolean
  setStyle: (patch: Record<string, unknown>, key?: string) => void
  k: (n: string) => string
}) {
  return (
    <Section title="Typography">
      <div className="grid grid-cols-2 gap-2">
        <Field label="Size (pt)" htmlFor={k('size')}>
          <NumberInput id={k('size')} label="Font size" value={style?.fontSizePt} min={5} max={48} step={0.5} optional disabled={readOnly} onCommit={(v) => setStyle({ fontSizePt: v })} />
        </Field>
        <Field label="Font" htmlFor={k('font')}>
          <Select id={k('font')} value={style?.font ?? 'sans'} disabled={readOnly} onChange={(e) => setStyle({ font: e.target.value === 'sans' ? undefined : e.target.value })}>
            <option value="sans">Sans</option>
            <option value="serif">Serif</option>
            <option value="mono">Mono</option>
          </Select>
        </Field>
        <div className="col-span-2 flex flex-wrap items-center gap-2">
          <ToggleButton label="Bold" pressed={!!style?.bold} disabled={readOnly} onChange={(v) => setStyle({ bold: v || undefined })}>
            <Bold />
          </ToggleButton>
          <ToggleButton label="Italic" pressed={!!style?.italic} disabled={readOnly} onChange={(v) => setStyle({ italic: v || undefined })}>
            <Italic />
          </ToggleButton>
          <Segmented<Align>
            label="Alignment"
            value={style?.align ?? 'left'}
            onChange={(a) => !readOnly && setStyle({ align: a === 'left' ? undefined : a })}
            options={ALIGN_OPTIONS}
          />
        </div>
        <Field label="Color" className="col-span-2">
          <ColorInput label="Text color" value={style?.color} placeholder="#1A232C" disabled={readOnly} onChange={(color) => setStyle({ color })} />
        </Field>
      </div>
    </Section>
  )
}

function ToggleButton({ label, pressed, onChange, disabled, children }: { label: string; pressed: boolean; onChange: (v: boolean) => void; disabled?: boolean; children: ReactNode }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      aria-pressed={pressed}
      disabled={disabled}
      onClick={() => onChange(!pressed)}
      className={cn(
        'grid size-[30px] place-items-center rounded-md border border-line-strong text-ink-2 [&_svg]:size-4',
        pressed ? 'border-cyan bg-cyan-soft text-cyan-strong' : 'bg-surface hover:bg-surface-2',
      )}
    >
      {children}
    </button>
  )
}

function ImageProperties({
  element,
  readOnly,
  invalid,
  setProps,
  k,
}: {
  element: Extract<LayoutElement, { type: 'image' }>
  readOnly: boolean
  invalid: boolean
  setProps: (patch: Record<string, unknown>, key?: string) => void
  k: (n: string) => string
}) {
  const [error, setError] = useState<string>()
  const onFile = (file: File) => {
    if (file.type !== 'image/png' && file.type !== 'image/jpeg') return setError('Choose a PNG or JPEG file.')
    if (file.size > 500 * 1024) return setError('The image is larger than 500 KB.')
    const reader = new FileReader()
    reader.onload = () => {
      setError(undefined)
      setProps({ src: String(reader.result) })
    }
    reader.onerror = () => setError('Could not read the file.')
    reader.readAsDataURL(file)
  }
  return (
    <Section title="Image">
      <div className="flex flex-col gap-2">
        <Field label="File (PNG or JPEG, up to 500 KB)" htmlFor={k('file')} error={error ?? (invalid ? 'Choose a PNG or JPEG image.' : undefined)} hint="Embedded in the template; external links aren't allowed.">
          <Input id={k('file')} type="file" accept="image/png,image/jpeg" disabled={readOnly} onChange={(e) => e.target.files?.[0] && onFile(e.target.files[0])} />
        </Field>
        <Field label="Alternative text" htmlFor={k('alt')}>
          <Input id={k('alt')} value={element.props.alt ?? ''} disabled={readOnly} onChange={(e) => setProps({ alt: e.target.value || undefined }, k('alt'))} />
        </Field>
        <Field label="Fit" htmlFor={k('fit')}>
          <Select id={k('fit')} value={element.props.fit ?? 'contain'} disabled={readOnly} onChange={(e) => setProps({ fit: e.target.value === 'contain' ? undefined : e.target.value })}>
            <option value="contain">Contain (whole image)</option>
            <option value="cover">Cover (fill, may crop)</option>
          </Select>
        </Field>
      </div>
    </Section>
  )
}
