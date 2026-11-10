/**
 * Sign-in (plan §7, M9). The API says which mode it runs in (`GET /api/auth/config`):
 * - Entra: MSAL (auth code + PKCE, redirect). Tokens come from MSAL's session cache and are renewed silently.
 * - Development: synthetic users (only offered by an API running in Development); the "token" is `dev:<user id>`,
 *   kept in sessionStorage for this tab.
 * Either way, the API client and the SignalR connection ask `getAccessToken()` for the bearer token.
 */
import { InteractionRequiredAuthError, PublicClientApplication, type AccountInfo } from '@azure/msal-browser'

import type { AuthConfigDto, MeDto } from './api/generated'

export interface Session {
  mode: string
  me: MeDto
  getToken: () => Promise<string | null>
  signOut: () => void
}

const DEV_TOKEN_KEY = 'verso.devToken'

let session: Session | null = null

/** The bearer token for API calls, or null when signed out. */
export async function getAccessToken(): Promise<string | null> {
  return session ? session.getToken() : null
}

export function currentSession(): Session | null {
  return session
}

/** Sets (or clears) the session. Also how tests sign in without the API (see src/test/setup.ts). */
export function setSession(next: Session | null) {
  session = next
}

/** Raised by the API client on a 401 while signed in (expired or revoked sign-in): the app shows sign-in again. */
export const UNAUTHORIZED_EVENT = 'verso:unauthorized'

export async function loadAuthConfig(): Promise<AuthConfigDto> {
  const response = await fetch('/api/auth/config', { headers: { Accept: 'application/json' } })
  if (!response.ok) throw new Error(`The API did not return its sign-in settings (HTTP ${response.status}). Is Verso.Api running?`)
  return (await response.json()) as AuthConfigDto
}

/** Who the token belongs to, as the API sees them (roles and permissions come from the API, never from the token here). */
async function fetchMe(token: string): Promise<MeDto> {
  const response = await fetch('/api/v1/me', { headers: { Accept: 'application/json', Authorization: `Bearer ${token}` } })
  if (response.status === 401) throw new SignInRejected()
  if (!response.ok) throw new Error(`Could not load your profile (HTTP ${response.status}).`)
  return (await response.json()) as MeDto
}

export class SignInRejected extends Error {
  constructor() {
    super('The API did not accept the sign-in. Sign in again.')
    this.name = 'SignInRejected'
  }
}

// ----- Development sign-in -----

export function signInAsDevelopmentUser(token: string) {
  sessionStorage.setItem(DEV_TOKEN_KEY, token)
}

function developmentSession(token: string, me: MeDto): Session {
  return {
    mode: 'Development',
    me,
    getToken: async () => token,
    signOut: () => {
      sessionStorage.removeItem(DEV_TOKEN_KEY)
      setSession(null)
      window.location.assign('/')
    },
  }
}

// ----- Entra ID (MSAL) -----

let msal: PublicClientApplication | null = null

async function msalFor(config: AuthConfigDto): Promise<PublicClientApplication> {
  if (!config.entra) throw new Error('The API is in Entra mode but returned no Entra settings.')
  if (!msal) {
    msal = new PublicClientApplication({
      auth: { clientId: config.entra.clientId, authority: config.entra.authority, redirectUri: window.location.origin, postLogoutRedirectUri: window.location.origin },
      cache: { cacheLocation: 'sessionStorage' }, // not shared across tabs, gone when the browser closes
    })
    await msal.initialize()
  }
  return msal
}

export async function signInWithEntra(config: AuthConfigDto) {
  const app = await msalFor(config)
  await app.loginRedirect({ scopes: [config.entra!.scope] })
}

function entraSession(app: PublicClientApplication, account: AccountInfo, scope: string, me: MeDto): Session {
  return {
    mode: 'Entra',
    me,
    getToken: async () => {
      try {
        return (await app.acquireTokenSilent({ scopes: [scope], account })).accessToken
      } catch (e) {
        if (e instanceof InteractionRequiredAuthError) {
          await app.acquireTokenRedirect({ scopes: [scope], account }) // leaves the page; returns after sign-in
          return null
        }
        throw e
      }
    },
    signOut: () => {
      setSession(null)
      void app.logoutRedirect({ account })
    },
  }
}

/**
 * Restores the session of this tab: the development token, or the MSAL account (including the one returning from
 * the sign-in redirect). Null when the user still has to sign in.
 */
export async function restoreSession(config: AuthConfigDto): Promise<Session | null> {
  if (config.mode === 'Development') {
    const token = sessionStorage.getItem(DEV_TOKEN_KEY)
    if (!token) return null
    try {
      return developmentSession(token, await fetchMe(token))
    } catch (e) {
      if (e instanceof SignInRejected) {
        sessionStorage.removeItem(DEV_TOKEN_KEY) // e.g. the user list changed
        return null
      }
      throw e
    }
  }

  const app = await msalFor(config)
  const redirect = await app.handleRedirectPromise()
  const account = redirect?.account ?? app.getActiveAccount() ?? app.getAllAccounts()[0]
  if (!account) return null
  app.setActiveAccount(account)
  const scope = config.entra!.scope
  const token = (await app.acquireTokenSilent({ scopes: [scope], account }).catch(() => null))?.accessToken
  if (!token) return null
  return entraSession(app, account, scope, await fetchMe(token))
}
