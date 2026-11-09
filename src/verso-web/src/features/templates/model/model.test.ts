import { describe, expect, it } from 'vitest'

// Shared synthetic fixtures (also used by the .NET renderer tests).
import layoutRaw from '../../../../../../tests/fixtures/invoice/layout.json?raw'
import sampleRaw from '../../../../../../tests/fixtures/invoice/sample.json?raw'

import { bindingsOf, parseText, usedPaths } from './bindings'
import { commitFrom, createHistory, push, redo, replace, undo } from './history'
import { DEFAULT_PAGE_SETTINGS, emptyLayout, type Layout, type PositionedElement } from './layout'
import {
  addElement,
  allElements,
  duplicateElement,
  findElement,
  moveBlock,
  nudge,
  removeElement,
  setBandHeight,
  updatePlacement,
  updateProps,
  updateStyle,
} from './ops'
import { formatValue, resolvePath } from './sampleValues'
import { arrayPaths, inferSchema, rowFields, scalarPaths, schemaFields } from './schema'
import { elementIdForServerPath, validateDesign } from './validate'

const invoiceLayout = JSON.parse(layoutRaw) as Layout
const sample = JSON.parse(sampleRaw) as Record<string, unknown>
const page = DEFAULT_PAGE_SETTINGS

describe('layout operations', () => {
  it('adds header elements inside the band with a unique id', () => {
    const a = addElement(emptyLayout(), page, 'header', 'text', { at: { xMm: 10, yMm: 5 } })
    const b = addElement(a.layout, page, 'header', 'text')
    expect(a.id).toBe('text')
    expect(b.id).toBe('text-2')
    expect(b.layout.header?.heightMm).toBe(40)
    expect(findElement(b.layout, 'text')?.element).toMatchObject({ xMm: 10, yMm: 5, widthMm: 60, heightMm: 10 })
  })

  it('stacks new header elements below existing ones when no position is given', () => {
    let l = addElement(emptyLayout(), page, 'header', 'text').layout // y 0, height 10
    l = addElement(l, page, 'header', 'field', { binding: 'invoice.number' }).layout
    l = addElement(l, page, 'header', 'box').layout // 24 mm: no room below (band 40) → back to the top
    expect(allElements(l).map((e) => (e.element as PositionedElement).yMm)).toEqual([0, 11, 0])
  })

  it('names a new data field after its binding and keeps it inside the band', () => {
    const { layout, id } = addElement(emptyLayout(), page, 'footer', 'field', { binding: 'invoice.number', at: { xMm: 500, yMm: 500 } })
    const el = findElement(layout, id)!.element as PositionedElement
    expect(id).toBe('number')
    expect(el.props).toEqual({ binding: 'invoice.number' })
    expect(el.yMm + el.heightMm).toBeLessThanOrEqual(12)
    expect(el.xMm + el.widthMm).toBeLessThanOrEqual(178)
  })

  it('refuses types a region does not allow', () => {
    expect(() => addElement(emptyLayout(), page, 'body', 'box')).toThrow()
    expect(() => addElement(emptyLayout(), page, 'header', 'table')).toThrow()
  })

  it('inserts body blocks at an index and reorders them', () => {
    let l = addElement(emptyLayout(), page, 'body', 'text').layout
    l = addElement(l, page, 'body', 'divider').layout
    l = addElement(l, page, 'body', 'signature', { index: 0 }).layout
    expect(l.body!.blocks.map((b) => b.id)).toEqual(['signature', 'text', 'divider'])
    l = moveBlock(l, 'signature', 2)
    expect(l.body!.blocks.map((b) => b.id)).toEqual(['text', 'divider', 'signature'])
  })

  it('removes optional props and empty styles instead of storing undefined', () => {
    let l = addElement(emptyLayout(), page, 'body', 'field', { binding: 'a.b' }).layout
    l = updateProps(l, 'b', { filter: 'money' })
    l = updateStyle(l, 'b', { bold: true })
    l = updateProps(l, 'b', { filter: undefined })
    l = updateStyle(l, 'b', { bold: undefined })
    expect(JSON.parse(JSON.stringify(l.body!.blocks[0]))).toEqual({ id: 'b', type: 'field', props: { binding: 'a.b' } })
  })

  it('nudges within the band and rounds to 0.1 mm', () => {
    let l = addElement(emptyLayout(), page, 'header', 'field', { binding: 'x' }).layout
    l = nudge(l, page, 'x', 1.234, 100)
    expect(findElement(l, 'x')!.element).toMatchObject({ xMm: 1.2, yMm: 34 })
  })

  it('duplicates next to the original and deletes', () => {
    const d = duplicateElement(invoiceLayout, 'net')
    expect(d.id).toBe('net-copy')
    expect(d.layout.body!.blocks.map((b) => b.id).slice(1, 3)).toEqual(['net', 'net-copy'])
    expect(findElement(removeElement(d.layout, 'net-copy'), 'net-copy')).toBeUndefined()
  })

  it('never mutates its input', () => {
    const before = JSON.stringify(invoiceLayout)
    updatePlacement(invoiceLayout, 'title', { xMm: 3 })
    setBandHeight(invoiceLayout, 'header', 80)
    removeElement(invoiceLayout, 'lines')
    expect(JSON.stringify(invoiceLayout)).toBe(before)
  })
})

