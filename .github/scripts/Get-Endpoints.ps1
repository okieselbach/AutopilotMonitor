<#
.SYNOPSIS
    The one place the workflows and the release scripts take their hosts from.

.DESCRIPTION
    CI's member of the URL registry (Shared Constants.cs, web lib/config.ts, MCP src/config.ts):
    every own host the workflows, the .github/scripts helpers and the local release build talk to,
    plus the Authenticode timestamp server, is defined here once. Assert-HostsFromRegistry.ps1
    (Workflow gates) fails on any of these hosts spelled out elsewhere under .github/.

    The storage URLs derive from the account name, so blob and table can never point at two
    different accounts.

    A file in the repository rather than a GitHub variable: the local release build reads it too,
    and bearer tokens are sent to these URLs -- where a token goes stays a reviewed line in git.

.PARAMETER ExportToGitHubEnv
    Write every entry to $GITHUB_ENV instead of returning it, so the following steps of the job
    read them as environment variables ($env:AGENT_CONTAINER_URL, ${{ env.AGENT_CONTAINER_URL }}).

.OUTPUTS
    [ordered] hashtable, keyed by the names the environment variables carry.

.EXAMPLE
    $endpoints = & "$PSScriptRoot/Get-Endpoints.ps1"
    Invoke-WebRequest -Uri "$($endpoints.AGENT_CONTAINER_URL)/version.json"
#>
[CmdletBinding()]
param(
    [switch]$ExportToGitHubEnv
)

$ErrorActionPreference = 'Stop'

$storageAccount = 'autopilotmonitoreu'
# Pre-migration account in another tenant; only the fail-soft legacy mirror still writes there.
$legacyStorageAccount = 'autopilotmonitor'

$blob  = "https://$storageAccount.blob.core.windows.net"
$table = "https://$storageAccount.table.core.windows.net"

$endpoints = [ordered]@{
    STORAGE_BLOB_URL           = $blob
    STORAGE_TABLE_URL          = $table
    AGENT_CONTAINER_URL        = "$blob/agent"
    VERSIONS_CONTAINER_URL     = "$blob/versions"
    STATS_CONTAINER_URL        = "$blob/stats"
    # Platform settings row; CI writes only its agent and bootstrap version columns.
    ADMIN_CONFIG_URL           = "$table/AdminConfiguration(PartitionKey='GlobalConfig',RowKey='config')"
    LEGACY_AGENT_CONTAINER_URL = "https://$legacyStorageAccount.blob.core.windows.net/agent"
    # Front Door in front of the agent container: what customer scripts download from.
    DOWNLOAD_ALIAS_URL         = 'https://download.autopilotmonitor.com/agent'
    API_BASE_URL               = 'https://autopilotmonitor-api-eu.azurewebsites.net'
    MCP_BASE_URL               = 'https://mcp.autopilotmonitor.com'
    PORTAL_BASE_URL            = 'https://portal.autopilotmonitor.com'
    WWW_BASE_URL               = 'https://www.autopilotmonitor.com'
    DOCS_BASE_URL              = 'https://docs.autopilotmonitor.com'
    CODE_SIGN_TIMESTAMP_URL    = 'http://timestamp.acs.microsoft.com'
}

if ($ExportToGitHubEnv) {
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) {
        throw '-ExportToGitHubEnv only works inside a GitHub Actions step.'
    }
    $endpoints.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } |
        Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
    $endpoints.GetEnumerator() | ForEach-Object { Write-Host "$($_.Key) = $($_.Value)" }
    return
}

return $endpoints
