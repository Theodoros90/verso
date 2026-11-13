import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'

import { routes } from '@/app/routes'

import { abbreviate, parseConnections, seedConnections, serializeConnections } from './connections'

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })

function renderPage() {
  vi.stubGlobal('fetch', vi.fn(async () => json({ status: 'Healthy', totalDurationMs: 1, checks: [] })))
  render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/connections'] })} />)
}

describe('Connections page', () => {
  it('is in the navigation and groups the connections with their status', async () => {
    renderPage()
    const nav = screen.getByRole('navigation', { name: 'Main' })
    expect(within(nav).getByRole('link', { name: 'Connections' })).toHaveAttribute('aria-current', 'page')

    for (const group of ['Data sources', 'Delivery channels', 'Signing & storage']) {
      expect(await screen.findByRole('heading', { level: 2, name: group })).toBeInTheDocument()
    }
    const smtp = screen.getByRole('heading', { name: 'SMTP relay' }).closest('li')!
    expect(within(smtp).getByText('Failing')).toBeInTheDocument()
    expect(within(smtp).getByText('used by 3 steps')).toBeInTheDocument()
    expect(within(smtp).getByText(/SMTP credentials · smtp-relay/)).toBeInTheDocument()

    const salesforce = screen.getByRole('heading', { name: 'Salesforce' }).closest('li')!
    expect(within(salesforce).getByText('Off')).toBeInTheDocument()
    expect(within(salesforce).getByRole('switch', { name: 'Enable Salesforce' })).toHaveAttribute('aria-checked', 'false')
  })

  it('turns a connection on and off', async () => {
    renderPage()
    const sw = await screen.findByRole('switch', { name: 'Enable Salesforce' })
    await userEvent.click(sw)
    expect(sw).toHaveAttribute('aria-checked', 'true')
    expect(screen.getByRole('status')).toHaveTextContent('Salesforce turned on.')
    const card = screen.getByRole('heading', { name: 'Salesforce' }).closest('li')!
    expect(within(card).getByText('Connected')).toBeInTheDocument()
  })

  it('creates a connection, rejecting a missing endpoint and a duplicate name', async () => {
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'New connection' }))
    const dialog = screen.getByRole('dialog', { name: 'New connection' })

    await userEvent.type(within(dialog).getByLabelText('Name'), 'kafka')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))
    expect(within(dialog).getByText('A connection with that name already exists.')).toBeInTheDocument()
    expect(within(dialog).getByText('Endpoint or host is required.')).toBeInTheDocument()

    await userEvent.clear(within(dialog).getByLabelText('Name'))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Finance API')
    await userEvent.type(within(dialog).getByLabelText('Endpoint / host'), 'https://finance.example.test')
    await userEvent.selectOptions(within(dialog).getByLabelText('Group'), 'Delivery channels')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create connection' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    const card = screen.getByRole('heading', { name: 'Finance API' }).closest('li')!
    expect(within(card).getByText(/https:\/\/finance\.example\.test/)).toBeInTheDocument()
    expect(card.closest('section')).toHaveAccessibleName('Delivery channels')
  })

  it('configures a connection and runs a simulated test, with no secret field', async () => {
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Configure SMTP relay' }))
    const dialog = screen.getByRole('dialog', { name: 'SMTP relay' })

    expect(within(dialog).getByLabelText('Secret (managed outside Verso)')).toBeDisabled()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))
    expect(await within(dialog).findByText(/Simulated: the endpoint answered but rejected/)).toBeInTheDocument()

    await userEvent.clear(within(dialog).getByLabelText('Endpoint / host'))
    await userEvent.type(within(dialog).getByLabelText('Endpoint / host'), 'smtp.new.example.test:25')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))
    expect(screen.getByRole('status')).toHaveTextContent('Settings saved for SMTP relay.')
    expect(screen.getByText(/smtp\.new\.example\.test:25/)).toBeInTheDocument()
  })

  it('imports a connections file, skipping duplicates and invalid entries', async () => {
    renderPage()
    const file = new File(
      [
        JSON.stringify({
          format: 'verso-connections',
          version: 1,
          connections: [
            { name: 'Treasury API', group: 'Data sources', endpoint: 'https://treasury.example.test', auth: 'API key' },
            { name: 'Kafka', group: 'Data sources', endpoint: 'x' },
            { name: 'No endpoint', group: 'Data sources' },
          ],
        }),
      ],
      'c.json',
      { type: 'application/json' },
    )
    await userEvent.upload(await screen.findByLabelText('Import connections file'), file)

    expect(await screen.findByRole('heading', { name: 'Treasury API' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Imported 1 connection (1 already existed, 1 invalid).')
  })

  it('rejects a file that is not a connections export', async () => {
    renderPage()
    await userEvent.upload(await screen.findByLabelText('Import connections file'), new File(['{"hello":1}'], 'x.json', { type: 'application/json' }))
    expect(await screen.findByText('That file is not a Verso connections export.')).toBeInTheDocument()
  })
})

describe('connections model', () => {
  it('exports configuration only: no secret and no internal id', () => {
    const text = serializeConnections(seedConnections())
    expect(text).not.toMatch(/secret|password|"id"/i)
    const parsed = parseConnections(text)
    expect('items' in parsed && parsed.items).toHaveLength(seedConnections().length)
  })

  it('reports invalid JSON', () => {
    expect(parseConnections('nope')).toEqual({ error: 'That file is not valid JSON.' })
  })

  it('derives logo letters from the name', () => {
    expect(abbreviate('Finance API')).toBe('FA')
    expect(abbreviate('Kafka')).toBe('KAF')
    expect(abbreviate('  ')).toBe('?')
  })
})
