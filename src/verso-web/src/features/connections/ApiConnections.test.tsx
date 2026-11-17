import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { ApiConnectionDto } from '@/lib/api/generated'
import { setSession } from '@/lib/auth'
import { json, openConfiguration } from '@/test/configuration'

import { splitFullUrl } from './ApiFields'

const connection = (over: Partial<ApiConnectionDto> = {}): ApiConnectionDto => ({
  id: 'a-1',
  key: 'frankfurter',
  name: 'Frankfurter',
  baseUrl: 'https://api.frankfurter.dev/v1',
  testPath: '/latest?base=EUR&symbols=USD,GBP',
  authType: 'None',
  authName: '',
  tokenUrl: '',
  scope: '',
  timeoutSeconds: 15,
  isActive: true,
  hasSecret: false,
  updatedAt: '2026-11-17T10:00:00Z',
  updatedBy: 'dev.admin',
  ...over,
})

type Outcome = Partial<{ status: string; code: string; message: string; httpStatus: number; elapsedMs: number; contentType: string; responseBytes: number }>

/** A tiny in-memory API connections API that remembers what it was sent. */
function apiApi(initial: ApiConnectionDto[], fail?: () => Response | undefined, outcome: Outcome = { status: 'Succeeded', httpStatus: 200, elapsedMs: 42, contentType: 'application/json', responseBytes: 123 }) {
  const items = [...initial]
  const bodies: Record<string, unknown>[] = []
  const calls: string[] = []
  const handler = (url: string, init?: RequestInit) => {
    if (!url.startsWith('/api/v1/api-connections')) return undefined
    const method = init?.method ?? 'GET'
    calls.push(`${method} ${url}`)
    if (method === 'GET' && url.includes('/tests/')) return json({ id: 't1', connectionKey: 'frankfurter', requestedAt: '2026-11-17T10:00:00Z', ...outcome })
    if (method === 'GET') return json(items)
    if (method === 'POST' && url.endsWith('/tests')) return json({ id: 't1', connectionKey: 'frankfurter', status: 'Queued', requestedAt: '2026-11-17T10:00:00Z' }, 202)
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
      const created = connection({ id: `a-${items.length + 1}`, key: String(body.key), name: String(body.name), baseUrl: String(body.baseUrl), testPath: String(body.testPath), authType: String(body.authType), hasSecret: !!body.secret })
      items.push(created)
      return json(created, 201)
    }
    const c = items.find((x) => url.includes(x.id))!
    Object.assign(c, { name: body.name, authType: body.authType, hasSecret: body.clearSecret || body.authType === 'None' ? false : body.secret ? true : c.hasSecret })
    return json(c)
  }
  return { handler, bodies, calls }
}

async function chooseApi(drawer: HTMLElement) {
  await userEvent.click(await within(drawer).findByRole('button', { name: 'New connection' }))
  const dialog = screen.getByRole('dialog', { name: 'New connection' })
  await userEvent.selectOptions(within(dialog).getByLabelText('Authentication'), 'HTTP API')
  return dialog
}

describe('splitFullUrl', () => {
  it('splits a pasted address with a query into the base address and the test path', () => {
    expect(splitFullUrl('https://api.frankfurter.dev/v1/latest?base=EUR&symbols=USD,GBP')).toEqual({ baseUrl: 'https://api.frankfurter.dev/v1', testPath: '/latest?base=EUR&symbols=USD,GBP' })
    expect(splitFullUrl('  https://api.example.com/status?x=1 ')).toEqual({ baseUrl: 'https://api.example.com', testPath: '/status?x=1' })
  })

  it('leaves an address without a query, or without a path before it, alone', () => {
    expect(splitFullUrl('https://api.frankfurter.dev/v1')).toBeNull()
    expect(splitFullUrl('https://api.example.com?x=1')).toBeNull()
    expect(splitFullUrl('not a url?x=1')).toBeNull()
  })
})

