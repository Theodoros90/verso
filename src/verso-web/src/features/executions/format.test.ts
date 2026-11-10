import { describe, expect, it } from 'vitest'

import type { ExecutionEventDto } from '@/lib/api/generated'

import { dayEndIso, dayStartIso, formatBytes, formatDuration, runDuration, shortId, statusTone } from './format'
import { mergeEvents } from './useExecutionLive'

const ev = (id: number, type = 'StepStarted'): ExecutionEventDto => ({ id, executionId: 'x', nodeId: null, type, occurredAt: '2026-11-10T09:00:00Z', data: null })

describe('mergeEvents', () => {
  it('adds new events in id order and never duplicates', () => {
    expect(mergeEvents([ev(1), ev(2)], [ev(3), ev(4)]).map((e) => e.id)).toEqual([1, 2, 3, 4])
    expect(mergeEvents([ev(1), ev(2), ev(3)], [ev(2), ev(3), ev(4)]).map((e) => e.id)).toEqual([1, 2, 3, 4]) // overlapping catch-up
    expect(mergeEvents([ev(5)], [ev(3), ev(4)]).map((e) => e.id)).toEqual([3, 4, 5]) // late arrivals sort into place
  })

  it('keeps the existing list when nothing new arrives', () => {
    expect(mergeEvents([ev(1)], []).map((e) => e.id)).toEqual([1])
  })
})

describe('format helpers', () => {
  it('shows the last 8 characters of an id', () => {
    expect(shortId('0193a6c2-0000-7000-8000-00000000aaaa')).toBe('0000aaaa')
  })

  it('formats durations, bytes and run time', () => {
    expect(formatDuration(18)).toBe('18 ms')
    expect(formatDuration(4348)).toBe('4.3 s')
    expect(formatDuration(125_000)).toBe('2 min 5 s')
    expect(formatDuration(null)).toBe('–')
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(21284)).toBe('20.8 KB')
    expect(runDuration('2026-11-10T09:00:00Z', '2026-11-10T09:00:04.5Z')).toBe(4500)
    expect(runDuration('2026-11-10T09:00:00Z', null)).toBeNull()
  })

  it('maps statuses to pill tones', () => {
    expect(['Succeeded', 'Rejected', 'Failed', 'Running', 'Queued'].map(statusTone)).toEqual(['ok', 'warn', 'err', 'run', 'neutral'])
  })

  it('turns a date input into the start of that day and the start of the next', () => {
    expect(dayStartIso('2026-11-02')).toBe(new Date('2026-11-02T00:00:00').toISOString())
    expect(dayEndIso('2026-11-02')).toBe(new Date('2026-11-03T00:00:00').toISOString())
    expect(dayEndIso('2026-11-30')).toBe(new Date('2026-12-01T00:00:00').toISOString())
    expect(dayStartIso('')).toBeUndefined()
    expect(dayEndIso('')).toBeUndefined()
  })
})
