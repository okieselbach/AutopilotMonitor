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
    copy the stage-1 loader downloads on every enrolling device, whether it came from
    Intune or from the bootstrap MSI, so "current" is a correctness property, not
    cosmetics.

    Steps, in order:
      1. Parse $ScriptVersion from the loader and the bootstrap source.
      2. Render the -Dev variants by literal substitution of the URL/manifest
         defaults. A missing anchor is a hard failure -- a silently un-substituted
         dev script would point the dev fleet at the stable agent.
      3. Version-bump guard: if a published script differs from its source but
         carries the same $ScriptVersion, abort. Otherwise the docs badge,
         version.json.bootstrapVersion and the portal's "your script is outdated"
         hint would all keep asserting a version that is no longer what ships.
      4. Build the publish set (loader, bootstrap, both dev renders, the extras).
      5. Authenticode-sign every script (see -SigningToken). Signing is the LAST
         transformation: it covers the exact CRLF/UTF-8 bytes that get uploaded,
         and nothing may touch them afterwards or the signature breaks.
      6. Upload every script blob with Cache-Control: no-cache (they rotate in
         place), mirrored fail-soft to the legacy account.
      7. Reconcile the version oracles: version.json.bootstrapVersion and
         .loaderVersion (read-modify-write under If-Match, so a concurrent agent
         release cannot lose its agent fields) and
         AdminConfiguration.LatestBootstrapV2ScriptVersion.
      8. Verify through the alias -- re-download each blob and compare SHA-256.

    Comparisons against a published script always strip its signature block first
    (Get-ComparableText). A signature carries a timestamp, so signed bytes differ on
    every run; without stripping, the bump guard and the drift check would report a
    change for every unchanged script.

.PARAMETER LegacySasToken
    Container SAS for the legacy account. Optional -- a mirror failure warns, never fails.
    The legacy account lives in a different tenant, so the ambient Entra identity cannot
    reach it; this is the only credential the publisher still takes.

.PARAMETER RepoRoot
    Repository root. Defaults to the parent of .github/scripts.

.PARAMETER DryRun
    Run every check and render, upload nothing. A dry run signs when a token is supplied
    and skips signing when it is not: the PR gate runs this without any Azure login.

.PARAMETER SigningToken
    Azure Key Vault access token for AzureSignTool. MANDATORY for a real publish -- an
    unsigned customer script is not something this repo publishes. Together with
    -KeyVaultUrl and -CertificateName.

.PARAMETER ExpectedPublisher
    Optional -like pattern the signer subject must match after signing. Pins the publisher
    instead of accepting any trusted signature; the loader on the device pins the same way.

.PARAMETER SignedOutputDir
    Optional directory that receives a copy of every published file exactly as uploaded.
    Used by CI to offer the signed scripts as a build artifact for local inspection.

.EXAMPLE
    ./Publish-BootstrapScripts.ps1 -DryRun

