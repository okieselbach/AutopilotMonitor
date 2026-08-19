---
type: Concept
title: ESP Completion Starvation — the sits-d Cloud-PC complex
description: Why five Windows 365 Cloud PCs finished enrolling but never completed in the product — a SkipUser=true flow produces none of the signals every completion-gate evaluation site hangs off — plus the two adjacent defects fixed alongside (edge-only user-apps-settled synthesis, orphaned Hello-wizard restart replay) and what a Shell-Core replay deliberately does not do.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals
tags:
  - agent
  - esp
  - completion
  - skip-user
  - reboot
  - recovery
timestamp: 2026-08-20T00:00:00+02:00
---

# ESP Completion Starvation — the sits-d Cloud-PC complex

Seven Windows 365 Cloud PCs, same tenant, first sign-in the same morning. Two sessions
succeeded within minutes; five sat at `Completion is waiting on: hello_resolution` from
second five onward and ran into the six-hour max-lifetime watchdog — verdict `Incomplete`
on devices whose users were working normally the whole time.

The first diagnosis blamed the mid-ESP reboot (DeviceGuard/DmaGuard, see
[MDM Reboot Coalescing](mdm-reboot-coalescing.md)): the agent is killed in
`Stage=EspAccountSetup` and, so the theory went, loses the Shell-Core ESP exit to the
downtime. The evidence disproved it. `8110e262` ran **six hours uninterrupted** after its
last reboot with the Shell-Core watcher armed — and Windows never wrote a 62407 in all
that time, because on these machines **the user ESP page never existed**.

# Schema

## The actual defect — a satisfied gate nobody evaluates

The five stuck machines carried `SkipUserStatusPage=True` in the enrollment `FirstSync`
key (read 10+ times across three agent processes, `True` on every read — not a stale
single read). Windows honours it: no user ESP page, hence

* no Shell-Core 62407 (`esp_exiting`) — the page that would exit does not exist,
* no `AccountSetupCategory` provisioning registry — nothing writes categories for a page
  that never renders,
* desktop within seconds of sign-in.

The reducer is *built* for this flow: **arm B** of `ShouldTransitionToAwaitingHello`
(`SkipUserEsp == true`) declares the gate open, and the missing-prerequisites bookkeeping
already exempts the AccountSetup gate — which is why the sessions reported only
`hello_resolution` as missing. But the gate is a predicate, not a process. Something has
to evaluate it, and every evaluation site hangs off a signal a skip-user flow structurally
cannot produce:

| Evaluation site | Carrier signal | On SkipUser=true |
|---|---|---|
| `HandleEspExitingV1` | Shell-Core 62407 | never fires |
| `HandleEspPhaseChangedV1(FinalizingSetup)` | Shell-Core 62407 via coordinator | never fires |
| `HandleDesktopArrivedV1` fast-path | requires Hello policy **disabled** | Hello was enabled |
| `HandleImeUserSessionCompletedV1` arm-C attempt | requires the recorded final exit | unsatisfiable |

No knock, no promotion, no `HelloSafety` — and the `AdvisoryCompletion` backstop is armed
exclusively from an `EspExiting` signal, so it never armed either. The session idles to
the watchdog with an open gate.

## The fix — the observed skip is the exit evidence

`HandleImeUserSessionCompletedV1`'s completion attempt now accepts the observed skip in
place of the final exit (`skipUserExitEquivalent`): on a flow where no page exists, no
exit can ever say more than the `FirstSync` read already did. Everything else stays
mandatory — AccountSetup anchor, genuine (at-or-after-anchor) IME user-session completion,
DAD-validated real-user desktop — which makes this knock **stricter** than the existing
Shell-Core-carried arm-B promotion (pinned in `ClassicAwaitingHelloGuardTests`, which
requires no IME or desktop evidence at all). Hello semantics ride the existing rails:
enabled-or-unknown promotes to `AwaitingHello` with `HelloSafety` armed (on Cloud PCs
reached via RDP the Hello wizard never appears, so the synthetic timeout resolves the
wait), disabled completes directly with the synthetic `Skipped` outcome.

The knock deliberately lives on the IME user-session edge, not on desktop arrival:

