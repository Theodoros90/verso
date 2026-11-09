import { describe, expect, it } from 'vitest'

import type { NodeTypeDto } from '@/lib/api/generated'

import { addNode, asDefinition, connect, moveNode, nextPosition, removeEdges, removeNodes, updateNode, type WorkflowDefinition } from './definition'

const validateType: NodeTypeDto = {
  type: 'validate',
  label: 'Validate data',
  category: 'process',
  description: '',
  inputs: [{ id: 'in', label: 'In', required: true }],
  outputs: [
    { id: 'valid', label: 'Valid', required: true },
    { id: 'invalid', label: 'Invalid', required: false },
  ],
  configSchema: {},
  defaults: { required: [], types: [] },
}

const start: WorkflowDefinition = {
  nodes: [{ id: 'trigger', type: 'api-trigger', title: 'API trigger', position: { x: 0, y: 0 }, config: {} }],
  edges: [],
}

describe('workflow definition edits', () => {
  it('adds a node with its defaults, a unique id and rounded position', () => {
    const a = addNode(start, validateType, { x: 280.4, y: 10.6 })
    const b = addNode(a.def, validateType, { x: 560, y: 0 })

    expect(a.id).toBe('validate')
    expect(b.id).toBe('validate-2')
    expect(a.def.nodes[1]).toEqual({ id: 'validate', type: 'validate', title: 'Validate data', position: { x: 280, y: 11 }, config: { required: [], types: [] } })
    // Defaults are copied, not shared.
    ;(b.def.nodes[1]!.config.required as string[]).push('x')
    expect(validateType.defaults).toEqual({ required: [], types: [] })
  })

  it('connects an output once: reconnecting the same output replaces the edge', () => {
    let d = addNode(start, validateType, { x: 280, y: 0 }).def
    d = addNode(d, validateType, { x: 560, y: 0 }).def
    d = connect(d, 'trigger', 'out', 'validate', 'in')
    d = connect(d, 'trigger', 'out', 'validate-2', 'in')

    expect(d.edges).toEqual([{ id: 'trigger-out', source: 'trigger', sourcePort: 'out', target: 'validate-2', targetPort: 'in' }])
    expect(connect(d, 'validate', 'valid', 'validate', 'in')).toBe(d) // no self-loops
  })

  it('removing a node removes its edges', () => {
    let d = addNode(start, validateType, { x: 280, y: 0 }).def
    d = connect(d, 'trigger', 'out', 'validate', 'in')

    const r = removeNodes(d, ['validate'])
    expect(r.nodes.map((n) => n.id)).toEqual(['trigger'])
    expect(r.edges).toEqual([])
    expect(removeEdges(d, ['trigger-out']).edges).toEqual([])
  })

  it('leaves untouched parts identical (round-trip) and returns the same object for no-op moves', () => {
    const d = addNode(start, validateType, { x: 280, y: 0 }).def
    const moved = moveNode(d, 'validate', { x: 300, y: 0 })

    expect(moved.nodes[0]).toBe(d.nodes[0])
    expect(moveNode(moved, 'validate', { x: 300.2, y: 0 })).toBe(moved)
    expect(updateNode(d, 'validate', { title: 'Check invoice' }).nodes[1]!.title).toBe('Check invoice')
  })

  it('places new nodes to the right of the selected (or right-most) node', () => {
    const d = addNode(start, validateType, { x: 280, y: 40 }).def
    expect(nextPosition(d, 'trigger')).toEqual({ x: 280, y: 0 })
    expect(nextPosition(d)).toEqual({ x: 560, y: 40 })
    expect(nextPosition(asDefinition(null))).toEqual({ x: 0, y: 0 })
  })
})
