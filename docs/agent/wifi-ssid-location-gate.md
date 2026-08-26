---
type: Concept
title: WiFi SSID and the Windows 24H2 Location Gate
description: Why wifi_signal_info silently disappeared on Windows 11 24H2+ devices, why the netsh fallback could never fix it, and the tiered reader that restores the SSID from the SYSTEM context.
resource: src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Interop/WifiInfoProvider.cs
tags:
  - agent
  - interop
  - wifi
  - telemetry
timestamp: 2026-08-26T14:05:00+02:00
---

# Summary

The agent reports the enrollment WiFi as a `wifi_signal_info` event (SSID, signal, PHY, channel).
On Windows 11 24H2 and later the event went missing on a large share of devices — no error, no
partial payload, simply no event. The cause is a Windows privacy change, not a bug in the reader:
**`WlanQueryInterface` with `wlan_intf_opcode_current_connection` is gated behind precise-location
consent**, and the agent runs as SYSTEM in session 0 where that consent usually does not exist.

The fix is a tiered reader ([WifiInfoProvider](../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Interop/WifiInfoProvider.cs))
whose middle tier uses the one SSID API Microsoft left ungated.

# The Gate

Microsoft restricted the Win32 WLAN APIs that expose BSSIDs, because nearby-BSSID lists are a
precise-location oracle. Without consent these return `ERROR_ACCESS_DENIED` (5):

* `WlanGetAvailableNetworkList`
* `WlanGetNetworkBssList`
* `WlanScan`
* **`WlanQueryInterface` when `OpCode` is `wlan_intf_opcode_current_connection`** — the only opcode
  the agent needs, and the one that carries SSID, signal quality, PHY type.

Two properties of the gate decide the agent's fate:

1. **The consent prompt only appears for a process running in the user's context and outside
   `C:\Windows\System32`.** The agent is neither during ESP: it is SYSTEM, in session 0, before any
   user has seen the OOBE privacy page. It is therefore denied *silently* — no prompt, no event
   log, nothing that surfaces without asking the API for its return code.
2. **Consent resolves per user hive, with the device-level setting as the floor.** An interactive
   user typically has
   `HKCU\...\CapabilityAccessManager\ConsentStore\location\NonPackaged\Value = Allow`
   ("Let desktop apps access your location"). The SYSTEM account (`HKU\S-1-5-18`) normally has
   **nothing** set there and falls back to
   `HKLM\...\ConsentStore\location\Value`. Where that device-level value is `Deny`, SYSTEM is
   denied even though the logged-on user on the very same machine reads the SSID fine.

That asymmetry is why the failure looks random from the outside: it tracks the **image's
device-level location setting**, which varies per fleet, not per hardware, driver, or language.

# Why netsh Was Never the Answer

`NetshWifiFallback` was added in 2026-07 on the theory that inbox `netsh` still reported the
connection where the WLAN API did not, and it was later extended with localized label variants for
every enrollment-geography language. Field data says otherwise: **`netsh wlan` hangs off the same
location gate** ("Network shell commands need location permission to access WLAN information"), so
it fails in lockstep with tier 1 rather than covering for it. In the reported session the agent
build already contained the netsh fallback and still produced no event.

Consequence for future work: **adding more netsh language labels cannot fix a missing SSID.** Every
`wifi_signal_info` event that does exist carries a fully populated SSID — the failure is binary at
the reader level, never a half-parsed or mis-decoded payload. A missing SSID is an access problem,
not a parsing problem.

# The Tiered Reader

`WifiInfoProvider.TryRead` owns the order so the two collectors (`DeviceInfoCollector`,
`NetworkChangeDetector`) cannot drift apart. First non-null wins:

| # | Reader | Yields | Location-gated |
|---|---|---|---|
| 1 | `WifiInfoReader` (native `wlanapi.dll`) | SSID, signal %, PHY, channel | yes |
| 2 | `WinRtWifiSsidReader` (WinRT `GetConnectedSsid`) | SSID only | **no** |
| 3 | `NetshWifiFallback` (process spawn, localized parse) | SSID, signal %, radio type, channel | yes |

Tier 2 is Microsoft's own documented replacement for this exact scenario:
`Windows.Networking.Connectivity.WlanConnectionProfileDetails.GetConnectedSsid()`. It is not part of
the BSSID-bearing surface, so it needs no consent, and it has existed since Windows 10 1507.

It is bound through the CLR's built-in WinRT projection with
`Type.GetType("…, ContentType=WindowsRuntime")` — no NuGet package, no winmd reference, no csproj
change — the same mechanism [OobeStateReader](../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Interop/OobeStateReader.cs)
already uses from the SYSTEM service context. Every step degrades to null rather than throwing,
because the caller runs fire-and-forget inside `Task.Run`.

