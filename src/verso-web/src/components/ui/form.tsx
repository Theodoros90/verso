import * as React from 'react'

import { cn } from '@/lib/utils'

// Form primitives restyled to the design reference (.field, label, .input, .seg, switch, kbd).

const inputBase =
  'w-full rounded-[7px] border border-line-strong bg-surface px-2.5 py-[7px] text-[13.5px] text-ink placeholder:text-ink-3 ' +
  'focus-visible:outline-2 focus-visible:-outline-offset-1 focus-visible:outline-cyan disabled:opacity-60 aria-invalid:border-err'

export function Input({ className, ...props }: React.ComponentProps<'input'>) {
  return <input className={cn(inputBase, className)} {...props} />
}

export function Textarea({ className, ...props }: React.ComponentProps<'textarea'>) {
  return <textarea className={cn(inputBase, 'min-h-16 resize-y', className)} {...props} />
}

export function Select({ className, ...props }: React.ComponentProps<'select'>) {
  return <select className={cn(inputBase, 'py-[6px] pr-7', className)} {...props} />
}

export function Field({
  label,
  htmlFor,
  hint,
  error,
  className,
  children,
}: {
  label: React.ReactNode
  htmlFor?: string
  hint?: React.ReactNode
  error?: React.ReactNode
  className?: string
  children: React.ReactNode
}) {
  return (
    <div className={cn('flex flex-col gap-[5px]', className)}>
      <label htmlFor={htmlFor} className="text-[12.5px] font-medium text-ink-2">
        {label}
      </label>
      {children}
      {error && <p className="text-xs text-err">{error}</p>}
      {hint && !error && <p className="text-xs text-ink-3">{hint}</p>}
    </div>
  )
}

export function Switch({
  checked,
  onCheckedChange,
  label,
  disabled,
  hideLabel,
}: {
  checked: boolean
  onCheckedChange: (checked: boolean) => void
  label: string
  disabled?: boolean
  /** Keep the label for assistive technology only. */
  hideLabel?: boolean
}) {
  return (
    <label className={cn('inline-flex cursor-pointer items-center gap-[7px] text-[13px] text-ink-2', disabled && 'cursor-default opacity-50')}>
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        disabled={disabled}
        onClick={() => onCheckedChange(!checked)}
        className={cn('relative h-5 w-[34px] flex-none rounded-full transition-colors', checked ? 'bg-cyan' : 'bg-line-strong')}
      >
        <span
          aria-hidden="true"
          className={cn('absolute top-[3px] left-[3px] size-3.5 rounded-full bg-white transition-transform', checked && 'translate-x-3.5')}
        />
      </button>
      {hideLabel ? <span className="sr-only">{label}</span> : label}
    </label>
  )
}

export interface SegOption<T extends string> {
  value: T
  label: string
  icon?: React.ReactNode
}

/** Segmented single-choice control (.seg). */
export function Segmented<T extends string>({
  value,
  options,
  onChange,
  label,
}: {
  value: T | undefined
  options: SegOption<T>[]
  onChange: (value: T) => void
  label: string
}) {
  return (
    <div role="radiogroup" aria-label={label} className="inline-flex gap-0.5 rounded-lg border border-line-strong bg-surface-2 p-0.5">
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="radio"
          aria-checked={value === o.value}
          aria-label={o.icon ? o.label : undefined}
          title={o.icon ? o.label : undefined}
          onClick={() => onChange(o.value)}
          className={cn(
            'inline-flex items-center rounded-md px-[9px] py-[4px] text-[12.5px] text-ink-2 [&_svg]:size-4',
            value === o.value && 'bg-surface text-ink shadow-[0_1px_2px_rgba(0,0,0,.12)]',
          )}
        >
          {o.icon ?? o.label}
        </button>
      ))}
    </div>
  )
}

export function Kbd({ children }: { children: React.ReactNode }) {
  return (
    <kbd className="rounded border border-b-2 border-line-strong bg-surface px-[5px] font-mono text-[11px] text-ink-2">{children}</kbd>
  )
}
