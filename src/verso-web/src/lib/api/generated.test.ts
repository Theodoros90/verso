import { describe, expect, it } from 'vitest'

import openApiRaw from '../../../../../docs/openapi/v1.json?raw'
// @ts-expect-error -- plain ESM script without type declarations
import { generate } from '../../../scripts/generate-api-types.mjs'
import generatedRaw from './generated.ts?raw'

describe('generated API types', () => {
  it('match docs/openapi/v1.json (run `npm run gen:api` after an API change)', () => {
    const expected = (generate as (doc: unknown) => string)(JSON.parse(openApiRaw))
    expect(generatedRaw.replace(/\r\n/g, '\n')).toBe(expected)
  })
})
