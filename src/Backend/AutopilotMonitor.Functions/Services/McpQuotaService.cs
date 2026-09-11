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
    /// budget (daily + monthly) that every request of that tenant's accounts counts against. Both must be
    /// free for a request to pass; the tenant budget is what makes "just create ten more accounts" pointless.
    ///
    /// Both budgets belong to the caller's HOME tenant (the token's tid) — "the budget follows the
    /// delegating tenant": a delegated (MSP) read into a managed tenant draws on the MSP's own windows, never
    /// on the managed tenant's, so a managed tenant is neither charged nor blocked by its manager's reads.
    ///
    /// User plan precedence: explicit per-user override (McpUsers.UsagePlan — honoured only for the identity
    /// the UPN is bound to, tid + oid, see McpUserService.GetBoundMcpUserAsync) → the home tenant's usage plan
    /// (TenantConfiguration.McpUsagePlanOverride, else the edition's catalog plan name). Limits come from the
    /// admin-editable SectionUsagePlans definitions (AdminConfiguration.PlanTierDefinitionsJson); when no
    /// definition matches the plan name, the static catalog fallbacks apply. An override naming a plan that
    /// exists nowhere resolves to the Community fallback (fail-closed).
    ///
    /// Tenant plan: the home tenant's usage plan — a per-user override lifts that person's own budget, never
    /// the organization's. A definition without tenant limits falls back to the edition's catalog tenant
    /// limits; an explicit 0 lifts that window.
    ///
    /// Purchased delegation slots grow BOTH budgets: every slot beyond the edition's included ones
    /// (TenantEntitlementService.GetPurchasedDelegatedSlots — entitlement-gated, so a Community or a
    /// conferred-Pro tenant earns nothing) adds the plan definition's slot values (else the edition's catalog
    /// slot values) to each window. A window lifted to 0 (unlimited) stays unlimited.
    ///
    /// Counters: user counters ride on the UserUsageLog table (PK = oid), tenant counters on the
    /// McpTenantUsage table (PK = home tenantId, RK = {yyyyMMdd}_{oid} — one partition read per check, no
    /// hot row); both are written fire-and-forget by McpQuotaEnforcementMiddleware for X-Client-Source: mcp
    /// requests. Daily = today's rows, monthly = the sum over the month. The usage SNAPSHOTS are cached for
    /// 60 seconds — per user (oid) and per tenant — so the worst-case overshoot is bounded
    /// (limit + 60s × request-rate) — the same posture as the sliding-window rate limiter. Limits are
    /// re-resolved per check from services that carry their own caches, so a plan change applies at once.
    /// </summary>
    public class McpQuotaService
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

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
        /// Resolves the caller's effective plans + limits and checks the current user AND tenant usage against
        /// them. <paramref name="tenantId"/> is the token's tid — the identity the per-user override is bound to
        /// and the tenant whose organization budget every request of this caller draws on, a delegated read
        /// included. Fail-open on counter/storage errors (a broken quota check must not take down MCP);
        /// fail-closed on plan resolution (unknown plan → Community).
        /// </summary>
        public virtual async Task<McpQuotaDecision> CheckAsync(string oid, string? upn, string? tenantId)
        {
            var nowUtc = _time.GetUtcNow().UtcDateTime;
            var limits = await ResolvePlanAsync(AdminIdentity.Create(upn, tenantId, oid), tenantId);

            var user = await ReadUserUsageAsync(oid, nowUtc);
            if (user == null)
                return McpQuotaDecision.FailOpen(limits);

            var tenant = await ReadTenantUsageAsync(tenantId, limits, nowUtc);
            if (tenant == null)
                return McpQuotaDecision.FailOpen(limits);

            return BuildDecision(limits, user.DailyUsed, user.MonthlyUsed, tenant.DailyUsed, tenant.MonthlyUsed, nowUtc);
        }

        /// <summary>
        /// Plan resolution only (no counter read). <paramref name="identity"/> is the caller's validated
        /// (upn, tid, oid); the per-user override applies only when that identity IS the one the McpUsers UPN
        /// is bound to — a null / unbound identity gets the home tenant's plan. Both window sets follow
        /// <paramref name="tenantId"/>, the caller's home tenant.
        /// </summary>
        public virtual async Task<McpPlanLimits> ResolvePlanAsync(AdminIdentity? identity, string? tenantId)
        {
            var home = await ResolveHomeAsync(tenantId);
            return Compose(await ResolveUserLimitsAsync(identity, home), ResolveTenantLimits(home));
        }

        /// <summary>
        /// One tenant's organization windows alone (plan name + tenant limits with the slot growth applied; no
        /// counters, no per-user part) — for the organization usage report.
        /// </summary>
        public virtual async Task<TenantPlanLimits> ResolveTenantPlanAsync(string tenantId)
            => ResolveTenantLimits(await ResolveHomeAsync(tenantId));

        /// <summary>
        /// Everything about the home tenant a resolution needs, read once per check (each entitlement read is
        /// served from the tenant-config cache): the plan definitions, the entitlement set, the usage plan name
        /// and the purchased delegation slots.
        /// </summary>
        private async Task<HomeTenantPlan> ResolveHomeAsync(string? tenantId)
            => new(
                await LoadDefinitionsAsync(),
                await _entitlementService.GetEntitlementsAsync(tenantId),
                await _entitlementService.GetMcpUsagePlanNameAsync(tenantId),
                await _entitlementService.GetPurchasedDelegatedSlotsAsync(tenantId));

        private async Task<UserPlanLimits> ResolveUserLimitsAsync(AdminIdentity? identity, HomeTenantPlan home)
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

            // 2. Else the home tenant's usage plan (the tenant-wide GA override, else the edition default).
            var planName = overridePlan ?? home.PlanName;

            // 3. User limits: the definition for the (possibly overridden) plan name, else the catalog fallback
            //    for the edition plans, else Community (fail-closed for overrides naming a plan that exists nowhere).
            var definition = Find(home.Definitions, planName);
            var fallback = FeatureEntitlementCatalog.IsPermanentProTier(planName)
                ? FeatureEntitlementCatalog.Get(TenantEdition.Pro)
                : FeatureEntitlementCatalog.Get(TenantEdition.Community);

            // 4. Slot growth: the definition's slot values, else the HOME tenant's catalog values — a per-user
            //    override plan without slot fields still grows with the home tenant's purchased slots.
            var slotDaily = definition?.SlotDailyRequestLimit ?? home.Entitlements.McpSlotDailyRequestLimit;
            var slotMonthly = definition?.SlotMonthlyRequestLimit ?? home.Entitlements.McpSlotMonthlyRequestLimit;

            return new UserPlanLimits(
                planName,
                Grow(definition?.DailyRequestLimit ?? fallback.McpDailyRequestLimit, home.PurchasedSlots, slotDaily),
                Grow(definition?.MonthlyRequestLimit ?? fallback.McpMonthlyRequestLimit, home.PurchasedSlots, slotMonthly),
                home.PurchasedSlots, slotDaily, slotMonthly);
        }

        /// <summary>
        /// The home tenant's organization windows: its usage plan definition when that carries tenant limits
        /// (null = not set → the edition's catalog tenant limits; an explicit 0 lifts the window), never a
        /// per-user override, plus the slot growth (definition's slot values, else the catalog's).
        /// </summary>
        private static TenantPlanLimits ResolveTenantLimits(HomeTenantPlan home)
        {
            var definition = Find(home.Definitions, home.PlanName);
            var slotDaily = definition?.SlotTenantDailyRequestLimit ?? home.Entitlements.McpSlotTenantDailyRequestLimit;
            var slotMonthly = definition?.SlotTenantMonthlyRequestLimit ?? home.Entitlements.McpSlotTenantMonthlyRequestLimit;

            return new TenantPlanLimits(
                home.PlanName,
                Grow(definition?.TenantDailyRequestLimit ?? home.Entitlements.McpTenantDailyRequestLimit, home.PurchasedSlots, slotDaily),
                Grow(definition?.TenantMonthlyRequestLimit ?? home.Entitlements.McpTenantMonthlyRequestLimit, home.PurchasedSlots, slotMonthly),
                home.PurchasedSlots, slotDaily, slotMonthly);
        }

        /// <summary>
        /// Pure: a window grows by purchased slots × per-slot value. A lifted window (0 = unlimited) stays
        /// lifted — growth must never turn "unlimited" into a limit.
        /// </summary>
        internal static int Grow(int baseLimit, int purchasedSlots, int perSlot)
            => baseLimit <= 0 || purchasedSlots <= 0 || perSlot <= 0
                ? baseLimit
                : baseLimit + purchasedSlots * perSlot;

        private async Task<List<PlanTierDefinition>> LoadDefinitionsAsync()
        {
            try
            {
                var adminConfig = await _adminConfigService.GetConfigurationAsync();
                return PlanTierDefinitionParser.Parse(adminConfig.PlanTierDefinitionsJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[McpQuota] Plan definitions unavailable — using catalog fallback");
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
        /// The tenant's organization counters (cached snapshot, shared by every member of that tenant).
        /// Skipped — zero counters, no read, nothing cached — when nothing could ever block on it (no tenant,
        /// or both windows lifted). Null = read failed (fail-open, nothing cached).
        /// </summary>
        private async Task<UsageSnapshot?> ReadTenantUsageAsync(string? tenantId, McpPlanLimits limits, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || (limits.TenantDailyLimit <= 0 && limits.TenantMonthlyLimit <= 0))
                return UsageSnapshot.Zero;

            var cacheKey = TenantCacheKey(tenantId);
            if (_cache.TryGetValue<UsageSnapshot>(cacheKey, out var cached) && cached != null)
                return cached;

            try
            {
                var (monthStart, today) = Window(nowUtc);
                var records = await _usageRepo.GetTenantUsageAsync(tenantId, monthStart, today);
                var snapshot = new UsageSnapshot(
                    records.Where(r => r.Date == today).Sum(r => r.RequestCount),
                    records.Sum(r => r.RequestCount));
                _cache.Set(cacheKey, snapshot, CacheDuration);
                return snapshot;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[McpQuota] Tenant usage lookup failed for tenant={TenantId} — allowing (fail-open)", tenantId);
                return null;
            }
        }

        private static (string MonthStart, string Today) Window(DateTime nowUtc)
            => (new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).ToString("yyyyMMdd"),
                nowUtc.ToString("yyyyMMdd"));

        private static McpPlanLimits Compose(UserPlanLimits user, TenantPlanLimits tenant)
            => new(user.PlanName, user.DailyLimit, user.MonthlyLimit, tenant.TenantPlan, tenant.TenantDailyLimit, tenant.TenantMonthlyLimit,
                user.PurchasedDelegatedSlots, user.SlotDailyLimit, user.SlotMonthlyLimit, tenant.SlotTenantDailyLimit, tenant.SlotTenantMonthlyLimit);

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
                ResetUtc = resetUtc,
                PurchasedDelegatedSlots = limits.PurchasedDelegatedSlots,
                SlotDailyLimit = limits.SlotDailyLimit,
                SlotMonthlyLimit = limits.SlotMonthlyLimit,
                SlotTenantDailyLimit = limits.SlotTenantDailyLimit,
                SlotTenantMonthlyLimit = limits.SlotTenantMonthlyLimit,
            };
        }

        private static bool Exceeded(int limit, long used) => limit > 0 && used >= limit;

        /// <summary>Cached counter pair (today / this month) for one user or one tenant.</summary>
        private sealed record UsageSnapshot(long DailyUsed, long MonthlyUsed)
        {
            public static readonly UsageSnapshot Zero = new(0, 0);
        }

        /// <summary>The home tenant's plan facts a resolution needs, read once per check.</summary>
        private sealed record HomeTenantPlan(
            List<PlanTierDefinition> Definitions, EditionEntitlements Entitlements, string PlanName, int PurchasedSlots);

        private sealed record UserPlanLimits(
            string PlanName, int DailyLimit, int MonthlyLimit,
            int PurchasedDelegatedSlots, int SlotDailyLimit, int SlotMonthlyLimit);

        /// <summary>
        /// The tenant's plan name and organization-wide windows (0 = unlimited; the slot growth is already
        /// applied) plus the growth breakdown: purchased slots and the per-slot values.
        /// </summary>
        public sealed record TenantPlanLimits(
            string TenantPlan, int TenantDailyLimit, int TenantMonthlyLimit,
            int PurchasedDelegatedSlots = 0, int SlotTenantDailyLimit = 0, int SlotTenantMonthlyLimit = 0);
    }

    /// <summary>
    /// Resolved plan names and limits for one caller: the user's plan (override or home plan) with the user
    /// windows, and the home tenant's plan with the organization-wide windows. 0 = unlimited. The limits
    /// already contain the growth from purchased delegation slots; the trailing fields carry the breakdown.
    /// </summary>
    public sealed record McpPlanLimits(
        string PlanName, int DailyLimit, int MonthlyLimit,
        string TenantPlan, int TenantDailyLimit, int TenantMonthlyLimit,
        int PurchasedDelegatedSlots = 0,
        int SlotDailyLimit = 0, int SlotMonthlyLimit = 0,
        int SlotTenantDailyLimit = 0, int SlotTenantMonthlyLimit = 0);

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
        /// <summary>The home tenant's usage plan — the organization-wide windows follow it, never the override.</summary>
        public string TenantPlan { get; init; } = string.Empty;
        public int TenantDailyLimit { get; init; }
        public int TenantMonthlyLimit { get; init; }
        public long TenantDailyUsed { get; init; }
        public long TenantMonthlyUsed { get; init; }
        /// <summary>When the exceeded (or daily, when allowed) window resets.</summary>
        public DateTime ResetUtc { get; init; }
        /// <summary>Delegation slots bought beyond the edition's included ones (0 = none; the limits above already grew by them).</summary>
        public int PurchasedDelegatedSlots { get; init; }
        public int SlotDailyLimit { get; init; }
        public int SlotMonthlyLimit { get; init; }
        public int SlotTenantDailyLimit { get; init; }
        public int SlotTenantMonthlyLimit { get; init; }

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
            ResetUtc = DateTime.MinValue,
            PurchasedDelegatedSlots = limits.PurchasedDelegatedSlots,
            SlotDailyLimit = limits.SlotDailyLimit,
            SlotMonthlyLimit = limits.SlotMonthlyLimit,
            SlotTenantDailyLimit = limits.SlotTenantDailyLimit,
            SlotTenantMonthlyLimit = limits.SlotTenantMonthlyLimit,
        };
    }
}
