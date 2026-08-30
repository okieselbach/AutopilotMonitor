---
type: concept
title: Lifecycle Manifests & Session Scope Resolution
description: The enforced completeness nets added by the fragility-audit P5/P6 remediation — table lifecycle buckets, the SessionsIndex field manifest, the single session-scope resolver with its SessionTenantLookup point-read, and the server-side dashboard free-text search.
resource: backend
tags: [lifecycle, offboarding, sessionsindex, manifest, scope-resolution, search, performance]
timestamp: 2026-08-13
---

# Schema

Four mechanisms replace hand-synchronized copies with a declared source plus tests that fail on drift.

## 1. Table lifecycle buckets

Every table in `Constants.TableNames` must belong to at least one deletion lifecycle:

| Bucket | Mechanism | Source of truth |
|---|---|---|
| Tenant-offboarding wipe | `TenantOffboardingHandler` bucket arrays (`TenantPartitionTables`, `CompositePartitionTables`, `DiscriminatorTables`, `PropertyOnlyTables`, `RowKeyTables`, `ArchivedRuleTables`) + the one-off `TenantConfiguration` final delete | handler arrays (read via reflection by the test) |
| Per-session cascade | `DeletionManifestBuilder.BuildAsync` step list (incl. the Final tombstone → Sessions + SessionsIndex) | a real manifest built against an empty-inventory fake |
| Kept by design | reviewed exception list with per-table reasons | `TableLifecycleBucketTests.KeptByDesign` |

`TableLifecycleBucketTests` (Functions.Tests) asserts: constants set-equal `TableNames.All`, no table unclassified, kept-by-design disjoint from every wipe path, bucket entries are real constants, offboarding buckets disjoint except documented multi-membership (`BootstrapSessions`).

**Not covered mechanically:** whether a bucket's key shape matches the table's writer. That was the UsageMetrics bug (PK=date, RK=tenantId sat in the exact-PK bucket; the wipe matched 0 rows) — SafeWipe **Variant D** (`WipeByRowKeyAsync`, `RowKey eq '{tenantId}'`) now covers RowKey-anchored tables. Read the writer's `new TableEntity(pk, rk)` before classifying a new table.

## 2. SessionsIndex field manifest

`SessionIndexFieldManifest` (Functions/Services) declares the full-mirror column set: `AlwaysProjected` (32, written with defaults on every rebuild), `ConditionallyProjected` (18, written only when present), `PrimaryOnly` (4 — ServerActions + deletion-CAS fields that never touch the index). `SessionIndexFieldManifestTests` pins `BuildSessionIndexEntity` against it bidirectionally (full-row fixture built from a test-local sample table, NOT from the manifest — deriving the fixture from the manifest would make the check circular), pins that every manifest field influences `MapToSessionSummary`, and pins the rebuild's `TableUpdateMode.Replace` (a merge-mode upsert could never clear a conditional column the primary blanked). `MergeSessionIndexAsync` logs a warning for merged keys outside the manifest (fail-soft drift guard).

## 3. Session scope resolution (one implementation)

`RequestContextExtensions.ResolveSessionScopeAsync(ctx, sessionRepo, sessionId, requireGlobalAdmin)` is the single implementation of the former 15-site copy-paste fallback ("which tenant owns this session?"):

- Non-global callers always keep the middleware-validated `TargetTenantId`.
- Global-scope callers resolve via `ISessionRepository.ResolveSessionTenantIdAsync` — a point-read on **SessionTenantLookup** (PK=sessionId, RK="tenant") with a legacy cross-partition SessionsIndex scan fallback that self-heals the lookup row, so the scan is paid at most once per pre-table session.
- `requireGlobalAdmin: true` on WRITE paths gates the cross-tenant reach on `IsGlobalAdmin` so a read-only Global Reader can never steer a write into a foreign tenant.
- Unknown session → `TargetTenantId`, leaving each endpoint's own not-found semantics (404 / empty 200 / 400) in charge.
- Delegated (MSP) callers deliberately get NO fallback — extending it to `AllowedTenantIds` is a pending user decision.

