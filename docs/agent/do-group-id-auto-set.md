---
type: Concept
title: DO GroupId Auto-Set — Delivery Optimization peering group from a network fingerprint
description: EnableDoGroupIdAutoSet (ConfigVersion 40) makes the agent set the DOGroupId policy value to a deterministic GUID derived from the default gateway's IPv4 address and MAC (RealmJoin-compatible byte layout), so devices on the same local network form one DO peering group during enrollment; foreign DOGroupId/DOGroupIdSource policies always win.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Transport/DoGroupIdService.cs
tags:
  - agent
  - delivery-optimization
  - network
  - tenant-configuration
timestamp: 2026-08-21T00:00:00+02:00
---

# DO GroupId Auto-Set

## Schema

### The problem

With Delivery Optimization download mode Group (2), peering is scoped by GroupId. When no
GroupId is configured, Windows derives it from AD site → authenticated domain SID → Entra
tenant ID — for a cloud-native tenant that means **one tenant-wide group**, so "peers on
the same network" degrades to "peers anywhere in the tenant" and DO's cloud service has to
match them by public IP. A GroupId derived from the local network makes the group exactly
"devices behind the same gateway" — including branch offices behind NAT. RealmJoin ships
this as its network-fingerprint GroupId; our agent is on the device even earlier (during
ESP, when the big app downloads actually happen), so it can seed the same value for
Autopilot enrollments.

### The setting

`TenantConfiguration.EnableDoGroupIdAutoSet` (`bool?`, default null→false), served as
`AgentConfigResponse.EnableDoGroupIdAutoSet` — **ConfigVersion 40**. Portal: Settings →
Agent → Settings, toggle "Set Delivery Optimization Group ID", stored via the usual
tenant-config roundtrip (`TableConfigRepository` Store+Map). Also shown in the admin
tenant-config report. `RemoteConfigMerger` maps the bool 1:1; it survives the offline
config cache like every plain feature flag.

The agent does NOT configure `DODownloadMode` — the fingerprint GroupId only has an
effect when the tenant already deploys download mode Group (2) via Intune/GPO.

### The fingerprint

`DoGroupIdService.ComputeGroupId(ipv4Bytes, macBytes)` builds a 16-byte GUID:

```
{101,48,6} + {0,0,0} + {ip[1], 0, ip[2], ip[3]} + mac[0..5]   →  new Guid(byte[16])
```

Gateway discovery reuses the shared `NetworkInterfaceLocator` heuristic (Up, not
Loopback/Tunnel, non-0.0.0.0 gateway; IPv4 gateway required), the gateway MAC comes from
`ArpNativeMethods` (`iphlpapi!SendARP`). The byte layout — including the IP shuffle and
the zero padding — is **RealmJoin-compatible and must never change**: devices managed by
RealmJoin on the same network compute the identical GUID and peer with ours. The registry
value is written as `Guid.ToString()` (braceless lowercase), identical to RealmJoin's
`SetValue(name, Guid)` string form. Deliberate divergence from RealmJoin: **no
random-GUID fallback** — a random per-device ID would isolate the device in a one-member
group, which is worse than the OS default grouping, so no gateway/no MAC means skip.

### Ownership + conflict rules (never fight a foreign policy)

Target: `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization\DOGroupId`
(64-bit registry view — the Policies store is WOW64-redirected; key paths reused from
`DoThrottlePolicyReader`). Only this GPO-store value is written; the legacy pre-1607
`GroupId` value name is intentionally not.

Skip conditions (Info event with `skipReason`, nothing written):

| skipReason | Meaning |
|---|---|
| `no_ipv4_gateway` | No active interface with an IPv4 default gateway |
| `arp_failed` | Gateway MAC could not be resolved via SendARP |
| `group_id_source_policy` | `DOGroupIdSource` configured in GPO or MDM store (dynamic group source wins) |
| `mdm_policy_present` | `DOGroupId` managed via the Policy CSP store (Intune owns it) |
| `foreign_value_present` | Existing GPO value neither matches the computed GUID nor the one we last wrote |

Ownership is tracked through the `StartupEventGate` fingerprint for the
`do_group_id_auto_set` key: `written:<guid>` records the GUID we last wrote, so after a
gateway change the agent recognizes its own stale value and updates it — while a value
written by anyone else is never overwritten (`DecideAction`: empty→write, equal→no-op,
ours→update, else→skip; unparseable existing values count as foreign). Every write is
verified by readback.

### The event

One `do_group_id_auto_set` event per outcome change (Source `StartupEnvironmentProbes`,
gated on the fingerprint — unchanged reruns across reboots stay silent; errors never
latch, so each failing start keeps its retry trail). Severity: Info for
success/skip, Warning for errors. Data: `groupId`, `previousValue`, `gatewayIp`,
`gatewayMac`, `written`, `skipped`, `skipReason`, `success`, `error`.

## Examples

First boot on a network with gateway `192.168.178.1` / MAC `00-11-22-33-44-55`:

```
do_group_id_auto_set (Info): Delivery Optimization GroupId set to
00063065-0000-00a8-b201-001122334455 (gateway 192.168.178.1)
```

Tenant already deploys a DO GroupId via Intune:

```
do_group_id_auto_set (Info): Delivery Optimization GroupId not set: mdm_policy_present
```

## Citations

* Service (fingerprint, ownership, write+readback): `../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Transport/DoGroupIdService.cs`
* ARP interop: `../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Interop/ArpNativeMethods.cs`
* Probe wiring + event + fingerprint dedup: `../../src/Agent/AutopilotMonitor.Agent.V2.Core/Runtime/StartupEnvironmentProbes.cs`
* Backend serve + ConfigVersion: `../../src/Backend/AutopilotMonitor.Functions/Functions/Config/GetAgentConfigFunction.cs`
* DO policy stores (key paths, Registry64 rationale): `../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/Periodic/DoThrottlePolicyReader.cs`
