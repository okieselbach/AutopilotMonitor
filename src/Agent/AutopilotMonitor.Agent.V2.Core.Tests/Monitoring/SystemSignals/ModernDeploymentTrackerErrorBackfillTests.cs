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
    /// Autopilot-channel error backfill (docs/agent/autopilot-ztd-diagnostics.md,
    /// Observability section): TPM/ZTD errors precede the agent's IME install, so a
    /// retry-then-success attempt leaves them in the event log with nobody watching.
    /// The backfill replays them as backfilled=true modern_deployment_error events; the
    /// RecordId watermark makes replays restart-safe. These tests pin the XPath, the
    /// watermark persistence roundtrip, and the backfilled payload marker.
    /// </summary>
    public sealed class ModernDeploymentTrackerErrorBackfillTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 7, 16, 14, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink;
        private readonly ModernDeploymentTracker _tracker;

        public ModernDeploymentTrackerErrorBackfillTests()
        {
            _sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(_sink, new VirtualClock(At));
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _tracker = new ModernDeploymentTracker(
                sessionId: "sess-error-backfill",
                tenantId: "tenant-error-backfill",
                post: post,
                logger: logger,
                backfillEnabled: false,
                stateDirectory: _tmp.Path);
        }

        public void Dispose() => _tmp.Dispose();

        [Fact]
        public void BuildErrorBackfillXPath_FiltersToCriticalAndErrorWithinLookback()
        {
            var xpath = ModernDeploymentTracker.BuildErrorBackfillXPath(240);

            Assert.Equal("*[System[(Level >= 1 and Level <= 2) and TimeCreated[timediff(@SystemTime) <= 14400000]]]", xpath);
        }

        [Fact]
        public void Watermark_RoundtripsThroughStateFile_AndIsMonotonic()
        {
            Assert.Null(_tracker.LoadAutopilotErrorBackfillState());

            _tracker.AdvanceAutopilotErrorWatermark(4711);
            Assert.Equal(4711, _tracker.LoadAutopilotErrorBackfillState()!.MaxRecordId);

            // Lower RecordIds must never regress the persisted watermark.
            _tracker.AdvanceAutopilotErrorWatermark(42);
            Assert.Equal(4711, _tracker.LoadAutopilotErrorBackfillState()!.MaxRecordId);

            _tracker.AdvanceAutopilotErrorWatermark(4712);
            Assert.Equal(4712, _tracker.LoadAutopilotErrorBackfillState()!.MaxRecordId);
        }

        [Fact]
        public void BackfilledErrorRecord_EmitsModernDeploymentError_WithBackfilledMarkerAndOriginalTime()
        {
            var originalTime = At.AddMinutes(-45); // written long before agent start

            _tracker.ProcessEvent(
                eventId: 815,
                level: 2,
                levelDisplayName: "Error",
                providerName: "ModernDeployment-Diagnostics-Provider",
                timeCreatedUtc: originalTime,
                formattedDescription: "ZtdDeviceHasNoAssignedProfile - No profile assigned to the device, and no default profile found in the tenant.",
                shortName: "Autopilot",
                channelName: ModernDeploymentTracker.AutopilotChannel,
                isBackfill: true);

            var posted = _sink.Posted.Single(p => p.Kind == DecisionSignalKind.InformationalEvent);
            Assert.Equal("modern_deployment_error", posted.Payload![SignalPayloadKeys.EventType]);

            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(posted.TypedPayload);
            Assert.Equal(true, data["backfilled"]);
            Assert.Equal(originalTime.ToString("o"), data["timeCreated"]);
            Assert.Equal(815, data["eventId"]);
        }

        [Fact]
        public void LiveErrorRecord_CarriesBackfilledFalse()
        {
            _tracker.ProcessEvent(
                eventId: 807,
                level: 2,
                levelDisplayName: "Error",
                providerName: "ModernDeployment-Diagnostics-Provider",
                timeCreatedUtc: At,
                formattedDescription: "ZtdDeviceIsNotRegistered",
                shortName: "Autopilot",
                channelName: ModernDeploymentTracker.AutopilotChannel,
                isBackfill: false);

            var posted = _sink.Posted.Single(p => p.Kind == DecisionSignalKind.InformationalEvent);
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(posted.TypedPayload);
            Assert.Equal(false, data["backfilled"]);
        }

        [Fact]
        public void BackfilledHarmlessId_StillRunsThroughRollupSuppression()
        {
            // Even in a backfill scan, the harmless-downgrade + burst rollup must apply — a
            // pre-start burst of a known-harmless Error-level ID must not flood the timeline.
            for (var i = 0; i < ModernDeploymentTracker.HarmlessRollupIndividualLimit + 10; i++)
            {
                _tracker.ProcessEvent(
                    eventId: 1005,
                    level: 2,
                    levelDisplayName: "Error",
                    providerName: "ModernDeployment-Diagnostics-Provider",
                    timeCreatedUtc: At.AddMinutes(-30).AddSeconds(i),
                    formattedDescription: "Known-harmless error burst",
                    shortName: "Autopilot",
                    channelName: ModernDeploymentTracker.AutopilotChannel,
                    isBackfill: true);
            }

            // Only the first N individual occurrences are forwarded (downgraded to
            // modern_deployment_log); the burst tail is suppressed until the next rollup step.
            var forwarded = _sink.Posted.Count(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "modern_deployment_log");
            Assert.Equal(ModernDeploymentTracker.HarmlessRollupIndividualLimit, forwarded);
        }
    }
}
