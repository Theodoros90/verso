import { describe, expect, it } from 'vitest'

import { formatRetention, parseRetention } from './retention'

describe('retention', () => {
  it('parses an amount and a unit', () => {
    expect(parseRetention('6m')).toEqual({ amount: 6, unit: 'm' })
    expect(parseRetention('90d')).toEqual({ amount: 90, unit: 'd' })
    expect(parseRetention('permanent')).toBeNull()
    expect(parseRetention('0m')).toBeNull()
    expect(parseRetention('')).toBeNull()
  })

  it('formats for people', () => {
    expect(formatRetention('7y')).toBe('7 years')
    expect(formatRetention('1m')).toBe('1 month')
    expect(formatRetention('90d')).toBe('90 days')
    expect(formatRetention('permanent')).toBe('Permanently')
  })
})
