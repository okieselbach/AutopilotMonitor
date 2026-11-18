#Requires -Version 7.0
<#
.SYNOPSIS
    Post-deployment health checks. Anonymous probes only: no tokens, no secrets.

.DESCRIPTION
    Each check prints PASS / FAIL / WARN with the observed value and what it means.
    /api/health is IP rate-limited upstream (30/min): do not loop this script.

.EXAMPLE
    ./07-Test-Deployment.ps1 -ApiBaseUrl https://func-autopilotmonitor-prod.azurewebsites.net `
        -PortalUrl https://swa-autopilotmonitor-prod.azurestaticapps.net `
        -AgentContainerUrl https://stautopilotmonitorprod.blob.core.windows.net/agent `
        -McpUrl https://ca-autopilotmonitor-mcp-prod.xyz.uksouth.azurecontainerapps.io

.NOTES
    NOT EXECUTED by the author. The /api/health response shape is taken from the deploy workflow
    (commitHash, version, buildUtc); other fields are printed as returned.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ApiBaseUrl,
    [Parameter(Mandatory)] [string] $PortalUrl,
    [Parameter(Mandatory)] [string] $AgentContainerUrl,
    [string] $McpUrl
)
$script:fail = 0
function Result($state, $name, $detail) {
    $c = @{ PASS = 'Green'; FAIL = 'Red'; WARN = 'Yellow' }[$state]
    Write-Host ("[{0}] {1}" -f $state, $name) -ForegroundColor $c -NoNewline; Write-Host "  $detail"
    if ($state -eq 'FAIL') { $script:fail++ }
}
function Get-Status($uri, $method = 'GET', $body = $null) {
    try {
        $r = Invoke-WebRequest -Uri $uri -Method $method -Body $body -ContentType 'application/json' -SkipHttpErrorCheck -TimeoutSec 60 -UseBasicParsing
        return $r
    } catch { return [pscustomobject]@{ StatusCode = 0; Content = $_.Exception.Message } }
}
$api = $ApiBaseUrl.TrimEnd('/'); $portal = $PortalUrl.TrimEnd('/'); $agent = $AgentContainerUrl.TrimEnd('/')

# 1 API health
$r = Get-Status "$api/api/health"
if ($r.StatusCode -eq 200) {
    $h = $r.Content | ConvertFrom-Json
    Result PASS 'API /api/health' "version=$($h.version) commit=$($h.commitHash) built=$($h.buildUtc)"
    if (-not $h.commitHash) { Result WARN 'API build stamp' 'No commitHash: this is a local/unstamped build, not a CI artifact.' }
    $r.Content | ConvertFrom-Json | ConvertTo-Json -Depth 4 | Write-Host -ForegroundColor DarkGray
} else { Result FAIL 'API /api/health' "HTTP $($r.StatusCode). Check: Function app running? Key Vault refs resolved? See docs/05 troubleshooting." }

# 2 Protected endpoint must say 401 (proves auth middleware is in front, not an open API)
$r = Get-Status "$api/api/metrics/summary"
if ($r.StatusCode -eq 401) { Result PASS 'API auth gate' 'Anonymous call to a protected route returns 401.' }
else { Result FAIL 'API auth gate' "Expected 401, got $($r.StatusCode). If 200: STOP, the API is open." }

# 3 Agent route without a client certificate must be refused (mTLS design)
$r = Get-Status "$api/api/agent/config" 'GET'
if ($r.StatusCode -in 400, 401, 403) { Result PASS 'Agent mTLS gate' "No client cert -> HTTP $($r.StatusCode) (expected refusal)." }
else { Result WARN 'Agent mTLS gate' "Got $($r.StatusCode). If the platform did not request a cert the app cannot authenticate devices: check clientCertEnabled/clientCertMode on the Function app." }

# 4 Portal
$r = Get-Status "$portal/version.json"
if ($r.StatusCode -eq 200) { Result PASS 'Portal /version.json' $r.Content.Trim() } else { Result FAIL 'Portal /version.json' "HTTP $($r.StatusCode). Web not deployed yet?" }
$r = Get-Status "$portal/"
if ($r.StatusCode -eq 200 -and $r.Content -match 'YOUR_CLIENT_ID_HERE') { Result FAIL 'Portal client id' 'Build contains the placeholder client id. Rebuild with NEXT_PUBLIC_ENTRA_CLIENT_ID.' }
elseif ($r.StatusCode -eq 200) { Result PASS 'Portal home' 'Loads, no placeholder client id in HTML.' }

# 5 Agent artifacts (anonymous read; this is the container devices download from)
foreach ($f in 'version.json', 'AutopilotMonitor-Agent.zip', 'Install-AutopilotMonitor.ps1', 'Start-AutopilotMonitor.ps1') {
    $r = Get-Status "$agent/$f" 'HEAD'
    if ($r.StatusCode -eq 200) { Result PASS "Agent blob $f" "HTTP 200, length=$($r.Headers['Content-Length'])" } else { Result FAIL "Agent blob $f" "HTTP $($r.StatusCode). Publish the signed artifacts (docs/04 step 9)." }
}
$r = Get-Status "$agent/version.json"
if ($r.StatusCode -eq 200) { $v = $r.Content | ConvertFrom-Json; Result PASS 'Agent manifest' "version=$($v.version) sha256=$($v.sha256.Substring(0,12))... commit=$($v.commit)" }

# 6 MCP
if ($McpUrl) {
    $m = $McpUrl.TrimEnd('/')
    $r = Get-Status "$m/health"
    if ($r.StatusCode -eq 200) { $h = $r.Content | ConvertFrom-Json; Result PASS 'MCP /health' "version=$($h.version) commit=$($h.commit) docs=$($h.docs.chunks) chunks" }
    else { Result FAIL 'MCP /health' "HTTP $($r.StatusCode) (first request after scale-to-zero can take ~25 s; retry once)." }
    $r = Get-Status "$m/.well-known/oauth-protected-resource/mcp"
    if ($r.StatusCode -eq 200) {
        $j = $r.Content | ConvertFrom-Json
        Result PASS 'MCP OAuth metadata' "resource=$($j.resource)"
        if ($j.resource -notlike "$m*") { Result FAIL 'MCP_PUBLIC_URL pin' "Advertised resource '$($j.resource)' does not match $m" }
    } else { Result FAIL 'MCP OAuth metadata' "HTTP $($r.StatusCode)" }
    $r = Get-Status "$m/mcp" 'POST' '{}'
    if ($r.StatusCode -eq 401) { Result PASS 'MCP auth gate' 'Unauthenticated /mcp returns 401.' } else { Result WARN 'MCP auth gate' "Expected 401, got $($r.StatusCode)." }
}

Write-Host ''
if ($script:fail) { Write-Host "$script:fail check(s) FAILED." -ForegroundColor Red; exit 1 } else { Write-Host 'All hard checks passed. Now run the telemetry checks in docs/05 (needs a real enrollment).' -ForegroundColor Green }
