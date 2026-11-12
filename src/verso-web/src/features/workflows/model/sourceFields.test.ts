import { describe, expect, it } from 'vitest'

import type { DefNode, WorkflowDefinition } from './definition'
import { sourceFields } from './sourceFields'

const node = (id: string, type: string, config: Record<string, unknown> = {}): DefNode => ({ id, type, title: id, position: { x: 0, y: 0 }, config })

/** A straight line of steps: each one's first output leads to the next. */
function line(...nodes: DefNode[]): WorkflowDefinition {
  return {
    nodes,
    edges: nodes.slice(1).map((n, i) => ({ id: `e${i}`, source: nodes[i]!.id, sourcePort: 'out', target: n.id, targetPort: 'in' })),
  }
}

const trigger = node('t', 'api-trigger', { samplePayload: '{ "invoice": { "number": "A-1" }, "totals": { "gross": 10 }, "lines": [ { "qty": 1 } ] }' })

describe('sourceFields', () => {
  it('offers the trigger sample, and nothing for steps that have no source', () => {
    expect(sourceFields(line(trigger, node('if', 'if-else')), 'if')).toEqual(['invoice.number', 'totals.gross']) // not inside lists
    expect(sourceFields(line(node('t', 'manual-trigger'), node('if', 'if-else')), 'if')).toEqual([])
    expect(sourceFields(line(trigger), 't')).toEqual([]) // a trigger receives nothing
    expect(sourceFields(line(trigger), 'missing')).toEqual([])
  })

  it('adds what Map fields and Transform write, and drops what Transform removes', () => {
    const map = node('m', 'map-fields', { rows: [{ from: 'invoice.number', to: 'doc.id', transform: 'as-is' }, { from: '', to: '' }] })
    const transform = node('x', 'transform', {
      rules: [
        { target: 'totals.rounded', operation: 'round', a: 'totals.gross' },
        { target: 'invoice', operation: 'remove' },
      ],
    })
    expect(sourceFields(line(trigger, map, transform, node('sw', 'switch')), 'sw')).toEqual(['doc.id', 'totals.gross', 'totals.rounded'])
  })

  it('adds the fields of a Merge lookup table, under the field it writes into', () => {
    const lookup = JSON.stringify([{ id: 'C-1', name: 'A', address: { city: 'X' } }, { id: 'C-2', vip: true }])
    const into = node('mg', 'merge-data', { key: 'customer.id', lookupKey: 'id', lookup, into: 'customer', source: '' })
    expect(sourceFields(line(trigger, into, node('if', 'if-else')), 'if')).toEqual([
      'customer.address.city', 'customer.id', 'customer.name', 'customer.vip', 'invoice.number', 'totals.gross',
    ])
    const onList = node('mg', 'merge-data', { lookup, source: 'lines' })
    expect(sourceFields(line(trigger, onList, node('if', 'if-else')), 'if')).toEqual(['invoice.number', 'totals.gross'])
  })

  it('forgets everything known before a Parse step, whose result is unknown here', () => {
    expect(sourceFields(line(trigger, node('p', 'parse'), node('if', 'if-else')), 'if')).toEqual([])
    const map = node('m', 'map-fields', { rows: [{ from: 'a', to: 'after.parse', transform: 'as-is' }] })
    expect(sourceFields(line(trigger, node('p', 'parse'), map, node('if', 'if-else')), 'if')).toEqual(['after.parse'])
  })

  it('does not hang on a loop in the draft, and unites paths where branches meet', () => {
    const loop: WorkflowDefinition = {
      nodes: [trigger, node('a', 'map-fields', { rows: [{ from: 'x', to: 'one', transform: 'as-is' }] }), node('b', 'if-else')],
      edges: [
        { id: '1', source: 't', sourcePort: 'out', target: 'a', targetPort: 'in' },
        { id: '2', source: 'a', sourcePort: 'success', target: 'b', targetPort: 'in' },
        { id: '3', source: 'b', sourcePort: 'yes', target: 'a', targetPort: 'in' },
      ],
    }
    expect(sourceFields(loop, 'b')).toEqual(['invoice.number', 'one', 'totals.gross'])

    const meet: WorkflowDefinition = {
      nodes: [trigger, node('l', 'map-fields', { rows: [{ from: 'x', to: 'left', transform: 'as-is' }] }), node('r', 'map-fields', { rows: [{ from: 'x', to: 'right', transform: 'as-is' }] }), node('j', 'if-else')],
      edges: [
        { id: '1', source: 't', sourcePort: 'out', target: 'l', targetPort: 'in' },
        { id: '2', source: 't', sourcePort: 'out2', target: 'r', targetPort: 'in' },
        { id: '3', source: 'l', sourcePort: 'success', target: 'j', targetPort: 'in' },
        { id: '4', source: 'r', sourcePort: 'success', target: 'j', targetPort: 'in' },
      ],
    }
    expect(sourceFields(meet, 'j')).toEqual(['invoice.number', 'left', 'right', 'totals.gross'])
  })
})

describe('sourceFields inside a For each', () => {
  const sample = node('t', 'api-trigger', {
    samplePayload: '{ "invoice": { "number": "A-1" }, "lines": [ { "description": "Fee", "amount": 5 }, { "description": "Tax", "amount": 1 } ], "tags": [ "a", "b" ] }',
  })
  const graph = (config: Record<string, unknown>): WorkflowDefinition => ({
    nodes: [sample, node('loop', 'for-each', config), node('body', 'if-else'), node('after', 'archive')],
    edges: [
      { id: 'e0', source: 't', sourcePort: 'out', target: 'loop', targetPort: 'in' },
      { id: 'e1', source: 'loop', sourcePort: 'each', target: 'body', targetPort: 'in' },
      { id: 'e2', source: 'loop', sourcePort: 'empty', target: 'after', targetPort: 'in' },
      { id: 'e3', source: 'body', sourcePort: 'yes', target: 'loop', targetPort: 'in' }, // repeat
    ],
  })

  it('adds the item fields to the record (each), only inside the loop', () => {
    const d = graph({ field: 'lines', mode: 'each', item: 'line' })
    expect(sourceFields(d, 'body')).toEqual(['invoice.number', 'line.amount', 'line.description'])
    expect(sourceFields(d, 'after')).toEqual(['invoice.number']) // the empty path has no item
    expect(sourceFields(d, 'loop')).toEqual(['invoice.number']) // the repeat connection adds nothing
  })

  it('replaces the record with the item (split), and names a plain value', () => {
    expect(sourceFields(graph({ field: 'lines', mode: 'split', item: 'line' }), 'body')).toEqual(['amount', 'description'])
    expect(sourceFields(graph({ field: 'tags', mode: 'split', item: 'tag' }), 'body')).toEqual(['tag'])
    expect(sourceFields(graph({ field: 'tags', mode: 'each' }), 'body')).toEqual(['invoice.number', 'item']) // item name defaults to "item"
  })

  it('adds nothing when the list is not in the sample', () => {
    expect(sourceFields(graph({ field: 'nothing.here', mode: 'each', item: 'x' }), 'body')).toEqual(['invoice.number'])
  })
})
