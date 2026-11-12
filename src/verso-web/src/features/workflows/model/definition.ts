/**
 * Workflow definition (plan §4) and pure edits on it. The definition is stored exactly as edited, so untouched
 * nodes and edges keep their identity and content (reload round-trips exactly). React Flow is only a view of it.
 */
import type { NodeTypeDto } from '@/lib/api/generated'

import { canConnect } from './loops'

export interface DefNode {
  id: string
  type: string
  title: string
  position: { x: number; y: number }
  config: Record<string, unknown>
}

export interface DefEdge {
  id: string
  source: string
  sourcePort: string
  target: string
  targetPort: string
}

export interface WorkflowDefinition {
  nodes: DefNode[]
  edges: DefEdge[]
}

export function asDefinition(value: unknown): WorkflowDefinition {
  const v = value as Partial<WorkflowDefinition> | null
  return { nodes: Array.isArray(v?.nodes) ? v.nodes : [], edges: Array.isArray(v?.edges) ? v.edges : [] }
}

function uniqueId(taken: Set<string>, base: string): string {
  const stem = base.replace(/[^A-Za-z0-9_-]/g, '-').slice(0, 56) || 'node'
  if (!taken.has(stem)) return stem
  for (let n = 2; ; n++) if (!taken.has(`${stem}-${n}`)) return `${stem}-${n}`
}

const round = (v: number) => Math.round(v)

/** Adds a node of a catalog type with its default settings. */
export function addNode(def: WorkflowDefinition, type: NodeTypeDto, position: { x: number; y: number }): { def: WorkflowDefinition; id: string } {
  const id = uniqueId(new Set(def.nodes.map((n) => n.id)), type.type)
  const node: DefNode = {
    id,
    type: type.type,
    title: type.label,
    position: { x: round(position.x), y: round(position.y) },
    config: structuredClone((type.defaults ?? {}) as Record<string, unknown>),
  }
  return { def: { ...def, nodes: [...def.nodes, node] }, id }
}

export function moveNode(def: WorkflowDefinition, id: string, position: { x: number; y: number }): WorkflowDefinition {
  const x = round(position.x)
  const y = round(position.y)
  let changed = false
  const nodes = def.nodes.map((n) => {
    if (n.id !== id || (n.position.x === x && n.position.y === y)) return n
    changed = true
    return { ...n, position: { x, y } }
  })
  return changed ? { ...def, nodes } : def
}

export function removeNodes(def: WorkflowDefinition, ids: string[]): WorkflowDefinition {
  if (ids.length === 0) return def
  const set = new Set(ids)
  return { nodes: def.nodes.filter((n) => !set.has(n.id)), edges: def.edges.filter((e) => !set.has(e.source) && !set.has(e.target)) }
}

export function removeEdges(def: WorkflowDefinition, ids: string[]): WorkflowDefinition {
  if (ids.length === 0) return def
  const set = new Set(ids)
  return { ...def, edges: def.edges.filter((e) => !set.has(e.id)) }
}

/**
 * Connects an output to an input. An output leads to one next step, so an existing edge from it is replaced. A connection that
 * breaks the loop rules (see loops.ts) is refused: the definition comes back unchanged.
 */
export function connect(def: WorkflowDefinition, source: string, sourcePort: string, target: string, targetPort: string): WorkflowDefinition {
  if (source === target || !canConnect(def, source, sourcePort, target, targetPort).ok) return def
  const kept = def.edges.filter((e) => !(e.source === source && e.sourcePort === sourcePort))
  const id = uniqueId(new Set(kept.map((e) => e.id)), `${source}-${sourcePort}`)
  return { ...def, edges: [...kept, { id, source, sourcePort, target, targetPort }] }
}

export interface OutputPort {
  id: string
  label: string
  required: boolean
}

/**
 * A Switch's outputs are its branches (then "Other" when on), so they come from its settings; the catalog lists none.
 * Mirrors Switch.Ports on the server: a blank, repeated or reserved ("other") id gives no output.
 */
export function switchPorts(config: Record<string, unknown>): OutputPort[] {
  const ports: OutputPort[] = []
  const branches = Array.isArray(config.branches) ? (config.branches as Record<string, unknown>[]) : []
  for (const b of branches) {
    const id = typeof b?.id === 'string' ? b.id.trim() : ''
    if (id && id !== 'other' && !ports.some((p) => p.id === id)) {
      const label = typeof b.label === 'string' ? b.label.trim() : ''
      ports.push({ id, label: label || 'Untitled', required: false })
    }
  }
  if (config.other === true) ports.push({ id: 'other', label: 'Other', required: false })
  return ports
}

/** The outputs of one node: the catalog's, except for a Switch. */
export function outputPorts(nodeType: NodeTypeDto | undefined, node: Pick<DefNode, 'type' | 'config'>): OutputPort[] {
  return node.type === 'switch' ? switchPorts(node.config) : (nodeType?.outputs ?? [])
}

/** Changes a node. Connections from a Switch output that no longer exists (a removed branch, "Other" turned off) go with it. */
export function updateNode(def: WorkflowDefinition, id: string, patch: Partial<Pick<DefNode, 'title' | 'config'>>): WorkflowDefinition {
  const nodes = def.nodes.map((n) => (n.id === id ? { ...n, ...patch } : n))
  const updated = nodes.find((n) => n.id === id)
  if (!patch.config || updated?.type !== 'switch') return { ...def, nodes }
  const ports = new Set(switchPorts(updated.config).map((p) => p.id))
  return { nodes, edges: def.edges.filter((e) => e.source !== id || ports.has(e.sourcePort)) }
}

/** Position for a new node: right of the selected (or right-most) node, else the origin. */
export function nextPosition(def: WorkflowDefinition, afterId?: string): { x: number; y: number } {
  const anchor = def.nodes.find((n) => n.id === afterId) ?? [...def.nodes].sort((a, b) => b.position.x - a.position.x)[0]
  return anchor ? { x: anchor.position.x + 280, y: anchor.position.y } : { x: 0, y: 0 }
}
