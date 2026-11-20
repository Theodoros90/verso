import type { ComponentProps } from 'react'
import { useEffect, useId, useState } from 'react'

import { Select } from '@/components/ui/form'
import { apiConnectionsApi } from '@/lib/api/apiConnections'
import type { ApiConnectionOptionDto } from '@/lib/api/generated'

/**
 * The API connection of a Fetch from API step: a dropdown of the active API connections (key, name and host only).
 * A saved key that is not in the list (deleted or switched off) stays selected and is marked, so an old workflow doesn't silently change.
 */
export function ApiConnectionSelect({
  value,
  onChange,
  ...rest
}: Omit<ComponentProps<typeof Select>, 'value' | 'onChange'> & { value: string; onChange: (v: string) => void }) {
  const [options, setOptions] = useState<ApiConnectionOptionDto[] | null>(null)
  const errorId = useId()
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    const controller = new AbortController()
    apiConnectionsApi
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
      <Select {...rest} aria-describedby={failed ? errorId : undefined} value={value} onChange={(e) => onChange(e.target.value)}>
        {options === null && !value && <option value="">Loading…</option>}
        {options !== null && list.length === 0 && !failed && <option value="">No API connections yet. Create one in Configuration, Connections.</option>}
        {(list.length > 0 || failed) && !value && <option value="">Choose a connection…</option>}
        {value && !known && <option value={value}>{options === null ? value : `Unknown connection (${value})`}</option>}
        {list.map((o) => (
          <option key={o.key} value={o.key}>
            {o.name} ({o.host})
          </option>
        ))}
      </Select>
      {failed && <p id={errorId} role="alert" className="text-xs text-err">The connections could not be loaded.</p>}
    </div>
  )
}
