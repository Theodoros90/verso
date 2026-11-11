import { CircleCheck, CircleX, FolderCheck, LoaderCircle, Pencil, Plus, Trash2 } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'

import { usePermissions } from '@/app/AuthGate'
import { Button } from '@/components/ui/button'
import { Field, Input, Switch } from '@/components/ui/form'
import { adminFoldersApi } from '@/lib/api/adminFolders'
import { ApiError } from '@/lib/api/client'
import { folderInputApi } from '@/lib/api/folderInput'
import type { AdminFolderLocationDto, FolderAccessTestDto } from '@/lib/api/generated'
import { cn } from '@/lib/utils'

const POLL_MS = 4000
const TEST_POLL_MS = 1000
const TEST_POLL_LIMIT = 30

interface Draft {
  /** Set when editing; the key can't change. */
  editing: boolean
  key: string
  name: string
  path: string
  input: boolean
  output: boolean
}

const NEW_DRAFT: Draft = { editing: false, key: '', name: '', path: '', input: true, output: false }

type ListState = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; items: AdminFolderLocationDto[] }

/** Message and field errors of a failed request (validation problems name the field). */
function problemOf(e: unknown): { message: string; fields: Record<string, string> } {
  if (e instanceof ApiError) {
    const fields: Record<string, string> = {}
    for (const [k, v] of Object.entries(e.problem.errors ?? {})) fields[k] = v[0] ?? ''
    return { message: e.message, fields }
  }
  return { message: 'Something went wrong. Try again.', fields: {} }
}

/**
 * Administration of approved folder locations (Admin only). This is the one place a server path is typed: designers
 * only ever choose a location by name. The Worker validates each path under its own service identity and reports the
 * result, shown here as the location's status.
 */
