#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>The V1 confidence score of <see cref="ConfigMgrClientSnapshot"/>.</summary>
    public sealed class ConfigMgrClientScoreTests
    {
        [Fact]
        public void Nothing_found_scores_zero_and_is_not_detected()
        {
            Assert.Equal(0, ConfigMgrClientSnapshot.Empty.ConfidenceScore);
            Assert.False(ConfigMgrClientSnapshot.Empty.Detected);
        }

        [Fact]
        public void A_registered_running_client_scores_100()
        {
            var snapshot = new ConfigMgrClientSnapshot(true, true, "Running", "5.00.9999.1000", "ABC");
            Assert.Equal(100, snapshot.ConfidenceScore);
        }

        [Fact]
        public void The_folder_alone_is_a_weak_remnant_below_the_portal_threshold()
        {
            var snapshot = new ConfigMgrClientSnapshot(false, true, ConfigMgrClientProbe.ServiceNotFound, null, null);
            Assert.True(snapshot.Detected);
            Assert.Equal(10, snapshot.ConfidenceScore);
        }

        [Fact]
        public void A_stopped_service_counts_without_the_running_bonus()
        {
            var snapshot = new ConfigMgrClientSnapshot(true, true, "Stopped", "5.00.9999.1000", "ABC");
            Assert.Equal(80, snapshot.ConfidenceScore);
        }
    }

    /// <summary>
    /// Emission surface of <see cref="ConfigMgrClientDetector"/> via the real
    /// <see cref="InformationalEventPost"/>. Static probe override → serial collection.
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class ConfigMgrClientDetectorEmissionTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();

        public void Dispose() => _tmp.Dispose();

        private (ConfigMgrClientDetector sut, FakeSignalIngressSink sink) BuildDetector(StartupEventGate? gate = null)
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            var sink = new FakeSignalIngressSink();
            var clock = new VirtualClock(At);
            var sut = new ConfigMgrClientDetector("S1", "T1", new InformationalEventPost(sink, clock), logger, clock, gate);
            return (sut, sink);
        }

        private static IReadOnlyList<FakeSignalIngressSink.PostedSignal> Detected(FakeSignalIngressSink sink) =>
            sink.Posted.Where(p =>
                p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "configmgr_client_detected").ToList();

        private static ConfigMgrClientSnapshot Client(string serviceState, string? siteCode = "ABC") =>
            new ConfigMgrClientSnapshot(true, true, serviceState, "5.00.9999.1000", siteCode);

        [Fact]
        public void No_client_never_emits()
        {
            using var _probe = new ConfigMgrClientProbe.ScopedOverride(() => ConfigMgrClientSnapshot.Empty);
            var (sut, sink) = BuildDetector();

            sut.Check();
            sut.Check();

            Assert.Empty(Detected(sink));
        }

        [Fact]
        public void First_detection_emits_the_v1_payload()
        {
            using var _probe = new ConfigMgrClientProbe.ScopedOverride(() => Client("Running"));
            var (sut, sink) = BuildDetector();

            sut.Check();

            var evt = Assert.Single(Detected(sink));
            Assert.Equal(DecisionSignalKind.InformationalEvent, evt.Kind);
            Assert.Equal("Info", evt.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal(ConfigMgrClientDetector.SourceLabel, evt.Payload[SignalPayloadKeys.Source]);

            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(evt.TypedPayload);
            Assert.Equal(true, data["ccmRegistryFound"]);
            Assert.Equal(true, data["ccmDirectoryFound"]);
            Assert.Equal("Running", data["ccmServiceState"]);
            Assert.Equal("5.00.9999.1000", data["ccmVersion"]);
            Assert.Equal("ABC", data["siteCode"]);
            Assert.Equal(100, data["confidenceScore"]);
            Assert.Equal("startup", data["detectedAt"]);
        }

        [Fact]
        public void An_unchanged_state_emits_once_a_changed_state_again()
        {
            var current = Client("Stopped", siteCode: null);
            using var _probe = new ConfigMgrClientProbe.ScopedOverride(() => current);
            var (sut, sink) = BuildDetector();

            sut.Check();
            sut.Check();
            Assert.Single(Detected(sink));

            current = Client("Running");
            sut.Check();

            var events = Detected(sink);
            Assert.Equal(2, events.Count);
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(events[0].TypedPayload);
            Assert.Equal("unknown", data["siteCode"]); // missing values keep the V1 placeholder
            var latest = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(events[1].TypedPayload);
            Assert.Equal("Running", latest["ccmServiceState"]);
            Assert.Equal("poll", latest["detectedAt"]);
        }

        [Fact]
        public void A_restart_into_the_same_state_stays_silent()
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            var current = Client("Running");
            using var _probe = new ConfigMgrClientProbe.ScopedOverride(() => current);

            var (run1, sink1) = BuildDetector(new StartupEventGate(_tmp.Path, logger));
            run1.Check();
            Assert.Single(Detected(sink1));

            var (run2, sink2) = BuildDetector(new StartupEventGate(_tmp.Path, logger));
            run2.Check();
            Assert.Empty(Detected(sink2));

            current = Client("Stopped");
            run2.Check();
            Assert.Single(Detected(sink2));
        }

        [Fact]
        public void Emissions_are_capped_per_run()
        {
            var running = true;
            using var _probe = new ConfigMgrClientProbe.ScopedOverride(() => Client(running ? "Running" : "Stopped"));
            var (sut, sink) = BuildDetector();

            for (var i = 0; i < ConfigMgrClientDetector.MaxEmissionsPerRun * 2; i++)
            {
                sut.Check();
                running = !running;
            }

            Assert.Equal(ConfigMgrClientDetector.MaxEmissionsPerRun, Detected(sink).Count);
        }
    }
}
