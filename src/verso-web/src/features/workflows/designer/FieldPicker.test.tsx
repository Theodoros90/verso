import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it } from 'vitest'

import { FieldPicker } from './FieldPicker'

function Harness({ paths, initial = '' }: { paths: string[]; initial?: string }) {
  const [value, setValue] = useState(initial)
  return <FieldPicker ariaLabel="Field" paths={paths} value={value} onChange={setValue} />
}

const optionTexts = () => [...screen.getByRole('combobox', { name: 'Field' }).querySelectorAll('option')].map((o) => o.textContent)

describe('FieldPicker', () => {
  it('is a dropdown of the available fields', async () => {
    const user = userEvent.setup()
    render(<Harness paths={['a.b', 'c']} />)
    expect(optionTexts()).toEqual(['Choose a field…', 'a.b', 'c'])
    const select = screen.getByRole('combobox', { name: 'Field' })
    expect(select).toHaveValue('')
    await user.selectOptions(select, 'c')
    expect(select).toHaveValue('c')
  })

  it('is an empty dropdown, never a text box, when no fields are known', () => {
    render(<Harness paths={[]} />)
    expect(optionTexts()).toEqual(['No fields available'])
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument()
    expect(screen.getByText(/Connect this step to the trigger/)).toBeInTheDocument()
  })

  it('keeps a saved path that is no longer available, marked as such', () => {
    render(<Harness paths={['a.b']} initial="old.field" />)
    expect(optionTexts()).toEqual(['Choose a field…', 'old.field (not available)', 'a.b'])
    expect(screen.getByRole('combobox', { name: 'Field' })).toHaveValue('old.field')
  })
})
