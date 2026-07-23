using System;
using System.Net.NetworkInformation;
using System.Threading;
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
    /// See tasks/enrollment-status-reclassification.md.
    /// <para>
    /// Delivery hardening (2026-07-23): this is the FIRST request of a freshly booted process and
    /// the process never comes back — a miss is gone forever. The old single shot with a 5 s cap
    /// died on boot-time realities (NIC still associating, cold TLS/DNS, backend cold starts
    /// observed as &gt;5 s 503s). Now: wait briefly for the NIC, then up to
    /// <see cref="SendAttempts"/> attempts with a per-attempt timeout that survives a Functions
    /// cold start. The session is 48 h dead — spending up to ~75 s here is irrelevant, losing the
    /// only signal that the agent gave up is not.
    /// </para>
    /// </summary>
    internal static class EmergencyBreakReporter
    {
        private static readonly TimeSpan NetworkWaitMax = TimeSpan.FromSeconds(15);
        private const int SendAttempts = 3;
        private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
        // Upper bound for the blocking wait: NIC wait + 3×15s attempts + 2×5s delays + slack.
        private static readonly TimeSpan OverallBudget = TimeSpan.FromSeconds(75);

        public static void TrySend(AgentConfiguration agentConfig, string agentVersion, AgentLogger logger)
        {
            try
            {
                WaitForNetwork(logger);

                var auth = BackendClientFactory.BuildAuthClients(agentConfig, agentVersion, logger);
                var message =
                    $"Agent absolute session-age emergency break fired (cap {agentConfig.AbsoluteMaxSessionHours}h) — cleaning up and exiting.";

                // The process is about to exit, so block on the otherwise fire-and-forget send
                // rather than let it be abandoned mid-flight. TrySendAsync swallows its own HTTP
                // errors and keeps the retries inside one anti-flood reservation; the auth
                // bundle's HttpClients are intentionally left for process teardown.
                auth.EmergencyReporter
                    .TrySendAsync(
                        AgentErrorType.SessionAgeEmergencyBreak,
                        message,
                        attempts: SendAttempts,
                        perAttemptTimeout: PerAttemptTimeout,
                        retryDelay: RetryDelay)
                    .Wait(OverallBudget);
            }
            catch (Exception ex)
            {
                logger?.Debug($"Emergency-break report send failed (best-effort): {ex.Message}");
            }
        }

        /// <summary>
        /// Boot-time NIC grace: the break fires during bootstrap, often seconds after boot while
        /// Wi-Fi is still associating. Polls the cheap link-level signal — it cannot prove backend
        /// reachability (the send attempts handle that), it only avoids burning the first attempts
        /// into a known-dead link. Best-effort: probe errors abort the wait, never the send.
        /// </summary>
        private static void WaitForNetwork(AgentLogger logger)
        {
            try
            {
                if (NetworkInterface.GetIsNetworkAvailable()) return;

                logger?.Info($"Emergency-break report: no network link yet — waiting up to {NetworkWaitMax.TotalSeconds:F0}s.");
                var deadline = DateTime.UtcNow + NetworkWaitMax;
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                    if (NetworkInterface.GetIsNetworkAvailable()) return;
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"Emergency-break report: network probe failed ({ex.Message}) — sending anyway.");
            }
        }
    }
}
