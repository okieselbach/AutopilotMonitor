using System;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Best-effort emitter for the agent's absolute session-age emergency break. The break
    /// (<c>Program.Guards.CheckSessionAgeEmergencyBreak</c>) tears the agent down before the normal
    /// telemetry pipeline exists, so we build a throwaway auth bundle and fire a single
    /// <see cref="AgentErrorType.SessionAgeEmergencyBreak"/> report over the resilient emergency
    /// channel. The backend materializes an <c>agent_emergency_break</c> timeline event from it
    /// (<c>ReportAgentErrorFunction</c>), closing the silent-48h blind spot and letting the timeout
    /// classifier terminalize the session instead of waiting out the grace. Fully swallowed — a send
    /// failure (no network, no cert) must never delay the cleanup/exit the break exists to perform.
    /// See docs/design/enrollment-status-reclassification.md.
    /// </summary>
    internal static class EmergencyBreakReporter
    {
        public static void TrySend(AgentConfiguration agentConfig, string agentVersion, AgentLogger logger)
        {
            try
            {
                var auth = BackendClientFactory.BuildAuthClients(agentConfig, agentVersion, logger);
                var message =
                    $"Agent absolute session-age emergency break fired (cap {agentConfig.AbsoluteMaxSessionHours}h) — cleaning up and exiting.";

                // The process is about to exit, so block briefly on the otherwise fire-and-forget send
                // rather than let it be abandoned mid-flight. TrySendAsync swallows its own HTTP errors;
                // the auth bundle's HttpClients are intentionally left for process teardown.
                auth.EmergencyReporter
                    .TrySendAsync(AgentErrorType.SessionAgeEmergencyBreak, message)
                    .Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                logger?.Debug($"Emergency-break report send failed (best-effort): {ex.Message}");
            }
        }
    }
}
