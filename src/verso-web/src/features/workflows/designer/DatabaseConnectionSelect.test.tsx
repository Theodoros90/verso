import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { json } from '@/test/configuration'

import { DatabaseConnectionSelect } from './DatabaseConnectionSelect'

function stubOptions(options: unknown, status = 200) {
  const fetchMock = vi.fn(async (url: RequestInfo | URL) => (String(url) === '/api/v1/database-connection-options' ? json(options, status) : json({}, 404)))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('DatabaseConnectionSelect', () => {
  it('lists the active connections with their provider and reports the chosen key', async () => {
    stubOptions([
      { key: 'core-db', name: 'Core DB', provider: 'SQL Server' },
      { key: 'reports', name: 'Reports', provider: 'PostgreSQL' },
    ])
    const onChange = vi.fn()
    render(<DatabaseConnectionSelect aria-label="Database connection" value="" onChange={onChange} />)

    await waitFor(() => expect(screen.getByRole('option', { name: 'Core DB (SQL Server)' })).toBeInTheDocument())
    expect(screen.getByRole('option', { name: 'Reports (PostgreSQL)' })).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Database connection' }), 'reports')
    expect(onChange).toHaveBeenCalledWith('reports')
  })

  it('says so when there are no connections yet', async () => {
    stubOptions([])
    render(<DatabaseConnectionSelect aria-label="Database connection" value="" onChange={() => {}} />)
    expect(await screen.findByRole('option', { name: 'No connections yet. Create one in Configuration, Connections.' })).toBeInTheDocument()
  })

  it('keeps a saved key that is not in the list, shown as an unknown connection', async () => {
    stubOptions([{ key: 'core-db', name: 'Core DB', provider: 'SQL Server' }])
    render(<DatabaseConnectionSelect aria-label="Database connection" value="old-db" onChange={() => {}} />)
    await waitFor(() => expect(screen.getByRole('option', { name: 'Unknown connection (old-db)' })).toBeInTheDocument())
    expect(screen.getByRole('combobox', { name: 'Database connection' })).toHaveValue('old-db')
  })

  it('shows an error line and still keeps the saved key when the list cannot be loaded', async () => {
    stubOptions({ title: 'Forbidden' }, 403)
    render(<DatabaseConnectionSelect aria-label="Database connection" value="core-db" onChange={() => {}} />)
    expect(await screen.findByText('The connections could not be loaded.')).toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Database connection' })).toHaveValue('core-db')
  })
})
