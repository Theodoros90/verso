import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'

import type { FieldSchema } from './SchemaForm'
import { SwitchSettings } from './SwitchSettings'

const schema: FieldSchema = {
  type: 'object',
  properties: {
    field: { type: 'string', title: 'Field' },
    op: { type: 'string', enum: ['equals', 'contains'], 'x-enumLabels': ['is', 'contains'] },
    branches: {
      type: 'array',
      maxItems: 3,
      items: {
        type: 'object',
        properties: { compare: { type: 'string', enum: ['value', 'field'], 'x-enumLabels': ['A value', 'A field of the data'] } },
      },
    },
    other: { type: 'boolean', title: 'Add an Other path' },
  },
}

function Harness({ onValue }: { onValue?: (v: Record<string, unknown>) => void }) {
  const [value, setValue] = useState<Record<string, unknown>>({
    field: '',
    op: 'equals',
    other: true,
    branches: [{ id: 'branch-1', label: 'Branch 1', compare: 'value', value: '' }],
  })
  return (
    <SwitchSettings
      schema={schema}
      value={value}
      onChange={(next) => {
        setValue(next)
        onValue?.(next)
      }}
      ctx={{ templates: [], dataPaths: ['customer.deliveryPreference', 'billing.preference'], errorsFor: () => [], readOnly: false }}
    />
  )
}

describe('SwitchSettings', () => {
  it('picks the field, and a field to compare with, from a dropdown of the available data', async () => {
    const user = userEvent.setup()
    let latest: Record<string, unknown> = {}
    render(<Harness onValue={(v) => (latest = v)} />)
    const field = screen.getByRole('combobox', { name: 'Field *' })
    expect([...field.querySelectorAll('option')].map((o) => o.textContent)).toEqual(['Choose a field…', 'customer.deliveryPreference', 'billing.preference'])
    await user.selectOptions(field, 'customer.deliveryPreference')
    expect(latest.field).toBe('customer.deliveryPreference')

    expect(screen.getByRole('textbox', { name: 'Branch 1: value' })).toBeInTheDocument() // a typed value is plain text
    await user.selectOptions(screen.getByRole('combobox', { name: 'Branch 1: compare with' }), 'field')
    await user.selectOptions(screen.getByRole('combobox', { name: 'Branch 1: field to compare with' }), 'billing.preference')
    expect((latest.branches as { value: string }[])[0]!.value).toBe('billing.preference')
  })

  it('adds branches with their own output id, keeps the id when renamed, and removes them', async () => {
    const user = userEvent.setup()
    let latest: Record<string, unknown> = {}
    render(<Harness onValue={(v) => (latest = v)} />)

    await user.click(screen.getByRole('button', { name: 'Add branch' }))
    await user.click(screen.getByRole('button', { name: 'Add branch' }))
    expect(screen.getByRole('button', { name: 'Add branch' })).toBeDisabled() // the schema limits the number of branches
    expect((latest.branches as { id: string }[]).map((b) => b.id)).toEqual(['branch-1', 'branch-2', 'branch-3'])

    const name = screen.getByRole('textbox', { name: 'Branch 2: name' })
    await user.clear(name)
    await user.type(name, 'Print')
    expect((latest.branches as { id: string; label: string }[])[1]).toMatchObject({ id: 'branch-2', label: 'Print' })

    await user.click(screen.getByRole('button', { name: 'Remove branch 1' }))
    expect((latest.branches as { id: string }[]).map((b) => b.id)).toEqual(['branch-2', 'branch-3'])
    await user.click(screen.getByRole('button', { name: 'Add branch' }))
    expect((latest.branches as { id: string }[]).map((b) => b.id)).toEqual(['branch-2', 'branch-3', 'branch-4']) // a new branch never takes an id already in use
  })

  it('turns Other on and off', async () => {
    const user = userEvent.setup()
    let latest: Record<string, unknown> = {}
    render(<Harness onValue={(v) => (latest = v)} />)
    await user.click(screen.getByRole('switch'))
    expect(latest.other).toBe(false)
  })
})
