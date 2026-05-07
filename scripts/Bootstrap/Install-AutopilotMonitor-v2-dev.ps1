<#
.SYNOPSIS
    DEV variant of the bootstrap script to deploy and start the Autopilot Monitor agent.

.DESCRIPTION
    Funktionally identical to Install-AutopilotMonitor.ps1 with two differences:
      1. Default $AgentDownloadUrl points to AutopilotMonitor-Agent-dev.zip
      2. Integrity check is read from version-dev.json (NOT version.json)
    This enables parallel lab testing of unreleased agent builds via a dedicated
    Intune Platform Script assignment, without touching any production artefacts.

    See Install-AutopilotMonitor.ps1 for the full behaviour (guards, SHA-256
    integrity verification, scheduled task registration via --install, etc.).

.PARAMETER AgentDownloadUrl
    URL to download the DEV agent binaries from (ZIP file). Defaults to
    AutopilotMonitor-Agent-dev.zip in the same blob container as production.

.NOTES
    - ASCII-only. PowerShell 5.1 / IME AgentExecutor reads scripts without BOM
      as ANSI and corrupts multi-byte chars.
    - Guards are intentionally identical to Prod bootstrapper so lab behaviour
      matches production behaviour on fresh images.

.CHANGELOG
    2026-05-04  v2.0-pre-dev  Verify-block message aligned with prod variant: WMI-detached
                              runtime launch (Program.InstallMode.cs PR1) and XML-hardened
                              BootTrigger fallback (PR2). Comment trimmed.
    2026-04-20  v2.0-pre-dev  Forked from Install-AutopilotMonitor-Dev.ps1 v1.1-dev for V2-Agent.
                              Default URL -> AutopilotMonitor-Agent-V2-dev.zip,
                              integrity file -> version-v2-dev.json,
                              agent exe -> AutopilotMonitor.Agent.exe (V2 release line, identified via version-v2-dev.json + AssemblyVersion 2.0.x).
                              Plan: plans/REFACTOR_AGENT_V2.md section 4 M2.
    2026-04-14  v1.1-dev  Initial DEV variant forked from Install-AutopilotMonitor.ps1 v1.1.
                          Default URL -> AutopilotMonitor-Agent-dev.zip, integrity file -> version-dev.json.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$AgentDownloadUrl = "https://autopilotmonitor.blob.core.windows.net/agent/AutopilotMonitor-Agent-V2-dev.zip",

    [Parameter(Mandatory = $false)]
    [int]$MaxBootstrapWindowHours = 12
)