.EXAMPLE
    ./Publish-BootstrapScripts.ps1 -LegacySasToken $env:LEGACY_SAS -SigningToken $env:AKV_TOKEN `
        -KeyVaultUrl $env:KV_URL -CertificateName $env:CERT
#>
[CmdletBinding()]
param(
    [string]$LegacySasToken,
    [string]$RepoRoot,
    [string]$SigningToken,
    [string]$KeyVaultUrl,
    [string]$CertificateName,
    [string]$TimestampUrl = 'http://timestamp.acs.microsoft.com',
    [string]$ExpectedPublisher,
    [string]$SignedOutputDir,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
}

$ContainerUrl       = 'https://autopilotmonitoreu.blob.core.windows.net/agent'
$LegacyContainerUrl = 'https://autopilotmonitor.blob.core.windows.net/agent'
$AliasUrl           = 'https://download.autopilotmonitor.com/agent'
$TableUrl           = "https://autopilotmonitoreu.table.core.windows.net/AdminConfiguration(PartitionKey='GlobalConfig',RowKey='config')"

$BootstrapSource   = Join-Path $RepoRoot 'scripts/Bootstrap/Install-AutopilotMonitor.ps1'
$BootstrapBlob     = 'Install-AutopilotMonitor.ps1'
$DevBlob           = 'Install-AutopilotMonitor-Dev.ps1'
# Stage 1. Assigned in Intune once, and embedded in the bootstrap MSI; it downloads the
# bootstrap above on every device and verifies its publisher before running it.
$LoaderSource      = Join-Path $RepoRoot 'scripts/Bootstrap/Start-AutopilotMonitor.ps1'
$LoaderBlob        = 'Start-AutopilotMonitor.ps1'
$LoaderDevBlob     = 'Start-AutopilotMonitor-Dev.ps1'
$ScriptContentType = 'text/plain; charset=utf-8'

$SigningEnabled = -not [string]::IsNullOrWhiteSpace($SigningToken)
if (-not $DryRun -and -not $SigningEnabled) {
    throw ('Publishing requires -SigningToken (plus -KeyVaultUrl and -CertificateName): the ' +
           'loader on every device verifies the publisher of what it downloads, so an unsigned ' +
           'script would be rejected in the field. Only -DryRun may run without signing.')
}
if ($SigningEnabled -and ([string]::IsNullOrWhiteSpace($KeyVaultUrl) -or [string]::IsNullOrWhiteSpace($CertificateName))) {
    throw '-SigningToken needs -KeyVaultUrl and -CertificateName.'
}

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
    # Comma operator: PowerShell unrolls an array returned from a function into the output
    # stream and reassembles it as Object[], which would silently defeat every [byte[]] use
    # downstream. ',' wraps it so the byte[] survives the return.
    if ($resp.Content -is [byte[]]) { return ,$resp.Content }
    return ,[System.Text.Encoding]::UTF8.GetBytes([string]$resp.Content)
}

# Deterministic publish form: CRLF, UTF-8 without BOM. Git normalises line endings on
# checkout (a Windows runner yields CRLF, a core.autocrlf=false clone yields LF), so
# publishing the raw working-copy bytes would emit different blobs for identical source
# and make every byte comparison -- guard and verify alike -- environment-dependent.
# CRLF is also what PS 5.1 on the device expects and what is published today.
function Get-PublishBytes {
    param([string]$Text)
    $normalised = $Text.Replace("`r`n", "`n").Replace("`n", "`r`n")
    # ',' keeps this a byte[]: without it PowerShell unrolls the array into the output stream
    # and the caller receives Object[]. Invoke-RestMethod then serialises an Object[] body as
    # space-separated decimals instead of raw bytes -- a silent corruption of the upload.
    return ,(New-Object System.Text.UTF8Encoding $false).GetBytes($normalised)
}

# A PowerShell signature is appended as a comment block at the end of the file, so the
# published bytes are "source + signature". Everything that compares a published script to
# its source has to cut that block off first -- the signature carries a timestamp and is
# therefore different on every publish, while the script above it is unchanged.
function Remove-SignatureBlock {
    param([string]$Text)
    $marker = '# SIG # Begin signature block'
    $index = $Text.IndexOf($marker)
    if ($index -lt 0) { return $Text }
    return $Text.Substring(0, $index)
}

# Comparison form: no signature, LF, no trailing blank lines. Used for "is the published
# copy still this source" -- never for what gets uploaded.
function Get-ComparableText {
    param([string]$Text)
    return (Remove-SignatureBlock $Text).Replace("`r`n", "`n").TrimEnd("`n")
}

# Signs the publish set in place: each item's bytes are written to a work directory, signed
# there, verified, and read back as the bytes that will be uploaded. Signing has to be the
# last transformation -- the signature covers exactly these bytes, so nothing may normalise
# or re-encode them afterwards.
function Set-PublishSetSignature {
    param([object[]]$Items, [string]$WorkDir)

    if (-not (Get-Command azuresigntool -ErrorAction SilentlyContinue)) {
        throw "azuresigntool is not on PATH. Install it with: dotnet tool install --global AzureSignTool --version 7.0.1"
    }

    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

    $paths = @()
    foreach ($item in $Items) {
        $path = Join-Path $WorkDir $item.BlobName
        [System.IO.File]::WriteAllBytes($path, [byte[]]$item.Bytes)
        $paths += $path
    }

    Write-Host "Signing $($paths.Count) script(s) with certificate '$CertificateName'..."
    & azuresigntool.exe sign --verbose `
        --azure-key-vault-url $KeyVaultUrl `
        --azure-key-vault-accesstoken $SigningToken `
        --azure-key-vault-certificate $CertificateName `
        --timestamp-rfc3161 $TimestampUrl `
        --file-digest sha256 `
        --description 'Autopilot Monitor bootstrap' `
        --description-url 'https://www.autopilotmonitor.com' `
        $paths

    # Native tool: PowerShell does not throw on a non-zero exit, and an unnoticed signing
    # failure would publish unsigned scripts that every loader in the field then rejects.
    if ($LASTEXITCODE -ne 0) {
        throw "azuresigntool failed with exit code $LASTEXITCODE -- nothing was uploaded."
    }

    foreach ($item in $Items) {
        $path = Join-Path $WorkDir $item.BlobName
        $sig = Get-AuthenticodeSignature -FilePath $path
        if ($sig.Status -ne 'Valid') {
            throw "Signature on $($item.BlobName) is '$($sig.Status)', expected 'Valid'."
        }
        # Without a timestamp the signature dies with the certificate, and these scripts sit
        # on customer devices far longer than a certificate lifetime.
        if (-not $sig.TimeStamperCertificate) {
            throw "Signature on $($item.BlobName) carries no timestamp."
        }
        if ($ExpectedPublisher -and $sig.SignerCertificate.Subject -notlike $ExpectedPublisher) {
            throw "$($item.BlobName) was signed by '$($sig.SignerCertificate.Subject)', expected '$ExpectedPublisher'."
        }
        # Plain assignment, NO comma operator: the ',' elsewhere in this file exists because
        # PowerShell unrolls an array RETURNED from a function, which is not what a property
        # assignment does -- here it would wrap the byte[] in an Object[] of length 1 and the
        # upload would fail converting it back.
        $signedBytes = [System.IO.File]::ReadAllBytes($path)
        if ($signedBytes.Length -le $item.Bytes.Length) {
            throw ("Signed $($item.BlobName) is $($signedBytes.Length) bytes but the unsigned form " +
                   "was $($item.Bytes.Length) -- a signature only ever adds bytes, so this is a " +
                   'read-back bug, not a signing result.')
        }
        $item.Bytes = $signedBytes
        Write-Host "  signed $($item.BlobName) ($($signedBytes.Length) bytes, signer $($sig.SignerCertificate.Subject.Split(',')[0]))"
    }
}

# Aborts when a published script changed behaviour without a version bump. Consumers act on
# the version (docs badge, version.json, the portal's outdated-script hint), so an unchanged
# version has to mean unchanged behaviour.
function Assert-VersionBump {
    param(
        [string]$BlobName,
        [string]$SourceContent,
        [string]$SourceOrigin,
        [string]$SourceVersion
    )

    $publishedBytes = Get-PublishedBytes "$AliasUrl/$BlobName"
    if ($null -eq $publishedBytes) {
        Write-Host "No published $BlobName yet -- version-bump guard skipped (first publish)"
        return
    }

    $publishedText = [System.Text.Encoding]::UTF8.GetString($publishedBytes)
    if ((Get-ComparableText $publishedText) -eq (Get-ComparableText $SourceContent)) {
        Write-Host "Published $BlobName is already this source (signature block aside)"
        return
    }

    $publishedVersion = Get-BootstrapScriptVersion -Content $publishedText -Origin "$AliasUrl/$BlobName" -Optional

    if ($null -eq $publishedVersion) {
        Write-Host "::warning::The published $BlobName is not a readable script (no ScriptVersion found). Republishing over it."
    } elseif ($publishedVersion -ne $SourceVersion) {
        Write-Host "Version bump: $publishedVersion -> $SourceVersion ($BlobName)"
    } elseif ((Get-CodeFingerprint -Text (Remove-SignatureBlock $publishedText) -Origin "$AliasUrl/$BlobName") -eq
              (Get-CodeFingerprint -Text $SourceContent -Origin $SourceOrigin)) {
        # Comments, typos in comments, reflow -- nothing a consumer of the version can act on.
        Write-Host "::warning::$BlobName changed in comments or formatting only; publishing under the unchanged version $SourceVersion."
    } else {
        throw ("Version-bump guard: $BlobName changed behaviour but ScriptVersion is still $SourceVersion. " +
               "Bump it in $SourceOrigin -- the docs badge, version.json and the " +
               'portal outdated-script hint all read that value, and customers decide from it whether ' +
               'to re-upload their Intune copy. Comment-only and formatting changes do not need a bump.')
    }
}

function Get-BootstrapScriptVersion {
    param([string]$Content, [string]$Origin, [switch]$Optional)
    if ($Content -match '\$ScriptVersion\s*=\s*"([\d\.\-a-zA-Z]+)"') { return $Matches[1] }
    # Unreadable SOURCE is fatal. An unreadable PUBLISHED copy must not be: that is exactly
    # the state a republish is meant to repair, and a guard that blocks the repair is worse
    # than no guard.
    if ($Optional) { return $null }
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

# ------------------------------------------------------------------ 1. sources
$sourceContent = Get-Content $BootstrapSource -Raw
$sourceBytes   = Get-PublishBytes $sourceContent
$scriptVersion = Get-BootstrapScriptVersion -Content $sourceContent -Origin $BootstrapSource
Write-Host "Bootstrap script version: $scriptVersion"

$loaderContent = Get-Content $LoaderSource -Raw
$loaderVersion = Get-BootstrapScriptVersion -Content $loaderContent -Origin $LoaderSource
Write-Host "Loader script version: $loaderVersion"

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

# The dev loader must fetch the dev bootstrap, or a dev device would run the stable chain.
$stableLoaderLiteral = '$BootstrapUrl = "https://download.autopilotmonitor.com/agent/Install-AutopilotMonitor.ps1"'
$devLoaderLiteral    = '$BootstrapUrl = "https://download.autopilotmonitor.com/agent/Install-AutopilotMonitor-Dev.ps1"'

if ($loaderContent.IndexOf($stableLoaderLiteral) -lt 0) {
    throw "Loader dev-render: anchor literal missing in $LoaderSource"
}
$devLoaderContent = $loaderContent.Replace($stableLoaderLiteral, $devLoaderLiteral)
Write-Host "Rendered $LoaderDevBlob (points at $DevBlob)"

# ------------------------------------------------------------------ 3. bump guards
Assert-VersionBump -BlobName $BootstrapBlob -SourceContent $sourceContent -SourceOrigin $BootstrapSource -SourceVersion $scriptVersion
Assert-VersionBump -BlobName $LoaderBlob    -SourceContent $loaderContent -SourceOrigin $LoaderSource    -SourceVersion $loaderVersion

# ------------------------------------------------------------------ 4. publish set
# Sources beyond the bootstrap pair, read straight from the repo.
$extraSources = @(
    @{ Path = 'scripts/Bootstrap/Test-ShouldBootstrapAgent.ps1';       BlobName = 'Test-ShouldBootstrapAgent.ps1' }
    # Customer-side Graph add-on grant script -- docs and admin UI link it directly.
    @{ Path = 'scripts/CustomerSetup/Grant-AutopilotMonitorAddOn.ps1'; BlobName = 'Grant-AutopilotMonitorAddOn.ps1' }
)
# Uninstall-AutopilotMonitor.ps1 is deliberately NOT published: nothing links it, and
# an unauthenticated uninstall script on the public download host is not a feature.

# Text is the unsigned publish form and stays the reference for every comparison against a
# published copy; Bytes is what goes on the wire and carries the signature after step 4.
$publishSet = [System.Collections.Generic.List[object]]::new()
$publishSet.Add([pscustomobject]@{ BlobName = $LoaderBlob;    Text = $loaderContent;    Bytes = (Get-PublishBytes $loaderContent) })
$publishSet.Add([pscustomobject]@{ BlobName = $LoaderDevBlob; Text = $devLoaderContent; Bytes = (Get-PublishBytes $devLoaderContent) })
$publishSet.Add([pscustomobject]@{ BlobName = $BootstrapBlob; Text = $sourceContent;    Bytes = $sourceBytes })
$publishSet.Add([pscustomobject]@{ BlobName = $DevBlob;       Text = $devContent;       Bytes = (Get-PublishBytes $devContent) })
foreach ($extra in $extraSources) {
    $extraPath = Join-Path $RepoRoot $extra.Path
    if (-not (Test-Path $extraPath)) {
        throw "Publish source missing: $extraPath"
    }
    $extraText = Get-Content $extraPath -Raw
    $publishSet.Add([pscustomobject]@{ BlobName = $extra.BlobName; Text = $extraText; Bytes = (Get-PublishBytes $extraText) })
}

# ------------------------------------------------------------------ 5. sign
# After this point the bytes are final. Normalising, re-encoding or rewriting a line ending
# would invalidate the signature, and the device-side loader would refuse the script.
if ($SigningEnabled) {
    Set-PublishSetSignature -Items $publishSet -WorkDir (Join-Path ([System.IO.Path]::GetTempPath()) "apm-publish-$PID")
} else {
    Write-Host '::warning::No signing token supplied -- dry run continues with unsigned scripts.'
}

$legacySas = if ($LegacySasToken) { $LegacySasToken.TrimStart('?') } else { '' }

# Acquired lazily: a dry run must stay runnable without any Azure login -- the PR gate
# (bootstrap-script-gates.yml) executes this script with -DryRun and never logs in.
$authHeaders = if ($DryRun) { $null } else { & (Join-Path $PSScriptRoot 'Get-StorageAuthHeaders.ps1') }

# ------------------------------------------------------------------ 6. upload
foreach ($item in $publishSet) {
    # Explicit type, not a convenience: an Object[] body is uploaded as space-separated
    # decimals rather than raw bytes, and every hash check still passes because [byte[]]
    # parameter coercion repairs it everywhere EXCEPT the wire.
    [byte[]]$bytes = $item.Bytes
    $item | Add-Member -NotePropertyName Sha256 -NotePropertyValue (Get-Sha256 $bytes) -Force

    # Exactly the bytes that go on the wire, for the CI artifact. Written in a dry run too:
    # that is the run an operator uses to inspect a signed script before it is published.
    if ($SignedOutputDir) {
        New-Item -ItemType Directory -Force -Path $SignedOutputDir | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $SignedOutputDir $item.BlobName), $bytes)
    }

    if ($DryRun) {
        # Doubles as a drift check: says per blob whether the alias already serves this
        # source. Compared as text without the signature block -- signed bytes differ on
        # every run, so a byte comparison would report drift for every unchanged script.
        $served = Get-PublishedBytes "$AliasUrl/$($item.BlobName)"
        $state = if ($null -eq $served) { 'MISSING on the alias' }
                 elseif ((Get-ComparableText ([System.Text.Encoding]::UTF8.GetString($served))) -eq
                         (Get-ComparableText $item.Text)) { 'already current' }
                 else { 'STALE on the alias' }
        Write-Host "  [dry-run] $($item.BlobName) ($($bytes.Length) bytes, sha256 $($item.Sha256)) -- $state"
        continue
    }

    # no-cache on every blob: they all rotate in place and the download alias fronts them
    # with Front Door. Route caching is disabled there, but a stale script paired with a
    # fresh manifest would fail the bootstrap SHA check -- this keeps that class of bug
    # impossible even if caching is ever re-enabled.
    #
    # Content-Disposition: without it a text/plain blob opens as a web page in every
    # browser, and the documented "download the script" step silently becomes "copy this
    # text". The header makes the link a download everywhere, and command-line clients
    # (the loader, the MSI, curl) ignore it.
    $headers = @{
        'x-ms-blob-type'                = 'BlockBlob'
        'Content-Type'                  = $ScriptContentType
        'x-ms-blob-cache-control'       = 'no-cache'
        'x-ms-blob-content-disposition' = ('attachment; filename="{0}"' -f $item.BlobName)
    }
    Invoke-RestMethod -Uri "$ContainerUrl/$($item.BlobName)" -Method Put -Headers ($authHeaders + $headers) -Body $bytes | Out-Null

    # Read straight back from the blob (authoritative, no CDN in the way) before touching the
    # mirror or the next file. The alias verification at the end would catch a bad body too,
    # but only after every blob was already overwritten -- these are live customer downloads.
    $written = Get-PublishedBytes "$ContainerUrl/$($item.BlobName)"
    if ($null -eq $written -or (Get-Sha256 $written) -ne $item.Sha256) {
        $writtenLength = if ($null -eq $written) { 'missing' } else { "$($written.Length) bytes" }
        throw ("Upload of $($item.BlobName) did not round-trip: sent $($bytes.Length) bytes, " +
               "blob now holds $writtenLength. Publishing stopped before the mirror and the " +
               'remaining files, so no further blob was touched.')
    }
    Write-Host "  uploaded $($item.BlobName) ($($bytes.Length) bytes, round-trip verified)"

    if ($legacySas) {
        try {
            Invoke-RestMethod -Uri "$LegacyContainerUrl/$($item.BlobName)?$legacySas" -Method Put -Headers $headers -Body $bytes | Out-Null
            Write-Host "  mirrored $($item.BlobName) to legacy storage"
        } catch {
            Write-Host "::warning::legacy mirror failed for $($item.BlobName) -- $($_.Exception.Message)"
        }
    }
}

# ------------------------------------------------------------------ 7. version oracles
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

        if ($manifest.bootstrapVersion -eq $scriptVersion -and $manifest.loaderVersion -eq $loaderVersion) {
            Write-Host "version.json already reports bootstrapVersion $scriptVersion and loaderVersion $loaderVersion"
        } else {
            $etag = '"' + ([string]($manifestResp.Headers['ETag'] | Select-Object -First 1)).Trim('"') + '"'
            $manifest | Add-Member -NotePropertyName 'bootstrapVersion' -NotePropertyValue $scriptVersion -Force
            # Additive: no consumer reads loaderVersion yet. It exists so support can tell
            # which stage-1 file a device was assigned -- stage 2 is always current by
            # construction, so its version says nothing about the customer's Intune copy.
            $manifest | Add-Member -NotePropertyName 'loaderVersion' -NotePropertyValue $loaderVersion -Force
            $body = [System.Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
            $manifestHeaders = @{
                'x-ms-blob-type'          = 'BlockBlob'
                'Content-Type'            = 'application/json'
                'x-ms-blob-cache-control' = 'no-cache'
                'If-Match'                = $etag
            }
            Invoke-RestMethod -Uri "$ContainerUrl/version.json" -Method Put -Headers ($authHeaders + $manifestHeaders) -Body $body | Out-Null
            Write-Host "version.json bootstrapVersion -> $scriptVersion, loaderVersion -> $loaderVersion (agent fields untouched)"

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

if ($DryRun) {
    Write-Host "  [dry-run] would set AdminConfiguration.LatestBootstrapV2ScriptVersion = $scriptVersion"
} else {
    $tableHeaders = @{
        'Content-Type' = 'application/json'
        'Accept'       = 'application/json;odata=nometadata'
        'If-Match'     = '*'
    }
    $tableBody = @{ LatestBootstrapV2ScriptVersion = $scriptVersion } | ConvertTo-Json
    Invoke-RestMethod -Uri $TableUrl -Method Merge -Headers ($authHeaders + $tableHeaders) -Body $tableBody | Out-Null
    Write-Host "AdminConfiguration.LatestBootstrapV2ScriptVersion = $scriptVersion"
}

# ------------------------------------------------------------------ 8. verify via alias
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
        "Loader script version: **$loaderVersion** | Bootstrap script version: **$scriptVersion**",
        "Signed: **$(if ($SigningEnabled) { "yes, certificate '$CertificateName'" } else { 'NO (unsigned dry run)' })**",
        ''
    ) + ($publishSet | ForEach-Object { "- $($_.BlobName) -- sha256 $($_.Sha256)" })
    ($summary -join [Environment]::NewLine) | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}

Write-Host 'Done.'
