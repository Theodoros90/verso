import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { vi } from 'vitest'

import { routes } from '@/app/routes'

export const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

/** Renders the app at `/`, with the API answering via `handler` (health by default), and opens the gear drawer on a tab. */
export async function openConfiguration(
  tab: 'Connections' | 'Global variables' | 'Export / import',
  handler?: (url: string, init?: RequestInit) => Response | undefined | Promise<Response | undefined>,
) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      return (await handler?.(url, init)) ?? json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
    }),
  )
  render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />)
  await userEvent.click(screen.getByRole('button', { name: /^Configuration/ }))
  const drawer = screen.getByRole('dialog', { name: 'Configuration' })
  await userEvent.click(within(drawer).getByRole('tab', { name: tab }))
  return drawer
}
