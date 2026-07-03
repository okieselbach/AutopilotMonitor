using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic
{
    /// <summary>
    /// Collects system performance metrics (CPU, memory, disk) on a configurable interval.
    /// Optional collector - toggled on/off via remote config.
    /// </summary>
    public class PerformanceCollector : CollectorBase
    {
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _diskQueueCounter;

        // MON-B10 — one-shot low-disk warning. disk_free_gb was previously only visible inside the
        // Debug performance_snapshot payload (filtered out of timelines), so a disk-starved enrollment
        // had no actionable signal. Emit a single disk_space_low Warning on the transition below the
        // threshold; re-arm only after free space recovers past a higher mark (hysteresis) so the
        // event stays state-change-only and never turns into a heartbeat.
        //
        // M3 (delta review 2026-07-02): the latch is seeded from + persisted via the
        // StartupEventGate — a memory-only flag re-warned (ImmediateUpload) after every agent
        // restart on a persistently disk-starved device (stall restarts: up to ~88×/session).
        private const double DiskLowThresholdGb = 2.0;
        private const double DiskRecoveryThresholdGb = 3.0;
        private const string DiskLowFingerprint = "low";
        private const string DiskRearmedFingerprint = "rearmed";
        private bool _diskLowWarned;
        private readonly Core.Persistence.StartupEventGate _startupGate;

        // Network throughput tracking
        private string _activeNicId;
        private string _activeNicName;
        private long _prevBytesSent;
        private long _prevBytesReceived;
        private DateTime _prevNetSampleTime;
        private bool _networkInitialized;

        public PerformanceCollector(string sessionId, string tenantId, InformationalEventPost post,
            AgentLogger logger, int intervalSeconds = 60,
            Core.Persistence.StartupEventGate startupGate = null)
            : base(sessionId, tenantId, post, logger, intervalSeconds)
        {
            _startupGate = startupGate;
            // Seed the hysteresis latch from persisted state so a restart on a still-starved
            // disk does not re-warn (read-only peek — no gate claim).
            _diskLowWarned = _startupGate?.HasFingerprint(Constants.EventTypes.DiskSpaceLow, DiskLowFingerprint) ?? false;
        }

        /// <summary>Use a 5-second warm-up delay so counters are primed before first read.</summary>
        protected override TimeSpan GetInitialDelay() => TimeSpan.FromSeconds(5);

        protected override void OnBeforeStart()
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _diskQueueCounter = new PerformanceCounter("PhysicalDisk", "Current Disk Queue Length", "_Total");

                // Initial read to prime the counters (first reading is always 0)
                _cpuCounter.NextValue();
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to initialize performance counters: {ex.Message}");
            }

            // Initialize network throughput baseline
            try
            {
                var activeNic = FindActiveNetworkInterface();
                if (activeNic != null)
                {
                    _activeNicId = activeNic.Id;
                    _activeNicName = activeNic.Description;
                    var stats = activeNic.GetIPStatistics();
                    _prevBytesSent = stats.BytesSent;
                    _prevBytesReceived = stats.BytesReceived;
                    _prevNetSampleTime = DateTime.UtcNow;
                    _networkInitialized = true;
                    Logger.Debug($"Network throughput tracking initialized on: {_activeNicName}");
                }
                else
                {
                    Logger.Debug("No active network interface found for throughput tracking");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Failed to initialize network throughput baseline: {ex.Message}");
            }
        }

        protected override void OnAfterStop()
        {
            _cpuCounter?.Dispose();
            _cpuCounter = null;
            _diskQueueCounter?.Dispose();
            _diskQueueCounter = null;
        }

        protected override void Collect()
        {
            // Full path writes up to 12 keys (cpu + 3 memory + disk_queue + 2 disk + 5 network).
            // cap=12 → HashHelpers.GetPrime(12)=17 buckets → no resize on the last network key
            // (cap=8 would land at 11 buckets and resize on the 12th).
            var data = new Dictionary<string, object>(capacity: 12, StringComparer.Ordinal);

            // CPU usage
            try
            {
                var cpuPercent = _cpuCounter?.NextValue() ?? 0;
                data["cpu_percent"] = Math.Round(cpuPercent, 1);
            }
            catch (Exception ex)
            {
                Logger.Debug($"CPU counter read failed: {ex.Message}");
            }

            // Memory info via GlobalMemoryStatusEx (kernel32)
            try
            {
                if (MemoryNativeMethods.TryGetMemoryInfo(out var availBytes, out var totalBytes, out var loadPercent)
                    && totalBytes > 0)
                {
                    data["memory_available_mb"] = Math.Round(availBytes / (1024.0 * 1024.0), 0);
                    data["memory_total_mb"] = Math.Round(totalBytes / (1024.0 * 1024.0), 0);
                    data["memory_used_percent"] = Math.Round((double)loadPercent, 1);
                }
                else
                {
                    Logger.Debug("GlobalMemoryStatusEx returned no data");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Memory query failed: {ex.Message}");
            }

            // Disk queue length
            try
            {
                var diskQueue = _diskQueueCounter?.NextValue() ?? 0;
                data["disk_queue_length"] = Math.Round(diskQueue, 1);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Disk queue counter read failed: {ex.Message}");
            }

            // Disk free space on system drive
            try
            {
                var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
                var driveInfo = new DriveInfo(systemDrive);
                var freeGb = driveInfo.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                var totalGb = driveInfo.TotalSize / (1024.0 * 1024 * 1024);
                data["disk_free_gb"] = Math.Round(freeGb, 1);
                data["disk_total_gb"] = Math.Round(totalGb, 1);

                // MON-B10 — surface a dedicated Warning the moment the drive crosses below the
                // threshold, instead of leaving the only signal buried in this Debug snapshot.
                if (EvaluateDiskLowTransition(freeGb, ref _diskLowWarned, out var rearmed))
                {
                    EmitDiskLowWarning(freeGb, totalGb, systemDrive);
                    PersistDiskLowState(DiskLowFingerprint);
                }
                else if (rearmed)
                {
                    PersistDiskLowState(DiskRearmedFingerprint);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Disk space query failed: {ex.Message}");
            }

            // Network throughput (bytes sent/received delta + rate)
            try
            {
                if (_networkInitialized)
                {
                    // Re-find the NIC by cached ID
                    NetworkInterface currentNic = null;
                    try
                    {
                        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (nic.Id == _activeNicId)
                            {
                                currentNic = nic;
                                break;
                            }
                        }
                    }
                    catch { /* swallow */ }

                    // If cached NIC is gone, try to find a new active one
                    if (currentNic == null || currentNic.OperationalStatus != OperationalStatus.Up)
                    {
                        currentNic = FindActiveNetworkInterface();
                        if (currentNic != null)
                        {
                            _activeNicId = currentNic.Id;
                            _activeNicName = currentNic.Description;
                            var freshStats = currentNic.GetIPStatistics();
                            _prevBytesSent = freshStats.BytesSent;
                            _prevBytesReceived = freshStats.BytesReceived;
                            _prevNetSampleTime = DateTime.UtcNow;
                            Logger.Debug($"Network throughput tracking switched to: {_activeNicName}");
                        }
                        else
                        {
                            _networkInitialized = false;
                            Logger.Debug("Active network interface lost, throughput tracking paused");
                        }
                    }

                    if (currentNic != null && currentNic.OperationalStatus == OperationalStatus.Up)
                    {
                        var stats = currentNic.GetIPStatistics();
                        var now = DateTime.UtcNow;
                        var elapsedSeconds = (now - _prevNetSampleTime).TotalSeconds;

                        if (elapsedSeconds > 0)
                        {
                            var deltaSent = stats.BytesSent - _prevBytesSent;
                            var deltaReceived = stats.BytesReceived - _prevBytesReceived;

                            // Guard against counter reset (adapter reconnect)
                            if (deltaSent < 0) deltaSent = 0;
                            if (deltaReceived < 0) deltaReceived = 0;

                            data["net_adapter"] = _activeNicName;
                            data["net_bytes_sent_delta"] = deltaSent;
                            data["net_bytes_received_delta"] = deltaReceived;
                            data["net_send_rate_kbps"] = Math.Round(deltaSent * 8.0 / 1000.0 / elapsedSeconds, 1);
                            data["net_receive_rate_kbps"] = Math.Round(deltaReceived * 8.0 / 1000.0 / elapsedSeconds, 1);
                        }

                        _prevBytesSent = stats.BytesSent;
                        _prevBytesReceived = stats.BytesReceived;
                        _prevNetSampleTime = now;
                    }
                }
                else
                {
                    // Try to re-initialize (e.g., network came up after boot)
                    var activeNic = FindActiveNetworkInterface();
                    if (activeNic != null)
                    {
                        _activeNicId = activeNic.Id;
                        _activeNicName = activeNic.Description;
                        var stats = activeNic.GetIPStatistics();
                        _prevBytesSent = stats.BytesSent;
                        _prevBytesReceived = stats.BytesReceived;
                        _prevNetSampleTime = DateTime.UtcNow;
                        _networkInitialized = true;
                        Logger.Debug($"Network throughput tracking re-initialized on: {_activeNicName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Network throughput collection failed: {ex.Message}");
            }

            if (data.Count > 0)
            {
                Post.Emit(new EnrollmentEvent
                {
                    SessionId = SessionId,
                    TenantId = TenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = Constants.EventTypes.PerformanceSnapshot,
                    Severity = EventSeverity.Debug,
                    Source = "PerformanceCollector",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"CPU: {(data.ContainsKey("cpu_percent") ? data["cpu_percent"] : "?")}%, " +
                              $"Memory: {(data.ContainsKey("memory_used_percent") ? data["memory_used_percent"] : "?")}%, " +
                              $"Disk Free: {(data.ContainsKey("disk_free_gb") ? data["disk_free_gb"] : "?")} GB" +
                              (data.ContainsKey("net_receive_rate_kbps")
                                  ? $", Net: \u2193{data["net_receive_rate_kbps"]} / \u2191{data["net_send_rate_kbps"]} kbps"
                                  : ""),
                    Data = data
                });
            }
        }

        /// <summary>
        /// State-change-only decision for the one-shot low-disk warning. Returns <c>true</c> exactly on
        /// the transition below <see cref="DiskLowThresholdGb"/>. Once warned the collector stays silent
        /// (no heartbeat) until free space recovers past <see cref="DiskRecoveryThresholdGb"/>, which
        /// re-arms it — so a single enrollment that expands then cleans its temp/download cache can warn
        /// again without flapping inside the 1 GB hysteresis band.
        /// </summary>
        /// <param name="freeGb">Current free space on the system drive, in GB.</param>
        /// <param name="alreadyWarned">Per-collector latch: <c>true</c> while a low-disk warning is in
        /// effect. Updated in place to reflect the new armed state.</param>
        internal static bool EvaluateDiskLowTransition(double freeGb, ref bool alreadyWarned, out bool rearmed)
        {
            rearmed = false;
            if (freeGb < DiskLowThresholdGb)
            {
                if (alreadyWarned) return false; // still low, already warned → stay quiet
                alreadyWarned = true;
                return true;
            }

            if (freeGb >= DiskRecoveryThresholdGb && alreadyWarned)
            {
                alreadyWarned = false; // recovered with margin → re-arm for a future drop
                rearmed = true;        // caller persists the re-arm so it survives restarts (M3)
            }

            return false;
        }

        /// <summary>
        /// M3: records the disk-low latch state in the StartupEventGate so it survives agent
        /// restarts. Uses the gate's claim+commit pair as a persisted state cell — there is no
        /// separate event emission here (the Warning already went out for the "low" transition;
        /// the re-arm transition is state-only by design).
        /// </summary>
        private void PersistDiskLowState(string fingerprint)
        {
            if (_startupGate == null) return;
            if (_startupGate.ShouldEmit(Constants.EventTypes.DiskSpaceLow, fingerprint))
                _startupGate.MarkEmitted(Constants.EventTypes.DiskSpaceLow);
        }

        private void EmitDiskLowWarning(double freeGb, double totalGb, string drive)
        {
            var data = new Dictionary<string, object>(capacity: 4, StringComparer.Ordinal)
            {
                ["disk_free_gb"] = Math.Round(freeGb, 1),
                ["disk_total_gb"] = Math.Round(totalGb, 1),
                ["threshold_gb"] = DiskLowThresholdGb,
                ["drive"] = drive ?? "?",
            };

            Post.Emit(new EnrollmentEvent
            {
                SessionId = SessionId,
                TenantId = TenantId,
                Timestamp = DateTime.UtcNow,
                EventType = Constants.EventTypes.DiskSpaceLow,
                Severity = EventSeverity.Warning,
                Source = "PerformanceCollector",
                Phase = EnrollmentPhase.Unknown,
                ImmediateUpload = true,
                Message = $"Low disk space on {drive ?? "?"}: {Math.Round(freeGb, 1)} GB free " +
                          $"(below {DiskLowThresholdGb:0} GB threshold)",
                Data = data
            });

            Logger.Warning(
                $"Low disk space on {drive ?? "?"}: {Math.Round(freeGb, 1)} GB free " +
                $"(threshold {DiskLowThresholdGb:0} GB)");
        }

        /// <summary>
        /// Finds the active network interface: Up, not Loopback/Tunnel, has a non-0.0.0.0 gateway.
        /// </summary>
        private static NetworkInterface FindActiveNetworkInterface()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var nic in interfaces)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    var ipProps = nic.GetIPProperties();
                    foreach (var gw in ipProps.GatewayAddresses)
                    {
                        if (gw.Address.ToString() != "0.0.0.0")
                            return nic;
                    }
                }
            }
            catch
            {
                // Caller handles null
            }
            return null;
        }
    }
}
