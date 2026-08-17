# Invoke-BootstrapDownload.ps1 - thin runner embedded in the Bootstrap MSI.
#
# The MSI is only a delivery vehicle (MDM LOB channel installs earlier than the IME
# script/app batches during Autopilot Device Preparation). This runner downloads the
# CURRENT Install-AutopilotMonitor.ps1 from the download endpoint and executes it, so
# every piece of real bootstrap logic (guards, relax rules, agent download) stays
# server-updatable without re-shipping the MSI.
#
# Fail-soft by design: the runner always exits 0 so a transient network failure never
# marks the LOB install as failed; diagnostics go to msi-bootstrap.log.
# ASCII only - no special characters in this file.

$ErrorActionPreference = 'Stop'
$baseDir = Join-Path $env:ProgramData 'AutopilotMonitor'
$logDir  = Join-Path $baseDir 'Logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir 'msi-bootstrap.log'

function Write-Log([string]$message) {
    Add-Content -Path $log -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message)
}

try {
    Write-Log "MSI bootstrap runner started (user=$env:USERNAME, pid=$PID)."

    if (Test-Path 'HKLM:\SOFTWARE\AutopilotMonitor\Deployed') {
        Write-Log 'Deployed marker present - skipping, another channel already installed the agent.'
        exit 0
    }

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $url  = 'https://download.autopilotmonitor.com/agent/Install-AutopilotMonitor.ps1'
    $dest = Join-Path $baseDir 'Install-AutopilotMonitor.ps1'

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $dest
            break
        } catch {
            Write-Log "Download attempt $attempt failed: $_"
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds 10
        }
    }

    Write-Log "Bootstrap script downloaded to $dest - executing."
    # PS 5.1 file redirection (*>>) writes UTF-16LE, which garbles a log otherwise
    # written via Add-Content (ANSI). Route through Add-Content for one encoding.
    & $dest *>&1 | ForEach-Object { $_.ToString() } | Add-Content -Path $log
    Write-Log "Bootstrap script finished (exit code $LASTEXITCODE)."
    exit 0
} catch {
    Write-Log ("MSI bootstrap runner failed: " + ($_ | Out-String))
    exit 0
}
