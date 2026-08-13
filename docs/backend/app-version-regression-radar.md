---
type: Concept
title: App Version Duration Regression Radar
description: How the radar detects app versions whose median install duration regressed vs. the previous version (median lift ≥2x plus an absolute floor over first-seen-ordered versions), how alert episodes live in the appversionregression tracker keyspace, and which surfaces show them.
resource: src/Backend/AutopilotMonitor.Functions/Helpers/AppVersionRegressionRadar.cs
tags:
  - backend
  - apps
  - regression-radar
  - insights
timestamp: 2026-08-13T12:00:00+02:00
---

# App Version Duration Regression Radar

Detects "version X made this app's installs much slower" — the concrete, quotable insight
("median install duration rose from 11 to 29 min after version X") — from the per-(session,
app) `AppInstallSummaries` rows. Duration only: failure-rate per version is already visible
in the apps dashboard's `versionBreakdown`, and rate regressions belong to Wilson-interval
statistics, not medians.

# Schema

## Signal & detection

Per (tenant, app), the MEASURED install population is: `Status == "Succeeded"`, terminal
state not Skipped/Postponed, `MetricsMath.HasMeasuredDuration` (1s..6h — 0s means the start
was unobserved, above 6h means back-stamped batches), no `AppIdCollision`, non-empty
`AppVersion`. Versions are ordered by FIRST-SEEN install time (min `StartedAt`), never by
string comparison ("9.1" vs "2024.10" would lie). The comparison pair is always the newest
version (the one with the most recent measured install) vs. the version with the latest
first-seen strictly before it.

MEDIANS, not means (nearest-rank, shared with the apps dashboard via
`AppsAnalyticsHelper.Percentile`) — one back-stamped straggler must not fire a fleet alert.
A regression fires only when ALL hold over the trailing 35-day horizon:

1. both versions have ≥ 10 measured installs;
2. current median ≥ 2× the previous version's median;
3. absolute increase ≥ 300 s (a 20s→45s app is noise, not an incident).

## Suppression & limits

* Parallel ring rollouts can make "previous" a concurrently-deployed version — accepted in
  v1; the episode key (app, current version) still caps it at one bell per version.
* Platform kill switch: app setting `AppVersionRegressionRadarDisabled=true` skips the
  radar (fail-open — it only notifies, never mutates data).
* One cross-tenant install-summary read per pass (the same query the global apps dashboard
  runs), grouped by tenant; per-tenant failures are non-fatal.

## Alert episodes (tracker keyspace)

An episode lives as `appversionregression|{app-lower}|{version-lower}` (table-key-sanitized;
raw AppName/CurrentVersion live in columns, the mapper never parses the RowKey) in the
notification-tracker table, next to the `ruleregression|` keyspace: the row IS the dedup
(bell + ops event fire exactly once per episode) and the `versionRegressions[]` payload —
both medians, both sample counts, lift. Numbers refresh on every radar pass while the
episode stays active; `FirstNotifiedAt` never moves. The episode closes (dedup re-arms) when
the alerted version drains under 10 measured installs in the horizon or its recomputed
median falls back under 1.5× the previous version's median; the tracker's 30-day retention
sweep also re-arms, so a month-old still-burning regression rings once more by design.

## Notification chain (v1: bell + ops event)

* Tenant bell `app_version_duration_regression` (audience: Admin), href
  `/apps/detail?name={app}&days=35` (canonical `appDetailUrl` shape). Message carries the
  full numbers so the admin can verify without a portal round-trip.
* Ops event `AppVersionDurationRegression` (Tenant category, Warning) — dual-registered in
  the web `OPS_EVENT_TYPES` catalog so operators can wire Telegram/Teams/Slack alert rules.

## Surfaces

* `apps/{appName}/analytics` (+ global twin with `?tenantId=`) responds with
  `versionRegressions[]` (empty for the cross-tenant aggregate — episodes are per-tenant
  rows), and `versionBreakdown[]` rows now carry `measuredInstalls` /
  `medianDurationSeconds` / `p95DurationSeconds`.
* Apps detail page: amber "↑ Duration regression" banner above the version cards and a
  "Median Install Duration by Version" bar chart.

# Examples

A tenant ships Contoso VPN 2.4.0 after 2.3.9. 2.3.9 has 40 measured installs, median 660s;
2.4.0 accumulates 12 measured installs with median 1740s. Lift 2.6×, absolute +1080s →
episode `appversionregression|contoso vpn|2.4.0` opens, one bell + one ops event fire.
Subsequent passes refresh the numbers only. When 2.4.1 replaces 2.4.0 and 2.4.0 drains
below 10 measured installs in the horizon, the episode deletes and the dedup re-arms.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Helpers/AppVersionRegressionRadar.cs` — pure core
  (gates, first-seen ordering, re-arm).
* `src/Backend/AutopilotMonitor.Functions/Services/MaintenanceService.AppVersionRegression.cs`
  — orchestration, kill switch, bell/ops-event emission, wording contract.
* `src/Backend/AutopilotMonitor.Functions/DataAccess/TableStorage/TableHardwareRejectionNotificationTracker.cs`
  — episode keyspace, entity round-trip.
* `src/Shared/AutopilotMonitor.Shared/Models/Metrics/AppVersionRegressionAlert.cs` — persisted
  alert payload.
* [Rule Regression Radar](rule-regression-radar.md) — the sibling pattern this radar mirrors.
