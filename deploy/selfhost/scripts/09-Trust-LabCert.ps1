#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    LAB ONLY. Run on a Hyper-V TEST VM: trust the self-signed signing certificate so Authenticode status is "Valid".

.DESCRIPTION
    Adds the PUBLIC certificate to LocalMachine\Root and LocalMachine\TrustedPublisher.
    The trust must exist BEFORE Autopilot starts, because the loader runs during OOBE.

    Easiest way, at the OOBE first screen of the VM:
      1. Press Shift+F10 (admin command prompt).
      2. Get lab-signing.cer onto the VM: attach a small ISO / VHDX with the file, or use Hyper-V "Insert Integration
         Services"/enhanced session file copy, e.g. D:\lab-signing.cer
      3. certutil -addstore -f Root D:\lab-signing.cer
         certutil -addstore -f TrustedPublisher D:\lab-signing.cer
      4. Take a Hyper-V CHECKPOINT now. Restore this checkpoint before every test run, so trust is always present
         and the agent's registry marker is always absent.
    Or run this script from the same prompt:  powershell -ExecutionPolicy Bypass -File D:\09-Trust-LabCert.ps1 -CerPath D:\lab-signing.cer

    Never do this on a real or customer device: it makes that machine trust anything signed with the lab key.
#>
param([Parameter(Mandatory)] [string] $CerPath)
$ErrorActionPreference = 'Stop'
$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $CerPath
foreach ($store in 'Root', 'TrustedPublisher') {
    $s = New-Object System.Security.Cryptography.X509Certificates.X509Store $store, 'LocalMachine'
    $s.Open('ReadWrite'); $s.Add($c); $s.Close()
    Write-Host "Added to LocalMachine\$store : $($c.Subject)  [$($c.Thumbprint)]"
}
Write-Host 'Now verify a signed file:  Get-AuthenticodeSignature <file>   (Status must be Valid)'
