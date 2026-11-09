import { cn } from '@/lib/utils'

export type PillTone = 'ok' | 'warn' | 'err' | 'run' | 'neutral'

const TONES: Record<PillTone, string> = {
  ok: 'bg-ok-soft text-ok',
  warn: 'bg-warn-soft text-warn',
  err: 'bg-err-soft text-err',
  run: 'bg-cyan-soft text-cyan',
  neutral: 'bg-surface-3 text-ink-2',
}

/** Status pill with a leading dot, as in the design reference (.pill). */
export function StatusPill({ tone, children, className }: { tone: PillTone; children: React.ReactNode; className?: string }) {
  return (
    <span
      className={cn(
        'inline-flex items-center gap-[5px] whitespace-nowrap rounded-full py-0.5 pr-2 pl-[7px] text-xs font-medium',
        TONES[tone],
        className,
      )}
    >
      <span aria-hidden="true" className={cn('size-1.5 rounded-full bg-current', tone === 'run' && 'animate-pulse')} />
      {children}
    </span>
  )
}