export function FolderLocationsPage() {
  const { administer } = usePermissions()
  const [state, setState] = useState<ListState>({ kind: 'loading' })
  const [draft, setDraft] = useState<Draft | null>(null)
  const [saving, setSaving] = useState(false)
  const [formError, setFormError] = useState<{ message: string; fields: Record<string, string> } | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [testing, setTesting] = useState<string | null>(null)
  const [testResult, setTestResult] = useState<{ key: string; test: FolderAccessTestDto } | null>(null)

  const reload = useCallback(async (signal?: AbortSignal) => {
    try {
      setState({ kind: 'ready', items: await adminFoldersApi.list(signal) })
    } catch (e) {
      if (!signal?.aborted) setState({ kind: 'error', message: problemOf(e).message })
    }
  }, [])

  useEffect(() => {
    if (!administer) return
    const controller = new AbortController()
    void reload(controller.signal)
    return () => controller.abort()
  }, [administer, reload])

  // A new or changed location is picked up by the Worker within about 15 seconds: keep looking while one is waiting.
  const waiting = state.kind === 'ready' && state.items.some((l) => l.problem?.startsWith('Waiting for the Worker'))
  useEffect(() => {
    if (!waiting) return
    const timer = setInterval(() => void reload(), POLL_MS)
    return () => clearInterval(timer)
  }, [waiting, reload])

  if (!administer) {
    return (
      <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
        <p role="alert" className="max-w-xl rounded-lg border border-line bg-surface px-4 py-3 text-ink-2">
          Folder locations are managed by the Admin role.
        </p>
      </div>
    )
  }

  const save = async () => {
    if (!draft) return
    setSaving(true)
    setFormError(null)
    try {
      await adminFoldersApi.save(draft.key.trim(), { name: draft.name, path: draft.path, input: draft.input, output: draft.output })
      setNotice(`Saved '${draft.key.trim()}'. The Worker picks it up within about 15 seconds.`)
      setDraft(null)
      await reload()
    } catch (e) {
      setFormError(problemOf(e))
    } finally {
      setSaving(false)
    }
  }

  const remove = async (l: AdminFolderLocationDto) => {
    if (!window.confirm(`Remove '${l.name}'? Workflows that use it will stop finding files until another location is chosen.`)) return
    setNotice(null)
    try {
      await adminFoldersApi.remove(l.key)
      setNotice(`Removed '${l.key}'.`)
      await reload()
    } catch (e) {
      setNotice(problemOf(e).message)
    }
  }

  const test = async (l: AdminFolderLocationDto) => {
    setTesting(l.key)
    setTestResult(null)
    setNotice(null)
    try {
      const role = l.input ? 'source' : 'processed'
      let current = await folderInputApi.requestTest([{ role, location: l.key, subfolder: '' }])
      for (let i = 0; i < TEST_POLL_LIMIT && (current.status === 'Queued' || current.status === 'Running'); i++) {
        await new Promise((r) => setTimeout(r, TEST_POLL_MS))
        current = await folderInputApi.test(current.id)
      }
      if (current.status === 'Queued' || current.status === 'Running') setNotice("The Worker hasn't run the test yet. Is the Worker running?")
      else setTestResult({ key: l.key, test: current })
    } catch (e) {
      setNotice(problemOf(e).message)
    } finally {
      setTesting(null)
    }
  }

  return (
    <div className="max-w-[1440px] px-4 pt-6 pb-10 md:px-7">
      <p className="mb-4 max-w-prose text-ink-2">
        The folders workflows may read files from and move files to. Only locations listed here (or set in the Worker’s configuration) can be chosen in a
        Folder input step, and designers never see the path. The Worker reaches every folder as its own service account, so give that account access to
        each one. Paths are as the <em>Worker’s</em> server sees them.
      </p>

      {notice && (
        <p role="status" className="mb-3 max-w-prose rounded-md bg-cyan-soft px-3 py-2 text-[13px] text-ink-2">
          {notice}
        </p>
      )}

      <div className="mb-3 flex items-center gap-2">
        <Button
          onClick={() => {
            setDraft(NEW_DRAFT)
            setFormError(null)
          }}
          disabled={draft !== null}
        >
          <Plus /> Add folder location
        </Button>
      </div>

      {draft && (
        <form
          aria-label={draft.editing ? 'Edit folder location' : 'Add folder location'}
          className="mb-4 grid max-w-[860px] gap-3 rounded-[10px] border border-line bg-surface p-4 md:grid-cols-2"
          onSubmit={(e) => {
            e.preventDefault()
            void save()
          }}
        >
          <Field label="Key *" htmlFor="fl-key" hint="Short and permanent, such as ap-inbound. Lower-case letters, digits and '-'." error={formError?.fields.key}>
            <Input id="fl-key" value={draft.key} disabled={draft.editing} className="font-mono" onChange={(e) => setDraft({ ...draft, key: e.target.value })} />
          </Field>
          <Field label="Name *" htmlFor="fl-name" hint="What designers see, such as AP inbound (file server)." error={formError?.fields.name}>
            <Input id="fl-name" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
          </Field>
          <div className="md:col-span-2">
            <Field
              label="Path *"
              htmlFor="fl-path"
              hint="Absolute, as the Worker's server sees it: D:\Verso\inbound, \\fileserver\share\inbound, or /mnt/verso/inbound. No passwords."
              error={formError?.fields.path}
            >
              <Input id="fl-path" value={draft.path} spellCheck={false} className="font-mono text-[12.5px]" onChange={(e) => setDraft({ ...draft, path: e.target.value })} />
            </Field>
          </div>
          <div className="flex flex-col gap-1.5 md:col-span-2">
            <Switch checked={draft.input} onCheckedChange={(v) => setDraft({ ...draft, input: v })} label="Files can be picked up from here (source)" />
            <Switch checked={draft.output} onCheckedChange={(v) => setDraft({ ...draft, output: v })} label="Processed and failed files can be moved here (destination)" />
            {formError?.fields.input && <p className="text-xs text-err">{formError.fields.input}</p>}
          </div>
          {formError && Object.keys(formError.fields).length === 0 && (
            <p role="alert" className="text-[12.5px] text-err md:col-span-2">
              {formError.message}
            </p>
          )}
          <div className="flex gap-2 md:col-span-2">
            <Button type="submit" disabled={saving}>
              {saving && <LoaderCircle className="animate-spin" />} Save
            </Button>
            <Button type="button" variant="ghost" onClick={() => setDraft(null)}>
              Cancel
            </Button>
          </div>
        </form>
      )}

      {state.kind === 'loading' && <p className="text-ink-3">Loading…</p>}
      {state.kind === 'error' && (
        <p role="alert" className="text-err">
          {state.message}
        </p>
      )}
      {state.kind === 'ready' && state.items.length === 0 && (
        <p className="text-ink-3">No locations yet. Add one to let workflows read from a folder.</p>
      )}
      {state.kind === 'ready' && state.items.length > 0 && (
        <div className="overflow-x-auto rounded-[10px] border border-line bg-surface">
          <table className="w-full min-w-[820px] text-left text-[13px]">
            <thead className="text-ink-3">
              <tr className="border-b border-line">
                <th className="px-3.5 py-2.5 font-medium">Name</th>
                <th className="px-3.5 py-2.5 font-medium">Path</th>
                <th className="px-3.5 py-2.5 font-medium">Used for</th>
                <th className="px-3.5 py-2.5 font-medium">Status</th>
                <th className="px-3.5 py-2.5 font-medium">
                  <span className="sr-only">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {state.items.map((l) => (
                <tr key={l.key} className="border-b border-line align-top last:border-0">
                  <td className="px-3.5 py-2.5">
                    <div className="font-medium">{l.name}</div>
                    <div className="font-mono text-[11.5px] text-ink-3">{l.key}</div>
                  </td>
                  <td className="px-3.5 py-2.5 font-mono text-[12px] break-all">
                    {l.path ?? <span className="font-sans text-ink-3">Set in the Worker’s configuration</span>}
                  </td>
                  <td className="px-3.5 py-2.5">{[l.input && 'Source', l.output && 'Destination'].filter(Boolean).join(' and ')}</td>
                  <td className="px-3.5 py-2.5">
                    <span className={cn('flex items-start gap-1.5', l.enabled ? 'text-ok' : l.problem?.startsWith('Waiting') ? 'text-ink-3' : 'text-err')}>
                      {l.enabled ? <CircleCheck className="mt-0.5 size-3.5 flex-none" aria-hidden="true" /> : <CircleX className="mt-0.5 size-3.5 flex-none" aria-hidden="true" />}
                      <span>{l.enabled ? 'Ready' : (l.problem ?? 'Unavailable')}</span>
                    </span>
                  </td>
                  <td className="px-3.5 py-2.5">
                    <div className="flex justify-end gap-1">
                      <Button size="sm" variant="ghost" disabled={testing !== null || !l.enabled} onClick={() => void test(l)} aria-label={`Test access to ${l.name}`}>
                        {testing === l.key ? <LoaderCircle className="animate-spin" /> : <FolderCheck />} Test
                      </Button>
                      {l.managed && (
                        <>
                          <Button
                            size="sm"
                            variant="ghost"
                            aria-label={`Edit ${l.name}`}
                            onClick={() => {
                              setDraft({ editing: true, key: l.key, name: l.name, path: l.path ?? '', input: l.input, output: l.output })
                              setFormError(null)
                            }}
                          >
                            <Pencil /> Edit
                          </Button>
                          <Button size="sm" variant="ghost" className="text-err" aria-label={`Remove ${l.name}`} onClick={() => void remove(l)}>
                            <Trash2 /> Remove
                          </Button>
                        </>
                      )}
                    </div>
                    {testResult?.key === l.key && (
                      <ul aria-label={`Access test of ${l.name}`} className="mt-2 flex flex-col gap-0.5 text-[12px]">
                        {testResult.test.status === 'Failed' && <li className="text-err">{testResult.test.error}</li>}
                        {testResult.test.results.flatMap((r) => r.checks).map((c) => (
                          <li key={c.name} className={c.ok ? 'text-ok' : 'text-err'}>
                            {c.ok ? '✓' : '✗'} {c.message}
                          </li>
                        ))}
                        {testResult.test.identity && (
                          <li className="text-ink-3">
                            Checked as <span className="font-mono">{testResult.test.identity}</span> on <span className="font-mono">{testResult.test.machine}</span>.
                          </li>
                        )}
                      </ul>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
