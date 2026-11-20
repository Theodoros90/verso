# Sample: exchange rate for an invoice (Fetch from API step)

An invoice arrives as JSON with its currency and the currency the customer wants to see. The workflow asks a public exchange-rate API for the rate and the generated PDF shows it. Synthetic data only; the invoice is invented. This sample is a recipe to build in the designer, there is no loader script for it. Uses the Fetch from API step ([ADR 0021](../adr/0021-fetch-api-step.md)) and an API connection ([ADR 0017](../adr/0017-api-connections.md)).

## The data

```json
{
  "invoice": { "number": "INV-2026-0042", "currency": "EUR", "targetCurrency": "USD", "total": 1250.00 }
}
```

## The connection (once, by an administrator)

1. Configuration (the gear), Connections, **New connection**, authentication **HTTP API**.
2. Base address `https://api.frankfurter.dev`, authentication **None**, test path `/v1/latest?base=EUR&symbols=USD`.
3. Press **Test connection** first (the Worker must be running). It shows the HTTP status, the time and the content type.
4. In production set `ApiConnections:AllowedHosts` in the Worker's configuration to include `api.frankfurter.dev` ([setup.md, section 9](../setup.md)); the Worker server must be able to reach it over https.

## The step

```
Start ─ Validate ─ Fetch from API ── found ─ Generate PDF ─ Archive
                                  └─ not-found ─ (the run ends there: no rate, no document)
```

| Setting | Value |
|---|---|
| Connection | the Frankfurter connection |
| Path | `/v1/latest` |
| Query values | `base = invoice.currency` and `symbols = invoice.targetCurrency` (one per line) |
| Result name | `rates` |
| Largest answer | 64 KB (default) |
| Time limit | 15 seconds (default) |

With the data above the Worker sends `GET https://api.frankfurter.dev/v1/latest?base=EUR&symbols=USD`. The values are URL-encoded; the host always comes from the connection and cannot be changed by the data. A path can also hold data, for example `/customers/{{customer.number}}/rate`: each piece is URL-encoded as one piece.

## The merged shape and the template

The API answers something like (illustrative values):

```json
{ "amount": 1.0, "base": "EUR", "date": "2026-11-19", "rates": { "USD": 1.08 } }
```

The whole answer is merged at the top level of the run's data **under the result name**, so the data now holds `invoice` and `rates`, and the API's own `rates` field sits one level inside:

| Data path | Value |
|---|---|
| `rates.base` | `EUR` |
| `rates.date` | `2026-11-19` |
| `rates.rates.USD` | `1.08` |

In the template: `Rate on {{rates.date}}: 1 {{rates.base}} = {{rates.rates.USD}} {{invoice.targetCurrency}}`. Choose another result name (such as `fx`, giving `fx.rates.USD`) if `rates.rates` reads badly. A top-level field that is already called like the result name is replaced.

## What happens in other cases

- **`invoice.currency` or `invoice.targetCurrency` missing or null** (or a list, a group of fields): the data's fault. The step takes its **`failed`** port, **nothing is sent**, and the run ends **Rejected** with `data.fetch_invalid` unless you connect that port. The step's issues name the data path, never the value. Fix the input and submit it again.
- **HTTP 404, an empty answer or a JSON `null`:** the **`not-found`** port, the data is unchanged. Connect it, or the run ends there. What an API answers for an unknown currency is the API's choice: if it answers another 4xx (for example 422) the run **fails** with `api.client_error` instead.
- **Timeouts, an unreachable host, HTTP 5xx or 429:** retried automatically (three attempts); everything else (`api.unauthorized`, `api.not_json`, `api.response_too_large`, `api.host_not_allowed`, ...) fails the run at once. See [runbook, C2](../runbook.md#c2-failures).
- **After an approval wait or a restart** the step fetches again, so the rate may differ from the first pass. If that fetch fails the run fails with `fetch.restore`; retry it from the run view.

## Things to know

- The step makes **one GET** and keeps only the HTTP status, the time, the answer's size and the connection key in the run's history, never the path values or the answer. Values can still reach the history if you put them there yourself (for example in an Archive metadata pattern such as `{{rates.rates.USD}}`).
- The answer must be JSON (`application/json` or `+json`) and at most the limit you set (1 to 256 KB).
- **Sending data to an API** (a POST with a body, for example) needs the future **Send to API** step, which is not built (it needs its own ADR). The **Deliver via API** step is still simulated: it sends nothing.
- Nothing has been verified against a live third-party API in the automated tests; try this sample against the public service from a non-production machine first.
