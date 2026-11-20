using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Fills the Entra region of every tenant row that has none, folded into the 2-hourly maintenance run.
    /// A new tenant gets its region with the first login; this run covers the tenants that existed before
    /// and a first login whose lookup did not answer in time.
    /// </summary>
    public class TenantRegionMaintenance
    {
        internal static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(3);

        /// <summary>Bounds the first run, which has every existing tenant to fill; the next run continues.</summary>
        internal static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(2);

        private static readonly LogThrottle FailureLog = new(TimeSpan.FromDays(1));

        private readonly IConfigRepository _configRepository;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly IEntraTenantRegionLookup _lookup;
        private readonly ILogger<TenantRegionMaintenance> _logger;
        private readonly TimeProvider _time;

        public TenantRegionMaintenance(
            IConfigRepository configRepository,
            TenantConfigurationService tenantConfigService,
            IEntraTenantRegionLookup lookup,
            ILogger<TenantRegionMaintenance> logger)
            : this(configRepository, tenantConfigService, lookup, logger, TimeProvider.System)
        {
        }

        internal TenantRegionMaintenance(
            IConfigRepository configRepository,
            TenantConfigurationService tenantConfigService,
            IEntraTenantRegionLookup lookup,
            ILogger<TenantRegionMaintenance> logger,
            TimeProvider time)
        {
            _configRepository = configRepository;
            _tenantConfigService = tenantConfigService;
            _lookup = lookup;
            _logger = logger;
            _time = time;
        }

        /// <summary>Never throws; returns how many tenants received a region.</summary>
        public async Task<int> RunAsync()
        {
            TenantConfiguration[] due;
            try
            {
                var configs = await _configRepository.GetAllTenantConfigurationsAsync().ConfigureAwait(false);
                due = configs
                    .Where(c => !string.IsNullOrEmpty(c.TenantId)
                                && string.IsNullOrEmpty(c.TenantRegionScope)
                                && !TenantOffboardFunction.IsOffboardingTombstone(c))
                    // The newest first: a fresh signup whose first-login lookup failed is not stuck behind the backfill.
                    .OrderByDescending(c => c.OnboardedAt ?? DateTime.MinValue)
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Entra tenant region fill: reading the tenant configurations failed");
                return 0;
            }

            var started = _time.GetUtcNow();
            var filled = 0;
            for (var i = 0; i < due.Length; i++)
            {
                if (_time.GetUtcNow() - started >= RunBudget)
                {
                    _logger.LogWarning(
                        "Entra tenant region fill stopped after {Budget}: {Remaining} tenant(s) left for the next run",
                        RunBudget, due.Length - i);
                    break;
                }

                var tenantId = due[i].TenantId;
                try
                {
                    using var budget = new CancellationTokenSource(AttemptBudget);
                    var lookup = await _lookup.LookupAsync(tenantId, budget.Token).ConfigureAwait(false);
                    if (lookup.Region is not { } region)
                    {
                        if (FailureLog.ShouldLog(tenantId))
                            _logger.LogWarning("Entra tenant region lookup failed for tenant {TenantId}: {Reason}", tenantId, lookup.Failure);
                        continue;
                    }

                    var update = await _tenantConfigService
                        .UpdateAsync(tenantId, region.ApplyTo, "maintenance", "Entra tenant region")
                        .ConfigureAwait(false);
                    if (update.Status == TenantConfigUpdateStatus.Updated)
                        filled++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Entra tenant region fill failed for tenant {TenantId}", tenantId);
                }
            }

            return filled;
        }
    }
}
