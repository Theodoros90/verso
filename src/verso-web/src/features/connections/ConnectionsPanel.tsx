import { Plus } from 'lucide-react'
import { useState } from 'react'

import { StatusPill } from '@/components/StatusPill'
import { Button } from '@/components/ui/button'
import { Switch } from '@/components/ui/form'
import { databaseConnectionsApi } from '@/lib/api/databaseConnections'
import { emailRelaysApi } from '@/lib/api/emailRelays'
import type { DatabaseConnectionDto, EmailRelayDto } from '@/lib/api/generated'

import { newLocalId, useConnections } from '../configuration/stores'
import { ConnectionDialog, type ConnectionDraft } from './ConnectionDialog'
import { DatabaseCard, DatabaseEditDialog, useDatabaseConnections } from './DatabaseConnectionCards'
import { RelayCard, RelayEditDialog, useEmailRelays } from './EmailRelayCards'
import {
  abbreviate,
  CONNECTION_GROUPS,
  displayStatus,
  type Connection,
} from './connections'

const groupId = (name: string) => `grp-${name.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`

/** Connections tab of the Configuration drawer (design reference): every external system in one place, grouped, with status and where it is used. Simulated and frontend-only. */
export function ConnectionsPanel() {
  const [connections, setConnections] = useConnections()
  const [editing, setEditing] = useState<Connection | 'new' | null>(null)
  const [notice, setNotice] = useState<{ tone: 'ok' | 'err'; text: string } | null>(null)
  const { relays, loadError, reload } = useEmailRelays()
  const [editingRelay, setEditingRelay] = useState<EmailRelayDto | null>(null)
  const dbs = useDatabaseConnections()
  const [editingDb, setEditingDb] = useState<DatabaseConnectionDto | null>(null)

  const toggle = (id: string, enabled: boolean) => {
    setConnections((list) => list.map((c) => (c.id === id ? { ...c, enabled } : c)))
    const name = connections.find((c) => c.id === id)?.name
    setNotice({ tone: 'ok', text: `${name} turned ${enabled ? 'on' : 'off'}.` })
  }

  const save = (draft: ConnectionDraft) => {
    if (editing === 'new') {
      setConnections((list) => [
        ...list,
        { ...draft, id: newLocalId(), health: 'ok', last: 'Not tested yet', enabled: true, usedBySteps: 0, color: '#5B6773', abbr: abbreviate(draft.name) },
      ])
      setNotice({ tone: 'ok', text: `Connection “${draft.name}” created.` })
    } else if (editing) {
      const id = editing.id
      setConnections((list) => list.map((c) => (c.id === id ? { ...c, ...draft, abbr: c.name === draft.name ? c.abbr : abbreviate(draft.name) } : c)))
      setNotice({ tone: 'ok', text: `Settings saved for ${draft.name}.` })
    }
    setEditing(null)
  }

  const toggleRelay = async (r: EmailRelayDto, active: boolean) => {
    try {
      await (active ? emailRelaysApi.activate(r.id) : emailRelaysApi.deactivate(r.id))
      setNotice({ tone: 'ok', text: `${r.name} turned ${active ? 'on' : 'off'}.` })
      await reload()
    } catch {
      setNotice({ tone: 'err', text: `Could not change ${r.name}.` })
    }
  }

  const toggleDb = async (c: DatabaseConnectionDto, active: boolean) => {
    try {
      await (active ? databaseConnectionsApi.activate(c.id) : databaseConnectionsApi.deactivate(c.id))
      setNotice({ tone: 'ok', text: `${c.name} turned ${active ? 'on' : 'off'}.` })
      await dbs.reload()
    } catch {
      setNotice({ tone: 'err', text: `Could not change ${c.name}.` })
    }
  }

  const relayNames = [...relays.map((r) => r.name), ...dbs.connections.map((c) => c.name)]

  return (
    <div className="px-5 py-4">
      <div className="mb-2 flex flex-wrap items-start gap-3">
        <p className="max-w-3xl min-w-64 flex-1 text-ink-2">
          <span className="text-ink-3">Every external system in one place: what it does, its credentials, and where it’s used. Workflow steps reference a connection by name — secrets never enter a workflow.</span>
        </p>
        <div className="flex gap-2">
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
        const groupRelays = group.name === 'Delivery channels' ? relays : []
        const groupDbs = group.name === 'Data sources' ? dbs.connections : []
        if (items.length === 0 && groupRelays.length === 0 && groupDbs.length === 0) return null
        return (
          <section key={group.name} aria-labelledby={groupId(group.name)} className="mt-5 first-of-type:mt-0">
            <div className="mb-3 flex items-baseline gap-2.5">
              <h2 id={groupId(group.name)} className="m-0 text-[15.5px] font-semibold">{group.name}</h2>
              <p className="m-0 text-[13px] text-ink-3">{group.description}</p>
            </div>
            <ul className="m-0 grid list-none grid-cols-[repeat(auto-fill,minmax(270px,1fr))] gap-3.5 p-0">
              {groupDbs.map((c) => (
                <DatabaseCard key={c.id} connection={c} onToggle={(active) => void toggleDb(c, active)} onConfigure={() => setEditingDb(c)} />
              ))}
              {groupRelays.map((r) => (
                <RelayCard key={r.id} relay={r} onToggle={(active) => void toggleRelay(r, active)} onConfigure={() => setEditingRelay(r)} />
              ))}
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

      {(loadError || dbs.loadError) && <p role="alert" className="mt-4 text-[13px] text-err">Could not load the email or database connections.</p>}

      <p className="mt-6 text-xs text-ink-3">Prototype: these connections are synthetic, kept in this browser only, and the test is simulated. No external service is contacted and no secret is stored. The exceptions are an email connection (authentication SMTP credentials) and a database connection (authentication SQL Server login): they are real, stored on the server, and their passwords are encrypted and never shown again.</p>

      {editing && (
        <ConnectionDialog
          key={editing === 'new' ? 'new' : editing.id}
          connection={editing === 'new' ? null : editing}
          existingNames={[...connections.filter((c) => editing === 'new' || c.id !== editing.id).map((c) => c.name), ...relayNames]}
          onSave={save}
          onDatabaseCreated={(name) => {
            setEditing(null)
            setNotice({ tone: 'ok', text: `Database connection “${name}” created.` })
            void dbs.reload()
          }}
          onRelayCreated={(name) => {
            setEditing(null)
            setNotice({ tone: 'ok', text: `Email connection “${name}” created.` })
            void reload()
          }}
          onClose={() => setEditing(null)}
        />
      )}

      {editingDb && (
        <DatabaseEditDialog
          key={editingDb.id}
          connection={editingDb}
          onClose={() => setEditingDb(null)}
          onSaved={async (name) => {
            setEditingDb(null)
            setNotice({ tone: 'ok', text: `Settings saved for ${name}.` })
            await dbs.reload()
          }}
        />
      )}

      {editingRelay && (
        <RelayEditDialog
          key={editingRelay.id}
          relay={editingRelay}
          onClose={() => setEditingRelay(null)}
          onSaved={async (name) => {
            setEditingRelay(null)
            setNotice({ tone: 'ok', text: `Settings saved for ${name}.` })
            await reload()
          }}
        />
      )}
    </div>
  )
}
