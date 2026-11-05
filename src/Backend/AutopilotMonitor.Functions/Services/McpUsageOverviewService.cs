using System.Globalization;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;
using static AutopilotMonitor.Functions.Services.McpUsageTelemetryQueries;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Builds the all-tenant MCP usage overview of the Global Admin metrics page (D-416). Two kinds of source, never
/// mixed inside one number: the quota counters (<c>McpTenantUsage</c> for tenants, accounts and days; each
/// account's quota state through <see cref="McpQuotaService.ReadStateAsync"/>, exactly what its next request
/// meets) for every "request" and every window, and the telemetry stores (<see cref="McpUsageTelemetryQueries"/>)
/// for calls, tools, refusals and per-minute peaks. A store that does not answer empties only its own parts.
/// </summary>
public class McpUsageOverviewService
{
    internal const int MaxDays = 90;
    private const int TelemetryBudgetSeconds = 60;
    private const int AccountReadParallelism = 8;

    private readonly IUserUsageRepository _usageRepo;
    private readonly McpQuotaService _quota;
    private readonly GlobalAdminService _globalAdmins;
    private readonly AdminConfigurationService _adminConfig;
    private readonly TenantConfigurationService _tenantConfig;
    private readonly TenantEntitlementService _entitlements;
    private readonly LogQuerySourceCatalog _sources;
    private readonly LogQueryExecutor _executor;
    private readonly ILogger<McpUsageOverviewService> _logger;
    private readonly TimeProvider _time;

    public McpUsageOverviewService(
        IUserUsageRepository usageRepo, McpQuotaService quota, GlobalAdminService globalAdmins,
        AdminConfigurationService adminConfig, TenantConfigurationService tenantConfig, TenantEntitlementService entitlements,
        LogQuerySourceCatalog sources, LogQueryExecutor executor, ILogger<McpUsageOverviewService> logger)
        : this(usageRepo, quota, globalAdmins, adminConfig, tenantConfig, entitlements, sources, executor, logger, TimeProvider.System)
    {
    }

    internal McpUsageOverviewService(
        IUserUsageRepository usageRepo, McpQuotaService quota, GlobalAdminService globalAdmins,
        AdminConfigurationService adminConfig, TenantConfigurationService tenantConfig, TenantEntitlementService entitlements,
        LogQuerySourceCatalog sources, LogQueryExecutor executor, ILogger<McpUsageOverviewService> logger, TimeProvider time)
    {
        _usageRepo = usageRepo;
        _quota = quota;
        _globalAdmins = globalAdmins;
        _adminConfig = adminConfig;
        _tenantConfig = tenantConfig;
        _entitlements = entitlements;
        _sources = sources;
        _executor = executor;
        _logger = logger;
        _time = time;
    }

    public async Task<GetGlobalMcpUsageOverviewResponse> BuildAsync(int days, bool includePlatformAdmins)
    {
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var windowDays = Math.Clamp(days, 1, MaxDays);
        var window = new Window(nowUtc.Date.AddDays(1 - windowDays), nowUtc.Date.AddDays(1), includePlatformAdmins);
        var dates = Dates.For(window, nowUtc);

        var countersTask = _usageRepo.GetAllTenantUsageAsync(dates.ReadFrom, dates.Today);
        var telemetryTask = ReadTelemetryAsync(window);
        await Task.WhenAll(countersTask, telemetryTask);
        var counters = countersTask.Result;
        var telemetry = telemetryTask.Result;

        // Every counter account with use in the window; its quota state and platform role come from the services.
        var principals = PrincipalsByAccount(counters);
        var counterAccounts = counters
            .Where(r => !string.IsNullOrEmpty(r.UserId) && dates.InWindow(r.Date))
            .Select(r => (Tenant: r.TenantId.ToLowerInvariant(), r.UserId))
            .Distinct()
            .ToList();
        var telemetryRoles = LatestRoles(telemetry.Calls);

        var facts = new System.Collections.Concurrent.ConcurrentDictionary<(string Tenant, string Principal), AccountFacts>();
        await Parallel.ForEachAsync(counterAccounts, new ParallelOptions { MaxDegreeOfParallelism = AccountReadParallelism }, async (account, _) =>
        {
            var principal = principals[account];
            var platformRole = await _globalAdmins.GetGlobalRoleAsync(AdminIdentity.Create(principal, account.Tenant, account.UserId));
            var state = await _quota.ReadStateAsync(account.UserId, principal, account.Tenant);
            facts[(account.Tenant, principal)] = new AccountFacts(account.UserId, platformRole, state);
        });

        var tenants = counters.Select(r => r.TenantId.ToLowerInvariant())
            .Concat(telemetry.Calls.Select(r => r.Tenant))
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var tenantLimits = new System.Collections.Concurrent.ConcurrentDictionary<string, McpQuotaService.TenantPlanLimits>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(tenants, new ParallelOptions { MaxDegreeOfParallelism = AccountReadParallelism }, async (tenant, _) =>
            tenantLimits[tenant] = await _quota.ResolveTenantPlanAsync(tenant));

        var minuteLimits = await ResolveMinuteLimitsAsync(telemetry, facts, telemetryRoles);

        return Assemble(new OverviewInputs(
            dates, includePlatformAdmins, counters, facts, tenantLimits, telemetryRoles, minuteLimits, telemetry));
    }

