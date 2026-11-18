#Requires -Version 7.2
#Requires -Modules Microsoft.Graph.Authentication
<#
.SYNOPSIS
    Deploys the (signed) Autopilot Monitor loader as an Intune PLATFORM SCRIPT and assigns it to a group.

.DESCRIPTION
    Downloads Start-AutopilotMonitor.ps1 from YOUR agent container (it must already be published and signed
    by your pipeline), checks the Authenticode signature + publisher, uploads it through the Graph beta
    deviceManagementScripts endpoint, and assigns it.

    Intune settings (match upstream's documented values):
      Run using logged-on credentials  : No  (SYSTEM)
      Enforce script signature check   : No  (the enrolling device does not have your cert in Trusted Publishers;
                                              the loader does its own publisher + hash verification)
      Run in 64-bit PowerShell host    : Yes

    Device Preparation (Autopilot v2) is different: the platform script runs only AFTER Microsoft 365 Apps.
    For full coverage deploy the MSI line-of-business app to the Device Preparation device group instead
    (portal steps in docs/05, section "Device Preparation").

.PARAMETER AgentContainerUrl  e.g. https://stautopilotmonitorprod.blob.core.windows.net/agent
.PARAMETER ExpectedPublisher  Subject pattern your signing cert carries, e.g. '*O=Clefsoft Ltd*'
.PARAMETER GroupName          Group to assign (default AutopilotMonitor-Devices; use Autopilot-Pilot first!)

.NOTES
    NOT EXECUTED by the author. The Graph payload follows the documented beta resource
    deviceManagementScript. Verify the assign payload in Graph Explorer if Intune returns 400;
    the portal route (docs/05) is the fallback. Use -WhatIf first.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $AgentContainerUrl,
    [Parameter(Mandatory)] [string] $ExpectedPublisher,
    [string] $GroupName = 'Autopilot-Pilot',
    [string] $ScriptName = 'Start Autopilot Monitor'
)
$ErrorActionPreference = 'Stop'

$tmp = Join-Path ([IO.Path]::GetTempPath()) 'Start-AutopilotMonitor.ps1'
Invoke-WebRequest -Uri "$($AgentContainerUrl.TrimEnd('/'))/Start-AutopilotMonitor.ps1" -OutFile $tmp -UseBasicParsing

$sig = Get-AuthenticodeSignature -FilePath $tmp
if ($sig.Status -ne 'Valid') { throw "Loader signature is '$($sig.Status)'. Do not upload an unsigned or untrusted loader." }
if ($sig.SignerCertificate.Subject -notlike $ExpectedPublisher) { throw "Loader signed by '$($sig.SignerCertificate.Subject)', expected '$ExpectedPublisher'." }
Write-Host "Signature OK: $($sig.SignerCertificate.Subject)" -ForegroundColor Green

Connect-MgGraph -TenantId $TenantId -NoWelcome -Scopes 'DeviceManagementConfiguration.ReadWrite.All', 'Group.Read.All' | Out-Null

$group = (Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/groups?`$filter=displayName eq '$GroupName'&`$select=id,displayName").value | Select-Object -First 1
if (-not $group) { throw "Group '$GroupName' not found. Run 05-New-IntuneGroups.ps1 first." }

$existing = (Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts?`$filter=displayName eq '$ScriptName'").value | Select-Object -First 1

$content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($tmp))
$payload = @{
    '@odata.type'         = '#microsoft.graph.deviceManagementScript'
    displayName           = $ScriptName
    description           = 'Autopilot Monitor loader. Guards make it a no-op on devices that are not mid-enrollment.'
    scriptContent         = $content
    fileName              = 'Start-AutopilotMonitor.ps1'
    runAsAccount          = 'system'
    enforceSignatureCheck = $false
    runAs32Bit            = $false
}

if (-not $PSCmdlet.ShouldProcess($ScriptName, "Create/update platform script and assign to $GroupName")) { return }

if ($existing) {
    Invoke-MgGraphRequest -Method PATCH -Uri "https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts/$($existing.id)" -Body ($payload | ConvertTo-Json -Depth 5) -ContentType 'application/json' | Out-Null
    $id = $existing.id; Write-Host "Updated existing script $id"
} else {
    $new = Invoke-MgGraphRequest -Method POST -Uri 'https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts' -Body ($payload | ConvertTo-Json -Depth 5) -ContentType 'application/json'
    $id = $new.id; Write-Host "Created script $id" -ForegroundColor Green
}

$assign = @{ deviceManagementScriptAssignments = @(@{ target = @{ '@odata.type' = '#microsoft.graph.groupAssignmentTarget'; groupId = $group.id } }) }
Invoke-MgGraphRequest -Method POST -Uri "https://graph.microsoft.com/beta/deviceManagement/deviceManagementScripts/$id/assign" -Body ($assign | ConvertTo-Json -Depth 6) -ContentType 'application/json' | Out-Null
Write-Host "Assigned to '$GroupName'. Detection: Intune platform scripts report Success/Failed per device; the functional check is a session appearing in the portal (docs/05)." -ForegroundColor Green
