---
type: Concept
title: Hello Wizard Un-Skip — vetoing the policy-disabled Hello shortcut
description: Why a single "WHfB disabled" policy read is not trusted for completion decisions — the HelloWizardStarted signal (prevention + cure) and the HelloTracker confirmation second read.
resource: /src/Shared/AutopilotMonitor.DecisionCore
tags:
  - agent
  - decision-engine
  - hello
  - completion
  - whfb
timestamp: 2026-07-13T00:00:00+02:00
---

# Hello Wizard Un-Skip

Session `772fe502` (2026-07-13) completed as *Succeeded* while the user was inside the
Windows Hello wizard. Cause: the user-scoped WHfB CSP value
(`HKLM\SOFTWARE\Microsoft\Policies\PassportForWork\{tenant}\{scope}\Policies\UsePassportForWork`)
**flip-flops** during User-ESP while Intune policy sync is still writing. A single read
caught "disabled", the engine set `HelloPolicyEnabled=false` (set-once, poll stops), and
the arm-C completion synthesized `HelloOutcome="Skipped"` → 5 s `finalizing_grace` →
`enrollment_complete` — 230 ms *after* Shell-Core event 62404 (CXID `AADHello`) proved
the wizard had launched. The wizard-start was previously an `InformationalEvent` only
(timeline pass-through, no state mutation).

Two independent defense layers fix this:

# Layer 1 — `DecisionSignalKind.HelloWizardStarted` (engine)

Shell-Core 62404 with CXID `AADHello`/`NGC` is a **genuine wizard-lifecycle observation**
(unlike the unreliable `hello_provisioning_willlaunch` registry snapshot). It is now a
dedicated signal: `ShellCoreTracker.HelloWizardStarted` event → coordinator forward
(`EspAndHelloTracker`, timestamp mirror) → `EspAndHelloTrackerAdapter` (fire-once) →
reducer. The startup backfill also replays 62404 (5-min lookback), so an agent restart
mid-wizard does not lose the observation. The reducer records the set-once fact
`DecisionState.HelloWizardStartedUtc`.

**Prevention** — every "may policy-disabled stand in for a Hello resolution?" decision
routes through one predicate (`DecisionEngine.Completion.cs`):

```
HelloPolicyDisabledWithoutWizard = HelloPolicyEnabled == false && HelloWizardStartedUtc == null
HelloSatisfiedForCompletion     = HelloResolvedUtc != null || HelloPolicyDisabledWithoutWizard
```

Once a wizard start is on record, the policy-disabled shortcut is vetoed at all five
Skipped-synthesis sites (EspExiting DeferredCompletion, DesktopArrived fast-path,
ImeUserSessionCompleted arm-C, AdvisoryCompletion deadline, AccountSetupProvisioningComplete
DeferredCompletion) *and* in the `completion_waiting` missing-prerequisites computation.
Sessions then take their pessimistic path: promote to `AwaitingHello` + arm HelloSafety
(300 s). The AdvisoryCompletion handler needed a dedicated
`HelloWizardObservedPromote` branch — its fallthrough is re-arm-or-**fail**, and
prevention alone would have failed a live-wizard session there.

**Cure** — `HandleHelloWizardStartedV1` (`DecisionEngine.Classic.cs`): when the state
already carries the engine-synthesized skip, the handler retracts it — cancel
`finalizing_grace` (if armed), null `HelloResolvedUtc`/`HelloOutcome`, back to
`AwaitingHello`, arm HelloSafety, emit `completion_waiting` (trigger
`HelloWizardStarted:UnSkip`). A real `HelloResolved` — or the HelloSafety timeout —
then decides the session.

Two deliberate design points:

1. **Synthetic-outcome discriminator.** The engine's synthesized outcomes are exact-case
   `"Skipped"` / `"Timeout"` (constants `SyntheticHelloOutcomeSkipped`/`...Timeout`);
   the tracker vocabulary is all-lowercase (`completed`, `skipped`, `not_configured`,
   `timeout`, `wizard_not_started`). The cure only ever retracts the exact-case
   `"Skipped"` — tracker-posted resolutions and the deliberate HelloSafety `"Timeout"`
   are never touched.
2. **First fact-nulling reducer.** DecisionState facts are otherwise set-once/monotonic.
   The retraction is legal exactly here because the fact is provably engine-synthesized
   and provably wrong (the wizard it claimed would never appear is running). The cure is
   **stage-agnostic** (guarded by the synthetic outcome, not by `Stage==Finalizing`):
   a synthetic skip can also sit parked behind a closed RealmJoin completion gate, whose
   release path completes on `HelloResolvedUtc != null` — the same bug through another
   door.

# Layer 2 — confirmation second read (HelloTracker)

`HelloTracker.ApplyPolicyRead` (extracted state machine, `HelloTracker.cs`):

| Read | Effect |
|------|--------|
| `enabled` (first) | commit immediately (pessimistic direction; `confirmedReads=1`) |
| `disabled` (first) | mark pending only; 10 s poll timer stays alive |
| `disabled` while pending | commit disabled (`confirmedReads=2`) |
| `enabled` while pending | commit enabled + `flipFlopDetected=true` (the 772fe502 catch) |
| `null` while pending | clear pending, keep polling (a vanished value is flip-flop evidence) |

While pending, `_isPolicyConfigured` stays false, so the Hello wait timers use the
longer policy-unknown cadence — the safe direction. The confirmation audit fields ride
on the `hello_policy_detected` event `Data`.

# Residual risk

A wizard that starts only *after* `Completed` (agent already terminal + shut down) is
still not catchable — the dispatch guard dead-ends post-terminal signals by design. The
confirmation read shrinks the window for the stale-disabled commit; the wizard signal
closes it for every ordering where 62404 fires before the 5 s grace elapses.

# Citations

- Reducer handler + prevention predicate: `src/Shared/AutopilotMonitor.DecisionCore/Engine/DecisionEngine.Classic.cs` (`HandleHelloWizardStartedV1`), `.../DecisionEngine.Completion.cs` (`HelloSatisfiedForCompletion`), `.../DecisionEngine.Edge.cs` (`HelloWizardObservedPromote`)
- Agent signal path: `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/ShellCoreTracker.cs` (62404 + backfill), `EspAndHelloTracker.cs`, `SignalAdapters/EspAndHelloTrackerAdapter.cs`
- Confirmation read: `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/HelloTracker.cs` (`ApplyPolicyRead`)
- Fixtures: `tests/fixtures/signal-kinds/hello-wizard-started-v1.json`, `tests/fixtures/enrollment-sessions/userdriven-hello-wizard-unskip-v1.jsonl`
- Related: [Decision Engine](decision-engine.md)
