---
type: concept
title: IME Log Tracker — Anchored Patterns and the Bounded Read Loop
description: Why every shipped IME log pattern starts with '^', why the on-device matcher reads lines through a byte-bounded reader with a per-line matching budget, and what an operator sees when a hostile log file is tailed — the IME Logs folder is writable by standard users and the SYSTEM tracker must stay linear on whatever lands there.
resource: src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime
tags: [agent, ime, regex, redos, log-tracker, reliability, security]
timestamp: 2026-08-30
---

# Problem

`ImeLogTracker` tails every file in `%ProgramData%\Microsoft\IntuneManagementExtension\Logs`
whose name matches the IME log families and matches each entry against every active pattern of
the shipped pack (`rules/ime-log-patterns/*.json` → `rules/dist/ime-log-patterns.json`, embedded
in agent and backend, delivered live through the config channel). That folder is writable by a
standard user, so the input is untrusted: anyone signed in during enrollment can append lines.

Before 2026-08-30 three things multiplied:

* **Unanchored patterns with unbounded lazy gaps.** `\[Win32App\]\[EspManager\] … registered.*?App name: (?<name>.*?)$`
  re-scans its `.*?` tail at every offset where the literal prefix occurs; repeating the prefix
  *m* times in one line of *n* chars costs O(m·n). `IME-APP-VERSION` chained two lazy scans
  (`.*?"DetectedIdentityVersion":\{[^}]*?"NewValue"`) and was quadratic even when anchored.
* **Per-pattern timeout, no per-line budget.** Each regex had a 1 s timeout, the timeout was
  swallowed at Debug, and the loop tried all ~80 active patterns for every line — one crafted
  line concatenating several trigger prefixes cost several seconds of SYSTEM CPU.
* **Unbounded line length.** `StreamReader.ReadLineAsync` materializes the whole line before
  anyone can look at its size; a multi-hundred-MB line is an OutOfMemory in the poll loop, which
  retries from the same bookmark every second.

Genuine IME decision signals (app failures, enforcement states, ESP phases, user-session
completion) queue behind that work — a monitoring blackout during the enrollment window.

# Design

## Anchored, linear pattern pack

* **Every shipped pattern starts with `^`.** The tracker compiles with
  `IgnoreCase | Compiled | Singleline` and no `Multiline`, so `^` is the start of the matched
  string — `CmTraceLogEntry.Message`, i.e. the text after `<![LOG[` (for multiline entries the
  assembled entry, still offset 0). .NET treats a leading `^` as a beginning anchor: a
  non-matching pattern costs O(prefix), never a scan. Evidence for the invariant: across eight
  real diagnostics sets (~9,000 entries, all six log families) every match of the old pack began
  at offset 0 — except one class of mid-line hits that were false positives (below).
* **No chained unbounded scans.** `IME-APP-VERSION` now spells the report-delta JSON out
  (`\{(?:"OldValue":[^,}]{0,64},)?"NewValue":"`) instead of scanning `[^}]*?` at every
  occurrence. The test `ImeLogPatternPackLinearityTests` rejects `[^x]*` shapes outright.
* **Raw lines.** A line that fails CMTrace parsing (orphaned continuation lines, lines after a
  dropped entry) is still matched raw; with `^` it matches only if it *begins* with the pattern.
  Field data: the unanchored `PS-SCRIPT-CONTEXT` fired 122 times on
  `[Win32App] SideCarScriptDetectionManager Launch powershell executor in machine session`
  (Win32 detection scripts in AppWorkload.log), each time overwriting the pending *platform*
  script's `RunContext`. Anchoring removed that contamination; it is asserted by test.
* **Custom patterns are not rewritten.** Tenant-authored patterns (and cached pre-anchor
  configs) keep their semantics; `CompilePatterns` names the enabled patterns without `^` once
  at Info so a pass summary's budget breaks can be attributed.

## Bounded read loop (`CheckLogFilesAsync`)

