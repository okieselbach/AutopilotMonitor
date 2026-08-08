---
type: Concept
title: Continue-Anyway Observation Mode — Device-Phase ESP Failure Defang
description: Operator-set tenant opt-in that turns an immediate Device-phase ESP terminal failure on a Continue-Anyway-enabled profile into a bounded 60-minute observation; the DAD-validated real-user desktop plus the Hello gate detect the true end, and the session completes as Succeeded with an esp-soft-failure marker.
resource: /src/Shared/AutopilotMonitor.DecisionCore/Engine/DecisionEngine.Edge.cs
tags:
  - agent
  - decision-engine
  - esp
  - continue-anyway
  - soft-failure
  - tenant-config
timestamp: 2026-08-08T00:00:00+02:00
---

# Continue-Anyway Observation Mode

## Problem

A Classic user-driven enrollment whose Device ESP hits the configured terminal timeout
(`syncFailureTimeoutMinutes`, typically 30) fails **before AccountSetup is ever entered**.
The classic advisory defang (`HandleEspTerminalFailureV1`) requires
`AccountSetupEnteredUtc != null`, so these sessions hard-fail immediately — even when the
ESP profile allows "Continue anyway" and users demonstrably dismiss the failure screen and
reach a working desktop. A fleet held at the timeout wall by one slow blocking app then
shows ~100% Failed (tenant c9787ba2: 948/1000 `esp_terminal_failure`, culprit "Encompass
Hybrid Installer"), which tells the tenant nothing.

## Mechanism

Opt-in flow (default off — absence keeps the immediate hard fail):

1. **Tenant setting** `EnableEspContinueAnywayObservation` (`TenantConfiguration`,
   operator-set only, NOT exposed in the tenant admin settings UI; the toggle lives in
   the Global-Admin tenant management modal next to Bootstrap Token / Unrestricted Mode,
   and the field is GA-gated on both write paths — the generic PUT silently reverts it
   for tenant admins, the field patch rejects it with 400) → served via `agent/config`
   (`AgentConfigResponse`, ConfigVersion 37).
2. **Agent** stamps `espContinueAnywayObservationEnabled=true` into the
   `EspConfigDetected` signal payload (both emitters: the orchestrator FirstSync
   bootstrap and `DeviceInfoCollector.PostEspConfigDetectedSignal`) — only when enabled,
   so the kernel stays config-free and replay/recovery reconstruct the fact from the
   signal log.
3. **Engine** (`HandleEspTerminalFailureV1`): a terminal ESP failure is defanged into an
   `esp_failure_advisory` (`advisoryReason=esp_failure_defanged_continueanyway_observation`)
   when ALL hold: profile allows Continue-Anyway, observation opt-in observed,
   `AccountSetupEnteredUtc == null`, and the scenario is Classic/Unknown with
   `PreProvisioningSide=None` and no registry self-deploying marker (WhiteGlove /
   SelfDeploying / DevicePreparation have no interactive user to press the button).
4. **Observation window**: the shared `AdvisoryCompletion` deadline is armed with a
   **60-minute** window (classic advisory: 30) — long enough for failure screen →
   Continue anyway → sign-in → desktop, or a full device-phase re-run via "Try again".

## Detecting the real end

The active-advisory-without-AccountSetup shape is unique to this arming site, so
`IsContinueAnywayObservationActive(state)` (advisory recorded ∧ not resolved ∧
AccountSetup never entered) needs no separate opt-in re-check. Three outcomes:

* **Continue anyway** — the DAD-validated real-user desktop arrives (defaultuser0/SYSTEM
  excluded at the detector) and the Hello gate is satisfied (resolved, or policy
  explicitly disabled without an observed wizard): `HandleDesktopArrivedV1` completes
  eagerly through Finalizing (trigger `DesktopArrived:ContinueAnywayObservation`) — the
  session end reflects the real end, not the window edge. The IME user-session gate is
  deliberately waived: it anchors on `AccountSetupEnteredUtc`, which is structurally
  absent here. The terminal `enrollment_complete` carries `espSoftFailure=true`,
  `completionSource=continue_anyway_observation` and the failed category; the backend
  stamps `EspSoftFailure`/`CompletionSource` on the Sessions row (+ SessionsIndex
  mirror) and the web renders the amber "with issues" badge plus a detail banner.
  Sessions completing under the CLASSIC advisory (AccountSetup entered) carry the same
  marker with `completionSource=continue_anyway_post_accountsetup`.
* **Try again** — the ESP re-runs the failed category; `DeviceSetupProvisioningComplete`
  resolves the advisory via the 4910a5a5 recovery hook (`esp_failure_advisory_resolved`)
  and the session continues on the normal rails, completing clean (no marker).
* **Nothing** — the window expires: un-defang to `enrollment_failed` with the original
  `esp_terminal_failure` reason and
  `advisoryReason=continue_anyway_observation_window_expired_without_completion_evidence`;
  `LastFailureTrigger=EspTerminalFailure` keeps the likely-stuck app promotion on. Every
  observed session therefore still gets a terminal verdict ≤ ~60 min after the failure.

Deadline-fire edge cases mirror the classic paths: desktop + unknown Hello promotes to
`AwaitingHello` with the HelloSafety window (never-observed promote), and enforcement
progress since arming re-arms the window (30-min re-arm, convergent) instead of failing a
device mid-"Try again".

## Status semantics

No new terminal status: the session is `Succeeded` + `EspSoftFailure=true` (user
decision 2026-08-08). Success-rate conventions stay untouched; honesty lives in the
badge, the detail banner, and ANALYZE-ESP-004 (which continues to fire on the un-defang
expiry path via `mayHaveContinuedAnyway`).

# Examples

Session `53d1e9f6-4eef-4f3b-a999-c63e96c5148d` (tenant c9787ba2, 2026-08-07) is the
motivating shape: DeviceSetup/Certificates failed at the 30-min wall while "Encompass
Hybrid Installer" was still installing, AccountSetup 0/5, Continue-Anyway allowed —
hard-failed under the default semantics.

Tests: `ContinueAnywayObservationTests` (DecisionCore.Tests) — defang, 60-min arming,
opt-out/scenario regressions, eager desktop completion with marker, clean-completion
marker absence, expiry un-defang, hello promotes, category recovery.

# Citations

* `src/Shared/AutopilotMonitor.DecisionCore/Engine/DecisionEngine.Edge.cs` — arming site,
  observation branches in `HandleAdvisoryCompletionDeadlineFired`,
  `IsContinueAnywayObservationActive`.
* `src/Shared/AutopilotMonitor.DecisionCore/Engine/DecisionEngine.Classic.cs` —
  `HandleDesktopArrivedV1` observation completion, `BuildEnrollmentCompleteEffect`
  soft-failure marker.
* `src/Backend/AutopilotMonitor.Functions/Services/EventIngestProcessor.Classification.cs`
  / `TableStorageService.Sessions.cs` — session stamping + index mirror.
* `src/Web/autopilot-monitor-web/components/SessionStatusBadge.tsx` — amber badge.
