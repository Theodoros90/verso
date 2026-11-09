import type { RouteObject } from 'react-router'

import { OverviewPage } from '@/features/overview/OverviewPage'
import { NotBuiltYetPage } from '@/features/placeholder/NotBuiltYetPage'
import { NotFoundPage } from '@/features/placeholder/NotFoundPage'
import { HealthProvider } from '@/features/health/useHealth'

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
      ...NAV_ITEMS.filter((item) => item.path !== '/').map((item) => ({
        path: item.path.slice(1),
        element: <NotBuiltYetPage item={item} />,
      })),
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]
