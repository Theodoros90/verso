import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { DatabaseConnectionDto } from '@/lib/api/generated'
import { setSession } from '@/lib/auth'
import { json, openConfiguration } from '@/test/configuration'

const connection = (over: Partial<DatabaseConnectionDto> = {}): DatabaseConnectionDto => ({
  id: 'd-1',
  provider: 'SqlServer',
  port: 0,
  key: 'reporting',
  name: 'Reporting DB',
  server: 'sql.example.test',
  database: 'Reports',
  userName: 'reader',
  encrypt: true,
  trustServerCertificate: false,
  timeoutSeconds: 15,
  isActive: true,
  hasPassword: true,
  updatedAt: '2026-11-16T10:00:00Z',
  updatedBy: 'dev.admin',
  allowWrites: false,
  ...over,
})

type Outcome = { status: string; code?: string; message?: string; serverVersion?: string }

/** A tiny in-memory database connections API that remembers what it was sent. */
function dbApi(initial: DatabaseConnectionDto[], fail?: () => Response | undefined, outcome: Outcome = { status: 'Succeeded', serverVersion: 'Microsoft SQL Server 2022 (test)' }) {
  const items = [...initial]
  const bodies: Record<string, unknown>[] = []
  const calls: string[] = []
  const handler = (url: string, init?: RequestInit) => {
    if (!url.startsWith('/api/v1/database-connections')) return undefined
    const method = init?.method ?? 'GET'
    calls.push(`${method} ${url}`)
    if (method === 'GET' && url.includes('/tests/')) return json({ id: 't1', connectionKey: 'reporting', requestedAt: '2026-11-16T10:00:00Z', ...outcome })
    if (method === 'GET') return json(items)
    if (method === 'POST' && url.endsWith('/tests')) return json({ id: 't1', connectionKey: 'reporting', status: 'Queued', requestedAt: '2026-11-16T10:00:00Z' }, 202)
    if (method === 'POST' && url.endsWith('/deactivate')) {
      const c = items.find((x) => url.includes(x.id))!
      c.isActive = false
      return json(c)
    }
    if (method === 'POST' && url.endsWith('/activate')) {
      const c = items.find((x) => url.includes(x.id))!
      c.isActive = true
      return json(c)
    }
    const body = JSON.parse(String(init!.body)) as Record<string, unknown>
    bodies.push(body)
    const failure = fail?.()
    if (failure) return failure
    if (method === 'POST') {
      const created = connection({ id: `d-${items.length + 1}`, key: String(body.key), name: String(body.name), server: String(body.server), hasPassword: !!body.password })
      items.push(created)
      return json(created, 201)
    }
    const c = items.find((x) => url.includes(x.id))!
    Object.assign(c, { name: body.name, server: body.server, hasPassword: body.clearPassword ? false : body.password ? true : c.hasPassword })
    return json(c)
  }
  return { handler, bodies, calls }
}

async function chooseSql(drawer: HTMLElement) {
  await userEvent.click(await within(drawer).findByRole('button', { name: 'New connection' }))
  const dialog = screen.getByRole('dialog', { name: 'New connection' })
  await userEvent.selectOptions(within(dialog).getByLabelText('Authentication'), 'Database login')
  return dialog
}

