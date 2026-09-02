using System;
using System.Collections.Generic;
using System.Linq;
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
    /// budget (daily + monthly) that every member's requests count against. Both must be free for a
    /// request to pass; the tenant budget is what makes "just create ten more accounts" pointless.
    ///
    /// User plan precedence: explicit per-user override (McpUsers.UsagePlan — honoured only for the identity
    /// the UPN is bound to, tid + oid, see McpUserService.GetBoundMcpUserAsync) → the caller's home-tenant
    /// edition default (FeatureEntitlementCatalog.McpUsagePlanName). Limits come from the admin-editable
    /// SectionUsagePlans definitions (AdminConfiguration.PlanTierDefinitionsJson); when no definition matches
    /// the plan name, the static catalog fallbacks apply. An override naming a plan that exists nowhere
    /// resolves to the Community fallback (fail-closed).
    ///
    /// Tenant plan: ALWAYS the tenant's edition plan — a per-user override lifts that person's own budget,
    /// never the organization's. A definition without tenant limits falls back to the edition's catalog
    /// tenant limits; an explicit 0 lifts that window.
    ///
    /// Counters: user counters ride on the UserUsageLog table (PK = oid), tenant counters on the
    /// McpTenantUsage table (PK = tenantId, RK = {yyyyMMdd}_{oid} — one partition read per check, no hot
    /// row); both are written fire-and-forget by McpQuotaEnforcementMiddleware for X-Client-Source: mcp
    /// requests. Daily = today's rows, monthly = the sum over the month. The decision is cached per user
    /// for 60 seconds, so the worst-case overshoot is bounded (limit + 60s × request-rate) — the same
    /// posture as the sliding-window rate limiter.
    /// </summary>
    public class McpQuotaService
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

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
        /// Resolves the caller's effective plans + limits and checks the current user AND tenant usage against
        /// them. <paramref name="tenantId"/> is the token's tid — a delegated (MSP) admin therefore draws on
        /// their HOME tenant's budget, never on a managed customer's. Fail-open on counter/storage errors (a
        /// broken quota check must not take down MCP); fail-closed on plan resolution (unknown plan → Community).
        /// </summary>
        public virtual async Task<McpQuotaDecision> CheckAsync(string oid, string? upn, string? tenantId)
        {
            var cacheKey = $"mcp-quota:{oid}";
            if (_cache.TryGetValue<McpQuotaDecision>(cacheKey, out var cached) && cached != null)
                return cached;

            var limits = await ResolvePlanAsync(AdminIdentity.Create(upn, tenantId, oid), tenantId);

            var nowUtc = _time.GetUtcNow().UtcDateTime;
            long dailyUsed = 0, monthlyUsed = 0, tenantDailyUsed = 0, tenantMonthlyUsed = 0;
            try
            {
                var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).ToString("yyyyMMdd");
                var today = nowUtc.ToString("yyyyMMdd");

                var records = await _usageRepo.GetUsageByUserAsync(oid, monthStart, today);
                monthlyUsed = records.Sum(r => r.RequestCount);
                dailyUsed = records.Where(r => r.Date == today).Sum(r => r.RequestCount);

                // The tenant read is skipped when nothing could ever block on it (no tenant, or both windows lifted).
                if (!string.IsNullOrWhiteSpace(tenantId) && (limits.TenantDailyLimit > 0 || limits.TenantMonthlyLimit > 0))
                {
                    var tenantRecords = await _usageRepo.GetTenantUsageAsync(tenantId, monthStart, today);
                    tenantMonthlyUsed = tenantRecords.Sum(r => r.RequestCount);
                    tenantDailyUsed = tenantRecords.Where(r => r.Date == today).Sum(r => r.RequestCount);
                }
            }
            catch (Exception ex)
            {
                // Fail-open: usage counters unavailable → allow. Do NOT cache the fail-open
                // decision — the next request retries the read.
                _logger.LogWarning(ex, "[McpQuota] Usage lookup failed for oid={Oid} — allowing (fail-open)", oid);
                return McpQuotaDecision.FailOpen(limits);
            }

            var decision = BuildDecision(limits, dailyUsed, monthlyUsed, tenantDailyUsed, tenantMonthlyUsed, nowUtc);
            _cache.Set(cacheKey, decision, CacheDuration);
            return decision;
        }

        /// <summary>
        /// Plan resolution only (no counter read) — used by the self-service usage endpoint.
        /// <paramref name="identity"/> is the caller's validated (upn, tid, oid); the per-user override applies
        /// only when that identity IS the one the McpUsers UPN is bound to — a null / unbound identity gets the
        /// tenant edition default. The tenant limits always follow the tenant's edition plan.
        /// </summary>
        public virtual async Task<McpPlanLimits> ResolvePlanAsync(AdminIdentity? identity, string? tenantId)
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

            // 2. Tenant edition default (fail-closed → Community inside the entitlement service).
            var entitlements = await _entitlementService.GetEntitlementsAsync(tenantId);
            var tenantPlan = entitlements.McpUsagePlanName;
            var planName = overridePlan ?? tenantPlan;

            // 3. Limits: admin-edited SectionUsagePlans definitions, else catalog fallbacks.
            var definitions = new List<PlanTierDefinition>();
            try
            {
                var adminConfig = await _adminConfigService.GetConfigurationAsync();
                definitions = PlanTierDefinitionParser.Parse(adminConfig.PlanTierDefinitionsJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[McpQuota] Plan definitions unavailable — using catalog fallback for plan {Plan}", planName);
            }

            // User limits: the definition for the (possibly overridden) plan name, else the catalog fallback
            // for the edition plans, else Community (fail-closed for overrides naming a plan that exists nowhere).
            var userDefinition = Find(definitions, planName);
            var userFallback = FeatureEntitlementCatalog.IsPermanentProTier(planName)
                ? FeatureEntitlementCatalog.Get(TenantEdition.Pro)
                : FeatureEntitlementCatalog.Get(TenantEdition.Community);
            var dailyLimit = userDefinition?.DailyRequestLimit ?? userFallback.McpDailyRequestLimit;
            var monthlyLimit = userDefinition?.MonthlyRequestLimit ?? userFallback.McpMonthlyRequestLimit;

            // Tenant limits: the TENANT plan's definition when it carries tenant limits (null = not set → the
            // edition's catalog tenant limits; an explicit 0 lifts the window), never the per-user override.
            var tenantDefinition = Find(definitions, tenantPlan);
            var tenantDailyLimit = tenantDefinition?.TenantDailyRequestLimit ?? entitlements.McpTenantDailyRequestLimit;
            var tenantMonthlyLimit = tenantDefinition?.TenantMonthlyRequestLimit ?? entitlements.McpTenantMonthlyRequestLimit;

            return new McpPlanLimits(planName, dailyLimit, monthlyLimit, tenantPlan, tenantDailyLimit, tenantMonthlyLimit);
        }

        private static PlanTierDefinition? Find(List<PlanTierDefinition> definitions, string planName)
            => definitions.FirstOrDefault(t => string.Equals(t.Name, planName, StringComparison.OrdinalIgnoreCase));

        internal static McpQuotaDecision BuildDecision(
            McpPlanLimits limits,
            long dailyUsed, long monthlyUsed, long tenantDailyUsed, long tenantMonthlyUsed,
            DateTime nowUtc)
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
                ResetUtc = resetUtc
            };
        }

        private static bool Exceeded(int limit, long used) => limit > 0 && used >= limit;
    }

    /// <summary>
    /// Resolved plan names and limits for one caller: the user's plan (override or tenant edition) with the
    /// user windows, and the tenant's edition plan with the organization-wide windows. 0 = unlimited.
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
        /// <summary>The tenant's edition plan — the organization-wide windows follow it, never the override.</summary>
        public string TenantPlan { get; init; } = string.Empty;
        public int TenantDailyLimit { get; init; }
        public int TenantMonthlyLimit { get; init; }
        public long TenantDailyUsed { get; init; }
        public long TenantMonthlyUsed { get; init; }
        /// <summary>When the exceeded (or daily, when allowed) window resets.</summary>
        public DateTime ResetUtc { get; init; }

        /// <summary>Limit of the exceeded window (0 when allowed).</summary>
        public int ExceededLimit => Level == McpQuotaLevel.Tenant
            ? (Scope == "monthly" ? TenantMonthlyLimit : TenantDailyLimit)
            : (Scope == "monthly" ? MonthlyLimit : DailyLimit);

        /// <summary>Used count of the exceeded window.</summary>
        public long ExceededUsed => Level == McpQuotaLevel.Tenant
            ? (Scope == "monthly" ? TenantMonthlyUsed : TenantDailyUsed)
            : (Scope == "monthly" ? MonthlyUsed : DailyUsed);

        public static McpQuotaDecision FailOpen(McpPlanLimits limits) => new()
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
            ResetUtc = DateTime.MinValue
        };
    }
}
