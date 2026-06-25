<#
.SYNOPSIS
    Bootstrap script to deploy and start the Autopilot Monitor agent. (DEV)

.DESCRIPTION
    Designed to be deployed via Intune as a PowerShell Script during Autopilot.
    Runs very early in the enrollment process (first Intune action) and:
      1. Runs pre-flight guards to skip productive devices and ghost re-installs.
         An OOBE-relax exemption allows install when a single freshly-created profile
         appears DURING OOBE (the "Windows Backup for Organizations" restore case).
      2. Downloads the monitoring agent ZIP from Azure Blob Storage
      3. Verifies integrity via SHA-256 hash from the version manifest (mandatory)
      4. Extracts the agent into %ProgramData%\AutopilotMonitor\Agent
      5. Runs the agent in --install mode (registers Scheduled Task, spawns the runtime)
      6. Verifies the runtime process actually launched
    Agent self-destructs when enrollment completes.

    OOBE-relax flow (guards 2+3): a real user profile (or a real LastLoggedOnUser)
    normally means "productive -> skip". Windows Backup for Organizations breaks that
    by restoring settings and creating ONE profile while still in OOBE. We relax guards
    2+3 ONLY when all three positives hold, and nothing else:
      (a) the OS positively reports OOBE InProgress (Test-OobeInProgress), AND
      (b) there is exactly ONE real user profile, AND
      (c) that profile was created within $OobeProfileMaxAgeMinutes minutes.
    Any miss keeps the original skip (SKIP-safe: we never install on uncertainty).

.PARAMETER AgentDownloadUrl
    URL to download the agent ZIP from. Defaults to the production blob; override only
    for parallel lab/dev assignments that point at a separate pre-release ZIP.

.PARAMETER VersionJsonName
    Filename of the integrity manifest in the same blob container as the agent ZIP.
    Defaults to "version.json"; override only for parallel lab/dev manifests.

.PARAMETER MaxBootstrapWindowHours
    Maximum device uptime (hours) within which the bootstrap is still considered
    valid. Devices booted more than this many hours ago are skipped because we no
    longer trust their OOBE state. Default: 12.

.PARAMETER OobeProfileMaxAgeMinutes
    Maximum age (minutes) of the single profile that may appear during OOBE for the
    OOBE-relax exemption to apply. The restore->bootstrap gap is always minutes; an
    older profile is treated as a productive device. Default: 15.

.NOTES
    - Agent is temporary and auto-removes after enrollment.
    - All files live under C:\ProgramData\AutopilotMonitor (easy cleanup).
    - One registry key remains after removal: HKLM\SOFTWARE\AutopilotMonitor\Deployed
      (prevents ghost re-installs on re-Autopilot of the same device).
    - Scheduled Task survives reboots during enrollment.
    - OOBE detection uses Windows.System.Profile.SystemSetupInfo.OutOfBoxExperienceState.
      Requires Windows 10, version 1809 (introduced in 10.0.17763.0). Older builds lack
      the class -> Test-OobeInProgress returns $false -> original (skip) behaviour. Docs:
      https://learn.microsoft.com/en-us/uwp/api/windows.system.profile.systemsetupinfo
    - This script MUST remain pure ASCII (no Unicode/UTF-8 special chars).
      PowerShell 5.1 (IME) reads scripts without BOM as ANSI, corrupting multi-byte chars.

.CHANGELOG
    2026-06-25  v2.1  OOBE-relax: detect genuine OOBE via WinRT
                      SystemSetupInfo.OutOfBoxExperienceState and exempt guards 2+3 when a
                      single freshly-created profile appears in OOBE (Windows Backup for
                      Organizations). SKIP-safe when the API is absent (< 1809).
                      Removed legacy Content-MD5 fallback; SHA-256 manifest verification is
                      now mandatory (no hash -> bootstrap aborts, no unverified install).
    2026-05-09  v2.0  Generic bootstrap: agent owns its own defaults (e.g. 600 s
                      TenantId-wait), so the script only calls `--install` plain.
                      Hardened post-install with a 10 s runtime-process verify.
    2026-04-09  v1.1  Introduced explicit script version, logged on startup.
    2026-03-31        Replaced OS age + MDM pre-flight checks with multi-signal guard
                      (registry deployment marker, WMI/filesystem user profile, last
                      logged-on user, 12 h bootstrap window).
    2026-03-30        Fixed non-ASCII characters that broke parsing under PowerShell 5.1.
    2026-03-29        Hardened integrity check: SHA-256 verification via version.json.
    2026-02-13        Simplified bootstrapper, introduced --install parameter for agent.
    2026-02-12        Robust download with integrity check (Content-MD5), boot time support,
                      pre-flight check to skip if agent already installed.
    2026-02-05        Initial version.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$AgentDownloadUrl = "https://autopilotmonitor.blob.core.windows.net/agent/AutopilotMonitor-Agent.zip",

    [Parameter(Mandatory = $false)]
    [string]$VersionJsonName = "version.json",

    [Parameter(Mandatory = $false)]
    [int]$MaxBootstrapWindowHours = 12,

    [Parameter(Mandatory = $false)]
    [int]$OobeProfileMaxAgeMinutes = 15
)

