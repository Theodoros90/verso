import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { MeDto } from '@/lib/api/generated'
import { currentSession, setSession } from '@/lib/auth'
import { testAdmin } from '@/test/setup'

import { routes } from './routes'

// Entra mode without a tenant: MSAL is faked (no account yet, so the app offers sign-in).
vi.mock('@azure/msal-browser', () => {
  class PublicClientApplication {
    async initialize() {}
    async handleRedirectPromise() {
      return null
    }
    getActiveAccount() {
      return null
    }
    getAllAccounts() {
      return []
    }
  }
  return { PublicClientApplication, InteractionRequiredAuthError: class extends Error {} }
})

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const devConfig = {
  mode: 'Development',
  entra: null,
  devUsers: [
    { id: 'dev.viewer', name: 'Vera Viewer', roles: ['Viewer'], token: 'dev:dev.viewer' },
    { id: 'dev.no-access', name: 'Nico No-Access', roles: [], token: 'dev:dev.no-access' },
  ],
}

const viewer: MeDto = {
  id: 'dev.viewer',
  name: 'Vera Viewer',
  roles: ['Viewer'],
  permissions: { read: true, templateAuthoring: false, workflowAuthoring: false, operate: false, readPayload: false, readAudit: false, administer: false },
}

const noAccess: MeDto = { ...viewer, id: 'dev.no-access', name: 'Nico No-Access', roles: [], permissions: { ...viewer.permissions, read: false } }

/** API stub: who `/me` is depends on the bearer token sent. */
function stubApi(people: Record<string, MeDto>, config: unknown = devConfig) {
  const fetchMock = vi.fn(async (url: string, init: RequestInit = {}) => {
    const token = new Headers(init.headers).get('Authorization')?.replace('Bearer ', '')
    if (url === '/api/health') return json({ status: 'Healthy', totalDurationMs: 1, checks: [] })
    if (url === '/api/auth/config') return json(config)
    if (url === '/api/v1/me') return token && people[token] ? json(people[token]) : json({ code: 'unauthenticated' }, 401)
    if (url.startsWith('/api/v1/templates')) return token && people[token] ? json({ items: [], page: 1, pageSize: 100, total: 0 }) : json({ code: 'unauthenticated' }, 401)
    return json({ title: 'Not found' }, 404)
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

describe('Sign-in', () => {
  beforeEach(() => {
    setSession(null) // these tests start signed out
    sessionStorage.clear()
  })

  it('offers the synthetic users in Development, and signs in as the one chosen', async () => {
    const fetchMock = stubApi({ 'dev:dev.viewer': viewer })
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/templates'] })} />)
    const user = userEvent.setup()

    const users = await screen.findByRole('list', { name: 'Development users' })
    expect(within(users).getAllByRole('button').map((b) => b.textContent)).toEqual(['Vera ViewerViewer', 'Nico No-Accessno role'])
    await user.click(within(users).getByRole('button', { name: /Vera Viewer/ }))

    expect(await screen.findByRole('navigation', { name: 'Main' })).toBeInTheDocument()
    expect(screen.getByText('Vera Viewer')).toBeInTheDocument()
    expect(currentSession()?.me.id).toBe('dev.viewer')
    // Every API call now carries the token.
    const templates = fetchMock.mock.calls.find(([url]) => String(url).startsWith('/api/v1/templates'))!
    expect(new Headers((templates[1] as RequestInit).headers).get('Authorization')).toBe('Bearer dev:dev.viewer')
  })

  it('keeps the tab signed in across a reload (the token is in sessionStorage)', async () => {
    sessionStorage.setItem('verso.devToken', 'dev:dev.viewer')
    stubApi({ 'dev:dev.viewer': viewer })
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />)

    expect(await screen.findByRole('navigation', { name: 'Main' })).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Development users' })).not.toBeInTheDocument()
  })

  it('explains when the user has no Verso role, and shows nothing of the app', async () => {
    sessionStorage.setItem('verso.devToken', 'dev:dev.no-access')
    stubApi({ 'dev:dev.no-access': noAccess })
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/executions'] })} />)

    expect(await screen.findByText('No access to Verso')).toBeInTheDocument()
    expect(screen.getByText('Nico No-Access')).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'Main' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument()
  })

  it('asks to sign in again when a stored token is no longer accepted', async () => {
    sessionStorage.setItem('verso.devToken', 'dev:someone-removed')
    stubApi({})
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />)

    expect(await screen.findByRole('list', { name: 'Development users' })).toBeInTheDocument()
    expect(sessionStorage.getItem('verso.devToken')).toBeNull()
  })

  it('offers Microsoft sign-in when the API runs with Entra ID, and never lists synthetic users', async () => {
    stubApi({}, { mode: 'Entra', entra: { authority: 'https://login.microsoftonline.com/t', clientId: 'spa', scope: 'api://x/access_as_user' }, devUsers: null })
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/'] })} />)

    expect(await screen.findByRole('button', { name: /Sign in with Microsoft/ })).toBeInTheDocument()
    expect(screen.queryByRole('list', { name: 'Development users' })).not.toBeInTheDocument()
  })

  it('shows sign-in again when the API starts refusing the token', async () => {
    setSession({ mode: 'Development', me: testAdmin, getToken: async () => 'dev:expired', signOut: () => undefined })
    stubApi({}) // nobody: every authenticated call is 401
    render(<RouterProvider router={createMemoryRouter(routes, { initialEntries: ['/templates'] })} />)

    await waitFor(() => expect(screen.getByRole('list', { name: 'Development users' })).toBeInTheDocument())
    expect(currentSession()).toBeNull()
  })
})
