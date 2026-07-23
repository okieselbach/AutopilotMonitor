---
type: Concept
title: ESP Policy-Provider Stall Detection — the no-timeout Setup/Apps wait and the co-management Sidecar gap
description: Why a registered EnrollmentStatusTracking CSP policy provider that never sets TrackingPoliciesCreated parks the user ESP at "Apps (Identifying)" indefinitely, the pre-provisioning + ConfigMgr field case, and how the always-on esp_policy_provider_stalled tripwire (15-min wall-clock dwell, StartupEventGate cross-restart dedup) surfaces it.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Enrollment/SystemSignals/EspPolicyProviderProbe.cs
tags:
  - agent
  - esp
  - enrollment-status-tracking
  - co-management
  - stall-detection
timestamp: 2026-07-23T00:00:00+02:00
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

## The field case

A tenant pre-provisions devices and installs the ConfigMgr client as a blocking app.
Occasionally the user-phase ESP reaches "Apps (Identifying)" and never leaves it — for
hours to days. On those devices `Device\Setup\Apps\PolicyProviders` contains **only a
`ConfigMgr` key (no `Sidecar`)** and `TrackingPoliciesCreated` is never set. Manually
renaming the key to `Sidecar` unblocks the ESP immediately (the IME then finds its
provider node and reports tracking-policies-created). The mechanism is exactly the
documented no-timeout wait; the unsupported pre-provisioning + co-management combination
explains why only some devices are affected. Why the Sidecar registration is missing or
displaced on those devices is not publicly documented — irrelevant for detection, since
the signature is deterministic.

## Detection

`EspPolicyProviderProbe` (fail-soft, static, `ScopedOverride` test seam — structural clone
of `EspTrackingInfoProbe`) enumerates all providers in both kinds and scopes.
`EspPolicyProviderStallDetector` evaluates a pure transition function on a 60-second tick
from the always-on `EspPolicyProviderStallHost`:

* A provider **continuously incomplete for ≥ 15 minutes** (wall-clock) is stalled. The
  dwell clock starts at first incomplete observation and resets on completion or
  disappearance. Missing values parse as incomplete (fail-toward-observation).
* Missing root key / empty provider list = normal early enrollment: clears all dwell
  state, never fires.
* One `esp_policy_provider_stalled` **Warning** per newly stalled provider set
  (`ImmediateUpload`), one-shot per provider in-process, cross-restart deduped via a
  `StartupEventGate` fingerprint over the sorted currently-stalled key set — a restart
  into the same stall stays silent, a new provider joining the stall re-reports.
* Payload: `providers[]` (full table: name, scope, kind, raw values, complete flag),
  `stalledProviders[]` with `stalledForMinutes`, `sidecarRegistered`, `dwellMinutes`.
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

Customer-case event (abbreviated):

```json
{
  "eventType": "esp_policy_provider_stalled",
  "severity": "Warning",
  "data": {
    "dwellMinutes": 15,
    "sidecarRegistered": false,
    "providers": [
      { "name": "ConfigMgr", "scope": "device", "kind": "setupApps",
        "trackingPoliciesCreated": null, "installationState": null, "complete": false }
    ],
    "stalledProviders": [
      { "name": "ConfigMgr", "scope": "device", "kind": "setupApps", "stalledForMinutes": 15.0 }
    ]
  }
}
```

`sidecarRegistered=false` while `setupApps` providers exist is the co-management
signature; `sidecarRegistered=true` with a stalled third-party provider generalizes the
same contract violation.

# Citations

* EnrollmentStatusTracking CSP — <https://learn.microsoft.com/en-us/windows/client-management/mdm/enrollmentstatustracking-csp>
* Windows Autopilot into co-management (requirements, provider-conflict warning, troubleshooting registry paths) — <https://learn.microsoft.com/en-us/intune/configmgr/comanage/autopilot-enrollment>
* Agent code: `EspPolicyProviderProbe.cs`, `EspPolicyProviderStallDetector.cs`, `EspPolicyProviderStallHost.cs` (SystemSignals / Orchestration.Hosts)
* Related: [MDM Reboot Coalescing](mdm-reboot-coalescing.md), [Gather-Rule Guardrails](../rules/gather-rule-guardrails.md)
