import {
  Braces,
  CircleAlert,
  CircleCheck,
  Eye,
  FilePlus2,
  Redo2,
  Ruler,
  Save,
  Undo2,
  UploadCloud,
  ZoomIn,
  ZoomOut,
} from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react'
import { Link, useBlocker, useParams } from 'react-router'

import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Switch } from '@/components/ui/form'
import { cn } from '@/lib/utils'
import { ApiError } from '@/lib/api/client'
import type { TemplateDto, TemplateVersionDto } from '@/lib/api/generated'
import { templatesApi } from '@/lib/api/templates'

import { usedPaths } from '../model/bindings'
import { asLayout, asPageSettings, TYPE_LABELS, type ElementType, type Layout, type PageSettings, type Region } from '../model/layout'
import {
  addBand,
  addElement,
  allElements,
  canPlace,
  duplicateElement,
  findElement,
  moveBlock,
  nudge,
  removeBand,
  removeElement,
  reorderInRegion,
  updateProps,
} from '../model/ops'
import { inferSchema, isEmptySchema, rowFields, schemaFields, type SchemaField } from '../model/schema'
import { elementIdForServerPath, validateDesign, type DesignIssue } from '../model/validate'
import { Canvas, type DropTarget } from './Canvas'
import { DataJsonDialog } from './DataJsonDialog'
import { useDesignerState, type Draft } from './designerState'
import { humanize, type DragPayload } from './dnd'
import { PreviewPane } from './PreviewPane'
import { PropertiesPanel } from './PropertiesPanel'
import { DataModel, ElementTree, Palette } from './SidePanel'

interface Loaded {
  template: TemplateDto
  version: TemplateVersionDto
  etag: string | null
}

type LoadState = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; loaded: Loaded; generation: number }

async function loadTemplate(id: string, signal?: AbortSignal): Promise<Loaded> {
  const template = await templatesApi.get(id, signal)
  if (template.draftVersionId) {
    const { body, etag } = await templatesApi.draft(id, signal)
    return { template, version: body, etag }
  }
  const versions = await templatesApi.versions(id, signal)
  const latest = versions.find((v) => v.status === 'Published')
  if (!latest) throw new Error('This template has no draft and no published version.')
  const { body, etag } = await templatesApi.version(id, latest.versionId, signal)
  return { template, version: body, etag }
}

/** Route /templates/:id. Loads the draft (or the latest published version, read-only) and hosts the designer. */
export function TemplateDesignerPage() {
  const { id = '' } = useParams()
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [flash, setFlash] = useState<string | null>(null)
  const generation = useRef(0)

  const reload = useCallback(
    (message?: string) => {
      setFlash(message ?? null)
      loadTemplate(id)
        .then((loaded) => setState({ kind: 'ready', loaded, generation: ++generation.current }))
        .catch((e: unknown) => setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load the template.' }))
    },
    [id],
  )

  useEffect(() => {
    const controller = new AbortController()
    setState({ kind: 'loading' })
    loadTemplate(id, controller.signal)
      .then((loaded) => setState({ kind: 'ready', loaded, generation: ++generation.current }))
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setState({ kind: 'error', message: e instanceof ApiError && e.status === 404 ? 'Template not found.' : e instanceof Error ? e.message : 'Could not load the template.' })
      })
    return () => controller.abort()
  }, [id])

  if (state.kind === 'loading') return <p className="p-6 text-ink-3">Loading template…</p>
  if (state.kind === 'error')
    return (
      <div className="p-6">
        <p role="alert" className="mb-3 rounded-lg border border-err/30 bg-err-soft px-4 py-3 text-err">
          {state.message}
        </p>
        <Link to="/templates" className="text-cyan">
          Back to templates
        </Link>
      </div>
    )
  return <Designer key={state.generation} loaded={state.loaded} flash={flash} onReload={reload} />
}

function toDraft(v: TemplateVersionDto): Draft {
  return {
    layout: asLayout(v.layout),
    pageSettings: asPageSettings(v.pageSettings),
    dataSchema: v.dataSchema ?? { type: 'object' },
    sampleData: v.sampleData ?? {},
  }
}

