import '@xyflow/react/dist/base.css'

import {
  Background,
  Controls,
  ReactFlow,
  ReactFlowProvider,
  useReactFlow,
  type Connection,
  type Edge,
  type EdgeChange,
  type NodeChange,
} from '@xyflow/react'
import { CircleAlert, CircleCheck, FilePlus2, Redo2, Save, Trash2, Undo2, UploadCloud } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useBlocker, useParams } from 'react-router'

import { usePermissions } from '@/app/AuthGate'
import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Field, Input } from '@/components/ui/form'
import { cn } from '@/lib/utils'
import { ApiError } from '@/lib/api/client'
import type { NodeTypeDto, WorkflowDto, WorkflowIssueDto, WorkflowVersionDto } from '@/lib/api/generated'
import { templatesApi } from '@/lib/api/templates'
import { workflowsApi } from '@/lib/api/workflows'
import { inferSchema, modelFields, scalarPaths, schemaFields } from '@/features/templates/model/schema'

import { addNode, asDefinition, connect, moveNode, nextPosition, removeEdges, removeNodes, updateNode, type WorkflowDefinition } from '../model/definition'
import { CATEGORY_COLORS, CATEGORY_LABELS, NODE_ICONS, NodeCard, type VersoFlowNode } from './NodeCard'
import { parseJsonObject } from '../model/payloadSchema'
import { ApiTriggerSettings, type TemplateSample } from './ApiTriggerSettings'
import { FolderInputSettings } from './FolderInputSettings'
import { MapFieldsSettings } from './MapFieldsSettings'
import { SchemaForm, type FieldSchema, type TemplateOption } from './SchemaForm'
import { useWorkflowState } from './workflowState'

interface Loaded {
  workflow: WorkflowDto
  version: WorkflowVersionDto
  etag: string | null
  nodeTypes: NodeTypeDto[]
}

type LoadState = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; loaded: Loaded; generation: number }

async function load(id: string, signal?: AbortSignal): Promise<Loaded> {
  const [workflow, nodeTypes] = await Promise.all([workflowsApi.get(id, signal), workflowsApi.nodeTypes(signal)])
  if (workflow.draftVersionId) {
    const { body, etag } = await workflowsApi.draft(id, signal)
    return { workflow, version: body, etag, nodeTypes }
  }
  const latest = (await workflowsApi.versions(id, signal)).find((v) => v.status === 'Published')
  if (!latest) throw new Error('This workflow has no draft and no published version.')
  const { body, etag } = await workflowsApi.version(id, latest.versionId, signal)
  return { workflow, version: body, etag, nodeTypes }
}

/** Route /workflows/:id. */
export function WorkflowDesignerPage() {
  const { id = '' } = useParams()
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [flash, setFlash] = useState<string | null>(null)
  const generation = useRef(0)

  const reload = useCallback(
    (message?: string) => {
      setFlash(message ?? null)
      load(id)
        .then((loaded) => setState({ kind: 'ready', loaded, generation: ++generation.current }))
        .catch((e: unknown) => setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load the workflow.' }))
    },
    [id],
  )

  useEffect(() => {
    const controller = new AbortController()
    setState({ kind: 'loading' })
    load(id, controller.signal)
      .then((loaded) => setState({ kind: 'ready', loaded, generation: ++generation.current }))
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setState({ kind: 'error', message: e instanceof ApiError && e.status === 404 ? 'Workflow not found.' : e instanceof Error ? e.message : 'Could not load the workflow.' })
      })
    return () => controller.abort()
  }, [id])

  if (state.kind === 'loading') return <p className="p-6 text-ink-3">Loading workflow…</p>
  if (state.kind === 'error')
    return (
      <div className="p-6">
        <p role="alert" className="mb-3 rounded-lg border border-err/30 bg-err-soft px-4 py-3 text-err">
          {state.message}
        </p>
        <Link to="/workflows" className="text-cyan">
          Back to workflows
        </Link>
      </div>
    )
  return (
    <ReactFlowProvider key={state.generation}>
      <Designer loaded={state.loaded} flash={flash} onReload={reload} />
    </ReactFlowProvider>
  )
}

