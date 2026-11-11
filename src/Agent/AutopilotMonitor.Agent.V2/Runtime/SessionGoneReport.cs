using System;
using System.Net;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// The one emergency report an agent sends when the backend answered HTTP 410 for its own
    /// session — on a telemetry upload (<see cref="ServerControlPlane.HandleSessionGone"/>) or at
    /// registration (<see cref="BackendSessionRegistration"/>). The send blocks within a bound:
    /// the caller ends the process right after and the cleanup deletes the agent, so a
    /// fire-and-forget send would be abandoned mid-flight. Retries stay inside one anti-flood
    /// reservation (ONE report). Never throws.
    /// </summary>
    internal static class SessionGoneReport
    {
        private const int Attempts = 2;
        private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

        public static void SendBlocking(
            EmergencyReporter emergencyReporter,
            AgentErrorType errorType,
            string message,
            AgentLogger logger)
        {
            if (emergencyReporter == null) return;

            try
            {
                emergencyReporter
                    .TrySendAsync(
                        errorType,
                        message,
                        httpStatusCode: (int)HttpStatusCode.Gone,
                        attempts: Attempts,
                        perAttemptTimeout: PerAttemptTimeout,
                        retryDelay: RetryDelay)
                    .Wait(Budget);
            }
            catch (Exception ex)
            {
                logger.Debug($"Session-gone report send failed (best-effort): {ex.Message}");
            }
        }
    }
}
