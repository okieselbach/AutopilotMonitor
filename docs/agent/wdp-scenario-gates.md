---
type: concept
title: WDP Scenario Gates & Completion Backstop
description: How the agent gates Autopilot/ESP-only machinery off on Windows Device Preparation (WDP) sessions and how a WDP session resolves without any ESP signal.
resource: agent
tags: [wdp, device-preparation, decision-engine, collectors, completion]
timestamp: 2026-08-18
---

# Schema

Windows Device Preparation (Autopilot v2, "WDP") has **no deployment profile and no ESP**.
Before the afee7ae0 audit (2026-08-18) the agent ran the full Classic apparatus anyway:
misleading `autopilot_profile_missing` warnings, a ZTD event-log query + HTTP probe,
`esp_config_detected` "unknown/unknown" noise, an Autopilot-channel noise flood
(EventIDs 100/417/418/1005, 900+ occurrences per session), a subtree registry watcher
woken by every WDP progress write, and — worst — no completion path when Hello never
resolved (park until the 360-min watchdog).

## Detection: deterministic vs. fallback

`EnrollmentRegistryDetector` classifies v2 through three rules. Only the first is
**deterministic WDP**:

1. `AutopilotSettings\DevicePreparation\BootstrapperAgent` subkey with non-empty
   `ExecutionContext` → the WDP policy orchestration tree, present before the agent
   starts. Exposed as `EnrollmentRegistryDetector.IsDeterministicDevicePreparation()`
   and the `DevicePreparationExecutionContextRule` constant.
2./3. `CloudAssignedDeviceRegistration == 2` / `CloudAssignedEspEnabled == 0` —
   profile-derived legacy signals. They imply a REAL Autopilot profile (just ESP-less),
   so every gate below deliberately ignores them: **gates require the deterministic
   marker and fail toward Classic behavior.**

## Agent-side gates (all keyed to the deterministic marker)

* `DeviceInfoCollector.CollectAutopilotProfile`: no `autopilot_profile` event, no
  `autopilot_profile_missing` warning, no ZTD event-log query, no ZTD HTTP probe.
  `enrollment_type_detected` still fires. (WDP devices DO carry an
  `AutopilotPolicyCache` with `ProfileAvailable=0` / error 807 `ZtdDeviceIsNotRegistered`
  — Windows runs the ZTD download regardless — so the probe fired for real before.)
* `CollectEspConfigurationLocked`: `esp_config_detected` is emitted only when at least
  one FirstSync value or ESP tracking list exists (generic null-gate, mirrors the
  orchestrator bootstrap's signal gate) — fixes "unknown/unknown" for WDP AND plain
  Entra joins.
* `DefaultComponentFactory`: `EspPolicyProviderStallHost` (60-s poll of the ESP-only
  `EnrollmentStatusTracking` key) is not created on WDP.
* `EspAndHelloTracker.Start`: `ProvisioningStatusTracker` is not started on WDP — its
  subtree watcher sits on `AutopilotSettings`, exactly where WDP's BootstrapperAgent
  writes progress, so it woke on every write only to read three ESP category values
  that never exist there.
* `ModernDeploymentTracker`: `DevicePreparationNoiseEventIds` {100, 417, 418, 1005}
  are suppressed entirely on WDP (before `FormatDescription()`); Critical (level 1)
  always passes; genuine WDP diagnostics (e.g. 408) keep flowing. The Autopilot error
  backfill (807/809/815/908 replay) is skipped — 807 is the EXPECTED state on WDP.
  The watcher itself stays on: WDP's provisioning stack logs into the same channels.
* `StallProbeCollector`: skips the ESP category registry scan on WDP and unions the
  noise IDs into its harmless set; the channel scans remain (they carry genuine WDP
  diagnostics during a stall).
* `EnrollmentTerminationHandler`: the `app_install_starved` terminal sweep is skipped
  on WDP — post-desktop installs are the DESIGNED order there (only DPP-policy apps
  run during provisioning); per-app data still travels via `app_tracking_summary` and
  final-status.json.

## DecisionCore: WDP completion without ESP facts

* **Seeding**: `EnrollmentFactsObserved` carries `enrollmentTypeDeterministic`;
  the deterministic marker seeds `DevicePreparation` at **High** confidence (same
  registry-determinism standard as the self-deploying OobeConfig bits), the fallback
  rules stay Medium. The three explicit downgrade guards from d0ab3eee remain as
  defence in depth.
* **Arm D** (`ShouldTransitionToAwaitingHello`): on WDP the DAD-validated real-user
  desktop is a legitimate promotion basis — arms A–C are unsatisfiable by construction
  (no provisioning categories, no Shell-Core 62407 final exit). With a disabled Hello
  policy the fast-path now completes at desktop arrival.
* **Prerequisites**: `completion_waiting` never lists
  `account_setup_provisioning_complete` on WDP (unsatisfiable; mirrors the
  `skipUserEsp` exemption).
* **Backstop**: `DeadlineNames.DevicePrepCompletion` (30 min, replay-safe base) is
  armed by `HandleDesktopArrivedV1` when a WDP session parks desktop-first with Hello
  pending. On fire it mirrors the HelloSafety timeout: synthetic
  `HelloOutcome=Timeout` + completion through Finalizing. This closes the
  "no Hello → park until watchdog" hole; a timer surviving into a terminal stage is
  swallowed by the post-terminal dispatch guard.
* **Tripwire**: `DecisionStepProcessor.IsParkedWithoutDeadline` has a WDP branch
  (dead-end zone = desktop arrived) so a regression in the backstop becomes visible
  as `session_parked_without_deadline`.

# Examples

Session afee7ae0 (the audit trigger): Succeeded in 9:32 min, but its timeline carried
`autopilot_profile` (807), an `autopilot_profile_missing` WARN, `esp_config_detected`
unknown/unknown, 30 `modern_deployment_log` rollup events and 10 `app_install_starved`
entries — all structurally meaningless on WDP. After the gates, the same session shape
emits none of these; completion rode Hello-skipped + desktop as before.

Deliberately NOT changed (open follow-ups, see the WDP audit topic memory): the ESP
phase vocabulary (`esp_phase_changed`/stage names ride the Classic rails on WDP),
subscription to the `BootstrapperAgentServiceLogProvider` event channel, and
`registry_app_state` as the primary WDP app source.

# Citations

* `src/Agent/AutopilotMonitor.Agent.V2.Core/Security/EnrollmentRegistryDetector.cs` — deterministic rule + helper
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/DeviceInfo/DeviceInfoCollector.NetworkAndSecurity.cs` — profile/ESP emission gates
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/ModernDeploymentTracker.cs` — `DevicePreparationNoiseEventIds`
* `src/Shared/AutopilotMonitor.DecisionCore/Engine/DecisionEngine.DevicePreparation.cs` — backstop deadline
* `src/Shared/AutopilotMonitor.DecisionCore/State/EnrollmentScenarioProfileUpdater.cs` — High/Medium seeding
* Tests: `DevicePreparationEngineTests`, `ModernDeploymentTrackerDevicePrepTests`, `EnrollmentTerminationHandlerTests.StarvedApps_skipped_on_device_preparation`, `DeviceInfoCollectorEspRefreshTests.NoEspEvidenceAtAll_SuppressesTheEvent`
