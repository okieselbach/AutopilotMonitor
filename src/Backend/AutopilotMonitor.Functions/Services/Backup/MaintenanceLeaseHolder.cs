using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Backup
{
    /// <summary>
    /// Owns a maintenance-lease for the lifetime of a backup or restore run and
    /// renews it in the background. Without the renewal loop the 60-second lease
    /// duration would expire long before the per-run budget (50 minutes), letting
    /// a parallel worker / timer / restore acquire the same "exclusive" lease and
    /// letting the watchdog see a free lease while a job is still running
    /// (Codex-Hotfix Wave1).
    /// <para>
    /// Renewal cadence is fixed at <see cref="RenewalInterval"/> (45 s, well under
    /// the 60-second lease). On any renewal failure the supplied
    /// <paramref name="handlerCts"/> is cancelled so the handler bails out
    /// promptly instead of running rogue under an expired lease.
    /// </para>
    /// <para>
    /// Disposal stops the renewal loop deterministically (CancellationTokenSource
    /// cancel + await the loop task) BEFORE releasing the lease, so a final
    /// in-flight renewal cannot race the release.
    /// </para>
    /// </summary>
    public sealed class MaintenanceLeaseHolder : IAsyncDisposable
    {
        /// <summary>Sub-60s renewal cadence — leaves headroom for transient retries.</summary>
        public static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(45);

        private readonly BlobLeaseClient _lease;
        private readonly CancellationTokenSource _renewalCts;
        private readonly CancellationTokenSource _handlerCts;
        private readonly Task _renewalLoop;
        private readonly ILogger _logger;
        private int _disposed;

        public MaintenanceLeaseHolder(BlobLeaseClient lease, CancellationTokenSource handlerCts, ILogger logger)
        {
            _lease = lease ?? throw new ArgumentNullException(nameof(lease));
            _handlerCts = handlerCts ?? throw new ArgumentNullException(nameof(handlerCts));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _renewalCts = new CancellationTokenSource();
            _renewalLoop = Task.Run(() => RunRenewalLoopAsync(_renewalCts.Token));
        }

        /// <summary>
        /// Sentinel value set if the renewal loop terminated because a renewal call
        /// threw — the handler should treat this as a maintenance-lease loss and
        /// bail out without writing the manifest or queue-deleting.
        /// </summary>
        public string? RenewalFailureReason { get; private set; }

        private async Task RunRenewalLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(RenewalInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;     // normal shutdown
                }

                try
                {
                    await _lease.RenewAsync(conditions: null, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;     // shutdown while renewing
                }
                catch (RequestFailedException ex)
                {
                    RenewalFailureReason = $"maintenance lease renewal failed (status={ex.Status}, code={ex.ErrorCode}): {ex.Message}";
                    _logger.LogError(ex, "MaintenanceLeaseHolder: lease renewal failed — cancelling handler");
                    try { _handlerCts.Cancel(); } catch (ObjectDisposedException) { }
                    return;
                }
                catch (Exception ex)
                {
                    RenewalFailureReason = $"maintenance lease renewal threw: {ex.Message}";
                    _logger.LogError(ex, "MaintenanceLeaseHolder: lease renewal unexpected throw — cancelling handler");
                    try { _handlerCts.Cancel(); } catch (ObjectDisposedException) { }
                    return;
                }
            }
        }

        /// <summary>
        /// Stops the renewal loop and releases the lease. Renewal-loop cancel +
        /// await happens BEFORE the release so a final renew cannot race a release.
        /// Safe to call multiple times.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { _renewalCts.Cancel(); } catch (ObjectDisposedException) { }
            try { await _renewalLoop.ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "MaintenanceLeaseHolder: renewal loop terminated with exception"); }
            _renewalCts.Dispose();

            try { await _lease.ReleaseAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "MaintenanceLeaseHolder: lease release failed (auto-expires within 60 s)"); }
        }
    }
}
