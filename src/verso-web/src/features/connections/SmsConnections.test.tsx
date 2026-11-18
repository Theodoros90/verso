import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import type { SmsConnectionDto } from '@/lib/api/generated'
import { setSession } from '@/lib/auth'
import { json, openConfiguration } from '@/test/configuration'

import { normaliseRecipient } from './SmsFields'

const connection = (over: Partial<SmsConnectionDto> = {}): SmsConnectionDto => ({
  id: 's-1',
  key: 'brevo-sms',
  name: 'Customer SMS',
  provider: 'brevo',
  baseUrl: 'https://api.brevo.com/v3',
  sender: 'TestSender',
  messageType: 'transactional',
  unicodeEnabled: true,
  organisationPrefix: 'TestOrg',
  timeoutSeconds: 15,
  description: 'for tests',
  isActive: true,
  hasSecret: true,
  updatedAt: '2026-11-17T10:00:00Z',
  updatedBy: 'dev.admin',
  ...over,
})

type Outcome = Partial<{ status: string; code: string; message: string; httpStatus: number; elapsedMs: number; messageId: string; smsCount: number; remainingCredits: number }>

/** A tiny in-memory SMS connections API that remembers what it was sent. */
function smsApi(
  initial: SmsConnectionDto[],
  fail?: () => Response | undefined,
  outcome: Outcome = { status: 'Succeeded', httpStatus: 201, elapsedMs: 88, messageId: '9001', smsCount: 1, remainingCredits: 41.5 },
  requestTestFailure?: Response,
) {
  const items = [...initial]
  const bodies: Record<string, unknown>[] = []
  const calls: string[] = []
  const handler = (url: string, init?: RequestInit) => {
    if (!url.startsWith('/api/v1/sms-connections')) return undefined
    const method = init?.method ?? 'GET'
    calls.push(`${method} ${url}`)
    if (method === 'GET' && url.includes('/tests/')) return json({ id: 't1', connectionKey: 'brevo-sms', requestedAt: '2026-11-17T10:00:00Z', ...outcome })
    if (method === 'GET') return json(items)
    if (method === 'POST' && url.endsWith('/tests')) {
      bodies.push(JSON.parse(String(init!.body)) as Record<string, unknown>)
      return requestTestFailure ?? json({ id: 't1', connectionKey: 'brevo-sms', status: 'Queued', requestedAt: '2026-11-17T10:00:00Z' }, 202)
    }
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
      const created = connection({ id: `s-${items.length + 1}`, key: String(body.key), name: String(body.name), sender: String(body.sender), messageType: String(body.messageType), hasSecret: !!body.secret })
      items.push(created)
      return json(created, 201)
    }
    const c = items.find((x) => url.includes(x.id))!
    Object.assign(c, { name: body.name, sender: body.sender, hasSecret: body.secret ? true : c.hasSecret })
    return json(c)
  }
  return { handler, bodies, calls }
}

async function chooseSms(drawer: HTMLElement) {
  await userEvent.click(await within(drawer).findByRole('button', { name: 'New connection' }))
  const dialog = screen.getByRole('dialog', { name: 'New connection' })
  await userEvent.selectOptions(within(dialog).getByLabelText('Authentication'), 'SMS (Brevo)')
  return dialog
}

async function openEdit(drawer: HTMLElement, name = 'Customer SMS') {
  await userEvent.click(await within(drawer).findByRole('button', { name: `Configure ${name}` }))
  return screen.getByRole('dialog', { name })
}

describe('normaliseRecipient', () => {
  it('reads international numbers with spaces, dashes, brackets, + or 00', () => {
    expect(normaliseRecipient('+357 99 000000')).toBe('35799000000')
    expect(normaliseRecipient('0035799000000')).toBe('35799000000')
    expect(normaliseRecipient('+(357) 99-000.000')).toBe('35799000000')
  })

  it('refuses numbers that are too short, too long, start with 0 or have letters', () => {
    expect(normaliseRecipient('')).toBeNull()
    expect(normaliseRecipient('1234567')).toBeNull()
    expect(normaliseRecipient('+0357 99 000000')).toBeNull()
    expect(normaliseRecipient('+357 99 00000a')).toBeNull()
    expect(normaliseRecipient('+3579900000012345')).toBeNull()
  })
})

