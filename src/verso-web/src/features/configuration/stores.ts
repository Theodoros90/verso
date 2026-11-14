import { useSyncExternalStore } from 'react'

import { seedConnections, type Connection } from '../connections/connections'
import { seedVariables, type GlobalVariable } from '../variables/variables'

// Connections, global variables and the environment label are frontend-only and simulated (no backend stores them, and
// no secret is ever kept). They live in a small shared store, so the Configuration drawer's tabs and its export see the
// same data, and are remembered in this browser. Storage can be missing or blocked: everything works without it.

type Updater<T> = T | ((current: T) => T)

function createStore<T>(storageKey: string, seed: () => T, isValid: (value: unknown) => value is T) {
  let value: T | undefined
  const listeners = new Set<() => void>()

  const load = (): T => {
    try {
      const raw = localStorage.getItem(storageKey)
      if (raw) {
        const parsed: unknown = JSON.parse(raw)
        if (isValid(parsed)) return parsed
      }
    } catch {
      // fall through to the seed
    }
    return seed()
  }
  const get = () => (value ??= load())
  const notify = () => listeners.forEach((l) => l())

  const set = (next: Updater<T>) => {
    value = typeof next === 'function' ? (next as (current: T) => T)(get()) : next
    try {
      localStorage.setItem(storageKey, JSON.stringify(value))
    } catch {
      // not remembered, still shared
    }
    notify()
  }
  const reset = () => {
    value = undefined
    try {
      localStorage.removeItem(storageKey)
    } catch {
      // nothing to remove
    }
    notify()
  }
  const subscribe = (listener: () => void) => {
    listeners.add(listener)
    return () => listeners.delete(listener)
  }

  return {
    get,
    set,
    reset,
    use: (): [T, (next: Updater<T>) => void] => [useSyncExternalStore(subscribe, get), set],
  }
}

const isList = (v: unknown): v is unknown[] => Array.isArray(v)

export const ENVIRONMENTS = ['Development', 'Test', 'Production'] as const
export type Environment = (typeof ENVIRONMENTS)[number]

const connections = createStore<Connection[]>('verso.connections', seedConnections, isList as (v: unknown) => v is Connection[])
const variables = createStore<GlobalVariable[]>('verso.variables', seedVariables, isList as (v: unknown) => v is GlobalVariable[])
const environment = createStore<Environment>('verso.environment', () => 'Test', (v): v is Environment => ENVIRONMENTS.includes(v as Environment))

export const connectionsStore = connections
export const variablesStore = variables
export const environmentStore = environment

export const useConnections = connections.use
export const useVariables = variables.use
export const useEnvironment = environment.use

/** Back to the synthetic starting data (tests, and a clean slate). */
export function resetConfigurationStores() {
  connections.reset()
  variables.reset()
  environment.reset()
}

let counter = 0
/** An id for an item created here or imported; unique across reloads, since items are remembered in the browser. */
export const newLocalId = () => `local-${Date.now().toString(36)}-${counter++}`
