using System;
using System.Threading;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Time budget for the Graph-backed device validators (Autopilot, corporate identifier,
    /// device association, Cloud PC). They used to run on the unnamed HttpClient with its 100-s
    /// default timeout, two attempts each, sequentially — a single stuck Graph call parked an
    /// agent-config request for 100+ s while the agent itself gives up after 30 s
    /// (BackendClientFactory), so the work was wasted on an abandoned request. The budget keeps
    /// the whole chain inside the agent's window: a transient (503 + Retry-After) answer after
    /// ≤ <see cref="ChainBudget"/> lets the agent's own 10/30/60-s retry take over.
    /// </summary>
    public static class DeviceValidationBudget
    {
        /// <summary>Hard cap on one Graph attempt (token acquire + query) inside a validator.</summary>
        public static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Total budget for the validator chain of one request. Below the agent's 30-s client
        /// timeout with headroom for the rest of the request.
        /// </summary>
        public static readonly TimeSpan ChainBudget = TimeSpan.FromSeconds(20);

        /// <summary>Chain-wide CTS; the caller disposes it after the validator block.</summary>
        public static CancellationTokenSource CreateChainCts()
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(ChainBudget);
            return cts;
        }

        /// <summary>Per-attempt CTS linked to the chain token; the validator disposes it after the attempt.</summary>
        public static CancellationTokenSource CreateAttemptCts(CancellationToken chain)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(chain);
            cts.CancelAfter(PerAttemptTimeout);
            return cts;
        }
    }
}
