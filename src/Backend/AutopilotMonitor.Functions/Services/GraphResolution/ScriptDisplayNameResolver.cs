using AutopilotMonitor.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Services.GraphResolution;

/// <summary>
/// Cache-first, list-pull-on-cold-tenant, per-ID-fallback-for-net-new resolver. See PR-C
/// in <c>tasks/graph-addon-permissions-plan.md</c> for the full flow diagram.
/// </summary>
public sealed class ScriptDisplayNameResolver : IScriptDisplayNameResolver
{
    /// <summary>How long a tenant-wide list-pull stays fresh before we re-pull on the next cache miss.</summary>
    internal static readonly TimeSpan FullRefreshWindow = TimeSpan.FromDays(7);

    /// <summary>Hard cap on a single Graph HTTP request — protects the request thread from a stuck Graph call.</summary>
    internal static readonly TimeSpan PerRequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Total budget for one <see cref="ResolveAsync"/> call across all HTTP work.</summary>
    internal static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tight cap on the token-acquire path specifically. The default
    /// <see cref="GraphTokenService"/> retries 5+15+30 s with no cancellation in older code paths;
    /// for this optional feature we want a hard wall well below <see cref="TotalBudget"/>.
    /// </summary>
    internal static readonly TimeSpan TokenAcquireBudget = TimeSpan.FromMilliseconds(1500);

    private const string GraphBetaBase = Constants.GraphBaseUrl + "/beta";
    private const int PageSize = 100;

    private readonly IGraphFeatureDetector _detector;
    private readonly IScriptNameCacheRepository _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ScriptDisplayNameResolver> _logger;
    private readonly TelemetryClient _telemetry;
    private readonly TimeProvider _time;

    public ScriptDisplayNameResolver(
        IGraphFeatureDetector detector,
        IScriptNameCacheRepository cache,
        IHttpClientFactory httpClientFactory,
        ILogger<ScriptDisplayNameResolver> logger,
        TelemetryClient telemetry)
        : this(detector, cache, httpClientFactory, logger, telemetry, TimeProvider.System)
    {
    }

    /// <summary>Test seam.</summary>
    internal ScriptDisplayNameResolver(
        IGraphFeatureDetector detector,
        IScriptNameCacheRepository cache,
        IHttpClientFactory httpClientFactory,
        ILogger<ScriptDisplayNameResolver> logger,
        TelemetryClient telemetry,
        TimeProvider time)
    {
        _detector = detector;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _telemetry = telemetry;
        _time = time;
    }

    public async Task<IReadOnlyDictionary<ScriptRef, string?>> ResolveAsync(
        string tenantId,
        IReadOnlyCollection<ScriptRef> refs,
        CancellationToken ct = default)
    {
        var result = new Dictionary<ScriptRef, string?>();
        if (refs == null || refs.Count == 0) return result;
        foreach (var r in refs) result[r] = null;

        if (string.IsNullOrWhiteSpace(tenantId)) return result;

        // Total budget starts BEFORE the detector call. A slow Graph token-acquire used to
        // be able to burn ~50 s before this method even decided whether to proceed; the
        // linked CTS + the detector's CT support cap that to TokenAcquireBudget.
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(TotalBudget);
        var budget = budgetCts.Token;

        // 1. Permission gate — with a tight sub-budget so the optional feature can't hang
        //    waiting for a slow Azure AD response on tenants with consent-propagation delay.
        GraphTenantTokenContext? ctx;
        using (var tokenCts = CancellationTokenSource.CreateLinkedTokenSource(budget))
        {
            tokenCts.CancelAfter(TokenAcquireBudget);
            ctx = await SafeAsync(_detector.TryGetTokenContextAsync(tenantId, tokenCts.Token)).ConfigureAwait(false);
        }
        if (ctx == null) return result;
        if (!ctx.GrantedRoles.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll))
        {
            return result;
        }

        // 2. Cache lookup for all refs.
        Dictionary<ScriptRef, ScriptDisplayNameEntry>? cached = null;
        try
        {
            var raw = await _cache.GetManyAsync(tenantId, refs, budget).ConfigureAwait(false);
            cached = raw.ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Script-name cache lookup failed for tenant {Tenant} — proceeding without cache", tenantId);
            cached = new Dictionary<ScriptRef, ScriptDisplayNameEntry>();
        }

        var cacheHits = 0;
        foreach (var kv in cached)
        {
            if (!kv.Value.IsNotFound) result[kv.Key] = kv.Value.DisplayName;
            // NotFound rows: leave result[ref] = null (already initialised).
            cacheHits++;
        }
        var triggeredFullPull = false;

