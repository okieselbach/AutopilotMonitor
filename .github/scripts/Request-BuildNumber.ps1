<#
.SYNOPSIS
    Reserves a monotone build number from a counter blob (ETag compare-and-swap).

.DESCRIPTION
    Single source of build numbers for every component that ships from this repo
    (agent, backend, MCP). Each component owns one counter blob holding a plain
    integer; this script reads it, writes value+1 back under If-Match, and returns
    the reserved number.

    Reserve-BEFORE-build: two concurrent builds can never mint the same number.
    A failed build burns a number, which is harmless -- uniqueness matters,
    density does not.

    The blob is public-read; only the write needs the container SAS.

    Why a counter and not the published version manifest: manifests are written
    on publish, not on build (the agent writes version.json only for stable
    releases), so two dev builds in a row would derive the same number and
    overwrite each other's versioned artifact.

.PARAMETER CounterUrl
    Full URL of the counter blob, without SAS.

.PARAMETER SasToken
    Container SAS with create+write permission. A leading '?' is tolerated.

.PARAMETER Override
    Explicit build number instead of counter+1. If it is not greater than the
    current counter the blob is left untouched (re-build of a number that was
    already reserved). 0 = no override.

.PARAMETER MaxAttempts
    CAS retries before giving up.

.OUTPUTS
    [int] the reserved build number. Progress goes to the host, not the pipeline.

.EXAMPLE
    $n = ./Request-BuildNumber.ps1 -CounterUrl $url -SasToken $sas
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CounterUrl,
    [Parameter(Mandatory = $true)][string]$SasToken,
    [int]$Override = 0,
    [int]$MaxAttempts = 5
)

$ErrorActionPreference = 'Stop'

# Tolerate both secret formats ("sp=cw&..." and "?sp=cw&...").
$writeUrl = $CounterUrl + '?' + $SasToken.TrimStart('?')

function Get-HttpStatus {
    param($ErrorRecord)
    if ($ErrorRecord.Exception.PSObject.Properties['Response'] -and $ErrorRecord.Exception.Response) {
        return [int]$ErrorRecord.Exception.Response.StatusCode
    }
    return 0
}

function Read-Counter {
    try {
        $resp = Invoke-WebRequest -Uri $CounterUrl -Method Get -UseBasicParsing
    } catch {
        if ((Get-HttpStatus $_) -eq 404) {
            throw "Counter blob not found at $CounterUrl. Seed it once with the last used build number: PUT the plain integer with 'x-ms-blob-type: BlockBlob' and 'If-None-Match: *'. See docs/versioning.md."
        }
        throw
    }
    $raw = if ($resp.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($resp.Content) } else { [string]$resp.Content }
    # If-Match requires the quoted ETag form; PowerShell strips the quotes on read.
    $etag = '"' + ([string]($resp.Headers['ETag'] | Select-Object -First 1)).Trim('"') + '"'
    return [pscustomobject]@{ Value = [int]$raw.Trim(); ETag = $etag }
}

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    $counter = Read-Counter

    if ($Override -gt 0 -and $Override -le $counter.Value) {
        Write-Host "  Build number $Override <= counter $($counter.Value) -- already reserved, counter blob unchanged"
        return $Override
    }
    $target = if ($Override -gt 0) { $Override } else { $counter.Value + 1 }

    try {
        $headers = @{ 'x-ms-blob-type' = 'BlockBlob'; 'If-Match' = $counter.ETag }
        Invoke-RestMethod -Uri $writeUrl -Method Put -Headers $headers -Body "$target" -ContentType 'text/plain' | Out-Null
        Write-Host "  Reserved build number $target (CAS write ok, attempt $attempt)"
        return $target
    } catch {
        if ((Get-HttpStatus $_) -eq 412) {
            Write-Host "  CAS conflict on attempt $attempt (another build reserved concurrently) -- retrying"
            Start-Sleep -Seconds ($attempt * 2)
        } else {
            throw
        }
    }
}

throw "Could not reserve a build number after $MaxAttempts CAS attempts"
