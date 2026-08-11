---
type: Concept
title: Session Annotations — Role-Separated Human Verdicts
description: The SessionAnnotations table (PK=tenantId, RK={sessionId}_{lane}) storing per-session human verdicts + notes in three role lanes (operator / tenantadmin / globaladmin), the per-lane write matrix with own-tenant binding, the platform-internal GA lane, the fired-rule-id snapshot that makes annotations rule-quality labels, and the portal/MCP surfaces.
resource: /src/Backend/AutopilotMonitor.Functions/Functions/Annotations/UpsertSessionAnnotationFunction.cs
tags:
  - backend
  - web
  - mcp
  - annotations
  - authorization
  - rule-quality
timestamp: 2026-08-11T00:00:00+02:00
---

# Schema

**Table.** `SessionAnnotations`: PartitionKey = `tenantId`, RowKey = `{sessionId}_{lane}` with
lane ∈ `operator | tenantadmin | globaladmin` (a closed set — `AnnotationLanes.All`). One row per
session + lane, edited in place; per-session reads use the RK range `ge '{sid}_' / lt '{sid}_~'`.
Serialization lives only in `TableSessionAnnotationRepository.StoreAnnotation`/`MapAnnotation`
(round-trip-pinned; corrupt `RuleIdsJson` degrades to an empty list on read).

**Row.** `Verdict` (nullable: `root_cause_confirmed | analysis_wrong | different_problem |
inconclusive`), `Note` (nullable, ≤ 4096 chars), `AuthorUpn`/`AuthorDisplayName` (last editor),
`CreatedByUpn`/`CreatedAtUtc` (first writer, immutable across edits), `UpdatedAtUtc`, and
`RuleIdsJson` — a **snapshot of the rule ids that had fired for the session at write time**
(from `IRuleRepository.GetRuleResultsAsync`, fail-soft to empty). The snapshot is what turns an
annotation into a rule-quality label without a join: filtering annotations by `ruleId` + `verdict`
yields confirmed vs false-positive counts per rule even after results are re-analyzed or deleted.

**Verdict + note are independently optional**; a PUT with both null **deletes** the lane row
(there is no DELETE route). Author identity is always stamped server-side from the JWT
(`TenantHelper.GetUserDisplayName` + `RequestContext.UserPrincipalName`) — a body-supplied
author never wins (same anti-spoof contract as the gather-rule PUT-upserts).

# Authorization

Routes (all in `EndpointAccessPolicyCatalog`):

| Route | Policy | Notes |
| --- | --- | --- |
| `GET sessions/{sessionId}/annotations` | `MemberRead` + QueryParam | handler filters the GA lane |
| `PUT sessions/{sessionId}/annotations/{lane}` | `TenantAdminOrOperator` + QueryParam | re-gated per lane in-function |
| `GET global/session-annotations` | `GlobalReadOrAdmin` + QueryParam | evaluation stream |

**Per-lane write matrix** (`UpsertSessionAnnotationFunction.IsLaneWritableByCaller`, an
`internal static` pure function like `QueueSessionActionFunction.IsTypeAllowedForCaller`):
`operator` → tenant Operator or tenant Admin; `tenantadmin` → tenant Admin; `globaladmin` →
Global Admin only. **Own-tenant binding:** the session's tenant is resolved *before* the gate
(GA cross-tenant fallback via `FindSessionTenantIdAsync`), and tenant-role inputs are zeroed
when the resolved tenant differs from the caller's JWT tenant — so a GA who is also an admin of
their home tenant can never write another tenant's operator/tenantadmin lanes. Cross-tenant, a
GA writes exactly the `globaladmin` lane (the platform labeling flow).

**GA lane is platform-internal.** `GetSessionAnnotationsFunction.FilterLanesForCaller` drops the
`globaladmin` lane for every caller without `HasGlobalScope` (tenant members and delegated
admins). Tenant lanes are readable by all members of that tenant.

**Audit.** Non-GA writes/clears log `LogAuditEntryAsync(..., "SessionAnnotation",
"{sessionId}/{lane}", ...)`; GA writes are skipped (platform-internal labeling must not surface
in the tenant-visible audit log — same convention as GA session-report submissions).

# Lifecycle

* **Per-session cascade delete**: three exact-RK steps (one per lane) in `DeletionManifestBuilder`
  (orders 17–19; inventory 20/21, tombstone 22). `ComputePreflightCounts` sums repeated-table
  steps so the `sessionAnnotations` count covers all lanes.
* **Tenant offboarding**: `SessionAnnotations` is in the PK=tenantId wipe list — deliberately
  wiped, unlike `Feedback` (annotations are customer session data, not product feedback).
* **Backup**: member of `Constants.CriticalBackupTables.All` — hand-labeled data is the most
  expensive data on the platform to lose.
* Being in `Constants.TableNames.All` auto-creates the table at startup and exposes it to the
  raw `list_tables`/`query_table` tools.

# Surfaces

* **Portal**: `SessionAnnotationsCard` on the session detail page (`#section-annotations`, after
  Analysis; registered in BOTH the section div and the `sessionSections` sidebar registry). The
  card mirrors the matrix via the pure module `sessionAnnotationLogic.ts` (vitest-pinned) and
  uses the explicit-Save pattern; the backend re-gates every save.
* **MCP read**: `get_session_summary` carries an `annotations` key (4th parallel leg, fail-soft
  null); `list_session_annotations` (`ga`-gated) is the evaluation stream with `tenantId / lane /
  verdict / ruleId / dateFrom / dateTo` filters and nextLink pagination. The `ruleId` filter is
  applied server-side but client-of-Azure (substring on the JSON column), so the backend
  back-fills short pages by looping the Azure continuation (bounded rounds) — filtered-out rows
  never consume page budget.
* **MCP write**: `annotate_session` (`strictGa`-gated, MUTATING) always PUTs the `globaladmin`
  lane — no lane argument by design. This is the frictionless labeling path after a session-debug.
* **Session reports**: `SessionReportService.SubmitReportAsync` snapshots ALL lanes into
  `annotations.json` inside the report ZIP (`annotationCount` in report-metadata.json; fail-soft).
  Safe because report blobs are readable only through the GA-only session-reports routes, and
  valuable because the ZIP outlives the session's retention/delete.

# Citations

* `src/Shared/AutopilotMonitor.Shared/Models/SessionAnnotation.cs` — model, lanes, verdicts.
* `src/Backend/AutopilotMonitor.Functions/DataAccess/TableStorage/TableSessionAnnotationRepository.cs` — Store/Map + query back-fill.
* `src/Backend/AutopilotMonitor.Functions/Functions/Annotations/` — the three endpoints.
* `src/Backend/AutopilotMonitor.Functions/Pagination/SessionAnnotationsPagination.cs` — filter-bound continuation tokens.
* `src/Web/autopilot-monitor-web/app/sessions/components/sessionAnnotationLogic.ts` — portal matrix mirror.
* `src/McpServer/autopilot-monitor-mcp/src/tools/admin.ts` — `list_session_annotations`, `annotate_session`.
