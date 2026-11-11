using AutopilotMonitor.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;

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
    /// Cap on the token-acquire path, whose retry chain (5+15+30 s) would otherwise outlast
    /// <see cref="TotalBudget"/>. The first mint on a cold instance often needs more than
    /// 1.5 s; a lower value drops every name on the first lookup after an idle period.
    /// </summary>
    internal static readonly TimeSpan TokenAcquireBudget = TimeSpan.FromSeconds(3);

    private const string GraphBetaBase = Constants.GraphBaseUrl + "/beta";
    private const int PageSize = 100;

    private readonly IGraphFeatureDetector _detector;
    private readonly IScriptNameCacheRepository _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ScriptDisplayNameResolver> _logger;
    private readonly TelemetryClient _telemetry;
    private readonly TimeProvider _time;
    private readonly TimeSpan _tokenAcquireBudget;

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
        TimeProvider time,
        TimeSpan? tokenAcquireBudget = null)
    {
        _detector = detector;
        _cache = cache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _telemetry = telemetry;
        _time = time;
        _tokenAcquireBudget = tokenAcquireBudget ?? TokenAcquireBudget;
    }

    // Reasons in the "lookup incomplete" line. The three time limits also set
    // ScriptDisplayNameLookup.TimedOut, which tells the caller that asking again can help.
    private const string TokenTimeout = "TokenTimeout";
    private const string TokenError = "TokenError";
    private const string LookupTimeout = "LookupTimeout";
    private const string LookupError = "LookupError";

    public async Task<ScriptDisplayNameLookup> ResolveAsync(
        string tenantId,
        IReadOnlyCollection<ScriptRef> refs,
        CancellationToken ct = default)
    {
        var result = new Dictionary<ScriptRef, string?>();
        if (refs == null || refs.Count == 0) return new ScriptDisplayNameLookup(result, TimedOut: false);
        foreach (var r in refs) result[r] = null;

        if (string.IsNullOrWhiteSpace(tenantId)) return new ScriptDisplayNameLookup(result, TimedOut: false);

        // Total budget starts BEFORE the detector call. A slow Graph token-acquire used to
        // be able to burn ~50 s before this method even decided whether to proceed; the
        // linked CTS + the detector's CT support cap that to TokenAcquireBudget.
        var started = Stopwatch.GetTimestamp();
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(TotalBudget);
        var budget = budgetCts.Token;

        // 1. Permission gate — with a tight sub-budget so the optional feature can't hang
        //    waiting for a slow Azure AD response on tenants with consent-propagation delay.
        GraphTenantTokenContext? ctx = null;
        string? tokenProblem = null;
        using (var tokenCts = CancellationTokenSource.CreateLinkedTokenSource(budget))
        {
            tokenCts.CancelAfter(_tokenAcquireBudget);
            try
            {
                ctx = await _detector.TryGetTokenContextAsync(tenantId, tokenCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Counted below as a timeout when it was this class's cap that fired.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Script-name resolver: token context failed for tenant={Tenant}", tenantId);
                tokenProblem = TokenError;
            }
            if (ctx == null && tokenProblem == null && tokenCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                tokenProblem = TokenTimeout;
            }
        }
        if (ctx == null)
        {
            // No token means no cache read either, so resolved and cacheHits are 0 by construction.
            if (tokenProblem != null) LogIncomplete(tenantId, new[] { tokenProblem }, refs.Count, resolved: 0, cacheHits: 0, started);
            return new ScriptDisplayNameLookup(result, TimedOut: tokenProblem == TokenTimeout);
        }
        if (!ctx.GrantedRoles.Contains(GraphAppPermissions.DeviceManagementScriptsReadAll))
        {
            return new ScriptDisplayNameLookup(result, TimedOut: false);
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
        if (missing.Count == 0) return new ScriptDisplayNameLookup(result, TimedOut: false);

        // 3. Decide per kind: list-full-pull when meta stale, else per-ID fallback.
        // Track kinds whose full-pull hit a TRANSIENT failure (429/503/network/timeout).
        // Falling back to per-ID for those kinds in the same call would just amplify the
        // throttling Graph is trying to apply, so we suppress per-ID for that kind here.
        var skipPerIdKinds = new HashSet<ScriptKind>();
        // Why refs may end without an answer; see LogIncomplete.
        var incomplete = new List<string>();
        var timedOut = false;

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
                    incomplete.Add($"ListTimeout:{kind}");
                    timedOut = true;
                }
                catch (HttpRequestException ex)
                {
                    // Graph said 4xx/5xx (or network blew up). Per-ID would just hammer the
                    // same throttle/outage with more requests — suppress fallback for this kind.
                    _logger.LogWarning(ex,
                        "Script-name resolver: full-pull transient for tenant={Tenant} kind={Kind} — skipping per-ID fallback for this kind",
                        tenantId, kind);
                    skipPerIdKinds.Add(kind);
                    incomplete.Add($"ListError:{kind}");
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
        //    This step stays after a successful list too: the list leaves out some scripts
        //    that a single GET returns, so "not in the list" never means "not found".
        var stillMissing = refs
            .Where(r => result[r] == null && !cached!.ContainsKey(r) && !skipPerIdKinds.Contains(r.Kind))
            .ToList();
        foreach (var r in stillMissing)
        {
            if (budget.IsCancellationRequested)
            {
                incomplete.Add(LookupTimeout);
                timedOut = true;
                break;
            }
            try
            {
                var (displayName, notFound) = await FetchSingleAsync(r.Kind, r.Id, ctx.AccessToken, budget).ConfigureAwait(false);
                if (notFound)
                {
                    await WriteCacheEntryAsync(tenantId, r, displayName: null, fileName: null, notFound: true, budget).ConfigureAwait(false);
                }
                else
                {
                    // A 200 answers for good even without a name: cached like the full pull caches it, a nameless
                    // script costs no Graph call on every view until the next full pull.
                    result[r] = displayName;
                    await WriteCacheEntryAsync(tenantId, r, displayName, fileName: null, notFound: false, budget).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Script-name resolver: per-ID fetch cancelled for tenant={Tenant} ref={Ref}", tenantId, r);
                incomplete.Add(LookupTimeout);
                timedOut = true;
                break;
            }
            catch (HttpRequestException ex)
            {
                // First transient HTTP failure aborts the whole per-ID fallback. Continuing
                // through 30 more refs that will likely also 429 just digs the hole deeper.
                _logger.LogWarning(ex,
                    "Script-name resolver: per-ID transient for tenant={Tenant} ref={Ref} — aborting remaining per-ID fallbacks",
                    tenantId, r);
                incomplete.Add(LookupError);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Script-name resolver: per-ID fetch failed for tenant={Tenant} ref={Ref}", tenantId, r);
                if (!incomplete.Contains(LookupError)) incomplete.Add(LookupError);
            }
        }

        var resolved = result.Values.Count(v => v != null);
        if (incomplete.Count > 0 && !ct.IsCancellationRequested)
        {
            LogIncomplete(tenantId, incomplete, refs.Count, resolved, cacheHits, started);
        }

        // Usage telemetry — fires only when the optional permission was active AND the
        // resolver actually did work. Volume = once per session-view that has scripts,
        // which is a meaningful "tenant is using this feature" pulse.
        EmitResolved(tenantId, refs.Count, resolved, cacheHits, triggeredFullPull);

        return new ScriptDisplayNameLookup(result, timedOut);
    }

    /// <summary>
    /// One Warning per lookup that ended without an answer for every ref because a limit of
    /// this class fired or Graph failed. Worker logs below Warning are not collected, and the
    /// health marker reads the message prefix and the <c>reasons=</c> field: keep both stable.
    /// </summary>
    private void LogIncomplete(
        string tenantId, IReadOnlyCollection<string> reasons, int requested, int resolved, int cacheHits, long started)
    {
        _logger.LogWarning(
            "Script-name lookup incomplete: tenant={Tenant} reasons={Reasons} requested={Requested} resolved={Resolved} cacheHits={CacheHits} elapsedMs={ElapsedMs}",
            tenantId, string.Join(",", reasons), requested, resolved, cacheHits,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private void EmitResolved(string tenantId, int refsRequested, int resolved, int cacheHits, bool triggeredFullPull)
    {
        try
        {
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

            var (scripts, nextUrl) = ReadScriptPage(body, withFileName: kind == ScriptKind.Platform);
            foreach (var (id, displayName, fileName) in scripts)
            {
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

            url = nextUrl;
        }

        // A budget that ends between two pages leaves the loop with pages still to read. That
        // list is incomplete and must not be stored, or the kind counts as fetched for a week.
        if (!string.IsNullOrEmpty(url)) ct.ThrowIfCancellationRequested();

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

        return (ReadScriptDisplayName(body), false);
    }

    /// <summary>
    /// Reads one page of a script list: id, display name and (platform scripts only) file name of
    /// every object entry with an id, plus the next page's url. A body that is not a JSON object
    /// throws, which ends the full pull and falls back to per-ID lookups.
    /// </summary>
    internal static (List<ScriptListItem> Scripts, string? NextUrl) ReadScriptPage(string body, bool withFileName)
    {
        var json = ParseScriptObject(body);
        var scripts = new List<ScriptListItem>();
        foreach (var item in UpstreamJson.Items(json, "value").Where(i => i.ValueKind == JsonValueKind.Object))
        {
            var id = UpstreamJson.GetString(item, "id");
            if (string.IsNullOrEmpty(id)) continue;
            scripts.Add(new ScriptListItem(id!, UpstreamJson.GetString(item, "displayName"),
                withFileName ? UpstreamJson.GetString(item, "fileName") : null));
        }
        return (scripts, UpstreamJson.GetNextLink(json));
    }

    /// <summary>Reads the display name of a single-script GET; a body that is not a JSON object throws.</summary>
    internal static string? ReadScriptDisplayName(string body) => UpstreamJson.GetString(ParseScriptObject(body), "displayName");

    // An empty or null body must throw like any other unreadable one: the full pull then falls back to
    // per-ID lookups instead of recording the list as fetched.
    private static JsonElement ParseScriptObject(string body)
        => UpstreamJson.ParseObjectOrNull(body) ?? throw new JsonException("The Graph script response is empty or null.");

    internal readonly record struct ScriptListItem(string Id, string? DisplayName, string? FileName);

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
