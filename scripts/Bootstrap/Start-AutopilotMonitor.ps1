<#
.SYNOPSIS
    Stage 1 of the Autopilot Monitor bootstrap: fetches the current installer and runs it.

.DESCRIPTION
    This is the file an administrator assigns in Intune, and the same file the bootstrap MSI
    installs and runs -- one implementation for both delivery channels; the MSI is only the
    packaging. It carries no enrollment logic at all: it downloads the published
    Install-AutopilotMonitor.ps1, proves that it was signed by Autopilot Monitor, and runs it
    in this process.

    Why two stages: the installer holds every guard, relax rule and download path, and those
    change. Whoever assigns the installer directly has to replace their Intune copy each
    time; whoever assigns this loader never does, because the part that changes lives on the
    server. Assigning the installer directly stays supported and unchanged.

    This file is deliberately boring. It is the one piece that cannot be fixed remotely, so
    it does the least it can: download, verify, invoke, log.

    Verification is by Authenticode publisher, not by hash. A hash oracle would have to be
    fetched from the same host as the script itself, so it could not survive a compromise of
    that host, while the expected publisher below travels with this file. It pins the
    certificate SUBJECT and not a thumbprint, so a certificate renewal does not brick a
    loader that is already deployed on devices.

    Fail-soft: the loader always exits 0 and never throws into the caller. A device that
    cannot reach the download endpoint is a device without monitoring, never a failed
    enrollment step. Verification failures are the same: nothing runs, and the reason is in
    the log. Refusing to execute unverified code as SYSTEM is the point of this file.

    This file MUST remain pure ASCII (PS 5.1 reads BOM-less files as ANSI).

.PARAMETER BootstrapUrl
    Installer URL. Defaults to the published stable installer; the -Dev variant of this
    loader is rendered from that default at publish time.

.PARAMETER LogFileName
    Log file inside the agent log directory. The MSI channel passes bootstrap-msi.log so the
    delivery channel stays visible in a diagnostics package; the diag tooling keys on these
    names, so do not rename them.

.PARAMETER BootstrapArguments
    Everything else is passed through to the installer unchanged, so any parameter the
    installer accepts can be set here without this file knowing about it.

.EXAMPLE
    .\Start-AutopilotMonitor.ps1

.EXAMPLE
    .\Start-AutopilotMonitor.ps1 -MaxBootstrapWindowHours 24

.NOTES
    Author  : Oliver Kieselbach (autopilotmonitor.com)
    Runtime : Windows PowerShell 5.1, SYSTEM context, during Autopilot enrollment

.CHANGELOG
    2026-09-08  v1.1  Also used by the bootstrap MSI, which replaced its own copy of this
                      logic; -LogFileName keeps the channel visible in diagnostics.
    2026-09-08  v1.0  Initial version: two-stage bootstrap, publisher-pinned verification.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$BootstrapUrl = "https://download.autopilotmonitor.com/agent/Install-AutopilotMonitor.ps1",

    [Parameter(Mandatory = $false)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$LogFileName = "bootstrap-loader.log",

    [Parameter(Mandatory = $false, ValueFromRemainingArguments = $true)]
    [string[]]$BootstrapArguments
)

# Script version (bump on meaningful changes; see .CHANGELOG above)
$ScriptVersion = "1.1"

# The publisher every downloaded stage-2 script must carry. A -like pattern on the subject,
# NOT a thumbprint: certificates are renewed, deployed loaders are not.
$ExpectedPublisher = "*O=glueckkanja AG*"

$LoaderBasePath = "$env:ProgramData\AutopilotMonitor"
$LoaderLogPath  = "$LoaderBasePath\Logs"
$LogFile        = "$LoaderLogPath\$LogFileName"
$BootstrapPath  = "$LoaderBasePath\Install-AutopilotMonitor.ps1"

$DownloadAttempts = 3
$RetryDelaySeconds = 10

