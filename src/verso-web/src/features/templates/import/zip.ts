import { ImportError } from './types'

/** Biggest single part we will inflate. A DOCX part is XML or an image; anything larger is refused (zip bombs). */
const MAX_ENTRY_BYTES = 30 * 1024 * 1024

interface Entry {
  method: number
  compressedSize: number
  size: number
  offset: number
}

export interface Zip {
  has(name: string): boolean
  /** The part's bytes, or null when it isn't in the package. */
  read(name: string): Promise<Uint8Array | null>
}

/** A small ZIP reader (stored and deflate parts) for DOCX packages, using the browser's own inflater. No ZIP64, no encryption. */
export function readZip(buffer: ArrayBuffer): Zip {
  const bytes = new Uint8Array(buffer)
  const view = new DataView(buffer)

  let end = -1
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 22 - 65_535); i--) {
    if (view.getUint32(i, true) === 0x06054b50) {
      end = i
      break
    }
  }
  if (end < 0) throw new ImportError('That file is not a valid DOCX (it is not a ZIP package).')

  const count = view.getUint16(end + 10, true)
  let p = view.getUint32(end + 16, true)
  const entries = new Map<string, Entry>()
  const names = new TextDecoder('utf-8')
  for (let n = 0; n < count; n++) {
    if (p + 46 > bytes.length || view.getUint32(p, true) !== 0x02014b50) throw new ImportError('That file is not a valid DOCX (damaged ZIP directory).')
    const flags = view.getUint16(p + 8, true)
    const nameLength = view.getUint16(p + 28, true)
    const extraLength = view.getUint16(p + 30, true)
    const commentLength = view.getUint16(p + 32, true)
    if (flags & 1) throw new ImportError('Password-protected documents can’t be imported. Remove the password in Word and try again.')
    const compressedSize = view.getUint32(p + 20, true)
    const size = view.getUint32(p + 24, true)
    const offset = view.getUint32(p + 42, true)
    if (compressedSize === 0xffffffff || size === 0xffffffff || offset === 0xffffffff) throw new ImportError('ZIP64 packages are not supported.')
    const name = names.decode(bytes.subarray(p + 46, p + 46 + nameLength))
    entries.set(name, { method: view.getUint16(p + 10, true), compressedSize, size, offset })
    p += 46 + nameLength + extraLength + commentLength
  }

  return {
    has: (name) => entries.has(name),
    async read(name) {
      const entry = entries.get(name)
      if (!entry) return null
      if (entry.size > MAX_ENTRY_BYTES) throw new ImportError('The document is too large to import.')
      const at = entry.offset
      if (at + 30 > bytes.length || view.getUint32(at, true) !== 0x04034b50) throw new ImportError('That file is not a valid DOCX (damaged part).')
      const start = at + 30 + view.getUint16(at + 26, true) + view.getUint16(at + 28, true)
      const data = bytes.subarray(start, start + entry.compressedSize)
      if (data.length !== entry.compressedSize) throw new ImportError('That file is not a valid DOCX (truncated).')
      if (entry.method === 0) return data.slice()
      if (entry.method !== 8) throw new ImportError('That DOCX uses a compression method Verso can’t read.')
      return inflate(data, Math.min(entry.size, MAX_ENTRY_BYTES))
    },
  }
}

async function inflate(data: Uint8Array, expected: number): Promise<Uint8Array> {
  if (typeof DecompressionStream === 'undefined') throw new ImportError('This browser can’t open DOCX files. Use a current version of Edge, Chrome or Firefox.')
  const source = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(data)
      controller.close()
    },
  })
  const reader = source.pipeThrough(new DecompressionStream('deflate-raw') as unknown as ReadableWritablePair<Uint8Array, Uint8Array>).getReader()
  const chunks: Uint8Array[] = []
  let total = 0
  for (;;) {
    const { done, value } = await reader.read()
    if (done) break
    total += value.length
    // The directory says how big the part is; a part that inflates to much more than that is a bomb or damaged.
    if (total > expected + 1024) {
      await reader.cancel()
      throw new ImportError('That file is not a valid DOCX (a part is larger than it says).')
    }
    chunks.push(value)
  }
  const out = new Uint8Array(total)
  let at = 0
  for (const c of chunks) {
    out.set(c, at)
    at += c.length
  }
  return out
}
