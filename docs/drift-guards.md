---
type: Concept
title: Drift Guards — SignalR Names, RowKeys, Wire DTOs, Tool Vocabularies, Config Sections, Scoped Routing
description: The 2026-08 fragility-audit follow-up contracts that turn formerly hand-synchronized mirrors into enforced ones — SignalR message-name catalog, the inverted-tick RowKey codec, tombstone table tagging, typed wire DTOs with generated TypeScript interfaces, the MCP's generated tool vocabularies with their inline-enum ratchet, per-section config PATCH, and the web's scoped-endpoint builders.
resource: /src/Backend/AutopilotMonitor.Functions.Tests/TypedResponseGuardTests.cs
tags:
  - contracts
  - guard-tests
  - signalr
  - web
timestamp: 2026-08-31T00:00:00+02:00
---

# Drift Guards

Follow-up to the 2026-08 fragility audit (see [Lifecycle Manifests & Session Scope
Resolution](backend/lifecycle-manifests-and-session-scope.md) for the earlier rounds).
Each section below replaces a hand-synchronized mirror with an enforced contract: a
change on one side now fails a build, a type check, or a test on the other side.

# Schema

## SignalR message names — one catalog, typed on both ends

`Constants.SignalRMessages` (Shared) is the complete catalog of SignalR target names.
Backend senders (output bindings and the imperative `SignalRNotificationService`)
reference the constants; the hub name is `SignalRGroupHelper.HubName`. The catalog is
exported into `shared-manifests.json` (`signalRMessages` section) by
`SharedManifestParityTests`, and the web's `SignalRContext.on/off` type their event
name against the generated union (`lib/signalrMessages.ts`) — subscribing to a name
the backend never sends is a tsc error, not a silent no-op. `newevents` is the one
legacy lowercase name; it is a persisted wire name, do not "fix" the casing.

## Inverted-tick RowKeys — one codec

