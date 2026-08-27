---
type: Concept
title: Verdict Calibration (classifier thermometer)
description: How every session status write declares its VerdictPath, how overriding a verdict preserves it as PriorStatus/PriorVerdictPath, how the daily calibration aggregate counts paths with the re-enrollment proxy and the correction stream, and how the drift radar (share / silence / evidence-gap) turns that into operator-only ops events.
resource: src/Backend/AutopilotMonitor.Functions/Services/MaintenanceService.VerdictCalibration.cs
tags:
  - backend
  - sessions
  - classifier
  - calibration
  - regression-radar
timestamp: 2026-08-27T20:00:00+02:00
---

# Verdict Calibration (classifier thermometer)

A session's terminal status is written by ~20 code paths: the agent's own terminal events,
the silence classifier (`EnrollmentTimeoutClassifier`, rules 1–6) reached via the
maintenance sweep / the agent's max-lifetime shutdown / late-telemetry heal / retro
reclassification, the analyze-rule engine, admin marks and session superseding. Verdict
calibration makes every one of those paths **countable**, tracks how often a path's verdict
was later **overridden**, attaches the delayed **re-enrollment proxy**, and raises an
operator-only alarm when a path's share **drifts**. It is deterministic, fact-based tuning
input for the rules — explicitly not a learned model, and never a customer-facing feature.

# Schema

## VerdictPath on the session row

`SessionSummary.VerdictPath` (`VerdictPaths` vocabulary, `origin:detail`, append-only) is
stamped by **every** status write — `UpdateSessionStatusAsync` takes it as a required
parameter, `StoreSessionAsync` stamps `register:new` / `register:whiteglove_resume` and
preserves an existing path through the re-registration Replace, `SetSessionPreProvisionedAsync`
requires it whenever it writes a status. Unlike `FailureSource` (persisted only on `Failed`),
the path survives for Incomplete, AwaitingUser, Stalled and Pending.

| Origin | Paths |
| --- | --- |
| `agent` | `complete`, `complete_soft` (Continue-Anyway), `failed`, `esp_failure_fallback`, `gather_complete`, `whiteglove_pending`, `whiteglove_resumed`, `stall_probe`, `stall_heal` |
| `ingest` | `wg_awaiting` (stall probe mapped to WhiteGlove AwaitingUser) |
| `sweep` | `stalled`, `wg_awaiting`, `sd_reconcile`, `r{rule}` (stage-2 classifier) |
| `maxlife` / `late` / `retro` | `r{rule}` (classifier via max-lifetime shutdown / late-telemetry heal / retro reclassification); `retro:sd_reconcile`, `retro:superseded` |
| `register` | `new`, `whiteglove_resume`, `superseded` |
| `rule` | `rule:{ruleId}` (analyze rule with MarkSessionAsFailed) |
| `manual` | `failed`, `succeeded` |
| `legacy` | read-side derivation only (`legacy:{rule}` when the rule is recoverable from the reason literal, `legacy:unknown` otherwise) — never written |

Classifier rule ids (`ClassifierRules`): `r1`, `r1b_awaiting`, `r1b_succeeded`, `r1c`, `r2`,
`r3`, `r4`, `r5_awaiting`, `r5_incomplete`, `r6`. `ClassifyTimedOutSession` returns
`(Status, Reason, Rule)` so each `return` names its rule.

## PriorStatus / PriorVerdictPath — the correction stream

When a write replaces a prior **verdict** (existing status ∈ Succeeded/Failed/Incomplete/
AwaitingUser with a stamped path, incoming status different), the storage seam preserves the
pair (`TableStorageService.ComputePriorVerdict`, both the ETag path and the force path). A
stall marker healing to InProgress, a same-status refresh, or an unstamped legacy row never
sets it. Admin marks, late agent completions and retro reclassifications all flow through it.

## Read-side derivation for pre-instrumentation rows

