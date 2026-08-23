---
type: Concept
title: Table Schema Sentinel (startup table initialization)
description: Why a cold start no longer issues one CreateTableIfNotExists per registered table - a hash over TableNames.All stored in AdminConfiguration gates the full pass, the SessionsIndex backfill only runs after a full pass and behind a conditional-insert claim, and daily maintenance repairs out-of-band deletions.
resource: /src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.cs
tags:
  - backend
  - table-storage
  - startup
  - maintenance
timestamp: 2026-08-23
---

# Schema

## Problem

`TableInitializerService` (hosted service) called `InitializeTablesAsync` on every process start, which issued `CreateTableIfNotExistsAsync` for every entry of `Constants.TableNames.All` (66 at the time of writing, DoP 8). Each call is a Create POST whose 409 is swallowed - on Flex Consumption with frequent cold starts that was ~9 serial round-trip waves before the first request. It was followed by a SessionsIndex emptiness query whose *error* path returned "empty", so a transient storage error triggered a full Sessions scan with serial upserts - on every scaled-out instance at once.

## Sentinel

* Row `AdminConfiguration` / PK `SchemaSentinel` / RK `tables`, property `TableSchemaHash` (+ `TableCount`, `UpdatedUtc`).
* Value = `ComputeTableSchemaHash(TableNames.All)`: SHA-256 over the ordinal-sorted, newline-joined table names. Derived, never hand-maintained: adding a table to `TableNames.All` changes the hash by construction (`TableLifecycleBucketTests` already forces every constant into `All`).
* `InitializeTablesAsync` (returns `bool fullPassRan`):
  1. Point-read the sentinel. Match -> mark initialized, **zero** table calls, return `false`.
  2. Missing / stale / unreadable (fresh storage has no AdminConfiguration table yet) -> `EnsureAllTablesAsync` (the old full pass), then upsert the sentinel **only if no table failed**, return `true`. A failed pass leaves `_tablesInitialized = false`, so the next call retries.
* Scale-out: two instances that both see a stale sentinel both run the idempotent pass - harmless.

## SessionsIndex backfill

* Only evaluated when the full pass ran (`fullPassRan == true`): an empty index can only exist on fresh storage, which is exactly the case without a sentinel.
* `IsSessionIndexEmptyAsync` now returns `false` on error (skip), not `true`.
* `TryClaimSessionIndexBackfillAsync`: conditional `AddEntityAsync` on `SchemaSentinel` / `sessionIndexBackfill` - only the winner scans; 409 or any error -> skip. The manual maintenance backfill (`RunManualAsync`) remains the safety net.

## Known gap and repair

A table deleted out-of-band (cleanup skill, portal, tests) is not noticed at startup anymore. `IStorageInitializer.EnsureAllAsync` runs as the first step of the daily timer maintenance (`MaintenanceService.RunAllAsync`), recreating it within 24h. Storage helpers are fail-soft in the meantime.

# Examples

* Deploy that adds a table: first cold start after the deploy runs the full pass once (log: "Table schema sentinel missing or stale"), every later start logs "Table schema sentinel matches (...)".
* Fresh storage account: sentinel read throws TableNotFound -> full pass -> index empty -> one instance claims and backfills.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.cs` - sentinel, `InitializeTablesAsync`, `EnsureAllTablesAsync`, `TryClaimSessionIndexBackfillAsync`
* `src/Backend/AutopilotMonitor.Functions/Services/TableInitializerService.cs` - startup gating
* `src/Backend/AutopilotMonitor.Functions/Services/MaintenanceService.cs` - daily `EnsureAllTablesAsync`
* `src/Backend/AutopilotMonitor.Functions.Tests/TableSchemaSentinelTests.cs`