    // ── Telemetry ──────────────────────────────────────────────────────────────────

    private async Task<TelemetryResult> ReadTelemetryAsync(Window window)
    {
        var calls = RunAsync(LogQuerySources.Backend, Calls(window), window, nameof(Calls));
        var routes = RunAsync(LogQuerySources.Backend, Routes(window), window, nameof(Routes));
        var refusals = RunAsync(LogQuerySources.Backend, Refusals(window), window, nameof(Refusals));
        var peaks = RunAsync(LogQuerySources.Backend, Peaks(window), window, nameof(Peaks));
        var quality = RunAsync(LogQuerySources.Mcp, ToolQuality(window), window, nameof(ToolQuality));
        var mcpRefusals = RunAsync(LogQuerySources.Mcp, McpRefusals(window), window, nameof(McpRefusals));
        var serverFacts = RunAsync(LogQuerySources.Mcp, McpServerFacts(window), window, nameof(McpServerFacts));
        await Task.WhenAll(calls, routes, refusals, peaks, quality, mcpRefusals, serverFacts);

        var all = new[] { calls.Result, routes.Result, refusals.Result, peaks.Result, quality.Result, mcpRefusals.Result, serverFacts.Result };
        return new TelemetryResult(
            RequestsAvailable: calls.Result.Table != null,
            McpLogAvailable: quality.Result.Table != null,
            Partial: all.Any(r => r.Partial),
            Calls: calls.Result.Table is { } c ? ParseCalls(c) : new(),
            Routes: routes.Result.Table is { } r ? ParseRoutes(r) : new(),
            Refusals: refusals.Result.Table is { } f ? ParseRefusals(f) : new(),
            Peaks: peaks.Result.Table is { } p ? ParsePeaks(p) : new(),
            ToolQuality: quality.Result.Table is { } q ? ParseToolQuality(q) : new(),
            McpRefusals: mcpRefusals.Result.Table is { } m ? ParseMcpRefusals(m) : new(),
            ServerFacts: serverFacts.Result.Table is { } s ? ParseServerFacts(s) : new ServerFacts(null, null));
    }

