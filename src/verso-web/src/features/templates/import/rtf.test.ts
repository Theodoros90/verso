import { describe, expect, it } from 'vitest'

import { validateDesign } from '../model/validate'
import { importDocument } from './index'
import { asFile, PNG_BASE64 } from './testFiles'

const pngHex = [...atob(PNG_BASE64)].map((c) => c.charCodeAt(0).toString(16).padStart(2, '0')).join('')

const rtf = (body: string, head = '') => `{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\froman Times New Roman;}{\\f1\\fswiss Arial;}{\\f2\\fmodern Courier New;}}{\\colortbl;\\red255\\green0\\blue0;}${head}${body}}`

const run = (text: string) => importDocument(asFile(text, 'x.rtf'))

describe('importing RTF', () => {
  it('reads paragraphs with bold, italic, size, colour, alignment and fonts', async () => {
    const design = await run(rtf('\\pard\\qc\\b\\fs40 Title\\b0\\par\\pard\\f2\\fs20 Code line\\par\\pard\\plain\\cf1\\i Red italic\\par'))

    expect(design.layout.body!.blocks.map((b) => ('props' in b ? b.props : null))).toEqual([
      { text: 'Title', style: { fontSizePt: 20, bold: true, align: 'center', font: 'serif' } },
      { text: 'Code line', style: { font: 'mono' } },
      { text: 'Red italic', style: { italic: true, color: '#FF0000', font: 'serif' } },
    ])
  })

  it('decodes escapes: hex bytes in the declared code page, unicode, symbols and braces', async () => {
    const greek = await run('{\\rtf1\\ansi\\ansicpg1253 \\\'c1\\\'e2\\\'e3 caf\\u233? \\u8364? \\emdash  \\{x\\}\\par}')
    expect((greek.layout.body!.blocks[0] as { props: { text: string } }).props.text).toBe('Αβγ café € — {x}')
  })

  it('skips destinations that are not text: stylesheet, info, ignorable groups, field instructions', async () => {
    const design = await run(
      rtf(
        'Visible {\\*\\generator hidden}{\\info{\\title Secret title}}{\\stylesheet{\\s1 Normal;}}{\\field{\\*\\fldinst PAGE}{\\fldrslt 7}} end\\par',
      ),
    )
    expect((design.layout.body!.blocks[0] as { props: { text: string } }).props.text).toBe('Visible 7 end')
  })

  it('turns a table into a repeating table with widths from the cell boundaries', async () => {
    const row = (cells: string[]) =>
      `\\trowd\\cellx4000\\cellx7000\\cellx10000 ${cells.map((c) => `\\pard\\intbl ${c}\\cell`).join('')}\\row`
    const design = await run(rtf(`Before\\par ${row(['Item', 'Price', 'Qty'])}${row(['\\{\\{line.name\\}\\}', '9.99', '1'])}\\pard After\\par`))

    const blocks = design.layout.body!.blocks
    expect(blocks.map((b) => b.type)).toEqual(['text', 'table', 'text'])
    expect((blocks[1] as { props: unknown }).props).toEqual({
      binding: 'items',
      columns: [
        { header: 'Item', binding: 'name', widthPct: 40 },
        { header: 'Price', binding: 'price', align: 'right', widthPct: 30 },
        { header: 'Qty', binding: 'qty', align: 'right', widthPct: 30 },
      ],
      style: { font: 'serif' }, // the document's default font (\deff0) is Times New Roman
    })
  })

  it('reads header, footer and page setup', async () => {
    const design = await run(
      rtf('{\\header\\pard\\b ACME Ltd\\par}{\\footer\\pard Page footer\\par}\\pard Body\\par', '\\paperw16838\\paperh11906\\landscape\\margl1134\\margr1134\\margt1134\\margb1134 '),
    )

    expect(design.pageSettings).toMatchObject({ orientation: 'landscape', marginsMm: { top: 20, left: 20 } })
    expect(design.layout.header!.elements).toHaveLength(1)
    expect(design.layout.footer!.elements).toHaveLength(1)
    expect(design.layout.body!.blocks).toHaveLength(1)
  })

  it('imports a PNG picture and reports a metafile as left out', async () => {
    const design = await run(rtf(`Logo\\par{\\*\\shppict{\\pict\\pngblip\\picwgoal1000\\pichgoal500 ${pngHex}}}{\\nonshppict{\\pict\\emfblip 0102}}\\par`))
    const image = design.layout.body!.blocks.find((b) => b.type === 'image')
    expect(image).toMatchObject({ props: { src: `data:image/png;base64,${PNG_BASE64}` } })
    const emf = await run(rtf('{\\pict\\emfblip 01020304}\\par'))
    expect(emf.report.join(' ')).toMatch(/PNG and JPEG/)
  })

  it('produces a layout the server accepts (apart from fields still to map)', async () => {
    const design = await run(rtf('\\pard\\fs24 Hello {{customer.name}}\\par\\trowd\\cellx5000\\cellx10000 \\pard\\intbl A\\cell\\pard\\intbl B\\cell\\row\\trowd\\cellx5000\\cellx10000 \\pard\\intbl 1\\cell\\pard\\intbl 2\\cell\\row\\pard\\par'))
    const errors = validateDesign(design.layout, design.pageSettings, { type: 'object' }).filter((i) => i.severity === 'error' && i.code !== 'binding.unmapped')
    expect(errors).toEqual([])
  })

  it('rejects broken RTF', async () => {
    await expect(run('{\\rtf1 unbalanced {')).rejects.toThrow(/unbalanced braces/)
  })
})
