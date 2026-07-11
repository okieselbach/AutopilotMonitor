---
type: How-to Guide
title: Optional Graph add-on permissions
description: Opt-in tenant-side Graph permission grants for optional features via appRoleAssignment, without changing the published app manifest.
resource: /scripts/CustomerSetup/Grant-AutopilotMonitorAddOn.ps1
tags:
  - graph
  - permissions
  - entra-id
  - customer-setup
timestamp: 2026-05-19T01:14:21+02:00
---

# Optional Graph add-on permissions

Autopilot Monitor's default Microsoft Graph permissions are intentionally minimal — they cover the core Autopilot device + corporate identifier validation use cases and nothing more. Some optional features need additional permissions on the Autopilot Monitor service principal **in your tenant**. Examples:

| Feature | Required Graph permission |
| --- | --- |
| Resolve Intune Platform Script + Remediation display names in session timelines | `DeviceManagementScripts.Read.All` |

Rather than baking these permissions into the published app manifest (which would force every customer to consent to them at first sign-up, even the ones who'll never use the feature), Autopilot Monitor offers them as **opt-in tenant-side grants**. You decide per feature, you grant only what you need.

## How it works

The grant happens through a **tenant-local `appRoleAssignment`** on the Autopilot Monitor service principal that already lives in your tenant after the initial admin consent. This is supported by Microsoft Graph and does **not** change the publisher's app manifest — the extra capability exists only for your tenant.

Behind the scenes:

1. The customer runs [`Grant-AutopilotMonitorAddOn.ps1`](../scripts/CustomerSetup/Grant-AutopilotMonitorAddOn.ps1) with a tenant-admin sign-in.
2. The script finds the Autopilot Monitor service principal in your tenant by its application (client) ID.
3. The script reads the requested feature(s), translates them to the underlying Graph permission(s), and creates a fresh `appRoleAssignment` for each — or skips it if already granted.
4. The next time the Autopilot Monitor backend acquires a token for your tenant, Azure AD includes the new permissions in the `roles` claim. The backend uses that claim to enable or disable optional features per tenant — no further configuration on your side.

## Prerequisites

- The Autopilot Monitor multi-tenant app must already be admin-consented in your tenant. Without that, the service principal doesn't exist yet and the script has nothing to grant against.
- The signed-in admin needs one of: **Global Administrator**, **Privileged Role Administrator**, or **Cloud Application Administrator** (sufficient for `AppRoleAssignment.ReadWrite.All` + `Application.Read.All`).
- The `Microsoft.Graph.Authentication` PowerShell module. The script auto-installs it for the current user if missing.

## Running the script

The easiest path is to open the admin UI in Autopilot Monitor → **Settings → Optional Graph capabilities**, hit **Copy command**, and paste the resulting PowerShell into a PS prompt. The ClientId is pre-filled with the live value for your environment.

If you prefer the high-level feature form:

```powershell
.\Grant-AutopilotMonitorAddOn.ps1 `
    -ClientId "<the-autopilot-monitor-app-id>" `
    -Features ScriptDisplayNames
```

Other modes:

```powershell
# Read-only inspection of currently granted permissions on the SP:
.\Grant-AutopilotMonitorAddOn.ps1 -ClientId "<...>" -Features ScriptDisplayNames -VerifyOnly

# Revoke a previously granted feature:
.\Grant-AutopilotMonitorAddOn.ps1 -ClientId "<...>" -Features ScriptDisplayNames -Revoke
```

After granting, return to the admin UI and click **Refresh permission status**. The backend caches its token (and the `roles` claim parsed from it) for up to ~1 hour; the refresh button clears that cache so the new permission takes effect immediately.

## What the script does NOT do

- It does **not** change the published Autopilot Monitor app manifest.
- It does **not** touch any other Entra application or service principal.
- It does **not** persist credentials anywhere; the only thing it writes is one or more `appRoleAssignment` rows on the Autopilot Monitor service principal in your tenant.

## Auditability

Granted permissions show up in the Entra portal under **Enterprise applications → Autopilot Monitor → Permissions → "Other granted permissions"**. They also appear in the Microsoft Graph API at `GET /servicePrincipals/{sp}/appRoleAssignments` — both during normal operation and in compliance audits. The Autopilot Monitor admin UI surface the same view at **Settings → Optional Graph capabilities**.

## Revoking

Use the script's `-Revoke` flag, or remove the assignment manually in the Entra portal. The admin UI **Refresh permission status** button will pick up the change on the next backend call.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Script errors with `Service principal not found` | The Autopilot Monitor app has never been admin-consented in your tenant. Run the consent flow first. |
| `AppRoleAssignment.ReadWrite.All` was NOT granted in the sign-in scopes line | Signed-in user lacks one of the required admin roles. PIM/JIT users: activate the eligible role before running the script. |
| Permission shows as granted in the UI but the backend still sees it as not granted | The backend's per-tenant token cache hasn't refreshed yet. Click **Refresh permission status** in the admin UI. |
| Permission grant succeeds but the optional feature still doesn't activate | Verify the granted permission via `Get-MgServicePrincipalAppRoleAssignment` and recheck the admin UI status panel. If still inconsistent, send the correlation IDs from the relevant calls to support. |
