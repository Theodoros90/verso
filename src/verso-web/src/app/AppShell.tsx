import { LogOut, Menu, Moon, Sun, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { NavLink, Outlet, useLocation } from 'react-router'

import { Button } from '@/components/ui/button'
import { HealthIndicator } from '@/features/health/HealthIndicator'
import { cn } from '@/lib/utils'

import { useSession } from './AuthGate'
import { NAV_GROUPS, NAV_ITEMS } from './navigation'
import { useTheme } from './theme'

/** CMY brand mark from the design reference. Fixed colors: it is a logo, not themed UI. */
function BrandMark() {
  return (
    <div aria-hidden="true" className="relative size-[30px] overflow-hidden rounded-[7px] border border-line bg-white">
      <span className="absolute top-1.5 left-[5px] size-3 rounded-full bg-[#00A9D4] mix-blend-multiply" />
      <span className="absolute top-1.5 left-3 size-3 rounded-full bg-[#D43A8C] mix-blend-multiply" />
      <span className="absolute top-3 left-[8.5px] size-3 rounded-full bg-[#F2C200] mix-blend-multiply" />
    </div>
  )
}

const initials = (name: string) =>
  name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]!.toUpperCase())
    .join('')

function Sidebar({ onNavigate }: { onNavigate: () => void }) {
  const session = useSession()
  const permissions = session.me.permissions
  return (
    <>
      <div className="flex items-center gap-2.5 px-[18px] pt-[18px] pb-3.5">
        <BrandMark />
        <div>
          <div className="text-[17px] font-bold tracking-[-0.01em]">Verso</div>
          <div className="-mt-0.5 text-xs text-ink-3">Document workflows</div>
        </div>
      </div>

      <nav aria-label="Main" className="flex flex-1 flex-col gap-0.5 overflow-auto px-2.5 py-1.5">
        {NAV_GROUPS.map((group, i) => (
          <div key={group.label ?? i} className="flex flex-col gap-0.5">
            {group.label && <div className="px-2.5 pt-3.5 pb-1.5 text-xs text-ink-3">{group.label}</div>}
            {group.items.filter((item) => !item.requires || permissions[item.requires]).map((item) => (
              <NavLink
                key={item.path}
                to={item.path}
                end={item.path === '/'}
                onClick={onNavigate}
                className={({ isActive }) =>
                  cn(
                    'flex items-center gap-2.5 rounded-[7px] px-2.5 py-2 font-medium text-ink-2 no-underline',
                    'hover:bg-surface-2 hover:text-ink',
                    isActive && 'bg-cyan-soft text-cyan-strong hover:bg-cyan-soft hover:text-cyan-strong [&_svg]:text-cyan',
                  )
                }
              >
                <item.icon className="size-4" strokeWidth={1.75} aria-hidden="true" />
                {item.label}
              </NavLink>
            ))}
          </div>
        ))}
      </nav>

      <div className="flex items-center gap-2.5 border-t border-line px-3.5 py-3">
        <div aria-hidden="true" className="grid size-[30px] flex-none place-items-center rounded-full bg-surface-3 text-xs font-semibold text-ink-2">
          {initials(session.me.name)}
        </div>
        <div className="min-w-0 flex-1">
          <div className="truncate text-[13px] leading-tight font-semibold">{session.me.name}</div>
          <div className="truncate text-xs text-ink-3">
            {session.me.roles.join(', ')}
            {session.mode === 'Development' && ' · development sign-in'}
          </div>
        </div>
        <Button variant="ghost" size="icon" aria-label="Sign out" title="Sign out" onClick={session.signOut}>
          <LogOut />
        </Button>
      </div>
    </>
  )
}

/** Application frame: sidebar + top bar + routed page, matching the design reference layout. */
export function AppShell() {
  const { resolved, toggle } = useTheme()
  const [menuOpen, setMenuOpen] = useState(false)
  const location = useLocation()
  const current = NAV_ITEMS.find((i) => (i.path === '/' ? location.pathname === '/' : location.pathname.startsWith(i.path)))

  useEffect(() => {
    document.title = current ? `${current.label} · Verso` : 'Verso'
  }, [current])

  return (
    <div className="grid h-full grid-cols-1 md:grid-cols-[236px_minmax(0,1fr)]">
      {/* Desktop sidebar */}
      <aside className="hidden min-h-0 flex-col border-r border-line bg-surface md:flex">
        <Sidebar onNavigate={() => {}} />
      </aside>

      {/* Mobile drawer */}
      {menuOpen && (
        <div className="fixed inset-0 z-40 md:hidden">
          <button
            aria-label="Close navigation"
            className="absolute inset-0 bg-black/30"
            onClick={() => setMenuOpen(false)}
          />
          <aside className="absolute inset-y-0 left-0 flex w-[260px] flex-col border-r border-line bg-surface">
            <Button variant="ghost" size="icon" className="absolute top-3 right-2" aria-label="Close navigation" onClick={() => setMenuOpen(false)}>
              <X />
            </Button>
            <Sidebar onNavigate={() => setMenuOpen(false)} />
          </aside>
        </div>
      )}

      <div className="flex min-h-0 min-w-0 flex-col">
        <header className="flex h-[58px] flex-none items-center gap-3 border-b border-line bg-surface px-4 md:px-5">
          <Button variant="ghost" size="icon" className="md:hidden" aria-label="Open navigation" onClick={() => setMenuOpen(true)}>
            <Menu />
          </Button>
          <h1 className="m-0 truncate text-base font-semibold tracking-[-0.005em]">{current?.label ?? 'Not found'}</h1>
          <div className="ml-auto flex items-center gap-2">
            <HealthIndicator />
            <Button
              variant="ghost"
              size="icon"
              onClick={toggle}
              aria-label={resolved === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
            >
              {resolved === 'dark' ? <Sun /> : <Moon />}
            </Button>
          </div>
        </header>

        <main className="min-h-0 flex-1 overflow-auto">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
