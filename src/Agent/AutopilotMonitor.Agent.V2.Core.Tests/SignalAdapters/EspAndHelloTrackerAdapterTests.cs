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
    public sealed class EspAndHelloTrackerAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public EspAndHelloTracker Coordinator { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                var trackerPost = new InformationalEventPost(new FakeSignalIngressSink(), Clock);
                Coordinator = new EspAndHelloTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: trackerPost,
                    logger: Logger);
            }

            public void Dispose()
            {
                Coordinator.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void HelloEvent_emits_HelloResolved_with_outcome_fallback_unknown()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerHelloFromTest(null);

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.HelloResolved, posted.Kind);
            Assert.Equal("EspAndHelloTracker", posted.SourceOrigin);
            Assert.Equal("unknown", posted.Payload![SignalPayloadKeys.HelloOutcome]);
        }

        [Theory]
        [InlineData("completed")]
        [InlineData("skipped")]
        [InlineData("timeout")]
        [InlineData("not_configured")]
        [InlineData("wizard_not_started")]
        public void HelloOutcome_propagates_verbatim_to_payload(string outcome)
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerHelloFromTest(outcome);

            Assert.Equal(outcome, f.Ingress.Posted[0].Payload![SignalPayloadKeys.HelloOutcome]);
        }

        [Fact]
        public void FinalizingEvent_emits_EspPhaseChanged_with_FinalizingSetup_payload()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerFinalizingFromTest("62404");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspPhaseChanged, posted.Kind);
            Assert.Equal(EnrollmentPhase.FinalizingSetup.ToString(), posted.Payload![SignalPayloadKeys.EspPhase]);
            Assert.Equal("62404", posted.Payload["reason"]);
        }

        [Fact]
        public void WhiteGloveEvent_emits_WhiteGloveShellCoreSuccess()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerWhiteGloveFromTest();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.WhiteGloveShellCoreSuccess, posted.Kind);
        }

        [Fact]
        public void EspFailureEvent_emits_EspTerminalFailure_with_merged_subSource_annotation()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerEspFailureFromTest("CoordinatorMergedFailure");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspTerminalFailure, posted.Kind);
            Assert.Equal("CoordinatorMergedFailure", posted.Payload!["failureType"]);
            Assert.Contains("merged", posted.Evidence.DerivationInputs!["subSource"]);
        }

        [Fact]
        public void EspFailureEvent_empty_type_falls_back_to_unknown()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            // Empty failureType is normalized to "unknown" by EspFailureDetectedEventArgs.
            adapter.TriggerEspFailureFromTest("");

            Assert.Equal("unknown", f.Ingress.Posted[0].Payload!["failureType"]);
        }

        [Fact]
        public void DeviceSetupCompleteEvent_emits_DeviceSetupProvisioningComplete_with_snapshot_fallback()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerDeviceSetupCompleteFromTest();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.DeviceSetupProvisioningComplete, posted.Kind);
            Assert.Equal("unknown", posted.Payload!["deviceSetupResolved"]);
        }

        [Fact]
        public void All_five_signal_kinds_can_fire_in_one_session_each_fire_once()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerFinalizingFromTest("r1");
            adapter.TriggerFinalizingFromTest("r2");   // dedup

            adapter.TriggerWhiteGloveFromTest();
            adapter.TriggerWhiteGloveFromTest();   // dedup

            adapter.TriggerHelloFromTest("completed");
            adapter.TriggerHelloFromTest("timeout");   // dedup

            adapter.TriggerEspFailureFromTest("X");
            adapter.TriggerEspFailureFromTest("Y");   // dedup

            adapter.TriggerDeviceSetupCompleteFromTest();
            adapter.TriggerDeviceSetupCompleteFromTest();   // dedup

            Assert.Equal(5, f.Ingress.Posted.Count);
            var kinds = f.Ingress.Posted.Select(p => p.Kind).ToList();
            Assert.Contains(DecisionSignalKind.EspPhaseChanged, kinds);
            Assert.Contains(DecisionSignalKind.WhiteGloveShellCoreSuccess, kinds);
            Assert.Contains(DecisionSignalKind.HelloResolved, kinds);
            Assert.Contains(DecisionSignalKind.EspTerminalFailure, kinds);
            Assert.Contains(DecisionSignalKind.DeviceSetupProvisioningComplete, kinds);
        }

        // ---- EspExited (coordinator-forwarded from ShellCoreTracker.EspExited) ----

        [Fact]
        public void EspExitedEvent_emits_EspExiting_signal_via_coordinator()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerEspExitingFromTest();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspExiting, posted.Kind);
            Assert.Equal("EspAndHelloTracker", posted.SourceOrigin);
            Assert.Equal(EvidenceKind.Derived, posted.Evidence.Kind);
            Assert.Contains("ESP exiting", posted.Evidence.Summary);
            Assert.Equal("ShellCoreTracker", posted.Evidence.DerivationInputs!["subSource"]);
            Assert.Equal("62407", posted.Evidence.DerivationInputs["eventId"]);
        }

        [Fact]
        public void EspExited_posts_every_occurrence_no_dedup_at_adapter_layer()
        {
            // Shell-Core 62407 fires at every ESP phase transition (Device→Account, Account→End).
            // The adapter must NOT dedup — the reducer (ShouldTransitionToAwaitingHello) decides
            // which occurrence is the genuine post-AccountSetup exit. (Coordinator-side
            // IsIntermediateDeviceEspExit filters the legacy FinalizingSetupPhaseTriggered path,
            // not this one.)
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerEspExitingFromTest();
            adapter.TriggerEspExitingFromTest();
            adapter.TriggerEspExitingFromTest();

            var espExitingPosts = f.Ingress.Posted.Where(p => p.Kind == DecisionSignalKind.EspExiting).ToList();
            Assert.Equal(3, espExitingPosts.Count);
        }

        [Fact]
        public void Ctor_null_args_throw()
        {
            using var f = new Fixture();
            Assert.Throws<ArgumentNullException>(() => new EspAndHelloTrackerAdapter(null!, f.Ingress, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new EspAndHelloTrackerAdapter(f.Coordinator, null!, f.Clock));
            Assert.Throws<ArgumentNullException>(() => new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, null!));
        }

        // PR4 (882fef64 debrief) — HelloPolicyDetected is forwarded from the inner HelloTracker
        // through the coordinator and posted as a DecisionSignalKind.HelloPolicyDetected signal
        // so the engine can read the fact off DecisionState.

        [Fact]
        public void HelloPolicyDetected_emits_HelloPolicyDetected_signal_with_payload()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerHelloPolicyDetectedFromTest(helloEnabled: true, source: "CSP/Intune (user-scoped)");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.HelloPolicyDetected, posted.Kind);
            Assert.Equal("EspAndHelloTracker", posted.SourceOrigin);
            Assert.Equal("true", posted.Payload![SignalPayloadKeys.HelloEnabled]);
            Assert.Equal("CSP/Intune (user-scoped)", posted.Payload[SignalPayloadKeys.HelloPolicySource]);
        }

        [Fact]
        public void HelloPolicyDetected_disabled_emits_false_payload()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerHelloPolicyDetectedFromTest(helloEnabled: false, source: "GPO");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.HelloPolicyDetected, posted.Kind);
            Assert.Equal("false", posted.Payload![SignalPayloadKeys.HelloEnabled]);
            Assert.Equal("GPO", posted.Payload[SignalPayloadKeys.HelloPolicySource]);
        }

        [Fact]
        public void HelloPolicyDetected_repeated_invocations_are_deduplicated()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerHelloPolicyDetectedFromTest(helloEnabled: true, source: "CSP/Intune");
            adapter.TriggerHelloPolicyDetectedFromTest(helloEnabled: false, source: "GPO");
            adapter.TriggerHelloPolicyDetectedFromTest(helloEnabled: true, source: "CSP/Intune");

            // Once-flag at the adapter layer — only the first detection is posted. The reducer
            // also dedupes at the engine level, so this is belt-and-suspenders.
            var helloPolicySignals = f.Ingress.Posted.Where(p => p.Kind == DecisionSignalKind.HelloPolicyDetected).ToList();
            Assert.Single(helloPolicySignals);
            Assert.Equal("true", helloPolicySignals[0].Payload![SignalPayloadKeys.HelloEnabled]);
        }

        [Fact]
        public void HelloPolicyDetected_empty_source_falls_back_to_unknown()
        {
            using var f = new Fixture();
            using var adapter = new EspAndHelloTrackerAdapter(f.Coordinator, f.Ingress, f.Clock);

            adapter.TriggerHelloPolicyDetectedFromTest(helloEnabled: true, source: "");

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal("unknown", posted.Payload![SignalPayloadKeys.HelloPolicySource]);
        }
    }
}
