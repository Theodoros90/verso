/**
 * Loops (For each). A workflow runs in one direction, with one exception: the last step of a For each's loop may connect back into
 * the For each itself ("repeat"). Everything else that would close a circle is refused. Mirrors the server's graph checks
 * (`graph.cycle`, `loop.nested`, `loop.shared_step`); the server validates again when the workflow is saved.
 */
import type { DefEdge, WorkflowDefinition } from './definition'

export const FOR_EACH = 'for-each'
const NO_LOOP_BACK_FROM = new Set([FOR_EACH, 'if-else', 'switch'])

/** True when <paramref name="to"/> can be reached from <paramref name="from"/> by following connections. */
export function reaches(edges: DefEdge[], from: string, to: string): boolean {
  const seen = new Set<string>()
  const stack = [from]
  while (stack.length) {
    const id = stack.pop()!
    if (id === to) return true
    if (seen.has(id)) continue
    seen.add(id)
    for (const e of edges) if (e.source === id) stack.push(e.target)
  }
  return false
}

/** Connections that return into a For each from further along its own path: the loop-back ("repeat") connections. */
export function backEdges(def: WorkflowDefinition): DefEdge[] {
  const forEach = new Set(def.nodes.filter((n) => n.type === FOR_EACH).map((n) => n.id))
  return def.edges.filter((e) => forEach.has(e.target) && e.targetPort === 'in' && reaches(def.edges, e.target, e.source))
}

/** The steps of a For each's loop: everything reachable from its 'each' output without coming back into the For each. */
export function loopBody(def: WorkflowDefinition, forEachId: string): Set<string> {
  const body = new Set<string>()
  const stack = def.edges.filter((e) => e.source === forEachId && e.sourcePort === 'each').map((e) => e.target)
  while (stack.length) {
    const id = stack.pop()!
    if (id === forEachId || body.has(id)) continue
    body.add(id)
    for (const e of def.edges) if (e.source === id) stack.push(e.target)
  }
  return body
}

/** The For each whose loop contains the step, if any. */
export function enclosingLoop(def: WorkflowDefinition, nodeId: string): string | undefined {
  return def.nodes.find((n) => n.type === FOR_EACH && loopBody(def, n.id).has(nodeId))?.id
}


/** Loop rules a definition breaks, as "<for each>:<problem>" keys. */
function loopProblems(def: WorkflowDefinition): Set<string> {
  const problems = new Set<string>()
  const types = new Map(def.nodes.map((n) => [n.id, n.type]))
  for (const loop of def.nodes.filter((n) => n.type === FOR_EACH)) {
    const body = loopBody(def, loop.id)
    for (const id of body) if (types.get(id) === FOR_EACH) problems.add(`${loop.id}:nested`)
    for (const e of def.edges) {
      if (body.has(e.target) && !body.has(e.source) && !(e.source === loop.id && e.sourcePort === 'each')) problems.add(`${loop.id}:shared`)
    }
  }
  return problems
}

export type ConnectCheck = { ok: true; loop?: boolean } | { ok: false; reason: string }

/** Whether an output may be connected to an input (the output's existing connection, if any, is replaced). */
export function canConnect(def: WorkflowDefinition, source: string, sourcePort: string, target: string, targetPort: string): ConnectCheck {
  const a = def.nodes.find((n) => n.id === source)
  const b = def.nodes.find((n) => n.id === target)
  if (!a || !b) return { ok: false, reason: 'Both steps must exist.' }
  if (source === target) return { ok: false, reason: 'A step can’t connect to itself.' }

  const kept = def.edges.filter((e) => !(e.source === source && e.sourcePort === sourcePort))
  const without: WorkflowDefinition = { ...def, edges: kept }
  const next: WorkflowDefinition = { ...def, edges: [...kept, { id: '_new', source, sourcePort, target, targetPort }] }

  let loop = false
  if (reaches(kept, target, source)) {
    const otherLoopBack = backEdges(without).some((e) => e.target === b.id)
    if (b.type === FOR_EACH && targetPort === 'in' && !NO_LOOP_BACK_FROM.has(a.type) && !otherLoopBack) loop = true
    else
      return {
        ok: false,
        reason: `That would create a loop back to ${a.title}. Only a For each step can loop — connect the last step of the loop back into the For each itself.`,
      }
  }

  const before = loopProblems(without)
  for (const p of loopProblems(next)) {
    if (before.has(p)) continue
    return {
      ok: false,
      reason: p.endsWith(':nested')
        ? 'A For each can’t sit inside the loop of another For each.'
        : 'A step inside a loop can’t also be reached from outside it. Keep the loop’s steps on their own path.',
    }
  }
  return loop ? { ok: true, loop } : { ok: true }
}