# Telling the Operator

A missing signal reading is indistinguishable from a bug unless the payload says why, and the
device-level location setting is not something an admin would think to check. When tier 2 wins
*and* the native tier reported `rc=5`, the `wifi_signal_info` payload therefore carries

```
wifiDataLimitedReason = "location_services_off"
```

and the event message gains `- signal unavailable, Location services off`, so the reason is visible
in the timeline itself. The portal's WiFi card (`DeviceDetailsCard`) matches that literal and
renders the explanation plus the Intune setting that fixes it. The value is a published contract —
agent, portal and the MCP device-property catalog all match the same string.

The claim is only made on observed evidence: any other native failure (no WLAN service, no
connected interface) leaves the field null rather than blaming a privacy setting we did not
measure. `WifiInfoProvider.IsLocationGateDenied` is the single predicate, pinned by tests at its
boundaries (`rc=5` yes; `rc=50`, `rc=5023`, `rc=1062` no).

Fleet-wide the same field is queryable through
`search_sessions(deviceProperties: { "wifi_signal_info.wifiDataLimitedReason": "location_services_off" })`,
which sizes how much of a fleet is affected before anyone changes a policy.

Tier 2 returns the SSID alone. Signal quality, PHY type and channel have no ungated source; they
stay null when the native tier is denied. That is deliberate — deriving a percentage from
`ConnectionProfile.GetSignalBars()` (0–5) would fabricate precision the API does not have.

# Diagnostics

The step-level native return codes were already collected, but they were logged with
`_logger.Debug` — and the agent's default log level is `Info`, so the diagnostic built to explain
this failure never reached a single field log. Both collectors now log at **Warning** when all tiers
fail, which they only reach when the active NIC really is WiFi. A native `rc=5` is spelled out as
`ERROR_ACCESS_DENIED` with the location-gate explanation, so the log line diagnoses itself:

```
EnrollmentTracker: no wifi_signal_info — no WiFi info from any reader — native: [<guid>]
query(current_connection) rc=5; ; WinRT: no SSID; netsh: no parsable output. rc=5 is
ERROR_ACCESS_DENIED — Windows 11 24H2+ gates the current-connection opcode (and netsh wlan)
behind precise-location consent, which is off in this image; enable Location services to
restore signal/PHY/channel.
```

# Evidence

Session `7305e0ad` (HP EliteBook X Flip G1i, build 26200.9168, agent 2.0.1407, reported as
`WIFI SSID fehlt`): `network_interface_info` shows the active NIC as `Intel(R) Wi-Fi 7 BE201`,
`Wireless80211`, `connectionType=WiFi`, so the `isWifi` gate passed and collection ran — yet zero
`wifi_signal_info` events across 214 events and 22 minutes, with the netsh fallback already shipped
in that build.

Fleet correlation over WiFi sessions from 2026-08-01 (n=276), share with a `wifi_signal_info` event:

| OS build | WiFi sessions | with SSID |
|---|---|---|
| 22631 (23H2, pre-gate) | 3 | 100% |
| 26100 (24H2) | 178 | 21% |
| 26200 (25H2) | 95 | 45% |

Within a single model the outcome splits by fleet, not by hardware — ThinkPad T14 Gen 6: 7/91;
ThinkPad E14 Gen 7: 9/9 — which is what a per-image privacy setting looks like.

Local verification (build 26220.9022): `HKLM\…\ConsentStore\location = Deny` while
`HKCU\…\ConsentStore\location\NonPackaged = Allow`; the interactive user reads the SSID through
both the native API and netsh, `HKU\S-1-5-18` has no consent value at all, and the WinRT chain
resolves and returns the SSID under net48.

# Operator Guidance

A device that reports no SSID is not misconfigured for enrollment — only the telemetry field is
missing. To get the full payload (signal, PHY, channel) back, the **device-level** location setting
must be on: *Settings → Privacy & security → Location → Location services*, or via Intune
(`Privacy/LetAppsAccessLocation`). Without it the SSID still arrives via tier 2.

# Citations

* [Changes to API behavior for Wi-Fi access and location](https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes)
* [WlanConnectionProfileDetails.GetConnectedSsid](https://learn.microsoft.com/en-us/uwp/api/windows.networking.connectivity.wlanconnectionprofiledetails.getconnectedssid)
* [WifiInfoProvider.cs](../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Interop/WifiInfoProvider.cs)
* [WinRtWifiSsidReader.cs](../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Interop/WinRtWifiSsidReader.cs)
* [WifiInfoReader.cs](../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Interop/WifiInfoReader.cs)
* [NetshWifiFallback.cs](../../src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Interop/NetshWifiFallback.cs)
