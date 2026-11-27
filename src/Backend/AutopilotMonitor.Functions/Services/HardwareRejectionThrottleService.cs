using System;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// In-memory throttle for hardware rejection webhook notifications.
    /// Ensures at most one notification per 24 hours per tenant+manufacturer+model combination,
    /// also for concurrent distress reports (<see cref="OncePerWindowGate"/>).
    /// Registered as singleton; state resets on Function App restart (acceptable trade-off).
    /// </summary>
    public class HardwareRejectionThrottleService
    {
        private static readonly TimeSpan ThrottleWindow = TimeSpan.FromHours(24);
        private readonly OncePerWindowGate _gate = new(ThrottleWindow);

        /// <summary>
        /// Returns true if a notification should be sent for this combination. Thread-safe: at most
        /// one concurrent caller per key wins.
        /// </summary>
        public bool ShouldNotify(string tenantId, string? manufacturer, string? model)
            => _gate.TryClaim($"{tenantId}|{manufacturer ?? ""}|{model ?? ""}");
    }
}
