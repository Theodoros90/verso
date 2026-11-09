import type { RouteObject } from 'react-router'

import { OverviewPage } from '@/features/overview/OverviewPage'
import { NotBuiltYetPage } from '@/features/placeholder/NotBuiltYetPage'
import { NotFoundPage } from '@/features/placeholder/NotFoundPage'
import { HealthProvider } from '@/features/health/useHealth'
import { TemplateDesignerPage } from '@/features/templates/designer/TemplateDesignerPage'
import { TemplateListPage } from '@/features/templates/TemplateListPage'
import { WorkflowDesignerPage } from '@/features/workflows/designer/WorkflowDesignerPage'
import { WorkflowListPage } from '@/features/workflows/WorkflowListPage'

import { AppShell } from './AppShell'
import { NAV_ITEMS } from './navigation'

export const routes: RouteObject[] = [
  {
    path: '/',
    element: (
      <HealthProvider>
        <AppShell />
      </HealthProvider>
    ),
    children: [
      { index: true, element: <OverviewPage /> },
      { path: 'templates', element: <TemplateListPage /> },
      { path: 'templates/:id', element: <TemplateDesignerPage /> },
      { path: 'workflows', element: <WorkflowListPage /> },
      { path: 'workflows/:id', element: <WorkflowDesignerPage /> },
      ...NAV_ITEMS.filter((item) => !['/', '/templates', '/workflows'].includes(item.path)).map((item) => ({
        path: item.path.slice(1),
        element: <NotBuiltYetPage item={item} />,
      })),
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]
