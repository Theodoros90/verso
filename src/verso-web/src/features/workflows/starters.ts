/**
 * Ready-made workflow starters: editable drafts with steps already connected. Each starter is a list of
 * catalog node types in a line; the definition is built from the live node catalog (default settings),
 * so nothing is configured: connections, templates and rules still need setting up before publishing.
 * Only existing node types are used; nothing here talks to an outside system.
 */
import type { NodeTypeDto } from '@/lib/api/generated'

import { addNode, connect, nextPosition, type WorkflowDefinition } from './model/definition'

export interface Starter {
  id: string
  name: string
  category: 'Finance' | 'Operations'
  description: string
  /** The trigger, in words. */
  trigger: string
  /** Node types, in order; each is connected from its predecessor's first output. */
  steps: string[]
  /** What still has to be set up before this can be published. */
  requirements: string[]
}

export const STARTER_CATEGORIES = ['All', 'Finance', 'Operations'] as const

export const starters: Starter[] = [
  {
    id: 'invoice',
    name: 'Invoice generation & delivery',
    category: 'Finance',
    description: 'Validate incoming invoice data, generate a document, send it and archive a copy.',
    trigger: 'API / webhook',
    steps: ['api-trigger', 'validate', 'map-fields', 'generate-pdf', 'send-email', 'archive'],
    requirements: ['Required fields and rules in Validate', 'A published invoice template in Generate PDF', 'Recipient and subject in Send email', 'Archive retention'],
  },
  {
    id: 'statements',
    name: 'Monthly account statements',
    category: 'Finance',
    description: 'Collect account records on a schedule and prepare customer statements.',
    trigger: 'Scheduled',
    steps: ['scheduled-trigger', 'erp-connector', 'map-fields', 'generate-pdf', 'send-email', 'archive'],
    requirements: ['Schedule and time zone (simulated trigger)', 'Field mapping from the records', 'A published statement template', 'Recipient mapping in Send email'],
  },
  {
    id: 'batch',
    name: 'Invoice batch from a folder',
    category: 'Operations',
    description: 'Pick up structured batch files and prepare one invoice document per record.',
    trigger: 'Folder input',
    steps: ['folder-input', 'parse', 'validate', 'for-each', 'generate-pdf', 'archive'],
    requirements: ['Folder and file pattern in Folder input', 'Source format in Parse', 'Rules in Validate', 'A published invoice template'],
  },
  {
    id: 'reminder',
    name: 'Payment reminders',
    category: 'Finance',
    description: 'Check which invoices are due and prepare customer reminder emails.',
    trigger: 'Scheduled',
    steps: ['scheduled-trigger', 'erp-connector', 'if-else', 'generate-email', 'send-email'],
    requirements: ['Schedule (simulated trigger)', 'The condition that decides who gets a reminder', 'Reminder email text', 'Recipient mapping in Send email'],
  },
  {
    id: 'review',
    name: 'Document review & approval',
    category: 'Operations',
    description: 'Generate a draft and route it for a human decision before archiving.',
    trigger: 'Manual',
    steps: ['manual-trigger', 'validate', 'generate-pdf', 'approval', 'archive'],
    requirements: ['Required fields in Validate', 'A published template', 'Approver and instructions in Approval', 'Archive retention'],
  },
]

/** Labels of a starter's steps, from the catalog when it is loaded (else the type names). */
export function stepLabels(starter: Starter, nodeTypes: NodeTypeDto[]): string[] {
  return starter.steps.map((t) => nodeTypes.find((n) => n.type === t)?.label ?? t)
}

/**
 * The starter as a draft definition: nodes in a row with default settings, each connected from the previous
 * node's first output to its input. Returns null when the catalog lacks one of the node types.
 */
export function buildStarterDefinition(starter: Starter, nodeTypes: NodeTypeDto[]): WorkflowDefinition | null {
  let def: WorkflowDefinition = { nodes: [], edges: [] }
  let previous: { id: string; type: NodeTypeDto } | null = null
  for (const typeName of starter.steps) {
    const type = nodeTypes.find((n) => n.type === typeName)
    if (!type) return null
    const added = addNode(def, type, nextPosition(def, previous?.id))
    def = added.def
    const input = type.inputs[0]
    const output = previous?.type.outputs[0]
    if (previous && input && output) def = connect(def, previous.id, output.id, added.id, input.id)
    previous = { id: added.id, type }
  }
  return def
}
