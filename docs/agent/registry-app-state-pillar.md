---
type: concept
title: Registry App-State Second Pillar
description: Why the agent reads IME's authoritative per-app state straight from the registry (Win32Apps / EspTrackingWin32Apps / StatusServiceReports), how the snapshot-and-diff observer works, and how registry/log reconciliation doubles as the built-in IME log-pattern drift alarm.
resource: agent
tags: [ime, registry, win32apps, observability, reconciliation, pattern-drift, wdp]
timestamp: 2026-08-18
---

# Schema

## Why a second pillar

The 2026-08-17 audit of the decompiled IME (1.97.107 / 1.104.102) proved that IME log
wording is an unstable contract: Microsoft removed the old V1/V2 processing pipeline
(killing 9 patterns silently), reworded the post-remediation line, changed the
report-delta quoting between 1.97 and 1.104, and deleted `[DO TEL]` entirely. The
registry, by contrast, is IME's *internal product contract* — the ESP page and Intune
reporting consume it — and cannot drift casually.

Architecture decision (Oliver, 2026-08-17): **hybrid**. Logs stay the narrative
(downloads, retries, exit codes, scripts — the timeline). The registry is the stable
truth for terminal state. Divergence between the two IS the alarm that log patterns
drifted — turning silent data loss into a Warning event.

## Observed surfaces

All under `HKLM\SOFTWARE\Microsoft\IntuneManagementExtension` (64-bit view), verified
against decompiled IME source:

| Surface | Content |
|---|---|
| `Win32Apps\<userGuid>\<appGuid>_<rev>` | `EnforcementStateMessage` JSON (EnforcementState + ErrorCode), `ExitCode`, `Intent`. Device context = `Guid.Empty` user. Non-GUID subkeys (GRS, OperationalState, Reporting, ProvisioningProgress) are skipped. |
| `EspTrackingWin32Apps\<userGuid>\<appGuid>_<rev>` | Which apps IME registered for ESP tracking, `EspTrackingWin32AppPhase`. |
| `SideCarPolicies\StatusServiceReports\<userGuid>\<appId>` | The `AppInstallStatusReport` the ESP page renders (`Status`: 1000=Installed, 1001=Installing, 3000=Failed, 2000er=NotApplicable). |

## Mechanism

`ImeRegistryAppStateObserver` + `ImeRegistryAppStateHost` (always-on, no config gate —
precedent `EspPolicyProviderStallHost`):

- **One recursive `RegistryWatcher`** on the IME root covers all three surfaces;
  changes are debounced (2 s trailing edge) into a tick. A 60-s periodic tick is both
  the reconciliation-settle evaluator and the poll-only fallback when the watcher
  fails to arm (`collector_degraded`, once).
- **Snapshot-and-diff**: `RegNotifyChangeKeyValue` gives coalesced key-scope edges
  only, so every tick re-reads the surfaces and diffs against the previous snapshot.
- **Silent baseline**: the state present at agent start is captured without events.
  Win32Apps keys survive re-enrollments — replaying them would be the registry twin
  of the historic-IME-replay bug. Only apps that *change during the session* are
  reported or judged.
- **`registry_app_state`** (Info): emitted per real field change
  (enforcementState/errorCode/exitCode/statusServiceStatus/espTracked/espPhase),
  capped at 200/session (cap announced once as Warning).
- **`app_state_reconciliation`** (Warning, once per app, immediate upload): after a
  changed entry has been terminal (EnforcementState 1000er=success / 5000er=error,
  or StatusService 1000er/3000) for ≥ 90 s, its outcome is compared against the
  log-derived `AppPackageState` from `ImeLogHost.AllKnownPackageStates`. Divergence
  reasons: `registry_error_log_installed`, `registry_success_log_error`,
  `app_unknown_to_log_tracking` (only when the tracker is actively tracking apps —
  an idle tracker is not judgeable).
- **Pure observability**: nothing feeds the DecisionEngine (same rule as
  oobe-state observability).

## WDP reuse (Paket 3)

The same host archetype (RegistryWatcher subtree + debounce + snapshot-diff) is the
intended vehicle for WDP live progress: watch
`HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotSettings\DevicePreparation\BootstrapperAgent`
(`ProgressUpdates` rows, `ResumeContext.ProvisioningResult` terminal marker) with the
RealmJoinWatcher appearance-watcher pattern for not-yet-existing keys, plus an
Event-142 channel watcher (`Microsoft-Autopilot-BootstrapperAgent/BootstrapperAgentServiceLogProvider`,
channel handling precedent in `DiagnosticsPackageService`). Win32Apps keys are written
on WDP devices too — the pillar works on both rails unchanged.

# Examples

Timeline evidence of a drifted pattern set: apps install fine (registry says
EnforcementState 1000) but the timeline shows no `app_install_completed` — each app
raises one `app_state_reconciliation` with `reason=app_unknown_to_log_tracking`.
Operator response: run `/ime-pattern-validate` against the session, fix patterns,
backend-deploy (no agent release needed for pattern fixes).

# Citations

- `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/ImeRegistryAppStateObserver.cs`
- `src/Agent/AutopilotMonitor.Agent.V2.Core/Orchestration/Hosts/ImeRegistryAppStateHost.cs`
- `tasks/ime-v1-code-audit-2026-08-17.md` (decompiled-source audit; registry surface catalog)
- Decompile archive: `okieselbach/ime-decompiles` (private), `C:\Code\GitHubRepos\ime-decompiles\<version>\decompiled`
