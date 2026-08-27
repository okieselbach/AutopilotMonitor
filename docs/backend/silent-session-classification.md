---
type: Concept
title: Silent-Session Classification (Sweep, Max-Lifetime, Retro-Reconcile)
description: How a session whose agent went silent gets its honest status — the two-stage maintenance sweep (2h Stalled marker, 5h timeout classification), the EnrollmentTimeoutClassifier rule order incl. the WhiteGlove Part-2 and self-deploying gates, which ingest paths share the classifier, and the admin retro-reconcile modes.
resource: src/Backend/AutopilotMonitor.Functions/Services/EnrollmentTimeoutClassifier.cs
tags:
  - backend
  - maintenance
  - session-status
  - classification
  - whiteglove
  - self-deploying
timestamp: 2026-08-27T20:00:00+02:00
---

# Silent-Session Classification

A session only ever becomes terminal through evidence. When the agent stops reporting
(power-off, reboot without relaunch, Wi-Fi never re-associating, max-lifetime watchdog),
the backend must decide the status from what it last saw — it never hard-fails a session
for being silent. One pure function holds that decision,
`EnrollmentTimeoutClassifier.ClassifyTimedOutSession`, and every silent-session path calls it
so a session gets the same verdict regardless of which path noticed the silence first.

# Schema

## Inputs (`EspProvisioningRollup` + session flags)

`ExtractRollup(events)` distills the event stream into facts: Device/Account Setup
subcategory counts (incl. the 30s fallback-confirmed messages), `HasExplicitFailure`,
`HasEnrollmentComplete` vs. `HasTerminalComplete` (the Part-1 `whiteglove_complete`),
`HasAgentEmergencyBreak`, `DesktopArrived`, `HelloResolved`, `HelloPolicyDisabled`,
`SkipUserEsp`, RealmJoin detection/resolution, `HasAppInstallFailure` (any
`app_install_failed`), and `HasAgentMaxLifetimeTimeout` (either max-lifetime shape:
`enrollment_failed`/`agent_timeout` or `agent_shutting_down`/`max_lifetime` — the agent is
provably gone). Session-row flags are passed alongside:
`IsPreProvisioned` + `ResumedAt` (WhiteGlove Part 2) and `IsSelfDeployingProfile`
(registry-confirmed `CloudAssignedOobeConfig 0x20|0x40`, set at registration).

## Rule order (first match wins)

| # | Condition | Verdict |
| --- | --- | --- |
| 1 | `HasExplicitFailure` | Failed |
| 1b | `IsWhiteGloveAwaitingUser`: pre-provisioned + resumed + Device Setup done + **no** user evidence since resume + no explicit failure/enrollment_complete | AwaitingUser within grace; Succeeded (honest WG reason) past grace |
| 1c | `IsSelfDeployingProvisioned`: self-deploying profile + Device Setup done + no explicit failure | **Succeeded** (reconcile) — no grace, no AwaitingUser: the profile has no user phase |
| 2 | Account Setup all-succeeded, or `enrollment_complete`, or `whiteglove_complete` on a **non-resumed** session | Succeeded |
| 3 | `HasAgentEmergencyBreak` | Incomplete immediately (agent self-destructed, nothing can arrive) |
| 4 | `DesktopArrived` + (Hello terminal, or Hello disabled + SkipUserEsp) | Succeeded (user provably finished) |
| 5 | Device Setup done + within grace + **no** max-lifetime watchdog observed | AwaitingUser |
| 5a | Device Setup done + past grace (or watchdog observed) + `DesktopArrived` + no `app_install_failed` | **Succeeded** — completed (assumed), `r5_assumed` |
| 5b | Device Setup done, otherwise past grace (or watchdog observed) | Incomplete |
| 6 | otherwise | Incomplete |

Grace = `ResolveGraceHours(SessionGraceHours, AbsoluteMaxSessionHours)` (~51h default),
measured from `ResumedAt ?? StartedAt`. Every Succeeded-reconcile reason carries the
silence-transparency clause (`AppendReconcileTiming`: last agent contact, silence, verdict time).

