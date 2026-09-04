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
    /// <para>
    /// FLOOD CONTROL. Any holder of a valid token for the API (every signed-in tenant user) can produce
    /// these denials at will, so an unbounded alarm would hand an attacker the operator's attention as a
    /// denial-of-service target. Two caps, both per instance:
    /// <list type="bullet">
    /// <item>one event per CALLER per hour, whatever route or tool they try — a sweep over every GA route
    /// is one event, not one per route (every attempt still lands in the Warning trace);</item>
    /// <item>at most <see cref="MaxEventsPerWindow"/> events per hour in total; the next denial produces ONE
    /// Critical "storm" marker and everything after it stays trace-only until the window resets.</item>
    /// </list>
    /// Worst case per instance and hour: <see cref="MaxEventsPerWindow"/> + 1 pushes. Flex Consumption
    /// fans a burst over several instances, so the fleet-wide figure is that times the instances that saw
    /// traffic — bounded, and the storm marker is itself the stronger signal.
    /// </para>
    /// </summary>
    public sealed class PrivilegedDenialReporter : IPrivilegedDenialReporter
    {
        /// <summary>Per-caller and per-instance budget window.</summary>
        internal static readonly TimeSpan Window = TimeSpan.FromHours(1);
        /// <summary>Normal events an instance emits per window before it switches to one storm marker.</summary>
        internal const int MaxEventsPerWindow = 5;

        private const string BudgetKey = "prd:budget";

        private readonly OpsEventService _opsEvents;
        private readonly IMemoryCache _cache;
        private readonly ILogger<PrivilegedDenialReporter> _logger;
        private readonly object _gate = new();

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
                Task? write;
                lock (_gate)
                {
                    // Per caller, not per path: the key is the identity, so a scripted sweep over the
                    // whole GA surface yields one event carrying the first path it touched.
                    var callerKey = $"prd:{denial.CallerId}";
                    if (_cache.TryGetValue(callerKey, out _))
                        return;

                    // Per-instance budget, FIXED window from the first event (IMemoryCache is per Function
                    // instance — deliberately no storage round-trip on a deny path; see the class comment
                    // for the fleet arithmetic).
                    var now = DateTimeOffset.UtcNow;
                    var budget = _cache.TryGetValue<BudgetWindow>(BudgetKey, out var open) && open != null && open.EndsAt > now
                        ? open
                        : new BudgetWindow(now.Add(Window));
                    budget.Count++;
                    _cache.Set(BudgetKey, budget, budget.EndsAt - now);

                    var count = budget.Count;
                    if (count > MaxEventsPerWindow + 1)
                        return; // storm already marked this window: trace-only from here

                    _cache.Set(callerKey, true, Window);
                    write = count <= MaxEventsPerWindow
                        ? _opsEvents.RecordPrivilegedRouteDeniedAsync(denial)
                        : _opsEvents.RecordPrivilegedRouteDenialStormAsync(denial, MaxEventsPerWindow, Window);
                }

                _ = write.ContinueWith(
                    t => _logger.LogWarning(t.Exception?.InnerException, "PrivilegedRouteDenied ops event failed"),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                // Never let the observer break the deny response.
                _logger.LogWarning(ex, "PrivilegedRouteDenied reporting failed");
            }
        }

        private sealed class BudgetWindow
        {
            public BudgetWindow(DateTimeOffset endsAt) => EndsAt = endsAt;
            public DateTimeOffset EndsAt { get; }
            public int Count { get; set; }
        }
    }
}
