import { Download, Loader2, Upload } from 'lucide-react'
import { useEffect, useId, useMemo, useRef, useState } from 'react'

import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Field, Input, Select, Switch } from '@/components/ui/form'
import { ApiError } from '@/lib/api/client'
import { configurationApi } from '@/lib/api/configuration'
import { documentCategoriesApi } from '@/lib/api/documentCategories'
import type { ConfigurationImportResult, ConfigurationPackage, DocumentCategoryDto } from '@/lib/api/generated'
import { templatesApi } from '@/lib/api/templates'
import { workflowsApi } from '@/lib/api/workflows'
import { usePermissions } from '@/app/AuthGate'

import { placeholder } from '../variables/variables'
import { applyLocal, buildFile, countConflicts, newCategories, PACKAGE_PRODUCT, PACKAGE_VERSION, readFile, type ApplyMode, type ConfigurationFile } from './package'
import { connectionsStore, ENVIRONMENTS, useConnections, useEnvironment, useVariables, variablesStore, type Environment } from './stores'

interface Item {
  /** Prefixed with its kind (`t:` template, `w:` workflow, `c:` connection, `v:` variable, `k:` document category) so one set can hold every unticked id. */
  key: string
  id: string
  label: string
  sub: string
}

const describeError = (e: unknown) => {
  if (e instanceof ApiError) {
    const fields = Object.entries(e.problem.errors ?? {}).slice(0, 4).map(([path, messages]) => `${path}: ${messages[0]}`)
    return [e.message, ...fields].join(' ')
  }
  return e instanceof Error ? e.message : 'Something went wrong.'
}

const plural = (n: number, word: string, many = `${word}s`) => `${n} ${n === 1 ? word : many}`

function download(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }))
  const a = document.createElement('a')
  a.href = url
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 4000)
}

/** Templates and workflows the user can export: real records from the API. */
function useCatalog() {
  const [state, setState] = useState<{ templates: Item[]; workflows: Item[]; loading: boolean; error: string | null }>({ templates: [], workflows: [], loading: true, error: null })
  useEffect(() => {
    const ctl = new AbortController()
    Promise.all([templatesApi.list(1, 100, ctl.signal), workflowsApi.list(1, 100, ctl.signal), workflowsApi.list(1, 100, ctl.signal, true)])
      .then(([templates, active, inactive]) =>
        setState({
          loading: false,
          error: null,
          templates: templates.items.map((t) => ({
            key: `t:${t.id}`,
            id: t.id,
            label: t.name,
            sub: `${t.key} · ${t.latestPublishedVersion ? `version ${t.latestPublishedVersion}` : 'draft only'}`,
          })),
          workflows: [...active.items, ...inactive.items].map((w) => ({
            key: `w:${w.id}`,
            id: w.id,
            label: w.name,
            sub: `${w.key} · ${w.latestPublishedVersion ? `version ${w.latestPublishedVersion}` : 'draft only'}${w.isActive ? '' : ' · deactivated'}`,
          })),
        }),
      )
      .catch((e) => !ctl.signal.aborted && setState((s) => ({ ...s, loading: false, error: describeError(e) })))
    return () => ctl.abort()
  }, [])
  return state
}

/** Document categories, active and inactive: real records from the API. */
function useCategories(reload: number) {
  const [categories, setCategories] = useState<DocumentCategoryDto[]>([])
  useEffect(() => {
    const ctl = new AbortController()
    documentCategoriesApi
      .list(true, ctl.signal)
      .then(setCategories)
      .catch(() => undefined) // the group just stays empty
    return () => ctl.abort()
  }, [reload])
  return categories
}

