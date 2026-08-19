<#
.SYNOPSIS
    Publishes the customer-facing PowerShell scripts behind download.autopilotmonitor.com/agent/.

.DESCRIPTION
    Single owner of the script-publishing chain. Called from two places:

      * publish-scripts.yml  -- on every push to main that touches the sources,
                                so a script fix reaches customers without waiting
                                for an agent release.
      * build-agent.yml      -- on a publish_as_stable run, so a release always
                                lands a consistent set (ZIP + manifest + scripts).

    Both callers run the same code. The scripts on the download alias are the live
    copy the WDP bootstrap MSI (Invoke-BootstrapDownload.ps1) downloads on every
    enrolling device, so "current" is a correctness property, not cosmetics.

    Steps, in order:
      1. Parse $ScriptVersion from the bootstrap source.
      2. Render the -Dev variant by literal substitution of the two URL/manifest
         defaults. A missing anchor is a hard failure -- a silently un-substituted
         dev script would point the dev fleet at the stable agent.
      3. Version-bump guard: if the published bootstrap differs from the source
         but carries the same $ScriptVersion, abort. Otherwise the docs badge,
         version.json.bootstrapVersion and the portal's "your script is outdated"
         hint would all keep asserting a version that is no longer what ships.
      4. Upload every script blob with Cache-Control: no-cache (they rotate in
         place), mirrored fail-soft to the legacy account.
      5. Reconcile the two version oracles: version.json.bootstrapVersion
         (read-modify-write under If-Match, so a concurrent agent release cannot
         lose its agent fields) and AdminConfiguration.LatestBootstrapV2ScriptVersion.
      6. Verify through the alias -- re-download each blob and compare SHA-256.

.PARAMETER SasToken
    Container SAS for the eu storage account with write permission. Leading '?' tolerated.

.PARAMETER LegacySasToken
    Container SAS for the legacy account. Optional -- a mirror failure warns, never fails.

.PARAMETER TableSasToken
    Table SAS for AdminConfiguration. Optional -- omitted or 'XXX' skips the oracle update.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of .github/scripts.

.PARAMETER DryRun
    Run every check and render, upload nothing.

.EXAMPLE
    ./Publish-BootstrapScripts.ps1 -DryRun

.EXAMPLE
    ./Publish-BootstrapScripts.ps1 -SasToken $env:SAS -TableSasToken $env:TABLE_SAS
#>
[CmdletBinding()]
param(
    [string]$SasToken,
    [string]$LegacySasToken,
    [string]$TableSasToken,
    [string]$RepoRoot,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
}
if (-not $DryRun -and [string]::IsNullOrWhiteSpace($SasToken)) {
    throw 'SasToken is required unless -DryRun is used.'
}

$ContainerUrl       = 'https://autopilotmonitoreu.blob.core.windows.net/agent'
$LegacyContainerUrl = 'https://autopilotmonitor.blob.core.windows.net/agent'
$AliasUrl           = 'https://download.autopilotmonitor.com/agent'
$TableUrl           = "https://autopilotmonitoreu.table.core.windows.net/AdminConfiguration(PartitionKey='GlobalConfig',RowKey='config')"

$BootstrapSource   = Join-Path $RepoRoot 'scripts/Bootstrap/Install-AutopilotMonitor.ps1'
$BootstrapBlob     = 'Install-AutopilotMonitor.ps1'
$DevBlob           = 'Install-AutopilotMonitor-Dev.ps1'
$ScriptContentType = 'text/plain; charset=utf-8'

function Get-HttpStatus {
    param($ErrorRecord)
    if ($ErrorRecord.Exception.PSObject.Properties['Response'] -and $ErrorRecord.Exception.Response) {
        return [int]$ErrorRecord.Exception.Response.StatusCode
    }
    return 0
}

