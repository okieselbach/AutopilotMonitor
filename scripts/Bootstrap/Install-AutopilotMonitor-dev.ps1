<#
.SYNOPSIS
    DEV bootstrap script (v2.3-dev) to deploy and start the Autopilot Monitor agent.

.DESCRIPTION
    DEV VARIANT of Install-AutopilotMonitor.ps1 for validating the v2.3 Cloud-PC-relax
    on Windows 365 before promoting it to the production script. Differences to v2.2:
      - Cloud-PC-relax (see below) as a second trigger for the guard 2+3 exemption
      - Raw OutOfBoxExperienceState value is logged (diagnosis)
      - Logs to bootstrap_agent_dev.log so runs never interleave with the prod script
      - Testable structure: guard/relax logic lives in functions; the script only
        executes when run directly. Dot-sourcing it (as the Pester suite in
        Install-AutopilotMonitor-dev.Tests.ps1 does) loads the functions without
        side effects.

    Designed to be deployed via Intune as a PowerShell Script during Autopilot.
    Runs very early in the enrollment process (first Intune action) and:
      1. Runs pre-flight guards to skip productive devices and ghost re-installs.
         A relax exemption allows install when a single freshly-created profile
         appears DURING OOBE (Windows Backup for Organizations restore) or on a
         freshly provisioned Windows 365 Cloud PC.
      2. Downloads the monitoring agent ZIP from Azure Blob Storage
      3. Verifies integrity via SHA-256 hash from the version manifest (mandatory)
      4. Extracts the agent into %ProgramData%\AutopilotMonitor\Agent
      5. Runs the agent in --install mode (registers Scheduled Task, spawns the runtime)
      6. Verifies the runtime process actually launched
    Agent self-destructs when enrollment completes.

    Relax flow (guards 2+3): a real user profile (or a real LastLoggedOnUser)
    normally means "productive -> skip". Two legitimate flows break that assumption:

    (1) Windows Backup for Organizations: one user signs in + restores DURING OOBE.
        Trigger: the OS positively reports OOBE InProgress (Test via WinRT
        SystemSetupInfo.OutOfBoxExperienceState).

    (2) Windows 365 Cloud PC: W365 provisions headless -- OOBE and Intune enrollment
        complete before any user ever connects, so OutOfBoxExperienceState is NEVER
        InProgress when this script runs. The assigned user's profile is created at
        first connect, typically seconds before IME pulls this script. Trigger: the
        device positively identifies as a Cloud PC via Win32_ComputerSystem
        (Manufacturer 'Microsoft Corporation' + Model 'Cloud PC*').

    Either trigger relaxes guards 2+3 ONLY when additionally:
      (a) there is exactly ONE real user profile, AND
      (b) that profile was created within $OobeProfileMaxAgeMinutes minutes.
    Any miss keeps the original skip (SKIP-safe: we never install on uncertainty).
    On productive devices the profile is old, so (b) keeps skipping correctly.
    Known accepted edge: a W365 Frontline shared-mode device can legitimately show a
    single fresh profile while productive; a mis-install there is one-time (agent
    finds enrollment complete and self-destructs; Deployed marker prevents repeats).

.PARAMETER AgentDownloadUrl
    URL to download the agent ZIP from. Defaults to the production download endpoint;
    override only for parallel lab/dev assignments that point at a separate pre-release ZIP.

.PARAMETER VersionJsonName
    Filename of the integrity manifest in the same blob container as the agent ZIP.
    Defaults to "version.json"; override only for parallel lab/dev manifests.

.PARAMETER MaxBootstrapWindowHours
    Maximum device uptime (hours) within which the bootstrap is still considered
    valid. Devices booted more than this many hours ago are skipped because we no
    longer trust their OOBE state. Default: 12.
    Cloud PC note: a Cloud PC may run for days between provisioning and the user's
    first connect; this guard then skips INTENTIONALLY -- the enrollment finished
    long ago and there is nothing left to monitor.

.PARAMETER OobeProfileMaxAgeMinutes
    Maximum age (minutes) of the single profile that may appear for the relax
    exemption to apply. The restore/first-connect -> bootstrap gap is always
    minutes; an older profile is treated as a productive device. Default: 15.

