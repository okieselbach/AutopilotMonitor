---
type: concept
title: Attempt-Scoped App Install Durations
description: AppInstallSummary.DurationSeconds measures the status-defining (last) install attempt via a LastAttemptStartedAt max-fold anchor — replacing the first-observation-to-last-terminal span that billed multi-pass IME processing gaps to trivial apps; time attribution counts the union of active attempt segments.
resource: src/Backend/AutopilotMonitor.Functions/Services/EventIngestProcessor.AppInstall.cs
tags: [backend, agent, ingest, app-installs, metrics, time-attribution, durations]
timestamp: 2026-08-21
---

# Problem

The IME processes the ESP app list in multiple passes: a device-ESP evaluation pass can
touch an app (often ending `Skipped`) long before the real install runs in a later pass.
`AppInstallSummary` is keyed by `{SessionId}_{AppName}` and folded from ALL of the app's
events, so `DurationSeconds = last terminal − first observation` spanned those passes.
Field case (session `44862cd3`, "Set TimeZone"): evaluation pass 09:16, real install
10:22:58 → 10:23:21 — reported 4002 s for a 23-second install. Fleet averages, the
Slowest-Apps ranking, p95s, and the version-regression radar all inherited the inflation;
the rule engine's `app_install_duration` (already attempt-scoped) contradicted the
dashboard for the same install.

# Schema

Three durable anchors on the row — each with a distinct, non-interchangeable job:

| Column | Fold | Role |
|---|---|---|
| `StartedAt` | min (earliest wins) | First observation. Server-side window/bucket filter of every AppInstallSummaries scan and the radar's version-ordering key. Never re-anchored. |
| `LastAttemptStartedAt` | **max** (in-batch and in `ReconcileAppInstallSummaryWithExisting`) | Anchor of `DurationSeconds`. Null = sentinel (row predates 2026-08, or no start observed) → duration falls back to the historical `StartedAt` span. Replay of an older batch can never regress it. |
| `InstallPassCount` | additive, guarded by `LastAttemptStartedAt` monotonicity (a batch whose newest start is not newer than the stored anchor adds 0) | Explains "finished long after first seen" (web ×N chip); 2+ is normal. |

`DurationSeconds = CompletedAt − LastAttemptStartedAt`, computed at the terminal event and
re-derived in one central reconcile site covering in-order and Q4 out-of-order batches.
A `LastAttemptStartedAt` newer than `CompletedAt` means a fresh pass is in flight — the
completed attempt's stored duration stands (the 0 value omits the column; Merge preserves).
`DownloadDurationSeconds` pairs the latest `app_download_started` with the NEXT
`app_install_started` (attempt-scoped, in-batch). The full span stays derivable as
`CompletedAt − StartedAt` — deliberately not a column.

Weaker-terminal guard (both in-batch and cross-batch): `app_install_completed` with
state `Skipped`/`Postponed` never overrides an `Installed`/`Error` terminal, nor its
`CompletedAt`/`DurationSeconds` — mirroring the long-pinned `app_install_skipped` rule.

# Consumers

* **Fleet metrics / apps dashboard / PDF** (`MetricsMath`, `AppsAnalyticsHelper`): values
  drop to attempt scale; labels say "install time (final attempt)". The 6 h
  `MaxPlausibleInstallDurationSeconds` gate remains (an attempt longer than the agent's
  lifetime is still unobservable).
* **AppVersionRegressionRadar**: cutover gate in `MeasuredAppGroups` — only rows with
  `LastAttemptStartedAt` enter the duration population, on BOTH comparison sides. A mixed
  population would mask real regressions and falsely re-arm active episodes for up to the
  35-day horizon. `MinMeasuredInstalls` keeps it silent until enough post-cutover rows
  exist. Thresholds (2.0× / 300 s) were tuned on span data — re-evaluate ~4 weeks after
  rollout.
* **Time attribution** (`TimeAttributionCalculator.BuildBlockingAppIntervals`):
  sequence-ordered start→terminal pairing yields one segment per pass/attempt. Per-app
  seconds = SUM of clamped segments (failed attempts and evaluation passes count — they
  occupied the path); occupancy merges the segments, never the per-app hulls; hull
  endpoints remain first-start→last-terminal for display and what-if. Deliberately
  BROADER than the metrics duration: active time across all passes vs. final attempt.
* **Agent payloads** (`AppInstallTiming` / `ImeLogTrackerAdapter.UpdateAppTiming`): a
  terminal→active transition re-arms BOTH endpoints, so event `durationSeconds` also
  describes the attempt (needs the agent release carrying this change).
* **Rule engine** `app_install_duration`: was already attempt-scoped — now consistent.

# Rollout

No backfill (user decision, analog to the CMTrace legacy): pre-cutover rows keep span
values, so time-series charts step down visibly at the changeover (documented in the
public platform changelog). Sentinel-gated columns follow the standard chain:
`BuildAppInstallSummaryEntity` + `MapToAppInstallSummary` + `AppsDashboardProjection`
(the only projection whose consumers read the new columns).

# Citations

* `src/Backend/AutopilotMonitor.Functions/Services/EventIngestProcessor.AppInstall.cs` — fold + weaker-terminal guard + attempt pairing
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.Metrics.cs` — reconcile max-fold, pass-count guard, central duration recompute
* `src/Backend/AutopilotMonitor.Functions/Helpers/AppVersionRegressionRadar.cs` — cutover gate
* `src/Backend/AutopilotMonitor.Functions/Helpers/TimeAttributionCalculator.cs` — per-pass segments + occupancy
* `src/Agent/AutopilotMonitor.Agent.V2.Core/SignalAdapters/ImeLogTrackerAdapter.cs` — payload timing re-arm
* Tests: `AppInstallTerminalStateAndReconcileTests`, `EventTimestampValidationTests`, `AppVersionRegressionRadarTests`, `TimeAttributionCalculatorTests`
