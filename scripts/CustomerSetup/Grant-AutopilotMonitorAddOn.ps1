#Requires -Version 5.1
<#
.SYNOPSIS
    Grants additional Microsoft Graph application permissions to the Autopilot Monitor
    service principal in the current tenant via tenant-side appRoleAssignment.

.DESCRIPTION
    The Autopilot Monitor multi-tenant app requests only a minimal default permission set.
    Optional features (such as resolving Intune Platform Script + Remediation Script display
    names in session timelines) require additional Graph application permissions on the
    Autopilot Monitor service principal in YOUR tenant. This script grants those permissions
    WITHOUT changing the publisher's app manifest -- the grant is local to your tenant.

    The script is idempotent: re-running skips permissions that are already granted and
    reports the final state. Use -VerifyOnly to inspect without making changes.

.PARAMETER ClientId
    Application (client) ID of the Autopilot Monitor multi-tenant app. Copy this from the
    Autopilot Monitor admin UI ("Settings -> Optional Graph capabilities") or from your
    vendor.

.PARAMETER Features
    One or more high-level feature identifiers. The script translates each to the underlying
    Graph permissions. Use this when you don't want to know permission strings.
    Available features:
      - ScriptDisplayNames     -> DeviceManagementScripts.Read.All
      - W365CloudPcValidation  -> CloudPC.Read.All
        (validates Windows 365 Cloud PCs against the tenant's Cloud PC inventory --
         Cloud PCs are never Autopilot-registered, so this enables agent monitoring
         of Cloud PC first-connect enrollment)

.PARAMETER Permissions
    Microsoft Graph application permission names to grant directly. Use when the admin UI
    rendered a copy-paste command for you, or when you have a specific list in mind.

.PARAMETER TenantId
    Tenant ID (GUID) or verified domain. Optional -- defaults to the signed-in context.

.PARAMETER VerifyOnly
    Read-only mode: lists current appRoleAssignments on the SP without modifying anything.

.PARAMETER Revoke
    Revokes the listed Features / Permissions instead of granting them.

.EXAMPLE
    # Grant the optional capability for resolving Intune script display names:
    .\Grant-AutopilotMonitorAddOn.ps1 -ClientId "<your-client-id>" -Features ScriptDisplayNames

