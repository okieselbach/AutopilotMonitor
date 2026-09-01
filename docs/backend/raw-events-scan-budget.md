---
type: Concept
title: Raw-Events Scan Budget — budgeted, resumable pages instead of timeouts
description: Why the cross-session raw-events query (query_raw_events / /api/raw/events) could not finish inside any client timeout, and the redesign - index rows carry the tenant (no SessionsIndex scans; the same scan sat behind search_sessions_by_cve and the deviceProperties path of search_sessions and is gone there too), the date window is pushed into the partition query as a RowKey range and into the index as a write-time pre-filter, per-session fetches run bounded-parallel in chunks, and a server-side budget ends a page early with `partial: true` on a cursor that loses nothing. Every paginated MCP tool now applies an explicit pageSize to a continuation, and the auto-exhaust tools retry a timeout once with a halved page. Includes the audit of the remaining unbounded cross-tenant aggregations with their live durations.
resource: /src/Backend/AutopilotMonitor.Functions/Pagination/RawEventsScan.cs
tags:
  - backend
  - mcp
  - pagination
  - table-storage
  - events
  - performance
timestamp: 2026-09-01T00:00:00+02:00
---

# Problem

`GET /api/global/raw/events?eventType=X&startedAfter=T&pageSize=1000` (the `query_raw_events`
MCP tool) was built as *one index page, then everything*. Per page it did:

1. an `EventTypeIndex` scan without PartitionKey (`EventType eq`) until 1000 rows were collected;
2. `BatchGetSessionsAsync(tenantId: null, 1000 ids)` — for every session a **SessionsIndex
   full-table scan** (`SessionId eq`, not a key), only to learn the tenant that the index
   row already carried as a column;
3. 1000 **serial** per-session `Events` queries;
4. the `startedAfter` filter last, in memory — and compared against the Azure system
   `Timestamp` (row write time), not the event time.

No deadline existed anywhere. The MCP aborts a request after 30 s; the backend kept working
and the whole page was lost. The second attempt was byte-identical — the nextLink carries the
page-1 `pageSize`, and `followNextLink` sent it verbatim, so the caller's smaller `pageSize`
was silently ignored — and failed identically. Two expensive failures, no data.

# Design

Four independent levers; together the worst case moves from "minutes, then lost" to "at most
the budget, always resumable".

1. **No tenant resolution.** `EventTypeIndex` rows carry `TenantId` + `SessionId`
   (PartitionKey `{tenantId}_{eventType}`, the eventType is known, so the suffix strip is the
   fallback for pre-column rows — `IndexRowKeys.ResolveTenantId`). The walk yields
   `EventTypeIndexEntry(tenantId, sessionId)` pairs; the siblings get their
   `SessionSummary` rows by **point read** on `Sessions` (`BatchGetSessionsByKeyAsync`)
   with the tenant the index row carries: `search_sessions_by_event` (EventTypeIndex),
   `search_sessions_by_cve` (CveIndex, PartitionKey `{tenantId}_{cveId}`) and the
   `deviceProperties` path of `search_sessions` (DeviceSnapshot, PartitionKey = tenantId).
   The scan-based batch helper (`SessionId eq` over SessionsIndex, one full-table scan per
   session) no longer exists.
2. **Date window pushed down, exactly.** The `Events` RowKey is
   `{ts:yyyyMMddHHmmssfff}_{seq}` (sanitized agent time), so `startedAfter`/`startedBefore`
   become an index-backed RowKey range inside the session partition
   (`BusinessTimestamp.EventDateFromClause/ToClause`, millisecond-granular supersets; the
   tick-exact filter stays in memory). A session outside the window costs one empty
   round-trip instead of 200 rows of DataJson. On the **index** a write-time pre-filter
   (`Timestamp ge startedAfter − 25 h`) skips sessions whose last write of that event type
   predates the window: an event's sanitized time is at most `MaxFutureToleranceHours` (24 h)
   ahead of its receipt, the index row is rewritten by the same ingest call, one hour covers
   request-internal ordering. Sound for `startedAfter` only — the index write time is the
   *last* write, so it bounds nothing for `startedBefore`.
   **Not used:** the index RowKey (session `StartedAt`). It looks like the obvious prefilter
   but is unsound: a WhiteGlove Part-2 session produces fresh events weeks after its
   `StartedAt`.
3. **Chunked, bounded-parallel walk with a budget** (`RawEventsScan`). Index rows are fetched
   in chunks of 100; each chunk's sessions are fetched with 20 in flight; the deadline
   (15 s, under the MCP's 30 s abort) is checked *between* chunks. The cursor returned is the
   Azure continuation after the last chunk that was walked **and** fanned out completely.
4. **Honest page semantics on the wire.** `pageSize` still counts index rows. A page the
   budget ended while rows remained carries `partial: true`; a page that filled or drained
   does not. Nothing on a partial page is missing up to its `nextLink`.

The date window now compares the row's **business time** (`OccurredUtc` → RowKey prefix →
system Timestamp, `RawEventTime.Resolve`) and orders by it, on both the single-session and
the cross-session path — the raw endpoints still *show* the system `Timestamp` column
verbatim (they are storage inspectors), they just no longer *filter* on it. An unparsable
`startedAfter`/`startedBefore` is a 400, not a silently dropped filter; bare values are UTC.

