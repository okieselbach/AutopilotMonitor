using System.Net;
using AutopilotMonitor.Functions.Extensions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Enforces the MCP daily/monthly request quota — the caller's own budget AND the organization-wide
/// budget of the CHARGED tenant (both counted here, both must be free). Runs after
/// <see cref="UserRateLimitMiddleware"/> (per-minute burst control) — this is the budget layer on
/// top. Applies ONLY to HTTP requests marked <c>X-Client-Source: mcp</c> with an authenticated
/// principal (oid); everything else passes through untouched.
///
/// <b>Which tenant is charged ("the budget follows the data"):</b>
/// <list type="bullet">
///   <item>A member reading their own tenant: the home tenant (JWT tid).</item>
///   <item>A delegated (MSP) admin reading ONE managed tenant (<c>?tenantId=</c>): that managed tenant — its
///   plan governs the organization windows, and its admins see the consumption on their MCP Usage page.
///   Exception: a tenant reached through a Tenant Group flagged <c>ChargeHomeTenantQuota</c> (operator-run
///   managed service) is charged to the admin's HOME tenant instead.</item>
///   <item>A delegated admin's bounded fleet aggregate (no tenantId on a subset-tier route): every charged
///   tenant in the managed set. Tenants whose budget is exhausted are EXCLUDED — <see cref="RequestContext.AllowedTenantIds"/>
///   is NARROWED to the admitted ones and the dropped set is published as <see cref="RequestContext.QuotaExcludedTenantIds"/>
///   for the handler to echo — instead of failing the whole request; only "everything exhausted" is a 429.</item>
///   <item>Global Admins are exempt from the quota and tracked on their HOME tenant only — never on a
///   target tenant's counters (they read every tenant for platform operations).</item>
/// </list>
/// <b>Isolation invariant:</b> narrowing only ever SHRINKS the published bound and never turns it into
/// <c>null</c> (null = unbounded in the repository). See <see cref="Narrow"/>.
///
/// This middleware also OWNS the usage-counter increment (moved here from AuthenticationMiddleware,
/// Codex finding 2026-07-07): check-then-increment, and only for requests that are actually
/// served — denied requests (403 upstream, 429 here) no longer inflate the counters, and a
/// request can never be blocked by its OWN in-flight increment. The increment stays
/// fire-and-forget (never blocks the request path).
///
/// <b>The quota boundary is deliberately SOFT, not exact.</b> McpQuotaService caches the usage
/// snapshots for 60 seconds (per instance; per user and per charged tenant), so an ALLOWED decision keeps
/// admitting requests inside that window even after the async increments push the stored counters past
/// the limit. Worst-case overshoot is bounded: ~60s × the request rate against that counter (× instance
/// count on scaled-out Flex Consumption) — the same deliberate posture as the sliding-window
/// rate limiter, trading exactness for one counter read per user / per tenant per minute instead of per
/// request. Pinned by McpQuotaServiceTests soft-boundary tests; do NOT re-document this as an
/// exact limit without reworking the snapshot cache.
///
/// Over-quota requests get 429 with a structured body, <c>Retry-After</c>, and
/// <c>X-MCP-Quota-*</c> headers; allowed MCP requests get the quota headers too so the MCP
/// client can surface remaining budget. Fail-open on storage/counter errors (handled inside
/// <see cref="McpQuotaService"/>), fail-closed on plan resolution (unknown plan → Community).
/// </summary>
public class McpQuotaEnforcementMiddleware : IFunctionsWorkerMiddleware
{
    private readonly McpQuotaService _quotaService;
    private readonly IUserUsageRepository _userUsageRepo;
    private readonly TenantConfigurationService _tenantConfigService;
    private readonly ILogger<McpQuotaEnforcementMiddleware> _logger;

    public McpQuotaEnforcementMiddleware(
        McpQuotaService quotaService,
        IUserUsageRepository userUsageRepo,
        TenantConfigurationService tenantConfigService,
        ILogger<McpQuotaEnforcementMiddleware> logger)
    {
        _quotaService = quotaService;
        _userUsageRepo = userUsageRepo;
        _tenantConfigService = tenantConfigService;
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            await next(context);
            return;
        }

        var isMcpRequest = string.Equals(
            httpContext.Request.Headers["X-Client-Source"].FirstOrDefault(), "mcp", StringComparison.OrdinalIgnoreCase);
        if (!isMcpRequest)
        {
            await next(context);
            return;
        }

        var principal = context.GetUser();
        var oid = principal?.GetObjectId();
        if (principal == null || string.IsNullOrEmpty(oid))
        {
            // Unauthenticated MCP probe — auth middleware / policy enforcement handle rejection.
            await next(context);
            return;
        }

