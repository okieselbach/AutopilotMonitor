<#
.SYNOPSIS
    Installs the Autopilot Monitor agent during Windows Autopilot enrollment.

.DESCRIPTION
    Runs as SYSTEM, assigned as an Intune platform script or started by
    Start-AutopilotMonitor.ps1. It installs the agent only while the device is in enrollment
    and skips productive devices: earlier deployment, real user profile, last logged-on user,
    or uptime beyond the bootstrap window. One profile created minutes ago is accepted during
    OOBE (Windows Backup for Organizations restore) and on a Windows 365 Cloud PC at first
    connect.

    The agent is installed only when its ZIP matches the SHA-256 in its manifest and every
    file matches the catalog signed by glueckkanja AG. The agent removes itself when
    enrollment completes; the registry value HKLM\SOFTWARE\AutopilotMonitor\Deployed stays so
    that the device is not installed again. A skipped device without an agent keeps nothing.

    Log: %ProgramData%\AutopilotMonitor\Logs\bootstrap-script.log when the agent is installed
    or the install fails; a skip is logged to the script output only. Exit code 0: installed
    or skipped. Exit code 1 with one error line: failed, or not run as SYSTEM or administrator.

.PARAMETER AgentDownloadUrl
    Agent ZIP to install. Default: the published agent.

.PARAMETER VersionJsonName
    Manifest next to the agent ZIP that holds its SHA-256. Default: version.json.

.PARAMETER MaxBootstrapWindowHours
    Devices up longer than this since their last boot are skipped; not applied to a Cloud PC
    at first connect. Default: 12.

.PARAMETER OobeProfileMaxAgeMinutes
    Maximum age of the one new profile accepted during OOBE or on a Cloud PC. Default: 15.

.NOTES
    Requires Windows PowerShell 5.1.
    Changes: https://docs.autopilotmonitor.com/changelog/platform-changelog
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$AgentDownloadUrl = "https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip",

    [Parameter(Mandatory = $false)]
    [string]$VersionJsonName = "version.json",

    [Parameter(Mandatory = $false)]
    [int]$MaxBootstrapWindowHours = 12,

    [Parameter(Mandatory = $false)]
    [int]$OobeProfileMaxAgeMinutes = 15
)

# The loader checks these two lines before it runs this script.
$ScriptVersion = "2.7"
$ScriptRole = "AutopilotMonitor-Installer"

# Signer of the agent's file catalog; a subject pattern, so certificate renewals keep working.
$ExpectedPublisher = "*O=glueckkanja AG*"
$AgentCatalogName = "AutopilotMonitor.Agent.cat"

$AgentBasePath = "$env:ProgramData\AutopilotMonitor"
$AgentBinPath = "$AgentBasePath\Agent"
$AgentLogPath = "$AgentBasePath\Logs"
$LogFile = "$AgentLogPath\bootstrap-script.log"

# Where loaders before v1.3 saved this script, and the name prefix of loader v1.3 run folders.
$LegacyInstallerPath = "$AgentBasePath\Install-AutopilotMonitor.ps1"
$LoaderRunFolderPrefix = "AutopilotMonitor-Loader-"

$AgentExeName = "AutopilotMonitor.Agent.exe"
$AgentTaskName = "AutopilotMonitor-Agent"
$DeploymentMarkerKey = "SOFTWARE\AutopilotMonitor"
$DeploymentMarkerValue = "Deployed"

# Lines wait in memory until an install has prepared the Logs folder.
$LogState = [pscustomobject]@{
    Ready   = $false
    Pending = (New-Object System.Collections.Generic.List[string])
}

# Write-Host, so log lines never end up in a function's return value.
function Write-Log {
    param([string]$Message)
    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Message
    Write-Host $line
    if ($LogState.Ready) {
        Add-Content -Path $LogFile -Value $line -ErrorAction SilentlyContinue
    } else {
        $LogState.Pending.Add($line)
    }
}

function Open-LogFile {
    if ($LogState.Pending.Count -gt 0) {
        [System.IO.File]::AppendAllText($LogFile, (($LogState.Pending -join "`r`n") + "`r`n"))
        $LogState.Pending.Clear()
    }
    $LogState.Ready = $true
}

# 64-bit registry view: a 32-bit host, the Intune default, would read WOW6432Node.
function Get-RegistryValue64 {
    param([string]$SubKey, [string]$Name)
    $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $hive.OpenSubKey($SubKey)
        if ($null -eq $key) { return $null }
        try { return $key.GetValue($Name) } finally { $key.Close() }
    } finally {
        $hive.Close()
    }
}