describe('data schema', () => {
  const schema = inferSchema(sample)
  const fields = schemaFields(schema)

  it('infers a schema from sample data, skipping _comment keys and detecting dates and decimals', () => {
    expect(fields.map((f) => f.name)).toEqual(['issuer', 'invoice', 'customer', 'lines', 'totals', 'payment'])
    expect(scalarPaths(fields).find((f) => f.path === 'invoice.date')?.kind).toBe('date')
    // qty is 18450 in one row and 1 in others → integer; unitPrice mixes 0.2185 and 592.04 → number
    expect(rowFields(fields, 'lines').map((f) => `${f.path}:${f.kind}`)).toEqual([
      'description:string',
      'qty:integer',
      'unit:string',
      'unitPrice:number',
      'vatRate:integer',
      'amount:number',
    ])
    expect(arrayPaths(fields).map((f) => f.path)).toEqual(['lines'])
  })

  it('maps every binding of the invoice fixture against the inferred schema', () => {
    const issues = validateDesign(invoiceLayout, page, schema)
    expect(issues.filter((i) => i.severity === 'error')).toEqual([])
  })

  it('reports unmapped bindings as errors, including text tokens and table columns', () => {
    let l = updateProps(invoiceLayout, 'inv-po', { binding: 'invoice.purchaseOrder' })
    l = updateProps(l, 'payment', { text: 'Ref {{payment.ref}}' })
    const lines = findElement(l, 'lines')!.element
    if (lines.type !== 'table') throw new Error()
    l = updateProps(l, 'lines', { columns: [...lines.props.columns, { header: 'X', binding: 'nope' }] })
    const unmapped = validateDesign(l, page, schema).filter((i) => i.code === 'binding.unmapped')
    expect(unmapped.map((i) => i.elementId)).toEqual(['inv-po', 'lines', 'payment'])
    expect(unmapped[1]!.message).toContain('lines[].nope')
  })

  it('treats a new template with an empty schema as having every binding unmapped', () => {
    const { layout } = addElement(emptyLayout(), page, 'body', 'field', { binding: 'invoice.number' })
    expect(validateDesign(layout, page, { type: 'object' }).map((i) => i.code)).toEqual(['binding.unmapped'])
  })
})

describe('structural validation (mirrors LayoutParser)', () => {
  it('reports duplicate ids, malformed tokens, missing bindings, bad images and column widths', () => {
    const l: Layout = {
      layoutSchemaVersion: 1,
      body: {
        blocks: [
          { id: 'a', type: 'text', props: { text: 'Hi {{bad path}}' } },
          { id: 'a', type: 'field', props: { binding: '' } },
          { id: 'img', type: 'image', props: { src: 'https://example.com/x.png' } },
          { id: 't', type: 'table', props: { binding: 'lines', columns: [{ header: 'A', binding: 'x', widthPct: 70 }, { header: 'B', binding: 'y', widthPct: 40 }] } },
        ],
      },
    }
    const codes = validateDesign(l, page, inferSchema({ lines: [{ x: 1, y: 2 }] })).map((i) => `${i.elementId}:${i.code}`)
    expect(codes).toEqual(['a:token.malformed', 'a:id.duplicate', 'a:binding.required', 'img:image.format', 't:columns.width'])
  })

  it('warns when a header element is cut off by its band', () => {
    const l = setBandHeight(invoiceLayout, 'header', 30)
    const clipped = validateDesign(l, page, inferSchema(sample)).filter((i) => i.code === 'position.clipped')
    expect(clipped.every((i) => i.severity === 'warning')).toBe(true)
    expect(clipped.map((i) => i.elementId)).toContain('billto-box')
  })

  it('maps server error paths to element ids', () => {
    expect(elementIdForServerPath(invoiceLayout, 'layout.header.elements[2].props.text')).toBe('title')
    expect(elementIdForServerPath(invoiceLayout, 'layout.body.blocks[0].props.columns')).toBe('lines')
    expect(elementIdForServerPath(invoiceLayout, 'pageSettings.marginsMm.top')).toBeUndefined()
  })
})

describe('bindings', () => {
  it('parses text tokens like the server', () => {
    expect(parseText('Due {{ invoice.dueDate | date }}!{{x|nope}}')).toEqual([
      { kind: 'literal', text: 'Due ' },
      { kind: 'token', path: 'invoice.dueDate', filter: 'date', raw: '{{ invoice.dueDate | date }}' },
      { kind: 'literal', text: '!' },
      { kind: 'malformed', raw: '{{x|nope}}' },
    ])
  })

  it('collects absolute paths, with table columns under their array', () => {
    const paths = usedPaths(allElements(invoiceLayout).map((l) => l.element))
    expect(paths).toContain('lines.unitPrice')
    expect(paths).toContain('issuer.vatNumber')
    expect(bindingsOf(findElement(invoiceLayout, 'logo')!.element)).toEqual([])
  })
})

describe('sample values', () => {
  it('formats like the server filters', () => {
    expect(formatValue(5516.69, 'money')).toBe('€5,516.69')
    expect(formatValue(-12, 'money')).toBe('-€12.00')
    expect(formatValue('2026-11-02', 'date')).toBe('02 Nov 2026')
    expect(formatValue(18450, 'number')).toBe('18,450')
    expect(formatValue({}, undefined)).toBeUndefined()
    expect(resolvePath(sample, 'customer.name')).toBe('Kestrel Logistics Ltd')
    expect(resolvePath(sample, 'customer.missing')).toBeUndefined()
  })
})

describe('history', () => {
  it('undoes and redoes, and records a drag as one step', () => {
    let h = createHistory(1)
    h = push(h, 2)
    h = push(h, 3)
    h = undo(h)
    expect(h.present).toBe(2)
    h = redo(h)
    expect(h.present).toBe(3)

    const origin = h.present
    h = replace(h, 4)
    h = replace(h, 5)
    h = commitFrom(h, origin)
    expect(h.present).toBe(5)
    expect(undo(h).present).toBe(3)
    expect(push(h, 5)).toBe(h)
  })
})
