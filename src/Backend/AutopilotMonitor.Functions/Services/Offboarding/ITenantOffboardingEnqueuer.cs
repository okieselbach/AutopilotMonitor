using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Offboarding;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// Producer side of the <c>tenant-offboarding</c> queue. Implementations MUST be fail-loud:
    /// the caller (<c>TenantOffboardFunction</c>) only returns 202 Accepted after a successful
    /// enqueue, otherwise the admin's offboard click would silently strand without a worker
    /// pickup (memory: feedback_storage_helpers_fail_soft).
    /// </summary>
    public interface ITenantOffboardingEnqueuer
    {
        /// <summary>
        /// Enqueue a tenant-offboarding envelope. <paramref name="visibilityDelay"/> is used by
        /// the handler's drain self-re-enqueue path (Plan §7.4 D2) to delay the next pickup by
        /// ~2 minutes; producers in <c>TenantOffboardFunction</c> leave it null for immediate
        /// visibility.
        /// </summary>
        Task EnqueueAsync(
            TenantOffboardingEnvelope envelope,
            TimeSpan? visibilityDelay = null,
            CancellationToken ct = default);
    }
}
