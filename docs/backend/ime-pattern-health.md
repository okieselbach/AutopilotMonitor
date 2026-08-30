---
type: concept
title: IME Pattern Health — the pattern-drift loop
description: How a Microsoft change to IME log wording becomes visible before it breaks timelines — the agent's session-end pattern histogram, the global per-IME-version statistics, the data-driven drift alarm, and the operator page/MCP tool that turn the alarm into a repair workflow.
resource: src/Backend/AutopilotMonitor.Functions/Services/Ime
tags: [backend, agent, ime, log-patterns, drift, ops-events, metrics, mcp]
timestamp: 2026-08-30
---

# Problem

The IME log-pattern pack is the product's core signal source, and Microsoft changes IME log
wording without notice. Until now a changed line was invisible until sessions looked wrong:
the agent kept matching nothing, the client log said nothing, and the only alarm was the
registry/log reconciliation for app states. Tracker problems (skipped lines, regex timeouts)
were a client-log Warning nobody sees without pulling diagnostics.

# Design

Three layers, one loop:

## 1. Tracker health in `agent_metrics_snapshot`

`ImeLogTracker` keeps cumulative counters (`ImeTrackerHealth`: lines read, entries matched,
oversized lines, regex timeouts, per-line budget breaks, held tails, unanchored patterns,
files tailed, backlog bytes = Σ file length − bookmark). `AgentSelfMetricsCollector` reads them
through a read-only probe (`ImeLogHost.GetTrackerHealth`) and adds `ime_*` keys to the periodic
snapshot next to the spool queue. Fleet expectation for every skip counter: **0**. The session
page's "IME Tracker" section shows queue depth and skips.

## 2. `ime_tracker_degraded` (Warning, one-shot, immediate upload)

The first pass that skipped work raises `ImeLogTracker.OnTrackerDegraded`; the adapter emits
the event with the counters, file and first skipped pattern. Persisted flag (`ImeTrackerState`)
so a restart cannot re-emit. Listed in the MCP `health_events` catalog; informational for the
idle/stall clocks (`SignalActivityClassifier`).

## 3. `ime_pattern_hits` → `ImePatternStats` → `ImePatternDriftSuspected`

* **Agent.** At termination (same slot as `app_tracking_summary`, so WhiteGlove Part 2 reports
  the whole session via the persisted histogram) the handler emits `ime_pattern_hits`:
  `hits` = every ENABLED pattern ID → match count, zeros included, plus the health counters and
  `imeVersion` (IME's own "Agent version is:" line). Only sessions with a terminal run report —
  crashes/kills are outside the denominator by construction.
* **Ingest.** `EventIngestProcessor` hands the event to `ImePatternHealthService` fire-and-forget:
  IDs are filtered against `BuiltInImeLogPatterns.BuiltInPatternIds` (a device may only claim an
  ID; tenant custom IDs never reach the global table), the version must pass
  `ImeMsiArchiver.IsPlausibleVersion` (event value first, session row as fallback), then one
  partition read + one transactional batch upsert into `ImePatternStats`
  (PK = version, RK = patternId: `Sessions`, `SessionsWithHit`, `Hits`, `LastHitAt`,
  `DriftFlaggedAt`). Permanent table (`TableLifecycleBucketTests` KeptByDesign) — the baseline
  needs the established versions.
* **Drift statistic (`ImePatternDriftEvaluator`, pure).** No hard-coded must-hit list.
  Baseline = the version with the most reporting sessions (≥ 100, never the candidate).
  A pattern is *expected* when its baseline hit rate (`SessionsWithHit / Sessions`) is ≥ 0.8.
  Drift is suspected when the candidate has ≥ 25 reporting sessions and an expected pattern
  matched in none of them. Conditional patterns (platform/remediation scripts, WinGet, errors)
  sit below 0.8 on the baseline and cannot alarm. One alarm per version × pattern:
  `TryMarkImePatternDriftFlaggedAsync` (ETag-conditional) then
  `OpsEventService.RecordImePatternDriftSuspectedAsync` (Warning, Agent, `System.Ingest`;
  dual-registered in the web `OPS_EVENT_TYPES`). The stats snapshot used for evaluation is
  cached 10 minutes and patched with each batch, so a version crossing the threshold is judged
  promptly.
* **Read side.** `GET metrics/ime-pattern-health` (`GlobalReadOrAdmin`) returns baseline,
  thresholds, versions (with `ImeVersionHistory` first/last seen and fleet session count),
  patterns (catalog + retired IDs still in the statistics), cells and open alerts. Web:
  Global Admin → Metrics → IME Pattern Health (matrix, legend: drift / silent / low / few).
  MCP: `get_ime_pattern_health` (GA + Global Reader).

# Workflow on an alert

1. `ImePatternDriftSuspected` names version + pattern; the matrix shows the cell.
2. `search_sessions imeAgentVersion=<version>` → `get_session_diagnostics` on a session with a
   package (upload mode must not be Off).
3. `/ime-pattern-validate` against the real log; `/ime-decompile` for the version's sources.
4. Fix the pattern under `rules/ime-log-patterns/` (anchored, linear —
   [IME Log Tracker](../agent/ime-log-tracker-matching-budget.md)); Backend-Deploy delivers it
   through the config channel without an agent release.

# Limits

* Denominator = sessions that reached a terminal run; a version that only crashes never gets
  a histogram and therefore never alarms here (it alarms elsewhere).
* The baseline is fleet-wide: a pattern that only some tenants can produce stays "conditional"
  even if it is 100 % within those tenants.
* Thresholds are constants (`ImePatternDriftEvaluator`), tuned for the current fleet size
  (~25k sessions/month); revisit when versions roll out faster than 25 sessions per day.

# Citations

* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/ImeTrackerHealth.cs`, `ImeLogTracker.cs` (`GetHealthSnapshot`, `OnTrackerDegraded`), `ImeTrackerStatePersistence.cs`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Termination/EnrollmentTerminationHandler.cs` (`EmitImePatternHits`)
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Periodic/AgentSelfMetricsCollector.cs`
* `src/Backend/AutopilotMonitor.Functions/Services/Ime/{ImePatternHealthService,ImePatternDriftEvaluator}.cs`
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.ImePatternStats.cs`
* `src/Backend/AutopilotMonitor.Functions/Functions/Metrics/GetImePatternHealthFunction.cs`
* `src/Web/autopilot-monitor-web/app/admin/metrics/sections/SectionImePatternHealth.tsx`, `imePatternHealthLogic.ts`
* `src/McpServer/autopilot-monitor-mcp/src/tools/sessions.ts` (`get_ime_pattern_health`)
