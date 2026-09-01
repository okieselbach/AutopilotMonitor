---
type: Concept
title: Ops-Events Query Surface
description: The server-side filter surface of GET /api/global/ops-events (category, eventType, severity/minSeverity, tenant drill, date window), why every filter is folded into the continuation fingerprint, why minSeverity expands to an OR-set, and why the paged cross-category read has to fan out over the complete category vocabulary.
resource: /src/Backend/AutopilotMonitor.Functions/Functions/Admin/GetOpsEventsFunction.cs
tags:
  - backend
  - table-storage
  - ops-events
  - pagination
  - mcp
timestamp: 2026-09-01T00:00:00+02:00
---

# Problem

`OpsEvents` is the platform operator's log: consent flows, maintenance runs, security blocks,
tenant offboards, agent emergency breaks, SLA and Azure Monitor alerts. It is partitioned by
**category** (`PartitionKey`), with a reverse-tick `RowKey` for newest-first ordering.

Until 2026-09-01 the read endpoint could only narrow by category and date window. Every other
question — *"did any `AgentEmergencyBreak` fire yesterday?"*, *"show me everything at Error or
worse"* — had to be answered by pulling the whole category and filtering it on the client. For the
MCP tool that client is a language model, so an event-type question cost a category-sized response
in model context.

# Schema

`GET /api/global/ops-events` (GA + Global Reader; `TenantScoping.None`, so a delegated admin cannot
reach it at all):

| Param | Where it is applied | Notes |
| --- | --- | --- |
| `category` | `PartitionKey eq` — indexed | One partition; omit to fan out over all of them |
| `eventType` | `EventType eq` — server-side scan | **Exact and case-sensitive**; Table Storage cannot fold case |
| `severity` | `Severity eq` — server-side scan | Normalised onto the canonical vocabulary before use |
| `minSeverity` | OR-set over `Severity` — server-side scan | Threshold: that level and everything above |
| `dateFrom` / `dateTo` | RowKey range — indexed | Both absent ⇒ backend resolves the last 30 days |
| `tenantId` | **in-memory**, post-fetch | Drill, not an isolation boundary — see below |
| `pageSize` / `continuation` | — | `pageSize` opts into pagination; absent ⇒ full window, no cap |

The filter surface lives in two shared pieces so no path can honour a different one:
`OpsEventFilterRequest` (query → `OpsEventQueryFilters` + pagination extras) and
`TableOpsEventRepository.AppendFieldFilters` (filters → OData clauses), which **both** filter
builders call — the single-category/unpaged builder and the per-partition fan-out builder that adds
the `RowKey gt` bound. This mirrors `AuditLogFilterRequest` / `AuditLogQueryFilters` on the audit
endpoints one-for-one.

## Why `minSeverity` is an OR-set, not a range

Table Storage compares strings lexicographically, and the severity names sort
`Critical < Error < Info < Warning` — nothing to do with severity order. A `Severity ge 'Warning'`
clause would return `Warning` only, silently dropping `Error` and `Critical`, which is precisely the
alert-worthy half. `minSeverity` therefore expands to
`(Severity eq 'Warning' or Severity eq 'Error' or Severity eq 'Critical')`. The ladder itself lives
in `OpsEventSeverity.Rank` and is shared with `OpsAlertDispatchService`, so *"Warning and above"* in
a read means exactly what *"Warning and above"* meant when the alert rules decided to fire.

`minSeverity=Info` emits no clause at all — it is the floor, so the clause would only cost query
length.

## Why severity is normalised but eventType is not

Both columns are matched with `eq`, which is case-sensitive. Severity has a closed vocabulary of
four values, so `?severity=warning` is normalised to `Warning`, and anything outside the vocabulary
is a **400** — an unnormalised value would return an empty page that reads exactly like *"no such
events happened"*.

Event types have a declared vocabulary too (`OpsEventTypes`, reflected into the shared manifest and
served to the model as `get_resource(name="ops_event_types")`), but the filter deliberately does
**not** validate against it: stored rows outlive the vocabulary, so a type that was retired after a
refactor must stay searchable in the historical window. The value is passed verbatim and a typo
legitimately returns zero rows — the catalog is there so nobody has to guess.

## Continuation fingerprint

Every filter value is appended to the pagination *extras*, which are both folded into the
continuation-token fingerprint and echoed on `nextLink`. Consequences:

* a token minted for one filter cannot page a different one (it is rejected with a 400),
* the follow-up request is self-contained — the resolved date window and the filters travel on the
  link, so no page re-resolves "now",
* extras are appended **after** the pre-existing `category` / `tenantId` discriminators and skipped
  when empty, so tokens minted before the field filters existed still fingerprint identically.

Because severity values are normalised *before* fingerprinting, `?severity=error` and
`?severity=Error` share one token instead of minting two that each reject the other.

## The tenant drill stays in memory

`?tenantId=` is filtered post-fetch (`GetOpsEventsFunction.FilterByTenant`) because OpsEvents is
partitioned by category, and — deliberately — because the in-memory comparison is
`OrdinalIgnoreCase` while an OData `eq` would be case-sensitive: stored tenant casing is not
guaranteed, so pushing this one down would lose rows. It is drill *correctness*, not isolation:
every caller that reaches this route already has full cross-tenant scope. Pages therefore can report
fewer items than `pageSize` when the drill is narrow.

## The category vocabulary must be complete

The paged cross-category read does not issue one cross-partition query (Azure would page it by
`PartitionKey asc`, so the first page would come entirely from the alphabetically first category).
It fans out per partition over a fixed list and merge-sorts. That list is therefore load-bearing: a
category that is written but not listed is **invisible to every paged reader** while the unpaged
full-table path still returns it. `Platform` (Azure Monitor alerts relayed through the ops alert
webhook) was missing that way from its introduction until 2026-09-01.

The list is now owned by `OpsEventCategory.All`, and `OpsEventCategoryCoverageTests` reflects over
the constants to fail the build when a new category is not added to it.

# Examples

```
# One event type, last 24h, cross-category
GET /api/global/ops-events?eventType=AgentEmergencyBreak&dateFrom=2026-08-31T12:00:00Z&pageSize=200

# Everything alert-worthy in the maintenance partition this month
GET /api/global/ops-events?category=Maintenance&minSeverity=Error&dateFrom=2026-08-01T00:00:00Z
```

MCP `get_ops_events` exposes the same surface plus a `days` shorthand, which it resolves to a
concrete `dateFrom` **client-side** — a backend-side "last N days" would re-resolve "now" on every
page and blow the token fingerprint. An explicit `dateFrom`/`dateTo` always wins over the shorthand.

# Citations

* `/src/Backend/AutopilotMonitor.Functions/Functions/Admin/GetOpsEventsFunction.cs`
* `/src/Backend/AutopilotMonitor.Functions/Functions/Admin/OpsEventFilterRequest.cs`
* `/src/Backend/AutopilotMonitor.Functions/DataAccess/TableStorage/TableOpsEventRepository.cs`
* `/src/Shared/AutopilotMonitor.Shared/DataAccess/IOpsEventRepository.cs`
* `/src/Shared/AutopilotMonitor.Shared/DataAccess/OpsEventTypes.cs` — the declared event-type vocabulary
* `/src/Backend/AutopilotMonitor.Functions.Tests/OpsEventFieldFilterTests.cs`
* `/src/Backend/AutopilotMonitor.Functions.Tests/OpsEventCategoryCoverageTests.cs`
* `/src/McpServer/autopilot-monitor-mcp/src/tools/admin.ts`
* [Business Timestamps](business-timestamps.md) — why the date window filters on the RowKey
