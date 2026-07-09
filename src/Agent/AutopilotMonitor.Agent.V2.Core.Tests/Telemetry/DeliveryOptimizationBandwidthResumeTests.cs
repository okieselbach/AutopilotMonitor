#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry
{
    /// <summary>
    /// Reboot-survivor behavior of the DeliveryOptimizationCollector's bandwidth estimate:
    /// a new collector over the same state directory (= the agent restarted after a reboot)
    /// must resume the persisted samples AND the interim once-guard — device_setup_end fires
    /// once per SESSION, not once per process, and pre-reboot samples reach the emitted event.
    /// No PowerShell runspace is needed: these tests never Start() the collector; resume,
    /// NotifyEspPhaseChanged and Stop() are all runspace-free paths.
    /// </summary>
    public sealed class DeliveryOptimizationBandwidthResumeTests
    {
        private const string Session = "session-1";
        private static readonly DateTime T0 = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

        private sealed class CapturingSink : ISignalIngressSink
        {
            public readonly List<IReadOnlyDictionary<string, string>?> Posts = new List<IReadOnlyDictionary<string, string>?>();

            public void Post(
                DecisionSignalKind kind,
                DateTime occurredAtUtc,
                string sourceOrigin,
                Evidence evidence,
                IReadOnlyDictionary<string, string>? payload = null,
                int kindSchemaVersion = 1,
                object? typedPayload = null)
            {
                Posts.Add(payload);
            }

            public IEnumerable<IReadOnlyDictionary<string, string>?> BandwidthPosts =>
                Posts.Where(p => p != null && p.TryGetValue(SignalPayloadKeys.EventType, out var t)
                                 && t == Constants.EventTypes.NetworkBandwidthEstimate);
        }

        private static DeliveryOptimizationCollector NewCollector(string stateDir, CapturingSink sink)
        {
            var logger = new AgentLogger(stateDir, AgentLogLevel.Info);
            return new DeliveryOptimizationCollector(
                sessionId: Session,
                tenantId: "tenant-1",
                post: new InformationalEventPost(sink, new VirtualClock(T0)),
                logger: logger,
                intervalSeconds: 3,
                getPackageStates: () => new AppPackageStateList(logger),
                onDoTelemetryReceived: null,
                logDirectory: stateDir,
                onOfficeDoSample: null,
                bandwidthStatePersistence: new BandwidthStatePersistence(stateDir, logger));
        }

        private static void PersistPreRebootState(string stateDir, bool interimEmitted)
        {
            var logger = new AgentLogger(stateDir, AgentLogLevel.Info);
            new BandwidthStatePersistence(stateDir, logger).Save(new BandwidthStateData
            {
                SessionId = Session,
                SavedAtUtc = T0,
                InterimEmitted = interimEmitted,
                WanSamplesMbps = new List<double> { 16.0, 15.8, 16.4 },
                LanSamplesMbps = new List<double>(),
                WanBytesObserved = 30_000_000,
                LanBytesObserved = 0,
            });
        }

        [Fact]
        public void Resumed_samples_reach_the_interim_event_after_a_reboot()
        {
            using var tmp = new TempDirectory();
            PersistPreRebootState(tmp.Path, interimEmitted: false);

            var sink = new CapturingSink();
            var collector = NewCollector(tmp.Path, sink);

            // Post-reboot process observes AccountSetup without any NEW download samples —
            // the emitted interim must carry the pre-reboot data.
            collector.NotifyEspPhaseChanged("AccountSetup");

            var post = Assert.Single(sink.BandwidthPosts);
            Assert.Contains("16", post![SignalPayloadKeys.Message]); // pre-reboot ~16 Mbps figure
        }

        [Fact]
        public void Interim_guard_survives_a_reboot()
        {
            using var tmp = new TempDirectory();
            PersistPreRebootState(tmp.Path, interimEmitted: true);

            var sink = new CapturingSink();
            var collector = NewCollector(tmp.Path, sink);

            collector.NotifyEspPhaseChanged("AccountSetup");

            Assert.Empty(sink.BandwidthPosts); // already emitted by the pre-reboot process
        }

        [Fact]
        public void Final_emission_at_stop_carries_resumed_samples_and_resaves_state()
        {
            using var tmp = new TempDirectory();
            PersistPreRebootState(tmp.Path, interimEmitted: true);

            var sink = new CapturingSink();
            var collector = NewCollector(tmp.Path, sink);
            collector.Stop();

            var post = Assert.Single(sink.BandwidthPosts);
            Assert.Contains("16", post![SignalPayloadKeys.Message]);

            // State file still loadable for the same session after the stop-path save.
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var persisted = new BandwidthStatePersistence(tmp.Path, logger).Load(Session);
            Assert.NotNull(persisted);
            Assert.Equal(3, persisted!.WanSamplesMbps!.Count);
        }

        [Fact]
        public void Without_persisted_state_nothing_emits_until_samples_exist()
        {
            using var tmp = new TempDirectory();
            var sink = new CapturingSink();
            var collector = NewCollector(tmp.Path, sink);

            collector.NotifyEspPhaseChanged("AccountSetup");
            collector.Stop();

            Assert.Empty(sink.BandwidthPosts);
        }
    }
}
