---
type: Reference
title: V2 Agent — Decision Engine (DecisionCore)
description: The pure-reducer state machine that decides enrollment completion/failure — signals, state, effects, the A/B/C completion arms, and its invariants.
resource: /src/Shared/AutopilotMonitor.DecisionCore
tags:
  - agent
  - decision-engine
  - reducer
  - completion
timestamp: 2026-07-11T00:00:00+02:00
---

# V2 Agent — Decision Engine (DecisionCore)

DecisionCore (`src/Shared/AutopilotMonitor.DecisionCore`, netstandard2.0) is a **pure
reducer**: a deterministic state machine with no I/O, no clock reads, no threads. The
agent feeds it an ordered stream of *signals*; it returns a new immutable state plus a
list of *effects* the host must execute. Because it is pure, the same persisted signal
stream always reproduces the same journal and terminal outcome — that is what makes
crash recovery a replay instead of a guess (determinism guarantee "L.5",
`Engine/IDecisionEngine.cs`).

# The reducer contract

```
DecisionStep step = engine.Reduce(DecisionState oldState, DecisionSignal signal)
// DecisionStep = { NewState, Transition, Effects[] }
```

- `Reduce` never throws: a handler exception advances only bookkeeping and journals a
  `reducer_exception` dead-end (fail-safe L.16).
- Dispatch is a switch on `(signal.Kind, signal.KindSchemaVersion)`; unknown pairs
  become journaled dead-ends, never crashes.
- Every step yields a `DecisionTransition` journal record: `Taken` (state change vs
  dead-end + reason), `StepIndex`, `FromStage/ToStage`, `Trigger`, `Guards`,
  `ReducerVersion`.

# State

`DecisionState` (schema **v4**) is an immutable value object; "change" = builder copy.
Key content:

- Identity + progress: `SessionId`, `TenantId`, `Stage`, `Outcome`, `StepIndex`,
  `LastAppliedSignalOrdinal`, `AgentBootUtc` (replay-safety anchor).
- **Facts** as `SignalFact<T>` — value **plus** `SourceSignalOrdinal` provenance, e.g.
  `AccountSetupEnteredUtc`, `EspFinalExitUtc`, `DesktopArrivedUtc`, `HelloResolvedUtc`,
  `ImeUserSessionCompletedUtc`, `HelloPolicyEnabled`, `HelloOutcome`.
- Aggregates: `AppInstallFacts`, `ScenarioProfile` + `ScenarioObservations`
  (enrollment-scenario classification, monotonic), `ClassifierOutcomes`, `RealmJoinFacts`.
- `Deadlines` — active timers the host has been asked to arm.

Stages: `SessionStarted → EspDeviceSetup → EspAccountSetup → AwaitingHello →
AwaitingDesktop → Finalizing → Completed` (plus `Failed`, `WhiteGloveCandidate/Sealed`,
`AwaitingDeviceOnlyEsp` for self-deploying, and edge stages). Terminal =
`Completed | Failed | WhiteGloveSealed`. `Finalizing` is deliberately non-terminal
(grace window). Outcomes: `EnrollmentComplete`, `EnrollmentFailed`,
`WhiteGlovePart1Sealed`, `Aborted`, `AdminPreempted`.

# Signals

`DecisionSignal` is the only input. Fields: `Kind` + `KindSchemaVersion` (dispatch key),
`SessionSignalOrdinal` (monotonic, assigned **only** by the ingress worker — never by
collectors), `OccurredAtUtc`, mandatory `Evidence` (classified `Raw | Derived |
Synthetic` — a signal without evidence is a bug), `Payload` (string dict) and
`TypedPayload` (structured sidecar).

Signal kinds by family (`Signals/DecisionSignalKind.cs`):

- **Raw observations**: `EspPhaseChanged`, `EspExiting`, `EspResumed`,
  `EspTerminalFailure`, `DesktopArrived`, `HelloResolved`, `ImeUserSessionCompleted`,
  `DeviceSetupProvisioningComplete`, `AccountSetupProvisioningComplete`,
  `AppInstallCompleted/Failed`, `AadUserJoinedLate`, `SystemRebootObserved`,
  `EspConfigDetected`, `HelloPolicyDetected`, WhiteGlove + RealmJoin kinds, …
- **Synthetic**: `DeadlineFired` (scheduler timer), `ClassifierVerdictIssued`,
  `EffectInfrastructureFailure`.
- **Lifecycle**: `SessionStarted`, `SessionAborted`, `AdminPreemptionDetected`.
- **`InformationalEvent`** — the single-rail pass-through: telemetry that must appear
  on the Events timeline but never influences decisions.

**Ordering is the ingest ordinal, never wall-clock.** Replayed IME/CMTrace lines carry
backdated source timestamps; all before/after comparisons in the reducer therefore
compare `SourceSignalOrdinal`s (e.g. "final exit after AccountSetup entry").

# Effects — the only side-channel

`DecisionEffect` kinds and how the host's `EffectRunner` treats failures:

| Kind | Purpose | Failure class |
| --- | --- | --- |
| `ScheduleDeadline` / `CancelDeadline` | arm/cancel OS timers that come back as `DeadlineFired` | **critical** — failure aborts the session (a lost timer means silent parking) |
| `EmitEventTimelineEntry` | timeline events incl. terminal `enrollment_complete` / `enrollment_failed` | transient — retried 100/400/1600 ms |
| `PersistSnapshot` | hint to write the recovery snapshot | transient |
| `RunClassifier` | scenario classification; verdict returns as a `ClassifierVerdictIssued` signal | optional — exception ⇒ Inconclusive, never aborts |