function Group({
  title,
  items,
  unticked,
  setUnticked,
  query,
  empty,
}: {
  title: string
  items: Item[]
  unticked: Set<string>
  setUnticked: (next: Set<string>) => void
  query: string
  empty?: string
}) {
  const visible = items.filter((i) => !query || `${i.label} ${i.sub}`.toLowerCase().includes(query))
  const selected = items.filter((i) => !unticked.has(i.key)).length
  const groupBox = useRef<HTMLInputElement>(null)
  useEffect(() => {
    if (groupBox.current) groupBox.current.indeterminate = selected > 0 && selected < items.length
  }, [selected, items.length])
  if (query && visible.length === 0) return null

  const toggle = (keys: string[], on: boolean) => {
    const next = new Set(unticked)
    keys.forEach((k) => (on ? next.delete(k) : next.add(k)))
    setUnticked(next)
  }
  return (
    <details open className="rounded-[10px] border border-line bg-surface">
      <summary className="flex cursor-pointer items-center gap-2 px-3.5 py-2.5 text-[13.5px] font-semibold">
        {title}
        <span className="font-normal text-ink-3">
          {selected} / {items.length}
        </span>
      </summary>
      <div className="border-t border-line px-3.5 py-2">
        {items.length === 0 ? (
          <p className="m-0 text-[13px] text-ink-3">{empty ?? 'Nothing here yet.'}</p>
        ) : (
          <>
            <label className="mb-1 flex items-center gap-2 text-[12.5px] text-ink-2">
              <input
                ref={groupBox}
                type="checkbox"
                aria-label={`Select all ${title.toLowerCase()}`}
                checked={selected === items.length}
                onChange={(e) => toggle(items.map((i) => i.key), e.target.checked)}
              />
              Select group
            </label>
            {visible.map((i) => (
              <label key={i.key} className="flex items-start gap-2 py-1 text-[13px]">
                <input type="checkbox" className="mt-0.5" checked={!unticked.has(i.key)} onChange={(e) => toggle([i.key], e.target.checked)} />
                <span>
                  {i.label}
                  <small className="block text-xs text-ink-3">{i.sub}</small>
                </span>
              </label>
            ))}
          </>
        )}
      </div>
    </details>
  )
}

const OUTCOME_TONE: Record<string, 'ok' | 'neutral' | 'warn'> = { created: 'ok', updated: 'ok', unchanged: 'neutral', skipped: 'warn' }

