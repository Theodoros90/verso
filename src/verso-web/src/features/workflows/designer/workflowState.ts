import { useMemo, useReducer } from 'react'

import { commitFrom, createHistory, push, redo, replace, undo, type History } from '@/lib/history'

import type { WorkflowDefinition } from '../model/definition'

export interface WorkflowState {
  history: History<WorkflowDefinition>
  /** As last loaded or saved; `history.present !== saved` means unsaved changes. */
  saved: WorkflowDefinition
  selection: string | null
  coalesceKey: string | null
}

type Action =
  | { type: 'edit'; fn: (d: WorkflowDefinition) => WorkflowDefinition; key?: string; select?: string | null }
  | { type: 'preview'; fn: (d: WorkflowDefinition) => WorkflowDefinition }
  | { type: 'commit'; origin: WorkflowDefinition }
  | { type: 'undo' }
  | { type: 'redo' }
  | { type: 'select'; id: string | null }
  | { type: 'saved'; def: WorkflowDefinition }

const keepSelection = (id: string | null, d: WorkflowDefinition) => (id && d.nodes.some((n) => n.id === id) ? id : null)

export function workflowReducer(state: WorkflowState, action: Action): WorkflowState {
  switch (action.type) {
    case 'edit': {
      const next = action.fn(state.history.present)
      if (next === state.history.present) return action.select !== undefined ? { ...state, selection: action.select } : state
      const coalesce = action.key !== undefined && action.key === state.coalesceKey
      const history = coalesce ? replace(state.history, next) : push(state.history, next)
      const selection = action.select !== undefined ? action.select : keepSelection(state.selection, next)
      return { ...state, history, selection, coalesceKey: action.key ?? null }
    }
    case 'preview':
      return { ...state, history: replace(state.history, action.fn(state.history.present)), coalesceKey: null }
    case 'commit':
      return { ...state, history: commitFrom(state.history, action.origin), coalesceKey: null }
    case 'undo': {
      const history = undo(state.history)
      return { ...state, history, selection: keepSelection(state.selection, history.present), coalesceKey: null }
    }
    case 'redo': {
      const history = redo(state.history)
      return { ...state, history, selection: keepSelection(state.selection, history.present), coalesceKey: null }
    }
    case 'select':
      return state.selection === action.id ? state : { ...state, selection: action.id, coalesceKey: null }
    case 'saved':
      return { ...state, saved: action.def, coalesceKey: null }
  }
}

export function useWorkflowState(initial: WorkflowDefinition) {
  const [state, dispatch] = useReducer(workflowReducer, initial, (def) => ({ history: createHistory(def), saved: def, selection: null, coalesceKey: null }))
  const actions = useMemo(
    () => ({
      edit: (fn: (d: WorkflowDefinition) => WorkflowDefinition, options: { key?: string; select?: string | null } = {}) => dispatch({ type: 'edit', fn, ...options }),
      preview: (fn: (d: WorkflowDefinition) => WorkflowDefinition) => dispatch({ type: 'preview', fn }),
      commit: (origin: WorkflowDefinition) => dispatch({ type: 'commit', origin }),
      undo: () => dispatch({ type: 'undo' }),
      redo: () => dispatch({ type: 'redo' }),
      select: (id: string | null) => dispatch({ type: 'select', id }),
      markSaved: (def: WorkflowDefinition) => dispatch({ type: 'saved', def }),
    }),
    [],
  )
  return [state, actions] as const
}
