import { Select } from '@/components/ui/form'
import { cn } from '@/lib/utils'

/**
 * Picks a field of the data from a dropdown of the fields known at this step (see sourceFields). Nothing is typed: with no known
 * fields the dropdown is empty. A saved path that is no longer available stays selectable, marked as such, so it isn't lost silently.
 */
export function FieldPicker({
  value,
  paths,
  onChange,
  ariaLabel,
  id,
  invalid,
  disabled,
}: {
  value: string
  paths: string[]
  onChange: (value: string) => void
  ariaLabel: string
  id?: string
  invalid?: boolean
  disabled?: boolean
}) {
  const stale = value !== '' && !paths.includes(value)

  return (
    <div className="flex flex-col gap-1">
      <Select
        id={id}
        aria-label={ariaLabel}
        aria-invalid={invalid}
        className={cn(value && 'font-mono text-[12.5px]')}
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
      >
        <option value="">{paths.length > 0 ? 'Choose a field…' : 'No fields available'}</option>
        {stale && <option value={value}>{value} (not available)</option>}
        {paths.map((p) => (
          <option key={p} value={p}>
            {p}
          </option>
        ))}
      </Select>
      {paths.length === 0 && (
        <p className="text-xs text-ink-3">Connect this step to the trigger, and give the trigger a sample payload, to see the fields.</p>
      )}
    </div>
  )
}
