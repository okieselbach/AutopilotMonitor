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
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Session caa6cf50 gate-starvation fix (2026-06-11) — coordinator-side synthesis tests.
    /// When a Shell-Core normal exit (62407) is forwarded while the injected IME probe reports
    /// "all tracked user-ESP apps terminal (0 failed)", <see cref="EspAndHelloTracker"/> must
    /// raise <c>AccountSetupProvisioningComplete</c> as alternative gate evidence — because a
    /// policy-skipped user-ESP app leaves the registry's Apps subcategory permanently
    /// <c>inProgress</c> and both registry-driven paths (normal + fallback) starve.
    /// </summary>
    public sealed class EspAndHelloTrackerUserAppsSettledSynthesisTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 6, 11, 8, 0, 2, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public FakeSignalIngressSink TrackerPostSink { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture() { Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Debug); }

            public EspAndHelloTracker BuildCoordinator(Func<bool> settledProbe)
                => BuildCoordinator(settledProbe, accountSetupActivityProbe: () => true, skipUserEsp: false);

            /// <summary>
            /// Codex review P1 (2026-08-19): the remembered exit edge is gated on positive evidence
            /// that the exit is post-AccountSetup, so these two probes decide whether the deferred
            /// re-check may fire at all.
            /// </summary>
            public EspAndHelloTracker BuildCoordinator(
                Func<bool> settledProbe,
                Func<bool> accountSetupActivityProbe,
                bool? skipUserEsp)
            {
                return new EspAndHelloTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: new InformationalEventPost(TrackerPostSink, Clock),
                    logger: Logger,
                    skipConfigProbe: () => (skipUserEsp, (bool?)false),
                    accountSetupActivityProbe: accountSetupActivityProbe,
                    userEspAppsSettledProbe: settledProbe);
            }

            public void Dispose() { Tmp.Dispose(); }
        }

        [Fact]
        public void EspExited_withSettledUserApps_raises_AccountSetupProvisioningComplete_after_EspExiting()
        {
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(() => true);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);

            // Signal order matters: EspExiting first (records EspFinalExitUtc), then the
            // synthesized gate signal (deferred-promote path reads the recorded fact).
            Assert.Equal(2, f.Ingress.Posted.Count);
            Assert.Equal(DecisionSignalKind.EspExiting, f.Ingress.Posted[0].Kind);
            Assert.Equal(DecisionSignalKind.AccountSetupProvisioningComplete, f.Ingress.Posted[1].Kind);
            Assert.True(coordinator.UserAppsSettledSynthesisFiredForTest);

            // Observability: the coordinator emits an esp_provisioning_status informational
            // event so session-debug can see WHY the gate opened without registry confirmation.
            var info = Assert.Single(f.TrackerPostSink.Posted);
            Assert.Equal(DecisionSignalKind.InformationalEvent, info.Kind);
            Assert.Equal("esp_provisioning_status", info.Payload![SignalPayloadKeys.EventType]);
            // EnrollmentEvent.Data flows through the typed sidecar (plan §1.3), not the string payload.
            var data = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyDictionary<string, object>>(info.TypedPayload);
            Assert.Equal("esp_exited_user_apps_settled_category_unresolved", data["fallbackReason"]);
        }

        [Fact]
        public void EspExited_withUnsettledUserApps_forwards_EspExiting_only()
        {
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(() => false);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspExiting, posted.Kind);
            Assert.False(coordinator.UserAppsSettledSynthesisFiredForTest);
            Assert.Empty(f.TrackerPostSink.Posted);
        }

        [Fact]
        public void Synthesis_fires_once_across_multiple_exits()
        {
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(() => true);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            coordinator.TriggerEspExitedForTest(Fixed.AddSeconds(30));

            // Two EspExiting forwards (no dedup by design), exactly one synthesized gate signal.
            Assert.Equal(2, f.Ingress.Posted.Count(p => p.Kind == DecisionSignalKind.EspExiting));
            Assert.Equal(1, f.Ingress.Posted.Count(p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete));
            Assert.Single(f.TrackerPostSink.Posted);
        }

        [Fact]
        public void Synthesis_becomes_eligible_on_a_later_exit_when_apps_settle_in_between()
        {
            // Exit #1 fires while user apps are still in flight (probe false) — no synthesis.
            // Apps settle, exit #2 fires — synthesis must trigger on the later exit.
            using var f = new Fixture();
            var settled = false;
            using var coordinator = f.BuildCoordinator(() => settled);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete);

            settled = true;
            coordinator.TriggerEspExitedForTest(Fixed.AddMinutes(10));

            Assert.Equal(1, f.Ingress.Posted.Count(p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete));
        }

        [Fact]
        public void ThrowingProbe_is_swallowed_and_EspExiting_still_forwards()
        {
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(() => throw new InvalidOperationException("probe boom"));
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspExiting, posted.Kind);
            Assert.False(coordinator.UserAppsSettledSynthesisFiredForTest);
        }

        [Fact]
        public void DefaultProbe_never_synthesizes()
        {
            // Single-tracker wiring scenarios construct the coordinator without the probe —
            // the default must preserve prior behaviour (no synthesis, ever).
            using var f = new Fixture();
            using var coordinator = new EspAndHelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(f.TrackerPostSink, f.Clock),
                logger: f.Logger,
                skipConfigProbe: () => ((bool?)false, (bool?)false),
                accountSetupActivityProbe: () => true);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspExiting, posted.Kind);
        }

        // ------------------------------------------------------------------------------
        // sits-d Cloud-PC fix (2026-08-19) — the synthesis is no longer edge-only.
        // Sessions 8110e262 / a89aac2d: the ESP page exited while 138 required user-ESP apps
        // were still in flight, they all reached a terminal state (0 failed) minutes later,
        // and because nothing re-checked, the enrollment never completed.
        // ------------------------------------------------------------------------------

        [Fact]
        public void Reevaluate_after_apps_settle_synthesizes_without_a_second_exit()
        {
            using var f = new Fixture();
            var settled = false;
            using var coordinator = f.BuildCoordinator(() => settled);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete);

            // The apps settle later; only the IME app-state callback fires — there is NO second
            // ESP exit on a real device, which is exactly why the edge-only version starved.
            settled = true;
            coordinator.ReevaluateUserAppsSettledSynthesis();

            Assert.Equal(1, f.Ingress.Posted.Count(p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete));
            Assert.True(coordinator.UserAppsSettledSynthesisFiredForTest);

            var info = Assert.Single(f.TrackerPostSink.Posted);
            var data = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyDictionary<string, object>>(info.TypedPayload);
            Assert.Equal("esp_exited_user_apps_settled_category_unresolved", data["fallbackReason"]);
        }

        [Fact]
        public void Reevaluate_without_an_observed_esp_exit_never_synthesizes()
        {
            // The gate needs BOTH facts. Settled apps alone must never open it — otherwise every
            // quiet moment during AccountSetup would look like completion.
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(() => true);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.ReevaluateUserAppsSettledSynthesis();
            coordinator.ReevaluateUserAppsSettledSynthesis();

            Assert.Empty(f.Ingress.Posted);
            Assert.Empty(f.TrackerPostSink.Posted);
            Assert.False(coordinator.UserAppsSettledSynthesisFiredForTest);
        }

        [Fact]
        public void Reevaluate_is_idempotent_after_the_synthesis_fired()
        {
            // The IME callback fires per app-state transition — on these tenants that is 138+
            // times. The synthesis must stay fire-once.
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(() => true);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            for (var i = 0; i < 20; i++) coordinator.ReevaluateUserAppsSettledSynthesis();

            Assert.Equal(1, f.Ingress.Posted.Count(p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete));
            Assert.Single(f.TrackerPostSink.Posted);
        }

        // ------------------------------------------------------------------------------
        // Codex review P1 (2026-08-19): Shell-Core raises 62407 at EVERY ESP phase transition.
        // Remembering the intermediate DeviceSetup→AccountSetup exit would let the deferred
        // re-check open the strong AccountSetup gate as soon as the last user app settles —
        // while the AccountSetup page is still up and its other subcategories still running.
        // ------------------------------------------------------------------------------

        [Fact]
        public void Intermediate_device_exit_is_not_remembered_as_the_edge()
        {
            using var f = new Fixture();
            var settled = false;
            // Classic user-driven enrollment (SkipUser=false) that has NOT reached AccountSetup:
            // this 62407 is the DeviceSetup→AccountSetup transition, not the final exit.
            using var coordinator = f.BuildCoordinator(
                () => settled, accountSetupActivityProbe: () => false, skipUserEsp: false);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);

            // Apps settle afterwards — the level is reached, but the edge was never a valid one.
            settled = true;
            for (var i = 0; i < 10; i++) coordinator.ReevaluateUserAppsSettledSynthesis();

            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete);
            Assert.False(coordinator.UserAppsSettledSynthesisFiredForTest);
            Assert.Empty(f.TrackerPostSink.Posted);
        }

        [Fact]
        public void Unknown_skip_user_is_treated_as_unconfirmed()
        {
            // Erring strict costs at worst today's stall; erring loose costs a premature Succeeded.
            using var f = new Fixture();
            var settled = false;
            using var coordinator = f.BuildCoordinator(
                () => settled, accountSetupActivityProbe: () => false, skipUserEsp: null);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            settled = true;
            coordinator.ReevaluateUserAppsSettledSynthesis();

            Assert.False(coordinator.UserAppsSettledSynthesisFiredForTest);
        }

        [Fact]
        public void Skip_user_profile_confirms_the_device_exit_as_final()
        {
            // SkipUser=true means there is no user ESP at all — the Device-ESP exit IS the final
            // one and no second exit is coming, so the edge is legitimately remembered.
            using var f = new Fixture();
            var settled = false;
            using var coordinator = f.BuildCoordinator(
                () => settled, accountSetupActivityProbe: () => false, skipUserEsp: true);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            settled = true;
            coordinator.ReevaluateUserAppsSettledSynthesis();

            Assert.Equal(1, f.Ingress.Posted.Count(p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete));
        }

        [Fact]
        public void Account_setup_activity_confirms_the_edge_for_the_deferred_recheck()
        {
            // The real reboot shape: AccountSetup activity is visible in the registry, the final
            // exit lands, the apps settle a moment later.
            using var f = new Fixture();
            var settled = false;
            using var coordinator = f.BuildCoordinator(
                () => settled, accountSetupActivityProbe: () => true, skipUserEsp: false);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete);

            settled = true;
            coordinator.ReevaluateUserAppsSettledSynthesis();

            Assert.Equal(1, f.Ingress.Posted.Count(p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete));
        }

        // NOTE: the "replayed exit" cases that used to live here are gone by construction —
        // ShellCoreTracker never re-raises a 62407 from the replay any more, so no backfilled exit
        // can reach this handler at all. That contract is pinned one layer up, in
        // ShellCoreTrackerReplayScopeTests, together with the reducer-side proof of why it matters
        // (ClassicEspExitingOnRestoredStateTests).

        [Fact]
        public void Reevaluate_stays_silent_while_apps_are_still_unsettled()
        {
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(() => false);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            for (var i = 0; i < 5; i++) coordinator.ReevaluateUserAppsSettledSynthesis();

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspExiting, posted.Kind);
            Assert.False(coordinator.UserAppsSettledSynthesisFiredForTest);
        }
    }
}
