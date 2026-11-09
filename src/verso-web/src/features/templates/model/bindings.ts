/** Finding and checking the data bindings an element uses. Token grammar mirrors Verso.Rendering.Binding.TextTemplate. */
import { FILTERS, type Filter, type LayoutElement } from './layout'
import { findField, isScalar, type SchemaField } from './schema'

const DATA_PATH = /^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/
const ANY_TOKEN = /\{\{([\s\S]*?)\}\}/g
const VALID_TOKEN = /^\s*([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*(?:\|\s*([a-z]+)\s*)?$/

export function isValidPath(path: string): boolean {
  return path.length <= 200 && DATA_PATH.test(path)
}

export type TextPart =
  | { kind: 'literal'; text: string }
  | { kind: 'token'; path: string; filter?: Filter; raw: string }
  | { kind: 'malformed'; raw: string }

/** Splits text like `Invoice {{invoice.number}}` into literal and token parts. */
export function parseText(text: string): TextPart[] {
  const parts: TextPart[] = []
  let last = 0
  for (const m of text.matchAll(ANY_TOKEN)) {
    const index = m.index ?? 0
    if (index > last) parts.push({ kind: 'literal', text: text.slice(last, index) })
    const valid = VALID_TOKEN.exec(m[1] ?? '')
    const filter = valid?.[2]
    if (valid && (filter === undefined || (FILTERS as readonly string[]).includes(filter))) {
      parts.push({ kind: 'token', path: valid[1]!, filter: filter as Filter | undefined, raw: m[0] })
    } else {
      parts.push({ kind: 'malformed', raw: m[0] })
    }
    last = index + m[0].length
  }
  if (last < text.length) parts.push({ kind: 'literal', text: text.slice(last) })
  return parts
}

export interface UsedBinding {
  /** Full data path from the root (table columns: `lines.description`). */
  path: string
  /** Where it's used, for messages: "binding", "text", "column 2". */
  where: string
  /** What kind of value the binding needs. */
  expects: 'scalar' | 'array'
  /** For table columns: the array path and the row-relative path. */
  column?: { arrayPath: string; relative: string; index: number }
}

/** Every data path an element refers to. Empty bindings are skipped (validation reports them as required). */
export function bindingsOf(element: LayoutElement): UsedBinding[] {
  switch (element.type) {
    case 'field':
      return element.props.binding ? [{ path: element.props.binding, where: 'binding', expects: 'scalar' }] : []
    case 'text':
      return parseText(element.props.text ?? '')
        .filter((p): p is Extract<TextPart, { kind: 'token' }> => p.kind === 'token')
        .map((p) => ({ path: p.path, where: 'text', expects: 'scalar' as const }))
    case 'table': {
      const used: UsedBinding[] = []
      const arrayPath = element.props.binding
      if (arrayPath) used.push({ path: arrayPath, where: 'binding', expects: 'array' })
      ;(element.props.columns ?? []).forEach((c, index) => {
        if (c.binding && arrayPath)
          used.push({
            path: `${arrayPath}.${c.binding}`,
            where: `column ${index + 1}`,
            expects: 'scalar',
            column: { arrayPath, relative: c.binding, index },
          })
      })
      return used
    }
    default:
      return []
  }
}

/** Whether a binding resolves against the data schema. */
export function isMapped(fields: SchemaField[], binding: UsedBinding): boolean {
  if (binding.column) {
    const array = findField(fields, binding.column.arrayPath)
    if (!array || array.kind !== 'array') return false
    const f = findField(array.children, binding.column.relative)
    return !!f && isScalar(f)
  }
  const f = findField(fields, binding.path)
  if (!f) return false
  return binding.expects === 'array' ? f.kind === 'array' : isScalar(f)
}

/** All paths (absolute; columns as `array.field`) that some element binds, for the "mapped" ticks in the data tree. */
export function usedPaths(elements: LayoutElement[]): Set<string> {
  const set = new Set<string>()
  for (const e of elements) for (const b of bindingsOf(e)) set.add(b.path)
  return set
}
