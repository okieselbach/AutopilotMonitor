using System.Threading;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    /// <summary>
    /// Thread-safe counters for HTTP request/response tracking.
    /// Incremented by BackendApiClient on every request, read by AgentSelfMetricsCollector.
    /// All operations are lock-free (Interlocked).
    /// </summary>
    public class NetworkMetrics
    {
        private long _requestCount;
        private long _failureCount;
        private long _totalBytesUp;
        private long _totalBytesDown;
        private long _totalLatencyMs;
        private long _okWithoutEchoCount;

        public void RecordRequest(long bytesUp, long bytesDown, long latencyMs, bool failed)
        {
            Interlocked.Increment(ref _requestCount);
            Interlocked.Add(ref _totalBytesUp, bytesUp);
            Interlocked.Add(ref _totalBytesDown, bytesDown);
            Interlocked.Add(ref _totalLatencyMs, latencyMs);
            if (failed)
                Interlocked.Increment(ref _failureCount);
        }

        /// <summary>
        /// Counts a 2xx telemetry upload whose response did not echo the request's
        /// <c>X-Correlation-ID</c>: something other than the backend may have answered (a block page
        /// served with 200). Observed only — the upload still counts as a success.
        /// </summary>
        public void RecordOkWithoutEcho() => Interlocked.Increment(ref _okWithoutEchoCount);

        /// <summary>Cumulative <see cref="RecordOkWithoutEcho"/> count since the process started.</summary>
        public long OkWithoutEchoCount => Interlocked.Read(ref _okWithoutEchoCount);

        /// <summary>
        /// Returns an immutable snapshot of all counters for delta calculation.
        /// </summary>
        public NetworkMetricsSnapshot GetSnapshot()
        {
            return new NetworkMetricsSnapshot(
                Interlocked.Read(ref _requestCount),
                Interlocked.Read(ref _failureCount),
                Interlocked.Read(ref _totalBytesUp),
                Interlocked.Read(ref _totalBytesDown),
                Interlocked.Read(ref _totalLatencyMs)
            );
        }
    }

    /// <summary>
    /// Immutable snapshot of network counters at a point in time.
    /// Used for delta calculation between two collection intervals.
    /// </summary>
    public class NetworkMetricsSnapshot
    {
        public long RequestCount { get; }
        public long FailureCount { get; }
        public long TotalBytesUp { get; }
        public long TotalBytesDown { get; }
        public long TotalLatencyMs { get; }

        public NetworkMetricsSnapshot(long requestCount, long failureCount,
            long totalBytesUp, long totalBytesDown, long totalLatencyMs)
        {
            RequestCount = requestCount;
            FailureCount = failureCount;
            TotalBytesUp = totalBytesUp;
            TotalBytesDown = totalBytesDown;
            TotalLatencyMs = totalLatencyMs;
        }

        /// <summary>
        /// Computes the delta between this snapshot and a previous one.
        /// </summary>
        public NetworkMetricsDelta DeltaFrom(NetworkMetricsSnapshot previous)
        {
            var requests = RequestCount - previous.RequestCount;
            var failures = FailureCount - previous.FailureCount;
            var bytesUp = TotalBytesUp - previous.TotalBytesUp;
            var bytesDown = TotalBytesDown - previous.TotalBytesDown;
            var latencyDelta = TotalLatencyMs - previous.TotalLatencyMs;
            var avgLatency = requests > 0 ? (double)latencyDelta / requests : 0;

            return new NetworkMetricsDelta(requests, failures, bytesUp, bytesDown, avgLatency);
        }
    }

    /// <summary>
    /// Computed delta values between two snapshots — ready for emission as event data.
    /// </summary>
    public class NetworkMetricsDelta
    {
        public long Requests { get; }
        public long Failures { get; }
        public long BytesUp { get; }
        public long BytesDown { get; }
        public double AvgLatencyMs { get; }

        public NetworkMetricsDelta(long requests, long failures,
            long bytesUp, long bytesDown, double avgLatencyMs)
        {
            Requests = requests;
            Failures = failures;
            BytesUp = bytesUp;
            BytesDown = bytesDown;
            AvgLatencyMs = avgLatencyMs;
        }
    }
}
