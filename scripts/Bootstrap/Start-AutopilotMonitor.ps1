<#
.SYNOPSIS
    Downloads the current Autopilot Monitor installer and runs it.

.DESCRIPTION
    Assign this script in Intune once; the bootstrap MSI runs the same script. It downloads
    Install-AutopilotMonitor.ps1 from download.autopilotmonitor.com and runs it only when the
    file carries a valid signature from glueckkanja AG and is installer version 2.7 or later.
    Because the installer is fetched on every run, this script does not need replacing when
    the installer changes.

    The download and the log go into %ProgramData%\AutopilotMonitor-Loader-<random>, which only
    SYSTEM and administrators can change. The loader keeps the downloaded file open from the
    check to the end of the run, so what it checked is what runs. After a successful run the
    folder is removed; after a failure it keeps the log for diagnosis, and a later installer
    run removes it.

    The exit code is always 0, so the loader never fails enrollment. Without SYSTEM or
    administrator rights it stops with an error and changes nothing.

    Any other arguments are passed to the installer, for example -MaxBootstrapWindowHours 24.

.PARAMETER BootstrapUrl
    Installer to download. Default: the published installer.

.PARAMETER LogFileName
    Name of the log file. Default: bootstrap-loader.log.

.NOTES
    Requires Windows PowerShell 5.1.
    Changes: https://docs.autopilotmonitor.com/changelog/platform-changelog
#>

param(
    [string]$BootstrapUrl = "https://download.autopilotmonitor.com/agent/Install-AutopilotMonitor.ps1",

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$LogFileName = "bootstrap-loader.log"
)

# Unbound arguments reach the installer by name. [CmdletBinding()] or a [Parameter()]
# attribute would reject them instead.
$InstallerArguments = $args

$ScriptVersion = "1.4"

# A subject pattern, not a thumbprint, so a certificate renewal keeps deployed loaders working.
$ExpectedPublisher = "*O=glueckkanja AG*"

$ExpectedInstallerRole = "AutopilotMonitor-Installer"
$MinimumInstallerVersion = [version]"2.8"

$RunFolderPrefix = "AutopilotMonitor-Loader-"

$DownloadAttempts = 3
$RetryDelaySeconds = 10

# The log file, and the handle that keeps the verified installer unchanged until the run ends.
$RunState = [pscustomobject]@{ LogFile = $null; Installer = $null }

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logMessage = "[$timestamp] $Message"
    Write-Host $logMessage
    if ($RunState.LogFile) {
        Add-Content -Path $RunState.LogFile -Value $logMessage -ErrorAction SilentlyContinue
    }
}

### Shared with Install-AutopilotMonitor.ps1, word for word.

function Test-IsElevated {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-ProtectedDirectorySecurity {
    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    $system = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $admins = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    $users = New-Object System.Security.Principal.SecurityIdentifier([System.Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)
    $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($system, 'FullControl', $inherit, $propagation, 'Allow')))
    $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins, 'FullControl', $inherit, $propagation, 'Allow')))
    $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', $inherit, $propagation, 'Allow')))
    $security.SetOwner($admins)
    return $security
}