        var missing = refs.Where(r => !cached.ContainsKey(r)).ToList();
        if (missing.Count == 0) return result;

        // 3. Decide per kind: list-full-pull when meta stale, else per-ID fallback.
        // Track kinds whose full-pull hit a TRANSIENT failure (429/503/network/timeout).
        // Falling back to per-ID for those kinds in the same call would just amplify the
        // throttling Graph is trying to apply, so we suppress per-ID for that kind here.
        var skipPerIdKinds = new HashSet<ScriptKind>();

        foreach (var kind in missing.Select(r => r.Kind).Distinct())
        {
            if (budget.IsCancellationRequested) break;

            var meta = await SafeAsync(_cache.TryGetMetaAsync(tenantId, kind, budget)).ConfigureAwait(false);
            var metaStale = meta == null || meta.LastFullRefreshAt < _time.GetUtcNow() - FullRefreshWindow;

            if (metaStale)
            {
                triggeredFullPull = true;
                try
                {
                    await FullPullAsync(tenantId, kind, ctx.AccessToken, result, budget).ConfigureAwait(false);
                    try
                    {
                        await _cache.UpsertMetaAsync(new ScriptNameCacheMeta
                        {
                            TenantId = tenantId,
                            Kind = kind,
                            LastFullRefreshAt = _time.GetUtcNow(),
                        }, budget).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Script-name cache: meta upsert failed for tenant={Tenant} kind={Kind} (data still cached)",
                            tenantId, kind);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Script-name resolver: full-pull cancelled for tenant={Tenant} kind={Kind}", tenantId, kind);
                    skipPerIdKinds.Add(kind);
                }
                catch (HttpRequestException ex)
                {
                    // Graph said 4xx/5xx (or network blew up). Per-ID would just hammer the
                    // same throttle/outage with more requests — suppress fallback for this kind.
                    _logger.LogWarning(ex,
                        "Script-name resolver: full-pull transient for tenant={Tenant} kind={Kind} — skipping per-ID fallback for this kind",
                        tenantId, kind);
                    skipPerIdKinds.Add(kind);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Script-name resolver: full-pull failed for tenant={Tenant} kind={Kind} — will fall back to per-ID",
                        tenantId, kind);
                }
            }
        }

        // 4. Per-ID fallback for whatever the full-pull (or its absence) did not produce.
        var stillMissing = refs
            .Where(r => result[r] == null && !cached!.ContainsKey(r) && !skipPerIdKinds.Contains(r.Kind))
            .ToList();
        foreach (var r in stillMissing)
        {
            if (budget.IsCancellationRequested) break;
            try
            {
                var (displayName, notFound) = await FetchSingleAsync(r.Kind, r.Id, ctx.AccessToken, budget).ConfigureAwait(false);
                if (notFound)
                {
                    await WriteCacheEntryAsync(tenantId, r, displayName: null, fileName: null, notFound: true, budget).ConfigureAwait(false);
                }
                else if (displayName != null)
                {
                    result[r] = displayName;
                    await WriteCacheEntryAsync(tenantId, r, displayName, fileName: null, notFound: false, budget).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Script-name resolver: per-ID fetch cancelled for tenant={Tenant} ref={Ref}", tenantId, r);
                break;
            }
            catch (HttpRequestException ex)
            {
                // First transient HTTP failure aborts the whole per-ID fallback. Continuing
                // through 30 more refs that will likely also 429 just digs the hole deeper.
                _logger.LogWarning(ex,
                    "Script-name resolver: per-ID transient for tenant={Tenant} ref={Ref} — aborting remaining per-ID fallbacks",
                    tenantId, r);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Script-name resolver: per-ID fetch failed for tenant={Tenant} ref={Ref}", tenantId, r);
            }
        }

        // Usage telemetry — fires only when the optional permission was active AND the
        // resolver actually did work. Volume = once per session-view that has scripts,
        // which is a meaningful "tenant is using this feature" pulse.
        EmitResolved(tenantId, refs.Count, result, cacheHits, triggeredFullPull);

        return result;
    }

    private void EmitResolved(string tenantId, int refsRequested, Dictionary<ScriptRef, string?> result, int cacheHits, bool triggeredFullPull)
    {
        try
        {
            var resolved = 0;
            foreach (var v in result.Values) { if (v != null) resolved++; }
            _telemetry.TrackEvent("ScriptDisplayNamesResolved", new Dictionary<string, string>
            {
                ["TenantId"] = tenantId,
                ["RefsRequested"] = refsRequested.ToString(CultureInfo.InvariantCulture),
                ["RefsResolved"] = resolved.ToString(CultureInfo.InvariantCulture),
                ["CacheHits"] = cacheHits.ToString(CultureInfo.InvariantCulture),
                ["TriggeredFullPull"] = triggeredFullPull.ToString(CultureInfo.InvariantCulture),
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ScriptDisplayNamesResolved telemetry emit failed");
        }
    }

    // ── Graph HTTP plumbing ──────────────────────────────────────────────────

    private async Task FullPullAsync(
        string tenantId, ScriptKind kind, string accessToken,
        Dictionary<ScriptRef, string?> result, CancellationToken ct)
    {
        var entries = new List<ScriptDisplayNameEntry>();
        var url = kind == ScriptKind.Platform
            ? $"{GraphBetaBase}/deviceManagement/deviceManagementScripts?$select=id,displayName,fileName&$top={PageSize}"
            : $"{GraphBetaBase}/deviceManagement/deviceHealthScripts?$select=id,displayName&$top={PageSize}";

        var now = _time.GetUtcNow();
        var refsInResult = new HashSet<string>(result.Keys.Where(r => r.Kind == kind).Select(r => r.Id), StringComparer.OrdinalIgnoreCase);

        while (!string.IsNullOrEmpty(url) && !ct.IsCancellationRequested)
        {
            using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perCallCts.CancelAfter(PerRequestTimeout);

            var (status, body) = await SendAsync(HttpMethod.Get, url, accessToken, perCallCts.Token).ConfigureAwait(false);
            if (status != HttpStatusCode.OK)
            {
                throw new HttpRequestException($"Graph LIST {url} returned {(int)status}");
            }

            var json = JObject.Parse(body);
            var values = json["value"] as JArray ?? new JArray();
            foreach (var item in values.OfType<JObject>())
            {
                var id = item["id"]?.ToString();
                if (string.IsNullOrEmpty(id)) continue;

                var displayName = item["displayName"]?.ToString();
                var fileName = kind == ScriptKind.Platform ? item["fileName"]?.ToString() : null;
                entries.Add(new ScriptDisplayNameEntry
                {
                    TenantId = tenantId,
                    Kind = kind,
                    ScriptId = id,
                    DisplayName = displayName,
                    FileName = fileName,
                    FetchedAt = now,
                    IsNotFound = false,
                });

                if (refsInResult.Contains(id))
                {
                    var refKey = new ScriptRef(kind, id);
                    // Use the original ref key shape for case-stability.
                    var existing = result.Keys.FirstOrDefault(r => r.Kind == kind && string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (!existing.Equals(default(ScriptRef))) result[existing] = displayName;
                    else result[refKey] = displayName;
                }
            }

            url = json["@odata.nextLink"]?.ToString();
        }

        if (entries.Count > 0)
        {
            try
            {
                await _cache.UpsertManyAsync(tenantId, entries, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Script-name cache: bulk upsert after full-pull failed for tenant={Tenant} kind={Kind} (results returned in-memory only)",
                    tenantId, kind);
            }
        }
    }

    private async Task<(string? DisplayName, bool NotFound)> FetchSingleAsync(
        ScriptKind kind, string id, string accessToken, CancellationToken ct)
    {
        var url = kind == ScriptKind.Platform
            ? $"{GraphBetaBase}/deviceManagement/deviceManagementScripts/{Uri.EscapeDataString(id)}?$select=id,displayName,fileName"
            : $"{GraphBetaBase}/deviceManagement/deviceHealthScripts/{Uri.EscapeDataString(id)}?$select=id,displayName";

        using var perCallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perCallCts.CancelAfter(PerRequestTimeout);

        var (status, body) = await SendAsync(HttpMethod.Get, url, accessToken, perCallCts.Token).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound)
        {
            return (null, true);
        }
        if (status != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"Graph GET {url} returned {(int)status}");
        }

        var json = JObject.Parse(body);
        return (json["displayName"]?.ToString(), false);
    }

    private async Task WriteCacheEntryAsync(
        string tenantId, ScriptRef r, string? displayName, string? fileName, bool notFound, CancellationToken ct)
    {
        try
        {
            await _cache.UpsertManyAsync(tenantId, new[]
            {
                new ScriptDisplayNameEntry
                {
                    TenantId = tenantId,
                    Kind = r.Kind,
                    ScriptId = r.Id,
                    DisplayName = displayName,
                    FileName = fileName,
                    FetchedAt = _time.GetUtcNow(),
                    IsNotFound = notFound,
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Script-name cache: single upsert failed for tenant={Tenant} ref={Ref}", tenantId, r);
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method, string url, string accessToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    private static async Task<T?> SafeAsync<T>(Task<T?> task) where T : class
    {
        try { return await task.ConfigureAwait(false); }
        catch { return null; }
    }
}
