import { LogIn, ShieldAlert } from 'lucide-react'
import { createContext, useCallback, useContext, useEffect, useState } from 'react'

import { Button } from '@/components/ui/button'
import type { AuthConfigDto, PermissionsDto } from '@/lib/api/generated'
import {
  currentSession,
  loadAuthConfig,
  restoreSession,
  setSession,
  signInAsDevelopmentUser,
  signInWithEntra,
  UNAUTHORIZED_EVENT,
  type Session,
} from '@/lib/auth'

const SessionContext = createContext<Session | null>(null)

/** The signed-in session. Only used below <AuthGate>, which renders nothing else until there is one. */
export function useSession(): Session {
  const session = useContext(SessionContext)
  if (!session) throw new Error('useSession must be used inside <AuthGate>.')
  return session
}

/** What the signed-in user may do, as computed by the API (the API enforces it; the UI only hides what would fail). */
export function usePermissions(): PermissionsDto {
  return useSession().me.permissions
}

type GateState =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'signIn'; config: AuthConfigDto }
  | { kind: 'noAccess'; session: Session }
  | { kind: 'ready'; session: Session }

const stateFor = (session: Session): GateState => (session.me.permissions.read ? { kind: 'ready', session } : { kind: 'noAccess', session })

/** Shows the app only to a signed-in user with a Verso role; otherwise sign-in, or an explanation. */
export function AuthGate({ children }: { children: React.ReactNode }) {
  const [state, setState] = useState<GateState>(() => {
    const existing = currentSession()
    return existing ? stateFor(existing) : { kind: 'loading' }
  })

  const start = useCallback(async () => {
    try {
      const config = await loadAuthConfig()
      const session = await restoreSession(config)
      setSession(session)
      setState(session ? stateFor(session) : { kind: 'signIn', config })
    } catch (e) {
      setState({ kind: 'error', message: e instanceof Error ? e.message : 'Sign-in failed.' })
    }
  }, [])

  useEffect(() => {
    if (!currentSession()) void start() // once on mount (start is stable); later restarts are explicit
  }, [start])

  useEffect(() => {
    // The API stopped accepting the token (expired, revoked): sign in again.
    const onUnauthorized = () => {
      setSession(null)
      setState({ kind: 'loading' })
      void start()
    }
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
  }, [start])

  if (state.kind === 'ready') return <SessionContext.Provider value={state.session}>{children}</SessionContext.Provider>

  return (
    <main className="grid min-h-screen place-items-center bg-[var(--background)] px-4">
      <section aria-labelledby="signin-h" className="w-full max-w-md rounded-[12px] border border-line bg-surface p-6 shadow-sm">
        <h1 id="signin-h" className="mb-4 text-[17px] font-semibold">
          Verso
        </h1>
        {state.kind === 'loading' && <p className="text-ink-3">Signing in…</p>}
        {state.kind === 'error' && (
          <>
            <p role="alert" className="mb-3 text-err">
              {state.message}
            </p>
            <Button onClick={() => { setState({ kind: 'loading' }); void start() }}>Try again</Button>
          </>
        )}
        {state.kind === 'signIn' && <SignIn config={state.config} onSignedIn={start} />}
        {state.kind === 'noAccess' && (
          <>
            <p className="mb-2 flex items-center gap-2 font-medium">
              <ShieldAlert className="size-4 text-warn" aria-hidden="true" /> No access to Verso
            </p>
            <p className="mb-4 text-ink-2">
              You’re signed in as <strong>{state.session.me.name}</strong>, but you don’t have a Verso role. Ask an administrator to assign one.
            </p>
            <Button onClick={state.session.signOut}>Sign out</Button>
          </>
        )}
      </section>
    </main>
  )
}

function SignIn({ config, onSignedIn }: { config: AuthConfigDto; onSignedIn: () => Promise<void> }) {
  const [busy, setBusy] = useState(false)

  if (config.mode === 'Development') {
    return (
      <>
        <p className="mb-1 font-medium">Development sign-in</p>
        <p className="mb-4 text-[13px] text-ink-3">Synthetic users, one role each. Only offered by an API running in Development.</p>
        <ul aria-label="Development users" className="flex flex-col gap-1.5">
          {(config.devUsers ?? []).map((u) => (
            <li key={u.id}>
              <button
                type="button"
                disabled={busy}
                onClick={async () => {
                  setBusy(true)
                  signInAsDevelopmentUser(u.token)
                  await onSignedIn()
                }}
                className="flex w-full items-center justify-between gap-3 rounded-lg border border-line px-3 py-2 text-left hover:border-cyan disabled:opacity-60"
              >
                <span className="font-medium">{u.name}</span>
                <span className="text-xs text-ink-3">{u.roles.length ? u.roles.join(', ') : 'no role'}</span>
              </button>
            </li>
          ))}
        </ul>
      </>
    )
  }

  return (
    <>
      <p className="mb-4 text-ink-2">Sign in with your organisation account.</p>
      <Button
        variant="primary"
        disabled={busy}
        onClick={async () => {
          setBusy(true)
          await signInWithEntra(config) // leaves the page and comes back signed in
        }}
      >
        <LogIn /> Sign in with Microsoft
      </Button>
    </>
  )
}
