<#
.SYNOPSIS
    Bootstrap script to deploy and start the Autopilot Monitor agent.

.DESCRIPTION
    This script is designed to be deployed via Intune as a PowerShell Script during Autopilot.
    It runs VERY EARLY in the enrollment process (first Intune action) and:
    1. Creates a unique session ID for this enrollment
    2. Downloads the monitoring agent binaries
    3. Verifies integrity via SHA-256 hash from version.json
    4. Installs agent binaries (agent uses built-in backend URL)
    5. Registers agent as Scheduled Task (runs on computer startup)
    6. Agent self-destructs when enrollment completes

    INTEGRITY VERIFICATION:
    The script downloads version.json from the same blob container as the agent ZIP.
    If version.json contains a "sha256" field, the downloaded ZIP is verified against it
    using SHA-256. This prevents tampering during download (MITM) and detects corrupted
    downloads. If the hash does not match, installation is aborted.

    For backward compatibility: if version.json does not contain a "sha256" field (older
    builds), the script falls back to the legacy Content-MD5 header check.

.PARAMETER AgentDownloadUrl
    URL to download the agent binaries from (ZIP file)

.EXAMPLE
    .\Install-AutopilotMonitor.ps1
    (Uses built-in backend URL from the agent)

.NOTES
    - Agent is temporary and auto-removes after enrollment
    - Everything in C:\ProgramData\AutopilotMonitor (easy cleanup)
    - One Registry key left after removal: HKLM\SOFTWARE\AutopilotMonitor\Deployed (prevents ghost re-installs)
    - Scheduled Task survives reboots during enrollment
    - SHA-256 integrity verification since v1.0.706+
    - IMPORTANT: This script MUST remain pure ASCII (no Unicode/UTF-8 special chars).
      PowerShell 5.1 (IME) reads scripts without BOM as ANSI, corrupting multi-byte chars.

.CHANGELOG
    2026-05-04  v2.0-pre  WMI-detached runtimelaunch (Program.InstallMode.cs PR1) instead 
                          of schtasks /Run, and BootTrigger fallback via XML-hardened task (PR2).
    2026-04-20  v2.0-pre  Forked from Install-AutopilotMonitor.ps1 v1.1 for V2-Agent.
                          URL -> AutopilotMonitor-Agent-V2.zip, integrity file -> version-v2.json,
                          agent exe -> AutopilotMonitor.Agent.exe (V2 release line, identified via version-v2.json + AssemblyVersion 2.0.x). Same guards + install flow.
    2026-04-09  v1.1  Introduced explicit script version and log it on startup
    (all entries above were v1.0)
    2026-03-31  Replaced OS age + MDM pre-flight checks with multi-signal guard:
                registry deployment marker, WMI/filesystem user profile detection,
                lastloggedonUser set, and 12h bootstrap window. 
    2026-03-30  Fixed non-ASCII characters (em-dashes, Unicode symbols) that broke
                script parsing under PowerShell 5.1 / IME AgentExecutor
    2026-03-29  Hardened integrity check: SHA-256 verification via version.json
    2026-02-16  Extended OS age default threshold
    2026-02-13  Simplified bootstrapper, introduced --install parameter for agent
    2026-02-12  More robust download with integrity check (Content-MD5)
    2026-02-12  Enhanced pre-flight checks, boot time support
    2026-02-12  Added pre-flight check: skip if agent already installed
    2026-02-05  Initial version
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$AgentDownloadUrl = "https://autopilotmonitor.blob.core.windows.net/agent/AutopilotMonitor-Agent-V2.zip",

    [Parameter(Mandatory = $false)]
    [int]$MaxBootstrapWindowHours = 12
)

# Script version (bump on meaningful changes; see .CHANGELOG above)
$ScriptVersion = "2.0-pre"

# Configuration - Everything in ProgramData for easy cleanup
$AgentBasePath = "$env:ProgramData\AutopilotMonitor"
$AgentBinPath = "$AgentBasePath\Agent"
$AgentLogPath = "$AgentBasePath\Logs"
$LogFile = "$AgentLogPath\bootstrap_agent.log"

# Ensure directories exist
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

function Get-FileMd5Base64 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $hashBytes = $md5.ComputeHash($stream)
        }
        finally {
            $stream.Dispose()
        }

        return [Convert]::ToBase64String($hashBytes)
    }
    finally {
        $md5.Dispose()
    }
}

