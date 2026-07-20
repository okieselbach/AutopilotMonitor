---
type: Concept
title: Business Timestamps vs. the Azure Tables System Timestamp
description: Why the system Timestamp is never authoritative, how the 2026-07-18 migration exposed that, and the OccurredUtc + RowKey-decode compensation (mappers, RowKey-range date filters, retention, backfill).
resource: /src/Backend/AutopilotMonitor.Functions/Helpers/BusinessTimestamp.cs
tags:
  - backend
  - table-storage
  - audit-log
  - ops-events
  - events
  - migration
timestamp: 2026-07-20T00:00:00+02:00
---

# Problem

Azure Table Storage's `Timestamp` is a **system property**: the server ignores any supplied
value on write and stamps the row's *write* time; every row rewrite (storage-account
migration, MERGE) resets it. Three tables nevertheless treated it as the business event
time — writes like `["Timestamp"] = evt.Timestamp` were silent no-ops, unnoticed because
write time ≈ event time in normal operation.

The 2026-07-18 EU storage migration rewrote every row, so all migrated rows in
**AuditLogs**, **OpsEvents**, and **Events** read as "2026-07-18 ~13:58 UTC": displays were
wrong, date-window filters matched the wrong rows, and retention froze (nothing older than
the migration moment) with a mass-delete cliff one retention period later.

# Rule

**The system Timestamp is never authoritative.** Every table that needs an event time
stores it in the custom column `OccurredUtc` (`BusinessTimestamp.OccurredUtcColumn`).
Custom columns are copied verbatim by `scripts/Migration/Copy-TableStorage.ps1`, so future
migrations preserve chronology. Migration checklist: any table whose reads/filters depend
on a timestamp must carry it as a custom column — audit this before every storage move.

# Recovery sources per table

The original time survives migrations inside the RowKey:

| Table | RowKey scheme | Decodable |
|---|---|---|
| AuditLogs | `!{MaxTicks-ticks:D19}_{guid:N}` (since 2026-05-06) | Yes; legacy bare-GUID rows (pre-2026-05) are lost |
| OpsEvents | `{MaxTicks-ticks:D19}` | Yes, all rows |
| Events | `{ts:yyyyMMddHHmmssfff}_{seq:D10}` (sanitized agent time) | Yes, all rows (ms precision) |

# Mechanism

`BusinessTimestamp` (Functions/Helpers) is the single source of truth: RowKey decoders,
the OData clause builders, and `GetUtcDateTime` (datetime columns materialize as
`DateTimeOffset` *or* `DateTime` depending on payload shape — always read via this helper).

1. **Read mappers** resolve `OccurredUtc` → RowKey decode → system Timestamp (last
   resort; only audit legacy GUID rows end there). Resolvers:
   `TableStorageService.ResolveAuditTimestamp`, `TableOpsEventRepository.ResolveTimestamp`,
   `TableStorageService.ResolveEventTimestamp` (also used by
   `GetEarliestSessionEventTimestampAsync`, which feeds StartedAt/Duration reconciliation —
   a system-Timestamp read there would rewrite migrated sessions' derived fields).
2. **Date-window filters and retention use RowKey ranges**, not property comparisons:
   OData excludes rows missing a compared property (an `OccurredUtc` filter would drop
   every not-yet-backfilled row), and RowKey ranges are index-backed. Boundary semantics
   are tick-exact equivalents of the old `Timestamp ge/le` clauses.
   **Trap:** audit legacy GUID RowKeys (hex first char) sort *after* all `!`-rows, so
   every lower bound `RowKey ge '!…'` must be paired with `RowKey lt '"'`
   (`AuditTimeEncodedUpperBound`) — without the guard, a retention sweep would delete all
   legacy rows. Pinned by `BusinessTimestampFilterClauseTests`.
3. **Writes** in all three tables now set `OccurredUtc`; the no-op `["Timestamp"]`
   entries were removed.
4. **Backfill** (`POST /api/maintenance/backfill-occurred-utc`, GlobalAdminOnly, dry-run
   by default, `table=audit|ops`, batched via `maxRows`/`continuation`): MERGE that adds
   only `OccurredUtc` decoded from the RowKey — deterministic and idempotent. **Events
   rows are deliberately never backfilled** (read-path decode is sufficient; no bulk
   mutation of enrollment evidence). Note: a MERGE resets the row's system Timestamp —
   irrelevant once nothing depends on it.
5. **Audit retention** runs two passes until ~2027-01: RowKey-range for `!`-rows plus a
   `RowKey ge '0' and Timestamp lt …` pass that ages legacy GUID rows out via their frozen
   migration-date system Timestamp (remove after 2027-03-01).

# Consequences

* Audit legacy GUID rows have "unknown date" semantics: excluded from date-filtered
  views, shown with the (wrong but honest) 2026-07-18 fallback in unfiltered views.
* Raw endpoints (`/api/raw/*`, `query_table`) intentionally keep showing the raw system
  Timestamp — they are storage-state inspectors, not business views.
* Event timestamps for pre-cutover rows resolve to the RowKey's sanitized agent time
  (ms precision); `ReceivedAt` (server ingest time, rows since 2026-03-19) and
  `OriginalTimestamp` (pre-clamp agent time, clamped rows only) remain separate fields.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Helpers/BusinessTimestamp.cs` — decoders + clause builders
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.Maintenance.cs` — audit write/mapper/filters/retention
* `src/Backend/AutopilotMonitor.Functions/DataAccess/TableStorage/TableOpsEventRepository.cs` — ops write/mapper/filters/retention
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.Sessions.cs` — event writes, `ResolveEventTimestamp`, earliest-event read
* `src/Backend/AutopilotMonitor.Functions/Services/OccurredUtcBackfillService.cs` + `Functions/Admin/BackfillOccurredUtcFunction.cs` — backfill
