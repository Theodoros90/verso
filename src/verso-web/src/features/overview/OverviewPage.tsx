import { CircleX, Hourglass, Plus, RefreshCw, type LucideIcon } from 'lucide-react'
import { Link } from 'react-router'

import { Button, buttonVariants } from '@/components/ui/button'
import type { ExecutionSummaryDto, TemplateSummaryDto } from '@/lib/api/generated'
import { cn } from '@/lib/utils'
import { formatDateTime, formatDuration, shortId } from '@/features/executions/format'
import { SystemStatusPanel } from '@/features/health/SystemStatusPanel'

import { computeStats, countByWorkflow, formatPercent, greeting, type DashboardStats, type HourBucket } from './dashboardStats'
import { useDashboardData, type AttentionList, type DashboardData } from './useDashboardData'

const number = new Intl.NumberFormat('en-GB')

function Panel({ title, aside, children, className }: { title: string; aside?: React.ReactNode; children: React.ReactNode; className?: string }) {
  const id = `panel-${title.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`
  return (
    <section aria-labelledby={id} className={cn('rounded-[10px] border border-line bg-surface', className)}>
      <header className="flex items-center gap-2.5 border-b border-line px-4 py-3">
        <h2 id={id} className="text-[14.5px] font-semibold">
          {title}
        </h2>
        {aside && <div className="ml-auto text-[12.5px] text-ink-3">{aside}</div>}
      </header>
      {children}
    </section>
  )
}

function Stat({ value, label, note, tone }: { value: string; label: string; note?: string; tone?: 'ok' | 'err' }) {
  return (
    <div>
      <div className="text-[28px] leading-[1.1] font-semibold tracking-[-0.02em] tabular-nums">{value}</div>
      <div className="text-[13px] text-ink-2">{label}</div>
      {note && <div className={cn('text-[12.5px] font-medium', tone === 'err' ? 'text-err' : tone === 'ok' ? 'text-ok' : 'text-ink-3')}>{note}</div>}
    </div>
  )
}

const CHART = { w: 720, h: 220, l: 40, r: 10, t: 12, b: 24 }

function niceMax(value: number): number {
  if (value <= 4) return 4
  const magnitude = 10 ** Math.floor(Math.log10(value))
  const step = [1, 2, 5, 10].map((m) => m * magnitude).find((s) => s >= value) ?? value
  return step
}

/** Runs submitted per hour today, stacked by outcome. Hours still to come stay empty. */
function HourlyChart({ hours, nowHour }: { hours: HourBucket[]; nowHour: number }) {
  const max = niceMax(Math.max(0, ...hours.map((h) => h.succeeded + h.failed + h.open)))
  const innerW = CHART.w - CHART.l - CHART.r
  const innerH = CHART.h - CHART.t - CHART.b
  const slot = innerW / 24
  const y = (v: number) => CHART.h - CHART.b - (v / max) * innerH
  const gridValues = [0, max / 2, max]
  const summary = hours
    .filter((h) => h.succeeded + h.failed + h.open > 0)
    .map((h) => `${String(h.hour).padStart(2, '0')}:00 ${h.succeeded + h.failed + h.open}`)
    .join(', ')

  return (
    <svg viewBox={`0 0 ${CHART.w} ${CHART.h}`} preserveAspectRatio="none" role="img" aria-label={`Runs submitted per hour today. ${summary || 'None yet.'}`} className="block h-[220px] w-full">
      {gridValues.map((v) => (
        <g key={v}>
          <line x1={CHART.l} x2={CHART.w - CHART.r} y1={y(v)} y2={y(v)} stroke="var(--line)" strokeWidth={1} />
          <text x={CHART.l - 8} y={y(v) + 4} textAnchor="end" fill="var(--ink-3)" fontSize={11}>
            {Number.isInteger(v) ? v : v.toFixed(1)}
          </text>
        </g>
      ))}
      {[0, 6, 12, 18, 23].map((h) => (
        <text key={h} x={CHART.l + h * slot + slot / 2} y={CHART.h - 6} textAnchor="middle" fill="var(--ink-3)" fontSize={11}>
          {String(h).padStart(2, '0')}:00
        </text>
      ))}
      {hours.map((h) => {
        let base = 0
        const x = CHART.l + h.hour * slot + slot * 0.15
        const width = slot * 0.7
        return (
          <g key={h.hour}>
            {(
              [
                [h.succeeded, 'var(--cyan)'],
                [h.open, 'var(--warn)'],
                [h.failed, 'var(--err)'],
              ] as const
            ).map(([count, fill]) => {
              if (count === 0) return null
              const top = y(base + count)
              const rect = <rect key={fill} x={x} y={top} width={width} height={y(base) - top} fill={fill} rx={1.5} />
              base += count
              return rect
            })}
          </g>
        )
      })}
      <line x1={CHART.l + nowHour * slot + slot / 2} x2={CHART.l + nowHour * slot + slot / 2} y1={CHART.t} y2={CHART.h - CHART.b} stroke="var(--magenta)" strokeWidth={1.5} />
      <text x={CHART.l + nowHour * slot + slot / 2 + 6} y={CHART.t + 10} fill="var(--magenta)" fontSize={11}>
        Now
      </text>
    </svg>
  )
}