Terminal and state-changing timeline effects carry a structured **audit trail** in
`TypedPayload` (built by `DecisionAuditTrailBuilder`): `decisionSource`, `trigger`,
`sessionStage`, `stepIndex`, `signalsSeen`, `signalEvidence`, `signalTimestamps`,
`scenario` — so every completion/failure in the portal is explainable from its own
event payload.

# Completion logic

## The A/B/C arms

The arms are **not** three pipelines — they are three alternative sufficient conditions
inside one guard, `ShouldTransitionToAwaitingHello`
(`Engine/DecisionEngine.Shared.cs`), which gates promotion out of the ESP stages toward
Hello/desktop completion:

- **Arm A — registry-strong AccountSetup success**: `AccountSetupProvisioningSucceededUtc`
  is set (the ESP registry reported `categorySucceeded`). Merely *entering* AccountSetup
  is insufficient — Shell-Core 62407 fires at every ESP page transition.
- **Arm B — SkipUser flow**: `ScenarioObservations.SkipUserEsp == true` — with no
  User-ESP page, the first `esp_exiting` *is* the genuine final exit.
- **Arm C — 4-fact eager completion** (all four mandatory):
  1. AccountSetup was entered, and
  2. a post-AccountSetup ESP final exit occurred (ordinal-ordered), and
  3. a *genuine* IME user-session completion (at-or-after the AccountSetup anchor —
     guards against the `defaultuser0` ghost), and
  4. a DAD-validated real-user desktop arrival.
  Arm C applies eagerly the same conjunction the 30-minute `AdvisoryCompletion`
  backstop would eventually trust.

**Layering note:** Arm C's app-install hardening lives in the **adapter**, not the
reducer. `ImeLogTrackerAdapter` defers posting `ImeUserSessionCompleted` while required
user-ESP Install-intent apps are still pending (IME logs "Completed user session" per
*pass*, not once per session — sessions 14690fc2/6cb01530). The reducer stays free of
app-pending guards; adapters sanitize evidence before it becomes a signal.

## Completion attempt sites and sequence

Completion can be attempted from whichever fact arrives last: `HelloResolved`,
`DesktopArrived` (including the Hello-disabled fast-path when `HelloPolicyEnabled ==
false` and an arm holds), `EspExiting`, `ImeUserSessionCompleted`,
`AccountSetupProvisioningComplete`, the Hello safety deadline, or the 30-minute
advisory backstop. All routes converge on `CompleteThroughFinalizingOrDefer`:

1. **Completion gates** (currently only the RealmJoin AND-gate): if RealmJoin was
   detected and is neither resolved nor timed-out (hard 60-min timeout), completion is
   *deferred* — the session stays in stage and emits `completion_waiting`.
2. `Finalizing` stage + ~5 s `FinalizingGrace` deadline.
3. Grace fires → `Completed`, `EnrollmentComplete` outcome, `enrollment_complete`
   timeline event with full audit payload.

**Self-deploying/kiosk** is a separate rail: no Hello, no user desktop —
`AwaitingDeviceOnlyEsp` with a detection deadline as the sole terminal entry, protected
by stale-fire and race guards. A registry-confirmed self-deploying profile also waives
IME false-positive AccountSetup entries.

# Invariants (violating any of these is a bug)

- **Post-terminal dispatch guard**: after `Completed`/`Failed`, every signal is a
  bookkept dead-end — **except `InformationalEvent`** (single-rail pass-through must
  keep flowing). `WhiteGloveSealed` is unguarded on purpose (Part 2 resumes fresh).
- **`InformationalEvent` never mutates decision state** — exactly one pass-through
  timeline effect, payload copied 1:1.
- **`AadUserJoinedLate` is observation-only** — never completion evidence.
- **Hello policy is orthogonal to completion** — `HelloPolicyEnabled` tunes wait
  cadence, never gates the outcome.
- **Raw IME "Completed user session" is weak evidence** — can run under `defaultuser0`;
  only meaningful with a real-user desktop and the AccountSetup anchor (Arm C).
- **Deadline bases are floored at `AgentBootUtc`** — replayed backdated signals must
  not arm past-due timers at boot.
- **Reducer-side deadline cancels need an explicit `CancelDeadline` effect** — a
  builder-only removal leaves the OS timer live.
- **No silent parking**: a non-terminal post-AccountSetup session must always hold a
  resolution-capable deadline; a 10-min dwell tripwire emits
  `session_parked_without_deadline`.

# Hosting & threading (inside the agent)

Wired by `EnrollmentOrchestrator.Start` (`src/Agent/AutopilotMonitor.Agent.V2.Core/Orchestration/`):

- `SignalIngress` — bounded queue (256, back-pressure) + **one** worker thread. Per
  signal, in order: assign ordinals → `SignalLog.Append` (durable, *before* reducing;
  append failure ⇒ reducer does not run, no ordinal gap) → `engine.Reduce` →
  `DecisionStepProcessor.ApplyStep`.
- `DecisionStepProcessor.ApplyStep` (same thread, no locks): journal append (the only
  hard-throwing path; 3 consecutive failures escalate to quarantine) → effects run
  synchronously via `EffectRunner` → best-effort snapshot → advance `CurrentState` →
  one-shot terminal-stage hook (raises the agent's `Terminated` event off-thread).
- The `DeadlineScheduler`'s `Fired` event loops back in as synthetic `DeadlineFired`
  signals.
- On restart, `RecoveryCoordinator` folds the persisted signal log through a transient
  `new DecisionEngine()` before the live pipeline starts — see
  [logs & persistence](logs-and-persistence.md).
