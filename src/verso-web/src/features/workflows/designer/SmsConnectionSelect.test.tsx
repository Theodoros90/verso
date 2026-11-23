import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { json } from '@/test/configuration'

import { SmsConnectionSelect } from './SmsConnectionSelect'

const OPTIONS_URL = '/api/v1/sms-connection-options'
const SENDING_OFF = 'Sending is switched off for this connection. An administrator can switch it on in Configuration, Connections.'
const MARKETING = 'Marketing connections can\'t be used in workflows.'

function stubOptions(options: unknown, status = 200) {
  const fetchMock = vi.fn(async (url: RequestInfo | URL, _init?: RequestInit) => (String(url) === OPTIONS_URL ? json(options, status) : json({}, 404)))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

const options = [
  { key: 'notices', name: 'Customer notices', messageType: 'transactional', allowsSending: true },
  { key: 'alerts', name: 'Card alerts', messageType: 'transactional', allowsSending: false },
  { key: 'promo', name: 'Promotions', messageType: 'marketing', allowsSending: true },
]

describe('SmsConnectionSelect', () => {
  it('lists the active connections as name and type and reports the chosen key', async () => {
    stubOptions(options)
    const onChange = vi.fn()
    render(<SmsConnectionSelect aria-label="SMS connection" value="" onChange={onChange} />)

    expect(await screen.findByRole('option', { name: 'Customer notices (transactional)' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Promotions (marketing)' })).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'SMS connection' }), 'notices')
    expect(onChange).toHaveBeenCalledWith('notices')
  })

  it('says so when there are no connections yet', async () => {
    stubOptions([])
    render(<SmsConnectionSelect aria-label="SMS connection" value="" onChange={() => {}} />)
    expect(await screen.findByRole('option', { name: 'No SMS connections yet. Create one in Configuration, Connections.' })).toBeInTheDocument()
  })

  it('keeps a saved key that is not in the list, shown as an unknown connection', async () => {
    stubOptions(options)
    render(<SmsConnectionSelect aria-label="SMS connection" value="old-sms" onChange={() => {}} />)
    await waitFor(() => expect(screen.getByRole('option', { name: 'Unknown connection (old-sms)' })).toBeInTheDocument())
    expect(screen.getByRole('combobox', { name: 'SMS connection' })).toHaveValue('old-sms')
  })

  it('shows an error line and keeps the saved key when the list cannot be loaded', async () => {
    stubOptions({ title: 'Forbidden' }, 403)
    render(<SmsConnectionSelect aria-label="SMS connection" value="notices" onChange={() => {}} />)
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('The connections could not be loaded.')
    expect(screen.getByRole('combobox', { name: 'SMS connection' })).toHaveAccessibleDescription('The connections could not be loaded.')
    expect(screen.getByRole('combobox', { name: 'SMS connection' })).toHaveValue('notices')
  })

  it('asks for the options once per mount, even after re-rendering', async () => {
    const fetchMock = stubOptions(options)
    const { rerender } = render(<SmsConnectionSelect aria-label="SMS connection" value="" onChange={() => {}} />)
    await screen.findByRole('option', { name: 'Customer notices (transactional)' })
    rerender(<SmsConnectionSelect aria-label="SMS connection" value="notices" onChange={() => {}} />)
    expect(fetchMock.mock.calls.filter(([u]) => String(u) === OPTIONS_URL)).toHaveLength(1)
  })

  it('with the sending flag, marks and warns about a connection whose sending is switched off', async () => {
    stubOptions(options)
    render(<SmsConnectionSelect aria-label="SMS connection" requiresSending value="alerts" onChange={() => {}} />)

    expect(await screen.findByRole('option', { name: 'Card alerts (transactional), sending off' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Customer notices (transactional)' })).toBeInTheDocument()
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(SENDING_OFF)
    expect(screen.getByRole('combobox', { name: 'SMS connection' })).toHaveAccessibleDescription(SENDING_OFF)
  })

  it('without the sending flag, does not warn about sending', async () => {
    stubOptions(options)
    render(<SmsConnectionSelect aria-label="SMS connection" value="alerts" onChange={() => {}} />)
    expect(await screen.findByRole('option', { name: 'Card alerts (transactional)' })).toBeInTheDocument()
    expect(screen.queryByText(SENDING_OFF)).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('a connection that can send shows no warning', async () => {
    stubOptions(options)
    render(<SmsConnectionSelect aria-label="SMS connection" requiresSending value="notices" onChange={() => {}} />)
    await screen.findByRole('option', { name: 'Customer notices (transactional)' })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('warns that a marketing connection cannot be used in workflows', async () => {
    stubOptions(options)
    render(<SmsConnectionSelect aria-label="SMS connection" requiresSending value="promo" onChange={() => {}} />)
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(MARKETING)
    expect(screen.getByRole('combobox', { name: 'SMS connection' })).toHaveAccessibleDescription(MARKETING)
  })

  it('cancels the request when it is removed before the answer arrives', async () => {
    const fetchMock = vi.fn((_url: RequestInfo | URL, init?: RequestInit) => new Promise<Response>((_resolve, reject) => init?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')))))
    vi.stubGlobal('fetch', fetchMock)
    const { unmount } = render(<SmsConnectionSelect aria-label="SMS connection" value="" onChange={() => {}} />)
    await waitFor(() => expect(fetchMock).toHaveBeenCalled())
    const signal = fetchMock.mock.calls[0]?.[1]?.signal as AbortSignal
    expect(signal.aborted).toBe(false)
    unmount()
    expect(signal.aborted).toBe(true)
  })
})