describe('Database connections (real connections in the Connections tab)', () => {
  it('shows the real connections as cards in Data sources, never the password', async () => {
    const drawer = await openConfiguration('Connections', dbApi([connection(), connection({ id: 'd-2', key: 'old', name: 'Old DB', hasPassword: false, isActive: false })]).handler)

    const card = (await within(drawer).findByRole('heading', { name: 'Reporting DB' })).closest('li')!
    expect(card.closest('section')).toHaveAccessibleName('Data sources')
    expect(within(card).getByText('Ready')).toBeInTheDocument()
    expect(within(card).getByText('Password saved')).toBeInTheDocument()
    expect(within(card).getByText(/Database login · sql\.example\.test \/ Reports/)).toBeInTheDocument()
    expect(within(drawer).getByRole('heading', { name: 'Old DB' }).closest('li')).toHaveTextContent('Off')
  })

  it('choosing Database login turns New connection into the real database form, with a password that can be typed', async () => {
    const drawer = await openConfiguration('Connections', dbApi([]).handler)
    const dialog = await chooseSql(drawer)

    expect(within(dialog).getByLabelText('Group')).toHaveValue('Data sources')
    expect(within(dialog).getByLabelText('Server name')).toBeInTheDocument()
    const password = within(dialog).getByLabelText('Password')
    expect(password).toBeEnabled()
    expect(password).toHaveAttribute('type', 'password')
    expect(within(dialog).getByLabelText('Encrypt the connection (recommended)')).toBeChecked()
    expect(within(dialog).getByLabelText('Trust the server certificate (non-production only)')).not.toBeChecked()
    expect(within(dialog).queryByLabelText('Secret (managed outside Verso)')).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: 'Test connection' })).not.toBeInTheDocument()
    expect(within(dialog).getByText(/After you create it, open Configure and use Test connection/)).toBeInTheDocument()
    expect(within(dialog).getByText('Use a read-only login: Verso checks that queries are a single SELECT, but the database login is the real protection.')).toBeInTheDocument()
  })

  it('creates the connection, sending the typed password once and never showing it again', async () => {
    const api = dbApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseSql(drawer)

    await userEvent.type(within(dialog).getByLabelText('Name'), 'Reporting DB')
    await userEvent.type(within(dialog).getByLabelText('Server name'), 'sql.example.test')
    await userEvent.type(within(dialog).getByLabelText('Login'), 'reader')
    await userEvent.type(within(dialog).getByLabelText('Password'), 'synthetic-db-pass')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'reporting')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(await within(drawer).findByRole('heading', { name: 'Reporting DB' })).toBeInTheDocument()
    expect(within(drawer).getByText('Database connection “Reporting DB” created.')).toBeInTheDocument()
    expect(api.bodies[0]).toMatchObject({ key: 'reporting', server: 'sql.example.test', userName: 'reader', password: 'synthetic-db-pass', encrypt: true, trustServerCertificate: false, clearPassword: false })
    expect(document.body.textContent).not.toContain('synthetic-db-pass')
  })

  it('checks the name, key, server and login before sending anything, and refuses connection-string tricks', async () => {
    const api = dbApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseSql(drawer)
    await userEvent.type(within(dialog).getByLabelText('Server name'), 'host;Encrypt=false')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'Bad Key')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(within(dialog).getByText('Give the connection a name.')).toBeInTheDocument()
    expect(within(dialog).getByText(/Enter a server name such as sql\.example\.com/)).toBeInTheDocument()
    expect(within(dialog).getByText('Enter the login.')).toBeInTheDocument()
    expect(within(dialog).getByText('Lower-case letters, digits and hyphens, 2–64 characters.')).toBeInTheDocument()
    expect(api.bodies).toHaveLength(0)
  })

  it('shows the duplicate-key error from the server under the key field', async () => {
    const drawer = await openConfiguration('Connections', dbApi([], () => json({ code: 'database_connection.key_taken', title: 'Key taken' }, 409)).handler)
    const dialog = await chooseSql(drawer)
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Dup')
    await userEvent.type(within(dialog).getByLabelText('Server name'), 'sql.example.test')
    await userEvent.type(within(dialog).getByLabelText('Login'), 'reader')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'reporting')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(await within(dialog).findByText('A connection with this key already exists.')).toBeInTheDocument()
  })

  it('editing without typing a password keeps the saved one; the remove box clears it; the key is fixed', async () => {
    const api = dbApi([connection()])
    const drawer = await openConfiguration('Connections', api.handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Reporting DB' }))
    const dialog = screen.getByRole('dialog', { name: 'Reporting DB' })

    expect(within(dialog).getByLabelText('Connection key')).toBeDisabled()
    expect(within(dialog).getByLabelText('Password')).toHaveValue('')
    await userEvent.clear(within(dialog).getByLabelText('Server name'))
    await userEvent.type(within(dialog).getByLabelText('Server name'), 'sql2.example.test')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))
    await within(drawer).findByText('Settings saved for Reporting DB.')
    expect(api.bodies[0]).toMatchObject({ server: 'sql2.example.test', password: null, clearPassword: false })

    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Reporting DB' }))
    const again = screen.getByRole('dialog', { name: 'Reporting DB' })
    await userEvent.click(within(again).getByRole('checkbox', { name: 'Remove the saved password' }))
    await userEvent.click(within(again).getByRole('button', { name: 'Save settings' }))
    await within(drawer).findByText('Settings saved for Reporting DB.')
    expect(api.bodies[1]).toMatchObject({ password: null, clearPassword: true })
  })

  it('Test connection asks the Worker to connect and reports success with the server version', async () => {
    const api = dbApi([connection()])
    const drawer = await openConfiguration('Connections', api.handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Reporting DB' }))
    const dialog = screen.getByRole('dialog', { name: 'Reporting DB' })

    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))

    expect(await within(dialog).findByText(/Connected · Microsoft SQL Server 2022 \(test\)/, {}, { timeout: 5000 })).toBeInTheDocument()
    expect(api.calls).toContain('POST /api/v1/database-connections/d-1/tests')
  })

  it('Test connection shows why a test failed, with the error code', async () => {
    const api = dbApi([connection()], undefined, { status: 'Failed', code: 'db.login_failed', message: 'The server rejected the login or the password.' })
    const drawer = await openConfiguration('Connections', api.handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Reporting DB' }))
    const dialog = screen.getByRole('dialog', { name: 'Reporting DB' })

    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))

    const alert = await within(dialog).findByText(/The test failed: The server rejected the login or the password/, {}, { timeout: 5000 })
    expect(alert).toHaveTextContent('db.login_failed')
  })

  it('Test connection is off for a switched-off connection', async () => {
    const drawer = await openConfiguration('Connections', dbApi([connection({ isActive: false })]).handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Reporting DB' }))

    expect(within(screen.getByRole('dialog', { name: 'Reporting DB' })).getByRole('button', { name: 'Test connection' })).toBeDisabled()
  })

  it('the switch deactivates and reactivates a connection', async () => {
    const api = dbApi([connection()])
    const drawer = await openConfiguration('Connections', api.handler)
    const toggle = await within(drawer).findByRole('switch', { name: 'Enable Reporting DB' })

    await userEvent.click(toggle)
    expect(await within(drawer).findByText('Reporting DB turned off.')).toBeInTheDocument()
    expect(api.calls).toContain('POST /api/v1/database-connections/d-1/deactivate')
    await userEvent.click(await within(drawer).findByRole('switch', { name: 'Enable Reporting DB' }))
    expect(await within(drawer).findByText('Reporting DB turned on.')).toBeInTheDocument()
  })

  it('people who are not administrators see no database connections and cannot create one', async () => {
    setSession({
      mode: 'Development',
      me: { id: 'dev.viewer', name: 'Vera Viewer', roles: ['Viewer'], permissions: { read: true, templateAuthoring: false, workflowAuthoring: false, operate: false, readPayload: false, readAudit: false, administer: false } },
      getToken: async () => 'dev:dev.viewer',
      signOut: () => undefined,
    })
    const api = dbApi([connection()])
    const drawer = await openConfiguration('Connections', api.handler)
    await within(drawer).findByRole('heading', { name: 'Print house SFTP' })
    expect(within(drawer).queryByRole('heading', { name: 'Reporting DB' })).not.toBeInTheDocument()
    expect(api.calls).toHaveLength(0)

    const dialog = await chooseSql(drawer)
    expect(within(dialog).getByText('Only administrators can create database connections.')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Create connection' })).toBeDisabled()
  })
})