## Why rule 1c exists (kiosk tenant audit 2026-08-23)

On a self-deploying profile Device ESP all-succeeded IS the enrollment's end; the agent's
own SelfDeploying terminal only adds a 5-min confirmation window
(`DecisionEngine.SelfDeploying.s_deviceOnlyEspDetectionWindow`). Tenant `aebdce78` (school,
Wi-Fi ThinkBooks, a language-change app forcing a reboot right after Device ESP) lost that
window on 836 of ~970 sessions in one week: the agent died 0–4 min after
`DeviceSetup fallback_confirmed`, the reboot relaunch never registered (30s registration
budget on a Wi-Fi link still associating), and rule 5 labeled finished kiosks
"awaiting user / Account Setup 0/5" → Incomplete 51h later. Nobody signs in on these devices
(prepared during the holidays, boxed, in service) — AwaitingUser was factually wrong and
Incomplete turned a working rollout into a red wall. Other self-deploying tenants completed
normally in the same window, so this is a classification gap, not a profile-wide agent bug.
Agent-side companion fix: `SessionRegistrationHelper` now waits up to 15s for a network link
and retries 6× (~62s backoff) instead of 5× (~30s). Measured 2026-08-27 (builds
2.0.1428/1429, both carrying the fix): the reconcile share did not move, and the backend saw
no registration attempt from any relaunch — no success, no 4xx, zero `register:superseded`
in the tenant — so the relaunch never reaches the network at all (device powered off after
ESP, or the relaunch never runs). The sweep reconcile is the permanent completion path for
this profile, not a stopgap.

## Callers (all pass both gate inputs)

| Path | File | Notes |
| --- | --- | --- |
| Sweep stage 1 (2h agent-silent marker) | `MaintenanceService.MarkStalledSessionsAsTimedOutAsync` | Default: InProgress → Stalled. Gates run BEFORE the Stalled write: `TryMarkWhiteGloveAwaitingUserAsync` (AwaitingUser) and `TryReconcileSelfDeployingAsync` (Succeeded). Only flagged sessions pay the event read. |
| Sweep stage 2 (SessionTimeoutHours, 5h default) | same | InProgress/Stalled/AwaitingUser past the window and past the 2h silence cutoff → full classification; AwaitingUser within grace is skipped without an event read. |
| Agent max-lifetime (`enrollment_failed`/max_lifetime, `agent_shutting_down`/max_lifetime) | `EventIngestProcessor.ApplyMaxLifetimeVerdictAsync` | Watchdog = "agent stopped waiting", not a verdict; classified from evidence. Pending sessions exempt. |
| Late-telemetry reconcile | `EventIngestProcessor.TryLateTelemetryReconcileAsync` | Heal-only: applies a Succeeded verdict to Incomplete/AwaitingUser rows when straggler events arrive. |
| Agent stall probe (`session_stalled`) | `EventIngestProcessor.TryMapStallToWhiteGloveAwaitingUserAsync` | Only the WG gate (1b); a stall probe on a self-deploying device with Device Setup done means the agent is alive and something else blocks — stays Stalled. |
| Admin retro-reconcile | `LegacyReclassificationService` | Modes `legacy_timeouts`, `pending_orphans`, `self_deploying_silent` (Incomplete/AwaitingUser/Stalled rows with the profile flag, heal-only to Succeeded, admin-marked rows skipped). `POST /api/maintenance/reclassify-legacy?mode=…&dryRun=false&tenantId=…&maxSessions=…`, dry-run by default. |

## Why rule 5a exists (calibration read 2026-08-27)