    /// <summary>One fixed query; any failure is logged with its cause and leaves the part empty, never the page.</summary>
    private async Task<(KqlTable? Table, bool Partial)> RunAsync(string sourceName, string query, Window window, string label)
    {
        if (!_sources.TryResolve(sourceName, out var source, out var unavailable))
        {
            _logger.LogWarning("[McpUsageOverview] {Query} skipped: {Reason}", label, unavailable);
            return (null, false);
        }

        try
        {
            var outcome = await _executor.ExecuteAsync(source!, query, window.Timespan, TelemetryBudgetSeconds);
            if (!outcome.Succeeded || outcome.Tables!.Count == 0)
            {
                var (message, code) = LogQueryExecutor.ExtractError(outcome.UpstreamBody ?? string.Empty);
                _logger.LogWarning("[McpUsageOverview] {Query} failed on {Source}: {Status} {Code} {Message}",
                    label, sourceName, outcome.UpstreamStatus, code, message);
                return (null, false);
            }
            if (outcome.PartialReason != null)
                _logger.LogWarning("[McpUsageOverview] {Query} on {Source} was cut: {Reason}", label, sourceName, outcome.PartialReason);
            return (outcome.Tables[0], outcome.PartialReason != null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[McpUsageOverview] {Query} failed on {Source}", label, sourceName);
            return (null, false);
        }
    }

    // ── Per-minute limits ──────────────────────────────────────────────────────────

    /// <summary>
    /// The backend per-minute limit each account with a peak or a refusal meets in its HOME tenant — the same
    /// precedence as UserRateLimitMiddleware on the integration surface: the Global Admin budget, else the
    /// tenant override, else the default raised to the edition floor (a Global Reader gets neither override nor
    /// floor). A delegated reader can meet another tenant's override when it reads there; the page shows home.
    /// </summary>
    private async Task<Dictionary<(string Tenant, string Principal), int>> ResolveMinuteLimitsAsync(
        TelemetryResult telemetry, IReadOnlyDictionary<(string Tenant, string Principal), AccountFacts> facts,
        IReadOnlyDictionary<(string Tenant, string Principal), string> telemetryRoles)
    {
        var config = await _adminConfig.GetConfigurationAsync();
        var keys = telemetry.Peaks.Select(p => (p.Tenant, p.Principal))
            .Concat(telemetry.Refusals.Where(r => r.Gate == RefusalMinute).Select(r => (r.Tenant, r.Principal)))
            .Distinct()
            .ToList();

        var limits = new Dictionary<(string Tenant, string Principal), int>();
        var perTenant = new Dictionary<string, (int? Override, int? Floor)>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var role = facts.TryGetValue(key, out var f) && f.PlatformRole != null
                ? f.PlatformRole
                : telemetryRoles.GetValueOrDefault(key);
            var isGlobalAdmin = role == Constants.GlobalRoles.GlobalAdmin;
            var hasGlobalScope = isGlobalAdmin || role == Constants.GlobalRoles.GlobalReader;

            (int? Override, int? Floor) tenantSide = (null, null);
            if (!hasGlobalScope && !string.IsNullOrEmpty(key.Tenant))
            {
                if (!perTenant.TryGetValue(key.Tenant, out tenantSide))
                {
                    var tenantConfig = await _tenantConfig.GetConfigurationIfExistsAsync(key.Tenant);
                    var entitlements = await _entitlements.GetEntitlementsAsync(key.Tenant);
                    tenantSide = (tenantConfig?.CustomUserRateLimitRequestsPerMinute, entitlements.UserRateLimitPerMinute);
                    perTenant[key.Tenant] = tenantSide;
                }
            }

            limits[key] = RateLimitResolver.ResolveUserLimit(
                ThrottleSurface.Integration, isGlobalAdmin, tenantSide.Override,
                config.UserRateLimitRequestsPerMinute, config.PortalUserRateLimitRequestsPerMinute,
                config.GlobalAdminRateLimitRequestsPerMinute, tenantSide.Floor);
        }
        return limits;
    }

    // ── Assembly (pure) ────────────────────────────────────────────────────────────

    /// <summary>
    /// The principal each counter account (lower-cased tenant, object id) stands for: the UPN of its newest row
    /// that carries one, lower-cased like the request telemetry; the object id for an account whose rows all
    /// predate the UPN column.
    /// </summary>
    internal static Dictionary<(string Tenant, string UserId), string> PrincipalsByAccount(IEnumerable<TenantUsageRecord> counters)
        => counters
            .Where(r => !string.IsNullOrEmpty(r.UserId))
            .GroupBy(r => (Tenant: r.TenantId.ToLowerInvariant(), r.UserId))
            .ToDictionary(
                g => g.Key,
                g => (g.OrderByDescending(r => r.Date, StringComparer.Ordinal)
                        .Select(r => r.UserPrincipalName)
                        .FirstOrDefault(u => !string.IsNullOrEmpty(u)) ?? g.Key.UserId).ToLowerInvariant());