function Get-Sha256 {
    param([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

# Raw bytes of a published blob, or $null when it does not exist yet.
function Get-PublishedBytes {
    param([string]$Url)
    try {
        $resp = Invoke-WebRequest -Uri $Url -Method Get -UseBasicParsing
    } catch {
        if ((Get-HttpStatus $_) -eq 404) { return $null }
        throw
    }
    if ($resp.Content -is [byte[]]) { return $resp.Content }
    return [System.Text.Encoding]::UTF8.GetBytes([string]$resp.Content)
}

# Deterministic publish form: CRLF, UTF-8 without BOM. Git normalises line endings on
# checkout (a Windows runner yields CRLF, a core.autocrlf=false clone yields LF), so
# publishing the raw working-copy bytes would emit different blobs for identical source
# and make every byte comparison -- guard and verify alike -- environment-dependent.
# CRLF is also what PS 5.1 on the device expects and what is published today.
function Get-PublishBytes {
    param([string]$Text)
    $normalised = $Text.Replace("`r`n", "`n").Replace("`n", "`r`n")
    return (New-Object System.Text.UTF8Encoding $false).GetBytes($normalised)
}

function Get-BootstrapScriptVersion {
    param([string]$Content, [string]$Origin)
    if ($Content -match '\$ScriptVersion\s*=\s*"([\d\.\-a-zA-Z]+)"') { return $Matches[1] }
    throw "Could not parse ScriptVersion from $Origin"
}

# What the script actually DOES, with comments and layout removed: the PowerShell token
# stream minus comments, newlines and line continuations. Lets the version-bump guard tell
# a behaviour change (must be versioned -- consumers act on the version) apart from a
# comment fix or reflow (nobody needs to know). Tokenising rather than regex-stripping
# keeps '#' inside strings from being mistaken for a comment.
function Get-CodeFingerprint {
    param([string]$Text, [string]$Origin)
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors -and $errors.Count -gt 0) {
        throw "Parse error in ${Origin}: $($errors[0])"
    }
    $ignored = @('Comment', 'NewLine', 'LineContinuation', 'EndOfInput')
    return (($tokens | Where-Object { $ignored -notcontains $_.Kind.ToString() } | ForEach-Object { $_.Text }) -join "`n")
}

# ------------------------------------------------------------------ 1. source
$sourceContent = Get-Content $BootstrapSource -Raw
$sourceBytes   = Get-PublishBytes $sourceContent
$scriptVersion = Get-BootstrapScriptVersion -Content $sourceContent -Origin $BootstrapSource
Write-Host "Bootstrap script version: $scriptVersion"

# ------------------------------------------------------------------ 2. dev render
$stableUrlLiteral = '$AgentDownloadUrl = "https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip"'
$devUrlLiteral    = '$AgentDownloadUrl = "https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent-dev.zip"'
$stableManLiteral = '$VersionJsonName = "version.json"'
$devManLiteral    = '$VersionJsonName = "version-dev.json"'

if ($sourceContent.IndexOf($stableUrlLiteral) -lt 0 -or $sourceContent.IndexOf($stableManLiteral) -lt 0) {
    throw "Bootstrap dev-render: anchor literal missing in $BootstrapSource"
}
$devContent = $sourceContent.Replace($stableUrlLiteral, $devUrlLiteral).Replace($stableManLiteral, $devManLiteral)
Write-Host "Rendered $DevBlob (dev agent URL + version-dev.json)"

# ------------------------------------------------------------------ 3. bump guard
$publishedBytes = Get-PublishedBytes "$AliasUrl/$BootstrapBlob"
if ($null -eq $publishedBytes) {
    Write-Host 'No published bootstrap yet -- version-bump guard skipped (first publish)'
} elseif ((Get-Sha256 $publishedBytes) -eq (Get-Sha256 $sourceBytes)) {
    Write-Host 'Published bootstrap is already byte-identical to the source'
} else {
    $publishedText    = [System.Text.Encoding]::UTF8.GetString($publishedBytes)
    $publishedVersion = Get-BootstrapScriptVersion -Content $publishedText -Origin "$AliasUrl/$BootstrapBlob"

    if ($publishedVersion -ne $scriptVersion) {
        Write-Host "Version bump: $publishedVersion -> $scriptVersion"
    } elseif ((Get-CodeFingerprint -Text $publishedText -Origin "$AliasUrl/$BootstrapBlob") -eq
              (Get-CodeFingerprint -Text $sourceContent -Origin $BootstrapSource)) {
        # Comments, typos in comments, reflow -- nothing a consumer of the version can act on.
        Write-Host "::warning::$BootstrapBlob changed in comments or formatting only; publishing under the unchanged version $scriptVersion."
    } else {
        throw ("Version-bump guard: $BootstrapBlob changed behaviour but ScriptVersion is still $scriptVersion. " +
               "Bump it in $BootstrapSource -- the docs badge, version.json.bootstrapVersion and the " +
               'portal outdated-script hint all read that value, and customers decide from it whether ' +
               'to re-upload their Intune copy. Comment-only and formatting changes do not need a bump.')
    }
}

# ------------------------------------------------------------------ 4. upload
# Sources beyond the bootstrap pair, read straight from the repo.
$extraSources = @(
    @{ Path = 'scripts/Bootstrap/Test-ShouldBootstrapAgent.ps1';       BlobName = 'Test-ShouldBootstrapAgent.ps1' }
    # Customer-side Graph add-on grant script -- docs and admin UI link it directly.
    @{ Path = 'scripts/CustomerSetup/Grant-AutopilotMonitorAddOn.ps1'; BlobName = 'Grant-AutopilotMonitorAddOn.ps1' }
)
# Uninstall-AutopilotMonitor.ps1 is deliberately NOT published: nothing links it, and
# an unauthenticated uninstall script on the public download host is not a feature.

$publishSet = [System.Collections.Generic.List[object]]::new()
$publishSet.Add([pscustomobject]@{ BlobName = $BootstrapBlob; Bytes = $sourceBytes })
$publishSet.Add([pscustomobject]@{ BlobName = $DevBlob;       Bytes = (Get-PublishBytes $devContent) })
foreach ($extra in $extraSources) {
    $extraPath = Join-Path $RepoRoot $extra.Path
    if (-not (Test-Path $extraPath)) {
        throw "Publish source missing: $extraPath"
    }
    $publishSet.Add([pscustomobject]@{ BlobName = $extra.BlobName; Bytes = (Get-PublishBytes (Get-Content $extraPath -Raw)) })
}

$legacySas = if ($LegacySasToken) { $LegacySasToken.TrimStart('?') } else { '' }
$writeSas  = if ($SasToken) { $SasToken.TrimStart('?') } else { '' }

foreach ($item in $publishSet) {
    $bytes = $item.Bytes
    $item | Add-Member -NotePropertyName Sha256 -NotePropertyValue (Get-Sha256 $bytes) -Force

    if ($DryRun) {
        # Doubles as a drift check: says per blob whether the alias already serves this source.
        $served = Get-PublishedBytes "$AliasUrl/$($item.BlobName)"
        $state = if ($null -eq $served) { 'MISSING on the alias' }
                 elseif ((Get-Sha256 $served) -eq $item.Sha256) { 'already current' }
                 else { 'STALE on the alias' }
        Write-Host "  [dry-run] $($item.BlobName) ($($bytes.Length) bytes, sha256 $($item.Sha256)) -- $state"
        continue
    }

    # no-cache on every blob: they all rotate in place and the download alias fronts them
    # with Front Door. Route caching is disabled there, but a stale script paired with a
    # fresh manifest would fail the bootstrap SHA check -- this keeps that class of bug
    # impossible even if caching is ever re-enabled.
    $headers = @{ 'x-ms-blob-type' = 'BlockBlob'; 'Content-Type' = $ScriptContentType; 'x-ms-blob-cache-control' = 'no-cache' }
    Invoke-RestMethod -Uri "$ContainerUrl/$($item.BlobName)?$writeSas" -Method Put -Headers $headers -Body $bytes | Out-Null
    Write-Host "  uploaded $($item.BlobName) ($($bytes.Length) bytes)"

    if ($legacySas) {
        try {
            Invoke-RestMethod -Uri "$LegacyContainerUrl/$($item.BlobName)?$legacySas" -Method Put -Headers $headers -Body $bytes | Out-Null
            Write-Host "  mirrored $($item.BlobName) to legacy storage"
        } catch {
            Write-Host "::warning::legacy mirror failed for $($item.BlobName) -- $($_.Exception.Message)"
        }
    }
}

# ------------------------------------------------------------------ 5. version oracles
# version.json is the agent manifest; only bootstrapVersion belongs to us. Read from the
# blob (not the alias) for an authoritative ETag, write back under If-Match so a concurrent
# agent release cannot lose its version/sha256 fields.
if (-not $DryRun) {
    try {
        $manifestResp = Invoke-WebRequest -Uri "$ContainerUrl/version.json" -Method Get -UseBasicParsing
        $manifestRaw = if ($manifestResp.Content -is [byte[]]) {
            [System.Text.Encoding]::UTF8.GetString($manifestResp.Content)
        } else { [string]$manifestResp.Content }
        $manifest = $manifestRaw | ConvertFrom-Json

        if ($manifest.bootstrapVersion -eq $scriptVersion) {
            Write-Host "version.json already reports bootstrapVersion $scriptVersion"
        } else {
            $etag = '"' + ([string]($manifestResp.Headers['ETag'] | Select-Object -First 1)).Trim('"') + '"'
            $manifest | Add-Member -NotePropertyName 'bootstrapVersion' -NotePropertyValue $scriptVersion -Force
            $body = [System.Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
            $manifestHeaders = @{
                'x-ms-blob-type'          = 'BlockBlob'
                'Content-Type'            = 'application/json'
                'x-ms-blob-cache-control' = 'no-cache'
                'If-Match'                = $etag
            }
            Invoke-RestMethod -Uri "$ContainerUrl/version.json?$writeSas" -Method Put -Headers $manifestHeaders -Body $body | Out-Null
            Write-Host "version.json bootstrapVersion -> $scriptVersion (agent fields untouched)"

            if ($legacySas) {
                try {
                    $legacyHeaders = @{ 'x-ms-blob-type' = 'BlockBlob'; 'Content-Type' = 'application/json'; 'x-ms-blob-cache-control' = 'no-cache' }
                    Invoke-RestMethod -Uri "$LegacyContainerUrl/version.json?$legacySas" -Method Put -Headers $legacyHeaders -Body $body | Out-Null
                } catch {
                    Write-Host "::warning::legacy mirror failed for version.json -- $($_.Exception.Message)"
                }
            }
        }
    } catch {
        if ((Get-HttpStatus $_) -eq 412) {
            throw 'version.json changed while publishing (concurrent agent release). Re-run this workflow.'
        }
        throw
    }
}

$tableSas = if ($TableSasToken) { $TableSasToken.TrimStart('?') } else { '' }
if ($DryRun) {
    Write-Host "  [dry-run] would set AdminConfiguration.LatestBootstrapV2ScriptVersion = $scriptVersion"
} elseif ([string]::IsNullOrEmpty($tableSas) -or $tableSas -eq 'XXX') {
    Write-Host 'SKIPPED: AdminConfiguration update -- set AZURE_TABLE_ADMIN_SAS_TOKEN to enable the portal version oracle'
} else {
    $tableHeaders = @{
        'Content-Type' = 'application/json'
        'Accept'       = 'application/json;odata=nometadata'
        'If-Match'     = '*'
    }
    $tableBody = @{ LatestBootstrapV2ScriptVersion = $scriptVersion } | ConvertTo-Json
    Invoke-RestMethod -Uri "$TableUrl`?$tableSas" -Method Merge -Headers $tableHeaders -Body $tableBody | Out-Null
    Write-Host "AdminConfiguration.LatestBootstrapV2ScriptVersion = $scriptVersion"
}

# ------------------------------------------------------------------ 6. verify via alias
if (-not $DryRun) {
    $pending = [System.Collections.ArrayList]::new()
    $publishSet | ForEach-Object { [void]$pending.Add($_) }

    for ($attempt = 1; $attempt -le 6 -and $pending.Count -gt 0; $attempt++) {
        if ($attempt -gt 1) { Start-Sleep -Seconds 20 }
        foreach ($item in @($pending)) {
            $servedBytes = Get-PublishedBytes "$AliasUrl/$($item.BlobName)"
            if ($null -ne $servedBytes -and (Get-Sha256 $servedBytes) -eq $item.Sha256) {
                Write-Host "  verified $($item.BlobName) via $AliasUrl"
                $pending.Remove($item)
            }
        }
        if ($pending.Count -gt 0) {
            Write-Host "  attempt $attempt : $($pending.Count) blob(s) not current on the alias yet"
        }
    }

    if ($pending.Count -gt 0) {
        $names = ($pending | ForEach-Object { $_.BlobName }) -join ', '
        throw ("Alias still serves stale content for: $names. The blobs were written, so this is a Front Door " +
               'cache issue -- purge with: az afd endpoint purge --resource-group rg-autopilotmonitor-prd-gwc ' +
               "--profile-name autopilotmonitor-fd --endpoint-name apm-download --content-paths '/agent/*'")
    }
}

if ($env:GITHUB_STEP_SUMMARY) {
    $mode = if ($DryRun) { 'DRY RUN -- nothing uploaded' } else { 'published + verified via the download alias' }
    $summary = @(
        "### Bootstrap scripts: $mode",
        '',
        "Bootstrap script version: **$scriptVersion**",
        ''
    ) + ($publishSet | ForEach-Object { "- $($_.BlobName) -- sha256 $($_.Sha256)" })
    ($summary -join [Environment]::NewLine) | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}

Write-Host 'Done.'
