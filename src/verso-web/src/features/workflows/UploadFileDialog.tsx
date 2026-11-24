import { Upload } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'

import { Button } from '@/components/ui/button'
import { Dialog } from '@/components/ui/dialog'
import { ApiError } from '@/lib/api/client'
import type { UploadSettingsDto } from '@/lib/api/generated'
import { uploadsApi, type UploadResult } from '@/lib/api/uploads'

type Settings = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; value: UploadSettingsDto }

const sizeText = (bytes: number) => (bytes >= 1_048_576 ? `${Math.round((bytes / 1_048_576) * 10) / 10} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`)
const extensionOf = (name: string) => {
  const dot = name.lastIndexOf('.')
  return dot < 0 ? '' : name.slice(dot).toLowerCase()
}
/** `.jpeg` is listed next to `.jpg`; show one entry per kind of file. */
const typesText = (accept: string[]) => accept.filter((e) => e !== '.jpeg').map((e) => e.slice(1).toUpperCase()).join(', ')

/** Uploads one file to a workflow that starts with a File upload step; the API checks it again (ADR 0023). */
export function UploadFileDialog({
  workflowKey,
  workflowName,
  open,
  onOpenChange,
}: {
  workflowKey: string
  workflowName: string
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const [settings, setSettings] = useState<Settings>({ kind: 'loading' })
  const [file, setFile] = useState<File | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [progress, setProgress] = useState(0)
  const [result, setResult] = useState<UploadResult | null>(null)
  const [dragging, setDragging] = useState(false)
  const input = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (!open) return
    setSettings({ kind: 'loading' })
    setFile(null)
    setProblem(null)
    setResult(null)
    setProgress(0)
    const controller = new AbortController()
    uploadsApi
      .settings(workflowKey, controller.signal)
      .then((value) => setSettings({ kind: 'ready', value }))
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setSettings({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load the upload settings.' })
      })
    return () => controller.abort()
  }, [open, workflowKey])

  const choose = (picked: File | undefined) => {
    if (!picked || settings.kind !== 'ready') return
    setResult(null)
    const { accept, maxBytes } = settings.value
    if (!accept.includes(extensionOf(picked.name))) {
      setFile(null)
      setProblem(`“${picked.name}” can’t be uploaded: files of type ${extensionOf(picked.name) || '(none)'} are not accepted. Allowed: ${typesText(accept)}.`)
    } else if (picked.size > maxBytes) {
      setFile(null)
      setProblem(`“${picked.name}” is too large (${sizeText(picked.size)}). The limit is ${sizeText(maxBytes)}.`)
    } else if (picked.size === 0) {
      setFile(null)
      setProblem(`“${picked.name}” is empty.`)
    } else {
      setFile(picked)
      setProblem(null)
    }
  }

  const send = async () => {
    if (!file || busy) return
    setBusy(true)
    setProblem(null)
    setProgress(0)
    try {
      setResult(await uploadsApi.upload(workflowKey, file, (f) => setProgress(f)))
    } catch (e) {
      setProblem(e instanceof ApiError ? (e.problem.detail ?? e.problem.title ?? e.message) : 'The upload was refused or interrupted: check the file size limit and try again.')
    } finally {
      setBusy(false)
    }
  }

  const ready = settings.kind === 'ready'
  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={`Upload a file to “${workflowName}”`}
      description="The file starts a new run of this workflow. Verso checks its type and content before anything runs."
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{result ? 'Done' : 'Cancel'}</Button>
          {!result && (
            <Button variant="primary" disabled={!file || busy || !ready} onClick={() => void send()}>
              {busy ? 'Uploading…' : 'Upload'}
            </Button>
          )}
        </>
      }
    >
      {settings.kind === 'loading' && <p className="text-ink-3">Loading…</p>}
      {settings.kind === 'error' && (
        <p role="alert" className="rounded-lg border border-err/30 bg-err-soft px-4 py-3 text-[13px] text-err">
          {settings.message}
        </p>
      )}
      {ready && (
        <div className="flex flex-col gap-3.5">
          <p className="text-[13px] text-ink-2">
            Allowed file types: {typesText(settings.value.accept)}, up to {sizeText(settings.value.maxBytes)}.
          </p>
          <div
            onDragOver={(e) => {
              e.preventDefault()
              setDragging(true)
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={(e) => {
              e.preventDefault()
              setDragging(false)
              if (!busy) choose(e.dataTransfer.files[0])
            }}
            className={`flex flex-col items-center gap-2 rounded-xl border border-dashed p-6 text-center ${dragging ? 'border-cyan bg-cyan-soft/40' : 'border-line-strong bg-surface-2'}`}
          >
            <Upload className="size-5 text-cyan" aria-hidden="true" />
            <p className="text-[13px] text-ink-2">Drop a file here, or</p>
            <Button disabled={busy} onClick={() => input.current?.click()}>
              Choose a file…
            </Button>
            <input
              ref={input}
              type="file"
              aria-label="Choose a file"
              accept={settings.value.accept.join(',')}
              className="sr-only"
              tabIndex={-1}
              disabled={busy}
              onChange={(e) => {
                choose(e.target.files?.[0])
                e.target.value = ''
              }}
            />
            {file && (
              <p className="text-[13px] font-medium text-ink">
                {file.name} <span className="font-normal text-ink-3">({sizeText(file.size)})</span>
              </p>
            )}
          </div>
          {busy && (
            <div role="progressbar" aria-label="Upload progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(progress * 100)} className="h-2 overflow-hidden rounded-full bg-surface-2">
              <div className="h-full bg-cyan" style={{ width: `${Math.round(progress * 100)}%` }} />
            </div>
          )}
          {problem && (
            <p role="alert" className="rounded-lg border border-err/30 bg-err-soft px-4 py-2.5 text-[13px] text-err">
              {problem}
            </p>
          )}
          {result && (
            <div role="status" className="rounded-lg border border-ok/30 bg-ok-soft px-4 py-2.5 text-[13px] text-ok">
              <p>{result.alreadyUploaded ? 'This file was already uploaded; showing the first run' : 'The file was uploaded and a new run has started.'}</p>
              <Link to={`/executions/${result.submission.executionId}`} className="font-medium underline">
                Open the run
              </Link>
            </div>
          )}
        </div>
      )}
    </Dialog>
  )
}
