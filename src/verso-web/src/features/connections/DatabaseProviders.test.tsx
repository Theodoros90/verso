import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { DatabaseConnectionDto } from '@/lib/api/generated'
import { json, openConfiguration } from '@/test/configuration'

const connection = (over: Partial<DatabaseConnectionDto> = {}): DatabaseConnectionDto => ({
  id: 'd-1',
  key: 'reporting',
  provider: 'PostgreSql',
  name: 'Reporting PG',
  server: 'pg.example.test',
  port: 5433,
  database: 'reports',
  userName: 'reader',
  encrypt: true,
  trustServerCertificate: false,
  timeoutSeconds: 15,
  isActive: true,
  hasPassword: true,
  updatedAt: '2026-11-16T10:00:00Z',
  updatedBy: 'dev.admin',
  ...over,
})

function dbApi(initial: DatabaseConnectionDto[]) {
  const items = [...initial]
  const bodies: Record<string, unknown>[] = []
  const handler = (url: string, init?: RequestInit) => {
    if (!url.startsWith('/api/v1/database-connections')) return undefined
    const method = init?.method ?? 'GET'
    if (method === 'GET') return json(items)
    const body = JSON.parse(String(init!.body)) as Record<string, unknown>
    bodies.push(body)
    if (method === 'POST') {
      const created = connection({ id: `d-${items.length + 1}`, key: String(body.key), provider: String(body.provider), name: String(body.name), server: String(body.server), port: Number(body.port) })
      items.push(created)
      return json(created, 201)
    }
    return json(items[0])
  }
  return { handler, bodies }
}

async function chooseDatabase(drawer: HTMLElement) {
  await userEvent.click(await within(drawer).findByRole('button', { name: 'New connection' }))
  const dialog = screen.getByRole('dialog', { name: 'New connection' })
  await userEvent.selectOptions(within(dialog).getByLabelText('Authentication'), 'Database login')
  return dialog
}

