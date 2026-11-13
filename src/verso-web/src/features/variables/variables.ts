// Global variables screen model. Frontend-only: nothing is persisted, and a sensitive variable never has its value
// entered or shown here (secrets belong to the bank's secret store).

export const VARIABLE_TYPES = ['Text', 'Number', 'Boolean', 'Date', 'JSON', 'Secret'] as const
export const VARIABLE_SCOPES = ['Tenant', 'Environment', 'Workflow'] as const
export const VARIABLE_ENVIRONMENTS = ['All', 'Development', 'Test', 'Production'] as const

export type VariableType = (typeof VARIABLE_TYPES)[number]
export type VariableScope = (typeof VARIABLE_SCOPES)[number]
export type VariableEnvironment = (typeof VARIABLE_ENVIRONMENTS)[number]

export interface GlobalVariable {
  id: string
  name: string
  type: VariableType
  scope: VariableScope
  env: VariableEnvironment
  /** Empty for sensitive variables: their value lives outside Verso. */
  value: string
  sensitive: boolean
  description: string
  updated: string
}

export const MASK = '••••••••••••••••'

/** Names are used as {{name}} in step text, so they are letters, digits and underscore only. */
export const VARIABLE_NAME = /^[A-Za-z_][A-Za-z0-9_]*$/

export const placeholder = (name: string) => `{{${name}}}`

/** Synthetic variables shown until a backend exists. */
export function seedVariables(): GlobalVariable[] {
  const v = (id: string, name: string, type: VariableType, scope: VariableScope, env: VariableEnvironment, value: string, description: string, updated: string): GlobalVariable => ({
    id,
    name,
    type,
    scope,
    env,
    value,
    sensitive: type === 'Secret',
    description,
    updated,
  })
  return [
    v('v1', 'companyName', 'Text', 'Tenant', 'All', 'Halcyon Energy', 'Legal name printed on every document.', '2 weeks ago'),
    v('v2', 'defaultCurrency', 'Text', 'Tenant', 'All', 'EUR', 'Used when the source record has no currency.', '2 weeks ago'),
    v('v3', 'vatRate', 'Number', 'Tenant', 'All', '19', 'Cyprus VAT rate for calculations and checks.', '1 month ago'),
    v('v4', 'invoiceEmailFrom', 'Text', 'Tenant', 'All', 'billing@halcyon-energy.cy', 'From address for invoice email.', '3 weeks ago'),
    v('v5', 'statementStorage', 'Text', 'Tenant', 'All', '/finance/statements', 'Base archive folder for statements.', '1 month ago'),
    v('v6', 'portalApiKey', 'Secret', 'Environment', 'Test', '', 'Key for the customer portal API. Referenced, never printed.', '5 days ago'),
  ]
}

export interface VariableDraft {
  name: string
  type: VariableType
  scope: VariableScope
  env: VariableEnvironment
  value: string
  sensitive: boolean
  description: string
}

export interface VariableErrors {
  name?: string
  value?: string
}

/** Validates a draft against the other variables' names. */
export function validateVariable(draft: VariableDraft, otherNames: string[]): VariableErrors {
  const errors: VariableErrors = {}
  const name = draft.name.trim()
  if (!name) errors.name = 'Give the variable a name.'
  else if (!VARIABLE_NAME.test(name)) errors.name = 'Use letters, numbers and underscore, starting with a letter or underscore.'
  else if (otherNames.some((n) => n.toLowerCase() === name.toLowerCase())) errors.name = 'A variable with that name already exists.'
  if (!draft.sensitive && !draft.value.trim()) errors.value = 'A value is required.'
  return errors
}
