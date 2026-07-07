using System;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Resolves a tenant's effective edition (Community/Enterprise) and its entitlements at read
    /// time. Rides on <see cref="TenantConfigurationService"/>'s 5-minute config cache — no cache
    /// of its own, so plan/trial mutations become visible within the existing staleness budget.
    ///
    /// Fail-closed: any storage/resolution failure yields Community. A broken entitlement lookup
    /// must never grant Enterprise capabilities.
    /// </summary>
    public class TenantEntitlementService
    {
        private readonly TenantConfigurationService _configService;
        private readonly ILogger<TenantEntitlementService> _logger;
        private readonly TimeProvider _time;

        public TenantEntitlementService(
            TenantConfigurationService configService,
            ILogger<TenantEntitlementService> logger)
            : this(configService, logger, TimeProvider.System)
        {
        }

        /// <summary>Test seam — inject a fake <see cref="TimeProvider"/> for deterministic trial math.</summary>
        public TenantEntitlementService(
            TenantConfigurationService configService,
            ILogger<TenantEntitlementService> logger,
            TimeProvider time)
        {
            _configService = configService;
            _logger = logger;
            _time = time;
        }

        /// <summary>
        /// Resolves the tenant's effective edition. Uses the strict point-read
        /// (<see cref="TenantConfigurationService.GetConfigurationIfExistsAsync"/>) so an
        /// entitlement check can never materialize a tenant-config row. No row / any error →
        /// Community (fail-closed).
        /// </summary>
        public virtual async Task<TenantEdition> GetEditionAsync(string? tenantId)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                return TenantEdition.Community;

            try
            {
                var config = await _configService.GetConfigurationIfExistsAsync(tenantId);
                if (config == null)
                    return TenantEdition.Community;

                return ResolveEdition(config, _time.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Entitlement] Edition resolution failed for tenant {TenantId} — treating as Community (fail-closed)",
                    tenantId);
                return TenantEdition.Community;
            }
        }

        /// <summary>Resolves the tenant's effective entitlement set (fail-closed → Community values).</summary>
        public virtual async Task<EditionEntitlements> GetEntitlementsAsync(string? tenantId)
            => FeatureEntitlementCatalog.Get(await GetEditionAsync(tenantId));

        /// <summary>Pure edition resolution for callers that already hold the config.</summary>
        public static TenantEdition ResolveEdition(TenantConfiguration config, DateTime nowUtc)
            => FeatureEntitlementCatalog.ResolveEdition(config.PlanTier, config.TrialExpiresUtc, nowUtc);

        /// <summary>
        /// The retention days the platform actually enforces for this config: the stored value
        /// clamped to the edition's cap. <c>days &lt;= 0</c> is the GA-only "infinite" escape hatch
        /// and is passed through unclamped (the fanout skips those tenants entirely).
        /// </summary>
        public static int GetEffectiveRetentionDays(TenantConfiguration config, DateTime nowUtc)
        {
            var days = config.DataRetentionDays;
            if (days <= 0)
                return 0;

            var cap = FeatureEntitlementCatalog.Get(ResolveEdition(config, nowUtc)).RetentionCapDays;
            return Math.Min(days, cap);
        }
    }
}