        var upn = principal.GetUserPrincipalName();
        var homeTenantId = principal.GetTenantId() ?? string.Empty;
        var requestContext = context.GetRequestContext();

        // GA is exempt from the quota and is tracked on its HOME tenant only — never on a target tenant's
        // counters (platform operations must not consume, or be blocked by, a customer's budget).
        if (requestContext.IsGlobalAdmin)
        {
            TrackUsage(httpContext, oid, upn, homeTenantId, new[] { homeTenantId });
            await next(context);
            return;
        }

        var scope = ResolveChargeScope(requestContext, homeTenantId);
        if (scope.Kind == McpChargeKind.BoundedAggregate)
        {
            await InvokeAggregateAsync(context, next, httpContext, requestContext, oid, upn, homeTenantId, scope);
            return;
        }

        var chargeTenantId = scope.TargetTenantId ?? homeTenantId;
        McpQuotaDecision decision;
        try
        {
            decision = await _quotaService.CheckAsync(oid, upn, homeTenantId, chargeTenantId);
        }
        catch (Exception ex)
        {
            // Belt-and-braces fail-open: the quota layer must never take MCP down.
            _logger.LogError(ex, "[McpQuota] Quota check threw for oid={Oid} — allowing request (fail-open)", oid);
            TrackUsage(httpContext, oid, upn, homeTenantId, new[] { chargeTenantId });
            await next(context);
            return;
        }

        StampQuotaHeaders(httpContext, decision, chargeTenantId);

        if (decision.Allowed)
        {
            // Check-then-increment: the decision above reflects previously SERVED requests only.
            TrackUsage(httpContext, oid, upn, homeTenantId, new[] { chargeTenantId });
            await next(context);
            return;
        }

