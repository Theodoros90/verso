import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'

import type { DefNode } from '../model/definition'
import { ForEachSettings, itemCount } from './ForEachSettings'
import { portLabel } from './NodeCard'
import type { FieldSchema, FormContext } from './SchemaForm'

const schema: FieldSchema = { type: 'object', properties: { field: { type: 'string', title: 'List field' } } }
const ctx = (dataPaths: string[] = []): FormContext => ({ templates: [], dataPaths, errorsFor: () => [], readOnly: false })

function Harness({ onValue }: { onValue?: (v: Record<string, unknown>) => void }) {
  const [value, setValue] = useState<Record<string, unknown>>({ field: '', mode: 'each', item: 'item', eachLabel: 'For each item', emptyLabel: 'No items' })
  return (
    <ForEachSettings
      schema={schema}
      value={value}
      onChange={(next) => {
        setValue(next)
        onValue?.(next)
      }}
      ctx={ctx(['invoice.lines', 'tags'])}
    />
  )
}

describe('ForEachSettings', () => {
  it('picks the list, the behaviour, the item name and the path names', async () => {
    const user = userEvent.setup()
    let latest: Record<string, unknown> = {}
    render(<Harness onValue={(v) => (latest = v)} />)

    await user.selectOptions(screen.getByRole('combobox', { name: 'List field *' }), 'invoice.lines')
    await user.selectOptions(screen.getByLabelText('Loop behaviour'), 'split')
    await user.clear(screen.getByLabelText('Item name'))
    await user.type(screen.getByLabelText('Item name'), 'line')
    await user.clear(screen.getByLabelText('Name for the empty path'))
    await user.type(screen.getByLabelText('Name for the empty path'), 'Nothing to do')

    expect(latest).toMatchObject({ field: 'invoice.lines', mode: 'split', item: 'line', emptyLabel: 'Nothing to do' })
    expect(screen.getByText(/each item into its own record/i)).toBeInTheDocument()
  })

  it('previews how often the loop runs from the sample', () => {
    const { rerender } = render(<ForEachSettings schema={schema} value={{ field: 'invoice.lines' }} sampleList={[{}, {}, {}]} onChange={() => {}} ctx={ctx()} />)
    expect(screen.getByText(/run 3 times — once per item/)).toBeInTheDocument()

    rerender(<ForEachSettings schema={schema} value={{ field: 'invoice.lines', emptyLabel: 'Nothing' }} sampleList={[]} onChange={() => {}} ctx={ctx()} />)
    expect(screen.getByText(/list is empty, so the “Nothing” path/)).toBeInTheDocument()
  })

  it('counts items', () => {
    expect([itemCount([1, 2]), itemCount({ a: 1 }), itemCount([]), itemCount(undefined)]).toEqual([2, 1, 0, 0])
  })

  it('names the outputs from the settings', () => {
    const node = (config: Record<string, unknown>): DefNode => ({ id: 'l', type: 'for-each', title: 'l', position: { x: 0, y: 0 }, config })
    expect(portLabel(node({ eachLabel: ' Per line ', emptyLabel: '' }), 'each', 'Each')).toBe('Per line')
    expect(portLabel(node({ eachLabel: ' Per line ', emptyLabel: '' }), 'empty', 'No items')).toBe('No items')
  })
})
