---
type: Concept
title: Post-Reboot Completion Recovery — surviving the forced mid-ESP restart
description: Why an agent restarted by a mid-ESP reboot used to lose the AccountSetup completion for the rest of the session (orphaned Shell-Core backfill + edge-only user-apps-settled synthesis), and the two mechanisms that recover it — a downtime-sized ESP-exit replay and a level-triggered re-check of the settled user-ESP apps.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals
tags:
  - agent
  - esp
  - completion
  - reboot
  - recovery
timestamp: 2026-08-19T00:00:00+02:00
---

# Post-Reboot Completion Recovery

A device-assigned policy that Windows flags as reboot-required (DeviceGuard/VBS,
DmaGuard — see [MDM Reboot Coalescing](mdm-reboot-coalescing.md)) forces a restart in the
middle of the ESP. The agent is killed (`previousExit=reboot_kill`), the user signs in a
second time, and AccountSetup finishes. Until 2026-08-19 the session did **not** finish
with it: the agent waited for signals whose window had already closed, emitted
`session_stalled` after 60 minutes, and ran out at the max-lifetime watchdog — verdict
`Incomplete`, "No Device Setup completion or explicit failure signal observed before
timeout", on devices that were completely healthy.

# Schema

The AccountSetup gate opens on one of two evidence pairs:

| Path | Evidence |
|---|---|
| Registry | `AccountSetupCategory.Status.categorySucceeded` confirmed by Windows |
| Synthesis | Shell-Core normal ESP exit (62407) **and** every required user-ESP app terminal with zero failures |

The synthesis exists because a policy-skipped user-ESP app leaves the registry's Apps
subcategory permanently `inProgress`, starving the registry path (session `caa6cf50`).

Two independent defects made the synthesis unreachable after a reboot.

## Defect 1 — the restart replay was never wired up

`ShellCoreTracker.BackfillRecentEspExitEvents()` had existed since session `772fe502` but
**no caller ever invoked it**. The Shell-Core `EventLogWatcher` only delivers records
written after `Start()`, so every agent restart silently dropped a Hello-wizard start
(62404) that occurred while the agent was down.

`EspAndHelloHost.Start()` now calls it — as `BackfillRecentHelloWizardStart`, which is what
it does.

### What the replay recovers, and what it deliberately does not

Wiring up dead code activated three rails at once. Only one of them is safe to replay, and
the asymmetry is the whole design:

| Record | Replayed | Why |
|---|---|---|
| 62404 Hello-wizard start | **yes** | A *conservative* fact: it vetoes a premature "Hello is disabled" skip and can never by itself complete a session. Replaying it can only make the agent wait longer, never finish early. This is the observation `772fe502` was about. |
| 62407 ESP exit | **no** | Cannot be placed in time — see below. |
| 62407 ESP failure | **no** | Re-injecting a historic failure as fresh can fail a session that recovered on retry (`ANALYZE-ESP-006`). |

A replayed exit is unusable, and every candidate ordering mechanism fails for a different
reason:

1. Windows writes the **identical** description `CommercialOOBE_ESPProgress_Page_Exiting`
   for the intermediate DeviceSetup→AccountSetup transition and for the final
   post-AccountSetup exit. The record carries no evidence of its own position.
2. Everything that could order it after the fact — the AccountSetup registry probe, the
   settled-apps probe — reads state as it is **now**, not as it was at the event's time. An
   agent that was down across the Device→AccountSetup transition would confirm the stale
   intermediate exit.
3. The reducer orders exits by **ingest ordinal**, not by timestamp
   (`IsPostAccountSetupFinalExit`, deliberately — replayed CMTrace lines carry backdated
   source times). A historic exit replayed today is assigned a fresher ordinal than reality,
   so it reads as post-AccountSetup by construction.
4. `HandleEspExitingV1` passes `espFinalExitInFlight: true` for every arriving exit — the
   signal carries no provenance at all. With restored state (AccountSetup entered, a genuine
   IME user session, desktop arrived) **arm C** of `ShouldTransitionToAwaitingHello` then
   opens on a historic intermediate exit.

Point 4 is why the fix cannot live in the reducer: the reducer is itself a completion gate
and cannot tell the two apart. `ClassicEspExitingOnRestoredStateTests` pins that it *does*
open on any arriving exit; `ShellCoreTrackerReplayScopeTests` pins that the replay never
produces one. The pair is the contract.

Records that are read but not replayed are counted and reported once as an `agent_trace`
(`skippedEspExits`, `skippedEspFailures`, oldest/newest). Silently dropping evidence is how
this class of bug survives.

### Sizing the window

A constant is provably wrong: the agent's scheduled task carries a `BootTrigger` **only**,
with no restart-on-failure (`Program.InstallMode.BuildScheduledTaskXml`). After an
`exception_crash` the agent does not come back until the next boot — possibly hours later.
`ResolveEspExitBackfillLookbackMinutes` therefore reaches back to the last moment the
previous run is known to have been alive, taking the wider of two independent inputs:

