import { describe, expect, it } from 'vitest'

import { validateDesign } from '../model/validate'
import { importDocument, ImportError } from './index'
import { asFile, docx, imagePara, para, PNG_BASE64, table, zip } from './testFiles'

/** An imported design must always be accepted by the server: no structural error. Unmapped data fields are expected. */
const structuralErrors = (design: Awaited<ReturnType<typeof importDocument>>) =>
  validateDesign(design.layout, design.pageSettings, { type: 'object' }).filter((i) => i.severity === 'error' && i.code !== 'binding.unmapped')

const blocksOf = (d: Awaited<ReturnType<typeof importDocument>>) => d.layout.body?.blocks ?? []

describe('importing a DOCX', () => {
  it('turns paragraphs into text blocks: merging neighbours in one style, spacing for blank lines, keeping styles and fields', async () => {
    const design = await importDocument(
      asFile(
        docx({
          body:
            para('Statement of account', { bold: true, size: 20, center: true }) +
            para('Dear {{customer.name}},') +
            para('Please find your statement below.') +
            para('') +
            para('Kind regards', { bold: true }),
        }),
        'letter.docx',
      ),
    )

    expect(blocksOf(design)).toEqual([
      { id: 'p1', type: 'text', props: { text: 'Statement of account', style: { fontSizePt: 20, bold: true, align: 'center' } } },
      { id: 'p2', type: 'text', props: { text: 'Dear {{customer.name}},\nPlease find your statement below.' } },
      { id: 'p3', spaceBeforeMm: 4, type: 'text', props: { text: 'Kind regards', style: { bold: true } } },
    ])
    expect(design.summary).toMatchObject({ text: 3, tables: 0, images: 0 })
    expect(structuralErrors(design)).toEqual([])
  })

  it('inherits size and bold from the paragraph style', async () => {
    const design = await importDocument(
      asFile(
        docx({
          styles: '<w:style w:styleId="Heading1"><w:rPr><w:b/><w:sz w:val="36"/></w:rPr></w:style><w:style w:styleId="Title" ><w:basedOn w:val="Heading1"/></w:style>',
          body: para('Big', { style: 'Title' }),
        }),
        'a.docx',
      ),
    )
    expect(blocksOf(design)[0]).toMatchObject({ props: { style: { fontSizePt: 18, bold: true } } })
  })

  it('turns a table into a repeating table: headings, bindings from {{fields}} or the headings, widths and number alignment', async () => {
    const design = await importDocument(
      asFile(
        docx({
          body: table(
            [
              ['Description', 'Unit price', 'Qty'],
              ['{{line.description}}', '12.50', '2'],
              ['dropped sample row', '1', '1'],
            ],
            [5000, 3000, 2000],
          ),
        }),
        't.docx',
      ),
    )

    expect(blocksOf(design)).toEqual([
      {
        id: 'table1',
        type: 'table',
        props: {
          binding: 'items',
          columns: [
            { header: 'Description', binding: 'description', widthPct: 50 },
            { header: 'Unit price', binding: 'unitPrice', align: 'right', widthPct: 30 },
            { header: 'Qty', binding: 'qty', align: 'right', widthPct: 20 },
          ],
        },
      },
    ])
    expect(design.report.join(' ')).toMatch(/below its first data row were dropped/)
    expect(design.report.join(' ')).toMatch(/map every column/)
    expect(structuralErrors(design)).toEqual([])
  })

  it('turns a layout-only table (one row or one column) into text', async () => {
    const design = await importDocument(asFile(docx({ body: table([['ACME Ltd', '1 High St']]) }), 'l.docx'))
    expect(blocksOf(design).map((b) => b.type)).toEqual(['text'])
    expect(design.report.join(' ')).toMatch(/page layout/)
  })

  it('imports PNG images with their picture data, and drops other formats', async () => {
    const design = await importDocument(asFile(docx({ body: para('Logo:') + imagePara, withImage: true }), 'i.docx'))
    const image = blocksOf(design).find((b) => b.type === 'image')
    expect(image).toMatchObject({ type: 'image', props: { src: `data:image/png;base64,${PNG_BASE64}`, fit: 'contain' } })
    expect(structuralErrors(design)).toEqual([])
  })

  it('maps the header and footer to positioned bands, and reads page margins and orientation', async () => {
    const design = await importDocument(
      asFile(
        docx({
          body: para('Body'),
          header: para('ACME Ltd', { bold: true, size: 14 }) + para('1 High Street'),
          footer: para('Registered in Cyprus', { size: 8 }),
          page: { w: 16838, h: 11906, top: 1134, right: 1134, bottom: 1134, left: 1134 },
        }),
        'h.docx',
      ),
    )

    expect(design.pageSettings).toMatchObject({ orientation: 'landscape', marginsMm: { top: 20, right: 20, bottom: 20, left: 20 } })
    expect(design.layout.header?.elements.map((e) => ({ type: e.type, xMm: e.xMm, text: 'text' in e.props ? e.props.text : '' }))).toEqual([
      { type: 'text', xMm: 0, text: 'ACME Ltd' },
      { type: 'text', xMm: 0, text: '1 High Street' },
    ])
    expect(design.layout.header!.heightMm).toBeGreaterThan(5)
    expect(design.layout.footer?.elements).toHaveLength(1)
    expect(design.summary).toMatchObject({ header: 2, footer: 1 })
    expect(structuralErrors(design)).toEqual([])
  })

  it('reports a page size that is not A4', async () => {
    const design = await importDocument(asFile(docx({ body: para('x'), page: { w: 12240, h: 15840, top: 1440, right: 1440, bottom: 1440, left: 1440 } }), 'us.docx'))
    expect(design.report.join(' ')).toMatch(/not A4/)
  })

  it('reads a package whose parts are stored without compression', async () => {
    const buffer = zip(
      {
        'word/document.xml': '<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Stored</w:t></w:r></w:p></w:body></w:document>',
      },
      true,
    )
    expect(blocksOf(await importDocument(asFile(buffer, 's.docx')))[0]).toMatchObject({ props: { text: 'Stored' } })
  })

  it('refuses a package with a document type declaration (entity bombs)', async () => {
    const buffer = zip({ 'word/document.xml': '<?xml version="1.0"?><!DOCTYPE d [<!ENTITY a "aaaa">]><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body/></w:document>' })
    await expect(importDocument(asFile(buffer, 'x.docx'))).rejects.toThrow(/document type declaration/)
  })

  it('refuses a ZIP with no Word document, and a damaged one', async () => {
    await expect(importDocument(asFile(zip({ 'other.txt': 'x' }), 'x.docx'))).rejects.toThrow('The package has no Word document.')
    await expect(importDocument(asFile('PK\u0003\u0004 not really a zip file at all', 'x.docx'))).rejects.toThrow(/not a valid DOCX/)
  })

  it('says so when nothing usable is in the document', async () => {
    const design = await importDocument(asFile(docx({ body: para('') }), 'empty.docx'))
    expect(design.layout).toEqual({ layoutSchemaVersion: 1 })
    expect(design.report.join(' ')).toMatch(/No text, images or tables/)
  })
})

describe('importing other files', () => {
  it('explains how to handle an old binary .doc', async () => {
    const ole = new Uint8Array([0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1, 0, 0, 0, 0])
    const error = await importDocument(asFile(ole.buffer as ArrayBuffer, 'old.doc')).catch((e: unknown) => e)
    expect(error).toBeInstanceOf(ImportError)
    expect((error as Error).message).toMatch(/Save As → Word Document \(\.docx\)/)
  })

  it('rejects unknown content and empty files, whatever the extension says', async () => {
    await expect(importDocument(asFile('just text', 'a.docx'))).rejects.toThrow('Choose a Word document (.docx) or an RTF file (.rtf).')
    await expect(importDocument(asFile('', 'a.docx'))).rejects.toThrow('The file is empty.')
  })

  it('recognises RTF by its content, so an RTF saved as .doc still imports', async () => {
    const design = await importDocument(asFile('{\\rtf1\\ansi Hello\\par}', 'saved-as.doc'))
    expect(blocksOf(design)[0]).toMatchObject({ props: { text: 'Hello' } })
  })
})
