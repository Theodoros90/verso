import { describe, expect, it } from 'vitest'

import type { NodeTypeDto } from '@/lib/api/generated'

import { addNode, asDefinition, connect, moveNode, nextPosition, outputPorts, removeEdges, removeNodes, switchPorts, updateNode, type WorkflowDefinition } from './definition'

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

describe('Switch outputs', () => {
  const branch = (id: string, label: string) => ({ id, label, compare: 'value', value: 'x' })
  const config = { field: 'a', op: 'equals', other: true, branches: [branch('email', 'Email'), branch('print', ' ')] }

  it('are the branches in order, then Other', () => {
    expect(switchPorts(config)).toEqual([
      { id: 'email', label: 'Email', required: false },
      { id: 'print', label: 'Untitled', required: false },
      { id: 'other', label: 'Other', required: false },
    ])
    expect(switchPorts({ ...config, other: false }).map((p) => p.id)).toEqual(['email', 'print'])
  })

  it('give no output to a blank, repeated or reserved id', () => {
    expect(switchPorts({ branches: [branch('a', 'A'), branch('a', 'B'), branch('other', 'C'), branch('', 'D')] }).map((p) => p.id)).toEqual(['a'])
  })

  it('come from the settings for a Switch and from the catalog for other steps', () => {
    expect(outputPorts(validateType, { type: 'switch', config }).map((p) => p.id)).toEqual(['email', 'print', 'other'])
    expect(outputPorts(validateType, { type: 'validate', config: {} }).map((p) => p.id)).toEqual(['valid', 'invalid'])
    expect(outputPorts(undefined, { type: 'unknown', config: {} })).toEqual([])
  })

  it('take their connections with them when a branch or Other is removed', () => {
    const def: WorkflowDefinition = {
      nodes: [
        { id: 'route', type: 'switch', title: 'Switch', position: { x: 0, y: 0 }, config },
        { id: 'a', type: 'validate', title: 'A', position: { x: 0, y: 0 }, config: {} },
      ],
      edges: [
        { id: 'e1', source: 'route', sourcePort: 'email', target: 'a', targetPort: 'in' },
        { id: 'e2', source: 'route', sourcePort: 'other', target: 'a', targetPort: 'in' },
        { id: 'e3', source: 'a', sourcePort: 'valid', target: 'route', targetPort: 'in' },
      ],
    }

    const noEmail = updateNode(def, 'route', { config: { ...config, branches: [branch('print', 'Print')] } })
    expect(noEmail.edges.map((e) => e.id)).toEqual(['e2', 'e3'])

    const renamed = updateNode(def, 'route', { config: { ...config, branches: [branch('email', 'Mail'), branch('print', 'P')] } })
    expect(renamed.edges.map((e) => e.id)).toEqual(['e1', 'e2', 'e3']) // a new name keeps the connection

    const noOther = updateNode(def, 'route', { config: { ...config, other: false } })
    expect(noOther.edges.map((e) => e.id)).toEqual(['e1', 'e3'])

    expect(updateNode(def, 'route', { title: 'Route' }).edges).toEqual(def.edges) // a title change touches nothing
  })
})