# Invariants

* Every request returns within about the budget plus one chunk of overshoot.
* Every request makes forward progress: at least one chunk is processed, and the cursor
  advances past it.
* A partial page loses nothing: resuming from its `nextLink` continues exactly after the last
  fully processed chunk; chunk order survives the parallel fan-out, so a re-run over the same
  cursor yields the same sequence.
* `pageSize` is not part of the continuation fingerprint (the Azure cursor is a row
  position), so it may change between pages.

# MCP side

* `followNextLink(basePath, params, continuation, overrides)`: an explicit `pageSize` on a
  follow-up call overrides the value embedded in the nextLink (`withQueryOverrides`), in
  **every** nextLink-paginated tool (`get_audit_logs`, `get_ops_events`,
  `list_session_reports`, `list_session_annotations`, `list_tenants`, `query_raw_events`,
  `query_raw_sessions`, `query_table`, `search_sessions`, `search_sessions_by_event`,
  `get_session_events`, `search_sessions_by_cve`). Their `pageSize` schemas have no
  `.default()` any more: `pageSizeForCall` sends the tool's first-page default only on a
  first-page call, so an omitted value on a follow-up keeps the nextLink's size and a given
  one wins. `fields` (a projection, never fingerprinted) is overridable the same way on the
  raw tools. Filters stay verbatim on purpose — they are fingerprinted, and a changed filter
  must be a clean 400, not a silent re-scope.
* `scanWithTimeoutFallback` on the three auto-exhaust tools (`query_raw_events`,
  `search_sessions`, `get_session_events`): a timeout is retried **once** with a halved
  `pageSize` (floor 25) on the same cursor; the page is marked `retriedWithPageSize` +
  `retryNote`. Offset pagers (`get_api_usage`, `get_geographic_sessions`,
  `get_software_inventory`) re-send every parameter per page and need none of this.
* The timeout error text distinguishes a continuation call ("re-send the SAME continuation
  with an explicitly smaller pageSize") from a first-page call.
* `scanUntilMatch` (auto-exhaust past empty pages) is unchanged; its own 10-page / 15 s
  budget bounds how many budgeted server pages one tool call walks.

Deploy order: backend first (the `partial` field is additive), then the MCP.

# What remains: unbounded cross-tenant aggregations (audit 2026-09-01)

No metrics, apps, geographic, search or vulnerability endpoint carries a wall-clock deadline,
a row cap (except the CveIndex scan's 50 000-row `truncated` cap) or a cancellation token;
each answers one call with the whole window. Live request durations over 30 days (worker
telemetry, unsampled): `GetGlobalAppSessions` p50 5.5 s / p95 14 s, `GetGlobalAppMetrics`
p95 13 s, `GetGlobalAppsList` p95 12 s, `GetGlobalGeographicMetrics` p95 11 s,
`GetPlatformUsageMetrics` p95 9 s, `GetGlobalPlatformMetrics` max 50 s (one call over 20 s;
its MCP timeout is 90 s), `GetGlobalGeographicLocationSessions` p50 5 s. So today a single
call does return everything, in 5–15 s at p95, and the headroom to the 30 s client abort
shrinks linearly with data volume: past it, there is no partial result and no cursor to
resume from — the retry fails identically. The drains behind them: SessionsIndex over the
whole `days` window without PartitionKey (`GetMetricsSummaryAsync`), Sessions by
`StartedAt` range without PartitionKey (`QuerySessionsByDateRangeAsync`: geographic + SLA),
AppInstallSummaries without PartitionKey (apps, usage, geographic), one Sessions point read
per distinct session **before** paging (`AppsAnalyticsHelper.LoadSessionLookupAsync`),
UserUsage with a null filter (`mcp-usage` without tenantId), 2000 sessions × one event
query (platform / agent-efficiency, 5-minute cache). These need either real pagination
under a budget or pre-aggregation; that is a design per endpoint family, not a side fix.

# Deliberately not built

* An async job with a result store (202 + jobId + polling): queue, worker, retention and
  RBAC on the result for a query that the four levers make fast enough.
* A client-configurable timeout: it only moves the wall; the fix is the server-side budget
  with a resumable cursor.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Pagination/RawEventsScan.cs` — the budgeted walk (`RawEventsScanTests`)
* `src/Backend/AutopilotMonitor.Functions/Functions/Raw/QueryRawEventsFunction.cs` — cross-session path, `partial`
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.AgentApi.cs` — `GetEventTypeIndexPageAsync`, point-read batch
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.Sessions.cs` — `GetSessionEventsRawByTypeAsync` RowKey window
* `src/Backend/AutopilotMonitor.Functions/Helpers/BusinessTimestamp.cs`, `RawEventTime.cs`, `EventTypeIndexKeys.cs`
* `src/Backend/AutopilotMonitor.Functions/Pagination/QueryRawEventsPagination.cs` — `IndexWriteTimeSlack`, `TryParseUtc`
* `src/McpServer/autopilot-monitor-mcp/src/client.ts` — `withQueryOverrides`, `effectivePageSize`, `scanWithTimeoutFallback`
* `docs/backend/business-timestamps.md` — why the system Timestamp is never a filter key