function LegendItem({ color, children }: { color: string; children: React.ReactNode }) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <i aria-hidden="true" className="inline-block size-2.5 rounded-sm" style={{ background: color }} />
      {children}
    </span>
  )
}

function OutputPanel({ stats, now }: { stats: DashboardStats; now: Date }) {
  return (
    <Panel title="Runs today">
      <div className="px-5 pt-5 pb-3">
        <div className="mb-3 flex flex-wrap gap-9">
          <Stat value={number.format(stats.today)} label="Runs submitted today" note={stats.open > 0 ? `${stats.open} still open` : undefined} />
          <Stat
            value={stats.firstTimeRate === null ? '–' : formatPercent(stats.firstTimeRate)}
            label="Succeeded first time"
            note={stats.firstTimeRate === null ? 'Nothing finished yet' : `${stats.succeeded} of ${stats.succeeded + stats.failed} finished`}
            tone={stats.firstTimeRate === 1 ? 'ok' : undefined}
          />
          <Stat
            value={formatDuration(stats.medianMs)}
            label="Median run time"
            note={stats.failed > 0 ? `${stats.failed} failed or rejected` : undefined}
            tone={stats.failed > 0 ? 'err' : undefined}
          />
        </div>
        <HourlyChart hours={stats.hours} nowHour={now.getHours()} />
        <div className="mt-1.5 flex flex-wrap gap-4 text-[12.5px] text-ink-2">
          <LegendItem color="var(--cyan)">Succeeded</LegendItem>
          <LegendItem color="var(--warn)">Queued, running or awaiting approval</LegendItem>
          <LegendItem color="var(--err)">Failed or rejected</LegendItem>
        </div>
      </div>
    </Panel>
  )
}

function AttentionItem({ icon: Icon, tone, title, children, to, action }: { icon: LucideIcon; tone: 'err' | 'warn'; title: string; children: React.ReactNode; to: string; action: string }) {
  return (
    <li className="flex items-start gap-3 border-b border-line px-4 py-3 last:border-b-0">
      <span aria-hidden="true" className={cn('grid size-[30px] flex-none place-items-center rounded-lg', tone === 'err' ? 'bg-err-soft text-err' : 'bg-warn-soft text-warn')}>
        <Icon className="size-4" />
      </span>
      <div className="min-w-0">
        <h3 className="text-[13.5px] font-semibold">{title}</h3>
        <p className="mt-0.5 mb-1.5 text-[13px] text-ink-2">{children}</p>
        <Link to={to} className={buttonVariants({ size: 'sm' })}>
          {action}
        </Link>
      </div>
    </li>
  )
}