`VerdictPathDerivation.Derive(session)` returns the stamped path verbatim, otherwise derives
from `AdminMarkedAction`, `FailureSource`, `ReconcileReason` and `FailureReason` using the
writers' fixed literals (classifier rule prefixes, the `max-lifetime watchdog` / `Retro-
reclassified` suffixes, `Rule: `, `Superseded by session`, `Late completion report received`).
Unambiguous rows get the real path (`agent:complete`, `manual:failed`, `rule:…`,
`maxlife:r6`), ambiguous-but-rule-known rows `legacy:{rule}`, the rest `legacy:unknown`. No
mass backfill: the aggregate counts derived rows with `DerivedCount` so the matrix shows
history while flagging its weaker evidence. Tests pin the literals against the classifier
output so a wording change fails loudly.

## Daily aggregate — `VerdictCalibrationAggregates`

PK = `TenantId` | `"global"`, RK = `yyyy-MM-dd` (session **StartedAt** date, like every daily
aggregate). `VerdictCalibrationDailyAggregate { Version, SessionCount, TerminalSessionCount,
Buckets[], ComputedAt }` with one bucket per (VerdictPath, Status):

| Field | Meaning |
| --- | --- |
| `Count` / `DerivedCount` | sessions currently on the path; of those, derived read-side |
| `Eligible7d` | terminal sessions whose end (`CompletedAt ?? LastEventAt ?? StartedAt`) lies ≥ 7 d before compute time |
| `ReEnrolled7d` | of the eligible: the device's `DeviceHistories` chain shows the next terminal session starting < 7 d after this one's end |
| `OverriddenByAdmin` / `OverriddenByLateCompletion` / `OverriddenOther` | sessions whose **PriorVerdictPath** is this bucket — attributed to the path that was overridden, so a bucket can carry overrides with `Count = 0` |

`MaintenanceService.SweepVerdictCalibrationAsync` (2-hourly, after the device-journey sweep
so the chains are fresh; manual maintenance parity; kill switch
`VerdictCalibrationSweepDisabled`) slices the tick's shared projected window scan
(`LoadSweepWindowSessionsAsync` — one 35-day cross-tenant drain feeds the time-attribution,
device-journey and calibration sweeps plus the radar's tenant discovery; the StartedAt-only
filter is a full-table read, so never per sweep), recomputes the rolling 30-day window whole (Replace),
deletes stale date rows it did not regenerate, and the unbounded-tables cleanup applies the
180-day retention. Sessions inside a deletion cascade are skipped; the table is regenerable
and is wiped with the tenant on offboarding.

## Read surfaces

* `GET global/metrics/verdict-calibration?days=&tenantId=` (GlobalReadOrAdmin,
  TenantScoping.QueryParam; `VerdictCalibrationResponse.Build`): per path — count, share of
  all window sessions, derived count, eligible/re-enrolled with `reEnrollRatePct` **null below
  20 eligible**, the three override counters, a today-anchored 7d-vs-28d share trend
  (`window7`, `baseline28`, `lift` null without baseline) — plus totals, the trend basis and
  the active `alerts[]`. Empty but well-formed before the first sweep.
* MCP `get_verdict_calibration` (platform scope: GA + Global Reader). Same endpoint, LLM-shaped in
  `verdict-calibration-shape.ts`: per-row `window7.sessions` / `baseline28.sessions` are dropped (the
  denominators live once in `trend`), `reEnrollRatePct` / `lift` are always present (`null` = withheld),
  and `minSharePct` / `top` trim the one-session tail — rows carrying overrides are never trimmed and
  everything dropped is reported in `omitted`. The wire shape the admin page consumes is untouched.
* Portal: Global Admin → Metrics → Verdict Calibration (`/admin/metrics/verdict-calibration`):
  matrix grouped by origin, status pill, `derived n` gray pill, trend glyph (arrow only from
  5 window hits and lift ≥ 2 / ≤ 0.5), re-enrollment rate or `— (n=…)`, overrides
  `admin / late / other`, `↑ Drift` pill on rows with an active episode, and the active
  episodes list.

## Drift radar — `VerdictCalibrationRadar`

Same statistics as the [rule regression radar](rule-regression-radar.md) (7-day window vs the
prior 28 days, ≥ 20 window sessions, lift ≥ 2, `MetricsMath.RateIncreaseSeparated`), with
stricter floors since the first-month tuning (2026-08-27): **≥ 10 window hits** (verdict
shares on single-digit counts are noise — a 3-hit episode survived as such), and a per-path
share regression additionally needs **≥ 5 baseline hits** — a path with no established
baseline is new (or renamed) vocabulary, not a regression. Anchored on yesterday, over every
partition with rows (each tenant and `global`):

| Kind | Signal | Re-arm |
| --- | --- | --- |
| `share_regression` | one **backend-decided** path's share of all sessions doubled (`sweep`/`maxlife`/`late`/`retro`/`rule`/`manual`/`ingest`; `agent:*` and `register:*` mirror customer workflow mix — first prod pass fired on `agent:whiteglove_pending` rollout weeks — and `legacy:*` is derived, all excluded per-path) | share < 1.5× baseline, the path stops occurring, the path is no longer eligible, or the current numbers fall below the fire floors |
| `silence_share_regression` | the `sweep:*` + `maxlife:*` share doubled — the backend had to decide more often because the agent went silent (a liveness signal, not a classifier one). Rule-shaped `legacy:*` paths count into BOTH sums (window and baseline): they were the same backend decisions before the 2026-08-23 instrumentation, and without that continuity the group had a near-zero baseline by construction and fired a lift-124 rollout artifact | share < 1.5× baseline or hits stop |
| `evidence_gap` | absolute: `r6` (pure fallthrough) decides ≥ 20 % of the window's classifier verdicts (stamped rule paths plus rule-shaped `legacy:*`, same continuity), ≥ 20 verdicts | < 15 % or < 20 verdicts |

Episodes are tracker rows `verdictcalibration|{kind}|{path}|{status}` (register = `AddEntity`,
409 = already burning, fail-closed; refresh carries `FirstNotifiedAt`; 30-day tracker
retention re-arms long burners). A new episode fires **one** `VerdictCalibrationDrift` ops
event (category Maintenance, Warning; dual-registered in the web `OPS_EVENT_TYPES`) — there
is deliberately **no tenant bell**. Per-path findings on a tenant partition get the on-fire
dimension correlation (`RuleRegressionRadar.ComputeDimensionConcentration` over the window's
sessions whose derived path matches), reported with the shared wording contract "correlated,
not necessarily causal"; group kinds and the global partition carry no dimension claim.
Kill switch `VerdictCalibrationRadarDisabled`.

# Examples

* **Reading the thermometer (prod, 2026-08-23 hand analysis that motivated this):**
  re-enrollment within 7 days after Succeeded 8.9 % (lab background), after Failed 24.3 %,
  after Incomplete 10.6 % — Incomplete behaves like Succeeded, so rules 5/6 are probably too
  cautious; the matrix now shows this per rule instead of per status.
* **First real calibration read (2026-08-27, four days instrumented):** the per-rule and
  per-tenant split resolved that hand analysis — `legacy:r5_incomplete` re-enrolled at 2.6 %
  (the `agent:complete_soft` background), spread across tenants, while `legacy:r6`'s 29 %
  was concentrated in one tenant that disciplinedly redoes failed enrollments (48.5 % there,
  6.7 % elsewhere — so r6 stays untouched, it correctly flags that tenant). Consequences
  shipped the same day: rule 5a "completed (assumed)" (see
  [Silent-Session Classification](silent-session-classification.md)), the max-lifetime grace
  skip, and the radar floors + legacy group continuity above — the first production month
  had produced nine standing alerts, every one an instrumentation-rollout artifact.
* **Correction stream:** a session swept to `sweep:r5_incomplete`, later completed by the
  agent → row `agent:complete` with `PriorVerdictPath = sweep:r5_incomplete`; the aggregate
  increments `OverriddenByLateCompletion` on the `sweep:r5_incomplete / Incomplete` bucket.
  Four admin marks since launch were invisible before — now each one counts against the path
  it corrected.
* **Evidence gap:** a tenant whose agent build stopped emitting the Device Setup rollup line
  drives its sweep verdicts into `r6`; the absolute gate fires at 20 % and names the
  tenant — the fix is agent-side, the classifier is only the messenger.

# Citations

* `src/Shared/AutopilotMonitor.Shared/Models/VerdictPaths.cs` — vocabulary + `ClassifierRules`
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.Sessions.cs` — `ComputePriorVerdict`, `ApplyVerdictPath`, re-registration preserve
* `src/Backend/AutopilotMonitor.Functions/Helpers/VerdictPathDerivation.cs`
* `src/Backend/AutopilotMonitor.Functions/Services/MaintenanceService.VerdictCalibration.cs` — sweep + `BuildVerdictCalibrationAggregates`
* `src/Backend/AutopilotMonitor.Functions/Functions/Metrics/GetVerdictCalibrationFunction.cs`
* `src/Backend/AutopilotMonitor.Functions/Helpers/VerdictCalibrationRadar.cs`, `Services/MaintenanceService.VerdictCalibrationRadar.cs`
* `src/McpServer/autopilot-monitor-mcp/src/tools/admin.ts` — `get_verdict_calibration`
* `src/McpServer/autopilot-monitor-mcp/src/verdict-calibration-shape.ts` — LLM-facing response shaping (`minSharePct` / `top`, explicit nulls)
* `src/Web/autopilot-monitor-web/app/admin/metrics/sections/SectionVerdictCalibration.tsx`
* Tests: `VerdictPathTests`, `VerdictCalibrationTests`, `VerdictCalibrationRadarTests`
* Related: [Silent-Session Classification](silent-session-classification.md), [Device Journeys](device-journeys.md), [Rule Regression Radar](rule-regression-radar.md)