# Script version (bump on meaningful changes; see .CHANGELOG above)
$ScriptVersion = "2.0-pre-dev"

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
    Write-Log "===== Autopilot Monitor Bootstrap Started (DEV) ====="
    Write-Log "Bootstrap script version: v$ScriptVersion"
    Write-Log "Agent download URL: $AgentDownloadUrl"

    # -- Pre-flight: Multi-signal guard (identical to production bootstrapper) --
    # Guard 1: Ghost re-installs (registry marker from previous deployment)
    # Guard 2: Productive devices (real user profile exists -- WMI + filesystem)
    # Guard 3: Productive devices (a real user has logged on before)
    # Guard 4: Bootstrap window expired (device uptime > 12h without agent)
    # Guard 5: Agent binary already present from a previous run

    # Guard 1
    $deployed = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\AutopilotMonitor' -Name 'Deployed' -ErrorAction SilentlyContinue).Deployed
    if ($deployed) {
        Write-Log "SKIP: Agent was previously deployed at $deployed."
        exit 0
    }

    # Guard 2
    $excludePattern = '^(defaultuser\d*|Public|Default( User)?|All Users)$'

    $profileNames = @(
        try {
            Get-CimInstance Win32_UserProfile -ErrorAction Stop |
                Where-Object { -not $_.Special -and $_.LocalPath -like 'C:\Users\*' } |
                ForEach-Object { Split-Path $_.LocalPath -Leaf }
        } catch { Write-Log "INFO: WMI profile query failed, continuing with filesystem check." }

        (Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue).Name
    ) | Where-Object { $_ -and $_ -notmatch $excludePattern } |
        Select-Object -Unique

    if ($profileNames) {
        $names = ($profileNames | Select-Object -First 3) -join ', '
        Write-Log "SKIP: Real user profile(s) found ($names). Device appears productive."
        exit 0
    }

    # Guard 3
    $lastLoggedOnUser = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI' -Name 'LastLoggedOnUser' -EA SilentlyContinue).LastLoggedOnUser
    if ($lastLoggedOnUser -and $lastLoggedOnUser -notmatch 'defaultuser\d*') {
        Write-Log "SKIP: LastLoggedOnUser found ($lastLoggedOnUser). Device appears productive."
        exit 0
    }

    # Guard 4
    $lastBoot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    $uptimeHours = ((Get-Date) - $lastBoot).TotalHours
    Write-Log "Device uptime: $([int]$uptimeHours)h (last boot: $lastBoot)"
    if ($uptimeHours -gt $MaxBootstrapWindowHours) {
        Write-Log "SKIP: Device uptime is $([int]$uptimeHours)h. OOBE state is older than accepted bootstrap window of ${MaxBootstrapWindowHours}h."
        exit 0
    }

    # Guard 5
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
        Write-Log "Downloading DEV agent from $AgentDownloadUrl..."

        try {
            # DEV: Integrity-File ist version-dev.json (im selben Container wie das -dev ZIP)
            $versionJsonUrl = $AgentDownloadUrl -replace '[^/]+$', 'version-v2-dev.json'
            $expectedSha256 = $null

            try {
                Write-Log "Fetching version-dev.json from $versionJsonUrl for integrity verification..."
                $versionJsonResponse = Invoke-RestMethod -Uri $versionJsonUrl -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
                if ($versionJsonResponse.sha256) {
                    $expectedSha256 = $versionJsonResponse.sha256.ToLowerInvariant()
                    Write-Log "SHA-256 hash from version-dev.json: $expectedSha256 (version: $($versionJsonResponse.version))"
                } else {
                    Write-Log "version-dev.json has no sha256 field - falling back to legacy MD5 check"
                }
            }
            catch {
                Write-Log "WARNING: Could not fetch version-dev.json - falling back to legacy MD5 check: $($_.Exception.Message)"
            }

            $zipPath = Join-Path $env:TEMP "AutopilotMonitor-Agent-V2-dev.zip"
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

            if ($expectedSha256) {
                $actualSha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
                Write-Log "Validating SHA-256 hash against version-dev.json"
                if ($actualSha256 -ne $expectedSha256) {
                    throw "SHA-256 integrity check FAILED. Expected='$expectedSha256', Actual='$actualSha256'. Download may be tampered or corrupted."
                }
                Write-Log "SHA-256 integrity check passed"
            }
            else {
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

            Expand-Archive -Path $zipPath -DestinationPath $AgentBinPath -Force
            Write-Log "Extracted agent to $AgentBinPath"

            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
            Write-Log "Cleaned up temporary files"

            if (-not (Test-Path $agentExePath)) {
                throw "Agent executable not found after extraction at $agentExePath"
            }

            Write-Log "DEV agent installation completed successfully"
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

    # Belt-and-suspenders runtime verify. --install spawns the runtime via WMI
    # (PR1, 2026-05-04), bypassing the schtasks /Run queue defer. This still
    # catches AV/EDR or AppLocker/WDAC kills after spawn. On miss, the BootTrigger
    # task (PR2 hardened) takes over at next boot.
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

    Write-Log "===== Bootstrap Completed Successfully (DEV) ====="

    exit 0
}
catch {
    Write-Log "===== Bootstrap FAILED (DEV) ====="
    Write-Log "ERROR: $($_.Exception.Message)"
    Write-Log "Stack trace: $($_.ScriptStackTrace)"
    Write-Log "Please check log file: $LogFile"

    $errMsg = "AutopilotMonitor bootstrap (DEV) failed: $($_.Exception.Message)"
    if ($errMsg.Length -gt 2048) { $errMsg = $errMsg.Substring(0, 1045) + '...' }
    [Console]::Error.WriteLine($errMsg)

    exit 1
}