        LogBlocked(oid, chargeTenantId, decision);
        var targetLabel = decision.TargetTenantId != null ? await ResolveTenantLabelAsync(decision.TargetTenantId) : null;
        await WriteExceededAsync(httpContext, decision, BuildExceededResponse(decision, targetLabel));
    }

    /// <summary>
    /// The bounded fleet aggregate: every charged tenant is checked; exhausted ones are dropped from the
    /// published bound (never the whole request), and the handler echoes them as quotaExcludedTenants.
    /// </summary>
    private async Task InvokeAggregateAsync(
        FunctionContext context, FunctionExecutionDelegate next, HttpContext httpContext, RequestContext requestContext,
        string oid, string? upn, string homeTenantId, McpChargeScope scope)
    {
        var chargeTenantIds = scope.ChargeMap!.Keys.ToList();
        McpAggregateQuotaResult result;
        try
        {
            result = await _quotaService.CheckManyAsync(oid, upn, homeTenantId, chargeTenantIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[McpQuota] Aggregate quota check threw for oid={Oid} — allowing request (fail-open)", oid);
            TrackUsage(httpContext, oid, upn, homeTenantId, chargeTenantIds);
            await next(context);
            return;
        }

        var excludedTargets = scope.TargetsOf(result.ExcludedTenantIds);
        if (!result.Allowed)
        {
            var blocking = result.BlockingDecision!;
            StampQuotaHeaders(httpContext, blocking, chargeTenantId: null);
            LogBlocked(oid, $"aggregate({chargeTenantIds.Count})", blocking);
            // A user-level block names the caller's own plan; "every managed tenant exhausted" names the count.
            var exhausted = blocking.Level == McpQuotaLevel.Tenant ? excludedTargets.Count : (int?)null;
            await WriteExceededAsync(httpContext, blocking, BuildExceededResponse(blocking, exhaustedTenantCount: exhausted));
            return;
        }

        var admittedTargets = scope.TargetsOf(result.AdmittedTenantIds);
        context.Items[RequestContext.ItemsKey] = Narrow(requestContext, admittedTargets, excludedTargets);

        StampQuotaHeaders(httpContext, result.UserDecision, chargeTenantId: null, includeTenantWindows: false);
        httpContext.Response.Headers["X-MCP-Quota-Excluded-Tenants"] = excludedTargets.Count.ToString();

        TrackUsage(httpContext, oid, upn, homeTenantId, result.AdmittedTenantIds);
        await next(context);
    }

    /// <summary>
    /// Pure: which tenant(s) this request draws on. Not delegated ⇒ home. A delegated single-target read ⇒
    /// that target, unless it was reached through a home-charged group ⇒ home. A delegated bounded aggregate ⇒
    /// one charge per managed tenant, home-charged ones mapped onto the home tenant (checked and counted
    /// ONCE, never excluded on the managed tenant's behalf).
    /// </summary>
    internal static McpChargeScope ResolveChargeScope(RequestContext ctx, string homeTenantId)
    {
        if (!ctx.IsDelegated)
            return McpChargeScope.Home(homeTenantId);

        var homeCharged = new HashSet<string>(ctx.HomeChargedTenantIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        if (ctx.IsDelegatedAggregate && ctx.AllowedTenantIds is { Count: > 0 })
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in ctx.AllowedTenantIds)
            {
                if (string.IsNullOrWhiteSpace(target))
                    continue;
                var charge = homeCharged.Contains(target) ? homeTenantId : target;
                if (!map.TryGetValue(charge, out var targets))
                    map[charge] = targets = new List<string>();
                targets.Add(target);
            }
            if (map.Count > 0)
                return new McpChargeScope(McpChargeKind.BoundedAggregate, homeTenantId, null, map);
        }

        var single = ctx.TargetTenantId;
        if (!string.IsNullOrEmpty(single)
            && !string.Equals(single, homeTenantId, StringComparison.OrdinalIgnoreCase)
            && !homeCharged.Contains(single))
            return new McpChargeScope(McpChargeKind.SingleTarget, homeTenantId, single, null);

        return McpChargeScope.Home(homeTenantId);
    }

    /// <summary>
    /// Pure. ISOLATION INVARIANT: the published bound is only ever NARROWED — the result is a non-null
    /// list that is a subset of the input bound (empty allowed: the repository then serves an empty page),
    /// and a context without a bound (null = GA/Reader, unbounded) is returned untouched. Never produce
    /// null here: null means "all tenants" to the repository.
    /// </summary>
    internal static RequestContext Narrow(RequestContext ctx, IReadOnlyCollection<string> admittedTargets, IReadOnlyCollection<string> excludedTargets)
    {
        if (ctx.AllowedTenantIds == null)
            return ctx;

        var bound = new HashSet<string>(ctx.AllowedTenantIds, StringComparer.OrdinalIgnoreCase);
        var narrowed = admittedTargets.Where(bound.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var excluded = excludedTargets.Where(bound.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return ctx with
        {
            AllowedTenantIds = narrowed,
            QuotaExcludedTenantIds = excluded.Count > 0 ? excluded : null,
        };
    }

    /// <summary>
    /// The 429 body for a blocked decision. The message names WHOSE budget is exhausted — a member hitting the
    /// organization-wide window must not conclude their own plan is too small, and a delegated admin hitting
    /// a MANAGED tenant's window must learn that the managed tenant's plan (not their own) governs it.
    /// Every Community block also says that Community is sized for occasional use and that Pro lifts the
    /// window: the quota is the upgrade lever ("the budget follows the data" — a managed Community customer
    /// is read with Community windows, so the fix is on the CUSTOMER's plan, never the MSP's).
    /// </summary>
    /// <param name="targetTenantLabel">Display label (domain) of <see cref="McpQuotaDecision.TargetTenantId"/>; falls back to the id.</param>
    /// <param name="exhaustedTenantCount">Set on the aggregate block where EVERY managed tenant is exhausted.</param>
    internal static McpQuotaExceededResponse BuildExceededResponse(
        McpQuotaDecision decision, string? targetTenantLabel = null, int? exhaustedTenantCount = null)
    {
        var resetStamp = $"{decision.ResetUtc:yyyy-MM-ddTHH:mm:ss}Z";
        var reset = $"Resets at {resetStamp}.";
        string message;
        string? targetTenantId = null;

        if (exhaustedTenantCount is int exhausted && decision.Level == McpQuotaLevel.Tenant)
        {
            message = $"MCP request quota exceeded for all {exhausted} managed tenants in scope (each managed tenant's own plan governs its organization windows; upgrading a managed tenant to Pro lifts them). Earliest reset at {resetStamp}.";
        }
        else if (decision.Level == McpQuotaLevel.Tenant && decision.TargetTenantId != null)
        {
            targetTenantId = decision.TargetTenantId;
            var label = string.IsNullOrWhiteSpace(targetTenantLabel) ? decision.TargetTenantId : targetTenantLabel;
            var upgrade = FeatureEntitlementCatalog.IsPermanentProTier(decision.TenantPlan)
                ? string.Empty
                : " That tenant is on the Community plan, which is sized for occasional use; its own plan governs this window, not yours. Upgrading that tenant to Pro lifts its organization windows.";
            message = $"MCP {decision.Scope} request quota of the managed tenant '{label}' exceeded (tenant plan '{decision.TenantPlan}', shared by all its members and delegated admins).{upgrade} {reset}";
        }
        else if (decision.Level == McpQuotaLevel.Tenant)
        {
            var upgrade = FeatureEntitlementCatalog.IsPermanentProTier(decision.TenantPlan)
                ? string.Empty
                : " The Community plan is sized for occasional use; upgrading your organization to Pro lifts its organization windows.";
            message = $"MCP {decision.Scope} request quota of your organization exceeded (tenant plan '{decision.TenantPlan}', shared by all its members).{upgrade} {reset}";
        }
        else
        {
            // Only the Community EDITION plan gets the Pro hint — a per-user override plan (any other name)
            // is already a deliberate individual budget, and "upgrade to Pro" would be wrong advice there.
            var upgrade = string.Equals(decision.Plan, FeatureEntitlementCatalog.CommunityTierName, StringComparison.OrdinalIgnoreCase)
                ? " The Community plan is sized for occasional use; Pro raises your daily and monthly windows."
                : string.Empty;
            message = $"MCP {decision.Scope} request quota exceeded for plan '{decision.Plan}'.{upgrade} {reset}";
        }

        return new McpQuotaExceededResponse
        {
            QuotaExceeded = true,
            Plan = decision.Plan,
            Scope = decision.Scope,
            Level = decision.Level ?? McpQuotaLevel.User,
            Limit = decision.ExceededLimit,
            Used = decision.ExceededUsed,
            ResetUtc = resetStamp,
            Message = message,
            TargetTenantId = targetTenantId,
        };
    }

    /// <summary>Blocked-path only (no hot-path read): the managed tenant's domain for the 429 text, else its id.</summary>
    private async Task<string> ResolveTenantLabelAsync(string tenantId)
    {
        try
        {
            var config = await _tenantConfigService.GetConfigurationIfExistsAsync(tenantId);
            return string.IsNullOrWhiteSpace(config?.DomainName) ? tenantId : config!.DomainName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[McpQuota] Tenant label lookup failed for {TenantId}", LogSanitizer.Clean(tenantId));
            return tenantId;
        }
    }

    private void LogBlocked(string oid, string chargedTenant, McpQuotaDecision decision)
    {
        _logger.LogWarning(
            "[McpQuota] BLOCKED oid={Oid} charged={ChargedTenant} level={Level} scope={Scope} plan={Plan} daily={DailyUsed}/{DailyLimit} monthly={MonthlyUsed}/{MonthlyLimit} tenantDaily={TenantDailyUsed}/{TenantDailyLimit} tenantMonthly={TenantMonthlyUsed}/{TenantMonthlyLimit}",
            oid, LogSanitizer.Clean(chargedTenant), decision.Level, decision.Scope, decision.Plan, decision.DailyUsed, decision.DailyLimit, decision.MonthlyUsed, decision.MonthlyLimit,
            decision.TenantDailyUsed, decision.TenantDailyLimit, decision.TenantMonthlyUsed, decision.TenantMonthlyLimit);
    }

    private static async Task WriteExceededAsync(HttpContext httpContext, McpQuotaDecision decision, McpQuotaExceededResponse body)
    {
        var retryAfterSeconds = Math.Max(1, (int)(decision.ResetUtc - DateTime.UtcNow).TotalSeconds);
        httpContext.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
        httpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        httpContext.Response.ContentType = "application/json";
        await httpContext.Response.WriteAsJsonAsync(body);
    }

    /// <summary>
    /// Fire-and-forget usage increment (same posture as the previous AuthenticationMiddleware
    /// tracking — never blocks or fails the request path). The per-user row (UserUsageLog) is ALWAYS
    /// attributed to the caller's HOME tenant — so a delegated admin's own rows never surface under a
    /// customer's per-user usage views — while the organization counter (McpTenantUsage) is written once
    /// per CHARGED tenant, carrying the caller's home tenant when it differs. Endpoint is normalized and
    /// prefixed with the X-MCP-Tool-Name when the MCP server supplies it.
    /// </summary>
    private void TrackUsage(HttpContext httpContext, string oid, string? upn, string homeTenantId, IReadOnlyCollection<string> chargeTenantIds)
    {
        var normalizedEndpoint = EndpointNormalizer.Normalize(httpContext.Request.Path.Value ?? string.Empty);
        var mcpToolName = httpContext.Request.Headers["X-MCP-Tool-Name"].FirstOrDefault();
        if (!string.IsNullOrEmpty(mcpToolName))
            normalizedEndpoint = $"{mcpToolName}:{normalizedEndpoint}";

        var repo = _userUsageRepo;
        var logger = _logger;
        var upnValue = upn ?? "unknown";
        var charges = chargeTenantIds.Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await repo.IncrementUsageAsync(oid, upnValue, homeTenantId, normalizedEndpoint);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[McpQuota] Failed to record usage: user={UserId}, endpoint={Endpoint}", LogSanitizer.Clean(oid), LogSanitizer.Clean(normalizedEndpoint));
            }

            // Organization-wide counters (the tenant quota's source). Separate try per tenant so a failure on
            // one row never starves the others.
            foreach (var chargeTenantId in charges)
            {
                var isForeign = !string.Equals(chargeTenantId, homeTenantId, StringComparison.OrdinalIgnoreCase);
                try
                {
                    await repo.IncrementTenantUsageAsync(chargeTenantId, oid, upn, isForeign ? homeTenantId : null);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[McpQuota] Failed to record tenant usage: tenant={TenantId}, user={UserId}", LogSanitizer.Clean(chargeTenantId), LogSanitizer.Clean(oid));
                }
            }
        });
    }

    /// <summary>
    /// Quota headers. The tenant windows describe the CHARGED tenant (named in X-MCP-Quota-Tenant-Id); on
    /// the aggregate path no single tenant applies, so only the caller's own windows are stamped.
    /// </summary>
    private static void StampQuotaHeaders(HttpContext httpContext, McpQuotaDecision decision, string? chargeTenantId, bool includeTenantWindows = true)
    {
        // Direct-write pattern — same as UserRateLimitMiddleware's X-RateLimit-* headers.
        httpContext.Response.Headers["X-MCP-Quota-Plan"] = decision.Plan;
        httpContext.Response.Headers["X-MCP-Quota-Daily-Limit"] = decision.DailyLimit.ToString();
        httpContext.Response.Headers["X-MCP-Quota-Monthly-Limit"] = decision.MonthlyLimit.ToString();
        if (decision.DailyUsed >= 0)
        {
            httpContext.Response.Headers["X-MCP-Quota-Daily-Used"] = decision.DailyUsed.ToString();
            httpContext.Response.Headers["X-MCP-Quota-Monthly-Used"] = decision.MonthlyUsed.ToString();
        }

        if (!includeTenantWindows)
            return;

        if (!string.IsNullOrEmpty(chargeTenantId))
            httpContext.Response.Headers["X-MCP-Quota-Tenant-Id"] = chargeTenantId;
        httpContext.Response.Headers["X-MCP-Quota-Tenant-Plan"] = decision.TenantPlan;
        httpContext.Response.Headers["X-MCP-Quota-Tenant-Daily-Limit"] = decision.TenantDailyLimit.ToString();
        httpContext.Response.Headers["X-MCP-Quota-Tenant-Monthly-Limit"] = decision.TenantMonthlyLimit.ToString();
        if (decision.TenantDailyUsed >= 0)
        {
            httpContext.Response.Headers["X-MCP-Quota-Tenant-Daily-Used"] = decision.TenantDailyUsed.ToString();
            httpContext.Response.Headers["X-MCP-Quota-Tenant-Monthly-Used"] = decision.TenantMonthlyUsed.ToString();
        }
    }
}

