/**
 * The template's data schema (a JSON Schema object) as a field tree for the data model panel and
 * the binding picker. Only the subset the designer needs is read: type, properties, items, format.
 */

export type FieldKind = 'string' | 'date' | 'number' | 'integer' | 'boolean' | 'object' | 'array' | 'unknown'

export interface SchemaField {
  /** Full dotted path from the data root, e.g. `customer.name`. For array items: relative path inside the item. */
  path: string
  name: string
  kind: FieldKind
  children: SchemaField[]
}

interface JsonSchema {
  type?: string | string[]
  format?: string
  properties?: Record<string, JsonSchema>
  items?: JsonSchema
}

function kindOf(schema: JsonSchema): FieldKind {
  const types = Array.isArray(schema.type) ? schema.type.filter((t) => t !== 'null') : schema.type ? [schema.type] : []
  const t = types[0] ?? (schema.properties ? 'object' : schema.items ? 'array' : undefined)
  switch (t) {
    case 'string':
      return schema.format === 'date' || schema.format === 'date-time' ? 'date' : 'string'
    case 'number':
      return 'number'
    case 'integer':
      return 'integer'
    case 'boolean':
      return 'boolean'
    case 'object':
      return 'object'
    case 'array':
      return 'array'
    default:
      return 'unknown'
  }
}

function fieldsOf(schema: JsonSchema | undefined, prefix: string): SchemaField[] {
  if (!schema || typeof schema !== 'object') return []
  const props = schema.properties
  if (!props || typeof props !== 'object') return []
  return Object.entries(props).map(([name, child]) => {
    const path = prefix ? `${prefix}.${name}` : name
    const kind = kindOf(child ?? {})
    let children: SchemaField[] = []
    if (kind === 'object') children = fieldsOf(child, path)
    // Array item fields are relative to the row, as table column bindings are.
    if (kind === 'array') children = fieldsOf(child.items, '')
    return { path, name, kind, children }
  })
}

export function schemaFields(dataSchema: unknown): SchemaField[] {
  return fieldsOf(dataSchema as JsonSchema, '')
}

export const isScalar = (f: SchemaField) => f.kind !== 'object' && f.kind !== 'array'

/** Every scalar path reachable without crossing an array (what fields and text tokens can bind to). */
export function scalarPaths(fields: SchemaField[]): SchemaField[] {
  const out: SchemaField[] = []
  const walk = (list: SchemaField[]) => {
    for (const f of list) {
      if (isScalar(f)) out.push(f)
      else if (f.kind === 'object') walk(f.children)
    }
  }
  walk(fields)
  return out
}

/** Every array path (what a repeating table can bind to). */
export function arrayPaths(fields: SchemaField[]): SchemaField[] {
  const out: SchemaField[] = []
  const walk = (list: SchemaField[]) => {
    for (const f of list) {
      if (f.kind === 'array') out.push(f)
      else if (f.kind === 'object') walk(f.children)
    }
  }
  walk(fields)
  return out
}

export function findField(fields: SchemaField[], path: string): SchemaField | undefined {
  const parts = path.split('.')
  let list = fields
  let found: SchemaField | undefined
  for (let i = 0; i < parts.length; i++) {
    found = list.find((f) => f.name === parts[i])
    if (!found) return undefined
    if (i < parts.length - 1) {
      if (found.kind !== 'object') return undefined
      list = found.children
    }
  }
  return found
}

/** Scalar fields of each row of the array at `arrayPath` (paths are relative to the row). */
export function rowFields(fields: SchemaField[], arrayPath: string): SchemaField[] {
  const array = findField(fields, arrayPath)
  if (!array || array.kind !== 'array') return []
  return scalarPaths(array.children)
}

const DATE_RE = /^\d{4}-\d{2}-\d{2}(T[\d:.]+(Z|[+-]\d{2}:\d{2})?)?$/

/** Infers a JSON Schema from sample data, so an author can import synthetic data instead of writing a schema. */
export function inferSchema(value: unknown): Record<string, unknown> {
  if (value === null || value === undefined) return {}
  if (Array.isArray(value)) {
    const firstObject = value.find((v) => v !== null && v !== undefined)
    return firstObject === undefined ? { type: 'array' } : { type: 'array', items: inferArrayItems(value) }
  }
  switch (typeof value) {
    case 'string':
      return DATE_RE.test(value) ? { type: 'string', format: value.length === 10 ? 'date' : 'date-time' } : { type: 'string' }
    case 'number':
      return { type: Number.isInteger(value) ? 'integer' : 'number' }
    case 'boolean':
      return { type: 'boolean' }
    case 'object': {
      const properties: Record<string, unknown> = {}
      for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
        if (k.startsWith('_')) continue // e.g. "_comment" in the synthetic fixtures
        properties[k] = inferSchema(v)
      }
      return { type: 'object', properties }
    }
    default:
      return {}
  }
}

/** Merges the rows of an array so a number that is an integer in one row and decimal in another becomes `number`. */
function inferArrayItems(rows: unknown[]): Record<string, unknown> {
  const objects = rows.filter((r): r is Record<string, unknown> => !!r && typeof r === 'object' && !Array.isArray(r))
  if (objects.length === 0) return inferSchema(rows.find((r) => r !== null))
  const properties: Record<string, Record<string, unknown>> = {}
  for (const row of objects) {
    for (const [k, v] of Object.entries(row)) {
      if (k.startsWith('_')) continue
      const s = inferSchema(v)
      const prev = properties[k]
      if (!prev || Object.keys(prev).length === 0) properties[k] = s
      else if (prev.type === 'integer' && s.type === 'number') properties[k] = s
    }
  }
  return { type: 'object', properties }
}

/** True when the schema describes no fields at all (e.g. a new template's `{ "type": "object" }`). */
export function isEmptySchema(dataSchema: unknown): boolean {
  return schemaFields(dataSchema).length === 0
}
