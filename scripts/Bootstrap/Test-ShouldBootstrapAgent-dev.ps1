# Autopilot Monitor Bootstrap Guard Test (DEV)
# Dry-run only: does not exit, does not install, only reports what WOULD happen.
# Mirrors Install-AutopilotMonitor-dev.ps1 incl. the OOBE-relax exemption for the
# "Windows Backup for Organizations" restore case (guards 2+3).

param(
    [string]$AgentBinPath = 'C:\ProgramData\AutopilotMonitor\Agent',
    [int]$OobeProfileMaxAgeMinutes = 15
)

$ErrorActionPreference = 'SilentlyContinue'

function Write-Step {
    param(
        [string]$Status,
        [string]$Message
    )

    $prefix = switch ($Status) {
        'PASS' { '[PASS]' }
        'SKIP' { '[SKIP]' }
        'WARN' { '[WARN]' }
        'INFO' { '[INFO]' }
        default { '[....]' }
    }

    Write-Host "$prefix $Message"
}

function Get-RegistryValueSafe {
    param(
        [string]$Path,
        [string]$Name
    )

    try {
        return (Get-ItemProperty -Path $Path -Name $Name -ErrorAction Stop).$Name
    }
    catch {
        return $null
    }
}

# Positive OOBE check via WinRT Windows.System.Profile.SystemSetupInfo.OutOfBoxExperienceState.
# Requires Windows 10, version 1809 (10.0.17763.0). Older builds / errors -> $false (SKIP-safe).
# Docs: https://learn.microsoft.com/en-us/uwp/api/windows.system.profile.systemsetupinfo
function Test-OobeInProgress {
    try {
        $null = [Windows.System.Profile.SystemSetupInfo, Windows.System.Profile, ContentType = WindowsRuntime]
        return ([Windows.System.Profile.SystemSetupInfo]::OutOfBoxExperienceState).ToString() -eq 'InProgress'
    }
    catch {
        return $false
    }
}

$AgentExePath = Join-Path $AgentBinPath 'AutopilotMonitor.Agent.exe'
$MaxBootstrapWindowHours = 12

$wouldInstall = $true
$reasons = New-Object System.Collections.Generic.List[string]

Write-Host ''
Write-Host '=== Autopilot Monitor Bootstrap Guard Test (Dry Run, DEV) ==='
Write-Host ''

# Guard 1: Agent was already deployed on this device (survives self-destruct)
$deployed = Get-RegistryValueSafe -Path 'HKLM:\SOFTWARE\AutopilotMonitor' -Name 'Deployed'
if ($deployed) {
    Write-Step -Status 'SKIP' -Message "Guard 1: Agent was previously deployed at '$deployed'."
    $wouldInstall = $false
    $reasons.Add("Previously deployed marker exists: $deployed")
} else {
    Write-Step -Status 'PASS' -Message 'Guard 1: No previous deployment marker found.'
}

# Collect real (non-special) user profiles as full paths (combined WMI/filesystem view).
$excludePattern = '^(defaultuser\d*|Public|Default( User)?|All Users|WDAGUtilityAccount)$'
$wmiProfileQueryFailed = $false

# NOTE: the outer @() is required. Without it the trailing Where/Select pipeline
# unwraps a single result back to a scalar string, so $profilePaths[0] would index
# into the string and return its first char ('C') instead of the full path.
$profilePaths = @(
    @(
        try {
            Get-CimInstance Win32_UserProfile -ErrorAction Stop |
                Where-Object { -not $_.Special -and $_.LocalPath -like 'C:\Users\*' } |
                ForEach-Object { $_.LocalPath }
        }
        catch {
            $wmiProfileQueryFailed = $true
        }

        (Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue).FullName
    ) |
    Where-Object { $_ -and (Split-Path $_ -Leaf) -notmatch $excludePattern } |
    Select-Object -Unique
)

if ($wmiProfileQueryFailed) {
    Write-Step -Status 'WARN' -Message 'Profiles: WMI query failed, filesystem check still applied.'
}

$profileNames = $profilePaths | ForEach-Object { Split-Path $_ -Leaf }

# OOBE-relax: the ONLY exemption to guards 2+3. Matches exactly the Windows Backup for
# Organizations restore: OOBE InProgress AND exactly one profile AND that profile fresh.
$oobeInProgress = Test-OobeInProgress
$profileAgeMin = $null
$oobeRelax = $false
if ($profilePaths.Count -eq 1) {
    try {
        $created = (Get-Item -LiteralPath $profilePaths[0] -Force -ErrorAction Stop).CreationTimeUtc
        $profileAgeMin = [math]::Round(((Get-Date).ToUniversalTime() - $created).TotalMinutes, 1)
    } catch { }
    if ($oobeInProgress -and $null -ne $profileAgeMin -and $profileAgeMin -ge 0 -and $profileAgeMin -lt $OobeProfileMaxAgeMinutes) {
        $oobeRelax = $true
    }
}

