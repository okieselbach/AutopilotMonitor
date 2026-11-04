using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Asks Entra again for every tenant with a Graph access suspicion or an open add-on episode,
    /// folded into the 2-hourly maintenance run. Without it the state would depend on device
    /// traffic: a suspicion from a Friday evening would stay unconfirmed until Monday, and a repair
    /// made directly in Entra (or an add-on grant) would keep the notice up until the next device sends.
    /// </summary>
    public class GraphAccessRecheckMaintenance
    {
        /// <summary>One token request answers the question; a rejection then only waits for its retry delay.</summary>
        internal static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(3);

        private readonly IConfigRepository _configRepository;
        private readonly GraphTokenService _graphTokenService;
        private readonly IGraphAccessHealthRecorder _accessHealth;
        private readonly ILogger<GraphAccessRecheckMaintenance> _logger;

        public GraphAccessRecheckMaintenance(
            IConfigRepository configRepository,
            GraphTokenService graphTokenService,
            IGraphAccessHealthRecorder accessHealth,
            ILogger<GraphAccessRecheckMaintenance> logger)
        {
            _configRepository = configRepository;
            _graphTokenService = graphTokenService;
            _accessHealth = accessHealth;
            _logger = logger;
        }

        /// <summary>Never throws; returns how many tenants it asked about.</summary>
        public async Task<int> RunAsync()
        {
            TenantConfiguration[] due;
            try
            {
                var configs = await _configRepository.GetAllTenantConfigurationsAsync().ConfigureAwait(false);
                due = configs.Where(c => NeedsToken(c) || AddOnValidation.All.Any(a => a.MissingSince(c) != null)).ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Graph access recheck: reading the tenant configurations failed");
                return 0;
            }

            foreach (var config in due)
            {
                try
                {
                    // A switched-off validation needs no token: its episode simply ends.
                    if (AddOnValidation.All.Any(a => a.MissingSince(config) != null && !a.IsEnabled(config)))
                        await _accessHealth.ReconcileSwitchedOffAddOnsAsync(config.TenantId).ConfigureAwait(false);
                    if (!NeedsToken(config))
                        continue;

                    // A cached token proves nothing either way: only a fresh answer may decide.
                    _graphTokenService.InvalidateTenant(config.TenantId);
                    using var budget = new CancellationTokenSource(AttemptBudget);
                    await _graphTokenService.GetAccessTokenForAgentValidationAsync(config.TenantId, budget.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Graph access recheck failed for tenant {TenantId}", config.TenantId);
                }
            }

            return due.Length;
        }

        /// <summary>
        /// Whether only a fresh token can decide: a token suspicion while a validation is on (without
        /// one the state means nothing and waits for the switch to come back), or an add-on episode
        /// whose switch is still on.
        /// </summary>
        private static bool NeedsToken(TenantConfiguration config) =>
            (config.GraphAccessRejectedSince != null && config.HasAnyDeviceValidation())
            || AddOnValidation.All.Any(a => a.MissingSince(config) != null && a.IsEnabled(config));
    }
}