try {
    Write-Log "===== Autopilot Monitor Bootstrap Started ====="
    Write-Log "Bootstrap script version: v$ScriptVersion"

    # -- Pre-flight: Multi-signal guard to prevent installation on non-provisioning devices --
    # Layered approach: each guard catches a different scenario.
    # Guard 1: Ghost re-installs (registry marker from previous deployment)
    # Guard 2: Productive devices (real user profile exists -- WMI + filesystem)
    # Guard 3: Productive devices (a real user has logged on before)
    # Guard 4: Bootstrap window expired (device uptime > 12h without agent)
    # Guard 5: Agent binary already present from a previous run
    # NOTE: OOBEInProgress is NOT used -- it is unreliable (observed =0 during active enrollment).

    # Guard 1: Agent was already deployed on this device (registry marker survives self-destruct)
    $deployed = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\AutopilotMonitor' -Name 'Deployed' -ErrorAction SilentlyContinue).Deployed
    if ($deployed) {
        Write-Log "SKIP: Agent was previously deployed at $deployed."
        exit 0
    }

    # Guard 2: No real user profile should exist yet (primary productive-device guard)
    # Combines WMI (Win32_UserProfile.Special flag) and filesystem for maximum reliability.
    $excludePattern = '^(defaultuser\d*|Public|Default( User)?|All Users)$'

    $profileNames = @(
        # WMI/CIM view -- Special flag reliably excludes SYSTEM/LocalService/NetworkService
        try {
            Get-CimInstance Win32_UserProfile -ErrorAction Stop |
                Where-Object { -not $_.Special -and $_.LocalPath -like 'C:\Users\*' } |
                ForEach-Object { Split-Path $_.LocalPath -Leaf }
        } catch { Write-Log "INFO: WMI profile query failed, continuing with filesystem check." }

        # Filesystem view -- catches profiles WMI might miss
        (Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue).Name
    ) | Where-Object { $_ -and $_ -notmatch $excludePattern } |
        Select-Object -Unique

    if ($profileNames) {
        $names = ($profileNames | Select-Object -First 3) -join ', '
        Write-Log "SKIP: Real user profile(s) found ($names). Device appears productive."
        exit 0
    }

    # Guard 3: LastLoggedOnUser -- during Device ESP no real user has logged on yet
    $lastLoggedOnUser = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI' -Name 'LastLoggedOnUser' -EA SilentlyContinue).LastLoggedOnUser
    if ($lastLoggedOnUser -and $lastLoggedOnUser -notmatch 'defaultuser\d*') {
        Write-Log "SKIP: LastLoggedOnUser found ($lastLoggedOnUser). Device appears productive."
        exit 0
    }

    # Guard 4: Bootstrap window check (no key, no user profiles, but device running too long)
    # NOT "how long may enrollment take" -- agent handles that internally (6h emergency break).
    # This is "how old can the OOBE state be before I no longer trust it for initial install".
    # 12h bootstrap window vs 6h agent emergency break = consistent layered approach.
    # Sleep/standby does NOT reset uptime -- only real boot/restart does.
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

    Write-Log "Pre-flight checks passed -- no prior deployment, no real user profiles, no logged-on user, within bootstrap window"

    # Download and extract agent binaries
    $agentExePath = Join-Path $AgentBinPath "AutopilotMonitor.Agent.exe"

    if (Test-Path $agentExePath) {
        Write-Log "Agent already installed at $agentExePath"
    }
    else {
        Write-Log "Downloading agent from $AgentDownloadUrl..."

        try {
            # Derive version.json URL from the agent download URL (same blob container)
            $versionJsonUrl = $AgentDownloadUrl -replace '[^/]+$', 'version-v2.json'
            $expectedSha256 = $null

            # Download version.json for SHA-256 integrity verification
            try {
                Write-Log "Fetching version.json from $versionJsonUrl for integrity verification..."
                $versionJsonResponse = Invoke-RestMethod -Uri $versionJsonUrl -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
                if ($versionJsonResponse.sha256) {
                    $expectedSha256 = $versionJsonResponse.sha256.ToLowerInvariant()
                    Write-Log "SHA-256 hash from version.json: $expectedSha256 (version: $($versionJsonResponse.version))"
                } else {
                    Write-Log "version.json has no sha256 field - falling back to legacy MD5 check (older build)"
                }
            }
            catch {
                Write-Log "WARNING: Could not fetch version.json - falling back to legacy MD5 check: $($_.Exception.Message)"
            }

            # Download agent ZIP
            $zipPath = Join-Path $env:TEMP "AutopilotMonitor-Agent-V2.zip"
            $maxDownloadAttempts = 3
            $downloadAttempt = 0
            $downloadResponse = $null

            do {
                $downloadAttempt++
                try {
                    Write-Log "Download attempt ${downloadAttempt}/${maxDownloadAttempts}"
                    $downloadResponse = Invoke-WebRequest `
                        -Uri $AgentDownloadUrl `
                        -OutFile $zipPath `
                        -UseBasicParsing `
                        -TimeoutSec 30 `
                        -ErrorAction Stop `
                        -PassThru
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

            # Integrity check: SHA-256 (preferred) or legacy Content-MD5 (fallback)
            if ($expectedSha256) {
                $actualSha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
                Write-Log "Validating SHA-256 hash against version.json"
                if ($actualSha256 -ne $expectedSha256) {
                    throw "SHA-256 integrity check FAILED. Expected='$expectedSha256', Actual='$actualSha256'. Download may be tampered or corrupted."
                }
                Write-Log "SHA-256 integrity check passed"
            }
            else {
                # Legacy fallback: Content-MD5 header check
                $expectedMd5Header = $downloadResponse.Headers["Content-MD5"]
                $expectedMd5 = if ($expectedMd5Header -is [System.Array]) { "$($expectedMd5Header[0])".Trim() } else { "$expectedMd5Header".Trim() }
                if ($expectedMd5 -notmatch '\S') {
                    Write-Log "WARNING: No SHA-256 and no Content-MD5 header - skipping integrity validation"
                }
                else {
                    $actualMd5 = Get-FileMd5Base64 -Path $zipPath
                    Write-Log "Validating Content-MD5 header against downloaded ZIP"
                    if ($actualMd5 -ne $expectedMd5) {
                        throw "MD5 integrity check failed. Expected (Content-MD5)='$expectedMd5', Actual='$actualMd5'"
                    }
                    Write-Log "MD5 integrity check passed"
                }
            }

            # Extract to agent bin path
            Expand-Archive -Path $zipPath -DestinationPath $AgentBinPath -Force
            Write-Log "Extracted agent to $AgentBinPath"

            # Cleanup
            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
            Write-Log "Cleaned up temporary files"

            # Verify extraction
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

    # TenantId-wait timeout (seconds). When the registry probe finds no TenantId on
    # first try, the agent watches Enrollments + CloudDomainJoin until populated.
    # Hybrid-AAD-joined devices typically need ~5 min for the AAD device cert, so
    # 600 s leaves headroom. 0 = no wait (legacy fast-fail).
    $tenantIdWaitSec = 600

    # Let the agent install/deploy itself and manage its own Scheduled Task
    Write-Log "Calling agent install mode (--install --tenant-id-wait $tenantIdWaitSec)..."
    & $agentExePath --install --tenant-id-wait $tenantIdWaitSec
    $installExitCode = $LASTEXITCODE
    if ($installExitCode -ne 0) {
        throw "Agent install failed with exit code $installExitCode"
    }
    Write-Log "Agent install mode completed successfully"

    $runtimeProcessName = 'AutopilotMonitor.Agent.V2'
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
        Write-Log ("WARNING: Runtime process verification FAILED. Agent --install reported success but no '{0}.exe' process appeared within {1}s. Likely silent block (AV/EDR, AppLocker/WDAC) of the WMI-detached launch. Agent should still come up at next boot via the BootTrigger task. Check Event Viewer > Microsoft > Windows > TaskScheduler/Operational and AV/EDR logs for '{0}.exe'." -f $runtimeProcessName, $verifyTimeoutSec)
    }

    Write-Log "===== Bootstrap Completed Successfully ====="

    exit 0
}
catch {
    Write-Log "===== Bootstrap FAILED ====="
    Write-Log "ERROR: $($_.Exception.Message)"
    Write-Log "Stack trace: $($_.ScriptStackTrace)"
    Write-Log "Please check log file: $LogFile"

    # some errors are captured by Intune but not fully visible in the UI (truncated, no stack trace), so also write critical info to stderr for better visibility.
    $errMsg = "AutopilotMonitor bootstrap failed: $($_.Exception.Message)"
    if ($errMsg.Length -gt 2048) { $errMsg = $errMsg.Substring(0, 1045) + '...' }
    [Console]::Error.WriteLine($errMsg)

    exit 1
}
