#Requires -Version 7.2
#Requires -Modules Microsoft.Graph.Authentication, Microsoft.Graph.Groups, Microsoft.Graph.Applications
<#
.SYNOPSIS
    Creates the three groups for the Autopilot Monitor pilot (and optionally maps the admin group to the app's Admin role).

.DESCRIPTION
    AutopilotMonitor-Devices   Dynamic DEVICE group: every Autopilot-registered device ([ZTDId] physical id).
                               Target for the bootstrap platform script. Safe to be broad: the script has its own
                               "is this device mid-enrollment" guards. This is upstream's documented rule.
    Autopilot-Pilot            Dynamic DEVICE group: Autopilot devices carrying the group tag AM-Pilot.
                               Target for your pilot Autopilot profile / ESP / apps, so only pilot hardware is
                               exposed to new config while you evaluate. (Group tag is set at Autopilot import.)
    AutopilotMonitor-Admins    Assigned USER security group: the humans who may use the portal. Optional mapping
                               to the app role "Admin" (needs Entra ID P1 for group-to-app assignment) and
                               ALSO add them in the portal under Settings > Access Management (table role wins
                               over the claim; Entra roles are off until the tenant enables them).

    Dynamic membership requires Microsoft Entra ID P1 (included with M365 Business Premium / E3 / E5).

.NOTES
    NOT EXECUTED by the author. Idempotent: existing groups are reused. Use -WhatIf first.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [string] $PilotGroupTag = 'AM-Pilot',
    [string] $AppDisplayName = 'Autopilot Monitor (clefsoft)',
    [switch] $MapAdminsToAppRole
)
$ErrorActionPreference = 'Stop'
Connect-MgGraph -TenantId $TenantId -NoWelcome -Scopes 'Group.ReadWrite.All', 'Application.Read.All', 'AppRoleAssignment.ReadWrite.All' | Out-Null

function Ensure-Group {
    param([string] $Name, [string] $Description, [hashtable] $Extra)
    $g = Get-MgGroup -Filter "displayName eq '$Name'" -ErrorAction Stop | Select-Object -First 1
    if ($g) { Write-Host "  exists: $Name ($($g.Id))"; return $g }
    $body = @{ displayName = $Name; description = $Description; mailEnabled = $false; securityEnabled = $true; mailNickname = ($Name -replace '[^A-Za-z0-9]', '') } + $Extra
    if ($PSCmdlet.ShouldProcess($Name, 'Create group')) {
        $g = New-MgGroup -BodyParameter $body
        Write-Host "  created: $Name ($($g.Id))" -ForegroundColor Green
    }
    return $g
}

$devices = Ensure-Group -Name 'AutopilotMonitor-Devices' -Description 'All Autopilot-registered devices. Target of the Autopilot Monitor bootstrap script.' -Extra @{
    groupTypes = @('DynamicMembership'); membershipRuleProcessingState = 'On'
    membershipRule = '(device.devicePhysicalIds -any _ -startsWith "[ZTDId]")'
}
$pilot = Ensure-Group -Name 'Autopilot-Pilot' -Description "Pilot Autopilot devices (group tag $PilotGroupTag)." -Extra @{
    groupTypes = @('DynamicMembership'); membershipRuleProcessingState = 'On'
    membershipRule = "(device.devicePhysicalIds -any _ -eq `"[OrderID]:$PilotGroupTag`")"
}
$admins = Ensure-Group -Name 'AutopilotMonitor-Admins' -Description 'People allowed to use the Autopilot Monitor portal / MCP.' -Extra @{}

if ($MapAdminsToAppRole -and $admins) {
    $sp = Get-MgServicePrincipal -Filter "displayName eq '$AppDisplayName'" | Select-Object -First 1
    if (-not $sp) { throw "Enterprise app '$AppDisplayName' not found. Run 02-New-EntraAppRegistration.ps1 first." }
    $adminRole = $sp.AppRoles | Where-Object { $_.Value -eq 'Admin' }
    if ($PSCmdlet.ShouldProcess('AutopilotMonitor-Admins', "Assign app role 'Admin' on $AppDisplayName")) {
        New-MgGroupAppRoleAssignment -GroupId $admins.Id -PrincipalId $admins.Id -ResourceId $sp.Id -AppRoleId $adminRole.Id | Out-Null
        Write-Host "  mapped AutopilotMonitor-Admins -> app role Admin" -ForegroundColor Green
    }
}
Write-Host "`nDynamic groups take minutes to populate. Check: Entra admin center > Groups > <group> > Members."