describe('Database types in the connection form', () => {
  it('offers SQL Server, PostgreSQL, MySQL / MariaDB and Oracle, with SQL Server first and each type’s default port as the hint', async () => {
    const drawer = await openConfiguration('Connections', dbApi([]).handler)
    const dialog = await chooseDatabase(drawer)

    const type = within(dialog).getByLabelText('Database type')
    expect(within(type).getAllByRole('option').map((o) => o.textContent)).toEqual(['SQL Server', 'PostgreSQL', 'MySQL / MariaDB', 'Oracle'])
    expect(type).toHaveValue('SqlServer')
    expect(within(dialog).getByLabelText('Port')).toHaveAttribute('placeholder', '1433')

    await userEvent.selectOptions(type, 'PostgreSql')
    expect(within(dialog).getByLabelText('Port')).toHaveAttribute('placeholder', '5432')
    await userEvent.selectOptions(type, 'MySql')
    expect(within(dialog).getByLabelText('Port')).toHaveAttribute('placeholder', '3306')
    await userEvent.selectOptions(type, 'Oracle')
    expect(within(dialog).getByLabelText('Port')).toHaveAttribute('placeholder', '1521')
  })

  it('creates a PostgreSQL connection with its type and port', async () => {
    const api = dbApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseDatabase(drawer)

    await userEvent.selectOptions(within(dialog).getByLabelText('Database type'), 'PostgreSql')
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Reporting PG')
    await userEvent.type(within(dialog).getByLabelText('Server name'), 'pg.example.test')
    await userEvent.type(within(dialog).getByLabelText('Port'), '5433')
    await userEvent.type(within(dialog).getByLabelText('Login'), 'reader')
    await userEvent.type(within(dialog).getByLabelText('Password'), 'synthetic-pg-pass')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'reporting')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    const card = (await within(drawer).findByRole('heading', { name: 'Reporting PG' })).closest('li')!
    expect(api.bodies[0]).toMatchObject({ provider: 'PostgreSql', server: 'pg.example.test', port: 5433, userName: 'reader', password: 'synthetic-pg-pass' })
    expect(within(card).getByText(/PostgreSQL · key/)).toBeInTheDocument()
    expect(within(card).getByText(/Database login · pg\.example\.test:5433/)).toBeInTheDocument()
    expect(document.body.textContent).not.toContain('synthetic-pg-pass')
  })

  it('an empty port is sent as 0, which means the type’s default', async () => {
    const api = dbApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseDatabase(drawer)
    await userEvent.selectOptions(within(dialog).getByLabelText('Database type'), 'MySql')
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Shop DB')
    await userEvent.type(within(dialog).getByLabelText('Server name'), 'mysql.example.test')
    await userEvent.type(within(dialog).getByLabelText('Login'), 'reader')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'shop')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    await within(drawer).findByRole('heading', { name: 'Shop DB' })
    expect(api.bodies[0]).toMatchObject({ provider: 'MySql', port: 0 })
  })

  it('PostgreSQL, MySQL and Oracle take a plain host: an instance or a port in the server is refused', async () => {
    const api = dbApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseDatabase(drawer)
    await userEvent.selectOptions(within(dialog).getByLabelText('Database type'), 'PostgreSql')
    await userEvent.type(within(dialog).getByLabelText('Server name'), 'pg.example.test,5432')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(within(dialog).getByText('Enter a host name or address such as db.example.com (the port is its own field).')).toBeInTheDocument()
    expect(api.bodies).toHaveLength(0)

    await userEvent.selectOptions(within(dialog).getByLabelText('Database type'), 'SqlServer')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))
    expect(within(dialog).queryByText(/the port is its own field/)).not.toBeInTheDocument() // SQL Server still accepts host,port
  })

  it('Oracle asks for a service name, has no trust-certificate switch, and never sends trust', async () => {
    const api = dbApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseDatabase(drawer)
    await userEvent.selectOptions(within(dialog).getByLabelText('Database type'), 'PostgreSql')
    await userEvent.click(within(dialog).getByLabelText('Trust the server certificate (non-production only)'))
    await userEvent.selectOptions(within(dialog).getByLabelText('Database type'), 'Oracle')

    expect(within(dialog).getByLabelText('Service name')).toBeInTheDocument()
    expect(within(dialog).queryByLabelText('Trust the server certificate (non-production only)')).not.toBeInTheDocument()
    expect(within(dialog).getByText(/Oracle checks the server certificate with the Worker/)).toBeInTheDocument()

    await userEvent.type(within(dialog).getByLabelText('Name'), 'Ora')
    await userEvent.type(within(dialog).getByLabelText('Server name'), 'ora.example.test')
    await userEvent.type(within(dialog).getByLabelText('Login'), 'reader')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'ora')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))
    expect(within(dialog).getByText('The service name is required.')).toBeInTheDocument()
    expect(api.bodies).toHaveLength(0)

    await userEvent.type(within(dialog).getByLabelText('Service name'), 'ORCLPDB1')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))
    await within(drawer).findByRole('heading', { name: 'Ora' })
    expect(api.bodies[0]).toMatchObject({ provider: 'Oracle', database: 'ORCLPDB1', trustServerCertificate: false })
  })

  it('editing shows the saved type, which cannot change, and keeps the port', async () => {
    const drawer = await openConfiguration('Connections', dbApi([connection()]).handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Reporting PG' }))
    const dialog = screen.getByRole('dialog', { name: 'Reporting PG' })

    expect(within(dialog).getByLabelText('Database type')).toHaveValue('PostgreSql')
    expect(within(dialog).getByLabelText('Database type')).toBeDisabled()
    expect(within(dialog).getByLabelText('Port')).toHaveValue('5433')
    expect(within(dialog).getByText('The type cannot change after creation.')).toBeInTheDocument()
  })
})
