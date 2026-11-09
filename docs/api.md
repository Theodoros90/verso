# Verso API

Endpoints that exist today. The planned contracts are in [plan §5](plan/2026-11-09-verso-mvp-plan.md#5-api-contracts). When an endpoint is added or changed, update this file in the same change.

In Development the generated OpenAPI document is served at `/openapi/v1.json`.

## `GET /api/health`

Anonymous. Reports whether the API is running and whether its dependencies are usable. Used by the SPA status indicator and, later, IIS / load-balancer probes.

| Status code | Meaning |
|---|---|
| `200 OK` | `status` is `Healthy` or `Degraded` (e.g. migrations pending) |
| `503 Service Unavailable` | `status` is `Unhealthy` (e.g. database unreachable) |

Body (same shape for 200 and 503):

```json
{
  "status": "Healthy",
  "totalDurationMs": 14.2,
  "checks": [
    {
      "name": "database",
      "status": "Healthy",
      "description": "Connected; schema is up to date.",
      "durationMs": 13.8,
      "data": { "appliedMigrations": 1, "pendingMigrations": 0 }
    }
  ]
}
```

| Check | Healthy | Degraded | Unhealthy |
|---|---|---|---|
| `database` | Connected, no pending migrations | Connected, `pendingMigrations` > 0 | Cannot connect, database missing, or check timed out (5 s) |

The response never includes connection strings, server names or exception details. Failures are logged server-side.

Source: [src/Verso.Api/Health/](../src/Verso.Api/Health/). Frontend client: [src/verso-web/src/lib/api/health.ts](../src/verso-web/src/lib/api/health.ts).
