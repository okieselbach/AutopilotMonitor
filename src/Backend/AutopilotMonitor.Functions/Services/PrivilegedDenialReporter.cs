using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// One refused request on a <c>GlobalAdminOnly</c> route, as seen by the policy middleware.
    /// Header-derived values (<see cref="ClientSource"/>, <see cref="McpToolName"/>) are caller-supplied
    /// and already sanitized/truncated by the middleware before they land here.
    /// </summary>
    public sealed record PrivilegedDenial(
        string Method,
        string Path,
        int StatusCode,
        string Reason,
        string Policy,
        string CallerId,
        string? Upn,
        string? ObjectId,
        string? TenantId,
        string CallerRole,
        string? ClientSource,
        string? McpToolName,
        string? CorrelationId);

    /// <summary>
    /// Seam between the policy middleware and the ops-event writer so the middleware stays unit-testable
    /// without the notification stack (the test harness passes a recording fake).
    /// </summary>
    public interface IPrivilegedDenialReporter
    {
        /// <summary>Fire-and-forget; never throws, never blocks the deny response.</summary>
        void Report(PrivilegedDenial denial);
    }

    /// <summary>
    /// Assume-breach layer for the Global-Admin-only surface: turns a 403 on a <c>GlobalAdminOnly</c>
    /// route into a <c>PrivilegedRouteDenied</c> ops event (Critical, or Warning for a Global Reader)
    /// that operators route to a push channel. The authorization decision itself is not touched — this
    /// only makes a refused probe visible with the backend's own view of the caller's identity.
    /// </summary>
    public sealed class PrivilegedDenialReporter : IPrivilegedDenialReporter
    {
        /// <summary>One event per (caller, path) per hour; the rest stays in the Warning trace.</summary>
        internal static readonly TimeSpan Throttle = TimeSpan.FromHours(1);

        private readonly OpsEventService _opsEvents;
        private readonly IMemoryCache _cache;
        private readonly ILogger<PrivilegedDenialReporter> _logger;

        public PrivilegedDenialReporter(OpsEventService opsEvents, IMemoryCache cache, ILogger<PrivilegedDenialReporter> logger)
        {
            _opsEvents = opsEvents;
            _cache = cache;
            _logger = logger;
        }

        public void Report(PrivilegedDenial denial)
        {
            try
            {
                // The throttle is an in-process IMemoryCache, i.e. PER FUNCTION INSTANCE. On Flex
                // Consumption a burst from one caller can fan out across instances, so the same
                // (caller, path) can surface once per instance that saw it. Deliberately accepted —
                // the count is a "how many instances noticed" figure, not an incident count, and a
                // distributed throttle is not worth a storage round-trip on a deny path. Keyed on the
                // path, not the tool name: a scripted sweep over every GA route still yields one
                // event per route, while a repeat on the same route stays quiet for an hour.
                var key = $"prd:{denial.CallerId}:{denial.Path}";
                if (_cache.TryGetValue(key, out _))
                    return;
                _cache.Set(key, true, Throttle);

                _ = _opsEvents.RecordPrivilegedRouteDeniedAsync(denial)
                    .ContinueWith(
                        t => _logger.LogWarning(t.Exception?.InnerException, "PrivilegedRouteDenied ops event failed"),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                // Never let the observer break the deny response.
                _logger.LogWarning(ex, "PrivilegedRouteDenied reporting failed");
            }
        }
    }
}
