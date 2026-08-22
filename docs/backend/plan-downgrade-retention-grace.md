---
type: concept
title: Plan Downgrade Policy & the Retention Grace Period
description: What happens when a tenant loses Pro (explicit downgrade or trial expiry) — every entitlement gates read-time and reversibly, except retention, which gets a 30-day grace before the Community cap starts hard-deleting; the anchor model, the ops events, and the deliberate non-grace of everything else.
resource: backend
tags: [plan, edition, downgrade, retention, grace-period, trial, entitlements]
timestamp: 2026-08-22
---

# Schema

## The downgrade model: gate, don't destroy

Edition is resolved at READ time (`FeatureEntitlementCatalog.ResolveEdition`): Pro ⇔
permanent tier `pro`/legacy `enterprise` OR an active trial. A plan mutation is a single
field write (`PATCH config/{tenantId}/plan`, GA-only) — there is no downgrade event
handler, no cascade. On Pro → Community every entitlement simply gates on the next read
(≤ ~5 min config cache):

* **Unrestricted Mode** — effective value flips false; the GA on-request flag and the
  tenant toggle survive untouched (inert). Re-upgrade restores it verbatim.
* **Delegated (MSP) admin** — scope suppressed at the home-tenant gate
  (`DelegatedAdminService.ApplyHomeTenantGateAsync`); grant rows / tenant groups /
  assignments are never touched. Resurrects on re-upgrade.
* **OOBE bootstrap** — endpoints 403/404 unless the additive GA per-tenant flag
  (`BootstrapTokenEnabled`) is set; `BootstrapSessions` rows are kept.
* **Rate limits / MCP quota** — pure computed-at-read floors/plan names.

**Retention is the one exception**: the retention sweep (12-hourly
`SessionDeletionMaintenance` → `SessionRetentionFanoutService`) hard-deletes sessions
older than `GetEffectiveRetentionDays` = `min(stored DataRetentionDays, edition cap)`.
Without protection, a downgrade (or trial expiry) with stored retention 365 would start
irreversibly cascade-deleting the 90–365-day band on the next tick.

## The retention grace period (30 days)

`FeatureEntitlementCatalog.RetentionDowngradeGraceDays = 30`. For 30 days after losing
Pro, `GetEffectiveRetentionDays` keeps clamping to the **Pro** cap (365); only after the
grace does the Community cap (90) bite. The stored `DataRetentionDays` is never rewritten.
`days <= 0` (GA-only "infinite") short-circuits before any cap — those tenants are never
swept, grace or not.

**Anchor** — "when was Pro lost" (`TenantEntitlementService.GetRetentionGraceEndUtc`,
returns null while effectively Pro; grace end = latest anchor + 30d):

* **Explicit downgrade**: `TenantConfiguration.ProDowngradedUtc` (backend-only field, no
  agent ConfigVersion impact), maintained exclusively by the plan endpoint's pure mutation
  core `PlanManagementFunction.ApplyPlanChanges`. Stamped when the EFFECTIVE edition
  (before vs. after, same `nowUtc`) transitions Pro → Community — this covers both a
  planTier downgrade and a GA explicitly ending a trial (`trialExpiresUtc: null`, where
  the trial timestamp disappears and could not anchor). Cleared on any transition back to
  effectively Pro (PATCH pro, trial grant, self-service trial start), so a re-upgrade also
  resets the grace clock.
* **Trial expiry**: no write happens — the stored `TrialExpiresUtc` itself is the anchor
  (read-time, like edition resolution). A planTier downgrade under a still-active trial
  deliberately does NOT stamp: the edition is still Pro, and the later trial expiry
  anchors the grace then.

The field is on the patch-endpoint `BaseDeniedFields` and `RevertProtectedFields` lists
(a config revert must not time-travel the anchor) and roundtrips the table-serialization
contract (Store+Map + roundtrip tests). Legacy rows read null → no anchor → no grace,
which is correct: no tenant had been downgraded before the feature existed.

## Ops visibility (dual-registered event types)

* `TenantPlanDowngraded` (Warning) — emitted by the PATCH handler on an effective
  Pro→Community transition; carries `retentionGraceEndsUtc` + stored retention days. The
  response body also returns `retentionGraceEndsUtc`.
* `TenantRetentionGraceExpiring` / `TenantRetentionGraceEnded` (Warning) — emitted by the
  daily `TrialExpirySweepFunction` (03:30 UTC) with the same stateless window mechanics as
  the trial events (≤3-day heads-up re-fired daily; ended once via the 24h look-back), and
  ONLY when data is actually at risk: stored `DataRetentionDays` above the Community cap
  (0 = infinite ⇒ never). Re-upgraded tenants go silent automatically (grace resolves null).

All three are registered in the web `OPS_EVENT_TYPES` catalog (OpsAlertRulesSection.tsx)
so Telegram alert rules can route them.

## Deliberate non-goals

* No grace for Unrestricted/MSP/bootstrap/limits — reversible gates need none.
* The retention WRITE cap (`TenantConfigValidation`) does not honor the grace: during the
  grace a tenant admin cannot SET a value above the Community cap. The grace protects
  existing data; new writes follow the new plan.
* The feature-flags endpoint keeps publishing the plain edition cap
  (`retentionCapDays`) — it feeds the settings input maximum, which is the write cap.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Services/TenantEntitlementService.cs` — grace + effective retention
* `src/Backend/AutopilotMonitor.Functions/Functions/Config/PlanManagementFunction.cs` — `ApplyPlanChanges` anchor lifecycle
* `src/Backend/AutopilotMonitor.Functions/Functions/Maintenance/TrialExpirySweepFunction.cs` — grace event windows
* `src/Backend/AutopilotMonitor.Functions/Services/Deletion/SessionRetentionFanoutService.cs` — enforcement point
* Tests: `TenantEntitlementServiceTests`, `PlanManagementTransitionTests`, `TrialExpirySweepFunctionTests`, `SessionRetentionFanoutServiceTests`, `TenantConfigTableSerializationTests`
