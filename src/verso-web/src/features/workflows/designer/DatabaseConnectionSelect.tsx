import type { ComponentProps } from 'react'
import { useEffect, useState } from 'react'

import { Select } from '@/components/ui/form'
import { databaseConnectionsApi } from '@/lib/api/databaseConnections'
import type { DatabaseConnectionOptionDto } from '@/lib/api/generated'

/**
 * The database connection of a Query database step: a dropdown of the active database connections (key, name and provider only).
 * A saved key that is not in the list (deleted or switched off) stays selected and is marked, so an old workflow doesn't silently change.
 */
export function DatabaseConnectionSelect({ value, onChange, ...rest }: Omit<ComponentProps<typeof Select>, 'value' | 'onChange'> & { value: string; onChange: (v: string) => void }) {
  const [options, setOptions] = useState<DatabaseConnectionOptionDto[] | null>(null)
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    const controller = new AbortController()
    databaseConnectionsApi
      .listOptions(controller.signal)
      .then((items) => setOptions(Array.isArray(items) ? items : []))
      .catch(() => {
        if (controller.signal.aborted) return
        setFailed(true)
        setOptions([])
      })
    return () => controller.abort()
  }, [])

  const list = options ?? []
  const known = list.some((o) => o.key === value)
  return (
    <div className="flex flex-col gap-1">
      <Select {...rest} value={value} onChange={(e) => onChange(e.target.value)}>
        {options === null && !value && <option value="">Loading…</option>}
        {options !== null && list.length === 0 && !failed && <option value="">No connections yet. Create one in Configuration, Connections.</option>}
        {(list.length > 0 || failed) && !value && <option value="">Choose a connection…</option>}
        {value && !known && <option value={value}>{options === null ? value : `Unknown connection (${value})`}</option>}
        {list.map((o) => (
          <option key={o.key} value={o.key}>
            {o.name} ({o.provider})
          </option>
        ))}
      </Select>
      {failed && <p className="text-xs text-err">The connections could not be loaded.</p>}
    </div>
  )
}
