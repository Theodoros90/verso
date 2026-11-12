import { describe, expect, it } from 'vitest'

import type { DefNode, WorkflowDefinition } from './definition'
import { connect } from './definition'
import { backEdges, canConnect, enclosingLoop, loopBody } from './loops'

const node = (id: string, type: string): DefNode => ({ id, type, title: id, position: { x: 0, y: 0 }, config: {} })
const edge = (source: string, sourcePort: string, target: string) => ({ id: `${source}-${sourcePort}`, source, sourcePort, target, targetPort: 'in' })

/** trigger -> loop (for each) -> each: map -> pdf */
const base: WorkflowDefinition = {
  nodes: [node('t', 'api-trigger'), node('loop', 'for-each'), node('map', 'map-fields'), node('pdf', 'generate-pdf')],
  edges: [edge('t', 'out', 'loop'), edge('loop', 'each', 'map'), edge('map', 'success', 'pdf')],
}

describe('loops', () => {
  it('allows the last step of the loop to connect back into the For each (once)', () => {
    expect(canConnect(base, 'pdf', 'success', 'loop', 'in')).toEqual({ ok: true, loop: true })
    const looped = connect(base, 'pdf', 'success', 'loop', 'in')
    expect(looped.edges).toHaveLength(4)
    expect(backEdges(looped).map((e) => e.source)).toEqual(['pdf'])

    // a second step may not loop back into the same For each
    expect(canConnect(looped, 'map', 'failed', 'loop', 'in').ok).toBe(false)
  })

  it('refuses every other loop, with the prototype message', () => {
    expect(canConnect(base, 'pdf', 'success', 'map', 'in')).toEqual({ ok: false, reason: expect.stringContaining('Only a For each step can loop') })
    expect(connect(base, 'pdf', 'success', 'map', 'in')).toBe(base)
    expect(canConnect(base, 'map', 'success', 'map', 'in').ok).toBe(false)
  })

  it('refuses loop-backs from a For each, If / else or Switch', () => {
    for (const type of ['if-else', 'switch', 'for-each']) {
      const d: WorkflowDefinition = { nodes: [...base.nodes, node('x', type)], edges: [...base.edges, edge('pdf', 'success', 'x')] }
      const port = type === 'for-each' ? 'each' : type === 'if-else' ? 'yes' : 'a'
      expect(canConnect(d, 'x', port, 'loop', 'in').ok, type).toBe(false)
    }
  })

  it('finds the steps of a loop, with or without the loop-back', () => {
    expect([...loopBody(base, 'loop')].sort()).toEqual(['map', 'pdf'])
    const looped = connect(base, 'pdf', 'success', 'loop', 'in')
    expect([...loopBody(looped, 'loop')].sort()).toEqual(['map', 'pdf'])
    expect(enclosingLoop(looped, 'pdf')).toBe('loop')
    expect(enclosingLoop(looped, 't')).toBeUndefined()
  })

  it('refuses a For each inside another For each loop', () => {
    const d: WorkflowDefinition = { nodes: [...base.nodes, node('inner', 'for-each')], edges: base.edges }
    expect(canConnect(d, 'map', 'failed', 'inner', 'in')).toEqual({ ok: false, reason: expect.stringContaining('inside the loop of another For each') })
    // the empty path is outside the loop
    expect(canConnect(d, 'loop', 'empty', 'inner', 'in').ok).toBe(true)
  })

  it('refuses a step that is reached from inside the loop and from outside it', () => {
    const d: WorkflowDefinition = { nodes: [...base.nodes, node('arch', 'archive')], edges: [...base.edges, edge('loop', 'empty', 'arch')] }
    expect(canConnect(d, 'pdf', 'success', 'arch', 'in')).toEqual({ ok: false, reason: expect.stringContaining('reached from outside') })
    // a separate step for each path is fine
    expect(canConnect({ ...d, nodes: [...d.nodes, node('arch2', 'archive')] }, 'pdf', 'success', 'arch2', 'in').ok).toBe(true)
  })
})
