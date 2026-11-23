import type { ComponentProps } from 'react'
import { useEffect, useId, useState } from 'react'

import { Select } from '@/components/ui/form'
import type { SmsConnectionOptionDto } from '@/lib/api/generated'
import { smsConnectionsApi } from '@/lib/api/smsConnections'

/**
 * The SMS connection of a Send SMS step: a dropdown of the active SMS connections (key, name, type and sending switch only).
 * A saved key that is not in the list (deleted or switched off) stays selected and is marked, so an old workflow doesn't silently change.
 */
export function SmsConnectionSelect({
  value,
  onChange,
  requiresSending = false,
  ...rest
}: Omit<ComponentProps<typeof Select>, 'value' | 'onChange'> & { value: string; onChange: (v: string) => void; /** The step sends messages: connections whose sending is switched off are marked and warned about. */ requiresSending?: boolean }) {
  const [options, setOptions] = useState<SmsConnectionOptionDto[] | null>(null)
  const errorId = useId()
  const sendingId = useId()
  const marketingId = useId()
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    const controller = new AbortController()
    smsConnectionsApi
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
  const selected = list.find((o) => o.key === value)
  const sendingOff = requiresSending && selected !== undefined && !selected.allowsSending
  const marketing = selected?.messageType === 'marketing'
  const describedBy = [failed && errorId, sendingOff && sendingId, marketing && marketingId].filter(Boolean).join(' ') || undefined
  return (
    <div className="flex flex-col gap-1">
      <Select {...rest} aria-describedby={describedBy} value={value} onChange={(e) => onChange(e.target.value)}>
        {options === null && !value && <option value="">Loading…</option>}
        {options !== null && list.length === 0 && !failed && <option value="">No SMS connections yet. Create one in Configuration, Connections.</option>}
        {(list.length > 0 || failed) && !value && <option value="">Choose a connection…</option>}
        {value && !selected && <option value={value}>{options === null ? value : `Unknown connection (${value})`}</option>}
        {list.map((o) => (
          <option key={o.key} value={o.key}>
            {o.name} ({o.messageType === 'marketing' ? 'marketing' : 'transactional'}){requiresSending && !o.allowsSending ? ', sending off' : ''}
          </option>
        ))}
      </Select>
      {failed && <p id={errorId} role="alert" className="text-xs text-err">The connections could not be loaded.</p>}
      {sendingOff && (
        <p id={sendingId} role="alert" className="text-xs text-err">
          Sending is switched off for this connection. An administrator can switch it on in Configuration, Connections.
        </p>
      )}
      {marketing && (
        <p id={marketingId} role="alert" className="text-xs text-err">
          Marketing connections can't be used in workflows.
        </p>
      )}
    </div>
  )
}