/** Export / import tab of the Configuration drawer (design reference). */
export function TransferPanel() {
  const permissions = usePermissions()
  const [connections, setConnections] = useConnections()
  const [variables, setVariables] = useVariables()
  const [environment, setEnvironment] = useEnvironment()
  const catalog = useCatalog()
  const [categoriesVersion, setCategoriesVersion] = useState(0)
  const categories = useCategories(categoriesVersion)
  const [unticked, setUnticked] = useState<Set<string>>(new Set())
  const [query, setQuery] = useState('')
  const [exporting, setExporting] = useState(false)
  const [exportNotice, setExportNotice] = useState<{ tone: 'ok' | 'err'; text: string } | null>(null)
  const searchId = useId()

  const connectionItems: Item[] = useMemo(() => connections.map((c) => ({ key: `c:${c.id}`, id: c.id, label: c.name, sub: c.category || c.group })), [connections])
  const variableItems: Item[] = useMemo(() => variables.map((v) => ({ key: `v:${v.id}`, id: v.id, label: placeholder(v.name), sub: v.type })), [variables])
  const categoryItems: Item[] = useMemo(() => categories.map((c) => ({ key: `k:${c.id}`, id: c.id, label: c.name, sub: c.isActive ? 'Active' : 'Inactive' })), [categories])
  const all = [...categoryItems, ...connectionItems, ...variableItems, ...catalog.templates, ...catalog.workflows]
  const chosen = (items: Item[]) => items.filter((i) => !unticked.has(i.key))
  const total = chosen(all).length
  const q = query.trim().toLowerCase()

  const setAll = (on: boolean) => setUnticked(on ? new Set() : new Set(all.map((i) => i.key)))

  const exportPackage = async () => {
    setExporting(true)
    setExportNotice(null)
    try {
      const templateIds = chosen(catalog.templates).map((i) => i.id)
      const workflowIds = chosen(catalog.workflows).map((i) => i.id)
      const server: ConfigurationPackage =
        templateIds.length || workflowIds.length
          ? await configurationApi.export({ templateIds, workflowIds, environment })
          : { product: PACKAGE_PRODUCT, schemaVersion: PACKAGE_VERSION, environment, exportedAt: new Date().toISOString(), templates: [], workflows: [] }
      const picked = new Set(chosen([...connectionItems, ...variableItems]).map((i) => i.id))
      const file = buildFile(
        server,
        connections.filter((c) => picked.has(c.id)),
        variables.filter((v) => picked.has(v.id)),
        categories.filter((c) => !unticked.has(`k:${c.id}`)),
      )
      download(`verso-config-${environment.toLowerCase()}-${new Date().toISOString().slice(0, 10)}.json`, JSON.stringify(file, null, 2))
      setExportNotice({
        tone: 'ok',
        text: `Exported ${plural(file.categories.length, 'category', 'categories')}, ${plural(file.connections.length, 'connection')}, ${plural(file.variables.length, 'variable')}, ${plural(file.templates?.length ?? 0, 'template')} and ${plural(file.workflows?.length ?? 0, 'workflow')}. Secrets are never included.`,
      })
    } catch (e) {
      setExportNotice({ tone: 'err', text: describeError(e) })
    } finally {
      setExporting(false)
    }
  }

  return (
    <div className="flex flex-col gap-5 px-5 py-4">
      <Field label="Current environment" htmlFor="cfg-env" hint="Label written into every export, so you can see where a package came from.">
        <Select id="cfg-env" value={environment} onChange={(e) => setEnvironment(e.target.value as Environment)}>
          {ENVIRONMENTS.map((e) => (
            <option key={e}>{e}</option>
          ))}
        </Select>
      </Field>

      <section aria-labelledby="cfg-export" className="flex flex-col gap-3">
        <h3 id="cfg-export" className="m-0 text-[14.5px] font-semibold">
          Export
        </h3>
        <p className="m-0 text-[13px] text-ink-3">Pick everything, or untick what you don’t want to move. Secrets are never included — the target environment sets its own.</p>
        <p className="m-0 rounded-[7px] border border-line bg-surface-2 px-3 py-2 text-[12.5px] text-ink-2">
          Templates export their latest published version (their draft when nothing is published). Workflows export the same way, with each template named by key.
        </p>
        <div>
          <label htmlFor={searchId} className="sr-only">
            Search export items
          </label>
          <Input id={searchId} type="search" placeholder="Search export items" value={query} onChange={(e) => setQuery(e.target.value)} />
        </div>
        <div className="flex items-center gap-3 text-[13px]">
          <Button type="button" size="sm" onClick={() => setAll(true)}>
            Select everything
          </Button>
          <Button type="button" size="sm" onClick={() => setAll(false)}>
            Select nothing
          </Button>
          <span aria-live="polite" className="text-ink-3">
            {total} of {all.length} items selected · JSON package
          </span>
        </div>
        {catalog.loading && (
          <p className="m-0 flex items-center gap-2 text-[13px] text-ink-3">
            <Loader2 className="size-4 animate-spin" aria-hidden="true" /> Loading templates and workflows…
          </p>
        )}
        {catalog.error && (
          <p role="alert" className="m-0 text-[13px] text-err">
            Templates and workflows could not be loaded: {catalog.error}
          </p>
        )}
        <Group title="Document categories" items={categoryItems} unticked={unticked} setUnticked={setUnticked} query={q} empty="No document categories yet." />
        <Group title="Connections" items={connectionItems} unticked={unticked} setUnticked={setUnticked} query={q} />
        <Group title="Global variables" items={variableItems} unticked={unticked} setUnticked={setUnticked} query={q} />
        <Group title="Templates" items={catalog.templates} unticked={unticked} setUnticked={setUnticked} query={q} empty="No templates yet." />
        <Group title="Workflows" items={catalog.workflows} unticked={unticked} setUnticked={setUnticked} query={q} empty="No workflows yet." />
        <div role="status" aria-label="Export result" className="min-h-5 text-[13px]">
          {exportNotice && <span className={exportNotice.tone === 'ok' ? 'text-ink-2' : 'text-err'}>{exportNotice.text}</span>}
        </div>
        <Button type="button" variant="primary" disabled={total === 0 || exporting} onClick={() => void exportPackage()}>
          {exporting ? <Loader2 className="animate-spin" /> : <Download />}
          Export {total} {total === 1 ? 'item' : 'items'}
        </Button>
      </section>

      {permissions.administer ? (
        <ImportSection
          connections={connections}
          variables={variables}
          categories={categories}
          onCategoriesChanged={() => setCategoriesVersion((v) => v + 1)}
          onApplied={(next) => {
            setConnections(next.connections)
            setVariables(next.variables)
          }}
        />
      ) : (
        <section aria-labelledby="cfg-import" className="flex flex-col gap-2">
          <h3 id="cfg-import" className="m-0 text-[14.5px] font-semibold">
            Import
          </h3>
          <p className="m-0 text-[13px] text-ink-3">Importing a package writes templates and workflows, so it is limited to administrators.</p>
        </section>
      )}
    </div>
  )
}

