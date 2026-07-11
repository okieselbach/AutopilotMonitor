---
type: Reference
title: V2 Agent — Logs, Persistence & Crash Recovery
description: Every file the agent persists under %ProgramData%\AutopilotMonitor, why each exists (especially the signal log), and the exact recovery/replay flow after a crash or reboot.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Orchestration/RecoveryCoordinator.cs
tags:
  - agent
  - persistence
  - signal-log
  - recovery
  - durability
timestamp: 2026-07-11T00:00:00+02:00
---

# V2 Agent — Logs, Persistence & Crash Recovery

An enrollment spans multiple reboots and can be killed at any moment (app-forced
restart, self-update, crash, power loss). The agent therefore persists three distinct
tiers of state — each answers a different question:

1. **Decision truth** (`State\`): *what did the decision engine see and decide?*
   Signal log + journal + snapshot. Must be deterministic and replayable.
2. **Transport** (`Spool\`): *what telemetry still has to reach the backend?*
   Durable upload queue + cursor. Best-effort; the backend dedups.
3. **Diagnostics** (`Logs\`, `Crashes\`): *what happened, for humans?* Trace log and
   crash dumps. Never allowed to affect decisions.

Plus small markers (session identity, completion, quarantine) that steer the next
process start.

# Why a signal log AND an event spool?

They look similar (both JSONL) but serve opposite masters:

- The **signal log** is the *input* truth of the decision engine. Recovery rebuilds
  decision state by replaying it; the snapshot is only a cache. An append failure here
  **blocks the reducer** — a signal that was never durably logged must not change state,
  or a post-crash replay would diverge from what the live run decided (determinism L.1/L.5).
- The **spool** is a *projection* for upload. Writing it is best-effort — a spool
  failure must never break ingress, and a lost non-critical item is acceptable because
  the backend dedups on `(PartitionKey, RowKey)`.

Same data, two files, because durability requirements differ: the signal log fsyncs
every line; the spool fsyncs only items flagged `RequiresImmediateFlush`.

# Artifact inventory

| Artifact | Path | Format | fsync | Rotation/cap |
| --- | --- | --- | --- | --- |
| Signal log | `State\signal-log.jsonl` | JSONL (`DecisionSignal`/line) | every append (WriteThrough + Flush, bufferSize=1) | none (per-enrollment lifecycle) |
| Journal | `State\journal.jsonl` | JSONL (`DecisionTransition`/line) | every append | atomic truncate only (phantom tail) |
| Snapshot | `State\snapshot.json` | JSON + SHA-256 checksum envelope | temp-write + `File.Replace` | single file |
| Event sequence | `State\event-sequence.json` | `{ LastAssignedSequence }` | atomic | none; survives WhiteGlove Part-2 resume |
| Spool | `Spool\spool.jsonl` | JSONL (`TelemetryItem`/line) | only `RequiresImmediateFlush` items | none — never truncated, cursor skips uploaded items |
| Upload cursor | `Spool\upload-cursor.json` | `{ LastUploadedItemId }` | atomic | idempotent, never regresses |
| Agent trace log | `Logs\agent_YYYYMMDD[_NNN].log` | text `[utc] [LEVEL] msg` | no | 50 MB → suffix rotation; consumed by `/diag-agent-log` |
| Crash dump + record | `Crashes\<stem>.dmp` + `.json` | MiniDump + JSON | n/a (process dying) | 5 files / 7 days |
| Startup-event gate | `State\startup-event-state.json` | JSON dict | no (atomic) | cross-reboot dedup of one-shot probes |
| Bandwidth state | `State\bandwidth-state.json` | JSON samples/counters | no (atomic) | reboot-survivor for the DO bandwidth estimate |
| Session identity | `session.id`, `session.created`, `whiteglove.complete` (root) | text | no | drives emergency-break age check + WG resume |
| Markers | `clean-exit.marker` (root), `enrollment-complete.marker` (State), `quarantine-requested.marker` (State) | text | no | consumed at next start |
| Config cache | `Config\remote-config.json` | JSON | no | security-sensitive fields stripped |

Per-collector state files (`OfficeInstallStatePersistence`, IME tracker state, script
execution state, log-file positions) follow the same fail-soft atomic-replace pattern.
Cleanup (`CleanupService`) removes the agent dirs on self-destruct but **keeps `Logs\`**
for forensics.

# The decision-truth triad

## Signal log — authoritative input

- Written by the single ingress worker, one line per accepted signal, **before** the
  reducer runs. Monotonic-ordinal guard on append.
- `bufferSize: 1` is load-bearing: session `b9b92d89` (2026-07-09) was killed mid-append
  by a self-update `Environment.Exit`; the default 4096-byte buffer flushed the same
  buffered line twice → byte-identical duplicate → non-monotonic ordinal → **recovery
  crash-loop**. Fixes (commits `57e4334b` + `a0238a4b`):
  - unbuffered append (nothing left for `Dispose` to re-flush),
  - read-side tolerance: a byte-identical *consecutive* duplicate line is skipped as a
    known crash artifact,
  - self-update restarts now route through graceful shutdown (full drain, bounded 15 s
    hard-exit fallback) instead of `Environment.Exit`,
  - poison-replay quarantine (below) so a corrupt log can never loop the agent again.
- Corrupt-tail rule: readers stop at the first unparsable line (a torn final write is
  expected, not fatal).

## Journal — authoritative applied-transition record

One `DecisionTransition` per reducer step, appended in `ApplyStep` step 1 — the **only**
persistence path that hard-throws. Three consecutive append failures escalate to a
quarantine request. Used during recovery to detect BEHIND/AHEAD crash windows against
the snapshot; AHEAD phantom entries are truncated to `.quarantine\<ts>\journal-phantom-tail.jsonl`.

## Snapshot — recovery cache, never truth

Full `DecisionState` (schema **v4**) in a checksum envelope, written best-effort after
meaningful steps (stage transition / non-telemetry effect) or every 100 pass-through
steps. Atomic temp-write + `File.Replace`; a crash mid-save leaves the old file intact.
Load returns `null` on any doubt (missing, IO error, checksum mismatch) — recovery then
falls back to full signal-log replay. Aborted/phantom states are never snapshotted.

# Recovery flow on restart

`RecoveryCoordinator.Recover` runs before the live pipeline; order is load-bearing:

1. **Death-rattle peek**: if the previous exit was unclean, read the prior snapshot
   *non-mutatingly* (`TryReadRaw`) for the `previous_crash_detected` context — before
   the pipeline may quarantine it.
2. **Quarantine marker** (`quarantine-requested.marker`, written mid-run when the
   journal became unappendable): quarantine all segments + snapshot *now*, at a moment
   when no writer holds them. Marker is deleted only on full success.
3. **WhiteGlove Part-2 resume**: prior stage `WhiteGloveSealed` → archive the reducer
   segments to `.part1-<ts>\` but **preserve `event-sequence.json`** (the Web UI splits
   Part 1/2 on the sequence).
4. **Open writers** — each scans its file for the last ordinal/step, stopping at the
   first unparsable line.
5. **Pick seed + replay stream**:
   - snapshot loads → seed from it, re-stamp `AgentBootUtc` to *now* (replayed signals
     must not arm past-due deadlines), replay only the signal-log **tail**
     (`ordinal > snapshot.LastAppliedSignalOrdinal`);
   - snapshot corrupt → quarantine snapshot only, replay the full signal log;
   - log head-corrupt → total-loss quarantine of everything, fresh Initial seed.
6. **Journal alignment**: truncate AHEAD phantoms to the seed's `StepIndex`.
7. **Replay + backfill**: fold the stream through a transient `DecisionEngine`,
   re-appending any missing journal entries (closes the BEHIND window).
8. **Poison guard**: if replay itself throws, quarantine snapshot + all segments and
   reseed Initial — the agent keeps running with fresh state instead of crash-looping.

**Duplicate avoidance, end to end**: monotonic append guards (signal log + journal) →
consecutive-duplicate skip on read → tail-replay past the snapshot ordinal → AHEAD
truncation → backend `(PartitionKey, RowKey)` dedup for re-uploaded spool items.

**Quarantine semantics**: files are *moved*, never deleted, into
`State\.quarantine\<utc-ts>\` with a `reason.txt` sidecar — evidence survives for
diagnostics ZIPs while the agent restarts from clean state.

# Related

- Pipeline that produces these files: [runtime overview](overview.md)
- What replay must reproduce deterministically: [decision engine](decision-engine.md)
