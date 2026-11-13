import {
  Activity,
  Archive,
  FileText,
  LayoutDashboard,
  PenTool,
  PlugZap,
  ShieldCheck,
  Workflow,
  type LucideIcon,
} from 'lucide-react'

import type { PermissionsDto } from '@/lib/api/generated'

export interface NavItem {
  path: string
  label: string
  icon: LucideIcon
  /** Plan milestone that builds this screen; shown on its placeholder page until then. */
  milestone?: string
  summary: string
  /** Shown only to users with this permission (the API refuses the others anyway). */
  requires?: keyof PermissionsDto
}

export interface NavGroup {
  label?: string
  items: NavItem[]
}

/**
 * Sidebar structure, following the design reference (Overview / Build / Run / Govern).
 * Assistant (AI) and Integrations are deferred, so they are not listed.
 */
export const NAV_GROUPS: NavGroup[] = [
  {
    items: [{ path: '/', label: 'Overview', icon: LayoutDashboard, summary: 'System status and recent activity.' }],
  },
  {
    label: 'Build',
    items: [
      {
        path: '/templates',
        label: 'Template designer',
        icon: PenTool,
        milestone: 'M2–M4',
        summary: 'Design invoice templates on an A4 canvas and bind fields to data.',
      },
      {
        path: '/workflows',
        label: 'Workflow designer',
        icon: Workflow,
        milestone: 'M6',
        summary: 'Connect trigger, validation, PDF generation and archive steps.',
      },
      {
        path: '/preview',
        label: 'Document preview',
        icon: FileText,
        milestone: 'M5',
        summary: 'Render a template with sample data as PDF.',
      },
    ],
  },
  {
    label: 'Run',
    items: [
      {
        path: '/executions',
        label: 'Executions',
        icon: Activity,
        milestone: 'M7–M8',
        summary: 'Submitted invoices, their steps and live progress.',
      },
      {
        path: '/connections',
        label: 'Connections',
        icon: PlugZap,
        summary: 'Every external system in one place: status, credentials and where it is used.',
      },
    ],
  },
  {
    label: 'Govern',
    items: [
      {
        path: '/archive',
        label: 'Archive',
        icon: Archive,
        milestone: 'M8',
        summary: 'Archived documents with hashes and retention.',
      },
      {
        path: '/audit',
        label: 'Audit trail',
        icon: ShieldCheck,
        milestone: 'M9',
        summary: 'Who changed, published, submitted or downloaded what.',
        requires: 'readAudit',
      },
    ],
  },
]

export const NAV_ITEMS: NavItem[] = NAV_GROUPS.flatMap((g) => g.items)
