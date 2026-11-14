import { describe, expect, it } from 'vitest'

import { seedConnections } from '../connections/connections'
import { seedVariables } from '../variables/variables'
import { applyLocal, buildFile, countConflicts, PACKAGE_PRODUCT, readFile } from './package'

const server = { product: PACKAGE_PRODUCT, schemaVersion: 1, environment: 'Test', exportedAt: '2026-11-14T10:00:00Z', templates: [], workflows: [] }
const text = (over: Record<string, unknown> = {}) => JSON.stringify({ ...buildFile(server, seedConnections(), seedVariables()), ...over })

describe('configuration file', () => {
  it('carries no secret and no internal id', () => {
    const file = buildFile(server, seedConnections(), seedVariables())
    const serialized = JSON.stringify(file)

    expect(serialized).not.toMatch(/"id"|password|"secret":/i)
    expect(file.variables.find((v) => v.name === 'portalApiKey')).toMatchObject({ sensitive: true, value: '' })
  })

  it('round-trips through readFile', () => {
    const read = readFile(text())
    if ('error' in read) throw new Error(read.error)

    expect(read.file.connections).toHaveLength(seedConnections().length)
    expect(read.file.variables).toHaveLength(seedVariables().length)
    expect(read.file.environment).toBe('Test')
    expect(read.skipped).toEqual({ connections: 0, variables: 0 })
  })

  it.each([
    ['not JSON', 'nope', 'That file is not valid JSON.'],
    ['another product', JSON.stringify({ product: 'x' }), 'That file is not a Verso configuration package.'],
    ['a newer version', text({ schemaVersion: 2 }), 'Unsupported package version 2.'],
    ['a missing section', text({ templates: undefined }), 'The templates section is missing.'],
  ])('rejects %s', (_name, input, error) => {
    expect(readFile(input)).toEqual({ error })
  })

  it('drops invalid connections and variables, and never keeps a value for a sensitive variable', () => {
    const read = readFile(
      text({
        connections: [{ name: 'No endpoint', group: 'Data sources' }],
        variables: [
          { name: 'ok', type: 'Text', value: 'v' },
          { name: 'bad name', type: 'Text', value: 'v' },
          { name: 'OK', type: 'Text', value: 'duplicate in the file' },
          { name: 'token', type: 'Secret', value: 'must-not-survive' },
        ],
      }),
    )
    if ('error' in read) throw new Error(read.error)

    expect(read.file.variables.map((v) => v.name)).toEqual(['ok', 'token'])
    expect(read.file.variables[1]).toMatchObject({ sensitive: true, value: '' })
    expect(read.skipped).toEqual({ connections: 1, variables: 2 })
  })
})

describe('applying a file', () => {
  const read = (over: Record<string, unknown>) => {
    const r = readFile(text(over))
    if ('error' in r) throw new Error(r.error)
    return r.file
  }
  const connections = seedConnections()
  const variables = seedVariables()

  it('counts the names already here, ignoring case', () => {
    const file = read({ variables: [{ name: 'VATRATE', type: 'Number', value: '21' }, { name: 'newOne', type: 'Text', value: 'x' }] })
    expect(countConflicts(file, connections, variables)).toEqual({ connections: connections.length, variables: 1 })
  })

  it('merge: updates same-named items in place and adds new ones, leaving the rest', () => {
    const file = read({ variables: [{ name: 'vatRate', type: 'Number', value: '21' }, { name: 'newOne', type: 'Text', value: 'x' }], connections: [] })
    const next = applyLocal(file, 'merge', connections, variables)

    expect(next.connections).toEqual(connections)
    expect(next.variables).toHaveLength(variables.length + 1)
    expect(next.variables.find((v) => v.name === 'vatRate')).toMatchObject({ id: variables.find((v) => v.name === 'vatRate')!.id, value: '21' })
    expect(next.variables.find((v) => v.name === 'companyName')).toBeDefined()
  })

  it('replace: makes the lists match the file, keeping ids of same-named items', () => {
    const file = read({ variables: [{ name: 'vatRate', type: 'Number', value: '21' }], connections: [] })
    const next = applyLocal(file, 'replace', connections, variables)

    expect(next.connections).toEqual([])
    expect(next.variables.map((v) => v.name)).toEqual(['vatRate'])
    expect(next.variables[0]!.id).toBe(variables.find((v) => v.name === 'vatRate')!.id)
  })
})