.NOTES
    - Agent is temporary and auto-removes after enrollment.
    - All files live under C:\ProgramData\AutopilotMonitor (easy cleanup).
    - One registry key remains after removal: HKLM\SOFTWARE\AutopilotMonitor\Deployed
      (prevents ghost re-installs on re-Autopilot of the same device).
    - Scheduled Task survives reboots during enrollment.
    - OOBE detection uses Windows.System.Profile.SystemSetupInfo.OutOfBoxExperienceState.
      Requires Windows 10, version 1809 (introduced in 10.0.17763.0). Older builds lack
      the class -> state reads as 'Unavailable' -> original (skip) behaviour. Docs:
      https://learn.microsoft.com/en-us/uwp/api/windows.system.profile.systemsetupinfo
    - This script MUST remain pure ASCII (no Unicode/UTF-8 special chars).
      PowerShell 5.1 (IME) reads scripts without BOM as ANSI, corrupting multi-byte chars.

.CHANGELOG
    2026-08-05  v2.3-dev  DEV variant (-dev.ps1, own log file bootstrap_agent_dev.log)
                      to validate on Windows 365 before promotion to v2.3:
                      Cloud-PC-relax -- a positively identified Cloud PC
                      (Win32_ComputerSystem Manufacturer 'Microsoft Corporation' +
                      Model 'Cloud PC*') now triggers the guard 2+3 relax alongside
                      OOBE InProgress; the single-fresh-profile conditions are
                      unchanged. Raw OutOfBoxExperienceState is now logged.
                      Refactored into functions with a dot-source entry guard so the
                      Pester suite (*.Tests.ps1) can load the logic without executing
                      the bootstrap; behaviour when run directly is unchanged.
    2026-07-20  v2.2  Switched default download endpoint from the legacy blob
                      (autopilotmonitor.blob.core.windows.net) to the Front Door alias
                      https://download.autopilotmonitor.com/agent/ (route /agent/*).
                      version.json is still derived from the same container.
    2026-06-25  v2.1  OOBE-relax: detect genuine OOBE via WinRT
                      SystemSetupInfo.OutOfBoxExperienceState and exempt guards 2+3 when a
                      single freshly-created profile appears in OOBE (Windows Backup for
                      Organizations). SKIP-safe when the API is absent (< 1809).
                      Removed legacy Content-MD5 fallback; SHA-256 manifest verification is
                      now mandatory (no hash -> bootstrap aborts, no unverified install).
    2026-05-09  v2.0  Generic bootstrap: agent owns its own defaults (e.g. 600 s
                      TenantId-wait), so the script only calls `--install` plain.
                      Hardened post-install with a 10 s runtime-process verify.
    (older entries: see Install-AutopilotMonitor.ps1)
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

# Script version (bump on meaningful changes; see .CHANGELOG above)
$ScriptVersion = "2.3-dev"

# Configuration - Everything in ProgramData for easy cleanup
$AgentBasePath = "$env:ProgramData\AutopilotMonitor"
$AgentBinPath = "$AgentBasePath\Agent"
$AgentLogPath = "$AgentBasePath\Logs"
$LogFile = "$AgentLogPath\bootstrap_agent_dev.log"

# Write-Host (NOT Write-Output): log calls happen inside functions that return
# decision objects; Write-Output would leak every log line into those return
# values and turn them into arrays. Write-Host goes to the console host only.
function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $logMessage = "[$timestamp] $Message"
    Write-Host $logMessage
    Add-Content -Path $LogFile -Value $logMessage -ErrorAction SilentlyContinue
}

# Raw OOBE state via the documented WinRT API
# Windows.System.Profile.SystemSetupInfo.OutOfBoxExperienceState.
# Returns the state string (InProgress/Completed/NotStarted). An older OS without the
# class (< Win10 1809 / 10.0.17763.0) or any error returns 'Unavailable'
# -> SKIP-safe (we never relax a guard on uncertainty).
# Docs: https://learn.microsoft.com/en-us/uwp/api/windows.system.profile.systemsetupinfo
function Get-OobeState {
    try {
        $null = [Windows.System.Profile.SystemSetupInfo, Windows.System.Profile, ContentType = WindowsRuntime]
        return ([Windows.System.Profile.SystemSetupInfo]::OutOfBoxExperienceState).ToString()
    }
    catch {
        return 'Unavailable'
    }
}

