/** Undo/redo over immutable snapshots of the editable draft content. */

export interface History<T> {
  past: T[]
  present: T
  future: T[]
}

export const HISTORY_LIMIT = 200

export function createHistory<T>(present: T): History<T> {
  return { past: [], present, future: [] }
}

/** Records a new state. Same-object updates are ignored. */
export function push<T>(h: History<T>, next: T): History<T> {
  if (next === h.present) return h
  const past = [...h.past, h.present]
  if (past.length > HISTORY_LIMIT) past.shift()
  return { past, present: next, future: [] }
}

/** Replaces the present without an undo step (used while dragging; the drag start is the undo point). */
export function replace<T>(h: History<T>, next: T): History<T> {
  return next === h.present ? h : { ...h, present: next }
}

/** Ends a gesture that used `replace`: records `origin` (the state before the gesture) as one undo step. */
export function commitFrom<T>(h: History<T>, origin: T): History<T> {
  if (origin === h.present) return h
  const past = [...h.past, origin]
  if (past.length > HISTORY_LIMIT) past.shift()
  return { past, present: h.present, future: [] }
}

export function undo<T>(h: History<T>): History<T> {
  if (h.past.length === 0) return h
  const previous = h.past[h.past.length - 1]!
  return { past: h.past.slice(0, -1), present: previous, future: [h.present, ...h.future] }
}

export function redo<T>(h: History<T>): History<T> {
  if (h.future.length === 0) return h
  const [next, ...future] = h.future
  return { past: [...h.past, h.present], present: next!, future }
}