function Test-RegistryKey64 {
    param([string]$SubKey)
    $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $hive.OpenSubKey($SubKey)
        if ($null -eq $key) { return $false }
        $key.Close()
        return $true
    } finally {
        $hive.Close()
    }
}

# OOBE state from Windows 10 1809 or later; 'Unavailable' otherwise, which relaxes no guard.
function Get-OobeState {
    try {
        $null = [Windows.System.Profile.SystemSetupInfo, Windows.System.Profile, ContentType = WindowsRuntime]
        return ([Windows.System.Profile.SystemSetupInfo]::OutOfBoxExperienceState).ToString()
    }
    catch {
        return 'Unavailable'
    }
}

# Windows 365 Cloud PC: both markers are required, since each alone has look-alikes.
function Test-IsCloudPc {
    try {
        $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        Write-Log "Computer identity: Manufacturer='$($cs.Manufacturer)' Model='$($cs.Model)'."
    }
    catch { Write-Log "INFO: computer identity query failed: $($_.Exception.Message)" }

    try {
        $regMarker = Test-RegistryKey64 -SubKey 'SOFTWARE\Microsoft\Windows365'
        $svcMarker = $null -ne (Get-Service -Name 'CloudManagedDesktopExtension' -ErrorAction SilentlyContinue)
        Write-Log "Cloud PC markers: Windows365 registry key=$regMarker; CloudManagedDesktopExtension service=$svcMarker."
        return ($regMarker -and $svcMarker)
    }
    catch {
        Write-Log "INFO: Cloud PC marker check failed: $($_.Exception.Message)"
        return $false
    }
}

# Real user profiles from WMI and C:\Users. Callers wrap the result in @(): a single path
# would otherwise come back as a string.
function Get-RealUserProfilePaths {
    $excludePattern = '^(defaultuser\d*|Public|Default( User)?|All Users|WDAGUtilityAccount)$'
    @(
        try {
            Get-CimInstance Win32_UserProfile -ErrorAction Stop |
                Where-Object { -not $_.Special -and $_.LocalPath -like 'C:\Users\*' } |
                ForEach-Object { $_.LocalPath }
        } catch { Write-Log "INFO: WMI profile query failed, continuing with filesystem check." }

        (Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue).FullName
    ) | Where-Object { $_ -and (Split-Path $_ -Leaf) -notmatch $excludePattern } |
        Select-Object -Unique
}

# The only exemption from guards 2 and 3: OOBE in progress or a Cloud PC, plus exactly one
# profile created less than OobeProfileMaxAgeMinutes ago.
function Get-RelaxDecision {
    param(
        [string[]]$ProfilePaths,
        [int]$OobeProfileMaxAgeMinutes
    )
    if ($null -eq $ProfilePaths) { $ProfilePaths = @() }

    $relaxActive = $false
    $oobeState = Get-OobeState
    $oobeInProgress = ($oobeState -eq 'InProgress')
    $isCloudPc = Test-IsCloudPc
    if ($isCloudPc) {
        Write-Log "Cloud PC detected (OOBE state: $oobeState)."
    }

    $relaxTrigger = $null
    if ($oobeInProgress) {
        $relaxTrigger = "OOBE InProgress (Windows Backup restore case)"
    } elseif ($isCloudPc) {
        $relaxTrigger = "Cloud PC (headless provisioning, OOBE state: $oobeState)"
    }

    if ($ProfilePaths.Count -eq 1 -and $relaxTrigger) {
        try {
            $created = (Get-Item -LiteralPath $ProfilePaths[0] -Force -ErrorAction Stop).CreationTimeUtc
            $ageMin = ((Get-Date).ToUniversalTime() - $created).TotalMinutes
            if ($ageMin -ge 0 -and $ageMin -lt $OobeProfileMaxAgeMinutes) {
                $relaxActive = $true
                Write-Log ("Relax active: {0} + single profile created {1:N1}min ago (< {2}min). Guards 2+3 relaxed." -f $relaxTrigger, $ageMin, $OobeProfileMaxAgeMinutes)
            } else {
                Write-Log ("Relax NOT applied: {0} + single profile '{1}', but profile created {2:N1}min ago (created {3:u}) is outside the [0, {4})min window." -f $relaxTrigger, (Split-Path $ProfilePaths[0] -Leaf), $ageMin, $created, $OobeProfileMaxAgeMinutes)
            }
        } catch { Write-Log "INFO: profile age check failed; relax not applied. $($_.Exception.Message)" }
    } elseif ($ProfilePaths.Count -eq 1) {
        Write-Log "Relax NOT applied: single profile present but neither OOBE InProgress nor Cloud PC (OOBE state: $oobeState)."
    } elseif ($ProfilePaths.Count -gt 1) {
        Write-Log ("Relax NOT applied: {0} real profiles present (relax requires exactly one). OOBE state: {1}; CloudPC: {2}." -f $ProfilePaths.Count, $oobeState, $isCloudPc)
    }

    return [pscustomobject]@{
        Active    = $relaxActive
        OobeState = $oobeState
        IsCloudPc = $isCloudPc
    }
}