# Positive Windows 365 Cloud PC detection via the documented WMI identity.
# Cloud PCs report Manufacturer 'Microsoft Corporation' and a Model starting with
# 'Cloud PC' (e.g. 'Cloud PC Enterprise 2vCPU/8GB/128GB').
# Returns the model string, or $null when not a Cloud PC / on any error (SKIP-safe).
function Get-CloudPcModel {
    try {
        $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        if ($cs.Manufacturer -eq 'Microsoft Corporation' -and $cs.Model -like 'Cloud PC*') {
            return $cs.Model
        }
    }
    catch { }
    return $null
}

# Collects real (non-special) user profiles as full paths (WMI Special flag + filesystem).
# NOTE: callers MUST wrap the result in @(...). PowerShell unrolls function output, so an
# empty result returns $null and a single path returns a scalar string, whose [0] indexer
# would yield its first char ('C') instead of the full path.
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

# Relax: the ONLY exemption to guards 2+3. Two triggers (see .DESCRIPTION):
#   (1) OOBE InProgress          -> Windows Backup for Organizations restore
#   (2) Cloud PC identity        -> Windows 365 (headless OOBE, profile at first connect)
# Either trigger additionally requires: exactly one real profile AND that profile
# created < $OobeProfileMaxAgeMinutes minutes ago. Any miss keeps the original skip.
# Returns an object with: Active (bool), OobeState (string), CloudPcModel (string or null).
function Get-RelaxDecision {
    param(
        [string[]]$ProfilePaths,
        [int]$OobeProfileMaxAgeMinutes
    )
    if ($null -eq $ProfilePaths) { $ProfilePaths = @() }

    $relaxActive = $false
    $oobeState = Get-OobeState
    $oobeInProgress = ($oobeState -eq 'InProgress')
    $cloudPcModel = Get-CloudPcModel
    if ($cloudPcModel) {
        Write-Log "Cloud PC detected: model '$cloudPcModel' (OOBE state: $oobeState)."
    }

    $relaxTrigger = $null
    if ($oobeInProgress) {
        $relaxTrigger = "OOBE InProgress (Windows Backup restore case)"
    } elseif ($cloudPcModel) {
        $relaxTrigger = "Cloud PC '$cloudPcModel' (headless provisioning, OOBE state: $oobeState)"
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
        Write-Log ("Relax NOT applied: {0} real profiles present (relax requires exactly one). OOBE state: {1}; CloudPC: {2}." -f $ProfilePaths.Count, $oobeState, [bool]$cloudPcModel)
    }

    return [pscustomobject]@{
        Active       = $relaxActive
        OobeState    = $oobeState
        CloudPcModel = $cloudPcModel
    }
}

