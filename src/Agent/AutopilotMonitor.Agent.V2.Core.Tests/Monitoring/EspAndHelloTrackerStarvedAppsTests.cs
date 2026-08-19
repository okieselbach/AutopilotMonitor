using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
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
    /// Liveness plan PR3 — live-path <c>app_install_starved</c> emission. When the ESP page
    /// exits while the user-apps-settled probe reports false, the coordinator names the
    /// starved app(s) (one-shot Warning per appId) instead of leaving the operator with an
    /// anonymous stall. The dedupe set is exposed via
    /// <see cref="EspAndHelloTracker.StarvedAppsReported"/> for the termination sweep.
    /// </summary>
    public sealed class EspAndHelloTrackerStarvedAppsTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 6, 12, 8, 0, 2, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public FakeSignalIngressSink TrackerPostSink { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture() { Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Debug); }

            public EspAndHelloTracker BuildCoordinator(
                Func<bool> settledProbe,
                Func<IReadOnlyList<AppPackageState>> starvedProbe)
            {
                return new EspAndHelloTracker(
                    sessionId: "S1",
                    tenantId: "T1",
                    post: new InformationalEventPost(TrackerPostSink, Clock),
                    logger: Logger,
                    skipConfigProbe: () => ((bool?)false, (bool?)false),
                    accountSetupActivityProbe: () => true,
                    userEspAppsSettledProbe: settledProbe,
                    starvedUserEspAppsProbe: starvedProbe);
            }

            public List<FakeSignalIngressSink.PostedSignal> StarvedPosts() =>
                TrackerPostSink.Posted.Where(p =>
                    p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et == "app_install_starved").ToList();

            public void Dispose() { Tmp.Dispose(); }
        }

        private static AppPackageState StarvedApp(string id, string? name = null)
        {
            var pkg = new AppPackageState(id, listPos: 0);
            pkg.UpdateIntent(AppIntent.Install);
            if (name != null) pkg.UpdateName(name);
            return pkg;
        }

        [Fact]
        public void Unsettled_exit_emits_warning_per_starved_app()
        {
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(
                () => false,
                () => new[] { StarvedApp("app-1", "Contoso Backgrounds") });
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);

            var post = Assert.Single(f.StarvedPosts());
            Assert.Equal("Warning", post.Payload![SignalPayloadKeys.Severity]);
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(post.TypedPayload);
            Assert.Equal("app-1", data["appId"]);
            Assert.Equal("Contoso Backgrounds", data["appName"]);
            Assert.Equal("esp_exited_user_apps_not_settled", data["trigger"]);
            Assert.Contains("app-1", coordinator.StarvedAppsReported);
        }

        [Fact]
        public void Repeated_exits_emit_once_per_appId()
        {
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(
                () => false,
                () => new[] { StarvedApp("app-1") });
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            coordinator.TriggerEspExitedForTest(Fixed.AddMinutes(5));

            Assert.Single(f.StarvedPosts());
        }

        [Fact]
        public void New_starved_app_on_later_exit_is_reported_additively()
        {
            using var f = new Fixture();
            var starved = new List<AppPackageState> { StarvedApp("app-1") };
            using var coordinator = f.BuildCoordinator(() => false, () => starved);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            starved.Add(StarvedApp("app-2"));
            coordinator.TriggerEspExitedForTest(Fixed.AddMinutes(5));

            var posts = f.StarvedPosts();
            Assert.Equal(2, posts.Count);
            Assert.Equal(2, coordinator.StarvedAppsReported.Count);
        }

        [Fact]
        public void Settled_exit_emits_no_starved_events()
        {
            // When the probe settles, the synthesis path runs instead — starved apps and a
            // settled gate are mutually exclusive by the IME probe's construction anyway.
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(
                () => true,
                () => new[] { StarvedApp("app-1") });
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);

            Assert.Empty(f.StarvedPosts());
        }

        [Fact]
        public void Throwing_starved_probe_is_swallowed()
        {
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(
                () => false,
                () => throw new InvalidOperationException("probe boom"));
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);

            var posted = Assert.Single(f.Ingress.Posted);
            Assert.Equal(DecisionSignalKind.EspExiting, posted.Kind);
            Assert.Empty(f.StarvedPosts());
        }

        [Fact]
        public void Default_probe_emits_nothing()
        {
            using var f = new Fixture();
            using var coordinator = new EspAndHelloTracker(
                sessionId: "S1",
                tenantId: "T1",
                post: new InformationalEventPost(f.TrackerPostSink, f.Clock),
                logger: f.Logger,
                skipConfigProbe: () => ((bool?)false, (bool?)false),
                accountSetupActivityProbe: () => true,
                userEspAppsSettledProbe: () => false);
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);

            Assert.Empty(f.StarvedPosts());
            Assert.Empty(coordinator.StarvedAppsReported);
        }

        [Fact]
        public void Reevaluate_does_not_re_emit_starved_warnings()
        {
            // sits-d Cloud-PC fix (2026-08-19): the synthesis re-check now runs on EVERY IME
            // app-state transition. It must not turn the one-shot starvation warning into a
            // per-transition stream — the re-check path suppresses the emission entirely
            // (the per-appId dedupe would swallow the duplicates, but walking the probe
            // hundreds of times on the IME hot path is waste, not safety).
            using var f = new Fixture();
            using var coordinator = f.BuildCoordinator(
                () => false,
                () => new[] { StarvedApp("app-1", "Contoso Backgrounds") });
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            var afterExit = f.StarvedPosts().Count;
            Assert.Equal(1, afterExit);

            for (var i = 0; i < 10; i++) coordinator.ReevaluateUserAppsSettledSynthesis();

            Assert.Equal(afterExit, f.StarvedPosts().Count);
            Assert.Single(coordinator.StarvedAppsReported);
        }

        [Fact]
        public void Starved_apps_that_settle_later_still_complete_the_enrollment()
        {
            // The real sits-d shape: the ESP exits with apps still starved (warning emitted),
            // the apps then all settle, and the re-check must open the gate — the warning
            // already on the timeline is history, not a permanent veto.
            using var f = new Fixture();
            var settled = false;
            using var coordinator = f.BuildCoordinator(
                () => settled,
                () => new[] { StarvedApp("app-1", "Contoso Backgrounds") });
            using var adapter = new EspAndHelloTrackerAdapter(coordinator, f.Ingress, f.Clock);

            coordinator.TriggerEspExitedForTest(Fixed);
            Assert.Single(f.StarvedPosts());
            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete);

            settled = true;
            coordinator.ReevaluateUserAppsSettledSynthesis();

            Assert.Equal(1, f.Ingress.Posted.Count(p => p.Kind == DecisionSignalKind.AccountSetupProvisioningComplete));
            Assert.Single(f.StarvedPosts());
        }
    }
}
