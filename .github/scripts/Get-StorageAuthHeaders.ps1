<#
.SYNOPSIS
    Returns the request headers that authorize an Azure Storage REST call with Microsoft Entra.

.DESCRIPTION
    One place for the storage OAuth contract shared by every publisher in this repo -- the
    agent ZIP and MSI, the customer bootstrap scripts, the version manifests and the build
    counters. Resource id, the az invocation, the service version and the failure message
    live here so they cannot drift between call sites.

    Identity comes from whatever `az login` established: the federated GitHub OIDC principal
    in CI (azure/login, subject repo:okieselbach/AutopilotMonitor:ref:refs/heads/main), the
    operator's own account for the local build. No secret is read, stored or passed around.

    x-ms-version is MANDATORY here, not decoration: Entra-authorized requests are rejected
    below 2017-11-09, and Table requests without a SAS must carry the header at all. 2021-08-06
    is old enough to be deployed in every region and new enough for everything we send.

    Callers merge the result into their own headers -- `$auth + @{ ... }` yields a new
    hashtable per request, so nothing is shared across uploads:

        $auth = & "$PSScriptRoot/Get-StorageAuthHeaders.ps1"
        Invoke-RestMethod -Uri $url -Method Put -Headers ($auth + @{ 'x-ms-blob-type' = 'BlockBlob' }) -Body $bytes

    Acquire once per step and reuse: every call shells out to az (~1s) and the token is valid
    for the lifetime of a job.

.OUTPUTS
    [hashtable] Authorization + x-ms-version.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# The data-plane resource, not management.azure.com -- a management token is accepted by
# neither the blob nor the table endpoint.
$token = az account get-access-token --resource https://storage.azure.com --query accessToken -o tsv 2>$null

if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
    throw ('Could not acquire an Azure Storage token. In CI this means the azure/login step ' +
           'is missing or its federated credential does not cover this ref; locally, run ' +
           '`az login`. A token alone is not enough: the principal also needs Storage Blob ' +
           'Data Contributor on the container it writes, and Storage Table Data Contributor ' +
           'on the table -- a missing role shows up later as HTTP 403 AuthorizationPermissionMismatch.')
}

return @{
    'Authorization' = "Bearer $token"
    'x-ms-version'  = '2021-08-06'
}