    /// <summary>What the I/O part learned about one counter account: its object id, platform role and quota state.</summary>
    internal sealed record AccountFacts(string UserId, string? PlatformRole, McpQuotaDecision Quota);

    internal sealed record TelemetryResult(
        bool RequestsAvailable, bool McpLogAvailable, bool Partial,
        List<CallRow> Calls, List<RouteRow> Routes, List<RefusalRow> Refusals, List<PeakRow> Peaks,
        List<ToolQualityRow> ToolQuality, List<McpRefusalRow> McpRefusals, ServerFacts ServerFacts);

    /// <summary>The window as yyyyMMdd strings (they sort as text).</summary>
    internal sealed record Dates(string From, string Today, string MonthStart)
    {
        /// <summary>One read covers the window and the current month (the quota windows).</summary>
        public string ReadFrom => string.CompareOrdinal(From, MonthStart) < 0 ? From : MonthStart;

        public bool InWindow(string day) => string.CompareOrdinal(day, From) >= 0 && string.CompareOrdinal(day, Today) <= 0;

        public IEnumerable<string> Days()
        {
            var day = DateTime.ParseExact(From, "yyyyMMdd", CultureInfo.InvariantCulture);
            var last = DateTime.ParseExact(Today, "yyyyMMdd", CultureInfo.InvariantCulture);
            for (; day <= last; day = day.AddDays(1))
                yield return day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        public static Dates For(Window window, DateTime nowUtc)
            => new(
                window.FromUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                nowUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
    }

    internal sealed record OverviewInputs(
        Dates Dates,
        bool IncludePlatformAdmins,
        IReadOnlyList<TenantUsageRecord> Counters,
        IReadOnlyDictionary<(string Tenant, string Principal), AccountFacts> Accounts,
        IReadOnlyDictionary<string, McpQuotaService.TenantPlanLimits> TenantLimits,
        IReadOnlyDictionary<(string Tenant, string Principal), string> TelemetryRoles,
        IReadOnlyDictionary<(string Tenant, string Principal), int> MinuteLimits,
        TelemetryResult Telemetry);

    /// <summary>The role of each account's latest call (lower-cased tenant and principal as key).</summary>
    internal static Dictionary<(string Tenant, string Principal), string> LatestRoles(IEnumerable<CallRow> calls)
        => calls
            .Where(c => !string.IsNullOrEmpty(c.Role))
            .GroupBy(c => (c.Tenant, c.Principal))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.LastAt ?? DateTime.MinValue).First().Role);

    private static bool IsPlatformRole(string? role)
        => role == Constants.GlobalRoles.GlobalAdmin || role == Constants.GlobalRoles.GlobalReader;

