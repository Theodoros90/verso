import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'

import { json } from '@/test/configuration'

import { ApiConnectionSelect } from './ApiConnectionSelect'

function stubOptions(options: unknown, status = 200) {
  const fetchMock = vi.fn(async (url: RequestInfo | URL, _init?: RequestInit) => (String(url) === '/api/v1/api-connection-options' ? json(options, status) : json({}, 404)))
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('ApiConnectionSelect', () => {
  it('lists the active connections as name and host and reports the chosen key', async () => {
    stubOptions([
      { key: 'rates', name: 'Exchange rates', host: 'rates.example.test' },
      { key: 'kyc', name: 'KYC service', host: 'kyc.example.test' },
    ])
    const onChange = vi.fn()
    render(<ApiConnectionSelect aria-label="API connection" value="" onChange={onChange} />)

    expect(await screen.findByRole('option', { name: 'Exchange rates (rates.example.test)' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'KYC service (kyc.example.test)' })).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'API connection' }), 'kyc')
    expect(onChange).toHaveBeenCalledWith('kyc')
  })

  it('says so when there are no connections yet', async () => {
    stubOptions([])
    render(<ApiConnectionSelect aria-label="API connection" value="" onChange={() => {}} />)
    expect(await screen.findByRole('option', { name: 'No API connections yet. Create one in Configuration, Connections.' })).toBeInTheDocument()
  })

  it('keeps a saved key that is not in the list, shown as an unknown connection', async () => {
    stubOptions([{ key: 'rates', name: 'Exchange rates', host: 'rates.example.test' }])
    render(<ApiConnectionSelect aria-label="API connection" value="old-api" onChange={() => {}} />)
    await waitFor(() => expect(screen.getByRole('option', { name: 'Unknown connection (old-api)' })).toBeInTheDocument())
    expect(screen.getByRole('combobox', { name: 'API connection' })).toHaveValue('old-api')
  })

  it('shows an error line and keeps the saved key when the list cannot be loaded', async () => {
    stubOptions({ title: 'Forbidden' }, 403)
    render(<ApiConnectionSelect aria-label="API connection" value="rates" onChange={() => {}} />)
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('The connections could not be loaded.')
    expect(screen.getByRole('combobox', { name: 'API connection' })).toHaveAccessibleDescription('The connections could not be loaded.')
    expect(screen.getByRole('combobox', { name: 'API connection' })).toHaveValue('rates')
  })

  it('asks for the options once per mount, even after re-rendering', async () => {
    const fetchMock = stubOptions([{ key: 'rates', name: 'Exchange rates', host: 'rates.example.test' }])
    const { rerender } = render(<ApiConnectionSelect aria-label="API connection" value="" onChange={() => {}} />)
    await screen.findByRole('option', { name: 'Exchange rates (rates.example.test)' })
    rerender(<ApiConnectionSelect aria-label="API connection" value="rates" onChange={() => {}} />)
    expect(fetchMock.mock.calls.filter(([u]) => String(u) === '/api/v1/api-connection-options')).toHaveLength(1)
  })

  it('cancels the request when it is removed before the answer arrives', async () => {
    const fetchMock = vi.fn((_url: RequestInfo | URL, init?: RequestInit) => new Promise<Response>((_resolve, reject) => init?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')))))
    vi.stubGlobal('fetch', fetchMock)
    const { unmount } = render(<ApiConnectionSelect aria-label="API connection" value="" onChange={() => {}} />)
    await waitFor(() => expect(fetchMock).toHaveBeenCalled())
    const signal = fetchMock.mock.calls[0]?.[1]?.signal as AbortSignal
    expect(signal.aborted).toBe(false)
    unmount()
    expect(signal.aborted).toBe(true)
  })
})
