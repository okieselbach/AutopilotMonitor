---
type: Concept
title: Secure Boot CA-2023 Detection — firmware evidence vs. Windows servicing status
description: Why ANALYZE-SEC-001 v3 reads the UEFI db/KEK variables directly (GetFirmwareEnvironmentVariableExW + SeSystemEnvironmentPrivilege) instead of trusting the SecureBoot\Servicing registry key, and how the one-sided uefiCa2023FirmwareConfirmed marker plus a not_exists precondition give absence-tolerant rule suppression across old and new agents.
resource: /src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Interop/UefiSecureBootCertReader.cs
tags:
  - agent
  - secureboot
  - ca2023
  - analyze-rules
  - backward-compatibility
timestamp: 2026-07-23T00:00:00+02:00
---

# Secure Boot CA-2023 Detection

## Schema

Two independent evidence sources describe the Windows UEFI CA 2023 rollout state:

1. **Servicing registry** (`HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\Servicing\UEFICA2023Status`)
   — reflects whether the *Windows Update* rollout ran and self-verified. Lags reality:
   devices whose firmware received the certificate another way (OEM factory image, WinCS
   CLI, manual deployment) or where WU simply has not run yet show `unknown`/`notfound`.
2. **Firmware itself** — the UEFI variables `db` (signature database, GUID
   `{d719b2cb-3d3a-4596-a3bc-dad00e67656f}`) and `KEK` (GUID
   `{8be4df61-93ca-11d2-aa0d-00e098032b8c}`). Authoritative. Certificate subject CNs are
   ASCII bytes inside the DER signature lists, so a byte-substring search for
   `Windows UEFI CA 2023` / `Microsoft Corporation KEK 2K CA 2023` decides presence —
   the same check `Get-SecureBootUEFI` enables from PowerShell.

`UefiSecureBootCertReader` (agent, `Monitoring/Interop`) reads both variables natively via
`GetFirmwareEnvironmentVariableExW` — no PowerShell runspace during OOBE. The call needs
`SeSystemEnvironmentPrivilege`: SYSTEM holds it default-disabled, so the reader enables it
on the process token (`AdjustTokenPrivileges`) and restores the previous state afterwards.
Fail-soft contract like `OobeStateReader`: never throws; failures map to status strings
(`not_uefi` = legacy BIOS via `ERROR_INVALID_FUNCTION`, `variable_not_found`,
`privilege_denied`, `error_<code>`).

Fields land on the existing `secureboot_status` event (once per agent start,
restart-deduped by the StartupEventGate): `uefiFirmwareReadStatus`, per-cert booleans
(`uefiDbHasWindowsUefiCa2023`, `uefiDbHasWindowsProductionPca2011`,
`uefiKekHasMicrosoftKek2kCa2023`, …) — emitted only when the respective read succeeded —
plus the marker below.

## The one-sided marker (absence-tolerant suppression)

Rule-engine constraints (pinned by `RuleEnginePreconditionTests`):

* **Conditions** never match on an absent field (null guard) — new fields must stay
  `required: false` or the rule dies for every old agent.
* **Preconditions**: `not_exists` passes while the field is absent/empty and fails as soon
  as the field is present with *any* value; comparison operators fail closed on absence.

A two-sided boolean therefore cannot drive suppression: `not_exists` on
`uefiDbHasWindowsUefiCa2023` would also suppress the rule when the field is present with
`false`. Hence the **one-sided marker** `uefiCa2023FirmwareConfirmed = true`, emitted
ONLY when the db read succeeded AND the 2023 certificate was found — never as `false`.
ANALYZE-SEC-001 v3 adds a `not_exists` precondition on it:

| Device state | Marker | Rule |
|---|---|---|
| Old agent (no firmware fields) | absent | evaluates exactly like v2 |
| New agent, cert missing or firmware unreadable | absent | evaluates; optional conditions `uefiDbHasWindowsUefiCa2023=false` (+20) / `uefiKekHasMicrosoftKek2kCa2023=false` (+10) raise confidence |
| New agent, firmware confirms cert | present | silently suppressed (the customer-reported false positive) |

Because the marker exists nowhere until the new agent ships, the v3 rule is safe to seed
**before** the agent rollout.

## Examples

Customer-equivalent verification from PowerShell (authoritative, matches the reader):

```powershell
[Text.Encoding]::ASCII.GetString((Get-SecureBootUEFI -Name db).Bytes).Contains('Windows UEFI CA 2023')
[Text.Encoding]::ASCII.GetString((Get-SecureBootUEFI -Name KEK).Bytes).Contains('Microsoft Corporation KEK 2K CA 2023')
```

## Citations

* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Interop/UefiSecureBootCertReader.cs` — native reader
* `src/Agent/AutopilotMonitor.Agent.V2.Core/Monitoring/Telemetry/DeviceInfo/DeviceInfoCollector.NetworkAndSecurity.cs` — `CollectSecureBootStatus` / `AppendFirmwareCertFields`
* `rules/analyze/ANALYZE-SEC-001.json` — v3.0.0 rule definition
* `src/Backend/AutopilotMonitor.Functions.Tests/RuleEnginePreconditionTests.cs` — pinned precondition semantics + v3 mirror tests
* Microsoft: [Act now — Secure Boot certificates expire June 2026](https://techcommunity.microsoft.com/blog/windows-itpro-blog/act-now-secure-boot-certificates-expire-in-june-2026/4426856)
