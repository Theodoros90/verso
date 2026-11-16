/** The archive step's "Keep for" value: `permanent`, or an amount and a unit: `90d`, `6m`, `7y`. */
export type RetentionUnit = 'd' | 'm' | 'y'

export const retentionUnits: { value: RetentionUnit; one: string; many: string }[] = [
  { value: 'd', one: 'day', many: 'days' },
  { value: 'm', one: 'month', many: 'months' },
  { value: 'y', one: 'year', many: 'years' },
]

export function parseRetention(value: string): { amount: number; unit: RetentionUnit } | null {
  const m = /^([1-9][0-9]{0,3})([dmy])$/.exec(value)
  return m ? { amount: Number(m[1]), unit: m[2] as RetentionUnit } : null
}

/** `7y` → "7 years", `1m` → "1 month", `permanent` → "Permanently". */
export function formatRetention(value: string): string {
  if (value === 'permanent') return 'Permanently'
  const p = parseRetention(value)
  if (!p) return value
  const unit = retentionUnits.find((u) => u.value === p.unit)!
  return `${p.amount} ${p.amount === 1 ? unit.one : unit.many}`
}