function AttentionPanel({ failed, awaiting }: { failed: AttentionList; awaiting: AttentionList }) {
  const count = failed.total + awaiting.total
  return (
    <Panel title="Needs attention" aside={`${count} ${count === 1 ? 'item' : 'items'}`}>
      {count === 0 ? (
        <p className="px-4 py-8 text-center text-ink-3">
          <strong className="mb-1 block text-[15px] text-ink">All clear</strong>
          No failed runs and nothing waiting for approval.
        </p>
      ) : (
        <ul>
          {failed.items.map((e: ExecutionSummaryDto) => (
            <AttentionItem key={e.id} icon={CircleX} tone="err" title={`Run ${shortId(e.id)} failed`} to={`/executions/${e.id}`} action="Review run">
              <span className="font-mono text-[12px]">{e.workflowKey}</span>: {e.failureMessage ?? 'A technical error stopped the run.'}
            </AttentionItem>
          ))}
          {failed.total > failed.items.length && (
            <li className="border-b border-line px-4 py-2.5 text-[13px]">
              <Link to="/executions" className="text-cyan-strong">
                {failed.total - failed.items.length} more failed runs
              </Link>
            </li>
          )}
          {awaiting.items.map((e: ExecutionSummaryDto) => (
            <AttentionItem key={e.id} icon={Hourglass} tone="warn" title={`Run ${shortId(e.id)} awaits approval`} to={`/executions/${e.id}`} action="Open run">
              <span className="font-mono text-[12px]">{e.workflowKey}</span>, submitted {formatDateTime(e.submittedAt)}.
            </AttentionItem>
          ))}
          {awaiting.total > awaiting.items.length && (
            <li className="px-4 py-2.5 text-[13px]">
              <Link to="/executions" className="text-cyan-strong">
                {awaiting.total - awaiting.items.length} more waiting for approval
              </Link>
            </li>
          )}
        </ul>
      )}
    </Panel>
  )
}

/** A tiny page sketch, as in the design reference's template cards. */
function MiniPage() {
  return (
    <div className="w-[120px] rounded-t-[1px] bg-white p-2.5 shadow-sm" style={{ height: 110 }} aria-hidden="true">
      <i className="mb-[5px] block h-[7px] w-[45%] rounded-sm bg-[#1A232C]" />
      <i className="mb-[5px] block h-1 w-3/5 rounded-sm bg-[#DCE2E8]" />
      <i className="mb-[5px] block h-1 w-2/5 rounded-sm bg-[#DCE2E8]" />
      <i className="mt-[9px] mb-[5px] block h-1 w-[30%] rounded-sm bg-cyan" />
      <i className="mb-[5px] block h-1 rounded-sm bg-[#DCE2E8]" />
      <i className="mb-[5px] block h-1 rounded-sm bg-[#DCE2E8]" />
      <i className="block h-1 w-[70%] rounded-sm bg-[#DCE2E8]" />
    </div>
  )
}

function TemplatesPanel({ templates, total }: { templates: TemplateSummaryDto[]; total: number }) {
  return (
    <Panel
      title="Recently edited templates"
      aside={
        <Link to="/templates" className={buttonVariants({ variant: 'ghost', size: 'sm' })}>
          Open designer
        </Link>
      }
    >
      {templates.length === 0 ? (
        <p className="px-4 py-8 text-center text-ink-3">
          <strong className="mb-1 block text-[15px] text-ink">No templates yet</strong>
          Create the first invoice template in the designer.
        </p>
      ) : (
        <div className="grid grid-cols-[repeat(auto-fill,minmax(200px,1fr))] gap-3.5 p-4">
          {templates.slice(0, 4).map((t) => (
            <Link key={t.id} to={`/templates/${t.id}`} className="flex flex-col overflow-hidden rounded-[10px] border border-line bg-surface text-inherit no-underline hover:border-line-strong">
              <div className="grid h-[120px] place-items-end justify-center overflow-hidden bg-surface-3 pt-3.5">
                <MiniPage />
              </div>
              <div className="px-3 py-2.5">
                <b className="block truncate text-[13.5px] font-semibold">{t.name}</b>
                <span className="text-[12.5px] text-ink-3">
                  {t.latestPublishedVersion ? `v${t.latestPublishedVersion}` : 'Not published'}
                  {t.hasDraft ? ', draft open' : ''}, edited {formatDateTime(t.updatedAt)}
                </span>
              </div>
            </Link>
          ))}
        </div>
      )}
      {total > 4 && <p className="border-t border-line px-4 py-2.5 text-[12.5px] text-ink-3">Showing 4 of {total} templates.</p>}
    </Panel>
  )
}