describe('SMS connections (real Brevo SMS in the Connections tab)', () => {
  it('shows the real connections as cards in Delivery channels, never the key', async () => {
    const drawer = await openConfiguration('Connections', smsApi([connection(), connection({ id: 's-2', key: 'promo', name: 'Promo SMS', messageType: 'marketing', isActive: false })]).handler)

    const card = (await within(drawer).findByRole('heading', { name: 'Customer SMS' })).closest('li')!
    expect(card.closest('section')).toHaveAccessibleName('Delivery channels')
    expect(within(card).getByText('Ready')).toBeInTheDocument()
    expect(within(card).getByText('Key saved')).toBeInTheDocument()
    expect(within(card).getByText(/TestSender · transactional/)).toBeInTheDocument()
    const other = within(drawer).getByRole('heading', { name: 'Promo SMS' }).closest('li')!
    expect(within(other).getByText('Off')).toBeInTheDocument()
    expect(drawer.querySelectorAll('input[type=password]')).toHaveLength(0) // a card never holds a key field
  })

  it('choosing SMS (Brevo) turns New connection into the real SMS form, with Brevo defaults', async () => {
    const drawer = await openConfiguration('Connections', smsApi([]).handler)
    const dialog = await chooseSms(drawer)

    expect(within(dialog).getByLabelText('Group')).toHaveValue('Delivery channels')
    expect(within(dialog).getByLabelText('Base address')).toHaveValue('https://api.brevo.com/v3')
    expect(within(dialog).getByLabelText('Type')).toHaveValue('transactional')
    expect(within(dialog).getByRole('switch', { name: /Allow Unicode/ })).toBeChecked()
    expect(within(dialog).getByLabelText('API key')).toHaveAttribute('type', 'password')
    expect(within(dialog).getByLabelText('Sender')).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Organisation prefix (optional)')).toBeInTheDocument()
    expect(within(dialog).queryByLabelText('Secret (managed outside Verso)')).not.toBeInTheDocument()
    expect(within(dialog).queryByRole('button', { name: 'Test connection' })).not.toBeInTheDocument()
    expect(within(dialog).getByText(/After you create it, open Configure and use Test connection/)).toBeInTheDocument()
  })

  it('warns about consent when the type is marketing', async () => {
    const drawer = await openConfiguration('Connections', smsApi([]).handler)
    const dialog = await chooseSms(drawer)
    expect(within(dialog).queryByText(/needs the recipient’s consent/)).not.toBeInTheDocument()

    await userEvent.selectOptions(within(dialog).getByLabelText('Type'), 'marketing')

    expect(within(dialog).getByText(/needs the recipient’s consent and opt-out wording/)).toBeInTheDocument()
  })

  it('creates a connection, sending the API key once and never showing it again', async () => {
    const api = smsApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseSms(drawer)

    await userEvent.type(within(dialog).getByLabelText('Name'), 'Customer SMS')
    await userEvent.type(within(dialog).getByLabelText('Sender'), 'TestSender')
    await userEvent.type(within(dialog).getByLabelText('Organisation prefix (optional)'), 'TestOrg')
    await userEvent.type(within(dialog).getByLabelText('API key'), 'test-key-not-real')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'brevo-sms')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(await within(drawer).findByRole('heading', { name: 'Customer SMS' })).toBeInTheDocument()
    expect(within(drawer).getByText('SMS connection “Customer SMS” created.')).toBeInTheDocument()
    expect(api.bodies[0]).toMatchObject({
      key: 'brevo-sms',
      provider: 'brevo',
      baseUrl: 'https://api.brevo.com/v3',
      sender: 'TestSender',
      messageType: 'transactional',
      unicodeEnabled: true,
      organisationPrefix: 'TestOrg',
      secret: 'test-key-not-real',
    })
    expect(document.body.textContent).not.toContain('test-key-not-real')
  })

  it('checks name, sender, key, API key and address before sending anything', async () => {
    const api = smsApi([])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await chooseSms(drawer)
    await userEvent.clear(within(dialog).getByLabelText('Base address'))
    await userEvent.type(within(dialog).getByLabelText('Base address'), 'http://insecure.example.test')
    await userEvent.type(within(dialog).getByLabelText('Sender'), 'Has Space')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'Bad Key')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(within(dialog).getByText('Give the connection a name.')).toBeInTheDocument()
    expect(within(dialog).getByText(/Enter the base address starting with https:\/\//)).toBeInTheDocument()
    expect(within(dialog).getByText(/Enter a sender of up to 11 letters and digits/)).toBeInTheDocument()
    expect(within(dialog).getByText('Lower-case letters, digits and hyphens, 2–64 characters.')).toBeInTheDocument()
    expect(within(dialog).getByText('Enter the Brevo API key.')).toBeInTheDocument()
    expect(api.bodies).toHaveLength(0)
  })

  it('shows the duplicate-key error from the server under the key field', async () => {
    const drawer = await openConfiguration('Connections', smsApi([], () => json({ code: 'sms_connection.key_taken', title: 'Key taken' }, 409)).handler)
    const dialog = await chooseSms(drawer)
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Dup')
    await userEvent.type(within(dialog).getByLabelText('Sender'), 'Verso')
    await userEvent.type(within(dialog).getByLabelText('API key'), 'test-key-not-real')
    await userEvent.type(within(dialog).getByLabelText('Connection key'), 'brevo-sms')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(await within(dialog).findByText('A connection with this key already exists.')).toBeInTheDocument()
  })

  it('editing without typing an API key keeps the saved one; the key of the connection is fixed', async () => {
    const api = smsApi([connection()])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await openEdit(drawer)

    expect(within(dialog).getByLabelText('Connection key')).toBeDisabled()
    expect(within(dialog).getByLabelText('API key')).toHaveValue('')
    expect(within(dialog).getByText('A key is saved. Leave empty to keep it; typing replaces it.')).toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))

    await within(drawer).findByText('Settings saved for Customer SMS.')
    expect(api.bodies[0]).toMatchObject({ sender: 'TestSender', secret: null })
  })

  it('Test connection asks for a number, sends it to the Worker, and shows only status, time, parts, credits and message id', async () => {
    const api = smsApi([connection()])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await openEdit(drawer)
    expect(within(dialog).queryByLabelText('Send the test SMS to')).not.toBeInTheDocument() // nothing is sent until a number is typed

    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))
    expect(within(dialog).getByText(/This sends one real SMS and uses one credit/)).toBeInTheDocument()
    await userEvent.type(within(dialog).getByLabelText('Send the test SMS to'), '+357 99 000000')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Send test SMS' }))

    expect(await within(dialog).findByText('SMS accepted by the provider · HTTP 201 · 88 ms · 1 SMS part · 41.5 credits left · message 9001.', {}, { timeout: 5000 })).toBeInTheDocument()
    expect(api.calls).toContain('POST /api/v1/sms-connections/s-1/tests')
    expect(api.bodies[0]).toEqual({ recipient: '+357 99 000000' })
  })

  it('refuses a number that is not in international format without calling the server', async () => {
    const api = smsApi([connection()])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await openEdit(drawer)

    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))
    await userEvent.type(within(dialog).getByLabelText('Send the test SMS to'), '12345')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Send test SMS' }))

    expect(within(dialog).getByText('Enter the phone number in international format, such as +357 99 000000.')).toBeInTheDocument()
    expect(api.calls.some((c) => c.endsWith('/tests'))).toBe(false)
  })

  it('Test connection shows why a test failed, with the code and the HTTP status', async () => {
    const api = smsApi([connection()], undefined, { status: 'Failed', code: 'sms.unauthorized', message: 'The SMS provider refused the API key (HTTP 401 or 403). Check the key and that this server may use it.', httpStatus: 401 })
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await openEdit(drawer)

    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))
    await userEvent.type(within(dialog).getByLabelText('Send the test SMS to'), '+35799000000')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Send test SMS' }))

    const alert = await within(dialog).findByText(/The test failed: The SMS provider refused the API key/, {}, { timeout: 5000 })
    expect(alert).toHaveTextContent('sms.unauthorized')
    expect(alert).toHaveTextContent('HTTP 401')
  })

  it('shows the too-soon refusal when a test was just sent', async () => {
    const tooSoon = json({ code: 'sms_connection.test_too_soon', title: 'Too soon', detail: 'A test was sent a moment ago. Each test sends a real SMS: wait 30 seconds and try again.' }, 409)
    const api = smsApi([connection()], undefined, undefined, tooSoon)
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await openEdit(drawer)

    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))
    await userEvent.type(within(dialog).getByLabelText('Send the test SMS to'), '+35799000000')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Send test SMS' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/wait 30 seconds|Too soon/)
  })

  it('Test connection is off for a switched-off connection, and the switch turns it on and off', async () => {
    const api = smsApi([connection({ isActive: false })])
    const drawer = await openConfiguration('Connections', api.handler)
    const dialog = await openEdit(drawer)
    expect(within(dialog).getByRole('button', { name: 'Test connection' })).toBeDisabled()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))

    await userEvent.click(await within(drawer).findByRole('switch', { name: 'Enable Customer SMS' }))
    expect(await within(drawer).findByText('Customer SMS turned on.')).toBeInTheDocument()
    expect(api.calls).toContain('POST /api/v1/sms-connections/s-1/activate')
  })

  it('people who are not administrators see no SMS connections and cannot create one', async () => {
    setSession({
      mode: 'Development',
      me: { id: 'dev.viewer', name: 'Vera Viewer', roles: ['Viewer'], permissions: { read: true, templateAuthoring: false, workflowAuthoring: false, operate: false, readPayload: false, readAudit: false, administer: false } },
      getToken: async () => 'dev:dev.viewer',
      signOut: () => undefined,
    })
    const api = smsApi([connection()])
    const drawer = await openConfiguration('Connections', api.handler)
    await within(drawer).findByRole('heading', { name: 'Print house SFTP' })
    expect(within(drawer).queryByRole('heading', { name: 'Customer SMS' })).not.toBeInTheDocument()
    expect(api.calls).toHaveLength(0)

    const dialog = await chooseSms(drawer)
    expect(within(dialog).getByText('Only administrators can create SMS connections.')).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Create connection' })).toBeDisabled()
  })
})
