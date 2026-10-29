using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Reports
{
    /// <summary>
    /// Per-instance cap on concurrent report uploads (singleton, shared by the session-report and the
    /// diagnostic-files route). A report body may be 20 MiB, roughly 150 MB of memory once read and
    /// parsed, and as gzip only a few KB on the wire; the slot is taken before the body is read and
    /// held until the response. Uploads are rare and seconds apart, so a caller who finds every slot
    /// taken is answered 429 at once. The first refusal per instance and hour raises
    /// <c>ReportUploadLimitReached</c> for the operators; later refusals in that window stay in the
    /// Warning trace, so nobody can flood the alarm.
    /// </summary>
    public sealed class ReportUploadGate : ConcurrencyGate
    {
        internal const int DefaultLimit = 3;
        internal static readonly TimeSpan AlertWindow = TimeSpan.FromHours(1);
        private const int RetryAfterSeconds = 5;

        private readonly OpsEventService _opsEvents;
        private readonly ILogger<ReportUploadGate> _logger;
        private long _nextAlertTicks;

        public ReportUploadGate(OpsEventService opsEvents, ILogger<ReportUploadGate> logger)
            : this(DefaultLimit, opsEvents, logger)
        {
        }

        internal ReportUploadGate(int limit, OpsEventService opsEvents, ILogger<ReportUploadGate> logger) : base(limit)
        {
            _opsEvents = opsEvents;
            _logger = logger;
        }

        /// <summary>The 429 for a caller who found every slot taken.</summary>
        public async Task<HttpResponseData> RefuseAsync(HttpRequestData req, string route, string? tenantId, string? upn)
        {
            _logger.LogWarning(
                "Report upload refused on {Route}: {Limit} uploads already in progress on this instance (tenant {TenantId})",
                route, Limit, tenantId);

            if (TakeAlert(DateTime.UtcNow))
                await _opsEvents.RecordReportUploadLimitReachedAsync(route, Limit, AlertWindow, tenantId, upn);

            return await req.ErrorAsync(HttpStatusCode.TooManyRequests, Constants.ApiErrorCodes.RateLimited,
                "Too many reports are being uploaded right now. Try again in a few seconds.",
                retryAfterSeconds: RetryAfterSeconds);
        }

        /// <summary>True for the first refusal of an alert window, the one that raises the ops event.</summary>
        internal bool TakeAlert(DateTime utcNow)
        {
            var next = Interlocked.Read(ref _nextAlertTicks);
            return utcNow.Ticks >= next
                && Interlocked.CompareExchange(ref _nextAlertTicks, utcNow.Add(AlertWindow).Ticks, next) == next;
        }
    }
}
