import { Download, Plus, Upload } from 'lucide-react'
import { useRef, useState } from 'react'

import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Switch } from '@/components/ui/form'

import { ConnectionDialog, type ConnectionDraft } from './ConnectionDialog'
import {
  abbreviate,
  CONNECTION_GROUPS,
  displayStatus,
  parseConnections,
  seedConnections,
  serializeConnections,
  type Connection,
} from './connections'

const groupId = (name: string) => `grp-${name.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`

let nextId = 1
const newId = () => `local-${nextId++}`

/** Connections (design reference): every external system in one place, grouped, with status and where it is used. Simulated and frontend-only. */
export function ConnectionsPage() {
  const [connections, setConnections] = useState<Connection[]>(seedConnections)
  const [editing, setEditing] = useState<Connection | 'new' | null>(null)
  const [notice, setNotice] = useState<{ tone: 'ok' | 'err'; text: string } | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)

  const toggle = (id: string, enabled: boolean) => {
    setConnections((list) => list.map((c) => (c.id === id ? { ...c, enabled } : c)))
    const name = connections.find((c) => c.id === id)?.name
    setNotice({ tone: 'ok', text: `${name} turned ${enabled ? 'on' : 'off'}.` })
  }

  const save = (draft: ConnectionDraft) => {
    if (editing === 'new') {
      setConnections((list) => [
        ...list,
        { ...draft, id: newId(), health: 'ok', last: 'Not tested yet', enabled: true, usedBySteps: 0, color: '#5B6773', abbr: abbreviate(draft.name) },
      ])
      setNotice({ tone: 'ok', text: `Connection “${draft.name}” created.` })
    } else if (editing) {
      const id = editing.id
      setConnections((list) => list.map((c) => (c.id === id ? { ...c, ...draft, abbr: c.name === draft.name ? c.abbr : abbreviate(draft.name) } : c)))
      setNotice({ tone: 'ok', text: `Settings saved for ${draft.name}.` })
    }
    setEditing(null)
  }

  const exportFile = () => {
    const url = URL.createObjectURL(new Blob([serializeConnections(connections)], { type: 'application/json' }))
    const a = document.createElement('a')
    a.href = url
    a.download = 'verso-connections.json'
    a.click()
    URL.revokeObjectURL(url)
    setNotice({ tone: 'ok', text: 'Exported the connection settings. Secrets are never included.' })
  }

  const importFile = async (file: File) => {
    const parsed = parseConnections(await file.text())
    if ('error' in parsed) {
      setNotice({ tone: 'err', text: parsed.error })
      return
    }
    const known = new Set(connections.map((c) => c.name.toLowerCase()))
    const fresh = parsed.items.filter((i) => !known.has(i.name.toLowerCase()))
    const duplicates = parsed.items.length - fresh.length
    setConnections((list) => [...list, ...fresh.map((i) => ({ ...i, id: newId() }))])
    const extra = [duplicates && `${duplicates} already existed`, parsed.skipped && `${parsed.skipped} invalid`].filter(Boolean).join(', ')
    setNotice({ tone: 'ok', text: `Imported ${fresh.length} connection${fresh.length === 1 ? '' : 's'}${extra ? ` (${extra})` : ''}.` })
  }

  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      <div className="mb-2 flex flex-wrap items-start gap-3">
        <p className="max-w-3xl min-w-64 flex-1 text-ink-2">
          <b className="text-[15.5px] font-semibold text-ink">Connections</b>{' '}
          <span className="text-ink-3">Every external system in one place: what it does, its credentials, and where it’s used. Workflow steps reference a connection by name — secrets never enter a workflow.</span>
        </p>
        <div className="flex gap-2">
          <Button type="button" onClick={exportFile}>
            <Download />
            Export
          </Button>
          <Button type="button" onClick={() => fileInput.current?.click()}>
            <Upload />
            Import
          </Button>
          <input
            ref={fileInput}
            type="file"
            accept="application/json,.json"
            hidden
            aria-label="Import connections file"
            onChange={(e) => {
              const file = e.target.files?.[0]
              e.target.value = ''
              if (file) void importFile(file)
            }}
          />
          <Button type="button" variant="primary" onClick={() => setEditing('new')}>
            <Plus />
            New connection
          </Button>
        </div>
      </div>

      <div role="status" className="min-h-6 text-[13px]">
        {notice && <span className={notice.tone === 'ok' ? 'text-ink-2' : 'text-err'}>{notice.text}</span>}
      </div>

      {CONNECTION_GROUPS.map((group) => {
        const items = connections.filter((c) => c.group === group.name)
        if (items.length === 0) return null
        return (
          <section key={group.name} aria-labelledby={groupId(group.name)} className="mt-5 first-of-type:mt-0">
            <div className="mb-3 flex items-baseline gap-2.5">
              <h2 id={groupId(group.name)} className="m-0 text-[15.5px] font-semibold">{group.name}</h2>
              <p className="m-0 text-[13px] text-ink-3">{group.description}</p>
            </div>
            <ul className="m-0 grid list-none grid-cols-[repeat(auto-fill,minmax(270px,1fr))] gap-3.5 p-0">
              {items.map((c) => {
                const status = displayStatus(c)
                return (
                  <li key={c.id} className={`flex flex-col gap-3 rounded-[10px] border border-line bg-surface p-4 ${c.enabled ? '' : 'opacity-70'}`}>
                    <div className="flex items-center gap-3">
                      <div aria-hidden="true" className="grid size-[38px] flex-none place-items-center rounded-[9px] text-[13px] font-bold text-white" style={{ background: c.color }}>
                        {c.abbr}
                      </div>
                      <div className="min-w-0 flex-1">
                        <h3 className="m-0 text-[14.5px] font-semibold">{c.name}</h3>
                        <div className="text-xs text-ink-3">{c.category}</div>
                      </div>
                      <Switch checked={c.enabled} onCheckedChange={(v) => toggle(c.id, v)} label={`Enable ${c.name}`} hideLabel />
                    </div>
                    <p className="m-0 text-[13px] text-ink-2">{c.description}</p>
                    <dl className="m-0 mt-auto flex items-baseline justify-between gap-3 text-[12.5px]">
                      <dt className="text-ink-3">Credential</dt>
                      <dd className="m-0 min-w-0 text-right font-mono text-[11.5px] break-words text-ink-2">
                        {c.auth} · {c.endpoint}
                      </dd>
                    </dl>
                    <div className="flex flex-wrap items-center gap-2 text-[12.5px] text-ink-3">
                      <StatusPill tone={status.tone}>{status.label}</StatusPill>
                      <span>{status.detail}</span>
                      {c.usedBySteps > 0 && (
                        <span className="rounded border border-line bg-surface-2 px-1.5 py-px text-xs text-ink-2">
                          used by {c.usedBySteps} {c.usedBySteps === 1 ? 'step' : 'steps'}
                        </span>
                      )}
                      <Button type="button" size="sm" className="ml-auto" onClick={() => setEditing(c)} aria-label={`Configure ${c.name}`}>
                        Configure
                      </Button>
                    </div>
                  </li>
                )
              })}
            </ul>
          </section>
        )
      })}

      <p className="mt-8 text-xs text-ink-3">Prototype: these connections are synthetic, kept in this page only, and the test is simulated. No external service is contacted and no secret is stored.</p>

      {editing && (
        <ConnectionDialog
          key={editing === 'new' ? 'new' : editing.id}
          connection={editing === 'new' ? null : editing}
          existingNames={connections.filter((c) => editing === 'new' || c.id !== editing.id).map((c) => c.name)}
          onSave={save}
          onClose={() => setEditing(null)}
        />
      )}
    </div>
  )
}
