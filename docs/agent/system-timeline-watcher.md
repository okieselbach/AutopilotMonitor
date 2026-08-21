---
type: Concept
title: System Timeline Watcher — clock steps and sleep episodes from the System event log
description: How the agent turns two enrollment blind spots into timeline ground truth via one EventLogWatcher on the System channel — system_clock_changed (Kernel-General 1, oldTime/newTime/timeDeltaMs/process) and system_sleep_episode (Power-Troubleshooter 1 classic sleep/hibernate, Kernel-Power 507 Modern Standby) — including backfill of pre-agent records, the payload-times-are-authoritative rule, SleepSpans in time attribution, the web clock-set jump cause, and ANALYZE-DEV-012.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/SystemTimelineTracker.cs
tags:
  - agent
  - eventlog
  - time
  - clock-skew
  - standby
  - collector
  - time-attribution
  - analyze-rules
timestamp: 2026-08-21T00:00:00+02:00
---

# System Timeline Watcher

Two things silently distort every session timeline and its wall-clock duration:

1. **The OS clock gets stepped** — classically w32time correcting a wrong BIOS clock early
   in OOBE, but also tzutil/MDM actions. Before this watcher the jump was only *inferred*
   (TimeJumpBadge, `clock_skew`/DEV-008); Windows records the ground truth.
2. **The device sleeps mid-enrollment** — the timeline shows an unexplained gap, the
   duration looks inflated, and nothing says "the device was simply asleep for 56 minutes".

`SystemTimelineTracker` (hosted by `SystemTimelineWatcherHost`, config flag
`Collectors.EnableSystemTimelineWatcher`, ConfigVersion 39) subscribes to the **System**
channel with one provider-qualified XPath and forwards both facts as events.

# Sources and events

| Source (System channel) | Emitted event | Key payload |
|---|---|---|
| `Microsoft-Windows-Kernel-General` EventID 1 | `system_clock_changed` | `oldTime`, `newTime` (ISO-8601), signed `timeDeltaMs`, `reason`/`reasonText` (1=`application_set`, 2=`hardware_clock_sync`), `processName`/`processId` |
| `Microsoft-Windows-Power-Troubleshooter` EventID 1 | `system_sleep_episode` | `kind`=`sleep`/`hibernate` (EffectiveState 5 ⇒ hibernate), `enteredAt`=SleepTime, `exitedAt`=WakeTime, `durationSeconds`, `wakeSourceType`/`wakeSourceText` |
| `Microsoft-Windows-Kernel-Power` EventID 507 | `system_sleep_episode` | `kind`=`modern_standby`, `exitedAt`=TimeCreated, `enteredAt`=exit − DurationInUs, `durationSeconds` (scenario), `sleepDurationSeconds` (real sleep), `reason`, `onAcPower`, `batteryRemainingCapacityOnExit` |

**506 is deliberately not consumed.** 507 carries the full episode; pairing 506/507 would
need an orphan-tolerant state machine across agent restarts for zero information gain — an
episode without a 507 never completed and would not be emitted under any design.

# Thresholds (constants, not knobs)

- `MinClockDeltaMs = 2000` — w32time logs Kernel-General 1 for routine **1 ms micro-slews**
  (verified live, several per hour); only genuine steps are emitted. Suppressed records are
  still claimed by the dedup watermark.
- `ClockDeltaWarningMs = 5 min` — larger steps are Warning + immediate upload (can break
  token/cert validation windows; matches the web TimeJumpBadge threshold).
- `MinSleepDurationSeconds = 60` — **Modern Standby only**: every screen-off scenario logs a
  506/507 pair, including seconds-long ones with `SleepDurationInUs 0` (`SleepEntered=false`);
  the single duration gate suppresses both. Classic Power-Troubleshooter episodes have no
  floor — they only exist for genuinely completed S3/S4 transitions.

# Payload times are authoritative

The decisive clock correction happens exactly when the device clock is untrustworthy, and
the backend clamps implausible event timestamps (±168 h/24 h) to server time. Every semantic
instant therefore travels as an explicit ISO-8601 payload field (`oldTime`/`newTime`,
`enteredAt`/`exitedAt`); consumers must never read the event's own `Timestamp` for these.
The event `Timestamp` is set to the record's TimeCreated (WindowsUpdateTracker backfill
convention) so rows land where the wake/step happened.

# Backfill and dedup

Template: `WindowsUpdateTracker`. `LoadWatermark → arm watcher → backfill` with lookback
`SystemEventBackfillLookbackMinutes` (default 1440 = 24 h; 0 disables). The System .evtx
persists across OOBE reboots, so the pre-agent w32time correction and pre-agent standby
episodes are recovered with `backfilled=true`. Cross-restart dedup via RecordId watermark
(`system-timeline-watermark.json` in the state directory) plus an intra-run `HashSet` —
deliberately **not** a high-water mark, which would drop exactly the older, never-emitted
pre-agent records (the live watcher arms before the backfill scan).

# Non-activity classification (both sides)

Both event types are environment observation, not enrollment progress:
`SignalActivityClassifier.NonActivityEventTypes` (agent — must not reset idle/stall clocks)
and `EventIngestProcessor.IsPeriodicOrStallEvent` (backend — a device that slept through its
stall must stay Stalled).

# Consumers

- **Web timeline** (`EventTimeline.tsx`): badge blocks for both events; the TimeJumpBadge
  gains the ground-truth cause `clock-set` — `classifyTimeJump` receives the session's
  `system_clock_changed` deltas and matches a backward display step against a backward clock
  set within max(60 s, 20 %) (`lib/timeProvenance.ts`).
- **Duration story** (`SessionInfoCard` via `useSessionDerivedData.standbySeconds`): the
  wall-clock duration deliberately keeps the pause (the enrollment really took that long);
  a `· 🌙 56m standby` note explains it. Episodes are dedup'd on `enteredAt`.
- **Time attribution** (`TimeAttributionCalculator.BuildSleepSpans`, AttributionVersion 3):
  `SleepSpan`s mirror `RebootSpan`s — cross-cutting annotations clipped to the observation
  windows (a WhiteGlove-pause episode contributes only its in-window flanks), never a slice
  of the wall-clock partition. Persisted as `SleepSeconds`/`SleepSpansJson`; rendered as the
  standby chip in `TimeAttributionLane`.
- **ANALYZE-DEV-012** (info, `trigger: single`, `on_event:system_sleep_episode`): fires once
  per session for episodes ≥ 5 min. Deliberately the ONLY new rule: clock issues already
  have DEV-008 (`clock_skew`) — a second clock rule would double-report the same action item
  (user decision 2026-08-21); clock steps stay timeline ground truth with badges instead.

# Citations

- `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/SystemTimelineTracker.cs`
- `src/Agent/AutopilotMonitor.Agent.V2.Core/Orchestration/Hosts/SystemTimelineWatcherHost.cs`
- `src/Backend/AutopilotMonitor.Functions/Helpers/TimeAttributionCalculator.cs` (`BuildSleepSpans`)
- `src/Web/autopilot-monitor-web/lib/timeProvenance.ts` (`clock-set`, `findExplainingClockChange`)
- `rules/analyze/ANALYZE-DEV-012.json`
- Live payload evidence (2026-08-21, Win11 26220): Kernel-General 1 with `TimeDeltaInMs=1`
  micro-slews; Kernel-Power 507 with `SleepDurationInUs=2324095807`; Power-Troubleshooter 1
  with `SleepTime`/`WakeTime` across a hibernate.
