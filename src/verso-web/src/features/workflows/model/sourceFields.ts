/**
 * The fields the run's data has when it reaches a step, as far as the designer can tell without running anything: the trigger's
 * sample payload, then what the steps in front of it add or remove. Used to suggest fields in If / else and Switch, so they offer
 * what the data really holds at that point rather than every field of the template.
 *
 *  - API trigger: the paths of its sample payload (the other triggers have no sample, so nothing is known).
 *  - Parse: the data is replaced by the parsed content, which is unknown here, so everything known so far is dropped.
 *  - Map fields: adds each row's destination.
 *  - Transform: adds each rule's target; Remove deletes it (and what is under it).
 *  - Merge data: adds the fields of the lookup table's records (under "Write matches into" when set), unless it enriches a list.
 *  - For each: steps inside its loop also have the current item. Mode "each": the item's fields as <item name>.<field> (the item
 *    itself when it is a plain value). Mode "split": the item becomes the record, so its fields replace everything else.
 *    Only known when the list is in the trigger's sample payload. The loop-back connection is ignored here.
 *  - Other steps pass the data on unchanged.
 * Where paths from several steps meet, the union is offered.
 */
import { arrayPaths, inferSchema, scalarPaths, schemaFields } from '@/features/templates/model/schema'

import type { DefNode, WorkflowDefinition } from './definition'
import { backEdges, FOR_EACH } from './loops'
import { parseJsonObject } from './payloadSchema'

const text = (v: unknown) => (typeof v === 'string' ? v.trim() : '')
const list = (v: unknown): Record<string, unknown>[] => (Array.isArray(v) ? v.filter((x): x is Record<string, unknown> => typeof x === 'object' && x !== null) : [])
const pathsOf = (value: unknown) => scalarPaths(schemaFields(inferSchema(value))).map((f) => f.path)
const under = (prefix: string, path: string) => path === prefix || path.startsWith(`${prefix}.`)

function lookupPaths(config: Record<string, unknown>): string[] {
  if (text(config.source)) return [] // each entry of a list is enriched: those paths aren't addressable
  let rows: unknown
  try {
    rows = JSON.parse(text(config.lookup) || '[]')
  } catch {
    return []
  }
  const into = text(config.into)
  const paths = new Set<string>()
  for (const row of list(rows)) for (const p of pathsOf(row)) paths.add(into ? `${into}.${p}` : p)
  return [...paths]
}

function apply(node: DefNode, incoming: Set<string>): Set<string> {
  const c = node.config
  const out = new Set(incoming)
  switch (node.type) {
    case 'api-trigger': {
      const sample = parseJsonObject(text(c.samplePayload))
      if (sample) for (const p of pathsOf(sample)) out.add(p)
      break
    }
    case 'parse':
      out.clear()
      break
    case 'map-fields':
      for (const row of list(c.rows)) if (text(row.to)) out.add(text(row.to))
      break
    case 'transform':
      for (const rule of list(c.rules)) {
        const target = text(rule.target)
        if (!target) continue
        if (rule.operation === 'remove') {
          for (const p of [...out]) if (under(target, p)) out.delete(p)
        } else {
          out.add(target)
        }
      }
      break
    case 'merge-data':
      for (const p of lookupPaths(c)) out.add(p)
      break
  }
  return out
}

/** The trigger's sample payload as an object, if it has one. */
export function samplePayload(def: WorkflowDefinition): Record<string, unknown> | undefined {
  const sample = def.nodes.find((n) => n.type === 'api-trigger')?.config.samplePayload
  return typeof sample === 'string' ? parseJsonObject(sample.trim()) : undefined
}

/** Paths of the lists in the trigger's sample payload (what a For each can go through). */
export function sampleLists(def: WorkflowDefinition): string[] {
  const sample = samplePayload(def)
  return sample ? arrayPaths(schemaFields(inferSchema(sample))).map((f) => f.path) : []
}

/** The value at a dotted path of the sample payload. */
export function sampleValue(def: WorkflowDefinition, path: string): unknown {
  let cur: unknown = samplePayload(def)
  for (const key of path.split('.')) {
    if (typeof cur !== 'object' || cur === null || Array.isArray(cur)) return undefined
    cur = (cur as Record<string, unknown>)[key]
  }
  return cur
}

const ITEM_NAME = /^[A-Za-z_][A-Za-z0-9_]{0,39}$/

/** What a For each hands to the steps of its loop: its item's fields, added to the record (each) or replacing it (split). */
function loopArrival(def: WorkflowDefinition, loop: DefNode, record: Set<string>): Set<string> {
  const name = text(loop.config.item) || 'item'
  const list = sampleValue(def, text(loop.config.field))
  if (!ITEM_NAME.test(name) || !Array.isArray(list)) return record
  const first = list.find((x) => x !== null && x !== undefined)
  const itemPaths =
    typeof first === 'object' && first !== null && !Array.isArray(first)
      ? scalarPaths(schemaFields(inferSchema(list).items)).map((f) => f.path)
      : first === undefined
        ? []
        : ['']
  const split = loop.config.mode === 'split'
  if (split) return new Set(itemPaths.map((p) => p || name)) // a plain value becomes { <item name>: value }
  const out = new Set(record)
  for (const p of itemPaths) out.add(p ? `${name}.${p}` : name)
  return out
}

/** The fields the data has on arrival at <paramref name="nodeId"/>, sorted. Empty when nothing is known. */
export function sourceFields(def: WorkflowDefinition, nodeId: string): string[] {
  const nodes = new Map(def.nodes.map((n) => [n.id, n]))
  const memo = new Map<string, Set<string>>()
  const visiting = new Set<string>() // a draft may hold a loop it isn't allowed to; it is reported when validated
  const loopBacks = new Set(backEdges(def).map((e) => e.id)) // the repeat connection brings nothing new in

  const arrival = (id: string): Set<string> => {
    const merged = new Set<string>()
    for (const e of def.edges) {
      if (e.target !== id || !nodes.has(e.source) || loopBacks.has(e.id)) continue
      const source = nodes.get(e.source)!
      const sent = source.type === FOR_EACH && e.sourcePort === 'each' ? loopArrival(def, source, departure(e.source)) : departure(e.source)
      for (const p of sent) merged.add(p)
    }
    return merged
  }
  const departure = (id: string): Set<string> => {
    const known = memo.get(id)
    if (known) return known
    if (visiting.has(id)) return new Set()
    visiting.add(id)
    const out = apply(nodes.get(id)!, arrival(id))
    visiting.delete(id)
    memo.set(id, out)
    return out
  }

  return nodes.has(nodeId) ? [...arrival(nodeId)].sort() : []
}