.EXAMPLE
    # Same effect, using the explicit-permission form (what the admin UI generates):
    .\Grant-AutopilotMonitorAddOn.ps1 `
        -ClientId "<your-client-id>" `
        -Permissions "DeviceManagementScripts.Read.All" `
        -TenantId "contoso.onmicrosoft.com"

.EXAMPLE
    # Verify current state only, no changes:
    .\Grant-AutopilotMonitorAddOn.ps1 -ClientId "<your-client-id>" -Features ScriptDisplayNames -VerifyOnly

.EXAMPLE
    # Revoke the add-on permission:
    .\Grant-AutopilotMonitorAddOn.ps1 -ClientId "<your-client-id>" -Features ScriptDisplayNames -Revoke

.NOTES
    Requires:
      - Microsoft.Graph.Authentication PowerShell module (auto-installs if missing).
      - Sign-in as Global Administrator OR Privileged Role Administrator OR Cloud Application Administrator.

    Sign-in behaviour:
      In Azure Cloud Shell the script signs in via the ambient identity endpoint (a delegated
      token for the already-signed-in user) - no device-code prompt, which Conditional Access
      policies commonly block. Outside Cloud Shell it falls back to a normal interactive
      Microsoft Graph sign-in. -TenantId only applies to the interactive fallback.
      - The Autopilot Monitor app must be admin-consented in your tenant first
        (its service principal must exist before grants can be added).

    After granting:
      The Autopilot Monitor backend may cache an older token for up to ~1 hour. Use the
      "Refresh permission status" button in the admin UI ("Settings -> Optional Graph
      capabilities") to apply changes immediately.
#>
[CmdletBinding(SupportsShouldProcess = $true, DefaultParameterSetName = 'ByFeatures', ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$')]
    [string]$ClientId,

    [Parameter(Mandatory = $true, ParameterSetName = 'ByFeatures')]
    [ValidateSet('ScriptDisplayNames', 'W365CloudPcValidation')]
    [string[]]$Features,

    [Parameter(Mandatory = $true, ParameterSetName = 'ByPermissions')]
    [string[]]$Permissions,

    [Parameter(Mandatory = $false)]
    [string]$TenantId,

    [Parameter(Mandatory = $false)]
    [switch]$VerifyOnly,

    [Parameter(Mandatory = $false)]
    [switch]$Revoke
)

$ErrorActionPreference = 'Stop'

# ---- Feature -> Graph permission catalog -----------------------------------
# Keep in lock-step with src/Shared/AutopilotMonitor.Shared/Models/Graph/GraphFeatureCatalog.cs
$FeatureCatalog = @{
    'ScriptDisplayNames'    = @('DeviceManagementScripts.Read.All')
    'W365CloudPcValidation' = @('CloudPC.Read.All')
}

# Translate Features -> Permissions when caller used the high-level form.
if ($PSCmdlet.ParameterSetName -eq 'ByFeatures') {
    $Permissions = @()
    foreach ($f in $Features) {
        $Permissions += $FeatureCatalog[$f]
    }
    $Permissions = $Permissions | Select-Object -Unique
}

# ---- Constants ----
$MicrosoftGraphAppId = '00000003-0000-0000-c000-000000000000'

# ---- Pre-flight: module ----
if (-not (Get-Module -ListAvailable -Name Microsoft.Graph.Authentication)) {
    Write-Host "[Setup] Microsoft.Graph.Authentication not installed. Installing for current user..." -ForegroundColor Yellow
    Install-Module Microsoft.Graph.Authentication -Scope CurrentUser -Force -AllowClobber
}
Import-Module Microsoft.Graph.Authentication -ErrorAction Stop

# ---- Sign-in ----
$connectParams = @{
    Scopes    = @('AppRoleAssignment.ReadWrite.All', 'Application.Read.All')
    NoWelcome = $true
}
if ($TenantId) { $connectParams['TenantId'] = $TenantId }

Write-Host ""
Write-Host "[Auth] Connecting to Microsoft Graph..." -ForegroundColor Cyan

# Preferred path: ambient identity. In Azure Cloud Shell the local identity endpoint issues a
# delegated Graph token for the signed-in user, which avoids the device-code flow that
# Conditional Access policies commonly block. Outside Azure this throws and we fall back to
# an interactive sign-in with explicit scopes.
$identityConnected = $false
try {
    Connect-MgGraph -Identity -NoWelcome -ErrorAction Stop | Out-Null
    # An ambient identity can exist yet hold no Graph permissions (e.g. a plain VM managed
    # identity) - probe with a read before committing to this session.
    Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/servicePrincipals?`$select=id&`$top=1" -ErrorAction Stop | Out-Null
    $identityConnected = $true
    Write-Host "[Auth] Connected via ambient identity (Azure Cloud Shell / managed identity)." -ForegroundColor Green
}
catch {
    if (Get-MgContext) { Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null }
    Write-Host "[Auth] No usable ambient identity - falling back to interactive sign-in..." -ForegroundColor Yellow
    Connect-MgGraph @connectParams | Out-Null
}

$ctx = Get-MgContext
if (-not $ctx) {
    Write-Error "Microsoft Graph sign-in failed."
    return
}
Write-Host "[Auth] Signed in as : $($ctx.Account)"
Write-Host "[Auth] Tenant       : $($ctx.TenantId)"
Write-Host "[Auth] Granted scopes: $($ctx.Scopes -join ', ')"

# Scope check only applies to the interactive path: an ambient-identity (Cloud Shell) token
# carries a fixed scope set (incl. Directory.AccessAsUser.All) instead of the requested scopes,
# and its effective rights follow the signed-in user's directory role.
if (-not $identityConnected -and -not ($ctx.Scopes -contains 'AppRoleAssignment.ReadWrite.All')) {
    Write-Warning "Scope 'AppRoleAssignment.ReadWrite.All' was NOT granted. The signed-in user likely lacks the required role (Global Admin / Privileged Role Admin / Cloud App Admin)."
    if (-not $VerifyOnly) { return }
}

# ---- 1. Find Autopilot Monitor SP in the current tenant ----
Write-Host ""
Write-Host "[1/4] Locating Autopilot Monitor service principal (AppId=$ClientId)..." -ForegroundColor Cyan
$spUri = "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId eq '$ClientId'&`$select=id,appId,displayName"
$spResponse = Invoke-MgGraphRequest -Method GET -Uri $spUri
$sp = @($spResponse.value) | Select-Object -First 1
if (-not $sp) {
    Write-Error @"
Service principal for AppId $ClientId was not found in tenant $($ctx.TenantId).
The Autopilot Monitor app must be admin-consented in this tenant first (one-time consent at
https://login.microsoftonline.com/$($ctx.TenantId)/adminconsent?client_id=$ClientId).
"@
    return
}
Write-Host "[1/4] Found: '$($sp.displayName)' (ObjectId=$($sp.id))" -ForegroundColor Green

# ---- 2. Microsoft Graph SP (resource) ----
Write-Host ""
Write-Host "[2/4] Locating Microsoft Graph service principal..." -ForegroundColor Cyan
$graphUri = "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId eq '$MicrosoftGraphAppId'&`$select=id,appRoles"
$graphResponse = Invoke-MgGraphRequest -Method GET -Uri $graphUri
$graphSp = @($graphResponse.value) | Select-Object -First 1
if (-not $graphSp) {
    Write-Error "Microsoft Graph service principal not found in tenant - unexpected, please report."
    return
}
Write-Host "[2/4] Microsoft Graph SP ObjectId=$($graphSp.id), exposes $($graphSp.appRoles.Count) appRoles" -ForegroundColor Green

# ---- 3. Read existing assignments on the Autopilot Monitor SP ----
Write-Host ""
Write-Host "[3/4] Reading existing appRoleAssignments..." -ForegroundColor Cyan
$existingUri = "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignments"
$existingResponse = Invoke-MgGraphRequest -Method GET -Uri $existingUri
$existing = @($existingResponse.value)
Write-Host "[3/4] Currently assigned: $($existing.Count) role(s)"

$existingMap = @{}
foreach ($asn in $existing) {
    $existingMap[$asn.appRoleId] = [pscustomobject]@{
        AssignmentId        = $asn.id
        ResourceDisplayName = $asn.resourceDisplayName
        AppRoleId           = $asn.appRoleId
    }
}

$graphRoleById = @{}
foreach ($r in $graphSp.appRoles) { $graphRoleById[$r.id] = $r.value }

if ($existing.Count -gt 0) {
    Write-Host ""
    Write-Host "    Current grants:" -ForegroundColor Gray
    foreach ($asn in $existing) {
        $name = $graphRoleById[$asn.appRoleId]
        if (-not $name) { $name = "<non-Graph resource: $($asn.resourceDisplayName)>" }
        Write-Host "      - $name" -ForegroundColor Gray
    }
}

if ($VerifyOnly) {
    Write-Host ""
    Write-Host "VerifyOnly mode - no changes made." -ForegroundColor Yellow
    return
}

# ---- 4. Grant or Revoke ----
Write-Host ""
$action = if ($Revoke) { 'Revoke' } else { 'Grant' }
Write-Host "[4/4] $action mode - processing $($Permissions.Count) permission(s)..." -ForegroundColor Cyan

$results = @()
foreach ($permName in $Permissions) {
    $appRole = $graphSp.appRoles | Where-Object {
        $_.value -eq $permName -and $_.allowedMemberTypes -contains 'Application'
    } | Select-Object -First 1

    if (-not $appRole) {
        Write-Warning "  [SKIP]  '$permName' - not found on Microsoft Graph as an Application role"
        $results += [pscustomobject]@{ Permission = $permName; Status = 'NotFound' }
        continue
    }

    if ($Revoke) {
        if (-not $existingMap.ContainsKey($appRole.id)) {
            Write-Host "  [SKIP]  '$permName' - not currently granted" -ForegroundColor Gray
            $results += [pscustomobject]@{ Permission = $permName; Status = 'NotGranted' }
            continue
        }
        $assignmentId = $existingMap[$appRole.id].AssignmentId
        $revokeUri = "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignments/$assignmentId"
        if ($PSCmdlet.ShouldProcess("$permName on SP $($sp.id)", "Revoke")) {
            try {
                Invoke-MgGraphRequest -Method DELETE -Uri $revokeUri | Out-Null
                Write-Host "  [DONE]  '$permName' revoked" -ForegroundColor Green
                $results += [pscustomobject]@{ Permission = $permName; Status = 'Revoked' }
            }
            catch {
                Write-Warning "  [FAIL]  '$permName' revoke failed: $($_.Exception.Message)"
                $results += [pscustomobject]@{ Permission = $permName; Status = 'Failed' }
            }
        }
        continue
    }

    if ($existingMap.ContainsKey($appRole.id)) {
        Write-Host "  [OK]    '$permName' already granted" -ForegroundColor Gray
        $results += [pscustomobject]@{ Permission = $permName; Status = 'AlreadyGranted' }
        continue
    }

    $body = @{
        principalId = $sp.id
        resourceId  = $graphSp.id
        appRoleId   = $appRole.id
    } | ConvertTo-Json
    $grantUri = "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignments"

    if ($PSCmdlet.ShouldProcess("$permName on SP $($sp.id)", "Grant")) {
        try {
            Invoke-MgGraphRequest -Method POST -Uri $grantUri -Body $body -ContentType 'application/json' | Out-Null
            Write-Host "  [DONE]  '$permName' granted" -ForegroundColor Green
            $results += [pscustomobject]@{ Permission = $permName; Status = 'Granted' }
        }
        catch {
            Write-Warning "  [FAIL]  '$permName' grant failed: $($_.Exception.Message)"
            $results += [pscustomobject]@{ Permission = $permName; Status = 'Failed' }
        }
    }
}

Write-Host ""
Write-Host "=========== Summary ===========" -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-Host
Write-Host ""
Write-Host "Note: token-cached backends may need up to ~1h to see the new permission set." -ForegroundColor Yellow
Write-Host "      Use 'Refresh permission status' in the admin UI to apply changes immediately." -ForegroundColor Yellow

Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null