function WorkflowRunsPanel({ week, weekTotal, workflowTotal }: { week: ExecutionSummaryDto[]; weekTotal: number; workflowTotal: number }) {
  const rows = countByWorkflow(week).slice(0, 6)
  const top = rows[0]?.count ?? 1
  return (
    <Panel title="Runs by workflow, last 7 days" aside={`${workflowTotal} active ${workflowTotal === 1 ? 'workflow' : 'workflows'}`}>
      <div className="p-4">
        {rows.length === 0 ? (
          <p className="py-4 text-center text-ink-3">No runs in the last 7 days.</p>
        ) : (
          rows.map((r) => (
            <div key={r.key} className="grid grid-cols-[minmax(90px,150px)_1fr_48px] items-center gap-2.5 py-[7px] text-[13.5px]">
              <span className="truncate font-mono text-[12px]" title={r.key}>
                {r.key}
              </span>
              <div role="presentation" className="h-2 overflow-hidden rounded-full bg-surface-3">
                <i className="block h-full rounded-full bg-cyan" style={{ width: `${(r.count / top) * 100}%` }} />
              </div>
              <span className="text-right tabular-nums">{number.format(r.count)}</span>
            </div>
          ))
        )}
        {weekTotal > week.length && <p className="mt-2 text-[12.5px] text-ink-3">Counted from the newest {number.format(week.length)} of {number.format(weekTotal)} runs.</p>}
      </div>
    </Panel>
  )
}

function Dashboard({ data }: { data: DashboardData }) {
  const now = data.loadedAt
  const stats = computeStats(data.week, now)
  const attention = data.failed.total + data.awaiting.total
  const summary =
    stats.today === 0
      ? 'Nothing has been submitted yet today.'
      : `${number.format(stats.today)} ${stats.today === 1 ? 'run' : 'runs'} submitted today, ${stats.succeeded} succeeded.`

  return (
    <>
      <div className="mb-5 flex flex-wrap items-end gap-4">
        <div>
          <h2 className="text-[26px] leading-[1.15] font-semibold tracking-[-0.02em]">{greeting(now)}</h2>
          <p className="mt-1 text-ink-2">
            {summary} {attention > 0 ? `${attention} ${attention === 1 ? 'run needs' : 'runs need'} a decision.` : ''}
          </p>
        </div>
        <div className="ml-auto flex gap-2">
          <Link to="/executions" className={buttonVariants()}>
            Open executions
          </Link>
          <Link to="/templates" className={buttonVariants({ variant: 'primary' })}>
            <Plus />
            New template
          </Link>
        </div>
      </div>

      <div className="grid items-start gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <OutputPanel stats={stats} now={now} />
        <AttentionPanel failed={data.failed} awaiting={data.awaiting} />
      </div>
      <div className="mt-4 grid items-start gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <TemplatesPanel templates={data.templates} total={data.templateTotal} />
        <WorkflowRunsPanel week={data.week} weekTotal={data.weekTotal} workflowTotal={data.workflowTotal} />
      </div>
    </>
  )
}

export function OverviewPage() {
  const { state, refresh } = useDashboardData()

  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      {state.kind === 'loading' && <p className="mb-5 text-ink-3">Loading the dashboard…</p>}
      {state.kind === 'error' && (
        <div className="mb-5 flex items-center gap-3 rounded-lg border border-err/30 bg-err-soft px-4 py-3 text-err">
          <span>{state.message}</span>
          <Button size="sm" className="ml-auto" onClick={refresh}>
            <RefreshCw />
            Try again
          </Button>
        </div>
      )}
      {state.kind === 'ready' && <Dashboard data={state.data} />}
      <div className="mt-4">
        <SystemStatusPanel />
      </div>
    </div>
  )
}