function Test-IsReparsePoint {
    param([string]$Path)
    return (([System.IO.File]::GetAttributes($Path) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Test-IsOwnedByUs {
    param([string]$Path)
    try {
        $owner = (Get-Item -LiteralPath $Path -Force -ErrorAction Stop).GetAccessControl([System.Security.AccessControl.AccessControlSections]::Owner).GetOwner([System.Security.Principal.SecurityIdentifier])
        return ($owner.IsWellKnown([System.Security.Principal.WellKnownSidType]::LocalSystemSid) -or
                $owner.IsWellKnown([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid))
    } catch {
        return $false
    }
}

function New-RunFolderName {
    $bytes = New-Object byte[] 4
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

function New-RunFolder {
    param([string]$Parent, [string]$Prefix)
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $path = Join-Path $Parent ($Prefix + (New-RunFolderName))
        if (Test-Path -LiteralPath $path) { continue }
        [System.IO.Directory]::CreateDirectory($path, (New-ProtectedDirectorySecurity)) | Out-Null
        if (-not (Test-IsReparsePoint $path) -and (Test-IsOwnedByUs $path)) { return $path }
    }
    throw "Could not create a run folder under $Parent."
}

### End of the shared functions.

# Keeps the system default (0, which includes TLS 1.3); otherwise adds TLS 1.2.
function Get-DownloadSecurityProtocol {
    param([Net.SecurityProtocolType]$Current)
    if ([int]$Current -eq 0) { return $Current }
    return ($Current -bor [Net.SecurityProtocolType]::Tls12)
}

# Exactly one role line and one version line, each at the start of a line; $null otherwise.
function Get-InstallerIdentity {
    param([string]$Text)
    $roles = [regex]::Matches($Text, '(?m)^\$ScriptRole[ \t]*=[ \t]*"([A-Za-z0-9.-]+)"[ \t]*\r?$')
    $versions = [regex]::Matches($Text, '(?m)^\$ScriptVersion[ \t]*=[ \t]*"(\d+(?:\.\d+){1,3})"[ \t]*\r?$')
    if ($roles.Count -ne 1 -or $versions.Count -ne 1) { return $null }
    return [pscustomobject]@{ Role = $roles[0].Groups[1].Value; Version = [version]$versions[0].Groups[1].Value }
}

# Runs after the signature check, so the lines it reads are signed.
function Test-InstallerIdentity {
    param([string]$Path)
    try {
        $identity = Get-InstallerIdentity -Text ([System.IO.File]::ReadAllText($Path))
    }
    catch {
        Write-Log "Identity check failed to run: $($_.Exception.Message)"
        return $false
    }
    if ($null -eq $identity) {
        Write-Log "REJECTED: the script does not declare exactly one role line and one version line."
        return $false
    }
    if ($identity.Role -ne $ExpectedInstallerRole) {
        Write-Log "REJECTED: role '$($identity.Role)' is not '$ExpectedInstallerRole'."
        return $false
    }
    if ($identity.Version -lt $MinimumInstallerVersion) {
        Write-Log "REJECTED: installer version $($identity.Version) is below the minimum $MinimumInstallerVersion."
        return $false
    }
    Write-Log "Installer accepted: $($identity.Role) v$($identity.Version)."
    return $true
}

# A valid signature from the expected publisher; any error counts as unverified.
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

    # The CN is enough for the log; the full subject is logged when it causes a rejection.
    $subject = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { "<unsigned>" }
    $signer = if ($subject -match 'CN=([^,]+)') { $Matches[1] } else { $subject }
    Write-Log "Signature status: $($sig.Status). Signer: $signer"

    if ($sig.Status -ne 'Valid') {
        Write-Log "REJECTED: expected signature status 'Valid'."
        return $false
    }
    if ($subject -notlike $ExpectedPublisher) {
        Write-Log "REJECTED: signer does not match the expected publisher. Full subject: $subject"
        return $false
    }

    Write-Log "Signature accepted (thumbprint $($sig.SignerCertificate.Thumbprint))."
    return $true
}

# A download that fails verification, such as a truncated one, is retried too. The run folder
# is checked before every attempt: one that is no longer a real folder of ours stops the run.
# The downloaded file is opened for reading before it is checked and stays open until the run
# ends, so no one can change or replace what was checked.
function Get-VerifiedBootstrapScript {
    param([string]$Url, [string]$Destination, [string]$RunFolder)

    for ($attempt = 1; $attempt -le $DownloadAttempts; $attempt++) {
        if ((Test-IsReparsePoint $RunFolder) -or -not (Test-IsOwnedByUs $RunFolder)) {
            Write-Log "REJECTED: the run folder $RunFolder is no longer a folder of ours. Nothing was downloaded."
            return $false
        }
        try {
            Write-Log "Download attempt $attempt/$DownloadAttempts from $Url"
            Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing -TimeoutSec 60 -ErrorAction Stop

            $held = New-Object System.IO.FileStream($Destination, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
            if ((Test-BootstrapSignature -Path $Destination) -and (Test-InstallerIdentity -Path $Destination)) {
                $RunState.Installer = $held
                return $true
            }
            $held.Dispose()
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

# The download always goes. After an installer exit 0 the log and the folder go too;
# otherwise they stay for diagnosis.
function Remove-LoaderRunFiles {
    param([string]$RunFolder, [string]$InstallerPath, [switch]$KeepLog)
    if ($RunState.Installer) {
        $RunState.Installer.Dispose()
        $RunState.Installer = $null
    }
    try {
        if ([System.IO.File]::Exists($InstallerPath)) { [System.IO.File]::Delete($InstallerPath) }
        if ($KeepLog) { return }
        $logFile = $RunState.LogFile
        $RunState.LogFile = $null
        if ($logFile -and [System.IO.File]::Exists($logFile)) { [System.IO.File]::Delete($logFile) }
        [System.IO.Directory]::Delete($RunFolder, $false)
    }
    catch {
        Write-Host "INFO: run folder $RunFolder was not fully removed: $($_.Exception.Message)"
    }
}

function Start-BootstrapLoader {
    if (-not (Test-IsElevated)) {
        $message = "AutopilotMonitor loader: not running as SYSTEM or administrator; nothing was downloaded or changed."
        Write-Host $message
        [Console]::Error.WriteLine($message)
        return
    }

    try {
        [Net.ServicePointManager]::SecurityProtocol = Get-DownloadSecurityProtocol -Current ([Net.ServicePointManager]::SecurityProtocol)
    }
    catch {
        Write-Host "INFO: could not adjust the TLS protocols: $($_.Exception.Message)"
    }

    try {
        $runFolder = New-RunFolder -Parent $env:ProgramData -Prefix $RunFolderPrefix
    }
    catch {
        Write-Host "AutopilotMonitor loader: no run folder ($($_.Exception.Message)). Nothing was downloaded or executed."
        return
    }

    $RunState.LogFile = Join-Path $runFolder $LogFileName
    Write-Log "=== Autopilot Monitor bootstrap loader v$ScriptVersion started (user=$env:USERNAME, pid=$PID) ==="
    Write-Log ("PowerShell {0}, {1}, language mode {2}, run folder {3}" -f $PSVersionTable.PSVersion, $(if ([Environment]::Is64BitProcess) { '64-bit' } else { '32-bit' }), $ExecutionContext.SessionState.LanguageMode, $runFolder)

    $installerPath = Join-Path $runFolder 'Install-AutopilotMonitor.ps1'
    $succeeded = $false
    try {
        if (-not (Get-VerifiedBootstrapScript -Url $BootstrapUrl -Destination $installerPath -RunFolder $runFolder)) {
            Write-Log "No verified installer available after $DownloadAttempts attempt(s). Nothing was executed."
            return
        }

        Write-Log "Executing $installerPath"

        # Same process, so the execution policy that admitted this script covers the installer.
        # The exit code is reset so that an installer that throws cannot report an older one.
        $global:LASTEXITCODE = $null
        try {
            & $installerPath @InstallerArguments
            Write-Log "Installer finished (exit code $LASTEXITCODE)."
            $succeeded = ($LASTEXITCODE -eq 0)
        }
        catch {
            Write-Log "Installer threw: $($_.Exception.Message)"
        }
    }
    finally {
        Remove-LoaderRunFiles -RunFolder $runFolder -InstallerPath $installerPath -KeepLog:(-not $succeeded)
    }
}

# Dot-sourcing (tests) loads the functions without running the loader.
if ($MyInvocation.InvocationName -ne '.') {
    try {
        Start-BootstrapLoader
    }
    catch {
        Write-Log "Loader failed: $($_.Exception.Message)"
    }
    exit 0
}
