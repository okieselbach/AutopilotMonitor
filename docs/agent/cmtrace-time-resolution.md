---
type: concept
title: CMTrace Time Resolution — Per-Line Self-Anchoring
description: How the agent converts bias-less CMTrace local times to UTC without trusting any process's timezone belief — each provably fresh line anchors its own offset against the agent clock on the 15-minute grid; everything not provably fresh falls back uniformly and says so.
resource: src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime
tags: [agent, ime, cmtrace, timestamps, timezone, calibration, provenance]
timestamp: 2026-09-01
---

# Problem

A CMTrace line carries local time and no offset — IME 1.104 writes `DateTime.Now.TimeOfDay`
in both of its trace listeners. Converting such a line with the reader's own
`TimeZoneInfo.Local` silently assumes the writing and reading processes agree on the zone.
They do not: each caches its zone at process start and follows neither a later `tzutil` nor
a Windows auto-timezone change. The error is `Offset_writer − Offset_reader` — zero only
when both believe the same thing, right or wrong. Field measurement over 11,068 sessions
found 26 sessions with a real error, from +1 h to −17 h.

Two designs failed before this one:

* **Fix the reader's belief** (`TimeZoneInfo.ClearCachedData`, live OS offset): makes the
  agent isolated-correct and thereby breaks the majority of sessions, where two equally
  stale beliefs cancel.
* **One measured offset per file** (agent 2.0.1410, reverted in `04b1a7c6`): a single log
  file holds lines from multiple writer *eras*. `AgentExecutor.log` is written by
  short-lived child processes whose zone belief flips per process — interleaved in one
  file, and flipping *back* (fixture:
  `tests/fixtures/cmtrace-logs/agentexecutor-two-writer-eras-v1.cmtrace`). Any cross-line
  anchor — per file, or nearest-in-write-order — applies one era's offset to the other
  era's lines. In the field this shifted `script_started` events by −9 h against their
  correct completions.

# Schema

The resolution rule, in order:

1. **Writer-declared bias** (`+480` suffix): authoritative, used as-is. Never a calibration
   anchor — it needs no measurement and must not overwrite one.
2. **Per-line self-anchoring** — only for lines read in a *provably fresh* pass:

   ```
   offset_line = round((local − agentUtcNow) / 15 min) · 15 min
   lineUtc     = local − offset_line
   ```

   Every real UTC offset is a whole multiple of 15 minutes, so the rounding absorbs poll
   and flush latency. No cross-line state exists, so interleaved eras resolve line by
   line — the property the fixture test pins. The line's own timestamp still contributes
   sub-poll precision and ordering (two lines 6 ms apart stay 6 ms apart; a plain
   `occurredAt = now` could not do that). Guards: grid residual ≤ 2 min, offset within
   UTC−12 … UTC+14.
3. **Uniform reader-zone fallback** for everything else: wrong in absolute terms whenever
   the writer held a different belief, but *self-consistent* — both ends of a duration are
   wrong by the same amount, so derived durations stay right. The revert's core lesson:
   partially corrected is strictly worse than uniformly wrong.

**Freshness** is what makes rule 2 sound, and it is strict:

* Every poll look at a file stamps `LastCheckedUtc` (`LogFilePositionTracker.MarkChecked`),
  including "no new data" and an empty first sight — so a file first seen empty
  (`AgentExecutor.log` appearing mid-enrollment) anchors from its very first content.
