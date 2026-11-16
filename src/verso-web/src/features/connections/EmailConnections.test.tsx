import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { EmailRelayDto } from '@/lib/api/generated'
import { setSession } from '@/lib/auth'
import { json, openConfiguration } from '@/test/configuration'

const relay = (over: Partial<EmailRelayDto> = {}): EmailRelayDto => ({
  id: 'r-1',
  key: 'statements',
  name: 'Statements mail',
  host: 'smtp.bank.example',
  port: 587,
  enableSsl: true,
  userName: 'svc-mail',
  fromAddress: 'statements@bank.example',
  fromName: 'Verso',
  timeoutSeconds: 30,
  allowedRecipientDomains: ['customers.example'],
  isActive: true,
  hasPassword: true,
  updatedAt: '2026-11-16T10:00:00Z',
  updatedBy: 'dev.admin',
  ...over,
})

/** A tiny in-memory email relays API that remembers what it was sent. */
function relaysApi(initial: EmailRelayDto[], fail?: () => Response | undefined, testOutcome: { status: string; code?: string; message?: string } = { status: 'Succeeded' }) {
  const items = [...initial]
  const bodies: Record<string, unknown>[] = []
  const calls: string[] = []
  const testBodies: Record<string, unknown>[] = []
  const handler = (url: string, init?: RequestInit) => {
    if (!url.startsWith('/api/v1/email-relays')) return undefined
    const method = init?.method ?? 'GET'
    calls.push(`${method} ${url}`)
    if (method === 'GET' && url.includes('/tests/')) return json({ id: 't1', relayKey: 'statements', requestedAt: '2026-11-16T10:00:00Z', ...testOutcome })
    if (method === 'GET') return json(items)
    if (method === 'POST' && url.endsWith('/tests')) {
      testBodies.push(JSON.parse(String(init!.body)) as Record<string, unknown>)
      return json({ id: 't1', relayKey: 'statements', status: 'Queued', requestedAt: '2026-11-16T10:00:00Z' }, 202)
    }
    if (method === 'POST' && url.endsWith('/deactivate')) {
      const r = items.find((x) => url.includes(x.id))!
      r.isActive = false
      return json(r)
    }
    if (method === 'POST' && url.endsWith('/activate')) {
      const r = items.find((x) => url.includes(x.id))!
      r.isActive = true
      return json(r)
    }
    const body = JSON.parse(String(init!.body)) as Record<string, unknown>
    bodies.push(body)
    const failure = fail?.()
    if (failure) return failure
    if (method === 'POST') {
      const created = relay({ id: `r-${items.length + 1}`, key: String(body.key), name: String(body.name), host: String(body.host), hasPassword: !!body.password })
      items.push(created)
      return json(created, 201)
    }
    const r = items.find((x) => url.includes(x.id))!
    Object.assign(r, { name: body.name, host: body.host, hasPassword: body.clearPassword ? false : body.password ? true : r.hasPassword })
    return json(r)
  }
  return { handler, bodies, calls, testBodies }
}

async function chooseSmtp(drawer: HTMLElement) {
  await userEvent.click(await within(drawer).findByRole('button', { name: 'New connection' }))
  const dialog = screen.getByRole('dialog', { name: 'New connection' })
  await userEvent.selectOptions(within(dialog).getByLabelText('Authentication'), 'SMTP credentials')
  return dialog
}

