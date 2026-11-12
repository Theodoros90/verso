import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'

import { IfElseSettings } from './IfElseSettings'
import type { FieldSchema } from './SchemaForm'

const schema: FieldSchema = {
  type: 'object',
  properties: {
    conditions: {
      type: 'array',
      title: 'Conditions',
      maxItems: 3,
      items: {
        type: 'object',
        properties: {
          join: { type: 'string', enum: ['and', 'or'], 'x-enumLabels': ['AND', 'OR'] },
          field: { type: 'string' },
          op: { type: 'string', enum: ['equals', 'is-empty'], 'x-enumLabels': ['is', 'is empty'] },
          compare: { type: 'string', enum: ['value', 'field'], 'x-enumLabels': ['A value', 'A field of the data'] },
          value: { type: 'string' },
        },
      },
    },
    yesLabel: { type: 'string', title: 'Name of the Yes path' },
    noLabel: { type: 'string', title: 'Name of the No path' },
  },
}

function Harness({ onValue }: { onValue?: (v: Record<string, unknown>) => void }) {
  const [value, setValue] = useState<Record<string, unknown>>({
    conditions: [{ join: 'and', field: '', op: 'equals', compare: 'value', value: '' }],
    yesLabel: 'Yes',
    noLabel: 'No',
  })
  return (
    <IfElseSettings
      schema={schema}
      value={value}
      onChange={(next) => {
        setValue(next)
        onValue?.(next)
      }}
      ctx={{ templates: [], dataPaths: ['totals.gross', 'totals.net'], errorsFor: () => [], readOnly: false }}
    />
  )
}

describe('IfElseSettings', () => {
  it('picks the field, and a field to compare with, from a dropdown of the available data', async () => {
    const user = userEvent.setup()
    let latest: Record<string, unknown> = {}
    render(<Harness onValue={(v) => (latest = v)} />)
    const field = screen.getByRole('combobox', { name: 'Condition 1: field' })
    expect([...field.querySelectorAll('option')].map((o) => o.textContent)).toEqual(['Choose a field…', 'totals.gross', 'totals.net'])
    await user.selectOptions(field, 'totals.gross')
    expect((latest.conditions as { field: string }[])[0]!.field).toBe('totals.gross')

    expect(screen.getByRole('textbox', { name: 'Condition 1: value' })).toBeInTheDocument() // a typed value is plain text
    await user.selectOptions(screen.getByRole('combobox', { name: 'Condition 1: compare with' }), 'field')
    const other = screen.getByRole('combobox', { name: 'Condition 1: field to compare with' })
    expect([...other.querySelectorAll('option')].map((o) => o.textContent)).toContain('totals.net')
    await user.selectOptions(other, 'totals.net')
    expect((latest.conditions as { value: string }[])[0]!.value).toBe('totals.net')
  })

  it('adds rows joined by AND or OR, hides the value for emptiness checks, and removes rows', async () => {
    const user = userEvent.setup()
    let latest: Record<string, unknown> = {}
    render(<Harness onValue={(v) => (latest = v)} />)

    expect(screen.queryByRole('combobox', { name: /joined to the row above/ })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Add condition' }))
    const join = screen.getByRole('combobox', { name: 'Condition 2: joined to the row above by' })
    expect(join).toHaveValue('and')
    await user.selectOptions(join, 'or')
    expect((latest.conditions as { join: string }[])[1]!.join).toBe('or')

    await user.selectOptions(screen.getByRole('combobox', { name: 'Condition 2: operator' }), 'is-empty')
    expect(screen.queryByRole('textbox', { name: 'Condition 2: value' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Add condition' }))
    expect(screen.getByRole('button', { name: 'Add condition' })).toBeDisabled() // the schema's maxItems

    await user.click(screen.getByRole('button', { name: 'Remove condition 2' }))
    expect(screen.getAllByRole('combobox', { name: /: operator/ })).toHaveLength(2)
  })
})
