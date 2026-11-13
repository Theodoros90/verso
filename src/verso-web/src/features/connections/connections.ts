// Connections screen model. Frontend-only and simulated: nothing here contacts an external system,
// and no secret is ever collected, stored or exported (secrets belong to the bank's secret store).

export const CONNECTION_GROUPS = [
  { name: 'Data sources', description: 'Where document data comes from' },
  { name: 'Delivery channels', description: 'Where finished documents go' },
  { name: 'Signing & storage', description: 'Sealing, retention and copies' },
] as const

export type ConnectionGroup = (typeof CONNECTION_GROUPS)[number]['name']
export type ConnectionHealth = 'ok' | 'warn' | 'err'

export const AUTH_METHODS = ['OAuth 2.0 client credentials', 'API key', 'Bearer token', 'Mutual TLS', 'Managed identity', 'SSH key', 'SMTP credentials', 'Certificate'] as const

export interface Connection {
  id: string
  group: ConnectionGroup
  name: string
  /** Protocol or format, shown under the name. */
  category: string
  description: string
  /** Last known health; ignored while the connection is switched off. */
  health: ConnectionHealth
  /** Short status detail next to the pill, e.g. "Synced 4 min ago". */
  last: string
  enabled: boolean
  auth: string
  endpoint: string
  /** Workflow steps that reference this connection (synthetic until workflows reference connections). */
  usedBySteps: number
  /** Logo tile colour and letters. */
  color: string
  abbr: string
}

/** Synthetic connections shown until a backend exists. */
export function seedConnections(): Connection[] {
  const c = (
    id: string,
    group: ConnectionGroup,
    name: string,
    category: string,
    description: string,
    health: ConnectionHealth,
    last: string,
    enabled: boolean,
    auth: string,
    endpoint: string,
    usedBySteps: number,
    color: string,
    abbr: string,
  ): Connection => ({ id, group, name, category, description, health, last, enabled, auth, endpoint, usedBySteps, color, abbr })
  return [
    c('sap', 'Data sources', 'SAP S/4HANA', 'OData v4 · API / HTTP endpoint', 'Billing documents and customer master data for invoices.', 'ok', 'Synced 4 min ago', true, 'OAuth 2.0 client credentials', 'https://s4h.halcyon.internal/sap/opu/odata4/billing/v1', 0, '#0A6EB4', 'SAP'),
    c('d365', 'Data sources', 'Microsoft Dynamics 365', 'Dataverse Web API · API / HTTP endpoint', 'Accounts, contacts and invoice lines for the commercial invoice.', 'warn', '12 records with missing fields', true, 'OAuth 2.0 client credentials', 'https://halcyon.crm4.dynamics.com/api/data/v9.2', 0, '#5E3BB0', 'D365'),
    c('core', 'Data sources', 'Core banking', 'ISO 20022 camt.053 · SQL database', 'Daily account statements and transaction history.', 'ok', 'Synced 01:12 today', true, 'Managed identity', 'sql-halcyon.database.windows.net / BillingCore', 0, '#1A3442', 'CB'),
    c('kafka', 'Data sources', 'Kafka', 'Topic billing.events · API / HTTP endpoint', 'Real-time trigger for single documents such as instant receipts.', 'ok', '1,204 events in the last hour', true, 'Mutual TLS', 'kafka.halcyon.internal:9093', 0, '#231F20', 'K'),
    c('sf', 'Data sources', 'Salesforce', 'REST API · API / HTTP endpoint', 'Opportunity data for quotes and proposals.', 'ok', 'Not connected', false, 'OAuth 2.0 client credentials', 'https://login.salesforce.com', 0, '#00A1E0', 'SF'),
    c('smtp', 'Delivery channels', 'SMTP relay', 'Email · SMTP email', 'Sends HTML email with PDF attachments from billing@halcyon-energy.cy.', 'err', '37 rejections today', true, 'SMTP credentials', 'smtp-relay.halcyon-energy.cy:587', 3, '#BE3D37', '@'),
    c('sftp', 'Delivery channels', 'Print house SFTP', 'PCL 6 batches · SFTP', 'Nightly print and post files to Cyprus Print Services.', 'warn', 'Key expires in 6 days', true, 'SSH key', 'sftp.cyprusprintservices.com:22', 3, '#5B6773', 'SFTP'),
    c('portal', 'Delivery channels', 'Customer portal', 'Webhook · API / HTTP endpoint', 'Publishes documents to the self-service portal and notifies customers.', 'ok', 'Delivered 2 min ago', true, 'API key', 'https://portal.halcyon-energy.cy/api/hooks/verso', 3, '#0A6E8A', 'WEB'),
    c('peppol', 'Delivery channels', 'Peppol e-invoicing', 'Access point · API / HTTP endpoint', 'UBL 2.1 invoices for EU business customers.', 'ok', '48 sent today', true, 'Mutual TLS', 'https://peppol.halcyon-accesspoint.cy/v1', 2, '#2F7D4F', 'PEP'),
    c('sms', 'Delivery channels', 'SMS gateway', 'Secure links · API / HTTP endpoint', 'Text message with a one-time link to the document.', 'ok', 'Not connected', false, 'API key', 'https://sms.example-gateway.com/v2', 0, '#B7791F', 'SMS'),
    c('qes', 'Signing & storage', 'Qualified e-signature', 'eIDAS QES · API / HTTP endpoint', 'Seals statements and certificates with a qualified certificate.', 'ok', 'Certificate valid to Mar 2028', true, 'Certificate', 'https://qes.halcyon-trust.cy/seal/v1', 1, '#A82E6B', 'QES'),
    c('archive', 'Signing & storage', 'Long-term archive', 'S3 Object Lock (WORM) · API / HTTP endpoint', 'Immutable storage with retention rules per document type.', 'ok', '2.4M documents', true, 'Managed identity', 'https://archive.halcyon.internal/verso', 2, '#E8B30F', 'S3'),
    c('sharepoint', 'Signing & storage', 'SharePoint Online', 'Microsoft Graph · API / HTTP endpoint', 'Copies customer correspondence into account folders.', 'ok', 'Synced 1 h ago', true, 'OAuth 2.0 client credentials', 'https://graph.microsoft.com/v1.0/sites/halcyon', 0, '#03787C', 'SP'),
  ]
}