describe('Email connections (real SMTP relays in the Connections tab)', () => {
  it('has no separate "New email relay" button: email connections start from New connection', async () => {
    const drawer = await openConfiguration('Connections', relaysApi([]).handler)
    await within(drawer).findByRole('button', { name: 'New connection' })
    expect(within(drawer).queryByRole('button', { name: /New email relay/ })).not.toBeInTheDocument()
  })

  it('shows the real relays as cards in Delivery channels, never the password', async () => {
    const drawer = await openConfiguration('Connections', relaysApi([relay(), relay({ id: 'r-2', key: 'internal', name: 'Internal mail', hasPassword: false, userName: 'svc', isActive: false })]).handler)

    const card = (await within(drawer).findByRole('heading', { name: 'Statements mail' })).closest('li')!
    expect(card.closest('section')).toHaveAccessibleName('Delivery channels')
    expect(within(card).getByText('Ready')).toBeInTheDocument()
    expect(within(card).getByText('Password saved')).toBeInTheDocument()
    expect(within(card).getByText(/SMTP credentials · smtp\.bank\.example:587/)).toBeInTheDocument()
    const other = within(drawer).getByRole('heading', { name: 'Internal mail' }).closest('li')!
    expect(within(other).getByText('Off')).toBeInTheDocument()
  })

  it('choosing SMTP credentials turns New connection into the real email form, with a password field that can be typed', async () => {
    const drawer = await openConfiguration('Connections', relaysApi([]).handler)
    const dialog = await chooseSmtp(drawer)

    expect(within(dialog).getByLabelText('Group')).toHaveValue('Delivery channels')
    expect(within(dialog).getByLabelText('Server address')).toBeInTheDocument()
    const password = within(dialog).getByLabelText('Password')
    expect(password).toBeEnabled()
    expect(password).toHaveAttribute('type', 'password')
    expect(within(dialog).queryByLabelText('Secret (managed outside Verso)')).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: 'Test connection' })).not.toBeInTheDocument()
  })

  it('creates the relay from a preset, sending the typed password once and never showing it again', async () => {
    const api = relaysApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseSmtp(drawer)

    await userEvent.click(within(dialog).getByRole('button', { name: 'Gmail' }))
    expect(within(dialog).getByLabelText('Server address')).toHaveValue('smtp.gmail.com')
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Gmail test')
    await userEvent.type(within(dialog).getByLabelText('Relay key'), 'gmail-test')
    await userEvent.type(within(dialog).getByLabelText('Username'), 'someone@example.com')
    await userEvent.type(within(dialog).getByLabelText('From address'), 'someone@example.com')
    await userEvent.type(within(dialog).getByLabelText('Password'), 'synthetic-pass')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(await within(drawer).findByRole('heading', { name: 'Gmail test' })).toBeInTheDocument()
    expect(within(drawer).getByText('Email connection “Gmail test” created.')).toBeInTheDocument()
    expect(api.bodies[0]).toMatchObject({ key: 'gmail-test', host: 'smtp.gmail.com', port: 587, enableSsl: true, password: 'synthetic-pass', userName: 'someone@example.com', clearPassword: false })
    expect(document.body.textContent).not.toContain('synthetic-pass')
  })

  it('checks the name, key, server and sender before sending anything', async () => {
    const api = relaysApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseSmtp(drawer)
    await userEvent.type(within(dialog).getByLabelText('Relay key'), 'Bad Key')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(within(dialog).getByText('Give the connection a name.')).toBeInTheDocument()
    expect(within(dialog).getByText('Server address is required.')).toBeInTheDocument()
    expect(within(dialog).getByText('Enter the sender as an email address.')).toBeInTheDocument()
    expect(within(dialog).getByText('Lower-case letters, digits and hyphens, 2–64 characters.')).toBeInTheDocument()
    expect(api.bodies).toHaveLength(0)
  })

  it('refuses a name already used by another connection, simulated or real', async () => {
    const drawer = await openConfiguration('Connections', relaysApi([relay()]).handler)
    await within(drawer).findByRole('heading', { name: 'Statements mail' })
    const dialog = await chooseSmtp(drawer)
    await userEvent.type(within(dialog).getByLabelText('Name'), 'statements mail')
    await userEvent.type(within(dialog).getByLabelText('Server address'), 'smtp.bank.example')
    await userEvent.type(within(dialog).getByLabelText('From address'), 'a@bank.example')
    await userEvent.type(within(dialog).getByLabelText('Relay key'), 'other')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(within(dialog).getByText('A connection with that name already exists.')).toBeInTheDocument()
  })

  it('shows the duplicate-key error from the server under the key field', async () => {
    const drawer = await openConfiguration('Connections', relaysApi([], () => json({ code: 'email_relay.key_taken', title: 'Key taken' }, 409)).handler)
    const dialog = await chooseSmtp(drawer)
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Dup')
    await userEvent.type(within(dialog).getByLabelText('Relay key'), 'statements')
    await userEvent.type(within(dialog).getByLabelText('Server address'), 'smtp.bank.example')
    await userEvent.type(within(dialog).getByLabelText('From address'), 'a@bank.example')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(await within(dialog).findByText('A relay with this key already exists.')).toBeInTheDocument()
  })

  it('editing without typing a password keeps the saved one; the remove box clears it; the key is fixed', async () => {
    const api = relaysApi([relay()])
    const drawer = await openConfiguration('Connections', api.handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Statements mail' }))
    const dialog = screen.getByRole('dialog', { name: 'Statements mail' })

    expect(within(dialog).getByLabelText('Relay key')).toBeDisabled()
    expect(within(dialog).getByLabelText('Password')).toHaveValue('')
    await userEvent.clear(within(dialog).getByLabelText('Server address'))
    await userEvent.type(within(dialog).getByLabelText('Server address'), 'smtp2.bank.example')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))
    await within(drawer).findByText('Settings saved for Statements mail.')
    expect(api.bodies[0]).toMatchObject({ host: 'smtp2.bank.example', password: null, clearPassword: false })

    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Statements mail' }))
    const again = screen.getByRole('dialog', { name: 'Statements mail' })
    await userEvent.click(within(again).getByRole('checkbox', { name: 'Remove the saved password' }))
    await userEvent.click(within(again).getByRole('button', { name: 'Save settings' }))
    await within(drawer).findByText('Settings saved for Statements mail.')
    expect(api.bodies[1]).toMatchObject({ password: null, clearPassword: true })
  })

  it('Test connection sends one test message through the saved relay and reports success', async () => {
    const api = relaysApi([relay()])
    const drawer = await openConfiguration('Connections', api.handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Statements mail' }))
    const dialog = screen.getByRole('dialog', { name: 'Statements mail' })

    await userEvent.type(within(dialog).getByLabelText('Send the test to'), 'me@bank.example')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))

    expect(await within(dialog).findByText('Test message sent. Check the inbox.', {}, { timeout: 5000 })).toBeInTheDocument()
    expect(api.testBodies).toEqual([{ toAddress: 'me@bank.example' }])
    expect(api.calls).toContain('POST /api/v1/email-relays/r-1/tests')
  })

  it('Test connection shows why a test failed, with the error code', async () => {
    const api = relaysApi([relay()], undefined, { status: 'Failed', code: 'email.relay_unavailable', message: 'The SMTP relay could not be reached or did not accept the message.' })
    const drawer = await openConfiguration('Connections', api.handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Statements mail' }))
    const dialog = screen.getByRole('dialog', { name: 'Statements mail' })

    await userEvent.type(within(dialog).getByLabelText('Send the test to'), 'me@bank.example')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))

    const alert = await within(dialog).findByText(/The test failed: The SMTP relay could not be reached/, {}, { timeout: 5000 })
    expect(alert).toHaveTextContent('email.relay_unavailable')
  })

  it('Test connection is off for a switched-off relay, and a new connection says to create it first', async () => {
    const drawer = await openConfiguration('Connections', relaysApi([relay({ isActive: false })]).handler)
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Configure Statements mail' }))
    expect(within(screen.getByRole('dialog', { name: 'Statements mail' })).getByRole('button', { name: 'Test connection' })).toBeDisabled()
    await userEvent.click(within(screen.getByRole('dialog', { name: 'Statements mail' })).getByRole('button', { name: 'Cancel' }))

    const dialog = await chooseSmtp(drawer)
    expect(within(dialog).getByText(/After you create it, open Configure and use Test connection/)).toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: 'Test connection' })).not.toBeInTheDocument()
  })

  it('the switch deactivates and reactivates a relay', async () => {
    const api = relaysApi([relay()])
    const drawer = await openConfiguration('Connections', api.handler)
    const toggle = await within(drawer).findByRole('switch', { name: 'Enable Statements mail' })
    expect(toggle).toHaveAttribute('aria-checked', 'true')

    await userEvent.click(toggle)
    expect(await within(drawer).findByText('Statements mail turned off.')).toBeInTheDocument()
    expect(api.calls).toContain('POST /api/v1/email-relays/r-1/deactivate')
    await userEvent.click(await within(drawer).findByRole('switch', { name: 'Enable Statements mail' }))
    expect(await within(drawer).findByText('Statements mail turned on.')).toBeInTheDocument()
  })

  it('people who are not administrators see no email relays and cannot create one', async () => {
    setSession({
      mode: 'Development',
      me: { id: 'dev.viewer', name: 'Vera Viewer', roles: ['Viewer'], permissions: { read: true, templateAuthoring: false, workflowAuthoring: false, operate: false, readPayload: false, readAudit: false, administer: false } },
      getToken: async () => 'dev:dev.viewer',
      signOut: () => undefined,
    })
    const api = relaysApi([relay()])
    const drawer = await openConfiguration('Connections', api.handler)
    await within(drawer).findByRole('heading', { name: 'Print house SFTP' })
    expect(within(drawer).queryByRole('heading', { name: 'Statements mail' })).not.toBeInTheDocument()
    expect(api.calls).toHaveLength(0) // nothing is requested that would be refused

    const dialog = await chooseSmtp(drawer)
    expect(within(dialog).getByText('Only administrators can create email connections.')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Create connection' })).toBeDisabled()
  })
})