* A pass's lines are fresh only if the file was previously observed *in this process* and
  the gap since the last look is ≤ `FreshLineMaxAge` (30 s — far above the 100 ms poll,
  far below the 13-minute zone where a line's *age* could round onto the offset grid).
* A restart-restored position bookmark deliberately carries **no** freshness: the first
  pass after a restart reads downtime backlog and must never anchor.

**Provenance** (`sourceOffsetOrigin` in event DataJson): `bias` | `line-anchored` |
`reader-zone-fallback` (plus retired `calibrated` from 2.0.1410 events). The applied
offset is always recorded (`sourceOffsetMinutes`), the raw local time too
(`sourceLocalTs`), so any event can be re-derived later. The per-*file* measurement still
runs, purely observationally (`measuredWriterOffsetMinutes`, Info log when it disagrees
with the process zone).

# Constraints

* An anchored line resolves to `now ± 2 min` *by construction*. The accepted edge: a
  freshly written line carrying a **replayed** old timestamp whose age is exactly
  N×15 min ± 2 min anchors to ≈now instead of its replayed past — bounded error, and
  sub-24 h replays are treated as current by the historic-replay guard anyway.
* Snapshot readers (`StallProbeCollector`, `LogParserCollector`) cannot tail and therefore
  cannot prove freshness — they stay on the marked fallback. This is a documented limit,
  not an omission; sharing the per-file measurement with them would re-apply exactly what
  the revert removed.
* Mixed pairs remain possible across a restart (start resolved from backlog fallback,
  completion line-anchored) and are recognizable via provenance.

# Line parsing (bounded cost)

`CmTraceLogParser.TryParseLine` (Shared) is the single CMTrace line parser for the agent
(`ImeLogTracker`, `LogParserCollector`, `StallProbeCollector`) and the backend
(`TestLogPatternFunction`). Both feed it input an outsider influences: tenant-supplied
sample lines (200 x 8 KB per request) on the backend, and on the agent every tailed log
file, including entries that IME assembles from installer or script output. It is
therefore not one regex. The former shape — a greedy `(?<message>.*)` in front of the
`]LOG]!><time="` literal, unanchored, no match timeout — re-tried every `<![LOG[`
occurrence as a fresh start and backtracked the message group across every later character
for each: quadratic in line length (an 8 KB line of repeated prefixes and `]` filler cost
~7 million engine steps; a 1 MB assembled entry would have spun the SYSTEM agent for
hours and blacked out IME tracking for the rest of the enrollment).

The parser reproduces exactly what that regex returned, in linear time: the message begins
after the first `<![LOG[` (a later start could only match when the first one does, since
`.*` spans the difference) and ends at the last `]LOG]!><time="` occurrence whose trailer
parses — earlier occurrences are tried in turn, which is the order greedy backtracking
visited them. The trailer is checked with a `\G`-anchored regex that has no ambiguous
quantifier and a 1-second match timeout as a backstop; a timeout means "not a CMTrace
line" (callers already have that path: raw-text matching in the tracker, `parse_failed`
on the backend), never an exception. `CmTraceLogParserTests` pins the equivalence against
the legacy regex over the shapes that matter (multiline messages, bias suffix, trailer
literal inside the message, a last trailer that fails to parse, BOM prefix) and the
reported worst case at 200 lines.

On the agent, `ImeLogTracker` additionally caps an assembled multiline entry at 100 raw
lines **and** 1 MB of characters (`MaxMultiLineBufferChars`). A capped entry is dropped
and logged at **Warning** — visible at the default `Info` level, so a real IME entry that
large shows up in the client log and can be acted on. The remaining physical lines of a
dropped entry are skipped until it closes or a new `<![LOG[` entry begins; previously they
fell through to raw-text pattern matching, so fragments of a discarded entry could fire
patterns without CMTrace context.

# Backend regression tripwire

