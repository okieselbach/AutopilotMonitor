---
type: Concept
title: MDM Reboot Coalescing — attributing the mid-ESP reboot and second sign-in to policy URIs
description: How the agent detects device-assigned MDM policies that force a coalesced reboot during ESP DeviceSetup (DM-Enterprise EventID 2800 + Shell-Core 62407 RebootCoalescing), and the ANALYZE-ESP-005 advisory that makes the invisible "second sign-in" fixable.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals
tags:
  - agent
  - esp
  - reboot
  - policy-attribution
  - second-sign-in
timestamp: 2026-07-20T00:00:00+02:00
---

# MDM Reboot Coalescing

Device-assigned MDM policies whose CSP URIs match the OS reboot-required catalog
(`HKLM\SOFTWARE\Microsoft\Provisioning\SyncML\RebootRequiredURIs`) force a **coalesced
reboot at the end of ESP DeviceSetup** — the user sees an unexpected restart and a
**second sign-in screen** before AccountSetup. This is expected OS behavior, invisible in
the Intune console; most admins simply live with it, not knowing that reassigning the
profiles to user groups removes the reboot entirely. Source research: Patch My PC,
["Avoid the second sign-in screen after Autopilot"](https://patchmypc.com/blog/autopilot-unexpected-reboot-autopilot-second-login-screen/).

The session already counts reboots (`system_reboot_detected` → `session.rebootCount`);
this feature adds the **cause attribution**.

# Signal chain

1. **EventID 2800**, `Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin`
   — logged by the SyncML engine once per policy URI that matched the reboot-required
   catalog, during device-policy application in DeviceSetup.
2. **EventID 62407**, `Microsoft-Windows-Shell-Core/Operational`, description containing
   `RebootCoalescing` — ESP/CloudExperienceHost initiating the coalesced reboot at the end
   of DeviceSetup.
3. The reboot kills the agent; the post-reboot restart re-observes both channels via
   backfill (the .evtx persists across the reboot).

# Detectors

## `MdmRebootPolicyTracker` (new collector, `MdmRebootPolicyWatcherHost`)

Structural clone of `WindowsUpdateTracker`: live `EventLogWatcher` on the DM-Enterprise
Admin channel (EventID 2800) + startup backfill (default 60 min lookback) + **cross-restart
RecordId watermark** (`mdm-reboot-watermark.json`). The watermark is mandatory here: the
coalesced reboot restarts the agent, and without it every reboot-forcing URI would be
re-emitted after the very reboot it caused.

Per 2800 record it emits one `mdm_policy_reboot_required` event (Warning,
`ImmediateUpload=true` — the announced reboot will kill the agent, flush now) with
`Data.rebootUri` (the CSP URI), `recordId`, `backfilled`, `description` (truncated 1000),
and a fail-soft `omadmRebootRequiredFlag` corroboration probe of
`HKLM\SOFTWARE\Microsoft\Provisioning\OMADM\SyncML\RebootRequired` (null = unknown, never
blocks the emit). Per-URI emission (not aggregated) is deliberate: an aggregate would need
a flush point that races the reboot kill.

**Tolerant matching**: the exact 2800 message text is unverified — emission is gated on
the EventID only. URI extraction prefers structured EventData values starting with `./`
and falls back to a permissive regex over the description (trailing punctuation trimmed).
Events with an unparseable URI are still emitted (timeline-visible, rule stays silent, and
the captured description tells us how to fix the extraction). Exact texts are validated
post-deploy on the E2E VM and recorded here.

Config (internal steering only, no admin UI — this is an agent-native collector, NOT an
`IAgentAnalyzer`): `Collectors.MdmRebootPolicyWatcherEnabled` (default true),
`Collectors.MdmRebootPolicyBackfillLookbackMinutes` (default 60). ConfigVersion 35.

## `ShellCoreTracker` — `RebootCoalescing` branch

The already-watched 62407 stream gets a fourth branch (checked AFTER
WhiteGlove/failure/exiting so their semantics stay untouched): description containing
`RebootCoalescing` → one `esp_reboot_coalescing` event (Warning, ImmediateUpload), pure
informational — no `FinalizingSetup` transition, no C# event raise, no DecisionCore
involvement.

**Deliberate deviation**: unlike the other 62407 branches, the backfill path DOES emit
this event. The live emit races the very reboot that kills the process; the post-restart
5-min backfill re-emission is the only reliable delivery. A live+backfill cross-process
duplicate is possible and accepted (fire-once bool resets per process; both copies carry
the identical historical timestamp; the backfill copy is marked `backfilled:true`; the
analyze rule uses exists-semantics). The shared 5-min lookback is NOT enlarged — the
rule's required condition is the watermarked 2800 event; coalescing is optional
corroboration only.

# Advisory — ANALYZE-ESP-005

"MDM Policy Forced a Mid-ESP Reboot (Second Sign-In)", severity `warning`,
`markSessionAsFailedDefault: false`. Required: `mdm_policy_reboot_required.rebootUri`
exists (first URI interpolates as `{{rebootUri}}`). Optional corroboration:
`esp_reboot_coalescing` observed (+20 confidence) and ≥2 reboot URIs (+10) on base 70 /
threshold 70. Remediation: map each URI to its Intune profile and reassign device → user
groups (or exclude the Autopilot device group), or accept + communicate the restart.
Because `{{token}}` interpolation is scalar-only, the explanation points at the timeline
for the complete URI list — each URI is its own event.

# Non-goals / invariants

* The new event types are **deliberately distinct from `system_reboot_detected`** — the
  backend RebootCount matches that string exactly in two places
  (`EventIngestProcessor.Classification` + the terminal recount); attribution events must
  never inflate the reboot count.
* No session-level field / SessionInfoCard change in this iteration (follow-up candidate).
* The `RebootRequiredURIs` catalog itself is not read (format unverified); only the 2800
  events (which carry the actually-triggered URIs) are consumed.

# Citations

* [Patch My PC — Avoid the second sign-in screen after Autopilot](https://patchmypc.com/blog/autopilot-unexpected-reboot-autopilot-second-login-screen/)
* [Microsoft Learn — Enrollment Status Page tracking](https://learn.microsoft.com/en-us/autopilot/enrollment-status)