| Input | Available for | Source |
|---|---|---|
| Snapshot mtime | every exit type | `snapshot.json` is rewritten on every decision step, so its last-write time is "when we last knew what was happening" |
| `LastBootUtc` | `hard_kill` / `reboot_kill` | covers a missing or never-written snapshot |

On `first_run` the replay stays **off** — no earlier process, no gap — so the happy path is
untouched. The 5-minute default is the **floor**, `ShellCoreTracker.ClampLookbackMinutes`
(360) the **ceiling**, and a timestamp in the future (clock skew across a reboot) is ignored
rather than producing a negative window.

This is a new caller for existing code, not a change to any other backfill. The
Hello/MDM-reboot/Windows-Update/ModernDeployment trackers each own a private backfill called
from their own `Start()`; none of them is touched.

## Defect 2 — the synthesis was edge-triggered

`MaybeSynthesizeAccountSetupCompleteFromSettledUserApps()` ran **only** from `OnEspExited`.
On a tenant with a large required-app set the ESP page exits while apps are still in
flight: the single attempt misses, emits `app_install_starved` for the apps that never
started, and nothing ever re-checks — not even when every tracked app reaches a terminal
state minutes later.

The ESP exit is an **edge** (it happens once); settled user-ESP apps are a **level** (they
can be reached later). `EspAndHelloTracker` now records the edge in `_espExitObserved` and
exposes `ReevaluateUserAppsSettledSynthesis()`, chained into `ImeLogTracker.OnAppStateChanged`
in `DefaultComponentFactory` (same preserve-previous pattern `DeliveryOptimizationHost`
uses). Every terminal app transition gives the synthesis another look.

This adds **no new completion path**. The gate conditions are unchanged — real ESP exit,
every required user-ESP app terminal, zero failures. The re-check only grants the existing,
deliberately conservative check a second opportunity. Three guards keep it cheap and safe:
the fire-once claim is an `Interlocked.CompareExchange` (the re-check runs on the IME log
thread, the edge on the Shell-Core watcher thread); the re-check path suppresses
`app_install_starved` emission so the one-shot warning does not become a per-transition
stream; and the edge itself is gated.

**Only a confirmed post-AccountSetup exit may be remembered.** Every exit that reaches the
coordinator is one the agent observed live — the replay never re-raises 62407 (above). That
is what makes the AccountSetup read below a valid ordering fact: the agent was continuously
observing up to that instant. On top of it, `IsConfirmedPostAccountSetupExit()` demands
positive evidence and is deliberately stricter than the existing
`IsIntermediateDeviceEspExit()` forward guard:

**The re-check must also run after the IME state restore.** `ImeLogTracker.Start()` restores
the persisted package states via `LoadState()` and raises no `OnAppStateChanged` for them, and
`ImeLogHost` starts *after* `EspAndHelloHost` (pinned by
`DefaultComponentFactoryOrderingTests`). A live exit observed before that restore completes
would therefore never get a second look. `ImeLogHost` takes an `onStateRestored` callback,
wired in the factory to `ReevaluateUserAppsSettledSynthesis()`.

The replay itself stays in `EspAndHelloHost.Start()` and does not depend on this ordering —
it feeds the reducer only, so there is nothing for it to read from the IME state. Keeping it
there also keeps this host's startup independent of the IME host's.

# Examples

Tenant `sits-d.cloud`, 2026-08-19, seven Windows 365 Cloud PCs, identical configuration
(~138 required apps at the user-ESP gate, DeviceGuard + DmaGuard device-assigned):

* `08fc6bda`, `3b9291aa` — `RebootCount=0`. AccountSetup finished before the coalesced
  reboot landed. Succeeded.
* `cb4a485a`, `e7ba63c9`, `8110e262`, `a89aac2d`, `3d6278fb` — `RebootCount>=1`. The agent
  was killed in `Stage=EspAccountSetup`, and no completion was ever derived again. All five
  ended `Incomplete` on healthy devices.

`8110e262` and `a89aac2d` are the sharpest evidence for defect 2: both reached
`App summary: 138/138 completed, 0 failed` and still never completed, because the settled
level arrived after the exit edge had already been consumed.

`3b9291aa` shows the independent starvation problem the same tenant has: 99
`app_install_starved` events (required apps that never started installing) on a session
that was nevertheless recorded as `Succeeded` — surfaced from now on by `ANALYZE-APP-016`.

# Citations

* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/ShellCoreTracker.cs` — `BackfillRecentEspExitEvents(int)`, `ClampLookbackMinutes`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/EspAndHelloTracker.cs` — `_espExitObserved`, `ReevaluateUserAppsSettledSynthesis`, `MaybeSynthesizeAccountSetupCompleteFromSettledUserApps`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Orchestration/Hosts/EspAndHelloHost.cs` — `Start()` ordering
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Orchestration/DefaultComponentFactory.cs` — lookback sizing, `OnAppStateChanged` chaining
* `src/Agent/AutopilotMonitor.Agent.V2/Runtime/AgentRuntimeHost.cs` — `previousBootUtc` hand-off
* [MDM Reboot Coalescing](mdm-reboot-coalescing.md) — the policy attribution for the reboot itself
* [Decision Engine](decision-engine.md) — the AccountSetup gate and completion arms