    /// <summary>
    /// Pure: folds counters, account facts and telemetry into the response. Platform admins drop out of every
    /// number unless included — except a tenant's organization windows, which stay the real counters.
    /// </summary>
    internal static GetGlobalMcpUsageOverviewResponse Assemble(OverviewInputs input)
    {
        var dates = input.Dates;
        var telemetry = input.Telemetry;

        string? RoleOf((string Tenant, string Principal) key)
            => input.TelemetryRoles.TryGetValue(key, out var role) ? role
               : input.Accounts.TryGetValue(key, out var facts) ? facts.PlatformRole
               : null;

        bool IsPlatform((string Tenant, string Principal) key)
            => IsPlatformRole(RoleOf(key))
               || (input.Accounts.TryGetValue(key, out var facts) && IsPlatformRole(facts.PlatformRole));

        bool Counts((string Tenant, string Principal) key) => input.IncludePlatformAdmins || !IsPlatform(key);

        // Counter rows per account key — the same principal per account as the I/O part used for its facts.
        var principals = PrincipalsByAccount(input.Counters);
        var counterRows = input.Counters
            .Where(r => !string.IsNullOrEmpty(r.UserId))
            .Select(r =>
            {
                var tenant = r.TenantId.ToLowerInvariant();
                return (Row: r, Key: (Tenant: tenant, Principal: principals[(tenant, r.UserId)]));
            })
            .ToList();

        var windowRows = counterRows.Where(x => dates.InWindow(x.Row.Date) && Counts(x.Key)).ToList();
        var calls = telemetry.Calls.Where(c => Counts((c.Tenant, c.Principal))).ToList();
        var quotaHits = telemetry.Refusals
            .Where(r => r.Gate == RefusalQuota && Counts((r.Tenant, r.Principal)))
            .OrderByDescending(r => r.LastAt)
            .Select(r => new McpUsageQuotaHitItem
            {
                Date = r.Day,
                TenantId = r.Tenant,
                Principal = string.IsNullOrEmpty(r.Principal) ? null : r.Principal,
                Level = r.Level,
                Scope = r.Scope,
                Plan = r.Plan,
                Limit = r.Limit,
                Used = r.Used,
                Refused = r.Refused,
                FirstAt = r.FirstAt,
                LastAt = r.LastAt,
            })
            .ToList();

        // ── Accounts ──
        var accountKeys = windowRows.Select(x => x.Key)
            .Concat(calls.Select(c => (c.Tenant, c.Principal)))
            .Distinct()
            .ToList();

        var accounts = new Dictionary<(string Tenant, string Principal), McpUsageAccountItem>();
        foreach (var key in accountKeys)
        {
            var rows = windowRows.Where(x => x.Key == key).Select(x => x.Row).ToList();
            var accountCalls = calls.Where(c => (c.Tenant, c.Principal) == key).ToList();
            var byDay = rows.GroupBy(r => r.Date).Select(g => (Day: g.Key, Requests: g.Sum(r => r.RequestCount)))
                .OrderByDescending(d => d.Requests).ThenByDescending(d => d.Day, StringComparer.Ordinal).ToList();
            input.Accounts.TryGetValue(key, out var facts);
            var role = RoleOf(key);
            var lastCounter = rows.Select(r => r.LastRequestAt).Max();
            var lastCall = accountCalls.Select(c => c.LastAt).Max();

            accounts[key] = new McpUsageAccountItem
            {
                UserId = facts?.UserId ?? rows.Select(r => r.UserId).FirstOrDefault(),
                Principal = key.Principal,
                Role = string.IsNullOrEmpty(role) ? null : role,
                IsPlatformAdmin = IsPlatform(key),
                Quota = facts == null ? null : new McpUsageAccountQuotaNode
                {
                    Plan = facts.Quota.Plan,
                    DailyLimit = facts.Quota.DailyLimit,
                    MonthlyLimit = facts.Quota.MonthlyLimit,
                    DailyUsed = facts.Quota.DailyUsed,
                    MonthlyUsed = facts.Quota.MonthlyUsed,
                },
                Calls = accountCalls.Sum(c => c.Calls),
                Requests = rows.Sum(r => r.RequestCount),
                PeakDay = byDay.Count > 0 ? byDay[0].Day : null,
                PeakDayRequests = byDay.Count > 0 ? byDay[0].Requests : null,
                QuotaHits = quotaHits.Count(h => h.Level == McpQuotaLevel.User && h.TenantId == key.Tenant && h.Principal == key.Principal),
                TopTools = accountCalls.GroupBy(c => c.Tool)
                    .Select(g => new McpUsageToolCallsItem { Tool = g.Key, Calls = g.Sum(c => c.Calls) })
                    .OrderByDescending(t => t.Calls).ThenBy(t => t.Tool, StringComparer.Ordinal)
                    .Take(3).ToList(),
                LastRequestAt = Later(lastCounter, lastCall),
            };
        }

        // ── Tenants ──
        var tenants = accounts.Keys.Select(k => k.Tenant).Distinct(StringComparer.Ordinal)
            .Select(tenant =>
            {
                var members = accounts.Where(a => a.Key.Tenant == tenant).Select(a => a.Value)
                    .OrderByDescending(a => a.Calls).ThenByDescending(a => a.Requests)
                    .ThenBy(a => a.Principal, StringComparer.Ordinal).ToList();
                var limits = input.TenantLimits.TryGetValue(tenant, out var l) ? l : new McpQuotaService.TenantPlanLimits(string.Empty, 0, 0);
                // The organization windows are the tenant's real counters — every account, platform admins included.
                var tenantRows = counterRows.Where(x => x.Key.Tenant == tenant).Select(x => x.Row);
                return new McpUsageTenantItem
                {
                    TenantId = tenant,
                    Quota = McpQuotaNodes.BuildOrganizationQuota(tenantRows, limits, dates.Today, dates.MonthStart),
                    Calls = members.Sum(a => a.Calls),
                    Requests = members.Sum(a => a.Requests),
                    QuotaHits = quotaHits.Count(h => h.TenantId == tenant),
                    LastRequestAt = members.Select(a => a.LastRequestAt).Max(),
                    Accounts = members,
                };
            })
            .OrderByDescending(t => t.Calls).ThenByDescending(t => t.Requests).ThenBy(t => t.TenantId, StringComparer.Ordinal)
            .ToList();

        // ── Tools ──
        var routesByTool = telemetry.Routes
            .GroupBy(r => r.Tool)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<McpUsageRouteItem>)g
                .OrderByDescending(r => r.Requests).ThenBy(r => r.Route, StringComparer.Ordinal)
                .Select(r => new McpUsageRouteItem { Route = r.Route, Requests = r.Requests }).ToList());
        var quality = telemetry.ToolQuality.ToDictionary(q => q.Tool, StringComparer.Ordinal);
        var backendTools = calls.GroupBy(c => c.Tool).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var tools = backendTools.Keys.Concat(quality.Keys).Distinct(StringComparer.Ordinal)
            .Where(tool => !string.IsNullOrEmpty(tool))
            .Select(tool =>
            {
                backendTools.TryGetValue(tool, out var rows);
                quality.TryGetValue(tool, out var q);
                var served = rows?.Sum(c => c.ServedCalls) ?? 0;
                return new McpUsageToolItem
                {
                    Tool = tool,
                    // Calls that reached the backend come from the request telemetry, like every account's calls;
                    // a tool the backend never saw is counted from the MCP log.
                    Calls = rows != null ? rows.Sum(c => c.Calls) : q!.Calls,
                    Accounts = rows?.Select(c => (c.Tenant, c.Principal)).Distinct().Count(),
                    Tenants = rows?.Select(c => c.Tenant).Distinct(StringComparer.Ordinal).Count(),
                    RequestsPerCall = served > 0 ? Math.Round((double)rows!.Sum(c => c.Requests) / served, 2) : 0,
                    ErrorRate = q != null && q.Calls > 0 ? Math.Round((double)q.Errors / q.Calls, 4) : null,
                    P95DurationMs = q?.P95Ms,
                    AvgResultChars = q?.AvgChars is double chars ? (long)Math.Round(chars) : null,
                    OverCapCalls = q?.OverCap,
                    Routes = routesByTool.TryGetValue(tool, out var routes) ? routes : Array.Empty<McpUsageRouteItem>(),
                };
            })
            .OrderByDescending(t => t.Calls).ThenBy(t => t.Tool, StringComparer.Ordinal)
            .ToList();

        // ── Days ──
        string PlanOf(string tenant) => input.TenantLimits.TryGetValue(tenant, out var l) && !string.IsNullOrEmpty(l.TenantPlan) ? l.TenantPlan : "unknown";
        var daily = dates.Days().Select(day =>
        {
            var dayCalls = calls.Where(c => c.Day == day).ToList();
            var dayRows = windowRows.Where(x => x.Row.Date == day).ToList();
            return new McpUsageDayItem
            {
                Date = day,
                Calls = dayCalls.Sum(c => c.Calls),
                Requests = dayRows.Sum(x => x.Row.RequestCount),
                Accounts = dayRows.Select(x => x.Key).Concat(dayCalls.Select(c => (c.Tenant, c.Principal))).Distinct().Count(),
                CallsByPlan = dayCalls.GroupBy(c => PlanOf(c.Tenant))
                    .Select(g => new McpUsagePlanCallsItem { Plan = g.Key, Calls = g.Sum(c => c.Calls) })
                    .OrderByDescending(p => p.Calls).ThenBy(p => p.Plan, StringComparer.Ordinal).ToList(),
            };
        }).ToList();

        // ── Per-minute limits ──
        var tenantOfPrincipal = accounts.Keys.GroupBy(k => k.Principal).ToDictionary(g => g.Key, g => g.First().Tenant, StringComparer.Ordinal);
        var mcpRefusals = telemetry.McpRefusals
            .Where(m => !string.IsNullOrEmpty(m.Principal))
            .Select(m => (Key: (Tenant: tenantOfPrincipal.GetValueOrDefault(m.Principal) ?? string.Empty, m.Principal), Row: m))
            .Where(m => Counts(m.Key))
            .ToDictionary(m => m.Key, m => m.Row);
        var backendRefusals = telemetry.Refusals
            .Where(r => r.Gate == RefusalMinute && Counts((r.Tenant, r.Principal)))
            .GroupBy(r => (r.Tenant, r.Principal))
            .ToDictionary(g => g.Key, g => (Refused: g.Sum(r => r.Refused), LastAt: g.Max(r => r.LastAt)));
        var peaks = telemetry.Peaks.Where(p => Counts((p.Tenant, p.Principal)))
            .GroupBy(p => (p.Tenant, p.Principal))
            .ToDictionary(g => g.Key, g => g.ToList());

        var mcpLimit = telemetry.ServerFacts.RateLimitPerMinute;
        var minuteItems = peaks.Keys.Concat(mcpRefusals.Keys).Concat(backendRefusals.Keys).Distinct()
            .Select(key =>
            {
                peaks.TryGetValue(key, out var accountPeaks);
                var callPeak = accountPeaks?.FirstOrDefault(p => p.Metric == PeakCalls);
                var requestPeak = accountPeaks?.FirstOrDefault(p => p.Metric == PeakRequests);
                mcpRefusals.TryGetValue(key, out var mcpRefused);
                var hasBackendRefusal = backendRefusals.TryGetValue(key, out var backendRefused);
                return new McpUsageMinuteItem
                {
                    TenantId = key.Tenant,
                    Principal = key.Principal,
                    PeakCalls = callPeak?.Peak ?? 0,
                    PeakCallsAt = callPeak?.PeakAt,
                    McpRefused = mcpRefused?.Refused ?? 0,
                    McpLastRefusedAt = mcpRefused?.LastAt,
                    PeakRequests = requestPeak?.Peak ?? 0,
                    PeakRequestsAt = requestPeak?.PeakAt,
                    BackendLimit = input.MinuteLimits.GetValueOrDefault(key),
                    BackendRefused = hasBackendRefusal ? backendRefused.Refused : 0,
                    BackendLastRefusedAt = hasBackendRefusal ? backendRefused.LastAt : null,
                };
            })
            .OrderByDescending(m => Math.Max(
                mcpLimit is int limit && limit > 0 ? (double)m.PeakCalls / limit : 0,
                m.BackendLimit > 0 ? (double)m.PeakRequests / m.BackendLimit : 0))
            .ThenBy(m => m.Principal, StringComparer.Ordinal)
            .ToList();

        var mcpLogFrom = telemetry.ServerFacts.Oldest is DateTime oldest
            ? oldest.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : null;

        return new GetGlobalMcpUsageOverviewResponse
        {
            DateFrom = dates.From,
            DateTo = dates.Today,
            IncludesPlatformAdmins = input.IncludePlatformAdmins,
            Telemetry = new McpUsageTelemetryStatus
            {
                RequestsAvailable = telemetry.RequestsAvailable,
                McpLogAvailable = telemetry.McpLogAvailable,
                McpLogFrom = mcpLogFrom != null && string.CompareOrdinal(mcpLogFrom, dates.From) > 0 ? mcpLogFrom : null,
                Partial = telemetry.Partial ? true : null,
            },
            Daily = daily,
            Tenants = tenants,
            Tools = tools,
            QuotaHits = quotaHits,
            MinuteLimits = new McpUsageMinuteLimits { McpServerLimit = mcpLimit, Accounts = minuteItems },
        };
    }

    private static DateTime? Later(DateTime? a, DateTime? b) => a == null ? b : b == null ? a : (a > b ? a : b);
}
