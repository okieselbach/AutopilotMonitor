<#
.SYNOPSIS
    Fails when a workflow or a CI helper spells out a host instead of reading Get-Endpoints.ps1.

.DESCRIPTION
    The CI counterpart of the URL-registry guard tests (HardcodedUrlGuardTests,
    hardcodedUrls.guard.test.ts, hardcoded-urls.guard.test.ts). Enforced: every host the registry
    defines -- derived from it, so the list cannot fall behind -- plus any Azure storage, App Service
    or autopilotmonitor.com host, so a new or misspelled account is caught as well.

    Scans .github/workflows/*.yml and .github/scripts/*.ps1 except the registry itself. Comment lines
    and PowerShell block comments do not count: comments may cite URLs.

.PARAMETER Root
    Repository root to scan. Defaults to the parent of .github/scripts.
#>
[CmdletBinding()]
param(
    [string]$Root
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
}

$endpoints = & (Join-Path $PSScriptRoot 'Get-Endpoints.ps1')
$registryHosts = $endpoints.Values | ForEach-Object { ([uri]$_).Host } | Sort-Object -Unique
$patterns = @($registryHosts | ForEach-Object { [regex]::Escape($_) }) + @(
    '[a-z0-9-]+\.(blob|table|queue|file)\.core\.windows\.net'
    '[a-z0-9-]+\.azurewebsites\.net'
    '([a-z0-9-]+\.)*autopilotmonitor\.com'
)
$hostPattern = [regex]::new('(' + ($patterns -join '|') + ')', 'IgnoreCase')

$files = @(
    Get-ChildItem -Path (Join-Path $Root '.github/workflows') -File -Filter '*.yml'
    Get-ChildItem -Path (Join-Path $Root '.github/workflows') -File -Filter '*.yaml'
    Get-ChildItem -Path (Join-Path $Root '.github/scripts') -File -Filter '*.ps1' |
        Where-Object Name -ne 'Get-Endpoints.ps1'
)

$findings = 0
foreach ($file in $files) {
    $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName) -replace '\\', '/'
    $isPowerShell = $file.Extension -eq '.ps1'
    $inBlock = $false
    $lineNumber = 0
    foreach ($line in [System.IO.File]::ReadAllLines($file.FullName)) {
        $lineNumber++
        $code = $line
        if ($isPowerShell) {
            if ($inBlock) {
                $end = $code.IndexOf('#>')
                if ($end -lt 0) { continue }
                $code = $code.Substring($end + 2)
                $inBlock = $false
            }
            $start = $code.IndexOf('<#')
            if ($start -ge 0) {
                $end = $code.IndexOf('#>', $start + 2)
                if ($end -lt 0) {
                    $code = $code.Substring(0, $start)
                    $inBlock = $true
                } else {
                    $code = $code.Remove($start, $end + 2 - $start)
                }
            }
        }
        if ($code.TrimStart().StartsWith('#')) { continue }

        $match = $hostPattern.Match($code)
        if ($match.Success) {
            Write-Host "::error file=$relative,line=$lineNumber::$($match.Value) is spelled out here -- take it from .github/scripts/Get-Endpoints.ps1."
            $findings++
        }
    }
}

if ($findings -gt 0) {
    throw "$findings host literal(s) outside the endpoint registry."
}
Write-Host "No host literal outside .github/scripts/Get-Endpoints.ps1 ($($files.Count) files scanned)."
