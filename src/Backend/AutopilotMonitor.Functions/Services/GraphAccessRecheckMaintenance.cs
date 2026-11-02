using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Asks Entra again for every tenant with a Graph access suspicion, folded into the 2-hourly
    /// maintenance run. Without it the state would depend on device traffic: a suspicion from a
    /// Friday evening would stay unconfirmed until Monday, and a repair made directly in Entra would
    /// keep the notice up until the next device sends.
    /// </summary>
    public class GraphAccessRecheckMaintenance
    {
        /// <summary>One token request answers the question; a rejection then only waits for its retry delay.</summary>
        internal static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(3);

        private readonly IConfigRepository _configRepository;
        private readonly GraphTokenService _graphTokenService;
        private readonly ILogger<GraphAccessRecheckMaintenance> _logger;

        public GraphAccessRecheckMaintenance(
            IConfigRepository configRepository,
            GraphTokenService graphTokenService,
            ILogger<GraphAccessRecheckMaintenance> logger)
        {
            _configRepository = configRepository;
            _graphTokenService = graphTokenService;
            _logger = logger;
        }

        /// <summary>Never throws; returns how many tenants it asked about.</summary>
        public async Task<int> RunAsync()
        {
            string[] tenantIds;
            try
            {
                var configs = await _configRepository.GetAllTenantConfigurationsAsync().ConfigureAwait(false);
                // Without device validation the state means nothing: it waits for the switch to come back.
                tenantIds = configs
                    .Where(c => c.GraphAccessRejectedSince != null && c.HasAnyDeviceValidation())
                    .Select(c => c.TenantId)
                    .ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Graph access recheck: reading the tenant configurations failed");
                return 0;
            }

            foreach (var tenantId in tenantIds)
            {
                try
                {
                    // A cached token proves nothing either way: only a fresh answer may decide.
                    _graphTokenService.InvalidateTenant(tenantId);
                    using var budget = new CancellationTokenSource(AttemptBudget);
                    await _graphTokenService.GetAccessTokenForAgentValidationAsync(tenantId, budget.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Graph access recheck failed for tenant {TenantId}", tenantId);
                }
            }

            return tenantIds.Length;
        }
    }
}
