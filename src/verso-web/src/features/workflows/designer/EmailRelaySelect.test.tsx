import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'

import { json } from '@/test/configuration'

import { SchemaForm, type FieldSchema, type FormContext } from './SchemaForm'

const schema: FieldSchema = {
  type: 'object',
  properties: {
    relay: { type: 'string', title: 'Email connection', 'x-widget': 'email-relay' },
  },
}
const ctx: FormContext = { templates: [], dataPaths: [], errorsFor: () => [], readOnly: false }

function Harness({ initial }: { initial: string }) {
  const [value, setValue] = useState<Record<string, unknown>>({ relay: initial })
  return <SchemaForm schema={schema} value={value} onChange={(next) => setValue(next)} ctx={ctx} />
}

function stubOptions(options: unknown, status = 200) {
  const fetchMock = vi.fn(async (url: RequestInfo | URL) => (String(url) === '/api/v1/email-relay-options' ? json(options, status) : json({}, 404)))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('Email connection setting of the Send email step', () => {
  it('is a dropdown of the global email connections, with Default first', async () => {
    stubOptions([
      { key: 'statements', name: 'Statements mail', isActive: true },
      { key: 'internal', name: 'Internal mail', isActive: false },
    ])
    render(<Harness initial="" />)

    const select = await screen.findByRole('combobox', { name: 'Email connection' })
    await waitFor(() => expect(screen.getByRole('option', { name: 'Statements mail (statements)' })).toBeInTheDocument())
    expect(screen.getAllByRole('option').map((o) => o.textContent)).toEqual(['Default email connection', 'Statements mail (statements)', 'Internal mail (internal), switched off'])
    expect(select).toHaveValue('')

    await userEvent.selectOptions(select, 'statements')
    expect(select).toHaveValue('statements')
    await userEvent.selectOptions(select, '')
    expect(select).toHaveValue('')
  })

  it('keeps a saved key that is no longer in the list, marked as not found, instead of silently changing it', async () => {
    stubOptions([{ key: 'statements', name: 'Statements mail', isActive: true }])
    render(<Harness initial="old-relay" />)

    await waitFor(() => expect(screen.getByRole('option', { name: 'old-relay (not found)' })).toBeInTheDocument())
    expect(screen.getByRole('combobox', { name: 'Email connection' })).toHaveValue('old-relay')
  })

  it('still works, with only Default and the saved key, when the list cannot be loaded', async () => {
    stubOptions({ title: 'Forbidden' }, 403)
    render(<Harness initial="statements" />)

    await waitFor(() => expect(screen.getByRole('option', { name: 'statements (not found)' })).toBeInTheDocument())
    expect(screen.getByRole('option', { name: 'Default email connection' })).toBeInTheDocument()
  })
})
