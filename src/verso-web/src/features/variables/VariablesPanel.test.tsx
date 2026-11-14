import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'

import { openConfiguration } from '@/test/configuration'

import { seedVariables, validateVariable, type VariableDraft } from './variables'

const renderPage = () => openConfiguration('Global variables')

const row = (name: string) => screen.getByText(`{{${name}}}`).closest('tr')!

describe('Global variables tab', () => {
  it('lives in the gear drawer (not the sidebar) and lists variables, masking sensitive values', async () => {
    await renderPage()
    expect(screen.queryByRole('link', { name: 'Global variables', hidden: true })).not.toBeInTheDocument() // the modal drawer hides the sidebar

    expect(await screen.findByText('6 variables')).toBeInTheDocument()
    expect(within(row('vatRate')).getByText('19')).toBeInTheDocument()
    const secret = row('portalApiKey')
    expect(within(secret).getByText('••••••••••••••••')).toBeInTheDocument()
    expect(within(secret).getAllByText('Secret')).toHaveLength(2) // the type and the Sensitive pill
  })

  it('creates a variable, validating the name and value', async () => {
    await renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'New variable' }))
    const dialog = screen.getByRole('dialog', { name: 'New variable' })

    await userEvent.type(within(dialog).getByLabelText('Name'), 'bad name')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create variable' }))
    expect(within(dialog).getByText(/Use letters, numbers and underscore/)).toBeInTheDocument()
    expect(within(dialog).getByText('A value is required.')).toBeInTheDocument()

    await userEvent.clear(within(dialog).getByLabelText('Name'))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'VATRATE')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create variable' }))
    expect(within(dialog).getByText('A variable with that name already exists.')).toBeInTheDocument()

    await userEvent.clear(within(dialog).getByLabelText('Name'))
    await userEvent.type(within(dialog).getByLabelText('Name'), 'supportEmail')
    await userEvent.type(within(dialog).getByLabelText('Value'), 'help@example.test')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create variable' }))

    expect(screen.queryByRole('dialog', { name: 'New variable' })).not.toBeInTheDocument()
    expect(within(row('supportEmail')).getByText('help@example.test')).toBeInTheDocument()
    expect(screen.getByText('7 variables')).toBeInTheDocument()
    expect(within(screen.getByRole('dialog', { name: 'Configuration' })).getByRole('status')).toHaveTextContent('Variable {{supportEmail}} created.')
  })

  it('edits a variable', async () => {
    await renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Edit {{vatRate}}' }))
    const dialog = screen.getByRole('dialog', { name: '{{vatRate}}' })
    await userEvent.clear(within(dialog).getByLabelText('Value'))
    await userEvent.type(within(dialog).getByLabelText('Value'), '21')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    expect(within(row('vatRate')).getByText('21')).toBeInTheDocument()
    expect(within(row('vatRate')).getByText('Just now')).toBeInTheDocument()
  })

  it('never asks for a secret value: marking a variable sensitive replaces the value field', async () => {
    await renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Edit {{companyName}}' }))
    const dialog = screen.getByRole('dialog', { name: '{{companyName}}' })
    await userEvent.click(within(dialog).getByRole('switch'))

    expect(within(dialog).getByLabelText('Value (managed outside Verso)')).toBeDisabled()
    expect(within(dialog).getByLabelText('Type')).toHaveValue('Secret')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save changes' }))

    const r = row('companyName')
    expect(within(r).queryByText('Halcyon Energy')).not.toBeInTheDocument()
    expect(within(r).getAllByText('Secret')).toHaveLength(2)
  })

  it('deletes a variable after confirmation', async () => {
    await renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Delete {{vatRate}}' }))
    const dialog = screen.getByRole('dialog', { name: 'Delete {{vatRate}}?' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    expect(screen.getByText('{{vatRate}}')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Delete {{vatRate}}' }))
    await userEvent.click(within(screen.getByRole('dialog', { name: 'Delete {{vatRate}}?' })).getByRole('button', { name: 'Delete variable' }))
    expect(screen.queryByText('{{vatRate}}')).not.toBeInTheDocument()
    expect(screen.getByText('5 variables')).toBeInTheDocument()
  })
})

describe('validateVariable', () => {
  const draft = (over: Partial<VariableDraft>): VariableDraft => ({ name: 'x', type: 'Text', scope: 'Tenant', env: 'All', value: 'v', sensitive: false, description: '', ...over })

  it('accepts a sensitive variable without a value and rejects a bad or duplicate name', () => {
    expect(validateVariable(draft({ sensitive: true, value: '' }), [])).toEqual({})
    expect(validateVariable(draft({ name: '1abc' }), []).name).toMatch(/starting with a letter/)
    expect(validateVariable(draft({ name: 'Dup' }), ['dup']).name).toMatch(/already exists/)
  })

  it('seeds one secret whose value is not held', () => {
    const secrets = seedVariables().filter((v) => v.sensitive)
    expect(secrets).toHaveLength(1)
    expect(secrets[0]!.value).toBe('')
  })
})
