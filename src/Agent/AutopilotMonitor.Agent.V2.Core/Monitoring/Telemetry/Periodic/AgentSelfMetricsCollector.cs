#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic
{
    /// <summary>
    /// Measures the agent's own resource footprint: process CPU, memory, threads, handles,
    /// and HTTP network traffic. Emits agent_metrics_snapshot events via the standard event pipeline.
    /// No WMI, no PerformanceCounters — only Process properties and Interlocked counters.
    /// </summary>
    public class AgentSelfMetricsCollector : CollectorBase
    {
        // Pressure thresholds — once either is crossed within a session, a one-shot
        // `spool_pressure_detected` event is emitted with RequiresImmediateFlush=true
        // so the backend can surface the condition without waiting for the next snapshot
        // to be queried out of Storage. Sized so a healthy 6h provisioning session never
        // trips them; tripping is a strong "upload backlog is growing or downstream stalled"
        // signal worth investigating in the field.
        internal const int PressurePendingItemThreshold = 2000;
        internal const long PressureFileSizeBytesThreshold = 5L * 1024 * 1024; // 5 MB

        private readonly string _agentVersion;
        private readonly NetworkMetrics _networkMetrics;
        private readonly ITelemetrySpool? _telemetrySpool;

        // Previous sample for delta calculations
        private TimeSpan _prevCpuTime;
        private DateTime _prevWallTime;
        private NetworkMetricsSnapshot _prevNetSnapshot;

        // Fire-once flag for the spool-pressure event. Interlocked so concurrent Collect
        // ticks (shouldn't happen in CollectorBase, but cheap insurance) cannot double-emit.
        private int _pressureEmitted;

        public AgentSelfMetricsCollector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            NetworkMetrics networkMetrics,
            AgentLogger logger,
            string agentVersion = "unknown",
            int intervalSeconds = 60,
            ITelemetrySpool? telemetrySpool = null)
            : base(sessionId, tenantId, post, logger, intervalSeconds)
        {
            _networkMetrics = networkMetrics ?? throw new ArgumentNullException(nameof(networkMetrics));
            _agentVersion = string.IsNullOrWhiteSpace(agentVersion) ? "unknown" : agentVersion;
            _telemetrySpool = telemetrySpool;
        }

        protected override void OnBeforeStart()
        {
            // Prime the baseline for delta calculations
            try
            {
                var proc = Process.GetCurrentProcess();
                _prevCpuTime = proc.TotalProcessorTime;
                _prevWallTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to prime CPU baseline: {ex.Message}");
                _prevCpuTime = TimeSpan.Zero;
                _prevWallTime = DateTime.UtcNow;
            }
            _prevNetSnapshot = _networkMetrics.GetSnapshot();
        }

        protected override void Collect()
        {
            // Full path writes 18 keys: agent_version + process metrics (5: cpu, ws, private,
            // threads, handles) + spool stats (4) + network delta (8: requests, failures,
            // bytes_up/down, avg_latency, total_up/down/requests). cap=18 → HashHelpers.GetPrime(18)=23
            // buckets → no resize on the 18th key (cap=16 would land at 17 buckets and resize).
            var data = new Dictionary<string, object>(capacity: 18, StringComparer.Ordinal)
            {
                { "agent_version", _agentVersion }
            };
            var now = DateTime.UtcNow;

            // --- Process metrics (no WMI, no PerformanceCounter) ---
            try
            {
                var proc = Process.GetCurrentProcess();
                proc.Refresh(); // ensure fresh values

                // CPU %: (delta CPU time) / (delta wall time) / cores * 100
                var currentCpuTime = proc.TotalProcessorTime;
                var cpuDelta = currentCpuTime - _prevCpuTime;
                var wallDelta = now - _prevWallTime;

                if (wallDelta.TotalMilliseconds > 0)
                {
                    var cpuPercent = cpuDelta.TotalMilliseconds / wallDelta.TotalMilliseconds
                                     / Environment.ProcessorCount * 100.0;
                    data["agent_cpu_percent"] = Math.Round(cpuPercent, 2);
                }

                _prevCpuTime = currentCpuTime;
                _prevWallTime = now;

                // Memory
                data["agent_working_set_mb"] = Math.Round(proc.WorkingSet64 / (1024.0 * 1024), 1);
                data["agent_private_bytes_mb"] = Math.Round(proc.PrivateMemorySize64 / (1024.0 * 1024), 1);

                // Threads & handles
                data["agent_thread_count"] = proc.Threads.Count;
                data["agent_handle_count"] = proc.HandleCount;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Process metrics read failed: {ex.Message}");
            }

            // --- Spool stats (P2 telemetry) ---
            // _telemetrySpool is the live transport-layer spool, plumbed through
            // DefaultComponentFactory.SetTelemetrySpool from EnrollmentOrchestrator.Start
            // step 3. Null only on test fakes that don't construct a real spool.
            int pendingItemCount = 0;
            long spoolFileSizeBytes = 0L;
            if (_telemetrySpool != null)
            {
                try
                {
                    pendingItemCount = _telemetrySpool.PendingItemCount;
                    spoolFileSizeBytes = _telemetrySpool.SpoolFileSizeBytes;
                    var peakPending = _telemetrySpool.PeakPendingItemCount;
                    var totalEnqueued = _telemetrySpool.LastAssignedItemId + 1; // -1 sentinel → 0

                    data["spool_pending_item_count"] = pendingItemCount;
                    data["spool_peak_pending_item_count"] = peakPending;
                    data["spool_file_size_bytes"] = spoolFileSizeBytes;
                    data["spool_total_enqueued_count"] = totalEnqueued;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Spool metrics read failed: {ex.Message}");
                }
            }

            // --- Network delta ---
            try
            {
                var currentNet = _networkMetrics.GetSnapshot();
                var delta = currentNet.DeltaFrom(_prevNetSnapshot);
                _prevNetSnapshot = currentNet;

                data["net_requests"] = delta.Requests;
                data["net_failures"] = delta.Failures;
                data["net_bytes_up"] = delta.BytesUp;
                data["net_bytes_down"] = delta.BytesDown;
                data["net_avg_latency_ms"] = Math.Round(delta.AvgLatencyMs, 1);

                // Cumulative totals for easy "total cost of this session" view
                data["net_total_bytes_up"] = currentNet.TotalBytesUp;
                data["net_total_bytes_down"] = currentNet.TotalBytesDown;
                data["net_total_requests"] = currentNet.RequestCount;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Network metrics read failed: {ex.Message}");
            }

            if (data.Count > 0)
            {
                Post.Emit(new EnrollmentEvent
                {
                    SessionId = SessionId,
                    TenantId = TenantId,
                    Timestamp = now,
                    EventType = Constants.EventTypes.AgentMetricsSnapshot,
                    Severity = EventSeverity.Debug,
                    Source = "AgentSelfMetricsCollector",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Agent CPU: {(data.ContainsKey("agent_cpu_percent") ? data["agent_cpu_percent"] : "?")}%, " +
                              $"WS: {(data.ContainsKey("agent_working_set_mb") ? data["agent_working_set_mb"] : "?")} MB, " +
                              $"Net: {(data.ContainsKey("net_requests") ? data["net_requests"] : "?")} req, " +
                              $"\u2191{(data.ContainsKey("net_bytes_up") ? data["net_bytes_up"] : "?")} B, " +
                              $"\u2193{(data.ContainsKey("net_bytes_down") ? data["net_bytes_down"] : "?")} B",
                    Data = data
                });
            }

            // One-shot pressure event \u2014 fires once per session when the spool grows past
            // either threshold. ImmediateUpload=true so it shows up promptly on the
            // backend without waiting for the next batch drain.
            if (_telemetrySpool != null
                && (pendingItemCount > PressurePendingItemThreshold
                    || spoolFileSizeBytes > PressureFileSizeBytesThreshold)
                && Interlocked.CompareExchange(ref _pressureEmitted, 1, 0) == 0)
            {
                var pressureData = new Dictionary<string, object>(capacity: 8, StringComparer.Ordinal)
                {
                    { "pendingItemCount", pendingItemCount },
                    { "fileSizeBytes", spoolFileSizeBytes },
                    { "pendingThreshold", PressurePendingItemThreshold },
                    { "fileSizeThresholdBytes", PressureFileSizeBytesThreshold },
                    { "totalEnqueuedCount", _telemetrySpool.LastAssignedItemId + 1 },
                    { "lastUploadedItemId", _telemetrySpool.LastUploadedItemId },
                    { "ImmediateUpload", true }
                };

                Post.Emit(new EnrollmentEvent
                {
                    SessionId = SessionId,
                    TenantId = TenantId,
                    Timestamp = now,
                    EventType = Constants.EventTypes.SpoolPressureDetected,
                    Severity = EventSeverity.Warning,
                    Source = "AgentSelfMetricsCollector",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Telemetry spool pressure detected: pending={pendingItemCount}, " +
                              $"fileBytes={spoolFileSizeBytes} \u2014 upload may be stalled or session " +
                              $"is unusually long.",
                    Data = pressureData
                });

                Logger.Warning(
                    $"AgentSelfMetricsCollector: spool pressure detected " +
                    $"(pending={pendingItemCount}, fileBytes={spoolFileSizeBytes}). " +
                    $"Emitted spool_pressure_detected (one-shot per session).");
            }
        }
    }
}
