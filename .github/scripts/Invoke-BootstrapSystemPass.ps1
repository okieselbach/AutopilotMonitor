<#
.SYNOPSIS
    Runs the bootstrap integration tests once as SYSTEM, the account the bootstrap runs as on a device.

.DESCRIPTION
    Registers a one-time scheduled task as SYSTEM that runs
    scripts/Bootstrap/Bootstrap.Integration.Tests.ps1 under Windows PowerShell 5.1, waits for it,
    prints its output and fails when the run did not happen as SYSTEM or a test failed. Run as
    SYSTEM, the tests use the real folder permissions, including Administrators as owner.

    CI only (bootstrap-script-gates.yml): it needs an elevated runner and Pester installed for all
    users.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path,
    [int]$TimeoutMinutes = 20
)

$ErrorActionPreference = 'Stop'

$work = Join-Path ([System.IO.Path]::GetTempPath()) "apm-system-pass-$PID"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$runner = Join-Path $work 'run.ps1'
$output = Join-Path $work 'output.txt'
$result = Join-Path $work 'result.txt'
$tests = Join-Path $RepoRoot 'scripts\Bootstrap\Bootstrap.Integration.Tests.ps1'

$runnerText = @'
$ErrorActionPreference = 'Stop'
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
try {
    Import-Module Pester -MinimumVersion 5.5.0
    $configuration = New-PesterConfiguration
    $configuration.Run.Path = '__TESTS__'
    $configuration.Run.PassThru = $true
    $configuration.Output.Verbosity = 'Detailed'
    $run = Invoke-Pester -Configuration $configuration
    Set-Content -LiteralPath '__RESULT__' -Value ('{0}|{1}|{2}' -f $identity, $run.PassedCount, $run.FailedCount)
} catch {
    Set-Content -LiteralPath '__RESULT__' -Value ('{0}|error|{1}' -f $identity, $_.Exception.Message)
}
'@
Set-Content -LiteralPath $runner -Encoding ASCII -Value ($runnerText.Replace('__TESTS__', $tests).Replace('__RESULT__', $result))

$taskName = "apm-bootstrap-system-pass-$PID"
$arguments = "-NoProfile -ExecutionPolicy Bypass -Command `"& '$runner' *> '$output'`""
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument $arguments
$principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
try {
    Start-ScheduledTask -TaskName $taskName
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    do {
        Start-Sleep -Seconds 5
        $state = (Get-ScheduledTask -TaskName $taskName).State
    } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
    if ($state -eq 'Running') {
        Stop-ScheduledTask -TaskName $taskName
        throw "The SYSTEM pass did not finish within $TimeoutMinutes minutes."
    }
} finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

if (Test-Path -LiteralPath $output) { Get-Content -LiteralPath $output }
if (-not (Test-Path -LiteralPath $result)) { throw 'The SYSTEM pass wrote no result.' }
$identity, $passed, $failed = (Get-Content -LiteralPath $result -Raw).Trim().Split('|', 3)
if ($identity -ne 'S-1-5-18') { throw "The SYSTEM pass ran as $identity, not as SYSTEM." }
if ($passed -eq 'error') { throw "The SYSTEM pass could not run the tests: $failed" }
if ([int]$failed -gt 0 -or [int]$passed -eq 0) { throw "SYSTEM pass: $passed passed, $failed failed." }
Write-Host "SYSTEM pass: $passed passed, 0 failed."