* **`BoundedLineReader`** replaces `StreamReader`: byte-oriented, splits on `\n`, strips one
  `\r`, decodes UTF-8 per line, honours a BOM only at absolute file offset 0. A line beyond
  `MaxEntryBytes` (32 MB) is never materialized — the reader keeps the capped prefix, discards
  the rest while scanning for the terminator and flags `LastLineTruncated`. Its `Position` is
  the exact offset of the next unread byte, so bookmarks are exact (the StreamReader's decoder
  read-ahead used to lose up to a buffer of lines on a cancelled pass).
* **Why 32 MB.** Real logs carry single lines of ~110 KB routinely
  (`IntuneManagementExtension.log` policy dumps) and one large tenant's `AppWorkload.log`
  writes a 5.6 MB `Get policies = […]` line that `IME-POLICIES` legitimately captures (app
  names for the dashboard). A "few KB" cap would drop genuine entries; with anchored patterns
  the CPU cost of a large line is O(n) for the handful of patterns whose prefix matches, so the
  cap only has to bound memory. The same constant caps an assembled multiline entry.
* **Oversized lines are dropped, not matched.** Captures on a cut line would be wrong. If the
  capped prefix opens a CMTrace entry without closing it, the existing `skippingDroppedEntry`
  mechanism skips the entry's continuation lines instead of raw-matching them; a line that
  opens a *new* entry always ends the skip and is processed itself.
* **Per-line matching budget.** `MatchLine` runs all active patterns under a 2 s Stopwatch
  budget; a pattern that times out is skipped and counted, and once the budget is spent the
  remaining patterns are skipped for that line (matches already handled stand). No genuine line
  comes near it — the budget exists for unanchored custom patterns and hostile input.
* **Held-back tails.** An unterminated tail at EOF (a physical line without `\n`, or a
  multiline entry without `]LOG]!>`) is normally the writer mid-write. Instead of dropping it
  (and raw-matching its remainder next pass) the bookmark stays at the entry start. The hold
  lasts until the entry completes or the file has stood still for `HeldTailSettle` (1 s), and
  only for tails up to 1 MB — a never-closing hostile tail costs a handful of re-reads, then is
  processed as-is and the bookmark advances.
* **One Warning per pass and file.** `oversizedLines`, `regexTimeouts`, `lineBudgetBreaks` and
  the first skipped pattern ID are logged in a single line after the pass when any is non-zero
  (fail-soft with a trace); the first timeout per pattern per process additionally names the
  pattern at Debug. No per-line logging, so a hostile file cannot flood the agent log.
* **Deliberately not done: per-file preemption.** The first pass over a legitimate
  `AppWorkload.log` can be hundreds of MB and runs in one pass by design; after the changes
  above the work per byte is linear and small, so a writer can only delay processing in
  proportion to the bytes it writes.

# Verification

* `ImeLogPatternPackLinearityTests` loads the shipped pack, asserts the `^` invariant, runs four
  hostile constructions (repeated prefix, the reported `"DetectedIdentityVersion":{x`
  repetition, long word runs, prefix+keywords with newlines; 512 KB each) against every pattern
  with the agent's exact regex options and fails on a timeout or > 150 ms (best of three),
  and re-matches representative real lines with their captures.
* `BoundedLineReaderTests`, `ImeLogTrackerReadLoopHardeningTests` (oversized single line,
  oversized multiline opener, held-back tail, settle, cancelled pass bookmark, single Warning).
* Parity: the old and new packs were run over eight real diagnostics sets with the tracker's
  assembly semantics; the match sets are identical apart from the removed `PS-SCRIPT-CONTEXT`
  false positives. `--run-ime-matching` (the agent's offline matcher) now assembles multiline
  entries like the tracker so it reproduces the same result.

# Citations

* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/ImeLogTracker.LogProcessing.cs` — `CheckLogFilesAsync`, `MatchLine`, `HoldBackTail`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/BoundedLineReader.cs`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/ImeLogTracker.cs` — `MaxEntryBytes`, `HeldTailSettle`, `CompilePatterns`
* `rules/ime-log-patterns/*.json`, `rules/scripts/combine.js`
* `src/Agent/AutopilotMonitor.Agent.V2.Core.Tests/Monitoring/Ime/ImeLogPatternPackLinearityTests.cs`
* [CMTrace Time Resolution](cmtrace-time-resolution.md) — the linear CMTrace line parser this loop feeds
