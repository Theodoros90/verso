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
  allowSending: false,
  allowedCountryPrefixes: '',
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
      const created = connection({
        id: `s-${items.length + 1}`,
        key: String(body.key),
        name: String(body.name),
        sender: String(body.sender),
        messageType: String(body.messageType),
        hasSecret: !!body.secret,
        allowSending: body.allowSending === true,
        allowedCountryPrefixes: typeof body.allowedCountryPrefixes === 'string' ? body.allowedCountryPrefixes : '',
      })
      items.push(created)
      return json(created, 201)
    }
    const c = items.find((x) => url.includes(x.id))!
    Object.assign(c, {
      name: body.name,
      sender: body.sender,
      hasSecret: body.secret ? true : c.hasSecret,
      allowSending: typeof body.allowSending === 'boolean' ? body.allowSending : c.allowSending,
      allowedCountryPrefixes: typeof body.allowedCountryPrefixes === 'string' ? body.allowedCountryPrefixes : c.allowedCountryPrefixes,
    })
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

  describe('Allow workflows to send SMS', () => {
    const switchName = 'Allow workflows to send SMS'
    const confirmText = 'Workflows will be able to send real SMS to numbers taken from their data, within the allowed countries. Each message costs a credit and cannot be recalled.'
    const prefixHint = 'Digits without +, separated by commas, for example 357, 30. Nothing can be sent to a country that is not listed.'

    const fillNew = async (dialog: HTMLElement) => {
      await userEvent.type(within(dialog).getByLabelText('Name'), 'Customer SMS')
      await userEvent.type(within(dialog).getByLabelText('Sender'), 'TestSender')
      await userEvent.type(within(dialog).getByLabelText('API key'), 'test-key-not-real')
      await userEvent.type(within(dialog).getByLabelText('Connection key'), 'brevo-sms')
    }

    it('is off for a new connection, with the prefixes field and its helper, and a new connection sends allowSending false', async () => {
      const api = smsApi([])
      const drawer = await openConfiguration('Connections', api.handler)
      const dialog = await chooseSms(drawer)

      expect(within(dialog).getByRole('switch', { name: switchName })).toHaveAttribute('aria-checked', 'false')
      expect(within(dialog).getByLabelText('Allowed country prefixes')).toHaveValue('')
      expect(within(dialog).getByText(prefixHint)).toBeInTheDocument()
      await fillNew(dialog)
      await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

      await within(drawer).findByText('SMS connection “Customer SMS” created.')
      expect(api.bodies[0]).toMatchObject({ allowSending: false, allowedCountryPrefixes: '' })
    })

    it('asks for confirmation when turned on; cancelling keeps it off', async () => {
      const drawer = await openConfiguration('Connections', smsApi([]).handler)
      const dialog = await chooseSms(drawer)

      await userEvent.click(within(dialog).getByRole('switch', { name: switchName }))
      expect(within(dialog).getByText(confirmText)).toBeInTheDocument()
      expect(within(dialog).getByRole('switch', { name: switchName })).toHaveAttribute('aria-checked', 'false')
      await userEvent.click(within(dialog).getByRole('button', { name: 'Keep sending off' }))

      expect(within(dialog).queryByText(confirmText)).not.toBeInTheDocument()
      expect(within(dialog).getByRole('switch', { name: switchName })).toHaveAttribute('aria-checked', 'false')
    })

    it('confirming turns it on; the request carries allowSending true and the prefixes; the card shows "Sending on"', async () => {
      const api = smsApi([])
      const drawer = await openConfiguration('Connections', api.handler)
      const dialog = await chooseSms(drawer)
      await fillNew(dialog)
      await userEvent.type(within(dialog).getByLabelText('Allowed country prefixes'), '357, 30')

      await userEvent.click(within(dialog).getByRole('switch', { name: switchName }))
      await userEvent.click(within(dialog).getByRole('button', { name: 'Allow sending' }))
      expect(within(dialog).getByRole('switch', { name: switchName })).toHaveAttribute('aria-checked', 'true')
      await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

      const card = (await within(drawer).findByRole('heading', { name: 'Customer SMS' })).closest('li')!
      expect(within(card).getByText('Sending on')).toBeInTheDocument()
      expect(api.bodies[0]).toMatchObject({ allowSending: true, allowedCountryPrefixes: '357, 30' })
    })

    it('the card says "Sending on" only for connections that allow sending, and its guidance follows the switch', async () => {
      const drawer = await openConfiguration('Connections', smsApi([connection({ allowSending: true, allowedCountryPrefixes: '357' }), connection({ id: 's-2', key: 'other', name: 'Other SMS' })]).handler)
      const on = (await within(drawer).findByRole('heading', { name: 'Customer SMS' })).closest('li')!
      const off = within(drawer).getByRole('heading', { name: 'Other SMS' }).closest('li')!
      expect(within(on).getByText('Sending on')).toBeInTheDocument()
      expect(within(on).getByText(/Workflows can send real SMS through it to numbers starting with 357/)).toBeInTheDocument()
      expect(within(off).queryByText('Sending on')).not.toBeInTheDocument()
      expect(within(off).getByText(/Test connection sends one real SMS to the number you type/)).toBeInTheDocument()
    })

    it('a marketing connection with sending on says it cannot be used in workflows, on the card and in the dialog', async () => {
      const drawer = await openConfiguration('Connections', smsApi([connection({ messageType: 'marketing', allowSending: true, allowedCountryPrefixes: '357' })]).handler)
      const card = (await within(drawer).findByRole('heading', { name: 'Customer SMS' })).closest('li')!
      expect(within(card).queryByText(/Workflows can send/)).not.toBeInTheDocument()
      expect(within(card).getByText(/Marketing connections can’t be used in workflows/)).toBeInTheDocument()
      expect(within(card).queryByText('Sending on')).not.toBeInTheDocument()
      expect(within(card).getByText('Not used in workflows')).toBeInTheDocument()

      const dialog = await openEdit(drawer)
      expect(dialog).not.toHaveTextContent(/Workflows can send/)
      expect(dialog).toHaveAccessibleDescription(/Marketing connections can’t be used in workflows/)
    })

    it('shows the server\'s message for invalid prefixes under the field', async () => {
      const problem = () => json({ title: 'Validation failed', errors: { AllowedCountryPrefixes: ['Each prefix is 1 to 4 digits that do not start with 0.'] } }, 400)
      const drawer = await openConfiguration('Connections', smsApi([connection()], problem).handler)
      const dialog = await openEdit(drawer)
      await userEvent.type(within(dialog).getByLabelText('Allowed country prefixes'), '0abc')
      await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))

      expect(await within(dialog).findByText('Each prefix is 1 to 4 digits that do not start with 0.')).toBeInTheDocument()
      expect(within(dialog).getByLabelText('Allowed country prefixes')).toHaveAttribute('aria-invalid', 'true')
    })

    describe('saving an existing connection (the switch is sent only when changed)', () => {
      it('an unrelated edit of a connection that allows sending sends no allowSending value', async () => {
        const api = smsApi([connection({ allowSending: true, allowedCountryPrefixes: '357' })])
        const drawer = await openConfiguration('Connections', api.handler)
        const dialog = await openEdit(drawer)
        expect(within(dialog).getByRole('switch', { name: switchName })).toHaveAttribute('aria-checked', 'true')
        expect(within(dialog).getByLabelText('Allowed country prefixes')).toHaveValue('357')
        await userEvent.clear(within(dialog).getByLabelText('Name'))
        await userEvent.type(within(dialog).getByLabelText('Name'), 'Customer SMS 2')
        await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))

        await within(drawer).findByText('Settings saved for Customer SMS 2.')
        expect(api.bodies[0]?.allowSending ?? null).toBeNull()
        expect(api.bodies[0]?.allowedCountryPrefixes ?? null).toBeNull() // a stale dialog never overwrites the saved list
        expect(api.bodies[0]).toMatchObject({ name: 'Customer SMS 2' })
      })

      it('editing the prefixes sends them', async () => {
        const api = smsApi([connection({ allowSending: true, allowedCountryPrefixes: '357' })])
        const drawer = await openConfiguration('Connections', api.handler)
        const dialog = await openEdit(drawer)
        await userEvent.type(within(dialog).getByLabelText('Allowed country prefixes'), ', 30')
        await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))

        await within(drawer).findByText('Settings saved for Customer SMS.')
        expect(api.bodies[0]).toMatchObject({ allowedCountryPrefixes: '357, 30' })
        expect(api.bodies[0]?.allowSending ?? null).toBeNull()
      })

      it('clearing the prefixes sends an empty list', async () => {
        const api = smsApi([connection({ allowSending: true, allowedCountryPrefixes: '357' })])
        const drawer = await openConfiguration('Connections', api.handler)
        const dialog = await openEdit(drawer)
        await userEvent.clear(within(dialog).getByLabelText('Allowed country prefixes'))
        await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))

        await within(drawer).findByText('Settings saved for Customer SMS.')
        expect(api.bodies[0]).toMatchObject({ allowedCountryPrefixes: '' })
      })

      it('turning it on, then confirming, sends true', async () => {
        const api = smsApi([connection()])
        const drawer = await openConfiguration('Connections', api.handler)
        const dialog = await openEdit(drawer)
        await userEvent.click(within(dialog).getByRole('switch', { name: switchName }))
        await userEvent.click(within(dialog).getByRole('button', { name: 'Allow sending' }))
        await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))

        await within(drawer).findByText('Settings saved for Customer SMS.')
        expect(api.bodies[0]).toMatchObject({ allowSending: true })
      })

      it('turning it on, then cancelling, sends nothing about sending', async () => {
        const api = smsApi([connection()])
        const drawer = await openConfiguration('Connections', api.handler)
        const dialog = await openEdit(drawer)
        await userEvent.click(within(dialog).getByRole('switch', { name: switchName }))
        await userEvent.click(within(dialog).getByRole('button', { name: 'Keep sending off' }))
        await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))

        await within(drawer).findByText('Settings saved for Customer SMS.')
        expect(api.bodies[0]?.allowSending ?? null).toBeNull()
      })

      it('turning it off needs no confirmation and sends false', async () => {
        const api = smsApi([connection({ allowSending: true, allowedCountryPrefixes: '357' })])
        const drawer = await openConfiguration('Connections', api.handler)
        const dialog = await openEdit(drawer)
        await userEvent.click(within(dialog).getByRole('switch', { name: switchName }))
        expect(within(dialog).queryByText(confirmText)).not.toBeInTheDocument()
        expect(within(dialog).getByRole('switch', { name: switchName })).toHaveAttribute('aria-checked', 'false')
        await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))

        await within(drawer).findByText('Settings saved for Customer SMS.')
        expect(api.bodies[0]).toMatchObject({ allowSending: false })
      })

      it('the confirmation starts on the safe choice and Escape cancels it without closing the dialog', async () => {
        const drawer = await openConfiguration('Connections', smsApi([connection()]).handler)
        const dialog = await openEdit(drawer)
        await userEvent.click(within(dialog).getByRole('switch', { name: switchName }))
        expect(within(dialog).getByRole('button', { name: 'Keep sending off' })).toHaveFocus()

        await userEvent.keyboard('{Escape}')

        expect(within(dialog).queryByRole('group', { name: 'Confirm sending' })).not.toBeInTheDocument()
        expect(screen.getByRole('dialog', { name: 'Customer SMS' })).toBeInTheDocument()
        expect(within(dialog).getByRole('switch', { name: switchName })).toHaveAttribute('aria-checked', 'false')
        await userEvent.keyboard('{Escape}')
        expect(screen.queryByRole('dialog', { name: 'Customer SMS' })).not.toBeInTheDocument()
      })
    })
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
