# Enrollment Status Reclassification — Timeout ≠ Failure

**Status:** Proposal · **Owner:** Backend · **Trigger tenant:** crcins.com (`ca9e3c59-d8ec-4ee6-9544-05f62f85ac98`)

## Problem

The 5‑hour maintenance sweep marks every silent, non‑terminal session as **`Failed`**
([`MaintenanceService.MarkStalledSessionsAsTimedOutAsync`](../../src/Backend/AutopilotMonitor.Functions/Services/MaintenanceService.cs) — Stage 2 hard‑codes `SessionStatus.Failed`). This conflates
*"the agent stopped sending telemetry"* with *"the enrollment failed"* and produces a
massively inflated failure rate.

For crcins.com this is dramatic, but the mechanism is platform‑wide.

## Evidence (crcins.com, 90‑day window)

Reported failure rate **57.2 %** (1321 / 2310). Measured over the full failed population
(n = 1311) via `query_raw_events(eventType=esp_provisioning_status)` + `search_sessions`:

| Failure reason | Count | Share |
|---|---:|---:|
| **`Session timed out after 5 hours`** (no terminal event) | **1189** | **90.7 %** |
| ESP `WhiteGlove_Failed` | 37 | 2.8 % |
| Agent max‑lifetime | 35 | 2.7 % |
| `esp_terminal_failure` | 27 | 2.1 % |
| `Autopilot enrollment failed: Provisioning…` | 19 | 1.4 % |
| `esp_exit_without_completion` | 4 | 0.3 % |

**Explicit, real failures ≈ 87 → true failure rate ≈ 3.8 % (max ~5.3 %), not 57.2 %.**

### What the "5h timeouts" actually were

Last observed ESP state across the 1189 timeouts (1077 with captured
`esp_provisioning_status`, 90.6 % coverage):

| Last observed state | Count | Verdict |
|---|---:|---|
| **DeviceSetup 4/4 = fully provisioned (AADJ+MDM)** | **1067 / 1077 (99.1 %)** | device is enrolled |
| AccountSetup `0/5` — user phase never observed | 1024 (95.1 %) | **AwaitingUser** — not a failure |
| AccountSetup `1–4/5` — mid user phase, agent gone | 25 (2.3 %) | **AwaitingUser** |
| AccountSetup `5/5` — complete | 25 (2.3 %) | **reconcile → Succeeded** |
| no `esp_provisioning_status` at all | 112 (9.4 %) | **Incomplete/Unknown** |

Not a single 5h‑timeout carries an explicit `enrollment_failed` / `esp_failure` event.

### Two key data facts (drive the design)

1. **`categorySucceeded` is unreliable.** In every `esp_provisioning_status` payload —
   even at DeviceSetup 4/4 and AccountSetup 5/5 — Windows reports
   `categorySucceeded = "in_progress"`. The agent already compensates with a 30 s
   "all subcategories succeeded → treat complete" fallback for DeviceSetup
   ([`ProvisioningStatusTracker` / `EspAndHelloTracker`](../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/)).
   → **Gate on the subcategory rollup, never on `categorySucceeded`.**
