#Requires -Version 5.1
<#
.SYNOPSIS
    LAB ONLY. Creates a self-signed Authenticode (code-signing) certificate and imports it into the dedicated
    signing Key Vault, so the UNCHANGED upstream pipeline (AzureSignTool + Key Vault) can sign with it.

.DESCRIPTION
    Why this works: the loader/installer/agent pin the signer's SUBJECT (*O=<org>*) and require Authenticode
    status "Valid". "Valid" means the chain is trusted on that device. A self-signed cert is trusted only on
    devices where YOU put it in LocalMachine\Root first (script 09). So this suits Hyper-V test VMs only.
    It must never be used on real or customer hardware.

    The private key lives in Key Vault (imported, not exportable afterwards) and is deleted from this PC.
    Output: lab-signing.cer (PUBLIC part, safe to copy to test VMs).

    Set the patcher's publisherOrg to the same organisation (default "Clefsoft Lab") and the repo variable
    CODE_SIGN_EXPECTED_SUBJECT to "*O=Clefsoft Lab*".

.PARAMETER SigningKeyVaultName  Output "signingKeyVaultName" of the Bicep deployment (kv-autopilotmonitor-sign)
.PARAMETER CertificateName      Key Vault certificate name; becomes repo variable CODE_SIGN_CERTIFICATE_NAME

.NOTES
    NOT EXECUTED by the author (needs Windows PowerShell/pwsh on Windows; New-SelfSignedCertificate is Windows-only).
    Needs Azure CLI and "Key Vault Certificates Officer" on the signing vault (Bicep deployerObjectId grants it).
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $SigningKeyVaultName,
    [string] $CertificateName = 'autopilotmonitor-lab-signing',
    [string] $Organisation = 'Clefsoft Lab',
    [ValidateRange(1, 3)] [int] $Years = 1,
    [string] $OutDir = (Get-Location).Path
)
$ErrorActionPreference = 'Stop'
$subject = "CN=Autopilot Monitor Lab Signing, O=$Organisation"

if (-not $PSCmdlet.ShouldProcess($SigningKeyVaultName, "Create self-signed code-signing cert '$subject' and import it")) { return }

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -KeyAlgorithm RSA -KeyLength 3072 `
    -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy Exportable `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears($Years)

# one-time random PFX password, never printed
$pwBytes = [byte[]]::new(24); [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($pwBytes)
$pw = [Convert]::ToBase64String($pwBytes)
$pfx = Join-Path ([IO.Path]::GetTempPath()) "lab-signing-$([guid]::NewGuid()).pfx"
$cer = Join-Path $OutDir 'lab-signing.cer'
try {
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pw -AsPlainText -Force) | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null

    az keyvault certificate import --vault-name $SigningKeyVaultName --name $CertificateName --file $pfx --password $pw --only-show-errors --output none
    if ($LASTEXITCODE -ne 0) { throw 'az keyvault certificate import failed (role "Key Vault Certificates Officer" on the signing vault?)' }
} finally {
    Remove-Item $pfx -Force -ErrorAction SilentlyContinue
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue   # private key leaves this PC
}

Write-Host "Imported '$CertificateName' into $SigningKeyVaultName. Thumbprint $($cert.Thumbprint), expires $($cert.NotAfter.ToString('yyyy-MM-dd'))." -ForegroundColor Green
Write-Host "Public certificate: $cer   (copy this to test VMs and run 09-Trust-LabCert.ps1 there)"
Write-Host @"

Set these in the GitHub repo (Settings > Secrets and variables > Actions > Variables):
  CODE_SIGN_KEY_VAULT_URL     = https://$SigningKeyVaultName.vault.azure.net
  CODE_SIGN_CERTIFICATE_NAME  = $CertificateName
  CODE_SIGN_EXPECTED_SUBJECT  = *O=$Organisation*
  CODE_SIGN_CLIENT_ID         = <the OIDC app id from step 7>
Give that identity, on the SIGNING vault only:  az role assignment create --assignee <id> --role "Key Vault Crypto User" --scope <vault id>
                                                  az role assignment create --assignee <id> --role "Key Vault Certificate User" --scope <vault id>
Calendar the expiry. After it, every signed file stops verifying on devices.
"@
