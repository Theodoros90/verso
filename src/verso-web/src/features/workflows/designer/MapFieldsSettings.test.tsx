import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'

import { MapFieldsSettings, unmappedDestinations } from './MapFieldsSettings'
import type { FieldSchema, FormContext } from './SchemaForm'

const REQUIRED = ['invoice.number', 'customer.name', 'totals.gross'] // the chosen template's fields

const schema: FieldSchema = {
  type: 'object',
  required: ['rows'],
  properties: {
    rows: {
      type: 'array',
      title: 'Mappings',
      items: {
        type: 'object',
        properties: {
          from: { type: 'string', title: 'Source field', 'x-widget': 'mono' },
          to: { type: 'string', title: 'Destination field', 'x-widget': 'data-path' },
          transform: { type: 'string', title: 'Transform', enum: ['as-is', 'trim'], 'x-enumLabels': ['As is', 'Trim'] },
        },
      },
    },
  },
}

const ctx = (readOnly = false): FormContext => ({ templates: [], dataPaths: ['invoice.number'], errorsFor: () => [], readOnly })

describe('unmappedDestinations', () => {
  it('counts only rows with both a source and a destination', () => {
    const rows = [
      { from: 'no', to: 'invoice.number', transform: 'as-is' },
      { from: '', to: 'customer.name', transform: 'as-is' },
    ]
    expect(unmappedDestinations({ rows }, REQUIRED)).toEqual(['customer.name', 'totals.gross'])
  })
})

describe('MapFieldsSettings', () => {
  it('lists the unmapped template fields and adds a row for the one clicked, leaving the source empty', () => {
    const onChange = vi.fn()
    render(<MapFieldsSettings schema={schema} templateFields={REQUIRED} templateName="Statement" value={{ rows: [{ from: 'no', to: 'invoice.number', transform: 'as-is' }] }} onChange={onChange} ctx={ctx()} />)

    const panel = screen.getByRole('region', { name: 'Required template fields' })
    expect(within(panel).queryByRole('button', { name: /invoice\.number/ })).toBeNull()
    fireEvent.click(within(panel).getByRole('button', { name: /customer\.name/ }))

    expect(onChange).toHaveBeenCalledWith(
      { rows: [{ from: 'no', to: 'invoice.number', transform: 'as-is' }, { from: '', to: 'customer.name', transform: 'as-is' }] },
      'rows',
    )
  })

  it('says so when every required field is mapped', () => {
    const rows = REQUIRED.map((to) => ({ from: `src.${to}`, to, transform: 'as-is' }))
    render(<MapFieldsSettings schema={schema} templateFields={REQUIRED} templateName="Statement" value={{ rows }} onChange={vi.fn()} ctx={ctx()} />)
    expect(screen.getByText(/All 3 fields of .Statement. are mapped/)).toBeTruthy()
  })

  it('asks for a template when the workflow has none, and notes an empty data model', () => {
    const { rerender } = render(<MapFieldsSettings schema={schema} value={{ rows: [] }} onChange={vi.fn()} ctx={ctx()} />)
    expect(screen.getByText(/Choose a template in a Generate PDF step/)).toBeTruthy()
    expect(within(screen.getByRole('region', { name: 'Required template fields' })).queryAllByRole('button')).toHaveLength(0)
    rerender(<MapFieldsSettings schema={schema} value={{ rows: [] }} onChange={vi.fn()} ctx={ctx()} templateFields={[]} templateName="Statement" />)
    expect(screen.getByText(/no fields in its data model/)).toBeTruthy()
  })

  it('offers no suggestions to add when the workflow is read-only', () => {
    render(<MapFieldsSettings schema={schema} templateFields={REQUIRED} templateName="Statement" value={{ rows: [] }} onChange={vi.fn()} ctx={ctx(true)} />)
    const panel = screen.getByRole('region', { name: 'Required template fields' })
    for (const b of within(panel).getAllByRole('button')) expect((b as HTMLButtonElement).disabled).toBe(true)
  })
})