describe('API connections (real HTTP APIs in the Connections tab)', () => {
  it('shows the real connections as cards in Data sources, never the secret', async () => {
    const drawer = await openConfiguration('Connections', apiApi([connection(), connection({ id: 'a-2', key: 'billing', name: 'Billing API', authType: 'Bearer', hasSecret: true, baseUrl: 'https://billing.example.test/v2', isActive: false })]).handler)

    const card = (await within(drawer).findByRole('heading', { name: 'Frankfurter' })).closest('li')!
    expect(card.closest('section')).toHaveAccessibleName('Data sources')
    expect(within(card).getByText('Ready')).toBeInTheDocument()
    expect(within(card).getByText(/No authentication · api\.frankfurter\.dev/)).toBeInTheDocument()
    const other = within(drawer).getByRole('heading', { name: 'Billing API' }).closest('li')!
    expect(within(other).getByText('Off')).toBeInTheDocument()
    expect(within(other).getByText('Secret saved')).toBeInTheDocument()
    expect(drawer.querySelectorAll('input[type=password]')).toHaveLength(0) // a card never holds a secret field
  })

  it('choosing HTTP API turns New connection into the real API form', async () => {
    const drawer = await openConfiguration('Connections', apiApi([]).handler)
    const dialog = await chooseApi(drawer)

    expect(within(dialog).getByLabelText('Group')).toHaveValue('Data sources')
    expect(within(dialog).getByLabelText('Base address')).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Test request path')).toBeInTheDocument()
    expect(within(dialog).getByLabelText('API credentials')).toHaveValue('None')
    expect(within(dialog).queryByLabelText('Secret (managed outside Verso)')).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: 'Test connection' })).not.toBeInTheDocument()
    expect(within(dialog).getByText(/After you create it, open Configure and use Test connection/)).toBeInTheDocument()
  })

  it('shows only the fields each kind of credential needs', async () => {
    const drawer = await openConfiguration('Connections', apiApi([]).handler)
    const dialog = await chooseApi(drawer)
    const kind = within(dialog).getByLabelText('API credentials')

    expect(within(dialog).queryByLabelText('API key')).not.toBeInTheDocument()
    await userEvent.selectOptions(kind, 'ApiKeyHeader')
    expect(within(dialog).getByLabelText('Header name')).toBeInTheDocument()
    expect(within(dialog).getByLabelText('API key')).toHaveAttribute('type', 'password')
    await userEvent.selectOptions(kind, 'ApiKeyQuery')
    expect(within(dialog).getByLabelText('Query parameter name')).toBeInTheDocument()
    expect(within(dialog).getByText(/It is sent in the address, which some servers log/)).toBeInTheDocument()
    await userEvent.selectOptions(kind, 'Bearer')
    expect(within(dialog).queryByLabelText('Header name')).not.toBeInTheDocument()
    expect(within(dialog).getByLabelText('Bearer token')).toBeInTheDocument()
    await userEvent.selectOptions(kind, 'Basic')
    expect(within(dialog).getByLabelText('User name')).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Password')).toBeInTheDocument()
    await userEvent.selectOptions(kind, 'OAuthClientCredentials')
    expect(within(dialog).getByLabelText('Client id')).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Client secret')).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Token URL')).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Scope (optional)')).toBeInTheDocument()
  })

  it('splits a pasted full address into the base address and the test path', async () => {
    const drawer = await openConfiguration('Connections', apiApi([]).handler)
    const dialog = await chooseApi(drawer)

    await userEvent.type(within(dialog).getByLabelText('Base address'), 'https://api.frankfurter.dev/v1/latest?base=EUR&symbols=USD,GBP')
    await userEvent.tab() // leaving the field splits it

    expect(within(dialog).getByLabelText('Base address')).toHaveValue('https://api.frankfurter.dev/v1')
    expect(within(dialog).getByLabelText('Test request path')).toHaveValue('/latest?base=EUR&symbols=USD,GBP')
  })

  it('creates the Frankfurter example with no key, and shows it as a card', async () => {
    const api = apiApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseApi(drawer)

    await userEvent.type(within(dialog).getByLabelText('Name'), 'Frankfurter')
    await userEvent.type(within(dialog).getByLabelText('Base address'), 'https://api.frankfurter.dev/v1/latest?base=EUR&symbols=USD,GBP')
    await userEvent.tab()
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'frankfurter')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(await within(drawer).findByRole('heading', { name: 'Frankfurter' })).toBeInTheDocument()
    expect(within(drawer).getByText('API connection “Frankfurter” created.')).toBeInTheDocument()
    expect(api.bodies[0]).toMatchObject({ key: 'frankfurter', baseUrl: 'https://api.frankfurter.dev/v1', testPath: '/latest?base=EUR&symbols=USD,GBP', authType: 'None', secret: null })
  })

  it('creates a connection with an API key, sending it once and never showing it again', async () => {
    const api = apiApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseApi(drawer)

    await userEvent.type(within(dialog).getByLabelText('Name'), 'Billing API')
    await userEvent.type(within(dialog).getByLabelText('Base address'), 'https://billing.example.test/v2')
    await userEvent.type(within(dialog).getByLabelText('Test request path'), '/status')
    await userEvent.selectOptions(within(dialog).getByLabelText('API credentials'), 'ApiKeyHeader')
    await userEvent.type(within(dialog).getByLabelText('Header name'), 'X-API-Key')
    await userEvent.type(within(dialog).getByLabelText('API key'), 'synthetic-api-key')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'billing')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(await within(drawer).findByRole('heading', { name: 'Billing API' })).toBeInTheDocument()
    expect(api.bodies[0]).toMatchObject({ authType: 'ApiKeyHeader', authName: 'X-API-Key', secret: 'synthetic-api-key', clearSecret: false })
    expect(document.body.textContent).not.toContain('synthetic-api-key')
  })

  it('checks the address, path, header name and token URL before sending anything', async () => {
    const api = apiApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseApi(drawer)
    await userEvent.type(within(dialog).getByLabelText('Base address'), 'http://insecure.example.test')
    await userEvent.type(within(dialog).getByLabelText('Test request path'), 'status')
    await userEvent.selectOptions(within(dialog).getByLabelText('API credentials'), 'ApiKeyHeader')
    await userEvent.type(within(dialog).getByLabelText('Header name'), 'Bad Header')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'Bad Key')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(within(dialog).getByText('Give the connection a name.')).toBeInTheDocument()
    expect(within(dialog).getByText(/Enter the base address starting with https:\/\//)).toBeInTheDocument()
    expect(within(dialog).getByText(/Enter the test request path starting with \//)).toBeInTheDocument()
    expect(within(dialog).getByText('Enter the header name, such as X-API-Key.')).toBeInTheDocument()
    expect(within(dialog).getByText('Lower-case letters, digits and hyphens, 2–64 characters.')).toBeInTheDocument()

    await userEvent.selectOptions(within(dialog).getByLabelText('API credentials'), 'OAuthClientCredentials')
    await userEvent.clear(within(dialog).getByLabelText('Client id'))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))
    expect(within(dialog).getByText('Enter the client id.')).toBeInTheDocument()
    expect(within(dialog).getByText('Enter the token endpoint address starting with https://.')).toBeInTheDocument()
    expect(api.bodies).toHaveLength(0)
  })

  it('shows the duplicate-key error from the server under the key field', async () => {
    const drawer = await openConfiguration('Connections', apiApi([], () => json({ code: 'api_connection.key_taken', title: 'Key taken' }, 409)).handler)
    const dialog = await chooseApi(drawer)
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Dup')
    await userEvent.type(within(dialog).getByLabelText('Base address'), 'https://api.example.test')
    await userEvent.type(within(dialog).getByLabelText('Test request path'), '/status')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'frankfurter')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(await within(dialog).findByText('A connection with this key already exists.')).toBeInTheDocument()
  })

  it('editing without typing a secret keeps the saved one; the remove box clears it; the key is fixed', async () => {
    const api = apiApi([connection({ authType: 'Bearer', hasSecret: true })])
    const drawer = await openConfiguration('Connections', api.handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Frankfurter' }))
    const dialog = screen.getByRole('dialog', { name: 'Frankfurter' })

    expect(within(dialog).getByLabelText('Connection key')).toBeDisabled()
    expect(within(dialog).getByLabelText('Bearer token')).toHaveValue('')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))
    await within(drawer).findByText('Settings saved for Frankfurter.')
    expect(api.bodies[0]).toMatchObject({ authType: 'Bearer', secret: null, clearSecret: false })

    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Frankfurter' }))
    const again = screen.getByRole('dialog', { name: 'Frankfurter' })
    await userEvent.click(within(again).getByRole('checkbox', { name: 'Remove the saved secret' }))
    await userEvent.click(within(again).getByRole('button', { name: 'Save settings' }))
    await within(drawer).findByText('Settings saved for Frankfurter.')
    expect(api.bodies[1]).toMatchObject({ secret: null, clearSecret: true })
  })

  it('Test connection sends one GET through the Worker and shows only status, time, type and size', async () => {
    const api = apiApi([connection()])
    const drawer = await openConfiguration('Connections', api.handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Frankfurter' }))
    const dialog = screen.getByRole('dialog', { name: 'Frankfurter' })

    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))

    expect(await within(dialog).findByText('Connected · HTTP 200 · 42 ms · application/json · 123 bytes.', {}, { timeout: 5000 })).toBeInTheDocument()
    expect(api.calls).toContain('POST /api/v1/api-connections/a-1/tests')
  })

  it('Test connection shows why a test failed, with the code and the HTTP status', async () => {
    const api = apiApi([connection()], undefined, { status: 'Failed', code: 'api.unauthorized', message: 'The API refused the credentials (HTTP 401 or 403). Check the key, token or login.', httpStatus: 401 })
    const drawer = await openConfiguration('Connections', api.handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Frankfurter' }))
    const dialog = screen.getByRole('dialog', { name: 'Frankfurter' })

    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))

    const alert = await within(dialog).findByText(/The test failed: The API refused the credentials/, {}, { timeout: 5000 })
    expect(alert).toHaveTextContent('api.unauthorized')
    expect(alert).toHaveTextContent('HTTP 401')
  })

  it('Test connection is off for a switched-off connection, and the switch turns it on and off', async () => {
    const api = apiApi([connection({ isActive: false })])
    const drawer = await openConfiguration('Connections', api.handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Frankfurter' }))
    expect(within(screen.getByRole('dialog', { name: 'Frankfurter' })).getByRole('button', { name: 'Test connection' })).toBeDisabled()
    await userEvent.click(within(screen.getByRole('dialog', { name: 'Frankfurter' })).getByRole('button', { name: 'Cancel' }))

    await userEvent.click(await within(drawer).findByRole('switch', { name: 'Enable Frankfurter' }))
    expect(await within(drawer).findByText('Frankfurter turned on.')).toBeInTheDocument()
    expect(api.calls).toContain('POST /api/v1/api-connections/a-1/activate')
  })

  it('people who are not administrators see no API connections and cannot create one', async () => {
    setSession({
      mode: 'Development',
      me: { id: 'dev.viewer', name: 'Vera Viewer', roles: ['Viewer'], permissions: { read: true, templateAuthoring: false, workflowAuthoring: false, operate: false, readPayload: false, readAudit: false, administer: false } },
      getToken: async () => 'dev:dev.viewer',
      signOut: () => undefined,
    })
    const api = apiApi([connection()])
    const drawer = await openConfiguration('Connections', api.handler)
    await within(drawer).findByRole('heading', { name: 'Print house SFTP' })
    expect(within(drawer).queryByRole('heading', { name: 'Frankfurter' })).not.toBeInTheDocument()
    expect(api.calls).toHaveLength(0)

    const dialog = await chooseApi(drawer)
    expect(within(dialog).getByText('Only administrators can create API connections.')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Create connection' })).toBeDisabled()
  })
})