# Runs pre-flight guards 1-5 and returns the install decision WITHOUT side effects
# beyond logging. Returns an object with:
#   Install (bool), ReasonCode (null | AlreadyDeployed | ProfilesFound |
#   LastLoggedOnUser | UptimeExceeded | AgentPresent), RelaxActive (bool).
function Get-BootstrapDecision {
    param(
        [int]$MaxBootstrapWindowHours,
        [int]$OobeProfileMaxAgeMinutes,
        [string]$AgentBinPath
    )

    # Guard 1: Agent was already deployed on this device (registry marker survives self-destruct)
    $deployed = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\AutopilotMonitor' -Name 'Deployed' -ErrorAction SilentlyContinue).Deployed
    if ($deployed) {
        Write-Log "SKIP: Agent was previously deployed at $deployed."
        return [pscustomobject]@{ Install = $false; ReasonCode = 'AlreadyDeployed'; RelaxActive = $false }
    }

    # The @() wrap is required, see NOTE on Get-RealUserProfilePaths.
    $profilePaths = @(Get-RealUserProfilePaths)
    $relax = Get-RelaxDecision -ProfilePaths $profilePaths -OobeProfileMaxAgeMinutes $OobeProfileMaxAgeMinutes

    # Guard 2: No real user profile should exist yet (primary productive-device guard),
    # unless the relax signature holds.
    if ($profilePaths.Count -gt 0 -and -not $relax.Active) {
        $details = (($profilePaths | ForEach-Object {
            $leaf = Split-Path $_ -Leaf
            try {
                $c = (Get-Item -LiteralPath $_ -Force -ErrorAction Stop).CreationTimeUtc
                "{0} (created {1:u}, {2:N1}min ago)" -f $leaf, $c, ((Get-Date).ToUniversalTime() - $c).TotalMinutes
            } catch { "$leaf (age unknown)" }
        }) | Select-Object -First 3) -join '; '
        Write-Log "SKIP: Real user profile(s) found ($details). OOBE state: $($relax.OobeState). CloudPC: $([bool]$relax.CloudPcModel). Device appears productive."
        return [pscustomobject]@{ Install = $false; ReasonCode = 'ProfilesFound'; RelaxActive = $false }
    }

    # Guard 3: LastLoggedOnUser - during Device ESP no real user has logged on yet
    # (same relax exemption: the restoring/first-connect user signs in legitimately).
    $lastLoggedOnUser = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI' -Name 'LastLoggedOnUser' -EA SilentlyContinue).LastLoggedOnUser
    if ($lastLoggedOnUser -and $lastLoggedOnUser -notmatch 'defaultuser\d*' -and -not $relax.Active) {
        Write-Log "SKIP: LastLoggedOnUser found ($lastLoggedOnUser). Device appears productive."
        return [pscustomobject]@{ Install = $false; ReasonCode = 'LastLoggedOnUser'; RelaxActive = $false }
    }

    # Guard 4: Bootstrap window check - device uptime must be within accepted OOBE window.
    # Sleep/standby does not reset uptime, only real boot/restart does.
    # Cloud PC note: uptime may exceed the window when the user first connects days after
    # provisioning -> skip is intentional (enrollment finished long ago, nothing to monitor).
    $lastBoot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    $uptimeHours = ((Get-Date) - $lastBoot).TotalHours
    Write-Log "Device uptime: $([int]$uptimeHours)h (last boot: $lastBoot)"
    if ($uptimeHours -gt $MaxBootstrapWindowHours) {
        Write-Log "SKIP: Device uptime is $([int]$uptimeHours)h. OOBE state is older than accepted bootstrap window of ${MaxBootstrapWindowHours}h."
        return [pscustomobject]@{ Install = $false; ReasonCode = 'UptimeExceeded'; RelaxActive = $relax.Active }
    }

    # Guard 5: Is the agent already installed? (leftover from previous run)
    if (Test-Path $AgentBinPath) {
        $existingAgent = Get-ChildItem -Path $AgentBinPath -Filter "AutopilotMonitor.Agent.exe" -ErrorAction SilentlyContinue
        if ($existingAgent) {
            Write-Log "SKIP: Agent already installed at $($existingAgent.FullName)."
            return [pscustomobject]@{ Install = $false; ReasonCode = 'AgentPresent'; RelaxActive = $relax.Active }
        }
    }

    return [pscustomobject]@{ Install = $true; ReasonCode = $null; RelaxActive = $relax.Active }
}

# Main bootstrap flow. Only invoked when the script is run directly (see entry guard
# at the bottom); never on dot-source.
function Invoke-Bootstrap {
    try {
        New-Item -Path $AgentBasePath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
        New-Item -Path $AgentBinPath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null
        New-Item -Path $AgentLogPath -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null

        Write-Log "===== Autopilot Monitor Bootstrap Started ====="
        Write-Log "Bootstrap script version: v$ScriptVersion"

        $decision = Get-BootstrapDecision -MaxBootstrapWindowHours $MaxBootstrapWindowHours -OobeProfileMaxAgeMinutes $OobeProfileMaxAgeMinutes -AgentBinPath $AgentBinPath
        if (-not $decision.Install) {
            exit 0
        }

        Write-Log "Pre-flight checks passed -- no prior deployment, no productive-device signals, within bootstrap window (relaxActive=$($decision.RelaxActive))"

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
}

# Entry guard: execute only when run as a script (IME, console). Dot-sourcing
# (". .\Install-AutopilotMonitor-dev.ps1", as the Pester suite does) only loads
# the functions above and MUST stay side-effect free.
if ($MyInvocation.InvocationName -ne '.') {
    Invoke-Bootstrap
}
