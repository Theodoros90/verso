import { RefreshCw } from 'lucide-react'

import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

import { describeHealth, describeStatus } from './healthDisplay'
import { useHealth } from './useHealth'

const CHECK_LABELS: Record<string, string> = { database: 'Database' }

/** Overview panel: overall API status plus each health check the API reports. */
export function SystemStatusPanel() {
  const { result, checking, checkedAt, refresh } = useHealth()
  const overall = describeHealth(result)

  return (
    <section aria-labelledby="system-status-title" className="rounded-lg border border-line bg-surface">
      <header className="flex items-center gap-2.5 border-b border-line px-4 py-3.5">
        <h2 id="system-status-title" className="text-[14.5px] font-semibold">
          System status
        </h2>
        <StatusPill tone={overall.tone}>{overall.label}</StatusPill>
        <div className="ml-auto flex items-center gap-3">
          {checkedAt && (
            <span className="text-xs text-ink-3">
              Checked <time dateTime={checkedAt.toISOString()}>{checkedAt.toLocaleTimeString('en-GB')}</time>
            </span>
          )}
          <Button size="sm" onClick={refresh} disabled={checking}>
            <RefreshCw className={cn(checking && 'animate-spin')} />
            Check now
          </Button>
        </div>
      </header>

      <div className="p-4">
        {!result && <p className="text-ink-3">Contacting the API…</p>}

        {result?.kind === 'unreachable' && (
          <p role="alert" className="text-err">
            {result.reason}
          </p>
        )}

        {result?.kind === 'reported' && (
          <ul className="divide-y divide-line">
            <li className="flex items-center gap-3 pb-3">
              <span className="w-28 font-medium">API</span>
              <StatusPill tone="ok">Responding</StatusPill>
              <span className="ml-auto font-mono text-xs text-ink-3">{result.health.totalDurationMs} ms</span>
            </li>
            {result.health.checks.map((check) => {
              const display = describeStatus(check.status)
              return (
                <li key={check.name} className="flex items-center gap-3 py-3 last:pb-0">
                  <span className="w-28 font-medium">{CHECK_LABELS[check.name] ?? check.name}</span>
                  <StatusPill tone={display.tone}>{display.label}</StatusPill>
                  <span className="text-ink-2">{check.description}</span>
                  <span className="ml-auto font-mono text-xs text-ink-3">{check.durationMs} ms</span>
                </li>
              )
            })}
          </ul>
        )}
      </div>
    </section>
  )
}
