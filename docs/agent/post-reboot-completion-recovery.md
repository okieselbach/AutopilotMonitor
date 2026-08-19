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

**Scope: restart recovery only.** The replay re-reads observations the agent could not make
because no agent process was running. On `first_run` it stays **off** — no earlier process, no
gap; a 62407 already in the log belongs to a transition this agent was never meant to observe,
and replaying it would push an `EspExiting` into the reducer that the pre-fix agent never saw.
The happy path is therefore untouched.

**The window is measured, not guessed.** A constant is provably wrong here: the agent's
scheduled task carries a `BootTrigger` **only**, with no restart-on-failure
(`Program.InstallMode.BuildScheduledTaskXml`). After an `exception_crash` the agent does not
come back until the next boot — possibly hours later, long after Windows wrote the final ESP
exit. `ResolveEspExitBackfillLookbackMinutes` therefore reaches back to the last moment the
previous run is known to have been alive, taking the wider of two independent inputs:

| Input | Available for | Source |
|---|---|---|
| Snapshot mtime | every exit type | `snapshot.json` is rewritten on every decision step, so its last-write time is "when we last knew what was happening" |
| `LastBootUtc` | `hard_kill` / `reboot_kill` | covers a missing or never-written snapshot |

The 5-minute default remains the **floor** (a restart that returns in seconds still gets it) and
`ShellCoreTracker.ClampLookbackMinutes` the **ceiling** (360 — the agent's max lifetime), so
policy and backstop together can never build an unbounded event-log query. A timestamp in the
future (clock skew across a reboot) is ignored rather than producing a negative window.

This is a *new* caller for existing code, not a change to any other backfill. The
Hello/MDM-reboot/Windows-Update/ModernDeployment trackers each own a private backfill called
from their own `Start()`; none of them is touched.

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

**Only a LIVE, confirmed post-AccountSetup exit may be remembered.** Shell-Core raises 62407 at
every ESP phase transition and writes the *same* description
(`CommercialOOBE_ESPProgress_Page_Exiting`) for the intermediate DeviceSetup→AccountSetup
transition and for the final one — the event carries no evidence of its own position.
Remembering the wrong one would let the deferred re-check open the strong AccountSetup gate the
moment the last user app settles, while the AccountSetup page is still up and its other
subcategories are still running: a premature success.

Two filters therefore apply to the remembered edge:

1. **Live only.** A replayed exit is never remembered (`EspExitedEventArgs.IsBackfill`). The
   AccountSetup check below reads the registry *now*, not at the event's time; that read is a
   valid ordering fact only for an exit the agent observed while continuously running. An agent
   that was down across the DeviceSetup→AccountSetup transition would otherwise confirm the
   stale intermediate exit. A replayed exit still gets the single immediate synthesis attempt it
   always had.
2. **Confirmed post-AccountSetup.** `IsConfirmedPostAccountSetupExit()` demands positive
   evidence and is deliberately stricter than the existing `IsIntermediateDeviceEspExit()`
   forward guard:

| Evidence | Meaning |
|---|---|
| provisioning tracker reports AccountSetup activity | the page that just tore down is the AccountSetup one |
| `SkipUser == true` | the profile has no user ESP; the Device-ESP exit **is** the final one |

Anything else — including an unknown `SkipUser` — counts as *not confirmed*. The asymmetry is
intentional: erring strict costs at worst the pre-fix behaviour (the session stalls), while
erring loose costs a premature `Succeeded`, which nothing downstream can take back.

**The replay itself runs after the IME state restore.** `ImeLogTracker.Start()` restores the
persisted package states via `LoadState()` and raises no `OnAppStateChanged` for them, and
`ImeLogHost` starts *after* `EspAndHelloHost` (pinned by
`DefaultComponentFactoryOrderingTests`). Running the replay inside `EspAndHelloHost.Start()`
would therefore evaluate it against an empty app list — and since a replayed exit gets exactly
one synthesis attempt, that attempt would be worthless.

`ImeLogHost` takes an `onStateRestored` callback, wired in the factory to
`ReplayEspExitBackfill()` **then** `ReevaluateUserAppsSettledSynthesis()`. The replay's own
attempt now sees restored app states, which is what makes "the agent came back long after the
ESP finished" recoverable at all; the re-check afterwards covers the live-exit case where the
apps settled during the restore itself.

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
