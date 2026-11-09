import {
  Braces,
  Calendar,
  ChevronRight,
  CircleCheck,
  Database,
  FileJson,
  Folder,
  Hash,
  ImageIcon,
  Layers,
  List,
  Minus,
  Signature,
  Square,
  Table,
  ToggleLeft,
  Type,
  Upload,
  type LucideIcon,
} from 'lucide-react'
import { useRef, type KeyboardEvent, type ReactNode } from 'react'

import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

import { REGION_LABELS, REGIONS, TYPE_LABELS, type ElementType, type Layout, type LayoutElement, type Region } from '../model/layout'
import { regionItems } from '../model/ops'
import type { FieldKind, SchemaField } from '../model/schema'
import { elementLabel } from './Canvas'
import type { Selection } from './designerState'
import { setPayload } from './dnd'

export const TYPE_ICONS: Record<ElementType, LucideIcon> = {
  text: Type,
  field: Braces,
  table: Table,
  image: ImageIcon,
  divider: Minus,
  box: Square,
  signature: Signature,
}

const PALETTE: ElementType[] = ['text', 'field', 'table', 'image', 'divider', 'box', 'signature']

export function Section({ title, icon: Icon, note, children, actions }: { title: string; icon?: LucideIcon; note?: ReactNode; children: ReactNode; actions?: ReactNode }) {
  return (
    <section className="border-b border-line px-3.5 pt-3.5 pb-4" aria-label={title}>
      <h3 className="mb-2.5 flex items-center gap-2 text-[13px] font-semibold whitespace-nowrap">
        {Icon && <Icon className="size-4 text-ink-2" strokeWidth={1.75} aria-hidden="true" />}
        {title}
        {note && <span className="ml-auto text-xs font-normal text-ink-3">{note}</span>}
        {actions && <span className="ml-auto flex items-center gap-1">{actions}</span>}
      </h3>
      {children}
    </section>
  )
}

export function Palette({ disabled, onAdd }: { disabled: boolean; onAdd: (type: ElementType) => void }) {
  return (
    <Section title="Components" note={disabled ? undefined : 'Click or drag'}>
      <div className="grid grid-cols-2 gap-1.5">
        {PALETTE.map((type) => {
          const Icon = TYPE_ICONS[type]
          return (
            <button
              key={type}
              type="button"
              disabled={disabled}
              draggable={!disabled}
              onDragStart={(e) => setPayload(e, { kind: 'new', type })}
              onClick={() => onAdd(type)}
              aria-label={`Add ${TYPE_LABELS[type]}`}
              className="flex cursor-grab flex-col items-start gap-1.5 rounded-lg border border-line bg-surface-2 px-2.5 py-[9px] text-left text-[12.5px] font-medium text-ink hover:border-cyan hover:text-cyan-strong disabled:cursor-default disabled:opacity-50 disabled:hover:border-line disabled:hover:text-ink"
            >
              <Icon className="size-4 text-ink-2" strokeWidth={1.75} aria-hidden="true" />
              {TYPE_LABELS[type]}
            </button>
          )
        })}
      </div>
    </Section>
  )
}

const KIND_ICONS: Record<FieldKind, LucideIcon> = {
  string: Type,
  date: Calendar,
  number: Hash,
  integer: Hash,
  boolean: ToggleLeft,
  object: Folder,
  array: List,
  unknown: Type,
}

