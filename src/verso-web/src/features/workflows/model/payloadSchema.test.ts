import { describe, expect, it } from 'vitest'

import { jsonError, jsonObjectProblem, parseJsonObject, schemaErrors } from './payloadSchema'

const schema = {
  type: 'object',
  required: ['invoice', 'totals'],
  properties: {
    invoice: { type: 'object', required: ['number'], properties: { number: { type: 'string' }, lines: { type: 'array' } } },
    totals: { type: 'object', properties: { gross: { type: 'number' }, count: { type: 'integer' } } },
  },
}

describe('payload schema checks', () => {
  it('accepts a sample that fits', () => {
    expect(schemaErrors(schema, { invoice: { number: 'INV-1', lines: [] }, totals: { gross: 10.5, count: 2 } })).toEqual([])
  })

  it('reports missing required fields with their full path', () => {
    expect(schemaErrors(schema, { invoice: {} })).toEqual(['totals is required but missing', 'invoice.number is required but missing'])
  })

  it('reports wrong types, including integers and arrays', () => {
    expect(schemaErrors(schema, { invoice: { number: 7, lines: {} }, totals: { gross: '10', count: 1.5 } })).toEqual([
      'invoice.number should be string, found number',
      'invoice.lines should be array, found object',
      'totals.gross should be number, found string',
      'totals.count should be integer, found number',
    ])
    expect(schemaErrors(schema, [1])).toEqual(['payload should be object, found array'])
  })

  it('ignores a schema that is not an object', () => {
    expect(schemaErrors('object', { a: 1 })).toEqual([])
  })

  it('tells JSON, JSON objects and blank text apart', () => {
    expect(jsonError('')).toBeNull()
    expect(jsonError('{ "a": 1 }')).toBeNull()
    expect(jsonError('{ a: 1 }')).not.toBeNull()
    expect(parseJsonObject('[1]')).toBeUndefined()
    expect(parseJsonObject('{ "a": 1 }')).toEqual({ a: 1 })
    expect(jsonObjectProblem('')).toBeNull()
    expect(jsonObjectProblem('[1]')).toBe('Must be a JSON object.')
    expect(jsonObjectProblem('{')).toMatch(/^Not valid JSON/)
  })
})
