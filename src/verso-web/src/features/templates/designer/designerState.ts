import { useMemo, useReducer } from 'react'

import { commitFrom, createHistory, push, redo, replace, undo, type History } from '../model/history'
import type { Layout, PageSettings, Region } from '../model/layout'

/** The editable content of a draft: exactly what PUT …/draft saves. */
export interface Draft {
  layout: Layout
  pageSettings: PageSettings
  dataSchema: unknown
  sampleData: unknown
}

export type Selection = { kind: 'element'; id: string } | { kind: 'region'; region: Region } | null

export interface DesignerState {
  history: History<Draft>
  /** The content as last loaded or saved; `history.present !== saved` means unsaved changes. */
  saved: Draft
  selection: Selection
  /** Consecutive edits with the same key (typing in one field) become one undo step. */
  coalesceKey: string | null
}

type Action =
  | { type: 'reset'; draft: Draft }
  | { type: 'edit'; fn: (d: Draft) => Draft; key?: string; select?: Selection }
  | { type: 'preview'; fn: (d: Draft) => Draft }
  | { type: 'commit'; origin: Draft }
  | { type: 'undo' }
  | { type: 'redo' }
  | { type: 'select'; selection: Selection }
  | { type: 'saved'; draft: Draft }

function selectionStillValid(selection: Selection, draft: Draft): Selection {
  if (selection?.kind !== 'element') return selection
  const { layout } = draft
  const exists =
    layout.header?.elements.some((e) => e.id === selection.id) ||
    layout.body?.blocks.some((e) => e.id === selection.id) ||
    layout.footer?.elements.some((e) => e.id === selection.id)
  return exists ? selection : null
}

export function designerReducer(state: DesignerState, action: Action): DesignerState {
  switch (action.type) {
    case 'reset':
      return { history: createHistory(action.draft), saved: action.draft, selection: null, coalesceKey: null }
    case 'edit': {
      const next = action.fn(state.history.present)
      if (next === state.history.present) return action.select !== undefined ? { ...state, selection: action.select } : state
      const coalesce = action.key !== undefined && action.key === state.coalesceKey
      const history = coalesce ? replace(state.history, next) : push(state.history, next)
      const selection = action.select !== undefined ? action.select : selectionStillValid(state.selection, next)
      return { ...state, history, selection, coalesceKey: action.key ?? null }
    }
    case 'preview':
      return { ...state, history: replace(state.history, action.fn(state.history.present)), coalesceKey: null }
    case 'commit':
      return { ...state, history: commitFrom(state.history, action.origin), coalesceKey: null }
    case 'undo': {
      const history = undo(state.history)
      return { ...state, history, selection: selectionStillValid(state.selection, history.present), coalesceKey: null }
    }
    case 'redo': {
      const history = redo(state.history)
      return { ...state, history, selection: selectionStillValid(state.selection, history.present), coalesceKey: null }
    }
    case 'select':
      return { ...state, selection: action.selection, coalesceKey: null }
    case 'saved':
      return { ...state, saved: action.draft, coalesceKey: null }
  }
}

export function useDesignerState(initial: Draft) {
  const [state, dispatch] = useReducer(designerReducer, initial, (draft) => ({
    history: createHistory(draft),
    saved: draft,
    selection: null,
    coalesceKey: null,
  }))

  const actions = useMemo(
    () => ({
      reset: (draft: Draft) => dispatch({ type: 'reset', draft }),
      /** An undoable change. Pass `key` to merge consecutive edits of one input into one undo step. */
      edit: (fn: (d: Draft) => Draft, options: { key?: string; select?: Selection } = {}) =>
        dispatch({ type: 'edit', fn, ...options }),
      editLayout: (fn: (l: Layout) => Layout, options: { key?: string; select?: Selection } = {}) =>
        dispatch({ type: 'edit', fn: (d) => withLayout(d, fn(d.layout)), ...options }),
      /** A change during a drag; call `commit` with the state from before the drag when it ends. */
      previewLayout: (fn: (l: Layout) => Layout) => dispatch({ type: 'preview', fn: (d) => withLayout(d, fn(d.layout)) }),
      commit: (origin: Draft) => dispatch({ type: 'commit', origin }),
      undo: () => dispatch({ type: 'undo' }),
      redo: () => dispatch({ type: 'redo' }),
      select: (selection: Selection) => dispatch({ type: 'select', selection }),
      markSaved: (draft: Draft) => dispatch({ type: 'saved', draft }),
    }),
    [],
  )

  return [state, actions] as const
}

function withLayout(d: Draft, layout: Layout): Draft {
  return layout === d.layout ? d : { ...d, layout }
}

export type DesignerActions = ReturnType<typeof useDesignerState>[1]
