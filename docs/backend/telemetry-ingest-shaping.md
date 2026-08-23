---
type: Concept
title: Telemetry Ingest Shaping (App Insights / Log Analytics)
description: What the backend deliberately keeps out of Application Insights and why - one canonical request record (worker copy, per-item sampling bypass; host HTTP duplicate dropped by a workspace transformation), expected storage outcomes (404/412/409) and successful SignalR REST calls filtered in the worker, and the query rules that follow.
resource: /src/Backend/AutopilotMonitor.Functions/Telemetry/StorageDependencyFilterProcessor.cs
tags:
  - backend
  - telemetry
  - application-insights
  - cost
timestamp: 2026-08-23
---

# Telemetry Ingest Shaping

The backend is storage-I/O heavy and runs on the Functions isolated worker, which emits telemetry from TWO processes (host and worker). Left alone, Log Analytics ingestion is dominated by rows nobody reads. This document records what is removed, where, and what that means for queries.

# Schema

## Request telemetry — one canonical record

| Source | Emitted by | Content | Fate |
|---|---|---|---|
| Host HTTP request (`Properties.Source` empty, `Url` set) | Functions host process | Name, duration, status — no tenant/user/correlation | **Dropped at ingestion** by the workspace transformation on `AppRequests` (`infra/appinsights-workspace-transforms.bicep`) |
| Host non-HTTP invocation (`Url` empty) | Functions host process | Timer / queue executions | Kept (the worker middleware never sees them) |
| Worker request (`Properties.Source == 'WorkerMiddleware'`) | `RequestTelemetryMiddleware` | TenantId, UserId, UserRole, CorrelationId, ClientSource, McpToolName, HttpPath | **Kept, unsampled** — the canonical record |

Why two mechanisms:

* The host request item cannot be suppressed from worker code or from `host.json` (`Host.Results` controls log traces, not the request item; verified 2026-06-09). The only lever is a Log Analytics workspace transformation: `source | where tostring(Properties.Source) == 'WorkerMiddleware' or isempty(Url)`.
* `host.json` `samplingSettings.excludedTypes: "Request;Event"` only applies to the host process. The worker's `AddApplicationInsightsTelemetryWorkerService` runs its own adaptive sampling; before 2026-08-23 worker request rows carried `ItemCount` 2–5 on weekdays (≈30 % of individual requests missing, `count()` undercounting by the same share — `sum(ItemCount)` matched the host copy to 1.00 per day). Re-configuring the worker sampling chain did not take effect in the isolated worker (see the L4 note in `Program.cs`). The per-item opt-out does: the middleware sets `SamplingPercentage = 100` on its own `RequestTelemetry`, and the SDK sampling processor passes through every item whose sampling percentage is already set.

## Dependency telemetry — keep only signal

`StorageDependencyFilterProcessor` (worker pipeline) drops:

* successful Azure Storage calls (Table/Queue/Blob) in both shapes — the HTTP shape (`Azure table`, target `*.table.core.windows.net`) and the Azure SDK ActivitySource shape (`InProc | Microsoft.Tables`, `InProc | Microsoft.Storage`);
* storage calls with an **expected outcome**: `404` (point-read miss — EventTypeIndex, BlockedDevices lookups), `412` (ETag precondition), `409` (idempotent insert conflict). HTTP-shape rows carry the code in `ResultCode`; InProc rows only in `Properties["Error"]` (`Status: 404 (`). Before this filter these were 100 % of the remaining "failed" storage rows (~540 MB/week, two rows per call);
* successful Azure SignalR Service REST calls (`*.service.signalr.net` — group add/remove per connection, ~470 MB/week).

Everything else passes: storage 429/5xx/auth/timeouts, failed SignalR calls, all HTTP/Graph/NVD/MSRC dependencies, the worker's own `InProc Invoke` span, and every non-dependency item.

## Measured baseline (7 days before the change, `_BilledSize`, `autopilotmonitor-api-eu`)

| Table | Rows | MB | After |
|---|---|---|---|
| AppDependencies | 976k | 1248 | ≈ 240 (SignalR + expected-outcome rows gone) |
| AppRequests host copy | 274k | 323 | ≈ 0 (only timer/queue rows) |
| AppRequests worker copy | 189k | 208 | ≈ 300 (now 100 % of requests) |
| AppTraces | 142k | 137 | unchanged |
| AppEvents | 6k | 4 | unchanged — not worth touching |

Net ≈ −1.3 GB/week with MORE usable data (every request now has tenant context).

# Examples

Counting requests (exact since 2026-08-23; for older data replace `count()` with `sum(itemCount)`):

```kql
requests
| where customDimensions.Source == 'WorkerMiddleware'
| summarize total=count(), failed=countif(success == false) by name
```

Real storage trouble only:

```kql
dependencies
| where type startswith 'InProc | Microsoft.Tables' or target endswith '.table.core.windows.net'
| summarize count() by resultCode, tostring(customDimensions.Error)
```

Post-deploy verification of the per-item sampling bypass (must be `ItemCount == 1` only):

```kql
AppRequests
| where TimeGenerated > ago(1h) and tostring(Properties.Source) == 'WorkerMiddleware'
| summarize rows=count() by ItemCount
```

# Citations

* `src/Backend/AutopilotMonitor.Functions/Middleware/RequestTelemetryMiddleware.cs` — `SamplingPercentage = 100` before `TrackRequest`.
* `src/Backend/AutopilotMonitor.Functions/Telemetry/StorageDependencyFilterProcessor.cs` + `StorageDependencyFilterProcessorTests.cs` — the filter contract.
* `src/Backend/AutopilotMonitor.Functions/Program.cs` — the failed L4 worker-sampling attempt and why it must not be retried.
* `infra/appinsights-workspace-transforms.bicep` — the AppRequests transformation DCR, apply/verify/rollback commands.
* `.claude/commands/backend-logs.md`, `session-debug.md` — query conventions.
