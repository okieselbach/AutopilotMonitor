---
type: Reference
title: Autopilot ZTD Diagnostics — Event IDs, Registry Surfaces, Endpoints, Error Codes
description: Windows' own diagnostic surfaces for the Autopilot profile-download (ZTD) flow and known deployment error codes — what the agent reads, what each identifier means, and where the knowledge comes from.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/DeviceInfo/ZtdEvidence.cs
tags:
  - agent
  - autopilot
  - ztd
  - diagnostics
  - event-log
  - troubleshooting
timestamp: 2026-07-16T00:00:00+02:00
---

# Autopilot ZTD Diagnostics

Windows documents its own diagnostic surfaces for the Autopilot profile-download flow
(the Zero-Touch Deployment / "ZTD" service round-trip during OOBE). The agent consumes
three of them to turn the `autopilot_profile_missing` warning from a guess into an
evidence-backed verdict, and the backend rule engine uses the error-code map for
known-issue enrichment.

Consumers in this repo:

* `ZtdEvidence.cs` (agent) — one-shot event-log query + `Diagnostics\Autopilot` registry
  dump + deployment-service reachability probe on the profile-missing path.
* `ModernDeploymentTracker.cs` (agent) — continuous watcher of the same channel,
  Level ≤ 3 only (Warning/Error/Critical) plus targeted Info IDs.
