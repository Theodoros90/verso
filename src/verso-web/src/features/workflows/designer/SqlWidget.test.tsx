import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { json } from '@/test/configuration'

import { SchemaForm, type FieldSchema, type FormContext } from './SchemaForm'

const schema: FieldSchema = {
  type: 'object',
  properties: {
    sql: { type: 'string', title: 'SQL (one SELECT)', 'x-widget': 'sql' },
    maxRows: { type: 'integer', title: 'Most rows to read' },
  },
}
const ctx: FormContext = { templates: [], dataPaths: [], errorsFor: () => [], readOnly: false }

function Harness({ initial, onValue }: { initial: Record<string, unknown>; onValue?: (v: Record<string, unknown>) => void }) {
  const [value, setValue] = useState(initial)
  return (
    <SchemaForm
      schema={schema}
      value={value}
      onChange={(next) => {
        setValue(next)
        onValue?.(next)
      }}
      ctx={ctx}
    />
  )
}

describe('SQL setting of the Query database step', () => {
  it('is a monospace box with guidance and a live character counter', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => json({}, 404)))
    render(<Harness initial={{ sql: 'SELECT 1' }} />)
    const box = screen.getByRole('textbox', { name: /SQL/ })
    expect(box.tagName).toBe('TEXTAREA')
    expect(box).toHaveClass('font-mono')
    expect(box).toHaveAttribute('maxlength', '4000')
    expect(screen.getByText('One SELECT only. Write values as @name and map them below. Use a read-only database login.')).toBeInTheDocument()
    expect(screen.getByText('8 / 4000')).toBeInTheDocument()
    await userEvent.type(box, ' ')
    expect(screen.getByText('9 / 4000')).toBeInTheDocument()
  })

  it('stores whole numbers for a number setting', async () => {
    const seen: Record<string, unknown>[] = []
    render(<Harness initial={{ sql: '', maxRows: 1 }} onValue={(v) => seen.push(v)} />)
    const input = screen.getByRole('spinbutton', { name: /Most rows/ })
    expect(input).toHaveValue(1)
    await userEvent.type(input, '5')
    expect(seen.at(-1)?.maxRows).toBe(15)
  })
})
