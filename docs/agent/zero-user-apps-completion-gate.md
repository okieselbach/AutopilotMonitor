---
type: Concept
title: Zero User-Targeted Apps — the starved user-phase completion gate
description: Why a device with no user-targeted Intune apps could never satisfy the IME user-session gate, how that turned into a 30-minute esp_exit_without_completion_evidence false positive on a third of one tenant's enrollments, and the two fixes — reading the line IME actually writes in its zero-app branch, and refusing to fail a session while a completion gate is deliberately holding it.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime
tags:
  - agent
  - ime
  - esp
  - completion
  - realmjoin
  - false-positive
timestamp: 2026-08-25
---

# Summary

A Classic user-driven enrollment whose user has **zero targeted Intune Win32 apps** could not
produce the evidence every user-phase completion path required. The session parked, and the
30-minute `advisory_completion` backstop resolved it as
`enrollment_failed: esp_exit_without_completion_evidence` — while the user was at a working
desktop, Windows Hello was provisioned, and RealmJoin was still installing packages.

One tenant hit this on **10 of 30 enrollments in a week**. Every one of them was a false
positive; the devices were healthy in Intune and RealmJoin.

# The starvation chain

The IME writes `Completed user session N, userId: …` at the end of a user-session processing
pass. `IME-USER-SESSION-COMPLETED` matches it and the adapter posts
`ImeUserSessionCompleted`, which sets `DecisionState.ImeUserSessionCompletedUtc`.

But the IME never reaches that statement when the user has no assigned apps. Decompiled IME
`1.104.102.0`, `Win32AppPlugIn/ApplicationPoller.cs`:

```csharp
if (!appPolicies.Any())
{
    AppWorkloadLog.TraceInformation($"[Win32App] Get 0 apps for user session {user.RDSSessionId}, user id = {user.IntuneId}");
    RegisterApplicationsInIfInESP(sender, user, delegate {
        espLockInProcessor.AttemptToLockInApplications(user, new List<ProcessorSubgraph>());
    });
    continue;                       // skips the "Completed user session" statement below
}
```

Identical shape in `1.97.107.0`, `1.83.107.0`, `1.80.132.0`. With that fact missing, every door
out of the user phase is shut at once:

| Path | Requirement | Why it is dead |
|---|---|---|
| `ShouldTransitionToAwaitingHello` arm A | `AccountSetupProvisioningSucceededUtc` | ESP registry froze at `AccountSetup: 1 of 5`; neither `categorySucceeded` nor the all-subcategories fallback can ever hold |
| arm B | `SkipUserEsp` | Full ESP, not a skip-user flow |
| arm C | `IsImeUserSessionGenuine` | needs the missing IME fact |
| arm D | `EnrollmentMode.DevicePreparation` | Autopilot v1, not WDP |
| `MaybeSynthesizeAccountSetupCompleteFromSettledUserApps` | `AreUserEspAppsSettled` | returned `false` on an empty app list |
| `HandleAdvisoryCompletionDeadlineFired` conjunction | `imeUserSessionGenuine \|\| isObservationAdvisory` | both false |

The last row is the one that produced the verdict: the backstop that exists to resolve a
dead-end resolved it as a failure.

`AreUserEspAppsSettled` deserves its own note. Its "at least one required app" rule is
deliberately conservative — an empty list normally means *the phase just cleared* or *apps have
not surfaced yet*. But it made **zero apps**, the most settled state possible, indistinguishable
from *unknown*, and there was no evidence available to tell them apart.

# Fix 1 — read the line IME does write

`IME-USER-SESSION-ZERO-APPS` matches the zero-app line and routes it through the **same**
`OnUserSessionCompleted` callback. Downstream this genuinely *is* a completed user session, so
the adapter's pending-apps deferral and its fire-once flag apply unchanged.

This is not a weakened gate — it is IME's own verdict. The zero-app branch locks the ESP in with
an *empty* application list, and the sibling emit site in `AppWorkloadAbstraction.ExecuteUserCheckIn`
returns `ProviderResult.ProvisioningComplete` straight after logging it. All three emit sites are
user-session-scoped (`user.RDSSessionId`, `user.IntuneId`); none is device-scoped.

Guards that stay in force:

* **Phase guard** — the observation only fires while the tracker is in `AccountSetup`. Firing
  earlier would burn the adapter's fire-once flag on a pre-sign-in (defaultuser0) timestamp that
  `IsImeUserSessionGenuine` (`>= AccountSetupEnteredUtc`) can never accept. An observation made
  before the phase was known is remembered and replayed on the transition into it, because IME
  writes its check-in lines independently of when the agent first parses the phase marker.
* **Engine guards unchanged** — arm C still requires a post-AccountSetup final ESP exit *and* a
  DAD-validated real-user desktop.
* `AreUserEspAppsSettled` returns `true` for an empty list **only** with this explicit
  observation — never for a merely empty list.

# Fix 2 — do not fail while a completion gate is holding

An independent defect, and the decisive one for the affected tenant. `advisory_completion` runs
for 30 minutes. The RealmJoin completion gate blocks completion until phase 110 and carries its
*own* bounded resolution: 60 minutes from detection, re-armed on deployment activity, hard-capped
at 4 hours. The shorter timer always won.

Session `75d6ae8e` installed **25 RealmJoin packages successfully inside the window**, the last
one 2 minutes before the failure, while the engine's own `completion_waiting` still read
`waiting on: realmjoin_resolution`.

`HandleAdvisoryCompletionDeadlineFired` now re-arms instead of failing when the RealmJoin gate is
closed *and* its deadline is still armed, emitting `completion_waiting` with trigger
`DeadlineFired:advisory_completion:CompletionGateHolding`.

Convergence comes from the gate itself: `Resolved`, `FirstDeploymentIncomplete` and `Timeout` all
set `RealmJoinFacts.Outcome`, which opens `RealmJoinGateOpen` and stops the guard applying.
Requiring the gate's deadline to still be armed keeps a gate that can no longer resolve itself
from parking the session; `AgentMaxLifetimeMinutes` bounds it regardless.

# Timeline evidence

`ime_user_session_completed` carries an `evidence` field so the basis of the verdict is legible
without the diagnostics archive:

* `user_session_completed` — IME wrote its completion line and no required app was pending.
* `zero_user_apps_enumerated` — IME reported zero targeted apps; the user phase had nothing to
  enforce.

No new event type: the fact is the same, only the observation behind it differs.

# Deployment

The pattern ships through the config API and reaches existing agents without a rebuild; an agent
that does not know the `userSessionZeroApps` action logs one Debug line and ignores it. The
*behaviour* needs an agent release. Sessions already stamped `Failed` are not reclassified — the
backend mirrors the agent verdict (`VerdictPaths.AgentFailed`) and has no independent judgement
here by design.

# Citations

* `rules/ime-log-patterns/IME-USER-SESSION-ZERO-APPS.json`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/ImeLogTracker.Handlers.cs` — `HandleUserSessionZeroApps`, the phase replay in `HandleEspPhaseDetected`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/Ime/ImeLogTracker.cs` — `AreUserEspAppsSettled`
* `src/Agent/AutopilotMonitor.Agent.V2.Core/SignalAdapters/ImeLogTrackerAdapter.cs` — `EmitUserSessionCompleted`, evidence labels
* `src/Shared/AutopilotMonitor.DecisionCore/Engine/DecisionEngine.Edge.cs` — `HandleAdvisoryCompletionDeadlineFired`
* [decision-engine.md](decision-engine.md), [post-reboot-completion-recovery.md](post-reboot-completion-recovery.md)
