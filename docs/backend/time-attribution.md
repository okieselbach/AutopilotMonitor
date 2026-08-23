---
type: Concept
title: Enrollment Time Attribution (F1)
description: How a terminal session's wall clock is partitioned into named segments with an explicit unattributed remainder, how ESP-blocking apps and reboots are attributed, and where breakdowns and fleet aggregates are computed, stored and served.
resource: src/Backend/AutopilotMonitor.Functions/Helpers/TimeAttributionCalculator.cs
tags:
  - backend
  - metrics
  - time-attribution
  - insights
timestamp: 2026-07-26T20:00:00+02:00
---

# Enrollment Time Attribution (F1)

Answers "where did the 47 minutes go?" per session and per fleet. Every number is
traceable to observed events; a missing signal produces "unknown" or an explicit
unattributed remainder — never a guess, and the partition is never normalized to 100 %.

# Schema

## Invariant

For every computed breakdown:

```
sum(span seconds) + UnattributedSeconds == WallClockSeconds == session DurationSeconds
```

`DurationSeconds` is the authoritative wall clock — for WhiteGlove it is part 1 + part 2
WITHOUT the pause, and re-terminal stamps move `CompletedAt`, so `CompletedAt − StartedAt`
is never used (it diverges in ~25 % of terminal sessions). Span seconds are floored;
sub-second dust lands in the unattributed remainder.

## Observation windows

Windows are end-anchored on authoritative timestamps and constructed so their lengths sum
exactly to `DurationSeconds`:

* Non-WhiteGlove: one window `[CompletedAt − DurationSeconds, CompletedAt]`.
* WhiteGlove (ONE session row, two windows): part 2 = `[ResumedAt, CompletedAt]`; part 1 =
  `[part1End − (DurationSeconds − part2), part1End]` where `part1End` is anchored on the
  `whiteglove_part1_complete` event. The pause between the windows is never attributed.
  Fallback anchors set the `WhiteGloveAnchorsIncomplete` quality flag.

## Segments

Spans are sliced at phase-declaration events (only declaration events carry
`Phase != Unknown`) plus `desktop_arrived`, in canonical Sequence order; anchors whose
timestamp runs backward are dropped (`ClockSkewDropped`).

| EnrollmentPhase | Segment key |
|---|---|
| Start / DevicePreparation / DeviceSetup | `device_prep` |
| AppsDevice | `esp_apps` |
| AccountSetup / FinalizingSetup | `identity_hello` |
| AppsUser | `user_esp` |
| Complete, `desktop_arrived` event | `desktop_handoff` |
| Failed | attribution ends — tail stays unattributed |

## Quality flags (AttributionVersion 2)

Duration-critical flags (`ClockSkewDropped`, `PartialObservation`,
`WhiteGloveAnchorsIncomplete`, `PriorEnrollmentResidue`) exclude a breakdown from fleet
segment statistics with a disclosed count; the blocking-set flags (`BlockingSetUnknown`,
`BlockingSetTruncated`) only limit per-app evidence and never gate fleet stats.

`PriorEnrollmentResidue` (v2) fires on `historic_ime_replay_detected`: the IME log on disk
predates the enrollment, so the device was re-enrolled without a wipe — pre-installed apps
complete as instant detections and pull the phase anchors ahead of the real ESP page
(session f475e697: AccountSetup anchor 6.5 min early, 457 s misattributed to
`identity_hello`). `registry_app_baseline` successes are deliberately NOT a trigger — they
are by-design normal for Windows Device Preparation (DPP Batch-1 apps install before the
agent exists) and would starve that class's aggregates.

## ESP-blocking apps (positive evidence only)

Per-app intervals use EVENT timestamps (first started/download event → LAST terminal
event, covering IME retries) — never the agent payload timing, which freezes at the first
terminal transition. Blocking membership joins the app id against the session's latest
list-carrying `esp_config_detected` emission: listed ⇒ blocking; absent ⇒ unknown, never
false (`BlockingSetUnknown` / `BlockingSetTruncated` flags). Critical-path occupancy =
overlap-merged union of blocking intervals clipped to the `esp_apps` spans;
`EspAppsOccupancySeconds` is null (unknown) when no lists were observed.

