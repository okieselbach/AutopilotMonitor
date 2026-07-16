using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo
{
    /// <summary>
    /// Collects static and semi-static device information at enrollment startup and end.
    /// Emits device info events (os_info, boot_time, network_adapters, dns_configuration,
    /// proxy_configuration, autopilot_profile, enrollment_type_detected, secureboot_status,
    /// bitlocker_status, aad_join_status) via the provided emitEvent action.
    /// </summary>
    public partial class DeviceInfoCollector
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly ISignalIngressSink _signalIngress;
        private readonly IClock _clock;
        private readonly Persistence.StartupEventGate _startupGate;

        private const string RegKeyWindowsCurrentVersion = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        // Restart dedup (StartupEventGate) — exemptions. boot_time is genuinely new per boot
        // (boot timestamp + uptime); wifi_signal_info is a live diagnostic snapshot whose
        // signal-% churns by nature (kept ungated, analogous to power_state_check).
        private static readonly HashSet<string> GateExemptEventTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            Constants.EventTypes.BootTime,
            Constants.EventTypes.WifiSignalInfo,
        };

        // Volatile top-level payload fields excluded from the change fingerprint: the negotiated
        // (WiFi) link speed varies per association without the adapter actually changing — keeping
        // it in the fingerprint would defeat the dedup on every WiFi device.
        private static readonly Dictionary<string, string[]> GateFingerprintExcludedFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { Constants.EventTypes.NetworkInterfaceInfo, new[] { "linkSpeedMbps" } },
            // Probe latency/detail vary per run and per-ID event counts grow monotonically (the
            // 100 "waiting for profile" heartbeat) without the SITUATION changing — only a flipped
            // ztdVerdict / reachability / registry evidence justifies a re-emission after restart.
            { Constants.EventTypes.AutopilotProfileMissing, new[] { "ztdEndpointLatencyMs", "ztdEndpointDetail", "ztdEventIdCounts" } },
        };

        public DeviceInfoCollector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            ISignalIngressSink signalIngress = null,
            IClock clock = null,
            Persistence.StartupEventGate startupGate = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId  = tenantId  ?? throw new ArgumentNullException(nameof(tenantId));
            _post      = post      ?? throw new ArgumentNullException(nameof(post));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
            _signalIngress = signalIngress;
            _clock = clock;
            _startupGate = startupGate;
        }

        /// <summary>
        /// Runs all device info collectors at agent startup.
        /// Returns the detected enrollment type, Hybrid Join flag, and ESP skip configuration from registry.
        /// </summary>
        public (string enrollmentType, bool isHybridJoin, bool? skipUserStatusPage, bool? skipDeviceStatusPage, int? autopilotMode, bool hasAadJoinedUser, bool isFooUserDetected) CollectAll()
        {
            _logger.Info("EnrollmentTracker: collecting device info (at start)");

            CollectOSInfoAndBootTime();
            CollectNetworkAndDnsConfiguration();
            CollectProxyConfiguration();
            var profileResult = CollectAutopilotProfile();
            var espConfig = CollectEspConfiguration();
            CollectSecureBootStatus();
            CollectBitLockerStatus();
            CollectTpmStatus();
            CollectAadJoinStatus();
            CollectActiveNetworkInterfaceInfo();
            CollectHardwareSpec();

            return (profileResult.enrollmentType, profileResult.isHybridJoin, espConfig.skipUserStatusPage, espConfig.skipDeviceStatusPage, profileResult.autopilotMode, HasAadJoinedUser, IsFooUserDetected);
        }

        /// <summary>
        /// Re-collects enrollment-dependent device info that was likely empty/incomplete at startup.
        /// Called when DeviceSetup phase is first detected (ESP started = MDM enrollment + AAD join complete).
        /// Re-emits: aad_join_status, autopilot_profile, esp_config_detected, tpm_status.
        /// </summary>
        public void CollectAtEnrollmentStart()
        {
            _logger.Info("EnrollmentTracker: collecting device info (at enrollment start)");

            CollectAadJoinStatus();
            CollectAutopilotProfile();
            CollectEspConfiguration();
            CollectTpmStatus();
        }

        /// <summary>
        /// Re-collects device info that may change during enrollment (e.g. BitLocker enabled via policy).
        /// Called at enrollment complete / FinalizingSetup transition to capture final state.
        /// </summary>
        public void CollectAtEnd()
        {
            _logger.Info("EnrollmentTracker: collecting device info (at end)");

            // BitLocker can be enabled during enrollment via policy
            CollectBitLockerStatus();

            // Re-collect active network interface info (link speed, WiFi signal may have changed)
            CollectActiveNetworkInterfaceInfo();
        }

        /// <summary>
        /// Collects OS info and boot time in a single WMI query against Win32_OperatingSystem.
        /// Previously these were three separate queries (Caption, LastBootUpTime via two searchers);
        /// consolidated to a single round-trip.
        /// Emits two events (os_info, boot_time) for backward compatibility.
        /// </summary>
        private void CollectOSInfoAndBootTime()
        {
            string osName = string.Empty;
            var bootData = new Dictionary<string, object>();

            // Single WMI query for both Caption (OS name) and LastBootUpTime
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Caption, LastBootUpTime FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        osName = obj["Caption"]?.ToString() ?? string.Empty;

                        var lastBootUpTimeStr = obj["LastBootUpTime"]?.ToString();
                        if (!string.IsNullOrEmpty(lastBootUpTimeStr))
                        {
                            var bootTimeLocal = ManagementDateTimeConverter.ToDateTime(lastBootUpTimeStr);
                            var bootTimeUtc = DateTime.SpecifyKind(bootTimeLocal.ToUniversalTime(), DateTimeKind.Utc);

                            bootData["bootTimeUtc"] = bootTimeUtc.ToString("o");
                            bootData["bootTime"] = bootTimeUtc.ToString("o");

                            var uptime = DateTime.UtcNow - bootTimeUtc;
                            bootData["uptimeMinutes"] = (int)uptime.TotalMinutes;
                            bootData["uptimeHours"] = uptime.TotalHours;

                            _logger.Debug($"Boot time (UTC): {bootTimeUtc:o}, Uptime: {uptime.TotalMinutes:F1} minutes");
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to query Win32_OperatingSystem: {ex.Message}");
            }

            // Emit os_info event
            try
            {
                // Read individual registry values; reuse DeviceInfoProvider helpers where possible
                var edition = (Registry.GetValue(RegKeyWindowsCurrentVersion, "EditionID", string.Empty) ?? string.Empty).ToString();
                var compositionEdition = (Registry.GetValue(RegKeyWindowsCurrentVersion, "CompositionEditionID", string.Empty) ?? string.Empty).ToString();
                var currentBuild = (Registry.GetValue(RegKeyWindowsCurrentVersion, "CurrentBuild", string.Empty) ?? string.Empty).ToString();
                var buildBranch = (Registry.GetValue(RegKeyWindowsCurrentVersion, "BuildBranch", string.Empty) ?? string.Empty).ToString();
                var displayVersion = DeviceInfoProvider.GetOsDisplayVersion();
                var buildRevision = (Registry.GetValue(RegKeyWindowsCurrentVersion, "UBR", string.Empty) ?? string.Empty).ToString();

                var data = new Dictionary<string, object>
                {
                    { "version", Environment.OSVersion.Version.ToString() },
                    { "osVersion", osName },
                    { "edition", edition },
                    { "compositionEdition", compositionEdition },
                    { "currentBuild", currentBuild },
                    { "buildBranch", buildBranch },
                    { "displayVersion", displayVersion },
                    { "buildRevision", buildRevision }
                };

                var osVersion = osName;

                var message = string.IsNullOrWhiteSpace(osVersion) ? "OS information collected" : osVersion;
                if (!string.IsNullOrWhiteSpace(displayVersion))
                {
                    message += $" {displayVersion}";
                }

                if (!string.IsNullOrWhiteSpace(currentBuild))
                {
                    message += string.IsNullOrWhiteSpace(buildRevision)
                        ? $" (Build {currentBuild})"
                        : $" (Build {currentBuild}.{buildRevision})";
                }

                EmitDeviceInfoEvent(Constants.EventTypes.OsInfo, message, data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect OS info: {ex.Message}");
            }

            // Emit boot_time event
            try
            {
                if (!bootData.ContainsKey("bootTime"))
                {
                    bootData["note"] = "Boot time could not be determined";
                }

                EmitDeviceInfoEvent(Constants.EventTypes.BootTime,
                    bootData.ContainsKey("bootTime")
                        ? $"Last boot: {bootData["bootTime"]}"
                        : "Boot time unavailable",
                    bootData);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to emit boot time event: {ex.Message}");
            }
        }

        /// <summary>
        /// Collects network adapter and DNS configuration in a single WMI query.
        /// Previously these were two separate queries against Win32_NetworkAdapterConfiguration;
        /// consolidated to halve the WMI round-trips (each creates a new COM connection).
        /// Emits two events (network_adapters, dns_configuration) for backward compatibility.
        /// </summary>
    }
}
