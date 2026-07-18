using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Services;

namespace AutopilotMonitor.Agent.V2.Core.Configuration
{
    /// <summary>
    /// Agent-side evaluation of the config-channel endpoint migration signal
    /// (<see cref="AgentConfigResponse.MigrateToApiBaseUrl"/>). Mirrors the kill-switch
    /// hardening: only a LIVE fetch is honoured (the on-disk config cache strips the field on
    /// write and read, and this check re-validates the outcome as defence-in-depth), and the
    /// target must pass <see cref="AgentEndpointMigrationRules"/> even though the backend
    /// already validated it — a compromised or misconfigured backend must not be able to
    /// re-home agents to an arbitrary host.
    /// </summary>
    public static class EndpointMigration
    {
        /// <summary>
        /// Returns the normalized migration target when the config carries a valid, live-fetched
        /// re-home signal that differs from <paramref name="currentBaseUrl"/>; otherwise null.
        /// Never throws — every rejection is logged and degrades to "no migration".
        /// </summary>
        public static string ResolveTarget(
            AgentConfigResponse remoteConfig,
            RemoteConfigFetchOutcome fetchOutcome,
            string currentBaseUrl,
            AgentLogger logger)
        {
            if (remoteConfig == null || string.IsNullOrWhiteSpace(remoteConfig.MigrateToApiBaseUrl))
                return null;

            if (fetchOutcome != RemoteConfigFetchOutcome.Succeeded)
            {
                logger?.Warning(
                    $"Endpoint migration signal ignored — config source is {fetchOutcome}, not a live fetch.");
                return null;
            }

            if (!AgentEndpointMigrationRules.IsEffectiveMigration(
                    remoteConfig.MigrateToApiBaseUrl, currentBaseUrl, out var target))
            {
                // Either the target failed validation (scheme/host allowlist) or it equals the
                // URL the agent is already using (steady state during a migration window once
                // the new backend's own config echoes nothing).
                if (target == null && !AgentEndpointMigrationRules.TryNormalizeTarget(
                        remoteConfig.MigrateToApiBaseUrl, out _))
                {
                    logger?.Warning(
                        $"Endpoint migration signal REJECTED — target failed validation: '{remoteConfig.MigrateToApiBaseUrl}'");
                }
                return null;
            }

            return target;
        }
    }
}