# Guards 1-4; reads only. Returns Install, ReasonCode and RelaxActive.
function Get-BootstrapDecision {
    param(
        [int]$MaxBootstrapWindowHours,
        [int]$OobeProfileMaxAgeMinutes
    )

    # Guard 1: deployed before; the marker outlives the agent.
    $deployed = $null
    try { $deployed = Get-RegistryValue64 -SubKey $DeploymentMarkerKey -Name $DeploymentMarkerValue } catch { }
    if ($deployed) {
        Write-Log "SKIP: Agent was previously deployed at $deployed."
        return [pscustomobject]@{ Install = $false; ReasonCode = 'AlreadyDeployed'; RelaxActive = $false }
    }

    $profilePaths = @(Get-RealUserProfilePaths)
    $relax = Get-RelaxDecision -ProfilePaths $profilePaths -OobeProfileMaxAgeMinutes $OobeProfileMaxAgeMinutes

    # Guard 2: real user profiles.
    if ($profilePaths.Count -gt 0 -and -not $relax.Active) {
        $details = (($profilePaths | ForEach-Object {
            $leaf = Split-Path $_ -Leaf
            try {
                $c = (Get-Item -LiteralPath $_ -Force -ErrorAction Stop).CreationTimeUtc
                "{0} (created {1:u}, {2:N1}min ago)" -f $leaf, $c, ((Get-Date).ToUniversalTime() - $c).TotalMinutes
            } catch { "$leaf (age unknown)" }
        }) | Select-Object -First 3) -join '; '
        Write-Log "SKIP: Real user profile(s) found ($details). OOBE state: $($relax.OobeState). CloudPC: $($relax.IsCloudPc). Device appears productive."
        return [pscustomobject]@{ Install = $false; ReasonCode = 'ProfilesFound'; RelaxActive = $false }
    }

    # Guard 3: a real user has logged on.
    $lastLoggedOnUser = $null
    try { $lastLoggedOnUser = Get-RegistryValue64 -SubKey 'SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI' -Name 'LastLoggedOnUser' } catch { }
    if ($lastLoggedOnUser -and $lastLoggedOnUser -notmatch 'defaultuser\d*' -and -not $relax.Active) {
        Write-Log "SKIP: LastLoggedOnUser found ($lastLoggedOnUser). Device appears productive."
        return [pscustomobject]@{ Install = $false; ReasonCode = 'LastLoggedOnUser'; RelaxActive = $false }
    }

    # Guard 4: uptime since the last boot. A Cloud PC can wait days for its first connect, so
    # there the fresh profile is the time anchor.
    $lastBoot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    $uptimeHours = ((Get-Date) - $lastBoot).TotalHours
    Write-Log "Device uptime: $([int]$uptimeHours)h (last boot: $lastBoot)"
    if ($uptimeHours -gt $MaxBootstrapWindowHours) {
        if ($relax.Active -and $relax.IsCloudPc) {
            Write-Log "Uptime $([int]$uptimeHours)h exceeds the ${MaxBootstrapWindowHours}h window, but the Cloud PC relax is active (fresh first-connect profile is the time anchor). Continuing."
        } else {
            Write-Log "SKIP: Device uptime is $([int]$uptimeHours)h. OOBE state is older than accepted bootstrap window of ${MaxBootstrapWindowHours}h."
            return [pscustomobject]@{ Install = $false; ReasonCode = 'UptimeExceeded'; RelaxActive = $relax.Active }
        }
    }

    return [pscustomobject]@{ Install = $true; ReasonCode = $null; RelaxActive = $relax.Active }
}

# Why the agent folder belongs to an agent (marker, executable or task), or $null. What
# cannot be read counts as an agent, whose folder is never changed.
function Get-AgentPresence {
    try {
        if ($null -ne (Get-RegistryValue64 -SubKey $DeploymentMarkerKey -Name $DeploymentMarkerValue)) { return 'deployment marker' }
    } catch { return "deployment marker unreadable ($($_.Exception.Message))" }

    try {
        $null = Get-Item -LiteralPath (Join-Path $AgentBinPath $AgentExeName) -Force -ErrorAction Stop
        return 'agent executable'
    } catch {
        if ($_.CategoryInfo.Category -ne [System.Management.Automation.ErrorCategory]::ObjectNotFound) {
            return "agent executable unreadable ($($_.Exception.Message))"
        }
    }

    # The category, not the localized message, tells a missing task from a failed query.
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        try {
            $null = Get-ScheduledTask -TaskName $AgentTaskName -ErrorAction Stop
            return 'agent task'
        } catch {
            if ($_.CategoryInfo.Category -eq [System.Management.Automation.ErrorCategory]::ObjectNotFound) { return $null }
            if ($attempt -eq 2) { return "agent task unreadable ($($_.Exception.Message))" }
            Start-Sleep -Seconds 2
        }
    }
}

