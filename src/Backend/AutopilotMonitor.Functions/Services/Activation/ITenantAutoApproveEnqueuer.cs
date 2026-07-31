using System;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.Activation
{
    /// <summary>
    /// Producer abstraction for the <c>tenant-auto-approve</c> queue so the signup site
    /// (<c>AuthFunction</c>) is mockable in tests.
    /// </summary>
    public interface ITenantAutoApproveEnqueuer
    {
        Task EnqueueAsync(
            TenantAutoApproveEnvelope envelope,
            TimeSpan? visibilityDelay = null,
            CancellationToken ct = default);
    }
}
