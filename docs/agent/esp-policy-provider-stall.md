---
type: Concept
title: ESP Policy-Provider Stall Detection — the no-timeout Setup/Apps wait and the co-management Sidecar gap
description: Why the ESP's Apps wait is keyed to the Intune-registered "Sidecar" provider by name — a foreign provider (ConfigMgr) with TrackingPoliciesCreated=1 does not satisfy it — plus the classic never-completes stall, and how the always-on two-arm esp_policy_provider_stalled tripwire (reason=provider_incomplete | sidecar_provider_missing, 15-min wall-clock dwell, StartupEventGate cross-restart dedup) surfaces both.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/EspPolicyProviderProbe.cs
tags:
  - agent
  - esp
  - enrollment-status-tracking
  - co-management
  - stall-detection
timestamp: 2026-07-24T00:00:00+02:00
---

# ESP Policy-Provider Stall Detection

## Schema

The Enrollment Status Page decides when its "Apps" category may progress from
"Identifying" using the **EnrollmentStatusTracking CSP**
(<https://learn.microsoft.com/en-us/windows/client-management/mdm/enrollmentstatustracking-csp>),
mirrored in the registry under
`HKLM\SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking\{Device|<UserSID>}\…`.
Two contracts matter:

1. **`Setup/Apps/PolicyProviders/<Name>`** — per the CSP doc: *"Existence of this node
   indicates to the ESP that it shouldn't show the tracking status message until the
   TrackingPoliciesCreated node has been set to true."* This wait has **no timeout**. A
   registered provider that never sets `TrackingPoliciesCreated=1` parks the ESP at
   "Apps (Identifying)" indefinitely — observed for days in the field. Exists in device
   scope (`Device`) and user scope (`S-<SID>`, appears at sign-in).
2. **`DevicePreparation/PolicyProviders/<Name>/InstallationState`** — must reach 2
   (NotRequired) or 3 (Completed); 1=NotInstalled, 4=Error. Device scope only. The ESP
   applies a default 15-minute timeout here, so these stalls self-surface as ESP errors —
   the detector still records them for the diagnostic trail.

Known provider names: **`Sidecar` is the Intune Management Extension** (the CSP doc names
"Intune's agents, such as SideCar"); **`ConfigMgr`** is the Configuration Manager client,
registered during Autopilot-into-co-management — the troubleshooting section of
<https://learn.microsoft.com/en-us/intune/configmgr/comanage/autopilot-enrollment> queries
exactly these registry paths. The same document states that ESP policy providers **are not
aware of each other** and that **pre-provisioning is unsupported** with Autopilot into
co-management.

## The field case (issue #106)

A tenant pre-provisions devices and installs the ConfigMgr client as a blocking app.
Occasionally the user-phase ESP reaches "Apps (Identifying)" and never leaves it — for
hours to days. On those devices `Device\Setup\Apps\PolicyProviders` contains **only a
`ConfigMgr` key (no `Sidecar`)** — and, per the customer's follow-up, **`TrackingPoliciesCreated=1`
IS set under it**. The provider is "complete" by the CSP value contract, yet the ESP
still waits. Manually renaming the key to `Sidecar` — which creates no tracking entries,
only satisfies the name — unblocks the ESP instantly.

The naive CSP-doc reading ("any provider with TrackingPoliciesCreated=1 satisfies the
wait") is therefore wrong for this case. What the evidence supports instead: provider
nodes are registered **by the Intune service at enrollment**, and its ESP-policy WAP
payload always registers `Sidecar` (ConfigMgr joins only via a co-management settings
policy — see the payload documented by Niehaus). The ESP waits on the provider it
expects **by name**; a foreign provider occupying the slot does not satisfy it,
regardless of its completion values. On the affected devices the CM client (installed
as a plain Win32 app, no co-management settings policy) evidently displaced the
Sidecar node — consistent with Microsoft's warning that providers "aren't currently
aware of others" and that pre-provisioning is unsupported with co-management. The exact
CloudExperienceHost mechanism is not publicly documented; the registry signature —
setupApps providers present, `Sidecar` absent — is deterministic either way.

## Detection

`EspPolicyProviderProbe` (fail-soft, static, `ScopedOverride` test seam — structural clone
of `EspTrackingInfoProbe`) enumerates all providers in both kinds and scopes.
`EspPolicyProviderStallDetector` evaluates a pure transition function on a 60-second tick
from the always-on `EspPolicyProviderStallHost` — **two arms**, same 15-minute wall-clock
dwell, one shared event type discriminated by `reason`:

* **Arm 1, `reason=provider_incomplete`** — a provider **continuously incomplete for
  ≥ 15 minutes**. The dwell clock starts at first incomplete observation and resets on
  completion or disappearance. Missing values parse as incomplete
  (fail-toward-observation). Catches e.g. Sidecar itself never reporting.
* **Arm 2, `reason=sidecar_provider_missing`** — **≥ 1 `Setup\Apps` provider registered
  but none named `Sidecar`**, continuously for ≥ 15 minutes. Deliberately ignores
  provider completeness (the field case had `TrackingPoliciesCreated=1`). Sidecar
  registering later — the legitimate startup ordering — clears the condition and resets
  the clock. DevicePreparation-only snapshots don't activate it.
* When both arms cross in the same round, ONE event is emitted and
  `sidecar_provider_missing` wins as `reason` (it explains the hang regardless of the
  foreign provider's state).
* Missing root key / empty provider list = normal early enrollment: clears all dwell
  state (both arms), never fires.
* One `esp_policy_provider_stalled` **Warning** per newly stalled condition set
  (`ImmediateUpload`), one-shot per condition in-process, cross-restart deduped via a
  `StartupEventGate` fingerprint over the sorted currently-active fired conditions
  (provider keys + a `sidecarMissing` sentinel) — a restart into the same stall stays
  silent, a new condition joining re-reports.
* Payload: `reason`, `providers[]` (full table: name, scope, kind, raw values, complete
  flag), `stalledProviders[]` with `stalledForMinutes`, `sidecarMissingForMinutes`
  (arm 2 only), `sidecarRegistered`, `dwellMinutes`.
* **Purely observational**: emitted via `InformationalEventPost` (dispatch-guard exempt
  pass-through) — never touches decision state. No config toggle, no ConfigVersion bump
  (precedent: `disk_space_low`).

Deliberately **not** built on `StallProbeCollector` (its idle clock resets on any session
activity — a provider stall coexists with a chatty session) and not on the periodic
collectors (idle-stopped after 15 minutes — exactly the dormant window the dwell must
observe).

## Guardrail companion

`rules/guardrails.json` allow-lists `SOFTWARE\Microsoft\Windows\Autopilot` (category
"Autopilot / OOBE / Setup") so tenants can build their own gather rules on the subtree —
e.g. a `not_exists` registry rule on the `Sidecar` provider key. The subtree carries
deployment state only (provider registrations, tracking lists, app IDs, per-user SIDs);
sensitivity is at or below existing entries. Enforcement is agent-side (embedded
resource), so both the detector and the guardrail change ship with the agent release.

## Examples

Customer-case event (abbreviated) — note the provider is "complete" by CSP values, the
hang is carried entirely by `reason=sidecar_provider_missing`:

```json
{
  "eventType": "esp_policy_provider_stalled",
  "severity": "Warning",
  "data": {
    "reason": "sidecar_provider_missing",
    "dwellMinutes": 15,
    "sidecarRegistered": false,
    "sidecarMissingForMinutes": 15.0,
    "providers": [
      { "name": "ConfigMgr", "scope": "device", "kind": "setupApps",
        "trackingPoliciesCreated": 1, "installationState": null, "complete": true }
    ],
    "stalledProviders": []
  }
}
```

`reason=sidecar_provider_missing` is the co-management signature;
`reason=provider_incomplete` with `sidecarRegistered=true` covers a stalled Sidecar or
third-party provider — the generalized contract violation.

# Citations

* EnrollmentStatusTracking CSP — <https://learn.microsoft.com/en-us/windows/client-management/mdm/enrollmentstatustracking-csp>
* Windows Autopilot into co-management (requirements, provider-conflict warning, troubleshooting registry paths) — <https://learn.microsoft.com/en-us/intune/configmgr/comanage/autopilot-enrollment>
* Troubleshoot the ESP (models app tracking exclusively via the `Sidecar` subkey) — <https://learn.microsoft.com/en-us/troubleshoot/mem/intune/device-enrollment/understand-troubleshoot-esp>
* Niehaus: the Intune ESP-policy WAP payload registering the provider nodes (`Sidecar` always, `ConfigMgr` only with a co-management settings policy) — <https://oofhours.com/2023/08/25/run-an-sccm-task-sequence-during-autopilot/>
* Niehaus: ESP tracks arbitrarily named providers when they are properly registered — <https://oofhours.com/2023/09/18/track-anything-using-esp/>
* Agent code: `EspPolicyProviderProbe.cs`, `EspPolicyProviderStallDetector.cs`, `EspPolicyProviderStallHost.cs` (SystemSignals / Orchestration.Hosts)
* Related: [MDM Reboot Coalescing](mdm-reboot-coalescing.md), [Gather-Rule Guardrails](../rules/gather-rule-guardrails.md)