export type DisplayStatus = { tone: 'ok' | 'warn' | 'err' | 'neutral'; label: string; detail: string }

export function displayStatus(c: Connection): DisplayStatus {
  if (!c.enabled) return { tone: 'neutral', label: 'Off', detail: 'Not connected' }
  if (c.health === 'warn') return { tone: 'warn', label: 'Needs attention', detail: c.last }
  if (c.health === 'err') return { tone: 'err', label: 'Failing', detail: c.last }
  return { tone: 'ok', label: 'Connected', detail: c.last }
}

/** Logo letters for a new connection: up to three letters from the name's words. */
export function abbreviate(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean)
  if (words.length === 0) return '?'
  const letters = words.length === 1 ? words[0]!.slice(0, 3) : words.slice(0, 3).map((w) => w[0]).join('')
  return letters.toUpperCase()
}

export interface SimulatedTest {
  ok: boolean
  message: string
}

/** Pretends to test a connection from its last known health. No request is made. */
export function simulateTest(c: Pick<Connection, 'name' | 'health'>): SimulatedTest {
  const ms = 80 + ((c.name.length * 37) % 120)
  if (c.health === 'err') return { ok: false, message: 'Simulated: the endpoint answered but rejected the test message. Check the credential and sender settings.' }
  if (c.health === 'warn') return { ok: true, message: `Simulated: connected in ${ms} ms, but the credential needs attention.` }
  return { ok: true, message: `Simulated: connected in ${ms} ms. Authentication and permissions look correct.` }
}

type Exported = Omit<Connection, 'id'>

/** Export format: configuration only. There is no secret field to leak. */
export function serializeConnections(connections: Connection[]): string {
  const items: Exported[] = connections.map(({ id: _id, ...rest }) => rest)
  return JSON.stringify({ format: 'verso-connections', version: 1, connections: items }, null, 2)
}

const isGroup = (v: unknown): v is ConnectionGroup => CONNECTION_GROUPS.some((g) => g.name === v)
const str = (v: unknown) => (typeof v === 'string' ? v.trim() : '')

/** Parses an export file. Returns the valid items and how many entries were skipped. */
export function parseConnections(text: string): { items: Omit<Connection, 'id'>[]; skipped: number } | { error: string } {
  let data: unknown
  try {
    data = JSON.parse(text)
  } catch {
    return { error: 'That file is not valid JSON.' }
  }
  const list = (data as { format?: unknown; connections?: unknown } | null)?.connections
  if ((data as { format?: unknown } | null)?.format !== 'verso-connections' || !Array.isArray(list)) {
    return { error: 'That file is not a Verso connections export.' }
  }
  const items: Omit<Connection, 'id'>[] = []
  let skipped = 0
  for (const raw of list as Record<string, unknown>[]) {
    const name = str(raw?.name)
    const endpoint = str(raw?.endpoint)
    if (!name || !endpoint || !isGroup(raw.group)) {
      skipped++
      continue
    }
    items.push({
      group: raw.group,
      name,
      category: str(raw.category),
      description: str(raw.description),
      health: raw.health === 'warn' || raw.health === 'err' ? raw.health : 'ok',
      last: str(raw.last) || 'Not tested yet',
      enabled: raw.enabled !== false,
      auth: str(raw.auth) || 'API key',
      endpoint,
      usedBySteps: 0,
      color: /^#[0-9a-fA-F]{6}$/.test(str(raw.color)) ? str(raw.color) : '#5B6773',
      abbr: str(raw.abbr) || abbreviate(name),
    })
  }
  return { items, skipped }
}
