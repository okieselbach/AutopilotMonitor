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

## Defect 1 — the ESP-exit replay was never wired up

`ShellCoreTracker.BackfillRecentEspExitEvents()` had existed since session `772fe502` but
**no caller ever invoked it**. The Shell-Core `EventLogWatcher` only delivers records
written after `Start()`, so every agent restart silently dropped an ESP exit (62407) or
Hello-wizard start (62404) that occurred while the agent was down.

`EspAndHelloHost.Start()` now calls it. Ordering is Start-then-backfill: a record written
between the two is observed twice, which the design already tolerates (Shell-Core emits
62407 at every ESP phase transition; the reducer's `ShouldTransitionToAwaitingHello`
picks the genuine post-AccountSetup one). The reverse order could drop a record, which
costs the session its completion.

The fixed 5-minute lookback cannot span a reboot, so the window is now sized to the
downtime: on `previousExit=reboot_kill` the factory passes `PreviousExitSummary.LastBootUtc`
and the lookback becomes "everything since the boot that killed us", clamped to
`ShellCoreTracker.BackfillLookbackMaxMinutes` (360 — the agent's max lifetime). Every other
exit type keeps the 5-minute default.

**Newest exit wins.** Widening the window changes what the replay contains. The reader walks
oldest-first and the exit branch is fire-once, so a naive record-by-record replay hands over
the *first* match — with a downtime-sized window that is the intermediate
DeviceSetup→AccountSetup exit, and the final post-AccountSetup exit is then swallowed by the
fire-once guard. `ReplayBackfillRecords` therefore buffers the batch and lets only the newest
exit-matching record through the exit branch; ESP failures and the Hello-wizard rail keep
replaying chronologically, exactly as before.

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

**Only a confirmed post-AccountSetup exit may be remembered.** Shell-Core raises 62407 at
every ESP phase transition. Remembering the DeviceSetup→AccountSetup one would let the
deferred re-check open the strong AccountSetup gate the moment the last user app settles —
while the AccountSetup page is still up and its other subcategories are still running, i.e.
a premature success. `IsConfirmedPostAccountSetupExit()` demands positive evidence and is
deliberately stricter than the existing `IsIntermediateDeviceEspExit()` forward guard:

| Evidence | Meaning |
|---|---|
| provisioning tracker reports AccountSetup activity | the page that just tore down is the AccountSetup one |
| `SkipUser == true` | the profile has no user ESP; the Device-ESP exit **is** the final one |

Anything else — including an unknown `SkipUser` — counts as *not confirmed*. The asymmetry is
intentional: erring strict costs at worst the pre-fix behaviour (the session stalls), while
erring loose costs a premature `Succeeded`, which nothing downstream can take back.

**The re-check must also run after the IME state restore.** `ImeLogTracker.Start()` restores
the persisted package states via `LoadState()` and raises no `OnAppStateChanged` for them, and
`ImeLogHost` starts *after* `EspAndHelloHost` (pinned by
`DefaultComponentFactoryOrderingTests`). So on a restart the ESP-exit replay runs against an
empty app list. `ImeLogHost` takes an `onStateRestored` callback — wired in the factory to the
re-check — which covers the sharpest case of all: apps already terminal *before* the reboot,
with only the final exit lost to the downtime.

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