/** The template's current data model: its draft if it has one, else its latest published version. */
async function latestTemplateVersion(templateId: string, signal: AbortSignal) {
  const template = await templatesApi.get(templateId, signal)
  if (template.draftVersionId) return (await templatesApi.draft(templateId, signal)).body
  const published = (await templatesApi.versions(templateId, signal)).find((v) => v.status === 'Published')
  return published ? (await templatesApi.version(templateId, published.versionId, signal)).body : null
}

type Banner = { tone: 'ok' | 'warn' | 'err'; message: string; action?: { label: string; run: () => void } } | null

const nodeTypesForFlow = { verso: NodeCard }
const isEditable = (t: EventTarget | null) => t instanceof HTMLElement && (t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName))

/** The palette shows the first sentence only (as the prototype does); the rest, such as "Simulated: ...", stays in the settings panel and tooltip. */
const shortDescription = (description: string) => (description.split(/\.\s/)[0] ?? description).replace(/\.$/, '')

function Designer({ loaded, flash, onReload }: { loaded: Loaded; flash: string | null; onReload: (message?: string) => void }) {
  const { workflow, version, nodeTypes } = loaded
  const canEdit = usePermissions().workflowAuthoring // others see the workflow read-only (the API refuses their edits anyway)
  const readOnly = version.status !== 'Draft' || !canEdit
  const initial = useMemo(() => asDefinition(version.definition), [version])
  const [state, actions] = useWorkflowState(initial)
  const def = state.history.present
  const dirty = def !== state.saved
  const etag = useRef(loaded.etag)
  const flow = useReactFlow()
  const typeOf = useMemo(() => new Map(nodeTypes.map((t) => [t.type, t])), [nodeTypes])

  const [issues, setIssues] = useState<WorkflowIssueDto[]>([])
  const [validating, setValidating] = useState(false)
  const [busy, setBusy] = useState<'save' | 'publish' | 'draft' | null>(null)
  const [banner, setBanner] = useState<Banner>(flash ? { tone: 'ok', message: flash } : null)
  const [measured, setMeasured] = useState<Record<string, { width: number; height: number }>>({})
  const [templates, setTemplates] = useState<TemplateOption[]>([])
  const [templatePaths, setDataPaths] = useState<string[]>([])
  const [templateSampleData, setTemplateSampleData] = useState<unknown>(undefined)
  const [templateFields, setTemplateFields] = useState<string[] | undefined>(undefined)
  const dragOrigin = useRef<WorkflowDefinition | null>(null)

  // ---------- live validation (server is the source of truth) ----------
  useEffect(() => {
    if (readOnly) return
    const controller = new AbortController()
    setValidating(true)
    const timer = setTimeout(() => {
      workflowsApi
        .validate(workflow.id, def, controller.signal)
        .then((r) => setIssues(r.issues))
        .catch((e: unknown) => {
          if (e instanceof DOMException && e.name === 'AbortError') return
          if (e instanceof ApiError && e.problem.errors) {
            setIssues(Object.entries(e.problem.errors).map(([path, m]) => ({ severity: 'error', code: 'definition.invalid', message: `${path}: ${m[0]}`, nodeId: null, edgeId: null, port: null, field: null })))
          }
        })
        .finally(() => setValidating(false))
    }, 350)
    return () => {
      clearTimeout(timer)
      controller.abort()
    }
  }, [def, workflow.id, readOnly])

  // ---------- templates for the picker, and data paths for field suggestions ----------
  useEffect(() => {
    const controller = new AbortController()
    templatesApi
      .list(1, 100, controller.signal)
      .then(async (page) => {
        const base = page.items.map((t) => ({ id: t.id, name: t.name, key: t.key, publishedVersions: undefined as number[] | undefined }))
        setTemplates(base)
        const withVersions = await Promise.all(
          base.map(async (t) => {
            const versions = await templatesApi.versions(t.id, controller.signal).catch(() => [])
            return {
              ...t,
              publishedVersions: versions.filter((v) => v.status === 'Published' && v.versionNumber != null).map((v) => v.versionNumber!).sort((a, b) => b - a),
            }
          }),
        )
        setTemplates(withVersions)
      })
      .catch(() => {})
    return () => controller.abort()
  }, [])

  const referencedTemplate = def.nodes.find((n) => n.type === 'generate-pdf' && typeof n.config.templateId === 'string' && n.config.templateId)?.config.templateId as string | undefined
  useEffect(() => {
    if (!referencedTemplate) {
      setDataPaths([])
      setTemplateFields(undefined)
      setTemplateSampleData(undefined)
      return
    }
    const controller = new AbortController()
    // Field suggestions and sample data come from the template's latest version (draft or published).
    latestTemplateVersion(referencedTemplate, controller.signal)
      .then((v) => {
        setDataPaths(v ? scalarPaths(schemaFields(v.dataSchema)).map((f) => f.path) : [])
        setTemplateFields(v ? modelFields(schemaFields(v.dataSchema)) : undefined)
        setTemplateSampleData(v?.sampleData ?? undefined)
      })
      .catch(() => {
        setDataPaths([])
        setTemplateFields(undefined)
        setTemplateSampleData(undefined)
      })
    return () => controller.abort()
  }, [referencedTemplate])
  const templateName = templates.find((t) => t.id === referencedTemplate)?.name
  const templateSample: TemplateSample | undefined =
    templateSampleData !== undefined && templateSampleData !== null && templateName ? { templateName, data: templateSampleData } : undefined

  // Field suggestions: the referenced template's data model plus the API trigger's sample payload.
  const triggerSample = def.nodes.find((n) => n.type === 'api-trigger')?.config.samplePayload
  const dataPaths = useMemo(() => {
    const sample = typeof triggerSample === 'string' ? parseJsonObject(triggerSample) : undefined
    const fromSample = sample ? scalarPaths(schemaFields(inferSchema(sample))).map((f) => f.path) : []
    return [...new Set([...templatePaths, ...fromSample])]
  }, [templatePaths, triggerSample])

  // ---------- issues ----------
  const issuesByNode = useMemo(() => {
    const m = new Map<string, WorkflowIssueDto[]>()
    for (const i of issues) if (i.nodeId) m.set(i.nodeId, [...(m.get(i.nodeId) ?? []), i])
    return m
  }, [issues])
  const errorCount = issues.filter((i) => i.severity === 'error').length
  const warningCount = issues.length - errorCount

  // ---------- React Flow view of the definition ----------
  const nodes: VersoFlowNode[] = useMemo(
    () =>
      def.nodes.map((n) => ({
        id: n.id,
        type: 'verso' as const,
        position: n.position,
        selected: state.selection === n.id,
        draggable: !readOnly,
        deletable: !readOnly,
        measured: measured[n.id],
        data: { node: n, nodeType: typeOf.get(n.type), issues: issuesByNode.get(n.id) ?? [], outgoing: def.edges.filter((e) => e.source === n.id) },
      })),
    [def, state.selection, readOnly, measured, typeOf, issuesByNode],
  )
  const edgeIssues = useMemo(() => new Set(issues.filter((i) => i.edgeId && i.severity === 'error').map((i) => i.edgeId!)), [issues])
  const edges: Edge[] = useMemo(
    () =>
      def.edges.map((e) => ({
        id: e.id,
        source: e.source,
        sourceHandle: e.sourcePort,
        target: e.target,
        targetHandle: e.targetPort,
        deletable: !readOnly,
        label: e.sourcePort === 'out' || e.sourcePort === 'success' || e.sourcePort === 'valid' ? undefined : e.sourcePort,
        style: { stroke: edgeIssues.has(e.id) ? 'var(--err)' : 'var(--line-strong)', strokeWidth: 1.75 },
      })),
    [def.edges, readOnly, edgeIssues],
  )

  const onNodesChange = useCallback(
    (changes: NodeChange<VersoFlowNode>[]) => {
      const sizes: Record<string, { width: number; height: number }> = {}
      // Clicking a step sends "select it" and "deselect the previous one" together, in either order:
      // a newly selected step wins; otherwise deselecting the current step clears the selection.
      const selects = changes.filter((c) => c.type === 'select')
      const picked = selects.find((c) => c.selected)
      if (picked) actions.select(picked.id)
      else if (selects.some((c) => c.id === state.selection)) actions.select(null)
      for (const c of changes) {
        if (c.type === 'dimensions' && c.dimensions) sizes[c.id] = c.dimensions
        else if (c.type === 'position' && c.position && !readOnly) {
          if (c.dragging) {
            dragOrigin.current ??= def
            actions.preview((d) => moveNode(d, c.id, c.position!))
          } else {
            actions.preview((d) => moveNode(d, c.id, c.position!))
            if (dragOrigin.current) actions.commit(dragOrigin.current)
            dragOrigin.current = null
          }
        } else if (c.type === 'position' && c.dragging === false && dragOrigin.current) {
          actions.commit(dragOrigin.current)
          dragOrigin.current = null
        } else if (c.type === 'remove' && !readOnly) {
          actions.edit((d) => removeNodes(d, [c.id]))
        }
      }
      if (Object.keys(sizes).length) setMeasured((m) => ({ ...m, ...sizes }))
    },
    [actions, def, readOnly, state.selection],
  )

  const onEdgesChange = useCallback(
    (changes: EdgeChange[]) => {
      const removed = changes.filter((c) => c.type === 'remove').map((c) => c.id)
      if (removed.length && !readOnly) actions.edit((d) => removeEdges(d, removed))
    },
    [actions, readOnly],
  )

  const onConnect = useCallback(
    (c: Connection) => {
      if (readOnly || !c.source || !c.target || !c.sourceHandle || !c.targetHandle) return
      actions.edit((d) => connect(d, c.source, c.sourceHandle!, c.target, c.targetHandle!))
    },
    [actions, readOnly],
  )

  const addType = (type: NodeTypeDto, position?: { x: number; y: number }) => {
    if (readOnly) return
    const result = addNode(def, type, position ?? nextPosition(def, state.selection ?? undefined))
    actions.edit(() => result.def, { select: result.id })
  }

  // ---------- save / publish ----------
  const save = useCallback(async (): Promise<boolean> => {
    if (readOnly || !etag.current) return false
    const snapshot = def
    setBusy('save')
    try {
      const { etag: next } = await workflowsApi.saveDraft(workflow.id, snapshot, etag.current)
      etag.current = next
      actions.markSaved(snapshot)
      setBanner(null)
      return true
    } catch (e) {
      if (e instanceof ApiError && e.status === 412)
        setBanner({ tone: 'err', message: 'Someone else saved this workflow after you opened it. Reload to get their version (your unsaved changes will be lost).', action: { label: 'Reload', run: () => onReload() } })
      else setBanner({ tone: 'err', message: e instanceof Error ? e.message : 'Saving failed.' })
      return false
    } finally {
      setBusy(null)
    }
  }, [actions, def, onReload, readOnly, workflow.id])

  const publish = async () => {
    if (readOnly || errorCount > 0) return
    if (dirty && !(await save())) return
    setBusy('publish')
    try {
      const { body } = await workflowsApi.publish(workflow.id, etag.current!)
      onReload(`Published as version ${body.versionNumber}. Runs submitted to '${workflow.key}' will use it.`)
    } catch (e) {
      if (e instanceof ApiError && e.code === 'workflow.invalid' && Array.isArray((e.problem as { issues?: unknown }).issues)) {
        setIssues((e.problem as { issues: WorkflowIssueDto[] }).issues)
        setBanner({ tone: 'err', message: e.message })
      } else setBanner({ tone: 'err', message: e instanceof Error ? e.message : 'Publishing failed.' })
      setBusy(null)
    }
  }

  const startDraft = async () => {
    setBusy('draft')
    try {
      await workflowsApi.startDraft(workflow.id)
      onReload('New draft started from the latest published version.')
    } catch (e) {
      setBanner({ tone: 'err', message: e instanceof Error ? e.message : 'Could not start a draft.' })
      setBusy(null)
    }
  }

  // ---------- keyboard and leave guard ----------
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const mod = e.ctrlKey || e.metaKey
      if (mod && e.key.toLowerCase() === 's') {
        e.preventDefault()
        if (dirty && !busy) void save()
        return
      }
      if (isEditable(e.target)) return
      if (mod && e.key.toLowerCase() === 'z' && !e.shiftKey) {
        e.preventDefault()
        actions.undo()
      } else if (mod && (e.key.toLowerCase() === 'y' || (e.key.toLowerCase() === 'z' && e.shiftKey))) {
        e.preventDefault()
        actions.redo()
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

  const selected = def.nodes.find((n) => n.id === state.selection)
  const byCategory = useMemo(() => {
    const groups = new Map<string, NodeTypeDto[]>()
    for (const t of nodeTypes) groups.set(t.category, [...(groups.get(t.category) ?? []), t])
    return [...groups.entries()]
  }, [nodeTypes])
  const hasTrigger = def.nodes.some((n) => typeOf.get(n.type)?.category === 'input')

  return (
    <div className="grid h-full min-h-0 grid-cols-1 lg:grid-cols-[220px_minmax(0,1fr)_300px] xl:grid-cols-[240px_minmax(0,1fr)_320px]">
      <aside aria-label="Steps" className="min-h-0 overflow-auto border-r border-line bg-surface max-lg:order-2">
        <section aria-label="Palette" className="border-b border-line px-3.5 pt-3.5 pb-4">
          <h3 className="mb-2.5 flex items-center text-[13px] font-semibold">
            Steps <span className="ml-auto text-xs font-normal text-ink-3">{readOnly ? '' : 'Click or drag'}</span>
          </h3>
          {byCategory.map(([category, types]) => (
            <div key={category} className="mb-3">
              <div className="mb-1.5 flex items-center gap-1.5 text-xs text-ink-3">
                <span className="size-2 rounded-full" style={{ background: CATEGORY_COLORS[category] }} aria-hidden="true" />
                {CATEGORY_LABELS[category] ?? category}
              </div>
              <div className="flex flex-col gap-1.5">
                {types.map((t) => {
                  const Icon = NODE_ICONS[t.type] ?? CircleAlert
                  const disabled = readOnly || (t.category === 'input' && hasTrigger)
                  return (
                    <button
                      key={t.type}
                      type="button"
                      disabled={disabled}
                      draggable={!disabled}
                      onDragStart={(e) => {
                        e.dataTransfer.setData('application/x-verso-node', t.type)
                        e.dataTransfer.effectAllowed = 'copy'
                      }}
                      onClick={() => addType(t)}
                      aria-label={`Add ${t.label}`}
                      title={t.category === 'input' && hasTrigger ? 'A workflow has exactly one trigger.' : t.description}
                      className="flex items-start gap-2 rounded-lg border border-line bg-surface-2 px-2.5 py-2 text-left text-[12.5px] font-medium hover:border-cyan hover:text-cyan-strong disabled:cursor-default disabled:opacity-50 disabled:hover:border-line disabled:hover:text-ink"
                    >
                      <Icon className="mt-px size-4 flex-none" style={{ color: CATEGORY_COLORS[t.category] }} strokeWidth={1.75} aria-hidden="true" />
                      <span className="min-w-0">
                        <span className="block">{t.label}</span>
                        <span className="mt-0.5 block text-[11.5px] leading-snug font-normal text-ink-3">{shortDescription(t.description)}</span>
                      </span>
                    </button>
                  )
                })}
              </div>
            </div>
          ))}
        </section>
        <section aria-label="Issues" className="px-3.5 pt-3.5 pb-4">
          <h3 className="mb-2 text-[13px] font-semibold">Issues</h3>
          {readOnly ? (
            <p className="text-[12.5px] text-ink-3">Published versions passed validation.</p>
          ) : issues.length === 0 ? (
            <p className="flex items-center gap-1.5 text-[12.5px] text-ok">
              <CircleCheck className="size-3.5" aria-hidden="true" /> {validating ? 'Checking…' : 'No issues'}
            </p>
          ) : (
            <ul className="flex flex-col gap-1 text-[12.5px]">
              {issues.map((i, n) => (
                <li key={n}>
                  <button
                    type="button"
                    onClick={() => i.nodeId && actions.select(i.nodeId)}
                    className="flex w-full gap-1.5 rounded px-1 py-0.5 text-left hover:bg-surface-2"
                  >
                    <span className={cn('mt-1.5 size-1.5 flex-none rounded-full', i.severity === 'error' ? 'bg-err' : 'bg-warn')} aria-label={i.severity} />
                    <span className="text-ink-2">
                      {i.nodeId && <span className="font-medium text-ink">{def.nodes.find((x) => x.id === i.nodeId)?.title ?? i.nodeId}: </span>}
                      {i.message}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </section>
      </aside>

      <div className="flex min-h-[70vh] min-w-0 flex-col lg:min-h-0">
        <div role="toolbar" aria-label="Workflow designer" className="flex flex-none flex-wrap items-center gap-1.5 border-b border-line bg-surface px-3 py-2">
          <Link to="/workflows" className="mr-1 text-[13px] text-ink-3 no-underline hover:text-cyan">
            Workflows /
          </Link>
          <span className="mr-1.5 text-sm font-semibold">{workflow.name}</span>
          {readOnly ? <StatusPill tone="ok">Published v{version.versionNumber}</StatusPill> : <StatusPill tone="warn">Draft</StatusPill>}
          {dirty && <span className="text-xs text-ink-3">Unsaved changes</span>}
          <span aria-hidden="true" className="mx-1 h-5 w-px bg-line" />
          <Button variant="ghost" size="icon" aria-label="Undo" disabled={readOnly || state.history.past.length === 0} onClick={actions.undo}>
            <Undo2 />
          </Button>
          <Button variant="ghost" size="icon" aria-label="Redo" disabled={readOnly || state.history.future.length === 0} onClick={actions.redo}>
            <Redo2 />
          </Button>
          <div className="flex-1" />
          {!canEdit ? (
            <span className="text-[12.5px] text-ink-3">View only: editing needs the WorkflowAuthor role</span>
          ) : readOnly ? (
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
                disabled={errorCount > 0 || validating || busy !== null}
                title={errorCount > 0 ? `Fix ${errorCount} error${errorCount === 1 ? '' : 's'} before publishing.` : 'Freeze this draft as the next version'}
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
            <Button size="sm" variant="ghost" onClick={() => setBanner(null)}>
              Dismiss
            </Button>
          </div>
        )}

        <div
          className="relative min-h-0 flex-1 bg-[var(--stage)]"
          onDragOver={(e) => {
            if (!readOnly && Array.from(e.dataTransfer.types).includes('application/x-verso-node')) e.preventDefault()
          }}
          onDrop={(e) => {
            const type = typeOf.get(e.dataTransfer.getData('application/x-verso-node'))
            if (!type || readOnly) return
            e.preventDefault()
            addType(type, flow.screenToFlowPosition({ x: e.clientX, y: e.clientY }))
          }}
        >
          <ReactFlow<VersoFlowNode>
            nodes={nodes}
            edges={edges}
            nodeTypes={nodeTypesForFlow}
            onNodesChange={onNodesChange}
            onEdgesChange={onEdgesChange}
            onConnect={onConnect}
            onPaneClick={() => actions.select(null)}
            nodesConnectable={!readOnly}
            elementsSelectable
            deleteKeyCode={readOnly ? null : ['Delete', 'Backspace']}
            fitView
            fitViewOptions={{ padding: 0.3, maxZoom: 1 }}
            minZoom={0.3}
            maxZoom={1.5}
            aria-label="Workflow canvas"
          >
            <Background gap={16} size={1} color="var(--stage-dot)" />
            <Controls showInteractive={false} />
          </ReactFlow>
        </div>

        <div className="flex flex-none flex-wrap items-center gap-4 border-t border-line bg-surface px-3.5 py-1.5 text-[12.5px] text-ink-2">
          <span>
            {def.nodes.length} step{def.nodes.length === 1 ? '' : 's'}, {def.edges.length} connection{def.edges.length === 1 ? '' : 's'}
          </span>
          {!readOnly &&
            (errorCount > 0 ? (
              <span className="flex items-center gap-1.5 text-err">
                <CircleAlert className="size-3.5" aria-hidden="true" /> {errorCount} error{errorCount === 1 ? '' : 's'}
                {warningCount > 0 ? `, ${warningCount} warning${warningCount === 1 ? '' : 's'}` : ''}
              </span>
            ) : (
              <span className="flex items-center gap-1.5 text-ok">
                <CircleCheck className="size-3.5" aria-hidden="true" /> Ready to publish{warningCount > 0 ? ` (${warningCount} warning${warningCount === 1 ? '' : 's'})` : ''}
              </span>
            ))}
          <span className="ml-auto font-mono text-[11.5px] text-ink-3">POST /api/v1/workflows/{workflow.key}/executions</span>
        </div>
      </div>

      <aside aria-label="Settings" className="min-h-0 overflow-auto border-l border-line bg-surface max-lg:order-3">
        {selected ? (
          <NodeSettings
            key={selected.id}
            node={selected}
            nodeType={typeOf.get(selected.type)}
            issues={issuesByNode.get(selected.id) ?? []}
            readOnly={readOnly}
            templates={templates}
            dataPaths={dataPaths}
            workflowKey={workflow.key}
            templateSample={templateSample}
            templateFields={templateFields}
            templateName={templateName}
            workflowId={workflow.id}
            published={workflow.latestPublishedVersion != null}
            canTest={canEdit}
            onChange={(patch, key) => actions.edit((d) => updateNode(d, selected.id, patch), { key: `${selected.id}:${key}` })}
            onDelete={() => actions.edit((d) => removeNodes(d, [selected.id]), { select: null })}
          />
        ) : (
          <div className="px-3.5 py-3.5">
            <h3 className="mb-2 text-[13px] font-semibold">Workflow</h3>
            <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1.5 text-[12.5px]">
              <dt className="text-ink-3">Name</dt>
              <dd>{workflow.name}</dd>
              <dt className="text-ink-3">Key</dt>
              <dd className="font-mono">{workflow.key}</dd>
              <dt className="text-ink-3">Version</dt>
              <dd>{readOnly ? `v${version.versionNumber} (published)` : `Draft${workflow.latestPublishedVersion ? ` after v${workflow.latestPublishedVersion}` : ''}`}</dd>
            </dl>
            {workflow.description && <p className="mt-3 text-[12.5px] text-ink-2">{workflow.description}</p>}
            <p className="mt-4 text-[12.5px] text-ink-3">
              Select a step to edit its settings. Connect an output (right side of a step) to the next step's input (left side). Delete a selected step or connection with Delete.
            </p>
          </div>
        )}
      </aside>
    </div>
  )
}

function NodeSettings({
  node,
  nodeType,
  issues,
  readOnly,
  templates,
  dataPaths,
  workflowKey,
  templateSample,
  templateFields,
  templateName,
  workflowId,
  published,
  canTest,
  onChange,
  onDelete,
}: {
  node: WorkflowDefinition['nodes'][number]
  nodeType?: NodeTypeDto
  issues: WorkflowIssueDto[]
  readOnly: boolean
  templates: TemplateOption[]
  dataPaths: string[]
  workflowKey: string
  templateSample?: TemplateSample
  templateFields?: string[]
  templateName?: string
  workflowId: string
  published: boolean
  canTest: boolean
  onChange: (patch: { title?: string; config?: Record<string, unknown> }, key: string) => void
  onDelete: () => void
}) {
  const Icon = NODE_ICONS[node.type] ?? CircleAlert
  const fieldErrors = (field: string) => issues.filter((i) => i.field === field).map((i) => i.message)
  const general = issues.filter((i) => !i.field)

  return (
    <div>
      <div className="flex items-center gap-2 border-b border-line px-3.5 py-3">
        <Icon className="size-4" style={{ color: CATEGORY_COLORS[nodeType?.category ?? 'logic'] }} aria-hidden="true" />
        <h3 className="text-[13px] font-semibold">{nodeType?.label ?? node.type}</h3>
        <span className="ml-auto font-mono text-[11px] text-ink-3">{node.id}</span>
      </div>
      {general.length > 0 && (
        <ul className="mx-3.5 mt-3 flex flex-col gap-1" aria-label="Problems with this step">
          {general.map((i, n) => (
            <li key={n} className={cn('rounded-md px-2.5 py-1.5 text-[12.5px]', i.severity === 'error' ? 'bg-err-soft text-err' : 'bg-warn-soft text-warn')}>
              {i.message}
            </li>
          ))}
        </ul>
      )}
      <div className="flex flex-col gap-3 border-b border-line px-3.5 py-3.5">
        <Field label="Title" htmlFor="node-title">
          <Input id="node-title" value={node.title} maxLength={100} disabled={readOnly} onChange={(e) => onChange({ title: e.target.value }, 'title')} />
        </Field>
        {nodeType && <p className="text-[12.5px] text-ink-3">{nodeType.description}</p>}
      </div>
      <section aria-label="Step settings" className="border-b border-line px-3.5 py-3.5">
        <h4 className="mb-2.5 text-[13px] font-semibold">Settings</h4>
        {nodeType && node.type === 'api-trigger' ? (
          <ApiTriggerSettings
            schema={nodeType.configSchema as FieldSchema}
            value={node.config}
            onChange={(config, key) => onChange({ config }, key)}
            errorsFor={fieldErrors}
            readOnly={readOnly}
            workflowKey={workflowKey}
            templateSample={templateSample}
          />
        ) : nodeType && node.type === 'folder-input' ? (
          <FolderInputSettings
            schema={nodeType.configSchema as FieldSchema}
            value={node.config}
            onChange={(config, key) => onChange({ config }, key)}
            errorsFor={fieldErrors}
            readOnly={readOnly}
            canTest={canTest}
            workflowId={workflowId}
            published={published}
          />
        ) : nodeType && node.type === 'map-fields' ? (
          <MapFieldsSettings
            schema={nodeType.configSchema as FieldSchema}
            templateFields={templateFields}
            templateName={templateName}
            value={node.config}
            onChange={(config, key) => onChange({ config }, key)}
            ctx={{ templates, dataPaths, errorsFor: fieldErrors, readOnly }}
          />
        ) : nodeType ? (
          <SchemaForm
            schema={nodeType.configSchema as FieldSchema}
            value={node.config}
            onChange={(config, key) => onChange({ config }, key)}
            ctx={{ templates, dataPaths, errorsFor: fieldErrors, readOnly }}
          />
        ) : (
          <p className="text-[12.5px] text-err">Unknown step type '{node.type}'. Delete it or replace it with a supported step.</p>
        )}
      </section>
      {!readOnly && (
        <div className="px-3.5 py-3.5">
          <Button size="sm" variant="ghost" className="text-err" onClick={onDelete}>
            <Trash2 /> Delete step
          </Button>
        </div>
      )}
    </div>
  )
}
