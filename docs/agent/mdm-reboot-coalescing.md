---
type: Concept
title: MDM Reboot Coalescing — attributing the mid-ESP reboot and second sign-in to policy URIs
description: How the agent observes reboot-required MDM policy flags (DM-Enterprise EventID 2800, aggregated) and how ANALYZE-ESP-005 attributes an actually-observed enrollment reboot to them — including why the raw events stay neutral and why the Shell-Core RebootCoalescing match was removed.
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
**second sign-in screen** before AccountSetup. Invisible in the Intune console; most
admins live with it, not knowing that reassigning the profiles to user groups removes the
reboot entirely. Source research: Patch My PC,
["Avoid the second sign-in screen after Autopilot"](https://patchmypc.com/blog/autopilot-unexpected-reboot-autopilot-second-login-screen/).

The session already counts reboots (`system_reboot_detected` → `session.rebootCount`);
this feature adds the **cause attribution**.

# Verified signal facts (session b2e890c1, 2026-07-20)

* **EventID 2800**, `Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin`,
  one record per URI, verified description shape:
  `The following URI has triggered a reboot: (./Device/Vendor/MSFT/Policy/Config/ServiceControlManager/SvchostProcessMitigation).`
  Records arrive in sub-second bursts during policy sync.
* **Timing semantics (the critical nuance)**: 2800 fires at EVERY policy sync. Only URIs
  applied **during ESP DeviceSetup** are coalesced into the forced mid-ESP restart. In the
  verified session the flags fired during AccountSetup (after DeviceSetup completed) —
  **no reboot occurred**, and `omadmRebootRequiredFlag` read `false`. A detector that
  claims "reboot incoming" on every 2800 is therefore wrong.
* **Shell-Core 62407 "RebootCoalescing" — REMOVED as a signal.** The token appears in the
  ROUTINE bootstrap marker on every enrollment:
  `Name: 'CommercialOOBE_BootstrapStatusCategory_SubcategoryProcessing_Started', Value: '{"message":"BootstrapStatus: Starting subcategory DeviceSetup.RebootCoalescing...","errorCode":0}'`
  — the ESP merely *processes* the RebootCoalescing subcategory (which checks whether a
  reboot is needed). A substring match on it is a false positive by construction. If a
  real coalesced-reboot record is ever captured on the E2E VM, a precise pattern can be
  added then.

# Detector — `MdmRebootPolicyTracker` (`MdmRebootPolicyWatcherHost`)

Live `EventLogWatcher` on the DM-Enterprise Admin channel (EventID 2800) + startup
backfill (default 60 min lookback). Records are **buffered and debounced (10 s)**, then
flushed as **ONE aggregated `mdm_policy_reboot_required` event** per burst:

* `Severity=Info`, neutral message ("Windows flagged N MDM policy URIs as
  reboot-required: … (+N more)") — the raw event never claims a reboot.
* `Data`: `rebootUris` (distinct, sorted, capped 20), `uriCount` (uncapped distinct),
  `firstRebootUri` (record-time order, drives rule interpolation), `recordCount`,
  `unparsedCount`/`sampleDescription` (extraction diagnostics), `backfilled` (all-backfill),
  fail-soft `omadmRebootRequiredFlag` probe of
  `HKLM\SOFTWARE\Microsoft\Provisioning\OMADM\SyncML\RebootRequired` (null = unknown).
* `Timestamp` = earliest record time (backfilled bursts land at their historical instant);
  `ImmediateUpload=true` (one event per burst is cheap, and a DeviceSetup burst precedes a
  reboot that kills the agent).
* **Watermark is persisted at FLUSH, not at claim** (`mdm-reboot-watermark.json`): if the
  coalesced reboot kills the process before the debounced emit, nothing is marked — the
  post-restart backfill re-reads and emits the burst. A flushed burst is never repeated.

URI extraction: EventData values starting with `./` preferred, permissive regex over the
description as fallback (trailing punctuation trimmed — the verified text wraps the URI in
parens and ends with a period). Unparsed records still count into the aggregate.

Config (internal steering only, no admin UI — agent-native collector, NOT an
`IAgentAnalyzer`): `Collectors.MdmRebootPolicyWatcherEnabled` (default true),
`Collectors.MdmRebootPolicyBackfillLookbackMinutes` (default 60). ConfigVersion 35.

# Advisory — ANALYZE-ESP-005

The reboot **claim** lives here, and only here. Required conditions (BOTH):

1. `mdm_policy_reboot_required.firstRebootUri` exists (→ `{{firstRebootUri}}` interpolation)
2. `system_reboot_detected` count ≥ 1 — **an actually-observed reboot**

Policy flags alone (e.g. AccountSetup user-policy sync) stay advisory-silent. Optional
confidence: `uriCount ≥ 2` (+10), `omadmRebootRequiredFlag == true` (+15) on base 70 /
threshold 70. Remediation: map URIs to Intune profiles, reassign device → user groups (or
exclude the Autopilot device group), or accept + communicate the restart.

# Non-goals / invariants

* Event type **deliberately distinct from `system_reboot_detected`** — the backend
  RebootCount matches that string exactly in two places
  (`EventIngestProcessor.Classification` + terminal recount); attribution must never
  inflate the reboot count.
* No session-level field / SessionInfoCard change in this iteration (follow-up candidate).
* The `RebootRequiredURIs` catalog itself is not read; only the 2800 events (which carry
  the actually-triggered URIs) are consumed.

# Citations

* [Patch My PC — Avoid the second sign-in screen after Autopilot](https://patchmypc.com/blog/autopilot-unexpected-reboot-autopilot-second-login-screen/)
* [Microsoft Learn — Enrollment Status Page tracking](https://learn.microsoft.com/en-us/autopilot/enrollment-status)
