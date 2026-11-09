import { useCallback, useEffect, useState } from 'react'

export type ThemePreference = 'system' | 'light' | 'dark'
export type ResolvedTheme = 'light' | 'dark'

const STORAGE_KEY = 'verso.theme'
const darkQuery = () => window.matchMedia('(prefers-color-scheme: dark)')

function readPreference(): ThemePreference {
  try {
    const value = localStorage.getItem(STORAGE_KEY)
    return value === 'light' || value === 'dark' ? value : 'system'
  } catch {
    return 'system' // storage blocked (private mode, policy): fall back to the system theme
  }
}

function writePreference(preference: ThemePreference) {
  try {
    if (preference === 'system') localStorage.removeItem(STORAGE_KEY)
    else localStorage.setItem(STORAGE_KEY, preference)
  } catch {
    // Not persisting is acceptable; the theme still applies for this session.
  }
}

export function resolveTheme(preference: ThemePreference, systemDark: boolean): ResolvedTheme {
  return preference === 'dark' || (preference === 'system' && systemDark) ? 'dark' : 'light'
}

/** Theme state for the toggle. Applies the result to <html data-theme>, which the CSS tokens key off. */
export function useTheme() {
  const [preference, setPreference] = useState<ThemePreference>(readPreference)
  const [systemDark, setSystemDark] = useState(() => darkQuery().matches)

  useEffect(() => {
    const query = darkQuery()
    const onChange = (e: MediaQueryListEvent) => setSystemDark(e.matches)
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [])

  const resolved = resolveTheme(preference, systemDark)

  useEffect(() => {
    document.documentElement.dataset.theme = resolved
  }, [resolved])

  const toggle = useCallback(() => {
    const next: ThemePreference = resolved === 'dark' ? 'light' : 'dark'
    setPreference(next)
    writePreference(next)
  }, [resolved])

  return { resolved, toggle }
}
