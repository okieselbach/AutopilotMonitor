---
type: Concept
title: Device Journeys & First-Time-Right (F2)
description: How a device's terminal enrollment attempts are grouped into journeys (device key, junk-serial exclusion, first-success end, 30-day gap), where the DeviceHistories chain and the daily FTR aggregates are computed, stored and pruned, and which surfaces serve them.
resource: src/Backend/AutopilotMonitor.Functions/Helpers/DeviceJourneyCalculator.cs
tags:
  - backend
  - metrics
  - device-history
  - first-time-right
  - insights
timestamp: 2026-07-27T12:00:00+02:00
---

# Device Journeys & First-Time-Right (F2)

Answers "how many devices enrolled right on the first try?" and "show me every attempt
this device made". Session success rate hides wipe-and-retry loops — every retry that
eventually succeeds counts as a success; First-Time-Right (FTR) exposes the real pain.
All grouping semantics live server-side in `DeviceJourneyCalculator` (JourneyVersion 1);
no consumer re-derives them.

# Schema

## Device key

`(TenantId, normalized serial)` — normalization is trim + lower-case. Junk/placeholder
serials are excluded entirely (never a chain, never in FTR, disclosed as a per-day
exclusion count): `System Serial Number`, `To Be Filled By O.E.M.`, `Default string`,
`0`, `None`, `INVALID`, `Unknown` (the agent's WMI-failure sentinel), empty, or shorter
than 4 characters. A motherboard swap changes the serial and therefore the device key;
re-enrollment under a different tenant is a separate key by design.

## Journey

Terminal sessions of a device key ordered by StartedAt. The terminal set is
**Succeeded / Failed / Incomplete** — `Pending` (a sealed WhiteGlove part 1),
`AwaitingUser`, `Stalled` and `InProgress` are OPEN sessions and never attempts.

* A journey **ends** with the first terminal success; the next session starts a new
  journey (redeployment).
* A gap of **more than 30 days** since the previous attempt's terminal moment
  (`CompletedAt`, fallback StartedAt) starts a new journey even without a success
  (device shelved/repurposed). Constant, not a setting — production gaps: median 4.9 h,
  92 % < 7 d.
* WhiteGlove part 1 + part 2 share ONE session row, so a completed WG enrollment is ONE
  attempt; a WG device still waiting for its user session leaves the journey open —
  never a failed attempt.
* **Attempt count** = terminal sessions in the journey (`Failed` and `Incomplete` are
  non-successful attempts).
* **FTR rate** = completed journeys with attempt count 1 ÷ all completed journeys.
  Open and gap-abandoned journeys never enter numerator or denominator.

Durations shown for attempts are the sessions' authoritative `DurationSeconds` verbatim
(WhiteGlove pause excluded; `Incomplete` deliberately has none) — never
`CompletedAt − StartedAt`, which is later in ~25 % of terminal sessions.

## Persistence & compute

* `DeviceHistories` (PK = TenantId, RK = percent-encoded normalized serial): the chain of
  the device's terminal session refs `{sessionId, startedAt, completedAt, status,
  enrollmentType, isPreProvisioned, durationSeconds, adminMarked}` capped at the 20 most
  recent, plus derived `CurrentJourneyAttempts` (the LAST journey's attempts — the
  banner's "Attempt N"), `JourneyCount` (chain-scoped) and `JourneyVersion`. Written
  inline at the session-terminal single-writer seam
  (`TableSessionRepository.UpdateSessionStatusAsync`, all three terminal statuses,
  DeletionState-guarded) so the session banner is fresh; healed by the maintenance sweep.
  Deliberately NOT part of the per-session deletion manifest — the row aggregates many
  sessions. Wiped on tenant offboarding.
* Sweep (`SweepDeviceJourneysAsync`, 30-day rolling window, 2h tick): (1) drops chain
  refs of deleted sessions **tombstone-driven** — every session deletion (cascade and
  retention both run through the cascade worker) leaves a `SessionTombstones` marker for
  ~7 days, far longer than the sweep cadence, and even an expired-but-unpruned marker is
  valid deletion evidence; a chain left empty deletes its row. (2) Merges the window's
  terminal sessions into their chains (backfill, inline-miss healing, re-terminal
  reclassifications). (3) Recomputes the daily FTR aggregates idempotently.
* `DeviceJourneyAggregates` (PK = TenantId or `global`, RK = `{yyyy-MM-dd}`): per day —
  completed journeys, first-time-right count, attempt histogram, junk-serial exclusion
  count, `JourneyVersion`. A journey buckets on the StartedAt date of its completing
  success session (the platform's StartedAt-date convention; late terminals converge via
  the sweep). All counts are **additive**, so a window rate is the ratio of summed daily
  rows — there is no rolling-window row (unlike the median-based time-attribution
  aggregates, which cannot merge). 180-day retention. Rows exist below the ≥20 UI gate
  on purpose (the UI needs the n for "insufficient data (n=…)").

# Surfaces

* `GET api/metrics/device-history?serialNumber=&sessionId=` (MemberRead + tenant query
  param — the same route serves the web banner and MCP; a Global Admin passes `tenantId`)
  — the chain plus, with `sessionId`, that session's server-computed attempt number
  (live sessions get their would-be position via a virtual non-successful attempt, so the
  redeploy and gap rules apply). `history: null` = no recorded history.
* `GET api/metrics/device-journeys?days=` (MemberRead) /
  `GET api/global/metrics/device-journeys?days=&tenantId=` (GlobalReadOrAdmin) —
  `{ windowDays, totals, daily, repeatDevices }`. The days selector is honored because
  counts are additive. Without `tenantId` the global variant serves the cross-tenant
  aggregate rows and `repeatDevices: null` — a per-device list would require scanning
  every tenant's partition.
* Repeat devices (violator list, capped 10): devices whose LAST journey took ≥2 attempts
  and whose newest terminal session falls inside the window, ordered by attempts then
  recency; the failure reason comes from the newest failed attempt (bounded point-reads).
* Web: "Attempt N for this device · View history" banner on session detail (expandable
  chain); "First-time-right" section on Fleet Health (rate + weekly trend + attempt
  histogram + repeat devices). MCP: `get_device_history` (serial mode / fleet FTR mode).

# Examples

A device fails ESP twice and succeeds on the third try within one week: one journey,
three attempts, completed — counts once in the FTR denominator, not in the numerator,
and the histogram's bucket `3` gains one. The same device redeployed a month later and
succeeding immediately starts journey 2 with attempt count 1 — first-time-right. A
device with `serialNumber = "Unknown"` (WMI failure) never gets a chain; its terminal
sessions surface only as that day's disclosed exclusion count.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Helpers/DeviceJourneyCalculator.cs` — device key, junk list, grouping, attempt numbers (JourneyVersion 1).
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.DeviceHistory.cs` — persistence + inline seam entry.
* `src/Backend/AutopilotMonitor.Functions/Services/MaintenanceService.DeviceJourneys.cs` — sweep, tombstone pruning, daily aggregation.
* `src/Backend/AutopilotMonitor.Functions/Functions/Metrics/GetDeviceJourneyFunctions.cs` — read surfaces.
* `src/Backend/AutopilotMonitor.Functions.Tests/DeviceJourneyAndFtrTests.cs` — table-driven grouping/aggregation/attempt-number tests.
* `tasks/insights-expansion-spec.md` — F2 specification and source-data audit (§0.5).
