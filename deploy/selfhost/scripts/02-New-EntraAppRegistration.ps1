#Requires -Version 7.2
#Requires -Modules Microsoft.Graph.Authentication, Microsoft.Graph.Applications, Microsoft.Graph.Identity.SignIns
<#
.SYNOPSIS
    Creates the multi-tenant Entra app registration that Autopilot Monitor needs, grants tenant-wide
    consent in YOUR tenant, and stores the client secret in Key Vault (never on screen).

.DESCRIPTION
    Reconstructed from the backend/web/MCP source, because upstream's own setup tooling is private.
    One app registration serves three purposes:
      * Portal sign-in (SPA, MSAL) and the API audience   : api://<appId>, scope access_as_user
      * The MCP server's OAuth client (confidential)      : redirect <mcp>/oauth/callback + client secret
      * The backend's Graph client-credentials identity    : Graph application permissions, consented
                                                            per customer tenant (here: just yours)

    What the code requires on the registration (evidence in docs/02-architecture-and-prerequisites.md):
      signInAudience           AzureADMultipleOrgs   (JwtBearer authority "organizations")
      identifierUris           api://<appId>         (audience set is "<id>" and "api://<id>")
      scope                    access_as_user
      app role                 access_as_application (Application)  -> unattended MCP/automation principals
      app roles (User)         Admin, Operator, Viewer               -> optional Entra-role -> portal-role mapping
      access token version     2
      optional access claims   acct, idtyp                           -> guest detection, app-vs-user detection
      Graph application perm   DeviceManagementServiceConfig.Read.All  -> REQUIRED (device validation)
      SPA redirect             <portal origin>
      Web redirects            <portal>/settings/tenant/autopilot, <portal>/settings  (admin-consent return)
                               <mcp>/oauth/callback                    (MCP OAuth)

.PARAMETER PortalUrl   e.g. https://swa-autopilotmonitor-prod.azurestaticapps.net
.PARAMETER McpUrl      Optional. e.g. https://ca-autopilotmonitor-mcp-prod.<hash>.uksouth.azurecontainerapps.io
.PARAMETER KeyVaultName  Where the client secret is stored (secret name: entra-client-secret)
.PARAMETER OptionalFeatures  Extra Graph application permissions (see Grant-AutopilotMonitorAddOn.ps1 upstream)
.PARAMETER SecretMonths  Client secret lifetime. Short on purpose; rotate with this script's -RotateSecretOnly.
.PARAMETER PreAuthorizeAzureCli  Pre-authorizes the Azure CLI client app (04b07795-...) on access_as_user so an
    operator can run `az account get-access-token --scope api://<appId>/access_as_user` for runbook API calls
    (rules reseed, admin tasks). It adds no new people: the caller still needs a portal/Global Admin role. Off by default.

.EXAMPLE
    ./02-New-EntraAppRegistration.ps1 -TenantId clefsoft.co.uk -PortalUrl https://swa-...azurestaticapps.net `
        -KeyVaultName kv-autopilotmonitor-prod -OptionalFeatures IntuneDeviceBinding

.NOTES
    NOT EXECUTED by the author (no PowerShell/Graph SDK in the authoring sandbox). The property names and
    cmdlets follow the Microsoft Graph PowerShell SDK v2 and the Graph v1.0 application resource.
    Run once with -WhatIf first. You need: Application Administrator (or Cloud App Admin) to create the app,
    and Global Administrator or Privileged Role Administrator for the tenant-wide consent grants.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [ValidatePattern('^https://[^/]+$')] [string] $PortalUrl,
    [ValidatePattern('^https://[^/]+$')] [string] $McpUrl,
    [Parameter(Mandatory)] [string] $KeyVaultName,
    [string] $DisplayName = 'Autopilot Monitor (clefsoft)',
    [ValidateSet('IntuneDeviceBinding', 'ScriptDisplayNames', 'W365CloudPcValidation')]
    [string[]] $OptionalFeatures = @(),
    [ValidateRange(1, 24)] [int] $SecretMonths = 6,
    [switch] $RotateSecretOnly,
    [switch] $PreAuthorizeAzureCli
)

$ErrorActionPreference = 'Stop'
$graphAppId = '00000003-0000-0000-c000-000000000000'

Connect-MgGraph -TenantId $TenantId -NoWelcome -Scopes @(
    'Application.ReadWrite.All',
    'AppRoleAssignment.ReadWrite.All',
    'DelegatedPermissionGrant.ReadWrite.All'
) | Out-Null