export function DataModel({
  fields,
  used,
  readOnly,
  onAddField,
  onImportSample,
  onEditJson,
}: {
  fields: SchemaField[]
  /** Absolute paths bound somewhere (table columns as `array.field`). */
  used: Set<string>
  readOnly: boolean
  onAddField: (field: SchemaField) => void
  onImportSample: (file: File) => void
  onEditJson: () => void
}) {
  const fileRef = useRef<HTMLInputElement>(null)

  const renderFields = (list: SchemaField[], prefix: string, inArray: boolean): ReactNode =>
    list.map((f) => {
      const absolute = prefix ? `${prefix}.${f.name}` : f.path
      if (f.kind === 'object' || f.kind === 'array') {
        const Icon = KIND_ICONS[f.kind]
        return (
          <details key={absolute} open className="group/d">
            <summary className="flex cursor-pointer list-none items-center gap-1.5 rounded-[5px] p-1 font-medium hover:bg-surface-2 [&::-webkit-details-marker]:hidden">
              <ChevronRight className="size-3.5 text-ink-3 transition-transform group-open/d:rotate-90" aria-hidden="true" />
              <Icon className="size-[13px] text-ink-3" aria-hidden="true" />
              {f.name}
              {f.kind === 'array' && <span className="text-ink-3">[ ]</span>}
              {f.kind === 'array' && !readOnly && !inArray && (
                <button
                  type="button"
                  draggable
                  onDragStart={(e) => setPayload(e, { kind: 'field', path: absolute, fieldKind: 'array' })}
                  onClick={(e) => {
                    e.preventDefault()
                    onAddField({ ...f, path: absolute })
                  }}
                  className="ml-auto rounded px-1 text-[11px] font-normal text-cyan hover:bg-cyan-soft"
                  aria-label={`Add table for ${absolute}`}
                >
                  + table
                </button>
              )}
            </summary>
            <div className="pl-3">{renderFields(f.children, absolute, inArray || f.kind === 'array')}</div>
          </details>
        )
      }
      const Icon = KIND_ICONS[f.kind]
      const mapped = used.has(absolute)
      const content = (
        <>
          <Icon className="size-[13px] flex-none text-ink-3" aria-hidden="true" />
          <span className={cn('truncate', !mapped && 'text-ink-2')}>{f.name}</span>
          {mapped && <CircleCheck className="size-3.5 flex-none text-ok" aria-label="mapped" />}
          <span className="ml-auto font-mono text-[11px] text-ink-3">{f.kind}</span>
        </>
      )
      if (inArray || readOnly)
        return (
          <div key={absolute} className="flex items-center gap-[7px] rounded-[5px] py-1 pr-1.5 pl-6">
            {content}
          </div>
        )
      return (
        <button
          key={absolute}
          type="button"
          draggable
          onDragStart={(e) => setPayload(e, { kind: 'field', path: absolute, fieldKind: f.kind })}
          onClick={() => onAddField({ ...f, path: absolute })}
          aria-label={`Add field ${absolute}`}
          className="flex w-full cursor-grab items-center gap-[7px] rounded-[5px] py-1 pr-1.5 pl-6 text-left hover:bg-cyan-soft"
        >
          {content}
        </button>
      )
    })

  return (
    <Section
      title="Data model"
      icon={Database}
      actions={
        !readOnly && (
          <>
            <Button variant="ghost" size="sm" onClick={() => fileRef.current?.click()} title="Import synthetic sample data (JSON file)">
              <Upload /> Import
            </Button>
            <Button variant="ghost" size="sm" onClick={onEditJson} aria-label="Edit data model and sample data as JSON" title="Edit JSON">
              <FileJson />
            </Button>
            <input
              ref={fileRef}
              type="file"
              accept="application/json,.json"
              className="hidden"
              aria-label="Sample data file"
              onChange={(e) => {
                const file = e.target.files?.[0]
                if (file) onImportSample(file)
                e.target.value = ''
              }}
            />
          </>
        )
      }
    >
      {fields.length === 0 ? (
        <p className="text-[12.5px] text-ink-3">
          No data model yet. Import a synthetic sample JSON file; the fields are derived from it. Bindings stay unmapped until the data model has them.
        </p>
      ) : (
        <>
          <div className="text-[13px]">{renderFields(fields, '', false)}</div>
          {!readOnly && <p className="mt-2 text-xs text-ink-3">Click a field to add it, or drag it onto the page or onto a binding box.</p>}
        </>
      )}
    </Section>
  )
}

interface TreeRow {
  key: string
  selection: Exclude<Selection, null>
  level: 1 | 2
  label: string
  element?: LayoutElement
  region: Region
}

function treeRows(layout: Layout): TreeRow[] {
  const rows: TreeRow[] = []
  for (const region of REGIONS) {
    rows.push({ key: `r:${region}`, selection: { kind: 'region', region }, level: 1, label: REGION_LABELS[region], region })
    for (const e of regionItems(layout, region))
      rows.push({ key: `e:${e.id}`, selection: { kind: 'element', id: e.id }, level: 2, label: elementLabel(e), element: e, region })
  }
  return rows
}