type Banner = { tone: 'ok' | 'warn' | 'err'; message: string; action?: { label: string; run: () => void } } | null

const ZOOM_MIN = 0.4
const ZOOM_MAX = 1.5
const isEditableTarget = (t: EventTarget | null) =>
  t instanceof HTMLElement && (t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName))

function Designer({ loaded, flash, onReload }: { loaded: Loaded; flash: string | null; onReload: (message?: string) => void }) {
  const { template, version } = loaded
  const readOnly = version.status !== 'Draft'
  const [state, actions] = useDesignerState(useMemo(() => toDraft(version), [version]))
  const draft = state.history.present
  const dirty = draft !== state.saved
  const etag = useRef(loaded.etag)

  const [zoom, setZoom] = useState(0.8)
  const [showSample, setShowSample] = useState(false)
  const [showGrid, setShowGrid] = useState(false)
  const [previewOpen, setPreviewOpen] = useState(false)
  const [previewRevision, setPreviewRevision] = useState(0)
  const [busy, setBusy] = useState<'save' | 'publish' | 'draft' | null>(null)
  const [banner, setBanner] = useState<Banner>(flash ? { tone: 'ok', message: flash } : null)
  const [serverIssues, setServerIssues] = useState<DesignIssue[]>([])
  const [jsonOpen, setJsonOpen] = useState(false)
  const [issuesOpen, setIssuesOpen] = useState(false)
  const [savedAt, setSavedAt] = useState<Date | null>(null)
  const [announcement, setAnnouncement] = useState('')

  const fields = useMemo(() => schemaFields(draft.dataSchema), [draft.dataSchema])
  const located = useMemo(() => allElements(draft.layout), [draft.layout])
  const used = useMemo(() => usedPaths(located.map((l) => l.element)), [located])
  const designIssues = useMemo(() => validateDesign(draft.layout, draft.pageSettings, draft.dataSchema), [draft.layout, draft.pageSettings, draft.dataSchema])
  const issues = useMemo(() => [...designIssues, ...serverIssues], [designIssues, serverIssues])
  const errorCount = issues.filter((i) => i.severity === 'error').length
  // Unmapped = bound to a path the data model lacks, or no field chosen yet (the prototype's "unmapped field").
  const unmappedCount = issues.filter((i) => i.code === 'binding.unmapped' || i.code === 'binding.required').length
  const boundCount = located.filter((l) => l.element.type === 'field' || l.element.type === 'table' || (l.element.type === 'text' && /\{\{/.test(l.element.props.text))).length
  const flagged = useMemo(() => {
    const m = new Map<string, 'error' | 'warning'>()
    for (const i of issues) if (i.elementId && m.get(i.elementId) !== 'error') m.set(i.elementId, i.severity)
    return m
  }, [issues])
  const ctx = useMemo(() => ({ fields, sampleData: draft.sampleData, showSample }), [fields, draft.sampleData, showSample])

  // ---------- adding elements ----------

  const targetFor = useCallback(
    (type: ElementType): { region: Region; index?: number } => {
      const sel = state.selection
      let region: Region | undefined
      let index: number | undefined
      if (sel?.kind === 'element') {
        const found = findElement(draft.layout, sel.id)
        if (found) {
          region = found.region
          if (found.region === 'body') index = found.index + 1
        }
      } else if (sel?.kind === 'region') region = sel.region
      if (!region || !canPlace(type, region)) region = canPlace(type, 'body') ? 'body' : 'header'
      return { region, index }
    },
    [state.selection, draft.layout],
  )

  const place = useCallback(
    (build: (l: Layout, page: PageSettings) => { layout: Layout; id: string }, what: string) => {
      try {
        const result = build(draft.layout, draft.pageSettings)
        actions.edit((d) => ({ ...d, layout: result.layout }), { select: { kind: 'element', id: result.id } })
        setAnnouncement(`${what} added.`)
      } catch (e) {
        setBanner({ tone: 'warn', message: e instanceof Error ? e.message : 'Could not add the element.' })
      }
    },
    [actions, draft.layout, draft.pageSettings],
  )

  const addFieldAt = useCallback(
    (field: Pick<SchemaField, 'path' | 'name' | 'kind'>, target: { region: Region; index?: number; at?: { xMm: number; yMm: number } }) => {
      if (field.kind === 'array') {
        if (target.region !== 'body') {
          setBanner({ tone: 'warn', message: 'Repeating tables go in the body.' })
          return
        }
        place((l, page) => {
          const r = addElement(l, page, 'body', 'table', { binding: field.path, index: target.index })
          const columns = rowFields(fields, field.path)
            .slice(0, 8)
            .map((f) => ({
              header: humanize(f.name),
              binding: f.path,
              ...(f.kind === 'date' ? { filter: 'date' as const } : {}),
              ...(f.kind === 'number' || f.kind === 'integer' ? { align: 'right' as const } : {}),
            }))
          return columns.length === 0 ? r : { layout: updateProps(r.layout, r.id, { columns }), id: r.id }
        }, `Table for ${field.path}`)
        return
      }
      place(
        (l, page) => {
          const r = addElement(l, page, target.region, 'field', { binding: field.path, label: humanize(field.name), index: target.index, at: target.at })
          return field.kind === 'date' ? { layout: updateProps(r.layout, r.id, { filter: 'date' }), id: r.id } : r
        },
        `Field ${field.path}`,
      )
    },
    [fields, place],
  )

  const onAddType = (type: ElementType) => {
    const t = targetFor(type)
    place((l, page) => addElement(l, page, t.region, type, { index: t.index }), typeLabel(type))
  }

  const onAddField = (field: SchemaField) => {
    if (field.kind === 'array') return addFieldAt(field, { region: 'body', index: targetFor('table').index })
    addFieldAt(field, targetFor('field'))
  }

  const onDrop = (payload: DragPayload, target: DropTarget) => {
    const t = target.region === 'body' ? { region: target.region, index: target.index } : { region: target.region, at: target.at }
    if (payload.kind === 'new') {
      if (!canPlace(payload.type, target.region)) {
        setBanner({ tone: 'warn', message: `${typeLabel(payload.type)} can't go in the ${target.region}.` })
        return
      }
      place((l, page) => addElement(l, page, t.region, payload.type, t), typeLabel(payload.type))
    } else if (payload.kind === 'field') {
      addFieldAt({ path: payload.path, name: payload.path.split('.').pop()!, kind: payload.fieldKind }, t)
    } else if (payload.kind === 'block' && target.region === 'body') {
      const found = findElement(draft.layout, payload.id)
      if (!found || found.region !== 'body') return
      const to = target.index > found.index ? target.index - 1 : target.index
      actions.editLayout((l) => moveBlock(l, payload.id, to))
    }
  }

  // ---------- element commands ----------

  const deleteElement = useCallback(
    (id: string) => {
      actions.editLayout((l) => removeElement(l, id), { select: null })
      setAnnouncement(`${id} deleted.`)
    },
    [actions],
  )
  const duplicate = useCallback(
    (id: string) => {
      const r = duplicateElement(draft.layout, id)
      actions.edit((d) => ({ ...d, layout: r.layout }), { select: { kind: 'element', id: r.id } })
    },
    [actions, draft.layout],
  )
  const reorder = useCallback((id: string, delta: number) => actions.editLayout((l) => reorderInRegion(l, id, delta)), [actions])

  const onElementKey = (e: ReactKeyboardEvent, id: string, source: 'canvas' | 'tree') => {
    if (readOnly) return
    if (e.key === 'Delete' || e.key === 'Backspace') {
      e.preventDefault()
      deleteElement(id)
    } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'd') {
      e.preventDefault()
      duplicate(id)
    } else if (e.altKey && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) {
      e.preventDefault()
      reorder(id, e.key === 'ArrowUp' ? -1 : 1)
    } else if (source === 'canvas' && e.key.startsWith('Arrow') && !e.altKey) {
      const found = findElement(draft.layout, id)
      if (!found || found.region === 'body') return
      e.preventDefault()
      const step = e.shiftKey ? 5 : 1
      const dx = e.key === 'ArrowLeft' ? -step : e.key === 'ArrowRight' ? step : 0
      const dy = e.key === 'ArrowUp' ? -step : e.key === 'ArrowDown' ? step : 0
      actions.editLayout((l) => nudge(l, draft.pageSettings, id, dx, dy), { key: `nudge:${id}` })
    }
  }

  // ---------- save / publish ----------

  const save = useCallback(async (): Promise<boolean> => {
    if (readOnly || !etag.current) return false
    const snapshot = draft
    setBusy('save')
    try {
      const { etag: next } = await templatesApi.saveDraft(template.id, snapshot, etag.current)
      etag.current = next
      actions.markSaved(snapshot)
      setServerIssues([])
      setSavedAt(new Date())
      setPreviewRevision((r) => r + 1)
      setBanner(null)
      setAnnouncement('Draft saved.')
      return true
    } catch (e) {
      if (e instanceof ApiError && e.status === 412) {
        setBanner({
          tone: 'err',
          message: 'Someone else saved this draft after you opened it. Reload to get their version (your unsaved changes will be lost).',
          action: { label: 'Reload', run: () => onReload() },
        })
      } else if (e instanceof ApiError && e.status === 400 && e.problem.errors) {
        const mapped: DesignIssue[] = Object.entries(e.problem.errors).flatMap(([path, messages]) =>
          messages.map((m) => ({ severity: 'error' as const, code: 'server.validation', message: `${path}: ${m}`, elementId: elementIdForServerPath(snapshot.layout, path) })),
        )
        setServerIssues(mapped)
        setIssuesOpen(true)
        setBanner({ tone: 'err', message: 'The server rejected the draft. See the problems below.' })
      } else if (e instanceof ApiError && e.code === 'template.no_draft') {
        setBanner({ tone: 'err', message: 'This draft was published or replaced elsewhere.', action: { label: 'Reload', run: () => onReload() } })
      } else {
        setBanner({ tone: 'err', message: e instanceof Error ? e.message : 'Saving failed.' })
      }
      return false
    } finally {
      setBusy(null)
    }
  }, [actions, draft, onReload, readOnly, template.id])

  const publish = async () => {
    if (errorCount > 0 || readOnly) return
    if (dirty && !(await save())) return
    setBusy('publish')
    try {
      const { body } = await templatesApi.publish(template.id, etag.current!)
      onReload(`Published as version ${body.versionNumber}. Published versions can't be changed; start a new draft to edit.`)
    } catch (e) {
      setBanner({
        tone: 'err',
        message: e instanceof ApiError && e.status === 412 ? 'The draft changed elsewhere. Reload and try again.' : e instanceof Error ? e.message : 'Publishing failed.',
        action: e instanceof ApiError && e.status === 412 ? { label: 'Reload', run: () => onReload() } : undefined,
      })
      setBusy(null)
    }
  }

  const startDraft = async () => {
    setBusy('draft')
    try {
      await templatesApi.startDraft(template.id)
      onReload('New draft started from the latest published version.')
    } catch (e) {
      setBanner({ tone: 'err', message: e instanceof Error ? e.message : 'Could not start a draft.' })
      setBusy(null)
    }
  }

  // ---------- data ----------

  const importSample = async (file: File) => {
    let data: unknown
    try {
      data = JSON.parse(await file.text())
    } catch {
      setBanner({ tone: 'err', message: `${file.name} isn't valid JSON.` })
      return
    }
    if (!data || typeof data !== 'object' || Array.isArray(data)) {
      setBanner({ tone: 'err', message: 'Sample data must be a JSON object.' })
      return
    }
    const deriveSchema = isEmptySchema(draft.dataSchema) || window.confirm('Also replace the data model with one derived from this file?')
    actions.edit((d) => ({ ...d, sampleData: data, dataSchema: deriveSchema ? inferSchema(data) : d.dataSchema }))
    setShowSample(true)
    setBanner({ tone: 'ok', message: deriveSchema ? `Imported ${file.name} and derived the data model from it.` : `Imported ${file.name} as sample data.` })
  }

  // ---------- keyboard, leave guard ----------

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const mod = e.ctrlKey || e.metaKey
      if (mod && e.key.toLowerCase() === 's') {
        e.preventDefault()
        if (dirty && !busy) void save()
        return
      }
      if (isEditableTarget(e.target)) return
      if (mod && e.key.toLowerCase() === 'z' && !e.shiftKey) {
        e.preventDefault()
        actions.undo()
      } else if (mod && (e.key.toLowerCase() === 'y' || (e.key.toLowerCase() === 'z' && e.shiftKey))) {
        e.preventDefault()
        actions.redo()
      } else if (e.key === 'Escape') {
        actions.select(null)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [actions, busy, dirty, save])

  useEffect(() => {
    if (!dirty) return
    const onBeforeUnload = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', onBeforeUnload)
    return () => window.removeEventListener('beforeunload', onBeforeUnload)
  }, [dirty])

  const blocker = useBlocker(({ currentLocation, nextLocation }) => dirty && currentLocation.pathname !== nextLocation.pathname)
  useEffect(() => {
    if (blocker.state !== 'blocked') return
    if (window.confirm('You have unsaved changes. Leave and discard them?')) blocker.proceed()
    else blocker.reset()
  }, [blocker])

  const selectElement = (id: string) => {
    if (findElement(draft.layout, id)) actions.select({ kind: 'element', id })
  }

  const statusPill = readOnly ? <StatusPill tone="ok">Published v{version.versionNumber}</StatusPill> : <StatusPill tone="warn">Draft</StatusPill>

  return (
    <div className="grid h-full min-h-0 grid-cols-1 lg:grid-cols-[220px_minmax(0,1fr)_270px] xl:grid-cols-[248px_minmax(0,1fr)_300px]">
      <p className="sr-only" aria-live="polite">
        {announcement}
      </p>

      <aside aria-label="Components and data" className="min-h-0 overflow-auto border-r border-line bg-surface max-lg:order-2">
        <Palette disabled={readOnly} onAdd={onAddType} />
        <DataModel
          fields={fields}
          used={used}
          readOnly={readOnly}
          onAddField={onAddField}
          onImportSample={(f) => void importSample(f)}
          onEditJson={() => setJsonOpen(true)}
        />
        <ElementTree layout={draft.layout} selection={state.selection} flagged={flagged} onSelect={actions.select} onElementKey={(e, id) => onElementKey(e, id, 'tree')} />
      </aside>

      <div className="flex min-h-[70vh] min-w-0 flex-col lg:min-h-0">
        <div role="toolbar" aria-label="Designer" className="flex flex-none flex-wrap items-center gap-1.5 border-b border-line bg-surface px-3 py-2">
          <Link to="/templates" className="mr-1 text-[13px] text-ink-3 no-underline hover:text-cyan">
            Templates /
          </Link>
          <span className="mr-1.5 text-sm font-semibold">{template.name}</span>
          {statusPill}
          {dirty && <span className="text-xs text-ink-3">Unsaved changes</span>}
          <Sep />
          <Button variant="ghost" size="icon" aria-label="Undo" title="Undo (Ctrl+Z)" disabled={readOnly || state.history.past.length === 0} onClick={actions.undo}>
            <Undo2 />
          </Button>
          <Button variant="ghost" size="icon" aria-label="Redo" title="Redo (Ctrl+Y)" disabled={readOnly || state.history.future.length === 0} onClick={actions.redo}>
            <Redo2 />
          </Button>
          <Sep />
          <Button variant="ghost" size="icon" aria-label="Zoom out" disabled={zoom <= ZOOM_MIN} onClick={() => setZoom((z) => Math.max(ZOOM_MIN, Math.round((z - 0.1) * 10) / 10))}>
            <ZoomOut />
          </Button>
          <span className="min-w-10 text-center font-mono text-[12.5px] text-ink-2" aria-label="Zoom level">
            {Math.round(zoom * 100)}%
          </span>
          <Button variant="ghost" size="icon" aria-label="Zoom in" disabled={zoom >= ZOOM_MAX} onClick={() => setZoom((z) => Math.min(ZOOM_MAX, Math.round((z + 0.1) * 10) / 10))}>
            <ZoomIn />
          </Button>
          <Sep />
          <Switch label="Sample data" checked={showSample} onCheckedChange={setShowSample} />
          <Switch label="Grid" checked={showGrid} onCheckedChange={setShowGrid} />
          <div className="flex-1" />
          <Button size="sm" aria-pressed={previewOpen} disabled={readOnly} title={readOnly ? 'Preview renders the draft; start a new draft to preview.' : undefined} onClick={() => setPreviewOpen((o) => !o)}>
            <Eye /> Preview
          </Button>
          {readOnly ? (
            <Button size="sm" variant="primary" disabled={busy !== null} onClick={() => void startDraft()}>
              <FilePlus2 /> Start new draft
            </Button>
          ) : (
            <>
              <Button size="sm" disabled={!dirty || busy !== null} onClick={() => void save()}>
                <Save /> {busy === 'save' ? 'Saving…' : 'Save draft'}
              </Button>
              <Button
                size="sm"
                variant="primary"
                disabled={errorCount > 0 || busy !== null}
                title={errorCount > 0 ? `Fix ${errorCount} problem${errorCount === 1 ? '' : 's'} before publishing.` : 'Freeze this draft as the next version'}
                onClick={() => void publish()}
              >
                <UploadCloud /> {busy === 'publish' ? 'Publishing…' : 'Publish'}
              </Button>
            </>
          )}
        </div>

        {banner && (
          <div
            role={banner.tone === 'err' ? 'alert' : 'status'}
            className={cn(
              'flex flex-none items-center gap-3 border-b border-line px-3.5 py-2 text-[13px]',
              banner.tone === 'ok' && 'bg-ok-soft text-ok',
              banner.tone === 'warn' && 'bg-warn-soft text-warn',
              banner.tone === 'err' && 'bg-err-soft text-err',
            )}
          >
            <span className="flex-1">{banner.message}</span>
            {banner.action && (
              <Button size="sm" onClick={banner.action.run}>
                {banner.action.label}
              </Button>
            )}
            <Button size="sm" variant="ghost" onClick={() => setBanner(null)} aria-label="Dismiss message">
              Dismiss
            </Button>
          </div>
        )}

        <div className="flex min-h-0 flex-1">
          <div className="flex min-h-0 min-w-0 flex-1 flex-col">
            <Canvas
              draft={draft}
              selection={state.selection}
              zoom={zoom}
              showGrid={showGrid}
              ctx={ctx}
              readOnly={readOnly}
              flagged={flagged}
              onSelect={actions.select}
              onPreview={actions.previewLayout}
              onCommit={actions.commit}
              onDrop={onDrop}
              onAddBand={(r) => actions.editLayout((l) => addBand(l, r), { select: { kind: 'region', region: r } })}
              onElementKey={(e, id) => onElementKey(e, id, 'canvas')}
            />
          </div>
          {previewOpen && !readOnly && (
            <PreviewPane
              templateId={template.id}
              revision={previewRevision}
              dirty={dirty}
              onSave={() => void save()}
              onClose={() => setPreviewOpen(false)}
              onSelectElement={selectElement}
            />
          )}
        </div>

        {issuesOpen && issues.length > 0 && (
          <ul aria-label="Problems" className="max-h-40 flex-none overflow-auto border-t border-line bg-surface px-3 py-2 text-[12.5px]">
            {issues.map((i, n) => (
              <li key={n}>
                <button
                  type="button"
                  className="flex w-full items-center gap-2 rounded px-1 py-0.5 text-left hover:bg-surface-2"
                  onClick={() => (i.elementId ? selectElement(i.elementId) : actions.select(null))}
                >
                  <span className={cn('size-1.5 flex-none rounded-full', i.severity === 'error' ? 'bg-err' : 'bg-warn')} aria-hidden="true" />
                  <span className="text-ink-2">{i.message}</span>
                  {i.elementId && <span className="ml-auto font-mono text-[11px] text-ink-3">{i.elementId}</span>}
                </button>
              </li>
            ))}
          </ul>
        )}

        <div className="flex flex-none flex-wrap items-center gap-4 border-t border-line bg-surface px-3.5 py-1.5 text-[12.5px] text-ink-2">
          <span className="flex items-center gap-1.5">
            <Braces className="size-3.5" aria-hidden="true" /> {boundCount} bound element{boundCount === 1 ? '' : 's'}
          </span>
          {unmappedCount > 0 ? (
            <span className="flex items-center gap-1.5 text-err">
              <CircleAlert className="size-3.5" aria-hidden="true" /> {unmappedCount} unmapped field{unmappedCount === 1 ? '' : 's'}
            </span>
          ) : (
            <span className="flex items-center gap-1.5 text-ok">
              <CircleCheck className="size-3.5" aria-hidden="true" /> All fields mapped
            </span>
          )}
          <button
            type="button"
            className={cn('flex items-center gap-1.5 rounded px-1 hover:bg-surface-2', errorCount > 0 ? 'text-err' : 'text-ink-2')}
            aria-expanded={issuesOpen}
            onClick={() => setIssuesOpen((o) => !o)}
            disabled={issues.length === 0}
          >
            {issues.length === 0 ? 'No problems' : `${errorCount} error${errorCount === 1 ? '' : 's'}, ${issues.length - errorCount} warning${issues.length - errorCount === 1 ? '' : 's'}`}
          </button>
          <span className="flex items-center gap-1.5">
            <Ruler className="size-3.5" aria-hidden="true" /> Snap 1 mm (Alt: 0.1)
          </span>
          <span className="ml-auto text-ink-3">
            {readOnly
              ? `Published ${version.publishedAt ? new Date(version.publishedAt).toLocaleString('en-GB') : ''}`
              : savedAt
                ? `Saved ${savedAt.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })}`
                : `Last saved ${new Date(version.updatedAt).toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' })}`}
          </span>
        </div>
      </div>

      <aside aria-label="Properties" className="min-h-0 overflow-auto border-l border-line bg-surface max-lg:order-3">
        <PropertiesPanel
          draft={draft}
          selection={state.selection}
          issues={issues}
          fields={fields}
          readOnly={readOnly}
          templateName={template.name}
          templateKey={template.key}
          edit={actions.edit}
          editLayout={actions.editLayout}
          onDuplicate={duplicate}
          onDelete={deleteElement}
          onReorder={reorder}
          onAddBand={(r) => actions.editLayout((l) => addBand(l, r))}
          onRemoveBand={(r) => {
            const count = draft.layout[r]?.elements.length ?? 0
            if (count === 0 || window.confirm(`Remove the ${r} and its ${count} element${count === 1 ? '' : 's'}?`))
              actions.editLayout((l) => removeBand(l, r), { select: null })
          }}
        />
      </aside>

      <DataJsonDialog
        open={jsonOpen}
        onOpenChange={setJsonOpen}
        dataSchema={draft.dataSchema}
        sampleData={draft.sampleData}
        onApply={(dataSchema, sampleData) => actions.edit((d) => ({ ...d, dataSchema, sampleData }))}
      />
    </div>
  )
}

function Sep() {
  return <span aria-hidden="true" className="mx-1 h-5 w-px bg-line" />
}

function typeLabel(type: ElementType) {
  return TYPE_LABELS[type]
}