# The loader repeats the first six functions of this block word for word.
### BEGIN shared install helpers (the OOBE script embeds this block; keep it self-contained)

function Test-IsElevated {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

# SYSTEM and Administrators full control, Users read, nothing inherited. Owner Administrators,
# so a folder that any elevated account created counts as ours.
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

# Owner SYSTEM or Administrators. Any other owner, or one that cannot be read, is another
# account's.
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

# Random, so nobody can create a run folder's name ahead of the run.
function New-RunFolderName {
    $bytes = New-Object byte[] 4
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

# CreateDirectory leaves an existing folder as it is, so only a new name is used and the new
# folder is checked.
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

# 'Missing', 'Ours', or why the folder is not ours: 'Link', 'File' or 'Foreign'.
function Get-FolderState {
    param([string]$Path)
    try { $attributes = [System.IO.File]::GetAttributes($Path) }
    catch [System.IO.FileNotFoundException] { return 'Missing' }
    catch [System.IO.DirectoryNotFoundException] { return 'Missing' }
    if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return 'Link' }
    if (($attributes -band [System.IO.FileAttributes]::Directory) -eq 0) { return 'File' }
    if (-not (Test-IsOwnedByUs $Path)) { return 'Foreign' }
    return 'Ours'
}

function Remove-OwnFile {
    param([string]$Path)
    # A virus scanner may hold the file for a moment.
    for ($attempt = 1; ; $attempt++) {
        try {
            [System.IO.File]::Delete($Path)
            return
        } catch {
            if ($attempt -ge 3) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

function Remove-OwnTreeEntries {
    param([string]$Path, $Left)
    try { $entries = [System.IO.Directory]::GetFileSystemEntries($Path) } catch { $Left.Locked.Add($Path); return }
    foreach ($entry in $entries) {
        try {
            $attributes = [System.IO.File]::GetAttributes($entry)
            $isDirectory = ($attributes -band [System.IO.FileAttributes]::Directory) -ne 0
            if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                if ($isDirectory) { [System.IO.Directory]::Delete($entry, $false) } else { [System.IO.File]::Delete($entry) }
                continue
            }
            if (-not (Test-IsOwnedByUs $entry)) {
                $Left.Foreign.Add($entry)
                continue
            }
            if ($isDirectory) {
                Remove-OwnTreeEntries -Path $entry -Left $Left
                [System.IO.Directory]::Delete($entry, $false)
            } else {
                if (($attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
                    [System.IO.File]::SetAttributes($entry, ($attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)))
                }
                Remove-OwnFile -Path $entry
            }
        } catch {
            $Left.Locked.Add($entry)
        }
    }
}

# Deletes our own entries in a folder we own. Links are removed, never followed; other
# accounts' entries stay. Returns what stayed; never throws.
function Remove-OwnTree {
    param([string]$Path, [switch]$KeepRoot)
    $left = [pscustomobject]@{
        Foreign = New-Object System.Collections.Generic.List[string]
        Locked  = New-Object System.Collections.Generic.List[string]
    }
    Remove-OwnTreeEntries -Path $Path -Left $left
    if (-not $KeepRoot) {
        try { [System.IO.Directory]::Delete($Path, $false) } catch { $left.Locked.Add($Path) }
    }
    return $left
}

# Creates the agent folder protected, or protects and empties one of ours. Throws for a link,
# a file, or a folder that is, or holds entries that are, another account's.
function Initialize-AgentBase {
    param([string]$Path)
    $state = Get-FolderState $Path
    if ($state -eq 'Missing') {
        [System.IO.Directory]::CreateDirectory($Path, (New-ProtectedDirectorySecurity)) | Out-Null
        $state = Get-FolderState $Path
        if ($state -ne 'Ours') { throw "$Path could not be created as a protected folder ($state)." }
        return
    }
    if ($state -ne 'Ours') {
        throw "$Path is not a folder that SYSTEM or administrators created ($state); not installing into it."
    }
    (Get-Item -LiteralPath $Path -Force -ErrorAction Stop).SetAccessControl((New-ProtectedDirectorySecurity))
    $left = Remove-OwnTree -Path $Path -KeepRoot
    if ($left.Foreign.Count -gt 0) {
        throw ("$Path holds entries another account created ({0}); not installing into it." -f (($left.Foreign | Select-Object -First 3) -join ', '))
    }
}

# Why the extracted payload must not run; empty when it matches its signed catalog. The
# catalog Status covers the hashes only, so the signer is checked on its own. The comma keeps
# a single rejection an array.
function Get-AgentPayloadRejections {
    param(
        [string]$Path,
        [string]$ExpectedPublisher,
        [string]$CatalogName = 'AutopilotMonitor.Agent.cat'
    )

    $catalog = Join-Path $Path $CatalogName
    if (-not (Test-Path -LiteralPath $catalog -PathType Leaf)) {
        return , @("catalog $CatalogName missing")
    }

    try {
        $result = Test-FileCatalog -Path $Path -CatalogFilePath $catalog -FilesToSkip $CatalogName -Detailed -ErrorAction Stop
    }
    catch {
        return , @("catalog check failed to run ($($_.Exception.Message))")
    }
    if ($null -eq $result) {
        return , @('catalog check returned nothing')
    }

    $rejected = @()
    $signature = $result.Signature
    if ($null -eq $signature -or "$($signature.Status)" -ne 'Valid') {
        $status = if ($null -eq $signature) { 'missing' } else { "$($signature.Status)" }
        $rejected += "catalog signature status $status"
    }
    elseif ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notlike $ExpectedPublisher) {
        $subject = if ($null -eq $signature.SignerCertificate) { '' } else { $signature.SignerCertificate.Subject }
        $rejected += "catalog publisher '$subject' does not match $ExpectedPublisher"
    }

    if ("$($result.Status)" -ne 'Valid') {
        $expected = if ($null -eq $result.CatalogItems) { @{} } else { $result.CatalogItems }
        $actual = if ($null -eq $result.PathItems) { @{} } else { $result.PathItems }
        $names = {
            param($list)
            if ($list.Count -eq 0) { return 'none' }
            $shown = ($list | Select-Object -First 5) -join ', '
            if ($list.Count -gt 5) { $shown += " (+$($list.Count - 5) more)" }
            return $shown
        }
        $missing = @($expected.Keys | Where-Object { -not $actual.ContainsKey($_) } | Sort-Object)
        $extra = @($actual.Keys | Where-Object { -not $expected.ContainsKey($_) } | Sort-Object)
        $changed = @($expected.Keys | Where-Object { $actual.ContainsKey($_) -and $actual[$_] -ne $expected[$_] } | Sort-Object)
        $rejected += "payload differs from catalog (missing: $(& $names $missing); extra: $(& $names $extra); changed: $(& $names $changed))"
    }

    return , $rejected
}

# Verifies the payload in the run folder and moves it in as the agent folder. The move fails
# when that folder exists, so nothing is ever merged into it.
function Install-VerifiedAgentPayload {
    param(
        [string]$ZipPath,
        [string]$StagingPath,
        [string]$TargetPath,
        [string]$ExpectedPublisher,
        [string]$CatalogName = 'AutopilotMonitor.Agent.cat'
    )

    $payload = Join-Path $StagingPath 'Agent'
    Expand-Archive -Path $ZipPath -DestinationPath $payload -Force -ErrorAction Stop

    $rejections = Get-AgentPayloadRejections -Path $payload -ExpectedPublisher $ExpectedPublisher -CatalogName $CatalogName
    if ($rejections.Count -gt 0) {
        throw "Payload verification FAILED ($($rejections -join '; ')). Refusing to install an agent that is not the published one."
    }

    for ($attempt = 1; ; $attempt++) {
        try {
            [System.IO.Directory]::Move($payload, $TargetPath)
            return
        } catch {
            if ($attempt -ge 3 -or [System.IO.Directory]::Exists($TargetPath)) { throw }
            Start-Sleep -Seconds 1
        }
    }
}

# The agent writes its output to the host; only the exit code comes back.
function Invoke-AgentInstallMode {
    param([string]$ExePath, [string[]]$Arguments = @('--install'))
    & $ExePath @Arguments | Out-Host
    return $LASTEXITCODE
}
### END shared install helpers

# Skip without an agent: removes the agent folder. Other accounts' entries and files in use
# stay, and the folder with them. Returns a line for the log; never throws.
function Remove-AgentBaseOnSkip {
    param([string]$Path)
    try {
        $state = Get-FolderState $Path
        if ($state -eq 'Missing') { return "$Path does not exist; nothing to remove." }
        if ($state -ne 'Ours') { return "$Path left as it is ($state)." }
        (Get-Item -LiteralPath $Path -Force -ErrorAction Stop).SetAccessControl((New-ProtectedDirectorySecurity))
        $left = Remove-OwnTree -Path $Path
        if ($left.Foreign.Count -eq 0 -and $left.Locked.Count -eq 0) { return "$Path removed." }
        return ("$Path kept: {0} entries of another account, {1} in use ({2})." -f $left.Foreign.Count, $left.Locked.Count, ((@($left.Foreign) + @($left.Locked) | Select-Object -First 3) -join ', '))
    } catch {
        return "$Path left as it is ($($_.Exception.Message))."
    }
}

function Test-IsLoaderRunFolder {
    param([string]$Folder)
    if (-not $Folder) { return $false }
    $parent = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($Folder))
    return ([string]::Equals($parent, [System.IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase) -and
            [System.IO.Path]::GetFileName($Folder).StartsWith($LoaderRunFolderPrefix, [System.StringComparison]::OrdinalIgnoreCase))
}

function Test-IsLegacyInstallerPath {
    param([string]$ScriptPath)
    if (-not $ScriptPath) { return $false }
    return [string]::Equals([System.IO.Path]::GetFullPath($ScriptPath), [System.IO.Path]::GetFullPath($LegacyInstallerPath), [System.StringComparison]::OrdinalIgnoreCase)
}

# The loader's log, carried into Logs for the agent and the diagnostics package: whole from a
# loader v1.3 run folder; from older loaders, whose log folder users could write to, only the
# start line. Callers wrap the result in @(); never throws.
function Read-LoaderLogs {
    param([string]$ScriptPath, [string]$ScriptRoot)
    $logs = New-Object System.Collections.Generic.List[object]
    try {
        if (Test-IsLoaderRunFolder $ScriptRoot) {
            foreach ($file in [System.IO.Directory]::GetFiles($ScriptRoot, '*.log')) {
                $logs.Add([pscustomobject]@{ Name = [System.IO.Path]::GetFileName($file); Text = [System.IO.File]::ReadAllText($file) })
            }
        } elseif (Test-IsLegacyInstallerPath $ScriptPath) {
            foreach ($name in @('bootstrap-loader.log', 'bootstrap-msi.log')) {
                $path = Join-Path $AgentLogPath $name
                if (-not [System.IO.File]::Exists($path)) { continue }
                $stream = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]'ReadWrite, Delete')
                try {
                    $start = [Math]::Max([long]0, $stream.Length - 65536)
                    $null = $stream.Seek($start, [System.IO.SeekOrigin]::Begin)
                    $buffer = New-Object byte[] ($stream.Length - $start)
                    $read = $stream.Read($buffer, 0, $buffer.Length)
                    $tail = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $read)
                } finally {
                    $stream.Dispose()
                }
                $starts = [regex]::Matches($tail, '(?m)^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\] === Autopilot Monitor bootstrap loader v\d+(\.\d+){0,3} started')
                if ($starts.Count -gt 0) {
                    $logs.Add([pscustomobject]@{ Name = $name; Text = $starts[$starts.Count - 1].Value + " (carried over by the bootstrapper) ===`r`n" })
                }
            }
        }
    } catch { }
    return $logs.ToArray()
}

