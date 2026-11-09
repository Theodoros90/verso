import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { HealthResponse } from '@/lib/api/health'

import { NAV_ITEMS } from './navigation'
import { routes } from './routes'

const healthy: HealthResponse = {
  status: 'Healthy',
  totalDurationMs: 9.4,
  checks: [{ name: 'database', status: 'Healthy', description: 'Connected; schema is up to date.', durationMs: 8.1, data: null }],
}

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] })
  render(<RouterProvider router={router} />)
  return router
}

describe('AppShell', () => {
  beforeEach(() => {
    // A fresh Response per call: a body can only be read once.
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response(JSON.stringify(healthy), { status: 200 }))))
  })

  it('shows every navigation item from the design reference, without the deferred AI and Integrations screens', () => {
    renderAt('/')
    const nav = screen.getByRole('navigation', { name: 'Main' })

    for (const item of NAV_ITEMS) {
      expect(within(nav).getByRole('link', { name: item.label })).toBeInTheDocument()
    }
    expect(within(nav).queryByRole('link', { name: /assistant/i })).not.toBeInTheDocument()
    expect(within(nav).queryByRole('link', { name: /integrations/i })).not.toBeInTheDocument()
  })

  it('marks the current page in the navigation and the heading', () => {
    renderAt('/executions')
    const nav = screen.getByRole('navigation', { name: 'Main' })

    expect(within(nav).getByRole('link', { name: 'Executions' })).toHaveAttribute('aria-current', 'page')
    expect(within(nav).getByRole('link', { name: 'Overview' })).not.toHaveAttribute('aria-current')
    expect(screen.getByRole('heading', { level: 1, name: 'Executions' })).toBeInTheDocument()
    expect(screen.getByText('Not built yet.')).toBeInTheDocument()
  })

  it('navigates when a sidebar link is clicked', async () => {
    const router = renderAt('/')
    const nav = screen.getByRole('navigation', { name: 'Main' })

    await userEvent.click(within(nav).getByRole('link', { name: 'Archive' }))

    expect(router.state.location.pathname).toBe('/archive')
    expect(screen.getByRole('heading', { level: 1, name: 'Archive' })).toBeInTheDocument()
  })

  it('shows the API status in the top bar and the database check on Overview', async () => {
    renderAt('/')

    expect(await screen.findByRole('link', { name: /API status: Healthy/ })).toBeInTheDocument()
    expect(await screen.findByText('Connected; schema is up to date.')).toBeInTheDocument()
    expect(screen.getByText('Database')).toBeInTheDocument()
  })

  it('explains when the API cannot be reached', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))
    renderAt('/')

    expect(await screen.findByRole('alert')).toHaveTextContent('Is Verso.Api running?')
    expect(screen.getByRole('link', { name: /API status: Unreachable/ })).toBeInTheDocument()
  })

  it('toggles between light and dark theme', async () => {
    renderAt('/')
    document.documentElement.dataset.theme = 'light'

    await userEvent.click(screen.getByRole('button', { name: 'Switch to dark theme' }))
    expect(document.documentElement.dataset.theme).toBe('dark')

    await userEvent.click(screen.getByRole('button', { name: 'Switch to light theme' }))
    expect(document.documentElement.dataset.theme).toBe('light')
  })

  it('shows a not-found message for unknown paths', () => {
    renderAt('/nope')

    expect(screen.getByText(/doesn’t exist/)).toBeInTheDocument()
  })
})
