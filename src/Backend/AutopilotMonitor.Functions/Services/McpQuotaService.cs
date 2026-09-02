using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Config;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Resolves and enforces the MCP request quota: a per-USER budget (daily + monthly) AND a per-TENANT
    /// budget (daily + monthly) that every request charged to that tenant counts against. Both must be free
    /// for a request to pass; the tenant budget is what makes "just create ten more accounts" pointless.
    ///
    /// User plan precedence: explicit per-user override (McpUsers.UsagePlan — honoured only for the identity
    /// the UPN is bound to, tid + oid, see McpUserService.GetBoundMcpUserAsync) → the caller's HOME-tenant
    /// edition default (FeatureEntitlementCatalog.McpUsagePlanName). Limits come from the admin-editable
    /// SectionUsagePlans definitions (AdminConfiguration.PlanTierDefinitionsJson); when no definition matches
    /// the plan name, the static catalog fallbacks apply. An override naming a plan that exists nowhere
    /// resolves to the Community fallback (fail-closed).
    ///
    /// Tenant plan: ALWAYS the CHARGED tenant's edition plan — a per-user override lifts that person's own
    /// budget, never an organization's. The charged tenant is the caller's own tenant, or, for a delegated
    /// (MSP) read, the MANAGED tenant whose data is read ("the budget follows the data": a Community customer
    /// is read with Community windows even by a Pro MSP). A definition without tenant limits falls back to
    /// the edition's catalog tenant limits; an explicit 0 lifts that window.
    ///
    /// Counters: user counters ride on the UserUsageLog table (PK = oid), tenant counters on the
    /// McpTenantUsage table (PK = charged tenantId, RK = {yyyyMMdd}_{oid} — one partition read per check, no
    /// hot row); both are written fire-and-forget by McpQuotaEnforcementMiddleware for X-Client-Source: mcp
    /// requests. Daily = today's rows, monthly = the sum over the month. The usage SNAPSHOTS are cached for
    /// 60 seconds — per user (oid) and per charged tenant — so the worst-case overshoot is bounded
    /// (limit + 60s × request-rate) — the same posture as the sliding-window rate limiter. Limits are
    /// re-resolved per check from services that carry their own caches, so a plan change applies at once.
    /// </summary>
    public class McpQuotaService
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
        /// <summary>Concurrent tenant-snapshot reads for one bounded (fleet) aggregate check.</summary>
        private const int AggregateParallelism = 8;

        internal static string UserCacheKey(string oid) => $"mcp-quota:user:{oid}";
        internal static string TenantCacheKey(string tenantId) => $"mcp-quota:tenant:{tenantId.ToLowerInvariant()}";

        private readonly IUserUsageRepository _usageRepo;
        private readonly McpUserService _mcpUserService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly TenantEntitlementService _entitlementService;
        private readonly IMemoryCache _cache;
        private readonly ILogger<McpQuotaService> _logger;
        private readonly TimeProvider _time;

        public McpQuotaService(
            IUserUsageRepository usageRepo,
            McpUserService mcpUserService,
            AdminConfigurationService adminConfigService,
            TenantEntitlementService entitlementService,
            IMemoryCache cache,
            ILogger<McpQuotaService> logger)
            : this(usageRepo, mcpUserService, adminConfigService, entitlementService, cache, logger, TimeProvider.System)
        {
        }

        /// <summary>Test seam — inject a fake <see cref="TimeProvider"/> for deterministic window math.</summary>
        public McpQuotaService(
            IUserUsageRepository usageRepo,
            McpUserService mcpUserService,
            AdminConfigurationService adminConfigService,
            TenantEntitlementService entitlementService,
            IMemoryCache cache,
            ILogger<McpQuotaService> logger,
            TimeProvider time)
        {
            _usageRepo = usageRepo;
            _mcpUserService = mcpUserService;
            _adminConfigService = adminConfigService;
            _entitlementService = entitlementService;
            _cache = cache;
            _logger = logger;
            _time = time;
        }

        /// <summary>
        /// Checks the caller against their own budget and their OWN tenant's organization budget
        /// (<paramref name="tenantId"/> = the token's tid, charged and homed alike). The self-service usage
        /// endpoint and every non-delegated MCP request use this shape.
        /// </summary>
        public virtual Task<McpQuotaDecision> CheckAsync(string oid, string? upn, string? tenantId)
            => CheckAsync(oid, upn, tenantId, tenantId);

        /// <summary>
        /// Resolves the caller's effective plans + limits and checks the current user AND charged-tenant usage
        /// against them. <paramref name="homeTenantId"/> is the token's tid (the identity the per-user override
        /// is bound to, and the edition the user's own plan follows); <paramref name="chargeTenantId"/> is the
        /// tenant whose organization budget this request draws on — the home tenant, or the MANAGED tenant of a
        /// delegated (MSP) read. Fail-open on counter/storage errors (a broken quota check must not take down
        /// MCP); fail-closed on plan resolution (unknown plan → Community).
        /// </summary>
        public virtual async Task<McpQuotaDecision> CheckAsync(string oid, string? upn, string? homeTenantId, string? chargeTenantId)
        {
            var nowUtc = _time.GetUtcNow().UtcDateTime;
            var limits = await ResolvePlanAsync(AdminIdentity.Create(upn, homeTenantId, oid), homeTenantId, chargeTenantId);
            var targetTenantId = TargetOf(homeTenantId, chargeTenantId);

            var user = await ReadUserUsageAsync(oid, nowUtc);
            if (user == null)
                return McpQuotaDecision.FailOpen(limits, targetTenantId);

            var tenant = await ReadTenantUsageAsync(chargeTenantId, limits, nowUtc);
            if (tenant == null)
                return McpQuotaDecision.FailOpen(limits, targetTenantId);

            return BuildDecision(limits, user.DailyUsed, user.MonthlyUsed, tenant.DailyUsed, tenant.MonthlyUsed, nowUtc, targetTenantId);
        }

        /// <summary>
        /// Bounded (fleet) aggregate check for a delegated (MSP) caller: the caller's own budget once, then
        /// every charged tenant's organization budget. A tenant whose budget is exhausted is EXCLUDED (soft —
        /// the aggregate proceeds over the rest); the result is blocked only when the caller's own budget is
        /// exhausted or when every charged tenant is. A tenant whose counters cannot be read is admitted
        /// (fail-open per tenant, nothing cached). Reads run with bounded parallelism.
        /// </summary>
        public virtual async Task<McpAggregateQuotaResult> CheckManyAsync(
            string oid, string? upn, string? homeTenantId, IReadOnlyCollection<string> chargeTenantIds)
        {
            var nowUtc = _time.GetUtcNow().UtcDateTime;
            var identity = AdminIdentity.Create(upn, homeTenantId, oid);
            var (userLimits, definitions) = await ResolveUserLimitsAsync(identity, homeTenantId);
            var userOnly = Compose(userLimits, new TenantPlanLimits(userLimits.PlanName, 0, 0));

            var user = await ReadUserUsageAsync(oid, nowUtc);
            var userDecision = user == null
                ? McpQuotaDecision.FailOpen(userOnly)
                : BuildDecision(userOnly, user.DailyUsed, user.MonthlyUsed, 0, 0, nowUtc);
            if (!userDecision.Allowed)
            {
                return new McpAggregateQuotaResult
                {
                    Allowed = false,
                    UserDecision = userDecision,
                    BlockingDecision = userDecision,
                    ExcludedTenantIds = chargeTenantIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                };
            }

            var decisions = new ConcurrentDictionary<string, McpQuotaDecision>(StringComparer.OrdinalIgnoreCase);
            using var gate = new SemaphoreSlim(AggregateParallelism);
            await Task.WhenAll(chargeTenantIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(async tenantId =>
            {
                await gate.WaitAsync();
                try
                {
                    var limits = Compose(userLimits, await ResolveTenantLimitsAsync(tenantId, definitions));
                    var targetTenantId = TargetOf(homeTenantId, tenantId);
                    var tenant = await ReadTenantUsageAsync(tenantId, limits, nowUtc);
                    decisions[tenantId] = tenant == null
                        ? McpQuotaDecision.FailOpen(limits, targetTenantId)
                        : BuildDecision(limits, user?.DailyUsed ?? -1, user?.MonthlyUsed ?? -1,
                            tenant.DailyUsed, tenant.MonthlyUsed, nowUtc, targetTenantId);
                }
                finally
                {
                    gate.Release();
                }
            }));

            var admitted = decisions.Where(kv => kv.Value.Allowed).Select(kv => kv.Key).ToList();
            var excluded = decisions.Where(kv => !kv.Value.Allowed).Select(kv => kv.Key).ToList();
            var allExhausted = admitted.Count == 0 && excluded.Count > 0;

            return new McpAggregateQuotaResult
            {
                Allowed = !allExhausted,
                UserDecision = userDecision,
                // Retry-After for "everything is exhausted" is the moment at least ONE tenant becomes usable again.
                BlockingDecision = allExhausted ? excluded.Select(t => decisions[t]).MinBy(d => d.ResetUtc) : null,
                AdmittedTenantIds = admitted,
                ExcludedTenantIds = excluded,
            };
        }

        /// <summary>
        /// Plan resolution only (no counter read) for a caller whose charged tenant IS their home tenant —
        /// used by the self-service usage endpoint. See the four-argument overload.
        /// </summary>
        public virtual Task<McpPlanLimits> ResolvePlanAsync(AdminIdentity? identity, string? tenantId)
            => ResolvePlanAsync(identity, tenantId, tenantId);

        /// <summary>
        /// Plan resolution only (no counter read). <paramref name="identity"/> is the caller's validated
        /// (upn, tid, oid); the per-user override applies only when that identity IS the one the McpUsers UPN
        /// is bound to — a null / unbound identity gets the home tenant's edition default. The user windows
        /// follow <paramref name="homeTenantId"/>'s edition; the tenant windows ALWAYS follow the CHARGED
        /// tenant's edition plan (<paramref name="chargeTenantId"/>).
        /// </summary>
        public virtual async Task<McpPlanLimits> ResolvePlanAsync(AdminIdentity? identity, string? homeTenantId, string? chargeTenantId)
        {
            var (userLimits, definitions) = await ResolveUserLimitsAsync(identity, homeTenantId);
            return Compose(userLimits, await ResolveTenantLimitsAsync(chargeTenantId, definitions));
        }

        private async Task<(UserPlanLimits Limits, List<PlanTierDefinition> Definitions)> ResolveUserLimitsAsync(
            AdminIdentity? identity, string? homeTenantId)
        {
            // 1. Per-user override wins when set — for the BOUND identity only.
            string? overridePlan = null;
            if (identity != null)
            {
                try
                {
                    var mcpUser = await _mcpUserService.GetBoundMcpUserAsync(identity);
                    overridePlan = string.IsNullOrWhiteSpace(mcpUser?.UsagePlan) ? null : mcpUser!.UsagePlan!.Trim().ToLowerInvariant();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[McpQuota] McpUser lookup failed for {Upn} — falling back to tenant edition plan", identity.Upn);
                }
            }

            // 2. Home-tenant plan: the tenant-wide GA override (TenantConfiguration.McpUsagePlanOverride) when
            //    set, else the edition default (fail-closed → Community inside the entitlement service).
            var planName = overridePlan ?? await _entitlementService.GetMcpUsagePlanNameAsync(homeTenantId);

            // 3. Limits: admin-edited SectionUsagePlans definitions, else catalog fallbacks.
            var definitions = await LoadDefinitionsAsync(planName);

            // User limits: the definition for the (possibly overridden) plan name, else the catalog fallback
            // for the edition plans, else Community (fail-closed for overrides naming a plan that exists nowhere).
            var userDefinition = Find(definitions, planName);
            var userFallback = FeatureEntitlementCatalog.IsPermanentProTier(planName)
                ? FeatureEntitlementCatalog.Get(TenantEdition.Pro)
                : FeatureEntitlementCatalog.Get(TenantEdition.Community);
            var dailyLimit = userDefinition?.DailyRequestLimit ?? userFallback.McpDailyRequestLimit;
            var monthlyLimit = userDefinition?.MonthlyRequestLimit ?? userFallback.McpMonthlyRequestLimit;

            return (new UserPlanLimits(planName, dailyLimit, monthlyLimit), definitions);
        }

        /// <summary>
        /// The CHARGED tenant's organization windows: its tenant plan (the tenant-wide GA override, else the
        /// edition plan) definition when that carries tenant limits (null = not set → the edition's catalog
        /// tenant limits; an explicit 0 lifts the window), never a per-user override.
        /// </summary>
        private async Task<TenantPlanLimits> ResolveTenantLimitsAsync(string? chargeTenantId, List<PlanTierDefinition> definitions)
        {
            var entitlements = await _entitlementService.GetEntitlementsAsync(chargeTenantId);
            var tenantPlan = await _entitlementService.GetMcpUsagePlanNameAsync(chargeTenantId);
            var tenantDefinition = Find(definitions, tenantPlan);
            return new TenantPlanLimits(
                tenantPlan,
                tenantDefinition?.TenantDailyRequestLimit ?? entitlements.McpTenantDailyRequestLimit,
                tenantDefinition?.TenantMonthlyRequestLimit ?? entitlements.McpTenantMonthlyRequestLimit);
        }

        private async Task<List<PlanTierDefinition>> LoadDefinitionsAsync(string planName)
        {
            try
            {
                var adminConfig = await _adminConfigService.GetConfigurationAsync();
                return PlanTierDefinitionParser.Parse(adminConfig.PlanTierDefinitionsJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[McpQuota] Plan definitions unavailable — using catalog fallback for plan {Plan}", planName);
                return new List<PlanTierDefinition>();
            }
        }

        /// <summary>The caller's own counters (cached snapshot). Null = read failed (fail-open, nothing cached).</summary>
        private async Task<UsageSnapshot?> ReadUserUsageAsync(string oid, DateTime nowUtc)
        {
            var cacheKey = UserCacheKey(oid);
            if (_cache.TryGetValue<UsageSnapshot>(cacheKey, out var cached) && cached != null)
                return cached;

            try
            {
                var (monthStart, today) = Window(nowUtc);
                var records = await _usageRepo.GetUsageByUserAsync(oid, monthStart, today);
                var snapshot = new UsageSnapshot(
                    records.Where(r => r.Date == today).Sum(r => r.RequestCount),
                    records.Sum(r => r.RequestCount));
                _cache.Set(cacheKey, snapshot, CacheDuration);
                return snapshot;
            }
            catch (Exception ex)
            {
                // Fail-open: usage counters unavailable → allow. Do NOT cache — the next request retries the read.
                _logger.LogWarning(ex, "[McpQuota] Usage lookup failed for oid={Oid} — allowing (fail-open)", oid);
                return null;
            }
        }

        /// <summary>
        /// The charged tenant's organization counters (cached snapshot, shared by every caller charged to that
        /// tenant). Skipped — zero counters, no read, nothing cached — when nothing could ever block on it (no
        /// tenant, or both windows lifted). Null = read failed (fail-open, nothing cached).
        /// </summary>
        private async Task<UsageSnapshot?> ReadTenantUsageAsync(string? chargeTenantId, McpPlanLimits limits, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(chargeTenantId) || (limits.TenantDailyLimit <= 0 && limits.TenantMonthlyLimit <= 0))
                return UsageSnapshot.Zero;

            var cacheKey = TenantCacheKey(chargeTenantId);
            if (_cache.TryGetValue<UsageSnapshot>(cacheKey, out var cached) && cached != null)
                return cached;

            try
            {
                var (monthStart, today) = Window(nowUtc);
                var records = await _usageRepo.GetTenantUsageAsync(chargeTenantId, monthStart, today);
                var snapshot = new UsageSnapshot(
                    records.Where(r => r.Date == today).Sum(r => r.RequestCount),
                    records.Sum(r => r.RequestCount));
                _cache.Set(cacheKey, snapshot, CacheDuration);
                return snapshot;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[McpQuota] Tenant usage lookup failed for tenant={TenantId} — allowing (fail-open)", chargeTenantId);
                return null;
            }
        }

        private static (string MonthStart, string Today) Window(DateTime nowUtc)
            => (new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).ToString("yyyyMMdd"),
                nowUtc.ToString("yyyyMMdd"));

        /// <summary>The managed tenant a decision names — null when the charged tenant IS the home tenant.</summary>
        private static string? TargetOf(string? homeTenantId, string? chargeTenantId)
            => !string.IsNullOrWhiteSpace(chargeTenantId)
               && !string.Equals(chargeTenantId, homeTenantId, StringComparison.OrdinalIgnoreCase)
                ? chargeTenantId
                : null;

        private static McpPlanLimits Compose(UserPlanLimits user, TenantPlanLimits tenant)
            => new(user.PlanName, user.DailyLimit, user.MonthlyLimit, tenant.TenantPlan, tenant.TenantDailyLimit, tenant.TenantMonthlyLimit);

        private static PlanTierDefinition? Find(List<PlanTierDefinition> definitions, string planName)
            => definitions.FirstOrDefault(t => string.Equals(t.Name, planName, StringComparison.OrdinalIgnoreCase));

        internal static McpQuotaDecision BuildDecision(
            McpPlanLimits limits,
            long dailyUsed, long monthlyUsed, long tenantDailyUsed, long tenantMonthlyUsed,
            DateTime nowUtc,
            string? targetTenantId = null)
        {
            // Daily windows reset at midnight UTC; monthly on the 1st. 0/negative limit = unlimited
            // for that window (an operator can deliberately lift a window via SectionUsagePlans).
            // Report the LONGEST exceeded window first so Retry-After is honest; within a window the
            // caller's own budget is named before the organization's.
            string? scope = null, level = null;
            if (Exceeded(limits.MonthlyLimit, monthlyUsed)) (scope, level) = ("monthly", McpQuotaLevel.User);
            else if (Exceeded(limits.TenantMonthlyLimit, tenantMonthlyUsed)) (scope, level) = ("monthly", McpQuotaLevel.Tenant);
            else if (Exceeded(limits.DailyLimit, dailyUsed)) (scope, level) = ("daily", McpQuotaLevel.User);
            else if (Exceeded(limits.TenantDailyLimit, tenantDailyUsed)) (scope, level) = ("daily", McpQuotaLevel.Tenant);

            var resetUtc = scope == "monthly"
                ? new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1)
                : nowUtc.Date.AddDays(1);

            return new McpQuotaDecision
            {
                Allowed = scope == null,
                Plan = limits.PlanName,
                Scope = scope,
                Level = level,
                DailyLimit = limits.DailyLimit,
                MonthlyLimit = limits.MonthlyLimit,
                DailyUsed = dailyUsed,
                MonthlyUsed = monthlyUsed,
                TenantPlan = limits.TenantPlan,
                TenantDailyLimit = limits.TenantDailyLimit,
                TenantMonthlyLimit = limits.TenantMonthlyLimit,
                TenantDailyUsed = tenantDailyUsed,
                TenantMonthlyUsed = tenantMonthlyUsed,
                ResetUtc = resetUtc,
                TargetTenantId = targetTenantId,
            };
        }

        private static bool Exceeded(int limit, long used) => limit > 0 && used >= limit;

        /// <summary>Cached counter pair (today / this month) for one user or one charged tenant.</summary>
        private sealed record UsageSnapshot(long DailyUsed, long MonthlyUsed)
        {
            public static readonly UsageSnapshot Zero = new(0, 0);
        }

        private sealed record UserPlanLimits(string PlanName, int DailyLimit, int MonthlyLimit);
        private sealed record TenantPlanLimits(string TenantPlan, int TenantDailyLimit, int TenantMonthlyLimit);
    }

    /// <summary>
    /// Resolved plan names and limits for one caller against one charged tenant: the user's plan (override
    /// or home edition) with the user windows, and the charged tenant's edition plan with the
    /// organization-wide windows. 0 = unlimited.
    /// </summary>
    public sealed record McpPlanLimits(
        string PlanName, int DailyLimit, int MonthlyLimit,
        string TenantPlan, int TenantDailyLimit, int TenantMonthlyLimit);

    /// <summary>Whose budget a blocked decision names — wire vocabulary of <c>level</c>.</summary>
    public static class McpQuotaLevel
    {
        public const string User = "user";
        public const string Tenant = "tenant";
    }

    /// <summary>Outcome of an MCP quota check.</summary>
    public sealed class McpQuotaDecision
    {
        public bool Allowed { get; init; }
        public string Plan { get; init; } = string.Empty;
        /// <summary>Which window was exceeded ("daily"/"monthly"), null when allowed.</summary>
        public string? Scope { get; init; }
        /// <summary>Whose budget was exceeded (<see cref="McpQuotaLevel"/>), null when allowed.</summary>
        public string? Level { get; init; }
        public int DailyLimit { get; init; }
        public int MonthlyLimit { get; init; }
        public long DailyUsed { get; init; }
        public long MonthlyUsed { get; init; }
        /// <summary>The CHARGED tenant's edition plan — the organization-wide windows follow it, never the override.</summary>
        public string TenantPlan { get; init; } = string.Empty;
        public int TenantDailyLimit { get; init; }
        public int TenantMonthlyLimit { get; init; }
        public long TenantDailyUsed { get; init; }
        public long TenantMonthlyUsed { get; init; }
        /// <summary>When the exceeded (or daily, when allowed) window resets.</summary>
        public DateTime ResetUtc { get; init; }
        /// <summary>
        /// The MANAGED tenant whose organization windows this decision reflects (a delegated read charged to
        /// the managed tenant); null when the charged tenant is the caller's own home tenant.
        /// </summary>
        public string? TargetTenantId { get; init; }

        /// <summary>Limit of the exceeded window (0 when allowed).</summary>
        public int ExceededLimit => Level == McpQuotaLevel.Tenant
            ? (Scope == "monthly" ? TenantMonthlyLimit : TenantDailyLimit)
            : (Scope == "monthly" ? MonthlyLimit : DailyLimit);

        /// <summary>Used count of the exceeded window.</summary>
        public long ExceededUsed => Level == McpQuotaLevel.Tenant
            ? (Scope == "monthly" ? TenantMonthlyUsed : TenantDailyUsed)
            : (Scope == "monthly" ? MonthlyUsed : DailyUsed);

        public static McpQuotaDecision FailOpen(McpPlanLimits limits, string? targetTenantId = null) => new()
        {
            Allowed = true,
            Plan = limits.PlanName,
            DailyLimit = limits.DailyLimit,
            MonthlyLimit = limits.MonthlyLimit,
            DailyUsed = -1,
            MonthlyUsed = -1,
            TenantPlan = limits.TenantPlan,
            TenantDailyLimit = limits.TenantDailyLimit,
            TenantMonthlyLimit = limits.TenantMonthlyLimit,
            TenantDailyUsed = -1,
            TenantMonthlyUsed = -1,
            ResetUtc = DateTime.MinValue,
            TargetTenantId = targetTenantId,
        };
    }

    /// <summary>
    /// Outcome of a bounded (fleet) aggregate check: which charged tenants may be served and which are
    /// dropped because their organization budget is exhausted. <see cref="Allowed"/> is false only when the
    /// caller's own budget is exhausted (then <see cref="BlockingDecision"/> is user-level and every tenant is
    /// excluded) or when EVERY charged tenant is exhausted (then it is the excluded decision with the earliest
    /// reset).
    /// </summary>
    public sealed class McpAggregateQuotaResult
    {
        public bool Allowed { get; init; }
        /// <summary>The caller's own windows (tenant windows zero) — for the response headers.</summary>
        public McpQuotaDecision UserDecision { get; init; } = default!;
        /// <summary>Set when <see cref="Allowed"/> is false.</summary>
        public McpQuotaDecision? BlockingDecision { get; init; }
        public IReadOnlyList<string> AdmittedTenantIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ExcludedTenantIds { get; init; } = Array.Empty<string>();
    }
}