The backend watches for the failure mode this design eliminates. On every terminal ingest
batch, the counter reconcile's existing event-partition scan additionally collects
`Δ = ReceivedAt − OccurredUtc` per event, split by `Source == "ImeLogTracker"` versus the
rest (`CmTraceSkewTripwire`, hooked in `EventIngestProcessor`). If
`median(Δ_IME) − median(Δ_other)` is a clean 15-minute multiple (residual < 2 min — the
same constants as the anchoring grid guard above) with ≥ 20 samples over ≥ 3 distinct
upload batches per side, and ≥ 80 % of the individual IME deltas (relative to the other
side's median) themselves sit within 2 min of some grid multiple — added after the 2026-08-28
soak, when relaunched agents re-tailing the IME log produced one burst holding most of a
session's IME samples with a continuum of ages whose median hit the grid by chance (7 false
fires in a week, 0 true) — a `CmTraceTimeSkewRegression` ops event fires (category Agent,
warning), carrying all numbers plus the session's `sourceOffsetOrigin` histogram.
Bias-dominated sessions are suppressed (writer-declared offsets cannot be an anchoring
regression); `measuredWriterOffsetMinutes` is never consulted (sticky after era
flip-backs). Kill switch: app setting `CmTraceSkewTripwireDisabled`. Goal state: the event
never fires — any hit is an anchoring case the per-line design misses, or a detector bug.

Samples are windowed to the session's most recent INGEST ERA before any of that is
computed: the batch stamps of both sides are walked backwards from the newest one and the
era ends at the first `ReceivedAt` gap wider than 2 h. A session partition is not one agent
run — a pre-provisioning session's Part 1 is written by whatever agent build was current
weeks earlier, and its events stay in the partition forever. Field 2026-09-01 (sessions
`e797117b` / `c06d639d` / `d7c8032b`, one tenant): 26 IME samples at exactly −60 min from a
2026-08-20 technician leg under agent 2.0.1409 — before per-line anchoring — outnumbered the
3…9 clean samples of the user leg that completed that morning and fired the tripwire against
2.0.1445, a build the device had self-updated to minutes earlier. The skew was real, 12 days
old, and already fixed; no scan-wide statistic can tell those apart, because event rows carry
no agent version. The era boundary can. `eraStartUtc` and the excluded per-side sample counts
travel in the ops event, so an inherited leg stays visible without being alarmed on. The
2 h threshold sits above any in-leg upload gap (a live agent uploads self-metrics every few
minutes, reports `session_stalled` at 60 min idle, and the maintenance sweep classifies
silence at 2 h) and below every pre-provisioning handover.

Device-clock problems (the customer-actionable cousin) are covered separately by the
`clock_skew` analyze condition behind `ANALYZE-DEV-008`, which excludes IME-derived events
precisely so an anchoring regression can never surface as a customer finding.

# Citations

* `CmTraceOffsetCalibrator.TryMeasureOffset` — the pure grid measurement, shared by
  anchoring and the observational per-file path.
* `CmTraceSkewTripwire` + `CmTraceSkewTripwireTests` — the backend tripwire above.
* `RuleEngine.ConditionEvaluators.EvaluateClockSkewCondition` + `RuleEngineClockSkewTests`
  — the customer-facing device-clock metrics (batch-median frames, spool-spread filter,
  plateau step detection, end-state persistence).
* `ImeLogTracker.LogProcessing.cs` `ResolveEntryUtc` — the resolution order above.
* `ImeLogTrackerLineAnchoringTests` — resolves-to-T across writer beliefs, interleaved
  eras in one chunk, freshness boundaries (first sight, empty first sight, poll gap,
  restart restore), the grid-replay edge, and the field fixture replayed at production
  poll cadence.
* `CmTraceCalibratorFileIsolationTests` — pins per-file isolation of the observational
  measurement (session e9753578 logged a cross-file pairing the committed code could not
  be shown to produce; the mechanism remains open, the resolution path is immune by
  design, and `CalibrateFrom` logs the anchor timestamp as a tripwire).
* `tasks/todo.md` (2026-08-20) — full evidence trail: field measurements, the revert,
  the two-writer-era fixture, and the disproven foreign-binary hypothesis (the SHA
  mismatch was the known release race against the 5-minute AdminConfiguration cache).
* `CmTraceLogParser.TryParseLine` + `CmTraceLogParserTests` — the linear parser and its
  legacy-regex equivalence oracle; `ImeLogTracker.LogProcessing.cs` multiline buffering
  (`MaxMultiLineBufferLines` / `MaxMultiLineBufferChars`, `skippingDroppedEntry`).
