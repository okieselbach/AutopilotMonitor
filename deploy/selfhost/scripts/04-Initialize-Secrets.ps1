#Requires -Version 7.2
<#
.SYNOPSIS
    Creates the Key Vault secrets the Function app references (everything except the Entra client secret,
    which 02-New-EntraAppRegistration.ps1 writes), then restarts the app so references resolve.

.DESCRIPTION
    pagination-signing-key       random; signs pagination cursors and push topic HMACs (PaginationTokenSigningKey)
    ops-alert-webhook-secret     random; the Azure Monitor alert webhook is FAIL-CLOSED without it (OpsAlertWebhookSecret)
    signalr-connection-string    read from the SignalR resource; the backend has no managed-identity path for SignalR

    Values are generated in memory and passed by file, never printed.

.PARAMETER ResourceGroup  e.g. rg-autopilotmonitor-prod-uks
.PARAMETER Rotate         Regenerate the two random secrets even if they exist (invalidates outstanding cursors).

.NOTES
    NOT EXECUTED by the author (no PowerShell in the sandbox). Uses only the Azure CLI. Needs
    "Key Vault Secrets Officer" on the vault (bicep deployerObjectId grants it temporarily).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [Parameter(Mandatory)] [string] $KeyVaultName,
    [Parameter(Mandatory)] [string] $SignalRName,
    [Parameter(Mandatory)] [string] $FunctionAppName,
    [switch] $Rotate
)
$ErrorActionPreference = 'Stop'

function Set-KvSecret([string] $Name, [string] $Value) {
    $tmp = New-TemporaryFile
    try {
        [IO.File]::WriteAllText($tmp.FullName, $Value)
        az keyvault secret set --vault-name $KeyVaultName --name $Name --file $tmp.FullName --encoding utf-8 --only-show-errors --output none
        if ($LASTEXITCODE -ne 0) { throw "Failed to set secret $Name" }
        Write-Host "  set $Name" -ForegroundColor Green
    } finally { Remove-Item $tmp.FullName -Force -ErrorAction SilentlyContinue }
}
function Test-KvSecret([string] $Name) {
    az keyvault secret show --vault-name $KeyVaultName --name $Name --query id --output tsv --only-show-errors 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}
function New-RandomSecret([int] $Bytes = 48) {
    $b = [byte[]]::new($Bytes); [Security.Cryptography.RandomNumberGenerator]::Fill($b); [Convert]::ToBase64String($b)
}

foreach ($n in 'pagination-signing-key', 'ops-alert-webhook-secret') {
    if ((Test-KvSecret $n) -and -not $Rotate) { Write-Host "  exists, keeping $n"; continue }
    if ($PSCmdlet.ShouldProcess($n, 'Create random secret')) { Set-KvSecret $n (New-RandomSecret) }
}

if ($PSCmdlet.ShouldProcess('signalr-connection-string', 'Copy from SignalR resource')) {
    $cs = az signalr key list --name $SignalRName --resource-group $ResourceGroup --query primaryConnectionString --output tsv --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($cs)) { throw 'Could not read the SignalR connection string' }
    Set-KvSecret 'signalr-connection-string' $cs.Trim()
}

if (-not (Test-KvSecret 'entra-client-secret')) {
    Write-Warning "Secret 'entra-client-secret' is missing. Run 02-New-EntraAppRegistration.ps1 before expecting the API to authenticate."
}

if ($PSCmdlet.ShouldProcess($FunctionAppName, 'Restart so Key Vault references re-resolve')) {
    az functionapp restart --name $FunctionAppName --resource-group $ResourceGroup --only-show-errors --output none
    Write-Host "Restarted $FunctionAppName. Key Vault references can take a minute to show 'Resolved'." -ForegroundColor Green
}
Write-Host "`nCheck resolution:  az rest --method get --url `"https://management.azure.com/subscriptions/<sub>/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/$FunctionAppName/config/configreferences/appsettings?api-version=2023-12-01`" --query `"value[].{name:name,status:properties.status}`""
