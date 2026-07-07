using System;
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
    /// Resolves and enforces the per-user MCP request quota (daily + monthly).
    ///
    /// Plan precedence: explicit per-user override (McpUsers.UsagePlan) → the caller's home-tenant
    /// edition default (FeatureEntitlementCatalog.McpUsagePlanName). Limits come from the
    /// admin-editable SectionUsagePlans definitions (AdminConfiguration.PlanTierDefinitionsJson);
    /// when no definition matches the plan name, the static catalog fallbacks apply. An override
    /// naming a plan that exists nowhere resolves to the Community fallback (fail-closed).
    ///
    /// Counters ride on the existing UserUsage table (written fire-and-forget by
    /// AuthenticationMiddleware for X-Client-Source: mcp requests): one partition-bounded query
    /// per check covering the current month; daily = today's rows, monthly = the sum. The
    /// decision is cached per instance for 60 seconds, so the worst-case overshoot is bounded
    /// (limit + 60s × request-rate) — the same posture as the sliding-window rate limiter.
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
        /// Resolves the caller's effective plan + limits and checks the current usage against them.
        /// Fail-open on counter/storage errors (a broken quota check must not take down MCP);
        /// fail-closed on plan resolution (unknown plan → Community limits).
        /// </summary>
        public virtual async Task<McpQuotaDecision> CheckAsync(string oid, string? upn, string? tenantId)
        {
            var cacheKey = $"mcp-quota:{oid}";
            if (_cache.TryGetValue<McpQuotaDecision>(cacheKey, out var cached) && cached != null)
                return cached;

            var (planName, dailyLimit, monthlyLimit) = await ResolvePlanAsync(upn, tenantId);

            var nowUtc = _time.GetUtcNow().UtcDateTime;
            long dailyUsed = 0, monthlyUsed = 0;
            try
            {
                var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var today = nowUtc.ToString("yyyyMMdd");
                var records = await _usageRepo.GetUsageByUserAsync(
                    oid, monthStart.ToString("yyyyMMdd"), today);

                monthlyUsed = records.Sum(r => r.RequestCount);
                dailyUsed = records.Where(r => r.Date == today).Sum(r => r.RequestCount);
            }
            catch (Exception ex)
            {
                // Fail-open: usage counters unavailable → allow. Do NOT cache the fail-open
                // decision — the next request retries the read.
                _logger.LogWarning(ex, "[McpQuota] Usage lookup failed for oid={Oid} — allowing (fail-open)", oid);
                return McpQuotaDecision.FailOpen(planName, dailyLimit, monthlyLimit);
            }

            var decision = BuildDecision(planName, dailyLimit, monthlyLimit, dailyUsed, monthlyUsed, nowUtc);
            _cache.Set(cacheKey, decision, CacheDuration);
            return decision;
        }

        /// <summary>
        /// Plan resolution only (no counter read) — used by the self-service usage endpoint.
        /// </summary>
        public virtual async Task<(string PlanName, int DailyLimit, int MonthlyLimit)> ResolvePlanAsync(string? upn, string? tenantId)
        {
            // 1. Per-user override wins when set.
            string? overridePlan = null;
            if (!string.IsNullOrWhiteSpace(upn))
            {
                try
                {
                    var mcpUser = await _mcpUserService.GetMcpUserAsync(upn.ToLowerInvariant());
                    overridePlan = string.IsNullOrWhiteSpace(mcpUser?.UsagePlan) ? null : mcpUser!.UsagePlan!.Trim().ToLowerInvariant();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[McpQuota] McpUser lookup failed for {Upn} — falling back to tenant edition plan", upn);
                }
            }

            // 2. Tenant edition default (fail-closed → Community inside the entitlement service).
            var entitlements = await _entitlementService.GetEntitlementsAsync(tenantId);
            var planName = overridePlan ?? entitlements.McpUsagePlanName;

            // 3. Limits: admin-edited SectionUsagePlans definition for that name, else catalog
            //    fallback for the edition plans, else Community fallback (fail-closed for
            //    overrides naming a plan that exists nowhere).
            try
            {
                var adminConfig = await _adminConfigService.GetConfigurationAsync();
                var definition = PlanTierDefinitionParser.Parse(adminConfig.PlanTierDefinitionsJson)
                    .FirstOrDefault(t => string.Equals(t.Name, planName, StringComparison.OrdinalIgnoreCase));
                if (definition != null)
                    return (planName, definition.DailyRequestLimit, definition.MonthlyRequestLimit);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[McpQuota] Plan definitions unavailable — using catalog fallback for plan {Plan}", planName);
            }

            var fallback = string.Equals(planName, FeatureEntitlementCatalog.EnterpriseTierName, StringComparison.OrdinalIgnoreCase)
                ? FeatureEntitlementCatalog.Get(TenantEdition.Enterprise)
                : FeatureEntitlementCatalog.Get(TenantEdition.Community);
            return (planName, fallback.McpDailyRequestLimit, fallback.McpMonthlyRequestLimit);
        }

        internal static McpQuotaDecision BuildDecision(
            string planName, int dailyLimit, int monthlyLimit, long dailyUsed, long monthlyUsed, DateTime nowUtc)
        {
            // Daily window resets at midnight UTC; monthly on the 1st. 0/negative limit = unlimited
            // for that scope (an operator can deliberately lift a scope via SectionUsagePlans).
            var dailyExceeded = dailyLimit > 0 && dailyUsed >= dailyLimit;
            var monthlyExceeded = monthlyLimit > 0 && monthlyUsed >= monthlyLimit;

            string? exceededScope = monthlyExceeded ? "monthly" : dailyExceeded ? "daily" : null;
            var resetUtc = exceededScope == "monthly"
                ? new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1)
                : nowUtc.Date.AddDays(1);

            return new McpQuotaDecision
            {
                Allowed = exceededScope == null,
                Plan = planName,
                Scope = exceededScope,
                DailyLimit = dailyLimit,
                MonthlyLimit = monthlyLimit,
                DailyUsed = dailyUsed,
                MonthlyUsed = monthlyUsed,
                ResetUtc = resetUtc
            };
        }
    }

    /// <summary>Outcome of an MCP quota check.</summary>
    public sealed class McpQuotaDecision
    {
        public bool Allowed { get; init; }
        public string Plan { get; init; } = string.Empty;
        /// <summary>Which window was exceeded ("daily"/"monthly"), null when allowed.</summary>
        public string? Scope { get; init; }
        public int DailyLimit { get; init; }
        public int MonthlyLimit { get; init; }
        public long DailyUsed { get; init; }
        public long MonthlyUsed { get; init; }
        /// <summary>When the exceeded (or daily, when allowed) window resets.</summary>
        public DateTime ResetUtc { get; init; }

        public static McpQuotaDecision FailOpen(string plan, int dailyLimit, int monthlyLimit) => new()
        {
            Allowed = true,
            Plan = plan,
            DailyLimit = dailyLimit,
            MonthlyLimit = monthlyLimit,
            DailyUsed = -1,
            MonthlyUsed = -1,
            ResetUtc = DateTime.MinValue
        };
    }
}
