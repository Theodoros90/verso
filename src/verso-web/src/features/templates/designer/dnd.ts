/** Drag-and-drop payloads between the palette, the data model tree, the canvas and the binding box. */
import type { ElementType } from '../model/layout'
import type { FieldKind } from '../model/schema'

export const DND_MIME = 'application/x-verso-designer'

export type DragPayload =
  | { kind: 'new'; type: ElementType }
  | { kind: 'field'; path: string; fieldKind: FieldKind }
  | { kind: 'block'; id: string }

export function setPayload(e: React.DragEvent, payload: DragPayload) {
  e.dataTransfer.setData(DND_MIME, JSON.stringify(payload))
  e.dataTransfer.effectAllowed = payload.kind === 'block' ? 'move' : 'copy'
}

export function hasPayload(e: React.DragEvent): boolean {
  return Array.from(e.dataTransfer.types).includes(DND_MIME)
}

export function getPayload(e: React.DragEvent): DragPayload | undefined {
  try {
    const raw = e.dataTransfer.getData(DND_MIME)
    return raw ? (JSON.parse(raw) as DragPayload) : undefined
  } catch {
    return undefined
  }
}

/** "dueDate" → "Due date", "vat_number" → "Vat number": a default label for a dropped field. */
export function humanize(name: string): string {
  const words = name
    .replace(/_/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .toLowerCase()
    .trim()
  return words.charAt(0).toUpperCase() + words.slice(1)
}
