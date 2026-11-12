import type { RouteObject } from 'react-router'

import { AuditPage } from '@/features/audit/AuditPage'
import { ArchivePage } from '@/features/executions/ArchivePage'
import { ExecutionDetailPage } from '@/features/executions/ExecutionDetailPage'
import { ExecutionListPage } from '@/features/executions/ExecutionListPage'
import { OverviewPage } from '@/features/overview/OverviewPage'
import { NotBuiltYetPage } from '@/features/placeholder/NotBuiltYetPage'
import { NotFoundPage } from '@/features/placeholder/NotFoundPage'
import { HealthProvider } from '@/features/health/useHealth'
import { TemplateDesignerPage } from '@/features/templates/designer/TemplateDesignerPage'
import { TemplateListPage } from '@/features/templates/TemplateListPage'
import { WorkflowDesignerPage } from '@/features/workflows/designer/WorkflowDesignerPage'
import { WorkflowListPage } from '@/features/workflows/WorkflowListPage'

import { AppShell } from './AppShell'
import { AuthGate } from './AuthGate'
import { NAV_ITEMS } from './navigation'

/** Screens that exist; the other navigation entries get the "not built yet" placeholder. */
const BUILT_PATHS = ['/', '/templates', '/workflows', '/executions', '/archive', '/audit']

export const routes: RouteObject[] = [
  {
    path: '/',
    element: (
      <HealthProvider>
        <AuthGate>
          <AppShell />
        </AuthGate>
      </HealthProvider>
    ),
    children: [
      { index: true, element: <OverviewPage /> },
      { path: 'templates', element: <TemplateListPage /> },
      { path: 'templates/:id', element: <TemplateDesignerPage /> },
      { path: 'workflows', element: <WorkflowListPage /> },
      { path: 'workflows/:id', element: <WorkflowDesignerPage /> },
      { path: 'executions', element: <ExecutionListPage /> },
      { path: 'executions/:id', element: <ExecutionDetailPage /> },
      { path: 'archive', element: <ArchivePage /> },
      { path: 'audit', element: <AuditPage /> },
      ...NAV_ITEMS.filter((item) => !BUILT_PATHS.includes(item.path)).map((item) => ({
        path: item.path.slice(1),
        element: <NotBuiltYetPage item={item} />,
      })),
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]
