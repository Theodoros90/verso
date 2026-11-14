import { Settings, X } from 'lucide-react'
import { Dialog as D } from 'radix-ui'
import { useState } from 'react'

import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

import { ConnectionsPanel } from '../connections/ConnectionsPanel'
import { VariablesPanel } from '../variables/VariablesPanel'
import { TransferPanel } from './TransferPanel'

export type ConfigurationTab = 'connections' | 'variables' | 'transfer'

const TABS: { id: ConfigurationTab; label: string }[] = [
  { id: 'connections', label: 'Connections' },
  { id: 'variables', label: 'Global variables' },
  { id: 'transfer', label: 'Export / import' },
]

/**
 * The gear in the top bar (design reference v14): one drawer for Connections, Global variables and moving
 * the configuration between environments (export / import).
 */
export function ConfigurationDrawer({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const [tab, setTab] = useState<ConfigurationTab>('connections')
  return (
    <D.Root open={open} onOpenChange={onOpenChange}>
      <D.Portal>
        <D.Overlay className="fixed inset-0 z-40 bg-black/35" />
        <D.Content className="fixed inset-y-0 right-0 z-40 flex w-full max-w-[760px] flex-col border-l border-line bg-background shadow-xl">
          <div className="flex items-start gap-3 border-b border-line bg-surface px-5 py-4">
            <Settings className="mt-0.5 size-5 text-ink-2" aria-hidden="true" />
            <div className="min-w-0 flex-1">
              <D.Title className="text-[15px] font-semibold">Configuration</D.Title>
              <D.Description className="mt-0.5 text-[13px] text-ink-2">Connections, global variables, and moving settings between environments.</D.Description>
            </div>
            <D.Close asChild>
              <Button variant="ghost" size="icon" aria-label="Close configuration" className="-mt-1 -mr-2">
                <X />
              </Button>
            </D.Close>
          </div>

          <div role="tablist" aria-label="Configuration sections" className="flex gap-1 border-b border-line bg-surface px-4 pt-2">
            {TABS.map((t) => (
              <button
                key={t.id}
                type="button"
                role="tab"
                id={`cfg-tab-${t.id}`}
                aria-selected={tab === t.id}
                aria-controls={`cfg-panel-${t.id}`}
                onClick={() => setTab(t.id)}
                className={cn(
                  'rounded-t-[7px] border-b-2 px-3 py-2 text-[13.5px] font-medium',
                  tab === t.id ? 'border-cyan text-cyan-strong' : 'border-transparent text-ink-2 hover:text-ink',
                )}
              >
                {t.label}
              </button>
            ))}
          </div>

          <div role="tabpanel" id={`cfg-panel-${tab}`} aria-labelledby={`cfg-tab-${tab}`} className="min-h-0 flex-1 overflow-auto">
            {tab === 'connections' && <ConnectionsPanel />}
            {tab === 'variables' && <VariablesPanel />}
            {tab === 'transfer' && <TransferPanel />}
          </div>
        </D.Content>
      </D.Portal>
    </D.Root>
  )
}