* **Ordering-robust against Fix 10.** An `EspPhaseChanged(AccountSetup)` arriving on
  `AwaitingHello` bounces the stage back and cancels `HelloSafety` (the premature-promotion
  guard). The IME AccountSetup phase line lands *after* the desktop on this flow, so a
  desktop-side knock would be undone two signals later. The IME phase line always precedes
  the user-session-complete line in log order, so a bounced promotion is re-knocked by the
  very next re-emission — including the restart re-parse.
* **Right semantics.** The IME user session completes when the required-app processing is
  done; a desktop-side knock would mark the session `Succeeded` mid-installation.
* Device Preparation is excluded — WDP keeps its Hello+Desktop conjunction and the
  `DevicePrepCompletion` backstop.

`ClassicSkipUserEspUserSessionCompletionTests` replays the real `8110e262` signal ordering
end to end (bootstrap `EspConfigDetected` → Hello enabled → desktop → AccountSetup anchor →
IME completion → HelloSafety → `enrollment_complete`), including the Fix-10
bounce-then-re-knock cycle, and pins the negative space (no skip observed / no desktop /
ghost pre-anchor IME completion / WDP).

Why five of seven identically administered Cloud PCs carried `SkipUserStatusPage=True` is
a tenant-side question (ESP profile assignment state at each machine's provisioning
moment); the value did not change during any observed session, and machine age does not
cleanly explain the split. The agent's job is to complete the flow Windows actually ran —
which it now does.

## Adjacent defect — the synthesis was edge-triggered

This one is real but belongs to the `SkipUser=false` flow (the two sessions that
succeeded). The user-apps-settled synthesis
(`MaybeSynthesizeAccountSetupCompleteFromSettledUserApps`, session `caa6cf50`) ran **only**
from `OnEspExited`. On a tenant with ~138 required apps the ESP page exits while apps are
still in flight: the single attempt misses and nothing ever re-checks, even when every
tracked app reaches a terminal state minutes later.

The ESP exit is an **edge** (happens once); settled user-ESP apps are a **level** (reached
later). `EspAndHelloTracker` records the edge in `_espExitObserved` and exposes
`ReevaluateUserAppsSettledSynthesis()`, chained into `ImeLogTracker.OnAppStateChanged` and
into the post-restore `onStateRestored` callback (a live exit observed before the IME
state restore completes would otherwise never get a second look). Gate conditions are
unchanged — no new completion path, only a second opportunity for the existing one. Only a
**live, confirmed post-AccountSetup** exit may be remembered
(`IsConfirmedPostAccountSetupExit`, stricter than the `IsIntermediateDeviceEspExit`
forward guard: unknown counts as not confirmed).

## Adjacent defect — the restart replay was never wired up

`ShellCoreTracker`'s backfill had existed since session `772fe502` with **no caller**; the
Shell-Core `EventLogWatcher` only delivers records written after `Start()`.
`EspAndHelloHost.Start()` now calls it as `BackfillRecentHelloWizardStart` — which is
exactly what it does and all it does:

| Record | Replayed | Why |
|---|---|---|
| 62404 Hello-wizard start | **yes** | Conservative: vetoes a premature "Hello is disabled" skip, can never complete a session by itself. The observation `772fe502` was about. |
| 62407 ESP exit | **no** | Cannot be placed in time — see below. |
| 62407 ESP failure | **no** | Re-injecting a historic failure as fresh can fail a session that recovered on retry (`ANALYZE-ESP-006`). |

A replayed exit is unusable, and every candidate ordering mechanism fails independently:

1. Windows writes the **identical** description `CommercialOOBE_ESPProgress_Page_Exiting`
   for the intermediate DeviceSetup→AccountSetup transition and the final exit.
2. Every post-hoc ordering probe (AccountSetup registry, settled-apps) reads state as it
   is **now**, not as it was at the event's time.
3. The reducer orders exits by **ingest ordinal**, not timestamp
   (`IsPostAccountSetupFinalExit`) — a historic exit replayed today gets a fresher ordinal
   than reality and reads as post-AccountSetup by construction.
