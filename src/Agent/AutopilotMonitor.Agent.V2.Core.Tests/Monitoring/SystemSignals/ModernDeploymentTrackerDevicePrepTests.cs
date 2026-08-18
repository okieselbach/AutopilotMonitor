using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Session afee7ae0 (2026-08-18) — Device Preparation structural-noise suppression.
    /// A nine-minute WDP session shipped 900+ occurrences of WIL error 1005 plus 400+
    /// duplicate-workload 417/418 records and "Autopilot policy not found" 100s — all
    /// structural on WDP (no deployment profile exists; the BootstrapperAgent's SLDM
    /// progress poll produces the 417/418 chatter). On WDP those IDs are suppressed
    /// entirely (<see cref="ModernDeploymentTracker.DevicePreparationNoiseEventIds"/>);
    /// genuine diagnostics (e.g. 408 provisioning warnings) and Critical records keep
    /// flowing, and Classic sessions keep the harmless-downgrade + rollup path.
    /// </summary>
    public sealed class ModernDeploymentTrackerDevicePrepTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 8, 18, 9, 10, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink;
        private readonly AgentLogger _logger;

        public ModernDeploymentTrackerDevicePrepTests()
        {
            _sink = new FakeSignalIngressSink();
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
        }

        public void Dispose() => _tmp.Dispose();

        private ModernDeploymentTracker BuildTracker(bool isDevicePreparation) =>
            new ModernDeploymentTracker(
                sessionId: "sess-wdp-noise",
                tenantId: "tenant-wdp-noise",
                post: new InformationalEventPost(_sink, new VirtualClock(At)),
                logger: _logger,
                backfillEnabled: false,
                isDevicePreparation: isDevicePreparation);

        private void Process(ModernDeploymentTracker tracker, int eventId, int level, string shortName, string channel)
        {
            tracker.ProcessEvent(
                eventId: eventId,
                level: level,
                levelDisplayName: level == 1 ? "Critical" : level == 2 ? "Error" : "Warning",
                providerName: "ModernDeployment-Diagnostics-Provider",
                timeCreatedUtc: At,
                formattedDescription: $"EventID {eventId} record.",
                shortName: shortName,
                channelName: channel,
                isBackfill: false);
        }

        private List<FakeSignalIngressSink.PostedSignal> PostedModernDeployment() =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et.StartsWith("modern_deployment", StringComparison.Ordinal)).ToList();

        [Theory]
        [InlineData(100, 3, "Autopilot")]           // policy not found — no profile exists on WDP
        [InlineData(417, 3, "Autopilot")]           // duplicate workload — SLDM poll chatter
        [InlineData(418, 3, "Autopilot")]           // duplicate batch — SLDM poll chatter
        [InlineData(1005, 2, "ManagementService")]  // WIL error storm
        public void WdpNoiseIds_AreFullySuppressed_OnDevicePreparation(int eventId, int level, string shortName)
        {
            var tracker = BuildTracker(isDevicePreparation: true);
            var channel = shortName == "Autopilot"
                ? ModernDeploymentTracker.AutopilotChannel
                : ModernDeploymentTracker.ManagementChannel;

            for (var i = 0; i < 10; i++)
            {
                Process(tracker, eventId, level, shortName, channel);
            }

            Assert.Empty(PostedModernDeployment());
        }

        [Fact]
        public void GenuineWdpWarning_408_KeepsFlowing_OnDevicePreparation()
        {
            // 408 "Autopilot Provisioning reported a warning" carries the real WDP session
            // context — it is NOT in the noise set and must keep flowing.
            var tracker = BuildTracker(isDevicePreparation: true);

            Process(tracker, 408, level: 3, shortName: "Autopilot", channel: ModernDeploymentTracker.AutopilotChannel);

            var posted = Assert.Single(PostedModernDeployment());
            Assert.Equal("modern_deployment_warning", posted.Payload![SignalPayloadKeys.EventType]);
        }

        [Fact]
        public void CriticalRecords_AreNeverSuppressed_EvenForNoiseIds()
        {
            var tracker = BuildTracker(isDevicePreparation: true);

            Process(tracker, 1005, level: 1, shortName: "ManagementService", channel: ModernDeploymentTracker.ManagementChannel);

            var posted = Assert.Single(PostedModernDeployment());
            Assert.Equal("modern_deployment_error", posted.Payload![SignalPayloadKeys.EventType]);
        }

        [Fact]
        public void ClassicSessions_KeepTheHarmlessDowngradePath_ForTheSameIds()
        {
            // Classic regression guard: without the WDP flag EventID 100 still flows through
            // the harmless-downgrade path (individual Debug emissions up to the rollup limit).
            var tracker = BuildTracker(isDevicePreparation: false);

            for (var i = 0; i < 2; i++)
            {
                Process(tracker, 100, level: 3, shortName: "Autopilot", channel: ModernDeploymentTracker.AutopilotChannel);
            }

            Assert.Equal(2, PostedModernDeployment().Count);
        }
    }
}