# Script version (bump on meaningful changes; see .CHANGELOG above)
$ScriptVersion = "2.1"

# Configuration - Everything in ProgramData for easy cleanup
$AgentBasePath = "$env:ProgramData\AutopilotMonitor"
$AgentBinPath = "$AgentBasePath\Agent"
$AgentLogPath = "$AgentBasePath\Logs"
$LogFile = "$AgentLogPath\bootstrap_agent.log"

New-Item -Path $AgentBasePath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
New-Item -Path $AgentBinPath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
New-Item -Path $AgentLogPath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logMessage = "[$timestamp] $Message"
    Write-Output $logMessage
    Add-Content -Path $LogFile -Value $logMessage -ErrorAction SilentlyContinue
}

# Positive OOBE check via the documented WinRT API
# Windows.System.Profile.SystemSetupInfo.OutOfBoxExperienceState.
# Returns $true ONLY when the OS reports InProgress. Any other state (Completed/NotStarted),
# an older OS without the class (< Win10 1809 / 10.0.17763.0), or any error returns $false
# -> SKIP-safe (we never relax a guard on uncertainty).
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

try {
    Write-Log "===== Autopilot Monitor Bootstrap Started ====="
    Write-Log "Bootstrap script version: v$ScriptVersion"

    # Guard 1: Agent was already deployed on this device (registry marker survives self-destruct)
    $deployed = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\AutopilotMonitor' -Name 'Deployed' -ErrorAction SilentlyContinue).Deployed
    if ($deployed) {
        Write-Log "SKIP: Agent was previously deployed at $deployed."
        exit 0
    }

    # Collect real (non-special) user profiles as full paths (WMI Special flag + filesystem).
    $excludePattern = '^(defaultuser\d*|Public|Default( User)?|All Users|WDAGUtilityAccount)$'

    # NOTE: the outer @() is required. Without it the trailing Where/Select pipeline
    # unwraps a single result back to a scalar string, so $profilePaths[0] would index
    # into the string and return its first char ('C') instead of the full path.
    $profilePaths = @(
        @(
            try {
                Get-CimInstance Win32_UserProfile -ErrorAction Stop |
                    Where-Object { -not $_.Special -and $_.LocalPath -like 'C:\Users\*' } |
                    ForEach-Object { $_.LocalPath }
            } catch { Write-Log "INFO: WMI profile query failed, continuing with filesystem check." }

            (Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue).FullName
        ) | Where-Object { $_ -and (Split-Path $_ -Leaf) -notmatch $excludePattern } |
            Select-Object -Unique
    )

    # OOBE-relax: the ONLY exemption to guards 2+3. Matches exactly the Windows Backup for
    # Organizations restore (one user signs in + restores DURING OOBE). All three must hold:
    #   OOBE InProgress  AND  exactly one profile  AND  that profile created < N minutes ago.
    $oobeRelax = $false
    $oobeInProgress = Test-OobeInProgress
    if ($profilePaths.Count -eq 1 -and $oobeInProgress) {
        try {
            $created = (Get-Item -LiteralPath $profilePaths[0] -Force -ErrorAction Stop).CreationTimeUtc
            $ageMin = ((Get-Date).ToUniversalTime() - $created).TotalMinutes
            if ($ageMin -ge 0 -and $ageMin -lt $OobeProfileMaxAgeMinutes) {
                $oobeRelax = $true
                Write-Log ("OOBE-relax active: OOBE InProgress + single profile created {0:N1}min ago (< {1}min). Guards 2+3 relaxed (Windows Backup restore case)." -f $ageMin, $OobeProfileMaxAgeMinutes)
            } else {
                Write-Log ("OOBE-relax NOT applied: OOBE InProgress + single profile '{0}', but profile created {1:N1}min ago (created {2:u}) is outside the [0, {3})min window." -f (Split-Path $profilePaths[0] -Leaf), $ageMin, $created, $OobeProfileMaxAgeMinutes)
            }
        } catch { Write-Log "INFO: profile age check failed; OOBE-relax not applied. $($_.Exception.Message)" }
    } elseif ($profilePaths.Count -eq 1 -and -not $oobeInProgress) {
        Write-Log "OOBE-relax NOT applied: single profile present but OutOfBoxExperienceState != InProgress (OOBE already finished / not detectable)."
    } elseif ($profilePaths.Count -gt 1) {
        Write-Log ("OOBE-relax NOT applied: {0} real profiles present (relax requires exactly one)." -f $profilePaths.Count)
    }

    # Guard 2: No real user profile should exist yet (primary productive-device guard),
    # unless the OOBE-relax signature holds.
    if ($profilePaths.Count -gt 0 -and -not $oobeRelax) {
        $details = (($profilePaths | ForEach-Object {
            $leaf = Split-Path $_ -Leaf
            try {
                $c = (Get-Item -LiteralPath $_ -Force -ErrorAction Stop).CreationTimeUtc
                "{0} (created {1:u}, {2:N1}min ago)" -f $leaf, $c, ((Get-Date).ToUniversalTime() - $c).TotalMinutes
            } catch { "$leaf (age unknown)" }
        }) | Select-Object -First 3) -join '; '
        Write-Log "SKIP: Real user profile(s) found ($details). OOBE InProgress=$oobeInProgress. Device appears productive."
        exit 0
    }

    # Guard 3: LastLoggedOnUser - during Device ESP no real user has logged on yet
    # (same OOBE-relax exemption: the restoring user signs in during OOBE).
    $lastLoggedOnUser = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI' -Name 'LastLoggedOnUser' -EA SilentlyContinue).LastLoggedOnUser
    if ($lastLoggedOnUser -and $lastLoggedOnUser -notmatch 'defaultuser\d*' -and -not $oobeRelax) {
        Write-Log "SKIP: LastLoggedOnUser found ($lastLoggedOnUser). Device appears productive."
        exit 0
    }

    # Guard 4: Bootstrap window check - device uptime must be within accepted OOBE window.
    # Sleep/standby does not reset uptime, only real boot/restart does.
    $lastBoot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    $uptimeHours = ((Get-Date) - $lastBoot).TotalHours
    Write-Log "Device uptime: $([int]$uptimeHours)h (last boot: $lastBoot)"
    if ($uptimeHours -gt $MaxBootstrapWindowHours) {
        Write-Log "SKIP: Device uptime is $([int]$uptimeHours)h. OOBE state is older than accepted bootstrap window of ${MaxBootstrapWindowHours}h."
        exit 0
    }

    # Guard 5: Is the agent already installed? (leftover from previous run)
    if (Test-Path $AgentBinPath) {
        $existingAgent = Get-ChildItem -Path $AgentBinPath -Filter "AutopilotMonitor.Agent.exe" -ErrorAction SilentlyContinue
        if ($existingAgent) {
            Write-Log "SKIP: Agent already installed at $($existingAgent.FullName)."
            exit 0
        }
    }

    Write-Log "Pre-flight checks passed -- no prior deployment, no productive-device signals, within bootstrap window (oobeRelax=$oobeRelax)"

    # Download and extract agent binaries
    $agentExePath = Join-Path $AgentBinPath "AutopilotMonitor.Agent.exe"

    if (Test-Path $agentExePath) {
        Write-Log "Agent already installed at $agentExePath"
    }
    else {
        Write-Log "Downloading agent from $AgentDownloadUrl..."

        try {
            # Derive manifest URL from the agent download URL (same blob container)
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

            $zipPath = Join-Path $env:TEMP "AutopilotMonitor-Agent.zip"
            $maxDownloadAttempts = 3
            $downloadAttempt = 0

            do {
                $downloadAttempt++
                try {
                    Write-Log "Download attempt ${downloadAttempt}/${maxDownloadAttempts}"
                    Invoke-WebRequest `
                        -Uri $AgentDownloadUrl `
                        -OutFile $zipPath `
                        -UseBasicParsing `
                        -TimeoutSec 30 `
                        -ErrorAction Stop
                    Write-Log "Downloaded agent to $zipPath"
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

            # SHA-256 from the version manifest is the only accepted integrity proof.
            # No hash -> refuse to install an unverified ZIP (SKIP-safe security posture).
            if (-not $expectedSha256) {
                throw "No SHA-256 hash available from manifest - refusing to install unverified agent ZIP."
            }
            $actualSha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
            Write-Log "Validating SHA-256 hash against manifest"
            if ($actualSha256 -ne $expectedSha256) {
                throw "SHA-256 integrity check FAILED. Expected='$expectedSha256', Actual='$actualSha256'. Download may be tampered or corrupted."
            }
            Write-Log "SHA-256 integrity check passed"

            Expand-Archive -Path $zipPath -DestinationPath $AgentBinPath -Force
            Write-Log "Extracted agent to $AgentBinPath"

            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
            Write-Log "Cleaned up temporary files"

            if (-not (Test-Path $agentExePath)) {
                throw "Agent executable not found after extraction at $agentExePath"
            }

            Write-Log "Agent installation completed successfully"
        }
        catch {
            Write-Log "ERROR downloading/extracting agent: $($_.Exception.Message)"
            throw
        }
    }

    Write-Log "Calling agent install mode (--install)..."
    & $agentExePath --install
    $installExitCode = $LASTEXITCODE
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
    Write-Log "Please check log file: $LogFile"

    $errMsg = "AutopilotMonitor bootstrap failed: $($_.Exception.Message)"
    if ($errMsg.Length -gt 2048) { $errMsg = $errMsg.Substring(0, 1045) + '...' }
    [Console]::Error.WriteLine($errMsg)

    exit 1
}
