# setup-backup-container.ps1
#
# Idempotent setup for the critical-table backup feature (plan §PR1).
#
# Creates the 'critical-table-backups' container in the backend storage account
# and applies a lifecycle rule that deletes blobs older than 90 days under the
# 'critical-table-backups/' prefix. Run once per environment, then again any
# time the storage account is rebuilt.
#
# WARNING (plan §Wave15 Azure-Hinweis): Blob Versioning and Blob Soft-Delete
# are STORAGE-ACCOUNT settings (not container-scoped). Enabling them affects
# every container in the account (e.g. 'diagnostics', 'offboarding-state').
# This script does NOT enable them automatically — operator must validate the
# blast radius separately and run the corresponding `az storage account
# blob-service-properties update` command if desired.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $StorageAccountName,
    [Parameter(Mandatory = $true)] [string] $ResourceGroupName,
    [string] $ContainerName = 'critical-table-backups',
    [int]    $RetentionDays = 90
)

$ErrorActionPreference = 'Stop'

Write-Host "==> Verifying Azure CLI login"
az account show --only-show-errors > $null

Write-Host "==> Ensuring container '$ContainerName' in account '$StorageAccountName'"
az storage container create `
    --account-name $StorageAccountName `
    --name $ContainerName `
    --auth-mode login `
    --only-show-errors `
    --fail-on-exist false | Out-Null

Write-Host "==> Building lifecycle rule (delete > $RetentionDays days, prefix '$ContainerName/')"
$policyJson = @"
{
  "rules": [
    {
      "enabled": true,
      "name": "delete-critical-table-backups",
      "type": "Lifecycle",
      "definition": {
        "actions": {
          "baseBlob": {
            "delete": { "daysAfterModificationGreaterThan": $RetentionDays }
          }
        },
        "filters": {
          "blobTypes": [ "blockBlob" ],
          "prefixMatch": [ "$ContainerName/" ]
        }
      }
    }
  ]
}
"@

$tmpFile = [System.IO.Path]::GetTempFileName()
try {
    Set-Content -Path $tmpFile -Value $policyJson -Encoding UTF8
    Write-Host "==> Applying lifecycle policy to storage account '$StorageAccountName'"
    Write-Host "    NOTE: this REPLACES the account's lifecycle policy. If other rules"
    Write-Host "          exist (e.g. for 'deletion-manifests'), merge them in manually."
    az storage account management-policy create `
        --account-name $StorageAccountName `
        --resource-group $ResourceGroupName `
        --policy "@$tmpFile" `
        --only-show-errors | Out-Null
}
finally {
    Remove-Item -Path $tmpFile -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "==> Done."
Write-Host "    Container ready: $ContainerName"
Write-Host "    Lifecycle: delete blobs > $RetentionDays days under '$ContainerName/' prefix"
Write-Host ""
Write-Host "Operator follow-ups (NOT applied by this script):"
Write-Host "  * If you also want Blob Versioning + Soft-Delete (defence-in-depth against"
Write-Host "    operator mistakes), run separately and confirm the blast radius first:"
Write-Host "      az storage account blob-service-properties update \\"
Write-Host "          --account-name $StorageAccountName \\"
Write-Host "          --resource-group $ResourceGroupName \\"
Write-Host "          --enable-versioning true \\"
Write-Host "          --enable-delete-retention true \\"
Write-Host "          --delete-retention-days 14"
Write-Host "    Both settings affect ALL containers in the account."