function Save-ClientSecret {
    param([string] $AppObjectId)
    $end = (Get-Date).AddMonths($SecretMonths).ToUniversalTime()
    $cred = Add-MgApplicationPassword -ApplicationId $AppObjectId -PasswordCredential @{
        displayName = "keyvault-$(Get-Date -Format 'yyyyMMdd')"
        endDateTime = $end
    }
    # Value goes file -> Key Vault -> deleted. It never reaches the console or the shell history.
    $tmp = New-TemporaryFile
    try {
        [IO.File]::WriteAllText($tmp.FullName, $cred.SecretText)
        az keyvault secret set --vault-name $KeyVaultName --name 'entra-client-secret' --file $tmp.FullName `
            --encoding utf-8 --expires $end.ToString('yyyy-MM-ddTHH:mm:ssZ') --only-show-errors --output none
        if ($LASTEXITCODE -ne 0) { throw 'az keyvault secret set failed (do you hold "Key Vault Secrets Officer" on the vault?)' }
    } finally { Remove-Item $tmp.FullName -Force -ErrorAction SilentlyContinue }
    Write-Host "Client secret stored in Key Vault '$KeyVaultName' (expires $($end.ToString('yyyy-MM-dd')))." -ForegroundColor Green
}

# ---------------------------------------------------------------- existing app? (idempotent)
$existing = Get-MgApplication -Filter "displayName eq '$DisplayName'" -ErrorAction Stop | Select-Object -First 1
if ($RotateSecretOnly) {
    if (-not $existing) { throw "No app named '$DisplayName' found." }
    if ($PSCmdlet.ShouldProcess($DisplayName, 'Create new client secret and store it in Key Vault')) { Save-ClientSecret -AppObjectId $existing.Id }
    return
}

# ---------------------------------------------------------------- Graph permission ids, looked up by NAME (never hard-code GUIDs)
$graphSp = Get-MgServicePrincipal -Filter "appId eq '$graphAppId'"
$roleId  = @{}; foreach ($r in $graphSp.AppRoles) { $roleId[$r.Value] = $r.Id }
$scopeId = @{}; foreach ($s in $graphSp.Oauth2PermissionScopes) { $scopeId[$s.Value] = $s.Id }

$graphAppPerms = @('DeviceManagementServiceConfig.Read.All')   # REQUIRED: device validation (Autopilot / Corporate Identifier / Device Association)
$featureMap = @{
    IntuneDeviceBinding   = 'DeviceManagementManagedDevices.Read.All'
    ScriptDisplayNames    = 'DeviceManagementScripts.Read.All'
    W365CloudPcValidation = 'CloudPC.Read.All'
}
foreach ($f in $OptionalFeatures) { $graphAppPerms += $featureMap[$f] }
foreach ($p in $graphAppPerms) { if (-not $roleId.ContainsKey($p)) { throw "Graph app role '$p' not found in this tenant's Graph service principal." } }

$scopeGuid = [guid]::NewGuid().ToString()
$roleGuids = @{ access_as_application = [guid]::NewGuid().ToString(); Admin = [guid]::NewGuid().ToString(); Operator = [guid]::NewGuid().ToString(); Viewer = [guid]::NewGuid().ToString() }

$spaUris = @($PortalUrl)
$webUris = @("$PortalUrl/settings/tenant/autopilot", "$PortalUrl/settings")
if ($McpUrl) { $webUris += "$McpUrl/oauth/callback" }

$body = @{
    displayName    = $DisplayName
    signInAudience = 'AzureADMultipleOrgs'
    api = @{
        requestedAccessTokenVersion = 2
        oauth2PermissionScopes = @(@{
            id = $scopeGuid; value = 'access_as_user'; type = 'Admin'; isEnabled = $true
            adminConsentDisplayName = 'Access Autopilot Monitor as the signed-in user'
            adminConsentDescription = 'Lets the app call the Autopilot Monitor API on behalf of the signed-in user.'
            userConsentDisplayName  = 'Access Autopilot Monitor as you'
            userConsentDescription  = 'Lets the app call the Autopilot Monitor API on your behalf.'
        })
    }
    appRoles = @(
        @{ id = $roleGuids.access_as_application; value = 'access_as_application'; displayName = 'Access Autopilot Monitor as an application'
           description = 'Unattended read-only access (MCP automation, pipelines).'; allowedMemberTypes = @('Application'); isEnabled = $true },
        @{ id = $roleGuids.Admin;    value = 'Admin';    displayName = 'Autopilot Monitor Admin';    description = 'Tenant admin.';  allowedMemberTypes = @('User'); isEnabled = $true },
        @{ id = $roleGuids.Operator; value = 'Operator'; displayName = 'Autopilot Monitor Operator'; description = 'Operator.';      allowedMemberTypes = @('User'); isEnabled = $true },
        @{ id = $roleGuids.Viewer;   value = 'Viewer';   displayName = 'Autopilot Monitor Viewer';   description = 'Read-only.';     allowedMemberTypes = @('User'); isEnabled = $true }
    )
    optionalClaims = @{ accessToken = @(@{ name = 'acct'; essential = $false }, @{ name = 'idtyp'; essential = $false }) }
    spa = @{ redirectUris = $spaUris }
    web = @{ redirectUris = $webUris; implicitGrantSettings = @{ enableIdTokenIssuance = $false; enableAccessTokenIssuance = $false } }
    requiredResourceAccess = @(@{
        resourceAppId  = $graphAppId
        resourceAccess = @($graphAppPerms | ForEach-Object { @{ id = $roleId[$_]; type = 'Role' } }) +
                         @(@{ id = $scopeId['User.Read']; type = 'Scope' })
    })
}

if ($existing) {
    Write-Host "App '$DisplayName' already exists (appId $($existing.AppId)); updating redirect URIs only." -ForegroundColor Yellow
    if ($PSCmdlet.ShouldProcess($DisplayName, 'Update redirect URIs')) {
        Update-MgApplication -ApplicationId $existing.Id -Spa @{ redirectUris = $spaUris } -Web @{ redirectUris = $webUris }
    }
    $app = $existing
} else {
    if (-not $PSCmdlet.ShouldProcess($DisplayName, 'Create multi-tenant app registration')) { return }
    $app = New-MgApplication -BodyParameter $body
    # identifier URI needs the appId, which only exists now
    Update-MgApplication -ApplicationId $app.Id -IdentifierUris @("api://$($app.AppId)")
    if ($PreAuthorizeAzureCli) {
        # Separate PATCH: Graph can reject preAuthorizedApplications in the same request that creates the scope.
        Update-MgApplication -ApplicationId $app.Id -BodyParameter @{ api = @{ preAuthorizedApplications = @(
            @{ appId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'; delegatedPermissionIds = @($scopeGuid) }) } }
        Write-Host 'Azure CLI pre-authorized on access_as_user.' -ForegroundColor Yellow
    }
    Write-Host "Created app '$DisplayName' appId=$($app.AppId)" -ForegroundColor Green
}

# ---------------------------------------------------------------- service principal + tenant-wide consent (own tenant)
$sp = Get-MgServicePrincipal -Filter "appId eq '$($app.AppId)'" | Select-Object -First 1
if (-not $sp) { $sp = New-MgServicePrincipal -AppId $app.AppId }

$have = @(Get-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $sp.Id -All | ForEach-Object { $_.AppRoleId })
foreach ($p in $graphAppPerms) {
    if ($have -contains $roleId[$p]) { Write-Host "  consent present: $p"; continue }
    New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $sp.Id -PrincipalId $sp.Id -ResourceId $graphSp.Id -AppRoleId $roleId[$p] | Out-Null
    Write-Host "  granted (application): $p" -ForegroundColor Green
}

# Delegated consent: the app's own scope, and Graph User.Read, for all users
$grants = @(Get-MgOauth2PermissionGrant -Filter "clientId eq '$($sp.Id)'" -All)
function Grant-Delegated($resourceSpId, $scope) {
    if ($grants | Where-Object { $_.ResourceId -eq $resourceSpId -and $_.Scope -match [regex]::Escape($scope) }) { Write-Host "  consent present (delegated): $scope"; return }
    New-MgOauth2PermissionGrant -BodyParameter @{ clientId = $sp.Id; consentType = 'AllPrincipals'; resourceId = $resourceSpId; scope = $scope } | Out-Null
    Write-Host "  granted (delegated): $scope" -ForegroundColor Green
}
Grant-Delegated $sp.Id 'access_as_user'
Grant-Delegated $graphSp.Id 'User.Read'

# ---------------------------------------------------------------- secret -> Key Vault
if (-not $existing) { Save-ClientSecret -AppObjectId $app.Id }

[pscustomobject]@{
    AppId            = $app.AppId
    ObjectId         = $app.Id
    ServicePrincipal = $sp.Id
    Audience         = "api://$($app.AppId)"
    Next             = "Re-run bicep pass 2 with entraClientId=$($app.AppId); set NEXT_PUBLIC_ENTRA_CLIENT_ID for the web build."
}
