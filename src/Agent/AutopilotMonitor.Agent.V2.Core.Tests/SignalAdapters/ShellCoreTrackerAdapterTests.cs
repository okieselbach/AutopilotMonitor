using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    public sealed class ShellCoreTrackerAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public ShellCoreTracker Tracker { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), Clock);
                Tracker = new ShellCoreTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: trackerPost,
                    logger: Logger,
                    helloTracker: null);
            }

            public void Dispose()
            {
                Tracker.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void FinalizingPhase_maps_to_EspPhaseChanged_with_Finalizing_payload()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerFinalizingFromTest("shell-core-62404");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, posted.Kind);
            Assert.Equal(Fixed, posted.OccurredAtUtc);
            Assert.Equal("ShellCoreTracker", posted.SourceOrigin);
            Assert.Equal(
                EnrollmentPhase.FinalizingSetup.ToString(),
                posted.Payload![SignalPayloadKeys.EspPhase]);
            Assert.Equal("shell-core-62404", posted.Payload["reason"]);
        }

        [Fact]
        public void WhiteGloveCompleted_maps_to_WhiteGloveShellCoreSuccess()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerWhiteGloveCompletedFromTest();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.WhiteGloveShellCoreSuccess, posted.Kind);
            Assert.Equal("ShellCoreTracker", posted.SourceOrigin);
            Assert.Equal(EvidenceKind.Derived, posted.Evidence.Kind);
            Assert.Contains("sealing success", posted.Evidence.Summary);
        }

        [Fact]
        public void EspFailure_maps_to_EspTerminalFailure_with_failureType_in_payload()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspFailureFromTest("AccountSetup_Timeout");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspTerminalFailure, posted.Kind);
            Assert.Equal("AccountSetup_Timeout", posted.Payload!["failureType"]);
            Assert.Equal("AccountSetup_Timeout", posted.Evidence.DerivationInputs!["failureType"]);
        }

        [Fact]
        public void EspFailure_empty_type_falls_back_to_unknown()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspFailureFromTest("");

            Assert.Equal("unknown", f.Ingress.Posted[0].Payload!["failureType"]);
        }

        [Fact]
        public void Each_signal_kind_is_deduplicated_independently()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerFinalizingFromTest("r1");
            adapter.TriggerFinalizingFromTest("r2");   // dedup
            adapter.TriggerWhiteGloveCompletedFromTest();
            adapter.TriggerWhiteGloveCompletedFromTest();   // dedup
            adapter.TriggerEspFailureFromTest("t1");
            adapter.TriggerEspFailureFromTest("t2");   // dedup

            Assert.Equal(3, f.Ingress.Posted.Count);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.EspPhaseChanged);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.WhiteGloveShellCoreSuccess);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.EspTerminalFailure);
        }

        [Fact]
        public void All_three_events_can_fire_in_one_session()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerFinalizingFromTest("phase-enter");
            adapter.TriggerWhiteGloveCompletedFromTest();
            adapter.TriggerEspFailureFromTest("Timeout");

            Assert.Equal(3, f.Ingress.Posted.Count);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, f.Ingress.Posted[0].Kind);
            Assert.Equal(DecisionSignalKind.WhiteGloveShellCoreSuccess, f.Ingress.Posted[1].Kind);
            Assert.Equal(DecisionSignalKind.EspTerminalFailure, f.Ingress.Posted[2].Kind);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(() => new ShellCoreTrackerAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new ShellCoreTrackerAdapter(f.Tracker, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, null!));
        }

        // ---- EspExited → DecisionSignalKind.EspExiting (Plan §EspExiting Adapter) ----

        [Fact]
        public void EspExited_maps_to_EspExiting_signal()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var sourceTime = new DateTime(2026, 5, 12, 17, 45, 30, DateTimeKind.Utc);

            adapter.TriggerEspExitingFromTest(sourceTime);

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspExiting, posted.Kind);
            Assert.Equal("ShellCoreTracker", posted.SourceOrigin);
            Assert.Equal(EvidenceKind.Derived, posted.Evidence.Kind);
            Assert.Contains("ESP exiting", posted.Evidence.Summary);
            Assert.Equal("Microsoft-Windows-Shell-Core", posted.Evidence.DerivationInputs!["eventSource"]);
            Assert.Equal("62407", posted.Evidence.DerivationInputs["eventId"]);
        }

        [Fact]
        public void EspExited_posts_every_occurrence_no_dedup()
        {
            // Shell-Core 62407 fires at every ESP phase transition (Device→Account, Account→End).
            // The adapter must NOT dedup — the reducer (ShouldTransitionToAwaitingHello)
            // distinguishes the genuine post-AccountSetup exit from intermediate ones.
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerEspExitingFromTest(new DateTime(2026, 5, 12, 17, 20, 0, DateTimeKind.Utc));
            adapter.TriggerEspExitingFromTest(new DateTime(2026, 5, 12, 17, 45, 0, DateTimeKind.Utc));
            adapter.TriggerEspExitingFromTest(new DateTime(2026, 5, 12, 18, 10, 0, DateTimeKind.Utc));

            Assert.Equal(3, f.Ingress.Posted.Count);
            Assert.All(f.Ingress.Posted, p => Assert.Equal(DecisionSignalKind.EspExiting, p.Kind));
        }

        [Fact]
        public void EspExited_preserves_source_event_timestamp_not_clock_now()
        {
            // OccurredAtUtc must come from the source event (live = log time, backfill =
            // record.TimeCreated), not from the adapter's clock — otherwise the engine's
            // EffectiveDeadlineBase would floor HelloSafety at "now" on a backfilled run.
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var pastTime = new DateTime(2026, 4, 1, 8, 15, 22, DateTimeKind.Utc); // before f.Clock = 2026-04-20

            adapter.TriggerEspExitingFromTest(pastTime);

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(pastTime, posted.OccurredAtUtc);
            Assert.NotEqual(f.Clock.UtcNow, posted.OccurredAtUtc);
        }

        [Fact]
        public void All_four_events_can_fire_in_one_session()
        {
            using var f = new Fixture();
            using var adapter = new ShellCoreTrackerAdapter(f.Tracker, f.Ingress, f.Clock);

            adapter.TriggerFinalizingFromTest("phase-enter");
            adapter.TriggerWhiteGloveCompletedFromTest();
            adapter.TriggerEspFailureFromTest("Timeout");
            adapter.TriggerEspExitingFromTest(Fixed);

            Assert.Equal(4, f.Ingress.Posted.Count);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.EspPhaseChanged);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.WhiteGloveShellCoreSuccess);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.EspTerminalFailure);
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.EspExiting);
        }
    }
}