The lookup row is **claimed at registration, first-writer-wins** (`ClaimSessionTenantLookupAsync`, create-only `AddEntity` BEFORE any Sessions/SessionsIndex write): on 409 the existing owner is read back — same tenant ⇒ normal re-registration, different tenant ⇒ `SessionTenantConflictException` ⇒ `409 Conflict` with `ErrorCode=session_owner_mismatch` (owning tenant not disclosed) plus a `SessionTenantConflict` ops event, and nothing is written. The claim is deliberately NOT fail-soft. Before this, the row was an unconditional last-writer-wins upsert after the session write, so any tenant's device (self-service onboarding) that knew a victim session GUID (portal deep links, Teams/Slack notifications, progress-portal URLs) could re-point the mapping at its own tenant and have every no-`tenantId` Global Admin/Reader read — and GA writes such as the `globaladmin` annotation lane — silently served from a forged session. Session ids are 122-bit random, so a genuine cross-tenant collision does not occur; a conflict is an attack or an agent bug and fails loudly. The legacy self-heal write is Add-only too (an existing claim is the authority), and `scripts/Migration/Backfill-SessionTenantLookup.ps1` closes the pre-table window once (idempotent, Add-only) so no legacy session id is claimable. The row is deleted by the cascade one step before the tombstone (no tombstone is kept — a freed id gives a foreign tenant nothing a fresh GUID would not) and is rebuildable from Sessions (not in the critical-backup set). The former lazy triggers (`events.Count == 0` → scan) are gone: empty-but-owned results no longer pay a table scan, and endpoints resolve upfront.

`requireGlobalAdmin: true` additionally corroborates the resolved tenant against its Sessions row (one point-read): a mapping whose session does not exist in the resolved tenant keeps `TargetTenantId`, so a stale or poisoned row can never steer a GA write — silently, without any operator confirmation step (a "confirm tenant GUID" dialog was rejected as unusable). Read paths keep the single point-read; their own session read is the corroboration (404).

Related enforcement added with the migration: `RecomputeTriggerGate` — `?reanalyze=true` and `?rescan=true` on the MemberRead session routes are actions (delete + rewrite stored results) and now 403 for Viewer / Global Reader / cross-tenant tenant roles (Global Admin or own-tenant Admin/Operator only).

## 4. Server-side dashboard search

`q=` on `/api/search/sessions` + `/api/global/search/sessions`: case-insensitive substring over exactly the fields the dashboard's client-side filter searches (device name, serial, manufacturer, model, status, sessionId, geo, agent version, OS fields — NOT the client-derived date/duration/blocked tokens; a server-only field would produce ghost results the client filters back out). Azure Tables has no substring operator, so the scan path backfills whole Azure pages until it has `pageSize` matches (max 10 pages per request, continuation at a page boundary — gap-free). `q` is bound into the pagination fingerprint. The web dashboard's `searchAll()` (useDashboardSessions) follows nextLink up to 10 requests / 50 matches and dedupe-merges results into the loaded list (`mergeSessionsById`), replacing the former `loadAll()` walk that downloaded the entire session history to search it client-side. Delegated callers in cross-tenant mode keep the legacy full sweep (the global search route has no delegated tier).

# Examples

Adding a new table: classify it in one lifecycle path before `TableLifecycleBucketTests` lets it ship — offboarding bucket (check the writer's PK/RK shape first), deletion-manifest step, or `KeptByDesign` with a reason.

Adding a mirrored SessionsIndex field: manifest entry + `BuildSessionIndexEntity` write + `MapToSessionSummary` read + a sample value in the test — any missing step is red.

# Citations

- `src/Backend/AutopilotMonitor.Functions.Tests/TableLifecycleBucketTests.cs`
- `src/Backend/AutopilotMonitor.Functions/Services/SessionIndexFieldManifest.cs`
- `src/Backend/AutopilotMonitor.Functions/Helpers/RequestContext.cs` (`ResolveSessionScopeAsync`)
- `src/Backend/AutopilotMonitor.Functions/Services/Offboarding/SafeWipeService.cs` (Variant D)
- `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.AgentApi.cs` (`MatchesFreeText`, backfill loop)