4. `HandleEspExitingV1` passes `espFinalExitInFlight: true` for every arriving exit; with
   restored state, arm C would open on a historic intermediate exit.

`ClassicEspExitingOnRestoredStateTests` pins that the reducer *does* open on any arriving
exit; `ShellCoreTrackerReplayScopeTests` pins that the replay never produces one. Skipped
62407 records are counted and reported once as an `agent_trace`
(`reason: replayed_62407_not_orderable`) — silently dropping evidence is how this class of
bug survives.

**Residual gap, named honestly:** a `SkipUser=false` session whose final exit falls
exactly into an agent downtime *and* whose AccountSetup category never resolves
(`1ec8f4c6` shape) still has no completion carrier after the restart. The normal case is
covered — `ProvisioningStatusTracker` reads the current registry at startup, so a
category that resolved during the downtime fires arm A. The residual shape is rare,
surfaced by `ANALYZE-ESP-005` via the `session_stalled` interim trigger, and deliberately
not patched with a replay that cannot be ordered.

### Sizing the replay window

A constant is provably wrong: the scheduled task has a `BootTrigger` only
(`Program.InstallMode.BuildScheduledTaskXml`) — after an `exception_crash` the agent does
not return until the next boot. `ResolveEspExitBackfillLookbackMinutes` reaches back to
the last moment the previous run is known to have been alive (max of `snapshot.json`
mtime and `LastBootUtc` for kill-type exits), floored at 5 minutes, capped at 360, off on
`first_run`, and immune to cross-reboot clock skew (a future timestamp is ignored).

# Examples

Tenant `sits-d.cloud`, 2026-08-19, seven Windows 365 Cloud PCs, first sign-ins the same
morning. The discriminator is a single registry value:

| Session | SkipUser | `esp_exiting` | AccountSetup registry | Reboots | Outcome |
|---|---|---|---|---|---|
| `08fc6bda` | False | yes | yes | 0 | Succeeded (10 min) |
| `3b9291aa` | False | yes | yes | 0 | Succeeded (2.1 h) |
| `8110e262` | True | — | — | 2 | Incomplete (6 h watchdog) |
| `a89aac2d` | True | — | — | 1 | Incomplete |
| `e7ba63c9` | True | — | — | 1 | Incomplete |
| `cb4a485a` | True | — | — | 2 | Incomplete |
| `3d6278fb` | True | — | — | 1 | Incomplete |

The reboot correlation was a red herring: `8110e262` ran six hours live after its final
reboot and would have seen every signal. `8110e262` and `a89aac2d` both reached
`138/138 completed, 0 failed` and a genuine IME user-session completion — with the fix,
all five complete a few minutes after their IME user session settles.

`08fc6bda` exercises the edge-level fix on the same day: ESP page exited normally while
`categorySucceeded` never confirmed — completed via the user-apps-settled synthesis.
`3b9291aa` shows the independent starvation visibility gap: 99 `app_install_starved`
events on a session recorded as `Succeeded` — surfaced from now on by `ANALYZE-APP-016`.

# Citations

* `src/Shared/AutopilotMonitor.DecisionCore/Engine/DecisionEngine.Classic.cs` — `HandleImeUserSessionCompletedV1`, `skipUserExitEquivalent`
* `src/Shared/AutopilotMonitor.DecisionCore/Engine/DecisionEngine.Shared.cs` — `ShouldTransitionToAwaitingHello` (arm B)
* `src/Agent/AutopilotMonitor.DecisionCore.Tests/ClassicSkipUserEspUserSessionCompletionTests.cs` — end-to-end replay of the 8110e262 ordering
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/ShellCoreTracker.cs` — `BackfillRecentHelloWizardStart`, `ReplayBackfillRecords`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/EspAndHelloTracker.cs` — `_espExitObserved`, `ReevaluateUserAppsSettledSynthesis`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Orchestration/DefaultComponentFactory.cs` — lookback sizing, `OnAppStateChanged` chaining
* [MDM Reboot Coalescing](mdm-reboot-coalescing.md) — the policy attribution for the reboots themselves
* [Decision Engine](decision-engine.md) — the AccountSetup gate and completion arms
