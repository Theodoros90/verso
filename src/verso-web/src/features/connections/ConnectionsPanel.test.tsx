import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { openConfiguration } from '@/test/configuration'

import { abbreviate, readConnections, seedConnections, toExported } from './connections'

const renderPage = () => openConfiguration('Connections')

describe('Connections tab', () => {
  it('lives in the gear drawer (not the sidebar) and groups the connections with their status', async () => {
    await renderPage()
    expect(screen.queryByRole('link', { name: 'Connections', hidden: true })).not.toBeInTheDocument() // the modal drawer hides the sidebar

    for (const group of ['Data sources', 'Delivery channels', 'Signing & storage']) {
      expect(await screen.findByRole('heading', { level: 2, name: group })).toBeInTheDocument()
    }
    const sftp = screen.getByRole('heading', { name: 'Print house SFTP' }).closest('li')!
    expect(within(sftp).getByText('Needs attention')).toBeInTheDocument()
    expect(within(sftp).getByText('used by 3 steps')).toBeInTheDocument()
    expect(within(sftp).getByText(/SSH key · sftp\.cyprusprintservices/)).toBeInTheDocument()

    const salesforce = screen.getByRole('heading', { name: 'Salesforce' }).closest('li')!
    expect(within(salesforce).getByText('Off')).toBeInTheDocument()
    expect(within(salesforce).getByRole('switch', { name: 'Enable Salesforce' })).toHaveAttribute('aria-checked', 'false')
  })

  it('turns a connection on and off', async () => {
    await renderPage()
    const sw = await screen.findByRole('switch', { name: 'Enable Salesforce' })
    await userEvent.click(sw)
    expect(sw).toHaveAttribute('aria-checked', 'true')
    expect(within(screen.getByRole('dialog', { name: 'Configuration' })).getByRole('status')).toHaveTextContent('Salesforce turned on.')
    const card = screen.getByRole('heading', { name: 'Salesforce' }).closest('li')!
    expect(within(card).getByText('Connected')).toBeInTheDocument()
  })

  it('creates a connection, rejecting a missing endpoint and a duplicate name', async () => {
    await renderPage()
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

    expect(screen.queryByRole('dialog', { name: 'New connection' })).not.toBeInTheDocument()
    const card = screen.getByRole('heading', { name: 'Finance API' }).closest('li')!
    expect(within(card).getByText(/https:\/\/finance\.example\.test/)).toBeInTheDocument()
    expect(card.closest('section')).toHaveAccessibleName('Delivery channels')
  })

  it('configures a connection and runs a simulated test, with no secret field', async () => {
    await renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Configure Print house SFTP' }))
    const dialog = screen.getByRole('dialog', { name: 'Print house SFTP' })

    expect(within(dialog).getByLabelText('Secret (managed outside Verso)')).toBeDisabled()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Test connection' }))
    expect(await within(dialog).findByText(/Simulated: connected in \d+ ms, but the credential needs attention/)).toBeInTheDocument()

    await userEvent.clear(within(dialog).getByLabelText('Endpoint / host'))
    await userEvent.type(within(dialog).getByLabelText('Endpoint / host'), 'sftp.new.example.test:22')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save settings' }))
    expect(within(screen.getByRole('dialog', { name: 'Configuration' })).getByRole('status')).toHaveTextContent('Settings saved for Print house SFTP.')
    expect(screen.getByText(/sftp\.new\.example\.test:22/)).toBeInTheDocument()
  })
})

describe('connections model', () => {
  it('exports configuration only: no secret and no internal id', () => {
    const text = JSON.stringify(seedConnections().map(toExported))
    expect(text).not.toMatch(/secret|password|"id"/i)
  })

  it('reads the connections of a package and skips invalid entries', () => {
    const { items, skipped } = readConnections([
      { name: 'Treasury API', group: 'Data sources', endpoint: 'https://treasury.example.test', auth: 'API key' },
      { name: 'No endpoint', group: 'Data sources' },
      { name: 'Bad group', group: 'Nope', endpoint: 'x' },
    ])
    expect(items.map((i) => i.name)).toEqual(['Treasury API'])
    expect(items[0]).toMatchObject({ enabled: true, health: 'ok', usedBySteps: 0, abbr: 'TA' })
    expect(skipped).toBe(2)
  })

  it('derives logo letters from the name', () => {
    expect(abbreviate('Finance API')).toBe('FA')
    expect(abbreviate('Kafka')).toBe('KAF')
    expect(abbreviate('  ')).toBe('?')
  })
})
