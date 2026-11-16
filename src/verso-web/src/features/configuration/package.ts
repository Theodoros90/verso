import type { ConfigurationPackage, DocumentCategoryDto } from '@/lib/api/generated'

import { readConnections, toExported, type Connection, type ExportedConnection } from '../connections/connections'
import { validateVariable, VARIABLE_ENVIRONMENTS, VARIABLE_SCOPES, VARIABLE_TYPES, type GlobalVariable } from '../variables/variables'
import { newLocalId } from './stores'

// The configuration file is one JSON document. The server owns `templates` and `workflows` (real records, see
// docs/api.md#configuration); `connections` and `variables` are frontend-only and travel in the same file. Secrets are
// never part of it: connections have no secret field and a sensitive variable is exported without its value.

export const PACKAGE_PRODUCT = 'Verso configuration package'
export const PACKAGE_VERSION = 1

export type ExportedVariable = Omit<GlobalVariable, 'id'>

/** A document category travels by name (ADR 0013 addendum 2): ids are environment-local. */
export interface ExportedCategory {
  name: string
  isActive: boolean
}

const MAX_CATEGORY_NAME = 100

export interface ConfigurationFile extends ConfigurationPackage {
  connections: ExportedConnection[]
  variables: ExportedVariable[]
  /** Absent in packages made before categories were included. */
  categories: ExportedCategory[]
}

export type ApplyMode = 'merge' | 'replace'

const toExportedVariable = ({ id: _id, ...rest }: GlobalVariable): ExportedVariable => ({ ...rest, value: rest.sensitive ? '' : rest.value })

/** The server's part (templates, workflows) plus the selected connections and variables. */
export function buildFile(
  server: ConfigurationPackage,
  connections: Connection[],
  variables: GlobalVariable[],
  categories: DocumentCategoryDto[] = [],
): ConfigurationFile {
  return {
    ...server,
    connections: connections.map(toExported),
    variables: variables.map(toExportedVariable),
    categories: categories.map((c) => ({ name: c.name, isActive: c.isActive })),
  }
}

export type ReadResult = { file: ConfigurationFile; skipped: { connections: number; variables: number; categories: number } } | { error: string }

/** Parses and checks a configuration file. Invalid connections and variables are dropped and counted, never guessed. */
export function readFile(text: string): ReadResult {
  let data: unknown
  try {
    data = JSON.parse(text)
  } catch {
    return { error: 'That file is not valid JSON.' }
  }
  const raw = data as Record<string, unknown> | null
  if (!raw || typeof raw !== 'object' || raw.product !== PACKAGE_PRODUCT) return { error: 'That file is not a Verso configuration package.' }
  if (raw.schemaVersion !== PACKAGE_VERSION) return { error: `Unsupported package version ${String(raw.schemaVersion)}.` }
  for (const section of ['connections', 'variables', 'templates', 'workflows'] as const) {
    if (!Array.isArray(raw[section])) return { error: `The ${section} section is missing.` }
  }

  const conns = readConnections(raw.connections as unknown[])
  const vars = readVariables(raw.variables as unknown[])
  const cats = readCategories(Array.isArray(raw.categories) ? raw.categories : [])
  return {
    file: {
      product: PACKAGE_PRODUCT,
      schemaVersion: PACKAGE_VERSION,
      environment: typeof raw.environment === 'string' ? raw.environment : null,
      exportedAt: typeof raw.exportedAt === 'string' ? raw.exportedAt : null,
      templates: raw.templates as ConfigurationFile['templates'],
      workflows: raw.workflows as ConfigurationFile['workflows'],
      connections: conns.items,
      variables: vars.items,
      categories: cats.items,
    },
    skipped: { connections: conns.skipped, variables: vars.skipped, categories: cats.skipped },
  }
}

