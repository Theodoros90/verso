import { Handle, Position, type Node, type NodeProps } from '@xyflow/react'
import { Archive, ArrowLeftRight, CalendarClock, Circle, Database, DatabaseZap, ClipboardList, FileCode, Files, FilePenLine, FileText, LayoutTemplate, FolderInput, FolderSync, GitBranch, Inbox, Merge, Mail, MousePointerClick, Printer, Repeat, Send, Server, Shuffle, ShieldCheck, Search, Split, Upload, UserCheck, Webhook, type LucideIcon } from 'lucide-react'

import { cn } from '@/lib/utils'
import type { NodeTypeDto, WorkflowIssueDto } from '@/lib/api/generated'

import { outputPorts, switchPorts, type DefNode, type DefEdge } from '../model/definition'

export const CATEGORY_COLORS: Record<string, string> = {
  input: 'var(--cat-input)',
  process: 'var(--cat-process)',
  logic: 'var(--cat-logic)',
  doc: 'var(--cat-doc)',
  ai: 'var(--cat-ai)',
  output: 'var(--cat-output)',
}

export const CATEGORY_LABELS: Record<string, string> = {
  input: 'Input',
  process: 'Processing',
  logic: 'Logic',
  doc: 'Document',
  ai: 'AI assistance',
  output: 'Output',
}

export const NODE_ICONS: Record<string, LucideIcon> = {
  'api-trigger': Webhook,
  'erp-connector': Database,
  'file-upload': Upload,
  'sftp-folder': FolderInput,
  'email-inbox': Inbox,
  'database-query': DatabaseZap,
  'form-submission': ClipboardList,
  'scheduled-trigger': CalendarClock,
  'manual-trigger': MousePointerClick,
  'folder-input': FolderSync,
  parse: FileCode,
  'map-fields': ArrowLeftRight,
  transform: Shuffle,
  'lookup-enrich': Search,
  'merge-data': Merge,
  approval: UserCheck,
  'if-else': GitBranch,
  switch: Split,
  'for-each': Repeat,
  validate: ShieldCheck,
  'generate-pdf': FileText,
  'generate-docx': FilePenLine,
  'generate-email': LayoutTemplate,
  'combine-pdfs': Files,
  'send-email': Mail,
  'print-queue': Printer,
  'upload-sftp': Server,
  'deliver-api': Send,
  archive: Archive,
}

/** An output's name on the card and on its connection. An If / else step lets its author rename the Yes and No paths, a For each its loop and empty paths. */
export function portLabel(node: DefNode, portId: string, fallback: string): string {
  if (node.type === 'switch') return switchPorts(node.config).find((p) => p.id === portId)?.label ?? fallback
  if (node.type === 'if-else' || node.type === 'for-each') {
    const custom = node.config[`${portId}Label`]
    if (typeof custom === 'string' && custom.trim()) return custom.trim()
  }
  return fallback
}

export type NodeStatus = 'configured' | 'warning' | 'error'

export function nodeStatus(issues: WorkflowIssueDto[]): NodeStatus {
  if (issues.some((i) => i.severity === 'error')) return 'error'
  if (issues.length > 0) return 'warning'
  return 'configured'
}

export interface NodeCardData extends Record<string, unknown> {
  node: DefNode
  nodeType?: NodeTypeDto
  issues: WorkflowIssueDto[]
  /** Edges from this node, to mark connected outputs. */
  outgoing: DefEdge[]
}

export type VersoFlowNode = Node<NodeCardData, 'verso'>

const STATUS_TEXT: Record<NodeStatus, string> = { configured: 'Configured', warning: 'Check', error: 'Needs attention' }

/** Node card (prototype: category stripe, icon, title, status; typed output ports on the right). */
export function NodeCard({ data, selected }: NodeProps<VersoFlowNode>) {
  const { node, nodeType, issues, outgoing } = data
  const Icon = NODE_ICONS[node.type] ?? Circle
  const color = CATEGORY_COLORS[nodeType?.category ?? 'logic'] ?? 'var(--cat-logic)'
  const status = nodeType ? nodeStatus(issues) : 'error'
  const errors = issues.filter((i) => i.severity === 'error').length
  const outputs = outputPorts(nodeType, node)

  return (
    <div
      className={cn(
        'w-[220px] rounded-[10px] border bg-surface text-ink shadow-[0_1px_2px_rgba(19,32,43,.08)]',
        selected ? 'border-cyan ring-2 ring-cyan/30' : status === 'error' ? 'border-err/60' : 'border-line',
      )}
      aria-label={`${node.title}, ${nodeType?.label ?? node.type}: ${STATUS_TEXT[status]}${errors ? `, ${errors} error${errors === 1 ? '' : 's'}` : ''}`}
    >
      <div className="flex items-center gap-2 rounded-t-[10px] border-b border-line px-2.5 py-2" style={{ boxShadow: `inset 3px 0 0 ${color}` }}>
        <span className="grid size-6 flex-none place-items-center rounded-md" style={{ background: `color-mix(in srgb, ${color} 14%, transparent)`, color }}>
          <Icon className="size-3.5" strokeWidth={2} aria-hidden="true" />
        </span>
        <div className="min-w-0 flex-1">
          <div className="truncate text-[13px] leading-tight font-semibold">{node.title || nodeType?.label || node.type}</div>
          <div className="truncate text-[11px] text-ink-3">{nodeType?.label ?? `Unknown type '${node.type}'`}</div>
        </div>
      </div>
      <div className="flex items-center gap-1.5 px-2.5 py-1.5 text-[11.5px]">
        <span
          className={cn(
            'inline-flex items-center gap-1 rounded-full px-1.5 py-px font-medium',
            status === 'configured' && 'bg-ok-soft text-ok',
            status === 'warning' && 'bg-warn-soft text-warn',
            status === 'error' && 'bg-err-soft text-err',
          )}
        >
          <span aria-hidden="true" className="size-1.5 rounded-full bg-current" />
          {STATUS_TEXT[status]}
          {errors > 1 ? ` (${errors})` : ''}
        </span>
      </div>
      {outputs.length > 0 && (
        <div className="flex flex-col border-t border-line py-1">
          {outputs.map((p) => {
            const connected = outgoing.some((e) => e.sourcePort === p.id)
            return (
              <div key={p.id} className="relative flex h-6 items-center justify-end pr-3.5 text-[11px] text-ink-2">
                <span className={cn(!connected && p.required && 'text-err')}>{portLabel(node, p.id, p.label)}</span>
                <Handle
                  type="source"
                  position={Position.Right}
                  id={p.id}
                  aria-label={`${node.title}: output ${p.label}`}
                  className={cn('!size-2.5 !border-2 !bg-surface', connected ? '!border-cyan' : p.required ? '!border-err' : '!border-line-strong')}
                  style={{ top: '50%' }}
                />
              </div>
            )
          })}
        </div>
      )}
      {(nodeType?.inputs ?? []).map((p) => (
        <Handle
          key={p.id}
          type="target"
          position={Position.Left}
          id={p.id}
          aria-label={`${node.title}: input`}
          className="!size-2.5 !border-2 !border-line-strong !bg-surface"
          style={{ top: 22 }}
        />
      ))}
    </div>
  )
}