`RowKeyCodec` (Functions/Helpers) is the single encoder/decoder for
`rev(t) = MaxTicks - t.Ticks` RowKeys. Every writer routes through it; the persisted
per-table shapes (D19 standard, AuditLogs `!`-prefix, UserActivity's legacy D20 width,
ConfigurationBackups' 28-char truncation) are pinned by `RowKeyCodecTests` and must
never change — a width/prefix change reorders live tables against their existing rows.
`BusinessTimestamp` keeps the OData clause builders and delegates decode to the codec.

## Tombstone rows carry their table

`DeletionRowDump.Table` names the source table on FINAL (Tombstone) rows — the one
step that mixes two tables while `DeletionStep.Table` is null.
`DeletionTombstoneTables.Resolve` is the only consumer-side resolution (delete +
restore + dry-run); the historical `Contains('_')` RowKey-shape heuristic survives
solely as the fallback for manifests written before the field existed and must outlive
one full manifest-retention cycle.

## Wire DTOs — generated interfaces, zero anonymous success bodies

Every HTTP success body is a flat class in `AutopilotMonitor.Shared.Models`
implementing the `IApiResponse` marker (2026-08-31, `feat/typed-api-contract` —
migrated from the 44 `OkAsync(new { … })` + 134 raw `WriteAsJsonAsync(new { … })`
sites the 2026-08-13 audit froze). Three layers enforce it:

- **Compile time**: the only `OkAsync/CreatedAsync/JsonAsync` overloads constrain on
  `IApiResponse` — an anonymous object cannot satisfy the constraint. Error bodies
  (first key `error`/`message`, or literal `success = false`) stay anonymous by design.
- **`TypedResponseGuardTests`** (successor of `OkAsyncBaselineGuardTests`): both
  per-file baselines are EMPTY — any anonymous success body, through the helpers or
  raw `WriteAsJsonAsync`, is a straight failure. A reflection fact keeps every
  `IApiResponse` implementer flat (System.Text.Json serializes derived properties
  before base ones — key order is wire contract; MCP hands raw JSON to an LLM) and
  inside the Shared assembly.
- **`*WireParityTests`**: each migrated site carries an ordinal old-anonymous-literal
  vs. new-DTO serialization proof (production `ApiJsonOptions`), including a null case
  per key that WhenWritingNull omits.
- **Object-slot ratchet** (2026-08-31 typisierung follow-up): a reflection walk over
  the reachable wire-type graph fails on any `object` / collection-of-`object`
  property outside `ObjectSlotBaseline` (sole entry: `RuleDryRunCondition.Evidence`,
  heterogeneous by design). `[ProjectedItems]` slots and dictionary VALUES are exempt.
  The former deliberately-untyped slots (RuleDryRun trace, HealthCheck,
  GlobalNotificationDto, TenantConfigFieldSchema, maintenance run reports,
  MetricsSummary items, auth/me, auth/mcp) are concrete Shared types now. The raw
  table entities left the wire entirely in the same pass: global-admins,
  tenant-admins and preview-whitelist serve flat rows (`GlobalAdminRow` /
  `TenantAdminRow` / `PreviewWhitelistTenantEntry`) WITHOUT
  partitionKey/rowKey/eTag/timestamp — a deliberate wire change pinned by exact-JSON
  shape tests instead of parity facts.

The DTO rules: declaration order == wire order; a property is nullable exactly when a
site can emit null (the key then vanishes — never add non-null defaults that would
invent a key); preformatted strings (`.ToString("o")`, enum `.ToString()`) stay
`string`; `fields=` projections stay `IReadOnlyList<object>` tagged
`[ProjectedItems(typeof(Item))]`; raw table rows stay verbatim-keyed dictionaries
(no `DictionaryKeyPolicy` — dictionary KEYS never run through the camelCase policy,
and dictionary VALUES keep explicit nulls, both pinned by tests).

`SharedManifestParityTests` (schemaVersion 2) reflects the full graph — every
`IApiResponse` implementer plus `[WireContract]`-marked payload types, transitively
closed by `WireTypeManifestBuilder` — into the `types` section of
`shared-manifests.json`, carrying C# `<summary>` texts. The web codegen
(`npm run generate:manifests`) emits `utils/wire-types.generated.ts` (one interface
per object, one string union per enum, JSDoc from the summaries); the hand-written
`types/session.ts` / `types/enrollment.ts` / `types/adminConfig.ts` re-export from
it. Freshness is pinned by the vitest suite and the `shared-manifests-in-sync` CI
job. Unmappable shapes (non-string dictionary keys, foreign BCL classes) fail the
manifest build instead of degrading to `unknown`.

## Tool vocabularies — the MCP derives them, it no longer retypes them

Wire TYPES reached the MCP from day one (`src/generated/wire-types.generated.ts`, the
same codegen output the web gets). Vocabularies did not: types are erased at runtime and
a `z.enum([...])` needs values, so every MCP tool schema hand-typed its own copy of
statuses, severities and categories — with nothing to compare against. The copies drifted
exactly as you would expect: `search_sessions` offered 5 of 8 session statuses (no
`AwaitingUser`, no `Incomplete`), both event readers offered 4 of 6 severities (no `Debug`,
no `Trace`), and `get_ops_events` named 6 ops categories while the backend wrote 7. None of
that surfaces as an error — the tool just quietly cannot ask for the missing value.

The vocabularies now ship as VALUES. `MCP_VOCABULARIES` in the codegen maps manifest
sections onto `src/generated/wire-vocabularies.generated.ts` (`export const
SESSION_STATUSES = [...] as const` plus its union type); the tool schemas do
`z.enum(SESSION_STATUSES)` and descriptions interpolate the list instead of spelling it.
Freshness is pinned twice: `wire-types-freshness.test.ts` compares the committed file
byte-for-byte against a fresh codegen run, and the `shared-manifests-in-sync` CI job
diffs it after regenerating.

`vocabulary-drift.guard.test.ts` is the ratchet that keeps it that way: every inline
`z.enum([...])` left in `src/tools/*.ts` must be on a baseline that names why it has no
backend owner (MCP-local knobs like `depth: fast|deep`, plus three free-string columns —
rule type, connection type, CVE risk — that have no C# constants class yet). A new
hand-typed list fails until it is derived or reviewed onto the baseline, and a stale
baseline entry fails too.

Ops event types got the missing owner: they were bare literals at the `OpsEventService`
call sites, so nothing could enumerate them and both the portal alert-rule catalog and the
MCP had to retype the list. `OpsEventTypes` (Shared) now declares all 77; the write sites
use the constants (a raw literal fails `OpsEventTypeDualRegisterTests`), the manifest
exports them, and `get_resource(name="ops_event_types")` serves categories + severities +
types to the model from the generated copy.

Backend-internal lists follow the same rule: a list that enumerates a constants class IS
the class. `TableOpsEventRepository.AllCategories` is `OpsEventCategory.All` rather than a
retyped copy — the copy had gone stale and silently hid every `Platform` (Azure Monitor)
event from the paged cross-category read while the unpaged path still showed it.

## Tenant settings — per-section PATCH

The web's Settings sections no longer PUT the full ~92-field configuration. Each
section's exact write surface lives in `app/settings/sectionFieldMap.ts` (owned fields
plus documented `alsoWrites` write-throughs); `saveConfiguration` diffs those fields
against the loaded config and PATCHes only the changes through
`PATCH config/{tenantId}/fields` — the transactional endpoint (CAS + fail-closed
backup + exactly-these-fields verify + auto-rollback) that previously served MCP only.
The policy line is TenantAdminOrGA; a tenant admin runs on the stricter
`TenantConfigCallerTier.TenantAdmin` whose deny-list turns GA-only fields into
explicit 400s. Deploy order: backend before web. `sectionFieldMap.test.ts` pins
field↔section ownership (each field exactly one owner, all fields on the model, none
server-denied); backups/revert stay GlobalAdminOnly.

## Scoped endpoint routing — one decision object

`lib/scopedApi.ts` owns the tenant/global endpoint-pair choice: pages pass their scope
hook object (`routeGlobal`, `selectedTenantId`, `effectiveTenantId`) instead of
hand-rolling `routeGlobal ? global(..., sel || undefined) : tenant(tid, ...)` with the
tenant parameter at a different position per pair. `useGlobalAdminScope` is now a thin
projection over `useAggregatedAdminScope` (one hook, aggregated as a mode) via the
pure `resolveConcreteScopeView`. `lib/scopedFetch.ts` adds the shared JSON+ok-check
fetch layer. `lib/navVisibility.ts` extracts the sidebar's roles × nav-config × guard
logic; its matrix test also ratchets the known "sidebar shows it, guard bounces it"
mismatches (plain Operator and GlobalReader vs. `/settings` — pending a user decision).

# Examples

Renaming a SignalR message end to end: change the constant in
`Constants.SignalRMessages` → `AM_WRITE_SHARED_MANIFESTS=1 dotnet test --filter
SharedManifestParityTests` → `node scripts/generate-shared-manifest-types.js` → tsc
now flags every stale web subscription literal.

Adding a field to a Settings section: add it to the section in `sectionFieldMap.ts`
(tsc validates the name against the manifest), wire the form state into the
`updatedConfig` construction in `TenantConfigContext`, and extend the expected-fields
list in `sectionFieldMap.test.ts`.

# Citations

- `src/Shared/AutopilotMonitor.Shared/Constants.cs` — SignalRMessages catalog
- `src/Web/autopilot-monitor-web/scripts/generate-shared-manifest-types.js` — `MCP_VOCABULARIES`, the vocabulary codegen
- `src/McpServer/autopilot-monitor-mcp/src/__tests__/vocabulary-drift.guard.test.ts` — the inline-enum ratchet
- `src/Shared/AutopilotMonitor.Shared/DataAccess/OpsEventTypes.cs` + `OpsEventTypeDualRegisterTests.cs`
- `src/Backend/AutopilotMonitor.Functions/Helpers/RowKeyCodec.cs` + `RowKeyCodecTests.cs`
- `src/Shared/AutopilotMonitor.Shared/Models/Deletion/DeletionManifest.cs` — `DeletionRowDump.Table`, `DeletionTombstoneTables`
- `src/Backend/AutopilotMonitor.Functions.Tests/TypedResponseGuardTests.cs` + `WireTypeManifestBuilder.cs` + the `*WireParityTests` files
- `src/Shared/AutopilotMonitor.Shared/Models/CommonApiModels.cs` — `IApiResponse`, `[WireContract]`, `[ProjectedItems]`
- `src/Web/autopilot-monitor-web/utils/wire-types.generated.ts` (via `scripts/generate-shared-manifest-types.js`)
- `src/Web/autopilot-monitor-web/app/settings/sectionFieldMap.ts` + `__tests__/sectionFieldMap.test.ts`
- `src/Web/autopilot-monitor-web/lib/scopedApi.ts`, `lib/navVisibility.ts`, `hooks/concreteAdminScopeView.ts`
- [Version Contract](versioning.md) — the web `/version.json` stamp + deploy verify added in the same round
