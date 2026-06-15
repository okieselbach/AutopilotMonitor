#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Runtime
{
    /// <summary>
    /// M4.6.γ — event-shape tests for the Legacy-parity startup probes. We cover the pure event
    /// builders directly; the end-to-end probe runner itself needs live HTTP/UDP/tzutil (no value
    /// in unit testing — excercised via the V2-agent test-VM smoke run deferred to Follow-Up).
    /// </summary>
    public sealed class StartupEnvironmentProbesTests
    {
        private static AgentConfiguration Config(bool geoEnabled = true, bool tzAutoSet = false, string? ntpServer = null) =>
            new AgentConfiguration
            {
                SessionId = "S1",
                TenantId = "T1",
                EnableGeoLocation = geoEnabled,
                EnableTimezoneAutoSet = tzAutoSet,
                NtpServer = ntpServer,
            };

        // ================================================================= Geo success

        [Fact]
        public void BuildGeoEvent_returns_info_device_location_event_with_all_fields()
        {
            var cfg = Config();
            var loc = new GeoLocationResult
            {
                Country = "DE",
                Region = "BW",
                City = "Stuttgart",
                Loc = "48.7,9.1",
                Timezone = "Europe/Berlin",
                Source = "ipinfo.io",
            };

            var evt = StartupEnvironmentProbes.BuildGeoEvent(cfg, loc);

            Assert.Equal("device_location", evt.EventType);
            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.Equal("StartupEnvironmentProbes", evt.Source);
            Assert.Equal("S1", evt.SessionId);
            Assert.Equal("T1", evt.TenantId);
            Assert.True(evt.ImmediateUpload);
            Assert.Contains("Stuttgart", evt.Message);
            Assert.Equal("DE", evt.Data["country"]);
            Assert.Equal("ipinfo.io", evt.Data["source"]);
        }

        // ================================================================= Outbound IP

        [Fact]
        public void BuildOutboundIpEvent_returns_trace_outbound_ip_event_with_ip_and_source()
        {
            var cfg = Config();
            var loc = new GeoLocationResult
            {
                // RFC 5737 documentation range — never a real address.
                Ip = "203.0.113.7",
                Source = "ipinfo.io",
            };

            var evt = StartupEnvironmentProbes.BuildOutboundIpEvent(cfg, loc);

            Assert.Equal("outbound_ip", evt.EventType);
            Assert.Equal(EventSeverity.Trace, evt.Severity);
            Assert.Equal("StartupEnvironmentProbes", evt.Source);
            Assert.Equal("S1", evt.SessionId);
            Assert.Equal("T1", evt.TenantId);
            Assert.Contains("203.0.113.7", evt.Message);
            Assert.Equal("203.0.113.7", evt.Data["ip"]);
            Assert.Equal("ipinfo.io", evt.Data["source"]);
        }

        // ================================================================= Geo failure

        [Fact]
        public void BuildGeoFailureEvent_returns_warning_agent_trace_with_provider_errors()
        {
            var cfg = Config();
            var attempt = new GeoLocationAttemptResult
            {
                PrimaryError = "connection refused",
                PrimaryRetryError = "DNS failure",
                FallbackError = "timeout",
            };

            var evt = StartupEnvironmentProbes.BuildGeoFailureEvent(cfg, attempt);

            Assert.Equal("agent_trace", evt.EventType);
            Assert.Equal(EventSeverity.Warning, evt.Severity);
            Assert.Equal("geo_location_failed", evt.Data["decision"]);
            Assert.Equal("connection refused", evt.Data["primaryError"]);
            Assert.Equal("DNS failure", evt.Data["primaryRetryError"]);
            Assert.Equal("timeout", evt.Data["fallbackError"]);
        }

        [Fact]
        public void BuildGeoFailureEvent_handles_null_attempt_gracefully()
        {
            var evt = StartupEnvironmentProbes.BuildGeoFailureEvent(Config(), attempt: null);

            Assert.Equal("agent_trace", evt.EventType);
            Assert.Equal("unknown", evt.Data["primaryError"]);
            Assert.Equal("unknown", evt.Data["primaryRetryError"]);
            Assert.Equal("unknown", evt.Data["fallbackError"]);
        }

        // ================================================================= Timezone

        [Fact]
        public void BuildTimezoneEvent_info_on_success()
        {
            var tz = new TimezoneSetResult
            {
                Success = true,
                IanaTimezone = "Europe/Berlin",
                WindowsTimezoneId = "W. Europe Standard Time",
                PreviousTimezone = "UTC",
            };

            var evt = StartupEnvironmentProbes.BuildTimezoneEvent(Config(), tz);

            Assert.Equal("timezone_auto_set", evt.EventType);
            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.Contains("W. Europe Standard Time", evt.Message);
            Assert.Equal(true, evt.Data["success"]);
        }

        [Fact]
        public void BuildTimezoneEvent_warning_on_failure()
        {
            var tz = new TimezoneSetResult
            {
                Success = false,
                IanaTimezone = "Europe/Berlin",
                Error = "tzutil failed",
            };

            var evt = StartupEnvironmentProbes.BuildTimezoneEvent(Config(), tz);

            Assert.Equal(EventSeverity.Warning, evt.Severity);
            Assert.Contains("tzutil failed", evt.Message);
            Assert.Equal("tzutil failed", evt.Data["error"]);
        }

        // ================================================================= NTP

        [Fact]
        public void BuildNtpEvent_info_for_small_offset()
        {
            var r = new NtpCheckResult
            {
                Success = true,
                OffsetSeconds = 2.3,
                NtpTime = new System.DateTime(2026, 4, 21, 10, 0, 0, System.DateTimeKind.Utc),
                LocalTime = new System.DateTime(2026, 4, 21, 10, 0, 2, System.DateTimeKind.Utc),
            };

            var evt = StartupEnvironmentProbes.BuildNtpEvent(Config(), "time.windows.com", r);

            Assert.Equal("ntp_time_check", evt.EventType);
            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.Equal("time.windows.com", evt.Data["ntpServer"]);
            Assert.Equal(2.3, evt.Data["offsetSeconds"]);
        }

        [Fact]
        public void BuildNtpEvent_warning_for_large_offset()
        {
            var r = new NtpCheckResult
            {
                Success = true,
                OffsetSeconds = 120.0,
                NtpTime = System.DateTime.UtcNow,
                LocalTime = System.DateTime.UtcNow,
            };

            var evt = StartupEnvironmentProbes.BuildNtpEvent(Config(), "time.windows.com", r);

            Assert.Equal(EventSeverity.Warning, evt.Severity);
        }

        [Fact]
        public void BuildNtpEvent_warning_on_failure_with_error_detail()
        {
            var r = new NtpCheckResult
            {
                Success = false,
                Error = "DNS resolution failed",
            };

            var evt = StartupEnvironmentProbes.BuildNtpEvent(Config(), "time.windows.com", r);

            Assert.Equal(EventSeverity.Warning, evt.Severity);
            Assert.Contains("DNS resolution failed", evt.Message);
            Assert.Equal("DNS resolution failed", evt.Data["error"]);
        }

        // ================================================================= PowerState

        [Fact]
        public void BuildPowerStateEvent_on_ac_with_battery_emits_info()
        {
            var r = new PowerStateResult
            {
                OnAcPower = true,
                HasBattery = true,
                BatteryPercent = 95,
                IsCharging = true,
                BatteryLifeMinutes = 180,
            };

            var evt = StartupEnvironmentProbes.BuildPowerStateEvent(Config(), r);

            Assert.Equal("power_state_check", evt.EventType);
            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.Equal("StartupEnvironmentProbes", evt.Source);
            Assert.Equal(EnrollmentPhase.Unknown, evt.Phase);
            Assert.Contains("AC", evt.Message);
            Assert.Contains("95%", evt.Message);
            Assert.Equal(true, evt.Data["onAcPower"]);
            Assert.Equal(true, evt.Data["hasBattery"]);
            Assert.Equal(95, evt.Data["batteryPercent"]);
            Assert.Equal(true, evt.Data["isCharging"]);
            Assert.Equal(180, evt.Data["batteryLifeMinutes"]);
        }

        [Fact]
        public void BuildPowerStateEvent_desktop_no_battery_emits_info()
        {
            var r = new PowerStateResult
            {
                OnAcPower = true,
                HasBattery = false,
            };

            var evt = StartupEnvironmentProbes.BuildPowerStateEvent(Config(), r);

            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.Contains("no battery", evt.Message);
            Assert.Equal(false, evt.Data["hasBattery"]);
            Assert.Equal("unknown", evt.Data["batteryPercent"]);
            Assert.Equal("unknown", evt.Data["batteryLifeMinutes"]);
        }

        [Fact]
        public void BuildPowerStateEvent_on_battery_high_charge_emits_info()
        {
            var r = new PowerStateResult
            {
                OnAcPower = false,
                HasBattery = true,
                BatteryPercent = 92,
                IsCharging = false,
                BatteryLifeMinutes = 240,
            };

            var evt = StartupEnvironmentProbes.BuildPowerStateEvent(Config(), r);

            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.Contains("on battery", evt.Message);
            Assert.Contains("92%", evt.Message);
            Assert.DoesNotContain("low charge", evt.Message);
            Assert.Equal(false, evt.Data["onAcPower"]);
            Assert.Equal(92, evt.Data["batteryPercent"]);
        }

        [Fact]
        public void BuildPowerStateEvent_on_battery_below_threshold_emits_warning()
        {
            var r = new PowerStateResult
            {
                OnAcPower = false,
                HasBattery = true,
                BatteryPercent = 65,
                IsCharging = false,
                BatteryLifeMinutes = 90,
            };

            var evt = StartupEnvironmentProbes.BuildPowerStateEvent(Config(), r);

            Assert.Equal(EventSeverity.Warning, evt.Severity);
            Assert.Contains("on battery", evt.Message);
            Assert.Contains("65%", evt.Message);
            Assert.Contains("low charge", evt.Message);
            Assert.Equal(65, evt.Data["batteryPercent"]);
        }

        [Fact]
        public void BuildPowerStateEvent_probe_error_emits_warning_with_error_detail()
        {
            var r = new PowerStateResult
            {
                ProbeError = "GetSystemPowerStatus returned false (Win32Error=87)",
            };

            var evt = StartupEnvironmentProbes.BuildPowerStateEvent(Config(), r);

            Assert.Equal(EventSeverity.Warning, evt.Severity);
            Assert.Contains("probe failed", evt.Message);
            Assert.Contains("Win32Error=87", evt.Message);
            Assert.Equal("GetSystemPowerStatus returned false (Win32Error=87)", evt.Data["error"]);
        }

        // ================================================================= Restart dedup (StartupEventGate)

        [Theory]
        [InlineData(30.0, true)]
        [InlineData(-30.0, true)]
        [InlineData(60.0, true)]    // exactly at tolerance still OK (matches Info severity)
        [InlineData(61.0, false)]
        [InlineData(-120.0, false)]
        public void IsNtpWithinTolerance_matches_the_severity_threshold(double offset, bool expected)
        {
            var r = new NtpCheckResult { Success = true, OffsetSeconds = offset };
            Assert.Equal(expected, StartupEnvironmentProbes.IsNtpWithinTolerance(r));
        }

        [Fact]
        public void IsNtpWithinTolerance_false_on_failure_or_null()
        {
            Assert.False(StartupEnvironmentProbes.IsNtpWithinTolerance(new NtpCheckResult { Success = false }));
            Assert.False(StartupEnvironmentProbes.IsNtpWithinTolerance(null!));
        }

        [Fact]
        public async Task RunAsync_skips_geo_timezone_and_ntp_once_all_succeeded_only_power_state_emits()
        {
            // All retry-until-success probes already latched by a previous agent run of this
            // enrollment → no network/tzutil calls, no duplicate events. Only the deliberately
            // ungated power-state snapshot goes out (battery drain is real per-boot information).
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var gate = new StartupEventGate(tmp.Path, logger);
            gate.MarkSucceeded(Constants.EventTypes.DeviceLocation);
            gate.MarkSucceeded(Constants.EventTypes.TimezoneAutoSet);
            gate.MarkSucceeded(Constants.EventTypes.NtpTimeCheck);

            var sink = new FakeSignalIngressSink();
            var clock = new VirtualClock(new System.DateTime(2026, 6, 12, 12, 0, 0, System.DateTimeKind.Utc));
            var post = new InformationalEventPost(sink, clock);

            await StartupEnvironmentProbes.RunAsync(Config(geoEnabled: true, tzAutoSet: true), logger, post, gate);

            var single = Assert.Single(sink.Posted);
            Assert.Equal(Constants.EventTypes.PowerStateCheck, single.Payload![SignalPayloadKeys.EventType]);
        }

        [Fact]
        public async Task RunAsync_skips_geo_block_when_timezone_autoset_is_disabled_and_location_succeeded()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var gate = new StartupEventGate(tmp.Path, logger);
            gate.MarkSucceeded(Constants.EventTypes.DeviceLocation);
            gate.MarkSucceeded(Constants.EventTypes.NtpTimeCheck);

            var sink = new FakeSignalIngressSink();
            var clock = new VirtualClock(new System.DateTime(2026, 6, 12, 12, 0, 0, System.DateTimeKind.Utc));
            var post = new InformationalEventPost(sink, clock);

            // TimezoneAutoSet disabled → the satisfied location alone closes the geo block.
            await StartupEnvironmentProbes.RunAsync(Config(geoEnabled: true, tzAutoSet: false), logger, post, gate);

            var single = Assert.Single(sink.Posted);
            Assert.Equal(Constants.EventTypes.PowerStateCheck, single.Payload![SignalPayloadKeys.EventType]);
        }
    }
}
