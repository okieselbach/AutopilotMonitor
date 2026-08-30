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
        // to be queried out of Storage. Both measure the PENDING backlog (items not yet
        // marked uploaded), never the append-only spool file size — a healthy long session
        // grows the file past any fixed size while its backlog stays near zero (field data
        // 2026-08: 99/100 pressure events were file-size-only false positives). Tripping is
        // therefore a genuine "upload is stalled or falling behind" signal.
        internal const int PressurePendingItemThreshold = 2000;
        internal const long PressurePendingBytesThreshold = 5L * 1024 * 1024; // 5 MB

        private readonly string _agentVersion;
        private readonly NetworkMetrics _networkMetrics;
        private readonly ITelemetrySpool? _telemetrySpool;
        private readonly Func<Monitoring.Enrollment.Ime.ImeTrackerHealth?>? _imeTrackerHealthProbe;

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
            ITelemetrySpool? telemetrySpool = null,
            Func<Monitoring.Enrollment.Ime.ImeTrackerHealth?>? imeTrackerHealthProbe = null)
            : base(sessionId, tenantId, post, logger, intervalSeconds)
        {
            _networkMetrics = networkMetrics ?? throw new ArgumentNullException(nameof(networkMetrics));
            _agentVersion = string.IsNullOrWhiteSpace(agentVersion) ? "unknown" : agentVersion;
            _telemetrySpool = telemetrySpool;
            _imeTrackerHealthProbe = imeTrackerHealthProbe;
        }

        protected override void OnBeforeStart()
        {
            // Prime the baseline for delta calculations
            try
            {
                using var proc = Process.GetCurrentProcess();
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
            // Full path writes 29 keys: agent_version + process metrics (5: cpu, ws, private,
            // threads, handles) + spool stats (5) + network delta (9: requests, failures,
            // bytes_up/down, avg_latency, total_up/down/requests/latency) + IME tracker health
            // (9). cap=29 → HashHelpers.GetPrime(29)=29 buckets → no resize on the last key.
            var data = new Dictionary<string, object>(capacity: 29, StringComparer.Ordinal)
            {
                { "agent_version", _agentVersion }
            };
            var now = DateTime.UtcNow;

            // --- Process metrics (no WMI, no PerformanceCounter) ---
            try
            {
                using var proc = Process.GetCurrentProcess();
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
            // _telemetrySpool is the live transport-layer spool, passed by
            // EnrollmentOrchestrator.Start into IComponentFactory.CreateCollectorHosts
            // (ARCH-F4). Null only on test fakes that don't construct a real spool.
            int pendingItemCount = 0;
            long pendingBytes = 0L;
            long spoolFileSizeBytes = 0L;
            if (_telemetrySpool != null)
            {
                try
                {
                    pendingItemCount = _telemetrySpool.PendingItemCount;
                    pendingBytes = _telemetrySpool.PendingBytes;
                    spoolFileSizeBytes = _telemetrySpool.SpoolFileSizeBytes;
                    var peakPending = _telemetrySpool.PeakPendingItemCount;
                    var totalEnqueued = _telemetrySpool.LastAssignedItemId + 1; // -1 sentinel → 0

                    data["spool_pending_item_count"] = pendingItemCount;
                    data["spool_pending_bytes"] = pendingBytes;
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
                // Cumulative latency sum: total/requests = session-wide average HTTP
                // round-trip. The backend projects that average onto the Session row
                // (AvgApiLatencyMs) — cumulative counters make the write idempotent.
                data["net_total_latency_ms"] = currentNet.TotalLatencyMs;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Network metrics read failed: {ex.Message}");
            }

            // --- IME log tracker health ---
            // Read-only probe over ImeLogHost (same pattern as the registry observer's
            // trackerStateProbe). The counters' fleet expectation is 0 for everything but
            // lines_read/entries_matched — a non-zero skip counter is the "tracker dropped
            // work" trace that used to exist only in the client log. Null when the IME host is
            // not running (test fakes, IME tracking disabled).
            if (_imeTrackerHealthProbe != null)
            {
                try
                {
                    var health = _imeTrackerHealthProbe();
                    if (health != null)
                    {
                        data["ime_files_tailed"] = health.FilesTailed;
                        data["ime_backlog_bytes"] = health.BacklogBytes;
                        data["ime_lines_read"] = health.LinesRead;
                        data["ime_entries_matched"] = health.EntriesMatched;
                        data["ime_oversized_lines"] = health.OversizedLines;
                        data["ime_regex_timeouts"] = health.RegexTimeouts;
                        data["ime_line_budget_breaks"] = health.BudgetBreaks;
                        data["ime_held_tails"] = health.HeldTails;
                        data["ime_unanchored_patterns"] = health.UnanchoredPatterns;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"IME tracker health read failed: {ex.Message}");
                }
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

            // One-shot pressure event \u2014 fires once per session when the pending backlog
            // grows past either threshold. ImmediateUpload=true so it shows up promptly on
            // the backend without waiting for the next batch drain.
            if (_telemetrySpool != null
                && (pendingItemCount > PressurePendingItemThreshold
                    || pendingBytes > PressurePendingBytesThreshold)
                && Interlocked.CompareExchange(ref _pressureEmitted, 1, 0) == 0)
            {
                var pressureData = new Dictionary<string, object>(capacity: 8, StringComparer.Ordinal)
                {
                    { "pendingItemCount", pendingItemCount },
                    { "pendingBytes", pendingBytes },
                    { "fileSizeBytes", spoolFileSizeBytes },   // informational only, not a trigger
                    { "pendingThreshold", PressurePendingItemThreshold },
                    { "pendingBytesThreshold", PressurePendingBytesThreshold },
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
                    Message = $"Telemetry spool pressure detected: pending={pendingItemCount} items / " +
                              $"{pendingBytes} bytes not yet uploaded \u2014 upload is stalled or " +
                              $"falling behind.",
                    Data = pressureData
                });

                Logger.Warning(
                    $"AgentSelfMetricsCollector: spool pressure detected " +
                    $"(pending={pendingItemCount}, pendingBytes={pendingBytes}). " +
                    $"Emitted spool_pressure_detected (one-shot per session).");
            }
        }
    }
}