2. **Desktop arrival ≠ complete.** In User‑driven ESP the desktop appears while the
   Account/user phase is still running. `desktop_arrived` is a hint, not a terminal.
   The authoritative terminal is **AccountSetup subcategory rollup = all succeeded**
   (which precedes the agent's own `enrollment_complete`).

## Root cause

The agent captures exactly the right signal (`esp_provisioning_status` with per‑category
subcategory rollups incl. AccountSetup), but:

- In ~95 % of timeouts the agent goes dark at the DeviceSetup→AccountSetup reboot,
  **before** the user logs in, so AccountSetup never progresses in telemetry (0/5). The
  user phase legitimately completes hours/days later (proven by session
  `cfa820da-…`: AccountSetup 5/5 + `enrollment_complete` the **next day**).
- The backend's 5h sweep fires first, hard‑labels `Failed`, and **never reconciles** when
  the late completion arrives.

So the fix is **classification + reconciliation in the backend** — no new agent load,
no heartbeat. The signals are already in the event stream.

## Design

### 1. Status model — a third terminal bucket

`SessionStatus` ([`SessionApiModels.cs:426`](../../src/Shared/AutopilotMonitor.Shared/Models/SessionApiModels.cs))
today: `InProgress, Pending, Stalled, Succeeded, Failed, Unknown`.

Add:

- **`AwaitingUser`** (non‑terminal): DeviceSetup succeeded, user/account phase not yet
  complete, agent silent but within grace. Can heal to `Succeeded` or expire to `Incomplete`.
- **`Incomplete`** (terminal, **non‑failure**): grace expired with no terminal evidence,
  or silence before DeviceSetup completed with no explicit failure. This is the
  "success/failed/**incomplete‑unknown**" third state for the stats. (Existing `Unknown`
  is folded into the Incomplete bucket for reporting.)

### 2. Classification at the sweep (deterministic, from data we already load)

The sweep already loads up to 1000 events and builds a snapshot
([`FailureSnapshotBuilder`](../../src/Backend/AutopilotMonitor.Functions/Services/FailureSnapshotBuilder.cs)).
Extend the snapshot to extract the **last DeviceSetup and AccountSetup subcategory rollups**
from `esp_provisioning_status` events, then decide the target status instead of hard‑coding
`Failed`:

```
Given a non-terminal session past the inactivity window:

1. Explicit enrollment_failed / esp_failure present            → Failed          (real)
2. AccountSetup rollup = ALL subcategories succeeded
   OR enrollment_complete / whiteglove_complete present         → Succeeded       (reconcile)
3. DeviceSetup rollup = ALL subcategories succeeded (or 30s
   fallback) AND no failure event:
      a. within grace (now - StartedAt < SessionGraceHours)      → AwaitingUser    (non-terminal)
      b. grace expired                                           → Incomplete      (terminal, non-failure)
4. else (silence before DeviceSetup complete, no failure)       → Incomplete
```

Rationale for each bucket sizing is in the Evidence table above.
⚠️ Gate step 2/3 on the **subcategory rollup**, not `categorySucceeded`.

### 3. Grace window (backend owns the wait — zero client cost)

Add `TenantConfiguration.SessionGraceHours` (default **72**). Two‑stage terminalization in
the same sweep that already runs:

- At `SessionTimeoutHours` (5h): non‑terminal → **`AwaitingUser`** (was `Failed`).
- At `SessionGraceHours` (72h): `AwaitingUser` → **`Incomplete`** (unless already reconciled).

The waiting session is just a table row compared against a timestamp — no process, no
telemetry, no agent change.

### 4. Reconciliation on late events

When a session in `Failed` / `Incomplete` / `AwaitingUser` later receives a genuine terminal
signal (`enrollment_complete`, `whiteglove_complete`, or AccountSetup rollup = all
succeeded), flip to **`Succeeded`**
([`EventIngestProcessor.Classification.cs`](../../src/Backend/AutopilotMonitor.Functions/Services/EventIngestProcessor.Classification.cs)
already routes `enrollment_complete` → `Succeeded`; the `UpdateSessionStatusAsync` guard must
permit an already‑terminal → `Succeeded` upgrade for these reconcile signals only).

### 5. Stats / metrics — expose 3 states, exclude Incomplete from failure rate

- `SessionStatusBuckets` ([`MetricsMath.cs:317`](../../src/Backend/AutopilotMonitor.Functions/Helpers/MetricsMath.cs))
  gains explicit `AwaitingUser` and `Incomplete` buckets (they currently fall into `Other`).
- `SessionStats` / Fleet Health: add `IncompleteLastNDays`; **failure rate denominator =
  `Succeeded + Failed` only** (Incomplete and AwaitingUser excluded). Headline becomes
  Success / Failed / Incomplete‑Unknown.
- Web: `SessionStatus` mirror + badge + `isTerminalStatus`
  ([`utils/sessionStatus.ts`](../../src/Web/autopilot-monitor-web/utils/sessionStatus.ts),
  [`SessionStatusBadge.tsx`](../../src/Web/autopilot-monitor-web/components/SessionStatusBadge.tsx)).

## Expected impact (crcins.com, from the measured buckets)

| | today | after |
|---|---:|---:|
| Succeeded | 599 | ~624 (+25 reconciled) |
| Failed | 1311 | ~122 (explicit failures + max‑lifetime) |
| AwaitingUser (new, transient) | – | ~1052 |
| Incomplete/Unknown (new) | – | ~115 |
| **Failure rate** | **57.2 %** | **~4–5 %** |

Platform‑wide the same reclassification de‑noises every tenant's failure rate.

## Non‑goals / explicitly rejected

- **Agent heartbeat** — rejected: continuous client load + zombie risk; the completion
  truth is already persisted (registry/ESP tracking survives reboots) and the wait belongs
  in the backend.
- **`desktop_arrived` as completion** — rejected: fires while the user phase is still running.

## Acceptance criteria

- No non‑terminal session is marked `Failed` by the sweep without an explicit failure event.
- A session reaching AccountSetup all‑succeeded (or `enrollment_complete`) after a prior
  sweep ends as `Succeeded`.
- `Failed` count only contains explicit‑failure sessions; failure‑rate excludes Incomplete.
- Reprocessing the crcins.com window reproduces the "after" table within rounding.