function sameSelection(a: Selection, b: Selection) {
  if (!a || !b) return a === b
  if (a.kind === 'element' && b.kind === 'element') return a.id === b.id
  if (a.kind === 'region' && b.kind === 'region') return a.region === b.region
  return false
}

/** Element tree: regions with their elements. Arrow keys move the selection; it is the keyboard way to select elements. */
export function ElementTree({
  layout,
  selection,
  flagged,
  onSelect,
  onElementKey,
}: {
  layout: Layout
  selection: Selection
  flagged: Map<string, 'error' | 'warning'>
  onSelect: (s: Selection) => void
  onElementKey: (e: KeyboardEvent, id: string) => void
}) {
  const rows = treeRows(layout)
  const listRef = useRef<HTMLUListElement>(null)
  const selectedIndex = rows.findIndex((r) => sameSelection(r.selection, selection))
  const focusIndex = selectedIndex === -1 ? 0 : selectedIndex

  const focusRow = (index: number) => {
    const row = rows[index]
    if (!row) return
    onSelect(row.selection)
    // Rows are keyed and already rendered, so focus can move right away (no frame delay between quick key presses).
    listRef.current?.querySelector<HTMLElement>(`[data-row="${CSS.escape(row.key)}"]`)?.focus()
  }

  const onKeyDown = (e: KeyboardEvent, index: number, row: TreeRow) => {
    if (e.key === 'ArrowDown' && !e.altKey) {
      e.preventDefault()
      focusRow(Math.min(rows.length - 1, index + 1))
    } else if (e.key === 'ArrowUp' && !e.altKey) {
      e.preventDefault()
      focusRow(Math.max(0, index - 1))
    } else if (e.key === 'Home') {
      e.preventDefault()
      focusRow(0)
    } else if (e.key === 'End') {
      e.preventDefault()
      focusRow(rows.length - 1)
    } else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      onSelect(row.selection)
    } else if (row.element) {
      onElementKey(e, row.element.id)
    }
  }

  return (
    <Section title="Elements" icon={Layers}>
      <ul ref={listRef} role="tree" aria-label="Elements" className="flex flex-col gap-px">
        {rows.map((row, index) => {
          const selected = sameSelection(row.selection, selection)
          const Icon = row.element ? TYPE_ICONS[row.element.type] : null
          const flag = row.element ? flagged.get(row.element.id) : undefined
          const count = row.level === 1 ? regionItems(layout, row.region).length : 0
          return (
            <li
              key={row.key}
              data-row={row.key}
              role="treeitem"
              aria-level={row.level}
              aria-selected={selected}
              aria-expanded={row.level === 1 ? true : undefined}
              tabIndex={index === focusIndex ? 0 : -1}
              onClick={() => onSelect(row.selection)}
              onKeyDown={(e) => onKeyDown(e, index, row)}
              className={cn(
                'flex cursor-pointer items-center gap-2 rounded-[5px] py-[5px] pr-1.5 text-[12.5px] text-ink-2 outline-none hover:bg-surface-2',
                'focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-cyan',
                row.level === 1 ? 'pl-1.5 font-semibold text-ink' : 'pl-6',
                selected && 'bg-cyan-soft text-cyan-strong hover:bg-cyan-soft',
              )}
            >
              {Icon && <Icon className="size-[13px] flex-none" strokeWidth={1.75} aria-hidden="true" />}
              <span className="truncate">{row.label}</span>
              {flag && (
                <span
                  className={cn('size-1.5 flex-none rounded-full', flag === 'error' ? 'bg-err' : 'bg-warn')}
                  aria-label={flag === 'error' ? 'has errors' : 'has warnings'}
                />
              )}
              <span className="ml-auto font-mono text-[11px] text-ink-3">
                {row.level === 1 ? count : row.element && 'yMm' in row.element ? `y ${row.element.yMm}` : ''}
              </span>
            </li>
          )
        })}
      </ul>
    </Section>
  )
}
