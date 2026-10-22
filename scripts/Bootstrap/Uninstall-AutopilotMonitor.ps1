<#
.SYNOPSIS
    Cleanup script to manually remove the Autopilot Monitor agent.

.DESCRIPTION
    This script removes all traces of the Autopilot Monitor agent:
    - Stops and removes the Scheduled Task
    - Deletes C:\ProgramData\AutopilotMonitor folder
    - Deletes C:\ProgramData\AutopilotMonitor-Loader-* folders that a failed loader run kept

    Normally the agent self-destructs after enrollment completion.
    Use this script only for manual cleanup or troubleshooting.

.EXAMPLE
    .\Uninstall-AutopilotMonitor.ps1

.NOTES
    - Requires Administrator privileges
    - Safe to run multiple times
    - Keeps HKLM\SOFTWARE\AutopilotMonitor\Deployed: the bootstrap reads it and never
      installs the agent on this device again
#>

[CmdletBinding()]
param()

$TaskName = "AutopilotMonitor-Agent"
$AgentBasePath = "$env:ProgramData\AutopilotMonitor"

Write-Host "===== Autopilot Monitor Cleanup Started =====" -ForegroundColor Cyan

# Stop and remove Scheduled Task
try {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

    if ($null -ne $task) {
        Write-Host "Stopping Scheduled Task..." -ForegroundColor Yellow
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

        Write-Host "Removing Scheduled Task..." -ForegroundColor Yellow
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
        Write-Host "  [OK] Scheduled Task removed" -ForegroundColor Green
    }
    else {
        Write-Host "  [SKIP] Scheduled Task not found (already removed)" -ForegroundColor Gray
    }
}
catch {
    Write-Host "  [FAIL] Error removing Scheduled Task: $($_.Exception.Message)" -ForegroundColor Red
}

# Delete agent folder
try {
    if (Test-Path $AgentBasePath) {
        Write-Host "Deleting agent folder: $AgentBasePath" -ForegroundColor Yellow

        # Wait a moment to ensure agent has stopped
        Start-Sleep -Seconds 2

        Remove-Item -Path $AgentBasePath -Recurse -Force -ErrorAction Stop
        Write-Host "  [OK] Agent folder deleted" -ForegroundColor Green
    }
    else {
        Write-Host "  [SKIP] Agent folder not found (already removed)" -ForegroundColor Gray
    }
}
catch {
    Write-Host "  [FAIL] Error deleting folder: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  [INFO] You may need to manually delete: $AgentBasePath" -ForegroundColor Yellow
}

# Delete loader run folders kept by failed runs
foreach ($runFolder in @(Get-ChildItem -Path $env:ProgramData -Directory -Filter 'AutopilotMonitor-Loader-*' -ErrorAction SilentlyContinue)) {
    try {
        Remove-Item -LiteralPath $runFolder.FullName -Recurse -Force -ErrorAction Stop
        Write-Host "  [OK] Loader run folder deleted: $($runFolder.FullName)" -ForegroundColor Green
    }
    catch {
        Write-Host "  [FAIL] Error deleting $($runFolder.FullName): $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "===== Cleanup Completed =====" -ForegroundColor Cyan
Write-Host "Autopilot Monitor agent has been removed from this device." -ForegroundColor Green
Write-Host ""