# Write-Host as well as the file: the Intune script output is what an administrator sees in
# the portal, the file is what a support case reads.
function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logMessage = "[$timestamp] $Message"
    Write-Host $logMessage
    Add-Content -Path $LogFile -Value $logMessage -ErrorAction SilentlyContinue
}

# True only when the file is signed, the signature validates against the machine's trust
# chain, and the signer is us. Any error is a false -- an unreadable signature is an
# unverified script.
function Test-BootstrapSignature {
    param([string]$Path)

    try {
        $sig = Get-AuthenticodeSignature -FilePath $Path -ErrorAction Stop
    }
    catch {
        Write-Log "Signature check failed to run: $($_.Exception.Message)"
        return $false
    }

    if ($null -eq $sig) {
        Write-Log "Signature check returned nothing."
        return $false
    }

    $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { "<unsigned>" }
    Write-Log "Signature status: $($sig.Status). Signer: $subject"

    if ($sig.Status -ne 'Valid') {
        Write-Log "REJECTED: expected signature status 'Valid'."
        return $false
    }
    if ($subject -notlike $ExpectedPublisher) {
        Write-Log "REJECTED: signer does not match the expected publisher."
        return $false
    }

    Write-Log "Signature accepted (thumbprint $($sig.SignerCertificate.Thumbprint))."
    return $true
}

# Downloads the installer and returns $true once a downloaded copy passes verification.
# Verification lives inside the retry loop on purpose: a truncated or proxy-mangled body
# fails the signature check, and that is worth another attempt.
function Get-VerifiedBootstrapScript {
    param([string]$Url, [string]$Destination)

    for ($attempt = 1; $attempt -le $DownloadAttempts; $attempt++) {
        try {
            Write-Log "Download attempt $attempt/$DownloadAttempts from $Url"
            Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing -TimeoutSec 60 -ErrorAction Stop

            if (Test-BootstrapSignature -Path $Destination) {
                return $true
            }
        }
        catch {
            Write-Log "Download attempt $attempt failed: $($_.Exception.Message)"
        }

        if ($attempt -lt $DownloadAttempts) {
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }

    return $false
}

# Main flow. Only invoked when the script is run directly (see entry guard at the bottom),
# so the Pester suite can dot-source this file without bootstrapping anything.
function Start-BootstrapLoader {
    New-Item -ItemType Directory -Force -Path $LoaderLogPath -ErrorAction SilentlyContinue | Out-Null

    Write-Log "=== Autopilot Monitor bootstrap loader v$ScriptVersion started (user=$env:USERNAME, pid=$PID) ==="

    # PS 5.1 on an OOBE device still defaults to TLS 1.0 for this process.
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    catch {
        Write-Log "INFO: could not raise the TLS protocol: $($_.Exception.Message)"
    }

    if (-not (Get-VerifiedBootstrapScript -Url $BootstrapUrl -Destination $BootstrapPath)) {
        Write-Log "No verified installer available after $DownloadAttempts attempt(s). Nothing was executed."
        return
    }

    Write-Log "Executing $BootstrapPath"

    # Same process, so the execution policy this loader runs under applies to stage 2 as
    # well and no second policy decision can strand the installer. Splatting an array of
    # remaining arguments keeps this file free of any knowledge about the installer's
    # parameters.
    $arguments = @()
    if ($BootstrapArguments) { $arguments = $BootstrapArguments }

    try {
        & $BootstrapPath @arguments
        Write-Log "Installer finished (exit code $LASTEXITCODE)."
    }
    catch {
        Write-Log "Installer threw: $($_.Exception.Message)"
    }
}

# Entry guard: dot-sourcing (Pester) loads the functions without running the loader.
if ($MyInvocation.InvocationName -ne '.') {
    try {
        Start-BootstrapLoader
    }
    catch {
        # Nothing above should throw; if it does, it must not become a failed Intune script.
        Write-Log "Loader failed: $($_.Exception.Message)"
    }
    exit 0
}