function ImportSection({
  connections,
  variables,
  categories,
  onCategoriesChanged,
  onApplied,
}: {
  connections: ReturnType<typeof connectionsStore.get>
  variables: ReturnType<typeof variablesStore.get>
  categories: DocumentCategoryDto[]
  onCategoriesChanged: () => void
  onApplied: (next: ReturnType<typeof applyLocal>) => void
}) {
  const [parsed, setParsed] = useState<{ file: ConfigurationFile; skipped: { connections: number; variables: number; categories: number } } | null>(null)
  const [mode, setMode] = useState<ApplyMode>('merge')
  const [includeWorkflows, setIncludeWorkflows] = useState(true)
  const [preview, setPreview] = useState<ConfigurationImportResult | null>(null)
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<{ tone: 'ok' | 'err'; text: string; warnings?: string[] } | null>(null)

  const serverPart = (file: ConfigurationFile): ConfigurationPackage => ({
    product: file.product,
    schemaVersion: file.schemaVersion,
    environment: file.environment,
    exportedAt: file.exportedAt,
    templates: file.templates,
    workflows: file.workflows,
  })
  const hasServerContent = (file: ConfigurationFile) => (file.templates?.length ?? 0) + (file.workflows?.length ?? 0) > 0

  // What the server would do is checked (dry run) whenever the file or the workflow switch changes.
  useEffect(() => {
    setPreview(null)
    if (!parsed || !hasServerContent(parsed.file)) return
    let current = true
    configurationApi
      .import({ package: serverPart(parsed.file), includeWorkflows }, true)
      .then((r) => current && setPreview(r))
      .catch((e) => {
        if (!current) return
        setParsed(null)
        setResult({ tone: 'err', text: describeError(e) })
      })
    return () => {
      current = false
    }
  }, [parsed, includeWorkflows])

  const onFile = async (file: File) => {
    setResult(null)
    setParsed(null)
    const read = readFile(await file.text())
    if ('error' in read) {
      setResult({ tone: 'err', text: read.error })
      return
    }
    setParsed(read)
  }

  const apply = async () => {
    if (!parsed) return
    setBusy(true)
    setResult(null)
    try {
      let imported: ConfigurationImportResult | null = null
      if (hasServerContent(parsed.file)) imported = await configurationApi.import({ package: serverPart(parsed.file), includeWorkflows }, false)
      onApplied(applyLocal(parsed.file, mode, connections, variables))
      // Categories: only the names this environment lacks are created (never changed or removed); an inactive one is created, then deactivated.
      let createdCategories = 0
      for (const c of newCategories(parsed.file, categories)) {
        try {
          const made = await documentCategoriesApi.create(c.name)
          if (!c.isActive) await documentCategoriesApi.deactivate(made.id)
          createdCategories++
        } catch (e) {
          if (!(e instanceof ApiError && e.status === 409)) throw e // 409 = the name appeared meanwhile: nothing to do
        }
      }
      if (parsed.file.categories.length > 0) onCategoriesChanged()
      const count = (kind: string) => imported?.items.filter((i) => i.kind === kind && (i.outcome === 'created' || i.outcome === 'updated')).length ?? 0
      const skipped = imported?.items.filter((i) => i.outcome === 'skipped').length ?? 0
      setResult({
        tone: 'ok',
        text: `Imported ${plural(createdCategories, 'new category', 'new categories')}, ${plural(parsed.file.connections.length, 'connection')}, ${plural(parsed.file.variables.length, 'variable')}, ${plural(count('template'), 'template')} and ${plural(count('workflow'), 'workflow')}${skipped ? ` (${skipped} skipped)` : ''}.`,
        warnings: imported?.warnings,
      })
      setParsed(null)
    } catch (e) {
      setResult({ tone: 'err', text: describeError(e) })
    } finally {
      setBusy(false)
    }
  }

  const conflicts = parsed ? countConflicts(parsed.file, connections, variables) : null
  const dropped = parsed ? parsed.skipped.connections + parsed.skipped.variables + parsed.skipped.categories : 0
  return (
    <section aria-labelledby="cfg-import" className="flex flex-col gap-3">
      <h3 id="cfg-import" className="m-0 text-[14.5px] font-semibold">
        Import
      </h3>
      <p className="m-0 text-[13px] text-ink-3">Upload a package from another environment. You will see a summary before anything changes.</p>
      <Input
        type="file"
        accept=".json,application/json"
        aria-label="Import configuration file"
        className="py-[7px]"
        onChange={(e) => {
          const file = e.target.files?.[0]
          e.target.value = ''
          if (file) void onFile(file)
        }}
      />
      <Field label="How to apply" htmlFor="cfg-mode" hint="Applies to connections and variables. Categories are only added, never changed or removed. Templates and workflows are matched by key and never removed.">
        <Select id="cfg-mode" value={mode} onChange={(e) => setMode(e.target.value as ApplyMode)}>
          <option value="merge">Merge — add new items, update items with the same name</option>
          <option value="replace">Replace — make this environment’s connections and variables match the package</option>
        </Select>
      </Field>
      <Switch checked={includeWorkflows} onCheckedChange={setIncludeWorkflows} label="Also import the package’s workflows (as drafts)" />

      {parsed && conflicts && (
        <dl className="m-0 grid grid-cols-[110px_1fr] gap-x-3 gap-y-1.5 rounded-[10px] border border-line bg-surface p-3.5 text-[13px]">
          <dt className="text-ink-3">Package</dt>
          <dd className="m-0">
            from <b>{parsed.file.environment ?? '?'}</b>
            {parsed.file.exportedAt && <>, exported {parsed.file.exportedAt.slice(0, 16).replace('T', ' ')}</>}
          </dd>
          <dt className="text-ink-3">Contents</dt>
          <dd className="m-0">
            {plural(parsed.file.categories.length, 'category', 'categories')}, {plural(parsed.file.connections.length, 'connection')}, {plural(parsed.file.variables.length, 'variable')}, {plural(parsed.file.templates?.length ?? 0, 'template')},{' '}
            {plural(parsed.file.workflows?.length ?? 0, 'workflow')}
            {dropped > 0 && <span className="text-warn"> · {dropped} invalid entries ignored</span>}
          </dd>
          <dt className="text-ink-3">Already here</dt>
          <dd className="m-0">
            {plural(conflicts.connections, 'connection')} and {plural(conflicts.variables, 'variable')} with the same name; {plural(parsed.file.categories.length - newCategories(parsed.file, categories).length, 'category', 'categories')} already exist and are kept as they are
          </dd>
          {preview && (
            <>
              <dt className="text-ink-3">On the server</dt>
              <dd className="m-0">
                <ul className="m-0 flex list-none flex-col gap-1.5 p-0">
                  {preview.items.map((i) => (
                    <li key={`${i.kind}:${i.key}`}>
                      <StatusPill tone={OUTCOME_TONE[i.outcome] ?? 'neutral'}>{i.outcome}</StatusPill> {i.kind} <b className="font-mono">{i.key}</b>
                      {i.message && <span className="block text-xs text-ink-3">{i.message}</span>}
                    </li>
                  ))}
                </ul>
                {preview.warnings.map((w) => (
                  <p key={w} className="m-0 mt-1.5 text-xs text-warn">
                    {w}
                  </p>
                ))}
              </dd>
            </>
          )}
        </dl>
      )}
      {parsed && hasServerContent(parsed.file) && !preview && !result && (
        <p className="m-0 flex items-center gap-2 text-[13px] text-ink-3">
          <Loader2 className="size-4 animate-spin" aria-hidden="true" /> Checking what the server would change…
        </p>
      )}
      {parsed && (
        <Button type="button" variant="primary" disabled={busy || (hasServerContent(parsed.file) && !preview)} onClick={() => void apply()}>
          {busy ? <Loader2 className="animate-spin" /> : <Upload />}
          Apply package
        </Button>
      )}
      <div role="status" aria-label="Import result" className="text-[13px]">
        {result && <span className={result.tone === 'ok' ? 'text-ink-2' : 'text-err'}>{result.text}</span>}
        {result?.warnings?.map((w) => (
          <p key={w} className="m-0 mt-1 text-xs text-warn">
            {w}
          </p>
        ))}
      </div>
    </section>
  )
}
