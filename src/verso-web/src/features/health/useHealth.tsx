import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react'

import { fetchHealth, type HealthResult } from '@/lib/api/health'

const REFRESH_MS = 30_000

interface HealthState {
  /** null until the first check finishes. */
  result: HealthResult | null
  checking: boolean
  checkedAt: Date | null
  refresh: () => void
}

const HealthContext = createContext<HealthState | null>(null)

/** Polls GET /api/health every 30 s; one poller shared by the top bar and the Overview page. */
export function HealthProvider({ children }: { children: React.ReactNode }) {
  const [result, setResult] = useState<HealthResult | null>(null)
  const [checking, setChecking] = useState(false)
  const [checkedAt, setCheckedAt] = useState<Date | null>(null)
  const inFlight = useRef<AbortController | null>(null)

  const refresh = useCallback(() => {
    inFlight.current?.abort()
    const controller = new AbortController()
    inFlight.current = controller
    setChecking(true)
    fetchHealth(controller.signal)
      .then((r) => {
        setResult(r)
        setCheckedAt(new Date())
        setChecking(false)
      })
      .catch(() => {
        // Aborted by a newer check or unmount; the newer check owns the state.
      })
  }, [])

  useEffect(() => {
    refresh()
    const timer = window.setInterval(refresh, REFRESH_MS)
    return () => {
      window.clearInterval(timer)
      inFlight.current?.abort()
    }
  }, [refresh])

  return <HealthContext.Provider value={{ result, checking, checkedAt, refresh }}>{children}</HealthContext.Provider>
}

export function useHealth(): HealthState {
  const value = useContext(HealthContext)
  if (!value) throw new Error('useHealth must be used inside <HealthProvider>')
  return value
}