function Write-LoaderLogs {
    param($Logs)
    foreach ($log in $Logs) {
        try { [System.IO.File]::AppendAllText((Join-Path $AgentLogPath $log.Name), $log.Text) } catch { }
    }
}

# Run folders that failed loader runs kept for diagnosis. Never throws.
function Remove-StaleLoaderRunFolders {
    param([string]$ScriptRoot)
    try { $folders = [System.IO.Directory]::GetDirectories($env:ProgramData, $LoaderRunFolderPrefix + '*') } catch { return }
    foreach ($folder in $folders) {
        if ($ScriptRoot -and [string]::Equals([System.IO.Path]::GetFullPath($folder), [System.IO.Path]::GetFullPath($ScriptRoot), [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        try {
            if ((Get-FolderState $folder) -eq 'Ours') { $null = Remove-OwnTree -Path $folder }
        } catch { }
    }
}

# The copy that loaders before v1.3 leave at a fixed path. Never throws: it runs in a finally.
function Remove-LegacyInstallerCopy {
    param([string]$ScriptPath)
    try {
        if ((Test-IsLegacyInstallerPath $ScriptPath) -and [System.IO.File]::Exists($ScriptPath)) {
            [System.IO.File]::Delete($ScriptPath)
        }
    } catch { }
}

function Invoke-Bootstrap {
    # One error preference, whatever the caller set; anything unexpected ends in the catch.
    $ErrorActionPreference = 'Stop'
    try {
        if (-not (Test-IsElevated)) {
            $message = "AutopilotMonitor bootstrap: not running as SYSTEM or administrator; nothing was changed."
            Write-Host $message
            [Console]::Error.WriteLine($message)
            exit 1
        }

        # The bootstrap MSI starts PowerShell inside the agent folder, and a working directory
        # cannot be deleted.
        [System.IO.Directory]::SetCurrentDirectory("$env:SystemRoot\System32")

        Write-Log "===== Autopilot Monitor Bootstrap Started ====="
        Write-Log "Bootstrap script version: v$ScriptVersion"

        $decision = Get-BootstrapDecision -MaxBootstrapWindowHours $MaxBootstrapWindowHours -OobeProfileMaxAgeMinutes $OobeProfileMaxAgeMinutes
        $agent = Get-AgentPresence
        if ($agent) {
            Write-Log "Agent present ($agent): $AgentBasePath is left as it is."
            Remove-StaleLoaderRunFolders -ScriptRoot $PSScriptRoot
            exit 0
        }
        if (-not $decision.Install) {
            Write-Log "No agent on this device: $(Remove-AgentBaseOnSkip -Path $AgentBasePath)"
            Remove-StaleLoaderRunFolders -ScriptRoot $PSScriptRoot
            exit 0
        }

        Write-Log "Pre-flight checks passed -- no prior deployment, no productive-device signals, within bootstrap window (relaxActive=$($decision.RelaxActive))"

        $loaderLogs = @(Read-LoaderLogs -ScriptPath $PSCommandPath -ScriptRoot $PSScriptRoot)
        Initialize-AgentBase -Path $AgentBasePath
        [System.IO.Directory]::CreateDirectory($AgentLogPath) | Out-Null
        Write-LoaderLogs $loaderLogs
        Open-LogFile
        Write-Log "Agent folder prepared: $AgentBasePath (protected, emptied)."
        Remove-StaleLoaderRunFolders -ScriptRoot $PSScriptRoot

        $agentExePath = Join-Path $AgentBinPath $AgentExeName
        $staging = New-RunFolder -Parent $AgentBasePath -Prefix 'Agent-Install-'
        Write-Log "Downloading agent from $AgentDownloadUrl into $staging..."
        try {
            # The manifest sits next to the agent ZIP.
            $versionJsonUrl = $AgentDownloadUrl -replace '[^/]+$', $VersionJsonName
            $expectedSha256 = $null
            $manifestAttempts = 3
            $manifestDelays = @(1, 2)   # short backoff between attempts; do not block enrollment

            for ($attempt = 1; $attempt -le $manifestAttempts; $attempt++) {
                try {
                    Write-Log "Fetching $VersionJsonName (attempt ${attempt}/${manifestAttempts}) from $versionJsonUrl for integrity verification..."
                    $versionJsonResponse = Invoke-RestMethod -Uri $versionJsonUrl -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
                    if ($versionJsonResponse.sha256) {
                        $expectedSha256 = $versionJsonResponse.sha256.ToLowerInvariant()
                        Write-Log "SHA-256 hash from manifest: $expectedSha256 (version: $($versionJsonResponse.version))"
                    } else {
                        Write-Log "WARNING: Manifest has no sha256 field - integrity cannot be verified."
                    }
                    break   # manifest reached (with or without hash); retrying would not help
                }
                catch {
                    Write-Log "WARNING: Could not fetch integrity manifest (attempt $attempt): $($_.Exception.Message)"
                    if ($attempt -lt $manifestAttempts) {
                        Start-Sleep -Seconds $manifestDelays[$attempt - 1]
                    }
                }
            }

            $zipPath = Join-Path $staging "AutopilotMonitor-Agent.zip"
            $maxDownloadAttempts = 3
            $downloadAttempt = 0

            do {
                $downloadAttempt++
                try {
                    Write-Log "Download attempt ${downloadAttempt}/${maxDownloadAttempts}"
                    $downloadStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                    Invoke-WebRequest `
                        -Uri $AgentDownloadUrl `
                        -OutFile $zipPath `
                        -UseBasicParsing `
                        -TimeoutSec 30 `
                        -ErrorAction Stop
                    $downloadStopwatch.Stop()
                    $downloadSeconds = [math]::Round($downloadStopwatch.Elapsed.TotalSeconds, 1)
                    Write-Log "Downloaded agent to $zipPath (took ${downloadSeconds}s)"
                    break
                }
                catch {
                    if ($downloadAttempt -ge $maxDownloadAttempts) {
                        throw
                    }

                    $retryDelaysInSeconds = @(2, 4, 8)
                    $retryDelaySeconds = $retryDelaysInSeconds[$downloadAttempt - 1]
                    Write-Log "Download failed (attempt $downloadAttempt): $($_.Exception.Message). Retrying in ${retryDelaySeconds}s..."
                    Start-Sleep -Seconds $retryDelaySeconds
                }
            } while ($downloadAttempt -lt $maxDownloadAttempts)

            # No hash, no install.
            if (-not $expectedSha256) {
                throw "No SHA-256 hash available from manifest - refusing to install unverified agent ZIP."
            }
            $actualSha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
            Write-Log "Validating SHA-256 hash against manifest"
            if ($actualSha256 -ne $expectedSha256) {
                throw "SHA-256 integrity check FAILED. Expected='$expectedSha256', Actual='$actualSha256'. Download may be tampered or corrupted."
            }
            Write-Log "SHA-256 integrity check passed"

            # The manifest comes from the same host as the ZIP; the signed catalog proves that
            # every file comes from the publisher.
            Write-Log "Verifying the payload against its signed catalog ($AgentCatalogName, publisher pin: $ExpectedPublisher)..."
            Install-VerifiedAgentPayload -ZipPath $zipPath -StagingPath $staging -TargetPath $AgentBinPath -ExpectedPublisher $ExpectedPublisher -CatalogName $AgentCatalogName
            Write-Log "Payload verified against its signed catalog and installed to $AgentBinPath"

            if (-not (Test-Path $agentExePath)) {
                throw "Agent executable not found after extraction at $agentExePath"
            }

            Write-Log "Agent installation completed successfully"
        }
        catch {
            Write-Log "ERROR downloading/verifying agent: $($_.Exception.Message)"
            throw
        }
        finally {
            $left = Remove-OwnTree -Path $staging
            if ($left.Foreign.Count -gt 0 -or $left.Locked.Count -gt 0) {
                Write-Log "Run folder $staging not fully removed ($($left.Foreign.Count + $left.Locked.Count) entries stay)."
            }
        }

        Write-Log "Calling agent install mode (--install)..."
        $installExitCode = Invoke-AgentInstallMode -ExePath $agentExePath
        if ($installExitCode -ne 0) {
            throw "Agent install failed with exit code $installExitCode"
        }
        Write-Log "Agent install mode completed successfully"

        $runtimeProcessName = 'AutopilotMonitor.Agent'
        $verifyTimeoutSec = 10
        $verifyDeadline = (Get-Date).AddSeconds($verifyTimeoutSec)
        $runtimeProc = $null
        while ((Get-Date) -lt $verifyDeadline) {
            $runtimeProc = Get-Process -Name $runtimeProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -ne $runtimeProc) { break }
            Start-Sleep -Milliseconds 500
        }
        if ($null -ne $runtimeProc) {
            $startedUtc = 'unavailable'
            try { $startedUtc = $runtimeProc.StartTime.ToUniversalTime().ToString('o') } catch { }
            Write-Log ("Runtime process verified: name={0}.exe pid={1} startedUtc={2}" -f $runtimeProcessName, $runtimeProc.Id, $startedUtc)
        } else {
            Write-Log ("WARNING: Runtime process verification FAILED. Agent --install reported success but no '{0}.exe' process appeared within {1}s. Likely silent block (AV/EDR, AppLocker/WDAC) of the runtime launch. Agent should still come up at next boot via the BootTrigger task. Check Event Viewer > Microsoft > Windows > TaskScheduler/Operational and AV/EDR logs for '{0}.exe'." -f $runtimeProcessName, $verifyTimeoutSec)
        }

        Write-Log "===== Bootstrap Completed Successfully ====="

        exit 0
    }
    catch {
        Write-Log "===== Bootstrap FAILED ====="
        Write-Log "ERROR: $($_.Exception.Message)"
        Write-Log "Stack trace: $($_.ScriptStackTrace)"
        if ($LogState.Ready) { Write-Log "Please check log file: $LogFile" }

        $errMsg = "AutopilotMonitor bootstrap failed: $($_.Exception.Message)"
        if ($errMsg.Length -gt 2048) { $errMsg = $errMsg.Substring(0, 2045) + '...' }
        [Console]::Error.WriteLine($errMsg)

        exit 1
    }
}

# Dot-sourcing (tests, release pipeline) loads the functions without running the bootstrap.
if ($MyInvocation.InvocationName -ne '.') {
    try {
        Invoke-Bootstrap
    }
    finally {
        Remove-LegacyInstallerCopy -ScriptPath $PSCommandPath
    }
}
