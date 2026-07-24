---
type: Concept
title: ServerActions & On-Demand Diagnostics Collection
description: The queue-on-session, deliver-on-ingest ServerAction channel (terminate_session / rotate_config / request_diagnostics), the operator-tier authorization split, the portal's Collect Logs flow including the admin quick-config path, and why the offboarding cascade sweeps the hosted diagnostics prefix.
resource: /src/Backend/AutopilotMonitor.Functions/Functions/Sessions/QueueSessionActionFunction.cs
tags:
  - backend
  - agent
  - web
  - server-actions
  - diagnostics
  - authorization
timestamp: 2026-07-24T00:00:00+02:00
---

# Schema

**Channel.** A `ServerAction` (`Type`, `Reason`, `RuleId`, `Params`, `QueuedAt`) is queued onto
`Sessions.PendingActionsJson` by server-side writers — the admin endpoint
`POST /api/sessions/{sessionId}/actions`, the RuleEngine, maintenance — and delivered
piggy-backed on the agent's next ingest response. No polling channel exists; delivery latency
equals the agent's telemetry cadence (seconds to ~1 minute during enrollment). Delivery is
at-least-once: the agent's `ServerActionDispatcher` dedups on `Type|RuleId|QueuedAt` and
processes a batch sequentially in queue order. Unknown types are logged and skipped, so new
types roll out server-first. Every action emits `server_action_received` and then
`server_action_executed` or `server_action_failed` into the session timeline.

**Authorization (two layers).** The route sits on `EndpointPolicy.TenantAdminOrOperator`
(tenant Admin, tenant Operator, or Global Admin — no Viewer, no Global Reader: it is a write
tier). Because the route serves multiple action kinds, `QueueSessionActionFunction`
re-gates per type via `IsTypeAllowedForCaller`: Operators may queue only
`request_diagnostics`; `terminate_session` and `rotate_config` require
`RequestContext.IsTenantAdmin || IsGlobalAdmin` (403 otherwise). Any route on this tier with
a multi-kind body MUST follow this same in-function re-gating pattern.

**On-demand collection (`request_diagnostics`).** The dispatcher calls
`DiagnosticsPackageService.CreateAndUploadAsync(enrollmentSucceeded: false, suffix:
"server-requested")` — producing `AgentDiagnostics-{sessionId}-{timestamp}-server-requested.zip`.
Passing `enrollmentSucceeded: false` is what makes on-demand collection work under
`DiagnosticsUploadMode=OnFailure`; only `Off` (or an unconfigured destination) blocks it,
surfacing as `server_action_failed`. The `diagnostics_uploaded` event stamps
`DiagnosticsBlobName` + `Destination` onto the Sessions row (latest wins — an on-demand name
is later replaced by the terminal package), and the SignalR `sessionUpdate` delta carries the
field, so the portal's Download button flips live.

**Portal flow (Collect Logs, session detail header).** The button is always rendered; the
disabled states carry tooltips (inactive session, cross-tenant view, missing role, diagnostics
not configured) as a deliberate nudge. Members below Admin cannot read the tenant config, so
the member-readable feature-flags endpoint exposes `diagnosticsUploadConfigured`
(`mode != Off && (customer SAS present || destination Hosted)`). Progress tracking is
sequence-based (backend-assigned, monotonic), never timestamp-based — agent clock skew and
at-least-once redelivery of older actions cannot produce false matches
(`collectLogsLogic.ts`). Cross-tenant views are gated off because `TenantHelper.GetTenantId`
resolves the tenant strictly from the caller's JWT.

**Admin quick-config.** For an unconfigured tenant, a Tenant Admin's click opens a dialog
that switches the tenant to `Destination=Hosted`, `Mode=OnFailure` (GET full config → patch
exactly those two fields → PUT verbatim), then queues `rotate_config` **before**
`request_diagnostics` — the dispatcher's sequential ordering guarantees the agent refetches
the now-enabled config (the `RemoteConfigMerger` applies diagnostics fields live, remote wins)
before building the package. This works against the already-deployed agent fleet; a
"one-time override" variant would not, because the `mode=Off` check is client-side in
`CreateAndUploadAsync` and the deployed handler reads no params.

**Offboarding cascade.** Latest-wins on `DiagnosticsBlobName` means a session can leave more
hosted blobs behind than the deletion manifest references. The cascade's Hosted branch
therefore sweeps the canonical prefix `{tenantId}/AgentDiagnostics-{sessionId}-`
(`HostedDiagnosticsBlobService.DeleteBySessionPrefixAsync`) plus the manifest-named blob as
belt-and-braces; a non-GUID legacy `SessionId` degrades to the single-name delete instead of
poisoning the cascade. The CustomerSas branch is unchanged (delete only with the `d`
permission; the customer's lifecycle rules stay authoritative).

# Examples

Queue an on-demand collection (Admin or Operator, own tenant):

```
POST /api/sessions/{sessionId}/actions
{ "type": "request_diagnostics", "reason": "On-demand log collection from portal" }
→ 202 { "success": true, "queuedAt": "..." }
```

Operator queueing an admin-only type:

```
POST /api/sessions/{sessionId}/actions
{ "type": "terminate_session" }
→ 403 { "message": "Action type 'terminate_session' requires the Tenant Admin role" }
```

Timeline for a successful round trip: `server_action_received` → `server_action_executed`
(with `blobName`) → `diagnostics_uploaded` → Sessions row + SignalR delta carry the new
`DiagnosticsBlobName`.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Functions/Sessions/QueueSessionActionFunction.cs` — allowed types, per-type caller gate.
* `src/Backend/AutopilotMonitor.Functions/Security/EndpointAccessPolicyCatalog.cs` — `TenantAdminOrOperator` tier definition.
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Runtime/ServerActionDispatcher.cs` — dedup, sequential dispatch, telemetry contract.
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Runtime/DiagnosticsPackageService.cs` — mode gates, archive build, suffix.
* `src/Backend/AutopilotMonitor.Functions/Services/Diagnostics/DiagnosticsBlobCascadeDeleter.cs` — hosted prefix sweep.
* `src/Web/autopilot-monitor-web/app/sessions/[sessionId]/components/collectLogsLogic.ts` — button gating matrix, sequence-based progress evaluation.