Write-Step -Status 'INFO' -Message "OOBE: OutOfBoxExperienceState InProgress = $oobeInProgress ; profileCount = $($profilePaths.Count) ; singleProfileAgeMin = $profileAgeMin ; OOBE-relax = $oobeRelax"

# Guard 2: No real user profile should exist yet (unless OOBE-relax holds)
if ($profilePaths.Count -gt 0 -and -not $oobeRelax) {
    $names = ($profileNames | Select-Object -First 3) -join ', '
    Write-Step -Status 'SKIP' -Message "Guard 2: Real user profile(s) found ($names). Device appears productive."
    $wouldInstall = $false
    $reasons.Add("Real user profile(s) found: $names")
} elseif ($profilePaths.Count -gt 0 -and $oobeRelax) {
    $names = ($profileNames | Select-Object -First 3) -join ', '
    Write-Step -Status 'PASS' -Message "Guard 2: Profile ($names) present but OOBE-relax active (single fresh profile in OOBE) -- not blocking."
} else {
    Write-Step -Status 'PASS' -Message 'Guard 2: No real user profiles found (combined WMI/filesystem view).'
}

# Guard 3: LastLoggedOnUser (unless OOBE-relax holds)
$lastLoggedOnUser = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI' -Name 'LastLoggedOnUser' -EA SilentlyContinue).LastLoggedOnUser
if ($lastLoggedOnUser -and $lastLoggedOnUser -notmatch 'defaultuser\d*' -and -not $oobeRelax) {
    Write-Step -Status 'SKIP' -Message "Guard 3: LastLoggedOnUser found ($lastLoggedOnUser). Device appears productive."
    $wouldInstall = $false
    $reasons.Add("LastLoggedOnUser found: $lastLoggedOnUser")
} elseif ($lastLoggedOnUser -and $lastLoggedOnUser -notmatch 'defaultuser\d*' -and $oobeRelax) {
    Write-Step -Status 'PASS' -Message "Guard 3: LastLoggedOnUser ($lastLoggedOnUser) present but OOBE-relax active -- not blocking."
} else {
    Write-Step -Status 'PASS' -Message "Guard 3: No real LastLoggedOnUser (value: '$lastLoggedOnUser')."
}

# Guard 4: Bootstrap window check (sleep/standby does NOT reset uptime, only real boot does)
$lastBoot = $null
$uptimeHours = $null

try {
    $lastBoot = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime
    $uptimeHours = ((Get-Date) - $lastBoot).TotalHours

    if ($uptimeHours -gt $MaxBootstrapWindowHours) {
        Write-Step -Status 'SKIP' -Message "Guard 4: Device uptime is $([int]$uptimeHours)h. Older than accepted bootstrap window of ${MaxBootstrapWindowHours}h."
        $wouldInstall = $false
        $reasons.Add("Uptime exceeds bootstrap window: $([int]$uptimeHours)h > ${MaxBootstrapWindowHours}h")
    } else {
        Write-Step -Status 'PASS' -Message "Guard 4: Device uptime is $([int]$uptimeHours)h and within bootstrap window of ${MaxBootstrapWindowHours}h."
    }
}
catch {
    Write-Step -Status 'WARN' -Message 'Guard 4: Could not determine LastBootUpTime / uptime.'
}

# Guard 5: Agent binary already present
if (Test-Path $AgentExePath) {
    Write-Step -Status 'SKIP' -Message "Guard 5: Agent already installed at '$AgentExePath'."
    $wouldInstall = $false
    $reasons.Add("Agent binary already present: $AgentExePath")
} else {
    Write-Step -Status 'PASS' -Message "Guard 5: Agent binary not present at '$AgentExePath'."
}

Write-Host ''
Write-Host '=== Result ==='
Write-Host ''

if ($wouldInstall) {
    Write-Host '[DECISION] WOULD INSTALL agent on this device.'
} else {
    Write-Host '[DECISION] WOULD SKIP agent installation on this device.'
}

Write-Host ''
Write-Host '=== Summary ==='

if ($reasons.Count -eq 0) {
    Write-Host 'No blocking reasons found.'
} else {
    foreach ($reason in $reasons) {
        Write-Host " - $reason"
    }
}

Write-Host ''
Write-Host '=== Raw Values ==='
Write-Host "Deployed marker           : $deployed"
Write-Host "DetectedProfileNames      : $($profileNames -join ', ')"
Write-Host "ProfileCount              : $($profilePaths.Count)"
Write-Host "OobeInProgress (WinRT)    : $oobeInProgress"
Write-Host "SingleProfileAgeMinutes   : $profileAgeMin"
Write-Host "OobeProfileMaxAgeMinutes  : $OobeProfileMaxAgeMinutes"
Write-Host "OobeRelaxActive           : $oobeRelax"
Write-Host "LastLoggedOnUser          : $lastLoggedOnUser"
Write-Host "MaxBootstrapWindowHours   : $MaxBootstrapWindowHours"
Write-Host "LastBootUpTime            : $lastBoot"
Write-Host "UptimeHours               : $([string]$uptimeHours)"
Write-Host "AgentExePath              : $AgentExePath"
Write-Host ''