The fleet what-if bound per app X is `max(0, cpEnd − cpEndWithoutX)` (latest interval end
with vs. without X; the idle gap before X counts — the ESP also waits for X to start).
It is an upper bound by construction; every consumer says "up to", never "you will save".

## Reboots

`system_reboot_detected` is detection-time-stamped by the NEXT agent run; the real boot
moment is its `lastBootUtc` payload. Reboot spans are therefore the event-stream gap
bracketing `lastBootUtc`, deduped per boot, clipped to the observation windows, and carried
as a cross-cutting annotation (they overlap the segment they started in — never a slice of
the partition).

## Persistence & compute

* `SessionTimeBreakdowns` (PK = TenantId, RK = SessionId): spans, reboot spans, blocking
  intervals (top 20 + uncapped count), quality flags, `AttributionVersion`. Written once at
  the session-terminal single-writer seam (`TableSessionRepository.UpdateSessionStatusAsync`,
  Succeeded/Failed only — Incomplete deliberately has no duration). Deleted with its session
  (deletion-manifest step) and on tenant offboarding.
* Self-healing 30-day maintenance sweep (2h tick): backfills missing breakdowns, folds
  late-terminating sessions into their StartedAt-date aggregates, and enforces version
  purity — a stale-version row whose events aged out counts as missing, never mixed.
  Reads the tick's **shared window scan** (`MaintenanceService.LoadSweepWindowSessionsAsync`,
  one projected 35-day cross-tenant drain via `MaintenanceSweepSessionProjection` that
  also feeds the device-journey and verdict-calibration sweeps) — the StartedAt-only
  filter is a full-table read in Table Storage, so it is done once per tick, not once per
  sweep; `MaintenanceSweepProjectionEquivalenceTests` pins the column set.
* `TimeAttributionAggregates` (PK = TenantId or `global`, RK = `{yyyy-MM-dd}|{class}` and
  `rolling30|{class}`): per enrollment class (`user_driven`, `whiteglove`, `self_deploying`,
  `device_preparation` — never mixed) median/p75/p90 per segment over the fixed six-segment
  stack (incl. `unattributed`; a session without a segment contributes 0), plus top blocking
  apps (≥5 sessions per app row). Clean/flagged/missing counts are part of every row; rows
  exist below the ≥20 UI gate on purpose (the UI needs the n for "insufficient data (n=…)").
  Daily rows retain 180 days; `rolling30` rows are refreshed whole (a median of per-day
  medians is not the range median — the rolling rows are the honest range statistics).

## Surfaces

* `GET api/sessions/{sessionId}/time-attribution` (MemberRead + tenant query param) —
  breakdown row or `breakdown: null` (pre-feature / non-terminal / Incomplete = no lane).
* `GET api/metrics/time-attribution` (MemberRead) / `GET api/global/metrics/time-attribution`
  (GlobalReadOrAdmin, optional `tenantId`) — `{ windowDays, classes (rolling), daily }`.
* Web: attribution lane under the session-detail phase timeline; "Time attribution" section
  on Fleet Health. MCP: `get_time_attribution` (sessionId mode / fleet mode).

# Examples

A 30-minute user-driven session: `device_prep` 5m → `esp_apps` 10m (occupancy 4m by one
blocking app) → `identity_hello` 8m → `user_esp` 5m → `desktop_handoff` 2m, unattributed 0.
A WhiteGlove session with a 2-day pause reports wall clock = part1 + part2 only; the resume
gap before the first part-2 phase declaration is unattributed, not guessed.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Helpers/TimeAttributionCalculator.cs` — calculator + semantics (AttributionVersion 2).
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.TimeAttribution.cs` — persistence.
* `src/Backend/AutopilotMonitor.Functions/Services/MaintenanceService.Aggregation.cs` — sweep + aggregation.
* `src/Backend/AutopilotMonitor.Functions.Tests/TimeAttributionCalculatorTests.cs` — golden fixtures pinning the invariant.
* `tasks/insights-expansion-spec.md` — F1 specification and source-data audit (§0.5).