The verdict-calibration matrix showed r5-Incomplete devices re-enrolling at 2.6 % within
7 days — the succeeded background (`agent:complete_soft` sits at 2.8 %), not the ~29 %
failure level — consistently across tenants, and half the sampled sessions were healthy
desktops with a clean app record whose agent died before the Hello terminal (some with the
agent's own "AccountSetup treated as complete" synthesis as the literally last event). Past
the full grace, "the user phase is still running" is no longer a possible explanation, so
desktop + zero `app_install_failed` + no explicit failure now reconciles to Succeeded
("completed (assumed)") instead of a red Incomplete. Sessions without a desktop (user never
began sign-in: battery-drained, powered off, max-lifetime) and sessions with any failed app
stay Incomplete — "completed" must not overclaim. Rule 4 remains the IMMEDIATE reconcile
(desktop + Hello proof); 5a only fires where waiting is over. Desktop arrival alone is still
rejected as a live completion signal.

## Closed gap: max-lifetime AwaitingUser (2026-08-27)

After max-lifetime the agent writes `enrollment-complete.marker` and self-destructs, so an
AwaitingUser verdict from that path could never heal — the calibration correction stream
confirmed it: all five observed `maxlife:r5_awaiting` episodes expired to Incomplete
unhealed, while live-agent `sweep:r5_awaiting` parks upgraded 7 of 26 to Succeeded. Rule 5
therefore skips the grace once `HasAgentMaxLifetimeTimeout` is in the stream and decides the
terminal fork (5a/5b) immediately; the max-lifetime ingest appends its trigger event to the
read stream so the fact is never missed, and late straggler telemetry still heals a terminal
verdict via `TryLateTelemetryReconcileAsync`. WhiteGlove rule 1b deliberately keeps its
park — sealed/boxed devices do heal (the observed `maxlife:r1b_awaiting` episode resolved to
Succeeded).

# Examples

* Session `195593e2` (tenant aebdce78): `DeviceSetup 4/4` + `fallback_confirmed`, `AccountSetup 0/5`
  (IME false positive), Hello disabled, SkipUser=True, silent after 13:01 → rule 1c →
  Succeeded "self-deploying profile — Device Setup fully provisioned … agent went silent
  before confirming completion. Agent last reported 2026-08-21 13:01 UTC …".
* Same event shape on a user-driven profile → rule 5 → AwaitingUser (within grace) /
  Incomplete (past grace). Guarded by
  `Classify_same_stream_without_self_deploying_flag_keeps_user_phase_verdicts`.
* fairstone WG Part 2 reseal-reboot, technician powers off at logon screen → rule 1b →
  AwaitingUser; user unboxes days later → ingest completion heals to Succeeded.

# Citations

* `src/Backend/AutopilotMonitor.Functions/Services/EnrollmentTimeoutClassifier.cs` — rollup + rule order, `IsWhiteGloveAwaitingUser`, `IsSelfDeployingProvisioned`, reason helpers.
* `src/Backend/AutopilotMonitor.Functions/Services/MaintenanceService.cs` — `MarkStalledSessionsAsTimedOutAsync`, `TryMarkWhiteGloveAwaitingUserAsync`, `TryReconcileSelfDeployingAsync`.
* `src/Backend/AutopilotMonitor.Functions/Services/EventIngestProcessor.Classification.cs` — max-lifetime verdict, late-telemetry reconcile, stall mapping.
* `src/Backend/AutopilotMonitor.Functions/Services/LegacyReclassificationService.cs`, `Functions/Admin/ReclassifyLegacySessionsFunction.cs` — retro modes.
* `src/Backend/AutopilotMonitor.Functions/Services/TableStorageService.Maintenance.cs` — `GetAgentSilentSessionsAsync`, `GetStalledSessionsAsync`, `GetSelfDeployingSilentSessionsAsync`.
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Security/SessionRegistrationHelper.cs` — link wait + 6-attempt registration budget.
* `src/Shared/AutopilotMonitor.DecisionCore/Engine/DecisionEngine.SelfDeploying.cs` — the agent-side 5-min SelfDeploying terminal window.
* `src/Backend/AutopilotMonitor.Functions.Tests/EnrollmentTimeoutClassifierTests.cs` — rule coverage incl. the 2026-08-23 self-deploying block.