* Analyze rules (backend) — HRESULT/known-issue map (see [Error codes](#schema--error-codes)).

# Schema — ModernDeployment Autopilot event channel

Channel: `Microsoft-Windows-ModernDeployment-Diagnostics-Provider/Autopilot`
(Event Viewer: Application and Services Logs → Microsoft → Windows →
ModernDeployment-Diagnostics-Provider → Autopilot).

| Event ID | Level | Meaning | Agent verdict mapping (`ztdVerdict`) |
| --- | --- | --- | --- |
| 100 | Warning | "Autopilot policy [name] not found" — temporary, device is waiting for the profile download. Windows can log hundreds of these per minute (harmless-rollup in `ModernDeploymentTracker`). | `waiting_for_profile_no_internet_confirmation` (when nothing else was logged) |
| 101/103/109/111 | Info | Profile settings retrieval/processing details. | not queried |
| 153 | Info | `ProfileState_Unknown → ProfileState_Available` — a profile was downloaded and is ready. | `profile_downloaded` |
| 160 | Info | Profile settings acquisition beginning. | context only |
| 161 | Info | Profile download succeeded. | `profile_downloaded` |
| 163 | Info | Download not required — device already provisioned (typically after `Sysprep /Generalize`, i.e. Autopilot-for-existing-devices images). | `already_provisioned` |
| 164 | Info | Internet available to attempt policy download — the authoritative witness that the ZTD round-trip could run at OOBE time. | with no 161: `download_attempted_no_profile_returned` (the assignment-gap signature, session 423b5360) |
| 171 | Error | TPM identity confirmation failed (HRESULT in payload) — self-deploying/pre-prov attestation. | context only (`inconclusive` if alone) |
| 172 | Error | Failed to set profile as available — usually follows 171. | context only |
| 807 | Error | `ZtdDeviceIsNotRegistered` — hardware hash not registered in Autopilot. | `device_not_registered` |
| 809 | Error | `ZtdDeviceHasNoAssignedProfile` — the assigned profile was deleted without cleanup. | `assigned_profile_deleted` |
| 815 | Error | `ZtdDeviceHasNoAssignedProfile` — no profile assigned and no tenant default profile. | `no_profile_assigned` |
| 908 | Error | `SerialNumberMismatch` / `ProductKeyIdMismatch` between Autopilot registration and physical hardware. | `serial_or_product_key_mismatch` |

Verdict priority: error IDs are authoritative (807 → 908 → 809 → 815), then the Info-flow
reconstruction (161/153 → 163 → 164 → 100). Implemented in
`ZtdEvidence.ComputeZtdVerdict`, pinned by `ZtdEvidenceVerdictTests`.

# Schema — Diagnostics registry key

`HKLM\SOFTWARE\Microsoft\Provisioning\Diagnostics\Autopilot` — written by the ZTD client,
dumped verbatim by the agent into the `autopilot_profile` event (`diagnosticsRegistry`):

| Value | Meaning |
| --- | --- |
| `AadTenantId` | GUID of the tenant the user signed into. |
| `CloudAssignedTenantDomain` | Tenant the device is registered with; **blank = not registered with Autopilot**. |
| `CloudAssignedTenantId` | GUID matching `CloudAssignedTenantDomain`; blank when not registered. |
| `IsAutopilotDisabled` | 1 = device not registered **or** profile download failed (network/firewall/timeout) — Microsoft's own catch-all indicator. |
| `TenantMatched` | 0 = user's tenant ≠ registration tenant → the "username belongs to another organization" OOBE error. |
| `CloudAssignedOobeConfig` | Bitmap of configured OOBE settings (SkipCortanaOptIn=1, OobeUserNotLocalAdmin=2, SkipExpressSettings=4, SkipOemRegistration=8, SkipEula=16). |

The profile cache itself lives in `HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache`
(`PolicyJsonCache`, `ProfileAvailable`) — `ProfileAvailable=0` plus an empty
`ZeroTouchConfig` is the "blank profile cached" state the troubleshooting FAQ describes
when no profile exists at check time. A reboot re-runs the download and can replace the
blank cache.

# Schema — Endpoints

| Endpoint | Purpose | Agent behavior |
| --- | --- | --- |
| `https://ztd.dds.microsoft.com` | Autopilot Deployment Service — profile delivery during OOBE. | Reachability probe on profile-missing (any HTTP status = reachable). **Note:** several third-party blogs cite `ztd.ssd.microsoft.com`; the official requirements page says `ztd.dds.microsoft.com`. |
| `https://login.live.com` | Second Deployment-Service URL; also needed by the Microsoft Sign-in Assistant (`wlidsvc`) — a policy that disables wlidsvc breaks profile download. | not probed |
| `*.microsoftaik.azure.net` (+ per-vendor EK cert URLs) | TPM attestation for self-deploying/pre-provisioning. | not probed |
| `lgmsapeweu.blob.core.windows.net` | Intune automatic diagnostics upload. | deliberately not probed (decision 2026-07-16: not mission-critical) |

# Schema — Error codes (backend known-issue map)

Codes documented in the known-issues / troubleshooting-FAQ pages that appear in ESP/enrollment
failures we can observe:

| Code | Meaning / context | Actionable guidance |
| --- | --- | --- |
| `0x800705B4` | Timeout; in self-deploying mode usually "device is not TPM 2.0 capable" (e.g. a VM). | Don't use self-deploying on VMs / non-TPM-2.0 hardware. |
| `0x801C03EA` | TPM attestation failed → Entra join with device token failed. | TPM/firmware issue; check attestation endpoints. |
| `0x81039001` | `E_AUTOPILOT_CLIENT_TPM_MAX_ATTESTATION_RETRY_EXCEEDED` (intermittent, "Securing your hardware"). | Retry provisioning; persistent → firmware. |
| `0x81039023` | TPM attestation failure on Windows 11 (pre-prov/self-deploy). | Fixed by KB5013943 (21H2) or later CU. |
| `0x81039024` | TPM has known vulnerabilities → attestation refused. | Update TPM firmware from OEM. |
| `0x80070490` | TPM attestation failure on AMD ASP fTPM platforms. | Update AMD firmware. |
| `0xC1036501` | Automatic MDM enrollment impossible — multiple MDM configurations in Entra ID. | Consolidate MDM configs. |
| `0x801C03F3` | Entra device object for the Autopilot device was deleted (logged in `Microsoft-Windows-User Device Registration/Admin`). | Deregister + re-register the device (recreates the object). |
| `0x80180014` | Device-record reuse in self-deploying/pre-prov mode, or Windows MDM enrollment disabled ("Enrollment blocked for AP device by SDM One Time Limit Check"). | Intune → Autopilot device → **Unblock device**, or allow Windows (MDM) in enrollment restrictions. |
| `0x80180018` | MDM enrollment refused — licensing (missing Intune/EMS license) or device cap. | Check user license + device limits. |
| `0x80070774` | Hybrid-join ESP failure — domain mismatch between Intune Connector for AD and device targeting. | Install/configure the connector in the matching domain. |
| `0x80004005` | Hybrid-join deployment timeout — **build-dependent known issue**, fixed in KB5065789 (25H2, ≥ 26200.6725), KB5065426 (24H2, ≥ 26100.6584), KB5070312 (23H2, ≥ 22631.6276). | Update the image / install the KB. |

Related non-code known issues worth remembering: clock skew of minutes causes TPM
attestation errors and ESP timeouts (`w32tm /resync /force`; the agent's `ntp_time_check`
event carries the measured offset); multiple/unexpected OOBE reboots can delete kiosk
autologon registry entries (fixed in 24H2 KB5058411); ESP does not support mixing LOB and
Win32 apps ("Another installation is in progress").

# Citations

* [Windows Autopilot troubleshooting FAQ](https://learn.microsoft.com/en-us/autopilot/troubleshooting-faq) — event IDs, registry values, process flow. (Checked 2026-07-16.)
* [Windows Autopilot known issues](https://learn.microsoft.com/en-us/autopilot/known-issues) — error codes, KB fixes. (Checked 2026-07-16.)
* [Windows Autopilot requirements — Networking](https://learn.microsoft.com/en-us/autopilot/requirements?tabs=networking) — endpoint list incl. `ztd.dds.microsoft.com`. (Checked 2026-07-16.)
* Re-check cadence: both Learn pages change regularly (known-issues carries an RSS feed:
  `https://learn.microsoft.com/api/search/rss?search=%22Be+informed+about+known+issues+that+might+occur+during+Windows+Autopilot+deployment.%22&locale=en-us&%24filter=`).
  When new event IDs / error codes appear, update `ZtdEvidence.cs`, the backend rule map,
  and this document together.
