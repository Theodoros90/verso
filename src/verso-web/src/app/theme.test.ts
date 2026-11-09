import { describe, expect, it } from 'vitest'

import { resolveTheme } from './theme'

describe('resolveTheme', () => {
  it.each([
    ['system', false, 'light'],
    ['system', true, 'dark'],
    ['light', true, 'light'],
    ['dark', false, 'dark'],
  ] as const)('preference %s with system dark=%s resolves to %s', (preference, systemDark, expected) => {
    expect(resolveTheme(preference, systemDark)).toBe(expected)
  })
})
