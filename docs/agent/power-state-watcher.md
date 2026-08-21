---
type: Concept
title: Power-State Watcher — live AC/battery transitions and threshold events during enrollment
description: How the agent observes AC↔battery switches and downward battery-threshold crossings mid-enrollment via a WMI push subscription (no polling), the power_state_change event semantics (transition field, 50/30/15 ladder, latch/debounce/cap), and the two analyze rules on top (ANALYZE-DEV-009/-010).
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Orchestration/Hosts/PowerStateWatcherHost.cs
tags:
  - agent
  - power
  - battery
  - collector
  - wmi
  - analyze-rules
timestamp: 2026-08-21T00:00:00+02:00
---

# Power-State Watcher

The startup probe (`power_state_check`, one-shot from `StartupEnvironmentProbes` via
`PowerStateProbe`/`GetSystemPowerStatus`) only captures the power state at agent start. A
device unplugged mid-enrollment — or draining its battery across a multi-hour ESP — was
invisible. The watcher closes that gap **event-driven, with zero polling**.

# Mechanism

- `PowerStateWatcherHost` (`Orchestration/Hosts`) subscribes to the WMI push event
  `SELECT * FROM Win32_PowerManagementEvent WHERE EventCode = 10` (PBT_APMPOWERSTATUSCHANGE:
  fires on AC↔DC switches and battery-percentage steps, granularity set by the battery
  driver, typically 1–3%). The event carries no payload — each arrival re-probes via the
  existing `PowerStateProbe.Probe()` (cheap P/Invoke).
- Snapshots are diffed by the pure `PowerStateTransitionTracker`
  (`Monitoring/Enrollment/SystemSignals`), which decides what to emit. The host only owns
  WMI, a 5 s trailing-edge debounce timer (dock-flap AC→battery→AC inside the window
  collapses to a no-diff) and the emit path (`InformationalEventPost`, single rail).
- **No battery / probe error ⇒ WMI is never armed** — desktops and VMs pay nothing. WMI
  arming runs on a background thread (WinMgmt can hang in OOBE; `ConsoleBypassWatcher`
  precedent); an arm failure emits one `collector_degraded` (`watcher_arm_failed`).
- Registered as a plain always-on peripheral host after `NetworkChangeHost`, deliberately
  NOT under `PeriodicCollectorLifecycleHost`: it is push-based (zero idle cost) and must be
  alive precisely when the device idles on battery. Kill-switch:
  `Collectors.EnablePowerStateWatcher` (default true, compile-time default like the other
  collector toggles; ConfigVersion 38).

# Event: power_state_change

`data.transition` discriminates three shapes (payload always carries `onAcPower`,
`batteryPercent`, `isCharging`, `batteryLifeMinutes`; unknowns as `"unknown"`):

| transition | Severity | Immediate | Notes |
|---|---|---|---|
| `ac_to_battery` | Warning | yes | one per accepted edge |
| `battery_to_ac` | Info | no | recovery |
| `threshold_crossed` | Info (50) / Warning (30) / Error (15) | 30+15 only | extra field `thresholdPercent` |

Threshold semantics (tracker):

- Ladder 50/30/15, evaluated only while ON battery; **each level latches once per agent
  lifetime** (charge-up + re-drain does not re-emit — no plug-cycle alarm spam).
- A multi-level jump emits only the LOWEST newly-crossed level (60%→10% ⇒ one `15` event).
- Entering battery power already below a level counts as crossing it; the arm-time baseline
  does too — an enrollment STARTING at 12% battery emits its `thresholdPercent: 15` event
  immediately (the analyze rule would otherwise never see sessions that began low).
- Lifetime emission cap 20 (storm backstop against a flapping dock / dying battery
  controller); the host logs one warning when it engages.

Backend/agent hygiene: `power_state_change` is classified as non-activity on both sides —
`SignalActivityClassifier.NonActivityEventTypes` (agent: must not reset the
PeriodicCollectorLifecycleHost/StallProbeHost idle clocks) and
`EventIngestProcessor.IsPeriodicOrStallEvent` (backend: must not heal a Stalled session —
battery drain is not enrollment progress).

# Analyze Rules

- **ANALYZE-DEV-009** (high): battery crossed 15% while on battery — matches the
  agent-stamped `thresholdPercent equals 15`.
- **ANALYZE-DEV-010** (warning): AC→battery switch — matches `transition equals
  ac_to_battery`.
- Both: `evaluateOn: ["enrollment_end", "whiteglove_sealed", "on_event:power_state_change"]`
  (live interim finding + terminal finalization), `markSessionAsFailedDefault: false`.
- Both are deliberately **single-required-condition**: `event_data` conditions scan events
  independently (no same-instance join), so a two-condition rule (e.g. `transition ==
  threshold_crossed AND batteryPercent < 15`) could false-positive across two different
  power events. `thresholdPercent` exists only on threshold events and its value pins the
  ladder level, so one condition fully identifies the instance.
- `{{token}}` caveat: `AddDataFieldsToEvidence` whitelists only generic identifiers
  (appId/errorCode/…), so `{{batteryPercent}}` would render literally. DEV-009 interpolates
  `{{thresholdPercent}}` (the matched condition's own dataField); the exact percentage lives
  on the linked event in the timeline.

# Citations

- `src/Agent/AutopilotMonitor.Agent.V2.Core/Orchestration/Hosts/PowerStateWatcherHost.cs`
- `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/PowerStateTransitionTracker.cs`
- `src/Agent/AutopilotMonitor.Agent.V2.Core/Runtime/PowerStateProbe.cs` (probe, unchanged)
- `rules/analyze/ANALYZE-DEV-009.json`, `rules/analyze/ANALYZE-DEV-010.json`
- [analyze-rule-triggers](../rules/analyze-rule-triggers.md) (interim-trigger design; the WG battery rule was its motivating case)