/// <summary>How an MCP request is charged — see <see cref="McpQuotaEnforcementMiddleware.ResolveChargeScope"/>.</summary>
internal enum McpChargeKind
{
    /// <summary>The caller's home tenant (JWT tid).</summary>
    Home,
    /// <summary>One managed tenant of a delegated (MSP) read.</summary>
    SingleTarget,
    /// <summary>A delegated bounded fleet aggregate: one charge per managed tenant (home-charged ones folded onto home).</summary>
    BoundedAggregate,
}

/// <summary>
/// Pure charge plan for one request. <see cref="ChargeMap"/> (aggregate only) maps each CHARGED tenant to
/// the managed TARGET tenants it pays for — a home-charged group's tenants all map onto the home tenant.
/// </summary>
internal sealed record McpChargeScope(
    McpChargeKind Kind,
    string HomeTenantId,
    string? TargetTenantId,
    IReadOnlyDictionary<string, List<string>>? ChargeMap)
{
    public static McpChargeScope Home(string homeTenantId) => new(McpChargeKind.Home, homeTenantId, null, null);

    /// <summary>The managed target tenants paid for by the given charged tenants (aggregate only).</summary>
    public IReadOnlyList<string> TargetsOf(IEnumerable<string> chargeTenantIds)
    {
        if (ChargeMap == null)
            return Array.Empty<string>();
        var targets = new List<string>();
        foreach (var charge in chargeTenantIds)
        {
            if (ChargeMap.TryGetValue(charge, out var mapped))
                targets.AddRange(mapped);
        }
        return targets;
    }
}