/** Invalid and repeated (case-insensitive) names are dropped and counted. */
function readCategories(list: unknown[]): { items: ExportedCategory[]; skipped: number } {
  const items: ExportedCategory[] = []
  let skipped = 0
  for (const raw of list as Record<string, unknown>[]) {
    const name = typeof raw?.name === 'string' ? raw.name.trim() : ''
    if (!name || name.length > MAX_CATEGORY_NAME || items.some((i) => sameName(i.name, name))) {
      skipped++
      continue
    }
    items.push({ name, isActive: raw.isActive !== false })
  }
  return { items, skipped }
}

function readVariables(list: unknown[]): { items: ExportedVariable[]; skipped: number } {
  const items: ExportedVariable[] = []
  let skipped = 0
  const str = (v: unknown) => (typeof v === 'string' ? v : '')
  for (const raw of list as Record<string, unknown>[]) {
    const type = VARIABLE_TYPES.find((t) => t === raw?.type)
    const sensitive = raw?.sensitive === true || type === 'Secret'
    const draft = {
      name: str(raw?.name).trim(),
      type: type ?? 'Text',
      scope: VARIABLE_SCOPES.find((s) => s === raw?.scope) ?? 'Tenant',
      env: VARIABLE_ENVIRONMENTS.find((e) => e === raw?.env) ?? 'All',
      value: sensitive ? '' : str(raw?.value),
      sensitive,
      description: str(raw?.description),
    }
    // Names inside the file must be unique too; the check against this environment happens when applying.
    if (!type || Object.keys(validateVariable(draft, items.map((i) => i.name))).length > 0) {
      skipped++
      continue
    }
    items.push({ ...draft, updated: 'Imported' })
  }
  return { items, skipped }
}

const sameName = (a: string, b: string) => a.toLowerCase() === b.toLowerCase()

/** How many of the file's connections and variables share a name with one already here. */
export function countConflicts(file: ConfigurationFile, connections: Connection[], variables: GlobalVariable[]) {
  return {
    connections: file.connections.filter((c) => connections.some((x) => sameName(x.name, c.name))).length,
    variables: file.variables.filter((v) => variables.some((x) => sameName(x.name, v.name))).length,
  }
}

/**
 * Applies the file's connections and variables. Merge adds new items and updates the ones with the same name (keeping
 * this environment's own id and switch state); replace makes the lists match the file. Sensitive variables keep their
 * local value-less state either way, since a package never carries a secret.
 */
export function applyLocal(file: ConfigurationFile, mode: ApplyMode, connections: Connection[], variables: GlobalVariable[]) {
  const withId = <T extends { name: string }>(local: { id: string; name: string }[], item: T) => ({
    ...item,
    id: local.find((x) => sameName(x.name, item.name))?.id ?? newLocalId(),
  })
  const nextConnections: Connection[] =
    mode === 'replace'
      ? file.connections.map((c) => withId(connections, c))
      : [
          ...connections.map((l) => {
            const incoming = file.connections.find((c) => sameName(c.name, l.name))
            return incoming ? { ...incoming, id: l.id } : l
          }),
          ...file.connections.filter((c) => !connections.some((l) => sameName(l.name, c.name))).map((c) => ({ ...c, id: newLocalId() })),
        ]
  const nextVariables: GlobalVariable[] =
    mode === 'replace'
      ? file.variables.map((v) => withId(variables, v))
      : [
          ...variables.map((l) => {
            const incoming = file.variables.find((v) => sameName(v.name, l.name))
            return incoming ? { ...incoming, id: l.id } : l
          }),
          ...file.variables.filter((v) => !variables.some((l) => sameName(l.name, v.name))).map((v) => ({ ...v, id: newLocalId() })),
        ]
  return { connections: nextConnections, variables: nextVariables }
}

/** The file's categories this environment doesn't have yet (matched by name, ignoring case). Existing ones are left as they are. */
export function newCategories(file: ConfigurationFile, existing: DocumentCategoryDto[]): ExportedCategory[] {
  return file.categories.filter((c) => !existing.some((e) => sameName(e.name, c.name)))
}
