---
type: Concept
title: W365 Cloud PC Device Validation — Cert-CN-Bound Fallback Validator
description: Why Windows 365 Cloud PCs can never pass the Autopilot serial lookup, and how the CloudPc validator admits exactly the tenant's service-provisioned Cloud PCs by binding the MDM certificate's Subject CN (= Intune device id) to the Graph virtualEndpoint/cloudPCs inventory.
resource: src/Backend/AutopilotMonitor.Functions/Security/CloudPcDeviceValidator.cs
tags:
  - backend
  - security
  - device-validation
  - windows365
  - cloudpc
  - graph
timestamp: 2026-08-06T00:00:00+02:00
---

# W365 Cloud PC Device Validation

Windows 365 Cloud PCs are provisioned headless by the Windows 365 service and are
**structurally never Autopilot-registered** — the serial lookup against
`windowsAutopilotDeviceIdentities` (and the Corporate Identifier fallback, whose
serials nobody can pre-import for VMs minted at provisioning time) always misses.
Before this validator, a Cloud PC agent died in the 403 "Device not registered"
even though its mTLS cert was perfectly valid, so Cloud PC first-connect
enrollment (Account Setup, RealmJoin, Hello) could not be monitored.

# Schema — the validator chain

`SecurityValidator.ValidateRequestAsync` stage 4 runs the same chain for every
device; there is **no device-type routing** and the local W365 markers
(Windows365 registry key, CloudManagedDesktopExtension service) are never
consulted for auth — they gain an attacker nothing:

1. **Autopilot serial lookup** (`ValidateAutopilotDevice`) — unchanged primary.
2. **Corporate Identifier** (`ValidateCorporateIdentifier`) — unchanged fallback.
3. **CloudPc** (`ValidateCloudPcDevice`, default off) — only reached when 1+2
   missed. Extracts the Intune device id from the **chain-validated** client
   certificate's Subject CN (`TryGetIntuneDeviceIdFromCertSubject`: CN present
   AND canonical GUID, else definitive reject) and requires a `cloudPC` object
   with `managedDeviceId eq <CN>` in the claimed tenant
   (`GET /v1.0/deviceManagement/virtualEndpoint/cloudPCs?$filter=…`).
   `ValidatedBy = ValidatorType.CloudPc (5)`.

Only machines actually provisioned by the Windows 365 service have a `cloudPC`
object — a regular Intune-enrolled non-Autopilot device still ends in the 403,
so the "Autopilot devices only" contract of stages 1–2 is not widened. The
tenant hard gate accepts `ValidateCloudPcDevice` as a third satisfying flag.

## Why the cert CN, not headers or local markers

Field-verified 2026-08-06 on a real W365 Enterprise Cloud PC (gktatooine.net):

- MDM client cert Subject `CN=07623d56-…` == Intune `managedDevice.id` ==
  `cloudPC.managedDeviceId`. The CN cannot be forged without the Intune MDM
  Device CA (chain pinned to embedded roots, `CustomRootTrust`).
- `$filter=managedDeviceId eq '…'` is supported server-side on **v1.0** — no
  paging fallback needed.
- `managedDevice.serialNumber` matches local `Win32_BIOS.SerialNumber` exactly;
  the "Cloud PC Enterprise …" model string exists **only** in Graph, never in
  local WMI (local WMI reports Manufacturer='Microsoft Corporation',
  Model='Virtual Machine').

Threat model: a valid Intune cert from a *different* tenant fails (its CN does
not exist in the claimed tenant's Cloud PC inventory — this is a *tenant
binding* the serial-based validators do not have); spoofed serial headers and
faked local W365 markers are irrelevant (never consulted); a rogue enrolled VM
with doctored SMBIOS cannot mint a `cloudPC` object (service-side state, not
device-reported inventory — the reason the model-string gate was rejected).
Retired/deprovisioned Cloud PCs disappear from the inventory and their
validation dies with them (compensates the absent cert revocation check).
Residual: a compromised *legitimate* Cloud PC of the tenant — same trust level
as any tenant device today, rate-limited by cert thumbprint.

## Graph permission plumbing

- Optional add-on permission `CloudPC.Read.All` — NOT part of the default
  consent set. Feature `W365CloudPcValidation` in `GraphFeatureCatalog` →
  surfaces automatically in the "Optional Graph capabilities" UI (the feature
  table and grant command are backend-driven) and in
  `Grant-AutopilotMonitorAddOn.ps1` (`-Features W365CloudPcValidation`).
  `GraphFeatureCatalogSyncTests` pins C# catalog ↔ grant script (previously
  comment-only lock-step).
- A Graph **403** (permission not granted) is a definitive negative (cached
  5 min), NOT transient — a 503 Retry-After loop cannot fix a missing grant.
  The error message names the add-on feature.
- Same resilience contract as `AutopilotDeviceValidator`: 30/5 min
  positive/negative cache, 2 attempts, transient → agent-facing 503.

## Bootstrap script interplay (v2.3-dev.3)

The Cloud-PC relax in `Install-AutopilotMonitor-dev.ps1` additionally exempts
**guard 4** (12 h uptime window): a Cloud PC runs headless for days before the
user's first connect, and the monitorable phase (Account Setup) only starts at
that first connect — the single-fresh-profile window (< 15 min) is the time
anchor there, not boot uptime. The OOBE-restore trigger keeps the uptime guard.

## Deliberately out of scope (follow-up)

Agent-side `IsCloudPc` classification (local markers → `SessionRegistration`)
and decision-engine awareness ("W365 session has no Device ESP phase") are a
separate change. Until then a Cloud PC session is recognizable by
`ValidatedBy = CloudPc` (SessionInfoCard renders "Windows 365 Cloud PC").

# Citations

- `src/Backend/AutopilotMonitor.Functions/Security/CloudPcDeviceValidator.cs`
- `src/Backend/AutopilotMonitor.Functions/Security/SecurityValidator.cs` — stage 4 chain, `TryGetIntuneDeviceIdFromCertSubject`
- `src/Shared/AutopilotMonitor.Shared/Models/Graph/GraphFeatureCatalog.cs` — `W365CloudPcValidation`
- `scripts/CustomerSetup/Grant-AutopilotMonitorAddOn.ps1` — customer-side grant
- `scripts/Bootstrap/Install-AutopilotMonitor-dev.ps1` — Cloud-PC relax + guard-4 exemption
- `src/Backend/AutopilotMonitor.Functions.Tests/CloudPcDeviceValidatorTests.cs`, `SecurityValidatorTests.cs`, `GraphFeatureCatalogSyncTests.cs`
